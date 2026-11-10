using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ProGPU.Text;
CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
var type=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;
var query=type.GetMethod(args[0]=="0"?"FindCoverage":"GetGlyphClass",BindingFlags.Static|BindingFlags.NonPublic)!.CreateDelegate<Query>();
byte[] bytes=File.ReadAllBytes(args[1]);var glyphs=new ushort[65536];var output=new uint[glyphs.Length];
for(uint i=0;i<glyphs.Length;i++)glyphs[i]=(ushort)(i*25173u+13849u);
var times=new double[40];var allocations=new long[40];ulong checksum=0;
for(int round=0;round<45;round++){
 long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
 for(int i=0;i<glyphs.Length;i++)output[i]=unchecked((uint)query(bytes,0,glyphs[i]));
 double elapsed=Stopwatch.GetElapsedTime(start).TotalMicroseconds;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
 ulong hash=1469598103934665603;foreach(uint v in output)hash=unchecked((hash^v)*1099511628211);
 if(round!=0&&checksum!=hash)throw new InvalidDataException();checksum=hash;
 if(round>=5){times[round-5]=elapsed;allocations[round-5]=allocated;}
}
Array.Sort(times);Array.Sort(allocations);
Console.WriteLine($"queries=65536 checksum={checksum} min_us={times[0]:F3} p50_us={times[20]:F3} p95_us={times[37]:F3} max_us={times[39]:F3} allocated_p50={allocations[20]}");
delegate int Query(ReadOnlySpan<byte> data,int offset,ushort glyph);
