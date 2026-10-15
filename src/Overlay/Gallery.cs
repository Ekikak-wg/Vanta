using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Галерея клипов: список с миниатюрами, предпросмотр, обрезка, переименование, удаление в Корзину. И индикатор записи.
 public sealed partial class Overlay {
  const int ClipsPageSize=40;
  List<ClipFile> allClips=new List<ClipFile>(),clips=new List<ClipFile>();string clipFilter="all";bool gridView=true;double previewVolume=0.8;readonly Dictionary<string,string> clipGames=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);int clipsShown=ClipsPageSize;ClipFile selectedClip;ClipInfoLoader clipLoader;
  readonly Dictionary<string,Image> clipImages=new Dictionary<string,Image>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<string,TextBlock> clipLines=new Dictionary<string,TextBlock>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<string,Border> clipRows=new Dictionary<string,Border>(StringComparer.OrdinalIgnoreCase);
  DispatcherTimer playTimer;bool playing,syncing,previewSound,precise,toMp4=true,trimBusy,clipsLoading;double trimStart,trimEnd,clipLength;
  string ffmpegPath="",ffmpegFound,lastDirectory="";CancellationTokenSource trimCancel;
  RecordingIndicator indicator;readonly DispatcherTimer indicatorTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
  string indicatorMode=RecordingIndicator.RecordOnly,indicatorCorner="tr";

  string ClipsDir{get{return directory.Length>0?directory:lastDirectory;}}
  bool ClipsOpen{get{return C<StackPanel>("ClipsPage").Visibility==Visibility.Visible;}}
  MediaElement Player{get{return C<MediaElement>("Player");}}
  static readonly CultureInfo Ru=CultureInfo.GetCultureInfo("ru-RU");

  void OpenClips(){ShowPage("clips");Run(LoadClips);}
  // Ищем ffmpeg один раз; не нашли — ищем снова при следующем действии (могли установить).
  string FindFfmpeg(){string f=ffmpegFound;if(f!=null&&File.Exists(f))return f;f=Ffmpeg.Find(ffmpegPath);ffmpegFound=f;return f;}

  void BindClips(){
   clipLoader=new ClipInfoLoader((c,b)=>Ui(()=>ClipInfoReady(c,b)));clipLoader.FfmpegPath=FindFfmpeg;
   C<Button>("ClipsFolderButton").Click+=(s,e)=>Run(OpenClipsFolder);
   C<Button>("MoreClipsButton").Click+=(s,e)=>{clipsShown+=ClipsPageSize;RenderClips();};
   foreach(var f in new[]{new[]{"FilterAll","all"},new[]{"FilterRecords","record"},new[]{"FilterReplays","replay"},new[]{"FilterShots","shot"}}){string tag=f[1];C<RadioButton>(f[0]).Click+=(s,e)=>{clipFilter=tag;clipsShown=ClipsPageSize;ApplyClipFilter();RenderClips();};}
   var player=Player;player.IsMuted=!previewSound;
   // Длительность — из Windows/ffprobe: проигрыватель у некоторых MP4 занижает её (4,0 с вместо 4,5 с у отрезка из ffmpeg).
   player.MediaOpened+=(s,e)=>{if(selectedClip==null)return;double d=selectedClip.Seconds>0?selectedClip.Seconds:player.NaturalDuration.HasTimeSpan?player.NaturalDuration.TimeSpan.TotalSeconds:-1;SetClipLength(d);C<TextBlock>("PlayerMessage").Text="";};
   player.MediaEnded+=(s,e)=>{playing=false;player.Pause();UpdatePlayButton();};
   player.MediaFailed+=(s,e)=>{playing=false;UpdatePlayButton();C<TextBlock>("PlayerMessage").Text="Здесь этот файл не воспроизводится (нет кодека в Windows). Откройте его в плеере — обрезка и остальное работают.";if(selectedClip!=null)SetClipLength(selectedClip.Seconds);};
   C<Button>("PlayButton").Click+=(s,e)=>TogglePlay();
   C<Button>("SoundButton").Click+=(s,e)=>{previewSound=!previewSound;player.IsMuted=!previewSound;UpdateSoundButton();SavePrefs();};
   var slider=C<Slider>("PlaySlider");slider.ValueChanged+=(s,e)=>{if(!syncing&&clipLength>0)player.Position=TimeSpan.FromSeconds(e.NewValue);UpdatePlayTime();};
   playTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};playTimer.Tick+=(s,e)=>SyncSlider();
   C<Button>("TrimStartButton").Click+=(s,e)=>SetTrim(true);
   C<Button>("TrimEndButton").Click+=(s,e)=>SetTrim(false);
   C<Button>("TrimResetButton").Click+=(s,e)=>{trimStart=0;trimEnd=clipLength;RenderTrim();};
   C<Canvas>("TrimBar").SizeChanged+=(s,e)=>RenderTrim();
   // Два понятных выбора вместо галочек: как вырезать и в каком формате сохранить.
   C<RadioButton>("TrimFast").IsChecked=!precise;C<RadioButton>("TrimExact").IsChecked=precise;
   C<RadioButton>("TrimFast").Click+=(s,e)=>{precise=false;SavePrefs();UpdateTrimOptions();RenderTrim();};C<RadioButton>("TrimExact").Click+=(s,e)=>{precise=true;SavePrefs();UpdateTrimOptions();RenderTrim();};
   C<RadioButton>("TrimMp4").IsChecked=toMp4;C<RadioButton>("TrimKeep").IsChecked=!toMp4;
   C<RadioButton>("TrimMp4").Click+=(s,e)=>{toMp4=true;SavePrefs();UpdateTrimOptions();};C<RadioButton>("TrimKeep").Click+=(s,e)=>{toMp4=false;SavePrefs();UpdateTrimOptions();};
   UpdateTrimOptions();
   C<Button>("TrimButton").Click+=(s,e)=>Run(TrimClip);
   C<Button>("RenameButton").Click+=(s,e)=>Run(RenameClip);
   C<TextBox>("RenameBox").KeyDown+=(s,e)=>{if(e.Key==Key.Enter){e.Handled=true;Run(RenameClip);}};
   C<Button>("OpenClipButton").Click+=(s,e)=>Run(async()=>{var c=SelectedOrThrow();Process.Start(new ProcessStartInfo(c.Path){UseShellExecute=true});await Task.FromResult(0);});
   C<Button>("RevealClipButton").Click+=(s,e)=>Run(async()=>{var c=SelectedOrThrow();Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+c.Path+"\""){UseShellExecute=true});await Task.FromResult(0);});
   C<Button>("DeleteClipButton").Click+=(s,e)=>Run(DeleteClip);
   C<Button>("FfmpegPickButton").Click+=(s,e)=>PickFfmpeg();
   var vg=C<RadioButton>("ViewGrid");var vl=C<RadioButton>("ViewList");vg.IsChecked=gridView;vl.IsChecked=!gridView;
   vg.Click+=(s,e)=>{gridView=true;SavePrefs();RenderClips();};vl.Click+=(s,e)=>{gridView=false;SavePrefs();RenderClips();};
   C<Button>("FullscreenButton").Click+=(s,e)=>{if(selectedClip!=null)OpenViewer(selectedClip);};
   var vs=C<Slider>("VolumeSlider");vs.Value=previewVolume;player.Volume=previewVolume;vs.ValueChanged+=(s,e)=>{previewVolume=e.NewValue;Player.Volume=previewVolume;if(e.NewValue>0&&!previewSound){previewSound=true;Player.IsMuted=false;UpdateSoundButton();}};vs.PreviewMouseUp+=(s,e)=>SavePrefs();
   UpdateSoundButton();}

  async Task OpenClipsFolder(){string dir=ClipsDir;if(dir.Length==0)throw new InvalidOperationException("Папка записей пока неизвестна: подключитесь к OBS");if(!Directory.Exists(dir))throw new DirectoryNotFoundException("Папка записей пока не создана");Process.Start(new ProcessStartInfo("explorer.exe","\""+dir.Replace("\"","")+"\""){UseShellExecute=true});await Task.FromResult(0);}
  // Файл удалили или переименовали мимо меню — обновляем список.
  void EnsureClipExists(ClipFile c){if(File.Exists(c.Path))return;CloseDetail();LoadClipsSelect(null);throw new InvalidOperationException("Файла уже нет — список обновлён");}
  ClipFile SelectedOrThrow(){if(selectedClip==null)throw new InvalidOperationException("Выберите клип");return selectedClip;}

  // Перечитать папку (после сохранения повтора, обрезки, переименования). Выбранный клип остаётся выбранным.
  async Task LoadClips(){LoadClipsSelect(null);await Task.FromResult(0);}
  async void LoadClipsSelect(string select){
   if(clipsLoading)return;clipsLoading=true;
   try{string dir=ClipsDir;var info=C<TextBlock>("ClipsInfo");
    if(dir.Length==0){clips=new List<ClipFile>();info.Text="Папка записей пока неизвестна — подключитесь к OBS.";RenderClips();return;}
    var dirs=new[]{dir,ShotDir,replayDir}.Where(x=>x.Length>0).Select(x=>x.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();List<ClipFile> list;try{list=await Task.Run(()=>dirs.SelectMany(x=>ClipLibrary.List(x)).OrderByDescending(c=>c.Modified).ToList());}catch(Exception ex){list=new List<ClipFile>();info.Text="Папку не прочитать: "+ex.Message;}
    allClips=list;ApplyClipFilter();string keep=select??(selectedClip==null?null:selectedClip.Path);
    long total=clips.Sum(c=>c.Size);info.Text=clips.Count==0?(clipFilter=="all"?"В папке «"+dir+"» пока нет видео и скриншотов. Сохраните повтор — он появится здесь.":"Здесь пока ничего нет."):Plural(clips.Count,"файл","файла","файлов")+" · "+ClipLibrary.Size(total)+" · "+dir;info.ToolTip=dir;
    var found=keep==null?null:clips.FirstOrDefault(c=>string.Equals(c.Path,keep,StringComparison.OrdinalIgnoreCase));
    if(found!=null){int index=clips.IndexOf(found);if(index>=clipsShown)clipsShown=(index/ClipsPageSize+1)*ClipsPageSize;}
    RenderClips();
    if(found==null){if(selectedClip!=null)CloseDetail();}
    else if(select!=null||selectedClip==null)SelectClip(found);
    else{selectedClip=found;HighlightSelected();}}
   finally{clipsLoading=false;}}
  static string ClipReplayPrefix="Replay"; // приставка повторов из профиля OBS
  static string ClipKind(ClipFile c){return c.IsImage?"shot":c.Name.StartsWith(ClipReplayPrefix,StringComparison.OrdinalIgnoreCase)?"replay":"record";}
  void ApplyClipFilter(){clips=clipFilter=="all"?allClips.ToList():allClips.Where(c=>ClipKind(c)==clipFilter).ToList();}
  static string Plural(int n,string one,string few,string many){int m=n%100,d=n%10;return n+" "+(m>=11&&m<=14?many:d==1?one:d>=2&&d<=4?few:many);}

  void RenderClips(){
   var list=C<StackPanel>("ClipList");list.Children.Clear();clipImages.Clear();clipLines.Clear();clipRows.Clear();clipBadges.Clear();
   var shown=clips.Take(clipsShown).ToList();
   if(!gridView)foreach(var c in shown)list.Children.Add(ClipRow(c));
   else for(int i=0;i<shown.Count;i+=2){var pair=new Grid{Margin=new Thickness(0,0,0,8)};pair.ColumnDefinitions.Add(new ColumnDefinition());pair.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(8)});pair.ColumnDefinitions.Add(new ColumnDefinition());
    pair.Children.Add(ClipCard(shown[i]));if(i+1<shown.Count){var second=ClipCard(shown[i+1]);Grid.SetColumn(second,2);pair.Children.Add(second);}list.Children.Add(pair);}
   C<Button>("MoreClipsButton").Visibility=clips.Count>shown.Count?Visibility.Visible:Visibility.Collapsed;
   C<Button>("MoreClipsButton").Content="Показать ещё ("+(clips.Count-shown.Count)+")";
   clipLoader.Request(shown.Where(c=>clipLoader.Cached(c)==null&&c.Seconds<0));HighlightSelected();}

  UIElement ClipRow(ClipFile c){
   var img=new Image{Width=112,Height=63,Stretch=Stretch.UniformToFill,Source=clipLoader.Cached(c)};
   var thumb=new Border{Width=112,Height=63,Background=Brush("#262A2F"),ClipToBounds=true,Child=img};
   var texts=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
   texts.Children.Add(new TextBlock{Text=c.Name,FontSize=13,Foreground=Brush("#E6EBDD"),TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=c.Name+c.Extension});
   var line=new TextBlock{Text=ClipLine(c),FontSize=11,Foreground=Brush("#8E99A4"),Margin=new Thickness(0,4,0,0),TextWrapping=TextWrapping.Wrap};texts.Children.Add(line);
   var g=new Grid();g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(112)});g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(10)});g.ColumnDefinitions.Add(new ColumnDefinition());
   g.Children.Add(thumb);Grid.SetColumn(texts,2);g.Children.Add(texts);
   var row=new Border{Background=Brush("#1D2227"),CornerRadius=new CornerRadius(8),Padding=new Thickness(6),Margin=new Thickness(0,0,0,6),Cursor=Cursors.Hand,Child=g};
   WireCard(row,c);clipImages[c.Path]=img;clipLines[c.Path]=line;clipRows[c.Path]=row;return row;}
  // Карточка для сетки: превью сверху, имя и подробности ниже.
  UIElement ClipCard(ClipFile c){
   var img=new Image{Height=100,Stretch=Stretch.UniformToFill,Source=clipLoader.Cached(c)};
   var thumb=new Border{Height=100,Background=Brush("#262A2F"),CornerRadius=new CornerRadius(6),ClipToBounds=true,Child=img};
   var badge=new Border{Background=Brush("#B3000000"),CornerRadius=new CornerRadius(4),Padding=new Thickness(6,2,6,2),Margin=new Thickness(6),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Child=new TextBlock{Text=c.IsImage?"PNG":c.Seconds>=0?ClipLibrary.Time(c.Seconds):"",FontSize=11,Foreground=Brush("#F5F6F7")}};
   var top=new Grid();top.Children.Add(thumb);top.Children.Add(badge);
   var stack=new StackPanel();stack.Children.Add(top);
   stack.Children.Add(new TextBlock{Text=c.Name,FontSize=12.5,Foreground=Brush("#E6E8EB"),TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(2,7,2,0),ToolTip=c.Name+c.Extension});
   var line=new TextBlock{Text=ClipLine(c),FontSize=10.5,Foreground=Brush("#8E99A4"),Margin=new Thickness(2,3,2,2),TextWrapping=TextWrapping.Wrap};stack.Children.Add(line);
   var card=new Border{Background=Brush("#1A1D23"),BorderBrush=Brush("#59343A43"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(6),Cursor=Cursors.Hand,Child=stack};
   WireCard(card,c);clipImages[c.Path]=img;clipLines[c.Path]=line;clipRows[c.Path]=card;
   if(c.IsImage||c.Seconds>=0)clipBadges[c.Path]=(TextBlock)badge.Child;return card;}
  readonly Dictionary<string,TextBlock> clipBadges=new Dictionary<string,TextBlock>(StringComparer.OrdinalIgnoreCase);
  // Щелчок — выбрать, двойной щелчок — во весь экран, правая кнопка — меню действий.
  void WireCard(Border row,ClipFile c){
   row.MouseEnter+=(s,e)=>row.Opacity=0.85;row.MouseLeave+=(s,e)=>row.Opacity=1;
   row.MouseLeftButtonUp+=(s,e)=>{if(selectedClip==null||selectedClip.Path!=c.Path)SelectClip(c);};
   row.MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2){e.Handled=true;OpenViewer(c);}};
   var menu=new ContextMenu();
   Action<string,Action> item=(text,act)=>{var mi=new MenuItem{Header=text};mi.Click+=(s,e)=>act();menu.Items.Add(mi);};
   item("Открыть во весь экран",()=>OpenViewer(c));
   item("Открыть в программе",()=>Run(async()=>{EnsureClipExists(c);Process.Start(new ProcessStartInfo(c.Path){UseShellExecute=true});await Task.FromResult(0);}));
   item("Показать в папке",()=>Run(async()=>{EnsureClipExists(c);Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+c.Path+"\""){UseShellExecute=true});await Task.FromResult(0);}));
   item("Переименовать",()=>{SelectClip(c);window.Dispatcher.BeginInvoke(new Action(()=>{var rb=C<TextBox>("RenameBox");rb.Focus();rb.SelectAll();}),DispatcherPriority.Loaded);});
   if(!c.IsImage)item("Обрезать",()=>{SelectClip(c);window.Dispatcher.BeginInvoke(new Action(()=>C<Slider>("PlaySlider").BringIntoView()),DispatcherPriority.Loaded);});
   item("Удалить в Корзину",()=>{SelectClip(c);Run(DeleteClip);});
   row.ContextMenu=menu;}
  // Просмотр во весь экран; пока он открыт, меню не прячется.
  void OpenViewer(ClipFile c){
   if(!File.Exists(c.Path)){Message("Файла уже нет");return;}
   double pos=selectedClip!=null&&selectedClip.Path==c.Path&&!c.IsImage?PlayPosition:0;PausePlayer();
   try{var v=new MediaViewer(window,c.Path,c.IsImage,pos,previewVolume,!previewSound,preview);dialogOpen=true;
    v.Closed+=(p,vol)=>{dialogOpen=false;previewVolume=vol;C<Slider>("VolumeSlider").Value=vol;window.Activate();if(!c.IsImage&&selectedClip!=null&&selectedClip.Path==c.Path&&p>0){Player.Position=TimeSpan.FromSeconds(p);syncing=true;C<Slider>("PlaySlider").Value=Math.Min(C<Slider>("PlaySlider").Maximum,p);syncing=false;UpdatePlayTime();}};
    v.Show();}
   catch(Exception ex){dialogOpen=false;Message("Не открыть: "+ex.Message);}}
  string ClipLine(ClipFile c){string game;if(!clipGames.TryGetValue(c.Path,out game))game=null;return (game!=null?game+" · ":"")+ClipLineBase(c);}
  static string ClipLineBase(ClipFile c){string k=ClipKind(c)=="shot"?"скриншот":ClipKind(c)=="replay"?"повтор":"запись";return k+" · "+c.Modified.ToString("d MMM, HH:mm",Ru)+(c.IsImage?(c.Width>0?" · "+c.Width+"×"+c.Height:""):c.Seconds>=0?" · "+ClipLibrary.Time(c.Seconds):"")+" · "+ClipLibrary.Size(c.Size)+" · "+c.Extension.TrimStart('.').ToUpperInvariant();}
  void ClipInfoReady(ClipFile c,BitmapSource b){
   Image img;if(b!=null&&clipImages.TryGetValue(c.Path,out img))img.Source=b;
   TextBlock line;if(clipLines.TryGetValue(c.Path,out line))line.Text=ClipLine(c);
   TextBlock bd;if(clipBadges.TryGetValue(c.Path,out bd))bd.Text=c.IsImage?(c.Width>0?c.Width+"×"+c.Height:"PNG"):c.Seconds>=0?ClipLibrary.Time(c.Seconds):"";
   // Точная длительность пришла позже проигрывателя: заменяем, если отрезок ещё не меняли.
   bool untouched=trimStart<=0&&Math.Abs(trimEnd-clipLength)<0.001;
   if(selectedClip!=null&&selectedClip.Path==c.Path&&c.Seconds>0&&Math.Abs(clipLength-c.Seconds)>0.05&&untouched&&C<TextBlock>("PlayerMessage").Text!="Загрузка…")SetClipLength(c.Seconds);}
  void HighlightSelected(){foreach(var kv in clipRows){bool on=selectedClip!=null&&kv.Key==selectedClip.Path;kv.Value.Background=Brush(on?"#1A2E22":gridView?"#1A1D23":"#1D2227");kv.Value.BorderBrush=Brush(on?"#00D46A":"#59343A43");kv.Value.BorderThickness=new Thickness(1);}}

  void SelectClip(ClipFile c){
   ClosePlayer();selectedClip=c;HighlightSelected();clipLength=0;trimStart=0;trimEnd=0;
   C<StackPanel>("ClipDetail").Visibility=Visibility.Visible;C<TextBlock>("ClipTitle").Text=c.Name+c.Extension;
   C<TextBox>("RenameBox").Text=c.Name;C<StackPanel>("TrimFormatPanel").Visibility=c.Extension.ToLowerInvariant()==".mp4"?Visibility.Collapsed:Visibility.Visible;UpdateTrimOptions();
   C<TextBlock>("TrimProgress").Text="";bool busyFile=ClipLibrary.InUse(c.Path);
   C<StackPanel>("VideoControls").Visibility=c.IsImage?Visibility.Collapsed:Visibility.Visible;var view=C<Image>("ShotView");view.Visibility=c.IsImage?Visibility.Visible:Visibility.Collapsed;view.Source=null;
   if(c.IsImage){C<TextBlock>("PlayerMessage").Text="";try{var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.DecodePixelWidth=1280;bi.UriSource=new Uri(c.Path);bi.EndInit();bi.Freeze();view.Source=bi;}catch(Exception ex){C<TextBlock>("PlayerMessage").Text="Изображение не открыть: "+ex.Message;}C<TextBox>("RenameBox").Text=c.Name;C<StackPanel>("ClipDetail").Visibility=Visibility.Visible;C<TextBlock>("ClipTitle").Text=c.Name+c.Extension;foreach(string n in new[]{"RenameButton","DeleteClipButton"})C<Button>(n).IsEnabled=true;C<ScrollViewer>("PageScroll").ScrollToTop();return;}
   foreach(string n in new[]{"TrimButton","RenameButton","DeleteClipButton"})C<Button>(n).IsEnabled=!busyFile||(n=="TrimButton"&&trimBusy);
   C<FrameworkElement>("FfmpegPanel").Visibility=FindFfmpeg()==null?Visibility.Visible:Visibility.Collapsed;
   var msg=C<TextBlock>("PlayerMessage");
   if(busyFile){msg.Text="Файл ещё пишется — дождитесь конца записи.";SetClipLength(c.Seconds);}
   else{msg.Text="Загрузка…";var player=Player;player.IsMuted=!previewSound;player.Source=new Uri(c.Path);player.Pause();} // Pause открывает файл и показывает первый кадр
   UpdatePlayButton();RenderTrim();C<ScrollViewer>("PageScroll").ScrollToTop();}
  void CloseDetail(){ClosePlayer();selectedClip=null;C<StackPanel>("ClipDetail").Visibility=Visibility.Collapsed;HighlightSelected();}
  void SetClipLength(double seconds){clipLength=Math.Max(0,seconds);trimStart=0;trimEnd=clipLength;syncing=true;var sl=C<Slider>("PlaySlider");sl.Maximum=Math.Max(0.1,clipLength);sl.Value=0;syncing=false;UpdatePlayTime();RenderTrim();}

  void ClosePlayer(){var sv=C<Image>("ShotView");if(sv!=null)sv.Source=null;if(playTimer!=null)playTimer.Stop();playing=false;var p=Player;try{p.Stop();p.Close();p.Source=null;}catch(Exception){}UpdatePlayButton();}
  void PausePlayer(){if(!playing)return;Player.Pause();playing=false;playTimer.Stop();UpdatePlayButton();}
  void TogglePlay(){if(selectedClip==null||Player.Source==null)return;
   if(playing){PausePlayer();return;}
   if(clipLength>0&&Player.Position.TotalSeconds>=clipLength-0.05)Player.Position=TimeSpan.Zero;
   Player.Play();playing=true;playTimer.Start();UpdatePlayButton();}
  void UpdatePlayButton(){C<Button>("PlayButton").Content=playing?"❚❚":"▶";}
  void UpdateSoundButton(){var b=C<Button>("SoundButton");b.Content=previewSound?"Звук вкл":"Звук выкл";b.ToolTip=previewSound?"Звук предпросмотра слышен и попадёт в запись, если пишется звук компьютера (картинка — нет)":"Включить звук предпросмотра. Он попадёт в запись, если пишется звук компьютера";}
  void SyncSlider(){if(!playing)return;syncing=true;C<Slider>("PlaySlider").Value=Math.Min(C<Slider>("PlaySlider").Maximum,Player.Position.TotalSeconds);syncing=false;UpdatePlayTime();}
  double PlayPosition{get{return Player.Source!=null&&Player.NaturalDuration.HasTimeSpan?Player.Position.TotalSeconds:C<Slider>("PlaySlider").Value;}}
  void UpdatePlayTime(){C<TextBlock>("PlayTime").Text=ClipLibrary.Time(C<Slider>("PlaySlider").Value,true)+" / "+ClipLibrary.Time(clipLength>0?clipLength:-1);}

  void SetTrim(bool start){
   if(clipLength<=0){Message("Длительность клипа неизвестна");return;}double pos=Math.Max(0,Math.Min(clipLength,PlayPosition));
   if(start){if(pos>trimEnd-0.5){Message("Начало должно быть раньше конца хотя бы на 0,5 с");return;}trimStart=pos;}
   else{if(pos<trimStart+0.5){Message("Конец должен быть позже начала хотя бы на 0,5 с");return;}trimEnd=pos;}
   RenderTrim();}
  void RenderTrim(){
   var bar=C<Canvas>("TrimBar");var r=C<System.Windows.Shapes.Rectangle>("TrimRange");double w=bar.ActualWidth;
   if(clipLength<=0||w<=0){r.Width=0;}else{Canvas.SetLeft(r,trimStart/clipLength*w);r.Width=Math.Max(2,(trimEnd-trimStart)/clipLength*w);}
   bool whole=clipLength>0&&trimStart<=0.05&&trimEnd>=clipLength-0.05;
   C<TextBlock>("TrimInfo").Text=clipLength<=0?"Длительность неизвестна — обрезка недоступна.":(whole?"Весь клип":"Отрезок "+ClipLibrary.Time(trimStart,true)+" – "+ClipLibrary.Time(trimEnd,true))+" · "+ClipLibrary.Time(trimEnd-trimStart,true);
   if(!trimBusy)C<Button>("TrimButton").Content=whole&&selectedClip!=null&&selectedClip.Extension.ToLowerInvariant()!=".mp4"?"Сохранить копию":"Сохранить отрезок";}

  void UpdateTrimOptions(){
   C<TextBlock>("TrimModeHint").Text=precise?"Отрезок начнётся ровно с выбранного места. Видео пересчитывается заново — это займёт от нескольких секунд до минут.":"За долю секунды и без потери качества. Начало может сдвинуться назад до 2 секунд — видео режется только по опорным кадрам.";
   C<TextBlock>("TrimFormatHint").Text=toMp4?"MP4 открывается прямо в Discord и на телефоне. Качество то же.":"Как у исходного файла (обычно MKV). Discord покажет его только как файл для скачивания.";}

  // Обрезка — в новый файл рядом, исходный не трогаем. Повторное нажатие — отмена.
  async Task TrimClip(){
   if(trimBusy){if(trimCancel!=null)trimCancel.Cancel();return;}
   var c=SelectedOrThrow();EnsureClipExists(c);string ff=FindFfmpeg();
   if(ff==null){C<FrameworkElement>("FfmpegPanel").Visibility=Visibility.Visible;throw new InvalidOperationException("Для обрезки нужен ffmpeg — см. подсказку ниже");}
   if(clipLength<=0)throw new InvalidOperationException("Длительность клипа неизвестна");
   if(trimEnd-trimStart<0.5)throw new InvalidOperationException("Отрезок короче 0,5 с");
   if(ClipLibrary.InUse(c.Path))throw new InvalidOperationException("Файл ещё пишется");
   bool mp4=toMp4||c.Extension.ToLowerInvariant()==".mp4";string output=ClipLibrary.TrimmedPath(c.Path,mp4),part=output+".part";
   double from=trimStart,to=trimEnd;bool exact=precise;PausePlayer();
   trimBusy=true;trimCancel=new CancellationTokenSource();var button=C<Button>("TrimButton");button.Content="Отменить";var progress=C<TextBlock>("TrimProgress");progress.Text="Сохраняю…";
   try{
    Action<double> report=p=>Ui(()=>{if(trimBusy)progress.Text="Сохраняю… "+(int)(p*100)+"%";});
    string nvencError=null;
    try{await Ffmpeg.Run(ff,Ffmpeg.TrimArgs(c.Path,part,from,to,exact,"h264_nvenc"),to-from,report,trimCancel.Token);}
    catch(InvalidOperationException ex){if(!exact)throw;nvencError=ex.Message;}
    if(nvencError!=null){Log.Write("Обрезка: NVENC недоступен ("+nvencError+"), перекодирую процессором");TryDelete(part);
     await Ffmpeg.Run(ff,Ffmpeg.TrimArgs(c.Path,part,from,to,true,"libx264"),to-from,report,trimCancel.Token);}
    File.Move(part,output);Track(output);string srcGame;if(clipGames.TryGetValue(c.Path,out srcGame)){clipGames[output]=srcGame;SavePrefs();}
    Log.Write("Клип сохранён: "+Path.GetFileName(c.Path)+" ["+ClipLibrary.Time(from,true)+"–"+ClipLibrary.Time(to,true)+(exact?", точно":"")+"] → "+Path.GetFileName(output));
    progress.Text="";Message("Сохранено: "+Path.GetFileName(output));if(!window.IsVisible)Notify("Клип сохранён",Path.GetFileName(output));
    LoadClipsSelect(output);}
   catch(OperationCanceledException){progress.Text="";Message("Сохранение отменено");}
   catch(Exception){progress.Text="";throw;}
   finally{trimBusy=false;TryDelete(part);trimCancel.Dispose();trimCancel=null;RenderTrim();}}
  static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch(Exception){}}

  // Файл может быть ещё занят проигрывателем пару сотен миллисекунд после закрытия — повторяем.
  async Task<T> WithFileRetry<T>(Func<T> action){for(int i=0;;i++){try{return action();}catch(IOException){if(i>=10)throw;}await Task.Delay(200);}}

  async Task RenameClip(){
   var c=SelectedOrThrow();string name=C<TextBox>("RenameBox").Text;string error=ClipLibrary.CheckName(name);if(error!=null)throw new InvalidOperationException(error);
   if(name.Trim()==c.Name)return;EnsureClipExists(c);
   ClosePlayer();string target=await WithFileRetry(()=>ClipLibrary.Rename(c.Path,name));managed.Rename(c.Path,target);string gm;if(clipGames.TryGetValue(c.Path,out gm)){clipGames.Remove(c.Path);clipGames[target]=gm;}SavePrefs();
   Log.Write("Клип переименован: "+Path.GetFileName(c.Path)+" → "+Path.GetFileName(target));Message("Переименовано: "+Path.GetFileName(target));
   LoadClipsSelect(target);}

  async Task DeleteClip(){
   if(dialogOpen)return; // вопрос уже открыт
   var c=SelectedOrThrow();EnsureClipExists(c);MessageBoxResult ok;dialogOpen=true;
   try{ok=MessageBox.Show(window,"Переместить «"+c.Name+c.Extension+"» в Корзину?\nЕго можно будет восстановить из Корзины.","Replay — клипы",MessageBoxButton.OKCancel,MessageBoxImage.Question);}
   finally{dialogOpen=false;window.Activate();}
   if(ok!=MessageBoxResult.OK)return;
   ClosePlayer();await WithFileRetry(()=>{ClipLibrary.Recycle(c.Path);return 0;});managed.Remove(c.Path);clipGames.Remove(c.Path);SavePrefs();
   Log.Write("Клип в Корзине: "+Path.GetFileName(c.Path));Message("В Корзине: "+c.Name+c.Extension);
   CloseDetail();LoadClipsSelect(null);}

  void PickFfmpeg(){
   dialogOpen=true;
   try{using(var d=new WF.OpenFileDialog{Title="Где ffmpeg.exe",Filter="ffmpeg.exe|ffmpeg.exe",CheckFileExists=true}){if(d.ShowDialog(new Owner(handle))!=WF.DialogResult.OK)return;ffmpegPath=d.FileName;ffmpegFound=null;}}
   finally{dialogOpen=false;window.Activate();}
   SavePrefs();bool ok=FindFfmpeg()!=null;C<FrameworkElement>("FfmpegPanel").Visibility=ok?Visibility.Collapsed:Visibility.Visible;Message(ok?"ffmpeg найден: обрезка доступна":"ffmpeg не найден");}

  // Повтор сохранён, запись остановлена — список обновляется, если галерея открыта.
  void ClipsChanged(){if(ClipsOpen&&window.IsVisible)LoadClipsSelect(null);}

  // ——— Индикатор записи ———
  void BindIndicator(){
   indicator=new RecordingIndicator(preview){Mode=indicatorMode,Corner=indicatorCorner};
   var box=C<ComboBox>("IndicatorBox");SelectTag(box,indicatorMode);
   box.SelectionChanged+=(s,e)=>{var it=box.SelectedItem as ComboBoxItem;if(it==null)return;indicatorMode=Convert.ToString(it.Tag);indicator.Mode=indicatorMode;SavePrefs();UpdateIndicator();};
   var corner=C<ComboBox>("IndicatorCornerBox");SelectTag(corner,indicatorCorner);
   corner.SelectionChanged+=(s,e)=>{var it=corner.SelectedItem as ComboBoxItem;if(it==null)return;indicatorCorner=Convert.ToString(it.Tag);indicator.Corner=indicatorCorner;SavePrefs();UpdateIndicator();};
   indicatorTimer.Tick+=(s,e)=>UpdateIndicator();indicatorTimer.Start();}
  bool indicatorFailed;
  void UpdateIndicator(){if(indicator==null||exiting)return;try{indicator.Update(obs.Connected&&correctProfile,record,DateTime.Now-recordStart,replay);}catch(Exception ex){if(!indicatorFailed){indicatorFailed=true;Log.Write("Индикатор: "+ex.Message);}}}
 }
}
