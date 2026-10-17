using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Раздел «Система»: уведомления, экспорт диагностики, сброс настроек меню.
 public sealed partial class Overlay {
  bool toastsOn=true;

  void BindSystem(){
   var tb=C<CheckBox>("ToastsBox");tb.IsChecked=toastsOn;toasts.Enabled=toastsOn;
   tb.Click+=(s,e)=>{toastsOn=tb.IsChecked==true;toasts.Enabled=toastsOn;SavePrefs();Message(toastsOn?"Уведомления включены":"Уведомления выключены — сообщения только в журнале и внизу меню");};
   var gp=C<CheckBox>("GpuPriorityBox");gp.Visibility=engine.Home.Own?Visibility.Visible:Visibility.Collapsed;
   if(engine.Home.Own){gp.IsChecked=EngineTasks.Installed();gp.Click+=(s,e)=>{bool want=gp.IsChecked==true;gp.IsChecked=!want;Run(()=>SetGpuPriority(want));};}
   C<Button>("ExportButton").Click+=(s,e)=>Run(ExportDiagnostics);
   C<Button>("ResetButton").Click+=(s,e)=>ResetSettings();}

  // Приоритет на видеокарте: задачи Планировщика создаются (или удаляются) копией меню с правами администратора,
  // затем движок перезапускается уже нужным способом. Накопленный повтор при перезапуске сбрасывается.
  // Выключение: сначала закрыть движок (копию с правами администратора закрывает только задача), потом удалить задачи.
  async Task SetGpuPriority(bool on){
   EnsureReady();engineRestarting=true;
   try{
    if(!on)await CloseEngine();
    dialogOpen=true;bool ok;
    try{ok=await Task.Run(()=>EngineTasks.RunElevated(on?EngineTasks.InstallArg:EngineTasks.RemoveArg));}finally{dialogOpen=false;window.Activate();}
    bool now=EngineTasks.Installed();C<CheckBox>("GpuPriorityBox").IsChecked=now;
    if(on&&now)await CloseEngine();
    if(!engine.IsRunning)LaunchObsHidden("смена приоритета");
    if(!ok||now!=on)throw new InvalidOperationException(on?"Приоритет не включён: Windows не дала прав администратора":"Приоритет не выключен");
    Log.Write(on?"Приоритет записи на видеокарте включён":"Приоритет записи на видеокарте выключен");}
   finally{engineRestarting=false;}
   Message(on?"Приоритет на видеокарте включён — движок перезапущен":"Приоритет на видеокарте выключен — движок перезапущен");}
  async Task CloseEngine(){engineClosedAt=DateTime.Now;engine.Close();for(int i=0;i<150&&engine.IsRunning;i++)await Task.Delay(100);}

  // Архив для диагностики: журналы меню, последние журналы OBS, настройки меню, сведения о системе. Настройки WebSocket (пароль) не берём.
  async Task ExportDiagnostics(){
   string target;dialogOpen=true;
   try{using(var d=new WF.SaveFileDialog{Title="Куда сохранить диагностику",Filter="Архив ZIP|*.zip",FileName="ReplayOBS-диагностика-"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm")+".zip",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}){if(d.ShowDialog(new Owner(handle))!=WF.DialogResult.OK)return;target=d.FileName;}}
   finally{dialogOpen=false;window.Activate();}
   var info=new StringBuilder();
   info.AppendLine("Replay OBS "+Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
   info.AppendLine("Windows "+Convert.ToString(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","CurrentBuildNumber","?"))+(Environment.Is64BitOperatingSystem?" x64":""));
   info.AppendLine("OBS: "+(engine.Connected?"подключён":"не подключён"));
   if(engine.Connected){try{info.AppendLine(await engine.Version());}catch(Exception){}}
   info.AppendLine("Профиль: "+profileName+" · режим вывода: "+outputMode+" · кодировщик: "+recEncoderId);
   info.AppendLine("Кодировщики: "+string.Join(", ",encoders.Select(x=>x.Id)));
   info.AppendLine("Размытие фона: "+(acrylic?"да":"нет")+" · сцена: "+currentScene);
   string logs=Log.Folder,settings=prefsPath,obsLogs=Path.Combine(ObsConfig.Root,"logs");string text=info.ToString();
   await Task.Run(()=>{
    if(File.Exists(target))File.Delete(target);
    using(var zip=ZipFile.Open(target,ZipArchiveMode.Create)){
     var e=zip.CreateEntry("система.txt");using(var w=new StreamWriter(e.Open(),new UTF8Encoding(true)))w.Write(text);
     if(Directory.Exists(logs))foreach(var f in Directory.GetFiles(logs,"*.log"))AddShared(zip,f,"меню/"+Path.GetFileName(f));
     if(File.Exists(settings))AddShared(zip,settings,"меню/overlay-settings.json");
     if(Directory.Exists(obsLogs))foreach(var f in new DirectoryInfo(obsLogs).GetFiles("*.txt").OrderByDescending(x=>x.LastWriteTime).Take(3))AddShared(zip,f.FullName,"obs/"+f.Name);}});
   Log.Write("Диагностика сохранена: "+Path.GetFileName(target));Message("Диагностика сохранена: "+target);
   Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+target+"\""){UseShellExecute=true});}
  // Файл может быть открыт на запись (журнал OBS) — читаем с общим доступом.
  static void AddShared(ZipArchive zip,string file,string name){
   try{using(var src=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var dst=zip.CreateEntry(name,CompressionLevel.Optimal).Open())src.CopyTo(dst);}catch(Exception){}}

  // Сброс настроек меню: резервная копия, перезапуск меню. OBS не трогаем; строгую защиту нужно сначала выключить (иначе её фильтры останутся в OBS).
  void ResetSettings(){
   if(strict!=null&&strict.On){Message("Сначала выключите строгую защиту в «Приватности» — иначе её фильтр и «шторка» останутся в движке записи");return;}
   MessageBoxResult ok;dialogOpen=true;
   try{ok=MessageBox.Show(window,"Вернуть все настройки меню к исходным?\n\nСбросятся горячие клавиши, исключения, списки игр, папки повторов и скриншотов, автоочистка и вид галереи. Настройки, сцены и источники движка записи не меняются; источники звука, созданные меню, останутся в нём.\n\nСтарые настройки сохранятся в overlay-settings.json.reset-backup. Меню перезапустится.","Replay — сброс настроек",MessageBoxButton.OKCancel,MessageBoxImage.Warning);}
   finally{dialogOpen=false;window.Activate();}
   if(ok!=MessageBoxResult.OK)return;
   try{RestoreHiddenNow();if(File.Exists(prefsPath))File.Copy(prefsPath,prefsPath+".reset-backup",true);
    resetting=true;Exit();File.Delete(prefsPath); // Exit сохранил бы настройки — удаляем после
    Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--hidden --from=reset --wait-pid="+Process.GetCurrentProcess().Id){UseShellExecute=false});}
   catch(Exception ex){Log.Write("Сброс настроек: "+ex.Message);}}
  bool resetting;
 }
}
