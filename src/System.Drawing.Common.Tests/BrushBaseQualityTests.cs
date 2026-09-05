using Xunit;

namespace System.Drawing.Tests;

public sealed class BrushBaseQualityTests
{
    [Fact]
    public void SolidBrushCloneOwnsIndependentState()
    {
        using var source = new SolidBrush(Color.Red);
        using var clone = Assert.IsType<SolidBrush>(source.Clone());

        clone.Color = Color.Blue;

        Assert.Equal(Color.Red, source.Color);
        Assert.Equal(Color.Blue, clone.Color);
    }

    [Fact]
    public void SolidBrushRejectsUseAfterDispose()
    {
        var brush = new SolidBrush(Color.Red);
        brush.Dispose();
        brush.Dispose();

        Assert.Throws<ObjectDisposedException>(() => brush.Clone());
        Assert.Throws<ObjectDisposedException>(() => brush.Color);
        Assert.Throws<ObjectDisposedException>(() => brush.Color = Color.Blue);
        Assert.Throws<ObjectDisposedException>(() => brush.ToProGpuBrush());
    }

    [Fact]
    public void DerivedBrushUsesOfficialCloneAndDisposeHooks()
    {
        var brush = new TestBrush();
        Assert.IsAssignableFrom<MarshalByRefObject>(brush);
        Assert.IsAssignableFrom<ICloneable>(brush);

        using var clone = Assert.IsType<TestBrush>(brush.Clone());
        brush.Dispose();
        brush.Dispose();

        Assert.Equal(2, brush.DisposeCalls);
        Assert.Throws<NotSupportedException>(() => clone.ToProGpuBrush());
    }

    [Fact]
    public void NativeBrushInjectionIsAnExplicitPlatformBoundary()
    {
        using var brush = new TestBrush();

        PlatformNotSupportedException exception = Assert.Throws<PlatformNotSupportedException>(() =>
            brush.SetNativeBrushForTest(new IntPtr(42)));

        Assert.Contains("Windows drawing adapter", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestBrush : Brush
    {
        public int DisposeCalls { get; private set; }

        public override object Clone() => new TestBrush();

        public void SetNativeBrushForTest(IntPtr value) => SetNativeBrush(value);

        protected override void Dispose(bool disposing) => DisposeCalls++;
    }
}
