using System.Runtime.InteropServices;
using SkiaSharp;
using SkiaSharp.Internals;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPlatformCompatibilityTests
{
    [Fact]
    public void PlatformConfigurationMatchesCurrentRuntime()
    {
        Assert.Equal(Environment.Is64BitProcess, PlatformConfiguration.Is64Bit);
        Assert.Equal(OperatingSystem.IsWindows(), PlatformConfiguration.IsWindows);
        Assert.Equal(OperatingSystem.IsLinux(), PlatformConfiguration.IsLinux);
        Assert.Equal(OperatingSystem.IsMacOS(), PlatformConfiguration.IsMac);
        Assert.Equal(
            RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64,
            PlatformConfiguration.IsArm);
        Assert.Equal(
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD(),
            PlatformConfiguration.IsUnix);

        var original = PlatformConfiguration.LinuxFlavor;
        try
        {
            PlatformConfiguration.LinuxFlavor = "test-flavor";
            Assert.Equal("test-flavor", PlatformConfiguration.LinuxFlavor);
        }
        finally
        {
            PlatformConfiguration.LinuxFlavor = original;
        }
    }

    [Fact]
    public void PlatformLockFactorySupportsEveryLockModeWithoutSteadyAllocation()
    {
        var platformLock = PlatformLock.Create();
        platformLock.EnterReadLock();
        platformLock.EnterReadLock();
        platformLock.ExitReadLock();
        platformLock.ExitReadLock();
        platformLock.EnterUpgradeableReadLock();
        platformLock.EnterWriteLock();
        platformLock.ExitWriteLock();
        platformLock.ExitUpgradeableReadLock();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000_000; index++)
        {
            platformLock.EnterReadLock();
            platformLock.ExitReadLock();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        var originalFactory = PlatformLock.Factory;
        var replacement = new ProbePlatformLock();
        try
        {
            PlatformLock.Factory = () => replacement;
            Assert.Same(replacement, PlatformLock.Create());
        }
        finally
        {
            PlatformLock.Factory = originalFactory;
        }
    }

    [Fact]
    public void ComInitializationIsIdempotentOnEveryPlatform()
    {
        using var initialization = new SKAutoCoInitialize();
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(initialization.Initialized);
        }

        initialization.Uninitialize();
        Assert.False(initialization.Initialized);
        initialization.Uninitialize();
    }

    private sealed class ProbePlatformLock : IPlatformLock
    {
        public void EnterReadLock()
        {
        }

        public void ExitReadLock()
        {
        }

        public void EnterUpgradeableReadLock()
        {
        }

        public void ExitUpgradeableReadLock()
        {
        }

        public void EnterWriteLock()
        {
        }

        public void ExitWriteLock()
        {
        }
    }
}
