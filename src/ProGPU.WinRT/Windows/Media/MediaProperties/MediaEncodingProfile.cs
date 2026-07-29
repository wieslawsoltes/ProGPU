namespace Windows.Media.MediaProperties;

public enum VideoEncodingQuality
{
    Auto,
    HD1080p,
    HD720p,
    Wvga,
    Ntsc,
    Pal,
    Vga,
    Qvga,
    Uhd2160p,
    Uhd4320p
}

public sealed class MediaRatio
{
    public uint Numerator { get; set; }
    public uint Denominator { get; set; } = 1;
}

public sealed class ContainerEncodingProperties
{
    public string Subtype { get; set; } = "MPEG4";
}

public sealed class VideoEncodingProperties
{
    public string Subtype { get; set; } = "H264";
    public uint Width { get; set; } = 1280;
    public uint Height { get; set; } = 720;
    public uint Bitrate { get; set; } = 5_000_000;
    public MediaRatio FrameRate { get; } = new()
    {
        Numerator = 30,
        Denominator = 1
    };
}

public sealed class AudioEncodingProperties
{
    public string Subtype { get; set; } = "AAC";
    public uint Bitrate { get; set; } = 192_000;
    public uint SampleRate { get; set; } = 48_000;
    public uint ChannelCount { get; set; } = 2;
}

/// <summary>
/// WinUI-aligned encoding profile with the fields required by ProGPU's native
/// composition exporters. Properties remain mutable like the official API.
/// </summary>
public sealed class MediaEncodingProfile
{
    public ContainerEncodingProperties? Container { get; set; } = new();
    public VideoEncodingProperties? Video { get; set; } = new();
    public AudioEncodingProperties? Audio { get; set; } = new();

    /// <summary>ProGPU convenience alias for Container.Subtype.</summary>
    public string ContainerSubtype
    {
        get => Container?.Subtype ?? string.Empty;
        set => RequireContainer().Subtype = value;
    }

    /// <summary>ProGPU convenience alias for Video.Subtype.</summary>
    public string? VideoSubtype
    {
        get => Video?.Subtype;
        set
        {
            if (value is null)
            {
                Video = null;
            }
            else
            {
                RequireVideo().Subtype = value;
            }
        }
    }

    /// <summary>ProGPU convenience alias for Audio.Subtype.</summary>
    public string? AudioSubtype
    {
        get => Audio?.Subtype;
        set
        {
            if (value is null)
            {
                Audio = null;
            }
            else
            {
                RequireAudio().Subtype = value;
            }
        }
    }

    public uint Width
    {
        get => Video?.Width ?? 0;
        set => RequireVideo().Width = value;
    }

    public uint Height
    {
        get => Video?.Height ?? 0;
        set => RequireVideo().Height = value;
    }

    public uint VideoBitrate
    {
        get => Video?.Bitrate ?? 0;
        set => RequireVideo().Bitrate = value;
    }

    public uint FrameRateNumerator
    {
        get => Video?.FrameRate.Numerator ?? 0;
        set => RequireVideo().FrameRate.Numerator = value;
    }

    public uint FrameRateDenominator
    {
        get => Video?.FrameRate.Denominator ?? 0;
        set => RequireVideo().FrameRate.Denominator = value;
    }

    public uint AudioBitrate
    {
        get => Audio?.Bitrate ?? 0;
        set => RequireAudio().Bitrate = value;
    }

    public uint AudioSampleRate
    {
        get => Audio?.SampleRate ?? 0;
        set => RequireAudio().SampleRate = value;
    }

    public uint AudioChannelCount
    {
        get => Audio?.ChannelCount ?? 0;
        set => RequireAudio().ChannelCount = value;
    }

    public static MediaEncodingProfile CreateMp4(
        VideoEncodingQuality quality)
    {
        (uint width, uint height, uint bitrate) = quality switch
        {
            VideoEncodingQuality.HD1080p =>
                (1920u, 1080u, 8_000_000u),
            VideoEncodingQuality.Wvga =>
                (800u, 480u, 2_500_000u),
            VideoEncodingQuality.Ntsc =>
                (720u, 480u, 2_500_000u),
            VideoEncodingQuality.Pal =>
                (720u, 576u, 2_500_000u),
            VideoEncodingQuality.Vga =>
                (640u, 480u, 2_000_000u),
            VideoEncodingQuality.Qvga =>
                (320u, 240u, 750_000u),
            VideoEncodingQuality.Uhd2160p =>
                (3840u, 2160u, 35_000_000u),
            VideoEncodingQuality.Uhd4320p =>
                (7680u, 4320u, 100_000_000u),
            _ => (1280u, 720u, 5_000_000u)
        };

        return new MediaEncodingProfile
        {
            Width = width,
            Height = height,
            VideoBitrate = bitrate
        };
    }

    private ContainerEncodingProperties RequireContainer() =>
        Container ??= new ContainerEncodingProperties();

    private VideoEncodingProperties RequireVideo() =>
        Video ??= new VideoEncodingProperties();

    private AudioEncodingProperties RequireAudio() =>
        Audio ??= new AudioEncodingProperties();
}
