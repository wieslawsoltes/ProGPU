using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.WebGPU;

namespace ProGPU.Backend.Native;

/// <summary>
/// Owns one ProGPU C++ renderer over an existing typed WebGPU device.
/// </summary>
/// <remarks>
/// Each typed family render crosses the C ABI once and submits one native
/// WebGPU command buffer. Semantic scene rendering also crosses once and
/// shares one encoder across distinct retained buffer domains. Reusing a domain
/// flushes the current graph before its payload can be overwritten. The compositor is
/// owner-thread affine and must be disposed before its
/// <see cref="WgpuContext"/> unless context disposal does so first.
/// The public constructor selects the directly linked wgpu-native module;
/// <see cref="NativeDawnAdapter"/> creates the provider-resolved Dawn variant.
/// </remarks>
public sealed unsafe class NativeCompositor : IDisposable
{
    private const uint ExternalImageSourceViewFlag = 1U;
    private const uint SceneFramePreserveTargetFlag = 1U << 0;
    private const uint SceneFrameDamageRectFlag = 1U << 1;

    private readonly WgpuContext _context;
    private readonly TextureFormat _targetFormat;
    private readonly NativeRendererInteropKind _interopKind;
    private nint _engine;
    private int _disposeState;

    /// <summary>Resolved occupied-page sampling policy, independent of ordinary image sampling.</summary>
    public GpuTilePageSamplingPath TilePageSamplingPath =>
        GpuImageSamplingPolicy.ResolveTilePagePath(_context.ImageSamplingPreference);

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
            Queue = (nuint)context.Queue,
            Flags = GetEngineFlags(context)
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
        _interopKind = NativeRendererInteropKind.WgpuNative;
        WgpuContext.Disposing += OnContextDisposing;
    }

    private NativeCompositor(
        WgpuContext context,
        TextureFormat targetFormat,
        nint engine,
        NativeRendererInteropKind interopKind)
    {
        _context = context;
        _targetFormat = targetFormat;
        _engine = engine;
        _interopKind = interopKind;
        WgpuContext.Disposing += OnContextDisposing;
    }

    internal static NativeCompositor CreateDawn(
        WgpuContext context,
        TextureFormat targetFormat,
        nuint instance,
        nuint device,
        nuint queue,
        nint resolverContext,
        nint resolveProc)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsDisposed || !context.IsInitialized)
        {
            throw new ObjectDisposedException(nameof(context));
        }
        if (context.BackendKind != WgpuBackendKind.DawnNative)
        {
            throw new NotSupportedException(
                "The provider-resolved native renderer requires an exact Dawn context.");
        }
        if (instance == 0 || device == 0 || queue == 0 ||
            resolverContext == 0 || resolveProc == 0)
        {
            throw new ArgumentException(
                "Dawn instance, device, queue, and procedure resolver handles are required.");
        }

        var options = CreateDawnOptions(
            context,
            targetFormat,
            instance,
            device,
            queue,
            resolverContext,
            resolveProc);
        nint engine = 0;
        lock (context.RenderLock)
        {
            NativeRendererStatus status =
                NativeDawnMethods.Create(&options, &engine);
            if (status != NativeRendererStatus.Success || engine == 0)
            {
                throw new NativeRendererException(
                    status,
                    "The provider-resolved ProGPU native renderer could not be created.");
            }
        }
        return new NativeCompositor(
            context,
            targetFormat,
            engine,
            NativeRendererInteropKind.Dawn);
    }

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    /// <summary>
    /// Creates a replacement native compositor on a newly initialized WebGPU
    /// context after device loss, preserving the latest immutable semantic
    /// scene snapshot without carrying any old-device GPU handle across.
    /// </summary>
    /// <remarks>
    /// The source remains terminal and caller-owned. Dispose it only after
    /// this transactional operation succeeds. The replacement's first frame
    /// rebuilds device resources; stable replay performs no payload upload.
    /// </remarks>
    public NativeCompositor Recreate(WgpuContext replacementContext)
    {
        ArgumentNullException.ThrowIfNull(replacementContext);
        if (!_context.IsDeviceLost)
        {
            throw new InvalidOperationException(
                "A native compositor can be recreated only after its WebGPU context reports device loss.");
        }
        if (ReferenceEquals(replacementContext, _context) ||
            replacementContext.IsDisposed ||
            !replacementContext.IsInitialized ||
            replacementContext.IsDeviceLost)
        {
            throw new ArgumentException(
                "The replacement must be a live, newly initialized WebGPU context.",
                nameof(replacementContext));
        }
        if (_interopKind != NativeRendererInteropKind.WgpuNative ||
            replacementContext.BackendKind != WgpuBackendKind.SilkNative)
        {
            throw new NotSupportedException(
                "Use NativeDawnAdapter.RecreateCompositor for a Dawn renderer; this overload accepts only the exact Silk.NET 2.23 wgpu-native ABI.");
        }

        var options = new NativeMethods.EngineOptions
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.EngineOptions>(),
            AbiVersion = NativeMethods.AbiVersion,
            BackendAbi = NativeMethods.WgpuNativeMay2024BackendAbi,
            TargetFormat = ToNativeFormat(_targetFormat),
            Device = (nuint)replacementContext.Device,
            Queue = (nuint)replacementContext.Queue,
            Flags = GetEngineFlags(replacementContext)
        };

        nint replacement = 0;
        lock (_context.RenderLock)
        {
            ThrowIfDisposed();
            ThrowForStatus(NativeRendererInterop.MarkDeviceLost(
                _interopKind, _engine));
            lock (replacementContext.RenderLock)
            {
                var status = NativeMethods.Recreate(
                    _engine,
                    &options,
                    &replacement);
                if (status != NativeRendererStatus.Success || replacement == 0)
                {
                    throw new NativeRendererException(
                        status,
                        ReadLastError());
                }
            }
        }
        return new NativeCompositor(
            replacementContext,
            _targetFormat,
            replacement,
            NativeRendererInteropKind.WgpuNative);
    }

    internal NativeCompositor RecreateDawn(
        WgpuContext replacementContext,
        nuint instance,
        nuint device,
        nuint queue,
        nint resolverContext,
        nint resolveProc)
    {
        ArgumentNullException.ThrowIfNull(replacementContext);
        if (_interopKind != NativeRendererInteropKind.Dawn ||
            !_context.IsDeviceLost)
        {
            throw new InvalidOperationException(
                "A Dawn compositor can be recreated only after its Dawn context reports device loss.");
        }
        if (ReferenceEquals(replacementContext, _context) ||
            replacementContext.IsDisposed ||
            !replacementContext.IsInitialized ||
            replacementContext.IsDeviceLost ||
            replacementContext.BackendKind != WgpuBackendKind.DawnNative)
        {
            throw new ArgumentException(
                "The replacement must be a live, newly initialized Dawn context.",
                nameof(replacementContext));
        }

        var options = CreateDawnOptions(
            replacementContext,
            _targetFormat,
            instance,
            device,
            queue,
            resolverContext,
            resolveProc);
        nint replacement = 0;
        lock (_context.RenderLock)
        {
            ThrowIfDisposed();
            ThrowForStatus(NativeDawnMethods.MarkDeviceLost(_engine));
            lock (replacementContext.RenderLock)
            {
                NativeRendererStatus status = NativeDawnMethods.Recreate(
                    _engine,
                    &options,
                    &replacement);
                if (status != NativeRendererStatus.Success || replacement == 0)
                {
                    throw new NativeRendererException(
                        status,
                        ReadLastError());
                }
            }
        }
        return new NativeCompositor(
            replacementContext,
            _targetFormat,
            replacement,
            NativeRendererInteropKind.Dawn);
    }

    /// <summary>
    /// Returns the queue token for the most recently submitted native frame.
    /// </summary>
    public NativeSubmissionToken GetLastSubmissionToken()
    {
        lock (_context.RenderLock)
        {
            ThrowIfGpuUnavailable();
            ulong value = 0;
            ThrowForStatus(NativeRendererInterop.GetLastSubmission(
                _interopKind, _engine, &value));
            return new NativeSubmissionToken(value, _engine);
        }
    }

    /// <summary>
    /// Returns pooled group-layer activity for the most recently submitted frame.
    /// </summary>
    public NativeLayerMetrics GetLayerMetrics()
    {
        var metrics = new NativeMethods.LayerMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.LayerMetrics>()
        };
        lock (_context.RenderLock)
        {
            ThrowIfDisposed();
            ThrowForStatus(NativeRendererInterop.GetLayerMetrics(
                _interopKind, _engine, &metrics));
        }
        return new NativeLayerMetrics(
            metrics.TextureWidth,
            metrics.TextureHeight,
            metrics.TextureGeneration,
            metrics.AllocationCount,
            metrics.ContentPassCount,
            metrics.CompositePassCount,
            metrics.CacheHit != 0U,
            metrics.TextureBytes,
            metrics.VertexUploadBytes,
            metrics.UniformUploadBytes,
            metrics.MaskKind,
            metrics.MaskRevision,
            metrics.MaskBindGroupGeneration,
            metrics.MaskBindGroupCacheHit != 0U,
            metrics.MaskUniformUploadBytes,
            metrics.ClipPathCount,
            metrics.ClipRasterizedPathCount,
            metrics.ClipPassCount,
            metrics.ClipCacheHit != 0U,
            metrics.ClipPathUploadBytes,
            metrics.ClipCoverageStagingBytes,
            metrics.ClipTextureBytes,
            metrics.EffectKind,
            metrics.EffectRevision,
            metrics.EffectPassCount,
            metrics.EffectCacheHit != 0U,
            metrics.EffectUniformUploadBytes,
            metrics.EffectTextureBytes,
            metrics.EffectCount,
            metrics.EffectChainRevision,
            metrics.EffectTextureGeneration,
            metrics.EffectAllocationCount,
            metrics.BlendMode,
            metrics.BlendSourcePassCount,
            metrics.BlendPipelineCacheHit != 0U,
            metrics.BlendSourceTextureGeneration,
            metrics.BlendSourceAllocationCount,
            metrics.BlendSourceTextureBytes);
    }

    /// <summary>
    /// Tests whether the GPU has completed a native submission without waiting.
    /// </summary>
    public bool IsSubmissionComplete(NativeSubmissionToken token) =>
        PollSubmission(token, wait: false);

    /// <summary>
    /// Waits until the GPU completes a native submission.
    /// </summary>
    public void WaitForSubmission(NativeSubmissionToken token)
    {
        if (!PollSubmission(token, wait: true))
        {
            throw new NativeRendererException(
                NativeRendererStatus.DeviceLost,
                "The WebGPU queue did not complete the requested native submission.");
        }
    }

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

    /// <summary>
    /// Transactionally replaces the same-device texture-view table used by
    /// external image resources in retained pointer-free scenes.
    /// </summary>
    public void BindSceneExternalImages(
        ReadOnlySpan<NativeSceneExternalImageBinding> bindings)
    {
        if ((uint)bindings.Length > NativeMethods.SceneMaximumResources)
        {
            throw new ArgumentOutOfRangeException(nameof(bindings));
        }

        NativeMethods.SceneExternalImageBinding[]? rented = null;
        Span<NativeMethods.SceneExternalImageBinding> nativeBindings =
            bindings.Length <= 64
                ? stackalloc NativeMethods.SceneExternalImageBinding[
                    bindings.Length]
                : (rented = ArrayPool<NativeMethods.SceneExternalImageBinding>
                    .Shared.Rent(bindings.Length)).AsSpan(0, bindings.Length);
        try
        {
            ulong previousResourceId = 0U;
            NativeSceneExternalImageRole previousRole =
                NativeSceneExternalImageRole.Primary;
            for (int index = 0; index < bindings.Length; index++)
            {
                ref readonly NativeSceneExternalImageBinding binding =
                    ref bindings[index];
                if (binding.ResourceId == 0U ||
                    binding.Generation == 0U ||
                    (uint)binding.Role >
                        (uint)NativeSceneExternalImageRole.Mask ||
                    (binding.ResourceId < previousResourceId ||
                        binding.ResourceId == previousResourceId &&
                        binding.Role <= previousRole))
                {
                    throw new ArgumentException(
                        "External scene image bindings must have nonzero generations and strictly increasing resource/role keys.",
                        nameof(bindings));
                }
                ValidateSceneExternalImageSource(
                    binding.Texture,
                    binding.Role);
                previousResourceId = binding.ResourceId;
                previousRole = binding.Role;
                nativeBindings[index] = new NativeMethods.SceneExternalImageBinding
                {
                    StructSize = (uint)Unsafe.SizeOf<
                        NativeMethods.SceneExternalImageBinding>(),
                    Flags = (uint)binding.Role,
                    ResourceId = binding.ResourceId,
                    Generation = binding.Generation,
                    TextureView = (nuint)binding.Texture.ViewPtr,
                    Width = binding.Texture.Width,
                    Height = binding.Texture.Height
                };
            }

            fixed (NativeMethods.SceneExternalImageBinding* bindingPointer =
                nativeBindings)
            {
                lock (_context.RenderLock)
                {
                    ThrowIfDisposed();
                    ThrowForStatus(
                        NativeRendererInterop.BindSceneExternalImages(
                            _interopKind,
                            _engine,
                            bindingPointer,
                            (nuint)bindings.Length));
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<NativeMethods.SceneExternalImageBinding>.Shared.Return(
                    rented);
            }
        }
    }

    /// <summary>
    /// Validates and transactionally installs one immutable semantic scene
    /// snapshot. The native engine copies a changed generation and retains an
    /// identical generation without copying it again.
    /// </summary>
    public NativeSceneUpdateMetrics UpdateScene(ReadOnlySpan<byte> stream)
    {
        if (stream.IsEmpty)
        {
            throw new ArgumentException(
                "A semantic scene stream cannot be empty.",
                nameof(stream));
        }
        var metrics = new NativeMethods.SceneMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.SceneMetrics>()
        };
        fixed (byte* streamPointer = stream)
        {
            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                ThrowForStatus(NativeRendererInterop.UpdateScene(
                    _interopKind,
                    _engine,
                    streamPointer,
                    (nuint)stream.Length,
                    &metrics));
            }
        }
        return ToSceneMetrics(metrics);
    }

    /// <summary>
    /// Begins one asynchronous GPU query against the hit-test index carried by
    /// the installed immutable semantic scene. Stable queries reuse all native
    /// scene buffers and cross the managed/native boundary once.
    /// </summary>
    public NativeGpuHitTestRequestToken BeginGpuHitTest(
        in NativeGpuHitTestQuery query)
    {
        var nativeQuery = query;
        ulong token = 0;
        lock (_context.RenderLock)
        {
            ThrowIfGpuUnavailable();
            ThrowForStatus(NativeRendererInterop.BeginHitTest(
                _interopKind,
                _engine,
                &nativeQuery,
                &token));
        }
        return new NativeGpuHitTestRequestToken(token, _engine);
    }

    /// <summary>
    /// Polls a GPU hit-test readback without blocking. The supplied span must
    /// cover the result capacity encoded in the originating query, or be empty
    /// to explicitly discard the ordered list while retaining the summary.
    /// In list mode, <paramref name="summary"/>'s Hit field is the total hit
    /// count and its remaining diagnostic fields describe traversal. In
    /// zero-list mode it contains the topmost hit instead.
    /// </summary>
    public bool TryPollGpuHitTest(
        NativeGpuHitTestRequestToken token,
        Span<NativeGpuHitTestResult> results,
        out int resultCount,
        out NativeGpuHitTestResult summary)
    {
        if (!token.IsValid || token.Owner != _engine)
        {
            throw new ArgumentException(
                "The GPU hit-test token belongs to another native compositor.",
                nameof(token));
        }
        if (results.Length > NativeGpuHitTestQuery.MaximumResultCount)
        {
            throw new ArgumentOutOfRangeException(nameof(results));
        }

        uint nativeResultCount = 0;
        byte complete = 0;
        NativeGpuHitTestResult nativeSummary = default;
        fixed (NativeGpuHitTestResult* resultPointer = results)
        {
            lock (_context.RenderLock)
            {
                ThrowIfGpuUnavailable();
                ThrowForStatus(NativeRendererInterop.PollHitTest(
                    _interopKind,
                    _engine,
                    token.Value,
                    resultPointer,
                    (uint)results.Length,
                    &nativeResultCount,
                    &nativeSummary,
                    &complete));
            }
        }
        resultCount = checked((int)nativeResultCount);
        summary = nativeSummary;
        return complete != 0;
    }

    /// <summary>
    /// Renders the installed immutable semantic scene generation in display
    /// list order to one target.
    /// </summary>
    public NativeSceneFrameMetrics RenderScene(
        GpuTexture target,
        float dpiScale,
        ulong sceneId,
        ulong generation,
        Vector4 clearColor)
    {
        ValidateTarget(target);
        NativeSceneFrameMetrics metrics = RenderSceneCore(
            new NativeSceneExternalTarget(
                (nuint)target.ViewPtr,
                target.Width,
                target.Height),
            dpiScale,
            sceneId,
            generation,
            clearColor,
            preserveTarget: false,
            damage: null);
        target.NotifyExternalContentChanged();
        return metrics;
    }

    /// <summary>
    /// Renders the installed immutable semantic scene generation while loading
    /// and preserving the complete target before replay.
    /// </summary>
    public NativeSceneFrameMetrics RenderScenePreservingTarget(
        GpuTexture target,
        float dpiScale,
        ulong sceneId,
        ulong generation,
        Vector4 clearColor)
    {
        ValidateTarget(target);
        NativeSceneFrameMetrics metrics = RenderSceneCore(
            new NativeSceneExternalTarget(
                (nuint)target.ViewPtr,
                target.Width,
                target.Height),
            dpiScale,
            sceneId,
            generation,
            clearColor,
            preserveTarget: true,
            damage: null);
        target.NotifyExternalContentChanged();
        return metrics;
    }

    /// <summary>
    /// Renders the installed immutable semantic scene generation while
    /// preserving target contents outside an optional logical damage rect.
    /// Complex isolated-layer scenes conservatively fall back to full replay.
    /// Damage replay does not clear the preserved target; the damaged area must
    /// be covered opaquely before translucent or blended content is replayed.
    /// </summary>
    public NativeSceneFrameMetrics RenderScene(
        GpuTexture target,
        float dpiScale,
        ulong sceneId,
        ulong generation,
        Vector4 clearColor,
        NativeSceneDamageRect? damage)
    {
        ValidateTarget(target);
        NativeSceneFrameMetrics metrics = RenderSceneCore(
            new NativeSceneExternalTarget(
                (nuint)target.ViewPtr,
                target.Width,
                target.Height),
            dpiScale,
            sceneId,
            generation,
            clearColor,
            preserveTarget: true,
            damage);
        target.NotifyExternalContentChanged();
        return metrics;
    }

    /// <summary>
    /// Renders the installed immutable semantic scene generation directly to
    /// a host-owned WebGPU texture view without acquiring texture ownership.
    /// </summary>
    public NativeSceneFrameMetrics RenderScene(
        NativeSceneExternalTarget target,
        float dpiScale,
        ulong sceneId,
        ulong generation,
        Vector4 clearColor)
    {
        ValidateExternalTarget(target);
        return RenderSceneCore(
            target,
            dpiScale,
            sceneId,
            generation,
            clearColor,
            preserveTarget: false,
            damage: null);
    }

    /// <summary>
    /// Renders the installed immutable semantic scene generation directly to
    /// a host-owned WebGPU texture view while preserving contents outside an
    /// optional logical damage rectangle.
    /// </summary>
    /// <remarks>
    /// The caller guarantees that the view belongs to this compositor's
    /// device, matches its configured format, permits render-attachment use,
    /// and remains alive through submission completion. This overload exists
    /// for swapchain hosts that already own the acquired view and must not
    /// transfer its texture reference into a managed wrapper.
    /// </remarks>
    public NativeSceneFrameMetrics RenderScene(
        NativeSceneExternalTarget target,
        float dpiScale,
        ulong sceneId,
        ulong generation,
        Vector4 clearColor,
        NativeSceneDamageRect? damage)
    {
        ValidateExternalTarget(target);
        return RenderSceneCore(
            target,
            dpiScale,
            sceneId,
            generation,
            clearColor,
            preserveTarget: true,
            damage);
    }

    private NativeSceneFrameMetrics RenderSceneCore(
        NativeSceneExternalTarget target,
        float dpiScale,
        ulong sceneId,
        ulong generation,
        Vector4 clearColor,
        bool preserveTarget,
        NativeSceneDamageRect? damage)
    {
        if (damage is { } value &&
            (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
             !float.IsFinite(value.Width) || !float.IsFinite(value.Height) ||
             value.Width <= 0f || value.Height <= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(damage));
        }
        var frame = new NativeMethods.SceneFrame
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.SceneFrame>(),
            Width = target.Width,
            Height = target.Height,
            DpiScale = dpiScale,
            TargetView = target.TextureView,
            ClearColor = new NativeMethods.NativeColor
            {
                R = clearColor.X,
                G = clearColor.Y,
                B = clearColor.Z,
                A = clearColor.W
            },
            SceneId = sceneId,
            Generation = generation,
            Flags = (preserveTarget ? SceneFramePreserveTargetFlag : 0U) |
                (damage.HasValue ? SceneFrameDamageRectFlag : 0U),
            DamageX = damage?.X ?? 0f,
            DamageY = damage?.Y ?? 0f,
            DamageWidth = damage?.Width ?? 0f,
            DamageHeight = damage?.Height ?? 0f
        };
        var metrics = new NativeMethods.SceneFrameMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.SceneFrameMetrics>()
        };
        lock (_context.RenderLock)
        {
            ThrowIfGpuUnavailable();
            var status = NativeRendererInterop.RenderScene(
                _interopKind, _engine, &frame, &metrics);
            if (status != NativeRendererStatus.Success)
            {
                throw new NativeRendererException(status, ReadLastError());
            }
        }
        return new NativeSceneFrameMetrics(
            metrics.CommandCount,
            metrics.DrawCallCount,
            metrics.FamilySwitchCount,
            metrics.SubmissionCount,
            metrics.VertexUploadBytes,
            metrics.IndexUploadBytes,
            metrics.TextureUploadBytes,
            metrics.UniformUploadBytes,
            metrics.CoverageStagingBytes,
            metrics.PayloadHash,
            metrics.BrushUploadBytes,
            metrics.GradientStopUploadBytes,
            metrics.TextStyleUploadBytes,
            metrics.ColorGlyphUploadBytes);
    }

    /// <summary>
    /// Runs the native pointer-free stream validator without creating or
    /// mutating a renderer instance.
    /// </summary>
    public static NativeSceneUpdateMetrics ValidateScene(
        ReadOnlySpan<byte> stream) =>
        ValidateScene(stream, NativeRendererInteropKind.WgpuNative);

    internal static NativeSceneUpdateMetrics ValidateScene(
        ReadOnlySpan<byte> stream,
        NativeRendererInteropKind interopKind)
    {
        if (stream.IsEmpty)
        {
            throw new ArgumentException(
                "A semantic scene stream cannot be empty.",
                nameof(stream));
        }
        var metrics = new NativeMethods.SceneMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.SceneMetrics>()
        };
        fixed (byte* streamPointer = stream)
        {
            var status = NativeRendererInterop.ValidateScene(
                interopKind,
                streamPointer,
                (nuint)stream.Length,
                &metrics);
            if (status != NativeRendererStatus.Success)
            {
                throw new NativeRendererException(
                    status,
                    $"The semantic scene stream failed validation at byte {metrics.ErrorOffset} ({metrics.ValidationError}).");
            }
        }
        return ToSceneMetrics(metrics);
    }

    public NativeFrameMetrics Render(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeSolidRectangle> rectangles,
        Vector4 clearColor,
        NativeDrawState drawState = default)
    {
        ValidateTarget(target);
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);

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
                RectangleCount = (nuint)rectangles.Length,
                DrawState = &nativeDrawState
            };
            var metrics = new NativeMethods.FrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.FrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfGpuUnavailable();
                var status = NativeRendererInterop.Render(
                    _interopKind, _engine, &frame, &metrics);
                GC.KeepAlive(drawState.GroupMask.ClipChain);
                GC.KeepAlive(drawState.GroupEffectChain);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
            }
            return new NativeFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.VertexUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount);
        }
    }

    public NativeAnalyticFrameMetrics RenderAnalytic(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeAnalyticPrimitive> primitives,
        Vector4 clearColor,
        NativeDrawState drawState = default)
    {
        ValidateTarget(target);
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);

        fixed (NativeAnalyticPrimitive* primitivePointer = primitives)
        {
            var frame = new NativeMethods.AnalyticFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.AnalyticFrame>(),
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
                Primitives = primitivePointer,
                PrimitiveCount = (nuint)primitives.Length,
                DrawState = &nativeDrawState
            };
            var metrics = new NativeMethods.AnalyticFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.AnalyticFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfGpuUnavailable();
                var status = NativeRendererInterop.RenderAnalytic(
                    _interopKind,
                    _engine,
                    &frame,
                    &metrics);
                GC.KeepAlive(drawState.GroupMask.ClipChain);
                GC.KeepAlive(drawState.GroupEffectChain);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
            }
            return new NativeAnalyticFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount);
        }
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0,
        NativeDrawState drawState = default)
    {
        return RenderGeometry(
            target,
            dpiScale,
            primitives,
            ReadOnlySpan<Vector2>.Empty,
            ReadOnlySpan<NativePolyline>.Empty,
            ReadOnlySpan<double>.Empty,
            ReadOnlySpan<NativeDashStyle>.Empty,
            ReadOnlySpan<NativeSpline>.Empty,
            clearColor,
            capturePayloadHash,
            contentRevision,
            drawState);
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<NativePolyline> polylines,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0,
        NativeDrawState drawState = default)
    {
        return RenderGeometry(
            target,
            dpiScale,
            primitives,
            points,
            polylines,
            ReadOnlySpan<double>.Empty,
            ReadOnlySpan<NativeDashStyle>.Empty,
            ReadOnlySpan<NativeSpline>.Empty,
            clearColor,
            capturePayloadHash,
            contentRevision,
            drawState);
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<NativePolyline> polylines,
        ReadOnlySpan<double> doubles,
        ReadOnlySpan<NativeSpline> splines,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0,
        NativeDrawState drawState = default)
    {
        return RenderGeometry(
            target,
            dpiScale,
            primitives,
            points,
            polylines,
            doubles,
            ReadOnlySpan<NativeDashStyle>.Empty,
            splines,
            clearColor,
            capturePayloadHash,
            contentRevision,
            drawState);
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<NativePolyline> polylines,
        ReadOnlySpan<double> doubles,
        ReadOnlySpan<NativeDashStyle> dashStyles,
        ReadOnlySpan<NativeSpline> splines,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0,
        NativeDrawState drawState = default)
    {
        ValidateTarget(target);
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);

        fixed (NativeGeometryPrimitive* primitivePointer = primitives)
        fixed (Vector2* pointPointer = points)
        fixed (NativePolyline* polylinePointer = polylines)
        fixed (double* doublePointer = doubles)
        fixed (NativeDashStyle* dashStylePointer = dashStyles)
        fixed (NativeSpline* splinePointer = splines)
        {
            var frame = new NativeMethods.GeometryFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GeometryFrame>(),
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
                Primitives = primitivePointer,
                PrimitiveCount = (nuint)primitives.Length,
                Flags = (capturePayloadHash
                        ? NativeMethods.GeometryFrameCapturePayloadHash
                        : 0U) |
                    (contentRevision != 0U
                        ? NativeMethods.GeometryFrameRetainCompiledPayload
                        : 0U),
                Reserved = contentRevision,
                Points = pointPointer,
                PointCount = (nuint)points.Length,
                Polylines = polylinePointer,
                PolylineCount = (nuint)polylines.Length,
                Doubles = doublePointer,
                DoubleCount = (nuint)doubles.Length,
                DashStyles = dashStylePointer,
                DashStyleCount = (nuint)dashStyles.Length,
                Splines = splinePointer,
                SplineCount = (nuint)splines.Length,
                DrawState = &nativeDrawState
            };
            var metrics = new NativeMethods.GeometryFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GeometryFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfGpuUnavailable();
                var status = NativeRendererInterop.RenderGeometry(
                    _interopKind,
                    _engine,
                    &frame,
                    &metrics);
                GC.KeepAlive(drawState.GroupMask.ClipChain);
                GC.KeepAlive(drawState.GroupEffectChain);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
            }
            return new NativeGeometryFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.BrushUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash);
        }
    }

    public NativePathFrameMetrics RenderPaths(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativePathFill> paths,
        ReadOnlySpan<NativePathSegment> segments,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0,
        NativeDrawState drawState = default,
        ReadOnlySpan<NativePathBooleanNode> booleanNodes = default,
        NativeSignedWindingExecutionPreference signedWindingExecution =
            NativeSignedWindingExecutionPreference.Fastest)
    {
        ValidateTarget(target);
        if (!Enum.IsDefined(signedWindingExecution))
        {
            throw new ArgumentOutOfRangeException(
                nameof(signedWindingExecution));
        }
        NativeSignedWindingExecutionPath signedWindingExecutionPath =
            signedWindingExecution ==
                NativeSignedWindingExecutionPreference.StagedVectorCompute
                ? NativeSignedWindingExecutionPath.StagedVectorCompute
                : NativeSignedWindingExecutionPath.InlineVectorCompute;
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);

        fixed (NativePathFill* pathPointer = paths)
        fixed (NativePathSegment* segmentPointer = segments)
        fixed (NativePathBooleanNode* booleanNodePointer = booleanNodes)
        {
            var frame = new NativeMethods.PathFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.PathFrame>(),
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
                Paths = pathPointer,
                PathCount = (nuint)paths.Length,
                Segments = segmentPointer,
                SegmentCount = (nuint)segments.Length,
                Flags = (capturePayloadHash
                        ? NativeMethods.GeometryFrameCapturePayloadHash
                        : 0U) |
                    (contentRevision != 0U
                        ? NativeMethods.GeometryFrameRetainCompiledPayload
                        : 0U) |
                    (signedWindingExecutionPath ==
                        NativeSignedWindingExecutionPath.StagedVectorCompute
                        ? NativeMethods.PathFrameStagedSignedWinding
                        : 0U),
                ContentRevision = contentRevision,
                DrawState = &nativeDrawState,
                BooleanNodes = booleanNodePointer,
                BooleanNodeCount = (nuint)booleanNodes.Length
            };
            var metrics = new NativeMethods.PathFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.PathFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfGpuUnavailable();
                var status = NativeRendererInterop.RenderPaths(
                    _interopKind, _engine, &frame, &metrics);
                GC.KeepAlive(drawState.GroupMask.ClipChain);
                GC.KeepAlive(drawState.GroupEffectChain);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
            }
            return new NativePathFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.RasterizedPathCount,
                metrics.AtlasWidth,
                metrics.AtlasHeight,
                metrics.AtlasGeneration,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.BrushUploadBytes,
                metrics.PathUploadBytes,
                metrics.CoverageStagingBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash,
                signedWindingExecutionPath);
        }
    }

    public NativeGlyphFrameMetrics RenderGlyphs(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGlyphOutline> outlines,
        ReadOnlySpan<NativePathSegment> segments,
        ReadOnlySpan<NativePositionedGlyph> glyphs,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0,
        NativeDrawState drawState = default)
    {
        ValidateTarget(target);
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);

        fixed (NativeGlyphOutline* outlinePointer = outlines)
        fixed (NativePathSegment* segmentPointer = segments)
        fixed (NativePositionedGlyph* glyphPointer = glyphs)
        {
            var frame = new NativeMethods.GlyphFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GlyphFrame>(),
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
                Outlines = outlinePointer,
                OutlineCount = (nuint)outlines.Length,
                Segments = segmentPointer,
                SegmentCount = (nuint)segments.Length,
                Glyphs = glyphPointer,
                GlyphCount = (nuint)glyphs.Length,
                Flags = (capturePayloadHash
                        ? NativeMethods.GeometryFrameCapturePayloadHash
                        : 0U) |
                    (contentRevision != 0U
                        ? NativeMethods.GeometryFrameRetainCompiledPayload
                        : 0U),
                ContentRevision = contentRevision,
                DrawState = &nativeDrawState
            };
            var metrics = new NativeMethods.GlyphFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GlyphFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfGpuUnavailable();
                var status = NativeRendererInterop.RenderGlyphs(
                    _interopKind,
                    _engine,
                    &frame,
                    &metrics);
                GC.KeepAlive(drawState.GroupMask.ClipChain);
                GC.KeepAlive(drawState.GroupEffectChain);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
            }
            return new NativeGlyphFrameMetrics(
                metrics.DrawCallCount,
                metrics.GlyphCount,
                metrics.RasterizedGlyphCount,
                metrics.AtlasWidth,
                metrics.AtlasHeight,
                metrics.AtlasGeneration,
                metrics.AtlasGrowthCount,
                metrics.InstanceUploadBytes,
                metrics.OutlineUploadBytes,
                metrics.CoverageStagingBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash);
        }
    }

    /// <summary>
    /// Uploads a retained straight-alpha RGBA8 image and renders it through
    /// the native WebGPU image pipeline.
    /// </summary>
    /// <remarks>
    /// The sampling parameters match the managed compositor contract. Only
    /// <see cref="NativeImageSampling.LinearMipmap"/> accepts anisotropy from
    /// one through sixteen; zero canonicalizes to one. Upload-backed images
    /// expose their base mip only; this API does not generate a mip chain.
    /// </remarks>
    public NativeImageFrameMetrics RenderImage(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<byte> rgbaPixels,
        uint imageWidth,
        uint imageHeight,
        uint rowBytes,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        Vector4 clearColor,
        uint imageRevision,
        uint contentRevision,
        NativeDrawState drawState = default) =>
        RenderImage(
            target,
            dpiScale,
            rgbaPixels,
            imageWidth,
            imageHeight,
            rowBytes,
            sourceRect,
            destinationRect,
            transform,
            opacity,
            sampling,
            clearColor,
            imageRevision,
            contentRevision,
            drawState,
            1,
            0f,
            0.5f);

    public NativeImageFrameMetrics RenderImage(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<byte> rgbaPixels,
        uint imageWidth,
        uint imageHeight,
        uint rowBytes,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        Vector4 clearColor,
        uint imageRevision,
        uint contentRevision,
        NativeDrawState drawState = default,
        byte maxAnisotropy = 1,
        float cubicB = 0f,
        float cubicC = 0.5f)
    {
        ValidateTarget(target);
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);

        fixed (byte* pixelPointer = rgbaPixels)
        {
            var frame = new NativeMethods.ImageFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrame>(),
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
                RgbaPixels = pixelPointer,
                PixelBytes = (nuint)rgbaPixels.Length,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                RowBytes = rowBytes,
                Sampling = sampling,
                ImageRevision = imageRevision,
                ContentRevision = contentRevision,
                SourceRect = sourceRect,
                DestinationRect = destinationRect,
                Transform = transform,
                Opacity = opacity,
                Reserved = 0U,
                ExternalSourceView = 0U,
                SourceFlags = 0U,
                Reserved2 = 0U,
                DrawState = &nativeDrawState,
                CubicB = cubicB,
                CubicC = cubicC,
                MaxAnisotropy = maxAnisotropy,
                Reserved3 = 0U
            };
            var metrics = new NativeMethods.ImageFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfGpuUnavailable();
                var status = NativeRendererInterop.RenderImage(
                    _interopKind,
                    _engine,
                    &frame,
                    &metrics);
                GC.KeepAlive(drawState.GroupMask.ClipChain);
                GC.KeepAlive(drawState.GroupEffectChain);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
            }
            return new NativeImageFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.TextureGeneration,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.TextureUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash);
        }
    }

    /// <summary>
    /// Samples an existing texture from the compositor's WebGPU device
    /// without transferring its pixels through the native ABI.
    /// </summary>
    /// <remarks>
    /// The native renderer retains the source view until another image source
    /// replaces it or this compositor is disposed. Keep <paramref name="source"/>
    /// alive and undisposed for that interval. Increment
    /// <paramref name="sourceRevision"/> after producer work changes the source
    /// contents and increment <paramref name="contentRevision"/> when the draw
    /// rectangle, transform, opacity, or sampling state changes. The complete
    /// managed sampler contract is supported. Mipmap modes sample mip levels
    /// exposed by the producer-owned <paramref name="source"/> view; the
    /// renderer does not generate them.
    /// </remarks>
    public NativeImageFrameMetrics RenderExternalImage(
        GpuTexture target,
        GpuTexture source,
        float dpiScale,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        Vector4 clearColor,
        uint sourceRevision,
        uint contentRevision,
        NativeDrawState drawState = default) =>
        RenderExternalImage(
            target,
            source,
            dpiScale,
            sourceRect,
            destinationRect,
            transform,
            opacity,
            sampling,
            clearColor,
            sourceRevision,
            contentRevision,
            drawState,
            1,
            0f,
            0.5f);

    public NativeImageFrameMetrics RenderExternalImage(
        GpuTexture target,
        GpuTexture source,
        float dpiScale,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        Vector4 clearColor,
        uint sourceRevision,
        uint contentRevision,
        NativeDrawState drawState = default,
        byte maxAnisotropy = 1,
        float cubicB = 0f,
        float cubicC = 0.5f)
    {
        ValidateTarget(target);
        ValidateImageSource(source, target);
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);
        var frame = new NativeMethods.ImageFrame
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrame>(),
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
            RgbaPixels = null,
            PixelBytes = 0U,
            ImageWidth = source.Width,
            ImageHeight = source.Height,
            RowBytes = 0U,
            Sampling = sampling,
            ImageRevision = sourceRevision,
            ContentRevision = contentRevision,
            SourceRect = sourceRect,
            DestinationRect = destinationRect,
            Transform = transform,
            Opacity = opacity,
            Reserved = 0U,
            ExternalSourceView = (nuint)source.ViewPtr,
            SourceFlags = ExternalImageSourceViewFlag,
            Reserved2 = 0U,
            ExternalMaskView = 0U,
            MaskWidth = 0U,
            MaskHeight = 0U,
            MaskDestinationRect = default,
            MaskRevision = 0U,
            MaskSampling = NativeImageSampling.Nearest,
            DrawState = &nativeDrawState,
            CubicB = cubicB,
            CubicC = cubicC,
            MaxAnisotropy = maxAnisotropy,
            Reserved3 = 0U
        };
        var metrics = new NativeMethods.ImageFrameMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrameMetrics>()
        };

        lock (_context.RenderLock)
        {
            ThrowIfGpuUnavailable();
            var status = NativeRendererInterop.RenderImage(
                _interopKind, _engine, &frame, &metrics);
            GC.KeepAlive(drawState.GroupMask.ClipChain);
            GC.KeepAlive(drawState.GroupEffectChain);
            if (status != NativeRendererStatus.Success)
            {
                throw new NativeRendererException(status, ReadLastError());
            }
            target.NotifyExternalContentChanged();
        }
        return new NativeImageFrameMetrics(
            metrics.DrawCallCount,
            metrics.VertexCount,
            metrics.IndexCount,
            metrics.TextureGeneration,
            metrics.VertexUploadBytes,
            metrics.IndexUploadBytes,
            metrics.TextureUploadBytes,
            metrics.UniformUploadBytes,
            metrics.SubmissionCount,
            metrics.PayloadHash);
    }

    /// <summary>
    /// Samples a same-device image through a same-device texture opacity mask
    /// without transferring either payload through the native ABI.
    /// </summary>
    /// <remarks>
    /// The mask red channel is mapped over <paramref name="maskDestinationRect"/>
    /// in logical target coordinates and multiplies source alpha. Both source
    /// views remain retained until replacement or compositor disposal, so both
    /// textures must remain alive for that interval. Source sampling supports
    /// the complete managed sampler contract; mask coverage intentionally
    /// remains nearest or linear.
    /// </remarks>
    public NativeImageFrameMetrics RenderMaskedExternalImage(
        GpuTexture target,
        GpuTexture source,
        GpuTexture mask,
        float dpiScale,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        NativeImageRect maskDestinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        NativeImageSampling maskSampling,
        Vector4 clearColor,
        uint sourceRevision,
        uint maskRevision,
        uint contentRevision,
        NativeDrawState drawState = default) =>
        RenderMaskedExternalImage(
            target,
            source,
            mask,
            dpiScale,
            sourceRect,
            destinationRect,
            maskDestinationRect,
            transform,
            opacity,
            sampling,
            maskSampling,
            clearColor,
            sourceRevision,
            maskRevision,
            contentRevision,
            drawState,
            1,
            0f,
            0.5f);

    public NativeImageFrameMetrics RenderMaskedExternalImage(
        GpuTexture target,
        GpuTexture source,
        GpuTexture mask,
        float dpiScale,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        NativeImageRect maskDestinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        NativeImageSampling maskSampling,
        Vector4 clearColor,
        uint sourceRevision,
        uint maskRevision,
        uint contentRevision,
        NativeDrawState drawState = default,
        byte maxAnisotropy = 1,
        float cubicB = 0f,
        float cubicC = 0.5f)
    {
        ValidateTarget(target);
        ValidateImageSource(source, target);
        ValidateImageMask(mask, target);
        NativeMethods.GroupMask nativeGroupMask = default;
        NativeMethods.ClipChain nativeClipChain = default;
        NativeMethods.GroupEffect nativeGroupEffect = default;
        NativeMethods.GroupEffectChain nativeGroupEffectChain = default;
        NativeMethods.GroupEffect* nativeGroupEffects = stackalloc
            NativeMethods.GroupEffect[NativeGroupEffectChain.MaximumEffectCount];
        var nativeDrawState = CreateDrawState(
            drawState,
            target,
            &nativeGroupMask,
            &nativeClipChain,
            &nativeGroupEffect,
            &nativeGroupEffectChain,
            nativeGroupEffects);
        var frame = new NativeMethods.ImageFrame
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrame>(),
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
            ImageWidth = source.Width,
            ImageHeight = source.Height,
            Sampling = sampling,
            ImageRevision = sourceRevision,
            ContentRevision = contentRevision,
            SourceRect = sourceRect,
            DestinationRect = destinationRect,
            Transform = transform,
            Opacity = opacity,
            ExternalSourceView = (nuint)source.ViewPtr,
            SourceFlags = ExternalImageSourceViewFlag,
            ExternalMaskView = (nuint)mask.ViewPtr,
            MaskWidth = mask.Width,
            MaskHeight = mask.Height,
            MaskDestinationRect = maskDestinationRect,
            MaskRevision = maskRevision,
            MaskSampling = maskSampling,
            DrawState = &nativeDrawState,
            CubicB = cubicB,
            CubicC = cubicC,
            MaxAnisotropy = maxAnisotropy,
            Reserved3 = 0U
        };
        var metrics = new NativeMethods.ImageFrameMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrameMetrics>()
        };

        lock (_context.RenderLock)
        {
            ThrowIfGpuUnavailable();
            var status = NativeRendererInterop.RenderImage(
                _interopKind, _engine, &frame, &metrics);
            GC.KeepAlive(drawState.GroupMask.ClipChain);
            GC.KeepAlive(drawState.GroupEffectChain);
            if (status != NativeRendererStatus.Success)
            {
                throw new NativeRendererException(status, ReadLastError());
            }
            target.NotifyExternalContentChanged();
        }
        return new NativeImageFrameMetrics(
            metrics.DrawCallCount,
            metrics.VertexCount,
            metrics.IndexCount,
            metrics.TextureGeneration,
            metrics.VertexUploadBytes,
            metrics.IndexUploadBytes,
            metrics.TextureUploadBytes,
            metrics.UniformUploadBytes,
            metrics.SubmissionCount,
            metrics.PayloadHash);
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
            NativeRendererInterop.Destroy(_interopKind, engine);
        }
    }

    private bool PollSubmission(NativeSubmissionToken token, bool wait)
    {
        if (!token.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(token),
                "A native submission token must be non-zero.");
        }

        lock (_context.RenderLock)
        {
            ThrowIfGpuUnavailable();
            if (token.Owner != _engine)
            {
                throw new ArgumentException(
                    "A native submission token belongs to a different compositor.",
                    nameof(token));
            }
            byte complete = 0;
            ThrowForStatus(NativeRendererInterop.PollSubmission(
                _interopKind,
                _engine,
                token.Value,
                wait ? (byte)1 : (byte)0,
                &complete));
            return complete != 0;
        }
    }

    private void ThrowForStatus(NativeRendererStatus status)
    {
        if (status != NativeRendererStatus.Success)
        {
            throw new NativeRendererException(status, ReadLastError());
        }
    }

    private string ReadLastError()
    {
        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* pointer = buffer)
        {
            var required = NativeRendererInterop.GetLastError(
                _interopKind,
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

    private void ThrowIfGpuUnavailable()
    {
        ThrowIfDisposed();
        if (!_context.IsDeviceLost)
        {
            return;
        }
        ThrowForStatus(NativeRendererInterop.MarkDeviceLost(
            _interopKind, _engine));
        throw new NativeRendererException(
            NativeRendererStatus.DeviceLost,
            ReadLastError());
    }

    private void ValidateTarget(GpuTexture target)
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
        if ((target.Usage & TextureUsage.RenderAttachment) == 0)
        {
            throw new ArgumentException(
                "The target must allow WebGPU render-attachment usage.",
                nameof(target));
        }
    }

    private void ValidateExternalTarget(NativeSceneExternalTarget target)
    {
        ThrowIfDisposed();
        if (target.TextureView == 0)
        {
            throw new ArgumentException(
                "A live host-owned WebGPU texture view is required.",
                nameof(target));
        }
        if (target.Width == 0 || target.Height == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                "A host-owned scene target requires nonzero dimensions.");
        }
    }

    private void ValidateImageSource(GpuTexture source, GpuTexture target)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, target))
        {
            throw new ArgumentException(
                "An external image source cannot also be the active render target.",
                nameof(source));
        }
        if (!ReferenceEquals(source.Context, _context))
        {
            throw new ArgumentException(
                "The external image source must belong to the native compositor's WebGPU device domain.",
                nameof(source));
        }
        if (source.IsDisposed || source.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(source));
        }
        if ((source.Usage & TextureUsage.TextureBinding) == 0 ||
            source.SampleCount != 1 ||
            source.AlphaMode != GpuTextureAlphaMode.Straight ||
            source.Format is not (
                TextureFormat.Rgba8Unorm or
                TextureFormat.Bgra8Unorm or
                TextureFormat.Rgba8UnormSrgb or
                TextureFormat.Bgra8UnormSrgb))
        {
            throw new ArgumentException(
                "The first external image lane requires a single-sample bindable straight-alpha RGBA/BGRA 8-bit texture.",
                nameof(source));
        }
    }

    private void ValidateSceneExternalImageSource(
        GpuTexture source,
        NativeSceneExternalImageRole role)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (!ReferenceEquals(source.Context, _context))
        {
            throw new ArgumentException(
                "The external scene image must belong to the native compositor's WebGPU device domain.",
                nameof(source));
        }
        if (source.IsDisposed || source.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(source));
        }
        bool supportedFormat = role switch
        {
            NativeSceneExternalImageRole.Primary => source.Format is
                TextureFormat.Rgba8Unorm or
                TextureFormat.Bgra8Unorm or
                TextureFormat.Rgba8UnormSrgb or
                TextureFormat.Bgra8UnormSrgb or
                TextureFormat.R8Unorm ||
                source.Format == ProGpuTextureFormats.R16Unorm,
            NativeSceneExternalImageRole.Chroma =>
                source.Format == TextureFormat.RG8Unorm ||
                source.Format == ProGpuTextureFormats.RG16Unorm,
            NativeSceneExternalImageRole.Mask =>
                source.Format == TextureFormat.R8Unorm,
            _ => false
        };
        bool supportedAlphaMode = role ==
            NativeSceneExternalImageRole.Primary
                ? source.AlphaMode is
                    GpuTextureAlphaMode.Straight or
                    GpuTextureAlphaMode.Premultiplied
                : source.AlphaMode == GpuTextureAlphaMode.Straight;
        if ((source.Usage & TextureUsage.TextureBinding) == 0 ||
            source.Dimension != GpuTextureDimension.Dimension2D ||
            source.DepthOrArrayLayers != 1 || source.SampleCount != 1 ||
            !supportedAlphaMode ||
            !supportedFormat)
        {
            throw new ArgumentException(
                "External scene images require a role-compatible single-sample bindable 2D texture; primary images may be straight or premultiplied while chroma and mask planes must be straight alpha.",
                nameof(source));
        }
    }

    private void ValidateImageMask(GpuTexture mask, GpuTexture target)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (ReferenceEquals(mask, target))
        {
            throw new ArgumentException(
                "An image mask cannot also be the active render target.",
                nameof(mask));
        }
        if (!ReferenceEquals(mask.Context, _context))
        {
            throw new ArgumentException(
                "The image mask must belong to the native compositor's WebGPU device domain.",
                nameof(mask));
        }
        if (mask.IsDisposed || mask.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(mask));
        }
        if ((mask.Usage & TextureUsage.TextureBinding) == 0 ||
            mask.SampleCount != 1 ||
            mask.Format is not (
                TextureFormat.R8Unorm or
                TextureFormat.Rgba8Unorm or
                TextureFormat.Bgra8Unorm))
        {
            throw new ArgumentException(
                "The initial native image-mask lane requires a single-sample bindable R8/RGBA/BGRA unorm texture.",
                nameof(mask));
        }
    }

    private static NativeDawnMethods.EngineOptions CreateDawnOptions(
        WgpuContext context,
        TextureFormat targetFormat,
        nuint instance,
        nuint device,
        nuint queue,
        nint resolverContext,
        nint resolveProc) => new()
        {
            StructSize = (uint)Unsafe.SizeOf<NativeDawnMethods.EngineOptions>(),
            NativeAbiVersion = NativeMethods.AbiVersion,
            AdapterAbiVersion = NativeDawnMethods.AdapterAbiVersion,
            ProviderAbiVersion = NativeDawnMethods.RequiredProviderAbiVersion,
            TargetFormat = ToNativeFormat(targetFormat),
            ResolverContext = resolverContext,
            ResolveProc = resolveProc,
            Instance = instance,
            Device = device,
            Queue = queue,
            Flags = GetEngineFlags(context)
        };

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

    private NativeMethods.DrawState CreateDrawState(
        NativeDrawState state,
        GpuTexture target,
        NativeMethods.GroupMask* nativeGroupMask,
        NativeMethods.ClipChain* nativeClipChain,
        NativeMethods.GroupEffect* nativeGroupEffect,
        NativeMethods.GroupEffectChain* nativeGroupEffectChain,
        NativeMethods.GroupEffect* nativeGroupEffects)
    {
        nuint groupMaskPointer = 0U;
        if (state.GroupMask.IsEnabled)
        {
            *nativeGroupMask = CreateGroupMask(
                state.GroupMask,
                target,
                nativeClipChain);
            groupMaskPointer = (nuint)nativeGroupMask;
        }

        nuint groupEffectPointer = 0U;
        nuint groupEffectChainPointer = 0U;
        if (state.GroupEffect.IsEnabled && state.GroupEffectChain is not null)
        {
            throw new ArgumentException(
                "A native draw state cannot specify both one effect and an effect chain.",
                nameof(state));
        }
        if (state.GroupEffect.IsEnabled)
        {
            *nativeGroupEffect = CreateGroupEffect(state.GroupEffect);
            groupEffectPointer = (nuint)nativeGroupEffect;
        }
        else if (state.GroupEffectChain is { } chain)
        {
            ReadOnlySpan<NativeGroupEffect> effects = chain.Effects;
            for (int index = 0; index < effects.Length; index++)
            {
                nativeGroupEffects[index] = CreateGroupEffect(effects[index]);
            }
            *nativeGroupEffectChain = new NativeMethods.GroupEffectChain
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GroupEffectChain>(),
                EffectCount = (uint)effects.Length,
                Revision = chain.Revision,
                Effects = nativeGroupEffects
            };
            groupEffectChainPointer = (nuint)nativeGroupEffectChain;
        }

        return new NativeMethods.DrawState
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.DrawState>(),
            Flags = (uint)state.Flags,
            Opacity = state.EffectiveOpacity,
            Reserved = 0U,
            ClipRect = state.ClipRect,
            GroupOpacity = state.EffectiveGroupOpacity,
            GroupRevision = state.GroupRevision,
            GroupMask = groupMaskPointer,
            GroupEffect = groupEffectPointer,
            GroupEffectChain = groupEffectChainPointer,
            GroupBlendMode = state.EffectiveGroupBlendMode,
            Reserved2 = 0U
        };
    }

    private static ulong GetEngineFlags(WgpuContext context) =>
        (context.ImageSamplingPreference == GpuImageSamplingPreference.NativeSampler
            ? NativeMethods.EngineImageRequireNativeSampling : 0UL) |
        (context.ImageSamplingPath == GpuImageSamplingPath.ExplicitShader
            ? NativeMethods.EngineImageExplicitShaderSampling : 0UL) |
        (context.GlyphRasterizationPath switch
        {
            GpuComputeExecutionPath.NativeCompute => 0UL,
            GpuComputeExecutionPath.RasterShader =>
                NativeMethods.EngineGlyphRasterShaderFallback,
            GpuComputeExecutionPath.IntrinsicSimdCpu =>
                NativeMethods.EngineGlyphIntrinsicSimdCpuFallback,
            GpuComputeExecutionPath.ScalarCpu =>
                NativeMethods.EngineGlyphScalarCpuFallback,
            _ => throw new ArgumentOutOfRangeException(nameof(context))
        });

    private static NativeMethods.GroupEffect CreateGroupEffect(
        NativeGroupEffect effect)
    {
        const float MaximumSigma = 128f / 3f;
        bool isGaussian = effect.Kind == NativeGroupEffectKind.GaussianBlur;
        bool isBox = effect.Kind == NativeGroupEffectKind.BoxBlur;
        bool isDropShadow = effect.Kind == NativeGroupEffectKind.DropShadow;
        bool invalidColor = !float.IsFinite(effect.Color.X) ||
            !float.IsFinite(effect.Color.Y) ||
            !float.IsFinite(effect.Color.Z) ||
            !float.IsFinite(effect.Color.W) ||
            effect.Color.X < 0f || effect.Color.X > 1f ||
            effect.Color.Y < 0f || effect.Color.Y > 1f ||
            effect.Color.Z < 0f || effect.Color.Z > 1f ||
            effect.Color.W < 0f || effect.Color.W > 1f;
        float maximumExtent = isBox ? 128f : MaximumSigma;
        bool requiresPositiveExtent = isGaussian || isBox;
        if ((!isGaussian && !isBox && !isDropShadow) ||
            !float.IsFinite(effect.SigmaX) ||
            !float.IsFinite(effect.SigmaY) ||
            effect.SigmaX < (requiresPositiveExtent ? 0.01f : 0f) ||
            effect.SigmaX > maximumExtent ||
            effect.SigmaY < (requiresPositiveExtent ? 0.01f : 0f) ||
            effect.SigmaY > maximumExtent ||
            (isDropShadow &&
             (!float.IsFinite(effect.Offset.X) ||
              !float.IsFinite(effect.Offset.Y) || invalidColor)) ||
            effect.Revision == 0U)
        {
            throw new ArgumentException(
                "A native group effect requires valid finite parameters, bounded blur extent, normalized drop-shadow color, and a nonzero revision.",
                nameof(effect));
        }

        return new NativeMethods.GroupEffect
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.GroupEffect>(),
            Kind = effect.Kind,
            Revision = effect.Revision,
            SigmaX = effect.SigmaX,
            SigmaY = effect.SigmaY,
            OffsetX = effect.Offset.X,
            OffsetY = effect.Offset.Y,
            ColorR = effect.Color.X,
            ColorG = effect.Color.Y,
            ColorB = effect.Color.Z,
            ColorA = effect.Color.W
        };
    }

    private NativeMethods.GroupMask CreateGroupMask(
        NativeGroupMask mask,
        GpuTexture target,
        NativeMethods.ClipChain* nativeClipChain)
    {
        switch (mask.Kind)
        {
            case NativeGroupMaskKind.Texture:
            {
                GpuTexture texture = mask.Texture ?? throw new ArgumentException(
                    "A native texture group mask requires a texture.",
                    nameof(mask));
                ValidateGroupMaskTexture(texture, target);
                if (!IsFinitePositiveRect(mask.DestinationRect) ||
                    mask.Sampling is not (
                        NativeImageSampling.Nearest or
                        NativeImageSampling.Linear) ||
                    mask.Revision == 0U)
                {
                    throw new ArgumentException(
                        "A native texture group mask requires a finite positive destination, supported sampling, and a nonzero revision.",
                        nameof(mask));
                }

                return new NativeMethods.GroupMask
                {
                    StructSize = (uint)Unsafe.SizeOf<NativeMethods.GroupMask>(),
                    Kind = NativeGroupMaskKind.Texture,
                    ExternalView = (nuint)texture.ViewPtr,
                    Width = texture.Width,
                    Height = texture.Height,
                    Sampling = mask.Sampling,
                    TextureFormat = ToNativeMaskFormat(texture.Format),
                    Revision = mask.Revision,
                    DestinationRect = mask.DestinationRect,
                    Transform = Matrix3x2.Identity,
                    Opacity = 1f
                };
            }
            case NativeGroupMaskKind.RoundedRectangle:
            {
                if (!IsFinitePositiveRect(mask.Bounds) ||
                    !IsFinite(mask.Transform) ||
                    MathF.Abs(mask.Transform.GetDeterminant()) <= 0.000001f ||
                    !IsFiniteNonNegative(mask.CornerRadiiX) ||
                    !IsFiniteNonNegative(mask.CornerRadiiY) ||
                    !float.IsFinite(mask.Opacity) ||
                    mask.Opacity < 0f || mask.Opacity > 1f)
                {
                    throw new ArgumentException(
                        "A native rounded group mask requires finite positive bounds, an invertible transform, nonnegative radii, and opacity in [0,1].",
                        nameof(mask));
                }

                return new NativeMethods.GroupMask
                {
                    StructSize = (uint)Unsafe.SizeOf<NativeMethods.GroupMask>(),
                    Kind = NativeGroupMaskKind.RoundedRectangle,
                    Sampling = NativeImageSampling.Linear,
                    Bounds = mask.Bounds,
                    Transform = mask.Transform,
                    CornerRadiiX = mask.CornerRadiiX,
                    CornerRadiiY = mask.CornerRadiiY,
                    Opacity = mask.Opacity
                };
            }
            case NativeGroupMaskKind.VectorClipChain:
            {
                NativeClipChain chain = mask.ClipChain ??
                    throw new ArgumentException(
                        "A native vector group mask requires a retained clip chain.",
                        nameof(mask));
                if (mask.Revision == 0U)
                {
                    throw new ArgumentException(
                        "A native vector group mask requires a nonzero revision.",
                        nameof(mask));
                }

                *nativeClipChain = new NativeMethods.ClipChain
                {
                    StructSize = (uint)Unsafe.SizeOf<NativeMethods.ClipChain>(),
                    Flags = chain.SignedWindingExecutionPath ==
                        NativeSignedWindingExecutionPath.StagedVectorCompute
                        ? NativeMethods.ClipChainStagedSignedWinding
                        : 0U,
                    Paths = chain.Paths,
                    PathCount = (nuint)chain.PathCount,
                    Segments = chain.Segments,
                    SegmentCount = (nuint)chain.SegmentCount,
                    BooleanNodes = chain.BooleanNodes,
                    BooleanNodeCount = (nuint)chain.BooleanNodeCount
                };
                return new NativeMethods.GroupMask
                {
                    StructSize = (uint)Unsafe.SizeOf<NativeMethods.GroupMask>(),
                    Kind = NativeGroupMaskKind.VectorClipChain,
                    Sampling = NativeImageSampling.Linear,
                    Revision = mask.Revision,
                    Transform = Matrix3x2.Identity,
                    Opacity = 1f,
                    ClipChain = nativeClipChain
                };
            }
            default:
                throw new ArgumentException(
                    "The native group mask kind is unsupported.",
                    nameof(mask));
        }
    }

    private void ValidateGroupMaskTexture(GpuTexture mask, GpuTexture target)
    {
        if (ReferenceEquals(mask, target))
        {
            throw new ArgumentException(
                "A group mask cannot also be the active render target.",
                nameof(mask));
        }
        if (!ReferenceEquals(mask.Context, _context))
        {
            throw new ArgumentException(
                "The group mask must belong to the native compositor's WebGPU device domain.",
                nameof(mask));
        }
        if (mask.IsDisposed || mask.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(mask));
        }
        if ((mask.Usage & TextureUsage.TextureBinding) == 0 ||
            mask.SampleCount != 1 || mask.Width > 16384U ||
            mask.Height > 16384U ||
            mask.Format is not (
                TextureFormat.R8Unorm or
                TextureFormat.Rgba8Unorm or
                TextureFormat.Bgra8Unorm))
        {
            throw new ArgumentException(
                "A native group mask requires a single-sample bindable R8/RGBA/BGRA unorm texture no larger than 16384 pixels per axis.",
                nameof(mask));
        }
    }

    private static NativeMaskTextureFormat ToNativeMaskFormat(
        TextureFormat format) => format switch
        {
            TextureFormat.R8Unorm => NativeMaskTextureFormat.R8Unorm,
            TextureFormat.Rgba8Unorm => NativeMaskTextureFormat.Rgba8Unorm,
            TextureFormat.Bgra8Unorm => NativeMaskTextureFormat.Bgra8Unorm,
            _ => throw new NotSupportedException(
                $"The native group mask does not support {format}.")
        };

    private static bool IsFinitePositiveRect(NativeImageRect rect) =>
        float.IsFinite(rect.X) && float.IsFinite(rect.Y) &&
        float.IsFinite(rect.Width) && float.IsFinite(rect.Height) &&
        rect.Width > 0f && rect.Height > 0f;

    private static bool IsFinite(Matrix3x2 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32);

    private static bool IsFiniteNonNegative(Vector4 value) =>
        float.IsFinite(value.X) && value.X >= 0f &&
        float.IsFinite(value.Y) && value.Y >= 0f &&
        float.IsFinite(value.Z) && value.Z >= 0f &&
        float.IsFinite(value.W) && value.W >= 0f;

    private static NativeSceneUpdateMetrics ToSceneMetrics(
        NativeMethods.SceneMetrics metrics) => new(
            metrics.CommandCount,
            metrics.ResourceCount,
            metrics.DrawCount,
            metrics.MaximumStackDepth,
            metrics.ValidationError,
            metrics.ErrorOffset,
            metrics.SceneId,
            metrics.Generation,
            metrics.SnapshotBytes,
            metrics.PayloadBytes,
            (metrics.Flags & NativeMethods.SceneMetricsSnapshotReused) != 0U);
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
