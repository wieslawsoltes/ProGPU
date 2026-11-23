// Original typed API calls, immutable inputs and reused output storage.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProGPU.Text;
var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
var type = typeof(TtfFont).Assembly.GetType("ProGPU.Text.IndicShapingData", true)!;
var raw = type.GetMethod("GetProperties", flags)!.CreateDelegate<Func<uint, ushort>>();
Func<uint, ushort> publicIndic = UnicodeShapingProperties.GetIndicProperties;
Func<uint, ushort> use = point => UnicodeShapingProperties.GetUseCategory(point);
Console.Error.WriteLine(RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.OSDescription);
using var input = new BinaryReader(new BufferedStream(Console.OpenStandardInput()));
int count = input.ReadInt32();
for (int c = 0; c < count; c++)
{
    uint mode = input.ReadUInt32(), repetitions = input.ReadUInt32();
    var points = new uint[input.ReadInt32()];
    for (int i = 0; i < points.Length; i++) points[i] = input.ReadUInt32();
    var query = mode switch { 0 => raw, 1 => publicIndic, 2 => use, _ => throw new ArgumentException() };
    var output = new ushort[points.Length];
    var samples = new double[40];
    long allocated = 0;
    for (int round = 0; round < 45; round++)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (uint repeat = 0; repeat < repetitions; repeat++)
            for (int i = 0; i < points.Length; i++) output[i] = query(points[i]);
        long end = Stopwatch.GetTimestamp();
        if (round >= 5)
        {
            samples[round - 5] = (end - start) * (1e9 / Stopwatch.Frequency) / repetitions / points.Length;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        GC.KeepAlive(output);
    }
    Array.Sort(samples);
    Console.WriteLine(JsonSerializer.Serialize(new { p50_ns = samples[19], p95_ns = samples[37],
        p99_ns = samples[39], allocated_bytes = allocated, values = output }));
}
