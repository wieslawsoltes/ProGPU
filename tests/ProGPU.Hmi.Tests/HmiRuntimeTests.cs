using ProGPU.Hmi;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiRuntimeTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static HmiRuntime Runtime() => new(HmiDemoProject.Create(), Start, historyCapacity: 3);
    [Fact]
    public void RuntimeDefaultsToReadOnlyAndRejectsDesignModeCommands()
    {
        var runtime = Runtime();
        Assert.Throws<InvalidOperationException>(() => runtime.Write("Pump.Setpoint", HmiValue.From(40d)));
        runtime.Start();
        Assert.Throws<InvalidOperationException>(() => runtime.Write("Pump.Setpoint", HmiValue.From(40d)));
        runtime.Start(allowLocalWrites: true);
        runtime.Write("Pump.Setpoint", HmiValue.From(40d));
        Assert.Equal(40d, runtime.Read("Pump.Setpoint").Value.Number);
        runtime.Stop();
        Assert.Throws<InvalidOperationException>(() => runtime.Write("Pump.Setpoint", HmiValue.From(20d)));
    }
    [Fact]
    public void InvalidWritesLeaveValuesUntouched()
    {
        var runtime = Runtime(); runtime.Start(true);
        Assert.Throws<InvalidDataException>(() => runtime.Write("Pump.Setpoint", HmiValue.From(200d)));
        Assert.Throws<InvalidDataException>(() => runtime.Write("Pump.Setpoint", HmiValue.From(true)));
        Assert.Throws<InvalidOperationException>(() => runtime.Write("Tank.Level", HmiValue.From(50d)));
        Assert.Equal(65d, runtime.Read("Pump.Setpoint").Value.Number);
    }
    [Fact]
    public void BatchValidationIsAtomic()
    {
        var runtime = Runtime();
        var batch = new Dictionary<string, HmiTagSample>
        {
            ["Tank.Level"] = new(HmiValue.From(99d), HmiQuality.Good, Start.AddSeconds(1)),
            ["missing"] = new(HmiValue.From(1d), HmiQuality.Good, Start.AddSeconds(1))
        };
        Assert.Throws<InvalidDataException>(() => runtime.Publish(batch, Start.AddSeconds(1)));
        Assert.Equal(62d, runtime.Read("Tank.Level").Value.Number);
        Assert.Equal(Start, runtime.Now);
    }
    [Fact]
    public void TimestampReversalAndFutureSamplesAreRejected()
    {
        var runtime = Runtime();
        runtime.Publish(new Dictionary<string, HmiTagSample> { ["Tank.Level"] = new(HmiValue.From(70d), HmiQuality.Good, Start.AddSeconds(1)) }, Start.AddSeconds(1));
        Assert.Throws<InvalidDataException>(() => runtime.Publish(new Dictionary<string, HmiTagSample> { ["Tank.Level"] = new(HmiValue.From(50d), HmiQuality.Good, Start) }, Start.AddSeconds(2)));
        Assert.Throws<InvalidDataException>(() => runtime.Publish(new Dictionary<string, HmiTagSample> { ["Tank.Level"] = new(HmiValue.From(50d), HmiQuality.Good, Start.AddSeconds(5)) }, Start.AddSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.AdvanceTime(Start));
        Assert.Equal(70d, runtime.Read("Tank.Level").Value.Number);
    }
    [Fact]
    public void StaleQualityChangesNotifyBindingsAndBlockWrites()
    {
        var runtime = Runtime(); runtime.Start(true);
        IReadOnlyList<string>? changed = null; runtime.TagsChanged += tags => changed = tags;
        runtime.AdvanceTime(Start.AddSeconds(6));
        Assert.Equal(HmiQuality.Stale, runtime.Read("Pump.Setpoint").Quality);
        Assert.Contains("Pump.Setpoint", changed!);
        Assert.Throws<InvalidOperationException>(() => runtime.Write("Pump.Setpoint", HmiValue.From(40d)));
    }
    [Fact]
    public void SimulationIsDeterministicAndHistoryIsBounded()
    {
        var first = Runtime(); var second = Runtime(); first.Start(true); second.Start(true);
        for (int i = 0; i < 20; i++) { first.AdvanceSimulation(TimeSpan.FromMilliseconds(100)); second.AdvanceSimulation(TimeSpan.FromMilliseconds(100)); }
        Assert.Equal(first.Read("Tank.Level"), second.Read("Tank.Level"));
        Assert.Equal(3, first.GetHistory("Tank.Level").Count);
        first.Write("Pump.Running", HmiValue.From(false));
        first.AdvanceSimulation(TimeSpan.FromMilliseconds(100));
        Assert.False(first.Read("Pump.Running").Value.Boolean);
    }
    [Fact]
    public void RecipePublishesOneAtomicBatchAndAuditsApplication()
    {
        var runtime = Runtime(); runtime.Start(true);
        int notifications = 0; runtime.TagsChanged += _ => notifications++;
        runtime.ApplyRecipe("Low demand");
        Assert.Equal(30d, runtime.Read("Pump.Setpoint").Value.Number);
        Assert.Equal(1, notifications);
        Assert.Contains(runtime.Audit, entry => entry.Operation == "Recipe");
    }
    [Fact]
    public void RecipeRejectsAllWritesWhenOneDestinationHasBadQuality()
    {
        var runtime = Runtime(); runtime.Start(true);
        runtime.Publish(new Dictionary<string, HmiTagSample> { ["Valve.Open"] = new(HmiValue.From(true), HmiQuality.Bad, Start) }, Start);
        Assert.Throws<InvalidOperationException>(() => runtime.ApplyRecipe("Low demand"));
        Assert.Equal(65d, runtime.Read("Pump.Setpoint").Value.Number);
    }
    [Fact]
    public void AlarmDelayDeadbandReturnAndAcknowledgementRemainDistinct()
    {
        var runtime = Runtime(); runtime.Start(true);
        var alarm = runtime.Alarms.Single(a => a.Definition.Id == "level-high");
        void Level(double value, double seconds) => runtime.Publish(new Dictionary<string, HmiTagSample> { ["Tank.Level"] = new(HmiValue.From(value), HmiQuality.Good, Start.AddSeconds(seconds)) }, Start.AddSeconds(seconds));
        Level(90, 1); Assert.False(alarm.IsActive);
        Level(90, 1.4); Assert.False(alarm.IsActive);
        Level(90, 1.5); Assert.True(alarm.IsActive); Assert.False(alarm.IsAcknowledged);
        runtime.Acknowledge("level-high"); Assert.True(alarm.IsActive); Assert.True(alarm.IsAcknowledged);
        Level(82, 2); Assert.True(alarm.IsActive);
        Level(79, 3); Assert.False(alarm.IsActive); Assert.NotNull(alarm.ReturnedAt);
        Level(90, 4); Level(90, 4.5); Assert.True(alarm.IsActive); Assert.False(alarm.IsAcknowledged);
        Level(70, 5); Assert.False(alarm.IsActive); Assert.True(alarm.NeedsAttention);
        runtime.Acknowledge("level-high"); Assert.False(alarm.NeedsAttention);
    }
    [Fact]
    public void BadQualityCannotSilentlyClearAnAlarm()
    {
        var runtime = Runtime();
        runtime.Publish(new Dictionary<string, HmiTagSample> { ["Line.Pressure"] = new(HmiValue.From(9d), HmiQuality.Good, Start) }, Start);
        var alarm = runtime.Alarms.Single(a => a.Definition.Id == "pressure-high");
        Assert.True(alarm.IsActive);
        runtime.Publish(new Dictionary<string, HmiTagSample> { ["Line.Pressure"] = new(HmiValue.From(0d), HmiQuality.Bad, Start.AddSeconds(1)) }, Start.AddSeconds(1));
        Assert.True(alarm.IsActive); Assert.True(alarm.IsQualityUnknown);
    }
    [Fact]
    public void NavigationIsValidatedAndDoesNotRequireWritePermission()
    {
        var runtime = Runtime(); runtime.Start(); string? destination = null; runtime.NavigationRequested += id => destination = id;
        runtime.Execute(new HmiAction { Kind = HmiActionKind.Navigate, Target = "operations" });
        Assert.Equal("operations", destination);
        Assert.Throws<InvalidDataException>(() => runtime.Execute(new HmiAction { Kind = HmiActionKind.Navigate, Target = "missing" }));
    }
    [Fact]
    public void RuntimeOwnsItsConfigurationSnapshot()
    {
        var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project, Start);
        project.Tags[0].Name = "changed"; project.Recipes.Clear();
        runtime.Start(true); runtime.ApplyRecipe("Low demand");
        Assert.Equal(62d, runtime.Read("Tank.Level").Value.Number);
    }
}
