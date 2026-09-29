using System.Xml.Linq;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableWindowInputDispatcherContractTests
{
    [Fact]
    public void InputDispatcherIsAnOptionalNeutralCapabilityWithoutChangingTheRegistrar()
    {
        Type capability = typeof(IPortableWindowInputDispatcher);
        Type registrar = typeof(IPortableWindowActivationServiceRegistrar);
        Assert.True(capability.IsInterface);
        Assert.Same(registrar.Assembly, capability.Assembly);
        Assert.Empty(capability.GetInterfaces());
        Assert.False(capability.IsAssignableFrom(registrar));
        Assert.Null(registrar.GetMethod(nameof(IPortableWindowInputDispatcher.TryPostInput)));

        var method = Assert.Single(capability.GetMethods());
        Assert.Equal(nameof(IPortableWindowInputDispatcher.TryPostInput), method.Name);
        Assert.True(method.IsAbstract);
        Assert.False(method.IsStatic);
        Assert.Equal(typeof(bool), method.ReturnType);
        Assert.Equal(new[] { typeof(object), typeof(Action) },
            method.GetParameters().Select(parameter => parameter.ParameterType));

        var original = registrar.GetMethod(nameof(IPortableWindowActivationServiceRegistrar.TryBeginInvokeInput));
        Assert.NotNull(original);
        Assert.True(original.IsAbstract);
        Assert.Equal(typeof(bool), original.ReturnType);
        Assert.Equal(new[] { typeof(object), typeof(Action) },
            original.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void InteropProjectRemainsIndependentOfSourceDispatcherAndRendererAssemblies()
    {
        var project = XDocument.Load(FindProject());
        Assert.Empty(project.Descendants().Where(element =>
            element.Name.LocalName is "ProjectReference" or "PackageReference"));
        Assert.Equal("net10.0", Assert.Single(project.Descendants("TargetFramework")).Value);
        Assert.Equal("LibreWPF.Interop", Assert.Single(project.Descendants("PackageId")).Value);
    }

    private static string FindProject()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName,
                "src", "ProGPU.Wpf.Interop", "ProGPU.Wpf.Interop.csproj");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not find the neutral interop project.");
    }
}
