using System.ComponentModel;

namespace System.Drawing.Printing;

public class PrintDocument : Component
{
    private PrinterSettings _printerSettings = new();
    private PageSettings _defaultPageSettings;

    public PrintDocument()
    {
        _defaultPageSettings = _printerSettings.DefaultPageSettings;
    }

    public string DocumentName { get; set; } = "document";
    public PageSettings DefaultPageSettings
    {
        get => _defaultPageSettings;
        set => _defaultPageSettings = value ?? throw new ArgumentNullException(nameof(value));
    }
    public bool OriginAtMargins { get; set; }
    public PrintController PrintController { get; set; } = new StandardPrintController();
    public PrinterSettings PrinterSettings
    {
        get => _printerSettings;
        set => _printerSettings = value ?? throw new ArgumentNullException(nameof(value));
    }

    public event PrintEventHandler? BeginPrint;
    public event PrintEventHandler? EndPrint;
    public event PrintPageEventHandler? PrintPage;
    public event QueryPageSettingsEventHandler? QueryPageSettings;

    public void Print()
    {
        var printEvent = new PrintEventArgs();
        OnBeginPrint(printEvent);
        if (printEvent.Cancel) return;

        PrintController.OnStartPrint(this, printEvent);
        try
        {
            bool more;
            do
            {
                var query = new QueryPageSettingsEventArgs((PageSettings)DefaultPageSettings.Clone());
                OnQueryPageSettings(query);
                if (query.Cancel) break;
                Rectangle pageBounds = query.PageSettings.Bounds;
                Margins margins = query.PageSettings.Margins;
                var marginBounds = new Rectangle(
                    margins.Left,
                    margins.Top,
                    Math.Max(0, pageBounds.Width - margins.Left - margins.Right),
                    Math.Max(0, pageBounds.Height - margins.Top - margins.Bottom));
                using Graphics measurementGraphics = PrinterSettings.CreateMeasurementGraphics(query.PageSettings);
                var page = new PrintPageEventArgs(measurementGraphics, marginBounds, pageBounds, query.PageSettings);
                Graphics? controllerGraphics = PrintController.OnStartPage(this, page);
                if (controllerGraphics is not null)
                {
                    page.Graphics = controllerGraphics;
                }

                try
                {
                    OnPrintPage(page);
                    PrintController.OnEndPage(this, page);
                }
                finally
                {
                    if (!ReferenceEquals(controllerGraphics, measurementGraphics))
                    {
                        controllerGraphics?.Dispose();
                    }
                }
                more = page.HasMorePages && !page.Cancel;
            }
            while (more);
        }
        finally
        {
            PrintController.OnEndPrint(this, printEvent);
            OnEndPrint(printEvent);
        }
    }

    protected virtual void OnBeginPrint(PrintEventArgs e) => BeginPrint?.Invoke(this, e);
    protected virtual void OnEndPrint(PrintEventArgs e) => EndPrint?.Invoke(this, e);
    protected virtual void OnPrintPage(PrintPageEventArgs e) => PrintPage?.Invoke(this, e);
    protected virtual void OnQueryPageSettings(QueryPageSettingsEventArgs e) => QueryPageSettings?.Invoke(this, e);

    public override string ToString() => $"[PrintDocument DocumentName={DocumentName}]";
}
