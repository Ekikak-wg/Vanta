using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ReplayOverlay;

namespace ReplaySetup {
 // Куда ставить. В тестовом режиме (--test-root) всё уходит в отдельную папку и ветку реестра.
 sealed class Paths {
  public string InstallDir,StartMenuDir,DesktopDir,StartupDir,RegistryRoot,LegacyDir,LegacyStartMenuDir; public bool Test;
  public string StartupShortcut { get { return Path.Combine(StartupDir,Brand.Shortcut); } }
  public string DesktopShortcut { get { return Path.Combine(DesktopDir,Brand.Shortcut); } }
  public string Exe { get { return Path.Combine(InstallDir,Brand.Exe); } }
  public string Uninstaller { get { return Path.Combine(InstallDir,"uninstall.exe"); } }
  public string EngineDir { get { return Path.Combine(InstallDir,"engine"); } }
  public string EngineMarker { get { return Path.Combine(EngineDir,"replay-engine.json"); } }
  public string RunKey { get { return RegistryRoot+@"\Run"; } }
  public string UninstallKey { get { return RegistryRoot+@"\Uninstall\"+Brand.UninstallKey; } }
  public string LegacyUninstallKey { get { return RegistryRoot+@"\Uninstall\"+Brand.Legacy.UninstallKey; } }
  public string LegacyEngineDir { get { return Path.Combine(LegacyDir,"engine"); } }
  public static Paths Real() {
   return new Paths{
    InstallDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\"+Brand.InstallFolder),
    StartMenuDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),Brand.Name),
    LegacyDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\"+Brand.Legacy.InstallFolder),
    LegacyStartMenuDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),Brand.Legacy.StartMenu),
    DesktopDir=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    StartupDir=Environment.GetFolderPath(Environment.SpecialFolder.Startup),
    RegistryRoot=@"Software\Microsoft\Windows\CurrentVersion"};
  }
  public static Paths ForTest(string root) {
   root=Path.GetFullPath(root);
   return new Paths{Test=true,InstallDir=Path.Combine(root,@"Programs\"+Brand.InstallFolder),StartMenuDir=Path.Combine(root,@"StartMenu\"+Brand.Name),LegacyDir=Path.Combine(root,@"Programs\"+Brand.Legacy.InstallFolder),LegacyStartMenuDir=Path.Combine(root,@"StartMenu\"+Brand.Legacy.StartMenu),DesktopDir=Path.Combine(root,"Desktop"),StartupDir=Path.Combine(root,"Startup"),RegistryRoot=@"Software\ReplayOBS-Test"};
  }
 }

 // Движок записи — официальный портативный OBS Studio (GPL-2.0) с GitHub проекта OBS. Версия и контрольная сумма закреплены:
 // установщик ставит ровно этот архив и ничего другого.
 static class EngineRelease {
  public const string Version="32.2.2";
  public const string Url="https://github.com/obsproject/obs-studio/releases/download/32.2.2/OBS-Studio-32.2.2-Windows-x64.zip";
  public const string Sha256="4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1";
  public const long Size=187851583;
 }

 sealed class Options {
  public bool Autostart=true,Desktop,Launch=true,GpuPriority,Engine=true;
  public string EngineZip;                                       // готовый архив вместо загрузки (без сети)
  public string EngineSha=EngineRelease.Sha256,EngineVersion=EngineRelease.Version; // подменяются только в тестах (--test-root)
  public Action<string> Progress=s=>{};
 }

 static class Installer {
  public static string Version { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }
  public static string InstalledVersion(Paths p) {
   using(var k=Registry.CurrentUser.OpenSubKey(p.UninstallKey))return k==null?null:k.GetValue("DisplayVersion") as string;
  }
  // Версия движка рядом с меню; null — движка нет.
  public static string EngineVersion(Paths p) { return EngineVersionIn(p.EngineDir); }
  public static string EngineVersionIn(string dir) {
   try { string marker=Path.Combine(dir,"replay-engine.json"); if(!File.Exists(marker)||!File.Exists(Path.Combine(dir,@"bin\64bit\obs64.exe")))return null; return Json.Str(Json.Decode(File.ReadAllText(marker)),"version",null); } catch { return null; }
  }
  // Установка под прежним именем (Replay OBS, до 1.7).
  public static bool LegacyInstalled(Paths p) { using(var k=Registry.CurrentUser.OpenSubKey(p.LegacyUninstallKey))return k!=null||File.Exists(Path.Combine(p.LegacyDir,Brand.Legacy.Exe)); }

  // Что выбрано у уже установленной версии — при обновлении это сохраняется (галочки установщика и тихий режим без ключей).
  public sealed class Current { public bool Installed,Autostart,Desktop,GpuPriority; }
  public static Current CurrentState(Paths p) {
   var c=new Current{Installed=InstalledVersion(p)!=null||LegacyInstalled(p)};
   if(!c.Installed)return c;
   using(var run=Registry.CurrentUser.OpenSubKey(p.RunKey))c.Autostart=File.Exists(p.StartupShortcut)||File.Exists(Path.Combine(p.StartupDir,Brand.Legacy.Shortcut))||(run!=null&&(run.GetValue(Brand.RunValue)!=null||run.GetValue(Brand.Legacy.RunValue)!=null));
   c.Desktop=File.Exists(p.DesktopShortcut)||File.Exists(Path.Combine(p.DesktopDir,Brand.Legacy.Shortcut));
   c.GpuPriority=!p.Test&&(TasksExist()||LegacyTasksExist());
   return c;
  }

  // ——— Загрузка и проверка движка ———
  // Сначала архив целиком скачивается и проверяется — до любых изменений на диске: сбой сети ничего не ломает.
  public static string FetchEngine(Paths p,Options o) {
   if(o.EngineZip!=null){o.Progress("Проверяю архив движка…");Verify(o.EngineZip,o.EngineSha);return o.EngineZip;}
   var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(p.InstallDir)));
   if(drive.AvailableFreeSpace<EngineRelease.Size*4)throw new IOException("Мало места на диске "+drive.Name+": для движка записи нужно около 700 МБ");
   string dir=Path.Combine(Path.GetTempPath(),"Vanta-engine-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(dir);
   string zip=Path.Combine(dir,"OBS-Studio-"+EngineRelease.Version+"-Windows-x64.zip");
   ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072; // TLS 1.2 — GitHub другой не принимает
   Exception last=null;
   for(int attempt=1;attempt<=3;attempt++){
    try{Download(EngineRelease.Url,zip,o.Progress);Verify(zip,o.EngineSha);return zip;}
    catch(Exception ex){last=ex;o.Progress("Загрузка прервалась ("+ex.Message+"), попытка "+(attempt+1)+" из 3…");Thread.Sleep(2000*attempt);}
   }
   try{Directory.Delete(dir,true);}catch{}
   throw new IOException("Не удалось скачать движок записи с GitHub: "+last.Message+". Проверьте интернет; если работает VPN — попробуйте выключить его или сменить сервер, и повторите.");
  }
  static void Download(string url,string file,Action<string> progress) {
   var req=(HttpWebRequest)WebRequest.Create(url);req.UserAgent="Vanta-Setup/"+Version;req.Timeout=30000;req.ReadWriteTimeout=60000;
   using(var resp=(HttpWebResponse)req.GetResponse())using(var src=resp.GetResponseStream())using(var dst=File.Create(file)){
    long total=resp.ContentLength>0?resp.ContentLength:EngineRelease.Size,done=0;var buf=new byte[1<<16];int n;var shown=DateTime.MinValue;
    while((n=src.Read(buf,0,buf.Length))>0){dst.Write(buf,0,n);done+=n;
     if((DateTime.Now-shown).TotalMilliseconds>250){shown=DateTime.Now;progress("Скачиваю движок записи: "+(done>>20)+" из "+(total>>20)+" МБ");}}
    if(done!=total)throw new IOException("скачано "+(done>>20)+" из "+(total>>20)+" МБ");}
  }
  public static string Sha256Of(string file){using(var s=File.OpenRead(file))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
  static void Verify(string zip,string sha){
   string got=Sha256Of(zip);
   if(got!=sha.ToLowerInvariant())throw new InvalidDataException("контрольная сумма архива движка не совпала — файл повреждён или подменён");}

  // Распаковка рядом: новая версия — в engine.new, настройки движка (папка config) переносятся из прежней; затем замена.
  public static void InstallEngine(Paths p,string zip,string version) {
   string fresh=p.EngineDir+".new",old=p.EngineDir+".old";
   foreach(string d in new[]{fresh,old})if(Directory.Exists(d))Directory.Delete(d,true);
   ZipFile.ExtractToDirectory(zip,fresh);
   if(!File.Exists(Path.Combine(fresh,@"bin\64bit\obs64.exe"))){Directory.Delete(fresh,true);throw new InvalidDataException("в архиве нет bin\\64bit\\obs64.exe");}
   string config=Path.Combine(p.EngineDir,"config");
   if(Directory.Exists(config))Directory.Move(config,Path.Combine(fresh,"config"));
   if(Directory.Exists(p.EngineDir))Directory.Move(p.EngineDir,old);
   Directory.Move(fresh,p.EngineDir);
   File.WriteAllText(p.EngineMarker,Json.Encode(Json.D("version",version,"source",EngineRelease.Url)),new System.Text.UTF8Encoding(false));
   if(Directory.Exists(old))try{Directory.Delete(old,true);}catch{}
  }

  // Свой движок запущен из папки установки (обычно его закрывает меню при выходе).
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h,int flags,System.Text.StringBuilder s,ref int size);
  static bool EngineRunning(Paths p){return EngineRunningIn(p.EngineDir);}
  static bool EngineRunningIn(string engineDir){
   string dir=Path.GetFullPath(engineDir)+"\\";
   foreach(var proc in Process.GetProcessesByName("obs64")){
    IntPtr h=OpenProcess(0x1000,false,proc.Id);if(h==IntPtr.Zero)continue;
    try{var s=new System.Text.StringBuilder(1024);int n=s.Capacity;if(QueryFullProcessImageName(h,0,s,ref n)&&s.ToString().StartsWith(dir,StringComparison.OrdinalIgnoreCase))return true;}finally{CloseHandle(h);}}
   return false;}

  // Закрывает установленное меню (сигнал выхода, затем принудительно — только процессы из папки установки).
  // Меню при выходе само закрывает свой движок.
  public static void StopOverlay(Paths p) {
   if(p.Test)return;
   EventWaitHandle quit; if(EventWaitHandle.TryOpenExisting(Names.Exit,out quit))using(quit)quit.Set();
   for(int i=0;i<100;i++){ if(Running(p).Count==0)break; Thread.Sleep(100); }
   foreach(var proc in Running(p))try{proc.Kill();proc.WaitForExit(3000);}catch{}
   for(int i=0;i<100&&EngineRunning(p);i++)Thread.Sleep(100);
   if(EngineRunning(p))throw new IOException("Движок записи ещё работает — выйдите из меню через значок в трее и повторите");
  }
  // Прежнее меню (Replay OBS): тот же порядок — сигнал выхода, затем принудительно только процессы из его папки; его движок закрывает оно само.
  public static void StopLegacy(Paths p) {
   if(p.Test||!Directory.Exists(p.LegacyDir))return;
   string exe=Path.GetFullPath(Path.Combine(p.LegacyDir,Brand.Legacy.Exe));
   Func<List<Process>> legacy=()=>Process.GetProcessesByName(Brand.Legacy.Process).Where(pr=>{try{return string.Equals(Path.GetFullPath(pr.MainModule.FileName),exe,StringComparison.OrdinalIgnoreCase);}catch{return false;}}).ToList();
   EventWaitHandle quit; if(EventWaitHandle.TryOpenExisting(Brand.Legacy.Exit,out quit))using(quit)quit.Set();
   for(int i=0;i<100;i++){ if(legacy().Count==0)break; Thread.Sleep(100); }
   foreach(var proc in legacy())try{proc.Kill();proc.WaitForExit(3000);}catch{}
   for(int i=0;i<100&&EngineRunningIn(p.LegacyEngineDir);i++)Thread.Sleep(100);
   if(EngineRunningIn(p.LegacyEngineDir))throw new IOException("Движок прежней версии (Replay OBS) ещё работает — выйдите из неё через значок в трее и повторите");
  }
  // Перенос прежней установки: движок с его настройками (без повторной загрузки), журналы и настройки меню. Затем следы прежнего имени убираются.
  public static List<string> MigrateLegacy(Paths p) {
   var log=new List<string>();if(!Directory.Exists(p.LegacyDir))return log;
   Directory.CreateDirectory(p.InstallDir);
   if(Directory.Exists(p.LegacyEngineDir)&&!Directory.Exists(p.EngineDir)){Directory.Move(p.LegacyEngineDir,p.EngineDir);log.Add("Движок записи перенесён из Replay OBS вместе с настройками");}
   string oldLogs=Path.Combine(p.LegacyDir,"logs"),newLogs=Path.Combine(p.InstallDir,"logs");
   if(Directory.Exists(oldLogs)&&!Directory.Exists(newLogs))Directory.Move(oldLogs,newLogs);
   string oldPrefs=Path.Combine(p.LegacyDir,Brand.Legacy.Settings),newPrefs=Path.Combine(p.InstallDir,Brand.Settings);
   if(File.Exists(oldPrefs)&&!File.Exists(newPrefs)){File.Copy(oldPrefs,newPrefs);log.Add("Настройки меню перенесены из Replay OBS");}
   return log;
  }
  public static void RemoveLegacyTraces(Paths p) {
   using(var run=Registry.CurrentUser.OpenSubKey(p.RunKey,true))if(run!=null)run.DeleteValue(Brand.Legacy.RunValue,false);
   Registry.CurrentUser.DeleteSubKeyTree(p.LegacyUninstallKey,false);
   foreach(string lnk in new[]{Path.Combine(p.StartupDir,Brand.Legacy.Shortcut),Path.Combine(p.DesktopDir,Brand.Legacy.Shortcut)})if(File.Exists(lnk))File.Delete(lnk);
   if(Directory.Exists(p.LegacyStartMenuDir))Directory.Delete(p.LegacyStartMenuDir,true);
   if(Directory.Exists(p.LegacyDir))try{Directory.Delete(p.LegacyDir,true);}catch(Exception){} // уже ничего нужного: движок и настройки перенесены
  }
  static List<Process> Running(Paths p) {
   var list=new List<Process>();
   foreach(var proc in Process.GetProcessesByName(Brand.Process)){ try { if(string.Equals(Path.GetFullPath(proc.MainModule.FileName),Path.GetFullPath(p.Exe),StringComparison.OrdinalIgnoreCase))list.Add(proc); } catch { } }
   return list;
  }

  public static List<string> Install(Paths p,Options o) {
   var log=new List<string>();
   // 1. Движок: нужен, если его нет или версия другая. Скачивается до любых изменений.
   bool legacy=Directory.Exists(p.LegacyDir);
   string have=EngineVersion(p)??(Directory.Exists(p.EngineDir)?null:EngineVersionIn(p.LegacyEngineDir)),zip=null;bool fetch=o.Engine&&have!=o.EngineVersion;
   if(fetch)zip=FetchEngine(p,o);
   try{
    o.Progress("Устанавливаю…");
    StopOverlay(p);
    if(legacy){StopLegacy(p);log.AddRange(MigrateLegacy(p));}
    Directory.CreateDirectory(p.InstallDir);
    using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.Vanta.exe"))
    using(var file=File.Create(p.Exe))payload.CopyTo(file);
    string self=Assembly.GetExecutingAssembly().Location;
    if(!string.Equals(Path.GetFullPath(self),Path.GetFullPath(p.Uninstaller),StringComparison.OrdinalIgnoreCase))File.Copy(self,p.Uninstaller,true);
    log.Add("Меню установлено: "+p.InstallDir);
    if(fetch){o.Progress("Распаковываю движок записи…");InstallEngine(p,zip,o.EngineVersion);log.Add(have==null?"Движок записи установлен (OBS "+o.EngineVersion+")":"Движок записи обновлён: "+have+" → "+o.EngineVersion+", его настройки сохранены");}
    else if(have!=null)log.Add("Движок записи уже установлен (OBS "+have+")");
   } finally { if(zip!=null&&o.EngineZip==null)try{Directory.Delete(Path.GetDirectoryName(zip),true);}catch{} }

   Directory.CreateDirectory(p.StartMenuDir);
   Shortcut(Path.Combine(p.StartMenuDir,Brand.Shortcut),p.Exe,"",Brand.Tagline);
   if(o.Desktop){Directory.CreateDirectory(p.DesktopDir);Shortcut(p.DesktopShortcut,p.Exe,"",Brand.Tagline);} else if(File.Exists(p.DesktopShortcut))File.Delete(p.DesktopShortcut);
   // Автозапуск двумя независимыми способами: раздел Run и ярлык в «Автозагрузке». Explorer обрабатывает их разными путями,
   // и на части систем запись Run пропускается без объяснений. Если сработают оба — вторая копия меню тихо закроется.
   using(var run=Registry.CurrentUser.CreateSubKey(p.RunKey)) {
    if(o.Autostart)run.SetValue(Brand.RunValue,"\""+p.Exe+"\" --hidden --from=run"); else run.DeleteValue(Brand.RunValue,false);
   }
   if(o.Autostart){Directory.CreateDirectory(p.StartupDir);Shortcut(p.StartupShortcut,p.Exe,"--hidden --from=startup",Brand.Tagline+" — автозапуск");}
   else if(File.Exists(p.StartupShortcut))File.Delete(p.StartupShortcut);
   log.Add(o.Autostart?"Меню будет запускаться вместе с Windows":"Автозапуск выключен");

   // Приоритет на видеокарте: задачи Планировщика создаёт само меню с правами администратора (один запрос Windows).
   if(!p.Test&&o.Engine){
    bool had=TasksExist(),old=LegacyTasksExist(); // задачи под прежним именем указывают на старые пути — их заменяют новые
    if(o.GpuPriority&&(!had||old))log.Add(RunElevated(p.Exe,"--install-engine-tasks")&&TasksExist()&&!LegacyTasksExist()?"Приоритет записи на видеокарте включён":"Приоритет на видеокарте не включён (Windows не дала прав) — его можно включить в меню: Настройки → Система");
    else if(!o.GpuPriority&&(had||old))log.Add(RemoveTasks(p.Exe,true)?"Приоритет записи на видеокарте выключен":"Задачи приоритета не удалены");
    else if(had)log.Add("Приоритет записи на видеокарте включён");}

   using(var k=Registry.CurrentUser.CreateSubKey(p.UninstallKey)) {
    if(legacy)RemoveLegacyTraces(p);
    k.SetValue("DisplayName",Brand.Name); k.SetValue("DisplayVersion",Version); k.SetValue("Publisher","erteterya");
    k.SetValue("InstallLocation",p.InstallDir); k.SetValue("DisplayIcon",p.Exe); k.SetValue("URLInfoAbout",Brand.Repo);
    k.SetValue("UninstallString","\""+p.Uninstaller+"\" --uninstall"); k.SetValue("QuietUninstallString","\""+p.Uninstaller+"\" --uninstall --silent");
    k.SetValue("NoModify",1,RegistryValueKind.DWord); k.SetValue("NoRepair",1,RegistryValueKind.DWord);
    k.SetValue("EstimatedSize",(int)(FolderSize(p.InstallDir)/1024),RegistryValueKind.DWord);
   }
   if(o.Launch&&!p.Test)Process.Start(new ProcessStartInfo(p.Exe){WorkingDirectory=p.InstallDir,UseShellExecute=true});
   return log;
  }
  static long FolderSize(string dir){try{return new DirectoryInfo(dir).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length);}catch{return 0;}}

  // ——— Задачи Планировщика (приоритет на видеокарте) ———
  const string TaskStart=Brand.TaskStart,TaskStop=Brand.TaskStop;
  static int Schtasks(string args){
   try{using(var pr=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe"),args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){pr.StandardOutput.ReadToEnd();pr.StandardError.ReadToEnd();return pr.WaitForExit(15000)?pr.ExitCode:-1;}}
   catch{return -1;}}
  public static bool TasksExist(){return Schtasks("/query /tn \""+TaskStart+"\"")==0||Schtasks("/query /tn \""+TaskStop+"\"")==0;}
  public static bool LegacyTasksExist(){return Schtasks("/query /tn \""+Brand.Legacy.TaskStart+"\"")==0||Schtasks("/query /tn \""+Brand.Legacy.TaskStop+"\"")==0;}
  // Сначала без прав администратора; не вышло — копией меню с правами (один запрос Windows), если это разрешено.
  static bool RemoveTasks(string exe,bool mayAsk){
   foreach(string t in new[]{TaskStart,TaskStop,Brand.Legacy.TaskStart,Brand.Legacy.TaskStop})Schtasks("/delete /tn \""+t+"\" /f");
   if((TasksExist()||LegacyTasksExist())&&mayAsk&&File.Exists(exe))RunElevated(exe,"--remove-engine-tasks");
   return !TasksExist()&&!LegacyTasksExist();}
  static bool RunElevated(string exe,string arg){
   try{using(var pr=Process.Start(new ProcessStartInfo(exe,arg){UseShellExecute=true,Verb="runas",WorkingDirectory=Path.GetDirectoryName(exe)})){pr.WaitForExit(60000);return pr.HasExited&&pr.ExitCode==0;}}
   catch(System.ComponentModel.Win32Exception){return false;}}

  // Удаляет меню, движок записи, ярлыки, автозапуск, задачи Планировщика и запись в «Установленных приложениях».
  // Записи, повторы и скриншоты не трогает.
  public static void Uninstall(Paths p,bool mayAsk) {
   StopOverlay(p);
   if(!p.Test)RemoveTasks(p.Exe,mayAsk);
   using(var run=Registry.CurrentUser.OpenSubKey(p.RunKey,true))if(run!=null)run.DeleteValue(Brand.RunValue,false);
   RemoveLegacyTraces(p);
   Registry.CurrentUser.DeleteSubKeyTree(p.UninstallKey,false);
   if(Directory.Exists(p.StartMenuDir))Directory.Delete(p.StartMenuDir,true);
   if(File.Exists(p.DesktopShortcut))File.Delete(p.DesktopShortcut);
   if(File.Exists(p.StartupShortcut))File.Delete(p.StartupShortcut);
   if(!Directory.Exists(p.InstallDir))return;
   string self=Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
   foreach(string d in Directory.GetDirectories(p.InstallDir))Directory.Delete(d,true); // движок записи, журналы
   foreach(string f in Directory.GetFiles(p.InstallDir))if(!string.Equals(Path.GetFullPath(f),self,StringComparison.OrdinalIgnoreCase))File.Delete(f);
   if(self.StartsWith(Path.GetFullPath(p.InstallDir)+"\\",StringComparison.OrdinalIgnoreCase))
    // Запущенный uninstall.exe не может удалить сам себя — папку уберёт cmd после выхода.
    Process.Start(new ProcessStartInfo("cmd.exe","/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \""+p.InstallDir+"\""){CreateNoWindow=true,UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});
   else Directory.Delete(p.InstallDir,true);
  }

  static void Shortcut(string lnk,string target,string args,string description) {
   var type=Type.GetTypeFromProgID("WScript.Shell"); object shell=Activator.CreateInstance(type);
   try {
    object sc=type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{lnk}); var t=sc.GetType();
    t.InvokeMember("TargetPath",BindingFlags.SetProperty,null,sc,new object[]{target});
    t.InvokeMember("Arguments",BindingFlags.SetProperty,null,sc,new object[]{args});
    t.InvokeMember("WorkingDirectory",BindingFlags.SetProperty,null,sc,new object[]{Path.GetDirectoryName(target)});
    t.InvokeMember("Description",BindingFlags.SetProperty,null,sc,new object[]{description});
    t.InvokeMember("IconLocation",BindingFlags.SetProperty,null,sc,new object[]{target+",0"});
    t.InvokeMember("Save",BindingFlags.InvokeMethod,null,sc,null);
    Marshal.FinalReleaseComObject(sc);
   } finally { Marshal.FinalReleaseComObject(shell); }
  }
 }

 sealed class SetupWindow {
  [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int attr,ref int value,int size);
  readonly Window w; readonly Paths paths;
  T C<T>(string name) where T:FrameworkElement { return (T)w.FindName(name); }
  public SetupWindow(Paths p,bool uninstallMode) {
   paths=p;
   using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup.xaml"))w=(Window)XamlReader.Load(s);
   w.SourceInitialized+=(s,e)=>{int dark=1;DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(w).Handle,20,ref dark,4);}; // тёмный заголовок окна
   try { using(var ico=Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.png")){var img=new BitmapImage();img.BeginInit();img.StreamSource=ico;img.CacheOption=BitmapCacheOption.OnLoad;img.EndInit();C<Image>("Logo").Source=img;w.Icon=img;} } catch { }
   string installed=Installer.InstalledVersion(p);
   C<TextBlock>("VersionLabel").Text="Версия "+Installer.Version+(installed!=null?" · установлена "+installed:"");
   C<TextBlock>("PathLabel").Text=p.InstallDir;
   string engine=Installer.EngineVersion(p)??Installer.EngineVersionIn(p.LegacyEngineDir);
   C<TextBlock>("EngineLabel").Text=engine==EngineRelease.Version?"установлен (OBS "+engine+")":engine!=null?"OBS "+engine+" — будет обновлён до "+EngineRelease.Version+" (скачается "+(EngineRelease.Size>>20)+" МБ)":"скачается с GitHub OBS при установке ("+(EngineRelease.Size>>20)+" МБ)";
   if(installed!=null){C<Button>("InstallButton").Content="Обновить";C<Button>("UninstallButton").Visibility=Visibility.Visible;
    var cur=Installer.CurrentState(p);C<CheckBox>("AutostartBox").IsChecked=cur.Autostart;C<CheckBox>("DesktopBox").IsChecked=cur.Desktop;C<CheckBox>("GpuBox").IsChecked=cur.GpuPriority;} // при обновлении — как сейчас
   C<Button>("CloseButton").Click+=(s,e)=>w.Close();
   C<Button>("InstallButton").Click+=(s,e)=>Install();
   C<Button>("UninstallButton").Click+=(s,e)=>Uninstall();
   if(uninstallMode)w.Loaded+=(s,e)=>Uninstall();
  }
  public Window Window { get { return w; } }
  void Status(string text,bool error) { var l=C<TextBlock>("StatusLabel"); l.Visibility=Visibility.Visible; l.Text=text; l.Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(error?"#F59B92":"#C6E8A3"); }
  void Busy(bool on) { foreach(string n in new[]{"InstallButton","UninstallButton","CloseButton","Options"})C<FrameworkElement>(n).IsEnabled=!on; }

  async void Install() {
   var o=new Options{Autostart=C<CheckBox>("AutostartBox").IsChecked==true,Desktop=C<CheckBox>("DesktopBox").IsChecked==true,Launch=C<CheckBox>("LaunchBox").IsChecked==true,GpuPriority=C<CheckBox>("GpuBox").IsChecked==true};
   o.Progress=s=>w.Dispatcher.BeginInvoke(new Action(()=>Status(s,false)));
   Busy(true); Status("Готовлю установку…",false);
   try {
    var log=await Task.Run(()=>Installer.Install(paths,o));
    Status("Готово.\n"+string.Join("\n",log)+(o.Launch?"\nМеню открыто — дальше Alt+Z.":"\nОткройте меню через «Пуск» → "+Brand.Name+" или Alt+Z."),false);
    C<Button>("InstallButton").Visibility=Visibility.Collapsed; C<Button>("CloseButton").Content="Закрыть"; C<FrameworkElement>("CloseButton").IsEnabled=true;
   } catch(Exception ex) { Status("Не удалось установить: "+ex.Message,true); Busy(false); C<Button>("InstallButton").Content="Повторить"; }
  }
  async void Uninstall() {
   if(MessageBox.Show(w,"Удалить "+Brand.Name+"?\n\nБудут удалены меню, движок записи, ярлыки и автозапуск. Ваши записи, повторы и скриншоты останутся.",Brand.Name,MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes){if(C<Button>("InstallButton").Visibility!=Visibility.Visible)w.Close();return;}
   Busy(true); Status("Удаляю…",false);
   try { await Task.Run(()=>Installer.Uninstall(paths,true)); Status(Brand.Name+" удалена.",false); C<Button>("InstallButton").Visibility=Visibility.Collapsed; C<Button>("UninstallButton").Visibility=Visibility.Collapsed; C<Button>("CloseButton").Content="Закрыть"; C<FrameworkElement>("CloseButton").IsEnabled=true; }
   catch(Exception ex) { Status("Не удалось удалить: "+ex.Message,true); Busy(false); }
  }
 }

 static class Program {
  [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
  // Ключи: --uninstall, --silent, --test-root <папка>, --no-autostart | --autostart, --desktop | --no-desktop, --no-launch,
  // --gpu-priority | --no-gpu-priority, --engine-zip <архив> (без загрузки), --no-engine.
  // Только с --test-root: --engine-sha256 <сумма>, --engine-version <версия> (поддельный архив в проверках).
  [STAThread] static int Main(string[] args) {
   var list=args.ToList(); int at=list.IndexOf("--test-root");
   var paths=at>=0&&at+1<list.Count?Paths.ForTest(list[at+1]):Paths.Real();
   Func<string,string> arg=k=>{int i=list.IndexOf(k);return i>=0&&i+1<list.Count?list[i+1]:null;};
   bool uninstall=list.Contains("--uninstall"),silent=list.Contains("--silent")||list.Contains("/S");
   if(silent) {
    AttachConsole(-1); // вывод в консоль, из которой запущен тихий режим
    try {
     if(uninstall){Installer.Uninstall(paths,false);Console.WriteLine("removed");return 0;}
     // Обновление без ключей сохраняет то, что выбрано сейчас; ключи явно включают или выключают.
     var cur=Installer.CurrentState(paths);
     var o=new Options{Launch=!list.Contains("--no-launch"),Engine=!list.Contains("--no-engine"),EngineZip=arg("--engine-zip"),
      Autostart=list.Contains("--no-autostart")?false:list.Contains("--autostart")||!cur.Installed||cur.Autostart,
      Desktop=list.Contains("--no-desktop")?false:list.Contains("--desktop")||(cur.Installed&&cur.Desktop),
      GpuPriority=list.Contains("--no-gpu-priority")?false:list.Contains("--gpu-priority")||cur.GpuPriority,
      Progress=s=>Console.WriteLine(s)};
     if(paths.Test){o.EngineSha=arg("--engine-sha256")??o.EngineSha;o.EngineVersion=arg("--engine-version")??o.EngineVersion;}
     foreach(string line in Installer.Install(paths,o))Console.WriteLine(line);
     return 0;
    } catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
   }
   var app=new Application(); var win=new SetupWindow(paths,uninstall);
   app.Run(win.Window); return 0;
  }
 }
}
