// Actual unchanged current C# executor and complete original records.
#nullable enable
using System.Reflection;
using ProGPU.Text;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate K Classify<B,G,K>(B buffer,G glyph);
 delegate bool Apply<B>(ReadOnlySpan<byte> data,B buffer,ushort lookup,int position);
 delegate bool Reverse<B>(ReadOnlySpan<byte> data,B buffer,int offset,int position,ushort flags);
 static void Main(){var t=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(t.GetNestedType("GlyphSubstitutionBuffer",F)!,t.GetNestedType("GlyphRecord",F)!,t.GetNestedType("GlyphSubstitutionBuffer",F)!.GetMethod("GetGlyphClassKind",F)!.ReturnType).Invoke(null,new object[]{t});}
 static void Run<B,G,K>(Type shaper) where B:class where G:struct where K:Enum {
  var apply=shaper.GetMethod("ApplyNestedLookup",F)!.CreateDelegate<Apply<B>>();var reverse=shaper.GetMethod("ApplyReverseChainingSubtable",F)!.CreateDelegate<Reverse<B>>();
  var classify=typeof(B).GetMethod("GetGlyphClassKind",F)!.CreateDelegate<Classify<B,G,K>>();
  Console.Error.WriteLine("Original GDEF class storage: "+Enum.GetUnderlyingType(typeof(K)).Name);
  var ctor=typeof(B).GetConstructors(F).Single(c=>!c.IsStatic);var parameters=ctor.GetParameters();
  var fields=new[]{"GlyphIndex","Cluster","GraphemeCluster","CharacterCluster","CodePoint","Flags","AdvanceX","AdvanceY","OffsetX","OffsetY","ArabicAction","SpaceFallback","FractionAction","ScriptFeatureMask","ScriptShaper","UseCategory","UseSyllable","IndicCategory","IndicPosition","IndicOriginalOrder","UseRphfEligible","Substituted","Ligated","MultipleSubstitutionComponent","Multiplied","LigatureComponentCount","LigatureComponent","HasCharacterCluster","Positioned","AttachmentKind","AttachmentTarget"}.Select(n=>typeof(G).GetField(n,F)!).ToArray();
  var manual=typeof(B).GetField("_manualJoiners",F)!;var restricted=typeof(B).GetField("_restrictLookupToSyllable",F)!;var syllable=typeof(B).GetField("_lookupSyllable",F)!;
  var context=typeof(B).GetField("_contextMatchEnd",F)!;var random=typeof(B).GetField("_randomState",F)!;var mark=typeof(B).GetField("_markFilteringCoverage",F)!;var depth=typeof(B).GetField("_nestedLookupDepth",F)!;
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  var fonts=new List<TtfFont>();int fontCount=input.ReadInt32();for(int i=0;i<fontCount;i++)fonts.Add(new TtfFont(input.ReadBytes(input.ReadInt32())));
  uint Class(B buffer,G g){try{return Convert.ToUInt32(classify(buffer,g));}catch(ArgumentOutOfRangeException){return 0x80000006u;}}
  if(Environment.GetEnvironmentVariable("PROGPU_RUST_GSUB_MARK_PROBE")=="1"){
   var buffer=(B)ctor.Invoke(new object[]{new List<G>(),fonts[0],Activator.CreateInstance(parameters[2].ParameterType)!,Activator.CreateInstance(parameters[3].ParameterType)!,ReadOnlyMemory<char>.Empty,ReadOnlyMemory<char>.Empty,false});
   var cpField=typeof(G).GetField("CodePoint",F)!;
   for(uint cp=0;cp<=0x110010;cp++){object g=default(G);cpField.SetValue(g,cp);output.Write(Class(buffer,(G)g));}
   return;
  }
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}
   int fontIndex=input.ReadInt32();ushort lookup=(ushort)input.ReadUInt32();int position=input.ReadInt32(),reverseOffset=input.ReadInt32();ushort flags=(ushort)input.ReadUInt32();uint state=input.ReadUInt32();byte active=(byte)input.ReadUInt32();int end=input.ReadInt32();uint rng=input.ReadUInt32();int count=input.ReadInt32();
   byte[] data=input.ReadBytes(length);var glyphs=new List<G>(count);
   for(int i=0;i<count;i++){object g=default(G);foreach(var field in fields){uint raw=input.ReadUInt32();Type t=field.FieldType;object value;if(t.IsEnum)value=Enum.ToObject(t,raw);else if(t==typeof(ushort))value=(ushort)raw;else if(t==typeof(short))value=unchecked((short)raw);else if(t==typeof(int))value=unchecked((int)raw);else if(t==typeof(byte))value=(byte)raw;else value=raw;field.SetValue(g,value);}glyphs.Add((G)g);}
   var buffer=(B)ctor.Invoke(new object[]{glyphs,fonts[fontIndex],Activator.CreateInstance(parameters[2].ParameterType)!,Activator.CreateInstance(parameters[3].ParameterType)!,ReadOnlyMemory<char>.Empty,ReadOnlyMemory<char>.Empty,false});
   manual.SetValue(buffer,(state&1)!=0);restricted.SetValue(buffer,(state&2)!=0);syllable.SetValue(buffer,active);context.SetValue(buffer,end);random.SetValue(buffer,rng);
   if(Environment.GetEnvironmentVariable("PROGPU_RUST_GSUB_BOUND_PROBE")=="1")Console.Error.WriteLine("Entering original bounded-work witness");
   uint error=0;bool changed=false;
   try {
    bool Invoke(int at)=>reverseOffset<0?apply(data,buffer,lookup,at):reverse(data,buffer,reverseOffset,at,flags);
    if(Environment.GetEnvironmentVariable("PROGPU_RUST_MANAGED_GSUB_BENCHMARK")=="1") {
     var original=new List<G>(glyphs);var positions=Enumerable.Range(0,glyphs.Count).Where(i=>(ushort)fields[0].GetValue(glyphs[i])! == 10).Reverse().ToArray();
     glyphs.EnsureCapacity(count*3+4);var times=new List<double>();var allocations=new List<long>();
     for(int round=0;round<45;round++) {
      glyphs.Clear();glyphs.AddRange(original);context.SetValue(buffer,end);random.SetValue(buffer,rng);changed=false;
      long allocated=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();
      foreach(int at in positions)changed |= Invoke(at);
      double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;
      allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
      if(round>=5){times.Add(elapsed);allocations.Add(allocated);}
     }
     times.Sort();allocations.Sort();
     Console.Error.WriteLine(FormattableString.Invariant($"execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));
    } else changed=Invoke(position);
   }catch(IndexOutOfRangeException){error=4;}catch(ArgumentOutOfRangeException){error=6;}catch(InvalidOperationException){error=7;}

   output.Write(error);output.Write(changed?1u:0u);output.Write((uint)glyphs.Count);output.Write((int)context.GetValue(buffer)!);output.Write((uint)random.GetValue(buffer)!);output.Write((int)mark.GetValue(buffer)!);output.Write((int)depth.GetValue(buffer)!);
   foreach(G g in glyphs)output.Write(Class(buffer,g));
   foreach(G g in glyphs)foreach(var field in fields)output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(g)!)));
   ((IDisposable)buffer).Dispose();
  }
 }
}
