using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ReplayOverlay {
 // Найденная игра: процесс и её главное окно.
 public sealed class GameInfo {
  public int Pid; public string Exe="",FileName="",Path="",Title="",Class="",Reason=""; public IntPtr Hwnd; public DateTime Started;
  // Строка окна в формате OBS («заголовок:класс:exe»; «#» → «#22», «:» → «#3A»), как её пишет сам OBS.
  public string ObsWindow { get { return Encode(Title)+":"+Encode(Class)+":"+Encode(FileName); } }
  public static string Encode(string s) { return (s??"").Replace("#","#22").Replace(":","#3A"); }
 }

 // Сведения об окне, по которым решаем «игра или нет». Отдельно от WinAPI — чтобы проверять тестами.
 public sealed class WindowFacts { public string Exe="",Path=""; public bool CoversMonitor; public bool? Has3D; }

 public static class GameRules {
  // Не игры, даже если развернуты во весь экран: браузеры, плееры, мессенджеры, лаунчеры, системные окна.
  public static readonly HashSet<string> BuiltInNotGames=new HashSet<string>(StringComparer.OrdinalIgnoreCase){
   "explorer","chrome","msedge","firefox","opera","brave","browser","vivaldi","iexplore","msedgewebview2",
   "steam","steamwebhelper","epicgameslauncher","epicwebhelper","riotclientservices","riotclientux","riotclientuxrender","upc","ubisoftconnect","galaxyclient","battle.net","eadesktop","origin","playnite.desktopapp",
   "discord","telegram","slack","teams","ms-teams","zoom","skype","whatsapp","viber",
   "obs64","obs32","streamlabs obs","vlc","mpc-hc64","mpc-hc","mpc-be64","potplayermini64","potplayermini","wmplayer","mpv","kmplayer",
   "applicationframehost","textinputhost","shellexperiencehost","searchhost","startmenuexperiencehost","lockapp","taskmgr","mstsc","powerpnt","photos","snippingtool","screenclippinghost",
   "code","devenv","notepad","sublime_text","claude","chatgpt","replayoverlay","overwolf","nvidia overlay","nvcontainer","sonar","wallpaper32","wallpaper64","wallpaperservice32",
   "unitycrashhandler64","unitycrashhandler32","crashreportclient","easyanticheat_eos_setup","powertoys","powertoys.quickaccess"};

  // Папки игровых библиотек. Сами лаунчеры лежат рядом, поэтому их папки исключены отдельно.
  static readonly string[] LibraryParts={@"\steamapps\common\",@"\epic games\",@"\riot games\",@"\gog galaxy\games\",@"\gog games\",@"\ubisoft game launcher\games\",@"\ea games\",@"\xboxgames\"};
  static readonly string[] LauncherParts={@"\epic games\launcher\",@"\riot games\riot client\",@"\steamapps\common\steamworks shared\",@"\steamapps\common\steamvr\",@"\steamapps\common\wallpaper_engine\"};

  public static bool IsLauncher(string path){return !string.IsNullOrEmpty(path)&&LauncherParts.Any(x=>path.ToLowerInvariant().Contains(x));}
  public static string Library(string path) {
   if(string.IsNullOrEmpty(path)||IsLauncher(path))return null; string p=path.ToLowerInvariant();
   foreach(string part in LibraryParts)if(p.Contains(part)){
    if(part.Contains("steam"))return "Steam"; if(part.Contains("epic"))return "Epic Games"; if(part.Contains("riot"))return "Riot";
    if(part.Contains("gog"))return "GOG"; if(part.Contains("ubisoft"))return "Ubisoft"; if(part.Contains("ea games"))return "EA"; return "Xbox";
   }
   return null;
  }

  // Причина, по которой окно — игра, или null.
  public static string Classify(WindowFacts f,ICollection<string> games,ICollection<string> notGames) {
   if(f==null||string.IsNullOrEmpty(f.Exe))return null;
   // Порядок: «Не считать играми» → «Мои игры» (ручной выбор сильнее встроенного списка) → встроенные исключения → признаки.
   if(notGames.Contains(f.Exe))return null;
   if(games.Contains(f.Exe))return "в списке «Мои игры»";
   if(BuiltInNotGames.Contains(f.Exe))return null;
   if(IsLauncher(f.Path))return null;
   string lib=Library(f.Path); if(lib!=null)return "из библиотеки "+lib;
   // Модули 3D у защищённых античитом процессов прочитать нельзя (null) — во весь экран считаем игрой.
   if(f.CoversMonitor&&f.Has3D!=false)return f.Has3D==true?"полноэкранное 3D-окно":"полноэкранное окно";
   return null;
  }
 }

 // Следит за окном на переднем плане и за процессом найденной игры. Alt+Tab игру не «отпускает»: она активна, пока жив процесс.
 public sealed class GameWatcher : IDisposable {
  delegate void WinEventProc(IntPtr hook,uint evt,IntPtr hwnd,int idObject,int idChild,uint thread,uint time);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left,Top,Right,Bottom; }
  [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor,Work; public uint Flags; }
  [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WinEventProc proc,uint pid,uint tid,uint flags);
  [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
  [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m,ref MONITORINFO info);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int attr,out RECT r,int size);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h,int flags,StringBuilder s,ref int size);
  [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h,out uint code);
  [DllImport("psapi.dll")] static extern bool EnumProcessModulesEx(IntPtr h,[Out] IntPtr[] modules,int size,out int needed,uint filter);
  [DllImport("psapi.dll",CharSet=CharSet.Unicode)] static extern uint GetModuleBaseName(IntPtr h,IntPtr module,StringBuilder s,int size);

  static readonly string[] Modules3D={"d3d9.dll","d3d11.dll","d3d12.dll","vulkan-1.dll","opengl32.dll"};
  readonly HashSet<string> games=new HashSet<string>(StringComparer.OrdinalIgnoreCase),notGames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  readonly WinEventProc proc; IntPtr hook; readonly DispatcherTimer settle=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(1500)},alive=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
  IntPtr gameHandle; bool enabled; readonly uint self=(uint)Process.GetCurrentProcess().Id;
  public GameInfo Current { get; private set; }
  public event Action<GameInfo> Started,Stopped;

  public GameWatcher() {
   proc=(h,e,w,o,c,t,ti)=>{Check();settle.Stop();settle.Start();}; // игры часто переходят в полноэкранный режим через секунду после появления окна
   settle.Tick+=(s,e)=>{settle.Stop();Check();};
   alive.Tick+=(s,e)=>CheckAlive();
  }
  public void SetLists(IEnumerable<string> myGames,IEnumerable<string> excluded){games.Clear();foreach(var g in myGames)games.Add(g);notGames.Clear();foreach(var n in excluded)notGames.Add(n);}
  public bool Enabled {
   get { return enabled; }
   set {
    if(value==enabled)return; enabled=value;
    if(value){hook=SetWinEventHook(3,3,IntPtr.Zero,proc,0,0,2);alive.Start();Check();} // EVENT_SYSTEM_FOREGROUND, вне процесса
    else{if(hook!=IntPtr.Zero)UnhookWinEvent(hook);hook=IntPtr.Zero;alive.Stop();settle.Stop();Current=null;if(gameHandle!=IntPtr.Zero){CloseHandle(gameHandle);gameHandle=IntPtr.Zero;}} // выключение — не конец игры: событие Stopped не шлём
   }
  }
  public void Recheck(){if(enabled)Check();}

  void Check() {
   if(!enabled)return;
   try {
    IntPtr h=GetForegroundWindow(); if(h==IntPtr.Zero||!IsWindowVisible(h)||IsIconic(h))return;
    uint pid; GetWindowThreadProcessId(h,out pid); if(pid==self||pid==0)return;
    if(Current!=null&&Current.Pid==pid){if(Current.Hwnd!=h&&Covers(h))Current.Hwnd=h;return;}
    var f=Facts(h,pid); string reason=GameRules.Classify(f,games,notGames); if(reason==null)return;
    var t=new StringBuilder(512); GetWindowText(h,t,512); var c=new StringBuilder(256); GetClassName(h,c,256);
    IntPtr ph=OpenProcess(0x1000|0x00100000,false,pid); // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
    if(ph==IntPtr.Zero)return;
    if(gameHandle!=IntPtr.Zero)CloseHandle(gameHandle); gameHandle=ph;
    Current=new GameInfo{Pid=(int)pid,Exe=f.Exe,FileName=System.IO.Path.GetFileName(f.Path),Path=f.Path,Title=t.ToString(),Class=c.ToString(),Hwnd=h,Reason=reason,Started=DateTime.Now};
    if(Started!=null)Started(Current);
   } catch { }
  }
  void CheckAlive(){if(Current==null||gameHandle==IntPtr.Zero)return;uint code;if(GetExitCodeProcess(gameHandle,out code)&&code==259)return;End();} // 259 = STILL_ACTIVE
  void End(){var old=Current;Current=null;if(gameHandle!=IntPtr.Zero){CloseHandle(gameHandle);gameHandle=IntPtr.Zero;}if(old!=null&&Stopped!=null)Stopped(old);}

  static bool Covers(IntPtr h) {
   RECT r; if(DwmGetWindowAttribute(h,9,out r,16)!=0&&!GetWindowRect(h,out r))return false;
   var mi=new MONITORINFO{Size=Marshal.SizeOf(typeof(MONITORINFO))}; if(!GetMonitorInfo(MonitorFromWindow(h,2),ref mi))return false;
   return Math.Abs(r.Left-mi.Monitor.Left)<=2&&Math.Abs(r.Top-mi.Monitor.Top)<=2&&Math.Abs(r.Right-mi.Monitor.Right)<=2&&Math.Abs(r.Bottom-mi.Monitor.Bottom)<=2;
  }
  public static WindowFacts Facts(IntPtr h,uint pid) {
   var f=new WindowFacts{CoversMonitor=Covers(h)};
   IntPtr p=OpenProcess(0x1000,false,pid); // PROCESS_QUERY_LIMITED_INFORMATION
   if(p!=IntPtr.Zero){try{var s=new StringBuilder(1024);int n=s.Capacity;if(QueryFullProcessImageName(p,0,s,ref n)){f.Path=s.ToString();f.Exe=System.IO.Path.GetFileNameWithoutExtension(f.Path);}}finally{CloseHandle(p);}}
   if(f.CoversMonitor)f.Has3D=Has3D(pid); // модули читаем только у полноэкранных окон
   return f;
  }
  static bool? Has3D(uint pid) {
   IntPtr p=OpenProcess(0x0400|0x0010,false,pid); if(p==IntPtr.Zero)return null; // QUERY_INFORMATION | VM_READ; у защищённых античитом — отказ
   try {
    var mods=new IntPtr[1024]; int needed; if(!EnumProcessModulesEx(p,mods,mods.Length*IntPtr.Size,out needed,3))return null;
    var name=new StringBuilder(260);
    for(int i=0;i<Math.Min(mods.Length,needed/IntPtr.Size);i++){name.Length=0;GetModuleBaseName(p,mods[i],name,260);if(Modules3D.Contains(name.ToString().ToLowerInvariant()))return true;}
    return false;
   } finally { CloseHandle(p); }
  }
  public void Dispose(){Enabled=false;}
 }

 // Применение найденной игры к OBS: захват игры — на её окно, сцена — по профилю, затем возврат как было.
 // Работает через функцию запроса к OBS, поэтому проверяется тестом без живого OBS.
 public sealed class GameSession {
  public delegate Task<Dictionary<string,object>> Caller(string request,Dictionary<string,object> data);
  readonly Caller call;
  public Dictionary<string,Dictionary<string,object>> Backup=new Dictionary<string,Dictionary<string,object>>(); // источник → прежние capture_mode/window/priority/capture_cursor
  public string SceneBefore,SceneByGame;
  static readonly string[] Keys={"capture_mode","window","priority","capture_cursor"};
  public GameSession(Caller caller){call=caller;}

  // Перенаправляет все источники «Захват игры» на окно игры. Уже настроенные на этот exe не трогает (иначе захват перецепится).
  public async Task<List<string>> Capture(GameInfo g,bool cursor) {
   var log=new List<string>(); object raw;
   var list=await call("GetInputList",Json.D("inputKind","game_capture")); list.TryGetValue("inputs",out raw);
   foreach(object o in (raw as object[])??new object[0]) {
    string name=Json.Str(Json.Obj(o),"inputName");
    var r=await call("GetInputSettings",Json.D("inputName",name)); var s=Json.Obj(r.ContainsKey("inputSettings")?r["inputSettings"]:null);
    bool same=Json.Str(s,"capture_mode")=="window"&&Json.Str(s,"window").EndsWith(":"+GameInfo.Encode(g.FileName),StringComparison.OrdinalIgnoreCase)&&(!s.ContainsKey("capture_cursor")||Json.Bool(s,"capture_cursor")==cursor);
    if(same){log.Add("«"+name+"» уже захватывает "+g.FileName);continue;}
    if(!Backup.ContainsKey(name)){var b=new Dictionary<string,object>();foreach(string k in Keys)b[k]=s.ContainsKey(k)?s[k]:null;Backup[name]=b;}
    await call("SetInputSettings",Json.D("inputName",name,"inputSettings",Json.D("capture_mode","window","window",g.ObsWindow,"priority",2,"capture_cursor",cursor),"overlay",true));
    log.Add("«"+name+"» → окно "+g.FileName);
   }
   return log;
  }
  public async Task SwitchScene(string target,string current) {
   if(target==null||target==current)return;
   if(SceneBefore==null)SceneBefore=current; SceneByGame=target;
   await call("SetCurrentProgramScene",Json.D("sceneName",target));
  }
  // Возврат: сцена — если её с тех пор не меняли; захват — к прежним настройкам (ключи, которых не было, удаляются).
  public async Task<List<string>> Restore(string current,bool scene) {
   var log=new List<string>();
   if(scene&&!string.IsNullOrEmpty(SceneByGame)&&!string.IsNullOrEmpty(SceneBefore)&&current==SceneByGame){await call("SetCurrentProgramScene",Json.D("sceneName",SceneBefore));log.Add("сцена возвращена: "+SceneBefore);}
   foreach(var pair in Backup.ToList()) {
    try {
     var r=await call("GetInputSettings",Json.D("inputName",pair.Key)); var s=Json.Obj(r.ContainsKey("inputSettings")?r["inputSettings"]:null);
     foreach(var kv in pair.Value){if(kv.Value==null)s.Remove(kv.Key);else s[kv.Key]=kv.Value;}
     await call("SetInputSettings",Json.D("inputName",pair.Key,"inputSettings",s,"overlay",false));
     log.Add("«"+pair.Key+"» — прежний захват");
    } catch(InvalidOperationException) { log.Add("«"+pair.Key+"» не найден — пропущен"); }
   }
   Backup.Clear(); SceneBefore=null; SceneByGame=null;
   return log;
  }
  // Источники «Захват игры» без рамки масштабирования растягиваем на весь кадр с сохранением пропорций:
  // тогда смена разрешения в игре не оставляет картинку маленькой или обрезанной. Уже настроенные не трогаем.
  public async Task<List<string>> FitToCanvas() {
   var log=new List<string>(); object raw;
   var video=await call("GetVideoSettings",Json.D()); int w=Json.Int(video,"baseWidth"),h=Json.Int(video,"baseHeight"); if(w<=0||h<=0)return log;
   var scenes=await call("GetSceneList",Json.D()); scenes.TryGetValue("scenes",out raw);
   foreach(object sc in (raw as object[])??new object[0]) {
    string scene=Json.Str(Json.Obj(sc),"sceneName"); object items;
    (await call("GetSceneItemList",Json.D("sceneName",scene))).TryGetValue("sceneItems",out items);
    foreach(object it in (items as object[])??new object[0]) {
     var item=Json.Obj(it); if(Json.Str(item,"inputKind")!="game_capture")continue; int id=Json.Int(item,"sceneItemId");
     var t=Json.Obj((await call("GetSceneItemTransform",Json.D("sceneName",scene,"sceneItemId",id)))["sceneItemTransform"]);
     if(Json.Str(t,"boundsType")!="OBS_BOUNDS_NONE")continue;
     await call("SetSceneItemTransform",Json.D("sceneName",scene,"sceneItemId",id,"sceneItemTransform",Json.D("boundsType","OBS_BOUNDS_SCALE_INNER","boundsWidth",w,"boundsHeight",h,"boundsAlignment",0,"alignment",5,"positionX",0,"positionY",0)));
     log.Add("«"+scene+"»: захват игры подогнан под кадр "+w+"×"+h);
    }
   }
   return log;
  }
  public static bool HasCapture(object[] items){return items.Any(i=>{string k=Json.Str(Json.Obj(i),"inputKind");return k=="game_capture"||k=="monitor_capture"||k=="window_capture";});}
 }
}
