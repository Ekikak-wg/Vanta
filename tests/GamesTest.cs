// Проверка распознавания игр (GameRules) и применения к OBS (GameSession) на поддельном OBS в памяти.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReplayOverlay;

static class GamesTest {
 static int failures;
 static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) failures++; }

 // Поддельный OBS: один источник захвата игры, две сцены, журнал запросов.
 sealed class FakeObs {
  public Dictionary<string, object> Settings = new Dictionary<string, object> { { "capture_mode", "any_fullscreen" }, { "anti_cheat_hook", true } };
  public string Scene = "05 · Заставка"; public string Bounds = "OBS_BOUNDS_NONE"; public List<string> Calls = new List<string>();
  public Task<Dictionary<string, object>> Call(string req, Dictionary<string, object> d) {
   Calls.Add(req); var r = new Dictionary<string, object>();
   if (req == "GetInputList") r["inputs"] = new object[] { Json.D("inputName", "Игра", "inputKind", "game_capture") };
   else if (req == "GetInputSettings") { if (Json.Str(d, "inputName") != "Игра") throw new InvalidOperationException("No source (600)"); r["inputSettings"] = new Dictionary<string, object>(Settings); }
   else if (req == "SetInputSettings") {
    var s = Json.Obj(d["inputSettings"]);
    if (Json.Bool(d, "overlay")) foreach (var kv in s) Settings[kv.Key] = kv.Value; else Settings = new Dictionary<string, object>(s);
   }
   else if (req == "SetCurrentProgramScene") Scene = Json.Str(d, "sceneName");
   else if (req == "GetVideoSettings") { r["baseWidth"] = 1920; r["baseHeight"] = 1080; }
   else if (req == "GetSceneList") r["scenes"] = new object[] { Json.D("sceneName", "02 · Игра") };
   else if (req == "GetSceneItemList") r["sceneItems"] = new object[] { Json.D("sceneItemId", 1, "inputKind", "game_capture") };
   else if (req == "GetSceneItemTransform") r["sceneItemTransform"] = Json.D("boundsType", Bounds);
   else if (req == "SetSceneItemTransform") Bounds = Json.Str(Json.Obj(d["sceneItemTransform"]), "boundsType");
   return Task.FromResult(r);
  }
 }

 static int Main() {
  var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "charmap" };
  var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SpaceGame" };
  Func<string, string, bool, bool?, string> C = (exe, path, covers, has3d) => GameRules.Classify(new WindowFacts { Exe = exe, Path = path, CoversMonitor = covers, Has3D = has3d }, none, none);

  Check(C("SpaceGame", @"D:\SteamLibrary\steamapps\common\SpaceGame\SpaceGame.exe", false, null) == "из библиотеки Steam", "Steam-игра в окне — игра");
  Check(C("Fortnite", @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient.exe", true, true) == "из библиотеки Epic Games", "Epic-игра — игра");
  Check(C("EpicGamesLauncher2", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher2.exe", true, true) == null, "лаунчер Epic — не игра");
  Check(C("wallpaper64", @"D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper64.exe", true, true) == null, "Wallpaper Engine — не игра");
  Check(C("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", true, true) == null, "браузер во весь экран — не игра");
  Check(C("vlc", @"C:\Program Files\VideoLAN\VLC\vlc.exe", true, true) == null, "плеер во весь экран — не игра");
  Check(C("somegame", @"D:\Games\Some\somegame.exe", true, true) == "полноэкранное 3D-окно", "неизвестная полноэкранная 3D-программа — игра");
  Check(C("valorant", @"D:\Other\valorant.exe", true, null) == "полноэкранное окно", "защищённый процесс во весь экран (модули не прочитать) — игра");
  Check(C("paint", @"C:\Windows\paint.exe", true, false) == null, "полноэкранное окно без 3D — не игра");
  Check(C("somegame", @"D:\Games\Some\somegame.exe", false, true) == null, "окно не во весь экран и не из библиотеки — не игра");
  Check(GameRules.Classify(new WindowFacts { Exe = "charmap", Path = @"C:\Windows\System32\charmap.exe" }, mine, none) == "в списке «Мои игры»", "ручной список «Мои игры»");
  Check(GameRules.Classify(new WindowFacts { Exe = "SpaceGame", Path = @"D:\SteamLibrary\steamapps\common\SpaceGame\SpaceGame.exe" }, none, excluded) == null, "список «Не считать играми» сильнее библиотеки");
  var minecraft = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "javaw" };
  var builtinOverride = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vlc" };
  Check(GameRules.Classify(new WindowFacts { Exe = "vlc", Path = @"C:\Program Files\VideoLAN\VLC\vlc.exe" }, builtinOverride, none) == "в списке «Мои игры»", "ручной выбор «Мои игры» сильнее встроенного списка не-игр");
  Check(GameRules.Classify(new WindowFacts { Exe = "javaw", Path = @"C:\Program Files\Java\bin\javaw.exe" }, minecraft, minecraft) == null, "если программа в обоих списках — «Не считать играми» важнее");

  var g = new GameInfo { Exe = "SpaceGame", FileName = "SpaceGame.exe", Title = "Space Game: #1", Class = "UnrealWindow" };
  Check(g.ObsWindow == "Space Game#3A #221:UnrealWindow:SpaceGame.exe", "строка окна OBS с экранированием «:» и «#»: " + g.ObsWindow);

  var obs = new FakeObs(); var session = new GameSession(obs.Call);
  var log = session.Capture(g, true).Result;
  Check(Json.Str(obs.Settings, "capture_mode") == "window" && Json.Str(obs.Settings, "window") == g.ObsWindow && Json.Int(obs.Settings, "priority") == 2, "захват игры перенаправлен на окно игры (совпадение по exe)");
  Check(Json.Bool(obs.Settings, "anti_cheat_hook"), "остальные настройки источника сохранены");
  int sets = obs.Calls.Count(c => c == "SetInputSettings");
  session.Capture(g, true).Wait();
  Check(obs.Calls.Count(c => c == "SetInputSettings") == sets, "повторно на ту же игру — без перенастройки (захват не перецепляется)");
  session.SwitchScene("02 · Игра", obs.Scene).Wait();
  Check(obs.Scene == "02 · Игра" && session.SceneBefore == "05 · Заставка", "сцена переключена на «Только игра», прежняя запомнена");
  Check(session.FitToCanvas().Result.Count == 1 && obs.Bounds == "OBS_BOUNDS_SCALE_INNER", "захват без рамки подогнан под кадр");
  Check(session.FitToCanvas().Result.Count == 0, "уже подогнанный — не трогается");

  var back = session.Restore(obs.Scene, true).Result;
  Check(obs.Scene == "05 · Заставка", "после игры сцена возвращена");
  Check(Json.Str(obs.Settings, "capture_mode") == "any_fullscreen" && !obs.Settings.ContainsKey("window") && !obs.Settings.ContainsKey("priority") && !obs.Settings.ContainsKey("capture_cursor") && Json.Bool(obs.Settings, "anti_cheat_hook"), "захват возвращён к прежним настройкам, лишние ключи убраны");

  var obs2 = new FakeObs(); var s2 = new GameSession(obs2.Call);
  s2.Capture(g, true).Wait(); s2.SwitchScene("02 · Игра", obs2.Scene).Wait();
  obs2.Scene = "01 · Экран"; // пользователь сам сменил сцену во время игры
  s2.Restore(obs2.Scene, true).Wait();
  Check(obs2.Scene == "01 · Экран", "сцену, выбранную вручную во время игры, не возвращаем");

  var obs3 = new FakeObs(); obs3.Scene = "02 · Игра"; var s3 = new GameSession(obs3.Call) { SceneBefore = "", SceneByGame = "02 · Игра" };
  int calls3 = obs3.Calls.Count; s3.Restore(obs3.Scene, true).Wait();
  Check(obs3.Scene == "02 · Игра" && !obs3.Calls.Skip(calls3).Contains("SetCurrentProgramScene"), "пустая «сцена до игры» (из старых настроек) — сцену не переключаем");

  Console.WriteLine(failures == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: провалов " + failures);
  return failures == 0 ? 0 : 1;
 }
}
