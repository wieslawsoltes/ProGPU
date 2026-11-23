using ProGPU.Text;
using var input = new BinaryReader(Console.OpenStandardInput());
using var output = new BinaryWriter(Console.OpenStandardOutput());
while (true)
{
    uint count;
    try { count = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
    var units = new char[count];
    for (int i = 0; i < units.Length; i++) units[i] = (char)input.ReadUInt16();
    var boundaries = OriginalGraphemePolicy.Capture(new string(units));
    output.Write(boundaries is null ? uint.MaxValue : (uint)boundaries.Length);
    if (boundaries is not null) foreach (int value in boundaries) output.Write((uint)value);
}
