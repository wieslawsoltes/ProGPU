using Xunit;

namespace System.Drawing.Tests;

public sealed class ImageAnimatorQualityTests
{
    [Fact]
    public void NullImageMatchesDesktopNoOpContract()
    {
        Assert.False(ImageAnimator.CanAnimate(null));
        ImageAnimator.Animate(null, null!);
        ImageAnimator.StopAnimate(null, null!);
        ImageAnimator.UpdateFrames(null);
    }
}
