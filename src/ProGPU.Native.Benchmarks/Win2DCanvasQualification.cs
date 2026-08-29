using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
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
        Require(
            CountYellowPixels(pixels, 90, 90, 230, 140) > 20,
            "The pinned Win2D text draw did not produce a visible yellow glyph run.");
        Require(
            first.ExecutionPath == ProGpuCanvasExecutionPath.NativeCppWebGpu &&
            second.ExecutionPath == ProGpuCanvasExecutionPath.NativeCppWebGpu &&
            first.SubmissionCount > 0 && second.SubmissionCount > 0 &&
            first.NativeDrawCount >= 6 && second.NativeDrawCount >= 1,
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
