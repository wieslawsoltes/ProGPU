using System;
using System.Collections;

namespace System.Drawing.Printing;

public class PrinterSettings : ICloneable
{
    private readonly PageSettings? _defaultPageSettings;
    public PrinterSettings() : this(false) { }
    internal PrinterSettings(bool skipDefaultPageSettings) =>
        _defaultPageSettings = skipDefaultPageSettings ? null : new PageSettings(this);

    public bool CanDuplex => false;
    public bool Collate { get; set; }
    public short Copies { get; set; } = 1;
    public PageSettings DefaultPageSettings => _defaultPageSettings is null ? new(this) : (PageSettings)_defaultPageSettings.Clone();
    public Duplex Duplex { get; set; } = Duplex.Default;
    public int FromPage { get; set; }
    public static StringCollection InstalledPrinters { get; } = new(Array.Empty<string>());
    public bool IsDefaultPrinter => false;
    public bool IsPlotter => false;
    public bool IsValid => false;
    public int LandscapeAngle => 0;
    public int MaximumCopies => 1;
    public int MaximumPage { get; set; } = 9999;
    public int MinimumPage { get; set; }
    public PaperSizeCollection PaperSizes { get; } = new(new[] { new PaperSize(PaperKind.Letter, "Letter", 850, 1100) });
    public PaperSourceCollection PaperSources { get; } = new(Array.Empty<PaperSource>());
    public string PrinterName { get; set; } = string.Empty;
    public PrinterResolutionCollection PrinterResolutions { get; } = new(Array.Empty<PrinterResolution>());
    public string PrintFileName { get; set; } = string.Empty;
    public PrintRange PrintRange { get; set; } = PrintRange.AllPages;
    public bool PrintToFile { get; set; }
    public bool SupportsColor => true;
    public int ToPage { get; set; }

    public object Clone() => new PrinterSettings
    {
        Collate = Collate, Copies = Copies, Duplex = Duplex, FromPage = FromPage,
        MaximumPage = MaximumPage, MinimumPage = MinimumPage, PrinterName = PrinterName,
        PrintFileName = PrintFileName, PrintRange = PrintRange, PrintToFile = PrintToFile, ToPage = ToPage,
    };

    public Graphics CreateMeasurementGraphics() => CreateMeasurementGraphics(DefaultPageSettings);
    public Graphics CreateMeasurementGraphics(bool honorOriginAtMargins) => CreateMeasurementGraphics();
    public Graphics CreateMeasurementGraphics(PageSettings pageSettings) => CreateMeasurementGraphics(pageSettings, false);
    public Graphics CreateMeasurementGraphics(PageSettings pageSettings, bool honorOriginAtMargins)
    {
        ArgumentNullException.ThrowIfNull(pageSettings);
        Rectangle bounds = pageSettings.Bounds;
        return Graphics.FromImage(new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)));
    }

    public IntPtr GetHdevmode() => NativeUnavailable();
    public IntPtr GetHdevmode(PageSettings pageSettings) => NativeUnavailable();
    public IntPtr GetHdevnames() => NativeUnavailable();
    public void SetHdevmode(IntPtr hdevmode) => NativeUnavailable();
    public void SetHdevnames(IntPtr hdevnames) => NativeUnavailable();
    public bool IsDirectPrintingSupported(Image image) => false;
    public bool IsDirectPrintingSupported(Imaging.ImageFormat imageFormat) => false;
    public override string ToString() => $"[PrinterSettings {PrinterName}]";
    private static IntPtr NativeUnavailable() => throw new PlatformNotSupportedException("Native printer handles require a platform adapter.");

    public sealed class StringCollection : ReadOnlyCollectionBase
    {
        internal StringCollection(IReadOnlyList<string> values) { foreach (string value in values) InnerList.Add(value); }
        public string this[int index] => (string)InnerList[index]!;
        public int IndexOf(string value) => InnerList.IndexOf(value);
        public bool Contains(string value) => InnerList.Contains(value);
        public void CopyTo(string[] array, int index) => InnerList.CopyTo(array, index);
    }
    public sealed class PaperSizeCollection : ReadOnlyCollectionBase
    {
        internal PaperSizeCollection(IReadOnlyList<PaperSize> values) { foreach (PaperSize value in values) InnerList.Add(value); }
        public PaperSize this[int index] => (PaperSize)InnerList[index]!;
        public int IndexOf(PaperSize value) => InnerList.IndexOf(value);
        public bool Contains(PaperSize value) => InnerList.Contains(value);
        public void CopyTo(PaperSize[] array, int index) => InnerList.CopyTo(array, index);
    }
    public sealed class PaperSourceCollection : ReadOnlyCollectionBase
    {
        internal PaperSourceCollection(IReadOnlyList<PaperSource> values) { foreach (PaperSource value in values) InnerList.Add(value); }
        public PaperSource this[int index] => (PaperSource)InnerList[index]!;
        public void CopyTo(PaperSource[] array, int index) => InnerList.CopyTo(array, index);
    }
    public sealed class PrinterResolutionCollection : ReadOnlyCollectionBase
    {
        internal PrinterResolutionCollection(IReadOnlyList<PrinterResolution> values) { foreach (PrinterResolution value in values) InnerList.Add(value); }
        public PrinterResolution this[int index] => (PrinterResolution)InnerList[index]!;
        public void CopyTo(PrinterResolution[] array, int index) => InnerList.CopyTo(array, index);
    }
}

public enum PaperSourceKind { Upper = 1, Lower = 2, Middle = 3, Manual = 4, Envelope = 5, ManualFeed = 6, AutomaticFeed = 7, TractorFeed = 8, SmallFormat = 9, LargeFormat = 10, LargeCapacity = 11, Cassette = 14, FormSource = 15, Custom = 257 }
public class PaperSource
{
    public PaperSource() { }
    internal PaperSource(PaperSourceKind kind, string name) { Kind = kind; RawKind = (int)kind; SourceName = name; }
    public PaperSourceKind Kind { get; }
    public int RawKind { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public override string ToString() => $"[PaperSource {SourceName} Kind={Kind}]";
}

public enum PrinterResolutionKind { High = -4, Medium = -3, Low = -2, Draft = -1, Custom = 0 }
public class PrinterResolution
{
    public PrinterResolution() { }
    internal PrinterResolution(PrinterResolutionKind kind, int x, int y) { Kind = kind; X = x; Y = y; }
    public PrinterResolutionKind Kind { get; }
    public int X { get; set; }
    public int Y { get; set; }
    public override string ToString() => $"[PrinterResolution X={X} Y={Y}]";
}
