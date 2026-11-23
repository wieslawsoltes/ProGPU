// Execute original process-neutral variation data. Name resolution is the same
// explicit callback dependency as the source constructor, with a frozen fixture.
#nullable enable
using System.Reflection;
using System.Numerics;
using ProGPU.Text;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate D? Create<D>(byte[] b,Dictionary<string,(uint,uint)> tables,Func<ushort,string?> names,int count);
 delegate void Simple<D,I>(D d,I i,ushort glyph,Vector2[] p,ushort[] ends);
 static uint Error(Exception e){while(e is TargetInvocationException&&e.InnerException is {} inner)e=inner;return e switch{IndexOutOfRangeException or InvalidDataException or OverflowException=>4u,ArgumentException=>1u,_=>throw e};}
 static string? Name(ushort n)=>n%3==0?null:n%3==1?"Axis\0"+(char)n:"\ud800"+(char)n;
 static void Bench(Action action){var times=new List<double>();var allocations=new List<long>();for(int n=0;n<45;n++){long bytes=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();action();double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;bytes=GC.GetAllocatedBytesForCurrentThread()-bytes;if(n>=5){times.Add(elapsed);allocations.Add(bytes);}}times.Sort();allocations.Sort();Console.Error.WriteLine(FormattableString.Invariant($"execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));}
 static void Main(){var a=typeof(TtfFont).Assembly;typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(a.GetType("ProGPU.Text.OpenTypeVariationData",true)!,a.GetType("ProGPU.Text.OpenTypeVariationInstance",true)!).Invoke(null,null);}
 static void Run<D,I>() where D:class where I:class {
  var create=typeof(D).GetMethod("TryCreate",F)!.CreateDelegate<Create<D>>();
  var instance=typeof(D).GetMethod("CreateInstance",F)!.CreateDelegate<Func<D,IReadOnlyList<FontVariationSetting>,I>>();
  var advance=typeof(D).GetMethod("GetAdvanceDelta",F)!.CreateDelegate<Func<D,I,ushort,float>>();
  var layout=typeof(D).GetMethod("GetLayoutDelta",F)!.CreateDelegate<Func<D,I,ushort,ushort,float>>();
  var metric=typeof(D).GetMethod("GetMetricDelta",F)!.CreateDelegate<Func<D,I,string,float>>();
  var phantom=typeof(D).GetMethod("GetGlyphPhantomAdvanceDelta",F)!.CreateDelegate<Func<D,I,ushort,int,float>>();
  var simple=typeof(D).GetMethod("ApplySimpleGlyphDeltas",F)!.CreateDelegate<Simple<D,I>>();
  var composite=typeof(D).GetMethod("GetCompositeGlyphDeltas",F)!.CreateDelegate<Func<D,I,ushort,int,Vector2[]>>();
  var axes=typeof(D).GetProperty("Axes",F)!.GetGetMethod(true)!.CreateDelegate<Func<D,IReadOnlyList<FontVariationAxis>>>();
  var usePhantom=typeof(D).GetProperty("UsesGlyphPhantomAdvance",F)!.GetGetMethod(true)!.CreateDelegate<Func<D,bool>>();
  var coords=typeof(I).GetProperty("NormalizedCoordinates",F)!.GetGetMethod()!.CreateDelegate<Func<I,short[]>>();
  var settings=typeof(I).GetProperty("Settings",F)!.GetGetMethod()!.CreateDelegate<Func<I,FontVariationSetting[]>>();
  var scalars=typeof(I).GetProperty("RegionScalars",F)!.GetGetMethod()!.CreateDelegate<Func<I,float[]>>();
  var key=typeof(I).GetProperty("CacheKey",F)!.GetGetMethod()!.CreateDelegate<Func<I,string>>();
  var isDefault=typeof(I).GetProperty("IsDefault",F)!.GetGetMethod()!.CreateDelegate<Func<I,bool>>();
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  string ReadString(){int count=input.ReadInt32();var v=new char[count];for(int n=0;n<count;n++)v[n]=(char)input.ReadUInt32();return new(v);}
  void WriteString(string v){output.Write((uint)v.Length);foreach(char c in v)output.Write((uint)c);}
  void Float(float v)=>output.Write(BitConverter.SingleToUInt32Bits(v));
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}byte[] b=input.ReadBytes(length);int glyphCount=input.ReadInt32();
   var selected=new FontVariationSetting[input.ReadInt32()];for(int n=0;n<selected.Length;n++)selected[n]=new(ReadString(),BitConverter.UInt32BitsToSingle(input.ReadUInt32()));
   int count=input.ReadInt32();var queries=new (uint Kind,ushort A,ushort B,int Count,Vector2[] Points,ushort[] Ends)[count];
   for(int n=0;n<count;n++){uint kind=input.ReadUInt32();ushort a=(ushort)input.ReadUInt32(),c=(ushort)input.ReadUInt32();int items=input.ReadInt32();var p=new Vector2[input.ReadInt32()];for(int j=0;j<p.Length;j++)p[j]=new(BitConverter.UInt32BitsToSingle(input.ReadUInt32()),BitConverter.UInt32BitsToSingle(input.ReadUInt32()));var ends=new ushort[input.ReadInt32()];for(int j=0;j<ends.Length;j++)ends[j]=(ushort)input.ReadUInt32();queries[n]=(kind,a,c,items,p,ends);}
   var tables=new Dictionary<string,(uint,uint)>();int ntable=(b[4]<<8)|b[5];uint U(int p)=>(uint)((b[p]<<24)|(b[p+1]<<16)|(b[p+2]<<8)|b[p+3]);for(int n=0;n<ntable;n++){int p=12+n*16;tables[new string(new[]{(char)b[p],(char)b[p+1],(char)b[p+2],(char)b[p+3]})]=(U(p+8),U(p+12));}
   D? data=null;if(Environment.GetEnvironmentVariable("PROGPU_RUST_INSTANCE_BENCHMARK")=="admission")Bench(()=>data=create(b,tables,Name,glyphCount));else data=create(b,tables,Name,glyphCount);output.Write(data is null?0u:1u);if(data is null)continue;
   output.Write(usePhantom(data)?1u:0u);var inventory=axes(data);output.Write((uint)inventory.Count);foreach(var a in inventory){WriteString(a.Tag);WriteString(a.Name);Float(a.Minimum);Float(a.Default);Float(a.Maximum);output.Write(a.IsHidden?1u:0u);}
   I? inst=null;uint error=0;try{if(Environment.GetEnvironmentVariable("PROGPU_RUST_INSTANCE_BENCHMARK")=="instance")Bench(()=>inst=instance(data,selected));else inst=instance(data,selected);}catch(Exception e){error=Error(e);}output.Write(error);if(inst is null)continue;
   output.Write(isDefault(inst)?1u:0u);var normalized=coords(inst);output.Write((uint)normalized.Length);foreach(short v in normalized)output.Write((int)v);WriteString(key(inst));var chosen=settings(inst);output.Write((uint)chosen.Length);foreach(var v in chosen){WriteString(v.Tag);Float(v.Value);}var weights=scalars(inst);output.Write((uint)weights.Length);foreach(float v in weights)Float(v);
   foreach(var q in queries){uint failure=0;float delta=0;Vector2[] result=Array.Empty<Vector2>();try{switch(q.Kind){case 0:delta=layout(data,inst,q.A,q.B);break;case 1:delta=advance(data,inst,q.A);break;case 2:delta=metric(data,inst,new[]{"hasc","hdsc","xhgt","test","???x"}[q.A%5]);break;case 3:result=q.Points;simple(data,inst,q.A,result,q.Ends);break;case 4:result=composite(data,inst,q.A,q.Count);break;case 5:delta=phantom(data,inst,q.A,q.Count);break;}}
    catch(Exception e){failure=Error(e);}output.Write(failure);Float(delta);output.Write((uint)result.Length);foreach(var p in result){Float(p.X);Float(p.Y);}
   }
  }
 }
}
