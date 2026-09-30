using System.Text.Json;
using ProGPU.Hmi;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("The regression probe must run with reflection serialization disabled.");

var project = HmiDemoProject.Create();
foreach (var symbol in Enum.GetValues<HmiSymbol>())
    project.Screens[0].Elements.Add(new HmiElement { Symbol = symbol, Label = "Zażółć / 控制 / محطة",
        Appearance = new() { QuarterTurns = 3, MirrorHorizontal = true, Presentation = HmiPresentation.Process, ShowEngineeringRange = false } });
project.Connections.Add(new HmiConnectionProfile
{
    Protocol = HmiConnectionProtocol.OpcUa,
    Mappings = [new HmiIoMapping { Tag = "Tank.Level", OpcUa = new HmiOpcUaAddress { NamespaceUri = "urn:plant:probe", Identifier = "s=Level" } }]
});
var diagram = HmiShowcaseProject.Create().Screens[0];
diagram.Id = "diagram-probe";
project.Screens.Add(diagram);
string expected = HmiProjectSerializer.Serialize(project);
var copy = HmiProjectSerializer.Clone(project);
if (HmiProjectSerializer.Serialize(copy) != expected) throw new InvalidOperationException("Generated project round-trip changed configuration.");
if (copy.Screens[^1].Links.Count != 3 || copy.Screens[^1].Links[0].Source.ElementId != diagram.Links[0].Source.ElementId)
    throw new InvalidOperationException("Generated JSON lost semantic diagram topology.");
var sourceBounds = new HmiRouteBox(0, 0, 100, 100);
var targetBounds = new HmiRouteBox(300, 0, 400, 100);
var route = HmiOrthogonalRouter.Route(new("source", new(100, 50), HmiPortDirection.Right, sourceBounds),
    new("target", new(300, 50), HmiPortDirection.Left, targetBounds), [new("source", sourceBounds), new("target", targetBounds)]);
if (route.Status != HmiRouteStatus.Success || route.Points.Count != 2) throw new InvalidOperationException("Native diagram routing probe failed.");
var runtime = new HmiRuntime(copy, DateTimeOffset.UnixEpoch);
runtime.Start(allowLocalWrites: true);
runtime.AdvanceSimulation(TimeSpan.FromMilliseconds(100));
runtime.ApplyRecipe("Low demand");
if (runtime.Read("Pump.Setpoint").Value.Number != 30) throw new InvalidOperationException("Runtime did not retain typed recipe values.");
string file = Path.Combine(Path.GetTempPath(), "progpu-hmi-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    await HmiProjectFile.SaveAsync(file, copy);
    if (HmiProjectSerializer.Serialize(await HmiProjectFile.LoadAsync(file)) != expected)
        throw new InvalidOperationException("Generated file round-trip changed configuration.");
}
finally { if (File.Exists(file)) File.Delete(file); }
Console.WriteLine($"PASS: reflection-disabled project graph, {Enum.GetValues<HmiSymbol>().Length} symbols, semantic links, Unicode, OPC UA profiles, runtime, recipes and async persistence.");
