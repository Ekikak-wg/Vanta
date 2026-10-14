// Проверка утечек кадров скрываемой программы в запись OBS.
//   PrivacyLeakTest run                   — показывает пурпурные окна в трёх сценариях (программа сама играет роль «исключённой»)
//   PrivacyLeakTest analyze <файл> [сек]  — ищет в последних N секундах записи кадры с пурпурным (через ffmpeg)
// Порядок: добавьте PrivacyLeakTest.exe в «Не записывать приложения», выберите сцену с захватом экрана,
// выполните run, сохраните повтор, выполните analyze. Ноль кадров — утечки нет.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class PrivacyLeakTest {
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
 static readonly Color Magenta = Color.FromArgb(255, 0, 255);

 static int Main(string[] args) {
  if (args.Length >= 1 && args[0] == "run") { Run(); return 0; }
  if (args.Length >= 2 && args[0] == "analyze") return Analyze(args[1], args.Length > 2 ? int.Parse(args[2]) : 40);
  Console.WriteLine("PrivacyLeakTest run | PrivacyLeakTest analyze <файл> [секунд с конца, по умолчанию 40]");
  return 2;
 }

 sealed class NoActivateForm : Form {
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x8; return p; } } // WS_EX_NOACTIVATE | WS_EX_TOPMOST
 }
 static void Pump(int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); } }
 static void Mark(string what) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what); }

 static void Run() {
  // 1. Обычное окно с активацией.
  for (int i = 0; i < 3; i++) {
   var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(700, 300, 400, 300), BackColor = Magenta, TopMost = true, ShowInTaskbar = false };
   Mark("окно — показ"); f.Show(); f.Activate(); Pump(1500); f.Close(); Mark("окно — скрыто"); Pump(1500);
  }
  // 2. Всплывающее окно без активации (как уведомления мессенджеров).
  for (int i = 0; i < 3; i++) {
   var f = new NoActivateForm { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(1500, 880, 380, 140), BackColor = Magenta, ShowInTaskbar = false };
   Mark("всплывашка — показ"); f.Show(); Pump(1500); f.Close(); Mark("всплывашка — скрыта"); Pump(1500);
  }
  // 3. Окно уже показано за краем экрана и выносится на экран без активации и без события «показ».
  for (int i = 0; i < 2; i++) {
   var f = new NoActivateForm { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(-4000, -4000, 400, 300), BackColor = Magenta, ShowInTaskbar = false };
   f.Show(); Pump(1500);
   Mark("вынос на экран — начало"); SetWindowPos(f.Handle, new IntPtr(-1), 100, 600, 0, 0, 0x0001 | 0x0010); Pump(1500);
   SetWindowPos(f.Handle, IntPtr.Zero, -4000, -4000, 0, 0, 0x0001 | 0x0010 | 0x0004); Mark("вынос на экран — убрано"); Pump(1500); f.Close();
  }
  Mark("готово — сохраните повтор и выполните analyze");
 }

 // Кадр считается «утёкшим», если в уменьшенной копии 192×108 есть хотя бы 12 пурпурных пикселей.
 static int Analyze(string file, int lastSeconds) {
  if (!File.Exists(file)) { Console.WriteLine("нет файла: " + file); return 2; }
  const int W = 192, H = 108, Size = W * H * 3;
  var psi = new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -sseof -" + lastSeconds + " -i \"" + file + "\" -map 0:v:0 -vf scale=" + W + ":" + H + ":flags=area -pix_fmt rgb24 -f rawvideo -") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
  var p = Process.Start(psi); var stream = p.StandardOutput.BaseStream;
  var frame = new byte[Size]; int index = 0, leaked = 0; var leaks = new List<string>();
  double fps = 60;
  while (true) {
   int got = 0; while (got < Size) { int r = stream.Read(frame, got, Size - got); if (r <= 0) break; got += r; }
   if (got < Size) break;
   int count = 0;
   for (int i = 0; i < Size; i += 3) if (frame[i] > 170 && frame[i + 1] < 100 && frame[i + 2] > 170) count++;
   if (count >= 12) { leaked++; if (leaks.Count < 30) leaks.Add((index / fps).ToString("0.00", CultureInfo.InvariantCulture) + " с (" + count + " пикс.)"); }
   index++;
  }
  p.WaitForExit();
  Console.WriteLine("проверено кадров: " + index + " (последние " + lastSeconds + " с)");
  Console.WriteLine("кадров с окном скрываемой программы: " + leaked + (leaks.Count > 0 ? " — " + string.Join(", ", leaks) : ""));
  Console.WriteLine(leaked == 0 ? "ИТОГ: утечек нет" : "ИТОГ: есть утечка");
  return leaked == 0 ? 0 : 1;
 }
}
