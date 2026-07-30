using Windows.Foundation;
using Windows.Media.Effects;

namespace Windows.Media.Editing;

/// <summary>
/// WinUI-aligned overlay descriptor. The clip remains independently editable
/// and is rendered at Delay within its containing z-ordered overlay layer.
/// </summary>
public sealed class MediaOverlay
{
    private TimeSpan _delay;
    private Rect _position;
    private double _opacity;

    public MediaOverlay(MediaClip clip)
        : this(
            clip,
            new Rect(0d, 0d, 0d, 0d),
            1d)
    {
    }

    public MediaOverlay(
        MediaClip clip,
        Rect position,
        double opacity)
    {
        Clip =
            clip ??
            throw new ArgumentNullException(nameof(clip));
        Position = position;
        Opacity = opacity;
        AudioEnabled = true;
    }

    public bool AudioEnabled { get; set; }

    public MediaClip Clip { get; }

    public TimeSpan Delay
    {
        get => _delay;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _delay = value;
        }
    }

    public double Opacity
    {
        get => _opacity;
        set
        {
            if (!double.IsFinite(value) ||
                value is < 0d or > 1d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _opacity = value;
        }
    }

    public Rect Position
    {
        get => _position;
        set
        {
            if (!double.IsFinite(value.X) ||
                !double.IsFinite(value.Y) ||
                !double.IsFinite(value.Width) ||
                !double.IsFinite(value.Height) ||
                value.Width < 0d ||
                value.Height < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _position = value;
        }
    }

    internal MediaOverlay Clone() =>
        new(
            Clip.Clone(),
            Position,
            Opacity)
        {
            AudioEnabled = AudioEnabled,
            Delay = Delay
        };
}

/// <summary>
/// Z-ordered collection of overlays. Higher collection indices are rendered
/// above lower indices, matching the official MediaOverlayLayer contract.
/// </summary>
public sealed class MediaOverlayLayer
{
    private readonly List<MediaOverlay> _overlays = [];

    public MediaOverlayLayer()
    {
    }

    public MediaOverlayLayer(
        IVideoCompositorDefinition compositorDefinition)
    {
        CustomCompositorDefinition =
            compositorDefinition ??
            throw new ArgumentNullException(
                nameof(compositorDefinition));
    }

    public IVideoCompositorDefinition?
        CustomCompositorDefinition { get; }

    public IList<MediaOverlay> Overlays => _overlays;

    public MediaOverlayLayer Clone()
    {
        var clone = CustomCompositorDefinition is null
            ? new MediaOverlayLayer()
            : new MediaOverlayLayer(
                MediaEditingEffectClone.Clone(
                    CustomCompositorDefinition));
        for (int index = 0;
             index < _overlays.Count;
             index++)
        {
            clone._overlays.Add(
                _overlays[index].Clone());
        }
        return clone;
    }
}
