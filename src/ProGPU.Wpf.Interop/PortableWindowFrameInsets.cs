namespace ProGPU.Wpf.Interop;

/// <summary>
/// Native non-client frame surrounding a source-owned top-level window's client.
/// Values are desktop logical units, never framebuffer pixels. A known zero
/// frame is valid for borderless windows; an unavailable native frame must be
/// reported as null by the provider instead.
/// </summary>
public readonly record struct PortableWindowFrameInsets(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public static PortableWindowFrameInsets Empty => default;

    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;

    public bool IsValid =>
        double.IsFinite(Left) && Left >= 0 &&
        double.IsFinite(Top) && Top >= 0 &&
        double.IsFinite(Right) && Right >= 0 &&
        double.IsFinite(Bottom) && Bottom >= 0;
}
