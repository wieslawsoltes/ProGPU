using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend.Native;

// Component workload, not a frame-rate benchmark. Native library lookup uses
// the same runtime-loader search path as the product.
bool retained = args.Contains("--retained", StringComparer.Ordinal);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
int count = args.Length > 0 ? int.Parse(args[0]) : 60000;
int iterations = args.Length > 1 ? int.Parse(args[1]) : 100;
if (count < 3 || count % 3 != 0 || iterations <= 0)
    throw new ArgumentOutOfRangeException(nameof(args));
var camera = new NativeSceneCamera3D(Matrix4x4.Identity, Matrix4x4.Identity, new(0, 0, 2));
var vertices = new NativeSceneMesh3DVertex[count];
var indices = new uint[count];
for (int i = 0; i < count; ++i)
{
    vertices[i].Position = new NativePoint3D(new Vector3((i % 3 - 1) * 0.8f, i % 2 * 0.8f, 0));
    vertices[i].Normal = new NativePoint3D(Vector3.UnitZ);
    indices[i] = (uint)i;
}
var mesh = new NativeSceneMesh3D
{
    StructSize = (uint)Unsafe.SizeOf<NativeSceneMesh3D>(),
    VertexCount = (uint)count, IndexCount = (uint)count,
    ModelTransform = new NativeMatrix4x4(Matrix4x4.Identity),
    NormalTransform = new NativeMatrix4x4(Matrix4x4.Identity),
    Color = Vector4.One, Opacity = 1,
    LightDirection = new() { Z = -1, W = 1 },
    AmbientColor = new() { X = 0.2f, Y = 0.2f, Z = 0.2f, W = 1 },
    SpecularColor = new() { W = 1 },
    MaterialAmbient = new() { X = 1, Y = 1, Z = 1, W = 1 }
};
var scene = new NativeMilViewport3DScene(camera, new(0, 0, 160, 120), [mesh], vertices, indices, []);
using var channel = new NativeMilChannel();
var batch = new NativeMilBatchBuilder();
batch.CreateResource(1, NativeMilResourceType.Viewport3DVisual);
batch.CreateVisual(1);
channel.Apply(batch.ToArray());
channel.SetViewport3DScene(1, scene);
NativeMilViewport3DSnapshot snapshot = NativeMilViewport3DSnapshot.Capture(scene);
for (int warmup = 0; warmup < 128; ++warmup) Update();
var samples = new double[9];
long allocated = 0;
ulong before = channel.GetResourceGeneration(1);
for (int sample = 0; sample < samples.Length; ++sample)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < iterations; ++i) Update();
    samples[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
    allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
}
ulong generationDelta = channel.GetResourceGeneration(1) - before;
Console.WriteLine($"mode={(retained ? "retained" : "unconditional")} vertices={count} iterations={iterations} payload={snapshot.PayloadByteCount} generationDelta={generationDelta} allocatedPerUpdate={(double)allocated / (samples.Length * iterations):F3}");
Console.WriteLine("samples_us=" + string.Join(',', samples.Select(v => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
Array.Sort(samples);
// Nine batch means do not establish per-operation tail percentiles.
Console.WriteLine($"batch_mean_median_us={samples[4]:F3} batch_mean_max_us={samples[8]:F3}");
if (!snapshot.Matches(scene) || (retained && generationDelta != 0))
    throw new InvalidOperationException("The retained update changed native scene identity.");

void Update()
{
    if (!retained || !snapshot.Matches(scene)) channel.SetViewport3DScene(1, scene);
}
