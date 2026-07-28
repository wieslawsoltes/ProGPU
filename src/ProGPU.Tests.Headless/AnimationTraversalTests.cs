using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class AnimationTraversalTests
{
    [Fact]
    public void InactiveTreeReturnsWithoutInvokingCustomAnimationHooks()
    {
        var root = new ContainerVisual();
        var child = new TestVisual();
        root.AddChild(child);

        root.UpdateAnimations(1f / 60f);

        Assert.Equal(0, child.UpdateCount);
    }

    [Fact]
    public void CustomAnimationActivityPropagatesAndDetachesWithSubtree()
    {
        var root = new ContainerVisual();
        var branch = new ContainerVisual();
        var child = new TestVisual();
        branch.AddChild(child);
        root.AddChild(branch);

        child.SetActive(true);
        root.UpdateAnimations(1f / 60f);
        Assert.Equal(1, child.UpdateCount);

        root.RemoveChild(branch);
        root.UpdateAnimations(1f / 60f);
        Assert.Equal(1, child.UpdateCount);
    }

    private sealed class TestVisual : Visual
    {
        internal int UpdateCount { get; private set; }

        internal void SetActive(bool value) =>
            SetCustomAnimationActive(value);

        protected override void OnUpdateAnimations(float elapsedSeconds) =>
            UpdateCount++;
    }
}
