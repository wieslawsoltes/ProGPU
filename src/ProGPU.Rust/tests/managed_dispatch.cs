// Actual original feature policy, all stage filters and complete raw stage executor.
#nullable enable
using System.Reflection;
using ProGPU.Text;
using ProGPU.Text.Shaping;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate void Apply<B,P,S>(TtfFont font,P plan,B buffer,TextShapingOptions options,S stage);
 delegate bool Enabled<B>(B buffer,int index,string tag,TextShapingOptions options);
 delegate bool May<B>(B buffer,string tag,TextShapingOptions options);
 delegate bool Stage<S>(string tag,S stage);
 delegate bool Manual<S>(S stage);
 delegate int Value(TextShapingOptions options,string tag,int cluster);
 delegate bool Explicit(TextShapingOptions options,string tag,int cluster);
 static void Main(){var t=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(t.GetNestedType("GlyphSubstitutionBuffer",F)!,t.GetNestedType("GlyphRecord",F)!,t.GetNestedType("SubstitutionPlan",F)!,t.GetNestedType("UseSubstitutionStage",F)!).Invoke(null,new object[]{t});}
 static void Run<B,G,P,S>(Type shaper) where B:class where G:struct where S:Enum {
  var apply=shaper.GetMethod("ApplySubstitutions",F)!.CreateDelegate<Apply<B,P,S>>();var create=shaper.GetMethod("CreateSubstitutionPlan",F)!;
  var enabled=typeof(B).GetMethod("IsFeatureEnabled",F)!.CreateDelegate<Enabled<B>>();var may=typeof(B).GetMethod("MayEnableFeature",F)!.CreateDelegate<May<B>>();
  var stage=shaper.GetMethod("IsSubstitutionStageFeature",F)!.CreateDelegate<Stage<S>>();var syllablePolicy=shaper.GetMethod("IsUsePerSyllableFeature",F)!.CreateDelegate<Stage<S>>();var manualPolicy=shaper.GetMethod("IsManualJoinerStage",F)!.CreateDelegate<Manual<S>>();
  var value=typeof(TextShapingOptions).GetMethod("GetFeatureValue",F)!.CreateDelegate<Value>();var explicitAt=typeof(TextShapingOptions).GetMethod("IsFeatureExplicitAt",F)!.CreateDelegate<Explicit>();var resolve=typeof(TextShapingOptions).GetMethod("ResolveExplicitFeatureTags",F)!;
  var ctor=typeof(B).GetConstructors(F).Single(c=>!c.IsStatic);var parameters=ctor.GetParameters();
  var fields=new[]{"GlyphIndex","Cluster","GraphemeCluster","CharacterCluster","CodePoint","Flags","AdvanceX","AdvanceY","OffsetX","OffsetY","ArabicAction","SpaceFallback","FractionAction","ScriptFeatureMask","ScriptShaper","UseCategory","UseSyllable","IndicCategory","IndicPosition","IndicOriginalOrder","UseRphfEligible","Substituted","Ligated","MultipleSubstitutionComponent","Multiplied","LigatureComponentCount","LigatureComponent","HasCharacterCluster","Positioned","AttachmentKind","AttachmentTarget"}.Select(n=>typeof(G).GetField(n,F)!).ToArray();
  var state=new[]{"_contextMatchEnd","_randomState","_markFilteringCoverage","_nestedLookupDepth","_manualJoiners","_restrictLookupToSyllable","_lookupSyllable","_hasFractionActions"}.Select(n=>typeof(B).GetField(n,F)!).ToArray();
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  string Tag(){var c=new char[4];for(int i=0;i<4;i++)c[i]=(char)input.ReadUInt16();return new(c);}
  OpenTypeFeatureSetting[] Settings(){int n=input.ReadInt32();var s=new OpenTypeFeatureSetting[n];for(int i=0;i<n;i++)s[i]=new(Tag(),input.ReadInt32());return s;}
  G Glyph(){object g=default(G);foreach(var field in fields){uint raw=input.ReadUInt32();Type t=field.FieldType;object v;if(t.IsEnum)v=Enum.ToObject(t,raw);else if(t==typeof(ushort))v=(ushort)raw;else if(t==typeof(short))v=unchecked((short)raw);else if(t==typeof(int))v=unchecked((int)raw);else if(t==typeof(byte))v=(byte)raw;else v=raw;field.SetValue(g,v);}return (G)g;}
  B Buffer(List<G> glyphs,TtfFont font)=>(B)ctor.Invoke(new object[]{glyphs,font,Activator.CreateInstance(parameters[2].ParameterType)!,Activator.CreateInstance(parameters[3].ParameterType)!,ReadOnlyMemory<char>.Empty,ReadOnlyMemory<char>.Empty,false});
  var baseFont=new TtfFont(input.ReadBytes(input.ReadInt32()));var options=new TextShapingOptions[input.ReadInt32()];
  for(int i=0;i<options.Length;i++){
   uint mode=input.ReadUInt32();var features=Settings();OpenTypeFeatureSetting[]? baseline=input.ReadUInt32()==0?null:Settings();int explicitCount=input.ReadInt32();HashSet<string>? tags=explicitCount<0?null:new(StringComparer.Ordinal);for(int j=0;j<explicitCount;j++)tags!.Add(Tag());
   var ranges=new ShapingFeature[input.ReadInt32()];for(int j=0;j<ranges.Length;j++)ranges[j]=new(new OpenTypeTag(input.ReadUInt32()),input.ReadUInt32(),input.ReadUInt32(),input.ReadUInt32());
   var o=mode==1?new TextShapingOptions():mode==2?TextShapingOptions.WithFeatures(features):new TextShapingOptions {Features=features,ExplicitFeatureTags=tags};
   typeof(TextShapingOptions).GetProperty("BaseFeatures",F)!.SetValue(o,baseline);typeof(TextShapingOptions).GetProperty("RangedFeatures",F)!.SetValue(o,new ReadOnlyMemory<ShapingFeature>(ranges));options[i]=o;
  }
  var queryGlyphs=new List<G>{default};var query=Buffer(queryGlyphs,baseFont);
  while(true){uint mode;try{mode=input.ReadUInt32();}catch(EndOfStreamException){break;}
   if(mode==0){var o=options[input.ReadInt32()];string tag=Tag();bool fractions=input.ReadUInt32()!=0;G g=Glyph();queryGlyphs[0]=g;state[7].SetValue(query,fractions);int cluster=(int)fields[1].GetValue(g)!;
    output.Write(value(o,tag,cluster));uint bits=(explicitAt(o,tag,cluster)?1u:0u)|(enabled(query,0,tag,o)?2u:0u)|(may(query,tag,o)?4u:0u)|(((IReadOnlySet<string>)resolve.Invoke(o,null)!).Contains(tag)?8u:0u);output.Write(bits);continue;}
   if(mode==1){string tag=Tag();S s=(S)Enum.ToObject(typeof(S),input.ReadUInt32());output.Write((stage(tag,s)?1u:0u)|(manualPolicy(s)?2u:0u)|(syllablePolicy(tag,s)?4u:0u));continue;}
   var opts=options[input.ReadInt32()];S stageId=(S)Enum.ToObject(typeof(S),input.ReadUInt32());var font=new TtfFont(input.ReadBytes(input.ReadInt32()));string script=Tag();
   int end=input.ReadInt32();uint random=input.ReadUInt32();bool manual=input.ReadUInt32()!=0,restrict=input.ReadUInt32()!=0;byte active=(byte)input.ReadUInt32();bool fraction=input.ReadUInt32()!=0;
   var glyphs=new List<G>();int count=input.ReadInt32();for(int i=0;i<count;i++)glyphs.Add(Glyph());var buffer=Buffer(glyphs,font);
   state[0].SetValue(buffer,end);state[1].SetValue(buffer,random);state[4].SetValue(buffer,manual);state[5].SetValue(buffer,restrict);state[6].SetValue(buffer,active);state[7].SetValue(buffer,fraction);
   uint error=0;try{
    P plan=(P)create.Invoke(null,new object?[]{font,null,opts,script})!;
    if(mode==3){
     var original=new List<G>(glyphs);glyphs.EnsureCapacity(count*3+4);var times=new List<double>();var allocations=new List<long>();
     for(int round=0;round<45;round++){
      glyphs.Clear();glyphs.AddRange(original);state[0].SetValue(buffer,end);state[1].SetValue(buffer,random);state[2].SetValue(buffer,-1);state[3].SetValue(buffer,0);state[4].SetValue(buffer,manual);state[5].SetValue(buffer,restrict);state[6].SetValue(buffer,active);state[7].SetValue(buffer,fraction);
      long allocated=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();
      apply(font,plan,buffer,opts,stageId);
      double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
      if(round>=5){times.Add(elapsed);allocations.Add(allocated);}
     }
     times.Sort();allocations.Sort();Console.Error.WriteLine(FormattableString.Invariant($"execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));
    }else apply(font,plan,buffer,opts,stageId);
   }catch(Exception e){while(e is TargetInvocationException && e.InnerException is {} inner)e=inner;error=e switch {IndexOutOfRangeException=>4u,ArgumentOutOfRangeException=>6u,InvalidOperationException=>7u,_=>throw e};}
   output.Write(error);output.Write((uint)glyphs.Count);foreach(var field in state)output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(buffer)!)));
   foreach(G g in glyphs)foreach(var field in fields)output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(g)!)));
   ((IDisposable)buffer).Dispose();
  }
  ((IDisposable)query).Dispose();
 }
}
