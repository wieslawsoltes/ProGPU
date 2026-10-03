using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

internal static partial class Program
{
    private sealed record VisualMaskInputCase(string Name, string Mask = "transparent-solid",
        double AncestorOpacity = 0, double LocalOpacity = 1, bool Singular = false,
        bool Triangle = false, bool ScrollClip = false, bool Detached = false);

    private sealed record MaskPointHit(int Owner, double X, double Y);
    private sealed record MaskRegionHit(int Owner, IntersectionDetail Detail)
    {
        public string DetailName => Detail.ToString();
    }

    private static void CaptureVisualMaskInput(string directory, string commit, Stopwatch timer)
    {
        VisualMaskInputCase[] cases =
        [
            new("transparent-solid-zero-ancestor"),
            new("opaque-ancestor", AncestorOpacity: 1),
            new("zero-ancestor-restored"),
            new("partial-gradient", Mask: "partial-gradient"),
            new("unrelated-drawing-mask", Mask: "unrelated-drawing"),
            new("zero-local-opacity", LocalOpacity: 0),
            new("mask-removed", Mask: "none"),
            new("mask-restored"),
            new("singular-source", Singular: true),
            new("transform-restored"),
            new("triangle-source-clip", Triangle: true),
            new("outer-scroll-clip", ScrollClip: true),
            new("clips-restored"),
            new("source-detached", Detached: true),
            new("source-reattached")
        ];
        // Literal original-source coordinates, independent of renderer output,
        // source mask coverage, hit-index packets, or product geometry helpers.
        Point[] points = [new(12, 14), new(24, 24), new(35, 15), new(50, 50), new(100.25, 100.25)];
        var retained = new VisualMaskInputScene();
        var observations = new List<object>();
        var failures = new List<string>();
        int queries = 0;
        foreach (VisualMaskInputCase test in cases)
        {
            retained.Apply(test);
            int[][] expected = test.Detached || test.Singular
                ? [[9], [9], [9], [], []]
                : test.Triangle
                    ? [[9, 1], [9], [9], [], []]
                    : test.ScrollClip
                        ? [[9, 1], [9, 7, 1], [], [], []]
                        : [[9, 1], [9, 7, 1], [9], [], []];
            var replays = new List<object>();
            for (int replay = 0; replay < 3; ++replay)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(60))
                    throw new TimeoutException("Original visual-mask input exceeded the shared reference deadline.");
                VisualMaskInputScene scene = retained;
                if (replay == 2)
                {
                    scene = new VisualMaskInputScene();
                    scene.Apply(test);
                }
                scene.AssertIdentity(test);
                var results = new List<object>();
                for (int index = 0; index < points.Length; ++index)
                {
                    Point point = points[index];
                    var pointHits = new List<MaskPointHit>();
                    VisualTreeHelper.HitTest(scene.Root, null, result =>
                    {
                        if (result is not PointHitTestResult hit)
                            throw new InvalidOperationException("Original point query returned a non-point result.");
                        pointHits.Add(new(scene.Owner(hit.VisualHit), hit.PointHit.X, hit.PointHit.Y));
                        return HitTestResultBehavior.Continue;
                    }, new PointHitTestParameters(point));
                    ++queries;
                    // Each positive region lies strictly within the same source
                    // geometry as its point, away from every clip/shape edge.
                    var region = new Rect(point.X, point.Y, 1, 1);
                    var regionHits = new List<MaskRegionHit>();
                    VisualTreeHelper.HitTest(scene.Root, null, result =>
                    {
                        if (result is not GeometryHitTestResult hit)
                            throw new InvalidOperationException("Original region query returned a non-geometry result.");
                        regionHits.Add(new(scene.Owner(hit.VisualHit), hit.IntersectionDetail));
                        return HitTestResultBehavior.Continue;
                    }, new GeometryHitTestParameters(new RectangleGeometry(region)));
                    ++queries;
                    MaskPointHit[] expectedPoints = expected[index]
                        .Select(owner => new MaskPointHit(owner, point.X, point.Y)).ToArray();
                    MaskRegionHit[] expectedRegions = expected[index]
                        .Select(owner => new MaskRegionHit(owner, IntersectionDetail.FullyContains)).ToArray();
                    if (!pointHits.SequenceEqual(expectedPoints))
                        failures.Add($"{test.Name}, replay {replay}, point {index}: exact source owners/order/coordinates differ.");
                    if (!regionHits.SequenceEqual(expectedRegions))
                        failures.Add($"{test.Name}, replay {replay}, region {index}: exact source owners/order/intersection details differ.");
                    results.Add(new { Point = point, Region = region, PointHits = pointHits,
                        RegionHits = regionHits, ExpectedPointHits = expectedPoints, ExpectedRegionHits = expectedRegions });
                }
                scene.AssertIdentity(test);
                replays.Add(new { Replay = replay, IndependentSource = replay == 2,
                    ActualAncestorOpacity = scene.Root.Opacity, ActualLocalOpacity = scene.Masked.Opacity,
                    Results = results });
            }
            observations.Add(new { Input = test, Replays = replays });
        }
        if (observations.Count != 15 || queries != 450)
            throw new InvalidOperationException("Original visual-mask input inventory changed.");
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, CaseCount = cases.Length, ReplaysPerCase = 3, QueryCount = queries,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            PresentationIdentity = typeof(VisualTreeHelper).Assembly.FullName,
            PresentationCore = FileIdentity(typeof(VisualTreeHelper).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            Source = new { Root = 10, Masked = 1, Child = 7, Sibling = 9,
                OwnBounds = new Rect(8, 10, 32, 24), ChildBounds = new Rect(20, 20, 16, 16),
                LocalClip = new Rect(10, 12, 20, 18), MaskDrawingBounds = new Rect(100, 100, 1, 1),
                ChildOrder = new[] { 1, 9 }, MaskedChildOrder = new[] { 7 } },
            Observations = observations, Failures = failures, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            Qualification = "Original VisualTreeHelper point/geometry source semantics only. No pixels, GPU input index, desktop routing, package or application qualification."
        };
        // Preserve complete original results before reporting an assertion failure.
        using (var file = new FileStream(Path.Combine(directory, "visual-mask-input.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("Original visual-mask input: 15 states, 45 replays, 450 point/geometry queries, 0 skipped.");
    }

    private sealed class VisualMaskInputRoot : ContainerVisual
    {
        internal void SetScrollClip(Rect? value) => VisualScrollableAreaClip = value;
    }

    private sealed class VisualMaskInputScene
    {
        internal VisualMaskInputRoot Root { get; } = new();
        internal DrawingVisual Masked { get; } = new();
        private DrawingVisual Child { get; } = new();
        private DrawingVisual Sibling { get; } = new();
        private Brush? selectedMask;
        private Geometry? selectedClip;

        internal VisualMaskInputScene()
        {
            Draw(Masked, new(8, 10, 32, 24));
            Draw(Child, new(20, 20, 16, 16));
            Draw(Sibling, new(8, 10, 32, 24));
            Masked.Children.Add(Child);
            Root.Children.Add(Masked);
            Root.Children.Add(Sibling);
        }

        internal void Apply(VisualMaskInputCase test)
        {
            // All mutation is outside hit callbacks. Retained objects and their
            // own drawings survive opacity, mask, clip and attachment changes.
            Root.Opacity = test.AncestorOpacity;
            Masked.Opacity = test.LocalOpacity;
            Masked.Transform = test.Singular ? new ScaleTransform(0, 1) : Transform.Identity;
            selectedMask = test.Mask switch
            {
                "transparent-solid" => Brushes.Transparent,
                "partial-gradient" => new LinearGradientBrush(Colors.Transparent,
                    Color.FromArgb(128, 255, 255, 255), new Point(0, 0), new Point(1, 0)),
                "unrelated-drawing" => new DrawingBrush(new GeometryDrawing(Brushes.Transparent, null,
                    new RectangleGeometry(new Rect(100, 100, 1, 1))))
                {
                    ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(100, 100, 1, 1), Viewport = new Rect(100, 100, 1, 1),
                    TileMode = TileMode.None, Stretch = Stretch.Fill
                },
                "none" => null,
                _ => throw new InvalidOperationException("Unknown mask input.")
            };
            Masked.OpacityMask = selectedMask;
            selectedClip = test.Triangle ? TriangleClip() : new RectangleGeometry(new Rect(10, 12, 20, 18));
            Masked.Clip = selectedClip;
            Root.SetScrollClip(test.ScrollClip ? new Rect(10, 12, 20, 18) : null);
            bool attached = Root.Children.Contains(Masked);
            if (test.Detached && attached) Root.Children.Remove(Masked);
            else if (!test.Detached && !attached) Root.Children.Insert(0, Masked);
        }

        internal int Owner(DependencyObject visual) => ReferenceEquals(visual, Masked) ? 1 :
            ReferenceEquals(visual, Child) ? 7 : ReferenceEquals(visual, Sibling) ? 9 :
            ReferenceEquals(visual, Root) ? 10 : -1;

        internal void AssertIdentity(VisualMaskInputCase test)
        {
            if (Root.Children.Count != (test.Detached ? 1 : 2) ||
                !ReferenceEquals(Root.Children[test.Detached ? 0 : 1], Sibling) ||
                (!test.Detached && !ReferenceEquals(Root.Children[0], Masked)) ||
                Masked.Children.Count != 1 || !ReferenceEquals(Masked.Children[0], Child) ||
                !ReferenceEquals(VisualTreeHelper.GetParent(Child), Masked) ||
                !ReferenceEquals(VisualTreeHelper.GetParent(Masked), test.Detached ? null : Root) ||
                !ReferenceEquals(Masked.OpacityMask, selectedMask) || !ReferenceEquals(Masked.Clip, selectedClip) ||
                Root.Opacity != test.AncestorOpacity || Masked.Opacity != test.LocalOpacity)
                throw new InvalidOperationException("Original retained mask source identity changed.");
        }

        private static void Draw(DrawingVisual visual, Rect rectangle)
        {
            using DrawingContext drawing = visual.RenderOpen();
            drawing.DrawRectangle(Brushes.White, null, rectangle);
        }

        private static Geometry TriangleClip()
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext writer = geometry.Open())
            {
                writer.BeginFigure(new Point(10, 12), true, true);
                writer.LineTo(new Point(30, 12), true, false);
                writer.LineTo(new Point(10, 30), true, false);
            }
            geometry.Freeze();
            return geometry;
        }
    }
}
