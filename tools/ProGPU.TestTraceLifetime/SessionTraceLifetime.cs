using System.Diagnostics;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollector.InProcDataCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.InProcDataCollector;

namespace ProGPU.Diagnostics;

// Enabled only in generated diagnostic runsettings. These typed VSTest callbacks
// bracket the runner session; no measured test or product code calls this class.
public sealed class SessionTraceLifetime : InProcDataCollection
{
    public void Initialize(IDataCollectionSink sink) { }
    public void TestSessionStart(TestSessionStartArgs args) { }
    public void TestCaseStart(TestCaseStartArgs args) { }
    public void TestCaseEnd(TestCaseEndArgs args) { }

    public void TestSessionEnd(TestSessionEndArgs args)
    {
        string? directory = Environment.GetEnvironmentVariable("PROGPU_TEST_TRACE_LIFETIME");
        if (string.IsNullOrEmpty(directory))
            return;
        string request = Path.Combine(directory, "testhost-session-ended");
        string acknowledged = Path.Combine(directory, "collector-finalized");
        try
        {
            using (var stream = new FileStream(request, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.Write(Environment.ProcessId);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(acknowledged) && deadline.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(20);
            if (!File.Exists(acknowledged))
                Console.Error.WriteLine("[DrawingTrace] Collector did not acknowledge session end within 30 seconds.");
        }
        catch (Exception error)
        {
            // Diagnostic failure must not replace the actual test exit code.
            Console.Error.WriteLine($"[DrawingTrace] Lifetime handshake failed: {error}");
        }
    }
}
