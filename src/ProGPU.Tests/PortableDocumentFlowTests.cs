using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortableDocumentFlowTests
{
    [Fact]
    public void LegacyDocumentProviderRejectsMissingPaginationExplicitly()
    {
        IPortableDocumentFlow provider = new Provider();
        Assert.Throws<PlatformNotSupportedException>(() => provider.Paginate([], 100, 1, []));
        Assert.Throws<PlatformNotSupportedException>(() => provider.ArrangeWithObjects([], 100, [], [new()], [], []));
        // Empty object input uses the existing provider's ordinary arrangement.
        Assert.Throws<InvalidOperationException>(() => provider.ArrangeWithObjects([], 100, [], [], [], []));
        Assert.Throws<PlatformNotSupportedException>(() => provider.ResolveWidthsWithRows([], 100, [new()], [], [], []));
        Assert.Throws<PlatformNotSupportedException>(() => provider.ResolveWidthsWithRows([], 100, [], [1], [], []));
        Assert.Throws<PlatformNotSupportedException>(() => provider.ArrangeWithRows([], 100, [], [], [], [], [new()], [], []));
        Assert.Throws<InvalidOperationException>(() => provider.ResolveWidthsWithRows([], 100, [], [], [], []));
        Assert.Throws<InvalidOperationException>(() => provider.ArrangeWithRows([], 100, [], [], [], [], [], [], []));
    }

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
