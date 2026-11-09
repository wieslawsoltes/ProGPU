// Execute the original repository policy; no copied runtime implementation.
using ProGPU.Text;
using System.Runtime.InteropServices;
using var input = new BinaryReader(new BufferedStream(Console.OpenStandardInput(), 65536));
using var output = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(), 65536));
Console.Error.WriteLine(RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.OSDescription);
bool benchmark = Environment.GetEnvironmentVariable("PROGPU_RUST_GRAPHEME_BENCHMARK") == "1";
while (true)
{
    uint length;
    try { length = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
    var units = new char[length];
    for (int i = 0; i < units.Length; i++) units[i] = (char)input.ReadUInt16();
    string source = new(units);
    if (benchmark)
    {
        var boundaries = new uint[units.Length + 1];
        var times = new List<double>(); var allocations = new List<long>();
        int count = 0; double first = 0;
        for (int round = 0; round < 45; round++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            count = 1; boundaries[0] = 0;
            for (int index = 0; index < source.Length;)
            {
                index += OriginalGraphemePolicy.GetLength(source.AsSpan(index));
                boundaries[count++] = (uint)index;
            }
            double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (round == 0) first = elapsed;
            if (round >= 5) { times.Add(elapsed); allocations.Add(allocated); }
        }
        times.Sort(); allocations.Sort();
        Console.Error.WriteLine(FormattableString.Invariant($"first_us={first:F3} p50_us={times[19]:F3} p95_us={times[37]:F3} p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));
        output.Write((uint)count); for (int i = 0; i < count; i++) output.Write(boundaries[i]);
        continue;
    }
    output.Write((uint)OriginalGraphemePolicy.GetLength(source));
    int[]? capture = OriginalGraphemePolicy.Capture(source);
    output.Write(capture is null ? uint.MaxValue : (uint)capture.Length);
    if (capture is not null) foreach (int boundary in capture) output.Write((uint)boundary);
    for (int start = 0; start <= source.Length; start++)
        output.Write((uint)OriginalGraphemePolicy.GetLength(source.AsSpan(start)));
}
