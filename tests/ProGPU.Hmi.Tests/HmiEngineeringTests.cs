using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiEngineeringTests
{
    [Fact]
    public void AnalysisReportsUnboundAndOffscreenComponentsWithoutMutation()
    {
        var project = HmiDemoProject.Create();
        project.Screens[0].Elements.Add(new HmiElement { Symbol = HmiSymbol.Gauge, X = -20 });
        string before = HmiProjectSerializer.Serialize(project);
        var report = HmiProjectAnalyzer.Analyze(project);
        Assert.Contains(report.Diagnostics, d => d.Code == "HMI2001");
        Assert.Contains(report.Diagnostics, d => d.Code == "HMI1003");
        Assert.Equal(before, HmiProjectSerializer.Serialize(project));
    }

    [Fact]
    public void CrossReferencesCoverCommandsAlarmsRecipesAndLiveMappings()
    {
        var project = HmiDemoProject.Create();
        project.Connections.Add(new HmiConnectionProfile { Mappings = [new HmiIoMapping { Tag = "Pump.Running", Type = HmiTagType.Boolean, Area = HmiModbusArea.Coil, Encoding = HmiRegisterEncoding.Boolean, Writable = true, InterlockTag = "Valve.Open" }] });
        var report = HmiProjectAnalyzer.Analyze(project);
        var usages = report.References.Where(r => r.Tag == "Pump.Running").Select(r => r.Kind).ToHashSet();
        Assert.Contains(HmiTagReferenceKind.Value, usages); Assert.Contains(HmiTagReferenceKind.Command, usages);
        Assert.Contains(HmiTagReferenceKind.Alarm, usages); Assert.Contains(HmiTagReferenceKind.Recipe, usages);
        Assert.Contains(HmiTagReferenceKind.Acquisition, usages);
        Assert.Contains(report.References, r => r.Tag == "Valve.Open" && r.Kind == HmiTagReferenceKind.Permissive);
    }

    [Fact]
    public void ResultBudgetIsReportedRatherThanSilentlyTruncating()
    {
        var report = HmiProjectAnalyzer.Analyze(HmiDemoProject.Create(), maximumResults: 1);
        Assert.True(report.IsTruncated);
        Assert.True(report.References.Count <= 1); Assert.True(report.Diagnostics.Count <= 1);
    }

    [Fact]
    public void InvalidProjectsProduceAnErrorDiagnostic()
    {
        var project = HmiDemoProject.Create(); project.StartScreenId = "missing";
        var report = HmiProjectAnalyzer.Analyze(project);
        Assert.Equal(HmiDiagnosticSeverity.Error, Assert.Single(report.Diagnostics).Severity);
        Assert.Empty(report.References);
    }

    [Fact]
    public void InvalidTrendConfigurationIsRejectedBeforeItEntersHistory()
    {
        var project = HmiDemoProject.Create();
        project.Screens[0].Elements[0].Trend.WindowSeconds = double.NaN;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Serialize(project));
    }
}
