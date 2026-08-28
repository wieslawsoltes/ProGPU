namespace System.Drawing;

/// <summary>
/// Animation contract for image codecs. The current ProGPU codecs decode a
/// single frame, so registration is deterministic and allocation-free.
/// </summary>
public static class ImageAnimator
{
    public static bool CanAnimate(Image? image)
    {
        return false;
    }

    public static void Animate(Image? image, EventHandler onFrameChangedHandler)
    {
    }

    public static void StopAnimate(Image? image, EventHandler onFrameChangedHandler)
    {
    }

    public static void UpdateFrames()
    {
    }

    public static void UpdateFrames(Image? image)
    {
    }
}
