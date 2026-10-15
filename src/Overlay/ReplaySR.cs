using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ReplayOverlay {
 // Отдельный мгновенный повтор через плагин Source Record: свои разрешение, частота кадров, кодек, качество, формат и звук.
 // Служебная сцена «Replay · повтор» вкладывает текущую сцену OBS (меню переставляет вложение при смене сцены),
 // на ней — фильтр Source Record в режиме буфера повтора. Обычный буфер OBS в это время выключен.
 public sealed partial class Overlay {
  public const string SrScene="Replay · повтор",SrFilter="Replay · повтор";
  bool srOn,srAvailable,srActive,srSwitching;
  string srEncoder="",srQuality="recommended",srScale="same",srFormat="mkv",srAudio="track1";int srFpsDiv=1;
  bool ReplayOn{get{return srOn?srActive:replay;}}

  void BindSR(){
   var sw=C<ToggleButton>("SrSwitch");sw.Click+=(s,e)=>{sw.IsChecked=srOn;Run(srOn?(Func<Task>)DisableSR:EnableSR);};
   C<Button>("SrApply").Click+=(s,e)=>Run(()=>ApplySR(true));
   SelectTag(C<ComboBox>("SrFpsBox"),srFpsDiv.ToString());SelectTag(C<ComboBox>("SrQualityBox"),srQuality);SelectTag(C<ComboBox>("SrFormatBox"),srFormat);SelectTag(C<ComboBox>("SrAudioBox"),srAudio);
   UpdateSrUi();}

  // Есть ли плагин, включён ли отдельный повтор и идёт ли он.
  async Task RefreshSR(){
   try{object raw;(await obs.Call("GetSourceFilterKindList")).TryGetValue("sourceFilterKinds",out raw);srAvailable=((raw as object[])??new object[0]).Any(k=>Convert.ToString(k)=="source_record_filter");}catch(Exception){srAvailable=false;}
   if(srOn){
    try{var f=await obs.Call("GetSourceFilter",Json.D("sourceName",SrScene,"filterName",SrFilter));srActive=Json.Bool(Json.Obj(f["filterSettings"]),"replay_buffer");}
    catch(InvalidOperationException){srActive=false;Log.Write("Отдельный повтор: служебной сцены нет — включите его заново");}
    // Обычный буфер OBS при отдельном повторе не нужен — он только нагружал бы видеокарту.
    if(replay){srSwitching=true;try{await obs.Call("StopReplayBuffer");await WaitBuffer(false);Log.Write("Обычный буфер OBS выключен: работает отдельный повтор");}catch(Exception){}finally{srSwitching=false;}}
    await UpdateReplayNest();}
   var cb=C<ComboBox>("SrCodecBox");var keep=TagOf(cb,srEncoder);bool was=loading;loading=true;
   try{cb.Items.Clear();cb.Items.Add(new ComboBoxItem{Content="Как у записи",Tag=""});foreach(var e in encoders)cb.Items.Add(new ComboBoxItem{Content=e.VendorLabel+" · "+e.CodecLabel,Tag=e.Id});SelectTag(cb,srEncoder);if(cb.SelectedItem==null)cb.SelectedIndex=0;}finally{loading=was;}
   UpdateSrUi();UpdateControls();}

  void UpdateSrUi(){
   C<ToggleButton>("SrSwitch").IsChecked=srOn;C<ToggleButton>("SrSwitch").IsEnabled=srAvailable||srOn;
   C<StackPanel>("SrPanel").Visibility=srOn?Visibility.Visible:Visibility.Collapsed;
   C<TextBlock>("SrHint").Text=!srAvailable&&!srOn?"Нужен плагин Source Record для OBS — без него повтор пишется с теми же качеством и дорожками, что и запись (вкладка «Запись»).":srOn?"Повтор пишется отдельно от записи — со своими настройками ниже. Обычный буфер OBS выключен, чтобы не кодировать дважды.":"Сейчас повтор пишется с теми же качеством и дорожками, что и запись. Включите, чтобы задать повтору свои.";}

  // Настройки фильтра Source Record: кодировщик и качество, масштаб, частота кадров, папка, имя, формат, звук.
  Dictionary<string,object> SrSettings(bool active){
   string encId=srEncoder.Length>0?srEncoder:recEncoderId;var enc=encoders.FirstOrDefault(x=>x.Id==encId)??EncoderCatalog.Describe(encId);
   if(enc==null)throw new InvalidOperationException("Не удалось определить кодировщик для повтора");
   var s=EncoderSettings.Build(enc,new QualityChoice{Mode=srQuality},null);
   s["encoder"]=enc.Id;s["record_mode"]=0;s["stream_mode"]=0;s["replay_buffer"]=active;s["replay_duration"]=duration;
   string dir=(replayDir.Length>0?replayDir:ClipsDir);if(dir.Length==0)throw new InvalidOperationException("Папка для повторов неизвестна");
   s["path"]=dir.Replace('\\','/');s["replay_filename_formatting"]=(replayPrefix.Length>0?replayPrefix+" ":"")+filenameFormat;s["rec_format"]=srFormat;
   // Масштаб не задаём: на OBS 32 плагин объявляет его кодировщику, но кадры остаются в размере сцены (проверено 15.10), а кодирование уходит на процессор.
   s["scale"]=false;
   s["frame_rate_divisor"]=srFpsDiv;
   s["different_audio"]=true;s["audio_track"]=srAudio=="all"?-1:1;s["audio_encoder"]="ffmpeg_aac";s["audio_bitrate"]=192;
   return s;}
  void ReadSrForm(){srEncoder=TagOf(C<ComboBox>("SrCodecBox"),"");srQuality=TagOf(C<ComboBox>("SrQualityBox"),"recommended");srScale="same";
   int d;srFpsDiv=int.TryParse(TagOf(C<ComboBox>("SrFpsBox"),"1"),out d)?d:1;srFormat=TagOf(C<ComboBox>("SrFormatBox"),"mkv");srAudio=TagOf(C<ComboBox>("SrAudioBox"),"track1");}

  async Task EnableSR(){
   EnsureReady();if(!srAvailable)throw new InvalidOperationException("Плагин Source Record не найден в OBS");ReadSrForm();
   object raw;(await obs.Call("GetSceneList")).TryGetValue("scenes",out raw);
   if(!((raw as object[])??new object[0]).Any(x=>Json.Str(Json.Obj(x),"sceneName")==SrScene))await obs.Call("CreateScene",Json.D("sceneName",SrScene));
   srOn=true;await UpdateReplayNest();
   bool exists=true;try{await obs.Call("GetSourceFilter",Json.D("sourceName",SrScene,"filterName",SrFilter));}catch(InvalidOperationException){exists=false;}
   if(exists)await obs.Call("SetSourceFilterSettings",Json.D("sourceName",SrScene,"filterName",SrFilter,"filterSettings",SrSettings(true),"overlay",false));
   else await obs.Call("CreateSourceFilter",Json.D("sourceName",SrScene,"filterName",SrFilter,"filterKind","source_record_filter","filterSettings",SrSettings(true)));
   srActive=true;SavePrefs();Log.Write("Отдельный повтор включён (Source Record)");
   await RefreshSR();Message("Отдельный повтор включён: свои качество и звук");}

  async Task DisableSR(){
   EnsureReady();srOn=false;srActive=false;SavePrefs();
   try{await obs.Call("RemoveScene",Json.D("sceneName",SrScene));}catch(InvalidOperationException){}
   sceneItems.Clear();Log.Write("Отдельный повтор выключен — снова обычный буфер OBS");
   if(replayWanted&&replayAvailable&&!replay){try{await obs.Call("StartReplayBuffer");}catch(Exception ex){Log.Write("Буфер OBS не включился: "+ex.Message);}}
   UpdateSrUi();await Refresh();Message("Повтор снова пишется с настройками записи");}

  // Применить: буфер плагина перезапускается — накопленная история сбрасывается.
  async Task ApplySR(bool fromForm){
   EnsureReady();if(!srOn)return;if(fromForm)ReadSrForm();
   // Масштаб и частоту кадров плагин задаёт только новому кодировщику — фильтр пересоздаётся.
   var settings=SrSettings(srActive||fromForm);
   try{await obs.Call("RemoveSourceFilter",Json.D("sourceName",SrScene,"filterName",SrFilter));}catch(InvalidOperationException){}
   await Task.Delay(700);
   await obs.Call("CreateSourceFilter",Json.D("sourceName",SrScene,"filterName",SrFilter,"filterKind","source_record_filter","filterSettings",settings));
   if(fromForm)srActive=true;SavePrefs();Log.Write("Отдельный повтор: "+(srEncoder.Length>0?srEncoder:"кодировщик записи")+" · "+srQuality+" · "+srScale+" · делитель FPS "+srFpsDiv+" · "+srFormat+" · звук "+srAudio);
   if(fromForm)Message("Настройки повтора применены · буфер запущен заново");}

  // Служебная сцена вкладывает ту сцену, что сейчас идёт в эфир или запись.
  async Task UpdateReplayNest(){
   if(!srOn||string.IsNullOrEmpty(currentScene)||currentScene==SrScene||currentScene==StrictPrivacy.CoverScene)return;
   try{object raw;(await obs.Call("GetSceneItemList",Json.D("sceneName",SrScene))).TryGetValue("sceneItems",out raw);var items=((raw as object[])??new object[0]).Select(Json.Obj).ToList();
    if(items.Any(i=>Json.Str(i,"sourceName")==currentScene)&&items.Count==1)return;
    await obs.Call("CreateSceneItem",Json.D("sceneName",SrScene,"sourceName",currentScene));
    foreach(var i in items.Where(i=>Json.Str(i,"sourceName")!=currentScene))await obs.Call("RemoveSceneItem",Json.D("sceneName",SrScene,"sceneItemId",Json.Int(i,"sceneItemId")));}
   catch(Exception ex){Log.Write("Отдельный повтор: сцена не переключена — "+ex.Message);}}

  // Вкл/выкл и сохранение — для обычного буфера OBS или для отдельного повтора.
  async Task ToggleReplay(){
   EnsureReady();
   if(!srOn){await obs.Call("ToggleReplayBuffer");await Refresh();return;}
   bool on=!srActive;var r=await obs.Call("CallVendorRequest",Json.D("vendorName","source-record","requestType",on?"replay_buffer_start":"replay_buffer_stop","requestData",Json.D("source",SrScene,"filter",SrFilter)));
   if(!Json.Bool(Json.Obj(r.ContainsKey("responseData")?r["responseData"]:null),"success"))throw new InvalidOperationException("Source Record не "+(on?"включил":"выключил")+" повтор");
   srActive=on;replayWanted=on;SavePrefs();Log.Write("Отдельный повтор "+(on?"включён":"выключен"));UpdateControls();UpdateIndicator();}
  async Task SaveSR(){
   if(!srActive)throw new InvalidOperationException("Сначала включите мгновенный повтор");
   var r=await obs.Call("CallVendorRequest",Json.D("vendorName","source-record","requestType","replay_buffer_save","requestData",Json.D("source",SrScene,"filter",SrFilter)));
   if(!Json.Bool(Json.Obj(r.ContainsKey("responseData")?r["responseData"]:null),"success"))throw new InvalidOperationException("Source Record не сохранил повтор");
   Message("Сохраняю повтор…");
   // Source Record 0.4.8 не присылает событие о сохранении — ждём сам файл: новый, с приставкой повторов, размер перестал расти.
   var since=DateTime.Now.AddSeconds(-2);string dir=replayDir.Length>0?replayDir:ClipsDir;string found=null;long lastSize=-1;
   for(int i=0;i<70&&found==null;i++){await Task.Delay(300);
    try{var f=new System.IO.DirectoryInfo(dir).GetFiles((replayPrefix.Length>0?replayPrefix:"")+"*").Where(x=>ClipLibrary.IsVideo(x.Name)&&x.LastWriteTime>=since).OrderByDescending(x=>x.LastWriteTime).FirstOrDefault();
     if(f==null)continue;f.Refresh();if(f.Length>0&&f.Length==lastSize&&!ClipLibrary.InUse(f.FullName))found=f.FullName;lastSize=f.Length;}catch(Exception){}}
   if(found==null){Warn("sr-save","Повтор не появился в папке «"+dir+"» — проверьте плагин Source Record и место на диске");return;}
   OnSrSaved(found);}
  string srLastHandled;
  void OnSrSaved(string path){
   if(string.Equals(path,srLastHandled,StringComparison.OrdinalIgnoreCase))return;srLastHandled=path;
   lastSaved=AfterReplaySaved(path);string name=System.IO.Path.GetFileName(lastSaved);
   Log.Write("Повтор сохранён (отдельный): "+name);Message("Сохранено: "+name);Notify("Сохранён момент из повтора",name,ToastKind.Ok);ClipsChanged();}
  // Если версия плагина присылает событие «повтор сохранён» — учитываем и его (без двойного уведомления).
  void OnVendorEvent(Dictionary<string,object> d){
   if(Json.Str(d,"vendorName")!="source-record"||Json.Str(d,"eventType")!="replay_buffer_saved")return;
   var e=Json.Obj(d.ContainsKey("eventData")?d["eventData"]:null);string path=Json.Str(e,"path").Replace('/','\\');if(path.Length>0)OnSrSaved(path);}
 }
}
