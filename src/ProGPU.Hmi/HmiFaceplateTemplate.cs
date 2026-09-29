namespace ProGPU.Hmi;

/// <summary>Reusable equipment composition with typed tag slots. Templates contain no nested templates or executable code.</summary>
public sealed class HmiFaceplateTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Equipment faceplate";
    public int Revision { get; set; } = 1;
    public float Width { get; set; } = 400;
    public float Height { get; set; } = 300;
    public List<HmiTagDefinition> Slots { get; set; } = [];
    public List<HmiElement> Elements { get; set; } = [];
}
