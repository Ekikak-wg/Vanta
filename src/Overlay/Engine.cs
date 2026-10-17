using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReplayOverlay {
 // Фаза записи или буфера повтора из события: включился, выключился или промежуточное состояние.
 public enum OutputPhase { Other, Started, Stopped }
 public sealed class RecordStatus { public bool Active; public int DurationMs; }

 // Движок записи — программа, которая на самом деле захватывает и кодирует видео. Меню обращается к ней только через этот интерфейс.
 // Сейчас реализация одна: OBS Studio через obs-websocket (ObsEngine). Всё, что есть только в OBS (сцены, профили, фильтры,
 // источники и их настройки), пока идёт через Call — именно эту часть потом заменит свой движок.
 public interface IEngine : IDisposable {
  string Name{get;}
  bool Connected{get;}
  event Action<bool> ConnectionChanged;
  event Action<string> Status;
  event Action<bool,OutputPhase,string> RecordChanged; // идёт ли запись, фаза, файл (после остановки)
  event Action<bool,OutputPhase> ReplayChanged;
  event Action<string> ReplaySaved;                    // путь к сохранённому повтору
  event Action<string,bool> MuteChanged;               // источник звука, выключен ли
  event Action Exiting;                                // движок закрывается по желанию пользователя (не сбой)
  event Action<string,Dictionary<string,object>> Event; // все события движка как есть — для частей, завязанных на OBS
  void Start();

  // Процесс движка
  EngineHome Home{get;}
  bool IsRunning{get;}
  void Close();                                        // закрыть свою копию штатно (OBS пользователя не закрывается никогда)
  bool IsProcess(string exeName);                      // это процесс движка (для списков программ и звука)
  bool LaunchHidden(bool startReplay,string reason);   // запустить без окна, если ещё не запущен
  void Open();                                         // показать окно движка (или запустить его)

  // Запись
  Task<bool> ToggleRecord();                           // возвращает: идёт ли запись после переключения
  Task<RecordStatus> Record();
  Task<bool> IsStreaming();
  Task<bool> OutputsBusy();                            // запись, трансляция или виртуальная камера
  Task<string> RecordDirectory();
  Task SetRecordDirectory(string dir);

  // Мгновенный повтор
  Task<bool?> ReplayActive();                          // null — буфер повтора выключен в настройках движка
  Task StartReplay();
  Task StartReplayWhenReady();
  Task StopReplay();
  Task ToggleReplay();
  Task RestartReplay();
  Task WaitReplay(bool active);
  Task SaveReplay();
  Task<int> ReplayDuration();                          // секунды; 0 — неизвестно
  Task SetReplayDuration(int seconds);
  Task SetReplayPrefix(string prefix);

  // Снимок источника
  Task SaveScreenshot(string source,string file,bool jpeg,int quality);

  // Звук
  Task<string> DefaultMic();                           // "" — микрофон не выбран
  Task<bool> IsMuted(string input);
  Task SetMuted(string input,bool muted);
  Task<bool> ToggleMute(string input);                 // возвращает: выключен ли после переключения
  Task<double> VolumeDb(string input);                 // исключение — у источника нет звука
  Task SetVolumeDb(string input,double db);            // −∞ — тишина
  Task<int> SyncOffset(string input);
  Task SetSyncOffset(string input,int ms);

  // Состояние
  Task<StatsSample> Stats();
  Task<string> Version();
  Task<Dictionary<string,object>> Call(string request,Dictionary<string,object> data=null);
 }

 // Где живёт движок: программа, её настройки и как её запускать.
 // Своя копия OBS (портативная, в папке «engine» рядом с меню) или — пока её нет — OBS Studio, установленный пользователем.
 public sealed class EngineHome {
  public string Name="OBS";
  public string Root;                                  // папка своей копии; null — OBS пользователя
  public string Executable;                            // null — найти установленный OBS
  public string ProcessName="obs64";
  public string HiddenArgs="--minimize-to-tray --disable-shutdown-check";
  public string PreferredFolder;                       // папка записей для нового профиля своей копии (прежняя папка пользователя)
  public bool Own{get{return Root!=null;}}
  public string ConfigRoot{get{return Own?Path.Combine(Root,@"config\obs-studio"):null;}}
  public string WebSocketConfig{get{return Own?Path.Combine(ConfigRoot,@"plugin_config\obs-websocket\config.json"):null;}}
  public static EngineHome UserObs(){return new EngineHome();}
  public static EngineHome OwnCopy(string root){return new EngineHome{Name="движок записи",Root=root,Executable=Path.Combine(root,@"bin\64bit\obs64.exe"),HiddenArgs="--portable --disable-updater --minimize-to-tray --disable-shutdown-check"};}
  public static EngineHome Find(){string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engine");return File.Exists(Path.Combine(root,@"bin\64bit\obs64.exe"))?OwnCopy(root):UserObs();}
  public string FindExecutable(){return Executable??ObsConfig.Executable();}
  // Свою копию узнаём по пути: рядом может работать OBS пользователя с тем же именем процесса.
  public Process[] Running(){
   var all=Process.GetProcessesByName(ProcessName);if(!Own)return all;
   return all.Where(p=>{try{return string.Equals(p.MainModule.FileName,Executable,StringComparison.OrdinalIgnoreCase);}catch(Exception){return false;}}).ToArray();}
 }

 static class EngineWindows {
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
  delegate bool EnumProc(IntPtr h,IntPtr l);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f,IntPtr l);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h,out int pid);
  [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h,System.Text.StringBuilder s,int n);
  // Главное окно OBS скрыто, поэтому Process.MainWindowHandle его не видит: ищем окно процесса с заголовком «OBS …».
  public static bool Close(int pid){
   IntPtr main=IntPtr.Zero;
   EnumWindows((h,l)=>{int id;GetWindowThreadProcessId(h,out id);if(id==pid){var sb=new System.Text.StringBuilder(256);GetWindowText(h,sb,256);if(sb.ToString().StartsWith("OBS ")){main=h;return false;}}return true;},IntPtr.Zero);
   return main!=IntPtr.Zero&&PostMessage(main,0x10,IntPtr.Zero,IntPtr.Zero);}
 }

 // Движок OBS Studio: запросы obs-websocket v5 и их события.
 public sealed class ObsEngine : IEngine {
  readonly EngineHome home; readonly ObsClient client;
  readonly Func<string,Dictionary<string,object>,Task<Dictionary<string,object>>> fake;
  public ObsEngine(EngineHome home){this.home=home;client=new ObsClient{ConfigFile=home.WebSocketConfig};
   if(home.Own)ObsConfig.RootOverride=home.ConfigRoot; // журналы, профиль и кодировщики читаются из своей копии
   client.ConnectionChanged+=c=>{var h=ConnectionChanged;if(h!=null)h(c);};
   client.Status+=s=>{var h=Status;if(h!=null)h(s);};
   client.EventReceived+=Deliver;}
  // Для проверок: ответы на запросы задаёт тест, события подаются через Deliver.
  public ObsEngine(Func<string,Dictionary<string,object>,Task<Dictionary<string,object>>> fakeCall){home=new EngineHome();fake=fakeCall;}

  public string Name{get{return home.Name;}}
  public bool Connected{get{return fake!=null||client.Connected;}}
  public event Action<bool> ConnectionChanged;
  public event Action<string> Status;
  public event Action<bool,OutputPhase,string> RecordChanged;
  public event Action<bool,OutputPhase> ReplayChanged;
  public event Action<string> ReplaySaved;
  public event Action<string,bool> MuteChanged;
  public event Action Exiting;
  public event Action<string,Dictionary<string,object>> Event;
  public void Start(){if(client!=null)client.Start();}
  public void Dispose(){if(client!=null)client.Dispose();}

  static OutputPhase Phase(Dictionary<string,object> d){string s=Json.Str(d,"outputState");return s=="OBS_WEBSOCKET_OUTPUT_STARTED"?OutputPhase.Started:s=="OBS_WEBSOCKET_OUTPUT_STOPPED"?OutputPhase.Stopped:OutputPhase.Other;}
  // Событие OBS: сначала понятное меню (запись, повтор, звук, выход), затем как есть — для частей, завязанных на OBS.
  public void Deliver(string type,Dictionary<string,object> d){
   if(type=="RecordStateChanged"){var h=RecordChanged;if(h!=null)h(Json.Bool(d,"outputActive"),Phase(d),Json.Str(d,"outputPath"));}
   else if(type=="ReplayBufferStateChanged"){var h=ReplayChanged;if(h!=null)h(Json.Bool(d,"outputActive"),Phase(d));}
   else if(type=="ReplayBufferSaved"){var h=ReplaySaved;if(h!=null)h(Json.Str(d,"savedReplayPath"));}
   else if(type=="InputMuteStateChanged"){var h=MuteChanged;if(h!=null)h(Json.Str(d,"inputName"),Json.Bool(d,"inputMuted"));}
   else if(type=="ExitStarted"){var h=Exiting;if(h!=null)h();}
   var all=Event;if(all!=null)all(type,d);}

  public Task<Dictionary<string,object>> Call(string request,Dictionary<string,object> data=null){return fake!=null?fake(request,data??Json.D()):client.Call(request,data);}

  // ——— Процесс ———
  public EngineHome Home{get{return home;}}
  public bool IsRunning{get{return home.Running().Length>0;}}
  // Ждём до 5 с, чтобы следующий запуск меню не застал копию на выходе (тогда новая не запустится, а старая вот-вот закроется).
  public void Close(){if(!home.Own)return;foreach(var p in home.Running()){try{if(EngineWindows.Close(p.Id)){Log.Write("Движок записи закрывается");if(!p.WaitForExit(5000))Log.Write("Движок записи закрывается дольше 5 с");}}catch(Exception ex){Log.Write("Движок не закрыт: "+ex.Message);}}}
  public bool IsProcess(string exeName){return string.Equals(exeName,home.ProcessName,StringComparison.OrdinalIgnoreCase)||string.Equals(exeName,"obs32",StringComparison.OrdinalIgnoreCase);}
  public bool LaunchHidden(bool startReplay,string reason){
   if(IsRunning)return false;string exe=home.FindExecutable();
   if(exe==null){Log.Write("OBS не найден — запуск пропущен");var h=Status;if(h!=null)h("OBS Studio не найден");return false;}
   if(home.Own){try{foreach(string line in EngineSetup.Prepare(home,home.PreferredFolder))Log.Write(line);}catch(Exception ex){Log.Write("Движок записи не подготовлен: "+ex.Message);return false;}}
   int stale=ObsConfig.ClearCrashSentinels();if(stale>0)Log.Write("Сняты признаки прошлого сбоя OBS ("+stale+"), чтобы он не ждал ответа в скрытом окне");
   string what=home.Own?"Движок записи запущен":"OBS запущен в трее";
   try{Process.Start(new ProcessStartInfo(exe,home.HiddenArgs+(startReplay?" --startreplaybuffer":"")){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=true});Log.Write(what+" ("+reason+")");return true;}
   catch(Exception ex){Log.Write("Не удалось запустить "+(home.Own?"движок записи":"OBS")+": "+ex.Message);return false;}}
  public void Open(){
   if(home.Own){if(!IsRunning)LaunchHidden(true,"по кнопке");return;} // у своей копии окна нет — только запуск
   var running=home.Running();
   if(running.Length>0){var h=running[0].MainWindowHandle;if(h!=IntPtr.Zero){Native.ShowWindow(h,9);Native.SetForegroundWindow(h);}return;}
   string exe=home.FindExecutable();if(exe==null)throw new FileNotFoundException("OBS Studio не найден. Установите его с obsproject.com");
   Process.Start(new ProcessStartInfo(exe,"--startreplaybuffer"){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=true});}

  // ——— Запись ———
  public async Task<bool> ToggleRecord(){return Json.Bool(await Call("ToggleRecord"),"outputActive");}
  public async Task<RecordStatus> Record(){var r=await Call("GetRecordStatus");return new RecordStatus{Active=Json.Bool(r,"outputActive"),DurationMs=Json.Int(r,"outputDuration")};}
  public async Task<bool> IsStreaming(){return Json.Bool(await Call("GetStreamStatus"),"outputActive");}
  public async Task<bool> OutputsBusy(){
   var rec=Call("GetRecordStatus");var stream=Call("GetStreamStatus");var camera=Call("GetVirtualCamStatus");
   return Json.Bool(await rec,"outputActive")||Json.Bool(await stream,"outputActive")||Json.Bool(await camera,"outputActive");}
  public async Task<string> RecordDirectory(){return Json.Str(await Call("GetRecordDirectory"),"recordDirectory").Replace('/','\\');}
  public Task SetRecordDirectory(string dir){return Call("SetRecordDirectory",Json.D("recordDirectory",dir.Replace('\\','/')));}

  // ——— Повтор ———
  public async Task<bool?> ReplayActive(){
   Dictionary<string,object> r;try{r=await Call("GetReplayBufferStatus");}catch(InvalidOperationException){return null;} // буфер выключен в настройках OBS
   return Json.Bool(r,"outputActive");}
  public Task StartReplay(){return Call("StartReplayBuffer");}
  public Task StopReplay(){return Call("StopReplayBuffer");}
  public Task ToggleReplay(){return Call("ToggleReplayBuffer");}
  public Task SaveReplay(){return Call("SaveReplayBuffer");}
  // После перезагрузки профиля буфер готов не сразу: OBS отвечает «недоступен» (604) — пробуем до 5 секунд.
  public async Task StartReplayWhenReady(){for(int i=0;;i++){try{await StartReplay();break;}catch(InvalidOperationException ex){if(!ex.Message.EndsWith("(604)")||i>=20)throw;}await Task.Delay(250);}await WaitReplay(true);}
  public async Task RestartReplay(){await StopReplay();await WaitReplay(false);await StartReplay();await WaitReplay(true);}
  public async Task WaitReplay(bool active){for(int i=0;i<100;i++){var s=await Call("GetReplayBufferStatus");if(Json.Bool(s,"outputActive")==active)return;await Task.Delay(100);}throw new TimeoutException("Буфер не перешёл в ожидаемое состояние");}
  // Параметры повтора лежат в разделе вывода профиля: простой режим — SimpleOutput, расширенный — AdvOut.
  async Task<string> OutputSection(){return Json.Str(await Call("GetProfileParameter",Json.D("parameterCategory","Output","parameterName","Mode")),"parameterValue")=="Simple"?"SimpleOutput":"AdvOut";}
  public async Task<int> ReplayDuration(){int v;return int.TryParse(Json.Str(await Call("GetProfileParameter",Json.D("parameterCategory",await OutputSection(),"parameterName","RecRBTime")),"parameterValue"),out v)?v:0;}
  public async Task SetReplayDuration(int seconds){await Call("SetProfileParameter",Json.D("parameterCategory",await OutputSection(),"parameterName","RecRBTime","parameterValue",seconds.ToString()));}
  public async Task SetReplayPrefix(string prefix){await Call("SetProfileParameter",Json.D("parameterCategory",await OutputSection(),"parameterName","RecRBPrefix","parameterValue",prefix));}

  // ——— Снимок ———
  public Task SaveScreenshot(string source,string file,bool jpeg,int quality){
   var req=Json.D("sourceName",source,"imageFormat",jpeg?"jpg":"png","imageFilePath",file);if(jpeg)req["imageCompressionQuality"]=quality;return Call("SaveSourceScreenshot",req);}

  // ——— Звук ———
  public async Task<string> DefaultMic(){return Json.Str(await Call("GetSpecialInputs"),"mic1");}
  public async Task<bool> IsMuted(string input){return Json.Bool(await Call("GetInputMute",Json.D("inputName",input)),"inputMuted");}
  public Task SetMuted(string input,bool muted){return Call("SetInputMute",Json.D("inputName",input,"inputMuted",muted));}
  public async Task<bool> ToggleMute(string input){return Json.Bool(await Call("ToggleInputMute",Json.D("inputName",input)),"inputMuted");}
  public async Task<double> VolumeDb(string input){var v=await Call("GetInputVolume",Json.D("inputName",input));object db;return v.TryGetValue("inputVolumeDb",out db)&&db!=null?Convert.ToDouble(db,System.Globalization.CultureInfo.InvariantCulture):-100;}
  public Task SetVolumeDb(string input,double db){return Call("SetInputVolume",double.IsNegativeInfinity(db)?Json.D("inputName",input,"inputVolumeMul",0):Json.D("inputName",input,"inputVolumeDb",db));}
  public async Task<int> SyncOffset(string input){return Json.Int(await Call("GetInputAudioSyncOffset",Json.D("inputName",input)),"inputAudioSyncOffset");}
  public Task SetSyncOffset(string input,int ms){return Call("SetInputAudioSyncOffset",Json.D("inputName",input,"inputAudioSyncOffset",ms));}

  // ——— Состояние ———
  public async Task<StatsSample> Stats(){return StatsSample.From(await Call("GetStats"));}
  public async Task<string> Version(){var v=await Call("GetVersion");return "OBS "+Json.Str(v,"obsVersion")+", obs-websocket "+Json.Str(v,"obsWebSocketVersion");}
 }
}
