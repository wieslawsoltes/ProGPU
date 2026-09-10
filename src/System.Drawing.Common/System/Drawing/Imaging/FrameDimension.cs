namespace System.Drawing.Imaging;

public sealed class FrameDimension
{
    public FrameDimension(Guid guid)
    {
        Guid = guid;
    }

    public Guid Guid { get; }

    public static FrameDimension Time { get; } =
        new(new Guid("6aedbd6d-3fb5-418a-83a6-7f45229dc872"));

    public static FrameDimension Resolution { get; } =
        new(new Guid("84236f7b-3bd3-428f-8dab-4ea1439ca315"));

    public static FrameDimension Page { get; } =
        new(new Guid("7462dc86-6180-4c7e-8e3f-ee7333a7a483"));

    public override bool Equals(object? obj) =>
        obj is FrameDimension other && other.Guid == Guid;

    public override int GetHashCode() => Guid.GetHashCode();

    public override string ToString()
    {
        if (Guid == Time.Guid)
        {
            return nameof(Time);
        }

        if (Guid == Resolution.Guid)
        {
            return nameof(Resolution);
        }

        if (Guid == Page.Guid)
        {
            return nameof(Page);
        }

        return $"[FrameDimension: {Guid:D}]";
    }
}
