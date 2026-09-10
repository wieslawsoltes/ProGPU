using Microsoft.UI.Xaml;
using ProGPU.CAD.Sample;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;

namespace ProGPU.CAD.Sample.Desktop;

public static class Program
{
    public static void Main(string[] args)
    {
        GlfwWindowing.Use();
        GlfwInput.RegisterPlatform();

        AppBuilder<CadSampleApp>
            .Configure()
            .WithTitle("ProGPU.CAD")
            .WithSize(1280, 800)
            .Build()
            .Run(args);
    }
}
