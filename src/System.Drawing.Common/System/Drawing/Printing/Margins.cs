using System.Diagnostics.CodeAnalysis;

namespace System.Drawing.Printing;

/// <summary>
/// Specifies page margins in hundredths of an inch.
/// </summary>
public class Margins : ICloneable
{
    private int _left;
    private int _right;
    private int _top;
    private int _bottom;

    public Margins()
        : this(100, 100, 100, 100)
    {
    }

    public Margins(int left, int right, int top, int bottom)
    {
        Validate(left, nameof(left));
        Validate(right, nameof(right));
        Validate(top, nameof(top));
        Validate(bottom, nameof(bottom));

        _left = left;
        _right = right;
        _top = top;
        _bottom = bottom;
    }

    public int Left
    {
        get => _left;
        set
        {
            Validate(value, nameof(value));
            _left = value;
        }
    }

    public int Right
    {
        get => _right;
        set
        {
            Validate(value, nameof(value));
            _right = value;
        }
    }

    public int Top
    {
        get => _top;
        set
        {
            Validate(value, nameof(value));
            _top = value;
        }
    }

    public int Bottom
    {
        get => _bottom;
        set
        {
            Validate(value, nameof(value));
            _bottom = value;
        }
    }

    public object Clone() => MemberwiseClone();

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is Margins margins
        && margins.Left == Left
        && margins.Right == Right
        && margins.Top == Top
        && margins.Bottom == Bottom;

    public override int GetHashCode() => HashCode.Combine(Left, Right, Top, Bottom);

    public static bool operator ==(Margins? left, Margins? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Margins? left, Margins? right) => !(left == right);

    public override string ToString() =>
        $"[Margins Left={Left} Right={Right} Top={Top} Bottom={Bottom}]";

    private static void Validate(int margin, string parameterName)
    {
        if (margin < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, margin, "Margin must be non-negative.");
        }
    }
}
