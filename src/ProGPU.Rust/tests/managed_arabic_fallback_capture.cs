// Actual ProGPU fallback observations; reflection transports test records only.
using System.Reflection;
using System.Runtime.InteropServices;
using ProGPU.Text;
using ProGPU.Text.Shaping;
static class Control
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    delegate B Create<B>(string text, TtfFont font, string script, ShapingClusterLevel level,
        ShapingBufferFlags flags, ReadOnlyMemory<char> pre, ReadOnlyMemory<char> post);
    static void Main()
    {
        var type = typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper", true)!;
        typeof(Control).GetMethod(nameof(Run), F)!.MakeGenericMethod(
            type.GetNestedType("GlyphSubstitutionBuffer", F)!, type.GetNestedType("GlyphRecord", F)!).Invoke(null, null);
    }
    static void Run<B,G>() where B : class where G : struct
    {
        var create = typeof(B).GetMethod("Create", F)!.CreateDelegate<Create<B>>();
        var apply = typeof(B).GetMethod("ApplyArabicFallback", F)!.CreateDelegate<Action<B,bool,TextShapingOptions>>();
        var records = typeof(B).GetField("_glyphs", F)!;
        var point = typeof(G).GetField("CodePoint", F)!;
        var glyph = typeof(G).GetField("GlyphIndex", F)!;
        var action = typeof(G).GetField("ArabicAction", F)!;
        using var input = new BinaryReader(new BufferedStream(Console.OpenStandardInput(), 65536));
        using var output = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(), 65536));
        var fonts = new TtfFont[input.ReadInt32()];
        for (int i=0;i<fonts.Length;i++) fonts[i]=new TtfFont(input.ReadBytes(input.ReadInt32()));
        var tags = new[] {"init","medi","fina","isol","rlig"};
        var options = new TextShapingOptions[32];
        for (int mask=0;mask<32;mask++) options[mask]=new TextShapingOptions {
            Features=tags.Select((tag,i)=>new OpenTypeFeatureSetting(tag,(mask>>i)&1)).ToArray() };
        while (true)
        {
            uint selected;
            try { selected=input.ReadUInt32(); } catch (EndOfStreamException) { break; }
            int mask=input.ReadInt32(), count=input.ReadInt32();
            B buffer=create(new string('A',count), fonts[selected], "arab", ShapingClusterLevel.MonotoneCharacters,
                ShapingBufferFlags.None,default,default);
            try {
                var list=(List<G>)records.GetValue(buffer)!;
                if(list.Count!=count)throw new InvalidDataException("actual source count differs");
                var span=CollectionsMarshal.AsSpan(list);
                for(int i=0;i<count;i++) {
                    object value=span[i];point.SetValue(value,input.ReadUInt32());
                    glyph.SetValue(value,checked((ushort)input.ReadUInt32()));
                    action.SetValue(value,checked((byte)input.ReadUInt32()));span[i]=(G)value;
                }
                apply(buffer,true,options[mask]);
                output.Write((uint)list.Count);
                foreach(var value in list)output.Write((uint)(ushort)glyph.GetValue(value)!);
            } finally { ((IDisposable)buffer).Dispose(); }
        }
    }
}
