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
  DateTime namesAge=DateTime.MinValue; bool enabled; string last="";
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
     foreach(var h in hooks)UnhookWinEvent(h); hooks.Clear(); safety.Stop(); debounce.Stop(); Report("");
    }
   }
  }
  public void Kick() { if(enabled)Check(); }

  void OnEvent(IntPtr hook,uint evt,IntPtr hwnd,int idObject,int idChild,uint thread,uint time) {
   if(evt>=0x8000) { if(idObject!=0||idChild!=0||hwnd==IntPtr.Zero||GetAncestor(hwnd,2)!=hwnd)return; }
   if(evt==MoveSizeStart) { safety.Interval=TimeSpan.FromMilliseconds(100); return; } // пока окно тащат — проверяем чаще
   if(evt==MoveSizeEnd)safety.Interval=TimeSpan.FromMilliseconds(1500);
   // Появление окна проверяем сразу: скрыть нужно как можно раньше. Остальное — с небольшой задержкой.
   if(evt==ForegroundEvent||evt==ObjectShow||evt==ObjectUncloaked||evt==MinimizeEnd||evt==SwitchStart||evt==MoveSizeEnd)Check();
   else if(!debounce.IsEnabled)debounce.Start();
  }

  void Check() { if(!enabled)return; try { Report(string.Join(", ",Evaluate())); } catch { } }
  void Report(string value) { if(value==last)return; last=value; if(Changed!=null)Changed(value); }

  string Name(uint pid) {
   if((DateTime.Now-namesAge).TotalSeconds>30){names.Clear();namesAge=DateTime.Now;}
   string n; if(names.TryGetValue(pid,out n))return n;
   n=ProcessName(pid); names[pid]=n; return n;
  }
  static bool Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h,14,out v,4)==0&&v!=0; }
  static string Class(IntPtr h) { var s=new StringBuilder(128); GetClassName(h,s,128); return s.ToString(); }
  static bool Bounds(IntPtr h,out RECT r) { return DwmGetWindowAttribute(h,9,out r,16)==0||GetWindowRect(h,out r); }
  static bool Opaque(IntPtr h,int ex) {
   if((ex&0x80000)==0)return true; // не layered — обычное непрозрачное окно
   uint key,flags; byte alpha; // layered: перекрывающим считаем только окно с явной полной непрозрачностью
   return GetLayeredWindowAttributes(h,out key,out alpha,out flags)&&(flags&2)!=0&&(flags&1)==0&&alpha==255;
  }

  // Список исключённых приложений, чьи окна сейчас видны хотя бы частично (с учётом перекрытия другими окнами).
  public List<string> Evaluate() {
   var found=new List<string>(); if(excluded.Count==0)return found;
   var screen=System.Windows.Forms.SystemInformation.VirtualScreen;
   bool switcher=false; var hasWindows=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   IntPtr covered=CreateRectRgn(0,0,0,0);
   try {
    EnumWindows((h,p)=>{ // порядок EnumWindows — сверху вниз по Z
     if(!IsWindowVisible(h)||Cloaked(h))return true;
     uint pid; GetWindowThreadProcessId(h,out pid); if(pid==self)return true;
     string name=Name(pid); bool isExcluded=excluded.Contains(name);
     if(isExcluded)hasWindows.Add(name);
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
}
