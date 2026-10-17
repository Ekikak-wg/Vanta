// Проверка Health.Problems: когда предупреждать о пропусках кадров и месте на диске, а когда молчать.
using System;
using System.Collections.Generic;
using System.Linq;
using ReplayOverlay;

static class DiagnosticsTest {
 static int failures;
 static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) failures++; }
 static StatsSample S(long outSkip, long outTotal, long renSkip, long renTotal, double diskMb) {
  return new StatsSample { OutputSkipped = outSkip, OutputTotal = outTotal, RenderSkipped = renSkip, RenderTotal = renTotal, DiskMb = diskMb, Fps = 60, Cpu = 3.5, MemoryMb = 420 };
 }
 static HashSet<string> Keys(StatsSample a, StatsSample b) { return new HashSet<string>(Health.Problems(a, b).Select(p => p.Key)); }

 static int Main() {
  var baseS = S(0, 6000, 0, 6000, 500000);
  Check(Keys(null, baseS).Count == 0, "первый снимок без предыдущего — без предупреждений");
  Check(Keys(baseS, S(0, 6600, 0, 6600, 500000)).Count == 0, "10 с без пропусков — тишина");
  Check(Keys(baseS, S(4, 6600, 0, 6600, 500000)).Count == 0, "4 пропуска — ниже порога, тишина");
  Check(Keys(baseS, S(5, 7100, 0, 6600, 500000)).Count == 0, "5 пропусков из 1100 (<1%) — тишина");
  Check(Keys(baseS, S(30, 6600, 0, 6600, 500000)).Contains("encoder"), "30 из 600 кадров кодировщика — предупреждение");
  Check(Keys(baseS, S(0, 6600, 40, 6600, 500000)).Contains("render"), "40 пропусков отрисовки — предупреждение");
  var both = Keys(baseS, S(30, 6600, 40, 6600, 500000));
  Check(both.Contains("encoder") && both.Contains("render"), "обе проблемы сразу — оба предупреждения");
  Check(Keys(baseS, S(0, 100, 0, 100, 500000)).Count == 0, "счётчики сбросились (OBS перезапущен) — без ложной тревоги");
  Check(Keys(baseS, S(0, 6600, 0, 6600, 4000)).Contains("disk"), "4 ГБ свободно — предупреждение о месте");
  Check(!Keys(baseS, S(0, 6600, 0, 6600, 6000)).Contains("disk"), "6 ГБ свободно — тишина");
  Check(!Keys(baseS, S(0, 6600, 0, 6600, 0)).Contains("disk"), "место неизвестно (0) — тишина");
  string text = Health.Problems(baseS, S(30, 6600, 0, 6600, 500000)).First().Value;
  Check(text.Contains("30") && text.Contains("630"), "в тексте число пропущенных и всех кадров: " + text);
  string sum = Health.Summary(S(0, 1, 0, 1, 1.7 * 1024 * 1024));
  Check(sum.Contains("60 к/с") && sum.Contains("ТБ"), "сводка: " + sum);
  // Раздельный счёт: 20 с после запуска или закрытия игры — переход, дальше — игра.
  var led = new SkipLedger(); var t0 = new DateTime(2026, 10, 17, 9, 0, 0);
  led.Add(S(0, 600, 0, 600, 1), S(1, 1200, 2, 1200, 1), t0);
  Check(led.InGame == 3 && led.Transition == 0, "без отметки пропуски считаются игровыми");
  led.Mark(t0.AddSeconds(5));
  led.Add(S(1, 1200, 2, 1200, 1), S(4, 1800, 3, 1800, 1), t0.AddSeconds(24));
  Check(led.Transition == 4 && led.InGame == 3 && led.Quiet(t0.AddSeconds(25)), "через 19 с после запуска игры — переход");
  led.Add(S(4, 1800, 3, 1800, 1), S(5, 2400, 3, 2400, 1), t0.AddSeconds(26));
  Check(led.InGame == 4 && !led.Quiet(t0.AddSeconds(26)), "через 21 с — снова игра");
  Check(led.Add(S(5, 2400, 3, 2400, 1), S(0, 60, 0, 60, 1), t0.AddSeconds(30)) == 0 && led.InGame == 4, "движок перезапущен — интервал не считается");
  Console.WriteLine(failures == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: провалов " + failures);
  return failures == 0 ? 0 : 1;
 }
}
