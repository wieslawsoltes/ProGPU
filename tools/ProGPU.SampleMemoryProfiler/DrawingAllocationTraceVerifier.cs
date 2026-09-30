using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

internal static class DrawingAllocationTraceVerifier
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
            return 2;
        DrawingTraceMethodRequirement[] requiredMethods;
        try
        {
            requiredMethods = DrawingTraceMethodRequirement.Parse(args.AsSpan(3));
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        string input = Path.GetFullPath(args[1]);
        string report = Path.GetFullPath(args[2]);
        string index = report + ".etlx";
        if (File.Exists(report) || File.Exists(index) || input == report || input == index)
        {
            Console.Error.WriteLine("Trace verification requires fresh report/index paths distinct from the input.");
            return 2;
        }
        try
        {
            // Never ContinueOnError: successful collection requires a complete,
            // parseable stream, including its terminal event-cache flush.
            TraceLog.CreateFromEventPipeDataFile(input, index, new TraceLogOptions
            {
                KeepAllEvents = true,
                OnLostEvents = (truncated, lost, _) =>
                {
                    if (truncated || lost != 0)
                        throw new InvalidDataException($"Trace loss: truncated={truncated}, events={lost}.");
                }
            });
            using var trace = new TraceLog(index);
            _ = trace.Clr;
            long events = 0, allocations = 0;
            var processes = new HashSet<int>();
            foreach (TraceEvent item in trace.Events)
            {
                events++;
                processes.Add(item.ProcessID);
                if (item.ProviderName != "Microsoft-Windows-DotNETRuntime")
                    continue;
                if ((int)item.ID == 303)
                    allocations++;
                if (item.PayloadNames.Contains("MethodName") && item.PayloadNames.Contains("MethodNamespace"))
                {
                    string? methodName = item.PayloadByName("MethodName")?.ToString();
                    string? methodNamespace = item.PayloadByName("MethodNamespace")?.ToString();
                    foreach (DrawingTraceMethodRequirement method in requiredMethods)
                        method.Observe(methodNamespace, methodName);
                }
            }
            bool metricMethod = requiredMethods[0].Found;
            string[] missingMethods = requiredMethods.Where(method => !method.Found)
                .Select(method => $"{method.MethodNamespace}.{method.MethodName}").ToArray();
            if (events == 0 || allocations == 0 || missingMethods.Length != 0 || processes.Count != 1 || trace.EventsLost != 0)
                throw new InvalidDataException($"Incomplete quality trace: events={events}, allocationSamples={allocations}, metricMethod={metricMethod}, missingMethods=[{string.Join(", ", missingMethods)}], processes={processes.Count}, lost={trace.EventsLost}.");
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 2, complete = true, reader = typeof(TraceLog).Assembly.FullName,
                events, allocationSamples = allocations, metricMethod, processId = processes.Single(),
                requiredMethods,
                startUtc = trace.SessionStartTime.ToUniversalTime(), endUtc = trace.SessionEndTime.ToUniversalTime(),
                lostEvents = trace.EventsLost
            }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { schemaVersion = 2, complete = false, requiredMethods, error = error.ToString() }));
            Console.Error.WriteLine(error);
            return 2;
        }
        finally
        {
            if (File.Exists(index))
                File.Delete(index);
        }
    }
}
