using System;
using Avalonia.SilkNet;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public sealed class SharedDeviceSelectionTests
{
    private const string Variable = "PROGPU_AVALONIA_SHARE_WGPU_DEVICE";

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void DeviceSharingIsDefaultAndCanBeDisabledForDifferentialProfiling(
        string? configured,
        bool expected)
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, configured);
            Assert.Equal(expected, WindowImpl.ShouldShareWebGpuDevice());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }
}
