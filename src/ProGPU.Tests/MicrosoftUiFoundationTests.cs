using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using ProGPU.WinUI.Platform;
using System.Globalization;
using System.Reflection;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

[Collection(PlatformThemeResourceCollection.Name)]
public sealed class MicrosoftUiFoundationTests
{
    [Fact]
    public void IdentifierValuesPreserveEqualityAndHashSemantics()
    {
        var display = new DisplayId(0x1234);
        var icon = new IconId(0x1234);
        var window = new WindowId(0x1234);

        Assert.Equal(new DisplayId(0x1234), display);
        Assert.Equal(new IconId(0x1234), icon);
        Assert.Equal(new WindowId(0x1234), window);
        Assert.True(display == new DisplayId(0x1234));
        Assert.True(icon != new IconId(0x4321));
        Assert.Equal(window.GetHashCode(), new WindowId(0x1234).GetHashCode());

        display.Value = 0x4321;
        Assert.Equal(0x4321UL, display.Value);
    }

    [Fact]
    public void Win32InteropRoundTripsTypedHandleBits()
    {
        var handle = new IntPtr(0x1234_5678);

        Assert.Equal(
            handle,
            Win32Interop.GetMonitorFromDisplayId(
                Win32Interop.GetDisplayIdFromMonitor(handle)));
        Assert.Equal(
            handle,
            Win32Interop.GetIconFromIconId(
                Win32Interop.GetIconIdFromIcon(handle)));
        Assert.Equal(
            handle,
            Win32Interop.GetWindowFromWindowId(
                Win32Interop.GetWindowIdFromWindow(handle)));
    }

    [Fact]
    public void Win32InteropPreservesNullHandle()
    {
        Assert.Equal(default, Win32Interop.GetDisplayIdFromMonitor(IntPtr.Zero));
        Assert.Equal(default, Win32Interop.GetIconIdFromIcon(IntPtr.Zero));
        Assert.Equal(default, Win32Interop.GetWindowIdFromWindow(IntPtr.Zero));
        Assert.Equal(IntPtr.Zero, Win32Interop.GetWindowFromWindowId(default));
    }

    [Fact]
    public void ColorHelperPreservesArgbChannels()
    {
        var color = ColorHelper.FromArgb(0x11, 0x22, 0x33, 0x44);

        Assert.Equal(0x11, color.A);
        Assert.Equal(0x22, color.R);
        Assert.Equal(0x33, color.G);
        Assert.Equal(0x44, color.B);
    }

    [Fact]
    public void ColorHelperUsesTypedLocalizedDisplayNameProvider()
    {
        var previousProvider = XamlPlatformResources.Provider;
        var provider = new TestHighContrastProvider();
        try
        {
            XamlPlatformResources.Provider = provider;
            Windows.UI.Color color =
                ColorHelper.FromArgb(0xFF, 0x64, 0x95, 0xED);

            Assert.Equal(
                "Localized Cornflower",
                ColorHelper.ToDisplayName(color));
            Assert.Equal(color, provider.DisplayNameColor);
            Assert.Equal(
                CultureInfo.CurrentUICulture,
                provider.DisplayNameCulture);
        }
        finally
        {
            XamlPlatformResources.Provider = previousProvider;
        }
    }

    [Fact]
    public void ColorHelperFailsExplicitlyWithoutDisplayNameProvider()
    {
        var previousProvider = XamlPlatformResources.Provider;
        try
        {
            XamlPlatformResources.Provider = null;

            Assert.Throws<PlatformNotSupportedException>(
                () => ColorHelper.ToDisplayName(Colors.Red));
        }
        finally
        {
            XamlPlatformResources.Provider = previousProvider;
        }
    }

    [Fact]
    public void ColorsExposeTheOfficialStaticPropertyShape()
    {
        var properties = typeof(Colors).GetProperties(
            BindingFlags.Public | BindingFlags.Static);

        Assert.Equal(141, properties.Length);
        Assert.All(
            properties,
            property =>
            {
                Assert.Equal(typeof(Windows.UI.Color), property.PropertyType);
                Assert.NotNull(property.GetMethod);
                Assert.True(property.GetMethod.IsStatic);
                Assert.Null(property.SetMethod);
            });
        Assert.Empty(typeof(Colors).GetConstructors());

        ulong fingerprint = 14695981039346656037UL;
        foreach (var property in properties.OrderBy(
                     property => property.Name,
                     StringComparer.Ordinal))
        {
            foreach (char character in property.Name)
                fingerprint = HashByte(fingerprint, (byte)character);
            fingerprint = HashByte(fingerprint, 0);

            var color = Assert.IsType<Windows.UI.Color>(
                property.GetValue(null));
            fingerprint = HashByte(fingerprint, color.A);
            fingerprint = HashByte(fingerprint, color.R);
            fingerprint = HashByte(fingerprint, color.G);
            fingerprint = HashByte(fingerprint, color.B);
        }

        Assert.Equal(0x04C213E8128032FFUL, fingerprint);
    }

    [Fact]
    public void ColorsPreservePublishedArgbValuesAndAliases()
    {
        AssertColor(0xFFF0F8FFu, Colors.AliceBlue);
        AssertColor(0xFF000000u, Colors.Black);
        AssertColor(0xFF6495EDu, Colors.CornflowerBlue);
        AssertColor(0xFFFFD700u, Colors.Gold);
        AssertColor(0xFF4B0082u, Colors.Indigo);
        AssertColor(0xFF00FF00u, Colors.Lime);
        AssertColor(0xFFC71585u, Colors.MediumVioletRed);
        AssertColor(0xFFFFDAB9u, Colors.PeachPuff);
        AssertColor(0xFF4682B4u, Colors.SteelBlue);
        AssertColor(0x00FFFFFFu, Colors.Transparent);
        AssertColor(0xFFFFFFFFu, Colors.White);
        AssertColor(0xFF9ACD32u, Colors.YellowGreen);
        Assert.Equal(Colors.Aqua, Colors.Cyan);
        Assert.Equal(Colors.Fuchsia, Colors.Magenta);
    }

    [Fact]
    public void ColorsHaveAllocationFreeSteadyStateAccess()
    {
        _ = Colors.AliceBlue;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int channelSum = 0;
        for (int index = 0; index < 100_000; index++)
        {
            var color = Colors.CornflowerBlue;
            channelSum += color.A + color.R + color.G + color.B;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(100_000 * (0xFF + 0x64 + 0x95 + 0xED), channelSum);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData(typeof(ColorHelper), 0x00010000u)]
    [InlineData(typeof(Colors), 0x00010000u)]
    [InlineData(typeof(DisplayId), 0x00010000u)]
    [InlineData(typeof(IconId), 0x00010000u)]
    [InlineData(typeof(WindowId), 0x00010000u)]
    [InlineData(typeof(ClosableNotifierHandler), 0x00010004u)]
    [InlineData(typeof(IClosableNotifier), 0x00010004u)]
    [InlineData(typeof(ThemeSettings), 0x00010004u)]
    public void FoundationTypesPublishOfficialContractVersion(
        Type type,
        uint expectedVersion)
    {
        var attribute = Assert.Single(
            type.GetCustomAttributesData(),
            attribute =>
                attribute.AttributeType ==
                typeof(ContractVersionAttribute));

        Assert.Collection(
            attribute.ConstructorArguments,
            contract => Assert.Equal(
                "Microsoft.Foundation.WindowsAppSDKContract",
                contract.Value),
            version => Assert.Equal(expectedVersion, version.Value));
    }

    [Fact]
    public void ContractVersionAttributeExposesOfficialConstructors()
    {
        var signatures = typeof(ContractVersionAttribute)
            .GetConstructors()
            .Select(
                constructor => string.Join(
                    ",",
                    constructor.GetParameters()
                        .Select(parameter => parameter.ParameterType.Name)))
            .OrderBy(signature => signature, StringComparer.Ordinal);

        Assert.Equal(
            ["String,UInt32", "Type,UInt32", "UInt32"],
            signatures);
    }

    [Fact]
    public void ThemeSettingsRequiresNonzeroWindowId()
    {
        Assert.Throws<ArgumentException>(
            () => ThemeSettings.CreateForWindowId(default));
    }

    [Fact]
    public void ThemeSettingsTracksContrastPropertiesAndChanges()
    {
        var previousProvider = XamlPlatformResources.Provider;
        var provider = new TestHighContrastProvider();
        try
        {
            XamlPlatformResources.Provider = provider;
            var settings = ThemeSettings.CreateForWindowId(
                new WindowId(0x1234));
            int notifications = 0;
            ThemeSettings? eventSender = null;
            object? eventArgs = null;
            settings.Changed += (sender, args) =>
            {
                notifications++;
                eventSender = sender;
                eventArgs = args;
            };

            Assert.False(settings.HighContrast);
            Assert.Equal(string.Empty, settings.HighContrastScheme);

            provider.Publish(true, "High Contrast Black");

            Assert.True(settings.HighContrast);
            Assert.Equal(
                "High Contrast Black",
                settings.HighContrastScheme);
            Assert.Equal(1, notifications);
            Assert.Same(settings, eventSender);
            Assert.Same(EventArgs.Empty, eventArgs);

            provider.Publish(true, "High Contrast Black");
            Assert.Equal(1, notifications);

            provider.Publish(true, "High Contrast White");
            Assert.Equal(2, notifications);
            Assert.Equal(
                "High Contrast White",
                settings.HighContrastScheme);

            provider.Publish(false, "ignored");
            Assert.False(settings.HighContrast);
            Assert.Equal(string.Empty, settings.HighContrastScheme);
            Assert.Equal(3, notifications);
        }
        finally
        {
            XamlPlatformResources.Provider = previousProvider;
        }
    }

    [Fact]
    public void ClosableNotifierContractExposesApplicationAndFrameworkEvents()
    {
        var notifier = new TestClosableNotifier();
        var notifications = new List<string>();
        notifier.Closed += () => notifications.Add("application");
        notifier.FrameworkClosed += () => notifications.Add("framework");

        notifier.Close();

        Assert.True(notifier.IsClosed);
        Assert.Equal(["framework", "application"], notifications);
    }

    private sealed class TestClosableNotifier : IClosableNotifier
    {
        public bool IsClosed { get; private set; }

        public event ClosableNotifierHandler? Closed;

        public event ClosableNotifierHandler? FrameworkClosed;

        public void Close()
        {
            IsClosed = true;
            FrameworkClosed?.Invoke();
            Closed?.Invoke();
        }
    }

    private sealed class TestHighContrastProvider :
        IXamlPlatformResourceProvider,
        IHighContrastSchemeProvider,
        IColorDisplayNameProvider
    {
        public bool IsHighContrast { get; private set; }

        public string HighContrastScheme { get; private set; } =
            string.Empty;

        public event EventHandler? ResourcesChanged;

        public Windows.UI.Color DisplayNameColor { get; private set; }

        public CultureInfo? DisplayNameCulture { get; private set; }

        public void Publish(bool highContrast, string scheme)
        {
            IsHighContrast = highContrast;
            HighContrastScheme = scheme;
            ResourcesChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool TryGetResource(
            object key,
            in XamlPlatformResourceContext context,
            out object? value)
        {
            value = null;
            return false;
        }

        public bool TryGetColorDisplayName(
            Windows.UI.Color color,
            CultureInfo culture,
            out string displayName)
        {
            DisplayNameColor = color;
            DisplayNameCulture = culture;
            displayName = "Localized Cornflower";
            return true;
        }
    }

    private static void AssertColor(uint expected, Windows.UI.Color actual)
    {
        Assert.Equal((byte)(expected >> 24), actual.A);
        Assert.Equal((byte)(expected >> 16), actual.R);
        Assert.Equal((byte)(expected >> 8), actual.G);
        Assert.Equal((byte)expected, actual.B);
    }

    private static ulong HashByte(ulong hash, byte value) =>
        unchecked((hash ^ value) * 1099511628211UL);
}
