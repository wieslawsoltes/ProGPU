using Microsoft.UI.Xaml;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;

namespace ProGPU.Samples.ActivityMonitor;

public static class Program
{
    public static void Main(string[] args)
    {
        GlfwWindowing.Use();
        GlfwInput.RegisterPlatform();

        AppBuilder<App>
            .Configure()
            .WithTitle("Activity Monitor")
            .WithSize(1440, 900)
            .Build()
            .Run(args);
    }
}
