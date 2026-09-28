using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// This is the original standalone reproducer authored for dotnet/runtime#134724.
// No renderer, test framework, profiler, listener or GC/JIT switch is involved.
if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
    throw new PlatformNotSupportedException("The 64-byte positive control requires a qualified 64-bit runtime.");

var watch = Stopwatch.StartNew();
long[] normal = new long[32];
long[] positive = new long[32];
long[] background = new long[192];
for (int i = 0; i < normal.Length; i++) normal[i] = MeasureEmpty();
for (int i = 0; i < positive.Length; i++) positive[i] = MeasurePositive();
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
using var ready = new ManualResetEventSlim();
Exception? workerError = null;
int requests = 0;
var worker = new Thread(() =>
{
    try
    {
        // Bounded live storage: sixteen 1-MiB arrays plus one replacement,
        // an array of roots and its retained small objects (less than 27 MiB).
        byte[][] ring = new byte[16][];
        for (int i = 0; i < ring.Length; i++) ring[i] = new byte[1024 * 1024];
        object[] roots = new object[262_144];
        for (int i = 0; i < roots.Length; i++) roots[i] = new Node(i);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        ready.Set();
        for (int i = 0; i < 256 && !stop.IsCancellationRequested; i++)
        {
            ring[i % ring.Length] = new byte[1024 * 1024];
            Interlocked.Increment(ref requests);
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            Thread.Sleep(4);
        }
        GC.KeepAlive(roots);
        GC.KeepAlive(ring);
    }
    catch (Exception exception) { workerError = exception; ready.Set(); }
}) { IsBackground = true, Name = "Bounded counter diagnostic worker" };
worker.Start();
try
{
    if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Worker startup exceeded five seconds.");
    if (workerError is not null) throw new InvalidOperationException("Worker failed.", workerError);
    for (int i = 0; i < background.Length; i++)
    {
        if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Diagnostic exceeded twenty seconds.");
        // Allocate the context anchor outside the no-inline measurement.
        object anchor = AllocatePositive();
        background[i] = MeasureEmpty();
        GC.KeepAlive(anchor);
    }
}
finally
{
    stop.Cancel();
    if (!worker.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Worker shutdown exceeded five seconds.");
}
Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription}; arch={RuntimeInformation.ProcessArchitecture}; serverGC={System.Runtime.GCSettings.IsServerGC}; requests={requests}; gen2={GC.CollectionCount(2)}; elapsed={watch.Elapsed.TotalSeconds:F3}s");
Console.WriteLine($"normal-empty: {string.Join(',', normal)}");
Console.WriteLine($"positive-heap-array: {string.Join(',', positive)}");
Console.WriteLine($"background-empty: {string.Join(',', background)}");
Console.WriteLine($"normalNonzero={normal.Count(value => value != 0)}; positive64={positive.Count(value => value == 64)}; backgroundNonzero={background.Count(value => value != 0)}");
if (workerError is not null) throw new InvalidOperationException("Worker failed.", workerError);
return normal.Any(value => value != 0) || positive.Any(value => value != 64) ? 2
    : background.Any(value => value != 0) ? 1 : 0;

[MethodImpl(MethodImplOptions.NoInlining)]
static long MeasureEmpty()
{
    long before = GC.GetAllocatedBytesForCurrentThread();
    Thread.SpinWait(100_000);
    long after = GC.GetAllocatedBytesForCurrentThread();
    return after - before;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static long MeasurePositive()
{
    long before = GC.GetAllocatedBytesForCurrentThread();
    object value = AllocatePositive();
    long after = GC.GetAllocatedBytesForCurrentThread();
    GC.KeepAlive(value);
    return after - before;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static object AllocatePositive() => new byte[37];

sealed class Node(long value) { public readonly long Value = value; }
