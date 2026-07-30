namespace Windows.Media;

/// <summary>
/// WinUI-aligned media kind used by system transport metadata.
/// </summary>
public enum MediaPlaybackType
{
    Unknown = 0,
    Music = 1,
    Video = 2,
    Image = 3
}

/// <summary>
/// Music metadata displayed by system media transport controls.
/// </summary>
public sealed class MusicDisplayProperties
{
    private string _albumArtist = string.Empty;
    private string _albumTitle = string.Empty;
    private string _artist = string.Empty;
    private string _title = string.Empty;
    private readonly List<string> _genres = [];

    public string AlbumArtist
    {
        get => _albumArtist;
        set => _albumArtist = value ?? string.Empty;
    }

    public string AlbumTitle
    {
        get => _albumTitle;
        set => _albumTitle = value ?? string.Empty;
    }

    public uint AlbumTrackCount { get; set; }

    public string Artist
    {
        get => _artist;
        set => _artist = value ?? string.Empty;
    }

    public IList<string> Genres => _genres;

    public string Title
    {
        get => _title;
        set => _title = value ?? string.Empty;
    }

    public uint TrackNumber { get; set; }

    internal void CopyTo(MusicDisplayProperties destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.AlbumArtist = AlbumArtist;
        destination.AlbumTitle = AlbumTitle;
        destination.AlbumTrackCount = AlbumTrackCount;
        destination.Artist = Artist;
        destination._genres.Clear();
        destination._genres.AddRange(_genres);
        destination.Title = Title;
        destination.TrackNumber = TrackNumber;
    }

    internal void ClearAll()
    {
        _albumArtist = string.Empty;
        _albumTitle = string.Empty;
        AlbumTrackCount = 0;
        _artist = string.Empty;
        _genres.Clear();
        _title = string.Empty;
        TrackNumber = 0;
    }
}

/// <summary>
/// Video metadata displayed by system media transport controls.
/// </summary>
public sealed class VideoDisplayProperties
{
    private string _subtitle = string.Empty;
    private string _title = string.Empty;
    private readonly List<string> _genres = [];

    public IList<string> Genres => _genres;

    public string Subtitle
    {
        get => _subtitle;
        set => _subtitle = value ?? string.Empty;
    }

    public string Title
    {
        get => _title;
        set => _title = value ?? string.Empty;
    }

    internal void CopyTo(VideoDisplayProperties destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination._genres.Clear();
        destination._genres.AddRange(_genres);
        destination.Subtitle = Subtitle;
        destination.Title = Title;
    }

    internal void ClearAll()
    {
        _genres.Clear();
        _subtitle = string.Empty;
        _title = string.Empty;
    }
}
