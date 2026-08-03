using System.Runtime.CompilerServices;

namespace SkiaSharp;

public enum SKPath1DPathEffectStyle
{
    Translate = 0,
    Rotate = 1,
    Morph = 2,
}

public enum SKTrimPathEffectMode
{
    Normal = 0,
    Inverted = 1,
}

public partial class SKPathEffect : SKObject
{
    [InlineArray(4)]
    private struct CommonDashIntervals
    {
        private float _element0;
    }

    internal enum EffectKind
    {
        Dash,
        Path1D,
        Line2D,
        Path2D,
        Compose,
        Corner,
        Discrete,
        Sum,
        Trim,
    }

    internal abstract class EffectData : IDisposable
    {
        protected EffectData(EffectKind kind)
        {
            Kind = kind;
        }

        public EffectKind Kind { get; }
        public abstract EffectData Clone();
        public virtual void Dispose()
        {
        }
    }

    internal sealed class DashData : EffectData
    {
        private CommonDashIntervals _commonIntervals;
        private readonly float[]? _overflowIntervals;
        private readonly int _intervalCount;

        public DashData(ReadOnlySpan<float> intervals, float phase)
            : base(EffectKind.Dash)
        {
            _intervalCount = intervals.Length;
            if (intervals.Length <= 4)
            {
                intervals.CopyTo(_commonIntervals);
            }
            else
            {
                _overflowIntervals = intervals.ToArray();
            }
            Phase = phase;
        }

        public ReadOnlySpan<float> Intervals => _overflowIntervals ?? _commonIntervals[.._intervalCount];
        public float Phase { get; }
        public override EffectData Clone() => new DashData(Intervals, Phase);
    }

    private sealed class DashPathEffect : SKPathEffect
    {
        private CommonDashIntervals _commonIntervals;
        private readonly float[]? _overflowIntervals;
        private readonly int _intervalCount;

        public DashPathEffect(ReadOnlySpan<float> intervals, float phase)
            : base()
        {
            _intervalCount = intervals.Length;
            if (intervals.Length <= 4)
            {
                intervals.CopyTo(_commonIntervals);
            }
            else
            {
                _overflowIntervals = intervals.ToArray();
            }
            StoredPhase = phase;
        }

        public ReadOnlySpan<float> StoredIntervals => _overflowIntervals ?? _commonIntervals[.._intervalCount];
        public float StoredPhase { get; }
    }

    internal sealed class Path1DData : EffectData
    {
        public Path1DData(SKPath path, float advance, float phase, SKPath1DPathEffectStyle style)
            : base(EffectKind.Path1D)
        {
            Path = path;
            Advance = advance;
            Phase = phase;
            Style = style;
        }

        public SKPath Path { get; }
        public float Advance { get; }
        public float Phase { get; }
        public SKPath1DPathEffectStyle Style { get; }
        public override EffectData Clone() => new Path1DData(new SKPath(Path), Advance, Phase, Style);
        public override void Dispose() => Path.Dispose();
    }

    internal sealed class Line2DData : EffectData
    {
        public Line2DData(float width, SKMatrix matrix)
            : base(EffectKind.Line2D)
        {
            Width = width;
            Matrix = matrix;
        }

        public float Width { get; }
        public SKMatrix Matrix { get; }
        public override EffectData Clone() => new Line2DData(Width, Matrix);
    }

    internal sealed class Path2DData : EffectData
    {
        public Path2DData(SKMatrix matrix, SKPath path)
            : base(EffectKind.Path2D)
        {
            Matrix = matrix;
            Path = path;
        }

        public SKMatrix Matrix { get; }
        public SKPath Path { get; }
        public override EffectData Clone() => new Path2DData(Matrix, new SKPath(Path));
        public override void Dispose() => Path.Dispose();
    }

    internal sealed class PairData : EffectData
    {
        public PairData(EffectKind kind, EffectData first, EffectData second)
            : base(kind)
        {
            First = first;
            Second = second;
        }

        public EffectData First { get; }
        public EffectData Second { get; }
        public override EffectData Clone() => new PairData(Kind, First.Clone(), Second.Clone());

        public override void Dispose()
        {
            First.Dispose();
            Second.Dispose();
        }
    }

    internal sealed class CornerData : EffectData
    {
        public CornerData(float radius)
            : base(EffectKind.Corner)
        {
            Radius = radius;
        }

        public float Radius { get; }
        public override EffectData Clone() => new CornerData(Radius);
    }

    internal sealed class DiscreteData : EffectData
    {
        public DiscreteData(float segmentLength, float deviation, uint seedAssist)
            : base(EffectKind.Discrete)
        {
            SegmentLength = segmentLength;
            Deviation = deviation;
            SeedAssist = seedAssist;
        }

        public float SegmentLength { get; }
        public float Deviation { get; }
        public uint SeedAssist { get; }
        public override EffectData Clone() => new DiscreteData(SegmentLength, Deviation, SeedAssist);
    }

    internal sealed class TrimData : EffectData
    {
        public TrimData(float start, float stop, SKTrimPathEffectMode mode)
            : base(EffectKind.Trim)
        {
            Start = start;
            Stop = stop;
            Mode = mode;
        }

        public float Start { get; }
        public float Stop { get; }
        public SKTrimPathEffectMode Mode { get; }
        public override EffectData Clone() => new TrimData(Start, Stop, Mode);
    }

    private readonly EffectData? _data;
    private int _referenceCount = 1;
    private bool _dataReleased;

    private SKPathEffect(EffectData data)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = data;
    }

    private SKPathEffect()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    internal EffectKind Kind => IsDash ? EffectKind.Dash : _data!.Kind;
    internal EffectData Data => _data ?? throw new InvalidOperationException("Inline dash state has no boxed effect data.");
    internal ReadOnlySpan<float> Intervals => this is DashPathEffect dash ? dash.StoredIntervals : ReadOnlySpan<float>.Empty;
    internal float Phase => this is DashPathEffect dash ? dash.StoredPhase : 0f;
    internal bool IsDash => this is DashPathEffect;

    private EffectData CloneEffectData() => IsDash
        ? new DashData(Intervals, Phase)
        : _data!.Clone();

    public static SKPathEffect Create1DPath(
        SKPath path,
        float advance,
        float phase,
        SKPath1DPathEffectStyle style)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new SKPathEffect(new Path1DData(new SKPath(path), advance, phase, style));
    }

    public static SKPathEffect Create2DLine(float width, SKMatrix matrix) =>
        new(new Line2DData(width, matrix));

    public static SKPathEffect Create2DPath(SKMatrix matrix, SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new SKPathEffect(new Path2DData(matrix, new SKPath(path)));
    }

    public static SKPathEffect CreateCompose(SKPathEffect outer, SKPathEffect inner)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        return new SKPathEffect(new PairData(EffectKind.Compose, outer.CloneEffectData(), inner.CloneEffectData()));
    }

    public static SKPathEffect CreateCorner(float radius) => new(new CornerData(radius));

    public static SKPathEffect CreateDash(float[] intervals, float phase)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        return new DashPathEffect(intervals, phase);
    }

    public static SKPathEffect CreateDiscrete(float segLength, float deviation, uint seedAssist = 0) =>
        new(new DiscreteData(segLength, deviation, seedAssist));

    public static SKPathEffect CreateSum(SKPathEffect first, SKPathEffect second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return new SKPathEffect(new PairData(EffectKind.Sum, first.CloneEffectData(), second.CloneEffectData()));
    }

    public static SKPathEffect CreateTrim(float start, float stop) =>
        CreateTrim(start, stop, SKTrimPathEffectMode.Normal);

    public static SKPathEffect CreateTrim(float start, float stop, SKTrimPathEffectMode mode) =>
        new(new TrimData(start, stop, mode));

    internal void AddReference()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(SKPathEffect));
        }

        checked
        {
            _referenceCount++;
        }
    }

    internal void ReleaseReference()
    {
        if (_referenceCount <= 0)
        {
            return;
        }

        _referenceCount--;
        if (_referenceCount == 0 && !_dataReleased)
        {
            _dataReleased = true;
            _data?.Dispose();
        }
    }

    protected override void DisposeManaged()
    {
        ReleaseReference();
        base.DisposeManaged();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
