// Calls actual original TtfFont and its retained SfntFontFace, never a mock parser.
#nullable enable
using System.Reflection;
using ProGPU.Text;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate bool Nominal<T>(T face,uint code,out ushort glyph);
 static uint Error(Exception e)=>e is OutOfMemoryException?7u:e is ArgumentException?1u:4u;
 static void Main()=>typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(typeof(TtfFont).Assembly.GetType("ProGPU.Text.SfntFontFace",true)!).Invoke(null,null);
 static void Run<T>() {
  var nominal=typeof(T).GetMethod("TryGetGlyphIndex",F)!.CreateDelegate<Nominal<T>>();var faceField=typeof(TtfFont).GetField("_face",F)!;
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}byte[] bytes=input.ReadBytes(length);int count=input.ReadInt32();var queries=new (uint Kind,uint Code,uint Selector)[count];for(int i=0;i<count;i++)queries[i]=(input.ReadUInt32(),input.ReadUInt32(),input.ReadUInt32());
   TtfFont? font=null;uint failure=0;try{font=new TtfFont(bytes);}catch(Exception e){failure=Error(e);}output.Write(failure);if(font is null)continue;T face=(T)faceField.GetValue(font)!;
   var results=new (uint Error,uint Found,uint Glyph)[count];
   void Execute(){for(int i=0;i<count;i++){var q=queries[i];uint error=0;bool found=false;ushort glyph=0;try{switch(q.Kind){case 0:found=nominal(face,q.Code,out glyph);break;case 1:glyph=font.GetGlyphIndex(q.Code);found=true;break;case 2:found=font.TryGetVariationGlyph(q.Code,q.Selector,out glyph);break;case 3:found=font.HasGlyph(q.Code);break;case 4:found=font.UsesSymbolCharacterMap;break;default:throw new ArgumentException();}}catch(Exception e){error=Error(e);}results[i]=(error,found?1u:0u,glyph);}}
   if(Environment.GetEnvironmentVariable("PROGPU_RUST_MAPPING_BENCHMARK") is not null){var times=new List<double>();var allocations=new List<long>();double first=0;for(int n=0;n<45;n++){long allocated=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();Execute();double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;if(n==0)first=elapsed;if(n>=5){times.Add(elapsed);allocations.Add(allocated);}}times.Sort();allocations.Sort();Console.Error.WriteLine(FormattableString.Invariant($"first_us={first:F3} execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));}else Execute();
   foreach(var r in results){output.Write(r.Error);output.Write(r.Found);output.Write(r.Glyph);}
  }
 }
}
