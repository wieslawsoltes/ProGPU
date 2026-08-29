using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Owns one transactional C++ channel for canonical WPF DUCE/MIL batches.
/// </summary>
/// <remarks>
/// The channel retains only native protocol state and is independent of a GPU
/// device. Select the module that will later own the semantic compositor so
/// protocol and renderer binaries are guaranteed to use the same native ABI.
/// Unsupported commands fail without partially mutating the channel.
/// </remarks>
public sealed unsafe class NativeMilChannel : IDisposable
{
    private readonly NativeMilBackend _backend;
    private nint _channel;
    private int _disposeState;

    public NativeMilChannel(NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        nint channel = 0;
        NativeMilStatus status = backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.Create(&channel)
            : NativeMilMethods.Create(&channel);
        if (status != NativeMilStatus.Success || channel == 0)
        {
            throw new NativeMilException(
                status,
                "The ProGPU native MIL channel could not be created.");
        }
        _backend = backend;
        _channel = channel;
    }

    public NativeMilBackend Backend => _backend;

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    public nuint ResourceCount
    {
        get
        {
            nint channel = GetChannel();
            return _backend == NativeMilBackend.Dawn
                ? NativeMilDawnMethods.GetResourceCount(channel)
                : NativeMilMethods.GetResourceCount(channel);
        }
    }

    public NativeMilBatchMetrics Apply(ReadOnlySpan<byte> batch)
    {
        nint channel = GetChannel();
        var metrics = new NativeMilMethods.BatchMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.BatchMetrics>()
        };
        fixed (byte* batchPointer = batch)
        {
            NativeMilStatus status = _backend == NativeMilBackend.Dawn
                ? NativeMilDawnMethods.Apply(
                    channel, batchPointer, (nuint)batch.Length, &metrics)
                : NativeMilMethods.Apply(
                    channel, batchPointer, (nuint)batch.Length, &metrics);
            if (status != NativeMilStatus.Success)
            {
                throw new NativeMilException(
                    status,
                    $"The MIL batch was rejected with {status} after {metrics.CommandCount} command(s) ({metrics.TotalBytes} bytes).");
            }
        }
        return new NativeMilBatchMetrics(
            metrics.CommandCount,
            metrics.SupportedCommandCount,
            metrics.UnsupportedCommandCount,
            metrics.CreatedResourceCount,
            metrics.DeletedResourceCount,
            metrics.UpdatedResourceCount,
            metrics.TotalBytes);
    }

    /// <summary>
    /// Copies straight-alpha RGBA8 pixels into the portable sideband for a
    /// canonical WPF <see cref="NativeMilResourceType.BitmapSource"/> handle.
    /// </summary>
    /// <remarks>
    /// Canonical MilCmdBitmapSource transports an in-process WIC pointer,
    /// which cannot cross the native portable boundary. The retained handle
    /// and ImageDrawing packet stay canonical; only pixel ownership uses this
    /// pointer-free typed binding.
    /// </remarks>
    public void SetBitmapSourceRgba8(
        uint handle,
        uint width,
        uint height,
        uint rowBytes,
        ReadOnlySpan<byte> pixels)
    {
        nint channel = GetChannel();
        fixed (byte* pixelPointer = pixels)
        {
            NativeMilStatus status = _backend == NativeMilBackend.Dawn
                ? NativeMilDawnMethods.SetBitmapSourceRgba8(
                    channel,
                    handle,
                    width,
                    height,
                    rowBytes,
                    pixelPointer,
                    (nuint)pixels.Length)
                : NativeMilMethods.SetBitmapSourceRgba8(
                    channel,
                    handle,
                    width,
                    height,
                    rowBytes,
                    pixelPointer,
                    (nuint)pixels.Length);
            if (status != NativeMilStatus.Success)
            {
                throw new NativeMilException(
                    status,
                    $"The RGBA8 BitmapSource binding for MIL handle {handle} was rejected with {status}.");
            }
        }
    }

    /// <summary>
    /// Binds the dimensions of a live same-device texture to a canonical WPF
    /// BitmapSource resource. The scene remains pointer-free; callers bind
    /// the texture view through <see cref="NativeCompositor.BindSceneExternalImages"/>
    /// before installing the compiled scene.
    /// </summary>
    public void SetBitmapSourceExternalImage(
        uint handle,
        uint width,
        uint height)
    {
        ArgumentOutOfRangeException.ThrowIfZero(handle);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        nint channel = GetChannel();
        NativeMilStatus status = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.SetBitmapSourceExternalImage(
                channel, handle, width, height)
            : NativeMilMethods.SetBitmapSourceExternalImage(
                channel, handle, width, height);
        if (status != NativeMilStatus.Success)
        {
            throw new NativeMilException(
                status,
                $"The external image descriptor for MIL BitmapSource handle {handle} was rejected with {status}.");
        }
    }

    /// <summary>
    /// Binds the dimensions of a live same-device video texture to a canonical
    /// WPF MediaPlayer resource. The scene remains pointer-free; callers bind
    /// the texture view through <see cref="NativeCompositor.BindSceneExternalImages"/>
    /// before installing the compiled scene.
    /// </summary>
    public void SetMediaPlayerExternalImage(
        uint handle,
        uint width,
        uint height)
    {
        ArgumentOutOfRangeException.ThrowIfZero(handle);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        nint channel = GetChannel();
        NativeMilStatus status = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.SetMediaPlayerExternalImage(
                channel, handle, width, height)
            : NativeMilMethods.SetMediaPlayerExternalImage(
                channel, handle, width, height);
        if (status != NativeMilStatus.Success)
        {
            throw new NativeMilException(
                status,
                $"The external image descriptor for MIL MediaPlayer handle {handle} was rejected with {status}.");
        }
    }

    /// <summary>
    /// Copies an SFNT/TTC font into the portable sideband for a canonical WPF
    /// <see cref="NativeMilResourceType.GlyphRun"/> handle.
    /// </summary>
    /// <remarks>
    /// Canonical MilCmdGlyphRunCreate transports an in-process IDWriteFont
    /// pointer. The retained packet remains canonical; only font ownership is
    /// replaced with typed, pointer-free bytes and a face index.
    /// </remarks>
    public void SetGlyphRunFontSfnt(
        uint handle,
        ReadOnlySpan<byte> fontData,
        uint faceIndex = 0,
        NativeMilGlyphStyleSimulations styleSimulations =
            NativeMilGlyphStyleSimulations.None)
    {
        if (fontData.IsEmpty ||
            (styleSimulations &
             ~(NativeMilGlyphStyleSimulations.Bold |
               NativeMilGlyphStyleSimulations.Italic)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontData));
        }
        nint channel = GetChannel();
        fixed (byte* fontPointer = fontData)
        {
            NativeMilStatus status = _backend == NativeMilBackend.Dawn
                ? NativeMilDawnMethods.SetGlyphRunFontSfnt(
                    channel,
                    handle,
                    faceIndex,
                    (uint)styleSimulations,
                    fontPointer,
                    (nuint)fontData.Length)
                : NativeMilMethods.SetGlyphRunFontSfnt(
                    channel,
                    handle,
                    faceIndex,
                    (uint)styleSimulations,
                    fontPointer,
                    (nuint)fontData.Length);
            if (status != NativeMilStatus.Success)
            {
                throw new NativeMilException(
                    status,
                    $"The SFNT font binding for MIL glyph-run handle {handle} was rejected with {status}.");
            }
        }
    }

    /// <summary>
    /// Sets the exact local content bounds for a canonical DrawingImage.
    /// </summary>
    public void SetDrawingImageBounds(
        uint handle,
        NativeMilRect bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width <= 0 ||
            !double.IsFinite(bounds.Height) || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }
        nint channel = GetChannel();
        NativeMilStatus status = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.SetDrawingImageBounds(
                channel, handle, bounds.X, bounds.Y,
                bounds.Width, bounds.Height)
            : NativeMilMethods.SetDrawingImageBounds(
                channel, handle, bounds.X, bounds.Y,
                bounds.Width, bounds.Height);
        if (status != NativeMilStatus.Success)
        {
            throw new NativeMilException(
                status,
                $"The bounds binding for MIL drawing-image handle {handle} was rejected with {status}.");
        }
    }

    /// <summary>
    /// Sets exact source-built DrawingGroup content bounds used for native
    /// spatial opacity-mask mapping and bounded group composition.
    /// </summary>
    public void SetDrawingGroupBounds(
        uint handle,
        NativeMilRect bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width <= 0 ||
            !double.IsFinite(bounds.Height) || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }
        nint channel = GetChannel();
        NativeMilStatus status = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.SetDrawingGroupBounds(
                channel, handle, bounds.X, bounds.Y,
                bounds.Width, bounds.Height)
            : NativeMilMethods.SetDrawingGroupBounds(
                channel, handle, bounds.X, bounds.Y,
                bounds.Width, bounds.Height);
        if (status != NativeMilStatus.Success)
        {
            throw new NativeMilException(
                status,
                $"The bounds binding for MIL drawing-group handle {handle} was rejected with {status}.");
        }
    }

    /// <summary>
    /// Sets exact source-built Visual descendant bounds used to size its
    /// native target-space BitmapCache page, bounded effect isolation, or
    /// bounded Visual opacity/opacity-mask group.
    /// </summary>
    public void SetVisualCacheBounds(
        uint handle,
        NativeMilRect bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width <= 0 ||
            !double.IsFinite(bounds.Height) || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }
        nint channel = GetChannel();
        NativeMilStatus status = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.SetVisualCacheBounds(
                channel, handle, bounds.X, bounds.Y,
                bounds.Width, bounds.Height)
            : NativeMilMethods.SetVisualCacheBounds(
                channel, handle, bounds.X, bounds.Y,
                bounds.Width, bounds.Height);
        if (status != NativeMilStatus.Success)
        {
            throw new NativeMilException(
                status,
                $"The cache bounds binding for MIL Visual handle {handle} was rejected with {status}.");
        }
    }

    /// <summary>
    /// Copies a flattened camera/mesh scene into the portable sideband for a
    /// canonical WPF <see cref="NativeMilResourceType.Viewport3DVisual"/>
    /// handle.
    /// </summary>
    public void SetViewport3DScene(
        uint handle,
        NativeMilViewport3DScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(scene.Meshes);
        ArgumentNullException.ThrowIfNull(scene.Vertices);
        ArgumentNullException.ThrowIfNull(scene.Indices);
        ArgumentNullException.ThrowIfNull(scene.Lights);
        ArgumentNullException.ThrowIfNull(scene.Materials);
        ArgumentNullException.ThrowIfNull(scene.GradientStops);
        if (scene.Meshes.Length == 0 || scene.Vertices.Length == 0 ||
            scene.Indices.Length == 0 ||
            (scene.Materials.Length != 0 &&
                scene.Materials.Length != scene.Meshes.Length) ||
            (scene.Materials.Length == 0 && scene.GradientStops.Length != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(scene));
        }
        nint channel = GetChannel();
        NativeSceneCamera3D camera = scene.Camera;
        fixed (NativeSceneMesh3D* meshes = scene.Meshes)
        fixed (NativeSceneMesh3DVertex* vertices = scene.Vertices)
        fixed (uint* indices = scene.Indices)
        fixed (NativeSceneLight3D* lights = scene.Lights)
        fixed (NativeSceneBrush* materials = scene.Materials)
        fixed (NativeSceneGradientStop* gradientStops = scene.GradientStops)
        {
            bool hasLights = scene.Lights.Length != 0;
            bool hasMaterials = scene.Materials.Length != 0;
            NativeMilStatus status = _backend == NativeMilBackend.Dawn
                ? hasMaterials
                    ? NativeMilDawnMethods.SetViewport3DSceneMaterials(
                        channel, handle, &camera, scene.Viewport,
                        meshes, (nuint)scene.Meshes.Length,
                        vertices, (nuint)scene.Vertices.Length,
                        indices, (nuint)scene.Indices.Length,
                        lights, (nuint)scene.Lights.Length,
                        materials, (nuint)scene.Materials.Length,
                        gradientStops, (nuint)scene.GradientStops.Length)
                    : hasLights
                    ? NativeMilDawnMethods.SetViewport3DSceneLights(
                        channel, handle, &camera, scene.Viewport,
                        meshes, (nuint)scene.Meshes.Length,
                        vertices, (nuint)scene.Vertices.Length,
                        indices, (nuint)scene.Indices.Length,
                        lights, (nuint)scene.Lights.Length)
                    : NativeMilDawnMethods.SetViewport3DScene(
                        channel, handle, &camera, scene.Viewport,
                        meshes, (nuint)scene.Meshes.Length,
                        vertices, (nuint)scene.Vertices.Length,
                        indices, (nuint)scene.Indices.Length)
                : hasMaterials
                    ? NativeMilMethods.SetViewport3DSceneMaterials(
                        channel, handle, &camera, scene.Viewport,
                        meshes, (nuint)scene.Meshes.Length,
                        vertices, (nuint)scene.Vertices.Length,
                        indices, (nuint)scene.Indices.Length,
                        lights, (nuint)scene.Lights.Length,
                        materials, (nuint)scene.Materials.Length,
                        gradientStops, (nuint)scene.GradientStops.Length)
                    : hasLights
                    ? NativeMilMethods.SetViewport3DSceneLights(
                        channel, handle, &camera, scene.Viewport,
                        meshes, (nuint)scene.Meshes.Length,
                        vertices, (nuint)scene.Vertices.Length,
                        indices, (nuint)scene.Indices.Length,
                        lights, (nuint)scene.Lights.Length)
                    : NativeMilMethods.SetViewport3DScene(
                        channel, handle, &camera, scene.Viewport,
                        meshes, (nuint)scene.Meshes.Length,
                        vertices, (nuint)scene.Vertices.Length,
                        indices, (nuint)scene.Indices.Length);
            if (status != NativeMilStatus.Success)
            {
                throw new NativeMilException(
                    status,
                    $"The 3D scene binding for MIL viewport handle {handle} was rejected with {status}.");
            }
        }
    }

    public bool HasResource(uint handle)
    {
        nint channel = GetChannel();
        return (_backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.HasResource(channel, handle)
            : NativeMilMethods.HasResource(channel, handle)) != 0;
    }

    public uint GetResourceType(uint handle)
    {
        nint channel = GetChannel();
        return _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetResourceType(channel, handle)
            : NativeMilMethods.GetResourceType(channel, handle);
    }

    public ulong GetResourceGeneration(uint handle)
    {
        nint channel = GetChannel();
        return _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetResourceGeneration(channel, handle)
            : NativeMilMethods.GetResourceGeneration(channel, handle);
    }

    public bool TryGetVisual(uint handle, out NativeMilVisualSnapshot snapshot)
    {
        nint channel = GetChannel();
        var native = new NativeMilMethods.VisualSnapshot
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.VisualSnapshot>()
        };
        byte found = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetVisual(channel, handle, &native)
            : NativeMilMethods.GetVisual(channel, handle, &native);
        snapshot = found == 0
            ? default
            : new NativeMilVisualSnapshot(
                native.Handle,
                native.OffsetX,
                native.OffsetY,
                native.Opacity,
                native.ContentHandle,
                native.ChildCount);
        return found != 0;
    }

    public bool TryGetVisualChild(uint handle, uint index, out uint childHandle)
    {
        nint channel = GetChannel();
        uint child = 0;
        byte found = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetVisualChild(channel, handle, index, &child)
            : NativeMilMethods.GetVisualChild(channel, handle, index, &child);
        childHandle = child;
        return found != 0;
    }

    public bool TryGetTarget(uint handle, out NativeMilTargetSnapshot snapshot)
    {
        nint channel = GetChannel();
        var native = new NativeMilMethods.TargetSnapshot
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.TargetSnapshot>()
        };
        byte found = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetTarget(channel, handle, &native)
            : NativeMilMethods.GetTarget(channel, handle, &native);
        snapshot = found == 0
            ? default
            : new NativeMilTargetSnapshot(
                native.Handle,
                native.RootHandle,
                native.ClearRed,
                native.ClearGreen,
                native.ClearBlue,
                native.ClearAlpha,
                native.Flags);
        return found != 0;
    }

    /// <summary>
    /// Compiles a retained MIL target into the semantic scene stream accepted
    /// by the ProGPU native compositor selected for this channel.
    /// </summary>
    public NativeMilCompiledScene CompileScene(
        uint targetHandle,
        ulong sceneId,
        ulong generation)
    {
        nint channel = GetChannel();
        var nativeMetrics = new NativeMilMethods.SceneMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.SceneMetrics>()
        };
        nuint requiredBytes = 0;
        NativeMilStatus status = BuildScene(
            channel,
            targetHandle,
            sceneId,
            generation,
            null,
            0,
            &requiredBytes,
            &nativeMetrics);
        ThrowSceneFailure(status, targetHandle);
        if (requiredBytes > int.MaxValue)
        {
            throw new NativeMilException(
                NativeMilStatus.CapacityExceeded,
                $"The semantic scene for MIL target {targetHandle} exceeds the managed buffer limit.");
        }

        byte[] stream = GC.AllocateUninitializedArray<byte>((int)requiredBytes);
        nativeMetrics.StructSize =
            (uint)Unsafe.SizeOf<NativeMilMethods.SceneMetrics>();
        nuint writtenBytes = 0;
        fixed (byte* destination = stream)
        {
            status = BuildScene(
                channel,
                targetHandle,
                sceneId,
                generation,
                destination,
                (nuint)stream.Length,
                &writtenBytes,
                &nativeMetrics);
        }
        ThrowSceneFailure(status, targetHandle);
        if (writtenBytes != requiredBytes)
        {
            throw new NativeMilException(
                NativeMilStatus.InvalidGraph,
                $"The retained MIL target {targetHandle} changed while its semantic scene was compiled.");
        }
        return new NativeMilCompiledScene(
            stream,
            new NativeMilSceneMetrics(
                nativeMetrics.VisualCount,
                nativeMetrics.RectangleCount,
                nativeMetrics.EllipseCount,
                nativeMetrics.RoundedRectangleCount,
                nativeMetrics.LineCount,
                nativeMetrics.BrushCount,
                nativeMetrics.MaximumVisualDepth,
                nativeMetrics.StreamBytes));
    }

    /// <summary>
    /// Compiles a stateful frame request. The native channel treats the two
    /// ABI calls made here as one request, which prevents future dynamic MIL
    /// state from advancing once for sizing and again for copying.
    /// </summary>
    public NativeMilStatefulCompiledScene CompileScene(
        NativeMilSceneBuildRequest request)
    {
        nint channel = GetChannel();
        var nativeRequest = new NativeMilMethods.SceneBuildRequest
        {
            StructSize =
                (uint)Unsafe.SizeOf<NativeMilMethods.SceneBuildRequest>(),
            Flags = (uint)request.Flags,
            TargetHandle = request.TargetHandle,
            SceneId = request.SceneId,
            Generation = request.Generation,
            DpiScaleX = request.DpiScaleX,
            DpiScaleY = request.DpiScaleY,
            MonotonicTimeNanoseconds = request.MonotonicTimeNanoseconds,
            RequestSerial = request.RequestSerial
        };
        var nativeMetrics = new NativeMilMethods.SceneMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.SceneMetrics>()
        };
        var nativeResult = new NativeMilMethods.SceneBuildResult
        {
            StructSize =
                (uint)Unsafe.SizeOf<NativeMilMethods.SceneBuildResult>()
        };
        nuint requiredBytes = 0;
        NativeMilStatus status = BuildSceneWithRequest(
            channel,
            &nativeRequest,
            null,
            0,
            &requiredBytes,
            &nativeMetrics,
            &nativeResult);
        ThrowSceneFailure(status, request.TargetHandle);
        if (requiredBytes > int.MaxValue)
        {
            throw new NativeMilException(
                NativeMilStatus.CapacityExceeded,
                $"The semantic scene for MIL target {request.TargetHandle} exceeds the managed buffer limit.");
        }

        byte[] stream = GC.AllocateUninitializedArray<byte>((int)requiredBytes);
        nativeMetrics.StructSize =
            (uint)Unsafe.SizeOf<NativeMilMethods.SceneMetrics>();
        nativeResult.StructSize =
            (uint)Unsafe.SizeOf<NativeMilMethods.SceneBuildResult>();
        nuint writtenBytes = 0;
        fixed (byte* destination = stream)
        {
            status = BuildSceneWithRequest(
                channel,
                &nativeRequest,
                destination,
                (nuint)stream.Length,
                &writtenBytes,
                &nativeMetrics,
                &nativeResult);
        }
        ThrowSceneFailure(status, request.TargetHandle);
        if (writtenBytes != requiredBytes ||
            nativeResult.RequestSerial != request.RequestSerial ||
            nativeResult.StreamBytes != (ulong)writtenBytes)
        {
            throw new NativeMilException(
                NativeMilStatus.InvalidGraph,
                $"The retained MIL target {request.TargetHandle} returned an inconsistent stateful scene result.");
        }

        return new NativeMilStatefulCompiledScene(
            stream,
            new NativeMilSceneMetrics(
                nativeMetrics.VisualCount,
                nativeMetrics.RectangleCount,
                nativeMetrics.EllipseCount,
                nativeMetrics.RoundedRectangleCount,
                nativeMetrics.LineCount,
                nativeMetrics.BrushCount,
                nativeMetrics.MaximumVisualDepth,
                nativeMetrics.StreamBytes),
            new NativeMilSceneBuildResult(
                (NativeMilSceneBuildResultFlags)nativeResult.Flags,
                nativeResult.RequestSerial,
                nativeResult.NextDueTimeNanoseconds,
                nativeResult.StreamBytes));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }
        nint channel = Interlocked.Exchange(ref _channel, 0);
        if (channel != 0)
        {
            if (_backend == NativeMilBackend.Dawn)
            {
                NativeMilDawnMethods.Destroy(channel);
            }
            else
            {
                NativeMilMethods.Destroy(channel);
            }
        }
        GC.SuppressFinalize(this);
    }

    private nint GetChannel()
    {
        nint channel = Volatile.Read(ref _channel);
        ObjectDisposedException.ThrowIf(channel == 0, this);
        return channel;
    }


    private NativeMilStatus BuildScene(
        nint channel,
        uint targetHandle,
        ulong sceneId,
        ulong generation,
        void* destination,
        nuint destinationSize,
        nuint* bytesWritten,
        NativeMilMethods.SceneMetrics* metrics)
    {
        return _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.BuildScene(
                channel,
                targetHandle,
                sceneId,
                generation,
                destination,
                destinationSize,
                bytesWritten,
                metrics)
            : NativeMilMethods.BuildScene(
                channel,
                targetHandle,
                sceneId,
                generation,
                destination,
                destinationSize,
                bytesWritten,
                metrics);
    }

    private NativeMilStatus BuildSceneWithRequest(
        nint channel,
        NativeMilMethods.SceneBuildRequest* request,
        void* destination,
        nuint destinationSize,
        nuint* bytesWritten,
        NativeMilMethods.SceneMetrics* metrics,
        NativeMilMethods.SceneBuildResult* buildResult)
    {
        return _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.BuildSceneWithRequest(
                channel,
                request,
                destination,
                destinationSize,
                bytesWritten,
                metrics,
                buildResult)
            : NativeMilMethods.BuildSceneWithRequest(
                channel,
                request,
                destination,
                destinationSize,
                bytesWritten,
                metrics,
                buildResult);
    }

    private static void ThrowSceneFailure(
        NativeMilStatus status,
        uint targetHandle)
    {
        if (status != NativeMilStatus.Success)
        {
            throw new NativeMilException(
                status,
                $"The retained MIL target {targetHandle} could not be compiled to a semantic scene.");
        }
    }
}
