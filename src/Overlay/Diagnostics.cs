using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ReplayOverlay {
 // Журнал работы меню: события, предупреждения, сводка каждые 5 минут. Паролей и настроек OBS сюда не пишем.
 // Файл logs\replay-overlay.log рядом с программой; при 1 МБ старый переименовывается в .1.log.
 public static class Log {
  static readonly object gate=new object();
  public static string Folder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs"); } }
  public static string FilePath { get { return Path.Combine(Folder,"replay-overlay.log"); } }
  public static void Write(string text) {
   try {
    lock(gate) {
     Directory.CreateDirectory(Folder);
     var info=new FileInfo(FilePath);
     if(info.Exists&&info.Length>1024*1024){string old=Path.Combine(Folder,"replay-overlay.1.log");if(File.Exists(old))File.Delete(old);File.Move(FilePath,old);}
     File.AppendAllText(FilePath,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)+"  "+text.Replace("\r"," ").Replace("\n"," ")+Environment.NewLine,new UTF8Encoding(false));
    }
   } catch { } // журнал не должен ломать меню
  }
 }

 // Снимок GetStats из obs-websocket.
 public sealed class StatsSample {
  public double Cpu,MemoryMb,DiskMb,Fps,RenderMs; public long RenderSkipped,RenderTotal,OutputSkipped,OutputTotal;
  static double D(Dictionary<string,object> d,string k){object x;return d.TryGetValue(k,out x)&&x!=null?Convert.ToDouble(x,CultureInfo.InvariantCulture):0;}
  static long L(Dictionary<string,object> d,string k){object x;return d.TryGetValue(k,out x)&&x!=null?Convert.ToInt64(x,CultureInfo.InvariantCulture):0;}
  public static StatsSample From(Dictionary<string,object> d) {
   return new StatsSample{Cpu=D(d,"cpuUsage"),MemoryMb=D(d,"memoryUsage"),DiskMb=D(d,"availableDiskSpace"),Fps=D(d,"activeFps"),RenderMs=D(d,"averageFrameRenderTime"),
    RenderSkipped=L(d,"renderSkippedFrames"),RenderTotal=L(d,"renderTotalFrames"),OutputSkipped=L(d,"outputSkippedFrames"),OutputTotal=L(d,"outputTotalFrames")};
  }
 }

 // Пропуски кадров раздельно: во время игры и в переходах — запуск и закрытие игры, первые секунды движка.
 // В переходах игра пересоздаёт окно и устройство видеокарты, и пара пропущенных кадров там ожидаема.
 public sealed class SkipLedger {
  public const int QuietSeconds=20;
  public long InGame,Transition;
  public DateTime QuietUntil=DateTime.MinValue;
  public void Mark(DateTime now){var t=now.AddSeconds(QuietSeconds);if(t>QuietUntil)QuietUntil=t;}
  public bool Quiet(DateTime now){return now<=QuietUntil;}
  // Новые пропуски (кодировщик + отрисовка) между снимками. Движок перезапустился (счётчики уменьшились) — интервал не считаем.
  public long Add(StatsSample prev,StatsSample cur,DateTime now){
   if(prev==null||cur.OutputSkipped<prev.OutputSkipped||cur.RenderSkipped<prev.RenderSkipped||cur.OutputTotal<prev.OutputTotal)return 0;
   long d=(cur.OutputSkipped-prev.OutputSkipped)+(cur.RenderSkipped-prev.RenderSkipped);
   if(Quiet(now))Transition+=d;else InGame+=d;
   return d;}
 }

 public static class Health {
  public const double LowDiskMb=5*1024; // предупреждаем, когда на диске записей меньше 5 ГБ

  public static string Summary(StatsSample s) {
   var c=CultureInfo.GetCultureInfo("ru-RU");
   return string.Format(c,"{0:0} к/с · процессор {1:0}% · ОЗУ {2:0} МБ · свободно {3}",s.Fps,s.Cpu,s.MemoryMb,s.DiskMb>=1024*1024?(s.DiskMb/1024/1024).ToString("0.0",c)+" ТБ":(s.DiskMb/1024).ToString("0.0",c)+" ГБ");
  }

  // Проблемы между двумя снимками: ключ (для ограничения частоты предупреждений) → текст.
  // Пропуски считаем проблемой, если их не меньше 5 и не меньше 1% кадров за интервал.
  public static List<KeyValuePair<string,string>> Problems(StatsSample prev,StatsSample cur) {
   var list=new List<KeyValuePair<string,string>>();
   if(prev!=null&&cur.OutputTotal>=prev.OutputTotal&&cur.OutputSkipped>=prev.OutputSkipped) {
    long total=cur.OutputTotal-prev.OutputTotal,skipped=cur.OutputSkipped-prev.OutputSkipped;
    if(skipped>=5&&skipped*100>=total)
     list.Add(new KeyValuePair<string,string>("encoder","Кодировщик не успевает: пропущено "+skipped+" из "+(total+skipped)+" кадров. Снизьте битрейт, разрешение или FPS, или выберите качество «Рекомендуемое»."));
   }
   if(prev!=null&&cur.RenderTotal>=prev.RenderTotal&&cur.RenderSkipped>=prev.RenderSkipped) {
    long total=cur.RenderTotal-prev.RenderTotal,skipped=cur.RenderSkipped-prev.RenderSkipped;
    if(skipped>=5&&skipped*100>=total)
     list.Add(new KeyValuePair<string,string>("render","Видеокарта загружена: OBS пропустил "+skipped+" кадров отрисовки. Ограничьте FPS в игре — запись станет плавнее."));
   }
   if(cur.DiskMb>0&&cur.DiskMb<LowDiskMb)
    list.Add(new KeyValuePair<string,string>("disk","Мало места на диске записей: "+(cur.DiskMb/1024).ToString("0.0",CultureInfo.GetCultureInfo("ru-RU"))+" ГБ. Повторы и записи могут не сохраниться."));
   return list;
  }
 }
}
