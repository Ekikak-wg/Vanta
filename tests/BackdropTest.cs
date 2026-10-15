// Размытие фона (акрил DWM) и исключение из захвата: окно 320×200 у левого нижнего угла на ~1,5 с, без активации.
// Снимок без исключения показывает, как выглядит размытие; с исключением — окна на снимке быть не должно.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayOverlay;

static class BackdropTest {
 [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h,uint a);
 static int failures;
 static void Check(bool ok,string what){Console.WriteLine((ok?"OK    ":"FAIL  ")+what);if(!ok)failures++;}
 static void Pump(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(s,e)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
 static Bitmap Grab(int x,int y,int w,int h){var b=new Bitmap(w,h);using(var g=Graphics.FromImage(b))g.CopyFromScreen(x,y,0,0,new System.Drawing.Size(w,h));return b;}
 static int Green(Bitmap b){int n=0;for(int y=0;y<b.Height;y+=2)for(int x=0;x<b.Width;x+=2){var c=b.GetPixel(x,y);if(c.G>180&&c.R<60&&c.B<140)n++;}return n;}
 [STAThread] static int Main(string[] args){
  Console.OutputEncoding=System.Text.Encoding.UTF8;string outDir=args.Length>0?args[0]:".";
  Check(Backdrop.Supported,"Windows поддерживает акриловый фон");
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  var area=System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
  foreach(bool exclude in new[]{false,true}){
   var w=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Topmost=true,ShowInTaskbar=false,ShowActivated=false,Width=320,Height=200,Left=area.Left+8,Top=area.Bottom-208};
   Backdrop.Prepare(w);
   var inner=new System.Windows.Controls.Border{Width=60,Height=40,Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(0,220,100))};
   w.Content=new System.Windows.Controls.Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB8,0x0D,0x0F,0x13)),Child=inner};
   bool applied=false;w.SourceInitialized+=(s,e)=>{var h=new WindowInteropHelper(w).Handle;applied=Backdrop.Apply(h);if(exclude)Check(SetWindowDisplayAffinity(h,0x11),"исключение из захвата принято Windows");};
   w.Show();Pump(700);
   using(var shot=Grab(area.Left+8,area.Bottom-208,320,200)){shot.Save(System.IO.Path.Combine(outDir,exclude?"backdrop-excluded.png":"backdrop-visible.png"));int g=Green(shot);
    if(!exclude){Check(applied,"акрил включён (DwmSetWindowAttribute)");Check(g>100,"без исключения окно видно на снимке ("+g+" зелёных точек)");}
    else Check(g==0,"с исключением окна на снимке нет ("+g+" зелёных точек)");}
   w.Close();Pump(200);}
  Console.WriteLine(failures==0?"ИТОГ: все проверки пройдены":"ИТОГ: провалов "+failures);return failures==0?0:1;
 }
}
