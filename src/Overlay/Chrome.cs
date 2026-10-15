using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ellipse=System.Windows.Shapes.Ellipse;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Окна поверх игры, которые не попадают в запись, пропускают мышь и не забирают фокус.
 static class OverlayWindows {
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
  [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int index,int value);
  [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h,uint affinity);
  public static Window Create(string title,bool capturable){
   var w=new Window{Title=title,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Topmost=true,ShowInTaskbar=false,ShowActivated=false,Focusable=false,SizeToContent=SizeToContent.WidthAndHeight,FontFamily=new FontFamily("Segoe UI"),UseLayoutRounding=true};
   w.SourceInitialized+=(s,e)=>{IntPtr h=new WindowInteropHelper(w).Handle;SetWindowLong(h,-20,GetWindowLong(h,-20)|0x20|0x80|0x08000000);if(!capturable)SetWindowDisplayAffinity(h,0x11);};
   return w;}
  public static Brush B(string c){var b=(Brush)new BrushConverter().ConvertFromString(c);b.Freeze();return b;}
  // Рабочая область монитора с указанным окном — в единицах WPF.
  public static Rect Area(IntPtr anchor,Visual v){var a=WF.Screen.FromHandle(anchor).WorkingArea;double sx=1,sy=1;var src=v==null?null:PresentationSource.FromVisual(v);
   if(src!=null&&src.CompositionTarget!=null){sx=src.CompositionTarget.TransformFromDevice.M11;sy=src.CompositionTarget.TransformFromDevice.M22;}
   return new Rect(a.Left*sx,a.Top*sy,a.Width*sx,a.Height*sy);}
  public static FontFamily Icons=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
 }

 public enum ToastKind{Info,Ok,Error,Warn,Record,Shot}

 // Уведомления справа сверху: карточка с логотипом, заголовком, именем файла и значком результата.
 // Не больше трёх на экране, остальные ждут в очереди.
 public sealed class ToastHost {
  const int MaxVisible=3;const double Gap=10,Margin=28;
  readonly bool capturable;readonly Func<IntPtr> anchor;
  readonly List<Window> shown=new List<Window>();readonly Queue<Tuple<ToastKind,string,string>> waiting=new Queue<Tuple<ToastKind,string,string>>();
  public bool Enabled=true;
  public ToastHost(bool capturableForTests,Func<IntPtr> anchorWindow){capturable=capturableForTests;anchor=anchorWindow;}

  public void Show(ToastKind kind,string title,string detail){
   if(!Enabled)return;
   if(shown.Count>=MaxVisible){waiting.Enqueue(Tuple.Create(kind,title,detail));return;}
   var w=Build(kind,title,detail);shown.Add(w);w.Opacity=0;w.Show();Layout(true);
   var tr=new TranslateTransform(24,0);((FrameworkElement)w.Content).RenderTransform=tr;
   w.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)));
   tr.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(24,0,TimeSpan.FromMilliseconds(220)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
   var t=new DispatcherTimer{Interval=TimeSpan.FromSeconds(kind==ToastKind.Error||kind==ToastKind.Warn?7:4)};
   t.Tick+=(s,e)=>{t.Stop();var fade=new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(200));fade.Completed+=(s2,e2)=>{w.Close();shown.Remove(w);Layout(false);if(waiting.Count>0){var n=waiting.Dequeue();Show(n.Item1,n.Item2,n.Item3);}};w.BeginAnimation(UIElement.OpacityProperty,fade);};t.Start();
  }
  public void CloseAll(){foreach(var w in shown.ToList())w.Close();shown.Clear();waiting.Clear();}

  void Layout(bool fresh){
   if(shown.Count==0)return;var area=OverlayWindows.Area(anchor(),shown[0]);double y=area.Top+Margin;
   foreach(var w in shown){w.UpdateLayout();w.Left=area.Right-w.ActualWidth-Margin;w.Top=y;y+=w.ActualHeight+Gap;}
  }

  Window Build(ToastKind kind,string title,string detail){
   var w=OverlayWindows.Create("Replay — уведомление",capturable);
   var logo=new Grid{Width=34,Height=34,VerticalAlignment=VerticalAlignment.Center};
   var ring=new Ellipse{Stroke=OverlayWindows.B("#00D46A"),StrokeThickness=3};logo.Children.Add(ring);logo.Children.Add(new Ellipse{Width=13,Height=13,Fill=OverlayWindows.B("#00D46A")});
   if(kind==ToastKind.Ok)ring.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0.3,1,TimeSpan.FromMilliseconds(500)){AutoReverse=true,RepeatBehavior=new RepeatBehavior(2)});
   var texts=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(14,0,12,0)};
   texts.Children.Add(new TextBlock{Text=title,FontSize=14,FontWeight=FontWeights.SemiBold,Foreground=OverlayWindows.B("#F5F6F7"),TextTrimming=TextTrimming.CharacterEllipsis});
   if(!string.IsNullOrEmpty(detail))texts.Children.Add(new TextBlock{Text=detail,FontSize=11.5,Foreground=OverlayWindows.B("#9CA3AF"),Margin=new Thickness(0,4,0,0),TextWrapping=TextWrapping.Wrap,MaxHeight=48});
   string color=kind==ToastKind.Ok?"#00D46A":kind==ToastKind.Error||kind==ToastKind.Record?"#F04452":kind==ToastKind.Warn?"#F59E0B":kind==ToastKind.Shot?"#00D46A":"#3A414B";
   string glyph=kind==ToastKind.Ok?"":kind==ToastKind.Error?"":kind==ToastKind.Warn?"":kind==ToastKind.Record?"":kind==ToastKind.Shot?"":"";
   var badge=new Grid{Width=24,Height=24,VerticalAlignment=VerticalAlignment.Center};badge.Children.Add(new Ellipse{Fill=OverlayWindows.B(color)});
   badge.Children.Add(new TextBlock{Text=glyph,FontFamily=OverlayWindows.Icons,FontSize=11,Foreground=OverlayWindows.B(kind==ToastKind.Info?"#F5F6F7":"#06140C"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
   var g=new Grid();g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});g.ColumnDefinitions.Add(new ColumnDefinition());g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   g.Children.Add(logo);Grid.SetColumn(texts,1);g.Children.Add(texts);Grid.SetColumn(badge,2);g.Children.Add(badge);
   w.Content=new Border{Width=380,MinHeight=76,Background=OverlayWindows.B("#F2121419"),BorderBrush=OverlayWindows.B("#59343A43"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),Padding=new Thickness(16,12,16,12),Child=g};
   return w;
  }
 }

 // Подсказка горячих клавиш справа снизу — видна, пока открыто меню.
 public sealed class KeysPanel {
  readonly bool capturable;Window w;StackPanel rows;public bool Enabled=true;
  public KeysPanel(bool capturableForTests){capturable=capturableForTests;}
  public void Show(IntPtr anchor,IEnumerable<KeyValuePair<string,string>> keys){
   if(!Enabled)return;var list=keys.Where(k=>!string.IsNullOrEmpty(k.Key)).ToList();if(list.Count==0){Hide();return;}
   if(w==null){w=OverlayWindows.Create("Replay — клавиши",capturable);rows=new StackPanel();
    w.Content=new Border{Width=280,Background=OverlayWindows.B("#E6121419"),BorderBrush=OverlayWindows.B("#59343A43"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(12,10,12,6),Child=rows,RenderTransform=new TranslateTransform()};}
   rows.Children.Clear();
   foreach(var k in list){var g=new Grid{Margin=new Thickness(0,0,0,6)};g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(96)});g.ColumnDefinitions.Add(new ColumnDefinition());
    g.Children.Add(new Border{Background=OverlayWindows.B("#2A2F37"),CornerRadius=new CornerRadius(6),Padding=new Thickness(8,3,8,3),HorizontalAlignment=HorizontalAlignment.Left,Child=new TextBlock{Text=k.Key.Replace("+"," + "),FontSize=11.5,Foreground=OverlayWindows.B("#E6E8EB")}});
    var t=new TextBlock{Text=k.Value,FontSize=11.5,Foreground=OverlayWindows.B("#9CA3AF"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(t,1);g.Children.Add(t);rows.Children.Add(g);}
   if(!w.IsVisible){w.Opacity=0;w.Show();}
   w.UpdateLayout();var area=OverlayWindows.Area(anchor,w);w.Left=area.Right-w.ActualWidth-24;w.Top=area.Bottom-w.ActualHeight-24;
   w.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(1,TimeSpan.FromMilliseconds(200)));
   ((TranslateTransform)((Border)w.Content).RenderTransform).BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(10,0,TimeSpan.FromMilliseconds(200)));
  }
  public void Hide(){if(w==null||!w.IsVisible)return;var a=new DoubleAnimation(0,TimeSpan.FromMilliseconds(180));a.Completed+=(s,e)=>{if(w!=null&&w.Opacity<0.05)w.Hide();};w.BeginAnimation(UIElement.OpacityProperty,a);
   ((TranslateTransform)((Border)w.Content).RenderTransform).BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(0,10,TimeSpan.FromMilliseconds(180)));}
  public void Dispose(){if(w!=null){w.Close();w=null;}}
 }

 // Размытие игры под панелью: акриловый фон DWM (Windows 11 22H2 и новее). На старых Windows — прежнее полупрозрачное окно.
 public static class Backdrop {
  [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int attr,ref int value,int size);
  [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h,ref Margins m);
  struct Margins{public int Left,Right,Top,Bottom;}
  public static bool Supported{get{try{int n;return int.TryParse(Convert.ToString(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","CurrentBuildNumber","0")),out n)&&n>=22621;}catch(Exception){return false;}}}
  // До создания окна: без слоёной прозрачности (с ней DWM не рисует фон) и со «стеклом» на всю площадь.
  public static void Prepare(Window w){
   w.AllowsTransparency=false;w.Background=Brushes.Transparent;
   System.Windows.Shell.WindowChrome.SetWindowChrome(w,new System.Windows.Shell.WindowChrome{GlassFrameThickness=new Thickness(-1),CaptionHeight=0,ResizeBorderThickness=new Thickness(0),CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false});}
  // После создания окна: прозрачная подложка WPF, тёмная тема, скруглённые углы, акрил. false — Windows отказалась.
  public static bool Apply(IntPtr h){
   var src=HwndSource.FromHwnd(h);if(src!=null&&src.CompositionTarget!=null)src.CompositionTarget.BackgroundColor=Colors.Transparent;
   var m=new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1};DwmExtendFrameIntoClientArea(h,ref m);
   int dark=1;DwmSetWindowAttribute(h,20,ref dark,4);int round=2;DwmSetWindowAttribute(h,33,ref round,4);
   int acrylic=3;return DwmSetWindowAttribute(h,38,ref acrylic,4)==0;}
 }

 // Снимок активного окна средствами Windows (без OBS). Окна, исключённые из захвата (меню Replay), в снимок не попадают.
 public static class WindowShot {
  [StructLayout(LayoutKind.Sequential)] struct RECT{public int Left,Top,Right,Bottom;}
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int attr,out RECT r,int size);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  public static string ExeOf(IntPtr h){uint pid;GetWindowThreadProcessId(h,out pid);try{return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;}catch(Exception){return "Window";}}
  public static void Save(IntPtr h,string file,bool jpeg,int quality){
   if(h==IntPtr.Zero)throw new InvalidOperationException("Нет активного окна");
   if(IsIconic(h))throw new InvalidOperationException("Окно свёрнуто");
   RECT r;if(DwmGetWindowAttribute(h,9,out r,Marshal.SizeOf(typeof(RECT)))!=0)GetWindowRect(h,out r); // 9 — видимые границы без тени
   int w=r.Right-r.Left,hh=r.Bottom-r.Top;if(w<=0||hh<=0)throw new InvalidOperationException("Окно не видно на экране");
   using(var bmp=new System.Drawing.Bitmap(w,hh))using(var g=System.Drawing.Graphics.FromImage(bmp)){
    g.CopyFromScreen(r.Left,r.Top,0,0,new System.Drawing.Size(w,hh));
    if(!jpeg){bmp.Save(file,System.Drawing.Imaging.ImageFormat.Png);return;}
    var codec=System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
    var ep=new System.Drawing.Imaging.EncoderParameters(1);ep.Param[0]=new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality,(long)Math.Max(1,Math.Min(100,quality)));bmp.Save(file,codec,ep);}}
 }

 // Заполненность диска: шкала показывает занятое место, текст — свободное.
 public sealed class DiskInfo {
  public string Name;public long Total,Free;public bool Available;
  public double UsedFraction{get{return Total>0?(Total-Free)/(double)Total:0;}}
  public static DiskInfo For(string folder){
   var d=new DiskInfo{Name="Диск"};
   try{if(string.IsNullOrEmpty(folder))return d;string root=Path.GetPathRoot(Path.GetFullPath(folder));if(string.IsNullOrEmpty(root))return d;
    var drive=new DriveInfo(root);d.Name=root.Length>=2&&root[1]==':'?"Диск ("+root.Substring(0,2).ToUpperInvariant()+")":root.TrimEnd('\\');
    if(!drive.IsReady)return d;d.Total=drive.TotalSize;d.Free=drive.AvailableFreeSpace;d.Available=d.Total>0;}
   catch(Exception){}
   return d;}
  // Зелёный — норма, оранжевый — занято больше 85%, красный — больше 95%.
  public string BarColor{get{double u=UsedFraction;return u>0.95?"#EF4444":u>0.85?"#F59E0B":"#00D46A";}}
  public static string Gb(long bytes){double g=bytes/1073741824.0;return (g>=100?Math.Round(g).ToString("0"):g.ToString("0.0",System.Globalization.CultureInfo.GetCultureInfo("ru-RU")))+" ГБ";}
 }
}
