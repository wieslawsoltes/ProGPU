using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

if (args.Length != 3 || File.Exists(args[2])) return 2;
using var control = JsonDocument.Parse(File.ReadAllText(args[1]));
DateTime unlock = control.RootElement.GetProperty("unlockUtc").GetDateTime();
using var conversion = File.CreateText(args[2] + ".conversion.log");
string etlx = TraceLog.CreateFromEventPipeDataFile(args[0], args[2] + ".etlx",
    new TraceLogOptions { ContinueOnError = false, ConversionLog = conversion });
using var log = new TraceLog(etlx);
var rows = new List<object>();
int startsBeforeUnlock = 0, stops = 0;
var blockedOwnerThreads = new HashSet<int>();
foreach (var e in log.Events)
{
    if (!e.EventName.StartsWith("Contention/")) continue;
    var stack = new List<string>();
    for (var frame = e.CallStack(); frame != null && stack.Count < 128; frame = frame.Caller)
        stack.Add(frame.CodeAddress.FullMethodName ?? frame.CodeAddress.ToString());
    DateTime utc = e.TimeStamp.ToUniversalTime();
    bool owner = stack.Any(s => s.Contains("BlockingOwner.Finalize"));
    // The trace's clock-to-UTC calibration and managed UTC reads can differ
    // slightly. Do not incorrectly require the event to follow a managed UTC
    // entry stamp. Require a whole second of margin before the actual unlock.
    bool beforeUnlock = utc < unlock.AddSeconds(-1);
    if (owner && e.EventName.Contains("Start") && beforeUnlock)
    {
        startsBeforeUnlock++;
        blockedOwnerThreads.Add(e.ThreadID);
    }
    // ContentionStop has no managed stack; pair its native thread with the
    // preceding known-owner start in this single-object deterministic control.
    if (e.EventName.Contains("Stop") && blockedOwnerThreads.Remove(e.ThreadID)) stops++;
    rows.Add(new { name = e.EventName, utc, timeMs = e.TimeStampRelativeMSec,
        pid = e.ProcessID, tid = e.ThreadID, owner, beforeUnlock,
        payload = e.PayloadNames.ToDictionary(n => n, n => e.PayloadByName(n)), stack });
}
var report = new { diagnosticOnly = true, qualification = false,
    traceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))),
    log.EventsLost, startsBeforeUnlock, stops,
    timingScope = "Start must precede actual unlock by more than one second; no exact cross-clock UTC entry ordering is assumed.", rows };
File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Blocked-owner contention starts before unlock={startsBeforeUnlock}; stops={stops}; lost={log.EventsLost}.");
return startsBeforeUnlock > 0 && stops > 0 && log.EventsLost == 0 ? 0 : 1;
