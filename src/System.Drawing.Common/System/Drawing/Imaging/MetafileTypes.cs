using System.Runtime.InteropServices;

namespace System.Drawing.Imaging;

public enum EmfType
{
    EmfOnly = 3,
    EmfPlusOnly = 4,
    EmfPlusDual = 5
}

public enum MetafileFrameUnit
{
    Pixel = GraphicsUnit.Pixel,
    Point = GraphicsUnit.Point,
    Inch = GraphicsUnit.Inch,
    Document = GraphicsUnit.Document,
    Millimeter = GraphicsUnit.Millimeter,
    GdiCompatible = 7
}

public enum MetafileType
{
    Invalid = 0,
    Wmf = 1,
    WmfPlaceable = 2,
    Emf = 3,
    EmfPlusOnly = 4,
    EmfPlusDual = 5
}

public delegate void PlayRecordCallback(
    EmfPlusRecordType recordType,
    int flags,
    int dataSize,
    IntPtr recordData);

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public sealed class MetaHeader
{
    public short Type { get; set; }
    public short HeaderSize { get; set; }
    public short Version { get; set; }
    public int Size { get; set; }
    public short NoObjects { get; set; }
    public int MaxRecord { get; set; }
    public short NoParameters { get; set; }

    internal MetaHeader CloneHeader() => new()
    {
        Type = Type,
        HeaderSize = HeaderSize,
        Version = Version,
        Size = Size,
        NoObjects = NoObjects,
        MaxRecord = MaxRecord,
        NoParameters = NoParameters
    };
}

[StructLayout(LayoutKind.Sequential)]
public sealed class MetafileHeader
{
    private readonly MetafileType _type;
    private readonly int _metafileSize;
    private readonly int _version;
    private readonly float _dpiX;
    private readonly float _dpiY;
    private readonly Rectangle _bounds;
    private readonly MetaHeader? _wmfHeader;
    private readonly int _emfPlusHeaderSize;
    private readonly int _logicalDpiX;
    private readonly int _logicalDpiY;
    private readonly bool _isDisplay;

    internal MetafileHeader(
        MetafileType type,
        int metafileSize,
        int version,
        float dpiX,
        float dpiY,
        Rectangle bounds,
        MetaHeader? wmfHeader,
        int emfPlusHeaderSize,
        int logicalDpiX,
        int logicalDpiY,
        bool isDisplay)
    {
        _type = type;
        _metafileSize = metafileSize;
        _version = version;
        _dpiX = dpiX;
        _dpiY = dpiY;
        _bounds = bounds;
        _wmfHeader = wmfHeader?.CloneHeader();
        _emfPlusHeaderSize = emfPlusHeaderSize;
        _logicalDpiX = logicalDpiX;
        _logicalDpiY = logicalDpiY;
        _isDisplay = isDisplay;
    }

    public MetafileType Type => _type;
    public int MetafileSize => _metafileSize;
    public int Version => _version;
    public float DpiX => _dpiX;
    public float DpiY => _dpiY;
    public Rectangle Bounds => _bounds;
    public bool IsWmf() => _type is MetafileType.Wmf or MetafileType.WmfPlaceable;
    public bool IsWmfPlaceable() => _type == MetafileType.WmfPlaceable;
    public bool IsEmf() => _type == MetafileType.Emf;
    public bool IsEmfOrEmfPlus() => _type is MetafileType.Emf or MetafileType.EmfPlusOnly or MetafileType.EmfPlusDual;
    public bool IsEmfPlus() => _type is MetafileType.EmfPlusOnly or MetafileType.EmfPlusDual;
    public bool IsEmfPlusDual() => _type == MetafileType.EmfPlusDual;
    public bool IsEmfPlusOnly() => _type == MetafileType.EmfPlusOnly;
    public bool IsDisplay() => IsEmfPlus() && _isDisplay;
    public MetaHeader WmfHeader => IsWmf()
        ? _wmfHeader!.CloneHeader()
        : throw new ArgumentException("Parameter is not valid.");
    public int EmfPlusHeaderSize => _emfPlusHeaderSize;
    public int LogicalDpiX => _logicalDpiX;
    public int LogicalDpiY => _logicalDpiY;

    internal MetafileHeader CloneHeader() => new(
        _type,
        _metafileSize,
        _version,
        _dpiX,
        _dpiY,
        _bounds,
        _wmfHeader,
        _emfPlusHeaderSize,
        _logicalDpiX,
        _logicalDpiY,
        _isDisplay);
}

public enum EmfPlusRecordType
{
    WmfRecordBase = 0x00010000,
    WmfSetBkColor = WmfRecordBase | 0x201,
    WmfSetBkMode = WmfRecordBase | 0x102,
    WmfSetMapMode = WmfRecordBase | 0x103,
    WmfSetROP2 = WmfRecordBase | 0x104,
    WmfSetRelAbs = WmfRecordBase | 0x105,
    WmfSetPolyFillMode = WmfRecordBase | 0x106,
    WmfSetStretchBltMode = WmfRecordBase | 0x107,
    WmfSetTextCharExtra = WmfRecordBase | 0x108,
    WmfSetTextColor = WmfRecordBase | 0x209,
    WmfSetTextJustification = WmfRecordBase | 0x20A,
    WmfSetWindowOrg = WmfRecordBase | 0x20B,
    WmfSetWindowExt = WmfRecordBase | 0x20C,
    WmfSetViewportOrg = WmfRecordBase | 0x20D,
    WmfSetViewportExt = WmfRecordBase | 0x20E,
    WmfOffsetWindowOrg = WmfRecordBase | 0x20F,
    WmfScaleWindowExt = WmfRecordBase | 0x410,
    WmfOffsetViewportOrg = WmfRecordBase | 0x211,
    WmfScaleViewportExt = WmfRecordBase | 0x412,
    WmfLineTo = WmfRecordBase | 0x213,
    WmfMoveTo = WmfRecordBase | 0x214,
    WmfExcludeClipRect = WmfRecordBase | 0x415,
    WmfIntersectClipRect = WmfRecordBase | 0x416,
    WmfArc = WmfRecordBase | 0x817,
    WmfEllipse = WmfRecordBase | 0x418,
    WmfFloodFill = WmfRecordBase | 0x419,
    WmfPie = WmfRecordBase | 0x81A,
    WmfRectangle = WmfRecordBase | 0x41B,
    WmfRoundRect = WmfRecordBase | 0x61C,
    WmfPatBlt = WmfRecordBase | 0x61D,
    WmfSaveDC = WmfRecordBase | 0x01E,
    WmfSetPixel = WmfRecordBase | 0x41F,
    WmfOffsetCilpRgn = WmfRecordBase | 0x220,
    WmfTextOut = WmfRecordBase | 0x521,
    WmfBitBlt = WmfRecordBase | 0x922,
    WmfStretchBlt = WmfRecordBase | 0xB23,
    WmfPolygon = WmfRecordBase | 0x324,
    WmfPolyline = WmfRecordBase | 0x325,
    WmfEscape = WmfRecordBase | 0x626,
    WmfRestoreDC = WmfRecordBase | 0x127,
    WmfFillRegion = WmfRecordBase | 0x228,
    WmfFrameRegion = WmfRecordBase | 0x429,
    WmfInvertRegion = WmfRecordBase | 0x12A,
    WmfPaintRegion = WmfRecordBase | 0x12B,
    WmfSelectClipRegion = WmfRecordBase | 0x12C,
    WmfSelectObject = WmfRecordBase | 0x12D,
    WmfSetTextAlign = WmfRecordBase | 0x12E,
    WmfChord = WmfRecordBase | 0x830,
    WmfSetMapperFlags = WmfRecordBase | 0x231,
    WmfExtTextOut = WmfRecordBase | 0xA32,
    WmfSetDibToDev = WmfRecordBase | 0xD33,
    WmfSelectPalette = WmfRecordBase | 0x234,
    WmfRealizePalette = WmfRecordBase | 0x035,
    WmfAnimatePalette = WmfRecordBase | 0x436,
    WmfSetPalEntries = WmfRecordBase | 0x037,
    WmfPolyPolygon = WmfRecordBase | 0x538,
    WmfResizePalette = WmfRecordBase | 0x139,
    WmfDibBitBlt = WmfRecordBase | 0x940,
    WmfDibStretchBlt = WmfRecordBase | 0xB41,
    WmfDibCreatePatternBrush = WmfRecordBase | 0x142,
    WmfStretchDib = WmfRecordBase | 0xF43,
    WmfExtFloodFill = WmfRecordBase | 0x548,
    WmfSetLayout = WmfRecordBase | 0x149,
    WmfDeleteObject = WmfRecordBase | 0x1F0,
    WmfCreatePalette = WmfRecordBase | 0x0F7,
    WmfCreatePatternBrush = WmfRecordBase | 0x1F9,
    WmfCreatePenIndirect = WmfRecordBase | 0x2FA,
    WmfCreateFontIndirect = WmfRecordBase | 0x2FB,
    WmfCreateBrushIndirect = WmfRecordBase | 0x2FC,
    WmfCreateRegion = WmfRecordBase | 0x6FF,

    EmfHeader = 1,
    EmfPolyBezier = 2,
    EmfPolygon = 3,
    EmfPolyline = 4,
    EmfPolyBezierTo = 5,
    EmfPolyLineTo = 6,
    EmfPolyPolyline = 7,
    EmfPolyPolygon = 8,
    EmfSetWindowExtEx = 9,
    EmfSetWindowOrgEx = 10,
    EmfSetViewportExtEx = 11,
    EmfSetViewportOrgEx = 12,
    EmfSetBrushOrgEx = 13,
    EmfEof = 14,
    EmfSetPixelV = 15,
    EmfSetMapperFlags = 16,
    EmfSetMapMode = 17,
    EmfSetBkMode = 18,
    EmfSetPolyFillMode = 19,
    EmfSetROP2 = 20,
    EmfSetStretchBltMode = 21,
    EmfSetTextAlign = 22,
    EmfSetColorAdjustment = 23,
    EmfSetTextColor = 24,
    EmfSetBkColor = 25,
    EmfOffsetClipRgn = 26,
    EmfMoveToEx = 27,
    EmfSetMetaRgn = 28,
    EmfExcludeClipRect = 29,
    EmfIntersectClipRect = 30,
    EmfScaleViewportExtEx = 31,
    EmfScaleWindowExtEx = 32,
    EmfSaveDC = 33,
    EmfRestoreDC = 34,
    EmfSetWorldTransform = 35,
    EmfModifyWorldTransform = 36,
    EmfSelectObject = 37,
    EmfCreatePen = 38,
    EmfCreateBrushIndirect = 39,
    EmfDeleteObject = 40,
    EmfAngleArc = 41,
    EmfEllipse = 42,
    EmfRectangle = 43,
    EmfRoundRect = 44,
    EmfRoundArc = 45,
    EmfChord = 46,
    EmfPie = 47,
    EmfSelectPalette = 48,
    EmfCreatePalette = 49,
    EmfSetPaletteEntries = 50,
    EmfResizePalette = 51,
    EmfRealizePalette = 52,
    EmfExtFloodFill = 53,
    EmfLineTo = 54,
    EmfArcTo = 55,
    EmfPolyDraw = 56,
    EmfSetArcDirection = 57,
    EmfSetMiterLimit = 58,
    EmfBeginPath = 59,
    EmfEndPath = 60,
    EmfCloseFigure = 61,
    EmfFillPath = 62,
    EmfStrokeAndFillPath = 63,
    EmfStrokePath = 64,
    EmfFlattenPath = 65,
    EmfWidenPath = 66,
    EmfSelectClipPath = 67,
    EmfAbortPath = 68,
    EmfReserved069 = 69,
    EmfGdiComment = 70,
    EmfFillRgn = 71,
    EmfFrameRgn = 72,
    EmfInvertRgn = 73,
    EmfPaintRgn = 74,
    EmfExtSelectClipRgn = 75,
    EmfBitBlt = 76,
    EmfStretchBlt = 77,
    EmfMaskBlt = 78,
    EmfPlgBlt = 79,
    EmfSetDIBitsToDevice = 80,
    EmfStretchDIBits = 81,
    EmfExtCreateFontIndirect = 82,
    EmfExtTextOutA = 83,
    EmfExtTextOutW = 84,
    EmfPolyBezier16 = 85,
    EmfPolygon16 = 86,
    EmfPolyline16 = 87,
    EmfPolyBezierTo16 = 88,
    EmfPolylineTo16 = 89,
    EmfPolyPolyline16 = 90,
    EmfPolyPolygon16 = 91,
    EmfPolyDraw16 = 92,
    EmfCreateMonoBrush = 93,
    EmfCreateDibPatternBrushPt = 94,
    EmfExtCreatePen = 95,
    EmfPolyTextOutA = 96,
    EmfPolyTextOutW = 97,
    EmfSetIcmMode = 98,
    EmfCreateColorSpace = 99,
    EmfSetColorSpace = 100,
    EmfDeleteColorSpace = 101,
    EmfGlsRecord = 102,
    EmfGlsBoundedRecord = 103,
    EmfPixelFormat = 104,
    EmfDrawEscape = 105,
    EmfExtEscape = 106,
    EmfStartDoc = 107,
    EmfSmallTextOut = 108,
    EmfForceUfiMapping = 109,
    EmfNamedEscpae = 110,
    EmfColorCorrectPalette = 111,
    EmfSetIcmProfileA = 112,
    EmfSetIcmProfileW = 113,
    EmfAlphaBlend = 114,
    EmfSetLayout = 115,
    EmfTransparentBlt = 116,
    EmfReserved117 = 117,
    EmfGradientFill = 118,
    EmfSetLinkedUfis = 119,
    EmfSetTextJustification = 120,
    EmfColorMatchToTargetW = 121,
    EmfCreateColorSpaceW = 122,
    EmfMax = 122,
    EmfMin = 1,

    EmfPlusRecordBase = 0x00004000,
    Invalid = EmfPlusRecordBase,
    Header,
    EndOfFile,
    Comment,
    GetDC,
    MultiFormatStart,
    MultiFormatSection,
    MultiFormatEnd,
    Object,
    Clear,
    FillRects,
    DrawRects,
    FillPolygon,
    DrawLines,
    FillEllipse,
    DrawEllipse,
    FillPie,
    DrawPie,
    DrawArc,
    FillRegion,
    FillPath,
    DrawPath,
    FillClosedCurve,
    DrawClosedCurve,
    DrawCurve,
    DrawBeziers,
    DrawImage,
    DrawImagePoints,
    DrawString,
    SetRenderingOrigin,
    SetAntiAliasMode,
    SetTextRenderingHint,
    SetTextContrast,
    SetInterpolationMode,
    SetPixelOffsetMode,
    SetCompositingMode,
    SetCompositingQuality,
    Save,
    Restore,
    BeginContainer,
    BeginContainerNoParams,
    EndContainer,
    SetWorldTransform,
    ResetWorldTransform,
    MultiplyWorldTransform,
    TranslateWorldTransform,
    ScaleWorldTransform,
    RotateWorldTransform,
    SetPageTransform,
    ResetClip,
    SetClipRect,
    SetClipPath,
    SetClipRegion,
    OffsetClip,
    DrawDriverString,
    Total,
    Max = Total - 1,
    Min = Header
}
