#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using ProGPU.Text;
static class Control{
 static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 delegate List<T> Select<T>(TtfFont font,ReadOnlySpan<byte> data,IReadOnlyList<OpenTypeFeatureSetting> settings,string script,string? language,ushort extension,out bool usesVariations);
 static void Main(string[] args){CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;var shaper=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),Flags)!.MakeGenericMethod(shaper.GetNestedType("EnabledLookup",Flags)!).Invoke(null,new object[]{args,shaper});}
 static void Run<T>(string[] args,Type shaper){
  var select=shaper.GetMethod("GetRawEnabledLookupIndices",Flags)!.CreateDelegate<Select<T>>();var index=typeof(T).GetProperty("LookupIndex",Flags)!;
  byte[] data=File.ReadAllBytes(args[0]);var font=new TtfFont(File.ReadAllBytes(args[1]));int U16(int p)=>(data[p]<<8)|data[p+1];int at=U16(6);var settings=new OpenTypeFeatureSetting[U16(at)];for(int i=0;i<settings.Length;i++)settings[i]=new(Encoding.ASCII.GetString(data,at+2+i*6,4));
  var times=new List<double>();uint checksum=0;int count=0;var watch=new Stopwatch();long allocations=0;
  for(int round=0;round<45;round++){long before=GC.GetAllocatedBytesForCurrentThread();watch.Restart();var result=select(font,data,settings,"DFLT",null,7,out _);watch.Stop();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
   uint hash=2166136261;foreach(T value in result)hash=unchecked((hash^(ushort)index.GetValue(value)!)*16777619);if(round!=0&&hash!=checksum)throw new InvalidDataException();checksum=hash;count=result.Count;if(round>=5){times.Add(watch.Elapsed.TotalMicroseconds);allocations+=allocated;}}
  times.Sort();Console.WriteLine($"lookups={count} checksum={checksum} p50_us={times[19]:F3} p95_us={times[37]:F3} p99_us={times[39]:F3} allocated_bytes_per_call={allocations/40}");
 }
}
