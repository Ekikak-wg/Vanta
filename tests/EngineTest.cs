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

   // Своя копия: подготовка файлов перед запуском
   string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ReplayEngine-"+Guid.NewGuid().ToString("N").Substring(0,8));
   try{
    var home=EngineHome.OwnCopy(root);
    Check(home.Own&&home.ConfigRoot==System.IO.Path.Combine(root,@"config\obs-studio")&&home.WebSocketConfig.EndsWith(@"plugin_config\obs-websocket\config.json"),"пути своей копии");
    Check(home.Running().Length==0,"своя копия не запущена (процесс OBS пользователя не считается своим)");
    Check(!EngineHome.UserObs().Own&&EngineHome.UserObs().WebSocketConfig==null,"OBS пользователя — без своих путей");
    string folder=System.IO.Path.Combine(root,"Видео");
    var first=EngineSetup.Prepare(home,folder);
    string cfg=home.ConfigRoot,user=System.IO.Path.Combine(cfg,"user.ini"),ini=System.IO.Path.Combine(cfg,@"basic\profiles\Replay\basic.ini");
    Check(System.IO.File.Exists(System.IO.Path.Combine(root,"portable_mode.txt"))&&System.IO.File.Exists(System.IO.Path.Combine(root,"disable_updater.txt")),"портативный режим и запрет обновлений");
    Check(EngineSetup.IniGet(user,"General","FirstRun")=="true"&&EngineSetup.IniGet(user,"BasicWindow","SysTrayWhenStarted")=="true"&&EngineSetup.IniGet(user,"Basic","Profile")=="Replay","user.ini: без мастера, в трей, профиль Replay");
    Check(EngineSetup.IniGet(System.IO.Path.Combine(cfg,"global.ini"),"General","EnableAutoUpdates")=="false","global.ini: обновления выключены");
    var ws=Json.Decode(System.IO.File.ReadAllText(home.WebSocketConfig));
    Check(Json.Bool(ws,"server_enabled")&&Json.Bool(ws,"auth_required")&&!Json.Bool(ws,"first_load")&&Json.Str(ws,"server_password").Length==20&&Json.Int(ws,"server_port")>=4466,"WebSocket своей копии: включён, пароль 20 символов, порт "+Json.Int(ws,"server_port"));
    Check(EngineSetup.IniGet(ini,"Output","Mode")=="Advanced"&&EngineSetup.IniGet(ini,"AdvOut","RecRB")=="true"&&EngineSetup.IniGet(ini,"AdvOut","RecFilePath")==folder.Replace('\\','/')&&System.IO.Directory.Exists(folder),"профиль: расширенный вывод, буфер повтора, папка записей");
    Check(EncoderCatalog.Describe(EngineSetup.IniGet(ini,"AdvOut","RecEncoder"))!=null&&System.IO.File.Exists(System.IO.Path.Combine(cfg,@"basic\profiles\Replay\recordEncoder.json")),"кодировщик профиля: "+EngineSetup.IniGet(ini,"AdvOut","RecEncoder")+" ("+string.Join("; ",first)+")");
    // Повторная подготовка: то, что меню поменяло потом, остаётся; обязательные ключи возвращаются.
    EngineSetup.IniSet(ini,"AdvOut","RecEncoder","obs_nvenc_hevc_tex");EngineSetup.IniSet(user,"BasicWindow","SysTrayWhenStarted","false");EngineSetup.IniSet(user,"BasicWindow","geometry","AdnQyw==");
    string pass=Json.Str(ws,"server_password");
    Check(EngineSetup.Prepare(home,folder).Count==0,"повторная подготовка ничего не создаёт заново");
    Check(EngineSetup.IniGet(ini,"AdvOut","RecEncoder")=="obs_nvenc_hevc_tex"&&Json.Str(Json.Decode(System.IO.File.ReadAllText(home.WebSocketConfig)),"server_password")==pass,"кодировщик и пароль не перезаписаны");
    Check(EngineSetup.IniGet(user,"BasicWindow","SysTrayWhenStarted")=="true"&&EngineSetup.IniGet(user,"BasicWindow","geometry")=="AdnQyw==","окно снова прячется; чужие ключи раздела на месте");
    // Журнал прошлого запуска: NVENC не нашёлся, есть AMF и процессор → при подготовке выбирается AMF того же кодека.
    EngineSetup.IniSet(ini,"AdvOut","RecEncoder","obs_nvenc_h264_tex");
    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(cfg,"logs"));
    System.IO.File.WriteAllText(System.IO.Path.Combine(cfg,@"logs\2026-10-16 10-00-00.txt"),"Available Encoders:\n  Video Encoders:\n\t- h264_texture_amf (AMD HW H.264 (AVC))\n\t- obs_x264 (x264)\n  Audio Encoders:\n\t- ffmpeg_aac (FFmpeg AAC)\n");
    var fixLog=EngineSetup.Prepare(home,folder);
    Check(EngineSetup.IniGet(ini,"AdvOut","RecEncoder")=="h264_texture_amf"&&fixLog.Count==1,"нет NVENC в журнале — выбран AMF ("+string.Join("; ",fixLog)+")");
    Check(EngineSetup.Prepare(home,folder).Count==0,"кодировщик есть в журнале — профиль не трогается");
    var raw=System.IO.File.ReadAllBytes(user);Check(raw.Length>3&&raw[0]==0xEF&&raw[1]==0xBB&&raw[2]==0xBF&&System.Text.Encoding.UTF8.GetString(raw).Count(c=>c=='\uFEFF')==1,"INI в UTF-8 с одной BOM");
   }finally{try{System.IO.Directory.Delete(root,true);}catch(Exception){}}
   Check(EngineSetup.EncoderFor(new[]{"Intel(R) UHD Graphics 770","NVIDIA GeForce RTX 4070"})=="obs_nvenc_h264_tex","Intel + NVIDIA → NVENC");
   Check(EngineSetup.EncoderFor(new[]{"AMD Radeon RX 7800 XT"})=="h264_texture_amf"&&EngineSetup.EncoderFor(new[]{"Intel(R) Arc(TM) A770 Graphics"})=="obs_qsv11_v2","AMD → AMF, Intel Arc → Quick Sync");
   Check(EngineSetup.EncoderFor(new[]{"Microsoft Basic Display Adapter"})=="obs_x264","без видеокарты — процессор");
   string mon=EngineSetup.PrimaryMonitorId();Check(mon.StartsWith(@"\\?\DISPLAY#")&&mon.EndsWith("{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"),"основной монитор: путь устройства, как в OBS");
   var col=EngineSetup.Collection(2560,1440);var srcs=((object[])col["sources"]).Select(Json.Obj).ToList();
   Check(Json.Str(col,"current_program_scene")==EngineSetup.GameScene&&srcs.Count(x=>Json.Str(x,"id")=="scene")==2&&Json.Str(Json.Obj(Json.Obj(col["AuxAudioDevice1"])["settings"]),"device_id")=="default","коллекция: две сцены, микрофон по умолчанию");
   var gs=srcs.First(x=>Json.Str(x,"id")=="game_capture");Check(!Json.Bool(Json.Obj(gs["settings"]),"capture_audio"),"захват игры без своего звука (нет двойного звука)");
   var item=Json.Obj(((object[])Json.Obj(srcs.First(x=>Json.Str(x,"name")==EngineSetup.GameScene)["settings"])["items"])[0]);
   Check(Json.Str(item,"source_uuid")==Json.Str(gs,"uuid")&&Json.Int(item,"bounds_type")==2&&Convert.ToDouble(Json.Obj(item["bounds"])["x"])==2560,"игра вписана в кадр 2560×1440");
   var screenItems=((object[])Json.Obj(srcs.First(x=>Json.Str(x,"name")==EngineSetup.ScreenScene)["settings"])["items"]).Select(Json.Obj).ToList();
   Check(screenItems.Count==2&&Json.Str(screenItems[0],"name")==EngineSetup.GameSource&&Json.Str(screenItems[1],"name")==EngineSetup.ScreenSource&&Json.Int(screenItems[1],"id")==2,"«Игра и экран»: игра снизу, экран сверху");
  }
  catch(Exception ex){Check(false,"исключение: "+ex);}
  Console.WriteLine(failures==0?"ИТОГ: все проверки пройдены":"ИТОГ: провалов "+failures);
  return failures==0?0:1;
 }
}
