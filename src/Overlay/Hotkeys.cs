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

}
