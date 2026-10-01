using System.Text.Json;
using Microsoft.UI.Xaml;
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using ProGPU.WinUI.Hmi.Workplace;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiWorkplaceTests
{
    private static HmiObjectAddress Pump => new("pumps", "pump-run");
    private static HmiObjectAddress Demand => new("pumps", "pump-demand");
    private static HmiWorkplaceSession Session() => new(HmiDcsProject.Create());
    private static HmiLocalCommandReview Review(HmiWorkplaceSession s)
    {
        s.Navigate(Pump.ScreenId, Pump.ElementId); s.StartSimulation();
        return s.ReviewValue(Pump, HmiValue.From(false));
    }

    [Fact]
    public void DcsSampleRoundTripsWithReflectionDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var p = HmiDcsProject.Create(); var json = HmiProjectSerializer.Serialize(p);
        Assert.Equal(json, HmiProjectSerializer.Serialize(HmiProjectSerializer.Deserialize(json)));
        Assert.Equal(5, p.Screens.Count);
        Assert.All(p.Screens.SelectMany(s => s.Elements), e => Assert.Equal(HmiGraphicStyle.HighPerformance, e.Appearance.GraphicStyle));
        Assert.Empty(p.Connections);
    }
    [Fact]
    public void StartupNeverEnablesCommandsOrCreatesEngineering()
    {
        using var studio = new HmiDcsStudio(automaticTicks: false);
        Assert.Null(studio.Engineering); Assert.False(studio.IsEngineering);
        Assert.Equal(HmiWorkplaceMode.Offline, studio.Operator.Session.Mode);
        Assert.False(studio.Operator.Session.Runtime.IsRunning);
        Assert.False(studio.Operator.Session.Runtime.AllowLocalWrites);
        Assert.Throws<InvalidOperationException>(() => studio.Operator.Session.ReviewValue(Pump, HmiValue.From(false)));
    }
    [Fact]
    public void OperatorLibraryHasNoDesignerOrProtocolDependency()
    {
        var names = typeof(HmiOperatorWorkplace).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("ProGPU.WinUI.Designer", names);
        Assert.DoesNotContain("ProGPU.WinUI.Hmi.Designer", names);
        Assert.DoesNotContain("ProGPU.Hmi.OpcUa", names);
        Assert.DoesNotContain("ProGPU.Hmi.Modbus", names);
        Assert.DoesNotContain("ProGPU.Hmi.Mqtt", names);
    }
    [Fact]
    public void HistoryIsBoundedAndBranchNavigationDropsForwardEntries()
    {
        using var s = Session();
        for (int i = 0; i < 100; i++) s.Navigate(i % 2 == 0 ? "pumps" : "treatment");
        int count = 0; while (s.CanGoBack) { s.Back(); count++; }
        Assert.Equal(HmiWorkplaceSession.MaximumNavigationEntries - 1, count);
        s.Forward(); Assert.True(s.CanGoForward);
        s.Navigate("utilities"); Assert.False(s.CanGoForward);
    }
    [Theory]
    [InlineData("missing", null)]
    [InlineData("pumps", "missing")]
    public void InvalidNavigationDoesNotPublishState(string screen, string? element)
    {
        using var s = Session(); var old = s.Location;
        Assert.Throws<ArgumentException>(() => s.Navigate(screen, element));
        Assert.Equal(old, s.Location); Assert.False(s.CanGoBack);
    }
    [Fact]
    public void SameLocationDoesNotDuplicateHistory()
    {
        using var s = Session(); s.Navigate(s.Location.ScreenId);
        Assert.False(s.CanGoBack);
    }
    [Fact]
    public void PinningIsBoundedAndUsesStableAddresses()
    {
        using var s = Session(); var objects = s.FindObjects("").Take(9).ToArray();
        foreach (var address in objects.Take(8)) s.TogglePin(address);
        Assert.Equal(8, s.PinnedObjects.Count);
        Assert.Throws<InvalidOperationException>(() => s.TogglePin(objects[8]));
        Assert.Equal(8, s.PinnedObjects.Count);
        s.TogglePin(objects[0]); s.TogglePin(objects[8]); Assert.Equal(8, s.PinnedObjects.Count);
        Assert.DoesNotContain(objects[0], s.PinnedObjects);
    }
    [Fact]
    public void SearchIsBoundedAndCaseInsensitive()
    {
        using var s = Session();
        Assert.Contains(Pump, s.FindObjects("pump.running"));
        Assert.Single(s.FindObjects("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.FindObjects("", 0));
    }
    [Fact]
    public void ProjectAndObjectSnapshotsAreDetached()
    {
        using var s = Session(); var p = s.GetProject(); p.Name = "changed";
        var e = s.GetObject(Pump); e.Tag = "missing";
        Assert.NotEqual("changed", s.GetProject().Name); Assert.Equal("Pump.Running", s.GetObject(Pump).Tag);
    }
    [Fact]
    public void ReviewDoesNotChangeFeedbackUntilConfirmedAndCannotReplay()
    {
        using var s = Session(); var review = Review(s);
        Assert.True(s.Runtime.Read("Pump.Running").Value.Boolean);
        Assert.True(s.IsReviewCurrent);
        s.Confirm(review); Assert.False(s.Runtime.Read("Pump.Running").Value.Boolean);
        Assert.Null(s.PendingReview);
        Assert.Throws<InvalidOperationException>(() => s.Confirm(review));
    }
    [Fact]
    public void ForeignReviewCannotConsumeTheCurrentReview()
    {
        using var a = Session(); using var b = Session();
        var one = Review(a); var two = Review(b);
        Assert.Throws<InvalidOperationException>(() => a.Confirm(two));
        Assert.Same(one, a.PendingReview);
    }
    [Fact]
    public void StaleFeedbackConsumesReviewWithoutWriting()
    {
        using var s = Session(); var review = Review(s);
        s.Runtime.Write("Pump.Running", HmiValue.From(false));
        Assert.Throws<InvalidOperationException>(() => s.Confirm(review));
        Assert.Null(s.PendingReview);
    }
    [Theory]
    [InlineData(HmiQuality.Bad)] [InlineData(HmiQuality.Stale)] [InlineData(HmiQuality.Uncertain)]
    public void InvalidFeedbackQualityBlocksConfirmation(HmiQuality quality)
    {
        using var s = Session(); var review = Review(s);
        s.Runtime.Publish(new Dictionary<string, HmiTagSample> { ["Pump.Running"] = new(HmiValue.From(true), quality, s.Runtime.Now) }, s.Runtime.Now);
        Assert.Throws<InvalidOperationException>(() => s.Confirm(review));
        Assert.True(s.Runtime.Read("Pump.Running").Value.Boolean);
    }
    [Fact]
    public void EnableBindingIsRecheckedAtConfirmation()
    {
        var p = HmiDcsProject.Create(); p.Screens.Single(s => s.Id == "pumps").Elements.Single(e => e.Id == "pump-run").EnabledTag = "Valve.Open";
        using var s = new HmiWorkplaceSession(p); var review = Review(s);
        s.Runtime.Write("Valve.Open", HmiValue.From(false));
        Assert.Throws<InvalidOperationException>(() => s.Confirm(review));
        Assert.True(s.Runtime.Read("Pump.Running").Value.Boolean);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ContextChangesRetireReviews(int change)
    {
        using var s = Session(); var review = Review(s);
        switch (change)
        {
            case 0: s.Navigate("overview"); break;
            case 1: s.StopSimulation(); s.StartSimulation(); break;
            case 2: s.CancelReview(); break;
            case 3: s.SetHold(true); break;
        }
        Assert.Throws<InvalidOperationException>(() => s.Confirm(review));
        Assert.True(s.Runtime.Read("Pump.Running").Value.Boolean);
    }
    [Fact]
    public void ExpiryUsesMonotonicTimeEvenWhenSimulationIsHeldOrWallClockMoves()
    {
        var clock = new Clock(); using var s = new HmiWorkplaceSession(HmiDcsProject.Create(), clock: clock);
        s.StartSimulation(); s.SetHold(true); s.Navigate(Pump.ScreenId, Pump.ElementId);
        var review = s.ReviewValue(Pump, HmiValue.From(false));
        clock.Advance(TimeSpan.FromSeconds(30)); clock.WallTime -= TimeSpan.FromHours(1);
        Assert.False(s.IsReviewCurrent);
        Assert.Throws<InvalidOperationException>(() => s.Confirm(review));
        Assert.True(s.Runtime.Read("Pump.Running").Value.Boolean);
    }
    [Fact]
    public void NumericReviewValidatesTypeAndEngineeringRange()
    {
        using var s = Session(); s.Navigate(Demand.ScreenId, Demand.ElementId); s.StartSimulation();
        Assert.Throws<InvalidDataException>(() => s.ReviewValue(Demand, HmiValue.From(101d)));
        Assert.Throws<InvalidDataException>(() => s.ReviewValue(Demand, HmiValue.From(true)));
        var review = s.ReviewValue(Demand, HmiValue.From(35d)); s.Confirm(review);
        Assert.Equal(35d, s.Runtime.Read("Pump.Setpoint").Value.Number);
    }
    [Fact]
    public void SimulationNeverTouchesTheDocumentAndHoldStopsTime()
    {
        using var s = Session(); var json = HmiProjectSerializer.Serialize(s.GetProject());
        s.StartSimulation(); s.SetHold(true); var now = s.Runtime.Now;
        s.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(now, s.Runtime.Now);
        s.Step(TimeSpan.FromMilliseconds(100)); Assert.Equal(now.AddMilliseconds(100), s.Runtime.Now);
        s.StopSimulation(); Assert.Equal(json, HmiProjectSerializer.Serialize(s.GetProject()));
    }
    [Fact]
    public void BorrowedRuntimeIsReadOnlyAndIsNotStoppedOnDisposal()
    {
        var p = HmiDcsProject.Create(); var runtime = new HmiRuntime(p); runtime.Start(true);
        using (var s = new HmiWorkplaceSession(p, runtime))
        {
            Assert.Equal(HmiWorkplaceMode.ExternalReadOnly, s.Mode);
            Assert.Throws<InvalidOperationException>(s.StartSimulation);
            Assert.Throws<InvalidOperationException>(() => s.ReviewValue(Pump, HmiValue.From(false)));
            Assert.Throws<InvalidOperationException>(() => s.Acknowledge("pump-stopped", "Actor", "Comment"));
        }
        Assert.True(runtime.IsRunning); runtime.Stop();
    }
    [Fact]
    public void AlarmAspectLocatesItsSourceAndAckRetainsTheCondition()
    {
        using var s = Session(); var review = Review(s); s.Confirm(review);
        var alarm = Assert.Single(s.GetAlarms(), a => a.Id == "pump-stopped");
        Assert.True(alarm.Active); Assert.False(alarm.Acknowledged);
        var source = s.LocateAlarm(alarm.Id); Assert.NotNull(source);
        Assert.Equal("Pump.Running", s.GetObject(source.Value).Tag);
        s.Acknowledge(alarm.Id, "Local operator", "Reviewed stopped pump");
        var after = Assert.Single(s.GetAlarms(), a => a.Id == alarm.Id);
        Assert.True(after.Active); Assert.True(after.Acknowledged);
        Assert.Contains(s.Runtime.AlarmEvents, e => e.Kind == HmiAlarmEventKind.Acknowledged && e.Actor == "Local operator");
    }
    [Fact]
    public void MissingRuntimeTagFailsBeforeAttaching()
    {
        var original = HmiDcsProject.Create(); var subset = HmiDemoProject.Create(); var runtime = new HmiRuntime(subset);
        Assert.Throws<ArgumentException>(() => new HmiWorkplaceSession(original, runtime));
        Assert.False(runtime.IsRunning);
    }
    [Fact]
    public void AspectNavigationRetainsProcessControlIdentity()
    {
        using var s = Session(); using var view = new HmiOperatorWorkplace(s);
        var screen = view.ProcessView; var pump = screen!.Controls.Single(c => c.ElementId == "overview-pump");
        s.Navigate("overview", pump.ElementId); s.Show(HmiWorkplaceView.Trends); s.Show(HmiWorkplaceView.Alarms); s.Show(HmiWorkplaceView.Process);
        Assert.Same(screen, view.ProcessView); Assert.Contains(pump, view.ProcessView!.Controls);
    }
    [Fact]
    public void EngineeringRoundTripPreservesEditsWithoutSilentlySavingOrRunning()
    {
        using var studio = new HmiDcsStudio(automaticTicks: false); int factories = 0;
        studio.ConnectionFactory = _ => { factories++; throw new InvalidOperationException("Must remain unused"); };
        var review = Review(studio.Operator.Session);
        studio.ShowEngineering(); Assert.NotNull(studio.Engineering);
        Assert.Throws<InvalidOperationException>(() => studio.Operator.Session.Confirm(review));
        var editor = studio.Engineering!; Assert.Equal("pumps", editor.Session.ActiveScreenId);
        int before = editor.Session.GetProject().Screens.Single(s => s.Id == "pumps").Elements.Count;
        editor.AddComponent(HmiSymbol.Heater); Assert.True(studio.IsDirty);
        studio.ShowOperator(); Assert.False(studio.Operator.Session.Runtime.IsRunning);
        Assert.Equal(before + 1, studio.Operator.Session.GetProject().Screens.Single(s => s.Id == "pumps").Elements.Count);
        Assert.True(studio.IsDirty); studio.ShowEngineering(); Assert.Same(editor, studio.Engineering);
        Assert.Equal(0, factories);
    }
    [Theory]
    [InlineData(HmiColorScheme.Light)] [InlineData(HmiColorScheme.Dark)] [InlineData(HmiColorScheme.HighContrast)]
    public void PaletteChangesDoNotEditDocumentsOrNavigate(HmiColorScheme scheme)
    {
        using var studio = new HmiDcsStudio(automaticTicks: false);
        var before = HmiProjectSerializer.Serialize(studio.GetProject()); var screen = studio.Operator.ProcessView;
        studio.ColorScheme = scheme;
        Assert.Equal(before, HmiProjectSerializer.Serialize(studio.GetProject())); Assert.Same(screen, studio.Operator.ProcessView);
    }
    [Fact]
    public void NeutralConnectionStyleChangesDoNotReroute()
    {
        using var s = Session(); using var view = new HmiOperatorWorkplace(s);
        var layer = view.ProcessView!.DiagramLayer; long count = layer.RoutingPasses;
        layer.GraphicStyle = HmiGraphicStyle.Process; layer.GraphicStyle = HmiGraphicStyle.HighPerformance;
        Assert.Equal(count, layer.RoutingPasses);
    }
    [Fact]
    public void DisposedSessionsRejectActionsAndDetachTheirCallbacks()
    {
        var s = Session(); int notifications = 0; s.TagsChanged += _ => notifications++;
        var runtime = s.Runtime; s.Dispose(); s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Navigate("overview"));
        runtime.Start(); runtime.AdvanceSimulation(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, notifications); runtime.Stop();
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        internal DateTimeOffset WallTime = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => WallTime;
        internal void Advance(TimeSpan time) { _ticks += time.Ticks; WallTime += time; }
    }
}
