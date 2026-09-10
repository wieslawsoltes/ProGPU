using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace System.Drawing.Interop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct LOGFONT
{
    internal const int FaceNameLength = 32;

    public int lfHeight;
    public int lfWidth;
    public int lfEscapement;
    public int lfOrientation;
    public int lfWeight;
    public byte lfItalic;
    public byte lfUnderline;
    public byte lfStrikeOut;
    public byte lfCharSet;
    public byte lfOutPrecision;
    public byte lfClipPrecision;
    public byte lfQuality;
    public byte lfPitchAndFamily;
    private fixed char _lfFaceName[FaceNameLength];

    [UnscopedRef]
    public Span<char> lfFaceName
        => MemoryMarshal.CreateSpan(ref _lfFaceName[0], FaceNameLength);

    internal readonly string GetFaceName()
    {
        fixed (char* faceName = _lfFaceName)
        {
            int length = 0;
            while (length < FaceNameLength && faceName[length] != '\0')
            {
                length++;
            }

            return new string(faceName, 0, length);
        }
    }

    internal void SetFaceName(string value)
    {
        Span<char> destination = lfFaceName;
        destination.Clear();
        value.AsSpan(0, Math.Min(value.Length, FaceNameLength - 1)).CopyTo(destination);
    }
}
