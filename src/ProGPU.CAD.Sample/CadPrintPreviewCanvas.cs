using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD.Sample;

/// <summary>
/// Retained physical-page preview surface shared by the desktop and browser
/// sample hosts.
/// </summary>
public sealed class CadPrintPreviewCanvas : FrameworkElement
{
    public static readonly DependencyProperty WorkspaceBrushProperty =
        DependencyProperty.Register(
            nameof(WorkspaceBrush),
            typeof(Brush),
            typeof(CadPrintPreviewCanvas),
            new PropertyMetadata(null) { AffectsRender = true });

    public static readonly DependencyProperty PaperBrushProperty =
        DependencyProperty.Register(
            nameof(PaperBrush),
            typeof(Brush),
            typeof(CadPrintPreviewCanvas),
            new PropertyMetadata(null) { AffectsRender = true });

    public static readonly DependencyProperty PageBorderBrushProperty =
        DependencyProperty.Register(
            nameof(PageBorderBrush),
            typeof(Brush),
            typeof(CadPrintPreviewCanvas),
            new PropertyMetadata(null, OnBorderBrushChanged)
            {
                AffectsRender = true,
            });

    public static readonly DependencyProperty PrintableAreaBorderBrushProperty =
        DependencyProperty.Register(
            nameof(PrintableAreaBorderBrush),
            typeof(Brush),
            typeof(CadPrintPreviewCanvas),
            new PropertyMetadata(null, OnBorderBrushChanged)
            {
                AffectsRender = true,
            });

    private GpuPicture? _pagePicture;
    private Pen? _pageBorder;
    private Pen? _printableAreaBorder;
    private CadPrintPixelSize _pageSizePixels;
    private CadPrintPixelRect _printableAreaPixels;

    public bool HasPage => _pagePicture is not null;

    public ulong ContentGeneration { get; private set; }

    public float OutputDpi { get; private set; }

    public CadPrintPixelSize PageSizePixels => _pageSizePixels;

    public CadPrintPixelRect PrintableAreaPixels => _printableAreaPixels;

    public Rect PageViewportRect => CreatePageViewportRect();

    public Brush? WorkspaceBrush
    {
        get => GetValue(WorkspaceBrushProperty) as Brush;
        set => SetValue(WorkspaceBrushProperty, value);
    }

    public Brush? PaperBrush
    {
        get => GetValue(PaperBrushProperty) as Brush;
        set => SetValue(PaperBrushProperty, value);
    }

    public Brush? PageBorderBrush
    {
        get => GetValue(PageBorderBrushProperty) as Brush;
        set => SetValue(PageBorderBrushProperty, value);
    }

    public Brush? PrintableAreaBorderBrush
    {
        get => GetValue(PrintableAreaBorderBrushProperty) as Brush;
        set => SetValue(PrintableAreaBorderBrushProperty, value);
    }

    public CadPrintPreviewCanvas()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        WorkspaceBrush = new ThemeResourceBrush("CardBackground");
        PaperBrush = new ThemeResourceBrush("PrintPaperBackground");
        PageBorderBrush = new ThemeResourceBrush("PrintPaperBorder");
        PrintableAreaBorderBrush =
            new ThemeResourceBrush("PrintPrintableAreaBorder");
        Unloaded += (_, _) => Clear();
    }

    /// <summary>
    /// Chooses a preview output DPI whose physical page fits the current logical
    /// viewport at one page pixel per logical pixel. Fixed physical lineweights
    /// therefore remain correctly proportional instead of being rescaled by a
    /// later picture transform.
    /// </summary>
    public static float CalculateFitOutputDpi(
        Vector2 viewportSize,
        double paperWidthMillimeters = 210.0,
        double paperHeightMillimeters = 297.0,
        float inset = 24.0f,
        float maximumDpi = 300.0f)
    {
        if (!float.IsFinite(viewportSize.X) ||
            !float.IsFinite(viewportSize.Y) ||
            viewportSize.X <= inset * 2.0f ||
            viewportSize.Y <= inset * 2.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportSize),
                "The print-preview viewport must have finite space inside its inset.");
        }
        if (!double.IsFinite(paperWidthMillimeters) ||
            !double.IsFinite(paperHeightMillimeters) ||
            paperWidthMillimeters <= 0.0 ||
            paperHeightMillimeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(paperWidthMillimeters),
                "The preview paper dimensions must be finite and positive.");
        }
        if (!float.IsFinite(inset) || inset < 0.0f ||
            !float.IsFinite(maximumDpi) || maximumDpi <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inset),
                "The preview inset and maximum DPI must be finite and valid.");
        }

        const double millimetersPerInch = 25.4;
        double availableWidth = viewportSize.X - (inset * 2.0f);
        double availableHeight = viewportSize.Y - (inset * 2.0f);
        double widthDpi = availableWidth * millimetersPerInch /
            paperWidthMillimeters;
        double heightDpi = availableHeight * millimetersPerInch /
            paperHeightMillimeters;
        double dpi = Math.Min(widthDpi, heightDpi);
        if (!double.IsFinite(dpi) || dpi <= 0.0)
        {
            throw new InvalidOperationException(
                "The print-preview output DPI could not be resolved.");
        }
        return checked((float)Math.Min(dpi, maximumDpi));
    }

    /// <summary>
    /// Replaces the preview with an independently retained page picture. The
    /// caller continues to own and may immediately dispose the source plan.
    /// </summary>
    public void Load(CadPrintPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        GpuPicture replacement = plan.CreatePagePicture();
        GpuPicture? previous = _pagePicture;
        _pagePicture = replacement;
        _pageSizePixels = plan.PageSizePixels;
        _printableAreaPixels = plan.PrintableAreaPixels;
        ContentGeneration = plan.ContentGeneration;
        OutputDpi = plan.OutputDpi;
        previous?.Dispose();
        Invalidate();
    }

    public void Clear()
    {
        GpuPicture? previous = _pagePicture;
        _pagePicture = null;
        _pageSizePixels = default;
        _printableAreaPixels = default;
        ContentGeneration = 0;
        OutputDpi = 0.0f;
        previous?.Dispose();
        Invalidate();
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(
            WorkspaceBrush,
            null,
            new Rect(0, 0, Size.X, Size.Y));
        GpuPicture? pagePicture = _pagePicture;
        if (pagePicture is null || Size.X <= 0.0f || Size.Y <= 0.0f)
        {
            return;
        }

        Rect page = CreatePageViewportRect();
        context.DrawRectangle(
            PaperBrush,
            _pageBorder,
            page);
        context.PushClip(page);
        context.DrawPictureTransformed(
            pagePicture,
            Matrix4x4.CreateTranslation(page.X, page.Y, 0.0f));
        context.PopClip();
        context.DrawRectangle(
            null,
            _printableAreaBorder,
            new Rect(
                page.X + _printableAreaPixels.X,
                page.Y + _printableAreaPixels.Y,
                _printableAreaPixels.Width,
                _printableAreaPixels.Height));
    }

    private static void OnBorderBrushChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var preview = (CadPrintPreviewCanvas)dependencyObject;
        if (args.Property == PageBorderBrushProperty)
        {
            preview._pageBorder = CreateFixedPen(args.NewValue as Brush);
        }
        else
        {
            preview._printableAreaBorder = CreateFixedPen(args.NewValue as Brush);
        }
    }

    private static Pen? CreateFixedPen(Brush? brush) =>
        brush is null
            ? null
            : new Pen(
                brush,
                1,
                strokeTransformMode: PenStrokeTransformMode.Fixed);

    private Rect CreatePageViewportRect()
    {
        float width = _pageSizePixels.Width;
        float height = _pageSizePixels.Height;
        return new Rect(
            (Size.X - width) * 0.5f,
            (Size.Y - height) * 0.5f,
            width,
            height);
    }
}
