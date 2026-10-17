// Проверка страницы «Клипы» в настоящем окне меню — за пределами экрана, без активации, без связи с OBS и без распознавания игр.
// Нужен ffmpeg (для тестовых роликов). Снимки страницы сохраняются в папку из первого аргумента (по умолчанию — рядом с тестом).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReplayOverlay;

static class GalleryUiTest {
 static int failures;
 static void Check(bool ok,string what){Console.WriteLine((ok?"OK    ":"FAIL  ")+what);if(!ok)failures++;}
 static Overlay ov;
 static T C<T>(string name) where T:FrameworkElement{return (T)ov.Window.FindName(name);}
 static object Call(string method,params object[] args){var m=typeof(Overlay).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Public);if(m==null)throw new MissingMethodException(method);return m.Invoke(ov,args);}
 static T Field<T>(string name){return (T)typeof(Overlay).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(ov);}
 static bool Pump(Func<bool> until,int ms){var sw=Stopwatch.StartNew();while(!until()&&sw.ElapsedMilliseconds<ms){var frame=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};t.Tick+=(s,e)=>{t.Stop();frame.Continue=false;};t.Start();Dispatcher.PushFrame(frame);}return until();}
 static void Wait(Task task,int ms){Pump(()=>task.IsCompleted,ms);if(task.IsFaulted)throw task.Exception.InnerException;}
 static void Snap(string file){var root=(FrameworkElement)ov.Window.Content;root.UpdateLayout();var rtb=new RenderTargetBitmap((int)root.ActualWidth,(int)Math.Min(root.ActualHeight,1100),96,96,PixelFormats.Pbgra32);rtb.Render(root);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));using(var f=File.Create(file))enc.Save(f);Console.WriteLine("INFO  снимок: "+file);}
 static void Run(string exe,string args){var p=Process.Start(new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true});p.WaitForExit();if(p.ExitCode!=0)throw new Exception("ffmpeg "+p.ExitCode);}

 [STAThread] static int Main(string[] args){
  Console.OutputEncoding=System.Text.Encoding.UTF8;
  string shots=args.Length>0?args[0]:AppDomain.CurrentDomain.BaseDirectory;Directory.CreateDirectory(shots);
  string ff=Ffmpeg.Find(null);if(ff==null){Console.WriteLine("INFO  ffmpeg не найден — тест пропущен");return 0;}
  string dir=Path.Combine(Path.GetTempPath(),"ReplayOBS-GalleryUi-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(dir);
  string prefs=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,Brand.Settings);
  try{
   Run(ff,"-hide_banner -v error -y -f lavfi -i testsrc2=size=640x360:rate=60:duration=8 -f lavfi -i sine=frequency=440:duration=8 -f lavfi -i sine=frequency=880:duration=8 -map 0 -map 1 -map 2 -c:v libx264 -preset ultrafast -g 120 -pix_fmt yuv420p -c:a aac \""+Path.Combine(dir,"Replay 2026-10-14_10-00-00.mkv")+"\"");
   Run(ff,"-hide_banner -v error -y -f lavfi -i smptebars=size=640x360:rate=30:duration=5 -f lavfi -i sine=duration=5 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac \""+Path.Combine(dir,"2026-10-13_21-30-00.mp4")+"\"");
   File.SetLastWriteTime(Path.Combine(dir,"2026-10-13_21-30-00.mp4"),DateTime.Now.AddDays(-1));
   // Настройки меню для теста: без игр, без запуска OBS, известная папка. OBS недоступен: путь к настройкам WebSocket — несуществующий.
   File.WriteAllText(prefs,Json.Encode(Json.D("autoGames",false,"indicator","off","lastDirectory",dir,"galleryGrid",false,"hotkeys",Json.D("menu","","save",""))));
   // Движка рядом с тестом нет: меню пишет «Движок записи не установлен» и ни к чему не подключается.
   var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
   ov=new Overlay(true);var w=ov.Window;w.ShowInTaskbar=false;w.ShowActivated=false;w.Topmost=false;
   w.Loaded+=(s,e)=>{w.Left=-30000;w.Top=0;}; // после Position(): окно уходит за экран до первой отрисовки
   w.Show();
   Check(w.Left<-20000&&!w.IsActive,"окно меню открыто за экраном и не активно");

   Call("OpenClips");
   var list=C<StackPanel>("ClipList");
   Check(Pump(()=>list.Children.Count==2,10000),"галерея: 2 видео из папки");
   Check(C<TextBlock>("ClipsInfo").Text.StartsWith("2 файла · "),"подпись: "+C<TextBlock>("ClipsInfo").Text);
   var clips=Field<List<ClipFile>>("clips");
   Check(clips[0].Name.StartsWith("Replay"),"новый клип — первым");
   var images=Field<Dictionary<string,Image>>("clipImages");
   Check(Pump(()=>images.Values.All(i=>i.Source!=null),15000),"миниатюры загружены");
   Check(Pump(()=>clips.All(c=>c.Seconds>0),5000)&&Math.Abs(clips[0].Seconds-8)<0.3,"длительность в строке: "+string.Join(", ",clips.Select(c=>c.Seconds.ToString("0.0"))));
   Snap(Path.Combine(shots,"gallery-list.png"));
   // Вид сеткой: две карточки в ряд, контекстное меню у каждой.
   var gv=typeof(Overlay).GetField("gridView",BindingFlags.NonPublic|BindingFlags.Instance);gv.SetValue(ov,true);Call("RenderClips");Pump(()=>false,400);
   var pair=list.Children[0] as Grid;
   Check(list.Children.Count==1&&pair!=null&&pair.Children.Count==2,"сетка: два клипа в одном ряду");
   var card=pair==null?null:pair.Children[0] as Border;
   Check(card!=null&&card.ContextMenu!=null&&card.ContextMenu.Items.Count==7,"у видео в меню 7 действий (открыть, программа, папка, переименовать, обрезать, дорожки отдельно, удалить)");
   Snap(Path.Combine(shots,"gallery-grid.png"));
   gv.SetValue(ov,false);Call("RenderClips");Pump(()=>false,200);

   Call("SelectClip",clips[0]);
   var msg=C<TextBlock>("PlayerMessage");
   Check(C<StackPanel>("ClipDetail").Visibility==Visibility.Visible,"выбранный клип открыт сверху");
   Check(Pump(()=>msg.Text=="",10000),"проигрыватель открыл MKV (сообщение: «"+msg.Text+"»)");
   Check(Math.Abs(Field<double>("clipLength")-8)<0.2,"длительность в проигрывателе: "+Field<double>("clipLength").ToString("0.00"));
   Check(C<MediaElement>("Player").IsMuted,"звук предпросмотра по умолчанию выключен");
   Call("TogglePlay");var player=C<MediaElement>("Player");
   Pump(()=>false,1200);double pos=player.Position.TotalSeconds;Call("TogglePlay");
   Check(pos>0.6,"воспроизведение идёт: за 1,2 с позиция "+pos.ToString("0.00")+" с");
   Check(C<Slider>("PlaySlider").Value>0.5,"ползунок следует за воспроизведением");
   C<Slider>("PlaySlider").Value=2.0;Pump(()=>false,200);Call("SetTrim",true);
   C<Slider>("PlaySlider").Value=6.5;Pump(()=>false,200);Call("SetTrim",false);
   Check(Math.Abs(Field<double>("trimStart")-2)<0.05&&Math.Abs(Field<double>("trimEnd")-6.5)<0.05,"отрезок 2–6,5 с задан кнопками «Начало здесь» / «Конец здесь»");
   Check(C<TextBlock>("TrimInfo").Text.StartsWith("Отрезок 0:02,0 – 0:06,5 · 0:04,5 — отмечен зелёным"),"подпись отрезка: "+C<TextBlock>("TrimInfo").Text);
   C<Slider>("PlaySlider").Value=1.0;Pump(()=>false,100);Call("SetTrim",false);
   Check(Math.Abs(Field<double>("trimEnd")-6.5)<0.05,"конец раньше начала — не принимается");
   Pump(()=>false,300);Snap(Path.Combine(shots,"gallery-detail.png"));

   // Быстрая обрезка при открытом проигрывателе (2,0 с — ключевой кадр, поэтому длительность ровно 4,5 с; точная проверена в ClipsTest)
   var ffTask=(Task)Call("TrimClip");
   Wait(ffTask,30000);
   string cut=Path.Combine(dir,"Replay 2026-10-14_10-00-00 (обрезка).mp4");
   Check(ffTask.IsCompleted&&!ffTask.IsFaulted&&File.Exists(cut),"отрезок сохранён рядом: "+Path.GetFileName(cut));
   Check(Pump(()=>list.Children.Count==3,5000),"галерея обновилась: 3 видео");
   Check(Pump(()=>Field<ClipFile>("selectedClip")!=null&&Field<ClipFile>("selectedClip").Path==cut,3000),"выбран новый отрезок");
   Check(Pump(()=>msg.Text==""&&Math.Abs(Field<double>("clipLength")-4.5)<0.15,10000),"длительность отрезка: "+Field<double>("clipLength").ToString("0.00")+" с (сообщение «"+msg.Text+"», проигрыватель "+(player.NaturalDuration.HasTimeSpan?player.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.000"):"нет")+", Windows "+Field<ClipFile>("selectedClip").Seconds.ToString("0.000")+", ffprobe "+Ffmpeg.Duration(Ffmpeg.Probe(ff),cut).ToString("0.000")+")");
   Check(File.Exists(Path.Combine(dir,"Replay 2026-10-14_10-00-00.mkv")),"исходный клип не тронут");

   // Переименование открытого в проигрывателе файла
   C<TextBox>("RenameBox").Text="Лучший момент";var rn=(Task)Call("RenameClip");Wait(rn,10000);
   string renamed=Path.Combine(dir,"Лучший момент.mp4");
   Check(!rn.IsFaulted&&File.Exists(renamed)&&!File.Exists(cut),"переименование файла, открытого в проигрывателе"+(rn.IsFaulted?": "+rn.Exception.InnerException.Message:""));
   Check(Pump(()=>Field<ClipFile>("selectedClip")!=null&&Field<ClipFile>("selectedClip").Path==renamed,3000),"после переименования выбран тот же клип");
   C<TextBox>("RenameBox").Text="плохое:имя";var bad=(Task)Call("RenameClip");Pump(()=>bad.IsCompleted,2000);
   Check(bad.IsFaulted&&File.Exists(renamed),"недопустимое имя — ошибка, файл на месте");

   Call("ShowPage","main");
   Check(C<MediaElement>("Player").Source==null,"уход со страницы закрывает проигрыватель");
   ov.Exit();
  }
  catch(Exception ex){Check(false,"исключение: "+ex);}
  finally{try{File.Delete(prefs);}catch(Exception){}try{Directory.Delete(dir,true);}catch(Exception ex){Console.WriteLine("INFO  папка не удалена: "+ex.Message);}}
  Console.WriteLine(failures==0?"ИТОГ: все проверки пройдены":"ИТОГ: провалов "+failures);
  return failures==0?0:1;
 }
}
