// Execute actual original raw GPOS and attachment methods. Reflection sets up
// original private buffers; typed delegates execute unchanged production methods.
#nullable enable
using System.Reflection;
using ProGPU.Text;
using ProGPU.Text.Shaping;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate bool Apply<B>(ReadOnlySpan<byte> data,B buffer,ushort lookup,int position);
 delegate K Classify<B,K>(B buffer,int index);
 delegate void Resolve<B>(B buffer);
 delegate void Zero<B>(B buffer,bool adjust);
 static void Main(){var t=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;var b=t.GetNestedType("GlyphPositionBuffer",F)!;typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(b,t.GetNestedType("GlyphSubstitutionBuffer",F)!,t.GetNestedType("GlyphRecord",F)!,b.GetMethod("GetGlyphClass",F)!.ReturnType).Invoke(null,new object[]{t});}
 static void Run<B,S,G,K>(Type shaper) where B:class where S:class where G:struct where K:Enum {
  var apply=shaper.GetMethod("ApplyRawPositionLookupAt",F)!.CreateDelegate<Apply<B>>();
  var resolve=typeof(B).GetMethod("ResolveAttachmentOffsets",F)!.CreateDelegate<Resolve<B>>();var zero=typeof(B).GetMethod("ZeroMarkAdvancesLate",F)!.CreateDelegate<Zero<B>>();var classify=typeof(B).GetMethod("GetGlyphClass",F)!.CreateDelegate<Classify<B,K>>();
  var ctor=typeof(B).GetConstructors(F).Single(c=>!c.IsStatic);var subCtor=typeof(S).GetConstructors(F).Single(c=>!c.IsStatic);var sp=subCtor.GetParameters();
  var fields=new[]{"GlyphIndex","Cluster","GraphemeCluster","CharacterCluster","CodePoint","Flags","AdvanceX","AdvanceY","OffsetX","OffsetY","ArabicAction","SpaceFallback","FractionAction","ScriptFeatureMask","ScriptShaper","UseCategory","UseSyllable","IndicCategory","IndicPosition","IndicOriginalOrder","UseRphfEligible","Substituted","Ligated","MultipleSubstitutionComponent","Multiplied","LigatureComponentCount","LigatureComponent","HasCharacterCluster","Positioned","AttachmentKind","AttachmentTarget"}.Select(n=>typeof(G).GetField(n,F)!).ToArray();
  var array=typeof(B).GetField("_glyphs",F)!;var size=typeof(B).GetField("_count",F)!;var mark=typeof(B).GetField("_markFilteringCoverage",F)!;
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  var fonts=new List<TtfFont>();int fontCount=input.ReadInt32();for(int i=0;i<fontCount;i++)fonts.Add(new TtfFont(input.ReadBytes(input.ReadInt32())));
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}
   int fontIndex=input.ReadInt32();ushort lookup=(ushort)input.ReadUInt32();int position=input.ReadInt32();var direction=(ShapingDirection)input.ReadUInt32();uint mode=input.ReadUInt32();int count=input.ReadInt32();byte[] data=input.ReadBytes(length);var glyphs=new G[count];
   for(int i=0;i<count;i++){object g=default(G);foreach(var field in fields){uint raw=input.ReadUInt32();Type t=field.FieldType;object v;if(t.IsEnum)v=Enum.ToObject(t,raw);else if(t==typeof(ushort))v=(ushort)raw;else if(t==typeof(short))v=unchecked((short)raw);else if(t==typeof(int))v=unchecked((int)raw);else if(t==typeof(byte))v=(byte)raw;else v=raw;field.SetValue(g,v);}glyphs[i]=(G)g;}
   var sub=(S)subCtor.Invoke(new object[]{new List<G>(),fonts[fontIndex],Activator.CreateInstance(sp[2].ParameterType)!,Activator.CreateInstance(sp[3].ParameterType)!,ReadOnlyMemory<char>.Empty,ReadOnlyMemory<char>.Empty,false});
   var buffer=(B)ctor.Invoke(new object[]{sub,fonts[fontIndex],direction,false,ShapingClusterLevel.MonotoneGraphemes,ShapingBufferFlags.None});
   // Empty source constructor performs real GDEF policy/ownership setup without
   // executing metric preparation; replacing its empty storage is transport only.
   array.SetValue(buffer,glyphs);size.SetValue(buffer,count);
   uint error=0;bool changed=false;
   try {
    void Invoke(){if(mode<2){for(int at=0;at<count;at++)changed|=apply(data,buffer,lookup,at);}if(mode==1||mode==2)resolve(buffer);if(mode>=3)zero(buffer,mode==4);}
    if(Environment.GetEnvironmentVariable("PROGPU_RUST_GPOS_BENCHMARK")=="1") {
     var original=(G[])glyphs.Clone();var times=new List<double>();var allocations=new List<long>();
     for(int round=0;round<45;round++) {
      original.CopyTo(glyphs,0);changed=false;
      long allocated=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();Invoke();
      double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
      if(round>=5){times.Add(elapsed);allocations.Add(allocated);}
     }
     times.Sort();allocations.Sort();Console.Error.WriteLine(FormattableString.Invariant($"execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));
    }else{if(mode<2)changed=apply(data,buffer,lookup,position);if(mode==1||mode==2)resolve(buffer);if(mode>=3)zero(buffer,mode==4);}
   }
   catch(IndexOutOfRangeException){error=4;}catch(ArgumentOutOfRangeException){error=6;}catch(InvalidOperationException){error=7;}
   output.Write(error);output.Write(changed?1u:0u);output.Write((int)mark.GetValue(buffer)!);
   for(int i=0;i<count;i++){try{output.Write(Convert.ToUInt32(classify(buffer,i)));}catch(ArgumentOutOfRangeException){output.Write(0x80000006u);}}
   foreach(G g in glyphs)foreach(var field in fields)output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(g)!)));
   ((IDisposable)buffer).Dispose();((IDisposable)sub).Dispose();
  }
 }
}
