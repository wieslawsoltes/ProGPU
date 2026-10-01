using ProGPU.WinUI.Hmi;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiPackageBoundaryTests
{
    [Fact]
    public void ProjectRuntimeHasNoUiOrDesignerDependency()
    {
        var references = typeof(HmiProject).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, assembly =>
            assembly.Name?.StartsWith("ProGPU.WinUI", StringComparison.Ordinal) == true ||
            assembly.Name is "ProGPU.Scene" or "ProGPU.Layout" or "ProGPU.Vector");
    }

    [Fact]
    public void StandaloneControlsDoNotReferenceTheDesigner()
    {
        var references = typeof(HmiControl).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, assembly =>
            assembly.Name is "ProGPU.WinUI.Designer" or "ProGPU.WinUI.Hmi.Designer");
    }
}
