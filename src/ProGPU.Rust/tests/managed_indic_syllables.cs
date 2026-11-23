// Observe original category contracts and syllable outcomes, never machine arrays.
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
        if (args.Length == 1 && args[0] == "--constants")
        {
            var data = typeof(TtfFont).Assembly.GetType("ProGPU.Text.IndicShapingData", true)!;
            foreach (var field in data.GetFields(F).Where(f => f.IsLiteral && f.FieldType == typeof(byte)))
                Console.WriteLine(field.Name + "=" + field.GetRawConstantValue());
            return;
        }
        var shaper = typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper", true)!;
        typeof(Control).GetMethod(nameof(Run), F)!.MakeGenericMethod(
            shaper.GetNestedType("GlyphSubstitutionBuffer", F)!, shaper.GetNestedType("GlyphRecord", F)!)
            .Invoke(null, null);
    }
    static void Run<B, G>() where B : class where G : struct
    {
        var create = typeof(B).GetMethod("Create", F)!.CreateDelegate<Create<B>>();
        var find = typeof(B).GetMethod("FindIndicSyllables", F)!.CreateDelegate<Action<B>>();
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
        B buffer = create("", font, "deva", 0, 0, default, default);
        var records = (List<G>)typeof(B).GetField("_glyphs", F)!.GetValue(buffer)!;
        while (true)
        {
            int count;
            try { count = input.ReadInt32(); } catch (EndOfStreamException) { break; }
            records.Clear();
            for (int i = 0; i < count; i++) records.Add(templates[input.ReadByte()]);
            find(buffer);
            foreach (G record in records) output.Write((byte)syllable.GetValue(record)!);
        }
    }
}
