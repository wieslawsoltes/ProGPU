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
    public void MacHotkeysIncludeCommandArrowLineNavigation()
    {
        PlatformHotkeyConfiguration hotkeys =
            SilkNetPlatform.CreateHotkeyConfiguration(
                isMacOS: true);

        Assert.Contains(
            new KeyGesture(Key.Left, KeyModifiers.Meta),
            hotkeys.MoveCursorToTheStartOfLine);
        Assert.Contains(
            new KeyGesture(
                Key.Right,
                KeyModifiers.Meta | KeyModifiers.Shift),
            hotkeys.MoveCursorToTheEndOfLineWithSelection);
    }

    [Fact]
    public void WindowsHotkeysIncludeShiftF10ContextMenu()
    {
        PlatformHotkeyConfiguration hotkeys =
            SilkNetPlatform.CreateHotkeyConfiguration(
                isMacOS: false,
                isWindows: true);

        Assert.Contains(
            new KeyGesture(Key.F10, KeyModifiers.Shift),
            hotkeys.OpenContextMenu);
    }

    [Theory]
    [InlineData(true, false, "⌘")]
    [InlineData(false, true, "Win")]
    [InlineData(false, false, "Super")]
    public void KeyGestureFormattingNamesThePlatformMetaKey(
        bool isMacOS,
        bool isWindows,
        string expected)
    {
        KeyGestureFormatInfo format =
            SilkNetPlatform.CreateKeyGestureFormatInfo(
                isMacOS,
                isWindows);

        Assert.Equal(expected, format.Meta);
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
        KeyGestureFormatInfo format = AvaloniaLocator.Current
            .GetRequiredService<KeyGestureFormatInfo>();

        Assert.Same(hotkeys, settings.HotkeyConfiguration);
        Assert.Equal(
            OperatingSystem.IsMacOS()
                ? KeyModifiers.Meta
                : KeyModifiers.Control,
            hotkeys.CommandModifiers);
        Assert.Equal(
            OperatingSystem.IsMacOS()
                ? "⌘"
                : OperatingSystem.IsWindows()
                    ? "Win"
                    : "Super",
            format.Meta);
    }
}
