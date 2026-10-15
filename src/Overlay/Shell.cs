using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Оболочка меню: вкладки, повтор, скриншоты, индикатор диска, подсказка клавиш, уведомления.
 public sealed partial class Overlay {
  ToastHost toasts;KeysPanel keysPanel;readonly DispatcherTimer diskTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};
  string currentPage="record";bool keysPanelOn=true;
  string shotDir="",shotFormat="png",shotSource="game";int shotQuality=90;
  static readonly string[] Pages={"record","replay","shot","clips","settings"};
  static readonly string[] PageNames={"RecordPage","ReplayPage","ShotPage","ClipsPage","SettingsPage"};
  static readonly string[] TabNames={"TabRecord","TabReplay","TabShot","TabGallery","TabSettings"};
  static readonly int[] DurationChips={60,180,300,600,1800};

  void BindShell(){
   toasts=new ToastHost(preview,()=>window.IsVisible?handle:Native.GetForegroundWindow());keysPanel=new KeysPanel(preview);
   for(int i=0;i<Pages.Length;i++){string p=Pages[i];C<RadioButton>(TabNames[i]).Checked+=(s,e)=>ShowPage(p);}
   C<Button>("GearButton").Click+=(s,e)=>ShowPage("settings");
   foreach(var nav in new[]{new[]{"NavAudio","AudioSection"},new[]{"NavCapture","CaptureSection"},new[]{"NavPrivacy","PrivacySection"},new[]{"NavHotkeys","HotkeySection"},new[]{"NavSystem","SystemSection"}}){string target=nav[1];C<Button>(nav[0]).Click+=(s,e)=>ScrollToSection(target);}
   // Переключатель повтора: состояние берётся только из OBS (событие ReplayBufferStateChanged), а не из нажатия.
   var rs=C<ToggleButton>("ReplaySwitch");rs.Click+=(s,e)=>{rs.IsChecked=replay;Run(async()=>{EnsureReady();await obs.Call("ToggleReplayBuffer");await Refresh();});};
   C<Button>("RecordKeyEdit").Click+=(s,e)=>{ShowPage("settings");StartCapture("record");ScrollToSection("HotkeySection");};
   C<Button>("ReplayOpenRecord").Click+=(s,e)=>ShowPage("record");
   foreach(int sec in DurationChips){int v=sec;C<RadioButton>("D"+sec).Click+=(s,e)=>C<Slider>("DurationSlider").Value=v;}
   C<Slider>("DurationSlider").ValueChanged+=(s,e)=>{int v=(int)e.NewValue;C<TextBlock>("DurationLabel").Text=FormatDuration(v);C<TextBlock>("DurationWarn").Text=v>600?"Длинный буфер хранится в оперативной памяти: при 20 Мбит/с 30 минут — около 4,5 ГБ.":"";MarkDurationChip(v);};
   C<Button>("DurationApply").Click+=(s,e)=>Run(ApplyDuration);MarkDurationChip(duration);
   var kp=C<CheckBox>("KeysPanelBox");kp.IsChecked=keysPanelOn;kp.Click+=(s,e)=>{keysPanelOn=kp.IsChecked==true;keysPanel.Enabled=keysPanelOn;if(keysPanelOn)ShowKeysPanel();else keysPanel.Hide();SavePrefs();};
   keysPanel.Enabled=keysPanelOn;
   diskTimer.Tick+=(s,e)=>UpdateDisk();diskTimer.Start();UpdateDisk();
   BindScreenshots();}

  // Переключение вкладки: окно не двигается, прокрутка — в начало.
  void ShowPage(string page){
   if(Array.IndexOf(Pages,page)<0)page="record";string before=currentPage;currentPage=page;
   for(int i=0;i<Pages.Length;i++){C<StackPanel>(PageNames[i]).Visibility=Pages[i]==page?Visibility.Visible:Visibility.Collapsed;var tab=C<RadioButton>(TabNames[i]);if(Pages[i]==page&&tab.IsChecked!=true)tab.IsChecked=true;}
   if(page!="clips")ClosePlayer();
   if(page==before&&before!=null)return;
   // Короткое появление новой вкладки: прозрачность и сдвиг на 8 px за 150 мс. Команды не ждут анимацию.
   {var sv=C<ScrollViewer>("PageScroll");var tt=sv.RenderTransform as System.Windows.Media.TranslateTransform;if(tt==null){tt=new System.Windows.Media.TranslateTransform();sv.RenderTransform=tt;}
    sv.BeginAnimation(UIElement.OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(0.25,1,TimeSpan.FromMilliseconds(150)));
    tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,new System.Windows.Media.Animation.DoubleAnimation(8,0,TimeSpan.FromMilliseconds(150)){EasingFunction=new System.Windows.Media.Animation.CubicEase{EasingMode=System.Windows.Media.Animation.EasingMode.EaseOut}});}
   C<ScrollViewer>("PageScroll").ScrollToTop();
   if(page=="record")Run(RenderMixer);
   if(page=="settings"){UpdateStorageUi();RenderExcluded();FillApps();RenderHotkeys();RenderGames();RenderAudioApps();FillAudioApps();Run(FillDevices);}
   if(page=="clips")Run(LoadClips);
   UpdateDisk();}
  void ScrollToSection(string name){window.Dispatcher.BeginInvoke(new Action(()=>{var el=C<FrameworkElement>(name);var sv=C<ScrollViewer>("PageScroll");var p=el.TranslatePoint(new Point(0,0),(UIElement)sv.Content);sv.ScrollToVerticalOffset(p.Y);}),DispatcherPriority.Loaded);}

  void MarkDurationChip(int seconds){foreach(int sec in DurationChips)C<RadioButton>("D"+sec).IsChecked=sec==seconds;}
  static string DurationWords(int s){if(s%60==0){int m=s/60;return m+" "+(m%10==1&&m%100!=11?"минуту":m%10>=2&&m%10<=4&&(m%100<12||m%100>14)?"минуты":"минут");}if(s<60)return s+" с";return (s/60)+" мин "+(s%60)+" с";}

  // Длительность буфера: OBS читает её при запуске буфера, поэтому буфер перезапускается.
  async Task ApplyDuration(){EnsureReady();int seconds=(int)C<Slider>("DurationSlider").Value;if(seconds==duration){Message("Длительность уже "+FormatDuration(seconds));return;}
   bool resume=replayAvailable&&Json.Bool(await obs.Call("GetReplayBufferStatus"),"outputActive");busy=true;UpdateControls();
   try{if(resume){await obs.Call("StopReplayBuffer");await WaitBuffer(false);}
    await obs.Call("SetProfileParameter",Json.D("parameterCategory",OutputSection,"parameterName","RecRBTime","parameterValue",seconds.ToString()));
    if(resume){await obs.Call("StartReplayBuffer");await WaitBuffer(true);}}
   finally{busy=false;UpdateControls();}
   duration=seconds;Log.Write("Длительность повтора: "+FormatDuration(seconds));Message("Длительность повтора: "+FormatDuration(seconds)+(resume?" · буфер запущен заново":""));await Refresh();}

  // ——— Индикатор диска ———
  void UpdateDisk(){
   string folder=currentPage=="shot"?ShotDir:ClipsDir;var d=DiskInfo.For(folder);
   C<TextBlock>("DiskName").Text=d.Name;var bar=C<Border>("DiskBar");var track=C<Grid>("DiskTrack");
   if(!d.Available){C<TextBlock>("DiskFree").Text=string.IsNullOrEmpty(folder)?"папка неизвестна":"Диск недоступен";bar.Width=0;C<Grid>("DiskRow").ToolTip=null;return;}
   C<TextBlock>("DiskFree").Text=DiskInfo.Gb(d.Free)+" свободно";bar.Background=Brush(d.BarColor);
   bar.Width=Math.Max(0,(track.ActualWidth>0?track.ActualWidth:200)*d.UsedFraction);
   C<Grid>("DiskRow").ToolTip="Занято: "+DiskInfo.Gb(d.Total-d.Free)+" / "+DiskInfo.Gb(d.Total)+" · "+folder;
   if(d.Free<(long)lowDiskGb*1073741824L)Warn("disk-"+d.Name,"Недостаточно места на диске: "+d.Name+" — "+DiskInfo.Gb(d.Free)+" свободно");}

  // Меню по сочетанию открывается, только когда клавиши отпущены. Иначе отпускание Alt получает уже меню,
  // а игра (например, Warframe) так и считает Alt зажатым — и держит курсор поверх игры даже после закрытия меню.
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
  DispatcherTimer keyWait;
  void ShowAfterKeysReleased(){
   if(keyWait!=null&&keyWait.IsEnabled)return;Hotkey k=HotkeyFor("menu");var started=DateTime.Now;
   keyWait=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
   keyWait.Tick+=(s,e)=>{bool held=new[]{0x10,0x11,0x12,0x5B,0x5C}.Any(vk=>(GetAsyncKeyState(vk)&0x8000)!=0)||(k!=null&&(GetAsyncKeyState((int)k.VirtualKey)&0x8000)!=0);
    if(held&&(DateTime.Now-started).TotalMilliseconds<1500)return;keyWait.Stop();Show();};
   keyWait.Start();}

  // ——— Подсказка клавиш справа снизу ———
  void ShowKeysPanel(){if(!keysPanelOn||!window.IsVisible)return;
   var list=new List<KeyValuePair<string,string>>();
   foreach(var a in new[]{new[]{"menu","Открыть оверлей"},new[]{"save","Сохранить повтор"},new[]{"record","Начать/остановить запись"},new[]{"shot","Сделать скриншот"}}){Hotkey k=HotkeyFor(a[0]);bool b;keyBusy.TryGetValue(a[0],out b);if(k!=null&&!b)list.Add(new KeyValuePair<string,string>(k.ToString(),a[1]));}
   try{keysPanel.Show(handle,list);}catch(Exception ex){Log.Write("Подсказка клавиш: "+ex.Message);}}

  void Notify(string title,string detail){Notify(title,detail,ToastKind.Info);}
  void Notify(string title,string detail,ToastKind kind){if(exiting||toasts==null)return;try{toasts.Show(kind,title,detail);}catch(Exception ex){Log.Write("Уведомление: "+ex.Message);}}

  // ——— Скриншоты ———
  string ShotDir{get{return shotDir.Length>0?shotDir:ClipsDir.Length>0?Path.Combine(ClipsDir,"Скриншоты"):"";}}
  void BindScreenshots(){
   C<Button>("ShotButton").Click+=(s,e)=>Run(TakeScreenshot);
   var png=C<RadioButton>("ShotPng");var jpg=C<RadioButton>("ShotJpeg");png.IsChecked=shotFormat!="jpeg";jpg.IsChecked=shotFormat=="jpeg";
   RoutedEventHandler fmt=(s,e)=>{shotFormat=jpg.IsChecked==true?"jpeg":"png";UpdateShotUi();SavePrefs();};png.Click+=fmt;jpg.Click+=fmt;
   var q=C<Slider>("ShotQuality");q.Value=shotQuality;q.ValueChanged+=(s,e)=>{shotQuality=(int)e.NewValue;C<TextBlock>("ShotQualityLabel").Text=shotQuality.ToString();};q.PreviewMouseUp+=(s,e)=>SavePrefs();
   var src=C<ComboBox>("ShotSourceBox");SelectTag(src,shotSource);src.SelectionChanged+=(s,e)=>{var it=src.SelectedItem as ComboBoxItem;if(it==null)return;shotSource=Convert.ToString(it.Tag);UpdateShotUi();SavePrefs();};
   C<Button>("ShotFolderPick").Click+=(s,e)=>PickShotFolder();
   C<Button>("ShotOpenFolder").Click+=(s,e)=>Run(async()=>{string d=ShotDir;if(d.Length==0)throw new InvalidOperationException("Папка пока неизвестна: подключитесь к OBS");Directory.CreateDirectory(d);Process.Start(new ProcessStartInfo("explorer.exe","\""+d+"\""){UseShellExecute=true});await Task.FromResult(0);});
   UpdateShotUi();}
  void UpdateShotUi(){bool jpeg=shotFormat=="jpeg";C<TextBlock>("ShotPngHint").Visibility=jpeg?Visibility.Collapsed:Visibility.Visible;C<StackPanel>("ShotJpegPanel").Visibility=jpeg?Visibility.Visible:Visibility.Collapsed;C<TextBlock>("ShotQualityLabel").Text=shotQuality.ToString();
   C<TextBlock>("ShotFolderLabel").Text=ShotDir.Length>0?ShotDir:"—";C<TextBlock>("ShotFolderLabel").ToolTip=ShotDir;
   C<TextBlock>("ShotSourceHint").Text=shotSource=="game"?"Снимок окна игры через захват игры OBS — без меню, уведомлений и других окон.":shotSource=="scene"?"Снимок того, что сейчас записывается: текущая сцена OBS целиком.":"Снимок всего монитора через захват экрана OBS. Окна из исключений в него попадут, если они видны.";
   Hotkey k=HotkeyFor("shot");C<TextBlock>("ShotKeyHint").Text=k!=null?k.ToString():"Сочетание не задано";}
  void PickShotFolder(){string chosen;dialogOpen=true;
   try{using(var d=new WF.FolderBrowserDialog{Description="Куда сохранять скриншоты",ShowNewFolderButton=true,SelectedPath=ShotDir}){if(d.ShowDialog(new Owner(handle))!=WF.DialogResult.OK)return;chosen=d.SelectedPath;}}
   finally{dialogOpen=false;window.Activate();}
   shotDir=chosen;SavePrefs();UpdateShotUi();UpdateDisk();Message("Папка скриншотов: "+chosen);}

  // Источник снимка ищется в OBS заново каждый раз: имена источников могут меняться.
  async Task<string> ShotSourceName(){
   if(shotSource=="scene"){if(string.IsNullOrEmpty(currentScene))throw new InvalidOperationException("Сцена OBS неизвестна");return currentScene;}
   string kind=shotSource=="screen"?"monitor_capture":"game_capture";
   if(shotSource=="game"&&gamesWatch.Current==null)throw new InvalidOperationException("Игра не обнаружена — выберите «Весь экран» или «То, что записывается»");
   if(!string.IsNullOrEmpty(currentScene))foreach(object raw in await Items(currentScene)){var it=Json.Obj(raw);if(Json.Str(it,"inputKind")==kind)return Json.Str(it,"sourceName");}
   object list;(await obs.Call("GetInputList",Json.D("inputKind",kind))).TryGetValue("inputs",out list);
   var first=((list as object[])??new object[0]).Select(o=>Json.Str(Json.Obj(o),"inputName")).FirstOrDefault();
   if(first==null)throw new InvalidOperationException(shotSource=="screen"?"В OBS нет захвата экрана":"В OBS нет захвата игры");
   return first;}
  async Task TakeScreenshot(){
   EnsureReady();string dir=ShotDir;if(dir.Length==0)throw new InvalidOperationException("Папка скриншотов неизвестна");Directory.CreateDirectory(dir);
   string source=await ShotSourceName();bool jpeg=shotFormat=="jpeg";var g=gamesWatch.Current;
   string name=(g!=null?g.Exe:"Screenshot")+"_"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");foreach(char c in Path.GetInvalidFileNameChars())name=name.Replace(c,'_');
   string file=Path.Combine(dir,name+(jpeg?".jpg":".png"));for(int i=2;File.Exists(file);i++)file=Path.Combine(dir,name+"_"+i+(jpeg?".jpg":".png"));
   var req=Json.D("sourceName",source,"imageFormat",jpeg?"jpg":"png","imageFilePath",file);if(jpeg)req["imageCompressionQuality"]=shotQuality;
   await obs.Call("SaveSourceScreenshot",req);
   if(!File.Exists(file))throw new IOException("OBS не сохранил снимок");
   Log.Write("Скриншот: "+Path.GetFileName(file)+" ("+source+")");Message("Скриншот: "+Path.GetFileName(file));
   Track(file);Notify("Скриншот сохранён",Path.GetFileName(file),ToastKind.Shot);ClipsChanged();}
 }
}
