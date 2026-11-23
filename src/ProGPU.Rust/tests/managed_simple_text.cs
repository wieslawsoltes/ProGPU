// Transport adapter linking the actual original SfntSimpleGlyphShaper.cs.
using System;
using System.IO;
using ProGPU.Text;

internal static class Program
{
    private static ushort[] Units(BinaryReader input)
    {
        var result = new ushort[input.ReadUInt32()];
        for (var i = 0; i < result.Length; i++) result[i] = input.ReadUInt16();
        return result;
    }
    private static char[] Text(BinaryReader input) => Array.ConvertAll(Units(input), v => (char)v);
    // Authored cmap fixture's explicit expected mapping. The independent C++
    // corpus qualifies actual cmap selection; this adapter exercises the original
    // managed callback boundary without introducing another font reader.
    private static ushort Map(uint point) => point == 0x41 ? (ushort)3 : point == 0x1F600 ? (ushort)7 : (ushort)0;
    private static void Main()
    {
        using var input = new BinaryReader(Console.OpenStandardInput());
        using var output = new BinaryWriter(Console.OpenStandardOutput());
        while (true)
        {
            uint mode;
            try { mode = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
            if (mode == 0)
            {
                var text = Text(input);
                for (var i = 0; i < text.Length; i++)
                {
                    var point = SfntSimpleGlyphShaper.ReadCodePoint(text, i, out var units);
                    output.Write(point); output.Write((uint)units);
                    output.Write(SfntSimpleGlyphShaper.IsFormattingControl(point) ? 1U : 0U);
                }
            }
            else if (mode == 1)
            {
                var text = Text(input); var blank = input.ReadUInt16(); var hyphen = input.ReadUInt16();
                try
                {
                    var run = SfntSimpleGlyphShaper.CreateGlyphRun(text, Map, blank, hyphen);
                    output.Write(0U); output.Write((uint)run.ClusterMap.Length); output.Write((uint)run.GlyphIndices.Length);
                    foreach (var value in run.ClusterMap) output.Write((uint)value);
                    foreach (var value in run.GlyphIndices) output.Write((uint)value);
                }
                catch (OverflowException) { output.Write(1U); }
            }
            else if (mode == 2)
            {
                var text = Text(input); var clusters = Units(input); var glyphs = Units(input);
                var metrics = new SfntSimpleGlyphMetrics[input.ReadUInt32()];
                for (var i = 0; i < metrics.Length; i++) metrics[i] = new(input.ReadUInt32(), input.ReadUInt32());
                var units = input.ReadUInt16(); var em = input.ReadDouble(); var scale = input.ReadDouble(); var sideways = input.ReadBoolean();
                var advances = new int[glyphs.Length + 2]; Array.Fill(advances, -1234567);
                SfntSimpleGlyphShaper.FillGlyphAdvances(text, clusters, glyphs, id => metrics[id], units, em, scale, sideways, advances);
                foreach (var value in advances) output.Write(value);
            }
            else throw new InvalidDataException("Unknown source-control protocol");
        }
    }
}
