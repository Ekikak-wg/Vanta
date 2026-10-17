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
  string prefs=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,Brand.Settings);
  File.WriteAllText(prefs,Json.Encode(Json.D("autoGames",false,"indicator","off","keysPanel",false,"lastDirectory",Path.GetTempPath(),"hotkeys",Json.D("menu","","save","","record","","shot",""))));
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
    Console.WriteLine("снимок: "+file);
    if(page=="record"){((System.Windows.Controls.ScrollViewer)w.FindName("PageScroll")).ScrollToEnd();Pump(300);root.UpdateLayout();var rtb2=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);rtb2.Render(root);var enc2=new PngBitmapEncoder();enc2.Frames.Add(BitmapFrame.Create(rtb2));using(var f2=File.Create(Path.Combine(outDir,"ui-record-bottom.png")))enc2.Save(f2);}}
   // Окно выбора приложения с настоящим списком запущенных программ.
   var open=typeof(Overlay).GetMethod("OpenPicker",BindingFlags.NonPublic|BindingFlags.Instance);
   open.Invoke(ov,new object[]{"Не записывать приложение","Пока окно этого приложения видно на экране, в записи будет только игра.",new System.Collections.Generic.List<string>(),new Action<string>(n=>Console.WriteLine("выбрано: "+n))});
   for(int i=0;i<60&&((System.Windows.Controls.StackPanel)w.FindName("PickerList")).Children.Count==0;i++)Pump(100);
   Console.WriteLine("программ в списке: "+((System.Windows.Controls.StackPanel)w.FindName("PickerList")).Children.Count);
   {var root=(FrameworkElement)w.Content;root.UpdateLayout();var rtb=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);rtb.Render(root);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));using(var f=File.Create(Path.Combine(outDir,"ui-picker.png")))enc.Save(f);}
   ov.Exit();return 0;}
  catch(Exception ex){Console.WriteLine("ОШИБКА: "+ex);return 1;}
  finally{try{File.Delete(prefs);}catch(Exception){}}
 }
}
