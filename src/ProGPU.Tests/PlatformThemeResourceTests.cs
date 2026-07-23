using Microsoft.UI.Xaml;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlatformThemeResourceCollection
{
    public const string Name = "Platform theme resources";
}

[Collection(PlatformThemeResourceCollection.Name)]
public sealed class PlatformThemeResourceTests
{
    [Fact]
    public void HighContrastThemeDictionaryOverridesRequestedLightOrDarkTheme()
    {
        var root = new ResourceDictionary();
        var merged = new ResourceDictionary();
        merged.ThemeDictionaries["Default"] = new ResourceDictionary
        {
            ["ThemeValue"] = "default"
        };
        merged.ThemeDictionaries["Light"] = new ResourceDictionary
        {
            ["ThemeValue"] = "light"
        };
        merged.ThemeDictionaries["HighContrast"] = new ResourceDictionary
        {
            ["ThemeValue"] = "contrast"
        };
        root.MergedDictionaries.Add(merged);

        Assert.True(root.TryLookup(
            "ThemeValue",
            ElementTheme.Light,
            isHighContrast: true,
            out var lightContrast));
        Assert.Equal("contrast", lightContrast);
        Assert.True(root.TryLookup(
            "ThemeValue",
            ElementTheme.Dark,
            isHighContrast: true,
            out var darkContrast));
        Assert.Equal("contrast", darkContrast);
        Assert.True(root.TryLookup(
            "ThemeValue",
            ElementTheme.Light,
            isHighContrast: false,
            out var light));
        Assert.Equal("light", light);
    }

    [Fact]
    public void PlatformProviderPrecedesFallbackAndPublishesThemeStateChanges()
    {
        var previousProvider = XamlPlatformResources.Provider;
        var previousHighContrast = ThemeManager.IsHighContrast;
        var provider = new TestPlatformResourceProvider
        {
            IsHighContrast = true
        };
        provider.Set(
            "SystemColorWindowTextColor",
            new Color(0x12, 0x34, 0x56));
        var notifications = 0;
        void OnThemeChanged() => notifications++;

        ThemeManager.ThemeChanged += OnThemeChanged;
        try
        {
            XamlPlatformResources.Provider = provider;

            var resolved = XamlResourceResolver.Resolve<Color>(
                lookupRoot: null,
                "SystemColorWindowTextColor");

            Assert.Equal((byte)0x12, resolved.R);
            Assert.Equal((byte)0x34, resolved.G);
            Assert.Equal((byte)0x56, resolved.B);
            Assert.True(ThemeManager.IsHighContrast);
            Assert.True(provider.LastContext.IsHighContrast);
            Assert.Equal(ThemeManager.CurrentTheme, provider.LastContext.Theme);
            Assert.Equal(ThemeManager.CurrentThemeFamily, provider.LastContext.ThemeFamily);

            provider.IsHighContrast = false;
            provider.Set(
                "SystemColorWindowTextColor",
                new Color(0x65, 0x43, 0x21));
            provider.PublishResourcesChanged();

            var updated = XamlResourceResolver.Resolve<Color>(
                lookupRoot: null,
                "SystemColorWindowTextColor");
            Assert.Equal((byte)0x65, updated.R);
            Assert.Equal((byte)0x43, updated.G);
            Assert.Equal((byte)0x21, updated.B);
            Assert.False(ThemeManager.IsHighContrast);
            Assert.True(notifications >= 2);
        }
        finally
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            XamlPlatformResources.Provider = previousProvider;
            ThemeManager.IsHighContrast = previousHighContrast;
        }
    }

    internal sealed class TestPlatformResourceProvider : IXamlPlatformResourceProvider
    {
        private readonly Dictionary<object, object?> _resources = new();

        public bool IsHighContrast { get; set; }
        public XamlPlatformResourceContext LastContext { get; private set; }

        public event EventHandler? ResourcesChanged;

        public void Set(object key, object? value) => _resources[key] = value;

        public void PublishResourcesChanged() =>
            ResourcesChanged?.Invoke(this, EventArgs.Empty);

        public bool TryGetResource(
            object key,
            in XamlPlatformResourceContext context,
            out object? value)
        {
            LastContext = context;
            return _resources.TryGetValue(key, out value);
        }
    }
}
