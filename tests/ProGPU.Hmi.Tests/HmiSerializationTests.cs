using System.Text.Json;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiSerializationTests
{
    [Fact]
    public void TestHostActuallyDisablesReflectionSerialization() => Assert.False(JsonSerializer.IsReflectionEnabledByDefault);

    [Fact]
    public void EntireProjectGraphRoundTripsWithoutReflection()
    {
        var project = HmiDemoProject.Create();
        project.Name = "Zażółć gęślą jaźń / 控制 / محطة";
        project.Connections.Add(new HmiConnectionProfile
        {
            Id = "opcua", Protocol = HmiConnectionProtocol.OpcUa, Port = 4840,
            Mappings = [new HmiIoMapping
            {
                Tag = "Tank.Level", Type = HmiTagType.Number,
                OpcUa = new HmiOpcUaAddress { NamespaceUri = "urn:plant:line", Identifier = "s=Tank.Level", DataType = HmiOpcUaDataType.Double }
            }]
        });
        string json = HmiProjectSerializer.Serialize(project);
        var copy = HmiProjectSerializer.Deserialize(json);
        Assert.Equal(json, HmiProjectSerializer.Serialize(copy));
        Assert.Equal(project.Name, copy.Name);
        Assert.Equal(HmiConnectionProtocol.OpcUa, copy.Connections[0].Protocol);
        Assert.Equal("urn:plant:line", copy.Connections[0].Mappings[0].OpcUa.NamespaceUri);
        Assert.NotSame(project.Screens[0], copy.Screens[0]);
        Assert.NotSame(project.Recipes[0].Values, copy.Recipes[0].Values);
    }

    [Fact]
    public void GalleryDesignerConstructionAndUndoWorkWithoutReflection()
    {
        using var host = new HmiDesignerHost();
        string before = host.Session.ExportJson();
        host.AddComponent(HmiSymbol.HeatExchanger);
        Assert.NotEqual(before, host.Session.ExportJson());
        host.Session.Undo();
        Assert.Equal(before, host.Session.ExportJson());
        host.Session.Redo();
        var clone = HmiProjectSerializer.Clone(host.Session.GetProject());
        Assert.Contains(clone.Screens[0].Elements, element => element.Symbol == HmiSymbol.HeatExchanger);
        host.StartPreview(automaticTicks: false);
        host.AdvancePreview(TimeSpan.FromMilliseconds(100));
        host.StopPreview();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("\"UnknownSymbol\"")]
    public void GeneratedSerializationRetainsStrictStringEnums(string symbol)
    {
        string json = HmiProjectSerializer.Serialize(HmiDemoProject.Create());
        Assert.Contains("\"symbol\": \"Label\"", json);
        json = json.Replace("\"symbol\": \"Label\"", "\"symbol\": " + symbol, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HmiProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void GeneratedSerializationRejectsUnknownNestedProperties()
    {
        string json = HmiProjectSerializer.Serialize(HmiDemoProject.Create());
        json = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"untrustedType\": \"System.Object\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HmiProjectSerializer.Deserialize(json));
    }
}
