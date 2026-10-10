namespace ProGPU.Wpf.Interop;

/// <summary>Captures one original resource, not its flattened aggregate matrix.</summary>
public interface IPortableTransformSource
{
    bool TryGetPortableTransform(out PortableTransform transform);
}

/// <summary>
/// An immutable snapshot of original double parameters. Consumers validate
/// finite values and graph budgets before publication. A group retains source
/// identities in source order; it does not evaluate, clone or reorder children.
/// </summary>
public abstract record PortableTransform;

public sealed record PortableMatrixTransform(PortableMatrix3x2 Matrix) : PortableTransform;
public sealed record PortableTranslateTransform(double X, double Y) : PortableTransform;
public sealed record PortableScaleTransform(
    double ScaleX, double ScaleY, double CenterX, double CenterY) : PortableTransform;
public sealed record PortableRotateTransform(
    double Angle, double CenterX, double CenterY) : PortableTransform;
public sealed record PortableSkewTransform(
    double AngleX, double AngleY, double CenterX, double CenterY) : PortableTransform;

public sealed record PortableTransformGroup : PortableTransform
{
    /// <summary>The existing native source graph's per-list record limit.</summary>
    public const int MaximumChildCount = 1 << 20;

    public PortableTransformGroup(ReadOnlySpan<object> children)
    {
        if (children.Length > MaximumChildCount)
            throw new ArgumentOutOfRangeException(nameof(children));
        foreach (object child in children)
            ArgumentNullException.ThrowIfNull(child);
        Children = Array.AsReadOnly(children.ToArray());
    }

    public IReadOnlyList<object> Children { get; }
}
