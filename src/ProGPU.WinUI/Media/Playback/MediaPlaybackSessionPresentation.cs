using System.Numerics;
using Windows.Media.MediaProperties;

namespace Windows.Media.Playback;

public enum SphericalVideoProjectionMode
{
    Spherical = 0,
    Flat = 1
}

public enum MediaPlaybackSessionVideoConstrictionReason
{
    None = 0,
    VirtualMachine = 1,
    UnsupportedDisplayAdapter = 2,
    UnsignedDriver = 3,
    FrameServerEnabled = 4,
    OutputProtectionFailed = 5,
    Unknown = 6
}

public sealed class MediaPlaybackSessionOutputDegradationPolicyState
{
    internal MediaPlaybackSessionOutputDegradationPolicyState(
        MediaPlaybackSessionVideoConstrictionReason reason)
    {
        VideoConstrictionReason = reason;
    }

    public MediaPlaybackSessionVideoConstrictionReason
        VideoConstrictionReason { get; }
}

public sealed class MediaPlaybackSphericalVideoProjection
{
    private readonly MediaPlaybackSession _session;
    private SphericalVideoFrameFormat _frameFormat;
    private float _horizontalFieldOfViewInDegrees = 120f;
    private bool _isEnabled;
    private SphericalVideoProjectionMode _projectionMode;
    private Quaternion _viewOrientation = Quaternion.Identity;

    internal MediaPlaybackSphericalVideoProjection(
        MediaPlaybackSession session)
    {
        _session = session;
    }

    public SphericalVideoFrameFormat FrameFormat
    {
        get => _frameFormat;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Set(ref _frameFormat, value);
        }
    }

    public double HorizontalFieldOfViewInDegrees
    {
        get => _horizontalFieldOfViewInDegrees;
        set
        {
            if (!double.IsFinite(value) ||
                value <= 0d ||
                value >= 180d)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            float normalized = (float)value;
            if (_horizontalFieldOfViewInDegrees == normalized)
            {
                return;
            }
            _horizontalFieldOfViewInDegrees = normalized;
            _session.NotifyPresentationChanged();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public SphericalVideoProjectionMode ProjectionMode
    {
        get => _projectionMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Set(ref _projectionMode, value);
        }
    }

    public Quaternion ViewOrientation
    {
        get => _viewOrientation;
        set
        {
            if (!float.IsFinite(value.X) ||
                !float.IsFinite(value.Y) ||
                !float.IsFinite(value.Z) ||
                !float.IsFinite(value.W) ||
                value.LengthSquared() <= float.Epsilon)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Quaternion normalized = Quaternion.Normalize(value);
            if (_viewOrientation == normalized)
            {
                return;
            }
            _viewOrientation = normalized;
            _session.NotifyPresentationChanged();
        }
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        _session.NotifyPresentationChanged();
    }
}
