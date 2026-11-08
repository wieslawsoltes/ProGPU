// Transport only; compile original managed resolver and generated data unchanged.
using ProGPU.Text;
using var input = new BinaryReader(Console.OpenStandardInput());
using var output = new BinaryWriter(Console.OpenStandardOutput());
while (true)
{
    uint mode;
    try { mode = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
    if (mode == 0) { output.Write((uint)UnicodeLineBreakResolver.GetClass(input.ReadUInt32())); continue; }
    if (mode != 1) throw new InvalidDataException();
    int count = checked((int)input.ReadUInt32());
    byte[] original = input.ReadBytes(checked(count * 16));
    if (original.Length != count * 16) throw new EndOfStreamException();
    var source = new UnicodeLineBreakScalar[count];
    for (int i=0; i<count; i++) source[i] = new(BitConverter.ToUInt32(original,i*16),BitConverter.ToUInt32(original,i*16+4),BitConverter.ToUInt16(original,i*16+8));
    var classes = new UnicodeLineBreakClass[input.ReadUInt32()];
    var breaks = new UnicodeLineBreakKind[input.ReadUInt32()];
    if (input.ReadUInt32() != 0) throw new InvalidDataException();
    Array.Fill(classes,UnicodeLineBreakClass.Surrogate); Array.Fill(breaks,UnicodeLineBreakKind.Opportunity);
    bool ok = UnicodeLineBreakResolver.TryResolve(source,classes,breaks,out var error);
    output.Write(ok?1u:0u); output.Write((uint)error);
    foreach(var c in classes)output.Write((byte)c);
    foreach(var b in breaks)output.Write((byte)b);
    output.Write(original);
}
