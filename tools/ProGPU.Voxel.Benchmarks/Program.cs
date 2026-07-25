using System.Diagnostics;
using System.Globalization;
using ProGPU.Voxel;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

const int seed = 1337;
const int radius = 3;
const int iterations = 5;

_ = VoxelTerrainGenerator.Generate(
    new VoxelTerrainSettings(seed, ChunkRadius: 1, BuildMeshes: true));

var generationTimes = new double[iterations];
var meshingTimes = new double[iterations];
long generatedBytes = 0;
long meshingBytes = 0;
VoxelWorld? finalWorld = null;

for (var iteration = 0; iteration < iterations; iteration++)
{
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = Stopwatch.StartNew();
    var world = VoxelTerrainGenerator.Generate(
        new VoxelTerrainSettings(seed, radius, BuildMeshes: false));
    timer.Stop();
    generationTimes[iteration] = timer.Elapsed.TotalMilliseconds;
    generatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

    allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    timer.Restart();
    world.BuildAllMeshes();
    timer.Stop();
    meshingTimes[iteration] = timer.Elapsed.TotalMilliseconds;
    meshingBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    finalWorld = world;
}

Array.Sort(generationTimes);
Array.Sort(meshingTimes);

var chunks = finalWorld!.Chunks.Count;
var nonAirBlocks = finalWorld.Chunks.Sum(static chunk => chunk.NonAirCount);
var vertices = finalWorld.Chunks.Sum(static chunk => chunk.Mesh?.Vertices.Length ?? 0);
var indices = finalWorld.Chunks.Sum(static chunk => chunk.Mesh?.Indices.Length ?? 0);
var visibleFaces = finalWorld.Chunks.Sum(static chunk => chunk.Mesh?.VisibleFaceCount ?? 0);
var mergedQuads = finalWorld.Chunks.Sum(static chunk => chunk.Mesh?.MergedQuadCount ?? 0);
var vertexBytes = vertices * 24L;
var indexBytes = indices * sizeof(uint);

Console.WriteLine(
    $"Voxel benchmark seed={seed} radius={radius} iterations={iterations} " +
    $"chunks={chunks} blocks={nonAirBlocks}");
Console.WriteLine(
    $"generation medianMs={generationTimes[iterations / 2]:F3} " +
    $"minMs={generationTimes[0]:F3} " +
    $"allocatedBytes={generatedBytes / iterations}");
Console.WriteLine(
    $"meshing medianMs={meshingTimes[iterations / 2]:F3} " +
    $"minMs={meshingTimes[0]:F3} " +
    $"allocatedBytes={meshingBytes / iterations}");
Console.WriteLine(
    $"surface visibleFaces={visibleFaces} mergedQuads={mergedQuads} " +
    $"reduction={(visibleFaces == 0 ? 0d : 1d - mergedQuads / (double)visibleFaces):P2} " +
    $"vertices={vertices} indices={indices} meshBytes={vertexBytes + indexBytes}");
