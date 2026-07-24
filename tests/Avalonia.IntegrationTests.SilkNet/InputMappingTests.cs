using Avalonia.Input;
using Avalonia.SilkNet;
using Silk.NET.Input;
using Xunit;
using AvaloniaKey = Avalonia.Input.Key;
using SilkKey = Silk.NET.Input.Key;

namespace Avalonia.IntegrationTests.SilkNet;

public class InputMappingTests
{
    [Theory]
    [InlineData(SilkKey.A, AvaloniaKey.A, PhysicalKey.A)]
    [InlineData(SilkKey.Z, AvaloniaKey.Z, PhysicalKey.Z)]
    [InlineData(SilkKey.Number7, AvaloniaKey.D7, PhysicalKey.Digit7)]
    [InlineData(SilkKey.Apostrophe, AvaloniaKey.OemQuotes, PhysicalKey.Quote)]
    [InlineData(SilkKey.LeftBracket, AvaloniaKey.OemOpenBrackets, PhysicalKey.BracketLeft)]
    [InlineData(SilkKey.PageDown, AvaloniaKey.PageDown, PhysicalKey.PageDown)]
    [InlineData(SilkKey.F24, AvaloniaKey.F24, PhysicalKey.F24)]
    [InlineData(SilkKey.KeypadAdd, AvaloniaKey.Add, PhysicalKey.NumPadAdd)]
    [InlineData(SilkKey.ControlRight, AvaloniaKey.RightCtrl, PhysicalKey.ControlRight)]
    [InlineData(SilkKey.SuperLeft, AvaloniaKey.LWin, PhysicalKey.MetaLeft)]
    [InlineData(SilkKey.Menu, AvaloniaKey.Apps, PhysicalKey.ContextMenu)]
    [InlineData(SilkKey.Unknown, AvaloniaKey.None, PhysicalKey.None)]
    public void MapsLogicalAndPhysicalKeys(
        SilkKey silkKey,
        AvaloniaKey expectedKey,
        PhysicalKey expectedPhysicalKey)
    {
        var mapping = SilkNetInputMappings.MapKey(silkKey);

        Assert.Equal(expectedKey, mapping.Key);
        Assert.Equal(expectedPhysicalKey, mapping.PhysicalKey);
    }

#if AVALONIA_MONOREPO_TESTS
    [Fact]
    public void KeyboardDeviceIsStableSingleton()
    {
        Assert.Same(SilkNetKeyboardDevice.Instance, SilkNetKeyboardDevice.Instance);
        Assert.Same(
            SilkNetKeyboardDevice.Instance,
            AvaloniaLocator.Current.GetService<IKeyboardDevice>());
    }
#endif
}
