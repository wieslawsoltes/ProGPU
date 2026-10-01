using System.Text.Json;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiDiagramModelTests
{
    internal static HmiProject Project()
    {
        var project = new HmiProject { Name = "Diagram fixture", StartScreenId = "overview", Screens = [new() { Id = "overview", Width = 1000, Height = 800 }],
            Tags = [new() { Name = "Running", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(true), Writable = true }] };
        project.Screens[0].Elements = [
            new() { Id = "a", Symbol = HmiSymbol.Valve, X = 60, Y = 200, Width = 180, Height = 190 },
            new() { Id = "b", Symbol = HmiSymbol.Pump, X = 460, Y = 200, Width = 180, Height = 190 }
        ];
        project.Screens[0].Links.Add(new() { Id = "ab", Source = new() { ElementId = "a", PortId = "outlet" }, Target = new() { ElementId = "b", PortId = "inlet" }, ActivityTag = "Running" });
        HmiProjectSerializer.Validate(project);
        return project;
    }

    [Theory]
    [InlineData(HmiLinkKind.Process)] [InlineData(HmiLinkKind.Signal)] [InlineData(HmiLinkKind.Electrical)]
    public void TopologyRoundTripsWithReflectionDisabledAndOwnsEndpointSnapshots(HmiLinkKind kind)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var project = Project(); var link = project.Screens[0].Links[0];
        link.Kind = kind; link.Name = "流量 / Żółć"; link.Thickness = 5; link.Clearance = 18; link.IsLocked = true;
        string json = HmiProjectSerializer.Serialize(project);
        var clone = HmiProjectSerializer.Deserialize(json);
        Assert.Equal(json, HmiProjectSerializer.Serialize(clone));
        clone.Screens[0].Links[0].Source.PortId = "changed";
        Assert.Equal("outlet", link.Source.PortId);
        var copy = link.Copy(true); Assert.NotEqual(link.Id, copy.Id); Assert.NotSame(link.Target, copy.Target);
    }

    [Fact]
    public void OlderDocumentsHaveAnIndependentEmptyLinkList()
    {
        var project = HmiDemoProject.Create();
        string json = HmiProjectSerializer.Serialize(project);
        // Remove the newly optional field using a DOM, not reflection-based serialization.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            using var parsed = JsonDocument.Parse(json);
            writer.WriteStartObject();
            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                if (property.Name != "screens") { property.WriteTo(writer); continue; }
                writer.WritePropertyName("screens"); writer.WriteStartArray();
                foreach (var screen in property.Value.EnumerateArray())
                {
                    writer.WriteStartObject(); foreach (var field in screen.EnumerateObject()) if (field.Name != "links") field.WriteTo(writer); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        var restored = HmiProjectSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        Assert.All(restored.Screens, screen => Assert.Empty(screen.Links));
        Assert.NotSame(restored.Screens[0].Links, restored.Screens[1].Links);
    }

    [Theory]
    [InlineData("missingElement")] [InlineData("missingPort")] [InlineData("samePort")] [InlineData("missingTag")]
    [InlineData("wrongTagType")] [InlineData("nullEndpoint")] [InlineData("nullLinks")] [InlineData("duplicateLink")]
    [InlineData("invalidEnum")] [InlineData("thickness")] [InlineData("clearance")] [InlineData("duplicateElement")]
    public void InvalidTopologyCannotPublishATransaction(string fault)
    {
        var session = new HmiDesignerSession(Project()); string before = session.ExportJson();
        Assert.Throws<InvalidDataException>(() => session.Edit("Invalid", p =>
        {
            var screen = p.Screens[0]; var link = screen.Links[0];
            switch (fault)
            {
                case "missingElement": link.Source.ElementId = "missing"; break;
                case "missingPort": link.Target.PortId = "not-a-nozzle"; break;
                case "samePort": link.Target = link.Source.Copy(); break;
                case "missingTag": link.ActivityTag = "missing"; break;
                case "wrongTagType": p.Tags[0].Type = HmiTagType.Number; p.Tags[0].InitialValue = HmiValue.From(1d); break;
                case "nullEndpoint": link.Source = null!; break;
                case "nullLinks": screen.Links = null!; break;
                case "duplicateLink": screen.Links.Add(link.Copy()); break;
                case "invalidEnum": link.Kind = (HmiLinkKind)999; break;
                case "thickness": link.Thickness = float.NaN; break;
                case "clearance": link.Clearance = 129; break;
                case "duplicateElement": screen.Elements.Add(screen.Elements[0].Copy()); break;
            }
        }));
        Assert.Equal(before, session.ExportJson()); Assert.False(session.CanUndo);
    }

    [Fact]
    public void ReferenceRenamingAndScreenDuplicationPreserveGraphIntegrity()
    {
        var session = new HmiDesignerSession(Project());
        session.RenameTag("Running", "Permissive");
        Assert.Equal("Permissive", session.GetProject().Screens[0].Links[0].ActivityTag);
        session.DuplicateScreen();
        var project = session.GetProject(); var copy = project.Screens[1];
        Assert.DoesNotContain(copy.Elements, e => e.Id is "a" or "b");
        Assert.NotEqual("ab", copy.Links[0].Id);
        Assert.Contains(copy.Elements, e => e.Id == copy.Links[0].Source.ElementId);
        Assert.Contains(copy.Elements, e => e.Id == copy.Links[0].Target.ElementId);
        session.Undo(); Assert.Single(session.GetProject().Screens);
        session.Redo(); Assert.Equal(2, session.GetProject().Screens.Count);
    }

    [Fact]
    public void IncidentLockedLinksBlockDeletionBeforeRemovingOtherLinks()
    {
        var screen = Project().Screens[0];
        var locked = screen.Links[0].Copy(true); locked.IsLocked = true; screen.Links.Add(locked);
        screen.Elements.RemoveAt(0);
        Assert.Throws<InvalidOperationException>(() => HmiDiagram.RemoveDanglingLinks(screen));
        Assert.Equal(2, screen.Links.Count);
        locked.IsLocked = false;
        HmiDiagram.RemoveDanglingLinks(screen); Assert.Empty(screen.Links);
    }

    [Fact]
    public void ClipboardDoesNotCopyLinksToExternalEquipment()
    {
        var screen = Project().Screens[0];
        Assert.Empty(HmiDiagram.CopyInternalLinks(screen.Links, new Dictionary<string, string> { ["a"] = "new-a" }));
        var copies = HmiDiagram.CopyInternalLinks(screen.Links, new Dictionary<string, string> { ["a"] = "new-a", ["b"] = "new-b" });
        Assert.Single(copies); Assert.Equal("new-a", copies[0].Source.ElementId); Assert.Equal("new-b", copies[0].Target.ElementId);
        Assert.NotEqual(screen.Links[0].Id, copies[0].Id);
    }

    [Fact]
    public void EngineeringReportIncludesDiagramFeedbackAsAnActualTagConsumer()
    {
        var report = HmiProjectAnalyzer.Analyze(Project());
        Assert.Contains(report.References, r => r.Tag == "Running" && r.Kind == HmiTagReferenceKind.Diagram);
        Assert.DoesNotContain(report.Diagnostics, d => d.Tag == "Running" && d.Code == "HMI3002");
    }

    [Fact]
    public void LinkBudgetIsValidatedBeforeSerialization()
    {
        var project = Project(); var screen = project.Screens[0];
        while (screen.Links.Count < HmiDiagram.MaximumLinksPerScreen) screen.Links.Add(screen.Links[0].Copy(true));
        HmiProjectSerializer.Validate(project);
        screen.Links.Add(screen.Links[0].Copy(true));
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Serialize(project));
    }

    [Fact]
    public void ShowcaseUsesThreeActualNozzleConnectionsInsteadOfTheOldDecorativePipe()
    {
        var screen = HmiShowcaseProject.Create().Screens[0];
        Assert.Equal(3, screen.Links.Count); Assert.DoesNotContain(screen.Elements, e => e.Symbol == HmiSymbol.Pipe);
        var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        Assert.All(layer.Routes.Values, route => Assert.Equal(HmiRouteStatus.Success, route.Status));
    }
}
