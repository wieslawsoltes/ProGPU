using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class Program
{
    private static readonly object Gate = new();
    private static long s_enterTicks, s_exitTicks;
    private static int s_finalizerThread;

    private sealed class BlockingOwner
    {
        ~BlockingOwner()
        {
            Volatile.Write(ref s_finalizerThread, Environment.CurrentManagedThreadId);
            Interlocked.Exchange(ref s_enterTicks, DateTime.UtcNow.Ticks);
            lock (Gate)
                Interlocked.Exchange(ref s_exitTicks, DateTime.UtcNow.Ticks);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BlockingOwner> Abandon() => new(new BlockingOwner());

    private static int Main(string[] args)
    {
        if (args.Length != 1 || File.Exists(args[0])) return 2;
        Exception? failure = null;
        var collector = new Thread(() =>
        {
            try { GC.Collect(); GC.WaitForPendingFinalizers(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "Bounded finalizer control" };
        bool completedWhileLocked;
        DateTime started, unlock;
        WeakReference<BlockingOwner> weak;
        var timer = Stopwatch.StartNew();
        lock (Gate)
        {
            weak = Abandon();
            started = DateTime.UtcNow;
            collector.Start();
            completedWhileLocked = collector.Join(TimeSpan.FromSeconds(5));
            unlock = DateTime.UtcNow;
        }
        bool completedAfterUnlock = collector.Join(TimeSpan.FromSeconds(5));
        bool alive = weak.TryGetTarget(out _);
        var report = new
        {
            diagnosticOnly = true, qualification = false,
            control = "One deliberate Monitor-blocked finalizer; no ProGPU code or GPU",
            processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
            originalJoinSeconds = 5, startedUtc = started, unlockUtc = unlock,
            finalizerEnterUtc = new DateTime(Interlocked.Read(ref s_enterTicks), DateTimeKind.Utc),
            finalizerExitUtc = new DateTime(Interlocked.Read(ref s_exitTicks), DateTimeKind.Utc),
            finalizerManagedThreadId = Volatile.Read(ref s_finalizerThread),
            completedWhileLocked, completedAfterUnlock, alive, failure = failure?.ToString(),
            elapsedSeconds = timer.Elapsed.TotalSeconds
        };
        File.WriteAllText(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return !completedWhileLocked && completedAfterUnlock && !alive && failure == null ? 0 : 1;
    }
}
