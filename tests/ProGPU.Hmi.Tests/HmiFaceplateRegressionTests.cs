using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiFaceplateRegressionTests
{
    [Fact]
    public void MasterGeometryChangesPreserveInstanceOrigins()
    {
        var project = HmiDemoProject.Create();
        var template = HmiBuiltInFaceplates.PumpStation(); project.Faceplates.Add(template);
        var ids = HmiFaceplates.Instantiate(project, project.Screens[0], template.Id, "P101", 100, 200);
        string id = ids[0];
        template.Elements[0].X += 25; template.Elements[0].Y += 10;
        HmiFaceplates.Synchronize(project, template.Id);
        var pump = project.Screens[0].Elements.Single(e => e.Id == id);
        Assert.Equal(145f, pump.X); Assert.Equal(230f, pump.Y);
        // Applying the same master twice cannot accumulate an origin drift.
        HmiFaceplates.Synchronize(project, template.Id);
        pump = project.Screens[0].Elements.Single(e => e.Id == id);
        Assert.Equal(145f, pump.X); Assert.Equal(230f, pump.Y);
        HmiProjectSerializer.Validate(project);
    }

    [Fact]
    public void MastersDoNotConsumeTheProjectsScreenBudgetDuringValidation()
    {
        var project = HmiDemoProject.Create(); project.Faceplates.Add(HmiBuiltInFaceplates.PumpStation());
        while (project.Screens.Count < 128) project.Screens.Add(new HmiScreen());
        HmiProjectSerializer.Validate(project);
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("element")]
    [InlineData("state")]
    [InlineData("action")]
    [InlineData("binding")]
    public void MalformedMastersFailAsValidationErrors(string member)
    {
        var project = HmiDemoProject.Create(); var template = HmiBuiltInFaceplates.PumpStation(); project.Faceplates.Add(template);
        switch (member)
        {
            case "slot": template.Slots[0] = null!; break;
            case "element": template.Elements[0] = null!; break;
            case "state": template.Elements[0].States.Add(null!); break;
            case "action": template.Elements[0].Action = null!; break;
            case "binding": template.Elements[0].Tag = null!; break;
        }
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(project));
    }
}
