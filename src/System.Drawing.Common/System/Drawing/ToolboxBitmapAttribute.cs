namespace System.Drawing;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolboxBitmapAttribute : Attribute
{
    public ToolboxBitmapAttribute(string imageFile)
    {
        ImageFile = imageFile;
    }

    public ToolboxBitmapAttribute(Type t)
    {
        ToolboxType = t;
    }

    public ToolboxBitmapAttribute(Type t, string name)
    {
        ToolboxType = t;
        Name = name;
    }

    public string? ImageFile { get; }

    public string? Name { get; }

    public Type? ToolboxType { get; }

    public Image GetImage(Type type)
    {
        return GetImage(type, false);
    }

    public Image GetImage(Type type, bool large)
    {
        int size = large ? 32 : 16;
        return new Bitmap(size, size);
    }
}
