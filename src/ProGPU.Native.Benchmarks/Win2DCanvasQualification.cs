using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProGPU.Backend;
using Windows.UI;

internal static class Win2DCanvasQualification
{
    private const int Width = 320;
    private const int Height = 240;

    public static void Run(string[] args)
    {
        using var context = new WgpuContext();
        context.Initialize(window: null);
        using var device = CanvasDevice.FromContext(context);
        using var target = new CanvasRenderTarget(
            device,
            Width,
            Height,
            96f);
        using var source = new CanvasRenderTarget(
            device,
            16,
            16,
            96f);
        using var checker = CanvasBitmap.CreateFromBytes(
            device,
            new byte[16],
            2,
            2,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96f,
            CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession sourceSession =
               source.CreateDrawingSession())
        {
            sourceSession.Clear(Color.FromArgb(0, 0, 0, 0));
            sourceSession.FillRectangle(
                0,
                0,
                16,
                16,
                Color.FromArgb(255, 255, 0, 255));
        }
        checker.SetPixelBytes(
        [
            128, 128, 128, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            128, 128, 128, 255
        ]);
        checker.SetPixelBytes(
            [0, 0, 0, 255],
            left: 1,
            top: 0,
            width: 1,
            height: 1);
        using var commandList = new CanvasCommandList(device);
        using (CanvasDrawingSession commandSession =
               commandList.CreateDrawingSession())
        {
            commandSession.FillRectangle(
                0,
                0,
                12,
                12,
                Color.FromArgb(255, 0, 255, 255));
            commandSession.Flush();
            commandSession.DrawLine(
                0,
                12,
                12,
                0,
                Color.FromArgb(255, 255, 255, 255),
                1);
        }
        RequireBounds(
            commandList.GetBounds(device),
            -0.5f,
            -0.5f,
            13f,
            13f,
            "command-list local bounds");
        RequireBounds(
            commandList.GetBounds(
                device,
                System.Numerics.Matrix3x2.CreateScale(2f) *
                System.Numerics.Matrix3x2.CreateTranslation(4f, 6f)),
            3f,
            5f,
            26f,
            26f,
            "command-list transformed bounds");

        using (CanvasDrawingSession drawingSession =
               target.CreateDrawingSession())
        {
            drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
            drawingSession.FillRectangle(
                8,
                8,
                40,
                30,
                Color.FromArgb(255, 255, 0, 0));
            DrawPinnedSimpleSample(drawingSession);
            drawingSession.DrawImage(
                source,
                new Windows.Foundation.Rect(120, 8, 16, 16));
            drawingSession.DrawImage(
                source,
                new Windows.Foundation.Rect(144, 8, 16, 16),
                new Windows.Foundation.Rect(0, 0, 16, 16),
                0.5f,
                CanvasImageInterpolation.NearestNeighbor);
            drawingSession.DrawImage(commandList, 176, 8);
            drawingSession.DrawImage(
                commandList,
                new Windows.Foundation.Rect(200, 48, 24, 24),
                new Windows.Foundation.Rect(0, 0, 6, 12),
                1f,
                CanvasImageInterpolation.Linear);
            using (var pathBuilder = new CanvasPathBuilder(device))
            {
                pathBuilder.BeginFigure(16, 156);
                pathBuilder.AddLine(88, 156);
                pathBuilder.AddQuadraticBezier(
                    new System.Numerics.Vector2(104, 176),
                    new System.Numerics.Vector2(88, 196));
                pathBuilder.AddCubicBezier(
                    new System.Numerics.Vector2(68, 208),
                    new System.Numerics.Vector2(36, 208),
                    new System.Numerics.Vector2(16, 196));
                pathBuilder.AddLine(16, 156);
                pathBuilder.EndFigure(CanvasFigureLoop.Closed);
                using CanvasGeometry path =
                    CanvasGeometry.CreatePath(pathBuilder);
                drawingSession.FillGeometry(
                    path,
                    Color.FromArgb(255, 0, 96, 255));
                drawingSession.DrawGeometry(
                    path,
                    Color.FromArgb(255, 255, 255, 255),
                    2);
            }
            using (CanvasGeometry clip = CanvasGeometry.CreateCircle(
                       device,
                       152,
                       180,
                       20))
            using (drawingSession.CreateLayer(1f, clip))
            {
                drawingSession.FillRectangle(
                    128,
                    156,
                    48,
                    48,
                    Color.FromArgb(255, 0, 255, 0));
            }
            using (drawingSession.CreateLayer(
                       1f,
                       new Windows.Foundation.Rect(192, 156, 24, 48)))
            {
                drawingSession.FillRectangle(
                    192,
                    156,
                    48,
                    48,
                    Color.FromArgb(255, 255, 0, 0));
            }
            using (var dashBuilder = new CanvasPathBuilder(device))
            using (var dashStyle = new CanvasStrokeStyle
                   {
                       DashStyle = CanvasDashStyle.Dash,
                       DashCap = CanvasCapStyle.Square,
                       StartCap = CanvasCapStyle.Round,
                       EndCap = CanvasCapStyle.Triangle
                   })
            {
                dashBuilder.BeginFigure(240, 220);
                dashBuilder.AddLine(304, 220);
                dashBuilder.EndFigure(CanvasFigureLoop.Open);
                using CanvasGeometry dashedLine =
                    CanvasGeometry.CreatePath(dashBuilder);
                drawingSession.DrawGeometry(
                    dashedLine,
                    Color.FromArgb(255, 0, 255, 255),
                    4,
                    dashStyle);
            }
            using (CanvasGeometry outer = CanvasGeometry.CreateRectangle(
                       device,
                       240,
                       156,
                       48,
                       48))
            using (CanvasGeometry hole = CanvasGeometry.CreateCircle(
                       device,
                       264,
                       180,
                       10))
            using (CanvasGeometry difference = outer.CombineWith(
                       hole,
                       System.Numerics.Matrix3x2.Identity,
                       CanvasGeometryCombine.Exclude))
            {
                drawingSession.FillGeometry(
                    difference,
                    Color.FromArgb(255, 160, 32, 192));
            }
            using (var linear = new CanvasLinearGradientBrush(
                       device,
                       Color.FromArgb(255, 255, 0, 0),
                       Color.FromArgb(255, 0, 0, 255))
                   {
                       StartPoint = new System.Numerics.Vector2(240, 8),
                       EndPoint = new System.Numerics.Vector2(312, 8)
                   })
            {
                drawingSession.FillRectangle(240, 8, 72, 32, linear);
            }
            using (var radial = new CanvasRadialGradientBrush(
                       device,
                       Color.FromArgb(255, 0, 255, 0),
                       Color.FromArgb(255, 0, 0, 255))
                   {
                       Center = new System.Numerics.Vector2(276, 72),
                       RadiusX = 28,
                       RadiusY = 24
                   })
            {
                drawingSession.FillEllipse(276, 72, 28, 24, radial);
            }
            using (var imageBrush = new CanvasImageBrush(device, checker)
                   {
                       ExtendX = CanvasEdgeBehavior.Wrap,
                       ExtendY = CanvasEdgeBehavior.Wrap,
                       Transform =
                           System.Numerics.Matrix3x2.CreateScale(8f) *
                           System.Numerics.Matrix3x2.CreateTranslation(8f, 72f),
                       Interpolation =
                           CanvasImageInterpolation.NearestNeighbor
                   })
            {
                drawingSession.FillRectangle(8, 72, 64, 64, imageBrush);
                RequireThrows<InvalidOperationException>(() =>
                    checker.SetPixelBytes(new byte[16]));
                checker.Dispose();
            }
            // Win2D executes DrawImage eagerly. ProGPU records until session
            // close, so its typed texture lease must preserve the source
            // without a staging readback after the public resource is closed.
            source.Dispose();
            commandList.Dispose();
        }

        ProGpuCanvasRenderMetrics first = target.LastRenderMetrics;
        using (CanvasDrawingSession drawingSession =
               target.CreateDrawingSession())
        {
            drawingSession.FillRectangle(
                60,
                8,
                40,
                30,
                Color.FromArgb(255, 0, 255, 0));
            drawingSession.DrawLine(
                8,
                60,
                100,
                60,
                Color.FromArgb(255, 0, 0, 255),
                2);
        }

        ProGpuCanvasRenderMetrics second = target.LastRenderMetrics;
        byte[] pixels = target.GetPixelBytes();
        RequirePixel(pixels, 2, 2, 0, 0, 0, 0);
        RequirePixel(pixels, 20, 20, 0, 0, 255, 255);
        RequirePixel(pixels, 75, 20, 0, 255, 0, 255);
        RequirePixel(pixels, 128, 16, 255, 0, 255, 255);
        RequirePixelInRange(
            pixels,
            152,
            16,
            minimum: 126,
            maximum: 129,
            expectedBlue: true,
            expectedRed: true);
        RequirePixel(pixels, 180, 12, 255, 255, 0, 255);
        RequirePixel(pixels, 212, 60, 255, 255, 0, 255);
        RequirePixel(pixels, 52, 180, 255, 96, 0, 255);
        RequirePixel(pixels, 152, 180, 0, 255, 0, 255);
        RequirePixel(pixels, 128, 156, 0, 0, 0, 0);
        RequirePixel(pixels, 204, 180, 0, 0, 255, 255);
        RequirePixel(pixels, 228, 180, 0, 0, 0, 0);
        RequirePixel(pixels, 244, 220, 255, 255, 0, 255);
        RequirePixel(pixels, 252, 220, 0, 0, 0, 0);
        RequirePixel(pixels, 260, 220, 255, 255, 0, 255);
        RequirePixel(pixels, 246, 180, 192, 32, 160, 255);
        RequirePixel(pixels, 264, 180, 0, 0, 0, 0);
        RequirePixel(pixels, 10, 74, 128, 128, 128, 255);
        RequirePixel(pixels, 18, 74, 0, 0, 0, 255);
        RequirePixel(pixels, 10, 82, 0, 0, 0, 255);
        RequirePixel(pixels, 18, 82, 128, 128, 128, 255);
        RequireOpaqueRedBlueInRange(
            pixels,
            276,
            24,
            minimum: 124,
            maximum: 131);
        RequireOpaqueGreenCenter(pixels, 276, 72);
        Require(
            CountYellowPixels(pixels, 90, 90, 230, 140) > 20,
            "The pinned Win2D text draw did not produce a visible yellow glyph run.");
        Require(
            first.ExecutionPath == ProGpuCanvasExecutionPath.NativeCppWebGpu &&
            second.ExecutionPath == ProGpuCanvasExecutionPath.NativeCppWebGpu &&
            first.SubmissionCount > 0 && second.SubmissionCount > 0 &&
            first.NativeDrawCount >= 14 && second.NativeDrawCount >= 1,
            $"Unexpected native Canvas metrics: first={first}, second={second}.");

        string? outputDirectory = ReadOptionalArgument(
            args,
            "--win2d-output");
        string hash = Convert.ToHexString(SHA256.HashData(pixels));
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            string stem = context.AdapterBackendType.ToString().ToLowerInvariant();
            WritePpm(
                Path.Combine(outputDirectory, $"progpu-win2d-{stem}.ppm"),
                pixels);
            File.WriteAllText(
                Path.Combine(outputDirectory, $"progpu-win2d-{stem}.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        Contract = "Win2D SimpleSample portable Canvas core",
                        Width,
                        Height,
                        Adapter = context.AdapterName,
                        Backend = context.AdapterBackendType.ToString(),
                        PixelFormat = target.Format.ToString(),
                        AlphaMode = target.AlphaMode.ToString(),
                        PixelSha256 = hash,
                        First = first,
                        Second = second
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        Console.WriteLine(
            "Qualified the source-compatible Win2D Canvas core through the " +
            $"ProGPU C++ renderer on '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}, sha256={hash}, " +
            $"draws={first.NativeDrawCount}+{second.NativeDrawCount}.");
    }

    private static void DrawPinnedSimpleSample(
        CanvasDrawingSession drawingSession)
    {
        drawingSession.DrawEllipse(
            155,
            115,
            80,
            30,
            Color.FromArgb(255, 0, 0, 0),
            3);
        drawingSession.DrawText(
            "Hello, world!",
            100,
            100,
            Color.FromArgb(255, 255, 255, 0));
    }

    private static int CountYellowPixels(
        ReadOnlySpan<byte> pixels,
        int left,
        int top,
        int right,
        int bottom)
    {
        int count = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int index = (y * Width + x) * 4;
                if (pixels[index + 2] > 128 &&
                    pixels[index + 1] > 128 &&
                    pixels[index] < 96 &&
                    pixels[index + 3] > 128)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void RequirePixel(
        ReadOnlySpan<byte> pixels,
        int x,
        int y,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        int index = (y * Width + x) * 4;
        Require(
            pixels[index] == blue &&
            pixels[index + 1] == green &&
            pixels[index + 2] == red &&
            pixels[index + 3] == alpha,
            $"Pixel ({x},{y}) was BGRA " +
            $"{pixels[index]},{pixels[index + 1]}," +
            $"{pixels[index + 2]},{pixels[index + 3]}; expected " +
            $"{blue},{green},{red},{alpha}.");
    }

    private static void RequireBounds(
        Windows.Foundation.Rect actual,
        float x,
        float y,
        float width,
        float height,
        string contract)
    {
        const double tolerance = 0.0001d;
        Require(
            Math.Abs(actual.X - x) <= tolerance &&
            Math.Abs(actual.Y - y) <= tolerance &&
            Math.Abs(actual.Width - width) <= tolerance &&
            Math.Abs(actual.Height - height) <= tolerance,
            $"Unexpected {contract}: {actual}.");
    }

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}.");
    }

    private static void RequirePixelInRange(
        ReadOnlySpan<byte> pixels,
        int x,
        int y,
        byte minimum,
        byte maximum,
        bool expectedBlue,
        bool expectedRed)
    {
        int index = (y * Width + x) * 4;
        byte blue = pixels[index];
        byte green = pixels[index + 1];
        byte red = pixels[index + 2];
        byte alpha = pixels[index + 3];
        Require(
            (!expectedBlue || blue >= minimum && blue <= maximum) &&
            (!expectedRed || red >= minimum && red <= maximum) &&
            green == 0 && alpha >= minimum && alpha <= maximum,
            $"Pixel ({x},{y}) was BGRA {blue},{green},{red},{alpha}; " +
            $"expected selected channels in [{minimum},{maximum}].");
    }

    private static void RequireOpaqueRedBlueInRange(
        ReadOnlySpan<byte> pixels,
        int x,
        int y,
        byte minimum,
        byte maximum)
    {
        int index = (y * Width + x) * 4;
        byte blue = pixels[index];
        byte green = pixels[index + 1];
        byte red = pixels[index + 2];
        byte alpha = pixels[index + 3];
        Require(
            blue >= minimum && blue <= maximum &&
            red >= minimum && red <= maximum &&
            green == 0 && alpha == byte.MaxValue,
            $"Pixel ({x},{y}) was BGRA {blue},{green},{red},{alpha}; " +
            $"expected opaque red/blue channels in [{minimum},{maximum}].");
    }

    private static void RequireOpaqueGreenCenter(
        ReadOnlySpan<byte> pixels,
        int x,
        int y)
    {
        int index = (y * Width + x) * 4;
        byte blue = pixels[index];
        byte green = pixels[index + 1];
        byte red = pixels[index + 2];
        byte alpha = pixels[index + 3];
        Require(
            blue <= 15 && green >= 240 && red == 0 && alpha == byte.MaxValue,
            $"Pixel ({x},{y}) was BGRA {blue},{green},{red},{alpha}; " +
            "expected the opaque radial-gradient green center.");
    }

    private static string? ReadOptionalArgument(
        string[] args,
        string name)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(
                    args[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WritePpm(string path, ReadOnlySpan<byte> bgra)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(
            $"P6\n{Width} {Height}\n255\n"));
        for (int index = 0; index < bgra.Length; index += 4)
        {
            writer.Write(bgra[index + 2]);
            writer.Write(bgra[index + 1]);
            writer.Write(bgra[index]);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
