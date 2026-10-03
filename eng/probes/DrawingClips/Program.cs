using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length is < 1 or > 2 ||
    (args.Length == 2 && args[1] != "--stroke-joins"))
    throw new ArgumentException("Expected an output JSON path and optional --stroke-joins.");
bool captureStrokeJoins = args.Length == 2;

string[] names = ["Replace", "Intersect", "Union", "Xor", "Exclude", "Complement",
    "save", "container", "flush", "translate", "page", "display", "rotate"];
var cases = new List<object>();
var transforms = CaptureTransforms();
foreach (string name in names)
{
    var elapsed = Stopwatch.StartNew();
    Console.WriteLine($"Drawing clip {name}: begin");
    using var bitmap = new Bitmap(64, 80);
    bitmap.SetResolution(96, 96);
    float[] bounds;
    float[] regionBounds;
    bool[] visible;
    using (Graphics graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.White);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.SetClip(new Rectangle(8, 40, 32, 24));
        graphics.TranslateTransform(8, 40);
        if (Enum.TryParse(name, out CombineMode mode))
        {
            graphics.SetClip(new Rectangle(8, 4, 32, 16), mode);
        }
        else
        {
            switch (name)
            {
                case "save":
                    GraphicsState state = graphics.Save();
                    graphics.SetClip(new Rectangle(1, 1, 2, 2));
                    graphics.Restore(state);
                    break;
                case "container":
                    GraphicsContainer container = graphics.BeginContainer();
                    graphics.SetClip(new Rectangle(1, 1, 2, 2));
                    graphics.EndContainer(container);
                    break;
                case "flush":
                    graphics.FillRectangle(Brushes.Red, -128, -128, 256, 256);
                    graphics.Flush();
                    break;
                case "translate":
                    graphics.ScaleTransform(2, 2);
                    graphics.TranslateClip(3, 4);
                    break;
                case "page":
                    graphics.ResetTransform();
                    graphics.PageUnit = GraphicsUnit.Pixel;
                    graphics.PageScale = 2;
                    graphics.IntersectClip(new Rectangle(4, 20, 16, 12));
                    break;
                case "display":
                    graphics.ResetTransform();
                    graphics.PageScale = 2;
                    graphics.IntersectClip(new Rectangle(4, 20, 16, 12));
                    break;
                case "rotate":
                    using (var matrix = new Matrix(0, 1, -1, 0, 56, 8))
                        graphics.Transform = matrix;
                    break;
                default:
                    throw new InvalidOperationException(name);
            }
        }
        bounds = Coordinates(graphics.ClipBounds);
        using Region clip = graphics.Clip;
        regionBounds = Coordinates(clip.GetBounds(graphics));
        visible = [graphics.IsVisible(4, 4), graphics.IsVisible(20, 10),
            graphics.IsVisible(new Rectangle(2, 2, 3, 3)),
            graphics.IsVisible(new Rectangle(20, 10, 3, 3))];
        graphics.FillRectangle(Brushes.Red, -128, -128, 256, 256);
    }
    byte[] pixels = new byte[64 * 80 * 4];
    Console.WriteLine($"Drawing clip {name}: recorded at {elapsed.ElapsedMilliseconds} ms; reading pixels");
    int ink = 0;
    for (int y = 0; y < 80; y++)
    {
        for (int x = 0; x < 64; x++)
        {
            Color pixel = bitmap.GetPixel(x, y);
            int index = (y * 64 + x) * 4;
            pixels[index] = pixel.R;
            pixels[index + 1] = pixel.G;
            pixels[index + 2] = pixel.B;
            pixels[index + 3] = pixel.A;
            if (pixel.ToArgb() == Color.Red.ToArgb()) ink++;
        }
    }
    // Preserve the already-read bytes for locating any platform pixel mismatch;
    // avoid another render/readback or an image-encoder dependency in the oracle.
    File.WriteAllBytes(args[0] + "." + name + ".rgba", pixels);
    ClipDiagnostics.Capture(name, args[0]);
    // Display units ignore PageScale: the two disjoint rectangles must remain
    // empty. Pixel units apply the same scale and retain their full overlap.
    if (name == "display" ? ink != 0 : ink == 0)
        throw new InvalidOperationException($"Unexpected reference ink for {name}: {ink}.");
    cases.Add(new { Name = name, Bounds = bounds, RegionBounds = regionBounds,
        Visible = visible, Ink = ink, PixelsSha256 = Convert.ToHexString(SHA256.HashData(pixels)) });
    Console.WriteLine($"Drawing clip {name}: completed at {elapsed.ElapsedMilliseconds} ms; ink={ink}");
}
var strokeJoins = captureStrokeJoins ? MiterJoins.Capture(args[0]) : new List<object>();
var assembly = typeof(Graphics).Assembly;
using var assemblyFile = File.OpenRead(assembly.Location);
File.WriteAllText(args[0], JsonSerializer.Serialize(new
{
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    Assembly = assembly.FullName, AssemblyPath = assembly.Location,
    AssemblySha256 = Convert.ToHexString(SHA256.HashData(assemblyFile)),
    Cases = cases, Transforms = transforms, StrokeJoins = strokeJoins
}, new JsonSerializerOptions { WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));

static float[] Coordinates(RectangleF rectangle) =>
    [rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height];

static List<object> CaptureTransforms()
{
    var result = new List<object>();
    foreach (string operation in new[] { "assign", "elements", "multiply", "scale", "translate", "rotate", "assign-offset", "elements-offset" })
    {
        float[] values = operation is "translate" or "rotate" or "assign-offset" or "elements-offset"
            ? [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f]
            : [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, 1e-12f, 1e30f, -2f];
        foreach (float value in values)
        {
            using var bitmap = new Bitmap(8, 8);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.TranslateTransform(3, 4);
            graphics.SetClip(new Rectangle(1, 2, 3, 4));
            string? error = null;
            using var matrix = operation.EndsWith("-offset", StringComparison.Ordinal)
                ? new Matrix(1, 0, 0, 1, value, value)
                : new Matrix(value, 0, 0, value, 0, 0);
            try
            {
                switch (operation)
                {
                    case "assign": graphics.Transform = matrix; break;
                    case "assign-offset": graphics.Transform = matrix; break;
                    case "elements": graphics.TransformElements = matrix.MatrixElements; break;
                    case "elements-offset": graphics.TransformElements = matrix.MatrixElements; break;
                    case "multiply": graphics.MultiplyTransform(matrix); break;
                    case "scale": graphics.ScaleTransform(value, value); break;
                    case "translate": graphics.TranslateTransform(value, value); break;
                    case "rotate": graphics.RotateTransform(value); break;
                }
            }
            catch (Exception exception) { error = exception.GetType().FullName; }
            using Matrix current = graphics.Transform;
            graphics.ResetTransform();
            result.Add(new { Operation = operation,
                Value = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Error = error, Matrix = current.Elements, Clip = Coordinates(graphics.ClipBounds) });
        }
    }
    return result;
}

static partial class ClipDiagnostics
{
    public static void Capture(string name, string output) => CapturePortable(name, output);
    static partial void CapturePortable(string name, string output);
}
