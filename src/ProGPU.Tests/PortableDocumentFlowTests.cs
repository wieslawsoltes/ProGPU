using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortableDocumentFlowTests
{
    [Fact]
    public void DocumentDefaultsRemainLazyAndPreserveExplicitPriority()
    {
        var fallback = new Provider();
        var custom = new Provider();
        using var registration = PortableWpfServiceRegistry.RegisterDocumentFlow(custom);
        PortableWpfServiceRegistry.EnsureDocumentFlow(fallback);
        PortableWpfServiceRegistry.EnsureDocumentFlow(new Provider());
        Assert.True(PortableWpfServiceRegistry.TryGetDocumentFlow(out var current));
        Assert.Same(custom, current);
        registration.Dispose();
        Assert.True(PortableWpfServiceRegistry.TryGetDocumentFlow(out current));
        Assert.Same(fallback, current);
    }

    private sealed class Provider : IPortableDocumentFlow
    {
        public void ResolveWidths(ReadOnlySpan<PortableDocumentBlock> blocks, double width, Span<PortableDocumentBox> boxes)
            => throw new InvalidOperationException("Registration must not invoke native layout.");
        public PortableDocumentExtent Arrange(ReadOnlySpan<PortableDocumentBlock> blocks, double width,
            ReadOnlySpan<PortableDocumentLine> lines, Span<PortableDocumentBox> boxes, Span<PortableDocumentLinePosition> positions)
            => throw new InvalidOperationException("Registration must not invoke native layout.");
    }
}
