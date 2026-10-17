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
  [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
  [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 }
 sealed class Owner : WF.IWin32Window { readonly IntPtr h; public Owner(IntPtr handle){h=handle;} public IntPtr Handle{get{return h;}} }
 public sealed partial class Overlay {
  readonly IEngine engine=new ObsEngine(EngineHome.Find()); readonly Window window; readonly WF.NotifyIcon tray;
  readonly bool preview; readonly DispatcherTimer clock=new DispatcherTimer();
  IntPtr handle,previousForeground; bool dialogOpen,exiting,loading,busy,refreshing,replay,record,micMuted,correctProfile,preferAlt=true,replayAvailable=true,encoderEditable;
  string profileName="",outputMode="Advanced",micName="",encoderPath="";
  // Действия с глобальными сочетаниями: ключ, подпись, сочетание по умолчанию (пусто — без сочетания).
  static readonly string[][] Actions={new[]{"menu","Открыть / закрыть меню","Alt+Z"},new[]{"save","Сохранить повтор","Alt+F10"},new[]{"replay","Включить / выключить повтор",""},new[]{"record","Начать / остановить запись","Alt+F9"},new[]{"modeGame","Режим «Только игра»",""},new[]{"modeDesktop","Режим «Игра и экран»",""},new[]{"mic","Микрофон вкл / выкл",""},new[]{"clips","Открыть галерею",""},new[]{"shot","Сделать скриншот","Alt+F8"}};
  readonly Dictionary<string,Hotkey> keys=new Dictionary<string,Hotkey>();readonly Dictionary<string,bool> keyBusy=new Dictionary<string,bool>();
  List<KeyValuePair<Hotkey,string>> obsKeys=new List<KeyValuePair<Hotkey,string>>();string capturing;
  int duration=300; string directory="",currentScene="",lastSaved=""; DateTime recordStart;
  readonly string prefsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"overlay-settings.json");
  // Фоновая работа: запуск OBS, удержание буфера, диагностика.
  bool launchObs=true,keepReplay=true,diagnostics=true,replayWanted=true,exitStarted,wasConnected;int connections;
  readonly DispatcherTimer statsTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(10)};
  StatsSample lastStats;int statsTimeouts,statsPolls;readonly Dictionary<string,DateTime> warned=new Dictionary<string,DateTime>();readonly List<DateTime> relaunches=new List<DateTime>();
  // Игры: распознавание, профили, возврат после игры.
  // Звук: микшер, дорожки, звук программ, устройства.
  AudioSetup audio;AudioState audioState=new AudioState();string audioMode="all";bool audioBusy,discordHasSource;
  readonly List<string> audioOnly=new List<string>(),audioExcept=new List<string>();
  readonly DispatcherTimer sessionTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)},volumeTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(60)};
  readonly Dictionary<string,double> pendingVolume=new Dictionary<string,double>();readonly Dictionary<string,System.Windows.Controls.Primitives.ToggleButton> muteButtons=new Dictionary<string,System.Windows.Controls.Primitives.ToggleButton>();
  readonly GameWatcher gamesWatch=new GameWatcher();GameSession gameSession;int appliedPid;bool gameBusy;
  readonly HashSet<string> myGames=new HashSet<string>(StringComparer.OrdinalIgnoreCase),notGames=new HashSet<string>(StringComparer.OrdinalIgnoreCase),noCursor=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<string,string> gameModes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); // exe → game|desktop|none (нет записи — как по умолчанию)
  bool autoGames=true,gameRestore=true;string gameAction="game";
  // Приватность: уведомления Windows, строгая защита («шторка» + задержка экрана), правила для игр.
  bool hideNotifications;StrictPrivacy strict;Dictionary<string,object> strictLoaded;DispatcherTimer revealTimer;
  readonly HashSet<string> coverScenesOn=new HashSet<string>();bool coverOn{get{return coverScenesOn.Count>0;}}
  readonly Dictionary<string,string> gamePrivacy=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); // exe → screen|none
  readonly PrivacyWatcher privacy=new PrivacyWatcher(); readonly HashSet<string> excluded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  readonly List<string> hidden=new List<string>(); // «сцена\nid» элементов захвата монитора, выключенных оверлеем
  readonly Dictionary<string,object[]> sceneItems=new Dictionary<string,object[]>(); string gameScene,desktopScene; bool privacyRunning,privacyAgain;
  public Overlay(bool previewMode) {
   preview=previewMode;
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Overlay.xaml"))window=(Window)XamlReader.Load(stream);
   acrylic=!previewMode&&Backdrop.Supported;
   if(acrylic){Backdrop.Prepare(window);var shell=(Border)window.FindName("Shell");shell.CornerRadius=new CornerRadius(0);shell.Background=Brush("#B80D0F13");((Border)window.FindName("Sidebar")).Background=Brush("#80101216");}
   if(preview)window.ShowInTaskbar=true;
   LoadPrefs();ForgetOtherEngine();privacy.SetExcluded(excluded);
   window.SourceInitialized+=(s,e)=>InitWindow();
   window.Closing+=(s,e)=>{if(!exiting){e.Cancel=true;Hide();}};
   window.IsVisibleChanged+=(s,e)=>{if(window.IsVisible){clock.Start();Run(Refresh);if(ClipsOpen)Run(LoadClips);if(currentPage=="record")Run(RenderMixer);ShowKeysPanel();UpdateDisk();}else{clock.Stop();PausePlayer();if(keysPanel!=null)keysPanel.Hide();if(capturing!=null){capturing=null;ConfigureHotkeys();}}};
   window.PreviewKeyDown+=(s,e)=>{if(capturing!=null){CaptureKey(e);return;}if(e.Key==Key.Escape&&PickerOpen){ClosePicker();e.Handled=true;return;}if(e.Key==Key.Escape){Hide();e.Handled=true;}};
   window.Deactivated+=(s,e)=>{if(!preview&&!dialogOpen&&!exiting&&window.IsVisible){var game=previousForeground;previousForeground=IntPtr.Zero;
    // Щелчок в игру мимо меню: игра активировалась без Alt и не прячет курсор. Повторяем активацию «меню → игра» с зажатым Alt, как при Alt+Tab.
    if(game!=IntPtr.Zero&&Native.GetForegroundWindow()==game&&(Native.GetAsyncKeyState(0x12)&0x8000)==0){Native.keybd_event(0x12,0x38,0,UIntPtr.Zero);try{Native.SetForegroundWindow(handle);Native.SetForegroundWindow(game);}finally{Native.keybd_event(0x12,0x38,2,UIntPtr.Zero);}}
    window.Hide();}};
   C<Button>("CloseButton").Click+=(s,e)=>Hide();
   C<Button>("FolderPickButton").Click+=(s,e)=>Run(PickFolder);
   C<Button>("ModeGameButton").Click+=(s,e)=>Run(()=>SetMode(false));
   C<Button>("ModeDesktopButton").Click+=(s,e)=>Run(()=>SetMode(true));
   C<ComboBox>("AppBox").DropDownOpened+=(s,e)=>FillApps();
   C<Button>("AddAppButton").Click+=(s,e)=>OpenPicker("Не записывать приложение","Пока окно этого приложения видно на экране, в записи будет только игра.",excluded,n=>{excluded.Add(n);ExclusionsChanged();Message(n+".exe не будет попадать в запись в режиме «Игра и экран»");});
   privacy.Changed+=names=>ApplyPrivacy();
   C<Button>("RecordButton").Click+=(s,e)=>Run(async()=>{EnsureReady();await engine.ToggleRecord();await Refresh();});
   C<Button>("SaveButton").Click+=(s,e)=>Run(SaveReplay);
   C<Button>("MicButton").Click+=(s,e)=>Run(async()=>{EnsureReady();if(micName.Length==0)throw new InvalidOperationException("В OBS не выбран микрофон: Настройки → Аудио");await engine.ToggleMute(micName);await Refresh();});
   C<Button>("LaunchButton").Click+=(s,e)=>Run(LaunchObs);if(engine.Home.Own)C<Button>("LaunchButton").Visibility=Visibility.Collapsed; // у своей копии окна нет
   C<Button>("ApplyButton").Click+=(s,e)=>Run(Apply);
   C<ComboBox>("SceneBox").SelectionChanged+=(s,e)=>{if(!loading&&C<ComboBox>("SceneBox").SelectedItem!=null)Run(async()=>{EnsureReady();await engine.Call("SetCurrentProgramScene",Json.D("sceneName",Convert.ToString(C<ComboBox>("SceneBox").SelectedItem)));});};
   statsTimer.Tick+=(s,e)=>PollStats();BindBackgroundOptions();BindGames();BindAudio();BindPrivacy();BindClips();BindIndicator();BindShell();BindVideo();BindPicker();BindSystem();BindSR();clock.Interval=TimeSpan.FromSeconds(1);clock.Tick+=(s,e)=>{if(record)C<TextBlock>("RecordState").Text=(DateTime.Now-recordStart).ToString(@"hh\:mm\:ss");};
   tray=new WF.NotifyIcon{Icon=AppIcon(),Visible=true,Text="Replay Overlay — OBS"};
   var menu=new WF.ContextMenuStrip();menu.Items.Add("Открыть меню",null,(s,e)=>Show());menu.Items.Add("Сохранить повтор",null,(s,e)=>Run(SaveReplay));menu.Items.Add("Сделать скриншот",null,(s,e)=>Run(TakeScreenshot));menu.Items.Add("Галерея",null,(s,e)=>{Show();Ui(OpenClips);});if(!engine.Home.Own)menu.Items.Add("Открыть OBS",null,(s,e)=>Run(LaunchObs));menu.Items.Add("Открыть журнал",null,(s,e)=>OpenLog());menu.Items.Add(new WF.ToolStripSeparator());menu.Items.Add("Выйти из оверлея",null,(s,e)=>Exit());tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>Show();
   engine.ConnectionChanged+=connected=>Ui(()=>{C<TextBlock>("ConnectionLabel").Text=connected?"OBS подключён":"Нет связи с OBS";C<Ellipse>("ConnectionDot").Fill=Brush(connected?"#8DCC38":"#D6A45B");UpdateControls();privacy.Enabled=connected&&(excluded.Count>0||hideNotifications);OnConnection(connected);if(connected){sceneItems.Clear();Run(RefreshWhenReady);}});
   engine.Status+=message=>Ui(()=>Message(message));
   engine.RecordChanged+=(active,phase,file)=>Ui(()=>OnRecordChanged(active,phase,file));
   engine.ReplayChanged+=(active,phase)=>Ui(()=>OnReplayChanged(active,phase));
   engine.ReplaySaved+=file=>Ui(()=>OnReplaySaved(file));
   engine.MuteChanged+=(input,muted)=>Ui(()=>OnMuteChanged(input,muted));
   engine.Exiting+=()=>Ui(()=>{exitStarted=true;Log.Write("OBS закрывается");});
   engine.Event+=(type,data)=>Ui(()=>OnEvent(type,data));
   window.Loaded+=(s,e)=>Position();
   UpdateControls();
  }
  public void StartHidden(){new WindowInteropHelper(window).EnsureHandle();string shortcut=C<TextBlock>("ShortcutLabel").Text;Notify("Replay Overlay запущен",shortcut.Length>0?shortcut+" — открыть меню":"Меню — через значок в трее");}
  T C<T>(string name) where T:FrameworkElement{return (T)window.FindName(name);}
  void SettingsPage(bool open){ShowPage(open?"settings":"record");}
  static Brush Brush(string color){return (Brush)new BrushConverter().ConvertFromString(color);}
  static string FormatDuration(int seconds){return (seconds/60)+":"+(seconds%60).ToString("00");}
  void Ui(Action action){if(!exiting)window.Dispatcher.BeginInvoke(action);}
  void Message(string text){C<TextBlock>("MessageLabel").Text=text;C<TextBlock>("MessageLabel").ToolTip=text;}
  void EnsureReady(){if(!engine.Connected)throw new InvalidOperationException("Запустите OBS и дождитесь подключения");if(!correctProfile)throw new InvalidOperationException("Подождите: меню получает настройки OBS");}
  async void Run(Func<Task> action){try{await action();}catch(Exception ex){Log.Write("Ошибка: "+ex.Message);Message(ex.Message);if(!window.IsVisible)Notify("Действие не выполнено",ex.Message,ToastKind.Error);}}
  bool acrylic; // размытие фона под панелью (Windows 11 22H2+)
  void InitWindow(){handle=new WindowInteropHelper(window).Handle;HwndSource.FromHwnd(handle).AddHook(Hook);if(!preview&&!Native.SetWindowDisplayAffinity(handle,0x11))Log.Write("Окно меню не удалось исключить из захвата");
   if(acrylic&&!Backdrop.Apply(handle)){Log.Write("Размытие фона недоступно — обычная подложка");C<Border>("Shell").Background=Brush("#F00D0F13");}ConfigureHotkeys();engine.Start();gamesWatch.Enabled=autoGames;Log.Write("Меню запущено, версия "+Assembly.GetExecutingAssembly().GetName().Version.ToString(3)+(StartedFrom=="--from=reset"?" (после сброса настроек)":StartedFrom=="--from=run"?" (автозапуск: реестр)":StartedFrom=="--from=startup"?" (автозапуск: папка «Автозагрузка»)":""));if(launchObs&&!preview)LaunchObsHidden("при запуске меню");}
  void UnregisterHotkeys(){for(int i=0;i<Actions.Length;i++)Native.UnregisterHotKey(handle,100+i);}
  void ConfigureHotkeys(){if(handle==IntPtr.Zero)return;UnregisterHotkeys();keyBusy.Clear();
   for(int i=0;i<Actions.Length;i++){Hotkey k;if(keys.TryGetValue(Actions[i][0],out k)&&k!=null)keyBusy[Actions[i][0]]=!Native.RegisterHotKey(handle,100+i,k.Win32Modifiers,k.VirtualKey);}
   Hotkey menu=HotkeyFor("menu");bool menuOk=menu!=null&&!keyBusy["menu"];C<TextBlock>("ShortcutLabel").Text=menuOk?menu.ToString():"";C<TextBlock>("ShortcutLabel").ToolTip=menuOk?"Открыть / закрыть меню":null;
   if(!menuOk)Message(menu==null?"Сочетание для меню не задано — открывайте его через значок в трее":"Сочетание "+menu+" занято другой программой — выберите другое в настройках");UpdateControls();}
  Hotkey HotkeyFor(string action){Hotkey k;return keys.TryGetValue(action,out k)?k:null;}
  IntPtr Hook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled){if(msg==0x0312){int i=wParam.ToInt32()-100;if(i>=0&&i<Actions.Length){DoAction(Actions[i][0]);handled=true;}}return IntPtr.Zero;}
  // Глобальное сочетание сработало. Если меню скрыто — результат показываем всплывающим уведомлением.
  void DoAction(string action){
   if(action=="menu"){if(window.IsVisible)Hide();else ShowAfterKeysReleased();return;}
   if(action=="save"){Run(SaveReplay);return;}
   if(action=="clips"){Show();Ui(OpenClips);return;}
   if(action=="shot"){Run(TakeScreenshot);return;}
   Run(async()=>{EnsureReady();string title,text;
    if(action=="replay"){await ToggleReplay();title="Мгновенный повтор";text=ReplayOn?"Включён":"Выключен";}
    else if(action=="record"){bool on=await engine.ToggleRecord();title="Запись";text=on?"Начата":"Остановлена";}
    else if(action=="mic"){if(micName.Length==0)throw new InvalidOperationException("В OBS не выбран микрофон");bool off=await engine.ToggleMute(micName);title="Микрофон";text=off?"Выключен":"Включён";}
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
  void Position(){var screen=WF.Screen.FromHandle(Native.GetForegroundWindow());var transform=PresentationSource.FromVisual(window);double dx=1,dy=1;if(transform!=null){dx=transform.CompositionTarget.TransformFromDevice.M11;dy=transform.CompositionTarget.TransformFromDevice.M22;}var area=screen.Bounds;window.WindowState=WindowState.Normal;window.Width=Math.Min(area.Width*dx,Math.Min(560,Math.Max(480,area.Width*dx*0.45))); /* не шире 45% экрана, чтобы не закрывать игру */window.Height=area.Height*dy;window.Left=area.Left*dx;window.Top=area.Top*dy;}
  public void Show(){Ui(()=>{if(window.IsVisible&&window.IsActive)return;var fg=Native.GetForegroundWindow();if(fg!=handle&&!window.IsVisible)previousForeground=fg;Position();window.Show();window.Activate();Native.SetForegroundWindow(handle);Run(Refresh);});}
  // Фокус отдаём игре, пока меню ещё на переднем плане: скрытое окно уже не имеет права его передать,
  // Windows остаётся без активного окна, и игра (Warframe) не прячет курсор даже после щелчка.
  // Ещё: Warframe снова прячет курсор, только если активируется, пока зажат Alt (как при Alt+Tab или закрытии по Alt+Z).
  // Поэтому при закрытии по Esc или «×» Alt на мгновение зажимается сам — если пользователь его не держит.
  void Hide(){if(!window.IsVisible)return;var back=previousForeground;previousForeground=IntPtr.Zero;bool ok=back!=IntPtr.Zero&&Native.IsWindow(back);
   bool altHeld=(Native.GetAsyncKeyState(0x12)&0x8000)!=0,tap=ok&&!altHeld;
   if(tap)Native.keybd_event(0x12,0x38,0,UIntPtr.Zero);
   try{if(ok)Native.SetForegroundWindow(back);window.Hide();if(ok&&Native.GetForegroundWindow()!=back)Native.SetForegroundWindow(back);}
   finally{if(tap)Native.keybd_event(0x12,0x38,2,UIntPtr.Zero);}}
  public Window Window {get{return window;}}
  public static string StartedFrom=""; // каким способом запущено: --from=run / --from=startup (для журнала)
  static System.Drawing.Icon AppIcon(){try{return System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);}catch{return System.Drawing.SystemIcons.Application;}}
  public void RequestExit(){window.Dispatcher.BeginInvoke(new Action(Exit));}
  void UpdateControls(){bool ready=engine.Connected&&correctProfile&&!busy;foreach(string n in new[]{"RecordButton","MicButton","ApplyButton","SceneBox","ModeGameButton","ModeDesktopButton","FolderPickButton","DurationApply","ShotButton"})C<FrameworkElement>(n).IsEnabled=ready;
   C<Button>("SaveButton").IsEnabled=ready&&ReplayOn;var rs=C<System.Windows.Controls.Primitives.ToggleButton>("ReplaySwitch");rs.IsEnabled=ready&&(srOn||replayAvailable);rs.IsChecked=ReplayOn;
   C<System.Windows.Controls.Primitives.ToggleButton>("RecordSwitch").IsChecked=record;C<StackPanel>("RecordTimer").Visibility=record?Visibility.Visible:Visibility.Collapsed;
   C<Button>("MicButton").IsEnabled=ready&&micName.Length>0;C<TextBox>("BitrateBox").IsEnabled=ready&&encoderEditable;C<ComboBox>("QualityBox").IsEnabled=ready&&encoderEditable;
   C<TextBlock>("ReplayState").Text=!engine.Connected?"OBS не подключён":!replayAvailable&&!srOn?"Включите в OBS: Настройки → Вывод → Буфер повтора":ReplayOn?"Включён · хранит последние "+DurationWords(duration)+(srOn?" · свои настройки":""):"Выключен";
   C<Button>("MicButton").Content=micMuted?"Выключен":"Включён";C<Button>("MicButton").Foreground=Brush(micMuted?"#F59B92":"#7DF0B0");
   C<TextBlock>("SaveTitle").Text="Сохранить последние "+DurationWords(duration);
   foreach(var a in new[]{new[]{"save","SaveHint"},new[]{"record","RecordKeyLabel"}}){Hotkey k=HotkeyFor(a[0]);bool b;keyBusy.TryGetValue(a[0],out b);C<TextBlock>(a[1]).Text=k==null?"не задано":b?k+" — занято":k.ToString();}
   Hotkey sk=HotkeyFor("shot");C<TextBlock>("ShotKeyHint").Text=sk!=null?sk.ToString():"Сочетание не задано";}
  async Task Refresh(){if(!engine.Connected||refreshing||busy)return;refreshing=true;try{
   var profileTask=engine.Call("GetProfileList");var recordTask=engine.Record();var scenesTask=engine.Call("GetSceneList");var dirTask=engine.RecordDirectory();var videoTask=engine.Call("GetVideoSettings");var micTask=engine.DefaultMic();var modeTask=engine.Call("GetProfileParameter",Json.D("parameterCategory","Output","parameterName","Mode"));
   await Task.WhenAll(profileTask,recordTask,scenesTask,dirTask,videoTask,micTask,modeTask);
   {var rs=await engine.ReplayActive();replay=rs==true;replayAvailable=rs!=null;} // null — буфер повтора выключен в настройках OBS
   profileName=Json.Str(profileTask.Result,"currentProfileName");outputMode=Json.Str(modeTask.Result,"parameterValue")=="Simple"?"Simple":"Advanced";micName=micTask.Result;correctProfile=true;record=recordTask.Result.Active;recordStart=DateTime.Now.AddMilliseconds(-recordTask.Result.DurationMs);directory=dirTask.Result;if(directory.Length>0&&directory!=lastDirectory){lastDirectory=directory;SavePrefs();}UpdateDisk();UpdateShotUi();C<TextBlock>("FolderLabel").Text=directory;C<TextBlock>("FolderLabel").ToolTip=directory;currentScene=Json.Str(scenesTask.Result,"currentProgramSceneName");
   loading=true;try{var box=C<ComboBox>("SceneBox");box.Items.Clear();object raw;if(scenesTask.Result.TryGetValue("scenes",out raw))foreach(object s in (object[])raw){string sn=Json.Str(Json.Obj(s),"sceneName");if(sn!=StrictPrivacy.CoverScene&&sn!=SrScene)box.Items.Add(sn);}box.SelectedItem=currentScene;
    var video=videoTask.Result;string size=Json.Int(video,"outputWidth")+","+Json.Int(video,"outputHeight");SelectTag(C<ComboBox>("ResolutionBox"),size);SelectContent(C<ComboBox>("FpsBox"),(Json.Int(video,"fpsNumerator")/Math.Max(1,Json.Int(video,"fpsDenominator"))).ToString());
    {int value=await engine.ReplayDuration();if(value>0)duration=value;C<Slider>("DurationSlider").Value=Math.Max(10,Math.Min(1800,duration));MarkDurationChip(duration);
     if(micName.Length>0){try{micMuted=await engine.IsMuted(micName);}catch(InvalidOperationException){micName="";}}
     await RefreshVideo();
     }
   }finally{loading=false;}
   C<TextBlock>("ConnectionLabel").Text="OBS подключён · профиль «"+profileName+"»";C<TextBlock>("FormatLabel").Text=outputMode=="Simple"?"Режим вывода OBS: простой":"Режим вывода OBS: расширенный";UpdateControls();
   {var names=new List<string>();foreach(var item in C<ComboBox>("SceneBox").Items)names.Add(Convert.ToString(item));await DetectModeScenes(names);}
   UpdateMode();ApplyPrivacy();privacy.Kick();await RefreshSR();RestoreReplay();ApplyGameOnce();StartAudioWatch();
  }finally{refreshing=false;}}
  static void SelectTag(ComboBox box,string tag){foreach(ComboBoxItem item in box.Items)if(Convert.ToString(item.Tag)==tag){box.SelectedItem=item;return;}}
  static void SelectContent(ComboBox box,string text){foreach(ComboBoxItem item in box.Items)if(Convert.ToString(item.Content)==text){box.SelectedItem=item;return;}}
  // Запись, повтор, звук и выход — понятные события движка; остальное (сцены, источники, плагины) — события OBS как есть.
  void OnRecordChanged(bool active,OutputPhase phase,string file){if(phase!=OutputPhase.Other)Log.Write(active?"Запись начата":"Запись остановлена");if(active&&!record)recordStart=DateTime.Now;record=active;UpdateControls();UpdateIndicator();if(phase==OutputPhase.Started){var g=gamesWatch.Current;Notify("Запись начата",g!=null?g.FileName:currentScene,ToastKind.Record);}if(!active&&file.Length>0){Track(file);Notify("Запись сохранена",Path.GetFileName(file),ToastKind.Ok);ClipsChanged();}}
  void OnReplayChanged(bool active,OutputPhase phase){replay=active;UpdateControls();
   if(phase==OutputPhase.Started){Log.Write("Буфер повтора включён");if(!replayWanted){replayWanted=true;SavePrefs();}}
   else if(phase==OutputPhase.Stopped){Log.Write("Буфер повтора выключен"+(busy?" (применение настроек)":exitStarted?" (OBS закрывается)":""));if(!busy&&!srSwitching&&!srOn&&!exitStarted&&replayWanted){replayWanted=false;SavePrefs();}}UpdateIndicator();}
  void OnReplaySaved(string file){lastSaved=AfterReplaySaved(file);Log.Write("Повтор сохранён: "+Path.GetFileName(lastSaved));Message("Сохранено: "+Path.GetFileName(lastSaved));Notify("Сохранён момент из повтора",Path.GetFileName(lastSaved),ToastKind.Ok);ClipsChanged();}
  void OnMuteChanged(string input,bool muted){System.Windows.Controls.Primitives.ToggleButton mb;if(muteButtons.TryGetValue(input,out mb))SetMuteLook(mb,muted);if(micName.Length>0&&input==micName){micMuted=muted;UpdateControls();}}
  void OnEvent(string type,Dictionary<string,object>d){
   if((type=="InputCreated"||type=="InputRemoved"||type=="InputNameChanged")&&C<StackPanel>("RecordPage").Visibility==Visibility.Visible)Run(RenderMixer);
   if(type=="VendorEvent")OnVendorEvent(d);
   else if(type=="CurrentProgramSceneChanged"){Run(UpdateReplayNest);currentScene=Json.Str(d,"sceneName");loading=true;C<ComboBox>("SceneBox").SelectedItem=currentScene;loading=false;UpdateMode();ApplyPrivacy();privacy.Kick();}
   else if(type=="SceneItemEnableStateChanged"){if(Json.Bool(d,"sceneItemEnabled")&&hidden.Remove(Json.Str(d,"sceneName")+"\n"+Json.Int(d,"sceneItemId"))){SavePrefs();ApplyPrivacy();}}
   else if(type=="SceneItemCreated"||type=="SceneItemRemoved"||type=="SceneNameChanged"||type=="SceneCreated"||type=="SceneRemoved"){sceneItems.Clear();Run(Refresh);}
   else if(type=="CurrentProfileChanged"||type=="CurrentSceneCollectionChanged"){sceneItems.Clear();Run(Refresh);}
  }
  void LoadPrefs(){foreach(var a in Actions)keys[a[0]]=Hotkey.Parse(a[2]);try{if(!File.Exists(prefsPath))return;var p=Json.Decode(File.ReadAllText(prefsPath));preferAlt=!p.ContainsKey("altZ")||Json.Bool(p,"altZ");object raw;
   if(p.TryGetValue("hotkeys",out raw)){var stored=Json.Obj(raw);foreach(var a in Actions)if(stored.ContainsKey(a[0]))keys[a[0]]=Hotkey.Parse(Json.Str(stored,a[0]));}
   else if(!preferAlt)keys["menu"]=Hotkey.Parse("Ctrl+Alt+Z"); // старая настройка «Клавиши»
   if(p.TryGetValue("excluded",out raw)&&raw is object[])foreach(object x in (object[])raw)if(Convert.ToString(x).Length>0)excluded.Add(Convert.ToString(x));
   if(p.TryGetValue("hiddenItems",out raw)&&raw is object[])foreach(object x in (object[])raw)if(!hidden.Contains(Convert.ToString(x)))hidden.Add(Convert.ToString(x));
   launchObs=Opt(p,"launchObs",true);keepReplay=Opt(p,"keepReplay",true);diagnostics=Opt(p,"diagnostics",true);replayWanted=Opt(p,"replayWanted",true);
   autoGames=Opt(p,"autoGames",true);gameRestore=Opt(p,"gameRestore",true);if(p.ContainsKey("gameAction"))gameAction=Json.Str(p,"gameAction");
   foreach(string k in new[]{"myGames","notGames","noCursor"})if(p.TryGetValue(k,out raw)&&raw is object[]){var set=k=="myGames"?myGames:k=="notGames"?notGames:noCursor;foreach(object x in (object[])raw)if(Convert.ToString(x).Length>0)set.Add(Convert.ToString(x));}
   if(p.TryGetValue("gameModes",out raw))foreach(var kv in Json.Obj(raw))gameModes[kv.Key]=Convert.ToString(kv.Value);
   if(p.TryGetValue("gameBackup",out raw)){var b=Json.Obj(raw);object inputs;if(b.TryGetValue("inputs",out inputs))foreach(var kv in Json.Obj(inputs))gameBackupLoaded[kv.Key]=Json.Obj(kv.Value);sceneBeforeLoaded=b.ContainsKey("before")?Json.Str(b,"before"):null;sceneByLoaded=b.ContainsKey("by")?Json.Str(b,"by"):null;}
   if(p.ContainsKey("audioMode"))audioMode=Json.Str(p,"audioMode");
   foreach(string k in new[]{"audioOnly","audioExcept"})if(p.TryGetValue(k,out raw)&&raw is object[]){var l=k=="audioOnly"?audioOnly:audioExcept;foreach(object x in (object[])raw){string v=Convert.ToString(x);if(v.Length>0&&!l.Contains(v))l.Add(v);}}
   if(p.TryGetValue("audio",out raw))audioState=AudioState.FromJson(Json.Obj(raw));
   hideNotifications=Opt(p,"hideNotifications",false);if(p.TryGetValue("strict",out raw))strictLoaded=Json.Obj(raw);
   if(p.TryGetValue("gamePrivacy",out raw))foreach(var kv in Json.Obj(raw))gamePrivacy[kv.Key]=Convert.ToString(kv.Value);
   if(p.ContainsKey("indicator"))indicatorMode=Json.Str(p,"indicator");if(p.ContainsKey("indicatorCorner"))indicatorCorner=Json.Str(p,"indicatorCorner");ffmpegPath=Json.Str(p,"ffmpeg");lastDirectory=Json.Str(p,"lastDirectory");previewSound=Opt(p,"previewSound",false);precise=Opt(p,"precise",false);toMp4=Opt(p,"toMp4",true);
   shotDir=Json.Str(p,"shotDir");if(p.ContainsKey("shotFormat"))shotFormat=Json.Str(p,"shotFormat");shotQuality=Math.Max(1,Math.Min(100,Json.Int(p,"shotQuality",90)));if(p.ContainsKey("shotSource"))shotSource=Json.Str(p,"shotSource");keysPanelOn=Opt(p,"keysPanel",true);toastsOn=Opt(p,"toasts",true);srOn=Opt(p,"srOn",false);srEncoder=Json.Str(p,"srEncoder");if(p.ContainsKey("srQuality"))srQuality=Json.Str(p,"srQuality");if(p.ContainsKey("srScale"))srScale=Json.Str(p,"srScale");srFpsDiv=Math.Max(1,Json.Int(p,"srFpsDiv",1));if(p.ContainsKey("srFormat"))srFormat=Json.Str(p,"srFormat");if(p.ContainsKey("srAudio"))srAudio=Json.Str(p,"srAudio");
   replayDir=Json.Str(p,"replayDir");if(p.TryGetValue("managed",out raw))managed.Load(raw);cleanupOn=Opt(p,"cleanup",false);cleanupDays=Json.Int(p,"cleanupDays",30);cleanupGb=Json.Int(p,"cleanupGb",0);lowDiskGb=Math.Max(1,Json.Int(p,"lowDiskGb",5));
   engineId=p.ContainsKey("engine")?Json.Str(p,"engine"):"user"; // до 1.7 меню работало только с OBS пользователя
   gridView=Opt(p,"galleryGrid",true);if(p.ContainsKey("previewVolume"))previewVolume=Math.Max(0,Math.Min(1,Convert.ToDouble(p["previewVolume"],System.Globalization.CultureInfo.InvariantCulture)));if(p.TryGetValue("clipGames",out raw))foreach(var kv in Json.Obj(raw))clipGames[kv.Key]=Convert.ToString(kv.Value);}catch{}}
  // Часть настроек привязана к сценам и источникам конкретного OBS: скрытые элементы, «шторка», копия настроек захвата игры,
  // источники звука, созданные меню, отдельный повтор (плагин). При смене движка они не подходят — забываются; остальное остаётся.
  string engineId;
  string EngineId{get{return engine.Home.Own?"own":"user";}}
  void ForgetOtherEngine(){
   if(engineId==null||engineId==EngineId){engineId=EngineId;return;}
   hidden.Clear();gameBackupLoaded.Clear();sceneBeforeLoaded=null;sceneByLoaded=null;strictLoaded=null;audioState=new AudioState();audioMode="all";srOn=false;
   Log.Write("Движок сменился ("+(engineId=="own"?"своя копия":"OBS пользователя")+" → "+(EngineId=="own"?"своя копия":"OBS пользователя")+"): настройки сцен, звука программ и отдельного повтора начаты заново");
   engineId=EngineId;SavePrefs();}
  readonly Dictionary<string,Dictionary<string,object>> gameBackupLoaded=new Dictionary<string,Dictionary<string,object>>();string sceneBeforeLoaded,sceneByLoaded;
  static bool Opt(Dictionary<string,object> p,string key,bool fallback){return p.ContainsKey(key)?Json.Bool(p,key):fallback;}
  void SavePrefs(){if(resetting)return;try{var hk=new Dictionary<string,string>();foreach(var a in Actions){Hotkey k=HotkeyFor(a[0]);hk[a[0]]=k==null?"":k.Serialize();}
   AtomicWrite(prefsPath,Json.Encode(Json.D("hotkeys",hk,"excluded",excluded.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray(),"hiddenItems",hidden.ToArray(),"launchObs",launchObs,"keepReplay",keepReplay,"diagnostics",diagnostics,"replayWanted",replayWanted,"autoGames",autoGames,"gameRestore",gameRestore,"gameAction",gameAction,"myGames",myGames.OrderBy(x=>x).ToArray(),"notGames",notGames.OrderBy(x=>x).ToArray(),"noCursor",noCursor.OrderBy(x=>x).ToArray(),"gameModes",gameModes,
    "gameBackup",gameSession==null?null:Json.D("inputs",gameSession.Backup,"before",gameSession.SceneBefore,"by",gameSession.SceneByGame),"audioMode",audioMode,"audioOnly",audioOnly.ToArray(),"audioExcept",audioExcept.ToArray(),"audio",audioState.ToJson(),"hideNotifications",hideNotifications,"strict",strict==null?null:strict.ToJson(),"gamePrivacy",gamePrivacy,"indicator",indicatorMode,"indicatorCorner",indicatorCorner,"ffmpeg",ffmpegPath,"lastDirectory",lastDirectory,"previewSound",previewSound,"precise",precise,"toMp4",toMp4,"shotDir",shotDir,"shotFormat",shotFormat,"shotQuality",shotQuality,"shotSource",shotSource,"keysPanel",keysPanelOn,"replayDir",replayDir,"managed",managed.ToJson(),"cleanup",cleanupOn,"cleanupDays",cleanupDays,"cleanupGb",cleanupGb,"lowDiskGb",lowDiskGb,"toasts",toastsOn,"srOn",srOn,"srEncoder",srEncoder,"srQuality",srQuality,"srScale",srScale,"srFpsDiv",srFpsDiv,"srFormat",srFormat,"srAudio",srAudio,"galleryGrid",gridView,"previewVolume",previewVolume,"clipGames",clipGames,"engine",EngineId)));}catch(Exception ex){Message("Настройки меню не сохранены: "+ex.Message);}}
  async Task<object[]> Items(string scene){object[] items;if(sceneItems.TryGetValue(scene,out items))return items;object raw;var r=await engine.Call("GetSceneItemList",Json.D("sceneName",scene));r.TryGetValue("sceneItems",out raw);items=(raw as object[])??new object[0];sceneItems[scene]=items;return items;}
  static bool Has(object[] items,string kind){return items.Any(i=>Json.Str(Json.Obj(i),"inputKind")==kind);}
  // «Только игра» — сцена с захватом игры без захвата монитора; «Игра и экран» — сцена с захватом монитора.
  async Task DetectModeScenes(List<string> names){gameScene=null;desktopScene=null;foreach(string n in names){if(n==StrictPrivacy.CoverScene)continue;var items=await Items(n);if(desktopScene==null&&Has(items,"monitor_capture"))desktopScene=n;else if(gameScene==null&&Has(items,"game_capture")&&!Has(items,"monitor_capture"))gameScene=n;}}
  async Task SetMode(bool desktop){EnsureReady();string scene=desktop?desktopScene:gameScene;if(scene==null)throw new InvalidOperationException(desktop?"В OBS нет сцены с захватом монитора":"В OBS нет сцены только с захватом игры");if(scene!=currentScene)await engine.Call("SetCurrentProgramScene",Json.D("sceneName",scene));}
  void UpdateMode(){bool game=currentScene!=null&&currentScene==gameScene,desk=currentScene!=null&&currentScene==desktopScene;C<Button>("ModeGameButton").Background=Brush(game?"#27351C":"#1D2227");C<Button>("ModeDesktopButton").Background=Brush(desk?"#27351C":"#1D2227");C<TextBlock>("ModeHint").Text=game?"Записывается только окно игры: Alt+Tab, рабочий стол и другие программы в запись не попадут.":desk?"Записывается весь монитор: после Alt+Tab видно всё, кроме приложений из исключений.":"Сейчас в OBS выбрана сцена «"+currentScene+"».";UpdatePrivacyLabel();}
  void UpdatePrivacyLabel(){var label=C<TextBlock>("PrivacyLabel");string now=privacy.Visible;bool covering=hidden.Count>0||coverOn;if(covering&&now.Length==0)now="весь экран (правило игры)";if(excluded.Count==0&&!hideNotifications&&GamePrivacy()==""){label.Visibility=Visibility.Collapsed;}else{label.Visibility=Visibility.Visible;label.Text=covering?"Сейчас скрыто от записи: "+now+" — в записи только игра":"Исключения: "+string.Join(", ",excluded.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).Concat(hideNotifications?new[]{PrivacyWatcher.NotificationsLabel}:new string[0]))+(currentScene==desktopScene?"":" · действуют в режиме «Игра и экран»");label.Foreground=Brush(covering?"#F2C46D":"#8E99A4");}string tip="Replay Overlay — OBS"+(covering?" · скрыто: "+now:"");if(tray!=null)tray.Text=tip.Length>63?tip.Substring(0,63):tip;}
  void ExclusionsChanged(){SavePrefs();privacy.SetExcluded(excluded);privacy.Enabled=engine.Connected&&(excluded.Count>0||hideNotifications);privacy.Kick();RenderExcluded();FillApps();UpdatePrivacyLabel();ApplyPrivacy();}
  void RenderExcluded(){Run(RenderPrivacyWarnings);var list=C<StackPanel>("ExcludedList");list.Children.Clear();if(excluded.Count==0){list.Children.Add(new TextBlock{Text="Список пуст",Foreground=Brush("#7F8A96"),FontSize=12,Margin=new Thickness(0,0,0,4)});return;}
   foreach(string name in excluded.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)){var row=new Grid{Margin=new Thickness(0,0,0,6)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.Children.Add(new TextBlock{Text=name+".exe",FontSize=13,Foreground=Brush("#E6EBDD"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});var remove=new Button{Content="Убрать",Padding=new Thickness(10,5,10,5),FontSize=11,ToolTip="Снова записывать "+name+".exe"};Grid.SetColumn(remove,1);string n=name;remove.Click+=(s,e)=>{excluded.Remove(n);ExclusionsChanged();};row.Children.Add(remove);list.Children.Add(row);}}
  void FillApps(){FillAppBox(C<ComboBox>("AppBox"),excluded);}
  void FillAppBox(ComboBox box,ICollection<string> skip){if(box.Visibility!=Visibility.Visible)return; // списки заменены окном выбора
   var current=box.SelectedItem as ComboBoxItem;string keep=current==null?null:Convert.ToString(current.Tag);box.Items.Clear();
   foreach(var app in PrivacyWatcher.RunningApps()){if(skip.Contains(app.Key))continue;string title=app.Value.Length>28?app.Value.Substring(0,27)+"…":app.Value;var item=new ComboBoxItem{Content=app.Key+".exe — "+title,Tag=app.Key,ToolTip=app.Value};box.Items.Add(item);if(app.Key==keep)box.SelectedItem=item;}
   if(box.SelectedItem==null&&box.Items.Count>0)box.SelectedIndex=0;}
  // Пока окно исключённого приложения видно, захват монитора в текущей сцене выключается; затем включается обратно.
  async void ApplyPrivacy(){if(privacyRunning){privacyAgain=true;return;}privacyRunning=true;try{do{privacyAgain=false;await SyncPrivacy();}while(privacyAgain);}catch(Exception ex){Message("Скрытие приложений: "+ex.Message);}finally{privacyRunning=false;try{UpdatePrivacyLabel();}catch(Exception ex){Log.Write("Подпись приватности: "+ex.Message);}}}
  async Task SyncPrivacy(){if(!engine.Connected||!correctProfile)return;bool want=WantHidden();bool useCover=strict!=null&&strict.On;bool changed=false;
   foreach(string key in hidden.ToList()){int cut=key.LastIndexOf('\n');string scene=key.Substring(0,cut);if(want&&scene==currentScene&&!useCover)continue;
    try{await engine.Call("SetSceneItemEnabled",Json.D("sceneName",scene,"sceneItemId",int.Parse(key.Substring(cut+1)),"sceneItemEnabled",true));}catch(InvalidOperationException){}
    hidden.Remove(key);changed=true;}
   if(useCover){await SyncCover(want);if(changed)SavePrefs();return;}
   if(want&&!string.IsNullOrEmpty(currentScene)){foreach(object raw in await Items(currentScene)){var item=Json.Obj(raw);if(Json.Str(item,"inputKind")!="monitor_capture")continue;int id=Json.Int(item,"sceneItemId");string key=currentScene+"\n"+id;if(hidden.Contains(key))continue;
    var state=await engine.Call("GetSceneItemEnabled",Json.D("sceneName",currentScene,"sceneItemId",id));if(!Json.Bool(state,"sceneItemEnabled"))continue;
    hidden.Add(key);changed=true;try{await engine.Call("SetSceneItemEnabled",Json.D("sceneName",currentScene,"sceneItemId",id,"sceneItemEnabled",false));}catch{hidden.Remove(key);throw;}}}
   if(changed)SavePrefs();}
  void RestoreHiddenNow(){if(!engine.Connected)return;foreach(string key in hidden.ToList()){int cut=key.LastIndexOf('\n');try{if(engine.Call("SetSceneItemEnabled",Json.D("sceneName",key.Substring(0,cut),"sceneItemId",int.Parse(key.Substring(cut+1)),"sceneItemEnabled",true)).Wait(1500))hidden.Remove(key);}catch{}}
   if(strict!=null)foreach(string sc in coverScenesOn.ToList()){try{if(engine.Call("SetSceneItemEnabled",Json.D("sceneName",sc,"sceneItemId",strict.CoverItems[sc],"sceneItemEnabled",false)).Wait(1500))coverScenesOn.Remove(sc);}catch{}}
   SavePrefs();}
  // ——— Фоновая работа ———
  void BindBackgroundOptions(){var launch=C<CheckBox>("LaunchObsBox");var keep=C<CheckBox>("KeepReplayBox");var diag=C<CheckBox>("DiagnosticsBox");
   launch.IsChecked=launchObs;keep.IsChecked=keepReplay;diag.IsChecked=diagnostics;
   launch.Click+=(s,e)=>{launchObs=launch.IsChecked==true;SavePrefs();};
   keep.Click+=(s,e)=>{keepReplay=keep.IsChecked==true;if(keepReplay)replayWanted=true;SavePrefs();RestoreReplay();};
   diag.Click+=(s,e)=>{diagnostics=diag.IsChecked==true;SavePrefs();if(!diagnostics)C<TextBlock>("HealthLabel").Text="";};
   C<Button>("LogButton").Click+=(s,e)=>OpenLog();}
  void OpenLog(){Log.Write("Журнал открыт");try{Process.Start(new ProcessStartInfo("notepad.exe","\""+Log.FilePath+"\""){UseShellExecute=true});}catch(Exception ex){Message(ex.Message);}}
  // Связь с OBS появилась или пропала. Пропала без события ExitStarted — значит OBS упал или его сняли: запускаем снова.
  void OnConnection(bool connected){
   if(connected==wasConnected)return;wasConnected=connected;
   if(connected){connections++;skips.Mark(DateTime.Now);Log.Write(engine.Home.Own?"Подключено к движку записи":"Подключено к OBS");exitStarted=false;statsTimeouts=0;lastStats=null;statsTimer.Start();PollStats();CheckEngineEncoder();return;}
   statsTimer.Stop();C<TextBlock>("HealthLabel").Text="";appliedPid=0;sessionTimer.Stop();
   // OBS пользователя, закрытый им самим, не трогаем. Своя копия нужна меню всегда: закрыл её не меню — запускаем снова.
   if(exitStarted&&!engine.Home.Own){Log.Write("OBS закрыт");return;}
   if(engineRestarting)return; // меню само перезапускает движок (смена кодировщика)
   Log.Write(exitStarted?"Движок записи закрыт не меню — запускаю снова":engine.Home.Own?"Связь с движком записи потеряна (сбой или зависание)":"Связь с OBS потеряна без закрытия OBS (сбой или зависание)");
   if((!launchObs&&!engine.Home.Own)||preview)return;
   var wait=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};wait.Tick+=(s,e)=>{wait.Stop();if(engine.Connected||exiting)return;
    if(engine.IsRunning){Warn("hang","OBS не отвечает. Если он завис — закройте его: меню запустит OBS заново и включит повтор.");return;}
    relaunches.RemoveAll(t=>(DateTime.Now-t).TotalMinutes>10);if(relaunches.Count>=3){Warn("relaunch","OBS падает слишком часто — автоматический перезапуск приостановлен.");return;}
    relaunches.Add(DateTime.Now);if(LaunchObsHidden("после сбоя"))Notify("OBS перезапущен","После сбоя OBS запущен снова, мгновенный повтор включится сам.");};wait.Start();}
  // Своя копия: кодировщик профиля должен быть среди найденных OBS при этом запуске. Нет — копия перезапускается,
  // и подготовка перед запуском выбирает лучший из найденных. Не чаще раза за сеанс меню.
  bool engineRestarted,engineRestarting;
  async void CheckEngineEncoder(){
   if(!engine.Home.Own||engineRestarted)return;
   try{string cur=null;
    for(int i=0;cur==null;i++){try{cur=Json.Str(await engine.Call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecEncoder")),"parameterValue");} // сразу после запуска движок отвечает «ещё не готов» (207)
     catch(InvalidOperationException ex){if(!ex.Message.EndsWith("(207)")||i>=20||!engine.Connected)throw;}if(cur==null)await Task.Delay(1000);}
    var found=EncoderCatalog.FromLatestLog();if(found.Count==0||found.Any(e=>e.Id==cur))return;
    engineRestarted=true;Log.Write("Движок записи: кодировщика «"+cur+"» на этом компьютере нет — перезапуск с другим");
    engineRestarting=true;engine.Close();for(int i=0;i<150&&engine.IsRunning;i++)await Task.Delay(100);
    if(!engine.IsRunning)LaunchObsHidden("смена кодировщика");else Log.Write("Движок записи не закрылся — кодировщик сменится при следующем запуске");}
   catch(Exception ex){Log.Write("Проверка кодировщика: "+ex.Message);}
   finally{engineRestarting=false;}}
  // Сразу после запуска OBS отвечает «ещё не готов» (код 207) — ждём до 20 секунд, не считая это ошибкой.
  async Task RefreshWhenReady(){for(int i=0;;i++){try{await Refresh();return;}catch(InvalidOperationException ex){if(!ex.Message.EndsWith("(207)")||i>=20||!engine.Connected)throw;}await Task.Delay(1000);}}
  // Запускает OBS свёрнутым в трей, если он ещё не запущен.
  bool LaunchObsHidden(string reason){engine.Home.PreferredFolder=lastDirectory;return engine.LaunchHidden(keepReplay&&replayWanted&&!srOn,reason);}
  // Буфер должен работать (его не выключали вручную), а он выключен — включаем.
  async void RestoreReplay(){
   if(srOn)return; // отдельный повтор (Source Record) работает сам, обычный буфер не нужен
   if(!keepReplay||!replayWanted||!engine.Connected||!correctProfile||busy||!replayAvailable||replay)return;
   try{await engine.StartReplay();Log.Write("Буфер повтора включён автоматически");if(connections>1&&!window.IsVisible)Notify("Мгновенный повтор","Включён снова");}
   catch(Exception ex){Log.Write("Не удалось включить буфер: "+ex.Message);}}
  // Раз в 10 с: пропуски кадров, место на диске, отвечает ли OBS. Сводка в журнал — раз в 5 минут.
  readonly SkipLedger skips=new SkipLedger();
  async void PollStats(){
   if(!engine.Connected)return;
   try{var cur=await engine.Stats();
    if(statsTimeouts>=2)Log.Write("OBS снова отвечает");statsTimeouts=0;
    var now=DateTime.Now;bool quiet=skips.Quiet(now);skips.Add(lastStats,cur,now);
    if(diagnostics&&!quiet)foreach(var p in Health.Problems(lastStats,cur))Warn(p.Key,p.Value); // при запуске и закрытии игры пропуски ожидаемы
    lastStats=cur;if(statsPolls++%30==0)Log.Write("Сводка: "+Health.Summary(cur)+" · пропуски в игре "+skips.InGame+", при запуске и закрытии игр "+skips.Transition+" (кодировщик "+cur.OutputSkipped+"/"+cur.OutputTotal+", отрисовка "+cur.RenderSkipped+"/"+cur.RenderTotal+") · меню "+(Environment.WorkingSet/1048576)+" МБ");
    if(diagnostics)C<TextBlock>("HealthLabel").Text=Health.Summary(cur);}
   catch(TimeoutException){statsTimeouts++;Log.Write("OBS не ответил на запрос статистики ("+statsTimeouts+")");if(statsTimeouts==2)Warn("hang","OBS не отвечает. Если он завис — закройте его: меню запустит OBS заново и включит повтор.");}
   catch(Exception){}}
  // Предупреждение: в журнал, в строку меню и уведомлением (не попадает в запись). Один вид — не чаще раза в 5 минут.
  void Warn(string key,string text){DateTime last;if(warned.TryGetValue(key,out last)&&(DateTime.Now-last).TotalMinutes<5)return;warned[key]=DateTime.Now;Log.Write("Предупреждение: "+text);Message(text);Notify("Внимание",text,ToastKind.Warn);}
  // ——— Игры ———
  void BindGames(){
   gameSession=new GameSession((r,d)=>engine.Call(r,d));foreach(var kv in gameBackupLoaded)gameSession.Backup[kv.Key]=kv.Value;gameSession.SceneBefore=string.IsNullOrEmpty(sceneBeforeLoaded)?null:sceneBeforeLoaded;gameSession.SceneByGame=string.IsNullOrEmpty(sceneByLoaded)?null:sceneByLoaded;
   var auto=C<CheckBox>("AutoGamesBox");var restore=C<CheckBox>("GameRestoreBox");var action=C<ComboBox>("GameActionBox");
   auto.IsChecked=autoGames;restore.IsChecked=gameRestore;SelectTag(action,gameAction);
   auto.Click+=(s,e)=>{autoGames=auto.IsChecked==true;gamesWatch.Enabled=autoGames;if(!autoGames){appliedPid=0;RestoreAfterGame();}SavePrefs();RenderGames();};
   restore.Click+=(s,e)=>{gameRestore=restore.IsChecked==true;SavePrefs();};
   action.SelectionChanged+=(s,e)=>{var it=action.SelectedItem as ComboBoxItem;if(it!=null){gameAction=Convert.ToString(it.Tag);SavePrefs();}};
   C<ComboBox>("GameAppBox").DropDownOpened+=(s,e)=>FillAppBox(C<ComboBox>("GameAppBox"),myGames);
   C<ComboBox>("NotGameAppBox").DropDownOpened+=(s,e)=>FillAppBox(C<ComboBox>("NotGameAppBox"),notGames);
   C<Button>("AddGameButton").Click+=(s,e)=>OpenPicker("Добавить игру","Меню будет считать эту программу игрой и захватывать её окно.",myGames,n=>{myGames.Add(n);notGames.Remove(n);GamesChanged();Message(n+".exe — в «Моих играх»");});
   C<Button>("AddNotGameButton").Click+=(s,e)=>OpenPicker("Не считать игрой","Программа не будет определяться как игра, даже если она на весь экран.",notGames,n=>{notGames.Add(n);myGames.Remove(n);GamesChanged();Message(n+".exe больше не считается игрой");});
   gamesWatch.Started+=OnGameStarted;gamesWatch.Stopped+=OnGameStopped;
   gamesWatch.SetLists(myGames,notGames);} // включается в InitWindow, когда меню собрано полностью
  void GamesChanged(){gamesWatch.SetLists(myGames,notGames);SavePrefs();RenderGames();FillAppBox(C<ComboBox>("GameAppBox"),myGames);FillAppBox(C<ComboBox>("NotGameAppBox"),notGames);gamesWatch.Recheck();}
  void UpdateGameLabel(){var g=gamesWatch.Current;var l=C<TextBlock>("GameLabel");l.Visibility=g==null?Visibility.Collapsed:Visibility.Visible;if(g!=null)l.Text="Игра: "+g.FileName;
   C<TextBlock>("GameState").Text=!autoGames?"Распознавание игр выключено.":g==null?"Игра не обнаружена — откройте игру, меню найдёт её само.":"Сейчас: "+g.FileName+" ("+g.Reason+").";}
  void RenderGames(){UpdateGameLabel();FillAppBox(C<ComboBox>("GameAppBox"),myGames);FillAppBox(C<ComboBox>("NotGameAppBox"),notGames);
   var list=C<StackPanel>("GameList");list.Children.Clear();
   if(myGames.Count==0)list.Children.Add(new TextBlock{Text="Пусто. Игры из Steam, Epic и других библиотек находятся сами; сюда добавляют остальные и задают им свой режим.",Foreground=Brush("#7F8A96"),FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,6)});
   foreach(string name in myGames.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)){string n=name;
    var row=new StackPanel{Margin=new Thickness(0,0,0,10)};row.Children.Add(new TextBlock{Text=n+".exe",FontSize=13,Foreground=Brush("#E6EBDD"),TextTrimming=TextTrimming.CharacterEllipsis});
    var g=new Grid();g.ColumnDefinitions.Add(new ColumnDefinition());g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(8)});g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(4)});g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    var mode=new ComboBox{ToolTip="Что делать со сценой при запуске этой игры"};foreach(var o in new[]{new[]{"default","Как по умолчанию"},new[]{"game","Только игра"},new[]{"desktop","Игра и экран"},new[]{"none","Не менять сцену"}})mode.Items.Add(new ComboBoxItem{Content=o[1],Tag=o[0]});
    string m;SelectTag(mode,gameModes.TryGetValue(n,out m)?m:"default");mode.SelectionChanged+=(s,e)=>{var it=mode.SelectedItem as ComboBoxItem;if(it==null)return;string t=Convert.ToString(it.Tag);if(t=="default")gameModes.Remove(n);else gameModes[n]=t;SavePrefs();};g.Children.Add(mode);
    var cursor=new CheckBox{Content="курсор",IsChecked=!noCursor.Contains(n),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,7,0,0),ToolTip="Показывать курсор мыши в записи этой игры"};Grid.SetColumn(cursor,2);
    cursor.Click+=(s,e)=>{if(cursor.IsChecked==true)noCursor.Remove(n);else noCursor.Add(n);SavePrefs();var cur=gamesWatch.Current;if(cur!=null&&string.Equals(cur.Exe,n,StringComparison.OrdinalIgnoreCase)){appliedPid=0;OnGameStarted(cur);}};g.Children.Add(cursor);
    var remove=new Button{Content="Убрать",Padding=new Thickness(10,5,10,5),FontSize=11,Margin=new Thickness(0,7,0,0)};Grid.SetColumn(remove,4);remove.Click+=(s,e)=>{myGames.Remove(n);gameModes.Remove(n);noCursor.Remove(n);gamePrivacy.Remove(n);GamesChanged();};g.Children.Add(remove);
    var priv=new ComboBox{ToolTip="Скрытие программ во время этой игры",Margin=new Thickness(0,6,0,0)};foreach(var o in new[]{new[]{"default","Скрытие программ: как обычно"},new[]{"screen","Скрывать весь экран, пока идёт игра"},new[]{"none","Не скрывать программы во время игры"}})priv.Items.Add(new ComboBoxItem{Content=o[1],Tag=o[0]});
    string pv;SelectTag(priv,gamePrivacy.TryGetValue(n,out pv)?pv:"default");priv.SelectionChanged+=(s,e)=>{var it=priv.SelectedItem as ComboBoxItem;if(it==null)return;string t=Convert.ToString(it.Tag);if(t=="default")gamePrivacy.Remove(n);else gamePrivacy[n]=t;SavePrefs();ApplyPrivacy();UpdatePrivacyLabel();};
    row.Children.Add(g);row.Children.Add(priv);list.Children.Add(row);}
   var nl=C<StackPanel>("NotGameList");nl.Children.Clear();
   if(notGames.Count==0)nl.Children.Add(new TextBlock{Text="Пусто",Foreground=Brush("#7F8A96"),FontSize=12,Margin=new Thickness(0,0,0,4)});
   foreach(string name in notGames.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)){string n=name;var row=new Grid{Margin=new Thickness(0,0,0,6)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    row.Children.Add(new TextBlock{Text=n+".exe",FontSize=13,Foreground=Brush("#E6EBDD"),VerticalAlignment=VerticalAlignment.Center});var remove=new Button{Content="Убрать",Padding=new Thickness(10,5,10,5),FontSize=11};Grid.SetColumn(remove,1);remove.Click+=(s,e)=>{notGames.Remove(n);GamesChanged();};row.Children.Add(remove);nl.Children.Add(row);}}
  // Игра найдена: захват игры — на её окно, сцена — по профилю игры или по умолчанию.
  // Сцену с заставкой во время трансляции не трогаем: там её выбрали намеренно.
  async void OnGameStarted(GameInfo g){
   UpdateGameLabel();ApplyPrivacy();if(appliedPid!=g.Pid)skips.Mark(DateTime.Now);if(appliedPid!=g.Pid)Log.Write("Найдена игра: "+g.FileName+" ("+g.Reason+")");
   if(!engine.Connected||!correctProfile||gameBusy||gameSession==null)return;gameBusy=true;
   try{
    foreach(string line in await gameSession.Capture(g,!noCursor.Contains(g.Exe)))Log.Write(line);
    foreach(string line in await gameSession.FitToCanvas())Log.Write(line);
    string mode;if(!gameModes.TryGetValue(g.Exe,out mode))mode=gameAction;
    string target=mode=="game"?gameScene:mode=="desktop"?desktopScene:null;
    if(target!=null&&target!=currentScene){
     bool fromCapture=!string.IsNullOrEmpty(currentScene)&&GameSession.HasCapture(await Items(currentScene));
     bool streaming=await engine.IsStreaming();
     if(fromCapture||!streaming){await gameSession.SwitchScene(target,currentScene);Log.Write("Сцена для игры: "+target);}
     else Log.Write("Идёт трансляция со сценой «"+currentScene+"» — сцену не меняю");}
    bool first=appliedPid!=g.Pid;appliedPid=g.Pid;SavePrefs();
    if(first&&!window.IsVisible)Notify("Игра: "+g.FileName,mode=="game"?"Записывается только окно игры":mode=="desktop"?"Записывается игра и весь экран":"Захват настроен на окно игры");
   }catch(Exception ex){Log.Write("Игра: не удалось настроить захват: "+ex.Message);Message("Игра: "+ex.Message);}
   finally{gameBusy=false;}}
  void OnGameStopped(GameInfo g){skips.Mark(DateTime.Now);Log.Write("Игра закрыта: "+g.FileName);appliedPid=0;UpdateGameLabel();RestoreAfterGame();ApplyPrivacy();}
  // После игры: прежняя сцена (если её не меняли вручную) и прежние настройки захвата игры.
  async void RestoreAfterGame(){
   if(gameSession==null||(gameSession.Backup.Count==0&&gameSession.SceneByGame==null))return;
   if(!gameRestore){gameSession.Backup.Clear();gameSession.SceneBefore=null;gameSession.SceneByGame=null;SavePrefs();return;}
   if(!engine.Connected||!correctProfile)return; // вернём при следующем подключении
   try{foreach(string line in await gameSession.Restore(currentScene,true))Log.Write("После игры: "+line);SavePrefs();}
   catch(Exception ex){Log.Write("После игры не удалось вернуть настройки: "+ex.Message);}}
  void ApplyGameOnce(){var g=gamesWatch.Current;if(g!=null){if(appliedPid!=g.Pid)OnGameStarted(g);}else RestoreAfterGame();}
  // ——— Звук ———
  List<string> CaptureScenes(){var l=new List<string>();if(gameScene!=null)l.Add(gameScene);if(desktopScene!=null&&desktopScene!=gameScene)l.Add(desktopScene);if(l.Count==0&&!string.IsNullOrEmpty(currentScene))l.Add(currentScene);return l;}
  void BindAudio(){
   audio=new AudioSetup((r,d)=>engine.Call(r,d),audioState);
   var mode=C<ComboBox>("AudioModeBox");SelectTag(mode,audioMode);
   mode.SelectionChanged+=(s,e)=>{var it=mode.SelectedItem as ComboBoxItem;if(it==null)return;string m=Convert.ToString(it.Tag);if(m!=audioMode)Run(()=>SetAudioMode(m));};
   foreach(var l in new[]{new[]{"LayoutCommon","common"},new[]{"LayoutSplit","split"},new[]{"LayoutCustom","custom"}}){string tag=l[1];C<RadioButton>(l[0]).Click+=(s,e)=>{chosenLayout=tag;UpdateLayoutUi();};}
   C<Button>("LayoutApply").Click+=(s,e)=>Run(ApplyTrackLayout);
   C<Button>("DuplicateFix").Click+=(s,e)=>Run(FixDuplicatesNow);
   C<ComboBox>("AudioAppBox").DropDownOpened+=(s,e)=>FillAudioApps();
   C<Button>("AddAudioAppButton").Click+=(s,e)=>{var l=audioMode=="except"?audioExcept:audioOnly;OpenPicker(audioMode=="except"?"Не записывать звук приложения":"Записывать звук приложения",audioMode=="except"?"Звук этой программы не попадёт в запись.":"В запись попадёт звук только выбранных программ (и игры, Discord, микрофона).",l,n=>{if(!l.Contains(n))l.Add(n);SavePrefs();RenderAudioApps();Run(ReapplyAudio);});};
   C<Button>("AddDeviceButton").Click+=(s,e)=>Run(AddDevice);
   sessionTimer.Tick+=(s,e)=>WatchSessions();volumeTimer.Tick+=(s,e)=>FlushVolumes();}
  void RenderAudio(){RenderAudioApps();FillAudioApps();Run(RenderMixer);Run(FillDevices);}
  // Режим звука программ: all — весь звук компьютера, only — только выбранные, except — все, кроме выбранных.
  async Task SetAudioMode(string m){EnsureReady();if(audioBusy)throw new InvalidOperationException("Подождите — звук ещё настраивается");audioBusy=true;
   try{foreach(string line in await audio.ApplyMode(m,m=="only"?(ICollection<string>)audioOnly:new List<string>(),CaptureScenes()))if(line.Length>0)Log.Write("Звук: "+line);
    audioMode=m;discordHasSource=await audio.ProcessSource("Discord.exe")!=null;SavePrefs();
    Message(m=="all"?"Записывается весь звук компьютера":m=="only"?"Записывается звук только выбранных программ":"Записывается звук всех программ, кроме выбранных");}
   catch{SelectTag(C<ComboBox>("AudioModeBox"),audioMode);throw;}
   finally{audioBusy=false;RenderAudioApps();StartAudioWatch();Run(RenderMixer);}}
  async Task ReapplyAudio(){
   if(audioMode=="only"){await SetAudioMode("only");return;}
   if(audioMode!="except")return;
   foreach(string n in audioExcept.ToList()){string src=AudioSetup.AppPrefix+n;if(audioState.Created.Contains(src)){await audio.Remove(src);Log.Write("Звук: «"+n+".exe» больше не записывается");}}
   SavePrefs();await RenderMixer();}
  async void StartAudioWatch(){
   if(audioMode!="except"||!engine.Connected||!correctProfile){sessionTimer.Stop();return;}
   try{discordHasSource=await audio.ProcessSource("Discord.exe")!=null;}catch{}
   if(!sessionTimer.IsEnabled){sessionTimer.Start();WatchSessions();}}
  // «Все, кроме»: каждые 2 с — новые программы со звуком получают свой источник. Игры, OBS и программы со своим источником пропускаются.
  async void WatchSessions(){
   if(audioBusy||!engine.Connected||audioMode!="except")return;audioBusy=true;
   try{var active=AudioSessions.Active();var skip=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"obs64","obs32","ReplayOverlay","audiodg"};
    if(discordHasSource)skip.Add("Discord");var g=gamesWatch.Current;if(g!=null)skip.Add(g.Exe);
    foreach(var kv in active)if(myGames.Contains(kv.Key)||GameRules.Library(kv.Value)!=null)skip.Add(kv.Key);
    var missing=AudioSetup.MissingApps(active.Keys,new HashSet<string>(audioExcept,StringComparer.OrdinalIgnoreCase),skip,audioState);
    foreach(string app in missing){string line=await audio.EnsureApp(app,CaptureScenes());if(line.Length>0)Log.Write("Звук: "+line);}
    if(missing.Count>0){SavePrefs();if(C<StackPanel>("RecordPage").Visibility==Visibility.Visible)await RenderMixer();}}
   catch(Exception ex){Log.Write("Звук программ: "+ex.Message);}
   finally{audioBusy=false;}}
  string chosenLayout="split";int recTracksNow=31;
  int CustomRecTracks(){int m=0;for(int i=0;i<6;i++)if(C<System.Windows.Controls.Primitives.ToggleButton>("RecT"+(i+1)).IsChecked==true)m|=1<<i;return m;}
  void UpdateLayoutUi(){
   C<StackPanel>("RecTracksPanel").Visibility=chosenLayout=="custom"?Visibility.Visible:Visibility.Collapsed;
   C<TextBlock>("LayoutHint").Text=chosenLayout=="common"?"Одна дорожка: игра, Discord, микрофон и программы смешаны. Файл открывается и звучит одинаково везде.":chosenLayout=="split"?"1 — всё вместе, 2 — игра, 3 — микрофон, 4 — Discord, 5 — рабочий стол и программы. В монтаже каждую можно отдельно приглушить. Плееры и Discord играют только дорожку 1.":"Вы сами выбираете записываемые дорожки ниже и раскладываете источники стрелкой «›». Меню проверит, нет ли двойного звука.";}
  // Двойной звук на записываемых дорожках: предупреждение и кнопка «Убрать двойной звук».
  async Task RefreshDuplicates(){
   var dw=C<TextBlock>("DuplicateWarn");var fix=C<Button>("DuplicateFix");
   try{var dups=AudioSetup.Duplicates(await audio.Sources(),recTracksNow);dw.Visibility=fix.Visibility=dups.Count>0?Visibility.Visible:Visibility.Collapsed;dw.Text=string.Join("\n",dups.Select(x=>"⚠ "+x));}
   catch(Exception ex){Log.Write("Проверка двойного звука: "+ex.Message);}}
  async Task FixDuplicatesNow(){EnsureReady();
   var plan=AudioSetup.FixDuplicates(await audio.Sources(),recTracksNow);
   foreach(var kv in plan){await audio.SetMask(kv.Key,kv.Value);Log.Write("Звук: «"+kv.Key+"» снят с дорожек, где уже есть звук рабочего стола");}
   Message(plan.Count==0?"Двойного звука нет":"Двойной звук убран: "+string.Join(", ",plan.Keys)+" — этот звук и так есть в звуке рабочего стола");await RenderMixer();}
  // Применить раскладку дорожек: маски источников и список записываемых дорожек. Если список меняется — буфер повтора перезапускается.
  async Task ApplyTrackLayout(){EnsureReady();string layout=chosenLayout;int custom=CustomRecTracks();
   if(layout=="custom"&&custom==0)throw new InvalidOperationException("Выберите хотя бы одну дорожку для записи");
   MessageBoxResult ok;dialogOpen=true;
   try{ok=MessageBox.Show(window,(layout=="common"?"Весь звук пойдёт в одну дорожку.":layout=="split"?"Звук разделится: 1 — всё вместе, 2 — игра, 3 — микрофон, 4 — Discord, 5 — рабочий стол.":"Будут записываться выбранные дорожки; источники останутся на своих дорожках.")+"\n\nЕсли меняется список записываемых дорожек, буфер повтора перезапустится и накопленная история сбросится. Продолжить?","Replay — звук",MessageBoxButton.OKCancel,MessageBoxImage.Question);}
   finally{dialogOpen=false;window.Activate();}
   if(ok!=MessageBoxResult.OK)return;
   bool discord=Process.GetProcessesByName("Discord").Length>0||File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Discord\Update.exe"));
   busy=true;UpdateControls();bool restart=false;
   try{
    if(layout=="split"&&discord&&await audio.ProcessSource("Discord.exe")==null&&CaptureScenes().Count>0){await audio.CreateInScenes("Discord — звук","wasapi_process_output_capture",Json.D("window","::Discord.exe","priority",2),CaptureScenes());Log.Write("Звук: создан источник «Discord — звук»");}
    var log=await audio.ApplyLayout(layout,audioMode=="all",custom);foreach(string l in log)Log.Write("Звук: "+l);SavePrefs();
    restart=replay&&log.Any(l=>l.StartsWith("в запись"));
    if(restart)await engine.RestartReplay();}
   finally{busy=false;UpdateControls();}
   Message((layout=="common"?"Звук — одной дорожкой":layout=="split"?"Звук разделён по дорожкам":"Раскладка дорожек сохранена")+(restart?" · буфер запущен заново":""));await RenderMixer();}
  async Task FillDevices(){
   if(!engine.Connected)return;var box=C<ComboBox>("DeviceBox");box.Items.Clear();
   foreach(bool input in new[]{true,false})foreach(var dv in await audio.Devices(input))box.Items.Add(new ComboBoxItem{Content=(input?"Микрофон: ":"Вывод: ")+dv.Key,Tag=(input?"in":"out")+"|"+dv.Key.Replace("|","/")+"|"+dv.Value,ToolTip=dv.Key});
   if(box.Items.Count>0)box.SelectedIndex=0;}
  async Task AddDevice(){EnsureReady();var it=C<ComboBox>("DeviceBox").SelectedItem as ComboBoxItem;if(it==null)throw new InvalidOperationException("Выберите устройство");
   var parts=Convert.ToString(it.Tag).Split(new[]{'|'},3);string name=await audio.AddDevice(parts[0]=="in",parts[1],parts[2],CaptureScenes());
   SavePrefs();Log.Write("Звук: добавлено «"+name+"»");Message("Добавлено: "+name);await RenderMixer();}
  void FillAudioApps(){var box=C<ComboBox>("AudioAppBox");if(box.Visibility!=Visibility.Visible)return;var listed=audioMode=="except"?audioExcept:audioOnly;var keep=box.SelectedItem as ComboBoxItem;string k=keep==null?null:Convert.ToString(keep.Tag);box.Items.Clear();
   var sound=AudioSessions.Active();var all=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var kv in PrivacyWatcher.RunningApps())all[kv.Key]=kv.Value;foreach(var kv in sound)if(!all.ContainsKey(kv.Key))all[kv.Key]="";
   foreach(var kv in all){if(listed.Contains(kv.Key,StringComparer.OrdinalIgnoreCase))continue;var item=new ComboBoxItem{Content=kv.Key+".exe"+(sound.ContainsKey(kv.Key)?" — звук":"")+(kv.Value.Length>0?" · "+(kv.Value.Length>20?kv.Value.Substring(0,19)+"…":kv.Value):""),Tag=kv.Key};box.Items.Add(item);if(kv.Key==k)box.SelectedItem=item;}
   if(box.SelectedItem==null&&box.Items.Count>0)box.SelectedIndex=0;}
  void RenderAudioApps(){
   C<TextBlock>("AudioModeHint").Text=audioMode=="all"?"Весь звук компьютера пишется одним источником «Звук рабочего стола».":audioMode=="only"?"Пишутся только программы из списка, а также игра, Discord и микрофон.":"Пишется звук всех программ, кроме списка. Новая программа со звуком добавляется сама за 2 секунды; системные звуки Windows не пишутся.";
   C<StackPanel>("AudioAppsPanel").Visibility=audioMode=="all"?Visibility.Collapsed:Visibility.Visible;
   C<TextBlock>("AudioAppsTitle").Text=audioMode=="except"?"Не записывать звук программ":"Записывать звук программ";
   var list=C<StackPanel>("AudioAppList");list.Children.Clear();var l=audioMode=="except"?audioExcept:audioOnly;
   if(l.Count==0)list.Children.Add(new TextBlock{Text="Список пуст",Foreground=Brush("#7F8A96"),FontSize=12,Margin=new Thickness(0,0,0,4)});
   foreach(string name in l.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToList()){string n=name;var row=new Grid{Margin=new Thickness(0,0,0,6)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    row.Children.Add(new TextBlock{Text=n+".exe",FontSize=13,Foreground=Brush("#E6EBDD"),VerticalAlignment=VerticalAlignment.Center});var rm=new Button{Content="Убрать",Padding=new Thickness(10,5,10,5),FontSize=11};Grid.SetColumn(rm,1);
    rm.Click+=(s,e)=>{l.Remove(n);SavePrefs();RenderAudioApps();if(audioMode=="only")Run(ReapplyAudio);};row.Children.Add(rm);list.Children.Add(row);}}
  // Микшер: громкость, вкл/выкл, дорожки 1–6 и задержка звука для каждого источника со звуком.
  async Task RenderMixer(){
   if(!engine.Connected||audio==null)return;object raw;(await engine.Call("GetInputList")).TryGetValue("inputs",out raw);
   var names=new string[6];for(int i=0;i<6;i++){try{names[i]=Json.Str(await engine.Call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","Track"+(i+1)+"Name")),"parameterValue");}catch(InvalidOperationException){names[i]="";}}trackNamesNow=names;
   int rec=0;try{int.TryParse(Json.Str(await engine.Call("GetProfileParameter",Json.D("parameterCategory","AdvOut","parameterName","RecTracks")),"parameterValue"),out rec);}catch(InvalidOperationException){}
   var parts=new List<string>();for(int i=0;i<6;i++)if((rec&(1<<i))!=0)parts.Add((i+1)+(names[i].Length>0?" "+names[i]:""));
   recTracksNow=rec;chosenLayout=AudioSetup.LayoutOf(rec);foreach(var l in new[]{new[]{"LayoutCommon","common"},new[]{"LayoutSplit","split"},new[]{"LayoutCustom","custom"}})C<RadioButton>(l[0]).IsChecked=l[1]==chosenLayout;
   for(int i=0;i<6;i++)C<System.Windows.Controls.Primitives.ToggleButton>("RecT"+(i+1)).IsChecked=(rec&(1<<i))!=0;UpdateLayoutUi();
   await RefreshDuplicates();
   C<TextBlock>("TracksInfo").Text=outputMode!="Advanced"?"Дорожки записи настраиваются в расширенном режиме вывода OBS.":parts.Count>0?"Записываются дорожки: "+string.Join(", ",parts)+".":"Дорожки записи не выбраны.";
   var rows=new List<UIElement>();var buttons=new Dictionary<string,System.Windows.Controls.Primitives.ToggleButton>();
   foreach(object o in (raw as object[])??new object[0]){string name=Json.Str(Json.Obj(o),"inputName"),kind=Json.Str(Json.Obj(o),"inputKind");
    double db;try{db=await engine.VolumeDb(name);}catch(InvalidOperationException){continue;} // источник без звука
    bool muted=await engine.IsMuted(name);int mask=await audio.GetMask(name);
    int offset=await engine.SyncOffset(name);
    rows.Add(MixerRow(name,kind,muted,db,mask,offset,names,buttons));}
   var list=C<StackPanel>("MixerList");list.Children.Clear();muteButtons.Clear();foreach(var kv in buttons)muteButtons[kv.Key]=kv.Value;foreach(var r in rows)list.Children.Add(r);
   if(rows.Count==0)list.Children.Add(new TextBlock{Text="Источников звука нет",Foreground=Brush("#7F8A96"),FontSize=12});}
  UIElement MixerRow(string name,string kind,bool muted,double db,int mask,int offset,string[] trackNames,Dictionary<string,System.Windows.Controls.Primitives.ToggleButton> buttons){
   var row=new StackPanel{Margin=new Thickness(0,0,0,4)};
   var top=new Grid{MinHeight=34};top.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(30)});top.ColumnDefinitions.Add(new ColumnDefinition());top.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});top.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   string lower=name.ToLowerInvariant();string glyph=kind=="wasapi_input_capture"?"\uE720":kind=="game_capture"?"\uE7FC":lower.Contains("discord")?"\uE8BD":kind=="wasapi_output_capture"?"\uE767":"\uE8D6";
   top.Children.Add(new TextBlock{Text=glyph,FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),FontSize=15,Foreground=Brush("#C9CDD3"),VerticalAlignment=VerticalAlignment.Center});
   var title=new TextBlock{Text=name,FontSize=13,Foreground=Brush("#E6E8EB"),TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,ToolTip=name};Grid.SetColumn(title,1);top.Children.Add(title);
   // Переключатель отражает состояние OBS: после щелчка ждём событие InputMuteStateChanged.
   var sw=new System.Windows.Controls.Primitives.ToggleButton{Style=(Style)window.FindResource("Switch"),Margin=new Thickness(8,0,4,0)};SetMuteLook(sw,muted);Grid.SetColumn(sw,2);
   sw.Click+=(s,e)=>{bool wanted=sw.IsChecked==true;sw.IsChecked=!wanted;Run(async()=>{await engine.SetMuted(name,!wanted);});};top.Children.Add(sw);buttons[name]=sw;
   var details=new StackPanel{Visibility=Visibility.Collapsed,Margin=new Thickness(30,2,0,10)};
   var more=new Button{Width=28,Height=28,Padding=new Thickness(0),Background=Brush("Transparent"),ToolTip="Громкость, дорожки, задержка",Content=new TextBlock{Text="\uE76C",FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),FontSize=11,Foreground=Brush("#9CA3AF")}};Grid.SetColumn(more,3);
   more.Click+=(s,e)=>{bool open=details.Visibility!=Visibility.Visible;details.Visibility=open?Visibility.Visible:Visibility.Collapsed;((TextBlock)more.Content).Text=open?"\uE70D":"\uE76C";};top.Children.Add(more);
   row.Children.Add(top);
   var vg=new Grid();vg.ColumnDefinitions.Add(new ColumnDefinition());vg.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(64)});
   var slider=new Slider{Minimum=-60,Maximum=0,Value=Math.Max(-60,Math.Min(0,db)),VerticalAlignment=VerticalAlignment.Center,ToolTip="Громкость источника"};var label=new TextBlock{Text=Db(db),FontSize=12,Foreground=Brush("#9CA3AF"),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(label,1);
   slider.ValueChanged+=(s,e)=>{label.Text=Db(e.NewValue);pendingVolume[name]=e.NewValue;if(!volumeTimer.IsEnabled)volumeTimer.Start();};vg.Children.Add(slider);vg.Children.Add(label);details.Children.Add(vg);
   details.Children.Add(new TextBlock{Text="Дорожки в файле и задержка звука",FontSize=11,Foreground=Brush("#7F8793"),Margin=new Thickness(0,6,0,4)});
   var tp=new StackPanel{Orientation=Orientation.Horizontal};
   for(int i=0;i<6;i++){int bit=1<<i;var t=new System.Windows.Controls.Primitives.ToggleButton{Content=(i+1).ToString(),IsChecked=(mask&bit)!=0,Style=(Style)window.FindResource("TrackToggle"),ToolTip="Дорожка "+(i+1)+(trackNames[i].Length>0?" — "+trackNames[i]:"")};
    t.Click+=(s,e)=>Run(async()=>{int m=await audio.GetMask(name);await audio.SetMask(name,t.IsChecked==true?m|bit:m&~bit);await RefreshDuplicates();});tp.Children.Add(t);}
   var off=new TextBox{Text=offset.ToString(),Width=58,Height=26,Padding=new Thickness(4,2,4,2),FontSize=12,Margin=new Thickness(8,0,4,0),ToolTip="Задержка звука в мс (от −950 до 20000)"};
   Action commit=()=>{int v;if(!int.TryParse(off.Text,out v)||v<-950||v>20000){Message("Задержка — целое число от −950 до 20000 мс");return;}Run(async()=>{await engine.SetSyncOffset(name,v);});};
   off.LostFocus+=(s,e)=>commit();off.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){commit();e.Handled=true;}};tp.Children.Add(off);
   tp.Children.Add(new TextBlock{Text="мс",FontSize=12,Foreground=Brush("#7F8793"),VerticalAlignment=VerticalAlignment.Center});details.Children.Add(tp);
   if(audioState.Created.Contains(name)){var rm=new Button{Content="Удалить источник",Padding=new Thickness(10,5,10,5),FontSize=11,Margin=new Thickness(0,8,0,0),HorizontalAlignment=HorizontalAlignment.Left,ToolTip="Удалить источник, добавленный меню"};
    rm.Click+=(s,e)=>Run(async()=>{await audio.Remove(name);if(name.StartsWith(AudioSetup.AppPrefix)){string exe=name.Substring(AudioSetup.AppPrefix.Length);audioOnly.Remove(exe);if(audioMode=="except"&&!audioExcept.Contains(exe))audioExcept.Add(exe);}SavePrefs();RenderAudioApps();await RenderMixer();});details.Children.Add(rm);}
   row.Children.Add(details);
   return row;}
  static string Db(double v){return v<=-60?"−∞":(v>0?"+":"")+v.ToString("0.0")+" дБ";}
  void SetMuteLook(System.Windows.Controls.Primitives.ToggleButton t,bool muted){t.IsChecked=!muted;t.ToolTip=muted?"Выключен — не записывается":"Включён — записывается";}
  void FlushVolumes(){volumeTimer.Stop();foreach(var kv in pendingVolume.ToList()){string n=kv.Key;double v=kv.Value;pendingVolume.Remove(n);
   Run(()=>engine.SetVolumeDb(n,v<=-60?double.NegativeInfinity:v));}}
  // ——— Приватность ———
  string GamePrivacy(){var g=gamesWatch.Current;string p;return g!=null&&gamePrivacy.TryGetValue(g.Exe,out p)?p:"";}
  // Скрывать ли экран сейчас: правило игры «весь экран» — всегда, «не скрывать» — никогда, иначе — если видно исключённое окно.
  bool WantHidden(){string gp=GamePrivacy();return gp=="screen"||(gp!="none"&&privacy.Enabled&&privacy.Visible.Length>0);}
  void BindPrivacy(){
   strict=new StrictPrivacy((r,d)=>engine.Call(r,d));
   if(strictLoaded!=null){strict.Load(strictLoaded);foreach(string k in strict.CoverItems.Keys)coverScenesOn.Add(k);} // после перезапуска «шторка» могла остаться — снимем при первой проверке
   privacy.Notifications=hideNotifications;
   var nb=C<CheckBox>("NotifyBox");nb.IsChecked=hideNotifications;nb.Click+=(s,e)=>{hideNotifications=nb.IsChecked==true;privacy.Notifications=hideNotifications;ExclusionsChanged();};
   var sb=C<CheckBox>("StrictBox");sb.IsChecked=strict.On;sb.Click+=(s,e)=>Run(()=>ToggleStrict(sb.IsChecked==true));}
  async Task ToggleStrict(bool on){var sb=C<CheckBox>("StrictBox");
   try{EnsureReady();
    if(on){var scenes=new List<string>();foreach(var item in C<ComboBox>("SceneBox").Items){string n=Convert.ToString(item);if((await Items(n)).Any(i=>Json.Str(Json.Obj(i),"inputKind")=="monitor_capture"))scenes.Add(n);}
     if(scenes.Count==0)throw new InvalidOperationException("Нет сцен с захватом экрана — строгая защита не нужна");
     RestoreHiddenNow();foreach(string l in await strict.Enable(100,scenes))Log.Write("Строгая защита: "+l);coverScenesOn.Clear();}
    else{foreach(string l in await strict.Disable())Log.Write("Строгая защита: "+l);coverScenesOn.Clear();}
    SavePrefs();sceneItems.Clear();Message(on?"Строгая защита включена: экран с задержкой 100 мс":"Строгая защита выключена");ApplyPrivacy();}
   catch{sb.IsChecked=strict.On;throw;}}
  // «Шторка» включается сразу, а снимается только после задержки экрана + запаса: задержанные кадры с окном должны пройти.
  async Task SyncCover(bool want){
   foreach(string sc in coverScenesOn.ToList())if(sc!=currentScene){await strict.SetCover(sc,false);coverScenesOn.Remove(sc);}
   string cur=currentScene??"";
   if(want&&strict.CoverItems.ContainsKey(cur)){if(revealTimer!=null)revealTimer.Stop();if(!coverScenesOn.Contains(cur)){await strict.SetCover(cur,true);coverScenesOn.Add(cur);Log.Write("Скрыто «шторкой»: "+(privacy.Visible.Length>0?privacy.Visible:"правило игры"));}}
   else if(!want&&coverScenesOn.Contains(cur)){
    if(revealTimer==null){revealTimer=new DispatcherTimer();revealTimer.Tick+=(s,e)=>{revealTimer.Stop();RevealNow();};}
    if(!revealTimer.IsEnabled){revealTimer.Interval=TimeSpan.FromMilliseconds(Math.Max(100,strict.AppliedDelay)+150);revealTimer.Start();}}}
  async void RevealNow(){
   if(WantHidden())return; // окно снова на экране — шторку оставляем
   try{foreach(string sc in coverScenesOn.ToList()){await strict.SetCover(sc,false);coverScenesOn.Remove(sc);}}catch(Exception ex){Log.Write("Шторка: "+ex.Message);}
   UpdatePrivacyLabel();}
  // Предупреждения, когда скрыть технически нельзя: окно захвачено напрямую, экран во вложенной сцене, оверлеи в захвате игры и т. п.
  async Task RenderPrivacyWarnings(){
   var box=C<TextBlock>("PrivacyWarnings");if(!engine.Connected||string.IsNullOrEmpty(currentScene)){box.Visibility=Visibility.Collapsed;return;}
   var list=new List<SceneSource>();bool monitor=false;
   foreach(object raw in await Items(currentScene)){var it=Json.Obj(raw);string kind=Json.Str(it,"inputKind"),name=Json.Str(it,"sourceName");if(name==StrictPrivacy.CoverScene)continue;var src=new SceneSource{Name=name,Kind=kind};
    if(kind=="monitor_capture")monitor=true;
    if(kind=="window_capture"||kind=="game_capture"){try{var st=Json.Obj((await engine.Call("GetInputSettings",Json.D("inputName",name)))["inputSettings"]);src.Window=Json.Str(st,"window");src.CaptureOverlays=Json.Bool(st,"capture_overlays");}catch(InvalidOperationException){}}
    bool isGroup=Json.Bool(it,"isGroup");
    if(isGroup||Json.Str(it,"sourceType")=="OBS_SOURCE_TYPE_SCENE"){src.Nested=true;try{object inner;(await engine.Call(isGroup?"GetGroupSceneItemList":"GetSceneItemList",Json.D("sceneName",name))).TryGetValue("sceneItems",out inner);src.NestedHasMonitor=((inner as object[])??new object[0]).Any(x=>Json.Str(Json.Obj(x),"inputKind")=="monitor_capture");}catch(InvalidOperationException){}}
    list.Add(src);}
   var g=gamesWatch.Current;var w=PrivacyAdvisor.Warnings(list,excluded,g==null?null:g.Exe,myGames,monitor);
   box.Visibility=w.Count==0?Visibility.Collapsed:Visibility.Visible;box.Text=string.Join("\n",w.Select(x=>"⚠ "+x));}
  async Task SaveReplay(){EnsureReady();if(srOn){await SaveSR();return;}if(await engine.ReplayActive()!=true)throw new InvalidOperationException("Сначала включите мгновенный повтор");await engine.SaveReplay();Message("Сохраняю повтор…");}
  async Task PickFolder(){EnsureReady();if((await engine.Record()).Active)throw new InvalidOperationException("Сначала остановите запись");
   string chosen;dialogOpen=true;try{using(var dialog=new WF.FolderBrowserDialog{Description="Куда сохранять повторы и записи OBS",ShowNewFolderButton=true,SelectedPath=directory}){if(dialog.ShowDialog(new Owner(handle))!=WF.DialogResult.OK)return;chosen=dialog.SelectedPath;}}finally{dialogOpen=false;window.Activate();}
   if(string.Equals(chosen.TrimEnd('\\'),directory.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return;
   string probe=Path.Combine(chosen,".replay-overlay-write-test");try{File.WriteAllText(probe,"");File.Delete(probe);}catch(Exception ex){throw new InvalidOperationException("В эту папку нельзя записывать: "+ex.Message);}
   bool resume=await engine.ReplayActive()==true;busy=true;UpdateControls();
   try{await engine.SetRecordDirectory(chosen);
    if(resume)await engine.RestartReplay();
   }finally{busy=false;UpdateControls();}
   Message("Папка записей: "+chosen+(resume?" · буфер запущен заново":""));await Refresh();}
  async Task LaunchObs(){engine.Open();await Task.FromResult(0);}
  Task WaitBuffer(bool active){return engine.WaitReplay(active);}
  static void AtomicWrite(string path,string contents){string temp=path+".overlay.tmp";File.WriteAllText(temp,contents,new System.Text.UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,path+".overlay.bak");else File.Move(temp,path);}
  string OutputSection{get{return outputMode=="Simple"?"SimpleOutput":"AdvOut";}}
  // Выход по любому пути (меню трея, событие выхода, завершение сеанса Windows): каждый шаг отдельно — сбой одного не мешает остальным.
  bool exitStarting;
  public void Exit(){if(exiting||exitStarting)return;exitStarting=true;Log.Write("Меню закрыто");
   foreach(var step in new KeyValuePair<string,Action>[]{
    new KeyValuePair<string,Action>("таймеры",()=>{cleanupTimer.Stop();statsTimer.Stop();sessionTimer.Stop();indicatorTimer.Stop();clock.Stop();}),
    new KeyValuePair<string,Action>("индикатор",()=>{if(indicator!=null)indicator.Dispose();}),
    new KeyValuePair<string,Action>("клипы",()=>{if(clipLoader!=null)clipLoader.Dispose();if(trimCancel!=null)trimCancel.Cancel();ClosePlayer();}),
    new KeyValuePair<string,Action>("игры",()=>gamesWatch.Dispose()),
    new KeyValuePair<string,Action>("приватность",()=>{privacy.Dispose();RestoreHiddenNow();}),
    new KeyValuePair<string,Action>("уведомление",()=>{exiting=true;if(toasts!=null)toasts.CloseAll();if(keysPanel!=null)keysPanel.Dispose();diskTimer.Stop();}),
    new KeyValuePair<string,Action>("клавиши",UnregisterHotkeys),
    new KeyValuePair<string,Action>("движок",()=>{engine.Dispose();engine.Close();}), // своя копия закрывается вместе с меню; OBS пользователя — нет
    new KeyValuePair<string,Action>("трей",()=>{tray.Visible=false;tray.Dispose();}),
    new KeyValuePair<string,Action>("приложение",()=>{if(Application.Current!=null)Application.Current.Shutdown();})})
   {try{step.Value();}catch(Exception ex){Log.Write("Выход ("+step.Key+"): "+ex.GetType().Name+": "+ex.Message);}}
   exiting=true;}
 }
 public static class Program {
  [STAThread] public static void Main(string[] args){
   // Служебные запуски (задачи Планировщика и запрос прав): делают одно действие и выходят, основное меню не трогают.
   if(args.Contains(EngineTasks.CloseArg)){Environment.Exit(ObsEngine.CloseHere(EngineHome.Find())==0?0:1);return;}
   if(args.Contains(EngineTasks.InstallArg)||args.Contains(EngineTasks.RemoveArg)){
    try{if(args.Contains(EngineTasks.RemoveArg))EngineTasks.Remove();else{var home=EngineHome.Find();if(!home.Own)throw new InvalidOperationException("Движок записи не найден рядом с меню");EngineTasks.Install(home,Assembly.GetExecutingAssembly().Location);}Environment.Exit(0);}
    catch(Exception ex){Log.Write("Задачи движка: "+ex.Message);Environment.Exit(2);}
    return;}
   // После сброса настроек новое меню ждёт, пока закроется старое (иначе единственный экземпляр не даст запуститься).
   var waitArg=args.FirstOrDefault(a=>a.StartsWith("--wait-pid="));int waitPid;
   if(waitArg!=null&&int.TryParse(waitArg.Substring(11),out waitPid)){try{using(var old=Process.GetProcessById(waitPid))old.WaitForExit(15000);}catch(ArgumentException){}}
   bool fresh;using(var mutex=new Mutex(true,Names.Mutex,out fresh))using(var wake=new EventWaitHandle(false,EventResetMode.AutoReset,Names.Show))using(var quit=new EventWaitHandle(false,EventResetMode.AutoReset,Names.Exit)){
   if(!fresh){if(!args.Contains("--hidden")){Native.AllowSetForegroundWindow(-1);wake.Set();}return;}var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};AppDomain.CurrentDomain.UnhandledException+=(s,e)=>Log.Write("Сбой меню: "+e.ExceptionObject);Overlay overlay=null;app.DispatcherUnhandledException+=(s,e)=>{e.Handled=true;MessageBox.Show("Ошибка оверлея: "+e.Exception.Message,"Replay Overlay");};try{Overlay.StartedFrom=args.FirstOrDefault(a=>a.StartsWith("--from="))??"";overlay=new Overlay(args.Contains("--preview"));var listener=new Thread(()=>{while(true){wake.WaitOne();overlay.Show();}}){IsBackground=true};listener.Start();var stopper=new Thread(()=>{quit.WaitOne();overlay.RequestExit();}){IsBackground=true};stopper.Start();if(args.Contains("--hidden")){overlay.StartHidden();app.Run();}else app.Run(overlay.Window);}catch(Exception ex){MessageBox.Show("Оверлей не запустился: "+ex.Message,"Replay Overlay");}finally{if(overlay!=null)overlay.Exit();}
  }}
 }
}
