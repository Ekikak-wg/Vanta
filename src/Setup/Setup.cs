using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
  public string InstallDir,StartMenuDir,DesktopDir,StartupDir,WebSocketConfig,RegistryRoot; public bool Test;
  public string StartupShortcut { get { return Path.Combine(StartupDir,"Replay Overlay.lnk"); } }
  public string Exe { get { return Path.Combine(InstallDir,"ReplayOverlay.exe"); } }
  public string Uninstaller { get { return Path.Combine(InstallDir,"uninstall.exe"); } }
  public string RunKey { get { return RegistryRoot+@"\Run"; } }
  public string UninstallKey { get { return RegistryRoot+@"\Uninstall\ReplayOBS"; } }
  public static Paths Real() {
   return new Paths{
    InstallDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\Replay OBS"),
    StartMenuDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Replay OBS"),
    DesktopDir=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    StartupDir=Environment.GetFolderPath(Environment.SpecialFolder.Startup),
    WebSocketConfig=ObsConfig.WebSocketConfig,
    RegistryRoot=@"Software\Microsoft\Windows\CurrentVersion"};
  }
  public static Paths ForTest(string root) {
   root=Path.GetFullPath(root);
   return new Paths{Test=true,InstallDir=Path.Combine(root,@"Programs\Replay OBS"),StartMenuDir=Path.Combine(root,@"StartMenu\Replay OBS"),DesktopDir=Path.Combine(root,"Desktop"),StartupDir=Path.Combine(root,"Startup"),
    WebSocketConfig=Path.Combine(root,@"obs-studio\plugin_config\obs-websocket\config.json"),RegistryRoot=@"Software\ReplayOBS-Test"};
  }
 }

 sealed class Options { public bool WebSocket=true,Autostart=true,Desktop,Launch=true,LaunchObs=true; }

 static class Installer {
  public static string Version { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }

  public static bool IsObsRunning() { return Process.GetProcessesByName("obs64").Length+Process.GetProcessesByName("obs32").Length>0; }
  public static string ObsVersion(out bool supported) {
   supported=false; string exe=ObsConfig.Executable(); if(exe==null)return null;
   string v=FileVersionInfo.GetVersionInfo(exe).ProductVersion??"";
   int major; supported=int.TryParse(v.Split('.')[0],out major)&&major>=30; return v.Length>0?v:"?";
  }
  // null — файла нет; иначе включён ли сервер.
  public static bool? WebSocketEnabled(Paths p,out int port) {
   port=4455; if(!File.Exists(p.WebSocketConfig))return null;
   try { var c=Json.Decode(File.ReadAllText(p.WebSocketConfig)); port=Json.Int(c,"server_port",4455); return Json.Bool(c,"server_enabled"); } catch { return null; }
  }
  public static string InstalledVersion(Paths p) {
   using(var k=Registry.CurrentUser.OpenSubKey(p.UninstallKey))return k==null?null:k.GetValue("DisplayVersion") as string;
  }

  // Включает сервер obs-websocket. Пароль не меняется, если он уже есть; новый — случайный.
  public static string EnableWebSocket(Paths p) {
   Dictionary<string,object> c;
   if(File.Exists(p.WebSocketConfig)) {
    c=Json.Decode(File.ReadAllText(p.WebSocketConfig));
    if(Json.Bool(c,"server_enabled"))return "WebSocket OBS уже включён — настройки не менялись";
    File.Copy(p.WebSocketConfig,p.WebSocketConfig+".replay-obs.bak",true);
   } else { Directory.CreateDirectory(Path.GetDirectoryName(p.WebSocketConfig)); c=new Dictionary<string,object>{{"alerts_enabled",false},{"first_load",false},{"server_port",4455}}; }
   c["server_enabled"]=true; c["auth_required"]=true;
   if(Json.Str(c,"server_password").Length==0)c["server_password"]=RandomPassword(20);
   if(!c.ContainsKey("server_port"))c["server_port"]=4455;
   string temp=p.WebSocketConfig+".tmp"; File.WriteAllText(temp,Json.Encode(c),new System.Text.UTF8Encoding(false));
   if(File.Exists(p.WebSocketConfig))File.Replace(temp,p.WebSocketConfig,null); else File.Move(temp,p.WebSocketConfig);
   return "WebSocket OBS включён (порт "+Json.Int(c,"server_port",4455)+", вход по паролю)";
  }
  static string RandomPassword(int length) {
   const string alphabet="ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"; var bytes=new byte[length];
   using(var rng=new RNGCryptoServiceProvider())rng.GetBytes(bytes);
   return new string(bytes.Select(b=>alphabet[b%alphabet.Length]).ToArray());
  }

  // Что выбрано у уже установленной версии — при обновлении это сохраняется (галочки установщика и тихий режим без ключей).
  public sealed class Current { public bool Installed,Autostart,Desktop,LaunchObs=true; }
  public static Current CurrentState(Paths p) {
   var c=new Current{Installed=InstalledVersion(p)!=null};
   if(!c.Installed)return c;
   using(var run=Registry.CurrentUser.OpenSubKey(p.RunKey))c.Autostart=File.Exists(p.StartupShortcut)||(run!=null&&run.GetValue("Replay Overlay")!=null);
   c.Desktop=File.Exists(Path.Combine(p.DesktopDir,"Replay Overlay.lnk"));
   try{string prefs=Path.Combine(p.InstallDir,"overlay-settings.json");if(File.Exists(prefs)){var cfg=Json.Decode(File.ReadAllText(prefs));if(cfg.ContainsKey("launchObs"))c.LaunchObs=Json.Bool(cfg,"launchObs");}}catch{}
   return c;
  }

  // Закрывает установленное меню (сигнал выхода, затем принудительно — только процессы из папки установки).
  public static void StopOverlay(Paths p) {
   if(p.Test)return;
   EventWaitHandle quit; if(EventWaitHandle.TryOpenExisting(Names.Exit,out quit))using(quit)quit.Set();
   for(int i=0;i<50;i++){ if(Running(p).Count==0)return; Thread.Sleep(100); }
   foreach(var proc in Running(p))try{proc.Kill();proc.WaitForExit(3000);}catch{}
  }
  static List<Process> Running(Paths p) {
   var list=new List<Process>();
   foreach(var proc in Process.GetProcessesByName("ReplayOverlay")){ try { if(string.Equals(Path.GetFullPath(proc.MainModule.FileName),Path.GetFullPath(p.Exe),StringComparison.OrdinalIgnoreCase))list.Add(proc); } catch { } }
   return list;
  }

  public static List<string> Install(Paths p,Options o) {
   var log=new List<string>();
   StopOverlay(p);
   Directory.CreateDirectory(p.InstallDir);
   using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.ReplayOverlay.exe"))
   using(var file=File.Create(p.Exe))payload.CopyTo(file);
   string self=Assembly.GetExecutingAssembly().Location;
   if(!string.Equals(Path.GetFullPath(self),Path.GetFullPath(p.Uninstaller),StringComparison.OrdinalIgnoreCase))File.Copy(self,p.Uninstaller,true);
   log.Add("Меню установлено: "+p.InstallDir);
   // Настройка меню «запускать OBS вместе с меню»; остальные его настройки при обновлении сохраняются.
   string prefs=Path.Combine(p.InstallDir,"overlay-settings.json");
   var cfg=File.Exists(prefs)?Json.Decode(File.ReadAllText(prefs)):new Dictionary<string,object>();
   cfg["launchObs"]=o.LaunchObs; File.WriteAllText(prefs,Json.Encode(cfg),new System.Text.UTF8Encoding(false));
   log.Add(o.LaunchObs?"OBS будет запускаться вместе с меню (в трее)":"OBS запускается вручную");

   Directory.CreateDirectory(p.StartMenuDir);
   Shortcut(Path.Combine(p.StartMenuDir,"Replay Overlay.lnk"),p.Exe,"","Меню повторов OBS");
   string desktop=Path.Combine(p.DesktopDir,"Replay Overlay.lnk");
   if(o.Desktop){Directory.CreateDirectory(p.DesktopDir);Shortcut(desktop,p.Exe,"","Меню повторов OBS");} else if(File.Exists(desktop))File.Delete(desktop);
   // Автозапуск двумя независимыми способами: раздел Run и ярлык в «Автозагрузке». Explorer обрабатывает их разными путями,
   // и на части систем запись Run пропускается без объяснений. Если сработают оба — вторая копия меню тихо закроется.
   using(var run=Registry.CurrentUser.CreateSubKey(p.RunKey)) {
    if(o.Autostart)run.SetValue("Replay Overlay","\""+p.Exe+"\" --hidden --from=run"); else run.DeleteValue("Replay Overlay",false);
   }
   if(o.Autostart){Directory.CreateDirectory(p.StartupDir);Shortcut(p.StartupShortcut,p.Exe,"--hidden --from=startup","Меню повторов OBS — автозапуск");}
   else if(File.Exists(p.StartupShortcut))File.Delete(p.StartupShortcut);
   log.Add(o.Autostart?"Меню будет запускаться вместе с Windows":"Автозапуск выключен");

   if(o.WebSocket)log.Add(EnableWebSocket(p));

   using(var k=Registry.CurrentUser.CreateSubKey(p.UninstallKey)) {
    k.SetValue("DisplayName","Replay OBS"); k.SetValue("DisplayVersion",Version); k.SetValue("Publisher","erteterya");
    k.SetValue("InstallLocation",p.InstallDir); k.SetValue("DisplayIcon",p.Exe); k.SetValue("URLInfoAbout","https://github.com/erteterya/Replay-OBS");
    k.SetValue("UninstallString","\""+p.Uninstaller+"\" --uninstall"); k.SetValue("QuietUninstallString","\""+p.Uninstaller+"\" --uninstall --silent");
    k.SetValue("NoModify",1,RegistryValueKind.DWord); k.SetValue("NoRepair",1,RegistryValueKind.DWord);
    k.SetValue("EstimatedSize",(int)((new FileInfo(p.Exe).Length+new FileInfo(p.Uninstaller).Length)/1024),RegistryValueKind.DWord);
   }
   if(o.Launch&&!p.Test)Process.Start(new ProcessStartInfo(p.Exe){WorkingDirectory=p.InstallDir,UseShellExecute=true});
   return log;
  }

  // Удаляет меню, ярлыки, автозапуск и запись в «Установленных приложениях». Настройки OBS не трогает.
  public static void Uninstall(Paths p) {
   StopOverlay(p);
   using(var run=Registry.CurrentUser.OpenSubKey(p.RunKey,true))if(run!=null)run.DeleteValue("Replay Overlay",false);
   Registry.CurrentUser.DeleteSubKeyTree(p.UninstallKey,false);
   if(Directory.Exists(p.StartMenuDir))Directory.Delete(p.StartMenuDir,true);
   string desktop=Path.Combine(p.DesktopDir,"Replay Overlay.lnk"); if(File.Exists(desktop))File.Delete(desktop);
   if(File.Exists(p.StartupShortcut))File.Delete(p.StartupShortcut);
   if(!Directory.Exists(p.InstallDir))return;
   string self=Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
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
   bool supported; string obs=Installer.ObsVersion(out supported);
   C<TextBlock>("ObsLabel").Text=obs==null?"не найден — установите OBS Studio 30 или новее":supported?"найден, версия "+obs:"версия "+obs+" — нужна 30 или новее";
   C<TextBlock>("ObsLabel").Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(obs!=null&&supported?"#C6E8A3":"#F2C46D");
   if(obs==null||!supported)C<Button>("ObsLink").Visibility=Visibility.Visible;
   C<Button>("ObsLink").Click+=(s,e)=>Process.Start(new ProcessStartInfo("https://obsproject.com/download"){UseShellExecute=true});
   int port; bool? socket=Installer.WebSocketEnabled(p,out port);
   C<TextBlock>("SocketLabel").Text=socket==true?"включён (порт "+port+")":socket==false?"выключен — установщик может включить":"настройка не найдена — будет создана";
   if(socket==true){C<CheckBox>("SocketBox").IsChecked=true;C<CheckBox>("SocketBox").IsEnabled=false;}
   if(installed!=null){C<Button>("InstallButton").Content="Обновить";C<Button>("UninstallButton").Visibility=Visibility.Visible;
    var cur=Installer.CurrentState(p);C<CheckBox>("AutostartBox").IsChecked=cur.Autostart;C<CheckBox>("DesktopBox").IsChecked=cur.Desktop;C<CheckBox>("ObsLaunchBox").IsChecked=cur.LaunchObs;} // при обновлении — как сейчас
   C<Button>("CloseButton").Click+=(s,e)=>w.Close();
   C<Button>("InstallButton").Click+=(s,e)=>Install();
   C<Button>("UninstallButton").Click+=(s,e)=>Uninstall();
   if(uninstallMode)w.Loaded+=(s,e)=>Uninstall();
  }
  public Window Window { get { return w; } }
  void Status(string text,bool error) { var l=C<TextBlock>("StatusLabel"); l.Visibility=Visibility.Visible; l.Text=text; l.Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(error?"#F59B92":"#C6E8A3"); }
  void Busy(bool on) { foreach(string n in new[]{"InstallButton","UninstallButton","CloseButton","Options"})C<FrameworkElement>(n).IsEnabled=!on; }

  async void Install() {
   var o=new Options{WebSocket=C<CheckBox>("SocketBox").IsChecked==true,Autostart=C<CheckBox>("AutostartBox").IsChecked==true,Desktop=C<CheckBox>("DesktopBox").IsChecked==true,Launch=C<CheckBox>("LaunchBox").IsChecked==true,LaunchObs=C<CheckBox>("ObsLaunchBox").IsChecked==true};
   int port; if(o.WebSocket&&Installer.WebSocketEnabled(paths,out port)!=true) {
    while(Installer.IsObsRunning()) {
     var r=MessageBox.Show(w,"Чтобы включить WebSocket, закройте OBS Studio и нажмите «Повторить».\n\n«Отмена» — установить без этого шага (включить можно в OBS: Сервис → Настройки WebSocket-сервера).","Replay OBS",MessageBoxButton.OKCancel,MessageBoxImage.Information);
     if(r!=MessageBoxResult.OK){o.WebSocket=false;break;}
    }
   }
   Busy(true); Status("Устанавливаю…",false);
   try {
    var log=await Task.Run(()=>Installer.Install(paths,o));
    Status("Готово.\n"+string.Join("\n",log)+(o.Launch?"\nМеню открыто — дальше Alt+Z.":"\nОткройте меню через «Пуск» → Replay Overlay или Alt+Z."),false);
    C<Button>("InstallButton").Visibility=Visibility.Collapsed; C<Button>("CloseButton").Content="Закрыть"; C<FrameworkElement>("CloseButton").IsEnabled=true;
   } catch(Exception ex) { Status("Не удалось установить: "+ex.Message,true); Busy(false); }
  }
  async void Uninstall() {
   if(MessageBox.Show(w,"Удалить Replay OBS?\n\nБудут удалены меню, ярлыки и автозапуск. Настройки OBS (профили, сцены, WebSocket) останутся как есть.","Replay OBS",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes){if(C<Button>("InstallButton").Visibility!=Visibility.Visible)w.Close();return;}
   Busy(true); Status("Удаляю…",false);
   try { await Task.Run(()=>Installer.Uninstall(paths)); Status("Replay OBS удалён.",false); C<Button>("InstallButton").Visibility=Visibility.Collapsed; C<Button>("UninstallButton").Visibility=Visibility.Collapsed; C<Button>("CloseButton").Content="Закрыть"; C<FrameworkElement>("CloseButton").IsEnabled=true; }
   catch(Exception ex) { Status("Не удалось удалить: "+ex.Message,true); Busy(false); }
  }
 }

 static class Program {
  [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
  // Ключи: --uninstall, --silent, --test-root <папка>, --no-websocket, --no-autostart, --no-obs-launch, --desktop, --no-launch.
  [STAThread] static int Main(string[] args) {
   var list=args.ToList(); int at=list.IndexOf("--test-root");
   var paths=at>=0&&at+1<list.Count?Paths.ForTest(list[at+1]):Paths.Real();
   bool uninstall=list.Contains("--uninstall"),silent=list.Contains("--silent")||list.Contains("/S");
   if(silent) {
    AttachConsole(-1); // вывод в консоль, из которой запущен тихий режим
    try {
     if(uninstall){Installer.Uninstall(paths);Console.WriteLine("removed");return 0;}
     // Обновление без ключей сохраняет то, что выбрано сейчас; ключи явно включают или выключают.
     var cur=Installer.CurrentState(paths);
     var o=new Options{WebSocket=!list.Contains("--no-websocket"),Launch=!list.Contains("--no-launch"),
      Autostart=list.Contains("--no-autostart")?false:list.Contains("--autostart")||!cur.Installed||cur.Autostart,
      Desktop=list.Contains("--no-desktop")?false:list.Contains("--desktop")||(cur.Installed&&cur.Desktop),
      LaunchObs=list.Contains("--no-obs-launch")?false:list.Contains("--obs-launch")||!cur.Installed||cur.LaunchObs};
     if(o.WebSocket&&Installer.IsObsRunning()&&!paths.Test){o.WebSocket=false;Console.WriteLine("OBS запущен — WebSocket не изменён");}
     foreach(string line in Installer.Install(paths,o))Console.WriteLine(line);
     return 0;
    } catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
   }
   var app=new Application(); var win=new SetupWindow(paths,uninstall);
   app.Run(win.Window); return 0;
  }
 }
}
