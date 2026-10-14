// Скорость реакции PrivacyWatcher на настоящие окна другой программы (charmap.exe — «исключённая»).
// Сценарии: вынесли на экран без активации (раньше ловилось только страховкой раз в 1,5 с), спрятали,
// показали без активации (как всплывающие окна мессенджеров), свернули, развернули.
// Окно на несколько секунд появляется поверх всех — запускать, когда никто не играет.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ReplayOverlay;

static class PrivacyLatencyTest {
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
 [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
 static int failures;
 static string seen = ""; static readonly Stopwatch clock = new Stopwatch();
 static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) failures++; }
 static double WaitFor(Func<string, bool> cond, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); if (cond(seen)) return sw.Elapsed.TotalMilliseconds; Thread.Sleep(1); } return -1; }
 static void Pump(int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); } }

 [STAThread] static int Main() {
  var w = new PrivacyWatcher(); w.SetExcluded(new[] { "charmap" }); w.Changed += v => seen = v;
  var p = Process.Start("charmap.exe");
  try {
   IntPtr h = IntPtr.Zero; for (int i = 0; i < 100 && h == IntPtr.Zero; i++) { Thread.Sleep(50); p.Refresh(); h = p.MainWindowHandle; }
   Check(h != IntPtr.Zero, "окно charmap появилось");
   SetWindowPos(h, IntPtr.Zero, -4000, -4000, 0, 0, 0x0001 | 0x0010 | 0x0004); // за краем экрана, без активации
   Pump(300);
   w.Enabled = true; Pump(500);
   Check(seen == "", "окно за краем экрана — не на экране");

   SetWindowPos(h, new IntPtr(-1), 200, 200, 0, 0, 0x0001 | 0x0010); // на экран, поверх всех, без активации и без события «показ»
   double t1 = WaitFor(v => v.Contains("charmap"), 3000);
   Check(t1 >= 0 && t1 < 200, "вынесли на экран без активации — замечено за " + t1.ToString("0") + " мс (раньше — до 1500 мс)");

   ShowWindow(h, 0); // SW_HIDE
   double t2 = WaitFor(v => v == "", 3000);
   Check(t2 >= 0 && t2 < 200, "спрятали — замечено за " + t2.ToString("0") + " мс");

   ShowWindow(h, 4); // SW_SHOWNOACTIVATE — как всплывающее окно мессенджера
   double t3 = WaitFor(v => v.Contains("charmap"), 3000);
   Check(t3 >= 0 && t3 < 100, "показали без активации — замечено за " + t3.ToString("0") + " мс");

   ShowWindow(h, 6); double t4 = WaitFor(v => v == "", 3000);   // свернули
   Check(t4 >= 0 && t4 < 200, "свернули — замечено за " + t4.ToString("0") + " мс");
   ShowWindow(h, 9); double t5 = WaitFor(v => v.Contains("charmap"), 3000); // развернули
   Check(t5 >= 0 && t5 < 100, "развернули — замечено за " + t5.ToString("0") + " мс");
  } finally { w.Enabled = false; try { p.CloseMainWindow(); if (!p.WaitForExit(2000)) p.Kill(); } catch { } }
  Console.WriteLine(failures == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: провалов " + failures);
  return failures == 0 ? 0 : 1;
 }
}
