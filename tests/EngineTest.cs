// Проверка ObsEngine: какие запросы obs-websocket стоят за действиями меню и как события OBS превращаются в события движка.
// Без OBS и без сети: ответы на запросы задаёт тест.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReplayOverlay;

static class EngineTest {
 static int failures;
 static void Check(bool ok,string what){Console.WriteLine((ok?"OK    ":"FAIL  ")+what);if(!ok)failures++;}
 static readonly List<string> sent=new List<string>();
 static Func<string,Dictionary<string,object>,Dictionary<string,object>> answer;
 static ObsEngine Make(){sent.Clear();return new ObsEngine((r,d)=>{sent.Add(r+(d.Count>0?" "+Json.Encode(d):""));return Task.Run(()=>answer(r,d));});}
 static void Fails(Func<Task> act,string what){try{act().Wait();Check(false,what);}catch(AggregateException ex){Check(ex.InnerException is InvalidOperationException,what+": "+ex.InnerException.Message);}}

 static int Main(){
  Console.OutputEncoding=System.Text.Encoding.UTF8;
  try{
   // Запись и состояние
   answer=(r,d)=>Json.D("outputActive",true,"outputDuration",1500);
   var e=Make();
   Check(e.ToggleRecord().Result&&sent.SequenceEqual(new[]{"ToggleRecord"}),"ToggleRecord: запись идёт после переключения");
   var rs=e.Record().Result;Check(rs.Active&&rs.DurationMs==1500,"GetRecordStatus → идёт, 1,5 с");
   answer=(r,d)=>Json.D("outputActive",r=="GetVirtualCamStatus");
   Check(Make().OutputsBusy().Result&&sent.Count==3,"занята виртуальная камера — выходы заняты");
   answer=(r,d)=>Json.D("outputActive",false);
   Check(!Make().OutputsBusy().Result,"всё остановлено — выходы свободны");
   answer=(r,d)=>Json.D("recordDirectory","D:/Видео/OBS");
   Check(Make().RecordDirectory().Result==@"D:\Видео\OBS","папка записей — с обратными косыми");
   e=Make();e.SetRecordDirectory(@"E:\Клипы").Wait();Check(sent[0]=="SetRecordDirectory "+Json.Encode(Json.D("recordDirectory","E:/Клипы")),"смена папки — с прямыми косыми");

   // Повтор
   answer=(r,d)=>{throw new InvalidOperationException("Replay buffer not active (604)");};
   Check(Make().ReplayActive().Result==null,"буфер выключен в настройках OBS → null");
   answer=(r,d)=>Json.D("outputActive",true);
   Check(Make().ReplayActive().Result==true,"буфер идёт → true");
   bool active=true;
   answer=(r,d)=>{if(r=="StopReplayBuffer")active=false;if(r=="StartReplayBuffer")active=true;return Json.D("outputActive",active);};
   e=Make();e.RestartReplay().Wait();
   Check(sent.SequenceEqual(new[]{"StopReplayBuffer","GetReplayBufferStatus","StartReplayBuffer","GetReplayBufferStatus"}),"перезапуск: стоп → ждать → старт → ждать ("+string.Join(", ",sent)+")");
   int tries=0;active=false;
   answer=(r,d)=>{if(r=="StartReplayBuffer"&&++tries<3)throw new InvalidOperationException("Output not ready (604)");if(r=="StartReplayBuffer")active=true;return Json.D("outputActive",active);};
   e=Make();e.StartReplayWhenReady().Wait();Check(tries==3&&active,"старт после смены профиля: ответ 604 повторяется, третья попытка успешна");
   answer=(r,d)=>{throw new InvalidOperationException("Нет прав (500)");};
   Fails(()=>Make().StartReplayWhenReady(),"другая ошибка старта не повторяется");
   foreach(string mode in new[]{"Simple","Advanced"}){
    answer=(r,d)=>Json.Str(d,"parameterName")=="Mode"?Json.D("parameterValue",mode):Json.D("parameterValue","120");
    e=Make();int sec=e.ReplayDuration().Result;
    Check(sec==120&&sent[1].Contains(mode=="Simple"?"SimpleOutput":"AdvOut"),"длительность повтора в режиме "+mode+": "+sent[1]);
    e=Make();e.SetReplayDuration(300).Wait();Check(sent[1].Contains("RecRBTime")&&sent[1].Contains("\"300\""),"запись длительности: "+sent[1]);}
   answer=(r,d)=>Json.D("parameterValue","");Check(Make().ReplayDuration().Result==0,"длительность неизвестна → 0");

   // Звук
   answer=(r,d)=>Json.D();
   e=Make();e.SetVolumeDb("Микрофон",double.NegativeInfinity).Wait();Check(sent[0].Contains("\"inputVolumeMul\":0")&&!sent[0].Contains("inputVolumeDb"),"тишина — множитель 0");
   e=Make();e.SetVolumeDb("Микрофон",-6.5).Wait();Check(sent[0].Contains("\"inputVolumeDb\":-6.5"),"громкость в дБ: "+sent[0]);
   Check(Make().VolumeDb("Игра").Result==-100,"нет значения в дБ → −100");
   answer=(r,d)=>Json.D("inputVolumeDb",-3.25,"inputMuted",true,"inputAudioSyncOffset",120,"mic1","Микрофон");
   e=Make();Check(e.VolumeDb("Игра").Result==-3.25&&e.IsMuted("Игра").Result&&e.SyncOffset("Игра").Result==120&&e.DefaultMic().Result=="Микрофон","громкость, выключен, задержка, микрофон по умолчанию");
   Check(e.ToggleMute("Микрофон").Result,"переключение микрофона возвращает «выключен»");

   // Снимок
   answer=(r,d)=>Json.D();
   e=Make();e.SaveScreenshot("Игра",@"C:\s.jpg",true,80).Wait();Check(sent[0].Contains("\"imageFormat\":\"jpg\"")&&sent[0].Contains("\"imageCompressionQuality\":80"),"JPEG с качеством");
   e=Make();e.SaveScreenshot("Игра",@"C:\s.png",false,80).Wait();Check(sent[0].Contains("\"png\"")&&!sent[0].Contains("Quality"),"PNG без качества");

   // События
   e=Make();var log=new List<string>();
   e.RecordChanged+=(a,p,f)=>log.Add("record "+a+" "+p+" "+f);e.ReplayChanged+=(a,p)=>log.Add("replay "+a+" "+p);e.ReplaySaved+=f=>log.Add("saved "+f);
   e.MuteChanged+=(n,m)=>log.Add("mute "+n+" "+m);e.Exiting+=()=>log.Add("exit");e.Event+=(t,d)=>log.Add("raw "+t);
   e.Deliver("RecordStateChanged",Json.D("outputActive",false,"outputState","OBS_WEBSOCKET_OUTPUT_STOPPED","outputPath","D:/a.mkv"));
   e.Deliver("RecordStateChanged",Json.D("outputActive",true,"outputState","OBS_WEBSOCKET_OUTPUT_STARTING"));
   e.Deliver("ReplayBufferStateChanged",Json.D("outputActive",true,"outputState","OBS_WEBSOCKET_OUTPUT_STARTED"));
   e.Deliver("ReplayBufferSaved",Json.D("savedReplayPath","D:/Replay 1.mkv"));
   e.Deliver("InputMuteStateChanged",Json.D("inputName","Микрофон","inputMuted",true));
   e.Deliver("ExitStarted",Json.D());
   e.Deliver("CurrentProgramSceneChanged",Json.D("sceneName","Игра"));
   var expect=new[]{"record False Stopped D:/a.mkv","raw RecordStateChanged","record True Other ","raw RecordStateChanged","replay True Started","raw ReplayBufferStateChanged","saved D:/Replay 1.mkv","raw ReplayBufferSaved","mute Микрофон True","raw InputMuteStateChanged","exit","raw ExitStarted","raw CurrentProgramSceneChanged"};
   Check(log.SequenceEqual(expect),"события OBS → события движка, затем как есть"+(log.SequenceEqual(expect)?"":": "+string.Join(" | ",log)));

   // Процесс
   Check(e.IsProcess("obs64")&&e.IsProcess("OBS64")&&e.IsProcess("obs32")&&!e.IsProcess("ReplayOverlay"),"процессы OBS узнаются по имени");
   Check(e.Name=="OBS"&&e.Connected,"имя движка и подключение (поддельное)");
  }
  catch(Exception ex){Check(false,"исключение: "+ex);}
  Console.WriteLine(failures==0?"ИТОГ: все проверки пройдены":"ИТОГ: провалов "+failures);
  return failures==0?0:1;
 }
}
