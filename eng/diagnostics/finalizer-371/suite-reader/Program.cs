using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

if (args.Length != 2 || File.Exists(args[1])) return 2;
using var conversion = File.CreateText(args[1] + ".conversion.log");
string etlx = TraceLog.CreateFromEventPipeDataFile(args[0], args[1] + ".etlx",
    new TraceLogOptions { ContinueOnError = false, ConversionLog = conversion });
using var log = new TraceLog(etlx);
var rows = new List<object>();
var pending = new Dictionary<(int Pid, int Tid), (double TimeMs, bool Finalizer)>();
long contentionStarts = 0, contentionStops = 0;
foreach (var e in log.Events)
{
    if (!e.EventName.StartsWith("Contention/")) continue;
    var stack = new List<string>();
    for (var frame = e.CallStack(); frame != null && stack.Count < 128; frame = frame.Caller)
        stack.Add(frame.CodeAddress.FullMethodName ?? frame.CodeAddress.ToString());
    bool finalizer = stack.Any(s => s.Contains(".Finalize(") || s.Contains("GC.RunFinalizers"));
    bool start = e.EventName.Contains("Start"), stop = e.EventName.Contains("Stop");
    double? pairedDurationMs = null;
    var key = (e.ProcessID, e.ThreadID);
    if (start) { contentionStarts++; pending[key] = (e.TimeStampRelativeMSec, finalizer); }
    if (stop)
    {
        contentionStops++;
        if (pending.Remove(key, out var preceding))
        {
            pairedDurationMs = e.TimeStampRelativeMSec - preceding.TimeMs;
            finalizer |= preceding.Finalizer;
        }
    }
    // Retain all finalizer starts/stops and all original long contention stops;
    // the complete immutable trace retains the other events for later analysis.
    if (finalizer || pairedDurationMs >= 500)
        rows.Add(new { name = e.EventName, utc = e.TimeStamp.ToUniversalTime(), timeMs = e.TimeStampRelativeMSec,
            pid = e.ProcessID, tid = e.ThreadID, finalizer, pairedDurationMs,
            payload = e.PayloadNames.ToDictionary(n => n, n => e.PayloadByName(n)), stack });
}
using var input = File.OpenRead(args[0]);
var report = new { diagnosticOnly = true, qualification = false,
    traceSha256 = Convert.ToHexString(SHA256.HashData(input)), log.EventsLost,
    contentionStarts, contentionStops, unpairedStarts = pending.Count, rows,
    limitation = "Managed Monitor contention only; a suite pass does not attribute historical failures. Original gate results remain independent." };
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Contention starts={contentionStarts}; stops={contentionStops}; retained={rows.Count}; lost={log.EventsLost}.");
return log.EventsLost == 0 ? 0 : 1;
