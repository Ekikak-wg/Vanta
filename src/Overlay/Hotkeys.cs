using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace ReplayOverlay {
 // Сочетание клавиш: модификаторы WPF + клавиша. Хранится строкой вида «Ctrl+Alt+F10».
 public sealed class Hotkey {
  public readonly ModifierKeys Mods; public readonly Key Key;
  public Hotkey(ModifierKeys mods,Key key) { Mods=mods; Key=key; }
  public static Hotkey Parse(string text) {
   if(string.IsNullOrWhiteSpace(text))return null;
   var parts=text.Split('+').Select(p=>p.Trim()).Where(p=>p.Length>0).ToList(); if(parts.Count==0)return null;
   var mods=ModifierKeys.None;
   foreach(string p in parts.Take(parts.Count-1)) {
    if(p=="Ctrl")mods|=ModifierKeys.Control; else if(p=="Alt")mods|=ModifierKeys.Alt; else if(p=="Shift")mods|=ModifierKeys.Shift; else if(p=="Win")mods|=ModifierKeys.Windows; else return null;
   }
   Key key; return Enum.TryParse(parts[parts.Count-1],out key)&&key!=Key.None?new Hotkey(mods,key):null;
  }
  // Без модификаторов разрешены только F1–F24, иначе клавиша перестанет работать в играх и при наборе текста.
  public static bool Allowed(ModifierKeys mods,Key key) { return mods!=ModifierKeys.None||(key>=Key.F1&&key<=Key.F24); }
  public static bool IsModifier(Key k) { return k==Key.LeftCtrl||k==Key.RightCtrl||k==Key.LeftAlt||k==Key.RightAlt||k==Key.LeftShift||k==Key.RightShift||k==Key.LWin||k==Key.RWin; }
  IEnumerable<string> ModNames() { if((Mods&ModifierKeys.Control)!=0)yield return "Ctrl"; if((Mods&ModifierKeys.Alt)!=0)yield return "Alt"; if((Mods&ModifierKeys.Shift)!=0)yield return "Shift"; if((Mods&ModifierKeys.Windows)!=0)yield return "Win"; }
  public string Serialize() { return string.Join("+",ModNames().Concat(new[]{Key.ToString()})); }
  public override string ToString() { return string.Join(" + ",ModNames().Concat(new[]{KeyName(Key)})); }
  static string KeyName(Key k) {
   if(k>=Key.D0&&k<=Key.D9)return ((int)(k-Key.D0)).ToString();
   if(k>=Key.NumPad0&&k<=Key.NumPad9)return "Num "+(int)(k-Key.NumPad0);
   switch(k){case Key.PageUp:return "PgUp";case Key.PageDown:return "PgDn";case Key.Snapshot:return "PrtSc";case Key.Scroll:return "ScrLk";case Key.Back:return "Backspace";case Key.Return:return "Enter";case Key.OemTilde:return "~";case Key.OemMinus:return "-";case Key.OemPlus:return "=";}
   return k.ToString();
  }
  public uint Win32Modifiers { get { uint m=0; if((Mods&ModifierKeys.Alt)!=0)m|=1; if((Mods&ModifierKeys.Control)!=0)m|=2; if((Mods&ModifierKeys.Shift)!=0)m|=4; if((Mods&ModifierKeys.Windows)!=0)m|=8; return m|0x4000; } } // MOD_NOREPEAT
  public uint VirtualKey { get { return (uint)KeyInterop.VirtualKeyFromKey(Key); } }
  public bool Same(Hotkey other) { return other!=null&&other.Mods==Mods&&other.Key==Key; }
 }

 // Только чтение горячих клавиш самого OBS — чтобы предупредить о совпадении (тогда сработают оба).
 public static class ObsHotkeys {
  static readonly Dictionary<string,string> Names=new Dictionary<string,string>{
   {"ReplayBuffer.Save","сохранить повтор"},{"OBSBasic.StartRecording","начать запись"},{"OBSBasic.StopRecording","остановить запись"},
   {"OBSBasic.StartReplayBuffer","включить повтор"},{"OBSBasic.StopReplayBuffer","выключить повтор"},{"OBSBasic.StartStreaming","начать стрим"},
   {"OBSBasic.StopStreaming","остановить стрим"},{"OBSBasic.StartVirtualCam","включить виртуальную камеру"},{"OBSBasic.StopVirtualCam","выключить виртуальную камеру"},
   {"OBSBasic.Transition","переход"},{"libobs.mute","выключить звук"},{"libobs.unmute","включить звук"},{"libobs.push-to-mute","push-to-mute"},{"libobs.push-to-talk","push-to-talk"}};

  public static List<KeyValuePair<Hotkey,string>> Read(string profileIni,string scenesJson) {
   var list=new List<KeyValuePair<Hotkey,string>>();
   try {
    bool inside=false;
    foreach(string line in File.ReadAllLines(profileIni)) {
     if(line.StartsWith("[")){inside=line.Trim()=="[Hotkeys]";continue;}
     int eq=line.IndexOf('='); if(!inside||eq<0)continue;
     foreach(var pair in Json.Decode(line.Substring(eq+1)))Add(list,pair.Key=="bindings"?line.Substring(0,eq):pair.Key,null,pair.Value);
    }
   } catch { }
   try {
    object raw; var root=Json.Decode(File.ReadAllText(scenesJson));
    if(root.TryGetValue("sources",out raw)&&raw is object[])foreach(object s in (object[])raw){var src=Json.Obj(s);object hk;if(src.TryGetValue("hotkeys",out hk))foreach(var pair in Json.Obj(hk))Add(list,pair.Key,Json.Str(src,"name"),pair.Value);}
   } catch { }
   return list;
  }
  static void Add(List<KeyValuePair<Hotkey,string>> list,string name,string source,object bindings) {
   var arr=bindings as object[]; if(arr==null)return;
   string what; if(!Names.TryGetValue(name,out what))what=name;
   if(name=="OBSBasic.SelectScene"&&source!=null)what="сцена «"+source+"»"; else if(source!=null)what="«"+source+"»: "+what;
   foreach(object b in arr){var hk=FromObs(Json.Obj(b));if(hk!=null&&!list.Any(x=>x.Key.Same(hk)&&x.Value==what))list.Add(new KeyValuePair<Hotkey,string>(hk,what));}
  }
  static Hotkey FromObs(Dictionary<string,object> b) {
   string k=Json.Str(b,"key"); if(!k.StartsWith("OBS_KEY_"))return null; k=k.Substring(8);
   Key key;
   if(k.Length==1&&char.IsLetter(k[0]))key=(Key)Enum.Parse(typeof(Key),k);
   else if(k.Length==1&&char.IsDigit(k[0]))key=Key.D0+(k[0]-'0');
   else if(k.StartsWith("NUM")&&k.Length==4&&char.IsDigit(k[3]))key=Key.NumPad0+(k[3]-'0');
   else if(k.StartsWith("F")&&k.Length<=3&&k.Skip(1).All(char.IsDigit))key=(Key)Enum.Parse(typeof(Key),k);
   else { var map=new Dictionary<string,Key>{{"SPACE",Key.Space},{"TAB",Key.Tab},{"INSERT",Key.Insert},{"HOME",Key.Home},{"END",Key.End},{"PAGEUP",Key.PageUp},{"PAGEDOWN",Key.PageDown},{"DELETE",Key.Delete},{"PAUSE",Key.Pause},{"PRINT",Key.Snapshot},{"SCROLLLOCK",Key.Scroll}}; if(!map.TryGetValue(k,out key))return null; }
   var mods=ModifierKeys.None;
   if(Json.Bool(b,"control"))mods|=ModifierKeys.Control; if(Json.Bool(b,"alt"))mods|=ModifierKeys.Alt; if(Json.Bool(b,"shift"))mods|=ModifierKeys.Shift; if(Json.Bool(b,"command"))mods|=ModifierKeys.Windows;
   return new Hotkey(mods,key);
  }
 }
}
