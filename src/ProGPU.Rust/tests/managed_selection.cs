// Full original managed enabled-lookup method, with actual TtfFont axis instances.
// Typed reflection delegates are transport in this test executable only.
#nullable enable
using System.Reflection;
using System.Text;
using ProGPU.Text;
static class Control {
 static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 delegate List<T> Select<T>(TtfFont font,ReadOnlySpan<byte> data,IReadOnlyList<OpenTypeFeatureSetting> settings,string script,string? language,ushort extension,out bool usesVariations);
 delegate bool Coordinate(TtfFont font,int axis,out short value);
 static string Tag(uint value)=>Encoding.ASCII.GetString(new[]{(byte)(value>>24),(byte)(value>>16),(byte)(value>>8),(byte)value});
 static void Main(){Type shaper=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),Flags)!.MakeGenericMethod(shaper.GetNestedType("EnabledLookup",Flags)!).Invoke(null,new object[]{shaper});}
 static void Run<T>(Type shaper){
  var select=shaper.GetMethod("GetRawEnabledLookupIndices",Flags)!.CreateDelegate<Select<T>>();var coordinate=typeof(TtfFont).GetMethod("TryGetNormalizedVariationCoordinate",Flags)!.CreateDelegate<Coordinate>();var index=typeof(T).GetProperty("LookupIndex",Flags)!;
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  while(true){uint fontLength;try{fontLength=input.ReadUInt32();}catch(EndOfStreamException){break;}
   uint length=input.ReadUInt32(),script=input.ReadUInt32(),language=input.ReadUInt32(),count=input.ReadUInt32(),axes=input.ReadUInt32();
   var font=new TtfFont(input.ReadBytes(checked((int)fontLength)));var data=input.ReadBytes(checked((int)length));var settings=new OpenTypeFeatureSetting[count];for(int i=0;i<settings.Length;i++)settings[i]=new(Tag(input.ReadUInt32()));
   var variations=new FontVariationSetting[axes];for(int i=0;i<variations.Length;i++){short c=unchecked((short)input.ReadUInt32());variations[i]=new(Tag(0x41583030u+(uint)i),c/16384f);}
   font=font.WithVariations(variations);for(int i=0;i<variations.Length;i++){if(!coordinate(font,i,out short c))throw new InvalidDataException("Original font has no normalized axis");output.Write((int)c);}
   var selected=select(font,data,settings,Tag(script),language==0?null:"tr",7,out _);output.Write((uint)selected.Count);foreach(T entry in selected)output.Write((uint)(ushort)index.GetValue(entry)!);
  }
 }
}
