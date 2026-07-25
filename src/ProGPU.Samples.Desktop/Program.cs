using Microsoft.UI.Xaml;
using ProGPU.Samples;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;

namespace ProGPU.Samples.Desktop;

public static class Program
{
    public static void Main(string[] args)
    {
        GlfwWindowing.Use();
        GlfwInput.RegisterPlatform();
        AppBuilder<App>.Configure()
            .WithTitle("ProGPU Substrate - High-Performance WinUI Gallery Dashboard")
            .WithSize(1280, 800)
            .Build()
            .Run(args);
    }
}
