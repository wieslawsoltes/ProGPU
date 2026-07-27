using System;
using Avalonia.SilkNet;

namespace Avalonia;

public static class SilkNetAppBuilderExtensions
{
    public static AppBuilder UseSilkNet(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .UseStandardRuntimePlatformSubsystem()
            .UseWindowingSubsystem(
                SilkNetPlatform.Initialize,
                "Silk.NET/GLFW");
    }
}
