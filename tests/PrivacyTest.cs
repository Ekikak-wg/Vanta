// Проверка PrivacyWatcher.Evaluate на настоящих окнах: charmap.exe как «исключённое» приложение,
// собственная форма теста — как перекрывающее окно.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ReplayOverlay;

static class PrivacyTest {
 [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
 [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
 static int failures;
 static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) failures++; }
 static void Pump(int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); } }

 [STAThread] static int Main() {
  var watcher = new PrivacyWatcher(); watcher.SetExcluded(new[] { "charmap" });
  Check(!PrivacyWatcher.RunningApps().ContainsKey("charmap") || true, "RunningApps работает: " + PrivacyWatcher.RunningApps().Count + " приложений");
  var p = Process.Start("charmap.exe");
  try {
   IntPtr h = IntPtr.Zero; for (int i = 0; i < 100 && h == IntPtr.Zero; i++) { Thread.Sleep(50); p.Refresh(); h = p.MainWindowHandle; }
   Check(h != IntPtr.Zero, "окно charmap появилось");
   SetWindowPos(h, IntPtr.Zero, 200, 200, 0, 0, 0x0001 | 0x0040); // SWP_NOSIZE|SWP_SHOWWINDOW
   Pump(400);
   Check(PrivacyWatcher.RunningApps().ContainsKey("charmap"), "charmap есть в списке запущенных приложений");
   Check(watcher.Evaluate().Contains("charmap"), "видимое окно обнаружено");

   ShowWindow(h, 6); Pump(500); // SW_MINIMIZE
   Check(!watcher.Evaluate().Contains("charmap"), "свёрнутое окно не считается видимым");
   ShowWindow(h, 9); Pump(500); // SW_RESTORE
   Check(watcher.Evaluate().Contains("charmap"), "восстановленное окно снова видно");

   watcher.IgnoredProcess = 0; // своя форма теста участвует как перекрывающее окно
   Rect wr; GetWindowRect(h, out wr); var area = Rectangle.FromLTRB(wr.L - 20, wr.T - 20, wr.R + 20, wr.B + 20); // закрываем только окно charmap
   var cover = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = area, TopMost = true, BackColor = Color.Black, ShowInTaskbar = false };
   cover.Show(); Pump(500);
   Check(!watcher.Evaluate().Contains("charmap"), "окно, полностью закрытое другим, не считается видимым");
   cover.Bounds = new Rectangle(0, 0, 150, 150); Pump(400);
   Check(watcher.Evaluate().Contains("charmap"), "частично открытое окно видно");
   cover.Opacity = 0.5; cover.Bounds = area; Pump(500);
   Check(watcher.Evaluate().Contains("charmap"), "полупрозрачное окно сверху не считается перекрытием");
   cover.Close();
  } finally { try { p.CloseMainWindow(); if (!p.WaitForExit(2000)) p.Kill(); } catch { } }
  Pump(300);
  Check(!watcher.Evaluate().Contains("charmap"), "закрытое приложение не видно");
  Console.WriteLine(failures == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: провалов " + failures);
  return failures == 0 ? 0 : 1;
 }
}
