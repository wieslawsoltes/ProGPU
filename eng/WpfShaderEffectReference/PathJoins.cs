using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    private sealed record PathJoinCase(bool Tiled, bool Reversal, bool Dashed)
    {
        public string Name => $"path-join-{(Tiled ? "tile" : "solid")}-{(Reversal ? "reversal" : "corner")}-{(Dashed ? "dashed" : "undashed")}";
    }

    // Authored, unexecuted original-source companion. These ordinary strokes
    // do not use ShaderEffect or depend on SoftwareOnly shader availability.
    private static void CapturePathJoins(string directory, string commit, Stopwatch timer)
    {
        var observations = new List<object>();
        var failures = new List<string>();
        int captures = 0, queries = 0;
        Exception? captureFailure = null;
        try
        {
            foreach (bool tiled in new[] { false, true })
            foreach (bool reversal in new[] { false, true })
            foreach (bool dashed in new[] { false, true })
            {
                CheckPathJoinDeadline(timer);
                var test = new PathJoinCase(tiled, reversal, dashed);
                var retained = new OriginalPathJoinScene(test);
                object input = retained.Describe();
                using (var file = new FileStream(Path.Combine(directory, test.Name + ".input.json"), FileMode.CreateNew))
                    JsonSerializer.Serialize(file, new { Case = test, Original = input }, new JsonSerializerOptions { WriteIndented = true });
                var replays = new List<object>();
                observations.Add(new { test.Name, Input = input, Replays = replays });
                byte[]? first = null;
                for (int replay = 0; replay < 3; ++replay)
                {
                    CheckPathJoinDeadline(timer);
                    OriginalPathJoinScene current = replay == 2 ? new(test) : retained;
                    object before = current.Describe();
                    var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(current.Root);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    var pixels = new byte[64 * 64 * 4];
                    bitmap.CopyPixels(pixels, 64 * 4, 0);
                    // Preserve every original capture before pixel, hit, or
                    // ownership assertions, including unsuccessful evidence.
                    SaveSamplerBitmap(directory, test.Name + $".replay-{replay}", bitmap, pixels);
                    ++captures;
                    var hits = new List<object>();
                    replays.Add(new
                    {
                        Replay = replay, SameSourceObjects = replay < 2, IndependentLiteralInstance = replay == 2,
                        Before = before, After = current.Describe(), Pixels = pixels,
                        PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)), Hits = hits
                    });
                    try { AssertPathJoinPixels(test, pixels); }
                    catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                    if (first != null && !first.AsSpan().SequenceEqual(pixels))
                        failures.Add($"{test.Name}: cold/warm/independent full BGRA bytes differ.");
                    first ??= pixels;
                    if (JsonSerializer.Serialize(before) != JsonSerializer.Serialize(current.Describe()) ||
                        JsonSerializer.Serialize(input) != JsonSerializer.Serialize(retained.Describe()))
                        failures.Add($"{test.Name}: original source metadata changed during capture.");

                    (Point Point, bool Expected)[] points = reversal
                        ? [(new(34.65, 40.25), true)]
                        : [(new(34.65, 42.65), true), (new(35.45, 43.45), false)];
                    foreach (var query in points)
                    {
                        CheckPathJoinDeadline(timer);
                        HitTestResult? result = VisualTreeHelper.HitTest(current.Stroke, query.Point);
                        bool actual = result != null;
                        bool sourceOwner = result == null || ReferenceEquals(result.VisualHit, current.Stroke);
                        ++queries;
                        hits.Add(new { X = query.Point.X, Y = query.Point.Y, query.Expected, Actual = actual, SourceOwner = sourceOwner });
                        if (actual != query.Expected || !sourceOwner)
                            failures.Add($"{test.Name}, replay {replay}: original point hit at {query.Point} differs.");
                    }
                }
            }
            if (observations.Count != 8 || captures != 24 || queries != 36)
                failures.Add("Original path-join inventory is incomplete.");
            CheckPathJoinDeadline(timer);
        }
        catch (Exception error)
        {
            captureFailure = error;
            failures.Add(error.ToString());
        }

        var receipt = new
        {
            Schema = 1, SourceCommit = commit, CaseFamily = "ordinary-wpf-curved-tiled-path-joins",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            PresentationIdentity = typeof(PathGeometry).Assembly.FullName,
            PresentationCore = FileIdentity(typeof(PathGeometry).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            ExpectedCaseCount = 8, CaseCount = observations.Count, ExpectedCaptures = 24, Captures = captures,
            PointQueries = queries, PixelWidth = 64, PixelHeight = 64, DpiX = 96, DpiY = 96,
            PixelFormat = "Pbgra32", EdgeMode = "Aliased", ShaderEffect = "none",
            SoftwareShaderAvailabilityRequired = false, Cases = observations, Failures = failures,
            QualifiedShaderCases = 0, QualifiedNativeCases = 0, QualifiedHardwareCases = 0,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            Qualification = "Original Microsoft WPF ordinary stroke pixels and point queries only; independent full-frame oracle, no tolerance. Not native provider, shader, hardware, package, or application qualification."
        };
        using (var file = new FileStream(Path.Combine(directory, failures.Count == 0
                ? "path-joins.json" : "path-joins.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (captureFailure != null) ExceptionDispatchInfo.Capture(captureFailure).Throw();
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("Original ordinary WPF path joins: 8 cases, 24 captures, 36 point queries, 0 skipped; no software-shader dependency.");
    }

    private static void CheckPathJoinDeadline(Stopwatch timer)
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(60))
            throw new TimeoutException("Original path joins exceeded the shared 60-second reference deadline.");
    }

    private static void AssertPathJoinPixels(PathJoinCase test, byte[] pixels)
    {
        if (pixels.Length != 64 * 64 * 4) throw new InvalidOperationException("Original path-join frame size changed.");
        // Literal pixel-center regions from the stated source figure. No
        // product stroker, Widen call, bounds query, or observed capture is used.
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 64; ++x)
        {
            double px = x + .5, py = y + .5;
            bool ink;
            if (test.Reversal)
                ink = px >= 12.25 && px < 36.25 && py >= 36.25 && py < 44.25;
            else
            {
                bool horizontal = px >= 12.25 && px < 32.25 && py >= 36.25 && py < 44.25;
                bool vertical = px >= 28.25 && px < 36.25 && py >= (test.Dashed ? 30.25 : 20.25) && py < 40.25;
                bool clipped = px >= 32.25 && px < 36.25 && py >= 40.25 && py < 44.25 &&
                    (px - 32.25) + (py - 40.25) <= 4 * Math.Sqrt(2);
                ink = horizontal || vertical || clipped;
            }
            for (int channel = 0; channel < 4; ++channel)
            {
                byte expected = channel == 3 || (ink && channel == 2) ? (byte)255 : (byte)0;
                byte actual = pixels[(y * 64 + x) * 4 + channel];
                if (actual != expected)
                    throw new InvalidOperationException($"{test.Name}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
            }
        }
    }

    private sealed class OriginalPathJoinScene
    {
        internal ContainerVisual Root { get; } = new();
        internal DrawingVisual Stroke { get; } = new();
        private PathGeometry Geometry { get; }
        private Pen Pen { get; }

        internal OriginalPathJoinScene(PathJoinCase test)
        {
            Brush brush = new SolidColorBrush(Colors.Red);
            if (test.Tiled)
            {
                byte[] bgra = [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255];
                var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, bgra, 8);
                image.Freeze();
                brush = new ImageBrush(image)
                {
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 8, 8),
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 2, 2),
                    TileMode = TileMode.Tile
                };
            }
            Pen = new Pen(brush, 8)
            {
                LineJoin = PenLineJoin.Miter, MiterLimit = 1,
                StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, DashCap = PenLineCap.Flat,
                DashStyle = test.Dashed ? new DashStyle([3.75, 1.25], 0) : DashStyles.Solid
            };
            var figure = new PathFigure { StartPoint = new Point(12.25, 40.25), IsFilled = false, IsClosed = false };
            figure.Segments.Add(new BezierSegment(new Point(16.25, 40.25), new Point(28.25, 40.25),
                new Point(32.25, 40.25), true) { IsSmoothJoin = false });
            figure.Segments.Add(new LineSegment(test.Reversal ? new Point(12.25, 40.25) : new Point(32.25, 20.25),
                true) { IsSmoothJoin = false });
            Geometry = new PathGeometry([figure]);
            RenderOptions.SetEdgeMode(Root, EdgeMode.Aliased);
            RenderOptions.SetEdgeMode(Stroke, EdgeMode.Aliased);
            var background = new DrawingVisual();
            using (DrawingContext drawing = background.RenderOpen())
                drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 64, 64));
            Root.Children.Add(background);
            using (DrawingContext drawing = Stroke.RenderOpen()) drawing.DrawGeometry(null, Pen, Geometry);
            Root.Children.Add(Stroke);
        }

        internal object Describe()
        {
            var figure = Geometry.Figures[0];
            var cubic = (BezierSegment)figure.Segments[0];
            var line = (LineSegment)figure.Segments[1];
            object brush;
            if (Pen.Brush is ImageBrush tile)
            {
                var image = (BitmapSource)tile.ImageSource;
                var bytes = new byte[16]; image.CopyPixels(bytes, 8, 0);
                brush = new { Kind = "ImageBrush", tile.Opacity, tile.ViewportUnits, tile.Viewport,
                    tile.ViewboxUnits, tile.Viewbox, tile.TileMode, tile.Stretch, tile.AlignmentX, tile.AlignmentY,
                    image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY, PixelFormat = image.Format.ToString(),
                    Bgra = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            }
            else brush = new { Kind = "SolidColorBrush", Pen.Brush.Opacity, Color = ((SolidColorBrush)Pen.Brush).Color.ToString() };
            return new
            {
                Brush = brush, Pen.Thickness, Pen.LineJoin, Pen.MiterLimit, Pen.StartLineCap, Pen.EndLineCap, Pen.DashCap,
                Dashes = Pen.DashStyle.Dashes.ToArray(), DashOffset = Pen.DashStyle.Offset,
                figure.StartPoint, figure.IsFilled, figure.IsClosed,
                Cubic = new { cubic.Point1, cubic.Point2, cubic.Point3, cubic.IsStroked, cubic.IsSmoothJoin },
                Line = new { line.Point, line.IsStroked, line.IsSmoothJoin },
                EdgeMode = RenderOptions.GetEdgeMode(Stroke), Fill = "none", ShaderEffect = "none"
            };
        }
    }
}
