using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.SilkNet;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class PlatformSettingsContractTests
{
    [Theory]
    [InlineData(
        false,
        KeyModifiers.Control,
        KeyModifiers.Control)]
    [InlineData(
        true,
        KeyModifiers.Meta,
        KeyModifiers.Alt)]
    public void HotkeysFollowDesktopPlatformConventions(
        bool isMacOS,
        KeyModifiers expectedCommand,
        KeyModifiers expectedWholeWord)
    {
        PlatformHotkeyConfiguration hotkeys =
            SilkNetPlatform.CreateHotkeyConfiguration(isMacOS);

        Assert.Equal(expectedCommand, hotkeys.CommandModifiers);
        Assert.Equal(KeyModifiers.Shift, hotkeys.SelectionModifiers);
        Assert.Equal(
            expectedWholeWord,
            hotkeys.WholeWordTextActionModifiers);
    }

    [Fact]
    public void RegistrationProvidesSettingsAndTheirHotkeys()
    {
        using var scope = AvaloniaLocator.EnterScope();

        SilkNetPlatform.RegisterPlatformSettings();

        IPlatformSettings settings = AvaloniaLocator.Current
            .GetRequiredService<IPlatformSettings>();
        PlatformHotkeyConfiguration hotkeys = AvaloniaLocator.Current
            .GetRequiredService<PlatformHotkeyConfiguration>();

        Assert.Same(hotkeys, settings.HotkeyConfiguration);
        Assert.Equal(
            OperatingSystem.IsMacOS()
                ? KeyModifiers.Meta
                : KeyModifiers.Control,
            hotkeys.CommandModifiers);
    }
}
