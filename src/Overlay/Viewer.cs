using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF=System.Windows.Forms;

namespace ReplayOverlay {
 // Просмотр клипа или скриншота во весь экран: видео — пуск, пауза, перемотка, громкость;
 // картинка — масштаб колесом и кнопками, перетаскивание мышью. Окно не попадает в запись. Esc — закрыть.
 public sealed class MediaViewer {
  [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h,uint affinity);
  readonly Window w;MediaElement player;Slider seek;TextBlock time;Button play;DispatcherTimer timer;bool playing,syncing;
  Image image;ScaleTransform scale;TranslateTransform pan;Point dragFrom;bool dragging;
  public event Action<double,double> Closed; // позиция видео (с), громкость 0…1 — чтобы вернуть их в маленький проигрыватель

  static Brush B(string c){return OverlayWindows.B(c);}
  static Button Btn(string glyph,string tip){return new Button{Content=new TextBlock{Text=glyph,FontFamily=OverlayWindows.Icons,FontSize=15,Foreground=B("#F5F6F7")},Width=40,Height=36,Padding=new Thickness(0),Background=B("#33FFFFFF"),ToolTip=tip,Margin=new Thickness(0,0,6,0)};}

  public MediaViewer(Window owner,string path,bool isImage,double position,double volume,bool muted,bool capturable){
   w=new Window{Title="Replay — просмотр",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Background=B("#F0000000"),Topmost=true,ShowInTaskbar=false,Owner=owner,FontFamily=new FontFamily("Segoe UI"),Foreground=B("#F5F6F7"),UseLayoutRounding=true};
   var area=WF.Screen.FromHandle(new WindowInteropHelper(owner).Handle).Bounds;
   w.SourceInitialized+=(s,e)=>{var src=PresentationSource.FromVisual(w);double sx=1,sy=1;if(src!=null){sx=src.CompositionTarget.TransformFromDevice.M11;sy=src.CompositionTarget.TransformFromDevice.M22;}
    w.Left=area.Left*sx;w.Top=area.Top*sy;w.Width=area.Width*sx;w.Height=area.Height*sy;if(!capturable)SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle,0x11);};
   var root=new Grid();root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var bar=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(20,10,20,20),HorizontalAlignment=HorizontalAlignment.Center};Grid.SetRow(bar,1);
   var title=new TextBlock{Text=System.IO.Path.GetFileName(path),FontSize=14,Foreground=B("#C4CAD2"),Margin=new Thickness(20,16,0,0),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
   var close=Btn("","Закрыть (Esc)");close.HorizontalAlignment=HorizontalAlignment.Right;close.VerticalAlignment=VerticalAlignment.Top;close.Margin=new Thickness(0,12,16,0);close.Click+=(s,e)=>w.Close();
   Style sliderStyle=owner.TryFindResource(typeof(Slider)) as Style; // те же ползунки, что в меню
   if(!isImage){
    player=new MediaElement{LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,ScrubbingEnabled=true,Stretch=Stretch.Uniform,Volume=volume,IsMuted=muted,Source=new Uri(path)};root.Children.Add(player);
    play=Btn("","Пауза (пробел)");play.Click+=(s,e)=>Toggle();bar.Children.Add(play);
    seek=new Slider{Width=Math.Max(300,area.Width*0.45),VerticalAlignment=VerticalAlignment.Center,IsMoveToPointEnabled=true,Margin=new Thickness(4,0,12,0)};seek.ValueChanged+=(s,e)=>{if(!syncing)player.Position=TimeSpan.FromSeconds(e.NewValue);UpdateTime();};if(sliderStyle!=null)seek.Style=sliderStyle;bar.Children.Add(seek);
    time=new TextBlock{FontSize=13,Foreground=B("#C4CAD2"),VerticalAlignment=VerticalAlignment.Center,Width=120};bar.Children.Add(time);
    var mute=Btn(muted?"":"","Звук вкл / выкл");mute.Click+=(s,e)=>{player.IsMuted=!player.IsMuted;((TextBlock)mute.Content).Text=player.IsMuted?"":"";};bar.Children.Add(mute);
    var vol=new Slider{Width=110,Minimum=0,Maximum=1,Value=volume,VerticalAlignment=VerticalAlignment.Center,ToolTip="Громкость"};vol.ValueChanged+=(s,e)=>{player.Volume=e.NewValue;if(player.IsMuted&&e.NewValue>0){player.IsMuted=false;((TextBlock)mute.Content).Text="";}};if(sliderStyle!=null)vol.Style=sliderStyle;bar.Children.Add(vol);
    player.MediaOpened+=(s,e)=>{if(player.NaturalDuration.HasTimeSpan)seek.Maximum=player.NaturalDuration.TimeSpan.TotalSeconds;player.Position=TimeSpan.FromSeconds(position);player.Play();playing=true;timer.Start();UpdateTime();};
    player.MediaEnded+=(s,e)=>{playing=false;player.Pause();((TextBlock)play.Content).Text="";};
    timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=(s,e)=>{syncing=true;seek.Value=player.Position.TotalSeconds;syncing=false;UpdateTime();};
    player.Pause();}
   else{
    var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.UriSource=new Uri(path);bi.EndInit();bi.Freeze();
    scale=new ScaleTransform(1,1);pan=new TranslateTransform();var tg=new TransformGroup();tg.Children.Add(scale);tg.Children.Add(pan);
    image=new Image{Source=bi,Stretch=Stretch.Uniform,RenderTransform=tg,RenderTransformOrigin=new Point(0.5,0.5),Cursor=Cursors.SizeAll};
    var host=new Border{Background=Brushes.Transparent,ClipToBounds=true,Child=image};root.Children.Add(host);
    host.MouseWheel+=(s,e)=>Zoom(e.Delta>0?1.2:1/1.2);
    host.MouseLeftButtonDown+=(s,e)=>{dragging=true;dragFrom=e.GetPosition(host);host.CaptureMouse();};
    host.MouseMove+=(s,e)=>{if(!dragging)return;var p=e.GetPosition(host);pan.X+=p.X-dragFrom.X;pan.Y+=p.Y-dragFrom.Y;dragFrom=p;};
    host.MouseLeftButtonUp+=(s,e)=>{dragging=false;host.ReleaseMouseCapture();};
    var minus=Btn("","Уменьшить");minus.Click+=(s,e)=>Zoom(1/1.25);bar.Children.Add(minus);
    var plus=Btn("","Увеличить");plus.Click+=(s,e)=>Zoom(1.25);bar.Children.Add(plus);
    var fit=Btn("","Вписать в экран");fit.Click+=(s,e)=>{scale.ScaleX=scale.ScaleY=1;pan.X=pan.Y=0;};bar.Children.Add(fit);
    bar.Children.Add(new TextBlock{Text=bi.PixelWidth+" × "+bi.PixelHeight,FontSize=13,Foreground=B("#C4CAD2"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,16,0)});}
   var original=new Button{Content="Открыть файл",Padding=new Thickness(14,8,14,8),Background=B("#33FFFFFF"),Foreground=B("#F5F6F7"),ToolTip="В программе по умолчанию"};
   original.Click+=(s,e)=>{try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});w.Close();}catch(Exception){}};bar.Children.Add(original);
   root.Children.Add(bar);root.Children.Add(title);root.Children.Add(close);w.Content=root;
   w.KeyDown+=(s,e)=>{if(e.Key==Key.Escape){w.Close();e.Handled=true;}else if(e.Key==Key.Space&&player!=null){Toggle();e.Handled=true;}
    else if(player!=null&&(e.Key==Key.Left||e.Key==Key.Right)){player.Position+=TimeSpan.FromSeconds(e.Key==Key.Left?-5:5);e.Handled=true;}};
   w.Closed+=(s,e)=>{double pos=0,vol=volume;if(player!=null){pos=player.Position.TotalSeconds;vol=player.Volume;if(timer!=null)timer.Stop();player.Stop();player.Close();}if(Closed!=null)Closed(pos,vol);};
  }
  void Toggle(){if(playing){player.Pause();playing=false;((TextBlock)play.Content).Text="";}else{if(seek.Maximum>0&&player.Position.TotalSeconds>=seek.Maximum-0.05)player.Position=TimeSpan.Zero;player.Play();playing=true;((TextBlock)play.Content).Text="";}}
  void UpdateTime(){if(time==null)return;time.Text=ClipLibrary.Time(player.Position.TotalSeconds,true)+" / "+ClipLibrary.Time(seek.Maximum);}
  void Zoom(double k){double s=Math.Max(0.2,Math.Min(16,scale.ScaleX*k));scale.ScaleX=scale.ScaleY=s;}
  public void Show(){w.Show();w.Activate();}
 }
}
