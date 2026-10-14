// Проверка синхронизации звука и видео в записи OBS.
//   AvSyncCheck signal [секунды]          — окно в левом верхнем углу: белая вспышка и писк 1 кГц раз в секунду
//   AvSyncCheck analyze <файл> [дорожка]  — находит вспышки и писки в записи (через ffmpeg) и считает расхождение
// Порядок: сцена с захватом экрана → AvSyncCheck signal 8 → сохранить повтор → AvSyncCheck analyze <повтор>.
// Положительное расхождение — звук отстаёт от картинки. Заметно на слух: звук раньше на 45 мс или позже на 125 мс (ITU-R BT.1359).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class AvSyncCheck {
 static int Main(string[] args) {
  if (args.Length >= 1 && args[0] == "signal") { Signal(args.Length > 1 ? int.Parse(args[1]) : 8); return 0; }
  if (args.Length >= 2 && args[0] == "analyze") return Analyze(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
  Console.WriteLine("AvSyncCheck signal [секунды] | AvSyncCheck analyze <файл> [номер аудиодорожки с 0]");
  return 2;
 }

 // Звук: весь сигнал одним WAV (писк 100 мс в начале каждой секунды), картинка — по тем же часам от начала воспроизведения.
 static void Signal(int seconds) {
  const int rate = 48000; int samples = rate * seconds;
  var ms = new MemoryStream(); var w = new BinaryWriter(ms);
  w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + samples * 2); w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
  w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
  w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(samples * 2);
  for (int i = 0; i < samples; i++) { bool on = (i % rate) < rate / 10; w.Write((short)(on ? Math.Sin(2 * Math.PI * 1000 * i / rate) * 20000 : 0)); }
  ms.Position = 0;
  var form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(0, 0, 260, 260), TopMost = true, BackColor = Color.Black, ShowInTaskbar = false };
  var player = new SoundPlayer(ms); player.Load();
  var clock = new Stopwatch(); var timer = new Timer { Interval = 5 };
  timer.Tick += (s, e) => {
   double t = clock.Elapsed.TotalSeconds;
   if (t >= seconds) { timer.Stop(); form.Close(); return; }
   var c = (t % 1.0) < 0.1 ? Color.White : Color.Black; if (form.BackColor != c) form.BackColor = c;
  };
  form.Shown += (s, e) => { player.Play(); clock.Start(); timer.Start(); };
  Application.Run(form);
 }

 static string Ffmpeg(string arguments) {
  var p = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -nostats " + arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
  var err = p.StandardError.ReadToEndAsync(); string output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
  return output + err.Result;
 }
 // Моменты, когда значение пересекает порог снизу вверх (не чаще раза в 0,5 с).
 static List<double> Onsets(List<KeyValuePair<double, double>> series, double threshold) {
  var r = new List<double>(); bool above = false;
  foreach (var p in series) { bool now = p.Value > threshold; if (now && !above && (r.Count == 0 || p.Key - r[r.Count - 1] > 0.5)) r.Add(p.Key); above = now; }
  return r;
 }
 static List<KeyValuePair<double, double>> Series(string log, string key) {
  var list = new List<KeyValuePair<double, double>>(); double t = double.NaN;
  foreach (string line in log.Split('\n')) {
   var m = Regex.Match(line, @"pts_time:([0-9.]+)"); if (m.Success) t = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
   int i = line.IndexOf(key + "="); if (i >= 0 && !double.IsNaN(t)) { double v; string s = line.Substring(i + key.Length + 1).Trim(); if (s == "-inf") v = -200; else if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue; list.Add(new KeyValuePair<double, double>(t, v)); }
  }
  return list;
 }

 static int Analyze(string file, int track) {
  if (!File.Exists(file)) { Console.WriteLine("нет файла: " + file); return 2; }
  string q = "\"" + file + "\"";
  var video = Series(Ffmpeg("-i " + q + " -map 0:v:0 -vf \"crop=200:200:20:20,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-\" -f null -"), "lavfi.signalstats.YAVG");
  var audio = Series(Ffmpeg("-i " + q + " -map 0:a:" + track + " -af \"asetnsamples=n=240:p=0,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-\" -f null -"), "lavfi.astats.Overall.RMS_level");
  if (video.Count == 0 || audio.Count == 0) { Console.WriteLine("ffmpeg не вернул данных (нужен ffmpeg в PATH и видео + аудио в файле)"); return 2; }
  double vMin = video.Min(p => p.Value), vMax = video.Max(p => p.Value);
  if (vMax - vMin < 50) { Console.WriteLine("вспышек не видно: окно сигнала не попало в кадр (нужна сцена с захватом экрана)"); return 1; }
  var flashes = Onsets(video, (vMin + vMax) / 2); var beeps = Onsets(audio, -35);
  var offsets = new List<double>();
  foreach (double f in flashes) { var near = beeps.Where(b => Math.Abs(b - f) < 0.4).OrderBy(b => Math.Abs(b - f)).ToList(); if (near.Count > 0) offsets.Add((near[0] - f) * 1000); }
  Console.WriteLine("вспышек: " + flashes.Count + ", писков: " + beeps.Count + ", пар: " + offsets.Count);
  if (offsets.Count < 3) { Console.WriteLine("слишком мало пар для вывода"); return 1; }
  offsets.Sort(); double median = offsets[offsets.Count / 2];
  Console.WriteLine("расхождение звука относительно картинки: медиана " + median.ToString("0") + " мс, разброс " + offsets[0].ToString("0") + "…" + offsets[offsets.Count - 1].ToString("0") + " мс");
  Console.WriteLine("точность измерения ≈ ±1 кадр (" + (1000.0 / 60).ToString("0") + " мс при 60 к/с) плюс задержка вывода звука Windows");
  bool ok = median > -45 && median < 125;
  Console.WriteLine(ok ? "ИТОГ: в пределах незаметного (−45…+125 мс)" : "ИТОГ: расхождение заметно — задайте задержку источника в меню (Настройки → Звук → мс)");
  return ok ? 0 : 1;
 }
}
