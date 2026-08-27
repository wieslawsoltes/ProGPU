namespace System.Drawing;

public enum CopyPixelOperation
{
    Blackness = 0x00000042,
    CaptureBlt = 0x40000000,
    DestinationInvert = 0x00550009,
    MergeCopy = 0x00C000CA,
    MergePaint = 0x00BB0226,
    NoMirrorBitmap = unchecked((int)0x80000000),
    NotSourceCopy = 0x00330008,
    NotSourceErase = 0x001100A6,
    PatCopy = 0x00F00021,
    PatInvert = 0x005A0049,
    PatPaint = 0x00FB0A09,
    SourceAnd = 0x008800C6,
    SourceCopy = 0x00CC0020,
    SourceErase = 0x00440328,
    SourceInvert = 0x00660046,
    SourcePaint = 0x00EE0086,
    Whiteness = 0x00FF0062
}
