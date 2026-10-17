using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ReplayOverlay {
 // Движок с правами администратора без запроса при каждом запуске. С ними OBS получает повышенный приоритет на видеокарте
 // («D3D11 GPU priority» в журнале) и реже пропускает кадры, когда игра загружает видеокарту полностью.
 // Две задачи Планировщика «с наивысшими правами» создаются один раз — один запрос разрешения Windows:
 // «Engine» запускает движок, «Close engine» закрывает его (окно процесса администратора не принимает WM_CLOSE от обычных программ).
 public static class EngineTasks {
  public const string Start=@"Replay OBS\Engine",Stop=@"Replay OBS\Close engine";
  public const string InstallArg="--install-engine-tasks",RemoveArg="--remove-engine-tasks",CloseArg="--close-engine";

  public static bool Installed(){return Schtasks("/query /tn \""+Start+"\"")==0&&Schtasks("/query /tn \""+Stop+"\"")==0;}
  public static bool Run(string task){return Schtasks("/run /tn \""+task+"\"")==0;}

  // Выполняется в процессе с правами администратора (ReplayOverlay.exe --install-engine-tasks).
  public static void Install(EngineHome home,string overlayExe){
   Create(Start,home.Executable,home.HiddenArgs);
   Create(Stop,overlayExe,CloseArg);}
  public static void Remove(){Schtasks("/delete /tn \""+Start+"\" /f");Schtasks("/delete /tn \""+Stop+"\" /f");}

  static void Create(string name,string exe,string args){
   string xml=Path.Combine(Path.GetTempPath(),"replay-task-"+Guid.NewGuid().ToString("N")+".xml");
   File.WriteAllText(xml,TaskXml(WindowsIdentity.GetCurrent().User.Value,exe,args,Path.GetDirectoryName(exe)),Encoding.Unicode);
   try{int code=Schtasks("/create /tn \""+name+"\" /xml \""+xml+"\" /f");if(code!=0)throw new InvalidOperationException("Планировщик не создал задачу «"+name+"» (код "+code+")");}
   finally{try{File.Delete(xml);}catch(Exception){}}}

  // Запуск по требованию от имени текущего пользователя с наивысшими правами; без ограничения времени, без условий питания.
  public static string TaskXml(string sid,string exe,string args,string dir){
   Func<string,string> x=SecurityElement.Escape;
   return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"+
    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"+
    " <RegistrationInfo><Description>Replay OBS: движок записи с повышенным приоритетом на видеокарте</Description></RegistrationInfo>\r\n"+
    " <Principals><Principal id=\"Author\"><UserId>"+x(sid)+"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n"+
    " <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"+
    "<AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled>"+
    "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>\r\n"+
    " <Actions Context=\"Author\"><Exec><Command>"+x(exe)+"</Command><Arguments>"+x(args)+"</Arguments><WorkingDirectory>"+x(dir)+"</WorkingDirectory></Exec></Actions>\r\n"+
    "</Task>\r\n";}

  static int Schtasks(string args){
   try{using(var p=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe"),args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
    p.StandardOutput.ReadToEnd();p.StandardError.ReadToEnd();return p.WaitForExit(15000)?p.ExitCode:-1;}}
   catch(Exception){return -1;}}

  // Запустить этот же exe с правами администратора (один запрос Windows) и дождаться результата.
  public static bool RunElevated(string arg){
   try{using(var p=Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,arg){UseShellExecute=true,Verb="runas"})){p.WaitForExit(60000);return p.HasExited&&p.ExitCode==0;}}
   catch(System.ComponentModel.Win32Exception){return false;} // запрос отклонён
  }
 }
}
