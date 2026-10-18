using System;
using System.IO;

namespace ReplayOverlay {
 // Название программы и все имена, которые от него зависят: файлы, папки, реестр, задачи, объекты синхронизации.
 public static class Brand {
  public const string Name="Vanta";
  public const string Exe="Vanta.exe",Process="Vanta",Setup="Vanta-Setup.exe";
  public const string Settings="vanta-settings.json",LogFile="vanta.log",LogOld="vanta.1.log";
  public const string InstallFolder="Vanta",Shortcut="Vanta.lnk",RunValue="Vanta",UninstallKey="Vanta";
  public const string TaskStart=@"Vanta\Engine",TaskStop=@"Vanta\Close engine";
  public const string Repo="https://github.com/Ekikak-wg/Vanta"; // прежний адрес Replay-OBS GitHub перенаправляет сюда
  public const string Tagline="Меню мгновенного повтора поверх игр";

  // Прежнее имя (Replay OBS, до 1.7): установщик переносит такую установку и убирает её следы.
  public static class Legacy {
   public const string InstallFolder="Replay OBS",Exe="ReplayOverlay.exe",Process="ReplayOverlay",Settings="overlay-settings.json";
   public const string Shortcut="Replay Overlay.lnk",RunValue="Replay Overlay",UninstallKey="ReplayOBS",StartMenu="Replay OBS";
   public const string TaskStart=@"Replay OBS\Engine",TaskStop=@"Replay OBS\Close engine";
   public const string Exit=@"Local\ReplayOverlay.Exit";
  }
 }

 // Имена объектов, через которые общаются копии меню и установщик.
 public static class Names {
  public const string Mutex=@"Local\Vanta.Instance";
  public const string Show=@"Local\Vanta.Show";
  public const string Exit=@"Local\Vanta.Exit";
 }

 // Где лежат файлы движка записи (своей копии OBS): журналы, профиль, коллекция сцен. Только чтение.
 public static class ObsConfig {
  public static string Root; // config\obs-studio движка; задаёт движок при создании

  // OBS 31+ хранит выбранный профиль и коллекцию в user.ini, более старые версии — в global.ini.
  public static string Ini(string section,string key) {
   foreach(string name in new[]{"user.ini","global.ini"}) {
    string path=Path.Combine(Root,name); if(!File.Exists(path))continue;
    string value=IniValue(path,section,key); if(value!=null)return value;
   }
   return null;
  }
  public static string IniValue(string path,string section,string key) {
   bool inside=false;
   foreach(string raw in File.ReadAllLines(path)) {
    string line=raw.Trim();
    if(line.StartsWith("[")){inside=string.Equals(line,"["+section+"]",StringComparison.OrdinalIgnoreCase);continue;}
    int eq=line.IndexOf('=');
    if(inside&&eq>0&&string.Equals(line.Substring(0,eq),key,StringComparison.OrdinalIgnoreCase))return line.Substring(eq+1);
   }
   return null;
  }

  public static string ProfileDirectory(string profileName) {
   string profiles=Path.Combine(Root,@"basic\profiles");
   if(Ini("Basic","Profile")==profileName){string dir=Ini("Basic","ProfileDir");if(!string.IsNullOrEmpty(dir)&&Directory.Exists(Path.Combine(profiles,dir)))return Path.Combine(profiles,dir);}
   if(Directory.Exists(profiles))
    foreach(string dir in Directory.GetDirectories(profiles)) {
     string ini=Path.Combine(dir,"basic.ini");
     try { if(File.Exists(ini)&&IniValue(ini,"General","Name")==profileName)return dir; } catch { }
    }
   return Path.Combine(profiles,profileName.Replace(' ','_'));
  }

  public static string SceneCollectionFile() {
   string file=Ini("Basic","SceneCollectionFile"); if(string.IsNullOrEmpty(file))return null;
   if(!file.EndsWith(".json",StringComparison.OrdinalIgnoreCase))file+=".json";
   return Path.Combine(Root,@"basic\scenes",file);
  }

  // После сбоя OBS (движок) при запуске спрашивает про безопасный режим модальным окном; запущенный в трее OBS на нём зависает
  // (флаг --disable-shutdown-check это окно не отключает). Признак сбоя — файлы .sentinel\run_*.
  // Вызывать только когда OBS не запущен: тогда все эти файлы устаревшие. Возвращает число удалённых.
  public static int ClearCrashSentinels() {
   string dir=Path.Combine(Root,".sentinel"); int removed=0;
   if(!Directory.Exists(dir))return 0;
   foreach(string f in Directory.GetFiles(dir,"run_*")){try{File.Delete(f);removed++;}catch{}}
   return removed;
  }
 }
}
