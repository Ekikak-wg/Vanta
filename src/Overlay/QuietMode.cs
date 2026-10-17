using System;
using System.Runtime.InteropServices;

namespace ReplayOverlay {
 // Режим Windows «Не беспокоить»: уведомления не всплывают и не звучат, а копятся в центре уведомлений.
 // Официального способа для обычных программ нет (FocusSessionManager — Limited Access Feature с токеном от Microsoft).
 // Поэтому — внутренний COM-объект Windows QuietHoursSettings, которым пользуется сама оболочка: выбранный профиль
 // «Unrestricted» — режим выключен, «PriorityOnly» — «Не беспокоить» (кнопка в Win+N). Описание интерфейса — проект
 // windows-focus-assist (Rafael Rivera, MIT). Событие WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED режим не включает: проверено.
 // Недокументировано: после записи профиль читается обратно, и если Windows его не приняла, меню честно об этом говорит.
 public static class QuietMode {
  [ComImport,Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IQuietHoursSettings {
   [PreserveSig] int GetUserSelectedProfile(out IntPtr profileId);
   [PreserveSig] int PutUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
  }
  static readonly Guid Clsid=new Guid("f53321fa-34f8-4b7f-b9a3-361877cb94cf");
  public const string Off="Microsoft.QuietHoursProfile.Unrestricted",DoNotDisturb="Microsoft.QuietHoursProfile.PriorityOnly";

  static T With<T>(Func<IQuietHoursSettings,T> act,T fallback){
   object o=null;
   try{o=Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid,true));return act((IQuietHoursSettings)o);}
   catch(Exception){return fallback;}
   finally{if(o!=null)try{Marshal.FinalReleaseComObject(o);}catch(Exception){}}}

  // Выбранный профиль; null — Windows не дала его прочитать.
  public static string Profile(){
   return With(s=>{IntPtr p;if(s.GetUserSelectedProfile(out p)!=0||p==IntPtr.Zero)return (string)null;try{return Marshal.PtrToStringUni(p);}finally{Marshal.FreeCoTaskMem(p);}},(string)null);}

  // Выбрать профиль и убедиться, что Windows его приняла.
  public static bool Set(string profile){
   return With(s=>s.PutUserSelectedProfile(profile)==0,false)&&Profile()==profile;}
 }
}
