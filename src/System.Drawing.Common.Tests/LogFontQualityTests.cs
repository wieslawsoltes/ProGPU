using System.Drawing.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class LogFontQualityTests
{
    private static int s_heightSink;

    [Fact]
    public void CanonicalLayoutAndFaceNameSpanMatchUnicodeLogFont()
    {
        Assert.Equal(92, Marshal.SizeOf<LOGFONT>());
        var logFont = new LOGFONT();
        "LibreWinForms".AsSpan().CopyTo(logFont.lfFaceName);

        Assert.Equal(32, logFont.lfFaceName.Length);
        Assert.Equal("LibreWinForms", logFont.GetFaceName());
        Assert.Equal('\0', logFont.lfFaceName[13]);
    }

    [Fact]
    public void TypedExportPreservesPortableFontIdentityAndStyle()
    {
        using FontFamily family = FontFamily.GenericMonospace;
        using var font = new Font(
            family,
            10f,
            FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout,
            GraphicsUnit.Point,
            gdiCharSet: 204,
            gdiVerticalFont: true);

        font.ToLogFont(out LOGFONT logFont);

        Assert.Equal(-13, logFont.lfHeight);
        Assert.Equal(700, logFont.lfWeight);
        Assert.Equal(1, logFont.lfItalic);
        Assert.Equal(1, logFont.lfUnderline);
        Assert.Equal(1, logFont.lfStrikeOut);
        Assert.Equal(204, logFont.lfCharSet);
        Assert.Equal($"@{family.Name}", logFont.GetFaceName());
        Assert.Equal(0, logFont.lfWidth);
        Assert.Equal(0, logFont.lfEscapement);
        Assert.Equal(0, logFont.lfOrientation);
    }

    [Fact]
    public void TypedImportMapsWeightStyleCharsetAndVerticalFace()
    {
        using FontFamily family = FontFamily.GenericMonospace;
        var logFont = new LOGFONT
        {
            lfHeight = -17,
            lfWeight = 550,
            lfItalic = 1,
            lfUnderline = 1,
            lfStrikeOut = 1,
            lfCharSet = 128,
        };
        $"@{family.Name}".AsSpan().CopyTo(logFont.lfFaceName);

        using Font font = Font.FromLogFont(in logFont);

        Assert.Equal(family.Name, font.Name);
        Assert.Equal(17f, font.Size);
        Assert.Equal(GraphicsUnit.World, font.Unit);
        Assert.Equal(FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout, font.Style);
        Assert.Equal(128, font.GdiCharSet);
        Assert.True(font.GdiVerticalFont);
    }

    [Fact]
    public void EmptyTypedLogFontIsRejectedButPortableDefaultSelectionIsAllowed()
    {
        var empty = new LOGFONT();
        Assert.Throws<ArgumentException>(() => Font.FromLogFont(in empty));

        var defaultSelection = new LOGFONT { lfOutPrecision = 7 };
        using Font font = Font.FromLogFont(in defaultSelection);
        Assert.NotEmpty(font.Name);
        Assert.Equal(12f, font.Size);
    }

    [Fact]
    public void BoxedCanonicalCompatibilityPathIsTypedAndMutable()
    {
        using FontFamily family = FontFamily.GenericSansSerif;
        using var source = new Font(family, 12f, FontStyle.Bold, GraphicsUnit.Pixel, 2, false);
        object boxed = new LOGFONT();

        source.ToLogFont(boxed);
        LOGFONT exported = Assert.IsType<LOGFONT>(boxed);
        using Font imported = Font.FromLogFont(boxed);

        Assert.Equal(-12, exported.lfHeight);
        Assert.Equal(700, exported.lfWeight);
        Assert.Equal(family.Name, exported.GetFaceName());
        Assert.Equal(source.Name, imported.Name);
        Assert.True(imported.Bold);
    }

    [Fact]
    public void LegacyArbitraryObjectLayoutsFailWithoutRuntimeReflection()
    {
        using var font = new Font(FontFamily.GenericSansSerif, 12f);
        var legacy = new LegacyLogFontShape();

        Assert.Throws<ArgumentException>(() => font.ToLogFont(legacy));
        Assert.Throws<ArgumentException>(() => Font.FromLogFont(legacy));
    }

    [Fact]
    public void ExplicitHdcOverloadsRemainTypedPlatformBoundary()
    {
        var logFont = new LOGFONT { lfHeight = -12 };
        Assert.Throws<ArgumentException>(() => Font.FromLogFont(in logFont, IntPtr.Zero));
        Assert.Throws<PlatformNotSupportedException>(() => Font.FromLogFont(in logFont, new IntPtr(1)));
        Assert.Throws<PlatformNotSupportedException>(() => Font.FromLogFont((object)logFont, new IntPtr(1)));
    }

    [Fact]
    public void GraphicsExportValidatesLifetimeAndUsesItsDpiContract()
    {
        using var bitmap = new Bitmap(8, 8);
        Graphics graphics = Graphics.FromImage(bitmap);
        using var font = new Font(FontFamily.GenericSansSerif, 9f);
        font.ToLogFont(out LOGFONT logFont, graphics);
        Assert.Equal(-12, logFont.lfHeight);

        graphics.Dispose();
        Assert.Throws<ArgumentException>(() => font.ToLogFont(out _, graphics));
    }

    [Fact]
    public void WarmedTypedExportIsAllocationFree()
    {
        using var font = new Font(FontFamily.GenericSansSerif, 12f);
        Export(font, 1_000);
        long before = GC.GetAllocatedBytesForCurrentThread();

        Export(font, 10_000);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(s_heightSink < 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Export(Font font, int count)
    {
        for (int index = 0; index < count; index++)
        {
            font.ToLogFont(out LOGFONT logFont);
            s_heightSink = logFont.lfHeight;
        }
    }

    private sealed class LegacyLogFontShape
    {
        public int Height { get; set; }
        public string FaceName { get; set; } = string.Empty;
    }
}
