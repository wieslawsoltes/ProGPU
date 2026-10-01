using ProGPU.WinUI.Hmi;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiAlarmConsoleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static void Pressure(HmiRuntime runtime, double value, int seconds = 0, HmiQuality quality = HmiQuality.Good) =>
        runtime.Publish(new Dictionary<string, HmiTagSample> { ["Line.Pressure"] = new(HmiValue.From(value), quality, Start.AddSeconds(seconds)) }, Start.AddSeconds(seconds));

    [Fact]
    public void JournalRecordsDistinctImmutableStateTransitionsAndActor()
    {
        var runtime = new HmiRuntime(HmiDemoProject.Create(), Start); runtime.Start();
        Pressure(runtime, 9, 1);
        runtime.Acknowledge("pressure-high", "operator-a", "Reviewed PT-201");
        Pressure(runtime, 1, 2);
        Pressure(runtime, 1, 3, HmiQuality.Bad);
        Pressure(runtime, 1, 4);
        Assert.Equal(new[] { HmiAlarmEventKind.Activated, HmiAlarmEventKind.Acknowledged, HmiAlarmEventKind.Returned, HmiAlarmEventKind.QualityLost, HmiAlarmEventKind.QualityRestored }, runtime.AlarmEvents.Select(e => e.Kind));
        Assert.Equal("operator-a", runtime.AlarmEvents[1].Actor);
        Assert.True(runtime.AlarmEvents[0].IsActive);
        Assert.False(runtime.Alarms.Single(a => a.Definition.Id == "pressure-high").IsActive);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, runtime.AlarmEvents.Select(e => e.Sequence));
    }

    [Fact]
    public void AlarmJournalEvictionIsBoundedAndExplicit()
    {
        var runtime = new HmiRuntime(HmiDemoProject.Create(), Start);
        for (int i = 0; i < 2100; i++) Pressure(runtime, i % 2 == 0 ? 9 : 1);
        Assert.Equal(2048, runtime.AlarmEvents.Count);
        Assert.Equal(52L, runtime.EvictedAlarmEvents);
        Assert.Equal(53L, runtime.AlarmEvents[0].Sequence);
    }

    [Fact]
    public void ConsoleFiltersAndAcknowledgesOnlyTheSelectedLocalAlarm()
    {
        var runtime = new HmiRuntime(HmiDemoProject.Create(), Start); runtime.Start();
        using var console = new HmiAlarmConsole(); console.AttachRuntime(runtime);
        Pressure(runtime, 9);
        Assert.Contains("pressure-high", console.VisibleAlarmIds);
        console.SelectAlarm("pressure-high");
        console.AcknowledgeSelected();
        Assert.False(runtime.Alarms.Single(a => a.Definition.Id == "pressure-high").IsAcknowledged);
        console.AllowAcknowledgement = true;
        console.Actor = "tester";
        console.AcknowledgeSelected();
        var alarm = runtime.Alarms.Single(a => a.Definition.Id == "pressure-high");
        Assert.True(alarm.IsAcknowledged); Assert.True(alarm.IsActive);
        console.Filter = HmiAlarmFilter.Unacknowledged;
        Assert.DoesNotContain("pressure-high", console.VisibleAlarmIds);
        console.Filter = HmiAlarmFilter.Active;
        console.SearchText = "PT-does-not-exist";
        Assert.Empty(console.VisibleAlarmIds);
        console.SearchText = "pressure";
        Assert.Contains("pressure-high", console.VisibleAlarmIds);
        console.AttachRuntime(null);
        Assert.Empty(console.VisibleAlarmIds);
        Pressure(runtime, 1);
        Assert.Null(console.Runtime);
    }

    [Fact]
    public void CsvQuotesMessagesAndNeutralizesOperatorFormulas()
    {
        var runtime = new HmiRuntime(HmiDemoProject.Create(), Start); runtime.Start(); Pressure(runtime, 9);
        runtime.Acknowledge("pressure-high", "=formula", "Reviewed, \"OK\"");
        string csv = runtime.ExportAlarmEventsCsv();
        Assert.Contains("\"'=formula\"", csv);
        Assert.Contains("\"Reviewed, \"\"OK\"\"\"", csv);
        Assert.Contains("Acknowledged", csv);
    }
}
