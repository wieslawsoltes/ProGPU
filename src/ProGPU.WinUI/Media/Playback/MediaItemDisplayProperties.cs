using Windows.Media;
using Windows.Storage.Streams;

namespace Windows.Media.Playback;

/// <summary>
/// Specifies which embedded metadata may be loaded automatically for a
/// playback item.
/// </summary>
public enum AutoLoadedDisplayPropertyKind
{
    None = 0,
    MusicOrVideo = 1,
    Music = 2,
    Video = 3
}

/// <summary>
/// WinUI-aligned display metadata snapshot for a MediaPlaybackItem.
/// </summary>
/// <remarks>
/// Clone and clear operations are O(G) for the total number of genre strings.
/// Thumbnail data remains behind its immutable stream reference and is not
/// copied by metadata updates.
/// </remarks>
public sealed class MediaItemDisplayProperties
{
    private MediaPlaybackType _type;

    public MediaItemDisplayProperties()
    {
        MusicProperties = new MusicDisplayProperties();
        VideoProperties = new VideoDisplayProperties();
    }

    public MusicDisplayProperties MusicProperties { get; }

    public RandomAccessStreamReference? Thumbnail { get; set; }

    public MediaPlaybackType Type
    {
        get => _type;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _type = value;
        }
    }

    public VideoDisplayProperties VideoProperties { get; }

    public void ClearAll()
    {
        MusicProperties.ClearAll();
        Thumbnail = null;
        _type = MediaPlaybackType.Unknown;
        VideoProperties.ClearAll();
    }

    internal MediaItemDisplayProperties Clone()
    {
        var clone = new MediaItemDisplayProperties
        {
            Thumbnail = Thumbnail,
            Type = Type
        };
        MusicProperties.CopyTo(clone.MusicProperties);
        VideoProperties.CopyTo(clone.VideoProperties);
        return clone;
    }
}
