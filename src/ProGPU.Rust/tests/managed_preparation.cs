// Actual original preparation and raw script selection. Reflection is transport only.
#nullable enable
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Text;
using ProGPU.Text.Shaping;
static class Control
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    delegate B Create<B>(string text, TtfFont font, string script, ShapingClusterLevel level,
                         ShapingBufferFlags flags, ReadOnlyMemory<char> pre, ReadOnlyMemory<char> post);
    delegate bool Select(TtfFont font, string script, out string layout);
    static void Main()
    {
        var shaper = typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper", true)!;
        typeof(Control).GetMethod(nameof(Run), F)!.MakeGenericMethod(
            shaper.GetNestedType("GlyphSubstitutionBuffer", F)!, shaper.GetNestedType("GlyphRecord", F)!)
            .Invoke(null, new object[] { shaper });
    }
    static void Run<B, G>(Type shaper) where B : class where G : struct
    {
        var create = typeof(B).GetMethod("Create", F)!.CreateDelegate<Create<B>>();
        var select = shaper.GetMethod("ResolveLayoutScript", F)!.CreateDelegate<Select>();
        var records = typeof(B).GetField("_glyphs", F)!;
        var fields = new[] { "GlyphIndex", "Cluster", "GraphemeCluster", "CharacterCluster", "CodePoint", "Flags",
            "AdvanceX", "AdvanceY", "OffsetX", "OffsetY", "ArabicAction", "SpaceFallback", "FractionAction",
            "ScriptFeatureMask", "ScriptShaper", "UseCategory", "UseSyllable", "IndicCategory", "IndicPosition",
            "IndicOriginalOrder", "UseRphfEligible", "Substituted", "Ligated", "MultipleSubstitutionComponent",
            "Multiplied", "LigatureComponentCount", "LigatureComponent", "HasCharacterCluster", "Positioned",
            "AttachmentKind", "AttachmentTarget" }.Select(name => typeof(G).GetField(name, F)!).ToArray();
        var state = new[] { "_contextMatchEnd", "_randomState", "_markFilteringCoverage", "_nestedLookupDepth",
            "_manualJoiners", "_restrictLookupToSyllable", "_lookupSyllable", "_hasFractionActions" }
            .Select(name => typeof(B).GetField(name, F)!).ToArray();
        using var input = new BinaryReader(new BufferedStream(Console.OpenStandardInput(), 65536));
        using var output = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(), 65536));
        Console.Error.WriteLine(RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.OSDescription);
        void Units(ReadOnlySpan<char> units) { output.Write((uint)units.Length); foreach (char unit in units) output.Write((uint)unit); }
        // These observations select a candidate repertoire; complete case comparison follows.
        foreach (string text in new[] { "\U000105c9", "\U000105d2\u0307", "a\u0897\u0334",
                     "a\u1acf\u0334", "a\u0301\u1acf", "\ufffe" })
            foreach (var form in new[] { NormalizationForm.FormD, NormalizationForm.FormC })
                try { Units(text.Normalize(form)); } catch (ArgumentException) { output.Write(uint.MaxValue); }
        var fonts = new TtfFont?[input.ReadInt32()];
        for (int i = 0; i < fonts.Length; i++)
        {
            byte[] data = input.ReadBytes(input.ReadInt32());
            try { fonts[i] = new TtfFont(data); } catch (Exception) { fonts[i] = null; }
        }
        while (true)
        {
            uint selected;
            try { selected = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
            var request = new uint[input.ReadInt32()];
            for (int i = 0; i < request.Length; i++) request[i] = input.ReadUInt32();
            int cursor = 0;
            char[] Text() { var value = new char[request[cursor++]]; for (int i = 0; i < value.Length; i++) value[i] = (char)request[cursor++]; return value; }
            string script = new(Text()); var level = (ShapingClusterLevel)request[cursor++];
            var flags = (ShapingBufferFlags)request[cursor++]; string text = new(Text()); char[] pre = Text(), post = Text();
            if (cursor != request.Length) throw new InvalidDataException("request tail");
            if (fonts[selected] is not { } font) { output.Write(1u); continue; }
            output.Write(0u); output.Write(select(font, script, out string layout) ? 1u : 0u); Units(layout);
            if (Environment.GetEnvironmentVariable("PROGPU_RUST_PREPARATION_BENCHMARK") == "1")
            {
                var times = new List<double>(); var retire = new List<double>(); var allocations = new List<long>(); double first = 0;
                for (int round = 0; round < 45; round++)
                {
                    long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    B candidate = create(text, font, script, level, flags, pre, post);
                    double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; GC.KeepAlive(candidate);
                    start = System.Diagnostics.Stopwatch.GetTimestamp(); ((IDisposable)candidate).Dispose();
                    double disposal = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
                    if (round == 0) first = elapsed;
                    if (round >= 5) { times.Add(elapsed); retire.Add(disposal); allocations.Add(allocated); }
                }
                times.Sort(); retire.Sort(); allocations.Sort();
                Console.Error.WriteLine(FormattableString.Invariant($"first_us={first:F3} p50_us={times[19]:F3} p95_us={times[37]:F3} p99_us={times[39]:F3} retire_p50_us={retire[19]:F3} allocated_p50_bytes={allocations[19]}"));
            }
            B? buffer = null;
            uint error = 0;
            try { buffer = create(text, font, script, level, flags, pre, post); }
            catch (Exception e) { error = e switch { ArgumentException => 2u, InvalidOperationException => 3u,
                IndexOutOfRangeException => 4u, _ => throw new InvalidOperationException("Unexpected original preparation failure", e) }; }
            output.Write(error);
            if (buffer is null) continue;
            try
            {
                var glyphs = (List<G>)records.GetValue(buffer)!;
                output.Write((uint)glyphs.Count);
                output.Write(ReferenceEquals(typeof(B).GetField("_font", F)!.GetValue(buffer), font) ? 1u : 0u);
                output.Write((uint)Convert.ToByte(typeof(B).GetField("_clusterLevel", F)!.GetValue(buffer)));
                output.Write((uint)Convert.ToByte(typeof(B).GetField("_bufferFlags", F)!.GetValue(buffer)));
                Units(((ReadOnlyMemory<char>)typeof(B).GetField("_preContext", F)!.GetValue(buffer)!).Span);
                Units(((ReadOnlyMemory<char>)typeof(B).GetField("_postContext", F)!.GetValue(buffer)!).Span);
                foreach (var field in state) output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(buffer)!)));
                foreach (G glyph in glyphs) foreach (var field in fields)
                    output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(glyph)!)));
            }
            finally { ((IDisposable)buffer).Dispose(); }
        }
    }
}
