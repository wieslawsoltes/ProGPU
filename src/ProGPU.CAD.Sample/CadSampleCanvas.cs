using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD.Sample;

/// <summary>Shared interactive retained CAD surface used by desktop and browser hosts.</summary>
public sealed class CadSampleCanvas : FrameworkElement
{
    private static readonly CadSnapshotOptions SnapshotOptions = new()
    {
        TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular),
    };

    private readonly Brush _background = new ThemeResourceBrush("CardBackground");
    private readonly Pen _border = new(
        new ThemeResourceBrush("ControlBorder"),
        1,
        strokeTransformMode: PenStrokeTransformMode.Fixed);
    private GpuPicture? _picture;
    private CadBounds3D _bounds;
    private Vector2 _pan;
    private Vector2 _pointerOrigin;
    private Vector2 _panOrigin;
    private float _zoom = 1;
    private bool _isPanning;
    private bool _needsFit = true;

    public CadDocumentSession? CurrentSession { get; private set; }

    public CadDocumentSnapshot? CurrentSnapshot { get; private set; }

    public CadSampleCanvas()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        Unloaded += (_, _) => ReleasePicture();
        Load(CreateRepresentativeDocument());
    }

    public void Load(CadDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session, SnapshotOptions);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        GpuPicture picture = scene.CreatePicture();
        GpuPicture? previous = _picture;
        _picture = picture;
        CurrentSession = session;
        CurrentSnapshot = snapshot;
        _bounds = snapshot.Bounds;
        _needsFit = true;
        previous?.Dispose();
        Invalidate();
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        if (_needsFit && Size.X > 0 && Size.Y > 0)
        {
            FitToView();
        }
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(_background, _border, new Rect(0, 0, Size.X, Size.Y));
        if (_picture is null || Size.X <= 0 || Size.Y <= 0)
        {
            return;
        }

        Matrix4x4 camera = new(
            _zoom, 0, 0, 0,
            0, -_zoom, 0, 0,
            0, 0, 1, 0,
            (Size.X * 0.5f) + _pan.X,
            (Size.Y * 0.5f) + _pan.Y,
            0, 1);
        context.PushClip(new Rect(0, 0, Size.X, Size.Y));
        context.DrawPicture(_picture, camera);
        context.PopClip();
    }

    public void FitToView()
    {
        if (_bounds.IsEmpty || Size.X <= 0 || Size.Y <= 0)
        {
            return;
        }

        double width = Math.Max(_bounds.Max.X - _bounds.Min.X, 1e-6);
        double height = Math.Max(_bounds.Max.Y - _bounds.Min.Y, 1e-6);
        _zoom = (float)Math.Min((Size.X * 0.88) / width, (Size.Y * 0.88) / height);
        _zoom = Math.Clamp(_zoom, 0.00001f, 1_000_000f);
        _pan = Vector2.Zero;
        _needsFit = false;
        Invalidate();
    }

    private void OnPointerPressed(object? sender, PointerRoutedEventArgs args)
    {
        if (!args.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _pointerOrigin = args.Position;
        _panOrigin = _pan;
        args.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerRoutedEventArgs args)
    {
        if (!_isPanning)
        {
            return;
        }

        _pan = _panOrigin + (args.Position - _pointerOrigin);
        Invalidate();
        args.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerRoutedEventArgs args)
    {
        _isPanning = false;
    }

    private void OnPointerWheelChanged(object? sender, PointerRoutedEventArgs args)
    {
        float factor = args.IsPreciseScrolling
            ? MathF.Exp(args.WheelDelta / 120f)
            : args.WheelDelta > 0 ? 1.15f : 0.85f;
        Vector2 center = Size * 0.5f;
        Vector2 local = (args.Position - center - _pan) / _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.00001f, 1_000_000f);
        _pan = args.Position - center - (local * _zoom);
        Invalidate();
        args.Handled = true;
    }

    private void ReleasePicture()
    {
        _picture?.Dispose();
        _picture = null;
    }

    private static CadDocumentSession CreateRepresentativeDocument()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Create representative CAD scene", document =>
        {
            document.Entities.Add(new Line(new XYZ(-80, -45, 0), new XYZ(80, -45, 0)));
            document.Entities.Add(new Circle(new XYZ(-38, 8, 0), 27));
            document.Entities.Add(new Arc(new XYZ(30, 8, 0), 30, 0.2, 5.1));
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(30, 8, 0),
                MajorAxisEndPoint = new XYZ(22, 9, 0),
                RadiusRatio = 0.38,
                StartParameter = 0.35,
                EndParameter = 5.65,
            });
            document.Entities.Add(new Solid(
                new XYZ(-9, -8, 0),
                new XYZ(7, -8, 0),
                new XYZ(7, 4, 0),
                new XYZ(-9, 4, 0)));
            document.Entities.Add(new Face3D
            {
                FirstCorner = new XYZ(-6, 12, 0),
                SecondCorner = new XYZ(9, 15, 2),
                ThirdCorner = new XYZ(4, 29, 5),
                FourthCorner = new XYZ(-11, 25, 2),
                Flags = InvisibleEdgeFlags.Third,
            });

            var polyline = new LwPolyline { IsClosed = true };
            polyline.Vertices.Add(new LwPolyline.Vertex(-72, -30));
            polyline.Vertices.Add(new LwPolyline.Vertex(-12, 42) { Bulge = -0.32 });
            polyline.Vertices.Add(new LwPolyline.Vertex(58, 35));
            polyline.Vertices.Add(new LwPolyline.Vertex(76, -22) { Bulge = 0.22 });
            document.Entities.Add(polyline);

            var spline = new Spline { Degree = 3 };
            spline.ControlPoints.AddRange([
                new XYZ(-75, -22, 0),
                new XYZ(-25, 68, 0),
                new XYZ(35, -68, 0),
                new XYZ(78, 18, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
            document.Entities.Add(spline);

            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("%%uProGPU%%u %%oCAD%%o")
            {
                Style = textStyle,
                InsertPoint = new XYZ(-34, -31, 0),
                Height = 7,
                WidthFactor = 0.9,
                ObliqueAngle = 0.08,
            });

            var block = new BlockRecord("ANALYTIC_SYMBOL");
            block.BlockEntity.BasePoint = new XYZ(5, 5, 0);
            block.Entities.Add(new Circle(new XYZ(5, 5, 0), 5));
            block.Entities.Add(new Line(new XYZ(0, 5, 0), new XYZ(10, 5, 0)));
            document.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(45, -13, 0),
                XScale = 1.7,
                YScale = 0.8,
                Rotation = 0.42,
                ColumnCount = 2,
                ColumnSpacing = 16,
            });
        });
        return session;
    }
}
