using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortableGeometryOperationsTests
{
    [Fact]
    public void ReplacementAndDefaultRegistrationPreserveExplicitProviderOwnership()
    {
        var first = new Service();
        var second = new Service();
        using var a = PortableWpfServiceRegistry.RegisterGeometryOperations(first);
        using var b = PortableWpfServiceRegistry.RegisterGeometryOperations(second);
        PortableWpfServiceRegistry.EnsureGeometryOperations(first);
        a.Dispose();
        Assert.True(PortableWpfServiceRegistry.TryGetGeometryOperations(out var current));
        Assert.Same(second, current);
        b.Dispose();
        Assert.False(PortableWpfServiceRegistry.TryGetGeometryOperations(out _));
    }

    private sealed class Service : IPortableGeometryOperations
    {
        public PortableGeometryPath Combine(PortableGeometryOperand first, PortableGeometryOperand second,
            PortableGeometryCombineMode mode, PortableMatrix3x2 transform, double tolerance, bool relative) =>
            throw new InvalidOperationException("Registration must not execute geometry or initialize a device.");
    }
}
