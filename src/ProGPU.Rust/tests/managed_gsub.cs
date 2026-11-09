// Calls actual original C# nested GSUB and reverse execution on real buffers.
// Reflection is transport only; no implementation is reproduced here.
#nullable enable
using System.Reflection;
using ProGPU.Text;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate bool Apply<B>(ReadOnlySpan<byte> data,B buffer,ushort lookup,int position);
 delegate bool Reverse<B>(ReadOnlySpan<byte> data,B buffer,int offset,int position,ushort flags);
 static void Main(){var t=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(t.GetNestedType("GlyphSubstitutionBuffer",F)!,t.GetNestedType("GlyphRecord",F)!).Invoke(null,new object[]{t});}
 static void Run<B,G>(Type shaper) where B:class where G:struct {
  var apply=shaper.GetMethod("ApplyNestedLookup",F)!.CreateDelegate<Apply<B>>();
  var reverse=shaper.GetMethod("ApplyReverseChainingSubtable",F)!.CreateDelegate<Reverse<B>>();
  var ctor=typeof(B).GetConstructors(F).Single(c=>!c.IsStatic);var parameters=ctor.GetParameters();
  var glyphCtor=typeof(G).GetConstructor(F,null,new[]{typeof(ushort),typeof(int),typeof(uint)},null)!;
  var fields=new[]{"GlyphIndex","CodePoint","Cluster","Flags","AdvanceX","AdvanceY","OffsetX","OffsetY","Substituted","Ligated","Multiplied","MultipleSubstitutionComponent","LigatureComponentCount","LigatureComponent"}.Select(n=>typeof(G).GetField(n,F)!).ToArray();
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  var font=new TtfFont(input.ReadBytes(input.ReadInt32()));
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}
   ushort lookup=(ushort)input.ReadUInt32();int position=input.ReadInt32(),count=input.ReadInt32(),reverseOffset=input.ReadInt32();
   byte[] data=input.ReadBytes(length);var glyphs=new List<G>(count);
   for(int i=0;i<count;i++){ushort id=(ushort)input.ReadUInt32();uint cp=input.ReadUInt32();int cluster=input.ReadInt32();object g=glyphCtor.Invoke(new object[]{id,cluster,cp});
    for(int j=3;j<8;j++){int v=input.ReadInt32();fields[j].SetValue(g,fields[j].FieldType.IsEnum?Enum.ToObject(fields[j].FieldType,v):Convert.ChangeType(v,fields[j].FieldType));}glyphs.Add((G)g);}
   var buffer=(B)ctor.Invoke(new object[]{glyphs,font,Activator.CreateInstance(parameters[2].ParameterType)!,Activator.CreateInstance(parameters[3].ParameterType)!,ReadOnlyMemory<char>.Empty,ReadOnlyMemory<char>.Empty,false});
   bool changed=reverseOffset<0?apply(data,buffer,lookup,position):reverse(data,buffer,reverseOffset,position,0);
   output.Write(changed?1u:0u);output.Write((uint)glyphs.Count);
   foreach(G g in glyphs)foreach(var field in fields){object v=field.GetValue(g)!;output.Write(unchecked((uint)Convert.ToInt64(v)));}
   ((IDisposable)buffer).Dispose();
  }
 }
}
