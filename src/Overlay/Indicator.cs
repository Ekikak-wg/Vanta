using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Ellipse=System.Windows.Shapes.Ellipse;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Значок состояния в углу экрана: не попадает в запись (исключён из захвата), пропускает щелчки мыши, не забирает фокус.
 public sealed class RecordingIndicator : IDisposable {
  public const string Off="off",RecordOnly="record",All="all";
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
  [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int index,int value);
  [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h,uint affinity);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  Window window;TextBlock text;Ellipse dot;readonly bool capturable;
  public string Mode=RecordOnly,Corner="tr";
  public RecordingIndicator(bool previewMode){capturable=previewMode;}

  // Что показать: null — ничего. Запись важнее повтора.
  public static string Label(string mode,bool connected,bool record,TimeSpan elapsed,bool replay) {
   if(mode==Off||!connected)return null;
   if(record)return "REC "+(elapsed.TotalHours>=1?((int)elapsed.TotalHours)+":"+elapsed.Minutes.ToString("00")+":"+elapsed.Seconds.ToString("00"):((int)elapsed.TotalMinutes).ToString("00")+":"+elapsed.Seconds.ToString("00"));
   if(mode==All&&replay)return "ПОВТОР";
   return null;
  }

  public void Update(bool connected,bool record,TimeSpan elapsed,bool replay) {
   string label=Label(Mode,connected,record,elapsed,replay);
   if(label==null){if(window!=null&&window.IsVisible)window.Hide();return;}
   if(window==null)Create();
   text.Text=label;dot.Fill=new SolidColorBrush(record?Color.FromRgb(0xFF,0x55,0x4E):Color.FromRgb(0x8D,0xCC,0x38));
   if(!window.IsVisible)window.Show();
   Place();
  }

  void Create() {
   dot=new Ellipse{Width=9,Height=9,Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center};
   text=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(0xF4,0xF5,0xF6)),FontSize=12,FontWeight=FontWeights.SemiBold,FontFamily=new FontFamily("Segoe UI"),VerticalAlignment=VerticalAlignment.Center};
   var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(dot);row.Children.Add(text);
   window=new Window{Title="Replay — индикатор",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Topmost=true,ShowInTaskbar=false,ShowActivated=false,Focusable=false,SizeToContent=SizeToContent.WidthAndHeight,
    Content=new Border{Background=new SolidColorBrush(Color.FromArgb(0xB8,0x11,0x13,0x16)),CornerRadius=new CornerRadius(6),Padding=new Thickness(9,4,10,5),Child=row}};
   window.SourceInitialized+=(s,e)=>{IntPtr h=new WindowInteropHelper(window).Handle;
    SetWindowLong(h,-20,GetWindowLong(h,-20)|0x20|0x80|0x08000000); // прозрачен для мыши, без значка в Alt+Tab, не активируется
    if(!capturable)SetWindowDisplayAffinity(h,0x11);};
   window.SizeChanged+=(s,e)=>Place();
  }

  // Угол монитора, на котором сейчас активное окно (обычно игра).
  void Place() {
   if(window==null||window.ActualWidth<=0)return;
   var area=WF.Screen.FromHandle(GetForegroundWindow()).Bounds;var src=PresentationSource.FromVisual(window);double sx=1,sy=1;
   if(src!=null&&src.CompositionTarget!=null){sx=src.CompositionTarget.TransformFromDevice.M11;sy=src.CompositionTarget.TransformFromDevice.M22;}
   double left=area.Left*sx,top=area.Top*sy,right=area.Right*sx,bottom=area.Bottom*sy,m=14;
   double x=Corner.EndsWith("l")?left+m:right-window.ActualWidth-m,y=Corner.StartsWith("b")?bottom-window.ActualHeight-m:top+m;
   if(Math.Abs(window.Left-x)>0.5)window.Left=x;if(Math.Abs(window.Top-y)>0.5)window.Top=y;
  }

  public void Dispose(){if(window!=null){window.Close();window=null;}}
 }
}
