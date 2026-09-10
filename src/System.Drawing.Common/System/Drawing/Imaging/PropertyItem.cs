namespace System.Drawing.Imaging;

public sealed class PropertyItem
{
    internal PropertyItem()
    {
    }

    internal PropertyItem(int id, short type, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id = id;
        Type = type;
        Len = value.Length;
        Value = value;
    }

    public int Id { get; set; }

    public int Len { get; set; }

    public short Type { get; set; }

    public byte[] Value { get; set; } = [];

    internal PropertyItem CloneItem() =>
        new(Id, Type, (byte[])Value.Clone()) { Len = Len };
}
