// Проверка галереи клипов: имена, список, переименование, занятые файлы, аргументы и настоящая обрезка через ffmpeg, индикатор.
// Обрезка проверяется на синтетическом ролике 640×360 с тремя звуковыми дорожками (если ffmpeg установлен).
// NVENC проверяется только при REPLAY_TEST_NVENC=1, удаление в Корзину — при REPLAY_TEST_RECYCLE=1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReplayOverlay;

static class ClipsTest {
 static int failures;
 static void Check(bool ok,string what){Console.WriteLine((ok?"OK    ":"FAIL  ")+what);if(!ok)failures++;}
 static void Info(string what){Console.WriteLine("INFO  "+what);}

 static string Run(string exe,string args){var psi=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  using(var p=Process.Start(psi)){var err=p.StandardError.ReadToEndAsync();string o=p.StandardOutput.ReadToEnd();p.WaitForExit();if(p.ExitCode!=0)throw new Exception(exe+": "+err.Result);return o;}}
 static double Dur(string probe,string file){return double.Parse(Run(probe,"-v error -show_entries format=duration -of csv=p=0 \""+file+"\"").Trim(),CultureInfo.InvariantCulture);}
 static List<string> Streams(string probe,string file){return Run(probe,"-v error -show_entries stream=codec_type,codec_name -of csv=p=0 \""+file+"\"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim()).ToList();}

 static int Main(){
  Console.OutputEncoding=System.Text.Encoding.UTF8;
  // Имена
  Check(ClipLibrary.CheckName("Лучший момент 1")==null,"обычное имя подходит");
  Check(ClipLibrary.CheckName("  ")!=null,"пустое имя — ошибка");
  Check(ClipLibrary.CheckName("a/b")!=null&&ClipLibrary.CheckName("a:b")!=null&&ClipLibrary.CheckName("a?b")!=null,"запрещённые символы — ошибка");
  Check(ClipLibrary.CheckName("клип.")!=null&&ClipLibrary.CheckName("клип ")!=null,"точка или пробел в конце — ошибка");
  Check(ClipLibrary.CheckName("CON")!=null&&ClipLibrary.CheckName("nul.txt")!=null,"зарезервированные имена Windows — ошибка");
  Check(ClipLibrary.CheckName(new string('x',181))!=null,"слишком длинное имя — ошибка");
  Check(ClipLibrary.Time(65)=="1:05"&&ClipLibrary.Time(3725)=="1:02:05"&&ClipLibrary.Time(12.34,true)=="0:12,3"&&ClipLibrary.Time(-1)=="—","формат времени");

  string dir=Path.Combine(Path.GetTempPath(),"ReplayOBS-ClipsTest-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(dir);
  try{
   // Список
   Action<string,int> make=(name,minutesAgo)=>{string p=Path.Combine(dir,name);File.WriteAllText(p,"x");File.SetLastWriteTime(p,DateTime.Now.AddMinutes(-minutesAgo));};
   make("Replay 1.mkv",30);make("Replay 2.mkv",10);make("запись.MP4",20);make("notes.txt",1);make("Replay 3.mp4.part",0);make("скрытый.mkv",5);
   File.SetAttributes(Path.Combine(dir,"скрытый.mkv"),FileAttributes.Hidden);
   var list=ClipLibrary.List(dir);
   Check(string.Join("|",list.Select(c=>c.Name))=="Replay 2|запись|Replay 1","видео новые сверху; .txt, .part и скрытые пропущены: "+string.Join("|",list.Select(c=>c.Name)));
   Check(ClipLibrary.List(Path.Combine(dir,"нет такой")).Count==0&&ClipLibrary.List("").Count==0,"нет папки — пустой список");

   // Путь для обрезки
   string src=Path.Combine(dir,"Replay 1.mkv");
   Check(Path.GetFileName(ClipLibrary.TrimmedPath(src,true))=="Replay 1 (обрезка).mp4","обрезка в MP4 — новое имя");
   Check(Path.GetFileName(ClipLibrary.TrimmedPath(src,false))=="Replay 1 (обрезка).mkv","обрезка в исходном формате");
   File.WriteAllText(Path.Combine(dir,"Replay 1 (обрезка).mp4"),"x");
   Check(Path.GetFileName(ClipLibrary.TrimmedPath(src,true))=="Replay 1 (обрезка 2).mp4","имя занято — номер 2");

   // Переименование и занятые файлы
   string renamed=ClipLibrary.Rename(src,"Мой клип");
   Check(Path.GetFileName(renamed)=="Мой клип.mkv"&&File.Exists(renamed)&&!File.Exists(src),"переименование сохраняет расширение");
   string err=null;try{ClipLibrary.Rename(renamed,"Replay 2");}catch(InvalidOperationException ex){err=ex.Message;}
   Check(err!=null&&err.Contains("уже есть")&&File.Exists(renamed),"имя занято — ошибка, файл на месте");
   string cased=ClipLibrary.Rename(renamed,"мой клип");
   Check(Path.GetFileName(new DirectoryInfo(dir).GetFiles("мой клип.mkv")[0].FullName)=="мой клип.mkv","смена только регистра букв работает");
   string busy=Path.Combine(dir,"Replay 2.mkv");
   using(new FileStream(busy,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite)){
    Check(ClipLibrary.InUse(busy),"файл, открытый на запись (как у OBS), — занят");
    err=null;try{ClipLibrary.Rename(busy,"другое");}catch(InvalidOperationException ex){err=ex.Message;}
    Check(err!=null&&File.Exists(busy),"занятый файл не переименовывается");
    err=null;try{ClipLibrary.Recycle(busy);}catch(InvalidOperationException ex){err=ex.Message;}
    Check(err!=null&&File.Exists(busy),"занятый файл не удаляется");}
   Check(!ClipLibrary.InUse(busy),"закрытый файл — свободен");
   if(Environment.GetEnvironmentVariable("REPLAY_TEST_RECYCLE")=="1"){ClipLibrary.Recycle(cased);Check(!File.Exists(cased),"удаление в Корзину");}

   // Аргументы
   string fast=Ffmpeg.TrimArgs(@"C:\v\a b.mkv",@"C:\v\a b (обрезка).mp4.part",3.25,7,false,null);
   Check(fast.Contains("-ss 3.25 -i \"C:\\v\\a b.mkv\" -t 3.75")&&fast.Contains("-map 0 -c copy")&&fast.Contains("-f mp4")&&fast.Contains("+faststart")&&fast.EndsWith("\"C:\\v\\a b (обрезка).mp4.part\""),"быстрая обрезка: копирование всех дорожек, MP4 по имени до .part");
   string exact=Ffmpeg.TrimArgs("a.mkv","a (обрезка).mkv.part",1,2,true,"libx264");
   Check(exact.Contains("-map 0:v:0 -map 0:a? -c:v libx264")&&exact.Contains("-c:a aac")&&exact.Contains("-f matroska"),"точная обрезка: перекодирование видео и всех звуковых дорожек, MKV");

   // Индикатор
   Check(RecordingIndicator.Label("off",true,true,TimeSpan.FromSeconds(5),true)==null,"индикатор выключен — ничего");
   Check(RecordingIndicator.Label("record",true,true,TimeSpan.FromSeconds(65),true)=="REC 01:05","запись — REC и время");
   Check(RecordingIndicator.Label("record",true,true,TimeSpan.FromSeconds(3725),false)=="REC 1:02:05","запись дольше часа");
   Check(RecordingIndicator.Label("record",true,false,TimeSpan.Zero,true)==null,"«только запись»: повтор не показывается");
   Check(RecordingIndicator.Label("all",true,false,TimeSpan.Zero,true)=="ПОВТОР","«запись и повтор»: повтор показывается");
   Check(RecordingIndicator.Label("all",false,true,TimeSpan.Zero,true)==null,"нет связи с OBS — ничего");

   // Настоящая обрезка
   string ff=Ffmpeg.Find(null),probe=Ffmpeg.Probe(ff);
   if(ff==null||probe==null){Info("ffmpeg не найден — обрезка на настоящем файле пропущена");}
   else{
    Info("ffmpeg: "+ff);
    string clip=Path.Combine(dir,"Replay synth.mkv");
    // 12 с, 60 к/с, ключевой кадр каждые 2 с (как у OBS с keyint_sec=2), три звуковые дорожки AAC.
    Run(ff,"-hide_banner -v error -y -f lavfi -i testsrc2=size=640x360:rate=60:duration=12 -f lavfi -i sine=frequency=440:duration=12 -f lavfi -i sine=frequency=660:duration=12 -f lavfi -i sine=frequency=880:duration=12 -map 0 -map 1 -map 2 -map 3 -c:v libx264 -preset ultrafast -g 120 -keyint_min 120 -sc_threshold 0 -pix_fmt yuv420p -c:a aac \""+clip+"\"");
    Check(Streams(probe,clip).Count==4,"синтетический ролик: видео и три звуковые дорожки");
    var progress=new List<double>();
    string outFast=ClipLibrary.TrimmedPath(clip,true);
    Ffmpeg.Run(ff,Ffmpeg.TrimArgs(clip,outFast+".part",5.0,9.0,false,null),4,p=>{lock(progress)progress.Add(p);},CancellationToken.None).Wait();File.Move(outFast+".part",outFast);
    var sf=Streams(probe,outFast);double df=Dur(probe,outFast);
    Check(sf.Count==4&&sf.Count(s=>s.StartsWith("aac"))==3,"быстрая обрезка: все 3 звуковые дорожки на месте ("+string.Join(", ",sf)+")");
    Check(df>=3.9&&df<=6.1,"быстрая обрезка 5–9 с: длительность "+df.ToString("0.00")+" с (4 с + сдвиг к ключевому кадру не больше 2 с)");
    Check(progress.Count>0&&progress.Max()>0.5,"прогресс приходит (макс. "+(progress.Count>0?progress.Max():0).ToString("0.00")+")");
    string outExact=ClipLibrary.TrimmedPath(clip,true);
    Ffmpeg.Run(ff,Ffmpeg.TrimArgs(clip,outExact+".part",5.0,9.0,true,"libx264"),4,null,CancellationToken.None).Wait();File.Move(outExact+".part",outExact);
    var se=Streams(probe,outExact);double de=Dur(probe,outExact);
    Check(Path.GetFileName(outExact)=="Replay synth (обрезка 2).mp4","второй отрезок получил номер 2");
    Check(se.Count==4&&se[0].StartsWith("h264")&&se.Count(s=>s.StartsWith("aac"))==3,"точная обрезка: видео H.264 и 3 дорожки AAC");
    Check(Math.Abs(de-4.0)<0.12,"точная обрезка 5–9 с: длительность "+de.ToString("0.000")+" с");
    // Дорожки отдельно (для CapCut): три звуковые дорожки — три файла M4A без перекодирования
    var codecs=Ffmpeg.AudioCodecs(probe,clip);
    Check(codecs.Count==3&&codecs.All(x=>x=="aac"),"ffprobe видит 3 звуковые дорожки AAC: "+string.Join(",",codecs));
    var outs=new List<string>{Path.Combine(dir,"Дорожка 1 Общий.m4a"),Path.Combine(dir,"Дорожка 2 Игра.m4a"),Path.Combine(dir,"Дорожка 3 Микрофон.m4a")};
    Ffmpeg.Run(ff,Ffmpeg.SplitAudioArgs(clip,outs,true),0,null,CancellationToken.None).Wait();
    Check(outs.All(File.Exists)&&outs.All(o=>Streams(probe,o).Count==1&&Streams(probe,o)[0].StartsWith("aac")),"каждая дорожка — отдельный файл с одной дорожкой AAC");
    Check(outs.All(o=>Math.Abs(Dur(probe,o)-12)<0.3),"длительность каждой дорожки — как у ролика (12 с)");
    if(Environment.GetEnvironmentVariable("REPLAY_TEST_NVENC")=="1"){string outN=ClipLibrary.TrimmedPath(clip,false);Ffmpeg.Run(ff,Ffmpeg.TrimArgs(clip,outN+".part",5,9,true,"h264_nvenc"),4,null,CancellationToken.None).Wait();File.Move(outN+".part",outN);Check(Math.Abs(Dur(probe,outN)-4)<0.12,"точная обрезка через NVENC");}
    // Ошибка ffmpeg — понятное исключение
    err=null;try{Ffmpeg.Run(ff,Ffmpeg.TrimArgs(Path.Combine(dir,"нет.mkv"),Path.Combine(dir,"x.mp4.part"),0,1,false,null),1,null,CancellationToken.None).Wait();}catch(AggregateException ex){err=ex.InnerException.Message;}
    Check(err!=null&&err.StartsWith("ffmpeg: "),"ошибка ffmpeg передаётся текстом: "+err);
    // Отмена: долгий ролик перекодируется, через 300 мс отменяем
    var cts=new CancellationTokenSource();string longOut=Path.Combine(dir,"long.mp4.part");
    var task=Ffmpeg.Run(ff,"-hide_banner -nostdin -y -f lavfi -i testsrc2=size=1280x720:rate=60:duration=600 -c:v libx264 -preset slow -f mp4 -progress pipe:1 \""+longOut+"\"",600,null,cts.Token);
    Thread.Sleep(300);var sw=Stopwatch.StartNew();cts.Cancel();bool cancelled=false;try{task.Wait();}catch(AggregateException ex){cancelled=ex.InnerException is OperationCanceledException;}
    Check(cancelled&&sw.ElapsedMilliseconds<3000,"отмена останавливает ffmpeg за "+sw.ElapsedMilliseconds+" мс");
    // Длительность и миниатюра из Windows; запасной вариант — ffmpeg
    double shellDur=ShellMedia.Duration(outExact);
    Check(Math.Abs(shellDur-de)<0.2,"Windows знает длительность MP4: "+shellDur.ToString("0.00")+" с");
    Info("Windows: длительность MKV "+ShellMedia.Duration(clip).ToString("0.00")+" с");
    var thumbResult=new System.Windows.Media.Imaging.BitmapSource[2];
    var t=new Thread(()=>{thumbResult[0]=ShellMedia.Thumbnail(outExact,224);thumbResult[1]=ShellMedia.Thumbnail(clip,224);});t.SetApartmentState(ApartmentState.STA);t.Start();t.Join();
    Info("миниатюра Windows: MP4 "+(thumbResult[0]==null?"нет":thumbResult[0].PixelWidth+"×"+thumbResult[0].PixelHeight)+", MKV "+(thumbResult[1]==null?"нет":thumbResult[1].PixelWidth+"×"+thumbResult[1].PixelHeight));
    var cf=ClipLibrary.List(dir).First(c=>c.Path==clip);cf.Seconds=12;
    string jpg=Ffmpeg.Thumbnail(ff,cf,224);
    Check(jpg!=null&&new FileInfo(jpg).Length>1000,"миниатюра через ffmpeg создана");
    Check(Ffmpeg.Thumbnail(ff,cf,224)==jpg,"повторно — из кэша");
    try{File.Delete(jpg);}catch(Exception){}
    // Загрузчик в фоне
    var got=new List<string>();var done=new ManualResetEvent(false);
    using(var loader=new ClipInfoLoader((c,b)=>{lock(got){got.Add(c.Name+"|"+(c.Seconds>0)+"|"+(b!=null));if(got.Count==2)done.Set();}}){FfmpegPath=()=>ff}){
     loader.Request(ClipLibrary.List(dir).Where(c=>c.Name.StartsWith("Replay synth")).Take(2));
     Check(done.WaitOne(30000),"фоновый загрузчик ответил по обоим клипам: "+string.Join("; ",got));
     Check(got.All(g=>g.EndsWith("|True|True")),"у обоих — длительность и миниатюра");}
   }
  }
  finally{try{Directory.Delete(dir,true);}catch(Exception ex){Info("временная папка не удалена: "+ex.Message);}}
  Console.WriteLine(failures==0?"ИТОГ: все проверки пройдены":"ИТОГ: провалов "+failures);
  return failures==0?0:1;
 }
}
