using ProGPU.Backend;
using ProGPU.Media.Containers;
using Silk.NET.WebGPU;

namespace ProGPU.Linux.Media;

internal enum LinuxMediaOverlayFrameDisposition
{
    Discard,
    Candidate,
    LookAhead
}

internal static class LinuxMediaOverlayFrameSelector
{
    internal static long GetInitialDecodeTicks(
        long trimStartTicks,
        long targetTicks)
    {
        if (trimStartTicks < 0 ||
            targetTicks < trimStartTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetTicks));
        }
        return targetTicks;
    }

    internal static LinuxMediaOverlayFrameDisposition
        Classify(
        long frameTicks,
        long trimStartTicks,
        long trimEndTicks,
        long targetTicks)
    {
        if (trimStartTicks < 0 ||
            trimEndTicks <= trimStartTicks ||
            targetTicks < trimStartTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trimStartTicks));
        }
        if (frameTicks < trimStartTicks ||
            frameTicks >= trimEndTicks)
        {
            return LinuxMediaOverlayFrameDisposition
                .Discard;
        }
        return frameTicks <= targetTicks
            ? LinuxMediaOverlayFrameDisposition
                .Candidate
            : LinuxMediaOverlayFrameDisposition
                .LookAhead;
    }
}

/// <summary>
/// Owns the bounded runtime state for one captured Linux overlay plan.
/// </summary>
/// <remarks>
/// Setup is O(O) time and storage for O overlays. Per-frame preparation is
/// O(O + D) for D decoded overlay frames advanced since the preceding output
/// timestamp and allocates no managed objects after lazy source setup. Each URI
/// overlay retains one reusable RGBA texture, two lazy blur textures only when
/// required, one selected native candidate, and one look-ahead candidate.
/// Storage is independent of composition duration and output-frame count.
/// </remarks>
internal sealed class LinuxMediaOverlayRuntime :
    IDisposable
{
    private readonly LinuxMediaOverlayPlan[] _plans;
    private readonly LinuxV4l2UriOverlayFrameSource?[]
        _uriSources;
    private long _lastCompositionTicks = -1;
    private int _disposed;

    internal LinuxMediaOverlayRuntime(
        IReadOnlyList<LinuxMediaOverlayPlan> plans,
        IReadOnlyList<LinuxVideoDecoderDevice>
            decoderDevices,
        WgpuContext context)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(decoderDevices);
        ArgumentNullException.ThrowIfNull(context);
        _plans = new LinuxMediaOverlayPlan[
            plans.Count];
        _uriSources =
            new LinuxV4l2UriOverlayFrameSource?[
                plans.Count];
        try
        {
            for (int index = 0;
                 index < plans.Count;
                 index++)
            {
                LinuxMediaOverlayPlan plan =
                    plans[index];
                _plans[index] = plan;
                if (plan.IsUri)
                {
                    _uriSources[index] =
                        new LinuxV4l2UriOverlayFrameSource(
                            index,
                            plan,
                            decoderDevices,
                            context);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal ReadOnlySpan<LinuxMediaOverlayPlan>
        Plans => _plans;

    internal bool HasActive(long compositionTicks)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        for (int index = 0;
             index < _plans.Length;
             index++)
        {
            if (_plans[index]
                .IsActive(compositionTicks))
            {
                return true;
            }
        }
        return false;
    }

    internal void Prepare(
        long compositionTicks,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (compositionTicks <
            _lastCompositionTicks)
        {
            throw new InvalidOperationException(
                "Linux overlay decoding requires nondecreasing composition timestamps.");
        }
        _lastCompositionTicks = compositionTicks;
        for (int index = 0;
             index < _plans.Length;
             index++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            LinuxV4l2UriOverlayFrameSource?
                source = _uriSources[index];
            if (source is not null &&
                _plans[index]
                    .IsActive(compositionTicks))
            {
                source.Prepare(
                    compositionTicks,
                    cancellationToken);
            }
        }
    }

    internal bool TryGetUriTexture(
        int index,
        out GpuTexture? texture)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if ((uint)index >=
            (uint)_uriSources.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index));
        }
        LinuxV4l2UriOverlayFrameSource?
            source = _uriSources[index];
        if (source is null)
        {
            texture = null;
            return false;
        }
        return source.TryGetTexture(out texture);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }
        for (int index = 0;
             index < _uriSources.Length;
             index++)
        {
            _uriSources[index]?.Dispose();
            _uriSources[index] = null;
        }
    }
}

/// <summary>
/// Forward-only V4L2 decoder and retained WebGPU image for one local URI
/// overlay.
/// </summary>
/// <remarks>
/// Compressed input and decode work are O(B + D) for B queued bytes and D
/// decoded frames. Selecting a frame retains at most two explicit native
/// capture leases: the latest candidate at or before the requested source
/// timestamp and the first look-ahead candidate after it. Only the selected
/// candidate is imported and effect-processed into the reusable GPU texture.
/// No decoded pixel is CPU mapped or copied.
/// </remarks>
internal sealed unsafe class
    LinuxV4l2UriOverlayFrameSource :
    IDisposable
{
    private readonly int _index;
    private readonly LinuxMediaOverlayPlan _plan;
    private readonly IReadOnlyList<
        LinuxVideoDecoderDevice> _decoderDevices;
    private readonly WgpuContext _context;
    private FileStream? _stream;
    private IsoBmffTrack? _track;
    private IsoBmffNalAccessUnitReader? _reader;
    private V4l2StatefulVideoDecoder? _decoder;
    private int _sampleIndex;
    private bool _decoderDraining;
    private V4l2DecodedFrame _candidate;
    private bool _hasCandidate;
    private V4l2DecodedFrame _lookAhead;
    private bool _hasLookAhead;
    private GpuTexture? _current;
    private GpuTexture? _blurSource;
    private GpuTexture? _blurIntermediate;
    private long _lastSourceTicks = -1;
    private bool _currentReady;
    private int _disposed;

    internal LinuxV4l2UriOverlayFrameSource(
        int index,
        LinuxMediaOverlayPlan plan,
        IReadOnlyList<LinuxVideoDecoderDevice>
            decoderDevices,
        WgpuContext context)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index));
        }
        if (!plan.IsUri)
        {
            throw new ArgumentException(
                "A URI overlay plan is required.",
                nameof(plan));
        }
        ArgumentNullException.ThrowIfNull(
            decoderDevices);
        ArgumentNullException.ThrowIfNull(context);
        _index = index;
        _plan = plan;
        _decoderDevices = decoderDevices;
        _context = context;
    }

    internal void Prepare(
        long compositionTicks,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        long sourceTicks =
            _plan.GetSourceTicks(
                compositionTicks);
        if (sourceTicks < _lastSourceTicks)
        {
            throw new InvalidOperationException(
                "Linux URI-overlay decoding requires nondecreasing source timestamps.");
        }
        if (sourceTicks == _lastSourceTicks)
        {
            return;
        }
        EnsureInitialized(sourceTicks);
        AdvanceTo(
            TimeSpan.FromTicks(sourceTicks),
            cancellationToken);
        _lastSourceTicks = sourceTicks;
    }

    internal bool TryGetTexture(
        out GpuTexture? texture)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        texture = _currentReady
            ? _current
            : null;
        return texture is not null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }
        ReleaseCandidate(
            ref _candidate,
            ref _hasCandidate);
        ReleaseCandidate(
            ref _lookAhead,
            ref _hasLookAhead);
        _decoder?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _decoder = null;
        _reader = null;
        _stream = null;
        _blurIntermediate?.Dispose();
        _blurSource?.Dispose();
        _current?.Dispose();
        _blurIntermediate = null;
        _blurSource = null;
        _current = null;
        _context.CleanupPendingResources();
    }

    private void EnsureInitialized(
        long initialSourceTicks)
    {
        if (_decoder is not null)
        {
            return;
        }
        FileStream? stream = null;
        IsoBmffNalAccessUnitReader? reader = null;
        V4l2StatefulVideoDecoder? decoder = null;
        try
        {
            string sourcePath =
                Path.GetFullPath(
                    _plan.SourceUri!.LocalPath);
            stream =
                new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.RandomAccess);
            IsoBmffTrack track =
                LinuxV4l2PreciseMediaCompositionExportProvider
                    .SelectTrack(
                        new IsoBmffDemuxer(stream)
                            .Parse());
            long sourceDurationTicks =
                GetTrackDurationTicks(track);
            if (_plan.SourceStartTicks < 0 ||
                _plan.SourceEndTrimTicks < 0 ||
                checked(
                    _plan.SourceStartTicks +
                    _plan.SourceEndTrimTicks) >=
                    sourceDurationTicks)
            {
                throw new InvalidDataException(
                    "The URI-overlay trim interval is outside the source duration.");
            }
            LinuxVideoDecoderDevice device =
                SelectDecoder(
                    track,
                    _decoderDevices);
            reader =
                new IsoBmffNalAccessUnitReader(
                    stream,
                    track);
            decoder =
                new V4l2StatefulVideoDecoder(
                    device.Path,
                    track,
                    preferNv12Capture: true);
            decoder.Open();
            _sampleIndex =
                LinuxV4l2PreciseMediaCompositionExportProvider
                    .FindDecodeStart(
                        track,
                        TimeSpan.FromTicks(
                            LinuxMediaOverlayFrameSelector
                                .GetInitialDecodeTicks(
                                _plan.SourceStartTicks,
                                initialSourceTicks)));
            _track = track;
            _stream = stream;
            _reader = reader;
            _decoder = decoder;
            stream = null;
            reader = null;
            decoder = null;
        }
        finally
        {
            decoder?.Dispose();
            reader?.Dispose();
            stream?.Dispose();
        }
    }

    private void AdvanceTo(
        TimeSpan target,
        CancellationToken cancellationToken)
    {
        V4l2StatefulVideoDecoder decoder =
            _decoder!;
        IsoBmffTrack track = _track!;
        IsoBmffNalAccessUnitReader reader =
            _reader!;
        TimeSpan trimStart =
            TimeSpan.FromTicks(
                _plan.SourceStartTicks);
        TimeSpan trimEnd =
            TimeSpan.FromTicks(
                checked(
                    GetTrackDurationTicks(track) -
                    _plan.SourceEndTrimTicks));

        if (_hasLookAhead)
        {
            if (_lookAhead.PresentationTime >
                target)
            {
                return;
            }
            ReplaceCandidate(
                _lookAhead);
            _lookAhead = default;
            _hasLookAhead = false;
        }

        while (!_hasLookAhead &&
               !decoder.EndOfStreamReached)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            while (_sampleIndex <
                   track.Samples.Length)
            {
                ReadOnlySpan<byte> accessUnit =
                    reader.Read(_sampleIndex);
                if (!decoder.TryQueueAccessUnit(
                        accessUnit,
                        LinuxV4l2PreciseMediaCompositionExportProvider
                            .PresentationTime(
                                track,
                                _sampleIndex)))
                {
                    break;
                }
                _sampleIndex++;
            }

            V4l2DecoderPumpResult pump =
                decoder.Pump(
                    timeoutMilliseconds: 4);
            if (pump ==
                V4l2DecoderPumpResult
                    .SourceChanged)
            {
                if (decoder.IsCaptureConfigured)
                {
                    throw new NotSupportedException(
                        "Dynamic URI-overlay source-size changes are not supported.");
                }
                decoder.ConfigureCapture();
                if (decoder.DecodedPixelFormat !=
                        V4l2DecodedPixelFormat
                            .Nv12 ||
                    decoder.CaptureWidth !=
                        track.Width ||
                    decoder.CaptureHeight !=
                        track.Height)
                {
                    throw new NotSupportedException(
                        "The URI-overlay decoder did not expose source-size linear NV12 DMA-BUF output.");
                }
            }

            while (decoder.TryDequeueFrame(
                       out V4l2DecodedFrame frame))
            {
                LinuxMediaOverlayFrameDisposition
                    disposition =
                        LinuxMediaOverlayFrameSelector
                            .Classify(
                                frame.PresentationTime
                                    .Ticks,
                                trimStart.Ticks,
                                trimEnd.Ticks,
                                target.Ticks);
                if (disposition ==
                    LinuxMediaOverlayFrameDisposition
                        .Discard)
                {
                    frame.Owner.Dispose();
                    continue;
                }
                if (disposition ==
                    LinuxMediaOverlayFrameDisposition
                        .Candidate)
                {
                    ReplaceCandidate(frame);
                    continue;
                }
                _lookAhead = frame;
                _hasLookAhead = true;
                break;
            }

            if (_sampleIndex ==
                    track.Samples.Length &&
                decoder.IsCaptureConfigured &&
                !decoder.HasQueuedOutput &&
                !_decoderDraining)
            {
                decoder.BeginDrain();
                _decoderDraining = true;
            }
        }

        if (_hasCandidate)
        {
            V4l2DecodedFrame frame =
                _candidate;
            _candidate = default;
            _hasCandidate = false;
            RenderSelectedFrame(in frame);
        }
    }

    private void ReplaceCandidate(
        V4l2DecodedFrame frame)
    {
        ReleaseCandidate(
            ref _candidate,
            ref _hasCandidate);
        _candidate = frame;
        _hasCandidate = true;
    }

    private void RenderSelectedFrame(
        in V4l2DecodedFrame frame)
    {
        if (frame.PixelFormat !=
                V4l2DecodedPixelFormat.Nv12 ||
            !frame.TryCreatePlanarExternalDescriptors(
                out ProGpuExternalTextureDescriptor
                    lumaDescriptor,
                out ProGpuExternalTextureDescriptor
                    chromaDescriptor))
        {
            frame.Owner.Dispose();
            throw new NotSupportedException(
                "The URI-overlay frame is not sampleable NV12 DMA-BUF.");
        }

        GpuTexture? luma = null;
        GpuTexture? chroma = null;
        var owner =
            new SharedOwnerRoot(frame.Owner);
        SharedOwnerLease? lumaOwner = null;
        SharedOwnerLease? chromaOwner = null;
        try
        {
            EnsureTextures(
                frame.Width,
                frame.Height);
            lumaOwner =
                owner.CreateLease();
            chromaOwner =
                owner.CreateLease();
            if (!_context.TryImportExternalTexture(
                    in lumaDescriptor,
                    lumaOwner,
                    out luma))
            {
                throw new NotSupportedException(
                    "Dawn could not import URI-overlay NV12 luma.");
            }
            lumaOwner = null;
            if (!_context.TryImportExternalTexture(
                    in chromaDescriptor,
                    chromaOwner,
                    out chroma))
            {
                throw new NotSupportedException(
                    "Dawn could not import URI-overlay NV12 chroma.");
            }
            chromaOwner = null;

            if (_plan.EffectPlan.HasSpatialEffect)
            {
                EnsureBlurTextures(
                    frame.Width,
                    frame.Height);
                GpuNv12Processor.ProcessToRgba(
                    luma,
                    chroma,
                    _blurSource!,
                    GpuTextureColorTransform.Identity,
                    inFlightSlot:
                        _index %
                        GpuNv12Processor
                            .MaxInFlightSlots);
                GpuTextureGaussianBlur.Blur(
                    _blurSource!,
                    _blurIntermediate!,
                    _current!.ViewPtr,
                    _current.Format,
                    _plan.EffectPlan
                        .BlurStandardDeviation,
                    _plan.EffectPlan
                        .ColorTransform);
            }
            else
            {
                GpuNv12Processor.ProcessToRgba(
                    luma,
                    chroma,
                    _current!,
                    _plan.EffectPlan
                        .ColorTransform,
                    inFlightSlot:
                        _index %
                        GpuNv12Processor
                            .MaxInFlightSlots);
            }
            _currentReady = true;
        }
        finally
        {
            luma?.Dispose();
            chroma?.Dispose();
            lumaOwner?.Dispose();
            chromaOwner?.Dispose();
            owner.Dispose();
        }
    }

    private void EnsureTextures(
        uint width,
        uint height)
    {
        if (_current is not null)
        {
            if (_current.Width != width ||
                _current.Height != height)
            {
                throw new NotSupportedException(
                    "Dynamic URI-overlay frame dimensions are not supported.");
            }
            return;
        }
        _current =
            CreateTexture(
                width,
                height,
                $"Linux Media URI Overlay {_index}");
    }

    private void EnsureBlurTextures(
        uint width,
        uint height)
    {
        _blurSource ??=
            CreateTexture(
                width,
                height,
                $"Linux Media URI Overlay {_index} Blur Source");
        _blurIntermediate ??=
            CreateTexture(
                width,
                height,
                $"Linux Media URI Overlay {_index} Blur Intermediate");
    }

    private GpuTexture CreateTexture(
        uint width,
        uint height,
        string label) =>
        new(
            _context,
            width,
            height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding |
                TextureUsage.RenderAttachment,
            label,
            alphaMode:
                GpuTextureAlphaMode.Straight);

    private static LinuxVideoDecoderDevice
        SelectDecoder(
        IsoBmffTrack track,
        IReadOnlyList<LinuxVideoDecoderDevice>
            devices)
    {
        LinuxHardwareVideoCodec required =
            track.Codec == IsoBmffCodec.H264
                ? LinuxHardwareVideoCodec.H264
                : LinuxHardwareVideoCodec.H265;
        for (int index = 0;
             index < devices.Count;
             index++)
        {
            LinuxVideoDecoderDevice device =
                devices[index];
            if (device.UsesMultiPlanarQueues &&
                device.SupportsStreaming &&
                (device.Codecs & required) != 0)
            {
                return device;
            }
        }
        throw new NotSupportedException(
            $"No streaming V4L2 decoder exposes {required} for the URI overlay.");
    }

    private static long GetTrackDurationTicks(
        IsoBmffTrack track) =>
        checked(
            (long)Math.Round(
                track.Duration *
                ((double)TimeSpan
                    .TicksPerSecond /
                 track.Timescale),
                MidpointRounding
                    .AwayFromZero));

    private static void ReleaseCandidate(
        ref V4l2DecodedFrame frame,
        ref bool hasFrame)
    {
        if (!hasFrame)
        {
            return;
        }
        frame.Owner.Dispose();
        frame = default;
        hasFrame = false;
    }
}
