using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Параметры записи (разрешение, кодировщик, кодек, качество, формат, имя) и раздел «Сохранение».
 public sealed partial class Overlay {
  List<EncoderInfo> encoders=new List<EncoderInfo>();string recEncoderId="";Dictionary<string,object> recEncoderSettings=new Dictionary<string,object>();
  int baseW=1920,baseH=1080;string recFormat="mkv",filenameFormat="%CCYY-%MM-%DD_%hh-%mm-%ss",replayPrefix="Replay";
  string replayDir="";readonly ManagedFiles managed=new ManagedFiles();bool cleanupOn;int cleanupDays=30,cleanupGb=0,lowDiskGb=5;
  readonly DispatcherTimer cleanupTimer=new DispatcherTimer{Interval=TimeSpan.FromHours(1)};
  const string TempProfile="Replay · служебный";

  void BindVideo(){
   C<Button>("NavVideo").Click+=(s,e)=>ScrollToSection("VideoSection");C<Button>("NavStorage").Click+=(s,e)=>ScrollToSection("StorageSection");
   C<Button>("VideoOpenRecord").Click+=(s,e)=>ShowPage("record");
   C<ComboBox>("ResolutionBox").SelectionChanged+=(s,e)=>{var it=C<ComboBox>("ResolutionBox").SelectedItem as ComboBoxItem;C<StackPanel>("CustomResPanel").Visibility=it!=null&&Convert.ToString(it.Tag)=="custom"?Visibility.Visible:Visibility.Collapsed;};
   C<ComboBox>("EncoderBox").SelectionChanged+=(s,e)=>{if(!loading)FillCodecs(null);};
   C<ComboBox>("QualityBox").SelectionChanged+=(s,e)=>UpdateQualityUi();C<ComboBox>("RateControlBox").SelectionChanged+=(s,e)=>UpdateQualityUi();
   C<Button>("StoreRecPick").Click+=(s,e)=>Run(PickFolder);
   C<Button>("StoreReplayPick").Click+=(s,e)=>PickReplayFolder();
   C<Button>("StoreReplayReset").Click+=(s,e)=>{replayDir="";SavePrefs();UpdateStorageUi();Message("Повторы сохраняются в папку записей");};
   C<Button>("StoreShotPick").Click+=(s,e)=>{PickShotFolder();UpdateStorageUi();};
   C<Button>("StoreOpen").Click+=(s,e)=>Run(OpenClipsFolder);
   C<Button>("ReplayPrefixApply").Click+=(s,e)=>Run(ApplyReplayPrefix);
   Action<TextBox,Action<int>,int,int> num=(box,set,min,max)=>{Action commit=()=>{int v;if(int.TryParse(box.Text.Trim(),out v)&&v>=min&&v<=max){set(v);SavePrefs();UpdateStorageUi();}else{Message("Введите целое число от "+min+" до "+max);UpdateStorageUi();}};box.LostFocus+=(s,e)=>commit();box.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){commit();e.Handled=true;}};};
   num(C<TextBox>("LowDiskBox"),v=>lowDiskGb=v,1,1000);num(C<TextBox>("CleanupDaysBox"),v=>cleanupDays=v,0,3650);num(C<TextBox>("CleanupGbBox"),v=>cleanupGb=v,0,100000);
   var cb=C<CheckBox>("CleanupBox");cb.IsChecked=cleanupOn;cb.Click+=(s,e)=>{cleanupOn=cb.IsChecked==true;SavePrefs();UpdateStorageUi();if(cleanupOn)RunCleanup(false);};
   C<Button>("CleanupNowButton").Click+=(s,e)=>RunCleanup(true);
   cleanupTimer.Tick+=(s,e)=>RunCleanup(false);cleanupTimer.Start();
   UpdateStorageUi();}

  // ——— Чтение текущих параметров из OBS ———
  async Task RefreshVideo(){
   encoders=await Task.Run(()=>EncoderCatalog.FromLatestLog());
   encoderPath=Path.Combine(ObsConfig.ProfileDirectory(profileName),"recordEncoder.json");
   recEncoderId=outputMode=="Advanced"?Json.Str(await engine.Call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecEncoder")),"parameterValue"):"";
   var cur=EncoderCatalog.Describe(recEncoderId);
   encoderEditable=outputMode=="Advanced"&&cur!=null&&encoders.Count>0;
   try{recEncoderSettings=File.Exists(encoderPath)?Json.Decode(File.ReadAllText(encoderPath)):new Dictionary<string,object>();}catch(Exception){recEncoderSettings=new Dictionary<string,object>();}
   var v=await engine.Call("GetVideoSettings");baseW=Json.Int(v,"baseWidth",1920);baseH=Json.Int(v,"baseHeight",1080);
   recFormat=await Param(OutputSection,"RecFormat2","mkv");filenameFormat=await Param("Output","FilenameFormatting","%CCYY-%MM-%DD_%hh-%mm-%ss");replayPrefix=await Param(OutputSection,"RecRBPrefix","Replay");
   bool was=loading;loading=true;
   try{FillResolution(Json.Int(v,"outputWidth",baseW),Json.Int(v,"outputHeight",baseH));
    var eb=C<ComboBox>("EncoderBox");eb.Items.Clear();
    foreach(var vendor in encoders.Select(x=>x.Vendor).Distinct()){var any=encoders.First(x=>x.Vendor==vendor);eb.Items.Add(new ComboBoxItem{Content=any.VendorLabel,Tag=vendor});}
    if(cur!=null&&!encoders.Any(x=>x.Vendor==cur.Vendor))eb.Items.Add(new ComboBoxItem{Content=cur.VendorLabel,Tag=cur.Vendor});
    SelectTag(eb,cur!=null?cur.Vendor:"");if(eb.SelectedItem==null&&eb.Items.Count>0)eb.SelectedIndex=0;
    FillCodecs(cur!=null?cur.Codec:null);
    var q=EncoderSettings.Read(cur,recEncoderSettings);SelectTag(C<ComboBox>("QualityBox"),q.Mode);SelectTag(C<ComboBox>("RateControlBox"),q.RateControl);C<TextBox>("BitrateBox").Text=q.Value.ToString();
    SelectTag(C<ComboBox>("FormatBox"),recFormat);C<TextBox>("FilenameBox").Text=filenameFormat;C<TextBox>("ReplayPrefixBox").Text=replayPrefix;}
   finally{loading=was;}
   foreach(string n in new[]{"EncoderBox","CodecBox","QualityBox","RateControlBox"})C<FrameworkElement>(n).IsEnabled=encoderEditable;
   C<TextBlock>("EncoderHint").Visibility=encoderEditable?Visibility.Collapsed:Visibility.Visible;
   C<TextBlock>("EncodersInfo").Text=encoders.Count==0?"Не удалось прочитать список кодировщиков":string.Join("\n",encoders.Select(x=>x.Vendor).Distinct().Select(vd=>encoders.First(x=>x.Vendor==vd).VendorLabel+": "+string.Join(", ",encoders.Where(x=>x.Vendor==vd).Select(x=>x.CodecLabel))));
   ClipReplayPrefix=replayPrefix;UpdateQualityUi();UpdateStorageUi();}
  async Task<string> Param(string category,string name,string fallback){try{string v=Json.Str(await engine.Call("GetProfileParameter",Json.D("parameterCategory",category,"parameterName",name)),"parameterValue");return v.Length>0?v:fallback;}catch(InvalidOperationException){return fallback;}}

  // Разрешения: как экран и стандартные меньше него (с теми же пропорциями) + своё.
  void FillResolution(int w,int h){
   var box=C<ComboBox>("ResolutionBox");box.Items.Clear();
   box.Items.Add(new ComboBoxItem{Content="Как экран — "+baseW+" × "+baseH,Tag=baseW+","+baseH});
   foreach(int sh in new[]{2160,1440,1080,900,720})if(sh<baseH){int sw=(int)Math.Round(baseW*(double)sh/baseH/2)*2;box.Items.Add(new ComboBoxItem{Content=sw+" × "+sh,Tag=sw+","+sh});}
   box.Items.Add(new ComboBoxItem{Content="Своё…",Tag="custom"});
   SelectTag(box,w+","+h);if(box.SelectedItem==null){SelectTag(box,"custom");C<TextBox>("CustomResBox").Text=w+"x"+h;}}
  void FillCodecs(string keep){
   var eb=C<ComboBox>("EncoderBox").SelectedItem as ComboBoxItem;string vendor=eb==null?"":Convert.ToString(eb.Tag);
   var cbx=C<ComboBox>("CodecBox");var old=cbx.SelectedItem as ComboBoxItem;if(keep==null&&old!=null)keep=Convert.ToString(old.Tag);cbx.Items.Clear();
   foreach(var e in encoders.Where(x=>x.Vendor==vendor))cbx.Items.Add(new ComboBoxItem{Content=e.CodecLabel,Tag=e.Codec,ToolTip=e.Id});
   var cur=EncoderCatalog.Describe(recEncoderId);if(cur!=null&&cur.Vendor==vendor&&!encoders.Any(x=>x.Id==cur.Id||(x.Vendor==cur.Vendor&&x.Codec==cur.Codec)))cbx.Items.Add(new ComboBoxItem{Content=cur.CodecLabel,Tag=cur.Codec});
   SelectTag(cbx,keep??"h264");if(cbx.SelectedItem==null&&cbx.Items.Count>0)cbx.SelectedIndex=0;}
  EncoderInfo SelectedEncoder(){var v=C<ComboBox>("EncoderBox").SelectedItem as ComboBoxItem;var c=C<ComboBox>("CodecBox").SelectedItem as ComboBoxItem;if(v==null||c==null)return null;string vendor=Convert.ToString(v.Tag),codec=Convert.ToString(c.Tag);
   return encoders.FirstOrDefault(x=>x.Vendor==vendor&&x.Codec==codec)??(EncoderCatalog.Describe(recEncoderId)!=null&&EncoderCatalog.Describe(recEncoderId).Vendor==vendor&&EncoderCatalog.Describe(recEncoderId).Codec==codec?EncoderCatalog.Describe(recEncoderId):null);}
  static string TagOf(ComboBox b,string fallback){var it=b.SelectedItem as ComboBoxItem;return it==null?fallback:Convert.ToString(it.Tag);}
  void UpdateQualityUi(){
   string mode=TagOf(C<ComboBox>("QualityBox"),"recommended"),rc=TagOf(C<ComboBox>("RateControlBox"),"VBR");
   C<Grid>("CustomQualityPanel").Visibility=mode=="custom"?Visibility.Visible:Visibility.Collapsed;
   C<TextBlock>("BitrateLabel").Text=rc=="CQP"?"Уровень CQP (меньше — лучше)":"Битрейт, Мбит/с";
   C<TextBlock>("QualityHint").Text=mode=="recommended"?"VBR 20 Мбит/с — хороший баланс качества и размера (около 9 ГБ в час).":mode=="high"?"VBR 40 Мбит/с, медленнее пресет — заметно чётче в динамичных сценах (около 18 ГБ в час).":mode=="max"?"Постоянное качество (CQP 18, для AV1 — 24): почти без потерь, файлы большие.":"Свой режим: VBR и CBR — по битрейту, CQP — по уровню качества.";}
  QualityChoice ReadQuality(){var q=new QualityChoice{Mode=TagOf(C<ComboBox>("QualityBox"),"recommended"),RateControl=TagOf(C<ComboBox>("RateControlBox"),"VBR")};
   if(q.Mode=="custom"){int v;if(!int.TryParse(C<TextBox>("BitrateBox").Text.Trim(),out v))throw new InvalidOperationException(q.RateControl=="CQP"?"Уровень CQP — целое число":"Битрейт — целое число Мбит/с");q.Value=v;string err=EncoderSettings.Check(q);if(err!=null)throw new InvalidOperationException(err);}
   return q;}
  int[] ReadResolution(){string tag=TagOf(C<ComboBox>("ResolutionBox"),baseW+","+baseH);string text=tag=="custom"?C<TextBox>("CustomResBox").Text.Trim().ToLowerInvariant().Replace("×","x").Replace(" ",""):tag.Replace(",","x");
   var p=text.Split('x');int w,h;if(p.Length!=2||!int.TryParse(p[0],out w)||!int.TryParse(p[1],out h)||w<320||h<240||w>7680||h>4320)throw new InvalidOperationException("Разрешение — ширина x высота, например 2560x1440");
   if(w%2!=0||h%2!=0)throw new InvalidOperationException("Ширина и высота должны быть чётными");return new[]{w,h};}
  static string CheckFilename(string f){if(f.Length==0)return "Введите шаблон имени";if(f.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return "В шаблоне имени недопустимый символ";return null;}

  // ——— Применение: запись, повтор и трансляция должны быть остановлены; при ошибке всё возвращается ———
  async Task Apply(){
   EnsureReady();var res=ReadResolution();int fps=int.Parse(Convert.ToString(((ComboBoxItem)C<ComboBox>("FpsBox").SelectedItem).Content));
   EncoderInfo enc=null;QualityChoice quality=null;if(encoderEditable){enc=SelectedEncoder();if(enc==null)throw new InvalidOperationException("Выберите кодировщик и кодек");quality=ReadQuality();}
   string format=TagOf(C<ComboBox>("FormatBox"),recFormat);if(enc!=null&&!EncoderSettings.FormatFits(format,enc.Codec))throw new InvalidOperationException("Этот формат не поддерживает выбранный кодек");
   string fname=C<TextBox>("FilenameBox").Text.Trim();string ferr=CheckFilename(fname);if(ferr!=null)throw new InvalidOperationException(ferr);
   bool outputsBusy=await engine.OutputsBusy();
   if(outputsBusy)throw new InvalidOperationException("Сначала остановите запись");
   string section=OutputSection,startProfile=profileName;
   string oldEncoderJson=File.Exists(encoderPath)?File.ReadAllText(encoderPath):null,oldEncoderId=recEncoderId,oldFormat=recFormat,oldFilename=filenameFormat;var oldVideo=await engine.Call("GetVideoSettings");
   bool typeChanged=enc!=null&&enc.Id!=recEncoderId;bool resume=replayAvailable&&await engine.ReplayActive()==true;bool changed=false;busy=true;UpdateControls();
   Exception failure=null;
   try{
    if(resume){await engine.StopReplay();await WaitBuffer(false);}
    if(Json.Str(await engine.Call("GetProfileList"),"currentProfileName")!=startProfile)throw new InvalidOperationException("Профиль изменился; применение отменено");
    changed=true;
    if(enc!=null){AtomicWrite(encoderPath,Json.Encode(EncoderSettings.Build(enc,quality,recEncoderSettings)));if(typeChanged)await engine.Call("SetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecEncoder","parameterValue",enc.Id));}
    if(format!=recFormat)await engine.Call("SetProfileParameter",Json.D("parameterCategory",section,"parameterName","RecFormat2","parameterValue",format));
    if(fname!=filenameFormat)await engine.Call("SetProfileParameter",Json.D("parameterCategory","Output","parameterName","FilenameFormatting","parameterValue",fname));
    await engine.Call("SetVideoSettings",Json.D("outputWidth",res[0],"outputHeight",res[1],"fpsNumerator",fps,"fpsDenominator",1));
    if(typeChanged)await ReloadProfile(startProfile);
    if(resume)await StartReplayWhenReady();
    Log.Write("Параметры записи: "+res[0]+"×"+res[1]+" "+fps+" к/с"+(enc!=null?" · "+enc.Id+" · "+quality.Mode+(quality.Mode=="custom"?" "+quality.RateControl+" "+quality.Value:""):"")+" · "+format);
    Message("Настройки применены"+(typeChanged?" · кодировщик: "+enc.VendorLabel+" "+enc.CodecLabel:"")+(resume?" · буфер запущен заново":""));}
   catch(Exception ex){failure=ex;}
   try{
    if(failure!=null){
     try{
      if(!engine.Connected)throw new IOException("Движок записи отключился");
      if(Json.Str(await engine.Call("GetProfileList"),"currentProfileName")!=startProfile)await engine.Call("SetCurrentProfile",Json.D("profileName",startProfile));
      if(changed){
       if(await engine.ReplayActive()==true){await engine.StopReplay();await WaitBuffer(false);}
       if(oldEncoderJson!=null)AtomicWrite(encoderPath,oldEncoderJson);
       if(typeChanged)await engine.Call("SetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecEncoder","parameterValue",oldEncoderId));
       await engine.Call("SetProfileParameter",Json.D("parameterCategory",section,"parameterName","RecFormat2","parameterValue",oldFormat));
       await engine.Call("SetProfileParameter",Json.D("parameterCategory","Output","parameterName","FilenameFormatting","parameterValue",oldFilename));
       await engine.Call("SetVideoSettings",Json.D("outputWidth",Json.Int(oldVideo,"outputWidth"),"outputHeight",Json.Int(oldVideo,"outputHeight"),"fpsNumerator",Json.Int(oldVideo,"fpsNumerator"),"fpsDenominator",Json.Int(oldVideo,"fpsDenominator")));
       if(typeChanged)await ReloadProfile(startProfile);}
      if(resume&&await engine.ReplayActive()!=true)await StartReplayWhenReady();}
     catch(Exception rollbackError){failure=new InvalidOperationException("Откат настроек записи не завершён. "+failure.Message+" / "+rollbackError.Message);}
     Log.Write("Параметры записи не применены: "+failure.Message);throw failure;}}
   finally{busy=false;UpdateControls();}
   await Refresh();}

  // Кодировщик OBS создаёт при загрузке профиля — чтобы сменить его, профиль перезагружается через служебный и обратно.
  async Task ReloadProfile(string name){
   object raw;(await engine.Call("GetProfileList")).TryGetValue("profiles",out raw);bool exists=((raw as object[])??new object[0]).Any(p=>Convert.ToString(p)==TempProfile);
   if(exists)await engine.Call("SetCurrentProfile",Json.D("profileName",TempProfile));else await engine.Call("CreateProfile",Json.D("profileName",TempProfile));
   await WaitProfile(TempProfile);
   await engine.Call("SetCurrentProfile",Json.D("profileName",name));
   await WaitProfile(name);
   // OBS переключает профиль не мгновенно: служебный удаляем с повторами, пока он не перестанет быть «занятым».
   for(int i=0;;i++){try{await engine.Call("RemoveProfile",Json.D("profileName",TempProfile));break;}catch(InvalidOperationException ex){if(i>=5){Log.Write("Служебный профиль не удалён: "+ex.Message);break;}}await Task.Delay(400);}
   Log.Write("Профиль движка перезагружен — новый кодировщик подключён");}
  async Task WaitProfile(string name){for(int i=0;i<50;i++){if(Json.Str(await engine.Call("GetProfileList"),"currentProfileName")==name)return;await Task.Delay(100);}}
  Task StartReplayWhenReady(){return engine.StartReplayWhenReady();}

  async Task ApplyReplayPrefix(){EnsureReady();string p=C<TextBox>("ReplayPrefixBox").Text.Trim();string err=p.Length==0?null:CheckFilename(p);if(err!=null)throw new InvalidOperationException(err);
   await engine.SetReplayPrefix(p);replayPrefix=p;ClipReplayPrefix=p.Length>0?p:"Replay";
   Message("Приставка повторов сохранена — подействует при следующем включении буфера");}

  // ——— Сохранение: папки, перенос повторов, учёт файлов, автоочистка ———
  void UpdateStorageUi(){
   C<TextBlock>("StoreRecLabel").Text=directory.Length>0?directory:ClipsDir.Length>0?ClipsDir:"—";
   C<TextBlock>("StoreReplayLabel").Text=replayDir.Length>0?replayDir:"как у записей";C<Button>("StoreReplayReset").IsEnabled=replayDir.Length>0;
   C<TextBlock>("StoreShotLabel").Text=ShotDir.Length>0?ShotDir:"—";
   C<TextBox>("LowDiskBox").Text=lowDiskGb.ToString();C<TextBox>("CleanupDaysBox").Text=cleanupDays.ToString();C<TextBox>("CleanupGbBox").Text=cleanupGb.ToString();
   C<Grid>("CleanupPanel").IsEnabled=cleanupOn;
   C<TextBlock>("CleanupInfo").Text=(cleanupOn?"Очистка раз в час и после сохранений. ":"Очистка выключена. ")+"Под учётом Replay: "+Plural(managed.Paths.Count(File.Exists),"файл","файла","файлов")+". Удаляются только они — в Корзину.";}
  void PickReplayFolder(){string chosen;dialogOpen=true;
   try{using(var d=new WF.FolderBrowserDialog{Description="Куда переносить мгновенные повторы",ShowNewFolderButton=true,SelectedPath=replayDir.Length>0?replayDir:ClipsDir}){if(d.ShowDialog(new Owner(handle))!=WF.DialogResult.OK)return;chosen=d.SelectedPath;}}
   finally{dialogOpen=false;window.Activate();}
   string probe=Path.Combine(chosen,".vanta-write-test");try{File.WriteAllText(probe,"");File.Delete(probe);}catch(Exception ex){Message("В эту папку нельзя записывать: "+ex.Message);return;}
   replayDir=string.Equals(chosen.TrimEnd('\\'),ClipsDir.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)?"":chosen;SavePrefs();UpdateStorageUi();Message("Повторы: "+(replayDir.Length>0?replayDir:"в папке записей"));}
  // Повтор сохранён OBS — переносим в папку повторов (если задана) и берём на учёт.
  string AfterReplaySaved(string path){
   if(string.IsNullOrEmpty(path))return path;string result=path;
   if(replayDir.Length>0&&!string.Equals(Path.GetDirectoryName(path).TrimEnd('\\'),replayDir.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)){
    try{Directory.CreateDirectory(replayDir);string target=Path.Combine(replayDir,Path.GetFileName(path));for(int i=2;File.Exists(target);i++)target=Path.Combine(replayDir,Path.GetFileNameWithoutExtension(path)+" ("+i+")"+Path.GetExtension(path));
     for(int i=0;;i++){try{File.Move(path,target);break;}catch(IOException){if(i>=15)throw;System.Threading.Thread.Sleep(200);}}
     result=target;Log.Write("Повтор перенесён в папку повторов");}
    catch(Exception ex){Log.Write("Повтор не перенесён: "+ex.Message);Warn("replay-move","Повтор не перенесён в папку повторов: "+ex.Message);}}
   Track(result);return result;}
  // На учёт: путь и игра, которая шла в момент сохранения (для карточки галереи).
  void Track(string path){if(string.IsNullOrEmpty(path))return;path=path.Replace('/','\\');managed.Add(path);var g=gamesWatch.Current;if(g!=null)clipGames[path]=Path.GetFileNameWithoutExtension(g.FileName);SavePrefs();}
  void RunCleanup(bool manual){
   int pruned=managed.Prune();
   if(!cleanupOn&&!manual){if(pruned>0)SavePrefs();return;}
   if(!cleanupOn&&manual){Message("Включите автоочистку и задайте срок или предел");return;}
   var items=managed.Paths.Where(File.Exists).Where(p=>!ClipLibrary.InUse(p)).Select(p=>{var f=new FileInfo(p);return new Cleanup.Item{Path=p,Modified=f.LastWriteTime,Size=f.Length};});
   var plan=Cleanup.Plan(items,cleanupDays,cleanupGb,DateTime.Now);int done=0;long freed=0;
   foreach(var it in plan){try{ClipLibrary.Recycle(it.Path);managed.Remove(it.Path);done++;freed+=it.Size;}catch(Exception ex){Log.Write("Очистка: "+Path.GetFileName(it.Path)+" — "+ex.Message);}}
   SavePrefs();UpdateStorageUi();
   if(done>0){Log.Write("Очистка: в Корзину "+done+" файлов, "+ClipLibrary.Size(freed));Notify("Старые файлы в Корзине",Plural(done,"файл","файла","файлов")+" · "+ClipLibrary.Size(freed),ToastKind.Info);ClipsChanged();}
   else if(manual)Message("Удалять нечего");}
 }
}
