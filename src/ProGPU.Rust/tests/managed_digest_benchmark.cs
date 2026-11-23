using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ProGPU.Text;
static class Control {
 static readonly BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static;
 delegate bool Parse<T>(ReadOnlySpan<byte> data,ushort index,ushort extension,out T value);
 delegate bool Query<T>(ref T value,ushort glyph);
 static void Main(string[] args){CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;var shaper=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),Flags)!.MakeGenericMethod(shaper.GetNestedType("GlyphSetDigest",Flags)!).Invoke(null,new object[]{args[0],shaper});}
 static void Run<D>(string path,Type shaper) where D:struct {
  var parse=shaper.GetMethod("TryCreateRawLookupDigest",Flags)!.CreateDelegate<Parse<D>>();var query=typeof(D).GetMethod("MayHave",Flags)!.CreateDelegate<Query<D>>();
  byte[] data=File.ReadAllBytes(path);var watch=Stopwatch.StartNew();if(!parse(data,0,7,out D digest))throw new InvalidDataException();watch.Stop();double prepare=watch.Elapsed.TotalMicroseconds;
  byte[] output=new byte[65536];var times=new List<double>();
  for(int round=0;round<45;round++){watch.Restart();for(int i=0;i<output.Length;i++)output[i]=query(ref digest,unchecked((ushort)(i*257)))?(byte)1:(byte)0;watch.Stop();GC.KeepAlive(output);if(round>=5)times.Add(watch.Elapsed.TotalMicroseconds);}
  uint hash=2166136261;foreach(byte b in output)hash=unchecked((hash^b)*16777619);times.Sort();Console.WriteLine($"prepare_us={prepare:F3} queries=65536 checksum={hash} p50_us={times[19]:F3} p95_us={times[37]:F3} p99_us={times[39]:F3}");
 }
}
