// Binary transport executing the actual original Uax9Resolver unchanged.
using ProGPU.Text.Bidi;
using System.IO;
internal static class Program
{
    private static void Main()
    {
        using var input=new BinaryReader(Console.OpenStandardInput());
        using var output=new BinaryWriter(Console.OpenStandardOutput());
        while (true)
        {
            uint count; try { count=input.ReadUInt32(); } catch (EndOfStreamException) { break; }
            var text=new char[count]; for (int i=0;i<text.Length;i++) text[i]=(char)input.ReadUInt16();
            var result=Uax9Resolver.Resolve(text,(sbyte)input.ReadInt32());
            output.Write((uint)result.ParagraphLevel);output.Write((uint)result.Levels.Length);
            foreach (var level in result.Levels) {output.Write((uint)level.Utf16Start);output.Write((uint)level.Utf16Length);output.Write((uint)level.Level);}
        }
    }
}
