using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Silk.NET.WebGPU;

namespace Microsoft.Graphics.Canvas;

/// <summary>
/// Win2D-shaped device whose portable drawing path targets the ProGPU C++
/// semantic renderer over the caller's WebGPU device.
/// </summary>
public sealed class CanvasDevice : ICanvasResourceCreator, IDisposable
{
    private static readonly object SharedLock = new();
    private static CanvasDevice? s_sharedDevice;
    private static long s_sceneId;

    private readonly bool _ownsContext;
    private readonly object _renderLock = new();
    private NativeCompositor? _bgraCompositor;
    private int _pixelConversionMode;
    private int _lastPixelConversionPath;
    private bool _isDisposed;

    public CanvasDevice()
        : this(CreateContext(), ownsContext: true)
    {
    }

    private CanvasDevice(WgpuContext context, bool ownsContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsInitialized || context.IsDisposed)
        {
            throw new ArgumentException(
                "CanvasDevice requires a live initialized WebGPU context.",
                nameof(context));
        }
        if (context.BackendKind != WgpuBackendKind.SilkNative)
        {
            throw new NotSupportedException(
                "The portable native Canvas lane currently requires the exact Silk.NET wgpu-native ABI. Dawn and browser adapters fail closed until their typed Canvas device factories are available.");
        }

        Context = context;
        _ownsContext = ownsContext;
    }

    public CanvasDevice Device => this;

    public WgpuContext Context { get; }

    public bool ForceSoftwareRenderer => false;

    public int MaximumBitmapSizeInPixels =>
        CanvasContract.MaximumBitmapSizeInPixels;

    public ProGpuCanvasExecutionPath ExecutionPath =>
        ProGpuCanvasExecutionPath.NativeCppWebGpu;

    public ProGpuCanvasCpuConversionMode PixelConversionMode
    {
        get => (ProGpuCanvasCpuConversionMode)Volatile.Read(
            ref _pixelConversionMode);
        set
        {
            if (value is < ProGpuCanvasCpuConversionMode.Automatic or
                > ProGpuCanvasCpuConversionMode.ScalarReference)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Volatile.Write(ref _pixelConversionMode, (int)value);
        }
    }

    public ProGpuCanvasCpuConversionPath LastPixelConversionPath =>
        (ProGpuCanvasCpuConversionPath)Volatile.Read(
            ref _lastPixelConversionPath);

    public bool IsDisposed => _isDisposed;

    public static CanvasDevice GetSharedDevice() =>
        GetSharedDevice(forceSoftwareRenderer: false);

    public static CanvasDevice GetSharedDevice(bool forceSoftwareRenderer)
    {
        if (forceSoftwareRenderer)
        {
            throw new NotSupportedException(
                "Portable CanvasDevice does not silently replace the GPU path with a software renderer.");
        }

        lock (SharedLock)
        {
            if (s_sharedDevice is not { IsDisposed: false })
            {
                s_sharedDevice = new CanvasDevice();
            }

            return s_sharedDevice;
        }
    }

    public static CanvasDevice FromContext(WgpuContext context) =>
        new(context, ownsContext: false);

    internal ulong AllocateSceneId()
    {
        ThrowIfDisposed();
        ulong value = checked((ulong)Interlocked.Increment(ref s_sceneId));
        return value == 0U
            ? checked((ulong)Interlocked.Increment(ref s_sceneId))
            : value;
    }

    internal void RecordPixelConversionPath(
        ProGpuCanvasCpuConversionPath path) =>
        Volatile.Write(ref _lastPixelConversionPath, (int)path);

    internal ProGpuCanvasRenderMetrics Render(
        GpuPicture picture,
        GpuTexture target,
        float dpi,
        ulong sceneId,
        ulong generation,
        Vector4 clearColor,
        bool preserveTarget)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(target);
        if (!ReferenceEquals(target.Context, Context))
        {
            throw new ArgumentException(
                "Canvas resources must remain in one WebGPU device domain.",
                nameof(target));
        }

        float dpiScale = dpi / CanvasContract.DefaultDpi;
        if (!GpuPictureNativeSceneCompiler.TryCompile(
                picture,
                sceneId,
                generation,
                new NativePictureCompileOptions(dpiScale),
                out NativeCompiledPicture? compiled,
                out NativePictureCompileFailure failure) ||
            compiled is null)
        {
            throw new NotSupportedException(
                $"The Canvas display list cannot be represented by the native scene ABI: {failure}.");
        }

        lock (_renderLock)
        {
            NativeCompositor compositor = _bgraCompositor ??=
                new NativeCompositor(Context, TextureFormat.Bgra8Unorm);
            NativeSceneUpdateMetrics update = compositor.UpdateScene(compiled);
            if (update.ValidationError != NativeSceneValidationError.None)
            {
                throw new InvalidOperationException(
                    $"The native Canvas scene was rejected: {update}.");
            }

            NativeSceneFrameMetrics frame = preserveTarget
                ? compositor.RenderScenePreservingTarget(
                    target,
                    dpiScale,
                    sceneId,
                    generation,
                    clearColor)
                : compositor.RenderScene(
                    target,
                    dpiScale,
                    sceneId,
                    generation,
                    clearColor);
            return new ProGpuCanvasRenderMetrics(
                ProGpuCanvasExecutionPath.NativeCppWebGpu,
                compiled.SourceCommandCount,
                compiled.NativeCommandCount,
                compiled.NativeDrawCount,
                frame.SubmissionCount,
                frame.DrawCallCount,
                frame.PayloadHash);
        }
    }

    private static WgpuContext CreateContext()
    {
        var context = new WgpuContext();
        try
        {
            context.Initialize(window: null);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CanvasDevice));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _bgraCompositor?.Dispose();
            _bgraCompositor = null;
            if (_ownsContext)
            {
                Context.Dispose();
            }
            _isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
