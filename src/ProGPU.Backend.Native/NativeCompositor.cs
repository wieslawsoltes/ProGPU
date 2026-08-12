using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.WebGPU;

namespace ProGPU.Backend.Native;

/// <summary>
/// Owns one ProGPU C++ renderer over an existing Silk/wgpu-native device.
/// </summary>
/// <remarks>
/// Rendering crosses the C ABI once per frame and submits one native WebGPU
/// command buffer. The compositor is owner-thread affine and must be disposed
/// before its <see cref="WgpuContext"/> unless context disposal does so first.
/// </remarks>
public sealed unsafe class NativeCompositor : IDisposable
{
    private readonly WgpuContext _context;
    private readonly TextureFormat _targetFormat;
    private nint _engine;
    private int _disposeState;

    public NativeCompositor(
        WgpuContext context,
        TextureFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsDisposed || !context.IsInitialized)
        {
            throw new ObjectDisposedException(nameof(context));
        }
        if (context.BackendKind != WgpuBackendKind.SilkNative)
        {
            throw new NotSupportedException(
                "This native binary accepts only the exact Silk.NET 2.23 wgpu-native ABI. Dawn and browser devices require their own adapters.");
        }

        var nativeFormat = ToNativeFormat(targetFormat);
        var options = new NativeMethods.EngineOptions
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.EngineOptions>(),
            AbiVersion = NativeMethods.AbiVersion,
            BackendAbi = NativeMethods.WgpuNativeMay2024BackendAbi,
            TargetFormat = nativeFormat,
            Device = (nuint)context.Device,
            Queue = (nuint)context.Queue
        };

        lock (context.RenderLock)
        {
            var engine = nint.Zero;
            var status = NativeMethods.Create(&options, &engine);
            if (status != NativeRendererStatus.Success || engine == 0)
            {
                throw new NativeRendererException(
                    status,
                    "The ProGPU native renderer could not be created.");
            }
            _engine = engine;
        }

        _context = context;
        _targetFormat = targetFormat;
        WgpuContext.Disposing += OnContextDisposing;
    }

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    public static NativeRendererInfo GetInfo()
    {
        if (NativeMethods.GetAbiVersion() != NativeMethods.AbiVersion)
        {
            throw new NotSupportedException(
                "The loaded ProGPU native renderer has an incompatible ABI.");
        }

        var info = new NativeMethods.EngineInfo
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.EngineInfo>()
        };
        if (NativeMethods.GetInfo(&info) == 0)
        {
            throw new InvalidOperationException(
                "The ProGPU native renderer did not return its capability record.");
        }

        byte* namePointer = info.Name;
        var name = Marshal.PtrToStringUTF8((nint)namePointer) ?? string.Empty;
        return new NativeRendererInfo(
            info.AbiVersion,
            info.BackendAbi,
            (NativeRendererCapabilities)info.Capabilities,
            name);
    }

    public NativeFrameMetrics Render(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeSolidRectangle> rectangles,
        Vector4 clearColor)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        if (!ReferenceEquals(target.Context, _context))
        {
            throw new ArgumentException(
                "The target must belong to the native compositor's WebGPU device domain.",
                nameof(target));
        }
        if (target.IsDisposed || target.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(target));
        }
        if (target.Format != _targetFormat || target.SampleCount != 1)
        {
            throw new ArgumentException(
                "The target format and sample count must match the native pipeline.",
                nameof(target));
        }
        if (!target.Usage.HasFlag(TextureUsage.RenderAttachment))
        {
            throw new ArgumentException(
                "The target must allow WebGPU render-attachment usage.",
                nameof(target));
        }

        fixed (NativeSolidRectangle* rectanglePointer = rectangles)
        {
            var frame = new NativeMethods.Frame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.Frame>(),
                Width = target.Width,
                Height = target.Height,
                DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr,
                ClearColor = new NativeMethods.NativeColor
                {
                    R = clearColor.X,
                    G = clearColor.Y,
                    B = clearColor.Z,
                    A = clearColor.W
                },
                Rectangles = rectanglePointer,
                RectangleCount = (nuint)rectangles.Length
            };
            var metrics = new NativeMethods.FrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.FrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                var status = NativeMethods.Render(_engine, &frame, &metrics);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
                _context.PollDevice(wait: false);
            }

            return new NativeFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.VertexUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount);
        }
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void OnContextDisposing(WgpuContext context)
    {
        if (ReferenceEquals(context, _context))
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        WgpuContext.Disposing -= OnContextDisposing;
        var engine = Interlocked.Exchange(ref _engine, 0);
        if (engine == 0)
        {
            return;
        }

        lock (_context.RenderLock)
        {
            NativeMethods.Destroy(engine);
        }
    }

    private string ReadLastError()
    {
        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* pointer = buffer)
        {
            var required = NativeMethods.GetLastError(
                _engine,
                pointer,
                (nuint)buffer.Length);
            if (required == 0)
            {
                return "The ProGPU native renderer returned an unspecified error.";
            }
            var terminator = buffer.IndexOf((byte)0);
            var length = terminator >= 0 ? terminator : buffer.Length;
            return Encoding.UTF8.GetString(buffer[..length]);
        }
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed || _context.IsDisposed || _engine == 0)
        {
            throw new ObjectDisposedException(nameof(NativeCompositor));
        }
    }

    private static NativeRendererTextureFormat ToNativeFormat(
        TextureFormat format) => format switch
        {
            TextureFormat.Rgba8Unorm => NativeRendererTextureFormat.Rgba8Unorm,
            TextureFormat.Bgra8Unorm => NativeRendererTextureFormat.Bgra8Unorm,
            TextureFormat.Rgba8UnormSrgb => NativeRendererTextureFormat.Rgba8UnormSrgb,
            TextureFormat.Bgra8UnormSrgb => NativeRendererTextureFormat.Bgra8UnormSrgb,
            _ => throw new NotSupportedException(
                $"The initial native renderer does not support {format} targets.")
        };
}

public sealed class NativeRendererException : Exception
{
    public NativeRendererException(
        NativeRendererStatus status,
        string message)
        : base(message)
    {
        Status = status;
    }

    public NativeRendererStatus Status { get; }
}
