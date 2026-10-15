using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Запущенная программа для окна выбора: имя exe, путь, описание, заголовок окна, есть ли звук.
 public sealed class AppEntry {
  public string Exe,Path,Title,Description;public bool HasWindow,HasSound;public ImageSource Icon;
  public string DisplayName{get{return !string.IsNullOrEmpty(Description)?Description:!string.IsNullOrEmpty(Title)?Title:Exe;}}
  // Поиск: по имени exe, описанию и заголовку, без учёта регистра.
  public bool Matches(string q){if(string.IsNullOrWhiteSpace(q))return true;q=q.Trim();return (Exe??"").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0||(Description??"").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0||(Title??"").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0;}
 }

 public static class AppCatalog {
  delegate bool EnumProc(IntPtr h,IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f,IntPtr p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h,uint cmd);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int i);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int a,out int v,int size);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h,int flags,StringBuilder s,ref int size);
  [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
  static string ExePath(uint pid){IntPtr h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero)return "";try{var s=new StringBuilder(1024);int n=s.Capacity;return QueryFullProcessImageName(h,0,s,ref n)?s.ToString():"";}finally{CloseHandle(h);}}
  // Служебные процессы Windows, которые не имеет смысла предлагать.
  static readonly HashSet<string> Skip=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"explorer","ApplicationFrameHost","TextInputHost","ShellExperienceHost","ShellHost","StartMenuExperienceHost","SearchHost","SystemSettings","LockApp","dwm","ReplayOverlay","obs64","audiodg","svchost","csrss"};

  // Программы с окнами и программы, которые сейчас выводят звук. Иконки — из exe (замороженные, можно отдавать в окно).
  public static List<AppEntry> Running(){
   var byExe=new Dictionary<string,AppEntry>(StringComparer.OrdinalIgnoreCase);uint self=(uint)Process.GetCurrentProcess().Id;
   EnumWindows((h,p)=>{
    if(!IsWindowVisible(h)||GetWindow(h,4)!=IntPtr.Zero||(GetWindowLong(h,-20)&0x80)!=0)return true;
    int cloaked;if(DwmGetWindowAttribute(h,14,out cloaked,4)==0&&cloaked!=0)return true;
    var t=new StringBuilder(256);if(GetWindowText(h,t,256)==0)return true;
    uint pid;GetWindowThreadProcessId(h,out pid);if(pid==self)return true;
    string path=ExePath(pid);if(path.Length==0)return true;string exe=System.IO.Path.GetFileNameWithoutExtension(path);
    if(Skip.Contains(exe)||byExe.ContainsKey(exe))return true;
    byExe[exe]=new AppEntry{Exe=exe,Path=path,Title=t.ToString(),HasWindow=true};return true;},IntPtr.Zero);
   foreach(var kv in AudioSessions.Active()){if(Skip.Contains(kv.Key))continue;AppEntry e;if(byExe.TryGetValue(kv.Key,out e))e.HasSound=true;else byExe[kv.Key]=new AppEntry{Exe=kv.Key,Path=kv.Value,HasSound=true};}
   foreach(var e in byExe.Values){
    try{var v=FileVersionInfo.GetVersionInfo(e.Path);string d=(v.FileDescription??"").Trim();if(d.Length==0)d=(v.ProductName??"").Trim();e.Description=d;}catch(Exception){}
    e.Icon=IconOf(e.Path);}
   return byExe.Values.OrderByDescending(e=>e.HasSound).ThenBy(e=>e.DisplayName,StringComparer.CurrentCultureIgnoreCase).ToList();}

  public static ImageSource IconOf(string path){
   try{using(var ic=System.Drawing.Icon.ExtractAssociatedIcon(path)){if(ic==null)return null;var src=Imaging.CreateBitmapSourceFromHIcon(ic.Handle,Int32Rect.Empty,BitmapSizeOptions.FromWidthAndHeight(32,32));src.Freeze();return src;}}
   catch(Exception){return null;}}
 }

 public sealed partial class Overlay {
  Action<string> pickerDone;ICollection<string> pickerSkip;List<AppEntry> pickerApps=new List<AppEntry>();bool pickerLoading;
  bool PickerOpen{get{return C<Border>("PickerLayer").Visibility==Visibility.Visible;}}

  void BindPicker(){
   C<Button>("PickerCancel").Click+=(s,e)=>ClosePicker();
   C<Button>("PickerRefresh").Click+=(s,e)=>Run(LoadPickerApps);
   C<Button>("PickerBrowse").Click+=(s,e)=>PickExe();
   C<TextBox>("PickerSearch").TextChanged+=(s,e)=>RenderPicker();
   C<Border>("PickerLayer").MouseLeftButtonDown+=(s,e)=>{if(e.OriginalSource==C<Border>("PickerLayer"))ClosePicker();};}

  // Окно выбора поверх меню: заголовок, что уже добавлено (не показываем), что сделать с выбранным exe.
  void OpenPicker(string title,string hint,ICollection<string> skip,Action<string> done){
   pickerDone=done;pickerSkip=skip;C<TextBlock>("PickerTitle").Text=title;C<TextBlock>("PickerHint").Text=hint;C<TextBox>("PickerSearch").Text="";
   C<Border>("PickerLayer").Visibility=Visibility.Visible;C<TextBox>("PickerSearch").Focus();Run(LoadPickerApps);}
  void ClosePicker(){C<Border>("PickerLayer").Visibility=Visibility.Collapsed;pickerDone=null;C<StackPanel>("PickerList").Children.Clear();}
  async Task LoadPickerApps(){
   if(pickerLoading)return;pickerLoading=true;C<TextBlock>("PickerStatus").Text="Ищу запущенные программы…";
   try{pickerApps=await Task.Run(()=>AppCatalog.Running());}finally{pickerLoading=false;}
   RenderPicker();}
  void RenderPicker(){
   var list=C<StackPanel>("PickerList");list.Children.Clear();string q=C<TextBox>("PickerSearch").Text;
   var shown=pickerApps.Where(a=>pickerSkip==null||!pickerSkip.Contains(a.Exe,StringComparer.OrdinalIgnoreCase)).Where(a=>a.Matches(q)).ToList();
   C<TextBlock>("PickerStatus").Text=pickerLoading?"Ищу запущенные программы…":shown.Count==0?(pickerApps.Count==0?"Запущенных программ не найдено — укажите exe-файл вручную.":"Ничего не найдено"):"";
   foreach(var a in shown){var app=a;
    var g=new Grid();g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(40)});g.ColumnDefinitions.Add(new ColumnDefinition());g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    g.Children.Add(app.Icon!=null?(UIElement)new Image{Source=app.Icon,Width=28,Height=28,VerticalAlignment=VerticalAlignment.Center}:new TextBlock{Text="",FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),FontSize=20,Foreground=Brush("#9CA3AF"),VerticalAlignment=VerticalAlignment.Center});
    var texts=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,8,0)};
    texts.Children.Add(new TextBlock{Text=app.DisplayName,FontSize=13,Foreground=Brush("#F5F6F7"),TextTrimming=TextTrimming.CharacterEllipsis});
    texts.Children.Add(new TextBlock{Text=app.Exe+".exe"+(!string.IsNullOrEmpty(app.Title)&&app.Title!=app.DisplayName?" · "+app.Title:""),FontSize=11,Foreground=Brush("#8E99A4"),TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,2,0,0)});
    Grid.SetColumn(texts,1);g.Children.Add(texts);
    if(app.HasSound){var badge=new Border{Background=Brush("#1F00D46A"),CornerRadius=new CornerRadius(5),Padding=new Thickness(7,2,7,2),VerticalAlignment=VerticalAlignment.Center,Child=new TextBlock{Text="звук",FontSize=11,Foreground=Brush("#7DF0B0")}};Grid.SetColumn(badge,2);g.Children.Add(badge);}
    var row=new Border{Padding=new Thickness(8,7,8,7),CornerRadius=new CornerRadius(8),Background=Brushes.Transparent,Cursor=Cursors.Hand,Child=g,ToolTip=app.Path};
    row.MouseEnter+=(s,e)=>row.Background=Brush("#252A32");row.MouseLeave+=(s,e)=>row.Background=Brushes.Transparent;
    row.MouseLeftButtonUp+=(s,e)=>Choose(app.Exe);list.Children.Add(row);}}
  void Choose(string exe){var done=pickerDone;ClosePicker();if(done!=null&&!string.IsNullOrEmpty(exe))done(exe);}
  // Программа не запущена — указываем её exe-файл.
  void PickExe(){
   string chosen=null;dialogOpen=true;
   try{using(var d=new WF.OpenFileDialog{Title="Выберите exe-файл программы",Filter="Программы (*.exe)|*.exe",CheckFileExists=true}){if(d.ShowDialog(new Owner(handle))==WF.DialogResult.OK)chosen=d.FileName;}}
   finally{dialogOpen=false;window.Activate();}
   if(chosen!=null)Choose(Path.GetFileNameWithoutExtension(chosen));}
 }
}
