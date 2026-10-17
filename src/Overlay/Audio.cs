using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ReplayOverlay {
 // Какие программы сейчас выводят звук (сеансы Windows Core Audio на всех активных устройствах вывода).
 public static class AudioSessions {
  [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
  [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator { int EnumAudioEndpoints(int dataFlow,int stateMask,out IMMDeviceCollection devices); int GetDefaultAudioEndpoint(int dataFlow,int role,out IMMDevice device); }
  [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceCollection { int GetCount(out uint count); int Item(uint index,out IMMDevice device); }
  [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice { int Activate(ref Guid iid,int clsCtx,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object iface); }
  [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionManager2 { int GetAudioSessionControl(IntPtr group,int flags,out IntPtr control); int GetSimpleAudioVolume(IntPtr group,int flags,out IntPtr volume); int GetSessionEnumerator(out IAudioSessionEnumerator sessions); }
  [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionEnumerator { int GetCount(out int count); int GetSession(int index,[MarshalAs(UnmanagedType.IUnknown)] out object session); }
  [ComImport,Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionControl2 {
   int GetState(out int state); int GetDisplayName(out IntPtr name); int SetDisplayName(string name,IntPtr ctx); int GetIconPath(out IntPtr path); int SetIconPath(string path,IntPtr ctx);
   int GetGroupingParam(out Guid group); int SetGroupingParam(ref Guid group,IntPtr ctx); int RegisterAudioSessionNotification(IntPtr n); int UnregisterAudioSessionNotification(IntPtr n);
   int GetSessionIdentifier(out IntPtr id); int GetSessionInstanceIdentifier(out IntPtr id); int GetProcessId(out uint pid); [PreserveSig] int IsSystemSoundsSession();
  }
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h,int flags,StringBuilder s,ref int size);

  // Программы с открытым звуковым сеансом (играет или готов играть): имя exe без «.exe» → полный путь.
  public static SortedDictionary<string,string> Active() {
   var result=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   object enumObj=null;
   try {
    enumObj=new MMDeviceEnumeratorCom(); var enumerator=(IMMDeviceEnumerator)enumObj;
    IMMDeviceCollection devices; if(enumerator.EnumAudioEndpoints(0,1,out devices)!=0)return result; // eRender, DEVICE_STATE_ACTIVE
    uint count; devices.GetCount(out count); Guid iid=typeof(IAudioSessionManager2).GUID;
    for(uint i=0;i<count;i++) {
     IMMDevice device; if(devices.Item(i,out device)!=0)continue;
     object mgrObj; if(device.Activate(ref iid,23,IntPtr.Zero,out mgrObj)!=0)continue; // CLSCTX_ALL
     IAudioSessionEnumerator sessions; if(((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions)!=0)continue;
     int n; sessions.GetCount(out n);
     for(int k=0;k<n;k++) {
      object o; if(sessions.GetSession(k,out o)!=0)continue; var s=o as IAudioSessionControl2; if(s==null)continue;
      int state; s.GetState(out state); if(state==2||s.IsSystemSoundsSession()==0)continue; // 2 = закрыт; 0 (S_OK) = системные звуки
      uint pid; if(s.GetProcessId(out pid)!=0||pid==0)continue;
      string path=ExePath(pid); if(path.Length>0)result[System.IO.Path.GetFileNameWithoutExtension(path)]=path;
     }
    }
   } catch { } // звук Windows недоступен — список пустой
   return result;
  }
  static string ExePath(uint pid) {
   IntPtr h=OpenProcess(0x1000,false,pid); if(h==IntPtr.Zero)return "";
   try { var s=new StringBuilder(1024); int n=s.Capacity; return QueryFullProcessImageName(h,0,s,ref n)?s.ToString():""; } finally { CloseHandle(h); }
  }
 }

 // Сохраняемое состояние звука, чтобы вернуть всё как было: прежние дорожки источников и созданные меню источники.
 public sealed class AudioState {
  public Dictionary<string,int> SavedMasks=new Dictionary<string,int>(); // источник → маска дорожек до смены режима
  public List<string> GameAudioEnabled=new List<string>();            // где меню включило «Захватывать звук» у захвата игры
  public List<string> Created=new List<string>();                     // источники, созданные меню (звук приложений, устройства)
  public Dictionary<string,object> ToJson(){return Json.D("masks",SavedMasks,"gameAudio",GameAudioEnabled.ToArray(),"created",Created.ToArray());}
  public static AudioState FromJson(Dictionary<string,object> d) {
   var s=new AudioState(); object raw;
   if(d.TryGetValue("masks",out raw))foreach(var kv in Json.Obj(raw))s.SavedMasks[kv.Key]=Convert.ToInt32(kv.Value);
   if(d.TryGetValue("gameAudio",out raw)&&raw is object[])foreach(object x in (object[])raw)s.GameAudioEnabled.Add(Convert.ToString(x));
   if(d.TryGetValue("created",out raw)&&raw is object[])foreach(object x in (object[])raw)s.Created.Add(Convert.ToString(x));
   return s;
  }
 }

 // Настройка звука в OBS через запросы obs-websocket. Отдельно от окна — проверяется тестом без живого OBS.
 public sealed class AudioSetup {
  public delegate Task<Dictionary<string,object>> Caller(string request,Dictionary<string,object> data);
  readonly Caller call; public readonly AudioState State;
  public const string AppPrefix="Звук · ";
  // Схема дорожек: 1 — общий звук, 2 — игра, 3 — микрофон, 4 — Discord, 5 — рабочий стол / приложения.
  public static readonly string[] TrackNames={"Общий","Игра","Микрофон","Discord","Рабочий стол"};
  public const int Full=1,Game=2,Mic=4,Discord=8,Desktop=16;

  public AudioSetup(Caller caller,AudioState state){call=caller;State=state;}

  public static Dictionary<string,object> Tracks(int mask){var d=new Dictionary<string,object>();for(int i=0;i<6;i++)d[(i+1).ToString()]=(mask&(1<<i))!=0;return d;}
  public static int Mask(Dictionary<string,object> tracks){int m=0;for(int i=0;i<6;i++)if(Json.Bool(tracks,(i+1).ToString()))m|=1<<i;return m;}
  public async Task<int> GetMask(string input){return Mask(Json.Obj((await call("GetInputAudioTracks",Json.D("inputName",input)))["inputAudioTracks"]));}
  public Task SetMask(string input,int mask){return call("SetInputAudioTracks",Json.D("inputName",input,"inputAudioTracks",Tracks(mask)));}

  async Task<List<Dictionary<string,object>>> Inputs(string kind) {
   object raw; (await call("GetInputList",kind==null?Json.D():Json.D("inputKind",kind))).TryGetValue("inputs",out raw);
   return ((raw as object[])??new object[0]).Select(Json.Obj).ToList();
  }
  // Источник «Звук приложения», захватывающий указанный exe (например Discord.exe), или null.
  public async Task<string> ProcessSource(string exeFile) {
   foreach(var i in await Inputs("wasapi_process_output_capture")) {
    string name=Json.Str(i,"inputName"); var s=Json.Obj((await call("GetInputSettings",Json.D("inputName",name)))["inputSettings"]);
    if(Json.Str(s,"window").EndsWith(":"+GameInfo.Encode(exeFile),StringComparison.OrdinalIgnoreCase))return name;
   }
   return null;
  }
  // Создаёт источник в первой сцене и добавляет его в остальные; если такой уже есть — только добавляет туда, где его нет.
  public async Task<string> CreateInScenes(string name,string kind,Dictionary<string,object> settings,IList<string> scenes) {
   bool exists=(await Inputs(null)).Any(i=>Json.Str(i,"inputName")==name);
   foreach(string scene in scenes) {
    if(!exists){await call("CreateInput",Json.D("sceneName",scene,"inputName",name,"inputKind",kind,"inputSettings",settings,"sceneItemEnabled",true));exists=true;if(!State.Created.Contains(name))State.Created.Add(name);continue;}
    object items; (await call("GetSceneItemList",Json.D("sceneName",scene))).TryGetValue("sceneItems",out items);
    if(!((items as object[])??new object[0]).Any(it=>Json.Str(Json.Obj(it),"sourceName")==name))await call("CreateSceneItem",Json.D("sceneName",scene,"sourceName",name));
   }
   return name;
  }
  async Task SaveAndSet(string input,int mask){if(!State.SavedMasks.ContainsKey(input))State.SavedMasks[input]=await GetMask(input);await SetMask(input,mask);}
  async Task<List<string>> GameCaptures(){return (await Inputs("game_capture")).Select(i=>Json.Str(i,"inputName")).ToList();}
  async Task EnableGameAudio(string input) {
   var s=Json.Obj((await call("GetInputSettings",Json.D("inputName",input)))["inputSettings"]);
   if(Json.Bool(s,"capture_audio"))return;
   await call("SetInputSettings",Json.D("inputName",input,"inputSettings",Json.D("capture_audio",true),"overlay",true));
   if(!State.GameAudioEnabled.Contains(input))State.GameAudioEnabled.Add(input);
  }

  // «Разделить звук по дорожкам»: общий звук, игра, микрофон, Discord, рабочий стол — каждый на своей дорожке.
  // Нужен расширенный режим вывода OBS: в простом режиме дорожки записи не настраиваются.
  public async Task<List<string>> SplitTracks(IList<string> scenes,bool discordPresent) {
   var log=new List<string>();
   if(Json.Str(await call("GetProfileParameter",Json.D("parameterCategory","Output","parameterName","Mode")),"parameterValue")!="Advanced")
    throw new InvalidOperationException("Дорожки настраиваются только в расширенном режиме вывода движка записи");
   var special=await call("GetSpecialInputs",Json.D());
   string desktop=Json.Str(special,"desktop1"),mic=Json.Str(special,"mic1");
   if(desktop.Length>0){await SetMask(desktop,Full|Desktop);log.Add("«"+desktop+"» → дорожки 1 и 5");}
   if(mic.Length>0){await SetMask(mic,Full|Mic);log.Add("«"+mic+"» → дорожки 1 и 3");}
   foreach(string g in await GameCaptures()){await EnableGameAudio(g);await SetMask(g,Game);log.Add("«"+g+"» (звук игры) → дорожка 2");}
   string discord=await ProcessSource("Discord.exe");
   if(discord==null&&discordPresent&&scenes.Count>0){discord=await CreateInScenes("Discord — звук","wasapi_process_output_capture",Json.D("window","::Discord.exe","priority",2),scenes);log.Add("создан источник «Discord — звук»");}
   if(discord!=null){await SetMask(discord,Discord);log.Add("«"+discord+"» → дорожка 4");}
   // Записываемые дорожки: добавляем 1–5 к уже выбранным; пустые названия дорожек заполняем.
   int rec; int.TryParse(Json.Str(await call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecTracks")),"parameterValue"),out rec);
   if((rec&31)!=31){await call("SetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecTracks","parameterValue",(rec|31).ToString()));log.Add("в запись и повтор включены дорожки 1–5");}
   for(int i=0;i<TrackNames.Length;i++) {
    string key="Track"+(i+1)+"Name";
    if(Json.Str(await call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName",key)),"parameterValue").Length==0)
     await call("SetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName",key,"parameterValue",TrackNames[i]));
   }
   return log;
  }

  // Режим звука приложений: "all" — весь звук компьютера; "only" — только выбранные; "except" — все, кроме выбранных.
  // В режимах only/except звук рабочего стола убирается из записи, а каждая нужная программа пишется своим источником
  // (Windows не умеет вычитать программу из общего звука). Игра и Discord остаются в общей дорожке через свои источники.
  public async Task<List<string>> ApplyMode(string mode,ICollection<string> apps,IList<string> scenes) {
   var log=new List<string>();
   string desktop=Json.Str(await call("GetSpecialInputs",Json.D()),"desktop1");
   if(mode=="all") {
    foreach(var kv in State.SavedMasks.ToList()){try{await SetMask(kv.Key,kv.Value);}catch(InvalidOperationException){}}
    State.SavedMasks.Clear();
    foreach(string name in State.Created.Where(n=>n.StartsWith(AppPrefix)).ToList()){try{await call("RemoveInput",Json.D("inputName",name));}catch(InvalidOperationException){}State.Created.Remove(name);}
    foreach(string g in State.GameAudioEnabled.ToList()){try{await call("SetInputSettings",Json.D("inputName",g,"inputSettings",Json.D("capture_audio",false),"overlay",true));}catch(InvalidOperationException){}}
    State.GameAudioEnabled.Clear();
    log.Add("весь звук компьютера снова пишется источником «"+desktop+"»");
    return log;
   }
   if(desktop.Length>0){await SaveAndSet(desktop,0);log.Add("«"+desktop+"» убран из записи");}
   foreach(string g in await GameCaptures()){await EnableGameAudio(g);await SaveAndSet(g,(await GetMask(g))|Full);}
   string discord=await ProcessSource("Discord.exe"); if(discord!=null)await SaveAndSet(discord,(await GetMask(discord))|Full);
   if(mode=="only") {
    // Лишние источники программ, которых больше нет в списке, убираем.
    foreach(string name in State.Created.Where(n=>n.StartsWith(AppPrefix)&&!apps.Contains(n.Substring(AppPrefix.Length))).ToList()){try{await call("RemoveInput",Json.D("inputName",name));}catch(InvalidOperationException){}State.Created.Remove(name);}
    foreach(string app in apps)log.Add(await EnsureApp(app,scenes));
   }
   return log;
  }
  // Источник звука одной программы на дорожках 1 и 5. Повторный вызов ничего не меняет.
  public async Task<string> EnsureApp(string exe,IList<string> scenes) {
   string name=AppPrefix+exe;
   bool exists=State.Created.Contains(name);
   await CreateInScenes(name,"wasapi_process_output_capture",Json.D("window","::"+GameInfo.Encode(exe+".exe"),"priority",2),scenes);
   if(!exists)await SetMask(name,Full|Desktop);
   return exists?"":"звук «"+exe+".exe» пишется отдельным источником";
  }
  // Устройства из списка OBS: input — микрофоны (берём у источника mic1), иначе устройства вывода (у desktop1).
  public async Task<List<KeyValuePair<string,string>>> Devices(bool input) {
   var list=new List<KeyValuePair<string,string>>();
   string source=Json.Str(await call("GetSpecialInputs",Json.D()),input?"mic1":"desktop1"); if(source.Length==0)return list;
   object raw; (await call("GetInputPropertiesListPropertyItems",Json.D("inputName",source,"propertyName","device_id"))).TryGetValue("propertyItems",out raw);
   foreach(object o in (raw as object[])??new object[0]){var it=Json.Obj(o);string v=Json.Str(it,"itemValue");if(v.Length>0&&v!="default"&&Json.Bool(it,"itemEnabled"))list.Add(new KeyValuePair<string,string>(Json.Str(it,"itemName"),v));}
   return list;
  }
  // Ещё один микрофон или устройство вывода: отдельный источник в сценах захвата (микрофон — дорожки 1 и 3, вывод — 1 и 5).
  public async Task<string> AddDevice(bool input,string deviceName,string deviceId,IList<string> scenes) {
   string name=(input?"Микрофон · ":"Устройство · ")+deviceName;
   bool exists=(await Inputs(null)).Any(i=>Json.Str(i,"inputName")==name);
   await CreateInScenes(name,input?"wasapi_input_capture":"wasapi_output_capture",Json.D("device_id",deviceId),scenes);
   if(!exists)await SetMask(name,input?Full|Mic:Full|Desktop);
   return name;
  }
  public async Task Remove(string name){await call("RemoveInput",Json.D("inputName",name));State.Created.Remove(name);State.SavedMasks.Remove(name);}
  // ——— Раскладка дорожек: common — всё в дорожку 1; split — 1 общий + 2 игра, 3 микрофон, 4 Discord, 5 рабочий стол/программы; custom — как настроил пользователь ———
  // Роли источников: desktop (звук рабочего стола), mic, game (захват игры), discord, app (звук программы от меню), output (ещё устройство вывода), other.
  // desktopRecorded — пишется ли звук рабочего стола (режим «весь звук компьютера»). Если нет, игра и Discord идут в общую дорожку своими источниками.
  public static int Desired(string layout,bool desktopRecorded,string role){
   // Отдельный источник программы (например «Музыка») при записи всего звука компьютера не нужен: его звук уже есть в рабочем столе.
   bool inFull=role=="mic"||role=="output"||(role=="desktop"&&desktopRecorded)||((role=="game"||role=="discord"||role=="app")&&!desktopRecorded);
   int m=inFull?Full:0;
   if(layout=="split"){if(role=="game")m|=Game;else if(role=="mic")m|=Mic;else if(role=="discord")m|=Discord;else if((role=="desktop"&&desktopRecorded)||(role=="app"&&!desktopRecorded)||role=="output")m|=Desktop;}
   return m;}
  // «Убрать двойной звук»: снять дублирующие источники с записываемых дорожек, где уже есть звук рабочего стола. Имя → новая маска.
  public static Dictionary<string,int> FixDuplicates(IEnumerable<AudioSource> sources,int recTracks){
   var list=sources.ToList();var res=new Dictionary<string,int>();int desktop=list.Where(s=>s.Role=="desktop").Aggregate(0,(a,s)=>a|s.Mask)&recTracks;
   foreach(var s in list.Where(x=>x.Role=="discord"||x.Role=="app"||(x.Role=="game"&&x.GameAudio)))if((s.Mask&desktop)!=0)res[s.Name]=s.Mask&~desktop;
   return res;}
  public static int RecTracksFor(string layout,int current,int custom){return layout=="common"?Full:layout=="split"?(31|(current&32)):Math.Max(1,custom&63);}
  public static string LayoutOf(int recTracks){return recTracks==1?"common":(recTracks&31)==31?"split":"custom";}
  public sealed class AudioSource{public string Name,Role;public int Mask;public bool GameAudio;}
  // Дубли: на записываемой дорожке есть «Звук рабочего стола» и ещё отдельный источник с тем же звуком (игра, Discord, программа).
  public static List<string> Duplicates(IEnumerable<AudioSource> sources,int recTracks){
   var list=sources.ToList();var res=new List<string>();
   foreach(var d in list.Where(s=>s.Role=="desktop"))for(int t=0;t<6;t++){int bit=1<<t;if((recTracks&bit)==0||(d.Mask&bit)==0)continue;
    foreach(var s in list.Where(x=>(x.Role=="discord"||x.Role=="app"||(x.Role=="game"&&x.GameAudio))&&(x.Mask&bit)!=0))
     res.Add("Дорожка "+(t+1)+": звук «"+s.Name+"» записывается дважды — и через «"+d.Name+"», и отдельно");}
   return res;}
  // Источники со звуком и их роли.
  public async Task<List<AudioSource>> Sources(){
   var special=await call("GetSpecialInputs",Json.D());string desktop=Json.Str(special,"desktop1");var res=new List<AudioSource>();
   foreach(var i in await Inputs(null)){string name=Json.Str(i,"inputName"),kind=Json.Str(i,"inputKind");string role;
    if(name==desktop)role="desktop";else if(kind=="wasapi_input_capture")role="mic";else if(kind=="game_capture")role="game";
    else if(kind=="wasapi_process_output_capture")role=name.StartsWith(AppPrefix)?"app":Json.Str(Json.Obj((await call("GetInputSettings",Json.D("inputName",name)))["inputSettings"]),"window").EndsWith(":Discord.exe",StringComparison.OrdinalIgnoreCase)?"discord":"app";
    else if(kind=="wasapi_output_capture")role="output";else continue;
    var src=new AudioSource{Name=name,Role=role};try{src.Mask=await GetMask(name);}catch(InvalidOperationException){continue;}
    if(role=="game")src.GameAudio=Json.Bool(Json.Obj((await call("GetInputSettings",Json.D("inputName",name)))["inputSettings"]),"capture_audio");
    res.Add(src);}
   return res;}
  // Применить раскладку. Возвращает журнал; RecTracks меняется только при необходимости (тогда вызывающий перезапускает буфер).
  public async Task<List<string>> ApplyLayout(string layout,bool desktopRecorded,int customRecTracks){
   var log=new List<string>();
   if(Json.Str(await call("GetProfileParameter",Json.D("parameterCategory","Output","parameterName","Mode")),"parameterValue")!="Advanced")
    throw new InvalidOperationException("Дорожки настраиваются только в расширенном режиме вывода движка записи");
   if(layout!="custom")foreach(var s in await Sources()){
    if(s.Role=="game"&&(layout=="split"||!desktopRecorded))await EnableGameAudio(s.Name);
    int want=Desired(layout,desktopRecorded,s.Role);if(want!=s.Mask){await SetMask(s.Name,want);log.Add("«"+s.Name+"» → "+(want==0?"не записывается":"дорожки "+string.Join(", ",Enumerable.Range(0,6).Where(t=>(want&(1<<t))!=0).Select(t=>(t+1).ToString()))));}
    // Сохранённые для режима «весь звук» маски — тоже по новой раскладке, чтобы возврат в этот режим её не ломал.
    if(State.SavedMasks.ContainsKey(s.Name))State.SavedMasks[s.Name]=Desired(layout,true,s.Role);}
   int rec;int.TryParse(Json.Str(await call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecTracks")),"parameterValue"),out rec);
   int target=RecTracksFor(layout,rec,customRecTracks);
   if(target!=rec){await call("SetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecTracks","parameterValue",target.ToString()));log.Add("в запись и повтор: дорожки "+string.Join(", ",Enumerable.Range(0,6).Where(t=>(target&(1<<t))!=0).Select(t=>(t+1).ToString())));}
   for(int i=0;i<TrackNames.Length;i++){string key="Track"+(i+1)+"Name";
    if(Json.Str(await call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName",key)),"parameterValue").Length==0)
     await call("SetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName",key,"parameterValue",TrackNames[i]));}
   return log;}

  // Для режима «все, кроме»: какие программы со звуком нужно добавить.
  public static List<string> MissingApps(IEnumerable<string> active,ICollection<string> excluded,ICollection<string> skip,AudioState state) {
   return active.Where(a=>!excluded.Contains(a)&&!skip.Contains(a)&&!state.Created.Contains(AppPrefix+a)).ToList();
  }
 }
}
