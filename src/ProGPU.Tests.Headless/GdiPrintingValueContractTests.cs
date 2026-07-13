using System.Drawing;
using System.Drawing.Printing;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class GdiPrintingValueContractTests
{
    [Fact]
    public void MarginsMatchSystemDrawingValueContract()
    {
        var defaults = new Margins();
        Assert.Equal(new Margins(100, 100, 100, 100), defaults);
        Assert.Equal("[Margins Left=100 Right=100 Top=100 Bottom=100]", defaults.ToString());

        var margins = new Margins(10, 20, 30, 40);
        var clone = Assert.IsType<Margins>(margins.Clone());
        Assert.NotSame(margins, clone);
        Assert.Equal(margins, clone);
        Assert.Throws<ArgumentOutOfRangeException>(() => margins.Left = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Margins(0, 0, -1, 0));
    }

    [Fact]
    public void PaperKindsKeepNativeWindowsIdentifiers()
    {
        Assert.Equal(0, (int)PaperKind.Custom);
        Assert.Equal(1, (int)PaperKind.Letter);
        Assert.Equal(9, (int)PaperKind.A4);
        Assert.Equal(72, (int)PaperKind.JapaneseEnvelopeKakuNumber3);
        Assert.Equal(118, (int)PaperKind.PrcEnvelopeNumber10Rotated);
    }

    [Fact]
    public void PaperSizeMatchesCustomAndRawKindContracts()
    {
        var custom = new PaperSize("Report", 827, 1169);
        Assert.Equal(PaperKind.Custom, custom.Kind);
        Assert.Equal(827, custom.Width);
        Assert.Equal(1169, custom.Height);
        Assert.Equal("[PaperSize Report Kind=Custom Height=1169 Width=827]", custom.ToString());

        var defaultConstructed = new PaperSize { RawKind = (int)PaperKind.A4 };
        defaultConstructed.Width = 827;
        defaultConstructed.Height = 1169;
        defaultConstructed.PaperName = "A4";
        Assert.Equal(PaperKind.A4, defaultConstructed.Kind);

        custom.RawKind = (int)PaperKind.Letter;
        Assert.Throws<ArgumentException>(() => custom.Width = 850);

        defaultConstructed.RawKind = 999;
        Assert.Equal(PaperKind.Custom, defaultConstructed.Kind);
        defaultConstructed.RawKind = -1;
        Assert.Equal((PaperKind)(-1), defaultConstructed.Kind);
    }

    [Fact]
    public void PageSettingsUseDeterministicLetterFallbackWithoutPrinterDiscovery()
    {
        var settings = new PageSettings();

        Assert.Equal(PaperKind.Letter, settings.PaperSize.Kind);
        Assert.Equal("Letter", settings.PaperSize.PaperName);
        Assert.Equal(new Rectangle(0, 0, 850, 1100), settings.Bounds);
        Assert.Equal(new RectangleF(0, 0, 850, 1100), settings.PrintableArea);
        Assert.Equal(new Margins(100, 100, 100, 100), settings.Margins);
        Assert.False(settings.Landscape);
        Assert.False(settings.Color);
        Assert.Equal(0f, settings.HardMarginX);
        Assert.Equal(0f, settings.HardMarginY);

        settings.Landscape = true;
        Assert.Equal(new Rectangle(0, 0, 1100, 850), settings.Bounds);
    }

    [Fact]
    public void PageSettingsCloneOwnsItsMarginsAndKeepsPaperValueState()
    {
        var settings = new PageSettings
        {
            Color = true,
            Landscape = true,
            Margins = new Margins(10, 20, 30, 40),
            PaperSize = new PaperSize("Report", 827, 1169)
        };

        var clone = Assert.IsType<PageSettings>(settings.Clone());
        Assert.NotSame(settings, clone);
        Assert.NotSame(settings.Margins, clone.Margins);
        Assert.Equal(settings.Margins, clone.Margins);
        Assert.Same(settings.PaperSize, clone.PaperSize);
        Assert.True(clone.Color);
        Assert.True(clone.Landscape);
        Assert.Equal(new Rectangle(0, 0, 1169, 827), clone.Bounds);
        Assert.Throws<ArgumentNullException>(() => clone.Margins = null!);
        Assert.Throws<ArgumentNullException>(() => clone.PaperSize = null!);
    }
}
