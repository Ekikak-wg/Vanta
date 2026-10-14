using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReplayOverlay {
 // Файлы, которые создал Replay (повторы, записи, скриншоты, отрезки). Автоочистка трогает только их.
 public sealed class ManagedFiles {
  const int Limit=20000;
  readonly List<string> paths=new List<string>();
  public IList<string> Paths{get{return paths.AsReadOnly();}}
  public void Load(object raw){paths.Clear();var arr=raw as object[];if(arr==null)return;foreach(object x in arr){string p=Convert.ToString(x);if(!string.IsNullOrEmpty(p)&&!Has(p))paths.Add(p);}}
  public object[] ToJson(){return paths.Cast<object>().ToArray();}
  public bool Has(string p){return paths.Any(x=>string.Equals(x,p,StringComparison.OrdinalIgnoreCase));}
  public void Add(string p){if(string.IsNullOrEmpty(p)||Has(p))return;paths.Add(p);while(paths.Count>Limit)paths.RemoveAt(0);}
  public void Remove(string p){paths.RemoveAll(x=>string.Equals(x,p,StringComparison.OrdinalIgnoreCase));}
  public void Rename(string from,string to){int i=paths.FindIndex(x=>string.Equals(x,from,StringComparison.OrdinalIgnoreCase));if(i>=0)paths[i]=to;}
  // Убрать из списка то, чего уже нет на диске (удалили вручную).
  public int Prune(){return paths.RemoveAll(p=>!File.Exists(p));}
 }

 public static class Cleanup {
  public sealed class Item{public string Path;public DateTime Modified;public long Size;}
  // Что удалить: старше maxDays (0 — без ограничения), затем самые старые, пока общий размер больше maxGb (0 — без ограничения).
  // Файлы, которые ещё пишутся, сюда не передаются.
  public static List<Item> Plan(IEnumerable<Item> files,int maxDays,int maxGb,DateTime now){
   var all=files.OrderBy(f=>f.Modified).ToList();var del=new List<Item>();
   if(maxDays>0)foreach(var f in all)if((now-f.Modified).TotalDays>maxDays)del.Add(f);
   if(maxGb>0){long limit=(long)maxGb*1073741824L;long total=all.Where(f=>!del.Contains(f)).Sum(f=>f.Size);
    foreach(var f in all){if(total<=limit)break;if(del.Contains(f))continue;del.Add(f);total-=f.Size;}}
   return del;}
 }
}
