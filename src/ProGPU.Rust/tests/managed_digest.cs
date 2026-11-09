// Invoke actual compiled original private types. Reflection remains test-only;
// no source extraction, translated algorithm or production runtime bridge.
using System.Reflection;
using ProGPU.Text;
static class Control {
 static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static;
 static Type Shaper=>typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",true)!;
 static void Main()=>typeof(Control).GetMethod(nameof(Run),Private)!.MakeGenericMethod(Shaper.GetNestedType("GlyphSetDigest",Private)!,Shaper.GetNestedType("RawContextRequirements",Private)!,Shaper.GetNestedType("RawCoverageMatcher",Private)!).Invoke(null,null);
 delegate bool Parse<T>(ReadOnlySpan<byte> data,ushort index,ushort extension,out T value);
 delegate bool Have<T>(ref T value,ushort glyph);
 delegate void Add<T>(ref T value,ushort glyph);
 delegate void Range<T>(ref T value,ushort first,ushort last);
 delegate bool Intersect<T>(ref T value,in T other);
 static void Run<D,C,M>() where D:struct where C:class where M:struct {
  var digest=Shaper.GetMethod("TryCreateRawLookupDigest",Private)!.CreateDelegate<Parse<D>>();
  var context=Shaper.GetMethod("TryCreateRawContextRequirements",Private)!.CreateDelegate<Parse<C>>();
  var have=typeof(D).GetMethod("MayHave",Private)!.CreateDelegate<Have<D>>();
  var add=typeof(D).GetMethod("Add",Private)!.CreateDelegate<Add<D>>();
  var range=typeof(D).GetMethod("AddRange",Private,new[]{typeof(ushort),typeof(ushort)})!.CreateDelegate<Range<D>>();
  var intersect=typeof(D).GetMethod("MayIntersect",Private)!.CreateDelegate<Intersect<D>>();
  var contains=typeof(M).GetMethod("Contains",Private)!.CreateDelegate<Have<M>>();
  var maskFields=new[]{"_shift4","_shift0","_shift6"}.Select(n=>typeof(D).GetField(n,Private)!).ToArray();
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  void Masks(D d){foreach(var f in maskFields)output.Write((ulong)f.GetValue(d)!);}
  while(true){uint length;try{length=input.ReadUInt32();}catch(EndOfStreamException){break;}
   ushort index=(ushort)input.ReadUInt32(),extension=(ushort)input.ReadUInt32();input.ReadUInt32();input.ReadUInt32();uint count=input.ReadUInt32();
   byte[] data=input.ReadBytes(checked((int)length));ushort[] queries=new ushort[count];for(int i=0;i<queries.Length;i++)queries[i]=(ushort)input.ReadUInt32();
   bool has=digest(data,index,extension,out D d);output.Write(has?1u:0u);if(has){Masks(d);foreach(ushort q in queries){output.Write(have(ref d,q)?1u:0u);D other=default;range(ref other,q,(ushort)Math.Min(65535,(int)q+3));add(ref other,unchecked((ushort)(q+4096)));output.Write(intersect(ref d,in other)?1u:0u);}}
   has=context(data,index,extension,out C c);output.Write(has?1u:0u);if(!has)continue;
   var subtables=(Array)typeof(C).GetField("_subtables",Private)!.GetValue(c)!;
   var coverages=new List<M>();var descriptors=new List<uint[]>();
   foreach(object sub in subtables){Type t=sub.GetType();var items=(M[])t.GetProperty("Coverages",Private)!.GetValue(sub)!;
    descriptors.Add(new[]{(uint)coverages.Count,(uint)items.Length,(uint)(int)t.GetProperty("BacktrackCount",Private)!.GetValue(sub)!,(uint)(int)t.GetProperty("InputCount",Private)!.GetValue(sub)!});coverages.AddRange(items);
   }
   output.Write((uint)descriptors.Count);output.Write((uint)coverages.Count);output.Write((uint)(ushort)typeof(C).GetField("_lookupFlags",Private)!.GetValue(c)!);
   foreach(var s in descriptors)foreach(uint n in s)output.Write(n);
   var digestProperty=typeof(M).GetProperty("Digest",Private)!;
   foreach(M item in coverages){M matcher=item;Masks((D)digestProperty.GetValue(matcher)!);foreach(ushort q in queries)output.Write(contains(ref matcher,q)?1u:0u);}
  }
 }
}
