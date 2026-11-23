// Actual TtfFont constructors/WithVariations/WithColorPalette and original queries.
#nullable enable
using System.Reflection;
using System.Numerics;
using ProGPU.Text;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate bool Coord(TtfFont f,int axis,out short value);
 delegate TtfFont Palette(TtfFont f,int index,ReadOnlySpan<FontPaletteOverride> values);
 record Op(uint Kind,int From,int Index,FontVariationSetting[] Settings,FontPaletteOverride[] Colors);
 record Query(uint Kind,ushort A,ushort B,uint Bits);
 static uint Error(Exception e){while(e is TargetInvocationException&&e.InnerException is {} inner)e=inner;return e switch{IndexOutOfRangeException or InvalidDataException or FormatException or OverflowException=>4u,ArgumentException=>1u,_=>throw e};}
 static void Main(){
  var type=typeof(TtfFont);var active=type.GetProperty("HasActiveFontVariations",F)!.GetGetMethod(true)!.CreateDelegate<Func<TtfFont,bool>>();var coord=type.GetMethod("TryGetNormalizedVariationCoordinate",F)!.CreateDelegate<Coord>();
  var layout=type.GetMethod("GetLayoutVariationDelta",F)!.CreateDelegate<Func<TtfFont,ushort,ushort,float>>();var simple=type.GetMethod("ApplySimpleGlyphVariations",F)!.CreateDelegate<Action<TtfFont,ushort,Vector2[],ushort[]>>();var composite=type.GetMethod("GetCompositeGlyphVariationOffsets",F)!.CreateDelegate<Func<TtfFont,int,ushort,Vector2[]>>();
  var palette=type.GetMethod("WithColorPalette",F)!.CreateDelegate<Palette>();var rootField=type.GetField("_variationRoot",F)!;var paletteField=type.GetField("_colorPalette",F)!;
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  string ReadText(){var c=new char[input.ReadInt32()];for(int i=0;i<c.Length;i++)c[i]=(char)input.ReadUInt32();return new(c);}
  void Text(string s){output.Write((uint)s.Length);foreach(char c in s)output.Write((uint)c);}void Float(float f)=>output.Write(BitConverter.SingleToUInt32Bits(f));void Bool(bool b)=>output.Write(b?1u:0u);
  void Snapshot(TtfFont f,List<TtfFont?> history,Query[] queries){
   foreach(var s in new[]{f.FamilyName,f.SubfamilyName,f.FullName,f.PostScriptName})Text(s);
   foreach(uint v in new uint[]{f.WeightClass,f.WidthClass,f.IsItalic?1u:0u,f.HasTrueTypeOutlines?1u:0u,f.HasCffOutlines?1u:0u,f.HasBitmapGlyphs?1u:0u,f.HasColorGlyphs?1u:0u,f.IsFixedPitch?1u:0u,f.UnitsPerEm,f.NumGlyphs})output.Write(v);
   foreach(short v in new[]{f.XMin,f.YMin,f.XMax,f.YMax,f.Ascender,f.Descender,f.LineGap})output.Write((int)v);
   foreach(short? v in new[]{f.UnderlinePosition,f.UnderlineThickness,f.StrikeoutPosition,f.StrikeoutThickness,f.XHeight,f.CapHeight}){Bool(v.HasValue);output.Write((int)(v??0));}
   var colors=(Vector4[]?)paletteField.GetValue(f)??Array.Empty<Vector4>();output.Write((uint)colors.Length);foreach(var c in colors){Float(c.X);Float(c.Y);Float(c.Z);Float(c.W);}Bool(active(f));
   var axes=f.VariationAxes;output.Write((uint)axes.Count);foreach(var a in axes){Text(a.Tag);Text(a.Name);Float(a.Minimum);Float(a.Default);Float(a.Maximum);Bool(a.IsHidden);}
   var settings=f.VariationSettings;output.Write((uint)settings.Count);foreach(var s in settings){Text(s.Tag);Float(s.Value);}
   for(int axis=-1;axis<=axes.Count;axis++){Bool(coord(f,axis,out short c));output.Write((int)c);}
   output.Write((uint)history.Count);foreach(var h in history){Bool(ReferenceEquals(f,h));Bool(f.Equals(h));if(f.Equals(h)&&f.GetHashCode()!=h!.GetHashCode())throw new Exception("Source equal hashes differ");}
   foreach(var q in queries){uint error=0;float result=0;Vector2[] points=q.Kind==4?new[]{new Vector2(0,3),new Vector2(17,32),new Vector2(33,-15),new Vector2(55,5)}:Array.Empty<Vector2>();
    try{switch(q.Kind){case 0:result=f.GetAdvanceWidth(q.A,BitConverter.UInt32BitsToSingle(q.Bits));break;case 1:result=f.GetAdvanceHeight(q.A,BitConverter.UInt32BitsToSingle(q.Bits));break;case 2:result=layout(f,q.A,q.B);break;case 3:points=composite(f,q.B,q.A);break;case 4:simple(f,q.A,points,new ushort[]{3});break;}}
    catch(Exception e){error=Error(e);}output.Write(error);if(q.Kind<3)Float(result);else{if(q.Kind==3)output.Write((uint)points.Length);foreach(var p in points){Float(p.X);Float(p.Y);}}
   }
  }
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}byte[] font=input.ReadBytes(length);int faceIndex=input.ReadInt32();var ops=new Op[input.ReadInt32()];for(int n=0;n<ops.Length;n++){uint kind=input.ReadUInt32();int from=input.ReadInt32(),index=input.ReadInt32();var settings=new FontVariationSetting[input.ReadInt32()];for(int j=0;j<settings.Length;j++)settings[j]=new(ReadText(),BitConverter.UInt32BitsToSingle(input.ReadUInt32()));var colors=new FontPaletteOverride[input.ReadInt32()];for(int j=0;j<colors.Length;j++)colors[j]=new((ushort)input.ReadUInt32(),input.ReadUInt32());ops[n]=new(kind,from,index,settings,colors);}var queries=new Query[input.ReadInt32()];for(int n=0;n<queries.Length;n++)queries[n]=new(input.ReadUInt32(),(ushort)input.ReadUInt32(),(ushort)input.ReadUInt32(),input.ReadUInt32());
   string? mode=Environment.GetEnvironmentVariable("PROGPU_RUST_FONT_BENCHMARK");
   if(mode is not null){
    TtfFont? baseFont=null,selected=null,result=null;var settings=ops[0].Settings;var rotating=new FontVariationSetting[45][];for(int n=0;n<45;n++){rotating[n]=(FontVariationSetting[])settings.Clone();rotating[n][0]=new("wght",110+n*10);}
    if(mode!="construction"){baseFont=new TtfFont(font,faceIndex);_ = baseFont.VariationAxes;if(mode=="hit"||mode=="palette")selected=baseFont.WithVariations(settings);}
    var overrides=new FontPaletteOverride[]{new(1,0x80402010)};var times=new List<double>();var allocations=new List<long>();double first=0;
    for(int n=0;n<45;n++){long allocated=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();result=mode switch{"construction"=>new TtfFont(font,faceIndex),"hit"=>baseFont!.WithVariations(settings),"rotating"=>baseFont!.WithVariations(rotating[n]),"palette"=>palette(selected!,1,overrides),_=>throw new ArgumentException()};double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;if(n==0)first=elapsed;if(n>=5){times.Add(elapsed);allocations.Add(allocated);}}
    times.Sort();allocations.Sort();Console.Error.WriteLine(FormattableString.Invariant($"first_us={first:F3} execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));Snapshot(result!,new(),Array.Empty<Query>());continue;
   }
   TtfFont? root=null;uint failure=0;try{root=new TtfFont(font,faceIndex);}catch(Exception e){failure=Error(e);}output.Write(failure);if(root is null)continue;var frames=new List<TtfFont?>{root,new TtfFont(font,faceIndex)};Snapshot(root,frames,queries);
   foreach(var op in ops){TtfFont? result=null;uint error=0;try{var f=frames[op.From]??throw new ArgumentException();result=op.Kind switch{0=>f.WithVariations(op.Settings),1=>palette(f,op.Index,op.Colors),2=>(TtfFont?)rootField.GetValue(f)??f,3=>f,_=>throw new ArgumentException()};}catch(Exception e){error=Error(e);}output.Write(error);if(result is not null)Snapshot(result,frames,queries);frames.Add(result);}
  }
 }
}
