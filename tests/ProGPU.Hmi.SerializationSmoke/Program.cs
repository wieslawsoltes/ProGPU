using System.Text.Json;
using ProGPU.Hmi;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("The regression probe must run with reflection serialization disabled.");

var dcs = HmiDcsProject.Create();
if (HmiProjectSerializer.Serialize(HmiProjectSerializer.Clone(dcs)) != HmiProjectSerializer.Serialize(dcs))
    throw new InvalidOperationException("Standalone DCS project round-trip changed configuration.");
using (var workplace = new HmiWorkplaceSession(dcs))
{
    workplace.Navigate("pumps", "pump-run"); workplace.StartSimulation();
    var review = workplace.ReviewValue(new("pumps", "pump-run"), HmiValue.From(false));
    workplace.Confirm(review);
    if (workplace.Runtime.Read("Pump.Running").Value.Boolean) throw new InvalidOperationException("Native workplace review failed.");
}
var conventions = HmiConventionsProject.Create();
if (HmiProjectSerializer.Serialize(HmiProjectSerializer.Clone(conventions)) != HmiProjectSerializer.Serialize(conventions))
    throw new InvalidOperationException("Generated graphic conventions, instrument identifiers or normal-band metadata changed.");
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
diagram.Links[0].Waypoints = [new(300, 120), new(500, 120)];
diagram.Links[0].StraightSegments = [0];
project.Screens.Add(diagram);
string expected = HmiProjectSerializer.Serialize(project);
var copy = HmiProjectSerializer.Clone(project);
if (HmiProjectSerializer.Serialize(copy) != expected) throw new InvalidOperationException("Generated project round-trip changed configuration.");
if (copy.Screens[^1].Links.Count != 3 || copy.Screens[^1].Links[0].Source.ElementId != diagram.Links[0].Source.ElementId)
    throw new InvalidOperationException("Generated JSON lost semantic diagram topology.");
if (!copy.Screens[^1].Links[0].StraightSegments.SequenceEqual([0])) throw new InvalidOperationException("Generated JSON lost straight spans.");
if (!copy.Screens[^1].Links[0].Waypoints.SequenceEqual(diagram.Links[0].Waypoints))
    throw new InvalidOperationException("Generated JSON lost ordered route waypoints.");
var sourceBounds = new HmiRouteBox(0, 0, 100, 100);
var targetBounds = new HmiRouteBox(300, 0, 400, 100);
var route = HmiOrthogonalRouter.Route(new("source", new(100, 50), HmiPortDirection.Right, sourceBounds),
    new("target", new(300, 50), HmiPortDirection.Left, targetBounds), [new("source", sourceBounds), new("target", targetBounds)]);
if (route.Status != HmiRouteStatus.Success || route.Points.Count != 2) throw new InvalidOperationException("Native diagram routing probe failed.");
var pins = new HmiPoint[] { new(150, -60), new(250, -60) };
var constrained = HmiOrthogonalRouter.Route(new("source", new(100, 50), HmiPortDirection.Right, sourceBounds),
    new("target", new(300, 50), HmiPortDirection.Left, targetBounds), [new("source", sourceBounds), new("target", targetBounds)], pins, [0]);
if (constrained.Status != HmiRouteStatus.Success || !pins.All(p => constrained.Points.Contains(p)) ||
    constrained.Points[constrained.Points.ToList().IndexOf(pins[0]) + 1] != pins[1])
    throw new InvalidOperationException("Native ordered-waypoint routing probe failed.");
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
Console.WriteLine($"PASS: reflection-disabled DCS project/local review and project graph, {Enum.GetValues<HmiSymbol>().Length} symbols, ordered route pins, semantic links, Unicode, OPC UA profiles, runtime, recipes and async persistence.");
