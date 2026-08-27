namespace System.Drawing.Printing;

/// <summary>
/// Holds deterministic portable page settings without querying an installed printer.
/// </summary>
public class PageSettings : ICloneable
{
    private bool _color;
    private bool _landscape;
    private Margins _margins = new();
    private PaperSize _paperSize = new(PaperKind.Letter, "Letter", 850, 1100);
    private PaperSource _paperSource = new();
    private PrinterResolution _printerResolution = new();
    private PrinterSettings _printerSettings;

    public PageSettings()
        : this(new PrinterSettings(skipDefaultPageSettings: true))
    {
    }

    public PageSettings(PrinterSettings printerSettings)
    {
        ArgumentNullException.ThrowIfNull(printerSettings);
        _printerSettings = printerSettings;
    }

    public Rectangle Bounds => _landscape
        ? new Rectangle(0, 0, _paperSize.Height, _paperSize.Width)
        : new Rectangle(0, 0, _paperSize.Width, _paperSize.Height);

    public bool Color
    {
        get => _color;
        set => _color = value;
    }

    public float HardMarginX => 0f;

    public float HardMarginY => 0f;

    public bool Landscape
    {
        get => _landscape;
        set => _landscape = value;
    }

    public Margins Margins
    {
        get => _margins;
        set => _margins = value ?? throw new ArgumentNullException(nameof(value));
    }

    public PaperSize PaperSize
    {
        get => _paperSize;
        set => _paperSize = value ?? throw new ArgumentNullException(nameof(value));
    }

    public PaperSource PaperSource
    {
        get => _paperSource;
        set => _paperSource = value ?? throw new ArgumentNullException(nameof(value));
    }

    public RectangleF PrintableArea
    {
        get
        {
            Rectangle bounds = Bounds;
            return new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }

    public PrinterResolution PrinterResolution
    {
        get => _printerResolution;
        set => _printerResolution = value ?? throw new ArgumentNullException(nameof(value));
    }

    public PrinterSettings PrinterSettings
    {
        get => _printerSettings;
        set => _printerSettings = value ?? new PrinterSettings();
    }

    public object Clone()
    {
        var clone = (PageSettings)MemberwiseClone();
        clone._margins = (Margins)_margins.Clone();
        return clone;
    }

    public void CopyToHdevmode(IntPtr hdevmode) =>
        throw new PlatformNotSupportedException("Native DEVMODE access requires a platform print adapter.");

    public void SetHdevmode(IntPtr hdevmode) =>
        throw new PlatformNotSupportedException("Native DEVMODE access requires a platform print adapter.");

    public override string ToString() =>
        $"[PageSettings: Color={Color}, Landscape={Landscape}, Margins={Margins}, PaperSize={PaperSize}, PaperSource={PaperSource}, PrinterResolution={PrinterResolution}]";
}
