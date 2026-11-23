// Actual original preparation, RawCoverageMatcher.Contains and ordered MayMatch,
// using the original GlyphSubstitutionBuffer and raw original GlyphRecord fields.
// Reflection is test transport only, outside all production APIs.
#nullable enable
using System.Reflection;
using System.Diagnostics;
using System.Globalization;
using ProGPU.Text;
static class Control {
 static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate bool Parse<C>(ReadOnlySpan<byte> data,ushort index,ushort extension,out C context);
 delegate bool Contains<M>(ref M matcher,ushort glyph);
 delegate bool Match<C,B,D>(C context,B buffer,in D digest);
 delegate void Add<D>(ref D digest,ushort glyph);
 delegate void Range<D>(ref D digest,ushort first,ushort last);
 static void Main(){var shaper=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),Flags)!.MakeGenericMethod(new[]{"RawContextRequirements","RawCoverageMatcher","GlyphSetDigest","GlyphSubstitutionBuffer","GlyphRecord"}.Select(n=>shaper.GetNestedType(n,Flags)!).ToArray()).Invoke(null,new object[]{shaper});}
 static void Run<C,M,D,B,G>(Type shaper) where C:class where M:struct where D:struct where B:class where G:struct {
  var parse=shaper.GetMethod("TryCreateRawContextRequirements",Flags)!.CreateDelegate<Parse<C>>();
  var contains=typeof(M).GetMethod("Contains",Flags)!.CreateDelegate<Contains<M>>();var match=typeof(C).GetMethod("MayMatch",Flags)!.CreateDelegate<Match<C,B,D>>();
  var add=typeof(D).GetMethod("Add",Flags)!.CreateDelegate<Add<D>>();var range=typeof(D).GetMethod("AddRange",Flags,new[]{typeof(ushort),typeof(ushort)})!.CreateDelegate<Range<D>>();
  var masks=new[]{"_shift4","_shift0","_shift6"}.Select(n=>typeof(D).GetField(n,Flags)!).ToArray();
  var glyphFields=new[]{"GlyphIndex","CodePoint","Substituted","ScriptShaper","ScriptFeatureMask","UseSyllable"}.Select(n=>typeof(G).GetField(n,Flags)!).ToArray();
  var ctor=typeof(B).GetConstructors(Flags).Single(c=>!c.IsStatic);var parameters=ctor.GetParameters();
  var manual=typeof(B).GetField("_manualJoiners",Flags)!;var restricted=typeof(B).GetField("_restrictLookupToSyllable",Flags)!;var syllable=typeof(B).GetField("_lookupSyllable",Flags)!;
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  var font=new TtfFont(input.ReadBytes(input.ReadInt32()));
  bool benchmark=Environment.GetEnvironmentVariable("PROGPU_RUST_CONTEXT_BENCHMARK")=="1";CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
  void Report(string name,List<double> times,long allocated){times.Sort();Console.Error.WriteLine($"{name}_p50_us={times[19]:F3} {name}_p95_us={times[37]:F3} {name}_p99_us={times[39]:F3} {name}_allocated_bytes_per_call={allocated/40}");}
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}
   ushort index=(ushort)input.ReadUInt32(),extension=(ushort)input.ReadUInt32();int queryCount=input.ReadInt32(),sampleCount=input.ReadInt32();
   byte[] data=input.ReadBytes(length);var queries=new ushort[queryCount];for(int i=0;i<queryCount;i++)queries[i]=(ushort)input.ReadUInt32();
   bool has=parse(data,index,extension,out C context);output.Write(has?1u:0u);
   if(benchmark){var times=new List<double>();long allocations=0;for(int round=0;round<45;round++){long before=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();bool ok=parse(data,index,extension,out C prepared);double us=Stopwatch.GetElapsedTime(start).TotalMicroseconds;long allocated=GC.GetAllocatedBytesForCurrentThread()-before;if(ok!=has)throw new InvalidDataException();GC.KeepAlive(prepared);if(round>=5){times.Add(us);allocations+=allocated;}}Report("prepare",times,allocations);}
   if(has){var subtables=(Array)typeof(C).GetField("_subtables",Flags)!.GetValue(context)!;var coverages=new List<M>();var descriptors=new List<uint[]>();
    foreach(object sub in subtables){Type t=sub.GetType();var items=(M[])t.GetProperty("Coverages",Flags)!.GetValue(sub)!;descriptors.Add(new[]{(uint)coverages.Count,(uint)items.Length,(uint)(int)t.GetProperty("BacktrackCount",Flags)!.GetValue(sub)!,(uint)(int)t.GetProperty("InputCount",Flags)!.GetValue(sub)!});coverages.AddRange(items);}
    output.Write((uint)(ushort)typeof(C).GetField("_lookupFlags",Flags)!.GetValue(context)!);output.Write((uint)descriptors.Count);output.Write((uint)coverages.Count);
    foreach(var descriptor in descriptors)foreach(uint n in descriptor)output.Write(n);
    foreach(M item in coverages){M matcher=item;object d=typeof(M).GetProperty("Digest",Flags)!.GetValue(matcher)!;foreach(var f in masks)output.Write((ulong)f.GetValue(d)!);foreach(ushort q in queries)output.Write(contains(ref matcher,q)?1u:0u);}
   }
   for(int sample=0;sample<sampleCount;sample++){
    uint state=input.ReadUInt32();byte active=(byte)input.ReadUInt32();int count=input.ReadInt32();bool allDigest=input.ReadUInt32()!=0;
    var glyphs=new List<G>(count);D digest=default;
    for(int i=0;i<count;i++){object g=default(G);for(int j=0;j<glyphFields.Length;j++){uint raw=input.ReadUInt32();glyphFields[j].SetValue(g,Convert.ChangeType(raw,glyphFields[j].FieldType));if(j==0)add(ref digest,(ushort)raw);}glyphs.Add((G)g);}
    if(allDigest)range(ref digest,0,65535);
    if(!has)continue;
    var buffer=(B)ctor.Invoke(new object[]{glyphs,font,Activator.CreateInstance(parameters[2].ParameterType)!,Activator.CreateInstance(parameters[3].ParameterType)!,ReadOnlyMemory<char>.Empty,ReadOnlyMemory<char>.Empty,false});
    manual.SetValue(buffer,(state&1)!=0);restricted.SetValue(buffer,(state&2)!=0);syllable.SetValue(buffer,active);
    bool matched=match(context,buffer,in digest);output.Write(matched?1u:0u);
    if(benchmark){var times=new List<double>();long allocations=0;for(int round=0;round<45;round++){int matches=0;long before=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int repeat=0;repeat<100;repeat++)if(match(context,buffer,in digest))matches++;double us=Stopwatch.GetElapsedTime(start).TotalMicroseconds/100;long allocated=(GC.GetAllocatedBytesForCurrentThread()-before)/100;if(matches!=(matched?100:0))throw new InvalidDataException();if(round>=5){times.Add(us);allocations+=allocated;}}Report("query",times,allocations);}
    ((IDisposable)buffer).Dispose();
   }
  }
 }
}
