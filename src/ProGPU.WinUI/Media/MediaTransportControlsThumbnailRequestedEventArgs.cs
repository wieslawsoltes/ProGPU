using Windows.Foundation;
using Windows.Storage.Streams;

namespace Microsoft.UI.Xaml.Media;

/// <summary>
/// Provides data for the
/// <see cref="Controls.MediaTransportControls.ThumbnailRequested"/> event.
/// </summary>
public sealed class
    MediaTransportControlsThumbnailRequestedEventArgs
{
    private readonly object _gate = new();
    private int _deferralCount;
    private bool _sealed;
    private Action<IInputStream?>? _completion;
    private IInputStream? _thumbnailImage;

    internal MediaTransportControlsThumbnailRequestedEventArgs()
    {
    }

    /// <summary>
    /// Defers completion while the application generates the thumbnail.
    /// </summary>
    public Deferral GetDeferral()
    {
        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "A deferral cannot be requested after thumbnail dispatch completes.");
            }

            _deferralCount++;
        }

        return new Deferral(CompleteDeferral);
    }

    /// <summary>
    /// Supplies the encoded thumbnail image stream for the current request.
    /// Stream ownership remains with the caller.
    /// </summary>
    public void SetThumbnailImage(IInputStream value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _thumbnailImage = value;
        }
    }

    internal void Seal(Action<IInputStream?> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        IInputStream? image = null;
        bool run;
        lock (_gate)
        {
            _sealed = true;
            _completion = completion;
            run = _deferralCount == 0;
            if (run)
            {
                image = _thumbnailImage;
                _completion = null;
            }
        }

        if (run)
        {
            completion(image);
        }
    }

    private void CompleteDeferral()
    {
        Action<IInputStream?>? completion = null;
        IInputStream? image = null;
        lock (_gate)
        {
            if (_deferralCount == 0)
            {
                return;
            }

            _deferralCount--;
            if (_sealed && _deferralCount == 0)
            {
                completion = _completion;
                image = _thumbnailImage;
                _completion = null;
            }
        }

        completion?.Invoke(image);
    }
}
