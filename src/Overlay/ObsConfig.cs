using System;
using System.IO;
using Microsoft.Win32;

namespace ReplayOverlay {
 // Имена объектов, через которые общаются копии меню и установщик.
 public static class Names {
  public const string Mutex=@"Local\ReplayOverlay.Instance";
  public const string Show=@"Local\ReplayOverlay.Show";
  public const string Exit=@"Local\ReplayOverlay.Exit";
 }

 // Где лежат файлы OBS Studio текущего пользователя. Только чтение.
 public static class ObsConfig {
  public static string RootOverride; // своя копия OBS: её папка настроек вместо %APPDATA%\obs-studio
  public static string Root { get { return RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"obs-studio"); } }
  public static string WebSocketConfig { get { return Path.Combine(Root,@"plugin_config\obs-websocket\config.json"); } }

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

  // После сбоя OBS при запуске спрашивает про безопасный режим модальным окном; запущенный в трее OBS на нём зависает
  // (флаг --disable-shutdown-check это окно не отключает). Признак сбоя — файлы .sentinel\run_*.
  // Вызывать только когда OBS не запущен: тогда все эти файлы устаревшие. Возвращает число удалённых.
  public static int ClearCrashSentinels() {
   string dir=Path.Combine(Root,".sentinel"); int removed=0;
   if(!Directory.Exists(dir))return 0;
   foreach(string f in Directory.GetFiles(dir,"run_*")){try{File.Delete(f);removed++;}catch{}}
   return removed;
  }

  public static string Executable() {
   try {
    using(var key=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64).OpenSubKey(@"SOFTWARE\OBS Studio")) {
     string dir=key==null?null:key.GetValue("") as string;
     if(!string.IsNullOrEmpty(dir)){string exe=Path.Combine(dir,@"bin\64bit\obs64.exe");if(File.Exists(exe))return exe;}
    }
   } catch { }
   string fallback=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"obs-studio\bin\64bit\obs64.exe");
   return File.Exists(fallback)?fallback:null;
  }
 }
}
