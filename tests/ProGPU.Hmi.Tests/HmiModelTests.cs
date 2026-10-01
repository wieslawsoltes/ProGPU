using System.Text.Json;
using ProGPU.Hmi;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiModelTests
{
    [Fact]
    public void DemoRoundTripsWithoutLosingConfiguration()
    {
        string json = HmiProjectSerializer.Serialize(HmiDemoProject.Create());
        Assert.Equal(json, HmiProjectSerializer.Serialize(HmiProjectSerializer.Deserialize(json)));
        var project = HmiProjectSerializer.Deserialize(json);
        Assert.Equal(2, project.Screens.Count);
        Assert.Equal(7, project.Tags.Count);
        Assert.Equal(3, project.Alarms.Count);
        Assert.Equal(2, project.Recipes.Count);
    }
    [Theory]
    [InlineData(HmiSymbol.Label)] [InlineData(HmiSymbol.NumericDisplay)] [InlineData(HmiSymbol.NumericInput)]
    [InlineData(HmiSymbol.Indicator)] [InlineData(HmiSymbol.PushButton)] [InlineData(HmiSymbol.ToggleSwitch)]
    [InlineData(HmiSymbol.Tank)] [InlineData(HmiSymbol.Pump)] [InlineData(HmiSymbol.Valve)] [InlineData(HmiSymbol.Motor)]
    [InlineData(HmiSymbol.Pipe)] [InlineData(HmiSymbol.Conveyor)] [InlineData(HmiSymbol.Gauge)] [InlineData(HmiSymbol.BarGraph)]
    [InlineData(HmiSymbol.Trend)] [InlineData(HmiSymbol.AlarmBanner)] [InlineData(HmiSymbol.AlarmList)]
    [InlineData(HmiSymbol.NavigationButton)] [InlineData(HmiSymbol.RecipeButton)] [InlineData(HmiSymbol.Rectangle)]
    public void EverySymbolRoundTrips(HmiSymbol symbol)
    {
        var project = HmiDemoProject.Create();
        project.Screens[0].Elements.Add(new HmiElement { Symbol = symbol, Label = "A & B < C", Tag = "Tank.Level", Group = "group1", IsLocked = true });
        var clone = HmiProjectSerializer.Clone(project);
        var element = clone.Screens[0].Elements[^1];
        Assert.Equal(symbol, element.Symbol); Assert.Equal("A & B < C", element.Label);
        Assert.True(element.IsLocked); Assert.Equal("group1", element.Group);
    }
    [Theory]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("1,5")] [InlineData("abc")]
    public void NumericParserRejectsMalformedOrNonFiniteValues(string value) => Assert.Throws<FormatException>(() => HmiValue.Parse(value, HmiTagType.Number));
    [Fact]
    public void ValuesRemainTyped()
    {
        Assert.Equal(1.25, HmiValue.Parse("1.25", HmiTagType.Number).Number);
        Assert.True(HmiValue.Parse("1", HmiTagType.Boolean).Boolean);
        Assert.Equal("1", HmiValue.Parse("1", HmiTagType.Text).Text);
    }
    [Fact]
    public void InvalidSchemaAndUnknownMembersAreRejected()
    {
        var project = HmiDemoProject.Create(); project.SchemaVersion = 99;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Serialize(project));
        Assert.Throws<JsonException>(() => HmiProjectSerializer.Deserialize("{\"notAProjectField\":true}"));
    }
    [Fact]
    public void InvalidReferencesAndGeometryAreRejected()
    {
        var project = HmiDemoProject.Create();
        project.Screens[0].Elements[0].Tag = "does.not.exist";
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
        project = HmiDemoProject.Create(); project.Screens[0].Elements[0].Width = float.NaN;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
        project = HmiDemoProject.Create(); project.Screens[0].Elements[0].VisibilityTag = "Tank.Level";
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
    }
    [Fact]
    public void DuplicateIdentitiesAreRejected()
    {
        var project = HmiDemoProject.Create(); project.Tags.Add(project.Tags[0]);
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
        project = HmiDemoProject.Create(); project.Screens[0].Elements.Add(project.Screens[0].Elements[0].Copy());
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
    }
    [Fact]
    public void UnknownActionAndReadOnlyRecipeAreRejected()
    {
        var project = HmiDemoProject.Create(); project.Screens[0].Elements[0].Action.Kind = (HmiActionKind)999;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
        project = HmiDemoProject.Create(); project.Recipes[0].Values["Tank.Level"] = HmiValue.From(50d);
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
    }
    [Fact]
    public void OversizedInputIsRejectedBeforeParsing() => Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Deserialize(new string('x', HmiProjectSerializer.MaximumDocumentBytes + 1)));
    [Fact]
    public void ElementCopiesOwnTheirActions()
    {
        var original = new HmiElement { Action = new HmiAction { Kind = HmiActionKind.Navigate, Target = "overview" } };
        var copy = original.Copy(newIdentity: true);
        copy.Action.Target = "other";
        Assert.NotEqual(original.Id, copy.Id); Assert.Equal("overview", original.Action.Target);
    }
    [Fact]
    public void HistoryRingPreservesChronologicalOrderAndBounds()
    {
        var history = new HmiHistory<int>(3);
        for (int i = 0; i < 100; i++) history.Add(i);
        Assert.Equal(new[] { 97, 98, 99 }, history.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => history[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => history[3]);
        history.Clear(); Assert.Empty(history);
    }
    [Fact]
    public async Task DesktopSaveLoadRoundTripsAndInvalidSavePreservesOriginal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "progpu-hmi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "project.hmi.json");
        try
        {
            var project = HmiDemoProject.Create();
            await HmiProjectFile.SaveAsync(path, project);
            string before = await File.ReadAllTextAsync(path);
            Assert.Equal(before, HmiProjectSerializer.Serialize(await HmiProjectFile.LoadAsync(path)));
            project.SchemaVersion = 99;
            await Assert.ThrowsAsync<InvalidDataException>(() => HmiProjectFile.SaveAsync(path, project));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
