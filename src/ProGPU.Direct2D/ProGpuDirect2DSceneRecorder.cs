using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using ProGPU.Backend.Native;

namespace ProGPU.Direct2D;

/// <summary>Immutable physical target dimensions and independent horizontal/vertical DPI.</summary>
public readonly record struct ProGpuDirect2DRecorderTarget(
    uint PixelWidth, uint PixelHeight, float DpiX = 96f, float DpiY = 96f)
{
    /// <summary>Validates the descriptor without loading a native library or creating a device.</summary>
    public void Validate()
    {
        if (PixelWidth == 0U || PixelHeight == 0U ||
            !float.IsFinite(DpiX) || !float.IsFinite(DpiY) || DpiX <= 0f || DpiY <= 0f)
            throw new ArgumentOutOfRangeException(nameof(ProGpuDirect2DRecorderTarget),
                "Target dimensions and DPI must be positive, with finite DPI.");
    }

    internal NativeDirect2DTargetExtent ToNative()
    {
        Validate();
        return new NativeDirect2DTargetExtent
        {
            StructSize = (uint)Unsafe.SizeOf<NativeDirect2DTargetExtent>(),
            PixelWidth = PixelWidth,
            PixelHeight = PixelHeight,
            DpiX = DpiX,
            DpiY = DpiY
        };
    }
}

/// <summary>
/// Owns an immutable-generation native Direct2D scene recorder without creating a
/// GPU surface. The current COM provider is Windows-only. Never mutate its acquired
/// command sink concurrently with measuring/writing the stream. Target changes
/// require a new recorder and generation, not mutation of an existing recording.
/// </summary>
public sealed unsafe class ProGpuDirect2DSceneRecorder : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly ProGpuDirect2DResourceDomain _domain;

    private ProGpuDirect2DSceneRecorder(nint recorder, ulong sceneId, ulong generation,
        ProGpuDirect2DRecorderTarget? target) : base(ownsHandle: true)
    {
        _domain = new ProGpuDirect2DResourceDomain(generation);
        SceneId = sceneId;
        Generation = generation;
        Target = target;
        SetHandle(recorder);
    }

    public ulong SceneId { get; }
    public ulong Generation { get; }
    public ProGpuDirect2DRecorderTarget? Target { get; }

    public static ProGpuDirect2DSceneRecorder Create(ulong sceneId, ulong generation,
        ProGpuDirect2DRecorderTarget? target = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sceneId);
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        NativeDirect2DTargetExtent extent = target?.ToNative() ?? default;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Direct2D COM recorder provider is Windows-only.");
        if (ProGpuDirect2DNative.GetAbiVersion() != ProGpuDirect2DNative.AbiVersion)
            throw new NotSupportedException("The native Direct2D ABI does not match the managed recorder.");
        nint recorder = 0;
        int nativeHResult = 0;
        var status = target.HasValue
            ? ProGpuDirect2DNative.SceneRecorderCreateForTarget(sceneId, generation,
                &extent, null, &recorder, &nativeHResult)
            : ProGpuDirect2DNative.SceneRecorderCreate(sceneId, generation,
                null, &recorder, &nativeHResult);
        try
        {
            ThrowIfFailed("scene recorder creation", status, nativeHResult);
            if (recorder == 0)
                throw new InvalidOperationException("Recorder creation returned no owned handle.");
            return new ProGpuDirect2DSceneRecorder(recorder, sceneId, generation, target);
        }
        catch
        {
            if (recorder != 0) ProGpuDirect2DNative.SceneRecorderDestroy(recorder);
            throw;
        }
    }

    /// <summary>
    /// Returns one caller-owned ID2D1CommandSink1 reference for native COM calls or
    /// ID2D1CommandList::Stream. The returned reference has its own lifetime; it
    /// does not grant concurrent mutation permission. Balance BeginDraw/EndDraw
    /// before measuring/writing. No per-draw P/Invoke or managed delegates are added.
    /// </summary>
    public ProGpuDirect2DComReference AcquireCommandSink()
    {
        bool added = false;
        nint sink = 0;
        try
        {
            DangerousAddRef(ref added);
            int nativeHResult = 0;
            var status = ProGpuDirect2DNative.SceneRecorderGetCommandSink(handle, &sink, &nativeHResult);
            ThrowIfFailed("scene recorder command sink", status, nativeHResult);
            if (sink == 0) throw new InvalidOperationException("Recorder returned no owned command sink.");
            var owned = new ProGpuDirect2DComReference(sink, ProGpuDirect2DInterfaceKind.D2D1CommandSink1, _domain);
            sink = 0;
            return owned;
        }
        finally
        {
            if (sink != 0) ProGpuDirect2DNative.ComRelease(sink);
            if (added) DangerousRelease();
        }
    }

    public ProGpuDirect2DSceneStreamResult MeasureSceneStream() => BuildSceneStream(default, true);

    public ProGpuDirect2DSceneStreamResult WriteSceneStream(Span<byte> destination)
    {
        if (destination.IsEmpty) throw new ArgumentException("Destination must not be empty.", nameof(destination));
        return BuildSceneStream(destination, false);
    }

    private ProGpuDirect2DSceneStreamResult BuildSceneStream(Span<byte> destination, bool measure)
    {
        bool added = false;
        try
        {
            DangerousAddRef(ref added);
            var result = new ProGpuDirect2DNative.NativeSceneStreamResult
            {
                StructSize = (uint)Unsafe.SizeOf<ProGpuDirect2DNative.NativeSceneStreamResult>()
            };
            int nativeHResult = 0;
            fixed (byte* bytes = destination)
            {
                var status = ProGpuDirect2DNative.SceneRecorderBuildStream(handle, bytes,
                    (ulong)destination.Length, &result, &nativeHResult);
                if (status == ProGpuDirect2DStatus.InsufficientBuffer)
                {
                    if (measure) return result.ToManaged();
                    throw new ArgumentException($"Destination requires {result.RequiredBytes} bytes.", nameof(destination));
                }
                ThrowIfFailed("scene recorder serialization", status, nativeHResult);
                return result.ToManaged();
            }
        }
        finally
        {
            if (added) DangerousRelease();
        }
    }

    protected override bool ReleaseHandle()
    {
        ProGpuDirect2DNative.SceneRecorderDestroy(handle);
        return true;
    }

    private static void ThrowIfFailed(string operation, ProGpuDirect2DStatus status, int nativeHResult)
    {
        if (status != ProGpuDirect2DStatus.Success)
            throw new ProGpuDirect2DException(operation, status, nativeHResult);
    }
}
