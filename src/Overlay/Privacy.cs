using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace ReplayOverlay {
 // Следит, видно ли на мониторе окно приложения из списка исключений.
 // Работает от системных событий окон; редкий таймер — только страховка от пропущенного события.
 public sealed class PrivacyWatcher : IDisposable {
  delegate void WinEventProc(IntPtr hook,uint evt,IntPtr hwnd,int idObject,int idChild,uint thread,uint time);
  delegate bool EnumProc(IntPtr h,IntPtr p);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WinEventProc proc,uint pid,uint tid,uint flags);
  [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc,IntPtr p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h,uint flags);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h,uint cmd);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr h,int index);
  [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h,out uint key,out byte alpha,out uint flags);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int attr,out RECT r,int size);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int attr,out int v,int size);
  [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
  [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst,IntPtr a,IntPtr b,int mode);
  [DllImport("gdi32.dll")] static extern int GetRgnBox(IntPtr rgn,out RECT r);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h,int flags,StringBuilder s,ref int size);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  // Имя процесса без перебора всех процессов системы (Process.GetProcessById заметно медленнее).
  static string ProcessName(uint pid) {
   IntPtr h=OpenProcess(0x1000,false,pid); if(h==IntPtr.Zero)return ""; // PROCESS_QUERY_LIMITED_INFORMATION
   try { var s=new StringBuilder(1024); int n=s.Capacity; return QueryFullProcessImageName(h,0,s,ref n)?System.IO.Path.GetFileNameWithoutExtension(s.ToString()):""; } finally { CloseHandle(h); }
  }

  const uint ForegroundEvent=0x0003,MoveSizeStart=0x000A,MoveSizeEnd=0x000B,SwitchStart=0x0014,SwitchEnd=0x0015,MinimizeStart=0x0016,MinimizeEnd=0x0017;
  const uint ObjectDestroy=0x8001,ObjectShow=0x8002,ObjectHide=0x8003,ObjectCloaked=0x8017,ObjectUncloaked=0x8018;
  // Окна Alt+Tab, Win+Tab и превью панели задач показывают миниатюры всех окон, даже свёрнутых.
  static readonly string[] SwitcherClasses={"XamlExplorerHostIslandWindow","MultitaskingViewFrame","TaskSwitcherWnd","TaskListThumbnailWnd"};

  readonly HashSet<string> excluded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<uint,string> names=new Dictionary<uint,string>();
  readonly List<IntPtr> hooks=new List<IntPtr>();
  readonly WinEventProc proc;
  readonly DispatcherTimer debounce=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(60)};
  readonly DispatcherTimer safety=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(1500)};
  uint self=(uint)Process.GetCurrentProcess().Id;
  public uint IgnoredProcess { get { return self; } set { self=value; } } // для тестов: 0 — учитывать и свои окна
  DateTime namesAge=DateTime.MinValue,lastCheck=DateTime.MinValue; bool enabled; string last="";
  // Адресные хуки на процессы исключённых программ: перемещение и смена порядка окон. Глобально эти события
  // слушать нельзя (их порождает каждое движение мыши), а без них окно, вынесенное на экран без активации,
  // замечалось бы только страховочной проверкой раз в 1,5 с.
  readonly Dictionary<uint,IntPtr> pidHooks=new Dictionary<uint,IntPtr>(); readonly HashSet<uint> seenPids=new HashSet<uint>();
  const uint ObjectReorder=0x8004,ObjectLocationChange=0x800B;
  public event Action<string> Changed; // пустая строка — исключённых окон на экране нет

  public PrivacyWatcher() {
   proc=OnEvent;
   debounce.Tick+=(s,e)=>{debounce.Stop();Check();};
   safety.Tick+=(s,e)=>Check();
  }
  public string Visible { get { return last; } }
  public void SetExcluded(IEnumerable<string> list) { excluded.Clear(); foreach(var x in list)excluded.Add(x); names.Clear(); }
  public bool Enabled {
   get { return enabled; }
   set {
    if(value==enabled)return; enabled=value;
    if(value) {
     foreach(var r in new[]{new[]{ForegroundEvent,ForegroundEvent},new[]{MoveSizeStart,MoveSizeEnd},new[]{SwitchStart,MinimizeEnd},new[]{ObjectDestroy,ObjectHide},new[]{ObjectCloaked,ObjectUncloaked}}) {
      var h=SetWinEventHook(r[0],r[1],IntPtr.Zero,proc,0,0,2); // WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS
      if(h!=IntPtr.Zero)hooks.Add(h);
     }
     safety.Interval=TimeSpan.FromMilliseconds(1500); safety.Start(); Check();
    } else {
     foreach(var h in hooks)UnhookWinEvent(h); hooks.Clear(); foreach(var h in pidHooks.Values)UnhookWinEvent(h); pidHooks.Clear(); safety.Stop(); debounce.Stop(); Report("");
    }
   }
  }
  public void Kick() { if(enabled)Check(); }

  void OnEvent(IntPtr hook,uint evt,IntPtr hwnd,int idObject,int idChild,uint thread,uint time) {
   if(evt>=ObjectReorder&&evt<=ObjectLocationChange) { // только от процессов исключённых программ (адресные хуки)
    if(evt==ObjectLocationChange&&(idObject!=0||hwnd==IntPtr.Zero||GetAncestor(hwnd,2)!=hwnd))return;
    if((DateTime.Now-lastCheck).TotalMilliseconds>=30)Check(); else if(!debounce.IsEnabled)debounce.Start();
    return;
   }
   if(evt>=0x8000) { if(idObject!=0||idChild!=0||hwnd==IntPtr.Zero||GetAncestor(hwnd,2)!=hwnd)return; }
   if(evt==MoveSizeStart) { safety.Interval=TimeSpan.FromMilliseconds(100); return; } // пока окно тащат — проверяем чаще
   if(evt==MoveSizeEnd)safety.Interval=TimeSpan.FromMilliseconds(1500);
   // Появление окна проверяем сразу: скрыть нужно как можно раньше. Остальное — с небольшой задержкой.
   if(evt==ForegroundEvent||evt==ObjectShow||evt==ObjectUncloaked||evt==MinimizeEnd||evt==SwitchStart||evt==MoveSizeEnd)Check();
   else if(!debounce.IsEnabled)debounce.Start();
  }

  void Check() { if(!enabled)return; lastCheck=DateTime.Now; try { Report(string.Join(", ",Evaluate())); SyncPidHooks(); } catch { } }
  void SyncPidHooks() {
   foreach(uint pid in seenPids.Where(p=>!pidHooks.ContainsKey(p)).ToList()){var h=SetWinEventHook(ObjectReorder,ObjectLocationChange,IntPtr.Zero,proc,pid,0,0);if(h!=IntPtr.Zero)pidHooks[pid]=h;}
   foreach(uint pid in pidHooks.Keys.Where(p=>!seenPids.Contains(p)).ToList()){UnhookWinEvent(pidHooks[pid]);pidHooks.Remove(pid);}
  }
  void Report(string value) { if(value==last)return; last=value; if(Changed!=null)Changed(value); }

  string Name(uint pid) {
   if((DateTime.Now-namesAge).TotalSeconds>30){names.Clear();namesAge=DateTime.Now;}
   string n; if(names.TryGetValue(pid,out n))return n;
   n=ProcessName(pid); names[pid]=n; return n;
  }
  static bool Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h,14,out v,4)==0&&v!=0; }
  static string Class(IntPtr h) { var s=new StringBuilder(128); GetClassName(h,s,128); return s.ToString(); }
  // Всплывающие уведомления Windows рисует оболочка (ShellExperienceHost), а не приложение-отправитель,
  // поэтому исключение приложения их не скрывает. Отдельная настройка — скрывать любое такое уведомление.
  public bool Notifications;
  public const string NotificationsLabel="уведомления Windows";
  static bool IsToast(string process,IntPtr h) {
   if(!string.Equals(process,"ShellExperienceHost",StringComparison.OrdinalIgnoreCase)&&!string.Equals(process,"ShellHost",StringComparison.OrdinalIgnoreCase))return false;
   if(Class(h)!="Windows.UI.Core.CoreWindow")return false;
   var t=new StringBuilder(256); GetWindowText(h,t,256); string title=t.ToString().ToLowerInvariant();
   return title.Contains("уведомлен")||title.Contains("notification");
  }
  static bool Bounds(IntPtr h,out RECT r) { return DwmGetWindowAttribute(h,9,out r,16)==0||GetWindowRect(h,out r); }
  static bool Opaque(IntPtr h,int ex) {
   if((ex&0x80000)==0)return true; // не layered — обычное непрозрачное окно
   uint key,flags; byte alpha; // layered: перекрывающим считаем только окно с явной полной непрозрачностью
   return GetLayeredWindowAttributes(h,out key,out alpha,out flags)&&(flags&2)!=0&&(flags&1)==0&&alpha==255;
  }

  // Список исключённых приложений, чьи окна сейчас видны хотя бы частично (с учётом перекрытия другими окнами).
  public List<string> Evaluate() {
   var found=new List<string>(); if(excluded.Count==0&&!Notifications)return found;
   var screen=System.Windows.Forms.SystemInformation.VirtualScreen;
   bool switcher=false; var hasWindows=new HashSet<string>(StringComparer.OrdinalIgnoreCase); seenPids.Clear();
   IntPtr covered=CreateRectRgn(0,0,0,0);
   try {
    EnumWindows((h,p)=>{ // порядок EnumWindows — сверху вниз по Z
     if(!IsWindowVisible(h)||Cloaked(h))return true;
     uint pid; GetWindowThreadProcessId(h,out pid); if(pid==self)return true;
     string name=Name(pid); bool toast=Notifications&&IsToast(name,h); bool isExcluded=toast||excluded.Contains(name);
     if(isExcluded&&!toast){hasWindows.Add(name);seenPids.Add(pid);}
     if(toast)name=NotificationsLabel;
     if(IsIconic(h))return true;
     if(Array.IndexOf(SwitcherClasses,Class(h))>=0){switcher=true;return true;}
     RECT r; if(!Bounds(h,out r))return true;
     r.Left=Math.Max(r.Left,screen.Left); r.Top=Math.Max(r.Top,screen.Top); r.Right=Math.Min(r.Right,screen.Right); r.Bottom=Math.Min(r.Bottom,screen.Bottom);
     if(r.Right-r.Left<2||r.Bottom-r.Top<2)return true;
     int ex=GetWindowLong(h,-20); bool clickThrough=(ex&0x20)!=0&&(ex&0x80000)!=0; // прозрачные для мыши оверлеи
     if(clickThrough)return true;
     IntPtr w=CreateRectRgn(r.Left,r.Top,r.Right,r.Bottom);
     try {
      if(isExcluded&&!found.Contains(name)) {
       IntPtr rest=CreateRectRgn(0,0,0,0); RECT box;
       if(CombineRgn(rest,w,covered,4)>1&&GetRgnBox(rest,out box)>1&&box.Right-box.Left>=8&&box.Bottom-box.Top>=8)found.Add(name);
       DeleteObject(rest);
      }
      if(Opaque(h,ex))CombineRgn(covered,covered,w,2);
     } finally { DeleteObject(w); }
     return true;
    },IntPtr.Zero);
   } finally { DeleteObject(covered); }
   if(switcher)foreach(var n in hasWindows)if(!found.Contains(n))found.Add(n);
   found.Sort(StringComparer.OrdinalIgnoreCase);
   return found;
  }

  // Запущенные приложения с обычными окнами: имя процесса → заголовок одного из окон.
  public static SortedDictionary<string,string> RunningApps() {
   var apps=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase); uint self=(uint)Process.GetCurrentProcess().Id;
   var cache=new Dictionary<uint,string>();
   EnumWindows((h,p)=>{
    if(!IsWindowVisible(h)||Cloaked(h)||GetWindow(h,4)!=IntPtr.Zero)return true; // 4 = GW_OWNER
    if((GetWindowLong(h,-20)&0x80)!=0)return true; // WS_EX_TOOLWINDOW
    var t=new StringBuilder(256); if(GetWindowText(h,t,256)==0)return true;
    uint pid; GetWindowThreadProcessId(h,out pid); if(pid==self)return true;
    string n; if(!cache.TryGetValue(pid,out n)){ n=ProcessName(pid); cache[pid]=n; }
    if(n.Length>0&&!apps.ContainsKey(n))apps[n]=t.ToString();
    return true;
   },IntPtr.Zero);
   return apps;
  }

  public void Dispose() { Enabled=false; }
 }

 // Источник в программной сцене — то, что нужно советчику по приватности.
 public sealed class SceneSource { public string Name="",Kind="",Window=""; public bool CaptureOverlays,Nested,NestedHasMonitor; }

 // Где исключение технически не сработает — чтобы честно предупредить, а не делать вид, что всё скрыто.
 public static class PrivacyAdvisor {
  static readonly string[] OverlayApps={"Discord","steam","steamwebhelper","Overwolf","NVIDIA Overlay","nvcontainer","GameBar","XboxGameBarWidgets","Medal"};
  public static string ExeOf(string obsWindow) {
   if(string.IsNullOrEmpty(obsWindow))return ""; int i=obsWindow.LastIndexOf(':'); string exe=i>=0?obsWindow.Substring(i+1):obsWindow;
   exe=exe.Replace("#3A",":").Replace("#22","#"); return exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?exe.Substring(0,exe.Length-4):exe;
  }
  public static List<string> Warnings(IEnumerable<SceneSource> items,ICollection<string> excluded,string gameExe,ICollection<string> myGames,bool hasMonitor) {
   var w=new List<string>();
   foreach(var s in items) {
    if(s.Kind=="window_capture"){string exe=ExeOf(s.Window);if(exe.Length>0&&excluded.Contains(exe))w.Add("«"+s.Name+"» захватывает окно "+exe+".exe напрямую — меню его не скроет. Уберите источник или программу из исключений.");}
    if(s.Kind=="game_capture"&&s.CaptureOverlays){var o=OverlayApps.Where(excluded.Contains).ToList();if(o.Count>0)w.Add("«"+s.Name+"» захватывает оверлеи поверх игры — окна "+string.Join(", ",o.Select(x=>x+".exe"))+" попадут в запись. Выключите «Захватывать сторонние оверлеи» в свойствах источника.");}
    if(s.Nested&&s.NestedHasMonitor)w.Add("Захват экрана во вложенной сцене или группе «"+s.Name+"» — меню не может его скрывать. Перенесите захват экрана прямо в сцену.");
   }
   if(!string.IsNullOrEmpty(gameExe)&&excluded.Contains(gameExe))w.Add(gameExe+".exe — это текущая игра: её окно и есть запись, скрыть его нельзя.");
   foreach(string g in myGames.Where(excluded.Contains))if(g!=gameExe)w.Add(g+".exe есть и в «Моих играх», и в исключениях: во время игры её окно будет в записи.");
   if(excluded.Count>0&&!hasMonitor&&w.Count==0)w.Add("В текущей сцене нет захвата экрана — скрывать нечего: другие окна в запись и так не попадают.");
   return w;
  }
 }

 // Строгая защита: захват экрана идёт с задержкой (фильтр «Задержка рендеринга»), а скрытие — «шторкой» поверх него.
 // Меню замечает окно за 10–30 мс, а его кадры доходят до записи только через задержку — поэтому успевают закрыться.
 // Звук всех источников сдвигается на ту же задержку, чтобы картинка и звук не разошлись.
 public sealed class StrictPrivacy {
  public delegate System.Threading.Tasks.Task<Dictionary<string,object>> Caller(string request,Dictionary<string,object> data);
  readonly Caller call;
  public const string CoverScene="Replay · скрытие",CoverColor="Replay · фон скрытия",FilterName="Replay · задержка экрана";
  public int AppliedDelay;                                                       // мс, 0 — выключено
  public Dictionary<string,int> CoverItems=new Dictionary<string,int>();        // сцена → id «шторки» в ней
  public List<string> Delayed=new List<string>(),Shifted=new List<string>();   // источники с фильтром задержки / со сдвигом звука
  public StrictPrivacy(Caller caller){call=caller;}
  public bool On { get { return AppliedDelay>0; } }

  async System.Threading.Tasks.Task<List<Dictionary<string,object>>> Items(string scene){object raw;(await call("GetSceneItemList",Json.D("sceneName",scene))).TryGetValue("sceneItems",out raw);return ((raw as object[])??new object[0]).Select(Json.Obj).ToList();}

  public async System.Threading.Tasks.Task<List<string>> Enable(int delayMs,IList<string> monitorScenes) {
   var log=new List<string>(); if(On)await Disable();
   var video=await call("GetVideoSettings",Json.D()); int w=Json.Int(video,"baseWidth",1920),h=Json.Int(video,"baseHeight",1080);
   object raw; (await call("GetSceneList",Json.D())).TryGetValue("scenes",out raw);
   if(!((raw as object[])??new object[0]).Any(s=>Json.Str(Json.Obj(s),"sceneName")==CoverScene)) {
    await call("CreateScene",Json.D("sceneName",CoverScene));
    await call("CreateInput",Json.D("sceneName",CoverScene,"inputName",CoverColor,"inputKind","color_source_v3","inputSettings",Json.D("color",0xFF141210L,"width",w,"height",h),"sceneItemEnabled",true));
    // Поверх фона — захват игры, растянутый на кадр: пока окно скрыто, в записи остаётся игра.
    object inputs; (await call("GetInputList",Json.D("inputKind","game_capture"))).TryGetValue("inputs",out inputs);
    foreach(object i in (inputs as object[])??new object[0]) {
     int id=Json.Int(await call("CreateSceneItem",Json.D("sceneName",CoverScene,"sourceName",Json.Str(Json.Obj(i),"inputName"))),"sceneItemId");
     await call("SetSceneItemTransform",Json.D("sceneName",CoverScene,"sceneItemId",id,"sceneItemTransform",Json.D("boundsType","OBS_BOUNDS_SCALE_INNER","boundsWidth",w,"boundsHeight",h,"boundsAlignment",0,"alignment",5,"positionX",0,"positionY",0)));
    }
    log.Add("создана сцена «"+CoverScene+"»");
   }
   foreach(string scene in monitorScenes) {
    var items=await Items(scene);
    foreach(var it in items.Where(i=>Json.Str(i,"inputKind")=="monitor_capture")) {
     string src=Json.Str(it,"sourceName"); if(Delayed.Contains(src))continue;
     await call("CreateSourceFilter",Json.D("sourceName",src,"filterName",FilterName,"filterKind","gpu_delay","filterSettings",Json.D("delay_ms",delayMs)));
     Delayed.Add(src); log.Add("«"+src+"»: задержка "+delayMs+" мс");
    }
    var cover=items.FirstOrDefault(i=>Json.Str(i,"sourceName")==CoverScene);
    int coverId=cover!=null?Json.Int(cover,"sceneItemId"):Json.Int(await call("CreateSceneItem",Json.D("sceneName",scene,"sourceName",CoverScene,"sceneItemEnabled",false)),"sceneItemId");
    await call("SetSceneItemEnabled",Json.D("sceneName",scene,"sceneItemId",coverId,"sceneItemEnabled",false));
    CoverItems[scene]=coverId;
   }
   // Звук — на ту же задержку, иначе он опередит картинку экрана.
   object all; (await call("GetInputList",Json.D())).TryGetValue("inputs",out all);
   foreach(object o in (all as object[])??new object[0]) {
    string name=Json.Str(Json.Obj(o),"inputName"); int offset;
    try{offset=Json.Int(await call("GetInputAudioSyncOffset",Json.D("inputName",name)),"inputAudioSyncOffset");}catch(InvalidOperationException){continue;} // без звука
    await call("SetInputAudioSyncOffset",Json.D("inputName",name,"inputAudioSyncOffset",Math.Min(20000,offset+delayMs)));Shifted.Add(name);
   }
   if(Shifted.Count>0)log.Add("звук "+Shifted.Count+" источников сдвинут на "+delayMs+" мс");
   AppliedDelay=delayMs; return log;
  }

  public async System.Threading.Tasks.Task<List<string>> Disable() {
   var log=new List<string>();
   foreach(string src in Delayed.ToList()){try{await call("RemoveSourceFilter",Json.D("sourceName",src,"filterName",FilterName));}catch(InvalidOperationException){}Delayed.Remove(src);}
   foreach(string name in Shifted.ToList()){try{int offset=Json.Int(await call("GetInputAudioSyncOffset",Json.D("inputName",name)),"inputAudioSyncOffset");await call("SetInputAudioSyncOffset",Json.D("inputName",name,"inputAudioSyncOffset",Math.Max(-950,offset-AppliedDelay)));}catch(InvalidOperationException){}Shifted.Remove(name);}
   foreach(var kv in CoverItems.ToList()){try{await call("RemoveSceneItem",Json.D("sceneName",kv.Key,"sceneItemId",kv.Value));}catch(InvalidOperationException){}}
   CoverItems.Clear();
   try{await call("RemoveScene",Json.D("sceneName",CoverScene));}catch(InvalidOperationException){}
   try{await call("RemoveInput",Json.D("inputName",CoverColor));}catch(InvalidOperationException){}
   AppliedDelay=0; log.Add("строгая защита выключена: задержка, сдвиг звука и «шторка» убраны");
   return log;
  }
  public async System.Threading.Tasks.Task SetCover(string scene,bool on){int id;if(CoverItems.TryGetValue(scene,out id))await call("SetSceneItemEnabled",Json.D("sceneName",scene,"sceneItemId",id,"sceneItemEnabled",on));}

  public Dictionary<string,object> ToJson(){return Json.D("delay",AppliedDelay,"cover",CoverItems,"delayed",Delayed.ToArray(),"shifted",Shifted.ToArray());}
  public void Load(Dictionary<string,object> d){
   AppliedDelay=Json.Int(d,"delay"); object raw;
   if(d.TryGetValue("cover",out raw))foreach(var kv in Json.Obj(raw))CoverItems[kv.Key]=Convert.ToInt32(kv.Value);
   if(d.TryGetValue("delayed",out raw)&&raw is object[])foreach(object x in (object[])raw)Delayed.Add(Convert.ToString(x));
   if(d.TryGetValue("shifted",out raw)&&raw is object[])foreach(object x in (object[])raw)Shifted.Add(Convert.ToString(x));
  }
 }
}
