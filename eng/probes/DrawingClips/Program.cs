using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 1)
    throw new ArgumentException("Expected an output JSON path.");

string[] names = ["Replace", "Intersect", "Union", "Xor", "Exclude", "Complement",
    "save", "container", "flush", "translate", "page", "rotate"];
var cases = new List<object>();
foreach (string name in names)
{
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
    if (ink == 0) throw new InvalidOperationException($"No reference ink for {name}.");
    cases.Add(new { Name = name, Bounds = bounds, RegionBounds = regionBounds,
        Visible = visible, Ink = ink, PixelsSha256 = Convert.ToHexString(SHA256.HashData(pixels)) });
}
var assembly = typeof(Graphics).Assembly;
using var assemblyFile = File.OpenRead(assembly.Location);
File.WriteAllText(args[0], JsonSerializer.Serialize(new
{
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    Assembly = assembly.FullName, AssemblyPath = assembly.Location,
    AssemblySha256 = Convert.ToHexString(SHA256.HashData(assemblyFile)),
    Cases = cases
}, new JsonSerializerOptions { WriteIndented = true }));

static float[] Coordinates(RectangleF rectangle) =>
    [rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height];
