using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;

namespace ProGPU.WinUI.Themes.Fluent;

/// <summary>
/// Entry point for the source-generated, unchanged Microsoft UI XAML Fluent resource dictionary.
/// Referencing this type loads the assembly whose generated module initializer registers the URI.
/// </summary>
public static class FluentThemeResources
{
    public const string ResourcePath =
        "ProGPU.WinUI.Themes.Fluent/Themes/Generic.xaml";

    public static ResourceDictionary CreateDictionary()
    {
        // Browser WebAssembly AOT can defer a referenced assembly's module constructor
        // until after this public entry point is reached. Root and run the generated,
        // idempotent provider registration before resolving the resource URI.
        RuntimeHelpers.RunModuleConstructor(typeof(FluentThemeResources).Module.ModuleHandle);
        return XamlResourceProviderRegistry.Create(
            new Uri(ResourcePath, UriKind.Relative));
    }

    public static ResourceDictionary Apply(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var dictionary = CreateDictionary();
        application.Resources.MergedDictionaries.Add(dictionary);
        return dictionary;
    }
}
