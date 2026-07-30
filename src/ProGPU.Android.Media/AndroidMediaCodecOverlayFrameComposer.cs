using Android.Media;
using Java.Nio;
using ProGPU.Backend;
using ProGPU.Media.Editing;

namespace ProGPU.Android.Media;

/// <summary>
/// Retained Android overlay scheduler and synchronous MediaCodec reader set.
/// </summary>
/// <remarks>
/// Setup is O(O) state for O overlays. Each output frame performs O(O)
/// allocation-free timeline checks and decodes only active URI overlays.
/// Every URI overlay retains one SurfaceTexture image and at most one
/// client-owned MediaCodec output buffer. Selected images pass through the
/// shared three-slot AHardwareBuffer ring; no decoded pixel is mapped.
/// </remarks>
internal sealed class
    AndroidMediaCodecOverlayFrameComposer :
    IDisposable
{
    private const long CodecTimeoutMicroseconds = 10_000;
    private readonly AndroidMediaCodecOverlayPlan[] _plans;
    private readonly OverlaySource?[] _sources;
    private bool _disposed;

    internal AndroidMediaCodecOverlayFrameComposer(
        IReadOnlyList<AndroidMediaCodecOverlayPlan> plans,
        AndroidMediaCodecGpuEncoderFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(sink);
        _plans = plans.ToArray();
        _sources =
            new OverlaySource?[_plans.Length];
        if (_plans.Length != 0)
        {
            sink.PrepareOverlayComposition();
        }
    }

    internal void Composite(
        long compositionMicroseconds,
        AndroidMediaCodecGpuEncoderFrameSink sink,
        GpuTexture destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(destination);
        if (compositionMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(compositionMicroseconds));
        }

        for (int index = 0;
             index < _plans.Length;
             index++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            ref readonly AndroidMediaCodecOverlayPlan plan =
                ref _plans[index];
            if (!plan.TryResolve(
                    compositionMicroseconds,
                    out long sourceMicroseconds) ||
                plan.Placement.Opacity == 0f)
            {
                continue;
            }

            if (plan.Clip.ArgbColor is uint color)
            {
                sink.CompositeColorLayer(
                    color,
                    destination,
                    plan.Placement,
                    plan.EffectPlan,
                    cancellationToken);
                continue;
            }

            OverlaySource source =
                _sources[index] ??=
                    new OverlaySource(
                        plan,
                        sourceMicroseconds,
                        sink);
            if (!source.TrySelect(
                    sourceMicroseconds,
                    sink,
                    cancellationToken))
            {
                continue;
            }
            sink.CompositeDecodedLayer(
                source.Input,
                destination,
                plan.Placement,
                plan.EffectPlan,
                cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (int index = 0;
             index < _sources.Length;
             index++)
        {
            _sources[index]?.Dispose();
            _sources[index] = null;
        }
    }

    private sealed class OverlaySource :
        IDisposable
    {
        private readonly AndroidMediaCodecOverlayPlan _plan;
        private readonly MediaExtractor _extractor;
        private readonly MediaCodec _decoder;
        private readonly MediaCodec.BufferInfo _info;
        private int _heldOutputIndex = -1;
        private long _heldTimestamp;
        private int _heldSize;
        private MediaCodecBufferFlags _heldFlags;
        private bool _decoderStarted;
        private bool _inputComplete;
        private bool _outputComplete;
        private bool _disposed;

        internal OverlaySource(
            in AndroidMediaCodecOverlayPlan plan,
            long initialSourceMicroseconds,
            AndroidMediaCodecGpuEncoderFrameSink sink)
        {
            _plan = plan;
            _extractor = new MediaExtractor();
            _info = new MediaCodec.BufferInfo();
            Input = sink.CreateOverlayDecoderInput();
            MediaCodec? decoder = null;
            try
            {
                _extractor.SetDataSource(
                    ToSource(
                        plan.Clip.SourceUri!));
                int track =
                    FindTrack(
                        _extractor,
                        "video/");
                if (track < 0)
                {
                    throw new InvalidDataException(
                        "An Android overlay source has no video track.");
                }

                using MediaFormat format =
                    _extractor.GetTrackFormat(track);
                string mime =
                    format.GetString(
                        MediaFormat.KeyMime) ??
                    throw new InvalidDataException(
                        "An Android overlay video track has no MIME type.");
                _extractor.SelectTrack(track);
                _extractor.SeekTo(
                    Math.Max(
                        plan.SourceStartMicroseconds,
                        initialSourceMicroseconds),
                    MediaExtractorSeekTo.PreviousSync);
                decoder =
                    MediaCodec.CreateDecoderByType(
                        mime);
                decoder.Configure(
                    format,
                    Input.Surface,
                    null,
                    MediaCodecConfigFlags.None);
                decoder.Start();
                _decoderStarted = true;
                _decoder = decoder;
                decoder = null;
            }
            catch
            {
                decoder?.Release();
                decoder?.Dispose();
                _info.Dispose();
                _extractor.Release();
                _extractor.Dispose();
                Input.Dispose();
                throw;
            }
        }

        internal AndroidDecoderSurfaceInput Input
        {
            get;
        }

        internal bool TrySelect(
            long sourceMicroseconds,
            AndroidMediaCodecGpuEncoderFrameSink sink,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
            if (sourceMicroseconds <
                    _plan.SourceStartMicroseconds ||
                sourceMicroseconds >=
                    _plan.SourceEndMicroseconds)
            {
                return false;
            }

            while (true)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                if (_heldOutputIndex >= 0)
                {
                    bool endOfStream =
                        (_heldFlags &
                         MediaCodecBufferFlags
                             .EndOfStream) != 0;
                    if (endOfStream &&
                        _heldSize <= 0)
                    {
                        ReleaseHeld(render: false);
                        _outputComplete = true;
                        break;
                    }
                    if (_heldTimestamp >=
                        _plan.SourceEndMicroseconds)
                    {
                        ReleaseHeld(render: false);
                        _outputComplete = true;
                        break;
                    }
                    if (_heldTimestamp <
                        _plan.SourceStartMicroseconds)
                    {
                        ReleaseHeld(render: false);
                        continue;
                    }
                    if (_heldTimestamp >
                        sourceMicroseconds)
                    {
                        break;
                    }

                    bool render =
                        _heldSize > 0 &&
                        (_heldFlags &
                         MediaCodecBufferFlags
                             .CodecConfig) == 0;
                    ReleaseHeld(render);
                    if (render)
                    {
                        sink.UpdateOverlayDecoderInput(
                            Input,
                            cancellationToken);
                    }
                    if (endOfStream)
                    {
                        _outputComplete = true;
                        break;
                    }
                    continue;
                }
                if (_outputComplete)
                {
                    break;
                }

                FeedInput();
                int outputIndex =
                    _decoder.DequeueOutputBuffer(
                        _info,
                        CodecTimeoutMicroseconds);
                if (outputIndex ==
                    (int)MediaCodecInfoState
                        .TryAgainLater)
                {
                    continue;
                }
                if (outputIndex ==
                    (int)MediaCodecInfoState
                        .OutputFormatChanged)
                {
                    using MediaFormat outputFormat =
                        _decoder.OutputFormat;
                    continue;
                }
                if (outputIndex < 0)
                {
                    continue;
                }

                _heldOutputIndex = outputIndex;
                _heldTimestamp =
                    _info.PresentationTimeUs;
                _heldSize = _info.Size;
                _heldFlags = _info.Flags;
            }

            return Input.HasCurrentImage;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_heldOutputIndex >= 0)
            {
                try
                {
                    ReleaseHeld(render: false);
                }
                catch
                {
                    // Continue releasing the remaining native resources.
                }
            }
            if (_decoderStarted)
            {
                try
                {
                    _decoder.Stop();
                }
                catch
                {
                    // Cleanup must not hide the export result.
                }
            }
            _decoder.Release();
            _decoder.Dispose();
            _info.Dispose();
            _extractor.Release();
            _extractor.Dispose();
            Input.Dispose();
        }

        private void FeedInput()
        {
            if (_inputComplete)
            {
                return;
            }

            int inputIndex =
                _decoder.DequeueInputBuffer(
                    CodecTimeoutMicroseconds);
            if (inputIndex < 0)
            {
                return;
            }
            ByteBuffer input =
                _decoder.GetInputBuffer(
                    inputIndex) ??
                throw new InvalidOperationException(
                    "Android overlay decoder returned no input buffer.");
            long sampleTime =
                _extractor.SampleTime;
            if (sampleTime < 0 ||
                sampleTime >=
                    _plan.SourceEndMicroseconds)
            {
                _decoder.QueueInputBuffer(
                    inputIndex,
                    0,
                    0,
                    0,
                    MediaCodecBufferFlags
                        .EndOfStream);
                _inputComplete = true;
                return;
            }

            int size =
                _extractor.ReadSampleData(
                    input,
                    0);
            if (size < 0)
            {
                _decoder.QueueInputBuffer(
                    inputIndex,
                    0,
                    0,
                    0,
                    MediaCodecBufferFlags
                        .EndOfStream);
                _inputComplete = true;
                return;
            }
            _decoder.QueueInputBuffer(
                inputIndex,
                0,
                size,
                sampleTime,
                ToCodecFlags(
                    _extractor.SampleFlags));
            _extractor.Advance();
        }

        private void ReleaseHeld(
            bool render)
        {
            int outputIndex =
                _heldOutputIndex;
            _heldOutputIndex = -1;
            _decoder.ReleaseOutputBuffer(
                outputIndex,
                render);
        }
    }

    private static int FindTrack(
        MediaExtractor extractor,
        string prefix)
    {
        for (int index = 0;
             index < extractor.TrackCount;
             index++)
        {
            using MediaFormat format =
                extractor.GetTrackFormat(index);
            string? mime =
                format.GetString(
                    MediaFormat.KeyMime);
            if (mime?.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase) ==
                true)
            {
                return index;
            }
        }
        return -1;
    }

    private static MediaCodecBufferFlags ToCodecFlags(
        MediaExtractorSampleFlags flags)
    {
        MediaCodecBufferFlags result =
            MediaCodecBufferFlags.None;
        if ((flags &
             MediaExtractorSampleFlags.Sync) != 0)
        {
            result |=
                MediaCodecBufferFlags.KeyFrame;
        }
        if ((flags &
             MediaExtractorSampleFlags.PartialFrame) != 0)
        {
            result |=
                MediaCodecBufferFlags.PartialFrame;
        }
        return result;
    }

    private static string ToSource(
        Uri source) =>
        source.IsFile
            ? source.LocalPath
            : source.AbsoluteUri;
}
