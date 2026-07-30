using ProGPU.Media.Editing;

namespace ProGPU.Windows.Media;

/// <summary>
/// Retained Media Foundation ARGB32 video reader used by composition
/// thumbnails and overlay rendering.
/// </summary>
/// <remarks>
/// Random reads seek and decode O(D) samples to the selected frame. Monotonic
/// reads retain the current and one look-ahead sample, decode each source
/// sample at most once, and use O(1) managed and native working storage.
/// Returned samples own one COM reference and must be released by the caller.
/// </remarks>
internal sealed class WindowsMediaFoundationVideoFrameReader :
    IDisposable
{
    private nint _reader;
    private nint _mediaType;
    private nint _currentSample;
    private nint _nextSample;
    private long _currentTimestamp = long.MinValue;
    private long _nextTimestamp = long.MinValue;
    private long _lastRequestedTimestamp = long.MinValue;
    private bool _forwardInitialized;
    private bool _endOfStream;

    internal WindowsMediaFoundationVideoFrameReader(
        Uri source,
        nint dxgiManager,
        uint width,
        uint height,
        uint frameRateNumerator,
        uint frameRateDenominator)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            _reader =
                WindowsMediaNative.CreateTranscodeSourceReader(
                    WindowsMediaFoundationCompositionExportProvider
                        .ToSourceUrl(source),
                    dxgiManager);
            _mediaType =
                WindowsMediaNative.CreateArgb32VideoType(
                    width,
                    height,
                    frameRateNumerator,
                    frameRateDenominator);
            WindowsMediaNative.ConfigureSourceReaderStream(
                _reader,
                WindowsMediaNative.FirstVideoStream,
                _mediaType);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint ReadFrame(
        long sourceTicks,
        MediaCompositionThumbnailPrecision precision,
        CancellationToken cancellationToken)
    {
        ResetForwardState();
        WindowsMediaNative.SetSourceReaderPosition(
            _reader,
            sourceTicks);
        nint candidate = 0;
        long candidateTimestamp = long.MinValue;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint sample =
                    WindowsMediaNative.ReadSourceSample(
                        _reader,
                        WindowsMediaNative.FirstVideoStream,
                        out uint flags,
                        out long timestamp);
                if ((flags &
                     WindowsMediaNative.SourceReaderEndOfStream) != 0)
                {
                    WindowsMediaNative.Release(sample);
                    break;
                }
                if (sample == 0)
                {
                    continue;
                }
                if (precision ==
                    MediaCompositionThumbnailPrecision
                        .NearestKeyFrame)
                {
                    WindowsMediaNative.Release(candidate);
                    candidate = sample;
                    break;
                }
                if (timestamp >= sourceTicks)
                {
                    if (candidate == 0 ||
                        timestamp - sourceTicks <
                        sourceTicks - candidateTimestamp)
                    {
                        WindowsMediaNative.Release(candidate);
                        candidate = sample;
                    }
                    else
                    {
                        WindowsMediaNative.Release(sample);
                    }
                    break;
                }
                WindowsMediaNative.Release(candidate);
                candidate = sample;
                candidateTimestamp = timestamp;
            }
            if (candidate == 0)
            {
                throw new InvalidDataException(
                    "Media Foundation returned no frame for the requested composition position.");
            }
            nint result = candidate;
            candidate = 0;
            return result;
        }
        finally
        {
            WindowsMediaNative.Release(candidate);
        }
    }

    internal nint ReadFrameForward(
        long sourceTicks,
        CancellationToken cancellationToken)
    {
        if (!_forwardInitialized ||
            sourceTicks < _lastRequestedTimestamp)
        {
            ResetForwardState();
            WindowsMediaNative.SetSourceReaderPosition(
                _reader,
                sourceTicks);
            _forwardInitialized = true;
        }
        _lastRequestedTimestamp = sourceTicks;

        while (!_endOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_nextSample != 0)
            {
                if (_nextTimestamp > sourceTicks)
                {
                    break;
                }
                PromoteNextSample();
                continue;
            }

            nint sample =
                WindowsMediaNative.ReadSourceSample(
                    _reader,
                    WindowsMediaNative.FirstVideoStream,
                    out uint flags,
                    out long timestamp);
            if ((flags &
                 WindowsMediaNative.SourceReaderEndOfStream) != 0)
            {
                WindowsMediaNative.Release(sample);
                _endOfStream = true;
                break;
            }
            if (sample == 0)
            {
                continue;
            }
            if (timestamp <= sourceTicks ||
                _currentSample == 0)
            {
                WindowsMediaNative.Release(
                    _currentSample);
                _currentSample = sample;
                _currentTimestamp = timestamp;
                continue;
            }
            _nextSample = sample;
            _nextTimestamp = timestamp;
            break;
        }

        nint selected =
            _currentSample != 0
                ? _currentSample
                : _nextSample;
        if (selected == 0)
        {
            throw new InvalidDataException(
                "Media Foundation returned no frame for the requested composition position.");
        }
        WindowsMediaNative.AddRef(selected);
        return selected;
    }

    public void Dispose()
    {
        ResetForwardState();
        WindowsMediaNative.Release(
            Interlocked.Exchange(
                ref _mediaType,
                0));
        WindowsMediaNative.Release(
            Interlocked.Exchange(
                ref _reader,
                0));
    }

    private void PromoteNextSample()
    {
        WindowsMediaNative.Release(_currentSample);
        _currentSample = _nextSample;
        _currentTimestamp = _nextTimestamp;
        _nextSample = 0;
        _nextTimestamp = long.MinValue;
    }

    private void ResetForwardState()
    {
        WindowsMediaNative.Release(
            Interlocked.Exchange(
                ref _currentSample,
                0));
        WindowsMediaNative.Release(
            Interlocked.Exchange(
                ref _nextSample,
                0));
        _currentTimestamp = long.MinValue;
        _nextTimestamp = long.MinValue;
        _lastRequestedTimestamp = long.MinValue;
        _forwardInitialized = false;
        _endOfStream = false;
    }
}
