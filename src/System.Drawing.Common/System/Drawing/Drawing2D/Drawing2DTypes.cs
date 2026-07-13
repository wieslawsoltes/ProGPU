namespace System.Drawing.Drawing2D;

public enum SmoothingMode
{
    Invalid = -1,
    Default = 0,
    HighSpeed = 1,
    HighQuality = 2,
    None = 3,
    AntiAlias = 4
}

public enum InterpolationMode
{
    Invalid = -1,
    Default = 0,
    Low = 1,
    High = 2,
    Bilinear = 3,
    Bicubic = 4,
    NearestNeighbor = 5,
    HighQualityBilinear = 6,
    HighQualityBicubic = 7
}

public enum PixelOffsetMode
{
    Invalid = -1,
    Default = 0,
    HighSpeed = 1,
    HighQuality = 2,
    None = 3,
    Half = 4
}

public enum CompositingQuality
{
    Invalid = -1,
    Default = 0,
    HighSpeed = 1,
    HighQuality = 2,
    GammaCorrected = 3,
    AssumeLinear = 4
}

public enum DashStyle
{
    Solid = 0,
    Dash = 1,
    Dot = 2,
    DashDot = 3,
    DashDotDot = 4,
    Custom = 5
}

public enum DashCap
{
    Flat = 0,
    Round = 2,
    Triangle = 3
}

public enum LineCap
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3,
    NoAnchor = 0x10,
    SquareAnchor = 0x11,
    RoundAnchor = 0x12,
    DiamondAnchor = 0x13,
    ArrowAnchor = 0x14,
    AnchorMask = 0xF0,
    Custom = 0xFF
}

public enum LineJoin
{
    Miter = 0,
    Bevel = 1,
    Round = 2,
    MiterClipped = 3
}
