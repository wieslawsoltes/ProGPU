namespace SkiaSharp;

public enum SKCodecResult
{
    Success = 0,
    IncompleteInput = 1,
    ErrorInInput = 2,
    InvalidConversion = 3,
    InvalidScale = 4,
    InvalidParameters = 5,
    InvalidInput = 6,
    CouldNotRewind = 7,
    InternalError = 8,
    Unimplemented = 9,
}

public enum SKZeroInitialized
{
    Yes = 0,
    No = 1,
}

public enum SKCodecAnimationBlend
{
    SrcOver = 0,
    Src = 1,
}

public enum SKCodecAnimationDisposalMethod
{
    Keep = 1,
    RestoreBackgroundColor = 2,
    RestorePrevious = 3,
}

public enum SKCodecScanlineOrder
{
    TopDown = 0,
    BottomUp = 1,
}

public enum SKEncodedOrigin
{
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 3,
    BottomLeft = 4,
    LeftTop = 5,
    RightTop = 6,
    RightBottom = 7,
    LeftBottom = 8,
    Default = TopLeft,
}

public struct SKCodecOptions : IEquatable<SKCodecOptions>
{
    public static readonly SKCodecOptions Default = new(SKZeroInitialized.No);

    public SKZeroInitialized ZeroInitialized { readonly get; set; }
    public SKRectI? Subset { readonly get; set; }
    public readonly bool HasSubset => Subset.HasValue;
    public int FrameIndex { readonly get; set; }
    public int PriorFrame { readonly get; set; }

    public SKCodecOptions(SKZeroInitialized zeroInitialized)
    {
        ZeroInitialized = zeroInitialized;
        Subset = null;
        FrameIndex = 0;
        PriorFrame = -1;
    }

    public SKCodecOptions(SKZeroInitialized zeroInitialized, SKRectI subset)
    {
        ZeroInitialized = zeroInitialized;
        Subset = subset;
        FrameIndex = 0;
        PriorFrame = -1;
    }

    public SKCodecOptions(SKRectI subset)
        : this(SKZeroInitialized.No, subset)
    {
    }

    public SKCodecOptions(int frameIndex)
    {
        ZeroInitialized = SKZeroInitialized.No;
        Subset = null;
        FrameIndex = frameIndex;
        PriorFrame = -1;
    }

    public SKCodecOptions(int frameIndex, int priorFrame)
    {
        ZeroInitialized = SKZeroInitialized.No;
        Subset = null;
        FrameIndex = frameIndex;
        PriorFrame = priorFrame;
    }

    public readonly bool Equals(SKCodecOptions obj) =>
        ZeroInitialized == obj.ZeroInitialized &&
        Nullable.Equals(Subset, obj.Subset) &&
        FrameIndex == obj.FrameIndex &&
        PriorFrame == obj.PriorFrame;

    public override readonly bool Equals(object? obj) => obj is SKCodecOptions other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(ZeroInitialized, Subset, FrameIndex, PriorFrame);
    public static bool operator ==(SKCodecOptions left, SKCodecOptions right) => left.Equals(right);
    public static bool operator !=(SKCodecOptions left, SKCodecOptions right) => !left.Equals(right);
}

public struct SKCodecFrameInfo : IEquatable<SKCodecFrameInfo>
{
    public int RequiredFrame { readonly get; set; }
    public int Duration { readonly get; set; }
    public bool FullyRecieved { readonly get; set; }
    public SKAlphaType AlphaType { readonly get; set; }
    public bool HasAlphaWithinBounds { readonly get; set; }
    public SKCodecAnimationDisposalMethod DisposalMethod { readonly get; set; }
    public SKCodecAnimationBlend Blend { readonly get; set; }
    public SKRectI FrameRect { readonly get; set; }

    public readonly bool Equals(SKCodecFrameInfo obj) =>
        RequiredFrame == obj.RequiredFrame &&
        Duration == obj.Duration &&
        FullyRecieved == obj.FullyRecieved &&
        AlphaType == obj.AlphaType &&
        HasAlphaWithinBounds == obj.HasAlphaWithinBounds &&
        DisposalMethod == obj.DisposalMethod &&
        Blend == obj.Blend &&
        FrameRect.Equals(obj.FrameRect);

    public override readonly bool Equals(object? obj) => obj is SKCodecFrameInfo other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(
        RequiredFrame,
        Duration,
        FullyRecieved,
        AlphaType,
        HasAlphaWithinBounds,
        DisposalMethod,
        Blend,
        FrameRect);
    public static bool operator ==(SKCodecFrameInfo left, SKCodecFrameInfo right) => left.Equals(right);
    public static bool operator !=(SKCodecFrameInfo left, SKCodecFrameInfo right) => !left.Equals(right);
}
