using System;
using System.ComponentModel;

namespace System.Drawing.Printing;

public enum PrintAction { PrintToFile = 0, PrintToPreview = 1, PrintToPrinter = 2 }
public enum PrintRange { AllPages = 0, Selection = 1, SomePages = 2, CurrentPage = 4 }
public enum Duplex { Default = -1, Simplex = 1, Vertical = 2, Horizontal = 3 }
public enum PrinterUnit { Display = 0, ThousandthsOfAnInch = 1, HundredthsOfAMillimeter = 2, TenthsOfAMillimeter = 3 }

public static class PrinterUnitConvert
{
    public static double Convert(double value, PrinterUnit fromUnit, PrinterUnit toUnit) =>
        value * InchesPerUnit(fromUnit) / InchesPerUnit(toUnit);
    public static int Convert(int value, PrinterUnit fromUnit, PrinterUnit toUnit) =>
        checked((int)Math.Round(Convert((double)value, fromUnit, toUnit)));
    public static Point Convert(Point value, PrinterUnit fromUnit, PrinterUnit toUnit) =>
        new(Convert(value.X, fromUnit, toUnit), Convert(value.Y, fromUnit, toUnit));
    public static Size Convert(Size value, PrinterUnit fromUnit, PrinterUnit toUnit) =>
        new(Convert(value.Width, fromUnit, toUnit), Convert(value.Height, fromUnit, toUnit));
    public static Rectangle Convert(Rectangle value, PrinterUnit fromUnit, PrinterUnit toUnit) =>
        new(Convert(value.Location, fromUnit, toUnit), Convert(value.Size, fromUnit, toUnit));
    public static Margins Convert(Margins value, PrinterUnit fromUnit, PrinterUnit toUnit)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(
            Convert(value.Left, fromUnit, toUnit), Convert(value.Right, fromUnit, toUnit),
            Convert(value.Top, fromUnit, toUnit), Convert(value.Bottom, fromUnit, toUnit));
    }

    private static double InchesPerUnit(PrinterUnit unit) => unit switch
    {
        PrinterUnit.Display => 0.01d,
        PrinterUnit.ThousandthsOfAnInch => 0.001d,
        PrinterUnit.HundredthsOfAMillimeter => 0.01d / 25.4d,
        PrinterUnit.TenthsOfAMillimeter => 0.1d / 25.4d,
        _ => throw new InvalidEnumArgumentException(nameof(unit), (int)unit, typeof(PrinterUnit)),
    };
}

public class PrintEventArgs : CancelEventArgs
{
    public PrintEventArgs() : this(PrintAction.PrintToPrinter) { }
    internal PrintEventArgs(PrintAction action) { PrintAction = action; }
    public PrintAction PrintAction { get; }
}

public class PrintPageEventArgs : EventArgs
{
    public PrintPageEventArgs(Graphics graphics, Rectangle marginBounds, Rectangle pageBounds, PageSettings pageSettings)
    {
        Graphics = graphics;
        MarginBounds = marginBounds;
        PageBounds = pageBounds;
        PageSettings = pageSettings ?? throw new ArgumentNullException(nameof(pageSettings));
    }
    public bool Cancel { get; set; }
    public Graphics Graphics { get; internal set; }
    public bool HasMorePages { get; set; }
    public Rectangle MarginBounds { get; }
    public Rectangle PageBounds { get; }
    public PageSettings PageSettings { get; }
}

public delegate void PrintEventHandler(object sender, PrintEventArgs e);
public delegate void PrintPageEventHandler(object sender, PrintPageEventArgs e);
public delegate void QueryPageSettingsEventHandler(object sender, QueryPageSettingsEventArgs e);

public class QueryPageSettingsEventArgs : PrintEventArgs
{
    private PageSettings _pageSettings;

    public QueryPageSettingsEventArgs(PageSettings pageSettings) =>
        _pageSettings = pageSettings;

    public PageSettings PageSettings
    {
        get
        {
            PageSettingsChanged = true;
            return _pageSettings;
        }
        set
        {
            _pageSettings = value ?? new PageSettings();
            PageSettingsChanged = true;
        }
    }

    internal bool PageSettingsChanged { get; set; }
}

public sealed class PreviewPageInfo
{
    public PreviewPageInfo(Image image, Size physicalSize)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        PhysicalSize = physicalSize;
    }
    public Image Image { get; }
    public Size PhysicalSize { get; }
}

public abstract class PrintController
{
    public virtual bool IsPreview => false;
    public virtual void OnStartPrint(PrintDocument document, PrintEventArgs e) { }
    public virtual Graphics? OnStartPage(PrintDocument document, PrintPageEventArgs e) => e.Graphics;
    public virtual void OnEndPage(PrintDocument document, PrintPageEventArgs e) { }
    public virtual void OnEndPrint(PrintDocument document, PrintEventArgs e) { }
}

public class StandardPrintController : PrintController
{
    public override void OnStartPrint(PrintDocument document, PrintEventArgs e) =>
        throw new PlatformNotSupportedException("Printing requires a platform print adapter.");
}

public class PreviewPrintController : PrintController
{
    private readonly List<PreviewPageInfo> _pages = [];
    private Bitmap? _currentBitmap;

    public override bool IsPreview => true;
    public virtual bool UseAntiAlias { get; set; }

    public override void OnStartPrint(PrintDocument document, PrintEventArgs e)
    {
        _pages.Clear();
    }

    public override Graphics OnStartPage(PrintDocument document, PrintPageEventArgs e)
    {
        Rectangle bounds = e.PageBounds;
        _currentBitmap = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        Graphics graphics = Graphics.FromImage(_currentBitmap);
        if (UseAntiAlias)
        {
            graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        }

        return graphics;
    }

    public override void OnEndPage(PrintDocument document, PrintPageEventArgs e)
    {
        if (_currentBitmap is not null)
        {
            _pages.Add(new PreviewPageInfo(_currentBitmap, e.PageBounds.Size));
            _currentBitmap = null;
        }
    }

    public PreviewPageInfo[] GetPreviewPageInfo() => _pages.ToArray();
}

[Serializable]
public class InvalidPrinterException : SystemException
{
    public InvalidPrinterException(PrinterSettings settings)
        : base($"The printer settings for '{settings?.PrinterName}' are not valid.")
    {
    }

    [Obsolete(DiagnosticId = "SYSLIB0051")]
    protected InvalidPrinterException(
        System.Runtime.Serialization.SerializationInfo info,
        System.Runtime.Serialization.StreamingContext context)
        : base(info, context)
    {
    }
}
