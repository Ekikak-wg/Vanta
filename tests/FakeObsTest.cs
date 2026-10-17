// Проверка ObsClient против поддельного сервера obs-websocket v5 (без OBS и без сети наружу).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReplayOverlay;

static class FakeObsTest {
 const string Password = "test-only-pass";
 static int failures;
 static int connections, identified, rejected;
 static readonly List<string> received = new List<string>();

 static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) failures++; }

 static int Main() {
  var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
  int port = ((IPEndPoint)listener.LocalEndpoint).Port;
  string dir = Path.Combine(Path.GetTempPath(), "replay-overlay-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
  Task.Run(() => Serve(listener));
  try {
   // 1. Неверный пароль: сервер закрывает соединение, клиент не считает себя подключённым.
   using (var bad = new ObsClient { ConfigFile = WriteConfig(dir, "bad.json", port, "wrong-password") }) {
    string status = null; bad.Status += s => status = s;
    bad.Start(); Thread.Sleep(1500);
    Check(!bad.Connected && rejected >= 1, "неверный пароль отклонён, клиент не подключён");
    Check(status != null, "клиенту сообщено о сбое подключения: " + status);
   }
   // 2. Верный пароль.
   using (var obs = new ObsClient { ConfigFile = WriteConfig(dir, "good.json", port, Password) }) {
    var events = new List<string>(); int ups = 0;
    obs.EventReceived += (t, d) => { lock (events) events.Add(t + ":" + Json.Str(d, "savedReplayPath")); };
    obs.ConnectionChanged += c => { if (c) Interlocked.Increment(ref ups); };
    int before = identified;
    obs.Start();
    Wait(() => obs.Connected, 3000);
    Check(obs.Connected && identified == before + 1, "аутентификация challenge/salt принята");
    Wait(() => { lock (events) return events.Count > 0; }, 2000);
    lock (events) Check(events.Contains("ReplayBufferSaved:C:/clip.mkv"), "событие ReplayBufferSaved доставлено");

    var slow = obs.Call("Slow", Json.D("n", 1)); var fast = obs.Call("Fast", Json.D("n", 2));
    Task.WaitAll(slow, fast);
    Check(Json.Str(slow.Result, "who") == "Slow" && Json.Int(slow.Result, "n") == 1 && Json.Str(fast.Result, "who") == "Fast" && Json.Int(fast.Result, "n") == 2, "ответы не по порядку сопоставлены по requestId");

    try { obs.Call("Fail").Wait(); Check(false, "ошибка OBS превращается в исключение"); }
    catch (AggregateException ex) { Check(ex.InnerException is InvalidOperationException && ex.InnerException.Message.Contains("604"), "ошибка OBS превращается в исключение: " + ex.InnerException.Message); }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    try { obs.Call("Never").Wait(); Check(false, "таймаут без ответа"); }
    catch (AggregateException ex) { Check(ex.InnerException is TimeoutException && sw.ElapsedMilliseconds >= 7500 && sw.ElapsedMilliseconds < 11000, "таймаут без ответа через " + sw.ElapsedMilliseconds + " мс"); }

    // 3. Обрыв во время запроса: ожидающий вызов завершается ошибкой, затем переподключение.
    var hanging = obs.Call("Drop");
    try { hanging.Wait(); Check(false, "обрыв завершает ожидающий вызов"); }
    catch (AggregateException ex) { Check(ex.InnerException is IOException, "обрыв завершает ожидающий вызов: " + ex.InnerException.GetType().Name); }
    Wait(() => !obs.Connected, 1000);
    Wait(() => obs.Connected && ups >= 2, 6000);
    Check(obs.Connected && ups >= 2, "автоматическое переподключение после обрыва");
    var again = obs.Call("Fast", Json.D("n", 3)); again.Wait();
    Check(Json.Int(again.Result, "n") == 3, "запросы работают после переподключения");
    lock (received) Check(received.Count(r => r == "Never") == 1 && received.All(r => r.Length > 0), "все запросы содержали requestType");
   }
   using (var none = new ObsClient { ConfigFile = Path.Combine(dir, "missing.json") }) { string s = null; none.Status += x => s = x; none.Start(); Thread.Sleep(800); Check(!none.Connected && s != null && s.Contains("Движок записи"), "нет конфигурации: понятное сообщение"); }
  } finally { listener.Stop(); try { Directory.Delete(dir, true); } catch { } }
  Console.WriteLine(failures == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: провалов " + failures);
  return failures == 0 ? 0 : 1;
 }

 static void Wait(Func<bool> cond, int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (!cond() && DateTime.Now < end) Thread.Sleep(20); }

 static string WriteConfig(string dir, string name, int port, string password) {
  string path = Path.Combine(dir, name);
  File.WriteAllText(path, Json.Encode(Json.D("server_enabled", true, "server_port", port, "auth_required", true, "server_password", password)));
  return path;
 }

 static void Serve(TcpListener listener) {
  while (true) {
   TcpClient client;
   try { client = listener.AcceptTcpClient(); } catch { return; }
   Task.Run(() => { try { Session(client); } catch { } finally { client.Close(); } });
  }
 }

 static string B64Sha(string s) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s))); }

 static void Session(TcpClient client) {
  Interlocked.Increment(ref connections);
  var stream = client.GetStream();
  // HTTP-рукопожатие WebSocket.
  var header = new StringBuilder(); var one = new byte[1];
  while (!header.ToString().EndsWith("\r\n\r\n")) { if (stream.Read(one, 0, 1) != 1) return; header.Append((char)one[0]); }
  string key = header.ToString().Split('\n').First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Substring(18).Trim();
  string accept; using (var sha1 = SHA1.Create()) accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
  var resp = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
  stream.Write(resp, 0, resp.Length);
  object gate = new object();
  Action<object> send = v => { lock (gate) WriteFrame(stream, 1, Encoding.UTF8.GetBytes(Json.Encode(v))); };

  string salt = "c2FsdA==", challenge = "Y2hhbGxlbmdl";
  send(Json.D("op", 0, "d", Json.D("obsWebSocketVersion", "5.6.0", "rpcVersion", 1, "authentication", Json.D("challenge", challenge, "salt", salt))));
  var identify = Json.Decode(ReadText(stream));
  var d = Json.Obj(identify["d"]);
  if (Json.Int(identify, "op") != 1 || Json.Str(d, "authentication") != B64Sha(B64Sha(Password + salt) + challenge)) {
   Interlocked.Increment(ref rejected); lock (gate) WriteFrame(stream, 8, new byte[] { 0x0F, 0xA9 }); return; // 4009 AuthenticationFailed
  }
  Interlocked.Increment(ref identified);
  send(Json.D("op", 2, "d", Json.D("negotiatedRpcVersion", 1)));
  send(Json.D("op", 5, "d", Json.D("eventType", "ReplayBufferSaved", "eventIntent", 64, "eventData", Json.D("savedReplayPath", "C:/clip.mkv"))));
  while (true) {
   string text = ReadText(stream); if (text == null) return;
   var msg = Json.Decode(text); var r = Json.Obj(msg["d"]);
   string type = Json.Str(r, "requestType"), id = Json.Str(r, "requestId"); var data = Json.Obj(r.ContainsKey("requestData") ? r["requestData"] : null);
   lock (received) received.Add(type);
   Func<bool, int, string, object, object> reply = (ok, code, comment, payload) => Json.D("op", 7, "d", Json.D("requestType", type, "requestId", id, "requestStatus", Json.D("result", ok, "code", code, "comment", comment), "responseData", payload));
   if (type == "Slow") Task.Delay(300).ContinueWith(_ => send(reply(true, 100, null, Json.D("who", "Slow", "n", Json.Int(data, "n")))));
   else if (type == "Fast") send(reply(true, 100, null, Json.D("who", "Fast", "n", Json.Int(data, "n"))));
   else if (type == "Fail") send(reply(false, 604, "bad", null));
   else if (type == "Drop") { Thread.Sleep(200); return; }
  }
 }

 static void WriteFrame(Stream s, int opcode, byte[] payload) {
  var head = new List<byte> { (byte)(0x80 | opcode) };
  if (payload.Length < 126) head.Add((byte)payload.Length);
  else if (payload.Length < 65536) { head.Add(126); head.Add((byte)(payload.Length >> 8)); head.Add((byte)payload.Length); }
  else { head.Add(127); for (int i = 7; i >= 0; i--) head.Add((byte)((long)payload.Length >> (8 * i))); }
  s.Write(head.ToArray(), 0, head.Count); s.Write(payload, 0, payload.Length); s.Flush();
 }

 static byte[] ReadExact(Stream s, int n) { var b = new byte[n]; int got = 0; while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return null; got += r; } return b; }

 static string ReadText(Stream s) {
  var all = new MemoryStream();
  while (true) {
   var h = ReadExact(s, 2); if (h == null) return null;
   int opcode = h[0] & 0x0F; bool fin = (h[0] & 0x80) != 0; long len = h[1] & 0x7F;
   if (len == 126) { var e = ReadExact(s, 2); len = (e[0] << 8) | e[1]; }
   else if (len == 127) { var e = ReadExact(s, 8); len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i]; }
   var mask = (h[1] & 0x80) != 0 ? ReadExact(s, 4) : null;
   var p = ReadExact(s, (int)len); if (p == null) return null;
   if (mask != null) for (int i = 0; i < p.Length; i++) p[i] ^= mask[i % 4];
   if (opcode == 8) return null;
   if (opcode == 9 || opcode == 10) continue;
   all.Write(p, 0, p.Length);
   if (fin) return Encoding.UTF8.GetString(all.ToArray());
  }
 }
}
