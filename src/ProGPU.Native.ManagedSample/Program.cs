using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

const uint width = 640;
const uint height = 360;
var outputPath = args.Length > 0
    ? args[0]
    : "progpu-native-managed-sample.ppm";

using var context = new WgpuContext();
context.Initialize(window: null);
using var target = new GpuTexture(
    context,
    width,
    height,
    TextureFormat.Rgba8Unorm,
    TextureUsage.RenderAttachment | TextureUsage.CopySrc,
    "ProGPU native managed sample target");
using var compositor = new NativeCompositor(
    context,
    TextureFormat.Rgba8Unorm);

ReadOnlySpan<NativeSolidRectangle> rectangles =
[
    new(48, 48, 180, 120, new Vector4(0.08f, 0.42f, 0.95f, 1f)),
    new(280, 64, 280, 132, new Vector4(0.98f, 0.52f, 0.08f, 1f)),
    new(128, 224, 384, 72, new Vector4(0.20f, 0.82f, 0.48f, 0.90f))
];
var metrics = compositor.Render(
    target,
    dpiScale: 1f,
    rectangles,
    new Vector4(0.02f, 0.025f, 0.04f, 1f));
var pixels = target.ReadPixels();

if (!HasExpectedColors(pixels, checked((int)width)))
{
    throw new InvalidOperationException(
        "The managed host did not observe the expected native GPU pixels.");
}
WritePpm(outputPath, pixels, checked((int)width), checked((int)height));
var info = NativeCompositor.GetInfo();
Console.WriteLine(
    $"[ProGPUNativeManaged] {info.Name}; " +
    $"vertices={metrics.VertexCount}; draws={metrics.DrawCallCount}; " +
    $"submissions={metrics.SubmissionCount}; output={outputPath}");

static bool HasExpectedColors(byte[] pixels, int width)
{
    ReadOnlySpan<byte> Pixel(int x, int y) =>
        pixels.AsSpan((y * width + x) * 4, 4);
    var blue = Pixel(100, 100);
    var amber = Pixel(360, 130);
    var background = Pixel(10, 10);
    return blue[2] > 180 && blue[0] < 100 &&
        amber[0] > 180 && amber[1] > 90 &&
        background[0] < 30 && background[1] < 30;
}

static void WritePpm(
    string path,
    byte[] pixels,
    int width,
    int height)
{
    using var output = File.Create(path);
    using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII);
    writer.Write(System.Text.Encoding.ASCII.GetBytes(
        $"P6\n{width} {height}\n255\n"));
    for (var offset = 0; offset < pixels.Length; offset += 4)
    {
        writer.Write(pixels, offset, 3);
    }
}
