using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReplayOverlay {
 // Кодировщик видео OBS: кто кодирует (видеокарта или процессор) и каким кодеком.
 public sealed class EncoderInfo {
  public string Id,Name,Vendor,Codec; // Vendor: nvenc|amf|qsv|cpu; Codec: h264|hevc|av1
  public string VendorLabel{get{return Vendor=="nvenc"?"NVIDIA NVENC":Vendor=="amf"?"AMD AMF":Vendor=="qsv"?"Intel Quick Sync":"Процессор";}}
  public string CodecLabel{get{return Codec=="hevc"?"HEVC (H.265)":Codec=="av1"?"AV1":"H.264";}}
 }

 public static class EncoderCatalog {
  // Известные кодировщики OBS: id → производитель и кодек. Остальные (AOM, SVT AV1 и т. п.) не предлагаем: для записи игры они слишком медленные.
  static readonly string[][] Known={
   new[]{"obs_nvenc_h264_tex","nvenc","h264"},new[]{"obs_nvenc_hevc_tex","nvenc","hevc"},new[]{"obs_nvenc_av1_tex","nvenc","av1"},
   new[]{"jim_nvenc","nvenc","h264"},new[]{"jim_hevc_nvenc","nvenc","hevc"},new[]{"jim_av1_nvenc","nvenc","av1"},
   new[]{"h264_texture_amf","amf","h264"},new[]{"h265_texture_amf","amf","hevc"},new[]{"av1_texture_amf","amf","av1"},
   new[]{"obs_qsv11_v2","qsv","h264"},new[]{"obs_qsv11","qsv","h264"},new[]{"obs_qsv11_hevc","qsv","hevc"},new[]{"obs_qsv11_av1","qsv","av1"},
   new[]{"obs_x264","cpu","h264"}};

  // Раздел «Available Encoders» журнала OBS: строки вида «- obs_nvenc_h264_tex (NVIDIA NVENC H.264)».
  public static List<EncoderInfo> Parse(string log){
   var found=new List<EncoderInfo>();if(string.IsNullOrEmpty(log))return found;
   int start=log.LastIndexOf("Available Encoders:",StringComparison.Ordinal);if(start<0)return found;
   int audio=log.IndexOf("Audio Encoders:",start,StringComparison.Ordinal);string block=audio>0?log.Substring(start,audio-start):log.Substring(start);
   foreach(Match m in Regex.Matches(block,@"-\s+([A-Za-z0-9_]+)\s+\(([^)\r\n]*)\)")){
    string id=m.Groups[1].Value;var k=Known.FirstOrDefault(x=>x[0]==id);if(k==null)continue;
    if(found.Any(f=>f.Vendor==k[1]&&f.Codec==k[2]))continue; // новые и старые имена NVENC/QSV — берём первое (новое)
    found.Add(new EncoderInfo{Id=id,Name=m.Groups[2].Value,Vendor=k[1],Codec=k[2]});}
   return found;}

  // Последний журнал OBS — там список кодировщиков, найденных при запуске.
  public static List<EncoderInfo> FromLatestLog(){
   try{string dir=Path.Combine(ObsConfig.Root,"logs");if(!Directory.Exists(dir))return new List<EncoderInfo>();
    var file=new DirectoryInfo(dir).GetFiles("*.txt").OrderByDescending(f=>f.LastWriteTime).FirstOrDefault();if(file==null)return new List<EncoderInfo>();
    using(var s=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var r=new StreamReader(s))return Parse(r.ReadToEnd());}
   catch(Exception){return new List<EncoderInfo>();}}

  public static EncoderInfo Describe(string id){var k=Known.FirstOrDefault(x=>x[0]==id);return k==null?null:new EncoderInfo{Id=id,Name=id,Vendor=k[1],Codec=k[2]};}
 }

 // Качество: recommended|high|max|custom. Для custom — режим (VBR|CBR|CQP) и значение (Мбит/с или уровень CQP).
 public sealed class QualityChoice {
  public string Mode="recommended",RateControl="VBR";public int Value=20;
  public static readonly string[] Modes={"recommended","high","max","custom"};
 }

 public static class EncoderSettings {
  // Настройки recordEncoder.json для выбранного кодировщика. Ключи — те, что читают модули OBS (obs-nvenc, obs-qsv11, AMF, obs-x264).
  // Проверено вживую только NVENC; ключи AMF и QSV взяты из их модулей OBS.
  public static Dictionary<string,object> Build(EncoderInfo e,QualityChoice q,Dictionary<string,object> previous){
   var s=new Dictionary<string,object>();
   if(previous!=null&&previous.ContainsKey("device"))s["device"]=previous["device"]; // выбранная видеокарта, если их несколько
   string rc;int bitrate=0,cqp=0;string speed;
   if(q.Mode=="custom"){rc=q.RateControl;if(rc=="CQP")cqp=q.Value;else bitrate=q.Value;speed="quality";}
   else if(q.Mode=="max"){rc="CQP";cqp=e.Codec=="av1"?24:18;speed="max";}
   else if(q.Mode=="high"){rc="VBR";bitrate=40;speed="quality";}
   else{rc="VBR";bitrate=20;speed="balanced";}
   int kbps=bitrate*1000;
   if(e.Vendor=="nvenc"){
    s["rate_control"]=rc;if(rc=="CQP")s["cqp"]=cqp;else{s["bitrate"]=kbps;s["max_bitrate"]=rc=="VBR"?kbps*3/2:kbps;}
    s["preset"]=speed=="max"?"p6":speed=="quality"?"p5":"p4";s["tune"]="hq";s["multipass"]=speed=="balanced"?"disabled":"qres";
    s["profile"]=e.Codec=="h264"?"high":"main";s["bf"]=2;s["lookahead"]=false;s["adaptive_quantization"]=speed!="balanced";s["keyint_sec"]=2;}
   else if(e.Vendor=="amf"){
    s["rate_control"]=rc=="VBR"?"VBR":rc;if(rc=="CQP")s["cqp"]=cqp;else s["bitrate"]=kbps;
    s["preset"]=speed=="balanced"?"balanced":"quality";s["keyint_sec"]=2;}
   else if(e.Vendor=="qsv"){
    s["rate_control"]=rc;if(rc=="CQP"){s["cqp"]=cqp;s["qpi"]=cqp;s["qpp"]=cqp;s["qpb"]=cqp;}else{s["bitrate"]=kbps;s["max_bitrate"]=rc=="VBR"?kbps*3/2:kbps;}
    s["target_usage"]=speed=="balanced"?"balanced":"quality";s["keyint_sec"]=2;}
   else{ // x264
    if(rc=="CQP"){s["rate_control"]="CRF";s["crf"]=cqp;}else{s["rate_control"]=rc;s["bitrate"]=kbps;}
    s["preset"]="veryfast";s["keyint_sec"]=2;}
   return s;}

  // Обратно: что показать в меню по текущему recordEncoder.json.
  public static QualityChoice Read(EncoderInfo e,Dictionary<string,object> s){
   var q=new QualityChoice();if(s==null||e==null)return q;
   string rc=Json.Str(s,"rate_control","VBR").ToUpperInvariant();if(rc=="CRF")rc="CQP";
   int kbps=Json.Int(s,"bitrate",20000),cqp=Json.Int(s,"cqp",Json.Int(s,"crf",20));
   var probe=new QualityChoice();
   foreach(string mode in new[]{"recommended","high","max"}){probe.Mode=mode;var built=Build(e,probe,null);
    if(Json.Str(built,"rate_control")==Json.Str(s,"rate_control")&&Json.Int(built,"bitrate",-1)==Json.Int(s,"bitrate",-1)&&Json.Int(built,"cqp",-1)==Json.Int(s,"cqp",-1)&&Json.Str(built,"preset")==Json.Str(s,"preset")){q.Mode=mode;return q;}}
   q.Mode="custom";q.RateControl=rc=="CBR"?"CBR":rc=="CQP"?"CQP":"VBR";q.Value=q.RateControl=="CQP"?cqp:Math.Max(1,kbps/1000);return q;}

  // Подходит ли контейнер для кодека: в FLV — только H.264.
  public static bool FormatFits(string format,string codec){return format!="flv"||codec=="h264";}
  public static string Check(QualityChoice q){
   if(q.Mode!="custom")return null;
   if(q.RateControl=="CQP"){if(q.Value<1||q.Value>51)return "Уровень CQP — от 1 (лучше) до 51";}
   else if(q.Value<2||q.Value>150)return "Битрейт — от 2 до 150 Мбит/с";
   return null;}
 }
}
