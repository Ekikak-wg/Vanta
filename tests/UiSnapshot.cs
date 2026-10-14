// Снимки всех вкладок нового меню в PNG — окно за экраном, без связи с OBS и без распознавания игр.
// UiSnapshot.exe <папка для снимков>
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReplayOverlay;

static class UiSnapshot {
 static void Pump(int ms){var frame=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(s,e)=>{t.Stop();frame.Continue=false;};t.Start();Dispatcher.PushFrame(frame);}
 [STAThread] static int Main(string[] args){
  string outDir=args.Length>0?args[0]:AppDomain.CurrentDomain.BaseDirectory;Directory.CreateDirectory(outDir);
  string prefs=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"overlay-settings.json");
  File.WriteAllText(prefs,Json.Encode(Json.D("autoGames",false,"launchObs",false,"indicator","off","keysPanel",false,"lastDirectory",Path.GetTempPath(),"hotkeys",Json.D("menu","","save","","record","","shot",""))));
  ObsClient.ConfigPath=Path.Combine(Path.GetTempPath(),"нет-config.json");
  try{
   var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
   var ov=new Overlay(true);var w=ov.Window;w.ShowInTaskbar=false;w.ShowActivated=false;w.Topmost=false;
   w.Loaded+=(s,e)=>{w.Left=-30000;w.Top=0;w.Height=1000;};w.Show();Pump(400);
   var show=typeof(Overlay).GetMethod("ShowPage",BindingFlags.NonPublic|BindingFlags.Instance);
   foreach(string page in new[]{"record","replay","shot","clips","settings"}){
    show.Invoke(ov,new object[]{page});Pump(300);
    var root=(FrameworkElement)w.Content;root.UpdateLayout();
    var rtb=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);rtb.Render(root);
    var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));string file=Path.Combine(outDir,"ui-"+page+".png");using(var f=File.Create(file))enc.Save(f);
    Console.WriteLine("снимок: "+file);}
   ov.Exit();return 0;}
  catch(Exception ex){Console.WriteLine("ОШИБКА: "+ex);return 1;}
  finally{try{File.Delete(prefs);}catch(Exception){}}
 }
}
