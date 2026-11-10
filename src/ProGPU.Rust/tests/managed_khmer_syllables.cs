// Observe original syllable results; no transition tables or engine arrays read.
#nullable enable
using System.Reflection;
using ProGPU.Text;
using ProGPU.Text.Shaping;
static class Control
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    delegate B Create<B>(string text, TtfFont font, string script, ShapingClusterLevel level,
                         ShapingBufferFlags flags, ReadOnlyMemory<char> pre, ReadOnlyMemory<char> post);
    static void Main(string[] args)
    {
        string script = args.Length == 0 ? "khmer" : args[0];
        if (script == "myanmar-constants")
        {
            var data = typeof(TtfFont).Assembly.GetType("ProGPU.Text.IndicShapingData", true)!;
            foreach (string name in new[] { "Asat", "MedialRa", "VariationSelector", "VedicSign", "ConsonantWithStacker",
                "PositionPreMatra", "PositionPreConsonant", "PositionBaseConsonant", "PositionAfterMain",
                "PositionBelowConsonant", "PositionBeforeSub", "PositionAfterSub", "PositionEnd" })
                Console.WriteLine(name + "=" + data.GetField(name, F)!.GetRawConstantValue());
            return;
        }
        if (script is not ("khmer" or "myanmar")) throw new ArgumentException("unknown script");
        var shaper = typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper", true)!;
        typeof(Control).GetMethod(nameof(Run), F)!.MakeGenericMethod(
            shaper.GetNestedType("GlyphSubstitutionBuffer", F)!, shaper.GetNestedType("GlyphRecord", F)!)
            .Invoke(null, new object[] { script });
    }
    static void Run<B, G>(string script) where B : class where G : struct
    {
        var create = typeof(B).GetMethod("Create", F)!.CreateDelegate<Create<B>>();
        var find = typeof(B).GetMethod(script == "myanmar" ? "FindMyanmarSyllables" : "FindKhmerSyllables", F)!.CreateDelegate<Action<B>>();
        var category = typeof(G).GetField("IndicCategory", F)!;
        var syllable = typeof(G).GetField("UseSyllable", F)!;
        var templates = new G[256];
        for (int i = 0; i < 256; i++)
        {
            object value = default(G);
            category.SetValue(value, (byte)i);
            syllable.SetValue(value, (byte)0xa5);
            templates[i] = (G)value;
        }
        using var input = new BinaryReader(new BufferedStream(Console.OpenStandardInput(), 65536));
        using var output = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(), 65536));
        var font = new TtfFont(input.ReadBytes(input.ReadInt32()));
        B buffer = create("", font, script == "myanmar" ? "mymr" : "khmr", 0, 0, default, default);
        var records = (List<G>)typeof(B).GetField("_glyphs", F)!.GetValue(buffer)!;
        while (true)
        {
            uint count;
            try { count = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
            records.Clear();
            for (uint i = 0; i < count; i++) records.Add(templates[input.ReadByte()]);
            find(buffer);
            foreach (G record in records) output.Write((byte)syllable.GetValue(record)!);
        }
    }
}
