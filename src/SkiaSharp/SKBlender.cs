namespace SkiaSharp;

public class SKBlender : SKObject
{
    private readonly SKBlendMode? _blendMode;
    private readonly ArithmeticBlend? _arithmetic;

    private SKBlender(SKBlendMode blendMode)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _blendMode = blendMode;
    }

    private SKBlender(ArithmeticBlend arithmetic)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _arithmetic = arithmetic;
    }

    internal bool IsArithmetic => _arithmetic.HasValue;

    internal ArithmeticBlend? Arithmetic => _arithmetic;

    internal bool TryGetBlendMode(out SKBlendMode blendMode)
    {
        blendMode = _blendMode.GetValueOrDefault();
        return _blendMode.HasValue;
    }

    public static SKBlender CreateBlendMode(SKBlendMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return new SKBlender(mode);
    }

    public static SKBlender? CreateArithmetic(
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePMColor)
    {
        if (!float.IsFinite(k1) ||
            !float.IsFinite(k2) ||
            !float.IsFinite(k3) ||
            !float.IsFinite(k4))
        {
            return null;
        }

        return new SKBlender(new ArithmeticBlend(k1, k2, k3, k4, enforcePMColor));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    internal readonly record struct ArithmeticBlend(
        float K1,
        float K2,
        float K3,
        float K4,
        bool EnforcePremul);
}
