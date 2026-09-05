using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeMilViewport3DSnapshotTests
{
    [Fact]
    public void EveryWireByteIsDefinedAndEveryMutationInvalidatesOwnedBaseline()
    {
        AssertLayout<NativeSceneCamera3D>(160);
        AssertLayout<NativeImageRect>(16);
        AssertLayout<NativeSceneMesh3D>(256);
        AssertLayout<NativeSceneMesh3DVertex>(48);
        AssertLayout<NativeSceneLight3D>(80);
        AssertLayout<NativeSceneBrush>(256);
        AssertLayout<NativeSceneGradientStop>(32);
        NativeMilViewport3DScene scene = CreateScene();
        NativeMilViewport3DSnapshot snapshot = NativeMilViewport3DSnapshot.Capture(scene);
        Assert.Equal(824, snapshot.PayloadByteCount);
        Assert.True(snapshot.Matches(scene));
        MutateEachByte(scene.Meshes, scene, snapshot);
        MutateEachByte(scene.Vertices, scene, snapshot);
        MutateEachByte(scene.Indices, scene, snapshot);
        MutateEachByte(scene.Lights, scene, snapshot);
        MutateEachByte(scene.Materials, scene, snapshot);
        MutateEachByte(scene.GradientStops, scene, snapshot);
        var camera = scene.Camera;
        Span<byte> cameraBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref camera, 1));
        for (int i = 0; i < cameraBytes.Length; ++i)
        {
            cameraBytes[i] ^= 1;
            Assert.False(snapshot.Matches(scene with { Camera = camera }));
            cameraBytes[i] ^= 1;
        }
        var viewport = scene.Viewport;
        Span<byte> viewportBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref viewport, 1));
        for (int i = 0; i < viewportBytes.Length; ++i)
        {
            viewportBytes[i] ^= 1;
            Assert.False(snapshot.Matches(scene with { Viewport = viewport }));
            viewportBytes[i] ^= 1;
        }
        Assert.True(snapshot.Matches(scene));
    }

    [Fact]
    public void IdenticalReplacementArraysMatchButNullsAndLengthsDoNot()
    {
        NativeMilViewport3DScene scene = CreateScene();
        NativeMilViewport3DSnapshot snapshot = NativeMilViewport3DSnapshot.Capture(scene);
        Assert.True(snapshot.Matches(CreateScene()));
        Assert.False(snapshot.Matches(null));
        Assert.False(snapshot.Matches(scene with { Meshes = [] }));
        Assert.False(snapshot.Matches(scene with { Vertices = [] }));
        Assert.False(snapshot.Matches(scene with { Indices = [] }));
        Assert.False(snapshot.Matches(scene with { Lights = [] }));
        Assert.False(snapshot.Matches(scene with { Materials = [] }));
        Assert.False(snapshot.Matches(scene with { GradientStops = [] }));
        Assert.False(snapshot.Matches(scene with { Meshes = null! }));
        Assert.False(snapshot.Matches(scene with { Vertices = null! }));
        Assert.False(snapshot.Matches(scene with { Indices = null! }));
        Assert.False(snapshot.Matches(scene with { Lights = null! }));
        Assert.False(snapshot.Matches(scene with { Materials = null! }));
        Assert.False(snapshot.Matches(scene with { GradientStops = null! }));
        Assert.Throws<ArgumentNullException>(() => NativeMilViewport3DSnapshot.Capture(null!));
        Assert.Throws<ArgumentNullException>(() => NativeMilViewport3DSnapshot.Capture(scene with { Vertices = null! }));
    }

    [Fact]
    public void RepeatedMatchesAllocateNothing()
    {
        NativeMilViewport3DScene scene = CreateScene();
        NativeMilViewport3DSnapshot snapshot = NativeMilViewport3DSnapshot.Capture(scene);
        for (int i = 0; i < 1000; ++i) snapshot.Matches(scene);
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool matches = true;
        for (int i = 0; i < 1000; ++i) matches &= snapshot.Matches(scene);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(matches);
        Assert.Equal(0, allocated);
    }

    private static NativeMilViewport3DScene CreateScene() => new(
        new NativeSceneCamera3D(Matrix4x4.Identity, Matrix4x4.Identity, new(0, 0, 2)),
        new(0, 0, 100, 100), new NativeSceneMesh3D[1],
        new NativeSceneMesh3DVertex[4], new uint[2], new NativeSceneLight3D[1])
    {
        Materials = new NativeSceneBrush[1],
        GradientStops = new NativeSceneGradientStop[1]
    };

    private static void MutateEachByte<T>(T[] values, NativeMilViewport3DScene scene,
        NativeMilViewport3DSnapshot snapshot) where T : unmanaged
    {
        Span<byte> bytes = MemoryMarshal.AsBytes(values.AsSpan());
        for (int i = 0; i < bytes.Length; ++i)
        {
            bytes[i] ^= 1;
            Assert.False(snapshot.Matches(scene));
            bytes[i] ^= 1;
        }
        Assert.True(snapshot.Matches(scene));
    }

    // Reflection is confined to the ABI test, never the comparison/product path.
    private static void AssertLayout<T>(int size) where T : unmanaged
    {
        Assert.Equal(size, Unsafe.SizeOf<T>());
        AssertDefinedBytes(typeof(T), size);
    }

    private static void AssertDefinedBytes(Type type, int size)
    {
        if (type.IsPrimitive || type.IsEnum) return;
        var covered = new bool[size];
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            int offset = Marshal.OffsetOf(type, field.Name).ToInt32();
            Type fieldType = field.FieldType.IsEnum ? Enum.GetUnderlyingType(field.FieldType) : field.FieldType;
            int fieldSize = Marshal.SizeOf(fieldType);
            AssertDefinedBytes(fieldType, fieldSize);
            for (int i = offset; i < offset + fieldSize; ++i)
            {
                Assert.False(covered[i], $"Overlapping wire field: {type}.{field.Name}");
                covered[i] = true;
            }
        }
        Assert.All(covered, Assert.True);
    }
}
