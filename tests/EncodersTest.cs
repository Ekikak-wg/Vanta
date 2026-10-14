// Кодировщики из журнала OBS, настройки recordEncoder.json, план автоочистки.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReplayOverlay;

static class EncodersTest {
 static int failures;
 static void Check(bool ok,string what){Console.WriteLine((ok?"OK    ":"FAIL  ")+what);if(!ok)failures++;}
 static int Main(){
  Console.OutputEncoding=System.Text.Encoding.UTF8;
  string log="08:35:57.803: Available Encoders:\n08:35:57.803:   Video Encoders:\n08:35:57.803: \t- ffmpeg_svt_av1 (SVT-AV1)\n08:35:57.803: \t- ffmpeg_aom_av1 (AOM AV1)\n08:35:57.803: \t- obs_nvenc_h264_tex (NVIDIA NVENC H.264)\n08:35:57.803: \t- obs_nvenc_hevc_tex (NVIDIA NVENC HEVC)\n08:35:57.803: \t- obs_nvenc_av1_tex (NVIDIA NVENC AV1)\n08:35:57.803: \t- jim_nvenc (NVIDIA NVENC H.264 старый)\n08:35:57.803: \t- obs_x264 (x264)\n08:35:57.803:   Audio Encoders:\n08:35:57.803: \t- ffmpeg_aac (FFmpeg AAC)\n";
  var list=EncoderCatalog.Parse(log);
  Check(string.Join(",",list.Select(e=>e.Id))=="obs_nvenc_h264_tex,obs_nvenc_hevc_tex,obs_nvenc_av1_tex,obs_x264","из журнала: NVENC H.264/HEVC/AV1 и x264; медленные AV1 и старое имя NVENC пропущены: "+string.Join(",",list.Select(e=>e.Id)));
  Check(list[2].Vendor=="nvenc"&&list[2].Codec=="av1"&&list[3].Vendor=="cpu","производитель и кодек определены");
  Check(EncoderCatalog.Parse("").Count==0&&EncoderCatalog.Parse("нет такого раздела").Count==0,"пустой журнал — пустой список");
  string amd="Available Encoders:\n Video Encoders:\n - h264_texture_amf (AMD HW H.264 (AVC))\n - h265_texture_amf (AMD HW H.265 (HEVC))\n - obs_qsv11_v2 (QuickSync H.264)\n Audio Encoders:\n";
  var l2=EncoderCatalog.Parse(amd);Check(l2.Count==3&&l2[0].Vendor=="amf"&&l2[1].Codec=="hevc"&&l2[2].Vendor=="qsv","AMD и Intel распознаются");

  var nv=list[0];var q=new QualityChoice{Mode="recommended"};var s=EncoderSettings.Build(nv,q,new Dictionary<string,object>{{"device",1}});
  Check(Json.Str(s,"rate_control")=="VBR"&&Json.Int(s,"bitrate")==20000&&Json.Int(s,"max_bitrate")==30000&&Json.Str(s,"preset")=="p4"&&Json.Int(s,"device")==1,"NVENC «Рекомендуемое»: VBR 20 Мбит/с, p4, видеокарта сохранена");
  Check(EncoderSettings.Read(nv,s).Mode=="recommended","чтение обратно — «Рекомендуемое»");
  var hi=EncoderSettings.Build(nv,new QualityChoice{Mode="high"},null);Check(EncoderSettings.Read(nv,hi).Mode=="high"&&Json.Int(hi,"bitrate")==40000,"«Высокое» — 40 Мбит/с и читается обратно");
  var mx=EncoderSettings.Build(list[2],new QualityChoice{Mode="max"},null);Check(Json.Str(mx,"rate_control")=="CQP"&&Json.Int(mx,"cqp")==24&&!mx.ContainsKey("bitrate"),"«Максимальное» для AV1 — CQP 24, без битрейта");
  var cu=EncoderSettings.Build(nv,new QualityChoice{Mode="custom",RateControl="CBR",Value=35},null);var back=EncoderSettings.Read(nv,cu);
  Check(Json.Str(cu,"rate_control")=="CBR"&&Json.Int(cu,"bitrate")==35000&&back.Mode=="custom"&&back.RateControl=="CBR"&&back.Value==35,"своё: CBR 35 Мбит/с туда и обратно");
  var old=new Dictionary<string,object>{{"rate_control","CQP"},{"cqp",21},{"preset","p4"},{"bitrate",20000}};var r=EncoderSettings.Read(nv,old);
  Check(r.Mode=="custom"&&r.RateControl=="CQP"&&r.Value==21,"настройки, сделанные в OBS вручную (CQP 21), показываются как «своё»");
  var x=EncoderSettings.Build(list[3],new QualityChoice{Mode="custom",RateControl="CQP",Value=20},null);Check(Json.Str(x,"rate_control")=="CRF"&&Json.Int(x,"crf")==20,"x264: CQP превращается в CRF");
  Check(EncoderSettings.Check(new QualityChoice{Mode="custom",RateControl="CQP",Value=60})!=null&&EncoderSettings.Check(new QualityChoice{Mode="custom",RateControl="VBR",Value=500})!=null&&EncoderSettings.Check(new QualityChoice{Mode="custom",RateControl="VBR",Value=50})==null,"проверка диапазонов");
  Check(!EncoderSettings.FormatFits("flv","hevc")&&EncoderSettings.FormatFits("mkv","av1"),"FLV — только H.264");

  var now=new DateTime(2026,10,14,12,0,0);long gb=1073741824L;
  var files=new[]{new Cleanup.Item{Path="a",Modified=now.AddDays(-40),Size=gb},new Cleanup.Item{Path="b",Modified=now.AddDays(-10),Size=3*gb},new Cleanup.Item{Path="c",Modified=now.AddDays(-5),Size=2*gb},new Cleanup.Item{Path="d",Modified=now.AddDays(-1),Size=2*gb}};
  Check(string.Join(",",Cleanup.Plan(files,30,0,now).Select(f=>f.Path))=="a","по сроку 30 дней — только старше 30");
  Check(string.Join(",",Cleanup.Plan(files,0,5,now).Select(f=>f.Path))=="a,b","по пределу 5 ГБ — самые старые, пока не влезет (8 → 4 ГБ)");
  Check(Cleanup.Plan(files,0,0,now).Count==0,"без срока и предела — ничего");
  Check(string.Join(",",Cleanup.Plan(files,30,4,now).Select(f=>f.Path))=="a,b","срок и предел вместе");

  var m=new ManagedFiles();m.Add(@"C:\v\a.mkv");m.Add(@"c:\V\A.MKV");m.Add(@"C:\v\b.mkv");m.Rename(@"C:\v\b.mkv",@"C:\v\c.mkv");m.Remove(@"C:\V\a.mkv");
  Check(m.Paths.Count==1&&m.Paths[0]==@"C:\v\c.mkv","учёт файлов: без дублей, переименование, удаление");
  var m2=new ManagedFiles();m2.Load(m.ToJson());Check(m2.Has(@"C:\v\c.mkv"),"учёт сохраняется и читается");
  string tmp=Path.GetTempFileName();m2.Add(tmp);Check(m2.Prune()==1&&m2.Has(tmp),"несуществующие файлы убираются из учёта");File.Delete(tmp);

  Console.WriteLine(failures==0?"ИТОГ: все проверки пройдены":"ИТОГ: провалов "+failures);
  return failures==0?0:1;
 }
}
