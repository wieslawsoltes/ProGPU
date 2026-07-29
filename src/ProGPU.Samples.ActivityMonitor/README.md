# ProGPU Activity Monitor sample

A standalone ProGPU.WinUI desktop sample inspired by the information architecture and
interaction model of macOS Activity Monitor.

The sample separates UI concerns from operating-system telemetry through
`IActivityMonitorDataSource`. The first concrete provider targets macOS; future Windows
and Linux providers can implement the same point-in-time snapshot contract without
changing the view layer.

Run the sample from the repository root:

```bash
dotnet run --project src/ProGPU.Samples.ActivityMonitor/ProGPU.Samples.ActivityMonitor.csproj
```
