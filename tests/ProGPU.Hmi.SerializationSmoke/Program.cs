using System.Text.Json;
using ProGPU.Hmi;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("The regression probe must run with reflection serialization disabled.");

var project = HmiDemoProject.Create();
foreach (var symbol in Enum.GetValues<HmiSymbol>())
    project.Screens[0].Elements.Add(new HmiElement { Symbol = symbol, Label = "Zażółć / 控制 / محطة" });
project.Connections.Add(new HmiConnectionProfile
{
    Protocol = HmiConnectionProtocol.OpcUa,
    Mappings = [new HmiIoMapping { Tag = "Tank.Level", OpcUa = new HmiOpcUaAddress { NamespaceUri = "urn:plant:probe", Identifier = "s=Level" } }]
});
string expected = HmiProjectSerializer.Serialize(project);
var copy = HmiProjectSerializer.Clone(project);
if (HmiProjectSerializer.Serialize(copy) != expected) throw new InvalidOperationException("Generated project round-trip changed configuration.");
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
Console.WriteLine($"PASS: reflection-disabled project graph, {Enum.GetValues<HmiSymbol>().Length} symbols, Unicode, OPC UA profiles, runtime, recipes and async persistence.");
