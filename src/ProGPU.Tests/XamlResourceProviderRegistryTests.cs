using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class XamlResourceProviderRegistryTests
{
    [Fact]
    public void SolidColorBrushSupportsPropertyBasedXamlConstruction()
    {
        var brush = new SolidColorBrush();

        Assert.Equal(default, brush.Color);
        brush.Color = new System.Numerics.Vector4(0.25f, 0.5f, 0.75f, 1f);
        Assert.Equal(new System.Numerics.Vector4(0.25f, 0.5f, 0.75f, 1f), brush.Color);
    }

    [Fact]
    public void SourceLoadsCompiledDictionaryThroughApplicationUriSuffix()
    {
        var identity = Guid.NewGuid().ToString("N");
        var registeredPath = $"/project/Themes/{identity}.xaml";
        XamlResourceProviderRegistry.Register(registeredPath, () =>
        {
            var result = new ResourceDictionary { ["Greeting"] = "Hello" };
            result.MergedDictionaries.Add(new ResourceDictionary { ["Merged"] = 42 });
            result.ThemeDictionaries["Dark"] = new ResourceDictionary { ["ThemeValue"] = "dark" };
            return result;
        });

        var target = new ResourceDictionary
        {
            ["Old"] = "removed",
            Source = new Uri($"ms-appx:///Themes/{identity}.xaml", UriKind.RelativeOrAbsolute)
        };

        Assert.False(target.ContainsKey("Old"));
        Assert.Equal("Hello", target["Greeting"]);
        Assert.Equal(42, target.MergedDictionaries[0]["Merged"]);
        Assert.Equal("dark", ((ResourceDictionary)target.ThemeDictionaries["Dark"])["ThemeValue"]);
    }

    [Fact]
    public void LookupUsesLocalThenReverseMergedPrecedenceAndSurvivesCycles()
    {
        var dictionary = new ResourceDictionary { ["Value"] = "local" };
        var first = new ResourceDictionary { ["Value"] = "first", ["FirstOnly"] = true };
        var second = new ResourceDictionary { ["Value"] = "second" };
        dictionary.MergedDictionaries.Add(first);
        dictionary.MergedDictionaries.Add(second);
        second.MergedDictionaries.Add(dictionary);

        Assert.True(dictionary.TryLookup("Value", ElementTheme.Default, out var local));
        Assert.Equal("local", local);
        dictionary.Remove("Value");
        Assert.True(dictionary.TryLookup("Value", ElementTheme.Default, out var merged));
        Assert.Equal("second", merged);
        Assert.True(dictionary.TryLookup("FirstOnly", ElementTheme.Default, out var firstOnly));
        Assert.Equal(true, firstOnly);
        Assert.False(dictionary.TryLookup("Missing", ElementTheme.Default, out _));
    }

    [Fact]
    public void LookupSelectsThemeDictionariesInsideMergedDictionaries()
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
        root.MergedDictionaries.Add(merged);

        Assert.True(root.TryLookup("ThemeValue", ElementTheme.Light, out var light));
        Assert.Equal("light", light);
        Assert.True(root.TryLookup("ThemeValue", ElementTheme.Dark, out var fallback));
        Assert.Equal("default", fallback);
    }

    [Fact]
    public void CyclicSourceLoadFailsBeforePublishingNewContents()
    {
        var identity = Guid.NewGuid().ToString("N");
        var firstUri = new Uri($"ms-appx:///Themes/{identity}-a.xaml", UriKind.RelativeOrAbsolute);
        var secondUri = new Uri($"ms-appx:///Themes/{identity}-b.xaml", UriKind.RelativeOrAbsolute);
        XamlResourceProviderRegistry.Register($"/project/Themes/{identity}-a.xaml", () =>
            new ResourceDictionary { Source = secondUri });
        XamlResourceProviderRegistry.Register($"/project/Themes/{identity}-b.xaml", () =>
            new ResourceDictionary { Source = firstUri });
        var target = new ResourceDictionary { ["Existing"] = "preserved" };

        var exception = Assert.Throws<InvalidOperationException>(() => target.Source = firstUri);

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserved", target["Existing"]);
        Assert.Null(target.Source);
    }

    [Fact]
    public void DuplicateAndAmbiguousProviderIdentitiesNeverSelectArbitrarily()
    {
        var identity = Guid.NewGuid().ToString("N");
        var firstPath = $"/package-a/Themes/{identity}.xaml";
        var secondPath = $"/package-b/Themes/{identity}.xaml";
        XamlResourceProviderRegistry.Register(firstPath,
            () => new ResourceDictionary { ["Provider"] = "first" });
        XamlResourceProviderRegistry.Register(secondPath,
            () => new ResourceDictionary { ["Provider"] = "second" });

        Assert.Throws<InvalidOperationException>(() =>
            XamlResourceProviderRegistry.Register(firstPath, () => new ResourceDictionary()));
        Assert.False(XamlResourceProviderRegistry.TryCreate(
            new Uri($"ms-appx:///Themes/{identity}.xaml", UriKind.RelativeOrAbsolute), out _));
        Assert.True(XamlResourceProviderRegistry.TryCreate(
            new Uri(firstPath, UriKind.RelativeOrAbsolute), out var exact));
        Assert.Equal("first", exact!["Provider"]);
        Assert.False(XamlResourceProviderRegistry.TryCreate(
            new Uri($"ms-appx:///Themes/missing-{identity}.xaml", UriKind.RelativeOrAbsolute), out _));
    }

    [Fact]
    public void QualifiedProviderAuthoritiesAreExactAndNeverEnterSuffixFallback()
    {
        var identity = Guid.NewGuid().ToString("N");
        XamlResourceProviderRegistry.Register(
            $"ms-appx://PackageA/Themes/{identity}.xaml",
            () => new ResourceDictionary { ["Provider"] = "A" });
        XamlResourceProviderRegistry.Register(
            $"ms-appx://PackageB/Themes/{identity}.xaml",
            () => new ResourceDictionary { ["Provider"] = "B" });

        Assert.True(XamlResourceProviderRegistry.TryCreate(
            new Uri($"ms-appx://PackageA/Themes/{identity}.xaml", UriKind.RelativeOrAbsolute), out var packageA));
        Assert.Equal("A", packageA!["Provider"]);
        Assert.True(XamlResourceProviderRegistry.TryCreate(
            new Uri($"ms-appx://PackageB/Themes/{identity}.xaml", UriKind.RelativeOrAbsolute), out var packageB));
        Assert.Equal("B", packageB!["Provider"]);
        Assert.False(XamlResourceProviderRegistry.TryCreate(
            new Uri($"ms-appx://Missing/Themes/{identity}.xaml", UriKind.RelativeOrAbsolute), out _));
        Assert.False(XamlResourceProviderRegistry.TryCreate(
            new Uri($"ms-appx:///Themes/{identity}.xaml", UriKind.RelativeOrAbsolute), out _));
    }

    [Fact]
    public void ThemeResourceBrushUsesGeneratedLookupRootAndReevaluatesOnThemeChange()
    {
        var light = new SolidColorBrush(0xFFFFFFFF);
        var dark = new SolidColorBrush(0x000000FF);
        var target = new Border();
        target.Resources.ThemeDictionaries["Light"] = new ResourceDictionary
        {
            ["LocalBrush"] = light,
            ["AliasBrush"] = new ThemeResource(target, "LocalBrush")
        };
        target.Resources.ThemeDictionaries["Dark"] = new ResourceDictionary
        {
            ["LocalBrush"] = dark,
            ["AliasBrush"] = new ThemeResource(target, "LocalBrush")
        };
        target.RequestedTheme = ElementTheme.Dark;

        target.Background = new ThemeResourceBrush(target, "AliasBrush");

        Assert.Same(dark, target.Background);
        target.RequestedTheme = ElementTheme.Light;
        Assert.Same(light, target.Background);

        target.Resources["CycleA"] = new ThemeResource(target, "CycleB");
        target.Resources["CycleB"] = new ThemeResource(target, "CycleA");
        Assert.Throws<InvalidOperationException>(() =>
            target.Background = new ThemeResourceBrush(target, "CycleA"));
    }

    [Fact]
    public void TypeValuedKeysRemainObjectsThroughStaticAndThemeResolution()
    {
        var expected = new SolidColorBrush(0x336699FF);
        var target = new Border();
        target.Resources[typeof(Border)] = expected;

        Assert.Same(expected, XamlResourceResolver.Resolve<Brush>(target, typeof(Border)));

        var marker = new ThemeResourceBrush(target, typeof(Border));
        Assert.IsAssignableFrom<Type>(marker.ResourceKey);
        target.Background = marker;
        Assert.Same(expected, target.Background);
    }

    [Fact]
    public void ResourceGenerationsPropagateAcrossMergedGraphsWithoutCycling()
    {
        var first = new ResourceDictionary();
        var second = new ResourceDictionary();
        first.MergedDictionaries.Add(second);
        second.MergedDictionaries.Add(first);
        var firstBefore = first.Generation;
        var secondBefore = second.Generation;
        var firstNotifications = 0;
        var secondNotifications = 0;
        first.Changed += (_, _) => firstNotifications++;
        second.Changed += (_, _) => secondNotifications++;

        second["Live"] = 1;

        Assert.Equal(firstBefore + 1, first.Generation);
        Assert.Equal(secondBefore + 1, second.Generation);
        Assert.Equal(1, firstNotifications);
        Assert.Equal(1, secondNotifications);
        second["Live"] = 1;
        Assert.Equal(secondBefore + 1, second.Generation);

        IDictionary<object, object> dictionaryView = second;
        dictionaryView["ViaInterface"] = 2;
        Assert.Equal(secondBefore + 2, second.Generation);
    }

    [Fact]
    public void ResourceMutationReevaluatesRetainedThemeResourceWithoutThemeSwitch()
    {
        var first = new SolidColorBrush(0xFF0000FF);
        var second = new SolidColorBrush(0x00FF00FF);
        var target = new Border();
        target.Resources["LiveBrush"] = first;
        target.Background = new ThemeResourceBrush(target, "LiveBrush");
        Assert.Same(first, target.Background);

        target.Resources["LiveBrush"] = second;

        Assert.Same(second, target.Background);
    }

    [Fact]
    public void ThemeDictionaryChildMutationPropagatesToOwningElement()
    {
        var first = new SolidColorBrush(0x112233FF);
        var second = new SolidColorBrush(0x445566FF);
        var dark = new ResourceDictionary { ["ThemeBrush"] = first };
        var target = new Border { RequestedTheme = ElementTheme.Dark };
        target.Resources.ThemeDictionaries["Dark"] = dark;
        target.Background = new ThemeResourceBrush(target, "ThemeBrush");
        Assert.Same(first, target.Background);

        dark["ThemeBrush"] = second;

        Assert.Same(second, target.Background);
    }

    [Fact]
    public void SourceReplacementPublishesExactlyOneGeneration()
    {
        var identity = Guid.NewGuid().ToString("N");
        XamlResourceProviderRegistry.Register($"/Themes/{identity}.xaml", () =>
        {
            var result = new ResourceDictionary { ["One"] = 1, ["Two"] = 2 };
            result.MergedDictionaries.Add(new ResourceDictionary { ["Merged"] = 3 });
            return result;
        });
        var target = new ResourceDictionary { ["Old"] = 0 };
        var before = target.Generation;
        var notifications = 0;
        ResourceDictionaryChangedEventArgs? change = null;
        target.Changed += (_, args) => { notifications++; change = args; };

        target.Source = new Uri($"ms-appx:///Themes/{identity}.xaml", UriKind.RelativeOrAbsolute);

        Assert.Equal(before + 1, target.Generation);
        Assert.Equal(1, notifications);
        Assert.Equal(ResourceDictionaryChangeKind.Source, change?.Kind);
        Assert.False(target.ContainsKey("Old"));
        Assert.Equal(3, target.MergedDictionaries[0]["Merged"]);
    }
}
