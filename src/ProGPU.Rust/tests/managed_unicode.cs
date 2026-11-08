// Transport only. All queried properties execute the original managed sources.
using ProGPU.Text;
using ProGPU.Text.Bidi;
using System.IO;
internal static class Program
{
    private static void Main()
    {
        using var input = new BinaryReader(Console.OpenStandardInput());
        using var output = new BinaryWriter(Console.OpenStandardOutput());
        while (true)
        {
            uint point;
            try { point = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
            output.Write(OpenTypeScriptResolver.GetScript(point).Value);
            output.Write((uint)UnicodeShapingProperties.GetCanonicalCombiningClass(point));
            output.Write((uint)UnicodeBidiData.GetClass(unchecked((int)point)));
            output.Write((uint)UnicodeShapingProperties.GetGeneralCategory(point));
            output.Write(UnicodeShapingProperties.GetMirroredCodePoint(point));
            output.Write(UnicodeShapingProperties.GetVerticalCodePoint(point));
            bool present = UnicodeBidiData.TryGetBracket(unchecked((int)point), out int pair, out BracketKind kind);
            output.Write(present ? 1U : 0U); output.Write((uint)pair); output.Write((uint)kind);
        }
    }
}
