using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReplayOverlay {
 // Файл повтора или записи в папке OBS.
 public sealed class ClipFile {
  public string Path,Name,Extension; public DateTime Modified; public long Size;
  public double Seconds=-1; // длительность; -1 — ещё не известна
  public bool IsImage; public int Width,Height; // скриншоты
 }

 // Список клипов, имена, переименование, удаление в Корзину.
 public static class ClipLibrary {
  public static readonly string[] Extensions={".mp4",".mkv",".mov",".flv",".ts",".m4v",".webm"};
  public static readonly string[] ImageExtensions={".png",".jpg",".jpeg"};
  public static bool IsVideo(string path){return Extensions.Contains(System.IO.Path.GetExtension(path)??"",StringComparer.OrdinalIgnoreCase);}
  public static bool IsImage(string path){return ImageExtensions.Contains(System.IO.Path.GetExtension(path)??"",StringComparer.OrdinalIgnoreCase);}

  // Видео из папки (без вложенных), новые сверху.
  public static List<ClipFile> List(string dir) {
   var list=new List<ClipFile>();if(string.IsNullOrEmpty(dir)||!Directory.Exists(dir))return list;
   foreach(var f in new DirectoryInfo(dir).EnumerateFiles()){
    bool image=IsImage(f.Name);if((!IsVideo(f.Name)&&!image)||(f.Attributes&(FileAttributes.Hidden|FileAttributes.System))!=0)continue;
    list.Add(new ClipFile{IsImage=image,Path=f.FullName,Name=System.IO.Path.GetFileNameWithoutExtension(f.Name),Extension=f.Extension,Modified=f.LastWriteTime,Size=f.Length});}
   return list.OrderByDescending(c=>c.Modified).ThenBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ToList();
  }

  // Файл ещё пишется (OBS держит его открытым на запись) — трогать нельзя.
  public static bool InUse(string path){if(!File.Exists(path))return false;try{using(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){}return false;}catch(IOException){return true;}catch(UnauthorizedAccessException){return true;}}

  static readonly string[] Reserved={"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"};
  // null — имя подходит; иначе — чем не подходит.
  public static string CheckName(string name) {
   if(name==null||name.Trim().Length==0)return "Введите имя";
   if(name.Length>180)return "Слишком длинное имя";
   char bad=name.FirstOrDefault(ch=>System.IO.Path.GetInvalidFileNameChars().Contains(ch));
   if(bad!='\0')return "Имя не может содержать символ «"+(bad<' '?"управляющий":bad.ToString())+"»";
   if(name.EndsWith(".")||name.EndsWith(" "))return "Имя не может заканчиваться точкой или пробелом";
   if(Reserved.Contains(name.Trim().Split('.')[0],StringComparer.OrdinalIgnoreCase))return "Это имя зарезервировано Windows";
   return null;
  }

  // Переименовать, сохранив расширение. Возвращает новый путь.
  public static string Rename(string path,string newName) {
   string error=CheckName(newName);if(error!=null)throw new InvalidOperationException(error);
   if(!File.Exists(path))throw new InvalidOperationException("Файла уже нет — список обновлён");
   newName=newName.Trim();string target=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),newName+System.IO.Path.GetExtension(path));
   if(target==path)return path;
   bool sameFile=string.Equals(target,path,StringComparison.OrdinalIgnoreCase); // меняется только регистр букв
   if(!sameFile&&File.Exists(target))throw new InvalidOperationException("Файл «"+System.IO.Path.GetFileName(target)+"» уже есть");
   if(InUse(path))throw new InvalidOperationException("Файл ещё пишется или открыт другой программой");
   File.Move(path,target);return target;
  }

  // Путь для обрезанной копии: «имя (обрезка).mp4», «имя (обрезка 2).mp4» …
  public static string TrimmedPath(string path,bool toMp4) {
   string dir=System.IO.Path.GetDirectoryName(path),name=System.IO.Path.GetFileNameWithoutExtension(path),ext=toMp4?".mp4":System.IO.Path.GetExtension(path);
   for(int i=1;;i++){string p=System.IO.Path.Combine(dir,name+(i==1?" (обрезка)":" (обрезка "+i+")")+ext);if(!File.Exists(p)&&!File.Exists(p+".part"))return p;}
  }

  // Удаление — только в Корзину: клип можно вернуть.
  public static void Recycle(string path) {
   if(!File.Exists(path))throw new InvalidOperationException("Файла уже нет — список обновлён");
   if(InUse(path))throw new InvalidOperationException("Файл ещё пишется или открыт другой программой");
   Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
  }

  public static string Time(double seconds,bool tenths=false) {
   if(seconds<0||double.IsNaN(seconds))return "—";
   var t=TimeSpan.FromSeconds(tenths?Math.Floor(seconds*10)/10:Math.Floor(seconds));
   string s=t.TotalHours>=1?((int)t.TotalHours)+":"+t.Minutes.ToString("00")+":"+t.Seconds.ToString("00"):((int)t.TotalMinutes)+":"+t.Seconds.ToString("00");
   return tenths?s+","+(t.Milliseconds/100):s;
  }
  public static string Size(long bytes) {
   if(bytes>=1L<<30)return (bytes/(double)(1L<<30)).ToString("0.0",CultureInfo.GetCultureInfo("ru-RU"))+" ГБ";
   if(bytes>=1L<<20)return (bytes/(double)(1L<<20)).ToString("0",CultureInfo.GetCultureInfo("ru-RU"))+" МБ";
   return Math.Max(1,bytes/1024)+" КБ";
  }
 }

 // ffmpeg — для обрезки и миниатюр, если Windows их не даёт. В комплект не входит: ищем установленный.
 public static class Ffmpeg {
  public static string Find(string configured) {
   var places=new List<string>();
   if(!string.IsNullOrEmpty(configured))places.Add(configured);
   places.Add(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ffmpeg.exe"));
   foreach(string dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(';'))if(dir.Trim().Length>0){try{places.Add(System.IO.Path.Combine(dir.Trim().Trim('"'),"ffmpeg.exe"));}catch(ArgumentException){}}
   string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
   places.Add(System.IO.Path.Combine(local,@"Microsoft\WinGet\Links\ffmpeg.exe"));
   foreach(string root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"C:\"})places.Add(System.IO.Path.Combine(root,@"ffmpeg\bin\ffmpeg.exe"));
   foreach(string p in places)if(File.Exists(p))return p;
   try{string packages=System.IO.Path.Combine(local,@"Microsoft\WinGet\Packages");if(Directory.Exists(packages))
    foreach(string pkg in Directory.GetDirectories(packages,"*FFmpeg*"))foreach(string p in Directory.EnumerateFiles(pkg,"ffmpeg.exe",SearchOption.AllDirectories))return p;}catch(Exception){}
   return null;
  }
  public static string Probe(string ffmpeg){if(ffmpeg==null)return null;string p=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ffmpeg),"ffprobe.exe");return File.Exists(p)?p:null;}

  static string S(double v){return v.ToString("0.###",CultureInfo.InvariantCulture);}
  static string Q(string path){return "\""+path+"\"";}
  // Аргументы обрезки. Быстро (precise=false) — без перекодирования, все дорожки как есть; начало — по ключевому кадру не позже start.
  // Точно — видео перекодируется (videoEncoder: h264_nvenc или libx264), звук всех дорожек — в AAC, чтобы не разъехался с картинкой.
  public static string TrimArgs(string input,string output,double start,double end,bool precise,string videoEncoder) {
   bool mp4=System.IO.Path.GetExtension(output.EndsWith(".part")?output.Substring(0,output.Length-5):output).ToLowerInvariant()==".mp4";
   var a=new StringBuilder("-hide_banner -nostdin -y -ss "+S(start)+" -i "+Q(input)+" -t "+S(end-start));
   if(!precise)a.Append(" -map 0 -c copy");
   else{a.Append(" -map 0:v:0 -map 0:a? -c:v "+videoEncoder);a.Append(videoEncoder=="h264_nvenc"?" -preset p5 -rc vbr -cq 19 -b:v 0":" -preset veryfast -crf 18");a.Append(" -pix_fmt yuv420p -c:a aac -b:a 192k");}
   a.Append(" -avoid_negative_ts make_zero");
   if(mp4)a.Append(" -movflags +faststart -f mp4");else a.Append(" -f matroska");
   a.Append(" -progress pipe:1 -nostats "+Q(output));
   return a.ToString();
  }

  // Запуск с прогрессом (0…1). Ошибка — исключение с последней строкой ffmpeg.
  public static async Task Run(string exe,string args,double seconds,Action<double> progress,CancellationToken cancel) {
   var psi=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
   using(var p=new Process{StartInfo=psi,EnableRaisingEvents=true}) {
    var tail=new Queue<string>();var exited=new TaskCompletionSource<bool>();
    p.OutputDataReceived+=(s,e)=>{if(e.Data==null||progress==null||seconds<=0)return;int eq=e.Data.IndexOf('=');if(eq<0)return;string k=e.Data.Substring(0,eq);long us;
     if((k=="out_time_us"||k=="out_time_ms")&&long.TryParse(e.Data.Substring(eq+1),out us))progress(Math.Max(0,Math.Min(1,us/1e6/seconds)));};
    p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null&&e.Data.Trim().Length>0)lock(tail){tail.Enqueue(e.Data.Trim());while(tail.Count>8)tail.Dequeue();}};
    p.Exited+=(s,e)=>exited.TrySetResult(true);
    p.Start();try{p.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(Exception){} // игра и OBS важнее
    p.BeginOutputReadLine();p.BeginErrorReadLine();
    using(cancel.Register(()=>{try{if(!p.HasExited)p.Kill();}catch(Exception){}}))await exited.Task.ConfigureAwait(false);
    p.WaitForExit();
    cancel.ThrowIfCancellationRequested();
    if(p.ExitCode!=0){string last;lock(tail)last=tail.Count>0?tail.Last():"код "+p.ExitCode;throw new InvalidOperationException("ffmpeg: "+last);}
   }
  }

  // Длительность через ffprobe (если Windows её не знает).
  public static double Duration(string ffprobe,string file) {
   if(ffprobe==null)return -1;
   try{var psi=new ProcessStartInfo(ffprobe,"-v error -show_entries format=duration -of csv=p=0 "+Q(file)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
    using(var p=Process.Start(psi)){string o=p.StandardOutput.ReadToEnd();if(!p.WaitForExit(10000)){try{p.Kill();}catch(Exception){}return -1;}double d;return double.TryParse(o.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out d)?d:-1;}}
   catch(Exception){return -1;}
  }

  // Кадр для миниатюры через ffmpeg — кэш во временной папке, ключ — путь, размер и время изменения.
  public static string Thumbnail(string ffmpeg,ClipFile clip,int width) {
   if(ffmpeg==null)return null;
   try{string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ReplayOBS-thumbs");Directory.CreateDirectory(dir);
    string key;using(var sha=SHA1.Create())key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(clip.Path.ToLowerInvariant()+"|"+clip.Size+"|"+clip.Modified.Ticks+"|"+width))).Replace("-","");
    string jpg=System.IO.Path.Combine(dir,key+".jpg");if(File.Exists(jpg))return jpg;
    double at=clip.Seconds>4?2:clip.Seconds>0?clip.Seconds/2:1;
    var psi=new ProcessStartInfo(ffmpeg,"-hide_banner -nostdin -v error -y -ss "+S(at)+" -i "+Q(clip.Path)+" -frames:v 1 -vf scale="+width+":-2 -q:v 4 "+Q(jpg)){UseShellExecute=false,CreateNoWindow=true};
    using(var p=Process.Start(psi)){if(!p.WaitForExit(15000)){try{p.Kill();}catch(Exception){}return null;}}
    return File.Exists(jpg)?jpg:null;}
   catch(Exception){return null;}
  }
 }

 // Миниатюры и длительность из Windows (то же, что показывает Проводник).
 public static class ShellMedia {
  [ComImport,Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellItemImageFactory{[PreserveSig]int GetImage(NativeSize size,int flags,out IntPtr bitmap);}
  [StructLayout(LayoutKind.Sequential)] struct NativeSize{public int Width,Height;}
  // Только порядок методов важен: вызываем лишь GetUInt64.
  [ComImport,Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellItem2{void BindToHandler();void GetParent();void GetDisplayName();void GetAttributes();void Compare();
   void GetPropertyStore();void GetPropertyStoreWithCreateObject();void GetPropertyStoreForKeys();void GetPropertyDescriptionList();void Update();void GetProperty();void GetCLSID();void GetFileTime();void GetInt32();void GetString();void GetUInt32();
   [PreserveSig]int GetUInt64(ref PropertyKey key,out ulong value);}
  [StructLayout(LayoutKind.Sequential)] struct PropertyKey{public Guid Format;public uint Id;}
  [StructLayout(LayoutKind.Sequential)] struct BITMAP{public int Type,Width,Height,WidthBytes;public ushort Planes,BitsPixel;public IntPtr Bits;}
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER{public int Size,Width,Height;public ushort Planes,BitCount;public int Compression,SizeImage,XPels,YPels,ClrUsed,ClrImportant;}
  [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)] static extern void SHCreateItemFromParsingName(string path,IntPtr bindContext,[MarshalAs(UnmanagedType.LPStruct)]Guid riid,[MarshalAs(UnmanagedType.Interface)]out object item);
  [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h,int size,ref BITMAP bitmap);
  [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc,IntPtr bitmap,uint start,uint lines,[Out]byte[] bits,ref BITMAPINFOHEADER info,uint usage);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h,IntPtr dc);

  // Миниатюра без иконки-заглушки: нет миниатюры — null. Альфа-канал игнорируется (у видео он бывает нулевым).
  public static BitmapSource Thumbnail(string path,int width) {
   object o=null;IntPtr hbm=IntPtr.Zero;
   try{SHCreateItemFromParsingName(path,IntPtr.Zero,typeof(IShellItemImageFactory).GUID,out o);
    if(((IShellItemImageFactory)o).GetImage(new NativeSize{Width=width,Height=width},0x8,out hbm)!=0||hbm==IntPtr.Zero)return null; // 0x8 — только миниатюра
    var bm=new BITMAP();if(GetObject(hbm,Marshal.SizeOf(typeof(BITMAP)),ref bm)==0||bm.Width<=0||bm.Height==0)return null;
    int w=bm.Width,h=Math.Abs(bm.Height);var info=new BITMAPINFOHEADER{Size=Marshal.SizeOf(typeof(BITMAPINFOHEADER)),Width=w,Height=-h,Planes=1,BitCount=32};
    var pixels=new byte[w*h*4];IntPtr dc=GetDC(IntPtr.Zero);int lines;try{lines=GetDIBits(dc,hbm,0,(uint)h,pixels,ref info,0);}finally{ReleaseDC(IntPtr.Zero,dc);}
    if(lines==0)return null;
    var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgr32,null,pixels,w*4);image.Freeze();return image;}
   catch(Exception){return null;}
   finally{if(hbm!=IntPtr.Zero)DeleteObject(hbm);if(o!=null)Marshal.ReleaseComObject(o);}
  }
  // System.Media.Duration (в сотнях наносекунд). Неизвестна — -1.
  public static double Duration(string path) {
   object o=null;
   try{SHCreateItemFromParsingName(path,IntPtr.Zero,typeof(IShellItem2).GUID,out o);var key=new PropertyKey{Format=new Guid("64440490-4C8B-11D1-8B70-080036B11A03"),Id=3};ulong v;
    return ((IShellItem2)o).GetUInt64(ref key,out v)==0&&v>0?v/1e7:-1;}
   catch(Exception){return -1;}
   finally{if(o!=null)Marshal.ReleaseComObject(o);}
  }
 }

 // Фоновая загрузка миниатюр и длительностей по одной, в отдельном STA-потоке; результат — через callback.
 public sealed class ClipInfoLoader : IDisposable {
  readonly Queue<ClipFile> queue=new Queue<ClipFile>();readonly object gate=new object();readonly Thread thread;bool stop;
  readonly Action<ClipFile,BitmapSource> done;
  public Func<string> FfmpegPath=()=>null;
  readonly Dictionary<string,BitmapSource> cache=new Dictionary<string,BitmapSource>(StringComparer.OrdinalIgnoreCase);readonly Dictionary<string,double> durations=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
  public ClipInfoLoader(Action<ClipFile,BitmapSource> callback){done=callback;thread=new Thread(Work){IsBackground=true,Name="Миниатюры клипов"};thread.SetApartmentState(ApartmentState.STA);thread.Start();}
  static string Key(ClipFile c){return c.Path+"|"+c.Size+"|"+c.Modified.Ticks;}
  // Уже известное — сразу, остальное — в очередь.
  public BitmapSource Cached(ClipFile c){lock(gate){BitmapSource b;double d;if(durations.TryGetValue(Key(c),out d))c.Seconds=d;return cache.TryGetValue(Key(c),out b)?b:null;}}
  public void Request(IEnumerable<ClipFile> clips){lock(gate){queue.Clear();foreach(var c in clips)if(!cache.ContainsKey(Key(c)))queue.Enqueue(c);Monitor.Pulse(gate);}}
  void Work(){
   while(true){ClipFile c;lock(gate){while(queue.Count==0&&!stop)Monitor.Wait(gate);if(stop)return;c=queue.Dequeue();}
    double d=-1;string ff=FfmpegPath();
    if(c.IsImage){try{var dec=BitmapDecoder.Create(new Uri(c.Path),BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);c.Width=dec.Frames[0].PixelWidth;c.Height=dec.Frames[0].PixelHeight;}catch(Exception){}}
    else{d=ShellMedia.Duration(c.Path);if(d<=0)d=Ffmpeg.Duration(Ffmpeg.Probe(ff),c.Path);}c.Seconds=d;
    BitmapSource b=ShellMedia.Thumbnail(c.Path,224);
    if(b==null&&!c.IsImage){string jpg=Ffmpeg.Thumbnail(ff,c,224);if(jpg!=null){try{var img=new BitmapImage();img.BeginInit();img.CacheOption=BitmapCacheOption.OnLoad;img.UriSource=new Uri(jpg);img.EndInit();img.Freeze();b=img;}catch(Exception){}}}
    lock(gate){cache[Key(c)]=b;durations[Key(c)]=d;}
    try{done(c,b);}catch(Exception){}}
  }
  public void Dispose(){lock(gate){stop=true;Monitor.Pulse(gate);}}
 }
}
