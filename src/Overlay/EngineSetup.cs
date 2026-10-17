using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ReplayOverlay {
 // Подготовка своей копии OBS перед запуском — только файлы, копия в это время не запущена.
 // Недостающее создаётся; ключи, без которых копия покажет окно, мастер первого запуска или начнёт обновляться, выставляются всегда.
 // Настройки, которые меню меняет потом (кодировщик, качество, папка), повторно не перезаписываются.
 public static class EngineSetup {
  public const string Profile="Replay";

  public static List<string> Prepare(EngineHome home,string recordDir){
   var log=new List<string>();string cfg=home.ConfigRoot;Directory.CreateDirectory(cfg);
   Touch(Path.Combine(home.Root,"portable_mode.txt"));Touch(Path.Combine(home.Root,"disable_updater.txt"));
   IniSet(Path.Combine(cfg,"global.ini"),"General","EnableAutoUpdates","false");
   string user=Path.Combine(cfg,"user.ini");
   IniSet(user,"General","FirstRun","true");IniSet(user,"General","ConfirmOnExit","false");
   IniSet(user,"BasicWindow","SysTrayEnabled","true");IniSet(user,"BasicWindow","SysTrayWhenStarted","true");IniSet(user,"BasicWindow","PreviewEnabled","false");
   if(IniGet(user,"Basic","Profile")==null){IniSet(user,"Basic","Profile",Profile);IniSet(user,"Basic","ProfileDir",Profile);IniSet(user,"Basic","SceneCollection",Profile);IniSet(user,"Basic","SceneCollectionFile",Profile);}
   if(!File.Exists(home.WebSocketConfig)){
    int port=FreePort(4466);Directory.CreateDirectory(Path.GetDirectoryName(home.WebSocketConfig));
    File.WriteAllText(home.WebSocketConfig,Json.Encode(Json.D("alerts_enabled",false,"auth_required",true,"first_load",false,"server_enabled",true,"server_password",Password(20),"server_port",port)),new UTF8Encoding(false));
    log.Add("Движок записи: управление на порту "+port);}
   string dir=Path.Combine(cfg,@"basic\profiles\"+Profile),ini=Path.Combine(dir,"basic.ini");
   if(!File.Exists(ini)){
    Directory.CreateDirectory(dir);string folder=string.IsNullOrEmpty(recordDir)?DefaultFolder():recordDir;Directory.CreateDirectory(folder);
    var enc=EncoderCatalog.Describe(EncoderFor(Adapters()));var screen=System.Windows.Forms.Screen.PrimaryScreen.Bounds;
    File.WriteAllText(ini,ProfileIni(folder,enc.Id,screen.Width,screen.Height),new UTF8Encoding(true));
    File.WriteAllText(Path.Combine(dir,"recordEncoder.json"),Json.Encode(EncoderSettings.Build(enc,new QualityChoice(),null)),new UTF8Encoding(false));
    log.Add("Движок записи: профиль создан — "+enc.VendorLabel+" "+enc.CodecLabel+", "+screen.Width+"×"+screen.Height+" 60 к/с, папка "+folder);}
   else{
    // Кодировщика из профиля нет среди найденных при прошлом запуске (догадка по видеокарте не подтвердилась, сменился драйвер) — лучший из найденных.
    string cur=IniGet(ini,"AdvOut","RecEncoder");var found=EncoderCatalog.FromLatestLog(cfg);
    if(found.Count>0&&!found.Any(e=>e.Id==cur)){var was=EncoderCatalog.Describe(cur);var best=EncoderCatalog.Best(found,was!=null?was.Codec:"h264");
     if(best!=null){IniSet(ini,"AdvOut","RecEncoder",best.Id);File.WriteAllText(Path.Combine(dir,"recordEncoder.json"),Json.Encode(EncoderSettings.Build(best,new QualityChoice(),null)),new UTF8Encoding(false));
      log.Add("Движок записи: кодировщика «"+cur+"» нет — выбран "+best.VendorLabel+" "+best.CodecLabel);}}}
   string scenes=Path.Combine(cfg,@"basic\scenes\"+Profile+".json");
   if(!File.Exists(scenes)){
    var screen=System.Windows.Forms.Screen.PrimaryScreen.Bounds;Directory.CreateDirectory(Path.GetDirectoryName(scenes));
    File.WriteAllText(scenes,Json.Encode(Collection(screen.Width,screen.Height)),new UTF8Encoding(false));
    log.Add("Движок записи: сцены «"+GameScene+"» и «"+ScreenScene+"», звук рабочего стола и микрофон");}
   return log;}

  public const string GameScene="Только игра",ScreenScene="Игра и экран",GameSource="Игра",ScreenSource="Экран";
  // Коллекция сцен своей копии. «Только игра» — захват игры (окно игры меню назначит само), «Игра и экран» — захват экрана.
  // Звук рабочего стола и микрофон — глобальные каналы, как в обычном OBS. У захвата игры своего звука нет: он уже есть в звуке рабочего стола.
  public static Dictionary<string,object> Collection(int width,int height){
   var game=Source(GameSource,"game_capture",Json.D("capture_mode","any_fullscreen","capture_audio",false,"capture_cursor",true,"anti_cheat_hook",true,"capture_overlays",false,"limit_framerate",false));
   var screen=Source(ScreenSource,"monitor_capture",Json.D("capture_cursor",true,"monitor_id",PrimaryMonitorId()));
   // В «Игра и экран» захват игры лежит под захватом экрана: его не видно, но он подключён к игре — работает снимок игры,
   // а когда меню прячет экран (видна программа из исключений), в записи остаётся игра.
   var gameScene=Scene(GameScene,width,height,game);var screenScene=Scene(ScreenScene,width,height,game,screen);
   return Json.D("name",Profile,"current_scene",GameScene,"current_program_scene",GameScene,
    "scene_order",new object[]{Json.D("name",GameScene),Json.D("name",ScreenScene)},
    "DesktopAudioDevice1",Source("Звук рабочего стола","wasapi_output_capture",Json.D("device_id","default")),
    "AuxAudioDevice1",Source("Микрофон","wasapi_input_capture",Json.D("device_id","default")),
    "sources",new object[]{game,screen,gameScene,screenScene},"groups",new object[0],"transition_duration",300);}
  static Dictionary<string,object> Source(string name,string kind,Dictionary<string,object> settings){
   return Json.D("name",name,"uuid",Guid.NewGuid().ToString(),"id",kind,"versioned_id",kind,"settings",settings,"mixers",63,"sync",0,"flags",0,"volume",1.0,"balance",0.5,"enabled",true,"muted",false,"monitoring_type",0);}
  // Сцена из источников (первый — нижний), каждый вписан в кадр целиком с сохранением пропорций.
  static Dictionary<string,object> Scene(string name,int width,int height,params Dictionary<string,object>[] sources){
   var items=sources.Select((source,i)=>(object)Json.D("name",source["name"],"source_uuid",source["uuid"],"visible",true,"locked",false,"rot",0.0,"align",5,"bounds_type",2,"bounds_align",0,
    "pos",Json.D("x",0.0,"y",0.0),"scale",Json.D("x",1.0,"y",1.0),"bounds",Json.D("x",(double)width,"y",(double)height),"id",i+1)).ToArray();
   return Source(name,"scene",Json.D("id_counter",items.Length,"custom_size",false,"items",items));}

  // Профиль: расширенный вывод, MKV, одна дорожка, буфер повтора на 5 минут, разрешение экрана, 60 к/с.
  public static string ProfileIni(string folder,string encoder,int width,int height){
   return string.Join("\r\n",new[]{
    "[General]","Name="+Profile,"",
    "[Output]","Mode=Advanced","FilenameFormatting=%CCYY-%MM-%DD_%hh-%mm-%ss","",
    "[AdvOut]","RecType=Standard","RecFilePath="+folder.Replace('\\','/'),"RecFormat2=mkv","RecTracks=1","RecEncoder="+encoder,
    "RecRB=true","RecRBTime=300","RecRBSize=1536","RecRBPrefix=Replay","RecSplitFile=false","",
    "[Video]","BaseCX="+width,"BaseCY="+height,"OutputCX="+width,"OutputCY="+height,"FPSType=0","FPSCommon=60","ColorFormat=NV12","",
    "[Audio]","SampleRate=48000","ChannelSetup=Stereo",""});}

  // Кодировщик по видеокарте: NVIDIA → NVENC, AMD → AMF, Intel → Quick Sync, иначе процессор.
  // Это первая догадка до запуска; какие кодировщики OBS на деле нашёл, меню видит потом в журнале копии.
  public static string EncoderFor(IEnumerable<string> adapters){
   var l=adapters.Select(x=>x.ToLowerInvariant()).ToList();
   if(l.Any(x=>x.Contains("nvidia")))return "obs_nvenc_h264_tex";
   if(l.Any(x=>x.Contains("radeon")||x.StartsWith("amd ")))return "h264_texture_amf";
   if(l.Any(x=>x.Contains("intel")&&(x.Contains("graphics")||x.Contains("arc")||x.Contains("iris")||x.Contains("uhd"))))return "obs_qsv11_v2";
   return "obs_x264";}
  static List<string> Adapters(){
   var names=new List<string>();
   try{using(var cls=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")){
    if(cls!=null)foreach(string k in cls.GetSubKeyNames()){try{using(var a=cls.OpenSubKey(k)){object d=a==null?null:a.GetValue("DriverDesc");if(d!=null)names.Add(Convert.ToString(d));}}catch(Exception){}}}}
   catch(Exception){}
   return names;}

  // Захват экрана OBS хранит монитор как путь устройства («\\?\DISPLAY#…#{e6f07b5f-…}»); без него кадр пустой.
  [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
  struct DisplayDevice{public int cb;[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr,SizeConst=32)]public string DeviceName;[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceString;public int StateFlags;[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceID;[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceKey;}
  [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern bool EnumDisplayDevices(string device,int index,ref DisplayDevice dd,int flags);
  public static string PrimaryMonitorId(){
   try{var dd=new DisplayDevice();dd.cb=System.Runtime.InteropServices.Marshal.SizeOf(dd);
    if(EnumDisplayDevices(System.Windows.Forms.Screen.PrimaryScreen.DeviceName,0,ref dd,1))return dd.DeviceID??""; // 1 — EDD_GET_DEVICE_INTERFACE_NAME
   }catch(Exception){}
   return "";}

  public static string DefaultFolder(){return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"Replay");}
  static int FreePort(int first){for(int p=first;p<first+40;p++){try{var l=new TcpListener(IPAddress.Loopback,p);l.Start();l.Stop();return p;}catch(SocketException){}}return first;}
  static string Password(int n){const string abc="ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";var b=new byte[n];using(var r=RandomNumberGenerator.Create())r.GetBytes(b);var s=new StringBuilder();foreach(byte x in b)s.Append(abc[x%abc.Length]);return s.ToString();}
  static void Touch(string path){if(!File.Exists(path))File.WriteAllText(path,"");}

  // INI OBS: UTF-8 с BOM, разделы [Имя], строки ключ=значение. Меняется только нужный ключ, остальное остаётся как было.
  public static string IniGet(string path,string section,string key){return File.Exists(path)?ObsConfig.IniValue(path,section,key):null;}
  public static void IniSet(string path,string section,string key,string value){
   var lines=File.Exists(path)?File.ReadAllLines(path).ToList():new List<string>();
   if(lines.Count>0)lines[0]=lines[0].TrimStart('\uFEFF');
   int start=lines.FindIndex(l=>string.Equals(l.Trim(),"["+section+"]",StringComparison.OrdinalIgnoreCase));
   if(start<0){if(lines.Count>0&&lines[lines.Count-1].Trim().Length>0)lines.Add("");lines.Add("["+section+"]");lines.Add(key+"="+value);}
   else{
    int end=start+1;while(end<lines.Count&&!lines[end].TrimStart().StartsWith("["))end++;
    int at=-1;for(int i=start+1;i<end;i++){int eq=lines[i].IndexOf('=');if(eq>0&&string.Equals(lines[i].Substring(0,eq).Trim(),key,StringComparison.OrdinalIgnoreCase)){at=i;break;}}
    if(at>=0)lines[at]=key+"="+value;
    else{int ins=end;while(ins>start+1&&lines[ins-1].Trim().Length==0)ins--;lines.Insert(ins,key+"="+value);}}
   Directory.CreateDirectory(Path.GetDirectoryName(path));
   File.WriteAllText(path,string.Join("\r\n",lines)+"\r\n",new UTF8Encoding(true));}
 }
}
