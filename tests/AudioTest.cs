// Проверка AudioSetup на поддельном OBS в памяти: дорожки, режимы звука приложений, устройства, возврат как было.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReplayOverlay;

static class AudioTest {
 static int failures;
 static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) failures++; }

 sealed class FakeObs {
  public string Mode = "Advanced"; public int RecTracks = 1;
  public Dictionary<string, string> Params = new Dictionary<string, string>();
  public Dictionary<string, string> Kinds = new Dictionary<string, string> { { "Звук рабочего стола", "wasapi_output_capture" }, { "Mic/Aux", "wasapi_input_capture" }, { "Захват игры", "game_capture" } };
  public Dictionary<string, Dictionary<string, object>> Settings = new Dictionary<string, Dictionary<string, object>> {
   { "Звук рабочего стола", new Dictionary<string, object>() }, { "Mic/Aux", new Dictionary<string, object>() }, { "Захват игры", new Dictionary<string, object> { { "capture_audio", false } } } };
  public Dictionary<string, int> Masks = new Dictionary<string, int> { { "Звук рабочего стола", 1 }, { "Mic/Aux", 1 }, { "Захват игры", 1 } };
  public Dictionary<string, List<string>> Scenes = new Dictionary<string, List<string>> { { "Игра", new List<string> { "Захват игры" } }, { "Экран", new List<string>() } };
  public List<string> Calls = new List<string>();
  public Task<Dictionary<string, object>> Call(string req, Dictionary<string, object> d) {
   Calls.Add(req); var r = new Dictionary<string, object>(); string name = Json.Str(d, "inputName");
   switch (req) {
    case "GetProfileParameter": {
      string p = Json.Str(d, "parameterName");
      r["parameterValue"] = p == "Mode" ? Mode : p == "RecTracks" ? RecTracks.ToString() : (Params.ContainsKey(p) ? Params[p] : ""); break; }
    case "SetProfileParameter": { string p = Json.Str(d, "parameterName"); if (p == "RecTracks") RecTracks = int.Parse(Json.Str(d, "parameterValue")); else Params[p] = Json.Str(d, "parameterValue"); break; }
    case "GetSpecialInputs": r["desktop1"] = "Звук рабочего стола"; r["mic1"] = "Mic/Aux"; break;
    case "GetInputList": {
      string kind = Json.Str(d, "inputKind");
      r["inputs"] = Kinds.Where(k => kind.Length == 0 || k.Value == kind).Select(k => (object)Json.D("inputName", k.Key, "inputKind", k.Value)).ToArray(); break; }
    case "GetInputSettings": if (!Settings.ContainsKey(name)) throw new InvalidOperationException("No source (600)"); r["inputSettings"] = new Dictionary<string, object>(Settings[name]); break;
    case "SetInputSettings": foreach (var kv in Json.Obj(d["inputSettings"])) Settings[name][kv.Key] = kv.Value; break;
    case "GetInputAudioTracks": r["inputAudioTracks"] = AudioSetup.Tracks(Masks[name]); break;
    case "SetInputAudioTracks": Masks[name] = AudioSetup.Mask(Json.Obj(d["inputAudioTracks"])); break;
    case "CreateInput": {
      if (Kinds.ContainsKey(name)) throw new InvalidOperationException("exists (601)");
      Kinds[name] = Json.Str(d, "inputKind"); Settings[name] = Json.Obj(d["inputSettings"]); Masks[name] = 1; Scenes[Json.Str(d, "sceneName")].Add(name); break; }
    case "CreateSceneItem": Scenes[Json.Str(d, "sceneName")].Add(Json.Str(d, "sourceName")); break;
    case "GetSceneItemList": r["sceneItems"] = Scenes[Json.Str(d, "sceneName")].Select(n => (object)Json.D("sourceName", n)).ToArray(); break;
    case "RemoveInput": Kinds.Remove(name); Settings.Remove(name); Masks.Remove(name); foreach (var s in Scenes.Values) s.Remove(name); break;
    case "GetInputPropertiesListPropertyItems":
      r["propertyItems"] = new object[] { Json.D("itemName", "По умолчанию", "itemValue", "default", "itemEnabled", true), Json.D("itemName", "Гарнитура", "itemValue", "{dev-1}", "itemEnabled", true), Json.D("itemName", "Отключено", "itemValue", "{dev-2}", "itemEnabled", false) }; break;
   }
   return Task.FromResult(r);
  }
 }

 static int Main() {
  var scenes = new List<string> { "Игра", "Экран" };
  Check(AudioSetup.Mask(AudioSetup.Tracks(AudioSetup.Full | AudioSetup.Desktop)) == 17, "маска дорожек ↔ словарь «1».«6»");

  var obs = new FakeObs(); var a = new AudioSetup(obs.Call, new AudioState());
  a.SplitTracks(scenes, true).Wait();
  Check(obs.Masks["Звук рабочего стола"] == 17 && obs.Masks["Mic/Aux"] == 5 && obs.Masks["Захват игры"] == 2, "дорожки: рабочий стол 1+5, микрофон 1+3, игра 2");
  Check(Json.Bool(obs.Settings["Захват игры"], "capture_audio"), "у захвата игры включён звук игры");
  Check(obs.Kinds.ContainsKey("Discord — звук") && obs.Masks["Discord — звук"] == 8 && obs.Scenes["Игра"].Contains("Discord — звук") && obs.Scenes["Экран"].Contains("Discord — звук"), "создан «Discord — звук» на дорожке 4, в обеих сценах");
  Check(Json.Str(obs.Settings["Discord — звук"], "window") == "::Discord.exe" && Json.Int(obs.Settings["Discord — звук"], "priority") == 2, "Discord захватывается по exe");
  Check(obs.RecTracks == 31, "в запись включены дорожки 1–5");
  Check(obs.Params["Track2Name"] == "Игра" && obs.Params["Track4Name"] == "Discord", "пустые названия дорожек заполнены");
  int creates = obs.Calls.Count(c => c == "CreateInput");
  a.SplitTracks(scenes, true).Wait();
  Check(obs.Calls.Count(c => c == "CreateInput") == creates, "повторный запуск не создаёт дубликатов");
  obs.Params["Track1Name"] = "Мой микс"; obs.Params.Remove("Track3Name"); a.SplitTracks(scenes, true).Wait();
  Check(obs.Params["Track1Name"] == "Мой микс", "своё название дорожки не перезаписывается");

  var simple = new FakeObs { Mode = "Simple" };
  try { new AudioSetup(simple.Call, new AudioState()).SplitTracks(scenes, true).Wait(); Check(false, "простой режим вывода — понятная ошибка"); }
  catch (AggregateException ex) { Check(ex.InnerException.Message.Contains("расширенный"), "простой режим вывода — понятная ошибка"); }

  // Режим «только выбранные»
  var o2 = new FakeObs(); var st = new AudioState(); var b = new AudioSetup(o2.Call, st);
  b.SplitTracks(scenes, true).Wait();
  b.ApplyMode("only", new List<string> { "Spotify" }, scenes).Wait();
  Check(o2.Masks["Звук рабочего стола"] == 0, "«только выбранные»: звук рабочего стола убран из записи");
  Check(o2.Masks["Захват игры"] == 3 && o2.Masks["Discord — звук"] == 9, "игра и Discord добавлены в общую дорожку");
  Check(o2.Kinds.ContainsKey("Звук · Spotify") && o2.Masks["Звук · Spotify"] == 17 && Json.Str(o2.Settings["Звук · Spotify"], "window") == "::Spotify.exe", "для Spotify создан свой источник на дорожках 1 и 5");
  b.ApplyMode("only", new List<string> { "vlc" }, scenes).Wait();
  Check(!o2.Kinds.ContainsKey("Звук · Spotify") && o2.Kinds.ContainsKey("Звук · vlc"), "убранная из списка программа — её источник удалён");
  Check(st.SavedMasks["Звук рабочего стола"] == 17, "прежняя маска рабочего стола запомнена один раз");

  // Режим «все, кроме»: какие программы добавить
  var missing = AudioSetup.MissingApps(new[] { "Spotify", "Telegram", "SpaceGame", "vlc", "obs64" }, new HashSet<string> { "Telegram" }, new HashSet<string> { "SpaceGame", "obs64" }, st);
  Check(missing.SequenceEqual(new[] { "Spotify" }), "«все, кроме»: исключённые, игра, OBS и уже добавленные — пропущены");

  // Назад к «весь звук»
  b.ApplyMode("all", new List<string>(), scenes).Wait();
  Check(o2.Masks["Звук рабочего стола"] == 17 && o2.Masks["Захват игры"] == 2 && o2.Masks["Discord — звук"] == 8, "«весь звук»: прежние дорожки вернулись");
  Check(!o2.Kinds.Keys.Any(k => k.StartsWith(AudioSetup.AppPrefix)) && o2.Kinds.ContainsKey("Discord — звук"), "источники программ удалены, «Discord — звук» оставлен");
  Check(st.SavedMasks.Count == 0, "сохранённые маски очищены");

  // Устройства
  var devs = b.Devices(true).Result;
  Check(devs.Count == 1 && devs[0].Key == "Гарнитура", "список устройств без «По умолчанию» и отключённых");
  string dev = b.AddDevice(true, "Гарнитура", "{dev-1}", scenes).Result;
  Check(dev == "Микрофон · Гарнитура" && o2.Masks[dev] == 5 && Json.Str(o2.Settings[dev], "device_id") == "{dev-1}" && st.Created.Contains(dev), "второй микрофон: дорожки 1 и 3, своё устройство");
  b.Remove(dev).Wait();
  Check(!o2.Kinds.ContainsKey(dev) && !st.Created.Contains(dev), "добавленное устройство удаляется");

  var json = Json.Decode(Json.Encode(st.ToJson())); var back = AudioState.FromJson(json);
  Check(back.Created.SequenceEqual(st.Created), "состояние звука сохраняется и читается");

  // Раскладка дорожек
  Func<string,bool,string,int> D = AudioSetup.Desired;
  Check(D("split", true, "desktop") == 17 && D("split", true, "mic") == 5 && D("split", true, "game") == 2 && D("split", true, "discord") == 8, "раздельные при «весь звук»: рабочий стол 1+5, микрофон 1+3, игра 2, Discord 4 — как прежняя кнопка");
  Check(D("split", false, "desktop") == 0 && D("split", false, "game") == 3 && D("split", false, "discord") == 9 && D("split", false, "app") == 17, "раздельные при «только выбранные»: рабочий стол не пишется, игра и Discord в общей дорожке своими источниками");
  Check(D("common", true, "desktop") == 1 && D("common", true, "game") == 0 && D("common", true, "discord") == 0 && D("common", true, "mic") == 1, "общая при «весь звук»: игра и Discord уже есть в звуке рабочего стола — отдельно не пишутся");
  Check(D("common", false, "game") == 1 && D("common", false, "app") == 1 && D("common", false, "desktop") == 0, "общая при «кроме выбранных»: всё своими источниками в дорожку 1");
  Check(AudioSetup.RecTracksFor("common", 63, 0) == 1 && AudioSetup.RecTracksFor("split", 1, 0) == 31 && AudioSetup.RecTracksFor("split", 33, 0) == 63 && AudioSetup.RecTracksFor("custom", 31, 5) == 5, "записываемые дорожки: общая — 1, раздельные — 1–5 (6-я сохраняется), своя — как выбрано");
  Check(AudioSetup.LayoutOf(1) == "common" && AudioSetup.LayoutOf(31) == "split" && AudioSetup.LayoutOf(63) == "split" && AudioSetup.LayoutOf(5) == "custom", "раскладка узнаётся по записываемым дорожкам");
  var srcOk = new[] { new AudioSetup.AudioSource { Name = "Рабочий стол", Role = "desktop", Mask = 17 }, new AudioSetup.AudioSource { Name = "Игра", Role = "game", Mask = 2, GameAudio = true }, new AudioSetup.AudioSource { Name = "Discord", Role = "discord", Mask = 8 } };
  Check(AudioSetup.Duplicates(srcOk, 31).Count == 0, "раздельные: дублей нет");
  var srcDup = new[] { new AudioSetup.AudioSource { Name = "Рабочий стол", Role = "desktop", Mask = 1 }, new AudioSetup.AudioSource { Name = "Игра", Role = "game", Mask = 1, GameAudio = true }, new AudioSetup.AudioSource { Name = "Discord", Role = "discord", Mask = 2 } };
  var dups = AudioSetup.Duplicates(srcDup, 1);
  Check(dups.Count == 1 && dups[0].Contains("Игра") && dups[0].StartsWith("Дорожка 1"), "игра и рабочий стол на одной записываемой дорожке — дубль: " + string.Join("; ", dups));
  var srcSilentGame = new[] { new AudioSetup.AudioSource { Name = "Рабочий стол", Role = "desktop", Mask = 1 }, new AudioSetup.AudioSource { Name = "Игра", Role = "game", Mask = 1, GameAudio = false } };
  Check(AudioSetup.Duplicates(srcSilentGame, 1).Count == 0, "захват игры без звука — не дубль");
  Check(AudioSetup.Duplicates(srcDup, 2).Count == 0, "дорожка 1 не записывается — дубля в файле нет");
  Check(D("split", true, "app") == 0 && D("common", true, "app") == 0, "источник программы (например «Музыка») при записи всего звука не ставится на дорожки рабочего стола");
  var music = new[] { new AudioSetup.AudioSource { Name = "Рабочий стол", Role = "desktop", Mask = 17 }, new AudioSetup.AudioSource { Name = "Музыка", Role = "app", Mask = 17 }, new AudioSetup.AudioSource { Name = "Игра", Role = "game", Mask = 2, GameAudio = true } };
  Check(AudioSetup.Duplicates(music, 31).Count == 2, "«Музыка» на дорожках 1 и 5 вместе с рабочим столом — два дубля");
  var fix = AudioSetup.FixDuplicates(music, 31);
  Check(fix.Count == 1 && fix["Музыка"] == 0, "«Убрать двойной звук»: «Музыка» снимается с дорожек 1 и 5, игра не трогается");
  var music6 = new[] { music[0], new AudioSetup.AudioSource { Name = "Музыка", Role = "app", Mask = 17 | 32 } };
  Check(AudioSetup.FixDuplicates(music6, 63)["Музыка"] == 32, "отдельная дорожка 6 у «Музыки» сохраняется");
  Console.WriteLine(failures == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: провалов " + failures);
  return failures == 0 ? 0 : 1;
 }
}
