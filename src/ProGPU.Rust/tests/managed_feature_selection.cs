// Actual compiled original managed selection, including winning metadata and
// digest masks. Reflection is test transport only, never production shaping.
#nullable enable
using System.Reflection;
using ProGPU.Text;
static class Control {
 static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 delegate List<T> Select<T>(TtfFont font,ReadOnlySpan<byte> data,IReadOnlyList<OpenTypeFeatureSetting> settings,string script,string? language,ushort extension,out bool usesVariations);
 delegate bool Coordinate(TtfFont font,int axis,out short value);
 static void Main(){Type shaper=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),Flags)!.MakeGenericMethod(shaper.GetNestedType("EnabledLookup",Flags)!).Invoke(null,new object[]{shaper});}
 static void Run<T>(Type shaper){
  var select=shaper.GetMethod("GetRawEnabledLookupIndices",Flags)!.CreateDelegate<Select<T>>();
  var coordinate=typeof(TtfFont).GetMethod("TryGetNormalizedVariationCoordinate",Flags)!.CreateDelegate<Coordinate>();
  PropertyInfo Property(string n)=>typeof(T).GetProperty(n,Flags)!;
  var index=Property("LookupIndex");var value=Property("Value");var tag=Property("Tag");var required=Property("Required");var has=Property("HasDigest");var digest=Property("Digest");
  var masks=new[]{"_shift4","_shift0","_shift6"}.Select(n=>digest.PropertyType.GetField(n,Flags)!).ToArray();
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  string Text(int n){char[] text=new char[n];for(int i=0;i<n;i++)text[i]=(char)input.ReadUInt16();return new string(text);}
  var fonts=new TtfFont[3];for(int i=0;i<fonts.Length;i++)fonts[i]=new TtfFont(input.ReadBytes(input.ReadInt32()));
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}
   int scriptLength=input.ReadInt32(),languageLength=input.ReadInt32(),count=input.ReadInt32(),axes=input.ReadInt32();ushort extension=(ushort)input.ReadUInt32();
   byte[] data=input.ReadBytes(length);string script=Text(scriptLength);string? language=languageLength<0?null:Text(languageLength);
   var settings=new OpenTypeFeatureSetting[count];for(int i=0;i<count;i++)settings[i]=new(Text(4),input.ReadInt32());
   var variations=new FontVariationSetting[axes];for(int i=0;i<axes;i++){short c=input.ReadInt16();variations[i]=new("AX0"+i,c/16384f);}
   var font=axes==0?fonts[0]:fonts[axes].WithVariations(variations);
   for(int i=0;i<axes;i++){if(!coordinate(font,i,out short c))throw new InvalidDataException("No original normalized axis");output.Write((int)c);}
   List<T> selected;bool uses;
   try { selected=select(font,data,settings,script,language,extension,out uses); }
   catch(IndexOutOfRangeException){output.Write(1u);continue;}
   catch(ArgumentOutOfRangeException){output.Write(1u);continue;}
   output.Write(0u);output.Write(uses?1u:0u);output.Write((uint)selected.Count);
   foreach(T entry in selected){output.Write((uint)(ushort)index.GetValue(entry)!);output.Write((int)value.GetValue(entry)!);foreach(char c in (string)tag.GetValue(entry)!)output.Write((ushort)c);output.Write((bool)required.GetValue(entry)!?1u:0u);bool h=(bool)has.GetValue(entry)!;output.Write(h?1u:0u);if(h){object d=digest.GetValue(entry)!;foreach(var field in masks)output.Write((ulong)field.GetValue(d)!);}}
  }
 }
}
