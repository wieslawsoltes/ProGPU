using System.Numerics;
using ProGPU.Vector;

namespace System.Drawing.Drawing2D;

public sealed class AdjustableArrowCap : CustomLineCap
{
    private float _height;
    private float _middleInset;
    private float _width;
    private bool _filled;

    public AdjustableArrowCap(float width, float height)
        : this(width, height, true)
    {
    }

    public AdjustableArrowCap(float width, float height, bool isFilled)
        : base(fillPath: null, strokePath: null, LineCap.Triangle)
    {
        _width = width;
        _height = height;
        _filled = isFilled;
    }

    private AdjustableArrowCap(AdjustableArrowCap source)
        : base(source)
    {
        _width = source._width;
        _height = source._height;
        _middleInset = source._middleInset;
        _filled = source._filled;
    }

    public bool Filled
    {
        get => Read(_filled);
        set
        {
            EnsureUsable();
            _filled = value;
        }
    }

    public float Height
    {
        get => Read(_height);
        set
        {
            EnsureUsable();
            _height = value;
        }
    }

    public float MiddleInset
    {
        get => Read(_middleInset);
        set
        {
            EnsureUsable();
            _middleInset = value;
        }
    }

    public float Width
    {
        get => Read(_width);
        set
        {
            EnsureUsable();
            _width = value;
        }
    }

    internal override PathGeometry? FillGeometry
        => Filled ? CreateArrowGeometry() : null;

    internal override PathGeometry? StrokeGeometry
        => Filled ? null : CreateArrowGeometry();

    internal override CustomLineCap CloneCore() => new AdjustableArrowCap(this);

    private PathGeometry? CreateArrowGeometry()
    {
        float width = Width;
        float height = Height;
        float inset = MiddleInset;
        if (!float.IsFinite(width) || !float.IsFinite(height) || !float.IsFinite(inset) ||
            MathF.Abs(width) <= 0.0001f || MathF.Abs(height) <= 0.0001f)
        {
            return null;
        }

        float halfWidth = width * 0.5f;
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        var figure = new PathFigure(new Vector2(-halfWidth, 0f), isClosed: true)
        {
            IsFilled = true,
        };
        figure.Segments.Add(new LineSegment(new Vector2(0f, height)));
        figure.Segments.Add(new LineSegment(new Vector2(halfWidth, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, inset)));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private T Read<T>(T value)
    {
        EnsureUsable();
        return value;
    }

    private void EnsureUsable()
    {
        _ = BaseCap;
    }
}
