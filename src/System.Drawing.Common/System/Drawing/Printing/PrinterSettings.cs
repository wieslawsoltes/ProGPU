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
    public static StringCollection InstalledPrinters => new(Array.Empty<string>());
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

    public class StringCollection : ICollection, IEnumerable<string>
    {
        private readonly List<string> _items;

        public StringCollection(string[] array)
        {
            ArgumentNullException.ThrowIfNull(array);
            _items = new List<string>(array);
        }

        public int Count => _items.Count;
        public virtual string this[int index] => _items[index];
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        public int Add(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _items.Add(value);
            return _items.Count - 1;
        }

        public int IndexOf(string value) => _items.IndexOf(value);
        public bool Contains(string value) => _items.Contains(value);
        public void CopyTo(string[] array, int index) => _items.CopyTo(array, index);
        void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public IEnumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator<string> IEnumerable<string>.GetEnumerator() => _items.GetEnumerator();
    }

    public class PaperSizeCollection : ICollection
    {
        private readonly List<PaperSize> _items;

        public PaperSizeCollection(PaperSize[] array)
        {
            ArgumentNullException.ThrowIfNull(array);
            _items = new List<PaperSize>(array);
        }

        public int Count => _items.Count;
        public virtual PaperSize this[int index] => _items[index];
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        public int Add(PaperSize value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _items.Add(value);
            return _items.Count - 1;
        }

        public int IndexOf(PaperSize value) => _items.IndexOf(value);
        public bool Contains(PaperSize value) => _items.Contains(value);
        public void CopyTo(PaperSize[] array, int index) => _items.CopyTo(array, index);
        void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public IEnumerator GetEnumerator() => _items.GetEnumerator();
    }

    public class PaperSourceCollection : ICollection
    {
        private readonly List<PaperSource> _items;

        public PaperSourceCollection(PaperSource[] array)
        {
            ArgumentNullException.ThrowIfNull(array);
            _items = new List<PaperSource>(array);
        }

        public int Count => _items.Count;
        public virtual PaperSource this[int index] => _items[index];
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        public int Add(PaperSource value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _items.Add(value);
            return _items.Count - 1;
        }

        public void CopyTo(PaperSource[] array, int index) => _items.CopyTo(array, index);
        void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public IEnumerator GetEnumerator() => _items.GetEnumerator();
    }

    public class PrinterResolutionCollection : ICollection
    {
        private readonly List<PrinterResolution> _items;

        public PrinterResolutionCollection(PrinterResolution[] array)
        {
            ArgumentNullException.ThrowIfNull(array);
            _items = new List<PrinterResolution>(array);
        }

        public int Count => _items.Count;
        public virtual PrinterResolution this[int index] => _items[index];
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        public int Add(PrinterResolution value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _items.Add(value);
            return _items.Count - 1;
        }

        public void CopyTo(PrinterResolution[] array, int index) => _items.CopyTo(array, index);
        void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public IEnumerator GetEnumerator() => _items.GetEnumerator();
    }
}

public enum PaperSourceKind { Upper = 1, Lower = 2, Middle = 3, Manual = 4, Envelope = 5, ManualFeed = 6, AutomaticFeed = 7, TractorFeed = 8, SmallFormat = 9, LargeFormat = 10, LargeCapacity = 11, Cassette = 14, FormSource = 15, Custom = 257 }
public class PaperSource
{
    private PaperSourceKind _kind = PaperSourceKind.Custom;

    public PaperSource() { }
    internal PaperSource(PaperSourceKind kind, string name) { _kind = kind; SourceName = name; }
    public PaperSourceKind Kind => (int)_kind >= 256
        ? PaperSourceKind.Custom
        : _kind;
    public int RawKind { get => (int)_kind; set => _kind = (PaperSourceKind)value; }
    public string SourceName { get; set; } = string.Empty;
    public override string ToString() => $"[PaperSource {SourceName} Kind={Kind}]";
}

public enum PrinterResolutionKind { High = -4, Medium = -3, Low = -2, Draft = -1, Custom = 0 }
public class PrinterResolution
{
    private PrinterResolutionKind _kind = PrinterResolutionKind.Custom;

    public PrinterResolution() { }
    internal PrinterResolution(PrinterResolutionKind kind, int x, int y) { Kind = kind; X = x; Y = y; }
    public PrinterResolutionKind Kind
    {
        get => _kind;
        set
        {
            if (value is < PrinterResolutionKind.High or > PrinterResolutionKind.Custom)
            {
                throw new System.ComponentModel.InvalidEnumArgumentException(
                    nameof(value),
                    (int)value,
                    typeof(PrinterResolutionKind));
            }

            _kind = value;
        }
    }
    public int X { get; set; }
    public int Y { get; set; }
    public override string ToString() => Kind != PrinterResolutionKind.Custom
        ? $"[PrinterResolution {Kind}]"
        : FormattableString.Invariant($"[PrinterResolution X={X} Y={Y}]");
}
