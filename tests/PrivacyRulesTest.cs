// Проверка советчика по приватности и строгой защиты (задержка экрана, «шторка», сдвиг звука) на поддельном OBS.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReplayOverlay;

static class PrivacyRulesTest {
 static int failures;
 static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) failures++; }

 sealed class FakeObs {
  public Dictionary<string, List<Dictionary<string, object>>> Scenes = new Dictionary<string, List<Dictionary<string, object>>> {
   { "Экран", new List<Dictionary<string, object>> { Json.D("sceneItemId", 1, "sourceName", "Игра", "inputKind", "game_capture"), Json.D("sceneItemId", 2, "sourceName", "Монитор", "inputKind", "monitor_capture") } },
   { "Игра", new List<Dictionary<string, object>> { Json.D("sceneItemId", 1, "sourceName", "Игра", "inputKind", "game_capture") } } };
  public Dictionary<string, int> Offsets = new Dictionary<string, int> { { "Звук рабочего стола", 0 }, { "Микрофон", 20 } };
  public Dictionary<string, List<string>> Filters = new Dictionary<string, List<string>>();
  public Dictionary<string, bool> Enabled = new Dictionary<string, bool>();
  public List<string> Inputs = new List<string> { "Игра", "Монитор", "Звук рабочего стола", "Микрофон" };
  int nextId = 10;
  public Task<Dictionary<string, object>> Call(string req, Dictionary<string, object> d) {
   var r = new Dictionary<string, object>(); string scene = Json.Str(d, "sceneName"), name = Json.Str(d, "inputName");
   switch (req) {
    case "GetVideoSettings": r["baseWidth"] = 1920; r["baseHeight"] = 1080; break;
    case "GetSceneList": r["scenes"] = Scenes.Keys.Select(k => (object)Json.D("sceneName", k)).ToArray(); break;
    case "CreateScene": Scenes[scene] = new List<Dictionary<string, object>>(); break;
    case "RemoveScene": if (!Scenes.Remove(scene)) throw new InvalidOperationException("No scene (600)"); break;
    case "CreateInput": Inputs.Add(name); Scenes[scene].Add(Json.D("sceneItemId", nextId++, "sourceName", name, "inputKind", Json.Str(d, "inputKind"))); break;
    case "RemoveInput": Inputs.Remove(name); break;
    case "GetInputList": { string kind = Json.Str(d, "inputKind"); r["inputs"] = Inputs.Where(i => kind.Length == 0 || (kind == "game_capture" && i == "Игра")).Select(i => (object)Json.D("inputName", i)).ToArray(); break; }
    case "CreateSceneItem": { int id = nextId++; Scenes[scene].Add(Json.D("sceneItemId", id, "sourceName", Json.Str(d, "sourceName"), "inputKind", "")); if (d.ContainsKey("sceneItemEnabled")) Enabled[scene + "/" + id] = Json.Bool(d, "sceneItemEnabled"); r["sceneItemId"] = id; break; }
    case "RemoveSceneItem": Scenes[scene].RemoveAll(i => Json.Int(i, "sceneItemId") == Json.Int(d, "sceneItemId")); break;
    case "GetSceneItemList": r["sceneItems"] = Scenes[scene].Cast<object>().ToArray(); break;
    case "SetSceneItemEnabled": Enabled[scene + "/" + Json.Int(d, "sceneItemId")] = Json.Bool(d, "sceneItemEnabled"); break;
    case "SetSceneItemTransform": break;
    case "CreateSourceFilter": { string s = Json.Str(d, "sourceName"); if (!Filters.ContainsKey(s)) Filters[s] = new List<string>(); Filters[s].Add(Json.Str(d, "filterKind") + ":" + Json.Int(Json.Obj(d["filterSettings"]), "delay_ms")); break; }
    case "RemoveSourceFilter": Filters.Remove(Json.Str(d, "sourceName")); break;
    case "GetInputAudioSyncOffset": if (!Offsets.ContainsKey(name)) throw new InvalidOperationException("does not support audio (604)"); r["inputAudioSyncOffset"] = Offsets[name]; break;
    case "SetInputAudioSyncOffset": Offsets[name] = Json.Int(d, "inputAudioSyncOffset"); break;
   }
   return Task.FromResult(r);
  }
 }

 static int Main() {
  var ex = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Telegram", "Discord" };
  var none = new HashSet<string>();
  Check(PrivacyAdvisor.ExeOf("Чат#3A личное:Qt51519QWindowIcon:Telegram.exe") == "Telegram", "exe из строки окна OBS");
  var w1 = PrivacyAdvisor.Warnings(new[] { new SceneSource { Name = "Окно чата", Kind = "window_capture", Window = "Чат:Qt:Telegram.exe" } }, ex, null, none, true);
  Check(w1.Count == 1 && w1[0].Contains("напрямую"), "предупреждение: окно исключённой программы захвачено напрямую");
  var w2 = PrivacyAdvisor.Warnings(new[] { new SceneSource { Name = "Игра", Kind = "game_capture", CaptureOverlays = true } }, ex, null, none, true);
  Check(w2.Count == 1 && w2[0].Contains("Discord.exe") && w2[0].Contains("оверлеи"), "предупреждение: захват игры пишет оверлей Discord");
  Check(PrivacyAdvisor.Warnings(new[] { new SceneSource { Name = "Игра", Kind = "game_capture", CaptureOverlays = false } }, ex, null, none, true).Count == 0, "без захвата оверлеев — тишина");
  var w3 = PrivacyAdvisor.Warnings(new[] { new SceneSource { Name = "Группа", Nested = true, NestedHasMonitor = true } }, ex, null, none, true);
  Check(w3.Count == 1 && w3[0].Contains("вложенной"), "предупреждение: захват экрана во вложенной сцене");
  Check(PrivacyAdvisor.Warnings(new SceneSource[0], ex, "Telegram", none, true).Any(x => x.Contains("текущая игра")), "предупреждение: исключённая программа — это игра");
  Check(PrivacyAdvisor.Warnings(new SceneSource[0], ex, null, new HashSet<string> { "Discord" }, true).Any(x => x.Contains("«Моих играх»")), "предупреждение: программа и в играх, и в исключениях");
  Check(PrivacyAdvisor.Warnings(new SceneSource[0], ex, null, none, false).Single().Contains("нет захвата экрана"), "сцена без захвата экрана — пояснение");
  Check(PrivacyAdvisor.Warnings(new SceneSource[0], none, null, none, false).Count == 0, "без исключений — без предупреждений");

  var obs = new FakeObs(); var strict = new StrictPrivacy(obs.Call);
  strict.Enable(100, new List<string> { "Экран" }).Wait();
  Check(obs.Scenes.ContainsKey(StrictPrivacy.CoverScene) && obs.Scenes[StrictPrivacy.CoverScene].Any(i => Json.Str(i, "sourceName") == "Игра"), "сцена «шторки»: фон и захват игры");
  Check(obs.Filters.ContainsKey("Монитор") && obs.Filters["Монитор"].Single() == "gpu_delay:100", "захват экрана получил задержку 100 мс");
  int cover = strict.CoverItems["Экран"];
  Check(obs.Scenes["Экран"].Last().ContainsKey("sourceName") && Json.Str(obs.Scenes["Экран"].Last(), "sourceName") == StrictPrivacy.CoverScene && !obs.Enabled["Экран/" + cover], "«шторка» — верхний слой сцены, выключена");
  Check(obs.Offsets["Звук рабочего стола"] == 100 && obs.Offsets["Микрофон"] == 120, "звук сдвинут на 100 мс (с учётом прежней задержки)");
  strict.SetCover("Экран", true).Wait();
  Check(obs.Enabled["Экран/" + cover], "скрытие — включение «шторки»");
  strict.SetCover("Игра", true).Wait();
  Check(!obs.Enabled.ContainsKey("Игра/" + cover), "в сцене без захвата экрана «шторки» нет — ничего не делается");
  var saved = Json.Decode(Json.Encode(strict.ToJson())); var restored = new StrictPrivacy(obs.Call); restored.Load(saved);
  Check(restored.On && restored.CoverItems["Экран"] == cover && restored.Delayed.Contains("Монитор"), "состояние строгой защиты сохраняется и читается");
  obs.Offsets["Микрофон"] = 150; // пользователь подправил задержку во время строгого режима
  restored.Disable().Wait();
  Check(!obs.Filters.ContainsKey("Монитор") && !obs.Scenes.ContainsKey(StrictPrivacy.CoverScene) && !obs.Scenes["Экран"].Any(i => Json.Str(i, "sourceName") == StrictPrivacy.CoverScene), "выключение: фильтр, «шторка» и её сцена убраны");
  Check(obs.Offsets["Звук рабочего стола"] == 0 && obs.Offsets["Микрофон"] == 50, "выключение: сдвиг звука снят, ручная правка сохранена");

  Console.WriteLine(failures == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: провалов " + failures);
  return failures == 0 ? 0 : 1;
 }
}
