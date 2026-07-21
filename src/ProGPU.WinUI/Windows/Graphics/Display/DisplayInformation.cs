using System;

namespace Windows.Graphics.Display;

[Flags]
public enum DisplayOrientations
{
    None = 0,
    Landscape = 1,
    Portrait = 2,
    LandscapeFlipped = 4,
    PortraitFlipped = 8
}

public enum ResolutionScale
{
    Invalid = 0,
    Scale100Percent = 100,
    Scale120Percent = 120,
    Scale125Percent = 125,
    Scale140Percent = 140,
    Scale150Percent = 150,
    Scale160Percent = 160,
    Scale175Percent = 175,
    Scale180Percent = 180,
    Scale200Percent = 200,
    Scale225Percent = 225,
    Scale250Percent = 250,
    Scale300Percent = 300,
    Scale350Percent = 350,
    Scale400Percent = 400,
    Scale450Percent = 450,
    Scale500Percent = 500
}

/// <summary>
/// Immutable host input for <see cref="DisplayInformation"/>. Physical dimensions
/// are raw pixels and scale is raw pixels per logical layout pixel.
/// </summary>
public readonly record struct DisplayInformationMetrics(
    DisplayOrientations CurrentOrientation,
    DisplayOrientations NativeOrientation,
    float LogicalDpi,
    double RawPixelsPerViewPixel,
    uint ScreenWidthInRawPixels,
    uint ScreenHeightInRawPixels,
    double? DiagonalSizeInInches = null);

public sealed class DisplayInformation
{
    private static readonly object EventArgs = new();
    private static readonly DisplayInformation CurrentView = new();
    private static DisplayOrientations _autoRotationPreferences;

    private DisplayInformation()
    {
    }

    public static DisplayOrientations AutoRotationPreferences
    {
        get => _autoRotationPreferences;
        set => _autoRotationPreferences = value;
    }

    public DisplayOrientations CurrentOrientation { get; private set; } = DisplayOrientations.Landscape;
    public DisplayOrientations NativeOrientation { get; private set; } = DisplayOrientations.Landscape;
    public float LogicalDpi { get; private set; } = 96f;
    public double RawPixelsPerViewPixel { get; private set; } = 1d;
    public ResolutionScale ResolutionScale { get; private set; } = ResolutionScale.Scale100Percent;
    public uint ScreenWidthInRawPixels { get; private set; }
    public uint ScreenHeightInRawPixels { get; private set; }
    public double? DiagonalSizeInInches { get; private set; }

    public event Windows.Foundation.TypedEventHandler<DisplayInformation, object>? OrientationChanged;
    public event Windows.Foundation.TypedEventHandler<DisplayInformation, object>? DpiChanged;

    public static DisplayInformation GetForCurrentView() => CurrentView;

    /// <summary>
    /// Publishes native display state to the current WinUI view. This is a ProGPU
    /// platform-host extension; application-facing properties and events retain the
    /// official <c>DisplayInformation</c> shapes.
    /// </summary>
    public static void NotifyHostMetricsChanged(DisplayInformationMetrics metrics) =>
        CurrentView.Update(metrics);

    private void Update(DisplayInformationMetrics metrics)
    {
        Validate(metrics);

        bool orientationChanged =
            CurrentOrientation != metrics.CurrentOrientation ||
            NativeOrientation != metrics.NativeOrientation;
        ResolutionScale resolutionScale = ResolveScale(metrics.RawPixelsPerViewPixel);
        bool dpiChanged =
            LogicalDpi != metrics.LogicalDpi ||
            RawPixelsPerViewPixel != metrics.RawPixelsPerViewPixel ||
            ResolutionScale != resolutionScale;

        CurrentOrientation = metrics.CurrentOrientation;
        NativeOrientation = metrics.NativeOrientation;
        LogicalDpi = metrics.LogicalDpi;
        RawPixelsPerViewPixel = metrics.RawPixelsPerViewPixel;
        ResolutionScale = resolutionScale;
        ScreenWidthInRawPixels = metrics.ScreenWidthInRawPixels;
        ScreenHeightInRawPixels = metrics.ScreenHeightInRawPixels;
        DiagonalSizeInInches = metrics.DiagonalSizeInInches;

        if (orientationChanged)
            OrientationChanged?.Invoke(this, EventArgs);
        if (dpiChanged)
            DpiChanged?.Invoke(this, EventArgs);
    }

    private static void Validate(DisplayInformationMetrics metrics)
    {
        if (!IsSingleOrientation(metrics.CurrentOrientation))
            throw new ArgumentOutOfRangeException(nameof(metrics), "Current orientation must identify one display orientation.");
        if (metrics.NativeOrientation is not (DisplayOrientations.Landscape or DisplayOrientations.Portrait))
            throw new ArgumentOutOfRangeException(nameof(metrics), "Native orientation must be Landscape or Portrait.");
        if (!float.IsFinite(metrics.LogicalDpi) || metrics.LogicalDpi <= 0f)
            throw new ArgumentOutOfRangeException(nameof(metrics), "Logical DPI must be finite and positive.");
        if (!double.IsFinite(metrics.RawPixelsPerViewPixel) || metrics.RawPixelsPerViewPixel <= 0d)
            throw new ArgumentOutOfRangeException(nameof(metrics), "Raw-pixel scale must be finite and positive.");
        if (metrics.DiagonalSizeInInches is { } diagonal && (!double.IsFinite(diagonal) || diagonal <= 0d))
            throw new ArgumentOutOfRangeException(nameof(metrics), "Display diagonal must be finite and positive when supplied.");
    }

    private static bool IsSingleOrientation(DisplayOrientations orientation) =>
        orientation is DisplayOrientations.Landscape or
            DisplayOrientations.Portrait or
            DisplayOrientations.LandscapeFlipped or
            DisplayOrientations.PortraitFlipped;

    private static ResolutionScale ResolveScale(double rawPixelsPerViewPixel)
    {
        int percentage = checked((int)Math.Round(rawPixelsPerViewPixel * 100d));
        ReadOnlySpan<ResolutionScale> scales =
        [
            ResolutionScale.Scale100Percent,
            ResolutionScale.Scale120Percent,
            ResolutionScale.Scale125Percent,
            ResolutionScale.Scale140Percent,
            ResolutionScale.Scale150Percent,
            ResolutionScale.Scale160Percent,
            ResolutionScale.Scale175Percent,
            ResolutionScale.Scale180Percent,
            ResolutionScale.Scale200Percent,
            ResolutionScale.Scale225Percent,
            ResolutionScale.Scale250Percent,
            ResolutionScale.Scale300Percent,
            ResolutionScale.Scale350Percent,
            ResolutionScale.Scale400Percent,
            ResolutionScale.Scale450Percent,
            ResolutionScale.Scale500Percent
        ];

        ResolutionScale nearest = scales[0];
        int nearestDistance = Math.Abs(percentage - (int)nearest);
        for (int i = 1; i < scales.Length; i++)
        {
            int distance = Math.Abs(percentage - (int)scales[i]);
            if (distance < nearestDistance)
            {
                nearest = scales[i];
                nearestDistance = distance;
            }
        }

        return nearest;
    }
}
