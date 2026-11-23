// Actual source instance capture and complete raw GPOS plan execution.
#nullable enable
using System.Reflection;
using ProGPU.Text;
using ProGPU.Text.Shaping;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate bool Apply<B,P>(TtfFont font,P plan,B buffer,TextShapingOptions options);
 delegate bool Coordinate(TtfFont font,int axis,out short coordinate);
 delegate float Delta(TtfFont font,ushort outer,ushort inner);
 delegate void Resolve<B>(B buffer);
 delegate K Classify<B,K>(B buffer,int index);
 static uint Error(Exception e){while(e is TargetInvocationException&&e.InnerException is {} inner)e=inner;return e switch{IndexOutOfRangeException=>4u,ArgumentOutOfRangeException=>6u,InvalidDataException=>4u,OverflowException=>4u,InvalidOperationException=>7u,_=>throw e};}
 static void Main(){var t=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;var b=t.GetNestedType("GlyphPositionBuffer",F)!;typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(b,t.GetNestedType("GlyphSubstitutionBuffer",F)!,t.GetNestedType("GlyphRecord",F)!,b.GetMethod("GetGlyphClass",F)!.ReturnType,t.GetNestedType("PositioningPlan",F)!).Invoke(null,new object[]{t});}
 static TextShapingOptions Options(uint mode){
  var features=new[]{new OpenTypeFeatureSetting("kern",mode==2?0:mode==4?-7:1),new OpenTypeFeatureSetting("dist",mode==4?-4:1),new OpenTypeFeatureSetting("mark",mode==4?-3:1)};
  if(mode==0)return new();if(mode==1)return TextShapingOptions.WithFeatures(features);
  var o=new TextShapingOptions{Features=features};
  if(mode==3||mode==5){typeof(TextShapingOptions).GetProperty("RangedFeatures",F)!.SetValue(o,new ReadOnlyMemory<ShapingFeature>(new[]{new ShapingFeature(new OpenTypeTag(0x6b65726e),mode==3?0u:1u,mode==3?0u:2u,mode==3?3u:5u),new ShapingFeature(new OpenTypeTag(0x6d61726b),0u,0u,3u)}));}
  if(mode==5)typeof(TextShapingOptions).GetProperty("BaseFeatures",F)!.SetValue(o,Array.Empty<OpenTypeFeatureSetting>());
  return o;
 }
 static void Run<B,S,G,K,P>(Type shaper) where B:class where S:class where G:struct where K:Enum {
  var apply=shaper.GetMethod("ApplyPositions",F)!.CreateDelegate<Apply<B,P>>();var create=shaper.GetMethod("CreatePositioningPlan",F)!;
  var coordinate=typeof(TtfFont).GetMethod("TryGetNormalizedVariationCoordinate",F)!.CreateDelegate<Coordinate>();var delta=typeof(TtfFont).GetMethod("GetLayoutVariationDelta",F)!.CreateDelegate<Delta>();
  var active=typeof(TtfFont).GetProperty("HasActiveFontVariations",F)!.GetGetMethod(true)!.CreateDelegate<Func<TtfFont,bool>>();
  var resolve=typeof(B).GetMethod("ResolveAttachmentOffsets",F)!.CreateDelegate<Resolve<B>>();var classify=typeof(B).GetMethod("GetGlyphClass",F)!.CreateDelegate<Classify<B,K>>();
  var ctor=typeof(B).GetConstructors(F).Single(c=>!c.IsStatic);var subCtor=typeof(S).GetConstructors(F).Single(c=>!c.IsStatic);var sp=subCtor.GetParameters();
  var fields=new[]{"GlyphIndex","Cluster","GraphemeCluster","CharacterCluster","CodePoint","Flags","AdvanceX","AdvanceY","OffsetX","OffsetY","ArabicAction","SpaceFallback","FractionAction","ScriptFeatureMask","ScriptShaper","UseCategory","UseSyllable","IndicCategory","IndicPosition","IndicOriginalOrder","UseRphfEligible","Substituted","Ligated","MultipleSubstitutionComponent","Multiplied","LigatureComponentCount","LigatureComponent","HasCharacterCluster","Positioned","AttachmentKind","AttachmentTarget"}.Select(n=>typeof(G).GetField(n,F)!).ToArray();
  var array=typeof(B).GetField("_glyphs",F)!;var size=typeof(B).GetField("_count",F)!;var mark=typeof(B).GetField("_markFilteringCoverage",F)!;
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());var roots=new TtfFont[input.ReadInt32()];for(int i=0;i<roots.Length;i++)roots[i]=new TtfFont(input.ReadBytes(input.ReadInt32()));
  while(true){int fontIndex;try{fontIndex=input.ReadInt32();}catch(EndOfStreamException){break;}
   int requested=input.ReadInt32();var direction=(ShapingDirection)input.ReadUInt32();uint mode=input.ReadUInt32(),policy=input.ReadUInt32();int count=input.ReadInt32();var queries=new (ushort Outer,ushort Inner)[input.ReadInt32()];for(int i=0;i<queries.Length;i++)queries[i]=((ushort)input.ReadUInt32(),(ushort)input.ReadUInt32());
   var glyphs=new G[count];for(int i=0;i<count;i++){object g=default(G);foreach(var field in fields){uint raw=input.ReadUInt32();Type t=field.FieldType;object v;if(t.IsEnum)v=Enum.ToObject(t,raw);else if(t==typeof(ushort))v=(ushort)raw;else if(t==typeof(short))v=unchecked((short)raw);else if(t==typeof(int))v=unchecked((int)raw);else if(t==typeof(byte))v=(byte)raw;else v=raw;field.SetValue(g,v);}glyphs[i]=(G)g;}
   // Real source selection supplies the normalized input boundary to the Rust
   // plan. Instance creation itself is not replaced by a fabricated font object.
   var font=roots[fontIndex].WithVariations(new FontVariationSetting("wght",requested/16384f),new FontVariationSetting("wdth",requested/-32768f),new FontVariationSetting("opsz",requested/49152f));
   var coords=new List<short>();for(int i=0;coordinate(font,i,out short v);i++)coords.Add(v);output.Write((uint)coords.Count);foreach(short c in coords)output.Write((int)c);output.Write(active(font)?1u:0u);
   foreach(var q in queries){uint error=0;float value=0;try{value=delta(font,q.Outer,q.Inner);}catch(Exception e){error=Error(e);}output.Write(error);output.Write(BitConverter.SingleToUInt32Bits(value));}
   var sub=(S)subCtor.Invoke(new object[]{new List<G>(),font,Activator.CreateInstance(sp[2].ParameterType)!,Activator.CreateInstance(sp[3].ParameterType)!,ReadOnlyMemory<char>.Empty,ReadOnlyMemory<char>.Empty,false});
   var buffer=(B)ctor.Invoke(new object[]{sub,font,direction,false,ShapingClusterLevel.MonotoneGraphemes,ShapingBufferFlags.None});array.SetValue(buffer,glyphs);size.SetValue(buffer,count);
   uint failure=0;bool kerning=false;try {
    var options=Options(policy);P plan=(P)create.Invoke(null,new object?[]{font,null,options,"DFLT"})!;
    void Invoke(){kerning=apply(font,plan,buffer,options);if(mode==1)resolve(buffer);}
    if(Environment.GetEnvironmentVariable("PROGPU_RUST_POSITIONING_BENCHMARK")=="1"){
     var original=(G[])glyphs.Clone();var times=new List<double>();var allocations=new List<long>();
     for(int round=0;round<45;round++){
      original.CopyTo(glyphs,0);kerning=false;long allocated=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();Invoke();
      double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
      if(round>=5){times.Add(elapsed);allocations.Add(allocated);}
     }
     times.Sort();allocations.Sort();Console.Error.WriteLine(FormattableString.Invariant($"execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));
    }else Invoke();
   }catch(Exception e){failure=Error(e);}
   output.Write(failure);output.Write(kerning?1u:0u);output.Write((int)mark.GetValue(buffer)!);
   for(int i=0;i<count;i++){try{output.Write(Convert.ToUInt32(classify(buffer,i)));}catch(ArgumentOutOfRangeException){output.Write(0x80000006u);}}
   foreach(G g in glyphs)foreach(var field in fields)output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(g)!)));
   ((IDisposable)buffer).Dispose();((IDisposable)sub).Dispose();
  }
 }
}
