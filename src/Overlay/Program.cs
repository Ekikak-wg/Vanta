using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Ellipse=System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 static class Native {
  [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h,int id);
  [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h,uint affinity);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
 }
 sealed class Owner : WF.IWin32Window { readonly IntPtr h; public Owner(IntPtr handle){h=handle;} public IntPtr Handle{get{return h;}} }
 public sealed class Overlay {
  readonly ObsClient obs=new ObsClient(); readonly Window window; readonly WF.NotifyIcon tray;
  readonly bool preview; readonly DispatcherTimer clock=new DispatcherTimer();
  IntPtr handle,previousForeground; bool dialogOpen,exiting,loading,busy,refreshing,replay,record,micMuted,correctProfile,preferAlt=true,replayAvailable=true,encoderEditable;
  string profileName="",outputMode="Advanced",micName="",encoderPath="";
  // Действия с глобальными сочетаниями: ключ, подпись, сочетание по умолчанию (пусто — без сочетания).
  static readonly string[][] Actions={new[]{"menu","Открыть / закрыть меню","Alt+Z"},new[]{"save","Сохранить повтор","Ctrl+Alt+F10"},new[]{"replay","Включить / выключить повтор",""},new[]{"record","Начать / остановить запись",""},new[]{"modeGame","Режим «Только игра»",""},new[]{"modeDesktop","Режим «Игра и экран»",""},new[]{"mic","Микрофон вкл / выкл",""}};
  readonly Dictionary<string,Hotkey> keys=new Dictionary<string,Hotkey>();readonly Dictionary<string,bool> keyBusy=new Dictionary<string,bool>();
  List<KeyValuePair<Hotkey,string>> obsKeys=new List<KeyValuePair<Hotkey,string>>();string capturing;
  int duration=300; string directory="",currentScene="",lastSaved=""; DateTime recordStart;
  readonly string prefsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"overlay-settings.json");
  Window toast; DispatcherTimer toastTimer;
  readonly PrivacyWatcher privacy=new PrivacyWatcher(); readonly HashSet<string> excluded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  readonly List<string> hidden=new List<string>(); // «сцена\nid» элементов захвата монитора, выключенных оверлеем
  readonly Dictionary<string,object[]> sceneItems=new Dictionary<string,object[]>(); string gameScene,desktopScene; bool privacyRunning,privacyAgain;
  public Overlay(bool previewMode) {
   preview=previewMode;
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Overlay.xaml"))window=(Window)XamlReader.Load(stream);
   if(preview)window.ShowInTaskbar=true;
   LoadPrefs();privacy.SetExcluded(excluded);
   window.SourceInitialized+=(s,e)=>InitWindow();
   window.Closing+=(s,e)=>{if(!exiting){e.Cancel=true;Hide();}};
   window.IsVisibleChanged+=(s,e)=>{if(window.IsVisible){clock.Start();Run(Refresh);}else{clock.Stop();if(capturing!=null){capturing=null;ConfigureHotkeys();}}};
   window.PreviewKeyDown+=(s,e)=>{if(capturing!=null){CaptureKey(e);return;}if(e.Key==Key.Escape){Hide();e.Handled=true;}};
   window.Deactivated+=(s,e)=>{if(!preview&&!dialogOpen&&!exiting&&window.IsVisible){previousForeground=IntPtr.Zero;window.Hide();}};
   C<Button>("CloseButton").Click+=(s,e)=>Hide();
   C<Button>("FolderPickButton").Click+=(s,e)=>Run(PickFolder);
   C<Button>("SettingsButton").Click+=(s,e)=>{SettingsPage(true);RenderExcluded();FillApps();RenderHotkeys();};
   C<Button>("ModeGameButton").Click+=(s,e)=>Run(()=>SetMode(false));
   C<Button>("ModeDesktopButton").Click+=(s,e)=>Run(()=>SetMode(true));
   C<ComboBox>("AppBox").DropDownOpened+=(s,e)=>FillApps();
   C<Button>("AddAppButton").Click+=(s,e)=>{var item=C<ComboBox>("AppBox").SelectedItem as ComboBoxItem;if(item==null){Message("Выберите запущенное приложение");return;}excluded.Add(Convert.ToString(item.Tag));ExclusionsChanged();Message(item.Tag+".exe не будет попадать в запись в режиме «Игра и экран»");};
   privacy.Changed+=names=>ApplyPrivacy();
   C<Button>("BackButton").Click+=(s,e)=>SettingsPage(false);
   C<Button>("ReplayButton").Click+=(s,e)=>Run(async()=>{EnsureReady();await obs.Call("ToggleReplayBuffer");await Refresh();});
   C<Button>("RecordButton").Click+=(s,e)=>Run(async()=>{EnsureReady();await obs.Call("ToggleRecord");await Refresh();});
   C<Button>("SaveButton").Click+=(s,e)=>Run(SaveReplay);
   C<Button>("MicButton").Click+=(s,e)=>Run(async()=>{EnsureReady();if(micName.Length==0)throw new InvalidOperationException("В OBS не выбран микрофон: Настройки → Аудио");await obs.Call("ToggleInputMute",Json.D("inputName",micName));await Refresh();});
   C<Button>("LaunchButton").Click+=(s,e)=>Run(LaunchObs);
   C<Button>("FolderButton").Click+=(s,e)=>Run(async()=>{if(directory.Length==0)throw new InvalidOperationException("Папка записей пока неизвестна: подключитесь к OBS");if(!Directory.Exists(directory))throw new DirectoryNotFoundException("Папка записей пока не создана");Process.Start(new ProcessStartInfo("explorer.exe","\""+directory.Replace("\"","")+"\""){UseShellExecute=true});await Task.FromResult(0);});
   C<Button>("ApplyButton").Click+=(s,e)=>Run(Apply);
   C<Button>("HotkeyButton").Click+=(s,e)=>{SettingsPage(true);RenderExcluded();FillApps();RenderHotkeys();window.Dispatcher.BeginInvoke(new Action(()=>C<StackPanel>("HotkeySection").BringIntoView()),DispatcherPriority.Loaded);};
   C<Slider>("DurationSlider").ValueChanged+=(s,e)=>C<TextBlock>("DurationLabel").Text=FormatDuration((int)e.NewValue);
   C<Button>("D30").Click+=(s,e)=>C<Slider>("DurationSlider").Value=30;
   C<Button>("D60").Click+=(s,e)=>C<Slider>("DurationSlider").Value=60;
   C<Button>("D180").Click+=(s,e)=>C<Slider>("DurationSlider").Value=180;
   C<Button>("D300").Click+=(s,e)=>C<Slider>("DurationSlider").Value=300;
   C<ComboBox>("SceneBox").SelectionChanged+=(s,e)=>{if(!loading&&C<ComboBox>("SceneBox").SelectedItem!=null)Run(async()=>{EnsureReady();await obs.Call("SetCurrentProgramScene",Json.D("sceneName",Convert.ToString(C<ComboBox>("SceneBox").SelectedItem)));});};
   clock.Interval=TimeSpan.FromSeconds(1);clock.Tick+=(s,e)=>{if(record)C<TextBlock>("RecordState").Text=(DateTime.Now-recordStart).ToString(@"hh\:mm\:ss");};
   tray=new WF.NotifyIcon{Icon=AppIcon(),Visible=true,Text="Replay Overlay — OBS"};
   var menu=new WF.ContextMenuStrip();menu.Items.Add("Открыть меню",null,(s,e)=>Show());menu.Items.Add("Сохранить повтор",null,(s,e)=>Run(SaveReplay));menu.Items.Add("Открыть OBS",null,(s,e)=>Run(LaunchObs));menu.Items.Add(new WF.ToolStripSeparator());menu.Items.Add("Выйти из оверлея",null,(s,e)=>Exit());tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>Show();
   obs.ConnectionChanged+=connected=>Ui(()=>{C<TextBlock>("ConnectionLabel").Text=connected?"OBS подключён":"Нет связи с OBS";C<Ellipse>("ConnectionDot").Fill=Brush(connected?"#8DCC38":"#D6A45B");UpdateControls();privacy.Enabled=connected&&excluded.Count>0;if(connected){sceneItems.Clear();Run(Refresh);}});
   obs.Status+=message=>Ui(()=>Message(message));
   obs.EventReceived+=(type,data)=>Ui(()=>OnEvent(type,data));
   window.Loaded+=(s,e)=>Position();
   UpdateControls();
  }
  public void StartHidden(){new WindowInteropHelper(window).EnsureHandle();string shortcut=C<TextBlock>("ShortcutLabel").Text;Notify("Replay Overlay запущен",shortcut.Length>0?shortcut+" — открыть меню":"Меню — через значок в трее");}
  T C<T>(string name) where T:FrameworkElement{return (T)window.FindName(name);}
  void SettingsPage(bool open){C<StackPanel>("MainPage").Visibility=open?Visibility.Collapsed:Visibility.Visible;C<StackPanel>("SettingsPage").Visibility=open?Visibility.Visible:Visibility.Collapsed;C<ScrollViewer>("PageScroll").ScrollToTop();}
  static Brush Brush(string color){return (Brush)new BrushConverter().ConvertFromString(color);}
  static string FormatDuration(int seconds){return (seconds/60)+":"+(seconds%60).ToString("00");}
  void Ui(Action action){if(!exiting)window.Dispatcher.BeginInvoke(action);}
  void Message(string text){C<TextBlock>("MessageLabel").Text=text;C<TextBlock>("MessageLabel").ToolTip=text;}
  void EnsureReady(){if(!obs.Connected)throw new InvalidOperationException("Запустите OBS и дождитесь подключения");if(!correctProfile)throw new InvalidOperationException("Подождите: меню получает настройки OBS");}
  async void Run(Func<Task> action){try{await action();}catch(Exception ex){Message(ex.Message);if(!window.IsVisible)Notify("Действие не выполнено",ex.Message);}}
  void InitWindow(){handle=new WindowInteropHelper(window).Handle;HwndSource.FromHwnd(handle).AddHook(Hook);if(!preview)Native.SetWindowDisplayAffinity(handle,0x11);ConfigureHotkeys();obs.Start();}
  void UnregisterHotkeys(){for(int i=0;i<Actions.Length;i++)Native.UnregisterHotKey(handle,100+i);}
  void ConfigureHotkeys(){if(handle==IntPtr.Zero)return;UnregisterHotkeys();keyBusy.Clear();
   for(int i=0;i<Actions.Length;i++){Hotkey k;if(keys.TryGetValue(Actions[i][0],out k)&&k!=null)keyBusy[Actions[i][0]]=!Native.RegisterHotKey(handle,100+i,k.Win32Modifiers,k.VirtualKey);}
   Hotkey menu=HotkeyFor("menu");bool menuOk=menu!=null&&!keyBusy["menu"];C<TextBlock>("ShortcutLabel").Text=menuOk?menu.ToString():"";C<TextBlock>("ShortcutLabel").ToolTip=menuOk?"Открыть / закрыть меню":null;
   if(!menuOk)Message(menu==null?"Сочетание для меню не задано — открывайте его через значок в трее":"Сочетание "+menu+" занято другой программой — выберите другое в настройках");UpdateControls();}
  Hotkey HotkeyFor(string action){Hotkey k;return keys.TryGetValue(action,out k)?k:null;}
  IntPtr Hook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled){if(msg==0x0312){int i=wParam.ToInt32()-100;if(i>=0&&i<Actions.Length){DoAction(Actions[i][0]);handled=true;}}return IntPtr.Zero;}
  // Глобальное сочетание сработало. Если меню скрыто — результат показываем всплывающим уведомлением.
  void DoAction(string action){
   if(action=="menu"){if(window.IsVisible)Hide();else Show();return;}
   if(action=="save"){Run(SaveReplay);return;}
   Run(async()=>{EnsureReady();string title,text;
    if(action=="replay"){var r=await obs.Call("ToggleReplayBuffer");title="Мгновенный повтор";text=Json.Bool(r,"outputActive")?"Включён":"Выключен";}
    else if(action=="record"){var r=await obs.Call("ToggleRecord");title="Запись";text=Json.Bool(r,"outputActive")?"Начата":"Остановлена";}
    else if(action=="mic"){if(micName.Length==0)throw new InvalidOperationException("В OBS не выбран микрофон");var r=await obs.Call("ToggleInputMute",Json.D("inputName",micName));title="Микрофон";text=Json.Bool(r,"inputMuted")?"Выключен":"Включён";}
    else{bool desktop=action=="modeDesktop";await SetMode(desktop);title="Что попадает в запись";text=desktop?"Игра и экран":"Только игра";}
    if(!window.IsVisible)Notify(title,text);else Message(title+": "+text.ToLower());await Refresh();});}
  void RenderHotkeys(){var list=C<StackPanel>("HotkeyList");list.Children.Clear();
   try{obsKeys=ObsHotkeys.Read(Path.Combine(ObsConfig.ProfileDirectory(profileName),"basic.ini"),ObsConfig.SceneCollectionFile());}catch{}
   foreach(var a in Actions){string action=a[0];Hotkey k=HotkeyFor(action);bool busyKey;keyBusy.TryGetValue(action,out busyKey);
    var row=new Grid{Margin=new Thickness(0,0,0,8)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(6)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(128)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(4)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    var label=new StackPanel{VerticalAlignment=VerticalAlignment.Center};label.Children.Add(new TextBlock{Text=a[1],FontSize=13,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#E6EBDD")});
    string warn=null;if(k!=null&&busyKey)warn="занято другой программой";else if(k!=null){var same=obsKeys.Where(o=>o.Key.Same(k)).Select(o=>o.Value).ToList();if(same.Count>0)warn="то же в OBS: "+string.Join(", ",same)+" — сработают оба";}
    if(warn!=null)label.Children.Add(new TextBlock{Text=warn,FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#F2C46D"),Margin=new Thickness(0,2,0,0)});
    row.Children.Add(label);
    var pick=new Button{Content=capturing==action?"Нажмите…":k==null?"—":k.ToString(),Padding=new Thickness(8,7,8,7),FontSize=12,Background=Brush(capturing==action?"#343C28":"#262A2F")};Grid.SetColumn(pick,2);pick.Click+=(s,e)=>StartCapture(action);row.Children.Add(pick);
    var clear=new Button{Content=new System.Windows.Shapes.Path{Data=Geometry.Parse("M0,0 L9,9 M9,0 L0,9"),Width=9,Height=9,Stroke=Brush("#C4CCD4"),StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round},Padding=new Thickness(9,8,9,8),Background=Brush("Transparent"),ToolTip="Без сочетания",IsEnabled=k!=null};Grid.SetColumn(clear,4);clear.Click+=(s,e)=>SetHotkey(action,null);row.Children.Add(clear);
    list.Children.Add(row);}}
  void StartCapture(string action){capturing=action;UnregisterHotkeys();RenderHotkeys();Message("Нажмите сочетание. Backspace — очистить, Esc — отмена");}
  void CaptureKey(KeyEventArgs e){e.Handled=true;Key k=e.Key==Key.System?e.SystemKey:e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;if(Hotkey.IsModifier(k))return;var mods=Keyboard.Modifiers;string action=capturing;
   if(mods==ModifierKeys.None&&k==Key.Escape){capturing=null;ConfigureHotkeys();RenderHotkeys();Message("Отменено");return;}
   if(mods==ModifierKeys.None&&(k==Key.Back||k==Key.Delete)){capturing=null;SetHotkey(action,null);return;}
   if(!Hotkey.Allowed(mods,k)){Message("Добавьте Ctrl, Alt, Shift или Win — одиночные клавиши разрешены только F1–F24");return;}
   capturing=null;SetHotkey(action,new Hotkey(mods,k));}
  void SetHotkey(string action,Hotkey k){capturing=null;string moved=null;if(k!=null)foreach(var a in Actions)if(a[0]!=action&&k.Same(HotkeyFor(a[0]))){keys[a[0]]=null;moved=a[1];}
   keys[action]=k;SavePrefs();ConfigureHotkeys();RenderHotkeys();bool busyKey;keyBusy.TryGetValue(action,out busyKey);
   Message(k==null?"Сочетание убрано":busyKey?k+" занято другой программой — выберите другое":k+" назначено"+(moved!=null?" (снято с действия «"+moved+"»)":""));}
  void Position(){var screen=WF.Screen.FromHandle(Native.GetForegroundWindow());var transform=PresentationSource.FromVisual(window);double dx=1,dy=1;if(transform!=null){dx=transform.CompositionTarget.TransformFromDevice.M11;dy=transform.CompositionTarget.TransformFromDevice.M22;}var area=screen.Bounds;window.WindowState=WindowState.Normal;window.Width=Math.Min(380,area.Width*dx);window.Height=area.Height*dy;window.Left=area.Left*dx;window.Top=area.Top*dy;}
  public void Show(){Ui(()=>{if(window.IsVisible&&window.IsActive)return;var fg=Native.GetForegroundWindow();if(fg!=handle&&!window.IsVisible)previousForeground=fg;Position();window.Show();window.Activate();Native.SetForegroundWindow(handle);Run(Refresh);});}
  void Hide(){if(!window.IsVisible)return;var back=previousForeground;previousForeground=IntPtr.Zero;window.Hide();if(back!=IntPtr.Zero&&Native.IsWindow(back))Native.SetForegroundWindow(back);}
  public Window Window {get{return window;}}
  static System.Drawing.Icon AppIcon(){try{return System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);}catch{return System.Drawing.SystemIcons.Application;}}
  public void RequestExit(){window.Dispatcher.BeginInvoke(new Action(Exit));}
  void UpdateControls(){bool ready=obs.Connected&&correctProfile&&!busy;foreach(string n in new[]{"ReplayButton","RecordButton","MicButton","ApplyButton","SceneBox","ModeGameButton","ModeDesktopButton","FolderPickButton"})C<FrameworkElement>(n).IsEnabled=ready;C<Button>("SaveButton").IsEnabled=ready&&replay;C<Button>("ReplayButton").IsEnabled=ready&&replayAvailable;C<Button>("MicButton").IsEnabled=ready&&micName.Length>0;C<TextBox>("BitrateBox").IsEnabled=ready&&encoderEditable;C<ComboBox>("QualityBox").IsEnabled=ready&&encoderEditable;C<TextBlock>("ReplayState").Text=!obs.Connected?"OBS не подключён":!replayAvailable?"Включите в OBS: Настройки → Вывод → Буфер повтора":replay?"Включён · до "+FormatDuration(duration):"Выключен";C<Button>("ReplayButton").Background=Brush(replay?"#27351C":"#1D2227");C<TextBlock>("RecordTitle").Text=record?"Остановить запись":"Запись";C<Ellipse>("RecordDot").Fill=Brush(record?"#FF655E":"#D1D7DD");if(!record)C<TextBlock>("RecordState").Text="Не идёт";C<Button>("MicButton").Content=micMuted?"Выключен":"Включён";C<Button>("MicButton").Foreground=Brush(micMuted?"#F59B92":"#C6E8A3");Hotkey save=HotkeyFor("save");bool saveBusy;keyBusy.TryGetValue("save",out saveBusy);C<TextBlock>("SaveHint").Text="До "+FormatDuration(duration)+(save!=null&&!saveBusy?" · "+save:"");}
  async Task Refresh(){if(!obs.Connected||refreshing||busy)return;refreshing=true;try{
   var profileTask=obs.Call("GetProfileList");var recordTask=obs.Call("GetRecordStatus");var scenesTask=obs.Call("GetSceneList");var dirTask=obs.Call("GetRecordDirectory");var videoTask=obs.Call("GetVideoSettings");var specialTask=obs.Call("GetSpecialInputs");var modeTask=obs.Call("GetProfileParameter",Json.D("parameterCategory","Output","parameterName","Mode"));
   await Task.WhenAll(profileTask,recordTask,scenesTask,dirTask,videoTask,specialTask,modeTask);
   try{replay=Json.Bool(await obs.Call("GetReplayBufferStatus"),"outputActive");replayAvailable=true;}catch(InvalidOperationException){replay=false;replayAvailable=false;} // буфер повтора выключен в настройках OBS
   profileName=Json.Str(profileTask.Result,"currentProfileName");outputMode=Json.Str(modeTask.Result,"parameterValue")=="Simple"?"Simple":"Advanced";micName=Json.Str(specialTask.Result,"mic1");correctProfile=true;record=Json.Bool(recordTask.Result,"outputActive");recordStart=DateTime.Now.AddMilliseconds(-Json.Int(recordTask.Result,"outputDuration"));directory=Json.Str(dirTask.Result,"recordDirectory").Replace('/','\\');C<TextBlock>("FolderLabel").Text=directory;C<TextBlock>("FolderLabel").ToolTip=directory;currentScene=Json.Str(scenesTask.Result,"currentProgramSceneName");
   loading=true;try{var box=C<ComboBox>("SceneBox");box.Items.Clear();object raw;if(scenesTask.Result.TryGetValue("scenes",out raw))foreach(object s in (object[])raw)box.Items.Add(Json.Str(Json.Obj(s),"sceneName"));box.SelectedItem=currentScene;
    var video=videoTask.Result;string size=Json.Int(video,"outputWidth")+","+Json.Int(video,"outputHeight");SelectTag(C<ComboBox>("ResolutionBox"),size);SelectContent(C<ComboBox>("FpsBox"),(Json.Int(video,"fpsNumerator")/Math.Max(1,Json.Int(video,"fpsDenominator"))).ToString());
    {var param=await obs.Call("GetProfileParameter",Json.D("parameterCategory",OutputSection,"parameterName","RecRBTime"));int value;if(int.TryParse(Json.Str(param,"parameterValue"),out value))duration=value;C<Slider>("DurationSlider").Value=Math.Max(3,Math.Min(300,duration));
     if(micName.Length>0){try{micMuted=Json.Bool(await obs.Call("GetInputMute",Json.D("inputName",micName)),"inputMuted");}catch(InvalidOperationException){micName="";}}
     // Битрейт и качество меняем только у NVENC в расширенном режиме вывода: там OBS читает recordEncoder.json при каждом запуске записи и буфера.
     encoderPath=Path.Combine(ObsConfig.ProfileDirectory(profileName),"recordEncoder.json");string recEncoder=outputMode=="Advanced"?Json.Str(await obs.Call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecEncoder")),"parameterValue"):"";
     encoderEditable=recEncoder.Contains("nvenc")&&File.Exists(encoderPath);C<TextBlock>("EncoderHint").Visibility=encoderEditable?Visibility.Collapsed:Visibility.Visible;
     if(encoderEditable){var enc=Json.Decode(File.ReadAllText(encoderPath));C<TextBox>("BitrateBox").Text=(Json.Int(enc,"bitrate",20000)/1000).ToString();C<ComboBox>("QualityBox").SelectedIndex=Json.Str(enc,"preset")=="p4"?0:Json.Str(enc,"multipass")=="qres"?2:1;}}
   }finally{loading=false;}
   C<TextBlock>("ConnectionLabel").Text="OBS подключён · профиль «"+profileName+"»";C<TextBlock>("FormatLabel").Text=outputMode=="Simple"?"Режим вывода OBS: простой":"Режим вывода OBS: расширенный";UpdateControls();
   {var names=new List<string>();foreach(var item in C<ComboBox>("SceneBox").Items)names.Add(Convert.ToString(item));await DetectModeScenes(names);}
   UpdateMode();ApplyPrivacy();privacy.Kick();
  }finally{refreshing=false;}}
  static void SelectTag(ComboBox box,string tag){foreach(ComboBoxItem item in box.Items)if(Convert.ToString(item.Tag)==tag){box.SelectedItem=item;return;}}
  static void SelectContent(ComboBox box,string text){foreach(ComboBoxItem item in box.Items)if(Convert.ToString(item.Content)==text){box.SelectedItem=item;return;}}
  void OnEvent(string type,Dictionary<string,object>d){
   if(type=="ReplayBufferStateChanged"){replay=Json.Bool(d,"outputActive");UpdateControls();}
   else if(type=="RecordStateChanged"){bool active=Json.Bool(d,"outputActive");if(active&&!record)recordStart=DateTime.Now;record=active;UpdateControls();if(!active&&Json.Str(d,"outputPath").Length>0)Notify("Запись сохранена",Path.GetFileName(Json.Str(d,"outputPath")));}
   else if(type=="ReplayBufferSaved"){lastSaved=Json.Str(d,"savedReplayPath");Message("Сохранено: "+Path.GetFileName(lastSaved));Notify("Повтор сохранён",Path.GetFileName(lastSaved));}
   else if(type=="InputMuteStateChanged"&&micName.Length>0&&Json.Str(d,"inputName")==micName){micMuted=Json.Bool(d,"inputMuted");UpdateControls();}
   else if(type=="CurrentProgramSceneChanged"){currentScene=Json.Str(d,"sceneName");loading=true;C<ComboBox>("SceneBox").SelectedItem=currentScene;loading=false;UpdateMode();ApplyPrivacy();privacy.Kick();}
   else if(type=="SceneItemEnableStateChanged"){if(Json.Bool(d,"sceneItemEnabled")&&hidden.Remove(Json.Str(d,"sceneName")+"\n"+Json.Int(d,"sceneItemId"))){SavePrefs();ApplyPrivacy();}}
   else if(type=="SceneItemCreated"||type=="SceneItemRemoved"||type=="SceneNameChanged"||type=="SceneCreated"||type=="SceneRemoved"){sceneItems.Clear();Run(Refresh);}
   else if(type=="CurrentProfileChanged"||type=="CurrentSceneCollectionChanged"){sceneItems.Clear();Run(Refresh);}
  }
  void LoadPrefs(){foreach(var a in Actions)keys[a[0]]=Hotkey.Parse(a[2]);try{if(!File.Exists(prefsPath))return;var p=Json.Decode(File.ReadAllText(prefsPath));preferAlt=!p.ContainsKey("altZ")||Json.Bool(p,"altZ");object raw;
   if(p.TryGetValue("hotkeys",out raw)){var stored=Json.Obj(raw);foreach(var a in Actions)if(stored.ContainsKey(a[0]))keys[a[0]]=Hotkey.Parse(Json.Str(stored,a[0]));}
   else if(!preferAlt)keys["menu"]=Hotkey.Parse("Ctrl+Alt+Z"); // старая настройка «Клавиши»
   if(p.TryGetValue("excluded",out raw)&&raw is object[])foreach(object x in (object[])raw)if(Convert.ToString(x).Length>0)excluded.Add(Convert.ToString(x));
   if(p.TryGetValue("hiddenItems",out raw)&&raw is object[])foreach(object x in (object[])raw)if(!hidden.Contains(Convert.ToString(x)))hidden.Add(Convert.ToString(x));}catch{}}
  void SavePrefs(){try{var hk=new Dictionary<string,string>();foreach(var a in Actions){Hotkey k=HotkeyFor(a[0]);hk[a[0]]=k==null?"":k.Serialize();}
   AtomicWrite(prefsPath,Json.Encode(Json.D("hotkeys",hk,"excluded",excluded.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray(),"hiddenItems",hidden.ToArray())));}catch(Exception ex){Message("Настройки меню не сохранены: "+ex.Message);}}
  async Task<object[]> Items(string scene){object[] items;if(sceneItems.TryGetValue(scene,out items))return items;object raw;var r=await obs.Call("GetSceneItemList",Json.D("sceneName",scene));r.TryGetValue("sceneItems",out raw);items=(raw as object[])??new object[0];sceneItems[scene]=items;return items;}
  static bool Has(object[] items,string kind){return items.Any(i=>Json.Str(Json.Obj(i),"inputKind")==kind);}
  // «Только игра» — сцена с захватом игры без захвата монитора; «Игра и экран» — сцена с захватом монитора.
  async Task DetectModeScenes(List<string> names){gameScene=null;desktopScene=null;foreach(string n in names){var items=await Items(n);if(desktopScene==null&&Has(items,"monitor_capture"))desktopScene=n;else if(gameScene==null&&Has(items,"game_capture")&&!Has(items,"monitor_capture"))gameScene=n;}}
  async Task SetMode(bool desktop){EnsureReady();string scene=desktop?desktopScene:gameScene;if(scene==null)throw new InvalidOperationException(desktop?"В OBS нет сцены с захватом монитора":"В OBS нет сцены только с захватом игры");if(scene!=currentScene)await obs.Call("SetCurrentProgramScene",Json.D("sceneName",scene));}
  void UpdateMode(){bool game=currentScene!=null&&currentScene==gameScene,desk=currentScene!=null&&currentScene==desktopScene;C<Button>("ModeGameButton").Background=Brush(game?"#27351C":"#1D2227");C<Button>("ModeDesktopButton").Background=Brush(desk?"#27351C":"#1D2227");C<TextBlock>("ModeHint").Text=game?"Записывается только окно игры: Alt+Tab, рабочий стол и другие программы в запись не попадут.":desk?"Записывается весь монитор: после Alt+Tab видно всё, кроме приложений из исключений.":"Сейчас в OBS выбрана сцена «"+currentScene+"».";UpdatePrivacyLabel();}
  void UpdatePrivacyLabel(){var label=C<TextBlock>("PrivacyLabel");string now=privacy.Visible;bool covering=hidden.Count>0;if(excluded.Count==0){label.Visibility=Visibility.Collapsed;}else{label.Visibility=Visibility.Visible;label.Text=covering?"Сейчас скрыто от записи: "+now+" — в записи только игра":"Исключения: "+string.Join(", ",excluded.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))+(currentScene==desktopScene?"":" · действуют в режиме «Игра и экран»");label.Foreground=Brush(covering?"#F2C46D":"#8E99A4");}string tip="Replay Overlay — OBS"+(covering?" · скрыто: "+now:"");tray.Text=tip.Length>63?tip.Substring(0,63):tip;}
  void ExclusionsChanged(){SavePrefs();privacy.SetExcluded(excluded);privacy.Enabled=obs.Connected&&excluded.Count>0;privacy.Kick();RenderExcluded();FillApps();UpdatePrivacyLabel();ApplyPrivacy();}
  void RenderExcluded(){var list=C<StackPanel>("ExcludedList");list.Children.Clear();if(excluded.Count==0){list.Children.Add(new TextBlock{Text="Список пуст",Foreground=Brush("#7F8A96"),FontSize=12,Margin=new Thickness(0,0,0,4)});return;}
   foreach(string name in excluded.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)){var row=new Grid{Margin=new Thickness(0,0,0,6)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.Children.Add(new TextBlock{Text=name+".exe",FontSize=13,Foreground=Brush("#E6EBDD"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});var remove=new Button{Content="Убрать",Padding=new Thickness(10,5,10,5),FontSize=11,ToolTip="Снова записывать "+name+".exe"};Grid.SetColumn(remove,1);string n=name;remove.Click+=(s,e)=>{excluded.Remove(n);ExclusionsChanged();};row.Children.Add(remove);list.Children.Add(row);}}
  void FillApps(){var box=C<ComboBox>("AppBox");var current=box.SelectedItem as ComboBoxItem;string keep=current==null?null:Convert.ToString(current.Tag);box.Items.Clear();
   foreach(var app in PrivacyWatcher.RunningApps()){if(excluded.Contains(app.Key))continue;string title=app.Value.Length>28?app.Value.Substring(0,27)+"…":app.Value;var item=new ComboBoxItem{Content=app.Key+".exe — "+title,Tag=app.Key,ToolTip=app.Value};box.Items.Add(item);if(app.Key==keep)box.SelectedItem=item;}
   if(box.SelectedItem==null&&box.Items.Count>0)box.SelectedIndex=0;}
  // Пока окно исключённого приложения видно, захват монитора в текущей сцене выключается; затем включается обратно.
  async void ApplyPrivacy(){if(privacyRunning){privacyAgain=true;return;}privacyRunning=true;try{do{privacyAgain=false;await SyncPrivacy();}while(privacyAgain);}catch(Exception ex){Message("Скрытие приложений: "+ex.Message);}finally{privacyRunning=false;UpdatePrivacyLabel();}}
  async Task SyncPrivacy(){if(!obs.Connected||!correctProfile)return;bool want=excluded.Count>0&&privacy.Enabled&&privacy.Visible.Length>0;bool changed=false;
   foreach(string key in hidden.ToList()){int cut=key.LastIndexOf('\n');string scene=key.Substring(0,cut);if(want&&scene==currentScene)continue;
    try{await obs.Call("SetSceneItemEnabled",Json.D("sceneName",scene,"sceneItemId",int.Parse(key.Substring(cut+1)),"sceneItemEnabled",true));}catch(InvalidOperationException){}
    hidden.Remove(key);changed=true;}
   if(want&&!string.IsNullOrEmpty(currentScene)){foreach(object raw in await Items(currentScene)){var item=Json.Obj(raw);if(Json.Str(item,"inputKind")!="monitor_capture")continue;int id=Json.Int(item,"sceneItemId");string key=currentScene+"\n"+id;if(hidden.Contains(key))continue;
    var state=await obs.Call("GetSceneItemEnabled",Json.D("sceneName",currentScene,"sceneItemId",id));if(!Json.Bool(state,"sceneItemEnabled"))continue;
    hidden.Add(key);changed=true;try{await obs.Call("SetSceneItemEnabled",Json.D("sceneName",currentScene,"sceneItemId",id,"sceneItemEnabled",false));}catch{hidden.Remove(key);throw;}}}
   if(changed)SavePrefs();}
  void RestoreHiddenNow(){if(!obs.Connected)return;foreach(string key in hidden.ToList()){int cut=key.LastIndexOf('\n');try{if(obs.Call("SetSceneItemEnabled",Json.D("sceneName",key.Substring(0,cut),"sceneItemId",int.Parse(key.Substring(cut+1)),"sceneItemEnabled",true)).Wait(1500))hidden.Remove(key);}catch{}}SavePrefs();}
  async Task SaveReplay(){EnsureReady();var status=await obs.Call("GetReplayBufferStatus");if(!Json.Bool(status,"outputActive"))throw new InvalidOperationException("Сначала включите мгновенный повтор");await obs.Call("SaveReplayBuffer");Message("Сохраняю повтор…");}
  async Task PickFolder(){EnsureReady();if(Json.Bool(await obs.Call("GetRecordStatus"),"outputActive"))throw new InvalidOperationException("Сначала остановите запись");
   string chosen;dialogOpen=true;try{using(var dialog=new WF.FolderBrowserDialog{Description="Куда сохранять повторы и записи OBS",ShowNewFolderButton=true,SelectedPath=directory}){if(dialog.ShowDialog(new Owner(handle))!=WF.DialogResult.OK)return;chosen=dialog.SelectedPath;}}finally{dialogOpen=false;window.Activate();}
   if(string.Equals(chosen.TrimEnd('\\'),directory.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return;
   string probe=Path.Combine(chosen,".replay-overlay-write-test");try{File.WriteAllText(probe,"");File.Delete(probe);}catch(Exception ex){throw new InvalidOperationException("В эту папку нельзя записывать: "+ex.Message);}
   bool resume=Json.Bool(await obs.Call("GetReplayBufferStatus"),"outputActive");busy=true;UpdateControls();
   try{await obs.Call("SetRecordDirectory",Json.D("recordDirectory",chosen.Replace('\\','/')));
    if(resume){await obs.Call("StopReplayBuffer");await WaitBuffer(false);await obs.Call("StartReplayBuffer");await WaitBuffer(true);}
   }finally{busy=false;UpdateControls();}
   Message("Папка записей: "+chosen+(resume?" · буфер запущен заново":""));await Refresh();}
  async Task LaunchObs(){var running=Process.GetProcessesByName("obs64");if(running.Length>0){var h=running[0].MainWindowHandle;if(h!=IntPtr.Zero){Native.ShowWindow(h,9);Native.SetForegroundWindow(h);}}else{string exe=ObsConfig.Executable();if(exe==null)throw new FileNotFoundException("OBS Studio не найден. Установите его с obsproject.com");Process.Start(new ProcessStartInfo(exe,"--startreplaybuffer"){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=true});}await Task.FromResult(0);}
  async Task WaitBuffer(bool active){for(int i=0;i<100;i++){var s=await obs.Call("GetReplayBufferStatus");if(Json.Bool(s,"outputActive")==active)return;await Task.Delay(100);}throw new TimeoutException("Буфер не перешёл в ожидаемое состояние");}
  static void AtomicWrite(string path,string contents){string temp=path+".overlay.tmp";File.WriteAllText(temp,contents,new System.Text.UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,path+".overlay.bak");else File.Move(temp,path);}
  string OutputSection{get{return outputMode=="Simple"?"SimpleOutput":"AdvOut";}}
  async Task Apply(){EnsureReady();int bitrate=0;bool editEncoder=encoderEditable;string section=OutputSection,startProfile=profileName;if(editEncoder&&(!int.TryParse(C<TextBox>("BitrateBox").Text,out bitrate)||bitrate<5||bitrate>100))throw new InvalidOperationException("Укажите целый битрейт от 5 до 100 Мбит/с");
   int seconds=(int)C<Slider>("DurationSlider").Value;string[] size=Convert.ToString(((ComboBoxItem)C<ComboBox>("ResolutionBox").SelectedItem).Tag).Split(',');int fps=int.Parse(Convert.ToString(((ComboBoxItem)C<ComboBox>("FpsBox").SelectedItem).Content));int quality=C<ComboBox>("QualityBox").SelectedIndex;
   var rec=await obs.Call("GetRecordStatus");var stream=await obs.Call("GetStreamStatus");var camera=await obs.Call("GetVirtualCamStatus");if(Json.Bool(rec,"outputActive")||Json.Bool(stream,"outputActive")||Json.Bool(camera,"outputActive"))throw new InvalidOperationException("Сначала остановите запись, трансляцию и виртуальную камеру в OBS");
   string oldEncoder=editEncoder?File.ReadAllText(encoderPath):null;var oldVideo=await obs.Call("GetVideoSettings");var oldDuration=await obs.Call("GetProfileParameter",Json.D("parameterCategory",section,"parameterName","RecRBTime"));bool resumeKnown=replayAvailable;bool resume=resumeKnown&&Json.Bool(await obs.Call("GetReplayBufferStatus"),"outputActive");bool changed=false;busy=true;UpdateControls();
   Exception failure=null;
   try{if(resume){await obs.Call("StopReplayBuffer");await WaitBuffer(false);}var check=await obs.Call("GetProfileList");if(Json.Str(check,"currentProfileName")!=startProfile)throw new InvalidOperationException("Профиль изменился; применение отменено");
    changed=true;if(editEncoder){var enc=Json.Decode(oldEncoder);enc["rate_control"]="VBR";enc["bitrate"]=bitrate*1000;enc["max_bitrate"]=bitrate*1500;enc["preset"]=quality==0?"p4":"p5";enc["multipass"]=quality==2?"qres":"disabled";enc["adaptive_quantization"]=quality!=0;enc["lookahead"]=false;AtomicWrite(encoderPath,Json.Encode(enc));}
    await obs.Call("SetProfileParameter",Json.D("parameterCategory",section,"parameterName","RecRBTime","parameterValue",seconds.ToString()));
    await obs.Call("SetVideoSettings",Json.D("outputWidth",int.Parse(size[0]),"outputHeight",int.Parse(size[1]),"fpsNumerator",fps,"fpsDenominator",1));
    if(resume){await obs.Call("StartReplayBuffer");await WaitBuffer(true);}Message("Настройки применены"+(resume?" · буфер запущен заново":""));
   }catch(Exception ex){failure=ex;}
   try{
    if(failure!=null){
     try{
      if(!obs.Connected)throw new IOException("OBS отключился");
      var now=await obs.Call("GetProfileList");
      if(Json.Str(now,"currentProfileName")!=startProfile)throw new InvalidOperationException("Выбран другой профиль");
      if(changed){
       if(Json.Bool(await obs.Call("GetReplayBufferStatus"),"outputActive")){await obs.Call("StopReplayBuffer");await WaitBuffer(false);}
       if(oldEncoder!=null)AtomicWrite(encoderPath,oldEncoder);
       await obs.Call("SetProfileParameter",Json.D("parameterCategory",section,"parameterName","RecRBTime","parameterValue",Json.Str(oldDuration,"parameterValue","300")));
       await obs.Call("SetVideoSettings",Json.D("outputWidth",Json.Int(oldVideo,"outputWidth"),"outputHeight",Json.Int(oldVideo,"outputHeight"),"fpsNumerator",Json.Int(oldVideo,"fpsNumerator"),"fpsDenominator",Json.Int(oldVideo,"fpsDenominator")));
      }
      if(resume&&!Json.Bool(await obs.Call("GetReplayBufferStatus"),"outputActive"))await obs.Call("StartReplayBuffer");
     }catch(Exception rollbackError){failure=new InvalidOperationException("Проверьте настройки OBS: откат не завершён. "+failure.Message+" / "+rollbackError.Message);}
     throw failure;
    }
   }finally{busy=false;UpdateControls();}
   await Refresh();
  }
  void Notify(string title,string detail){if(exiting)return;if(toast!=null)toast.Close();if(toastTimer!=null)toastTimer.Stop();var text=new StackPanel();text.Children.Add(new TextBlock{Text=title,Foreground=Brush("#B9EB79"),FontSize=15,FontWeight=FontWeights.SemiBold});text.Children.Add(new TextBlock{Text=detail,Foreground=Brush("#BCC6D0"),FontSize=11,Margin=new Thickness(0,7,0,0),TextTrimming=TextTrimming.CharacterEllipsis});toast=new Window{Title="Replay — уведомление",Width=360,Height=92,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brush("Transparent"),Topmost=true,ShowInTaskbar=false,ShowActivated=false,Content=new Border{Background=Brush("#F51A1E23"),BorderBrush=Brush("#536439"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(18),Child=text}};toast.SourceInitialized+=(s,e)=>{if(!preview)Native.SetWindowDisplayAffinity(new WindowInteropHelper(toast).Handle,0x11);};toast.Left=SystemParameters.WorkArea.Right-toast.Width-24;toast.Top=SystemParameters.WorkArea.Top+24;toast.Show();toastTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3.5)};toastTimer.Tick+=(s,e)=>{toastTimer.Stop();if(toast!=null){toast.Close();toast=null;}};toastTimer.Start();}
  public void Exit(){if(exiting)return;privacy.Dispose();RestoreHiddenNow();exiting=true;clock.Stop();if(toastTimer!=null)toastTimer.Stop();if(toast!=null)toast.Close();UnregisterHotkeys();obs.Dispose();tray.Visible=false;tray.Dispose();Application.Current.Shutdown();}
 }
 public static class Program {
  [STAThread] public static void Main(string[] args){bool fresh;using(var mutex=new Mutex(true,Names.Mutex,out fresh))using(var wake=new EventWaitHandle(false,EventResetMode.AutoReset,Names.Show))using(var quit=new EventWaitHandle(false,EventResetMode.AutoReset,Names.Exit)){
   if(!fresh){if(!args.Contains("--hidden")){Native.AllowSetForegroundWindow(-1);wake.Set();}return;}var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};Overlay overlay=null;app.DispatcherUnhandledException+=(s,e)=>{e.Handled=true;MessageBox.Show("Ошибка оверлея: "+e.Exception.Message,"Replay Overlay");};try{overlay=new Overlay(args.Contains("--preview"));var listener=new Thread(()=>{while(true){wake.WaitOne();overlay.Show();}}){IsBackground=true};listener.Start();var stopper=new Thread(()=>{quit.WaitOne();overlay.RequestExit();}){IsBackground=true};stopper.Start();if(args.Contains("--hidden")){overlay.StartHidden();app.Run();}else app.Run(overlay.Window);}catch(Exception ex){MessageBox.Show("Оверлей не запустился: "+ex.Message,"Replay Overlay");}finally{if(overlay!=null)overlay.Exit();}
  }}
 }
}
