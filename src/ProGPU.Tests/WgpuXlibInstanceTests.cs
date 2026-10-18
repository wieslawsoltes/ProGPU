using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed unsafe class WgpuXlibInstanceTests
{
    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, false)]
    [InlineData(8u, false)]
    public void OnlyAutomaticAndGlMasksNeedTheSourceDisplay(uint mask, bool expected) =>
        Assert.Equal(expected, WgpuXlibInstance.IncludesGl(mask));

    [Fact]
    public void NativeCallRetainsOriginalDescriptorChainAndSourceDisplay()
    {
        var extras = WgpuContext.CreateNativeInstanceExtras(3, WgpuDx12ShaderCompiler.Automatic, null, null);
        var descriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        var probe = new Probe { ExpectedDescriptor = &descriptor, ExpectedChain = &extras.Chain };
        nint entry = (nint)(delegate* unmanaged[Cdecl]<InstanceDescriptor*, nint, int, Instance*>)&Capture;
        Instance* result = WgpuXlibInstance.Create(&descriptor, (nint)(&probe), 0, entry);
        Assert.Equal((nint)(&probe), (nint)result);
        Assert.Equal(1, probe.Calls);
        Assert.True(probe.ExactArguments);
        Assert.Equal(3u, extras.Backends);
        Assert.Equal((nint)(&extras.Chain), (nint)descriptor.NextInChain);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InvalidInputRejectsBeforeEnteringNativeCode(int invalid)
    {
        var descriptor = new InstanceDescriptor();
        var probe = new Probe();
        InstanceDescriptor* descriptorPointer = &descriptor;
        nint display = (nint)(&probe);
        nint entry = (nint)(delegate* unmanaged[Cdecl]<InstanceDescriptor*, nint, int, Instance*>)&Capture;
        Exception? error = null;
        try
        {
            WgpuXlibInstance.Create(invalid == 0 ? null : descriptorPointer,
                invalid == 1 ? 0 : display, invalid == 2 ? -1 : invalid == 3 ? 1 : 0,
                invalid == 4 ? 0 : entry);
        }
        catch (Exception exception) { error = exception; }
        if (invalid == 0) Assert.IsType<ArgumentNullException>(error);
        else if (invalid == 1) Assert.IsType<ArgumentException>(error);
        else Assert.IsType<NotSupportedException>(error);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void FailedNativeInitializationCannotReturnAnUninitializedInstance()
    {
        var descriptor = new InstanceDescriptor();
        var probe = new Probe { Fail = true };
        nint entry = (nint)(delegate* unmanaged[Cdecl]<InstanceDescriptor*, nint, int, Instance*>)&Capture;
        Exception? error = null;
        try { WgpuXlibInstance.Create(&descriptor, (nint)(&probe), 0, entry); }
        catch (Exception exception) { error = exception; }
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, probe.Calls);
    }

    private struct Probe
    {
        public InstanceDescriptor* ExpectedDescriptor;
        public ChainedStruct* ExpectedChain;
        public int Calls;
        public bool ExactArguments;
        public bool Fail;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Instance* Capture(InstanceDescriptor* descriptor, nint display, int screen)
    {
        var probe = (Probe*)display;
        probe->Calls++;
        probe->ExactArguments = descriptor == probe->ExpectedDescriptor &&
            descriptor->NextInChain == probe->ExpectedChain && screen == 0;
        return probe->Fail ? null : (Instance*)display;
    }
}
