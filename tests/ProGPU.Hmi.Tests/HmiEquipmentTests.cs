using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiEquipmentTests
{
    [Fact]
    public void FaceplateInstancesBindTypedSlotsAndRetainSourceIdentityOnUpdate()
    {
        var project = HmiDemoProject.Create(); var template = HmiBuiltInFaceplates.PumpStation(); project.Faceplates.Add(template);
        var ids = HmiFaceplates.Instantiate(project, project.Screens[0], template.Id, "P101", 100, 200);
        HmiProjectSerializer.Validate(project);
        Assert.Equal(5, ids.Count); Assert.Contains(project.Tags, t => t.Name == "P101.Running" && t.Type == HmiTagType.Boolean);
        var pump = project.Screens[0].Elements.Single(e => e.Id == ids[0]);
        Assert.Equal("P101.Running", pump.Tag); Assert.Equal("P101.Trip", pump.States[0].Tag);
        template.Elements[0].Label = "Updated master"; template.Revision++;
        Assert.Equal(1, HmiFaceplates.Synchronize(project, template.Id));
        var updated = project.Screens[0].Elements.Single(e => e.Id == pump.Id);
        Assert.Equal("Updated master", updated.Label); Assert.Equal(120f, updated.X); Assert.Equal(220f, updated.Y);
        Assert.Equal(pump.FaceplateInstanceId, updated.FaceplateInstanceId);
        string json = HmiProjectSerializer.Serialize(project);
        Assert.Equal(json, HmiProjectSerializer.Serialize(HmiProjectSerializer.Deserialize(json)));
    }
    [Fact]
    public void ClipboardCopiesDetachFromFaceplateMasters()
    {
        var project = HmiDemoProject.Create(); project.Faceplates.Add(HmiBuiltInFaceplates.PumpStation());
        var ids = HmiFaceplates.Instantiate(project, project.Screens[0], project.Faceplates[0].Id, "P101", 0, 0);
        var copy = project.Screens[0].Elements.Single(e => e.Id == ids[0]).Copy(newIdentity: true);
        Assert.Empty(copy.FaceplateInstanceId); Assert.Empty(copy.FaceplateTemplateId);
        Assert.Equal("P101.Running", copy.Tag);
    }
    [Fact]
    public void CapturingAndDetachingEquipmentAreDocumentOperations()
    {
        var project = HmiDemoProject.Create();
        var pump = project.Screens[0].Elements.Single(e => e.Symbol == HmiSymbol.Pump);
        var template = HmiFaceplates.Capture(project, project.Screens[0], [pump.Id], "Transfer pump");
        project.Faceplates.Add(template);
        Assert.Equal("$Pump.Running", template.Elements[0].Tag);
        var ids = HmiFaceplates.Instantiate(project, project.Screens[0], template.Id, "Backup", 30, 40);
        var instance = project.Screens[0].Elements.Single(e => e.Id == ids[0]);
        HmiFaceplates.Detach(project.Screens[0], [instance.FaceplateInstanceId]);
        Assert.Empty(instance.FaceplateTemplateId);
        HmiProjectSerializer.Validate(project);
    }
    [Fact]
    public void FaceplateTypeConflictsRollbackThroughTheDesignerSession()
    {
        var project = HmiDemoProject.Create(); project.Faceplates.Add(HmiBuiltInFaceplates.PumpStation());
        project.Tags.Add(new HmiTagDefinition { Name = "P101.Running", Type = HmiTagType.Text, InitialValue = HmiValue.From("bad") });
        var session = new HmiDesignerSession(project); string before = session.ExportJson();
        Assert.Throws<InvalidDataException>(() => session.Edit("Insert", p => HmiFaceplates.Instantiate(p, p.Screens[0], p.Faceplates[0].Id, "P101", 0, 0)));
        Assert.Equal(before, session.ExportJson());
    }
    [Fact]
    public void StatePriorityAndUnknownQualityAreExplicit()
    {
        HmiStateRule[] rules = [new() { Tag = "run", Tone = HmiVisualTone.Running, Text = "RUN", Priority = 1 }, new() { Tag = "trip", Tone = HmiVisualTone.Fault, Text = "TRIP", Priority = 100 }];
        var state = HmiStateEvaluator.Evaluate(rules, _ => new HmiTagSample(HmiValue.From(true), HmiQuality.Good, DateTimeOffset.UtcNow));
        Assert.Equal(HmiVisualTone.Fault, state.Tone); Assert.Equal("TRIP", state.Text);
        var unknown = HmiStateEvaluator.Evaluate(rules, tag => tag == "trip" ? null : new HmiTagSample(HmiValue.From(true), HmiQuality.Good, DateTimeOffset.UtcNow));
        Assert.Equal(HmiVisualTone.Unknown, unknown.Tone);
    }
    [Fact]
    public void StateAndConnectionReferencesFollowTransactionalTagRename()
    {
        var project = HmiDemoProject.Create();
        project.Screens[0].Elements[0].States.Add(new() { Tag = "Pump.Running" });
        project.Connections.Add(new HmiConnectionProfile { Mappings = [new() { Tag = "Pump.Running", Type = HmiTagType.Boolean, Area = HmiModbusArea.Coil, Encoding = HmiRegisterEncoding.Boolean, InterlockTag = "Pump.Running" }] });
        var session = new HmiDesignerSession(project); session.RenameTag("Pump.Running", "P101.Run");
        var changed = session.GetProject();
        Assert.Equal("P101.Run", changed.Screens[0].Elements[0].States[0].Tag);
        Assert.Equal("P101.Run", changed.Connections[0].Mappings[0].Tag);
        Assert.Equal("P101.Run", changed.Connections[0].Mappings[0].InterlockTag);
    }
    [Fact]
    public void EightNewEquipmentSymbolsAreIndependentlyInstantiable()
    {
        HmiControl[] controls = [new HmiHeatExchanger(), new HmiFilter(), new HmiCompressor(), new HmiFan(), new HmiHeater(), new HmiThermometer(), new HmiBoiler(), new HmiCoolingTower()];
        Assert.Equal(8, controls.Select(c => c.Symbol).Distinct().Count());
        Assert.Equal(28, HmiControlCatalog.Items.Count);
        foreach (var control in controls)
        {
            control.UpdateState(new HmiVisualState(HmiVisualTone.Maintenance, "ISOLATED"));
            Assert.Equal("ISOLATED", control.StateText); Assert.True(control.Width > 0); Assert.True(control.Height > 0);
        }
    }
    [Fact]
    public void LiveRuntimeNeverStartsWithTrainingValuesMarkedGood()
    {
        var runtime = new HmiRuntime(HmiDemoProject.Create(), initializeGoodQuality: false);
        Assert.Equal(HmiQuality.Uncertain, runtime.Read("Tank.Level").Quality);
        Assert.Empty(runtime.GetHistory("Tank.Level"));
        Assert.All(runtime.Alarms, alarm => Assert.True(alarm.IsQualityUnknown));
    }
    [Fact]
    public void OpeningConfiguredProjectNeverConstructsANetworkAdapter()
    {
        var project = HmiDemoProject.Create(); project.Connections.Add(new HmiConnectionProfile());
        using var host = new HmiDesignerHost(project);
        host.ConnectionFactory = _ => throw new Exception("Must not be invoked by project load.");
        host.Session.ImportJson(host.Session.ExportJson());
        Assert.Null(host.ConnectionDiagnostics); Assert.False(host.IsPreviewing);
    }
}
