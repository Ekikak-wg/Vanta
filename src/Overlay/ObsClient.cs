using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ReplayOverlay {
 public static class Json {
  public static Dictionary<string,object> Obj(object value) { return value as Dictionary<string,object> ?? new Dictionary<string,object>(); }
  public static string Str(Dictionary<string,object> d,string key,string fallback="") { object x; return d.TryGetValue(key,out x)&&x!=null ? Convert.ToString(x,System.Globalization.CultureInfo.InvariantCulture) : fallback; }
  public static bool Bool(Dictionary<string,object>d,string key) { object x; return d.TryGetValue(key,out x)&&x!=null&&Convert.ToBoolean(x); }
  public static int Int(Dictionary<string,object>d,string key,int fallback=0) { object x; return d.TryGetValue(key,out x)&&x!=null?Convert.ToInt32(x):fallback; }
  public static Dictionary<string,object> D(params object[] pairs) { var d=new Dictionary<string,object>(); for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1]; return d; }
  public static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
  public static Dictionary<string,object> Decode(string value) { return Obj(new JavaScriptSerializer().DeserializeObject(value)); }
 }
 public sealed class ObsClient : IDisposable {
  private readonly CancellationTokenSource stop=new CancellationTokenSource();
  private readonly SemaphoreSlim sendGate=new SemaphoreSlim(1,1);
  private readonly ConcurrentDictionary<string,TaskCompletionSource<Dictionary<string,object>>> pending=new ConcurrentDictionary<string,TaskCompletionSource<Dictionary<string,object>>>();
  private ClientWebSocket socket;
  public string ConfigFile; // config.json obs-websocket движка записи
  public volatile bool Connected;
  public event Action<bool> ConnectionChanged;
  public event Action<string,Dictionary<string,object>> EventReceived;
  public event Action<string> Status;
  public void Start() { Task.Run((Func<Task>)ConnectLoop); }
  private static string Hash(string s) { using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s))); }
  public static string Authentication(string password,string salt,string challenge) { return Hash(Hash(password+salt)+challenge); }
  private async Task ConnectLoop() {
   while(!stop.IsCancellationRequested) {
    try {
     var config=Json.Decode(File.ReadAllText(ConfigFile));
     if(!Json.Bool(config,"server_enabled"))throw new InvalidOperationException("У движка записи выключено управление");
     int port=Json.Int(config,"server_port",4455);
     socket=new ClientWebSocket(); socket.Options.Proxy=null; socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(25);
     using(var connectTimeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
      connectTimeout.CancelAfter(5000);
      await socket.ConnectAsync(new Uri("ws://127.0.0.1:"+port),connectTimeout.Token).ConfigureAwait(false);
      var hello=await Receive(connectTimeout.Token).ConfigureAwait(false);
      if(Json.Int(hello,"op")!=0)throw new InvalidOperationException("Неподдерживаемый ответ движка записи");
      var data=Json.Obj(hello["d"]);
      var identify=Json.D("rpcVersion",1,"eventSubscriptions",719); // General|Config|Scenes|Inputs|Outputs|SceneItems|Vendors
      if(data.ContainsKey("authentication")) {
       var auth=Json.Obj(data["authentication"]);
       identify["authentication"]=Authentication(Json.Str(config,"server_password"),Json.Str(auth,"salt"),Json.Str(auth,"challenge"));
      }
      await Send(Json.D("op",1,"d",identify),connectTimeout.Token).ConfigureAwait(false);
      var reply=await Receive(connectTimeout.Token).ConfigureAwait(false);
      if(Json.Int(reply,"op")!=2)throw new InvalidOperationException("Движок записи отклонил подключение");
     }
     Connected=true; if(ConnectionChanged!=null)ConnectionChanged(true);
     while(socket.State==WebSocketState.Open&&!stop.IsCancellationRequested) {
      var msg=await Receive(stop.Token).ConfigureAwait(false); int op=Json.Int(msg,"op"); var d=Json.Obj(msg["d"]);
      if(op==7) {
       TaskCompletionSource<Dictionary<string,object>> promise;
       if(pending.TryRemove(Json.Str(d,"requestId"),out promise)) {
        var status=Json.Obj(d["requestStatus"]);
        if(Json.Bool(status,"result"))promise.TrySetResult(d.ContainsKey("responseData")?Json.Obj(d["responseData"]):Json.D());
        else promise.TrySetException(new InvalidOperationException(Json.Str(status,"comment","Движок записи отклонил команду")+" ("+Json.Int(status,"code")+")"));
       }
      } else if(op==5&&EventReceived!=null)EventReceived(Json.Str(d,"eventType"),d.ContainsKey("eventData")?Json.Obj(d["eventData"]):Json.D());
     }
    } catch(Exception ex) { if(!stop.IsCancellationRequested&&Status!=null)Status(ex is FileNotFoundException?"Движок записи ещё не настроен — жду…":"Жду движок записи…"); }
    finally {
     Connected=false; if(ConnectionChanged!=null)ConnectionChanged(false);
     foreach(var p in pending) { TaskCompletionSource<Dictionary<string,object>> t; if(pending.TryRemove(p.Key,out t))t.TrySetException(new IOException("Связь с движком записи прервана")); }
     if(socket!=null)socket.Dispose();
    }
    try { await Task.Delay(3000,stop.Token).ConfigureAwait(false); } catch(OperationCanceledException){break;}
   }
  }
  private async Task<Dictionary<string,object>> Receive(CancellationToken token) {
   var buffer=new byte[8192]; using(var memory=new MemoryStream()) {
    WebSocketReceiveResult r;
    do { r=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),token).ConfigureAwait(false); if(r.MessageType==WebSocketMessageType.Close)throw new IOException("Движок записи отключился"); memory.Write(buffer,0,r.Count); if(memory.Length>1048576)throw new IOException("Ответ движка записи слишком большой"); } while(!r.EndOfMessage);
    return Json.Decode(Encoding.UTF8.GetString(memory.ToArray()));
   }
  }
  private async Task Send(object value,CancellationToken token) {
   byte[] bytes=Encoding.UTF8.GetBytes(Json.Encode(value)); await sendGate.WaitAsync(token).ConfigureAwait(false);
   try { await socket.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,token).ConfigureAwait(false); } finally { sendGate.Release(); }
  }
  public async Task<Dictionary<string,object>> Call(string type,Dictionary<string,object> data=null) {
   if(!Connected)throw new InvalidOperationException("Движок записи ещё не подключён");
   string id=Guid.NewGuid().ToString("N"); var promise=new TaskCompletionSource<Dictionary<string,object>>(); pending[id]=promise;
   try {
    await Send(Json.D("op",6,"d",Json.D("requestType",type,"requestId",id,"requestData",data??Json.D())),stop.Token).ConfigureAwait(false);
    var done=await Task.WhenAny(promise.Task,Task.Delay(8000,stop.Token)).ConfigureAwait(false);
    if(done!=promise.Task)throw new TimeoutException("Движок записи не ответил. Состояние операции неизвестно.");
    return await promise.Task.ConfigureAwait(false);
   } finally { TaskCompletionSource<Dictionary<string,object>> old; pending.TryRemove(id,out old); }
  }
  public void Dispose() { stop.Cancel(); if(socket!=null)socket.Abort(); }
 }
}
