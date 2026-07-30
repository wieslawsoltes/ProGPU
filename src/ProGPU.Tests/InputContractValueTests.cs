using Microsoft.UI.Input;
using System.Reflection;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

public sealed class InputContractValueTests
{
    [Fact]
    public void InputEnumsPublishOfficialValuesAndStorage()
    {
        Assert.Equal(
            typeof(uint),
            Enum.GetUnderlyingType(
                typeof(InputPointerSourceDeviceKinds)));
        Assert.Equal(
            typeof(uint),
            Enum.GetUnderlyingType(
                typeof(VirtualKeyStates)));
        Assert.Equal(
            7,
            (int)FocusNavigationReason.Down);
        Assert.Equal(
            2,
            (int)FocusNavigationResult.NoFocusableElements);
        Assert.Equal(
            2,
            (int)InputActivationState.Activated);
        Assert.Equal(
            7U,
            (uint)(InputPointerSourceDeviceKinds.Touch |
                InputPointerSourceDeviceKinds.Pen |
                InputPointerSourceDeviceKinds.Mouse));
        Assert.Equal(
            16,
            (int)InputSystemCursorShape.AppStarting);
        Assert.Equal(
            8,
            (int)MoveSizeOperation.SizeTopRight);
        Assert.Equal(
            9,
            (int)NonClientRegionKind.Passthrough);
        Assert.Equal(
            3U,
            (uint)(VirtualKeyStates.Down |
                VirtualKeyStates.Locked));
    }

    [Theory]
    [InlineData(typeof(FocusNavigationReason), 0x00010005U)]
    [InlineData(typeof(FocusNavigationResult), 0x00010005U)]
    [InlineData(typeof(InputActivationState), 0x00010001U)]
    [InlineData(typeof(InputPointerSourceDeviceKinds), 0x00010000U)]
    [InlineData(typeof(InputSystemCursorShape), 0x00010000U)]
    [InlineData(typeof(MoveSizeOperation), 0x00010006U)]
    [InlineData(typeof(NonClientRegionKind), 0x00010004U)]
    [InlineData(typeof(VirtualKeyStates), 0x00010004U)]
    [InlineData(typeof(PhysicalKeyStatus), 0x00010004U)]
    public void InputValuesPublishOfficialContractVersion(
        Type type,
        uint expectedVersion)
    {
        CustomAttributeData attribute = Assert.Single(
            type.GetCustomAttributesData(),
            static candidate =>
                candidate.AttributeType ==
                typeof(ContractVersionAttribute));
        Assert.Equal(
            "Microsoft.Foundation.WindowsAppSDKContract",
            Assert.IsType<string>(
                attribute.ConstructorArguments[0].Value));
        Assert.Equal(
            expectedVersion,
            Assert.IsType<uint>(
                attribute.ConstructorArguments[1].Value));
    }

    [Fact]
    public void PhysicalKeyStatusIsAllocationFreeValueState()
    {
        const int Count = 100_000;
        var expected = new PhysicalKeyStatus(
            2,
            0x1e,
            true,
            false,
            true,
            false);
        _ = expected.GetHashCode();
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < Count; index++)
        {
            var actual = new PhysicalKeyStatus(
                2,
                0x1e,
                true,
                false,
                true,
                false);
            if (actual == expected)
                checksum ^= actual.GetHashCode();
        }

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }
}
