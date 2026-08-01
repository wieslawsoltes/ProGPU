using System;

namespace SkiaSharp;

public enum SKColorspacePrimariesCicp
{
    Unknown = 0,
    Rec709 = 1,
    Rec470SystemM = 4,
    Rec470SystemBg = 5,
    Rec601 = 6,
    SmpteSt240 = 7,
    GenericFilm = 8,
    Rec2020 = 9,
    SmpteSt4281 = 10,
    SmpteRp4312 = 11,
    SmpteEg4321 = 12,
    ItuTH273Value22 = 22,
}

public enum SKColorspaceTransferFnCicp
{
    Unknown = 0,
    Rec709 = 1,
    Rec470SystemM = 4,
    Rec470SystemBg = 5,
    Rec601 = 6,
    SmpteSt240 = 7,
    Linear = 8,
    Iec6196624 = 11,
    Iec6196621 = 13,
    Rec202010bit = 14,
    Rec202012bit = 15,
    Pq = 16,
    SmpteSt4281 = 17,
    Hlg = 18,
}

public class SKColorSpace : SKObject
{
    private const float TransferTolerance = 0.001f;
    private const float GamutTolerance = 0.01f;

    private static readonly SKColorSpace s_srgb = new(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Srgb);
    private static readonly SKColorSpace s_srgbLinear = new(SKColorSpaceTransferFn.Linear, SKColorSpaceXyz.Srgb);

    private readonly SKColorSpaceTransferFn _transferFunction;
    private readonly SKColorSpaceXyz _xyz;

    static SKColorSpace()
    {
        s_srgb.PreventPublicDisposal();
        s_srgbLinear.PreventPublicDisposal();
    }

    private SKColorSpace(SKColorSpaceTransferFn transferFunction, SKColorSpaceXyz xyz)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _transferFunction = transferFunction;
        _xyz = xyz;
    }

    internal SKColorSpaceTransferFn TransferFunction => _transferFunction;
    internal SKColorSpaceXyz Xyz => _xyz;
    internal bool IsLinear => GammaIsLinear;

    public bool GammaIsCloseToSrgb => _transferFunction == SKColorSpaceTransferFn.Srgb;

    public bool GammaIsLinear => _transferFunction == SKColorSpaceTransferFn.Linear;

    public bool IsNumericalTransferFunction => _transferFunction.IsNumerical;

    public bool IsSrgb => ReferenceEquals(this, s_srgb);

    public SKColorSpaceTransferFn GetNumericalTransferFunction() =>
        GetNumericalTransferFunction(out var transferFunction) ? transferFunction : SKColorSpaceTransferFn.Empty;

    public bool GetNumericalTransferFunction(out SKColorSpaceTransferFn fn)
    {
        fn = _transferFunction;
        return IsNumericalTransferFunction;
    }

    public SKColorSpaceXyz ToColorSpaceXyz() =>
        ToColorSpaceXyz(out var xyz) ? xyz : SKColorSpaceXyz.Empty;

    public bool ToColorSpaceXyz(out SKColorSpaceXyz toXyzD50)
    {
        toXyzD50 = _xyz;
        return true;
    }

    public SKColorSpace ToLinearGamma() => GammaIsLinear ? this : CreateRgb(SKColorSpaceTransferFn.Linear, _xyz)!;

    public SKColorSpaceIccProfile ToProfile() => new(_transferFunction, _xyz);

    public SKColorSpace ToSrgbGamma() => GammaIsCloseToSrgb ? this : CreateRgb(SKColorSpaceTransferFn.Srgb, _xyz)!;

    public static SKColorSpace? CreateCicp(
        SKColorspacePrimariesCicp colorPrimaries,
        SKColorspaceTransferFnCicp transferCharacteristics)
    {
        if (!TryGetCicpPrimaries(colorPrimaries, out var xyz) ||
            !TryGetCicpTransferFunction(transferCharacteristics, out var transferFunction))
        {
            return null;
        }

        return CreateRgb(transferFunction, xyz);
    }

    public static SKColorSpace? CreateIcc(SKColorSpaceIccProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.TryGetColorSpace(out var transferFunction, out var xyz)
            ? CreateRgb(transferFunction, xyz)
            : null;
    }

    public static SKColorSpace? CreateIcc(SKData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var profile = SKColorSpaceIccProfile.Create(input);
        return CreateIcc(profile!);
    }

    public static SKColorSpace? CreateIcc(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return CreateIcc(input.AsSpan());
    }

    public static SKColorSpace? CreateIcc(byte[] input, long length)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (length < 0 || length > input.LongLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return CreateIcc(input.AsSpan(0, checked((int)length)));
    }

    public static SKColorSpace? CreateIcc(IntPtr input, long length)
    {
        using var profile = SKColorSpaceIccProfile.Create(input, length);
        return CreateIcc(profile!);
    }

    public static SKColorSpace? CreateIcc(ReadOnlySpan<byte> input)
    {
        using var profile = SKColorSpaceIccProfile.Create(input);
        return CreateIcc(profile!);
    }

    public static SKColorSpace? CreateRgb(
        SKColorSpaceTransferFn transferFn,
        SKColorSpaceXyz toXyzD50)
    {
        if (!transferFn.IsValid || !MatrixIsFinite(toXyzD50))
        {
            return null;
        }

        SKColorSpaceTransferFn canonicalTransfer;
        if (TransferIsClose(transferFn, SKColorSpaceTransferFn.Srgb))
        {
            if (MatrixIsClose(toXyzD50, SKColorSpaceXyz.Srgb))
            {
                return s_srgb;
            }

            canonicalTransfer = SKColorSpaceTransferFn.Srgb;
        }
        else if (TransferIsTwoDotTwo(transferFn))
        {
            canonicalTransfer = SKColorSpaceTransferFn.TwoDotTwo;
        }
        else if (TransferIsLinear(transferFn))
        {
            if (MatrixIsClose(toXyzD50, SKColorSpaceXyz.Srgb))
            {
                return s_srgbLinear;
            }

            canonicalTransfer = SKColorSpaceTransferFn.Linear;
        }
        else
        {
            canonicalTransfer = transferFn;
        }

        return new SKColorSpace(canonicalTransfer, toXyzD50);
    }

    public static SKColorSpace CreateSrgb() => s_srgb;

    public static SKColorSpace CreateSrgbLinear() => s_srgbLinear;

    public static bool Equal(SKColorSpace left, SKColorSpace right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return ReferenceEquals(left, right) ||
               left._transferFunction == right._transferFunction && left._xyz == right._xyz;
    }

    private static bool TryGetCicpTransferFunction(
        SKColorspaceTransferFnCicp value,
        out SKColorSpaceTransferFn transferFunction)
    {
        transferFunction = value switch
        {
            SKColorspaceTransferFnCicp.Rec709 or
                SKColorspaceTransferFnCicp.Rec601 or
                SKColorspaceTransferFnCicp.Iec6196624 or
                SKColorspaceTransferFnCicp.Rec202010bit or
                SKColorspaceTransferFnCicp.Rec202012bit => new SKColorSpaceTransferFn(
                    2.4f, 1f, 0f, 0f, 0f, 0f, 0f),
            SKColorspaceTransferFnCicp.Rec470SystemM => SKColorSpaceTransferFn.TwoDotTwo,
            SKColorspaceTransferFnCicp.Rec470SystemBg => new SKColorSpaceTransferFn(2.8f, 1f, 0f, 0f, 0f, 0f, 0f),
            SKColorspaceTransferFnCicp.SmpteSt240 => new SKColorSpaceTransferFn(
                2.222222222222f, 0.899626676224f, 0.100373323776f, 0.25f, 0.091286342118f, 0f, 0f),
            SKColorspaceTransferFnCicp.Linear => SKColorSpaceTransferFn.Linear,
            SKColorspaceTransferFnCicp.Iec6196621 => SKColorSpaceTransferFn.Srgb,
            SKColorspaceTransferFnCicp.Pq => SKColorSpaceTransferFn.Pq,
            SKColorspaceTransferFnCicp.SmpteSt4281 => new SKColorSpaceTransferFn(
                2.6f, 1.034080527699f, 0f, 0f, 0f, 0f, 0f),
            SKColorspaceTransferFnCicp.Hlg => SKColorSpaceTransferFn.Hlg,
            _ => SKColorSpaceTransferFn.Empty,
        };
        return value is not SKColorspaceTransferFnCicp.Unknown && transferFunction.IsValid;
    }

    private static bool TryGetCicpPrimaries(SKColorspacePrimariesCicp value, out SKColorSpaceXyz xyz)
    {
        xyz = value switch
        {
            SKColorspacePrimariesCicp.Rec709 => SKColorSpaceXyz.Srgb,
            SKColorspacePrimariesCicp.Rec470SystemM => new SKColorSpaceXyz(
                .6344442f, .1851249f, .14465098f,
                .31099266f, .5914824f, .0975251f,
                -.0011835243f, .055515826f, .77087766f),
            SKColorspacePrimariesCicp.Rec470SystemBg => new SKColorSpaceXyz(
                .45523217f, .36758426f, .1414035f,
                .23227724f, .7078136f, .05990914f,
                .014539732f, .1049015f, .7057687f),
            SKColorspacePrimariesCicp.Rec601 or SKColorspacePrimariesCicp.SmpteSt240 => new SKColorSpaceXyz(
                .4162788f, .39318287f, .15475832f,
                .22167054f, .70327383f, .07505579f,
                .013651822f, .09134888f, .72020936f),
            SKColorspacePrimariesCicp.GenericFilm => new SKColorSpaceXyz(
                .56563556f, .25386554f, .14471897f,
                .26423603f, .68506896f, .05069512f,
                -.0013230182f, .054992165f, .77154076f),
            SKColorspacePrimariesCicp.Rec2020 => SKColorSpaceXyz.Rec2020,
            SKColorspacePrimariesCicp.SmpteSt4281 => new SKColorSpaceXyz(
                .9977545f, -.0041633165f, -.029371241f,
                -.009767767f, 1.018317f, -.008549048f,
                -.007416941f, .013441581f, .8191854f),
            SKColorspacePrimariesCicp.SmpteRp4312 => new SKColorSpaceXyz(
                .48614508f, .32383734f, .1542375f,
                .22667699f, .7103254f, .06299767f,
                -.0008005162f, .043238446f, .78277206f),
            SKColorspacePrimariesCicp.SmpteEg4321 => SKColorSpaceXyz.DisplayP3,
            SKColorspacePrimariesCicp.ItuTH273Value22 => new SKColorSpaceXyz(
                .45425415f, .35330448f, .1566613f,
                .24189259f, .6736451f, .084462374f,
                .014897218f, .09064627f, .71966654f),
            _ => SKColorSpaceXyz.Empty,
        };
        return value is
            SKColorspacePrimariesCicp.Rec709 or
            SKColorspacePrimariesCicp.Rec470SystemM or
            SKColorspacePrimariesCicp.Rec470SystemBg or
            SKColorspacePrimariesCicp.Rec601 or
            SKColorspacePrimariesCicp.SmpteSt240 or
            SKColorspacePrimariesCicp.GenericFilm or
            SKColorspacePrimariesCicp.Rec2020 or
            SKColorspacePrimariesCicp.SmpteSt4281 or
            SKColorspacePrimariesCicp.SmpteRp4312 or
            SKColorspacePrimariesCicp.SmpteEg4321 or
            SKColorspacePrimariesCicp.ItuTH273Value22;
    }

    private static bool MatrixIsFinite(SKColorSpaceXyz matrix)
    {
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if (!float.IsFinite(matrix[column, row]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool MatrixIsClose(SKColorSpaceXyz left, SKColorSpaceXyz right)
    {
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if (MathF.Abs(left[column, row] - right[column, row]) >= GamutTolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TransferIsClose(SKColorSpaceTransferFn left, SKColorSpaceTransferFn right) =>
        MathF.Abs(left.G - right.G) < TransferTolerance &&
        MathF.Abs(left.A - right.A) < TransferTolerance &&
        MathF.Abs(left.B - right.B) < TransferTolerance &&
        MathF.Abs(left.C - right.C) < TransferTolerance &&
        MathF.Abs(left.D - right.D) < TransferTolerance &&
        MathF.Abs(left.E - right.E) < TransferTolerance &&
        MathF.Abs(left.F - right.F) < TransferTolerance;

    private static bool TransferIsLinear(SKColorSpaceTransferFn value)
    {
        var linearExponent =
            MathF.Abs(value.A - 1f) < TransferTolerance &&
            MathF.Abs(value.B) < TransferTolerance &&
            MathF.Abs(value.E) < TransferTolerance &&
            MathF.Abs(value.G - 1f) < TransferTolerance &&
            value.D <= 0f;
        var linearSegment =
            MathF.Abs(value.C - 1f) < TransferTolerance &&
            MathF.Abs(value.F) < TransferTolerance &&
            value.D >= 1f;
        return linearExponent || linearSegment;
    }

    private static bool TransferIsTwoDotTwo(SKColorSpaceTransferFn value) =>
        MathF.Abs(value.A - 1f) < TransferTolerance &&
        MathF.Abs(value.B) < TransferTolerance &&
        MathF.Abs(value.E) < TransferTolerance &&
        MathF.Abs(value.G - 2.2f) < TransferTolerance &&
        value.D <= 0f;

}
