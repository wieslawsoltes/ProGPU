using System.Text;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.SourceGeneration;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlGeneratorHarnessTests
{
    [Fact]
    public void UnchangedRunCachesParseAndInfosetStage()
    {
        const string program = "namespace Demo { public sealed class Placeholder { } }";
        const string xaml = "<Placeholder xmlns=\"using:Demo\" />";
        var compilation = CSharpCompilation.Create(
            "IncrementalHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText("Placeholder.xaml", xaml)),
            CSharpParseOptions.Default,
            optionsProvider: null,
            new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(compilation);
        var result = Assert.Single(driver.GetRunResult().Results);
        var parseStep = Assert.Single(result.TrackedSteps["XamlParseAndInfoset"]);
        Assert.NotEmpty(parseStep.Outputs);
        Assert.All(parseStep.Outputs, output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }

    [Fact]
    public void ResourceDependencySlicesDoNotInvalidateUnrelatedGeneratorOutputs()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } }
  public static class XamlTemplateFactory {
    public static void BeginNameScope(object root) { }
    public static void RegisterName(object root, string name, object value) { }
  }
}
namespace Microsoft.UI.Xaml.HotReload { public interface IHotReloadable { void Reload(HotReloadContext context); } public sealed class HotReloadContext { } }
namespace Microsoft.UI.Xaml {
  public class FrameworkElement { public string? Name { get; set; } }
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> {
    public string? Source { get; set; }
    public System.Collections.Generic.List<ResourceDictionary> MergedDictionaries { get; } = new();
  }
  public static class XamlResourceResolver { public static T Resolve<T>(object root, object key) => default!; }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")] public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } public Microsoft.UI.Xaml.ResourceDictionary Resources { get; } = new(); }
  public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { public string? Text { get; set; } }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        var page = new InMemoryAdditionalText("/project/Pages/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Demo.MainPage">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source="../Themes/Colors.xaml" /></ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
  <TextBlock Text="{StaticResource Accent}" />
</Page>
""");
        var colors = new InMemoryAdditionalText("/project/Themes/Colors.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><x:String x:Key="Accent">red</x:String></ResourceDictionary>
""");
        var unrelated = new InMemoryAdditionalText("/project/Themes/Unrelated.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><x:String x:Key="Other">blue</x:String></ResourceDictionary>
""");
        var compilation = CSharpCompilation.Create(
            "ResourceIncrementalHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(page, colors, unrelated),
            CSharpParseOptions.Default,
            optionsProvider: null,
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var changedUnrelated = new InMemoryAdditionalText(unrelated.Path, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><x:String x:Key="Other2">green</x:String></ResourceDictionary>
""");
        driver = driver.ReplaceAdditionalText(unrelated, changedUnrelated).RunGenerators(compilation);
        var unrelatedRun = Assert.Single(driver.GetRunResult().Results);
        var semanticManifestOutputs = unrelatedRun.TrackedSteps["XamlSemanticResourceManifest"]
            .SelectMany(static step => step.Outputs).ToArray();
        Assert.Equal(2, semanticManifestOutputs.Count(output => output.Reason == IncrementalStepRunReason.Cached));
        Assert.Equal(1, semanticManifestOutputs.Count(output => output.Reason == IncrementalStepRunReason.Modified));
        var unrelatedOutputs = unrelatedRun.TrackedSteps["XamlBindLowerEmit"]
            .SelectMany(static step => step.Outputs).ToArray();
        Assert.Equal(2, unrelatedOutputs.Count(output => output.Reason == IncrementalStepRunReason.Cached));
        Assert.Equal(1, unrelatedOutputs.Count(output => output.Reason == IncrementalStepRunReason.Modified));

        var changedColors = new InMemoryAdditionalText(colors.Path, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><x:String x:Key="Accent">red</x:String><x:String x:Key="Accent2">orange</x:String></ResourceDictionary>
""");
        driver = driver.ReplaceAdditionalText(colors, changedColors).RunGenerators(compilation);
        var providerOutputs = Assert.Single(driver.GetRunResult().Results)
            .TrackedSteps["XamlBindLowerEmit"].SelectMany(static step => step.Outputs).ToArray();
        Assert.Equal(1, providerOutputs.Count(output => output.Reason == IncrementalStepRunReason.Cached));
        Assert.Equal(2, providerOutputs.Count(output => output.Reason == IncrementalStepRunReason.Modified));
    }

    [Fact]
    public void ThemePartitionIdentityInvalidatesDependentGeneratorOutput()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup { [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } } }
namespace Microsoft.UI.Xaml.HotReload { public interface IHotReloadable { void Reload(HotReloadContext context); } public sealed class HotReloadContext { } }
namespace Microsoft.UI.Xaml {
  public class FrameworkElement { public string? Name { get; set; } }
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> {
    public string? Source { get; set; }
    public System.Collections.Generic.List<ResourceDictionary> MergedDictionaries { get; } = new();
    public System.Collections.Generic.Dictionary<object, object> ThemeDictionaries { get; } = new();
  }
  public sealed class ThemeResource { public ThemeResource(object? root, object key) { } }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")] public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } public Microsoft.UI.Xaml.ResourceDictionary Resources { get; } = new(); }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        var page = new InMemoryAdditionalText("/project/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage"
      Content="{ThemeResource Accent}">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Colors.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var provider = new InMemoryAdditionalText("/project/Themes/Colors.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light"><x:String x:Key="Accent">light</x:String></ResourceDictionary>
    <ResourceDictionary x:Key="Dark"><x:String x:Key="Accent">dark</x:String></ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
""");
        var compilation = CSharpCompilation.Create(
            "ThemePartitionIncrementalHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(page, provider),
            CSharpParseOptions.Default,
            optionsProvider: null,
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var sources = Assert.Single(driver.GetRunResult().Results).GeneratedSources;
        Assert.Contains(sources, source => source.SourceText.ToString().Contains(
            "ThemeDictionaries.Add(\"Light\"", StringComparison.Ordinal));

        var changedProvider = new InMemoryAdditionalText(provider.Path, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Default"><x:String x:Key="Accent">light</x:String></ResourceDictionary>
    <ResourceDictionary x:Key="Dark"><x:String x:Key="Accent">dark</x:String></ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
""");
        driver = driver.ReplaceAdditionalText(provider, changedProvider).RunGenerators(compilation);
        var outputs = Assert.Single(driver.GetRunResult().Results)
            .TrackedSteps["XamlBindLowerEmit"].SelectMany(static step => step.Outputs).ToArray();
        Assert.Equal(2, outputs.Count(output => output.Reason == IncrementalStepRunReason.Modified));
    }

    [Fact]
    public void ProviderFormattingChangeKeepsDependentGeneratorOutputCached()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup { [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } } }
namespace Microsoft.UI.Xaml.HotReload { public interface IHotReloadable { void Reload(HotReloadContext context); } public sealed class HotReloadContext { } }
namespace Microsoft.UI.Xaml {
  public class FrameworkElement { public string? Name { get; set; } }
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> {
    public string? Source { get; set; }
    public System.Collections.Generic.List<ResourceDictionary> MergedDictionaries { get; } = new();
  }
  public static class XamlResourceResolver { public static T Resolve<T>(object root, object key) => default!; }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")] public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } public Microsoft.UI.Xaml.ResourceDictionary Resources { get; } = new(); }
  public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { public string? Text { get; set; } }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        var page = new InMemoryAdditionalText("/project/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Demo.MainPage">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Themes/Colors.xaml" /></ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
  <TextBlock Text="{StaticResource Accent}" />
</Page>
""");
        var compact = new InMemoryAdditionalText("/project/Themes/Colors.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><x:String x:Key="Accent">red</x:String></ResourceDictionary>
""");
        var expanded = new InMemoryAdditionalText(compact.Path, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <x:String x:Key="Accent">red</x:String>
</ResourceDictionary>
""");
        var compilation = CSharpCompilation.Create(
            "ResourceFormattingHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(page, compact),
            CSharpParseOptions.Default,
            optionsProvider: null,
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        driver = driver.ReplaceAdditionalText(compact, expanded).RunGenerators(compilation);

        var outputs = Assert.Single(driver.GetRunResult().Results)
            .TrackedSteps["XamlBindLowerEmit"].SelectMany(static step => step.Outputs).ToArray();
        Assert.Equal(1, outputs.Count(output => output.Reason == IncrementalStepRunReason.Cached));
        Assert.Equal(1, outputs.Count(output => output.Reason == IncrementalStepRunReason.Modified));
    }

    [Fact]
    public void AmbiguousResourceImportDiagnosticPointsToSourceValue()
    {
        const string program = """
namespace Microsoft.UI.Xaml {
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> {
    public string? Source { get; set; }
    public System.Collections.Generic.List<ResourceDictionary> MergedDictionaries { get; } = new();
  }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
""";
        const string importerSource = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Shared.xaml" />
  </ResourceDictionary.MergedDictionaries>
</ResourceDictionary>
""";
        var importer = new InMemoryAdditionalText("/project/Main.xaml", importerSource);
        var first = new InMemoryAdditionalText("/packages/A/Themes/Shared.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
""");
        var second = new InMemoryAdditionalText("/packages/B/Themes/Shared.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
""");
        var compilation = CSharpCompilation.Create(
            "AmbiguousResourceHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(importer, first, second),
            CSharpParseOptions.Default);

        driver = driver.RunGenerators(compilation);

        var diagnostic = Assert.Single(driver.GetRunResult().Diagnostics,
            candidate => candidate.Id == "PGXAML4004");
        Assert.Equal(importer.Path, diagnostic.Location.GetLineSpan().Path);
        Assert.Equal(2, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Equal(
            "Themes/Shared.xaml",
            importerSource.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Contains("matches 2 compiled XAML providers", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateLogicalProviderDiagnosticsPointToEachPublishingDocument()
    {
        const string program = """
namespace Microsoft.UI.Xaml {
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> { }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
""";
        const string dictionary = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
""";
        var first = new InMemoryAdditionalText("/checkout/A/Colors.xaml", dictionary);
        var second = new InMemoryAdditionalText("/checkout/B/Colors.xaml", dictionary);
        var compilation = CSharpCompilation.Create(
            "DuplicateResourceHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(second, first),
            CSharpParseOptions.Default,
            new LogicalPathsAnalyzerConfigOptionsProvider(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [first.Path] = "Themes/Colors.xaml",
                [second.Path] = "./Themes/Colors.xaml"
            }));

        driver = driver.RunGenerators(compilation);

        var diagnostics = driver.GetRunResult().Diagnostics
            .Where(candidate => candidate.Id == "PGXAML4007")
            .OrderBy(candidate => candidate.Location.GetLineSpan().Path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Equal(new[] { first.Path, second.Path },
            diagnostics.Select(candidate => candidate.Location.GetLineSpan().Path));
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(0, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
            Assert.Contains("'/Themes/Colors.xaml'", diagnostic.GetMessage(), StringComparison.Ordinal);
            Assert.DoesNotContain("/checkout/", diagnostic.GetMessage(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AdditionalFileMetadataCanOptOutOfCompilation()
    {
        var compilation = CSharpCompilation.Create(
            "OptOutHarness",
            new[] { CSharpSyntaxTree.ParseText("namespace Demo { public sealed class Placeholder { } }") },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var skipped = new InMemoryAdditionalText("Skipped.xaml", "<not-valid");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(skipped),
            CSharpParseOptions.Default,
            new TestAnalyzerConfigOptionsProvider(skipped.Path),
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var result = Assert.Single(driver.GetRunResult().Results);
        Assert.Empty(result.GeneratedSources);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id.StartsWith("PGXAML", StringComparison.Ordinal));
    }

    [Fact]
    public void LogicalPathMetadataControlsCompiledResourceIdentity()
    {
        const string program = """
namespace Microsoft.UI.Xaml {
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> { }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
""";
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="Greeting">Hello</x:String>
</ResourceDictionary>
""";
        var input = new InMemoryAdditionalText("/checkout/machine-specific/Themes/Colors.xaml", xaml);
        var compilation = CSharpCompilation.Create(
            "LogicalResourceHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(input),
            CSharpParseOptions.Default,
            new LogicalPathAnalyzerConfigOptionsProvider(input.Path, "Themes/Colors.xaml"));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var source = Assert.Single(Assert.Single(driver.GetRunResult().Results).GeneratedSources).SourceText.ToString();
        Assert.Contains("Register(\"/Themes/Colors.xaml\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("machine-specific", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QualifiedLogicalPathMetadataPreservesAuthorityInGeneratedRegistration()
    {
        const string program = """
namespace Microsoft.UI.Xaml {
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> { }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
""";
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="Greeting">Hello</x:String>
</ResourceDictionary>
""";
        var input = new InMemoryAdditionalText("/checkout/machine-specific/Themes/Colors.xaml", xaml);
        var compilation = CSharpCompilation.Create(
            "QualifiedLogicalResourceHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(input),
            CSharpParseOptions.Default,
            new LogicalPathAnalyzerConfigOptionsProvider(
                input.Path,
                "ms-appx://Contoso.Package/Themes/Colors.xaml?build=1#ignored"));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var source = Assert.Single(Assert.Single(driver.GetRunResult().Results).GeneratedSources).SourceText.ToString();
        Assert.Contains("Register(\"ms-appx://Contoso.Package/Themes/Colors.xaml\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("machine-specific", source, StringComparison.Ordinal);
        Assert.DoesNotContain("build=1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClasslessResourceFactoryUsesTargetAsDynamicLookupRoot()
    {
        const string program = """
namespace Microsoft.UI.Xaml {
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> { }
  public sealed class ThemeResource { public ThemeResource(object? root, string key) { } }
  public sealed class Setter { public object? Value { get; set; } }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
""";
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Setter x:Key="Alias" Value="{ThemeResource Actual}" />
</ResourceDictionary>
""";
        var compilation = CSharpCompilation.Create(
            "ClasslessLookupRootHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[] { new InMemoryAdditionalText("Themes/Aliases.xaml", xaml) },
            parseOptions: CSharpParseOptions.Default);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(Assert.Single(driver.GetRunResult().Results).GeneratedSources);
        var marker = Assert.Single(generated.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(), creation =>
                creation.Type.ToString().EndsWith("ThemeResource", StringComparison.Ordinal));
        Assert.Equal("target", marker.ArgumentList!.Arguments[0].Expression.ToString());
    }

    [Fact]
    public void GeneratorEnrichesPresentationNamespaceImplicitTypeKeysAcrossFiles()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup { [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } } }
namespace Microsoft.UI.Xaml.HotReload { public interface IHotReloadable { void Reload(HotReloadContext context); } public sealed class HotReloadContext { } }
namespace Microsoft.UI.Xaml {
  public class FrameworkElement { public string? Name { get; set; } }
  public class Style { public System.Type? TargetType { get; set; } }
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> {
    public System.Uri? Source { get; set; }
    public System.Collections.Generic.List<ResourceDictionary> MergedDictionaries { get; } = new();
  }
  public static class XamlResourceResolver { public static T Resolve<T>(object root, object key) => default!; }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")] public class Page : Microsoft.UI.Xaml.FrameworkElement {
    public object? Content { get; set; }
    public Microsoft.UI.Xaml.ResourceDictionary Resources { get; } = new();
  }
  public class Button : Microsoft.UI.Xaml.FrameworkElement { }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        var page = new InMemoryAdditionalText("/project/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage"
      Content="{StaticResource {x:Type Button}}">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Typed.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var provider = new InMemoryAdditionalText("/project/Themes/Typed.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style TargetType="Button" />
</ResourceDictionary>
""");
        var compilation = CSharpCompilation.Create(
            "TypedResourceGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(page, provider),
            CSharpParseOptions.Default);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(driver.GetRunResult().Results).GeneratedSources;
        Assert.Contains(generated.SelectMany(source => source.SyntaxTree.GetRoot().DescendantNodes())
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeOfExpressionSyntax>(), expression =>
                expression.Type.ToString() == "global::Microsoft.UI.Xaml.Controls.Button");
    }

    [Fact]
    public void GeneratorResolvesCrossFileConstantStaticAliasesWithoutFlatteningExpressions()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup { [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } } }
namespace Microsoft.UI.Xaml.HotReload { public interface IHotReloadable { void Reload(HotReloadContext context); } public sealed class HotReloadContext { } }
namespace Microsoft.UI.Xaml {
  public class FrameworkElement { public string? Name { get; set; } }
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> {
    public System.Uri? Source { get; set; }
    public System.Collections.Generic.List<ResourceDictionary> MergedDictionaries { get; } = new();
  }
  public static class XamlResourceResolver { public static T Resolve<T>(object root, object key) => default!; }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<ResourceDictionary> factory) { } }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")] public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } public Microsoft.UI.Xaml.ResourceDictionary Resources { get; } = new(); }
}
namespace Demo {
  public static class Keys { public const string Primary = "Accent"; public const string Alias = "Accent"; }
  public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { }
}
""";
        var page = new InMemoryAdditionalText("/project/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"
      Content="{StaticResource {x:Static local:Keys.Alias}}">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Keys.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var provider = new InMemoryAdditionalText("/project/Themes/Keys.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Static local:Keys.Primary}">value</x:String>
</ResourceDictionary>
""");
        var compilation = CSharpCompilation.Create(
            "ConstantResourceGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new ProGpuXamlSourceGenerator().AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(page, provider),
            CSharpParseOptions.Default);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var accesses = Assert.Single(driver.GetRunResult().Results).GeneratedSources
            .SelectMany(source => source.SyntaxTree.GetRoot().DescendantNodes())
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>()
            .Select(access => access.ToString()).ToArray();
        Assert.Contains("global::Demo.Keys.Primary", accesses);
        Assert.Contains("global::Demo.Keys.Alias", accesses);
    }

    [Fact]
    public void BuiltInProfileRegistryValidatesVersionAndCapabilities()
    {
        Assert.True(XamlFrameworkProfileRegistry.BuiltIn.TryCreate("winui", out var profile));
        Assert.Equal(XamlFrameworkContract.CurrentVersion, profile?.ContractVersion);
        Assert.True((profile?.Capabilities & XamlFrameworkCapabilities.StructuredCSharpEmission) != 0);
        Assert.True((profile?.Capabilities & XamlFrameworkCapabilities.Resources) != 0);
        Assert.IsAssignableFrom<IRoslynXamlCompiledResourceProfile>(profile);
        Assert.False(XamlFrameworkProfileRegistry.BuiltIn.TryCreate("missing", out _));
    }

    [Fact]
    public void IncrementalGeneratorUsesExplicitFrameworkPackageRegistry()
    {
        const string program = """
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml.Controls { public class Page { } }
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage" />
""";
        var registry = new XamlFrameworkProfileRegistry(
            new IXamlFrameworkProfileFactory[]
            {
                new AliasProfileFactory("Zeta"),
                new AliasProfileFactory("PackageProfile")
            });
        Assert.Equal(new[] { "PackageProfile", "Zeta" }, registry.ProfileIds);
        var compilation = CSharpCompilation.Create(
            "PackageProfileGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new GlobalAnalyzerConfigOptionsProvider(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build_property.ProGpuXamlFramework"] = "PackageProfile"
            });
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(
                new ProGpuXamlSourceGenerator(registry).AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("PackagePage.xaml", xaml)),
            CSharpParseOptions.Default,
            options);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);

        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Single(Assert.Single(driver.GetRunResult().Results).GeneratedSources);

        var missingOptions = new GlobalAnalyzerConfigOptionsProvider(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build_property.ProGpuXamlFramework"] = "Missing"
            });
        GeneratorDriver missingDriver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(
                new ProGpuXamlSourceGenerator(registry).AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("PackagePage.xaml", xaml)),
            CSharpParseOptions.Default,
            missingOptions);
        missingDriver = missingDriver.RunGenerators(compilation);
        var missingDiagnostic = Assert.Single(
            Assert.Single(missingDriver.GetRunResult().Results).Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML0002");
        Assert.Contains(
            "Installed profiles: PackageProfile, Zeta",
            missingDiagnostic.GetMessage());
    }

    [Fact]
    public void IncrementalGeneratorAppliesPackageProvidedBoundDocumentTransforms()
    {
        const string program = """
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml.Controls {
  public class Page { public string? Title { get; set; } }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage"
      Title="Before" />
""";
        var host = RoslynXamlExtensionHost.Create(
            new IRoslynXamlExtension[]
            {
                new GeneratorBoundTextTransform("Before", "After")
            });
        var registry = new XamlFrameworkProfileRegistry(
            new IXamlFrameworkProfileFactory[]
            {
                new AliasProfileFactory("PackageProfile", host)
            });
        var compilation = CSharpCompilation.Create(
            "PackageTransformGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new GlobalAnalyzerConfigOptionsProvider(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build_property.ProGpuXamlFramework"] = "PackageProfile"
            });
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            ImmutableArray.Create(
                new ProGpuXamlSourceGenerator(registry).AsSourceGenerator()),
            ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("PackagePage.xaml", xaml)),
            CSharpParseOptions.Default,
            options);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);

        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(
            Assert.Single(driver.GetRunResult().Results).GeneratedSources);
        var stringLiterals = generated.SyntaxTree.GetRoot()
            .DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.StringLiteralToken))
            .Select(token => token.ValueText)
            .ToArray();
        Assert.Contains("After", stringLiterals);
        Assert.DoesNotContain("Before", stringLiterals);

        Assert.True(registry.TryCreate("PackageProfile", out var directProfile));
        var directDocument = XamlParser.Parse(
            SourceText.From(xaml),
            "PackagePage.xaml").Document;
        var directResult = new CSharpXamlEmitter().Emit(
            directDocument,
            new RoslynXamlTypeSystem(compilation, directProfile!),
            directProfile!,
            new XamlCompilerOptions
            {
                Framework = directProfile!.Id,
                ResourceUri = "PackagePage.xaml",
                Strict = true,
                EmitHotReloadHooks = true,
                EmitSourceComments = true
            });
        var directSource = Assert.Single(directResult.Sources);
        Assert.Equal(directSource.Source, generated.SourceText.ToString());
        Assert.NotNull(generated.SourceText.Encoding);
        Assert.Empty(generated.SourceText.Encoding!.GetPreamble());
    }

    [Fact]
    public void IncrementalGeneratorEmitsCompilableStructuredCSharp()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } }
  public static class XamlTemplateFactory {
    public static void BeginNameScope(object root) { }
    public static void RegisterName(object root, string name, object value) { }
  }
}
namespace Microsoft.UI.Xaml.HotReload { public interface IHotReloadable { void Reload(HotReloadContext context); } public sealed class HotReloadContext { } }
namespace Microsoft.UI.Xaml { public class FrameworkElement { public string? Name { get; set; } public void RegisterName(string name, object value) { } } }
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")] public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Children")] public class StackPanel : Microsoft.UI.Xaml.FrameworkElement { public System.Collections.Generic.List<object> Children { get; } = new(); }
  public class TextBlock : Microsoft.UI.Xaml.FrameworkElement {
    public string? Text { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(
      System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? HiddenText { get; set; }
  }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        const string xaml = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"Demo.MainPage\"><StackPanel><TextBlock x:Name=\"Message\" Text=\"Hello\" HiddenText=\"Secret\"/></StackPanel></Page>";
        var compilation = CSharpCompilation.Create(
            "GeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[] { new InMemoryAdditionalText("MainPage.xaml", xaml) },
            parseOptions: CSharpParseOptions.Default);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var run = driver.GetRunResult();
        Assert.Single(run.Results[0].GeneratedSources);
        Assert.Contains("InitializeComponent", run.Results[0].GeneratedSources[0].SourceText.ToString());

        var profile = new WinUiXamlProfile();
        var document = XamlParser.Parse(SourceText.From(xaml), "MainPage.xaml").Document;
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var emitted = new CSharpXamlEmitter().Emit(
            document,
            typeSystem,
            profile,
            new XamlCompilerOptions());
        var generated = Assert.Single(emitted.Sources);
        Assert.NotNull(generated.UnformattedSyntaxTree);
        var projections = XamlProjectionMap.Read(generated.UnformattedSyntaxTree!);
        Assert.NotEmpty(projections);
        Assert.Contains(projections, projection => projection.StableNodeId != 0 && projection.MemberId != null);

        var method = generated.GeneratedSyntaxTree!.GetRoot()
            .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText == "InitializeComponent");
        var assignments = method.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>()
            .Select(assignment => assignment.Left.ToString())
            .ToArray();
        Assert.True(Array.IndexOf(assignments, "this.Content") < Array.IndexOf(assignments, "this.Message"));
        Assert.Contains(generated.GeneratedSyntaxTree.GetRoot()
            .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeSyntax>(),
            baseType => baseType.Type.ToString().Contains("IHotReloadable", StringComparison.Ordinal));

        var originalGeneratedTree = generated.UnformattedSyntaxTree!;
        var originalRoot = originalGeneratedTree.GetRoot();
        var helloLiteral = originalRoot.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax>()
            .Single(literal => literal.Token.ValueText == "Hello");
        var replacementLiteral = SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal("World & beyond"))
            .WithAdditionalAnnotations(helloLiteral.GetAnnotations(XamlProjectionMap.AnnotationKind));
        var changedRoot = originalRoot.ReplaceNode(helloLiteral, replacementLiteral);
        var changedGeneratedTree = CSharpSyntaxTree.Create(
            (Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode)changedRoot,
            CSharpParseOptions.Default,
            originalGeneratedTree.FilePath,
            Encoding.UTF8);
        var originalGeneratedCompilation = compilation.AddSyntaxTrees(originalGeneratedTree);
        var changedGeneratedCompilation = compilation.AddSyntaxTrees(changedGeneratedTree);
        var bound = new XamlSemanticBinder().Bind(
            new XamlInfosetConverter().Convert(
                document,
                new XamlInfosetConversionOptions
                {
                    Mode = XamlParseMode.Recovering
                }),
            typeSystem);
        var reverse = new XamlReverseProjectionService().ApplyLiteralEdits(
            bound,
            document.SyntaxTree,
            originalGeneratedCompilation.GetSemanticModel(originalGeneratedTree),
            changedGeneratedCompilation.GetSemanticModel(changedGeneratedTree));
        Assert.True(reverse.Succeeded, string.Join(Environment.NewLine, reverse.Conflicts.Select(conflict => conflict.Message)));
        Assert.Contains("Text=\"World &amp; beyond\"", reverse.GetChangedText().ToString(), StringComparison.Ordinal);

        var secretLiteral = originalRoot.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax>()
            .Single(literal => literal.Token.ValueText == "Secret");
        var changedSecretRoot = originalRoot.ReplaceNode(
            secretLiteral,
            SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal("Changed secret"))
                .WithAdditionalAnnotations(
                    secretLiteral.GetAnnotations(
                        XamlProjectionMap.AnnotationKind)));
        var changedSecretTree = CSharpSyntaxTree.Create(
            (Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode)changedSecretRoot,
            CSharpParseOptions.Default,
            originalGeneratedTree.FilePath,
            Encoding.UTF8);
        var rejectedByPolicy =
            new XamlReverseProjectionService().ApplyLiteralEdits(
                bound,
                document.SyntaxTree,
                originalGeneratedCompilation.GetSemanticModel(
                    originalGeneratedTree),
                compilation.AddSyntaxTrees(changedSecretTree)
                    .GetSemanticModel(changedSecretTree));
        Assert.False(rejectedByPolicy.Succeeded);
        Assert.Contains(
            rejectedByPolicy.Conflicts,
            conflict => conflict.Kind ==
                XamlReverseProjectionConflictKind
                    .SerializationPolicyChanged);
        Assert.Equal(
            document.SyntaxTree.GetText().ToString(),
            rejectedByPolicy.GetChangedText().ToString());

        var annotationDroppingRoot = originalRoot.ReplaceNode(
            helloLiteral,
            SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("Lost mapping")));
        var annotationDroppingTree = CSharpSyntaxTree.Create(
            (Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode)annotationDroppingRoot,
            CSharpParseOptions.Default,
            originalGeneratedTree.FilePath,
            Encoding.UTF8);
        var rejected = new XamlReverseProjectionService().ApplyLiteralEdits(
            document.SyntaxTree,
            originalGeneratedCompilation.GetSemanticModel(originalGeneratedTree),
            compilation.AddSyntaxTrees(annotationDroppingTree).GetSemanticModel(annotationDroppingTree));
        Assert.False(rejected.Succeeded);
        Assert.Contains(rejected.Conflicts, conflict => conflict.Kind == XamlReverseProjectionConflictKind.MissingProjection);
        Assert.Equal(document.SyntaxTree.GetText().ToString(), rejected.GetChangedText().ToString());
    }

    [Fact]
    public void IncrementalGeneratorEmitsCompilableWinUiBindingActivation()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class ContentPropertyAttribute : System.Attribute {
    public string? Name { get; set; }
  }
  public interface IXamlTemplateLifetime : System.IDisposable {
    void Initialize();
  }
  public static class XamlTemplateFactory {
    public static void BeginNameScope(Microsoft.UI.Xaml.FrameworkElement root) { }
    public static void RegisterName(Microsoft.UI.Xaml.FrameworkElement root, string name, object value) { }
    public static void AttachLifetime(
      Microsoft.UI.Xaml.FrameworkElement root,
      IXamlTemplateLifetime lifetime) { }
  }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  public sealed class DependencyProperty { }
  public class DependencyObject { public void SetValue(DependencyProperty property, object? value) { } }
  public class FrameworkElement : DependencyObject {
    public string? Name { get; set; }
    public void RegisterName(string name, object value) { }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public enum BindingMode { OneWay, OneTime, TwoWay }
  public enum UpdateSourceTrigger { Default, PropertyChanged, Explicit, LostFocus }
  public sealed class Binding {
    public string? Path { get; set; }
    public BindingMode Mode { get; set; }
    public string? ElementName { get; set; }
    public object? FallbackValue { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
  }
  public static class BindingOperations {
    private sealed class TestLifetime :
        Microsoft.UI.Xaml.Markup.IXamlTemplateLifetime {
      public void Initialize() { }
      public void Dispose() { }
    }
    public static Microsoft.UI.Xaml.Markup.IXamlTemplateLifetime BeginBindings() =>
      new TestLifetime();
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      string targetPropertyName,
      Binding binding,
      object? context = null,
      object? lookupRoot = null,
      Microsoft.UI.Xaml.Markup.IXamlTemplateLifetime? lifetime = null) =>
      new object();
  }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } }
  public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { public string? Text { get; set; } }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <TextBlock Text="{Binding Path=Customer.Name, Mode=TwoWay,
                            ElementName=SourceText, FallbackValue=missing}" />
</Page>
""";
        var compilation = CSharpCompilation.Create(
            "BindingGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText("MainPage.xaml", xaml)
            },
            parseOptions: CSharpParseOptions.Default);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);

        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(
            Assert.Single(driver.GetRunResult().Results).GeneratedSources);
        var root = generated.SyntaxTree.GetRoot();
        var invocation = Assert.Single(
            root.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            candidate => candidate.Expression.ToString().EndsWith(
                "BindingOperations.SetBinding",
                StringComparison.Ordinal));
        Assert.Equal(6, invocation.ArgumentList.Arguments.Count);
        Assert.Equal("this", invocation.ArgumentList.Arguments[4].Expression.ToString());
        Assert.Equal(
            "__xamlBindingLifetime",
            invocation.ArgumentList.Arguments[5].Expression.ToString());
    }

    [Fact]
    public void IncrementalGeneratorEmitsCompilableSymbolBoundXBindActivation()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class ContentPropertyAttribute : System.Attribute {
    public string? Name { get; set; }
  }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  public sealed class DependencyProperty { }
  public class DependencyObject { public void SetValue(DependencyProperty property, object? value) { } }
  public class FrameworkElement : DependencyObject {
    public string? Name { get; set; }
    public void RegisterName(string name, object value) { }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public enum BindingMode { OneWay, OneTime, TwoWay }
  public enum UpdateSourceTrigger { Default, PropertyChanged, Explicit, LostFocus }
  public interface ICompiledBindingPathSegment { }
  public interface ICompiledBindings { void Initialize(); void Update(); void StopTracking(); }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public sealed class CompiledBindingPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingPathSegment(
      string name,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter,
      Microsoft.UI.Xaml.DependencyProperty? property) { }
  }
  public sealed class CompiledBindingIndexerPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingIndexerPathSegment(
      int index,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter) { }
    public CompiledBindingIndexerPathSegment(
      string key,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter) { }
  }
  public sealed class CompiledBindingCastPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingCastPathSegment(
      string typeName,
      System.Func<TSource, TValue> getter) { }
  }
  public sealed class CompiledBindingFunctionPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingFunctionPathSegment(
      string methodName,
      System.Func<TSource, TValue> getter,
      ICompiledBindingPathSegment[] ownerPath,
      ICompiledBindingPathSegment[][] dependencies) { }
  }
  public sealed class CompiledBindingOptions {
    public BindingMode Mode { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public System.Action<object, object?>? BindBack { get; set; }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings(object source) => new TestCompiledBindings();
    public static void ClearBindingsForSource(object source) { }
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      Microsoft.UI.Xaml.DependencyProperty property,
      object source,
      System.Collections.Generic.IReadOnlyList<ICompiledBindingPathSegment> path,
      CompiledBindingOptions options) => new object();
  }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } }
  public class Grid : Microsoft.UI.Xaml.FrameworkElement {
    public static int GetRow(Microsoft.UI.Xaml.FrameworkElement element) => 0;
    public static void SetRow(Microsoft.UI.Xaml.FrameworkElement element, int value) { }
  }
}
namespace Demo {
  public partial class MainPage : Microsoft.UI.Xaml.Controls.Page {
    public ViewModel Model { get; } = new();
    public object Selected { get; } = new ViewModel();
    public Microsoft.UI.Xaml.FrameworkElement Element { get; } = new();
    public string Format(string value, int count) => value + count.ToString();
    private void HandleRaised() { }
  }
  public sealed class ViewModel {
    public System.Collections.Generic.List<string> Titles { get; } = new() { "Hello" };
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public static readonly Microsoft.UI.Xaml.DependencyProperty OtherProperty = new();
    public static readonly Microsoft.UI.Xaml.DependencyProperty RowProperty = new();
    public static readonly Microsoft.UI.Xaml.DependencyProperty SummaryProperty = new();
    public string? Value { get; set; }
    public string? Other { get; set; }
    public int Row { get; set; }
    public string? Summary { get; set; }
    public event System.EventHandler? Raised;
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:BindingTarget Value="{x:Bind Model.Titles[0], Mode=TwoWay}"
                       Other="{x:Bind ((local:ViewModel)Selected).Titles[0]}"
                       Row="{x:Bind Element.(Grid.Row), Mode=TwoWay}"
                       Summary="{x:Bind Format(Model.Titles[0], 2), Mode=OneWay}"
                       Raised="{x:Bind HandleRaised}" />
</Page>
""";
        var compilation = CSharpCompilation.Create(
            "XBindGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText("MainPage.xaml", xaml)
            },
            parseOptions: CSharpParseOptions.Default);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);

        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(
            Assert.Single(driver.GetRunResult().Results).GeneratedSources);
        var root = generated.SyntaxTree.GetRoot();
        var invocations = root.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(candidate => candidate.Expression.ToString().EndsWith(
                "CompiledBindingOperations.SetBinding",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, invocations.Length);
        var invocation = invocations[0];
        Assert.Equal("this", invocation.ArgumentList.Arguments[2].Expression.ToString());
        Assert.Contains(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(),
            creation => creation.Type.ToString().Contains(
                "CompiledBindingIndexerPathSegment",
                StringComparison.Ordinal));
        Assert.Contains(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(),
            creation => creation.Type.ToString().Contains(
                "CompiledBindingCastPathSegment",
                StringComparison.Ordinal));
        Assert.Contains(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(),
            creation => creation.Type.ToString().Contains(
                "CompiledBindingFunctionPathSegment",
                StringComparison.Ordinal));
        Assert.Contains(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            candidate => candidate.Expression.ToString().EndsWith(
                "Grid.GetRow",
                StringComparison.Ordinal));
        Assert.Contains(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            candidate => candidate.Expression.ToString().EndsWith(
                "Grid.SetRow",
                StringComparison.Ordinal));
        Assert.Contains(
            root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>(),
            assignment =>
                assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
                assignment.Right.ToString().Contains(
                    "this.HandleRaised()",
                    StringComparison.Ordinal));
        var bindingsProperty = Assert.Single(
            root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>(),
            property => property.Identifier.ValueText == "Bindings");
        Assert.Equal(
            "global::Microsoft.UI.Xaml.Data.ICompiledBindings",
            bindingsProperty.Type.ToString());
        Assert.Contains(
            bindingsProperty.Modifiers,
            modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
        var initializeComponent = Assert.Single(
            root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "InitializeComponent");
        var lifecycleStatements = initializeComponent.Body!.Statements
            .Select(static statement => statement.ToString())
            .ToArray();
        Assert.EndsWith(
            "ClearBindingsForSource(this);",
            lifecycleStatements[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "Bindings = global::Microsoft.UI.Xaml.Data.CompiledBindingOperations.BeginBindings(this);",
            lifecycleStatements[1],
            StringComparison.Ordinal);
        Assert.Equal("Bindings.Initialize();", lifecycleStatements[^1]);
    }

    [Fact]
    public void IncrementalGeneratorEmitsPerMaterializationTemplateBindingLifetime()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class ContentPropertyAttribute : System.Attribute {
    public string? Name { get; set; }
  }
  public interface IXamlTemplateLifetime : System.IDisposable {
    void Initialize();
  }
  public static class XamlTemplateFactory {
    public static void SetFactory(
      Microsoft.UI.Xaml.FrameworkTemplate template,
      System.Func<object?, Microsoft.UI.Xaml.FrameworkElement> factory) { }
    public static void BeginNameScope(Microsoft.UI.Xaml.FrameworkElement root) { }
    public static void RegisterName(Microsoft.UI.Xaml.FrameworkElement root, string name, object value) { }
    public static void AttachBindings(
      Microsoft.UI.Xaml.FrameworkElement root,
      Microsoft.UI.Xaml.Data.ICompiledBindings bindings) { }
    public static void AttachLifetime(
      Microsoft.UI.Xaml.FrameworkElement root,
      IXamlTemplateLifetime lifetime) { }
  }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  public sealed class DependencyProperty { }
  public class DependencyObject { }
  public class FrameworkElement : DependencyObject {
    public string? Name { get; set; }
    public void RegisterName(string name, object value) { }
  }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Template")]
  public class FrameworkTemplate : DependencyObject { }
  public class DataTemplate : FrameworkTemplate { }
}
namespace Microsoft.UI.Xaml.Data {
  public enum BindingMode { OneWay, OneTime, TwoWay }
  public enum UpdateSourceTrigger { Default, PropertyChanged, Explicit, LostFocus }
  public sealed class Binding {
    public string? Path { get; set; }
    public BindingMode Mode { get; set; }
  }
  public static class BindingMemberAccessorRegistry {
    public static void Register<TSource, TValue>(
      string memberName,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter = null) { }
    public static void RegisterIndexer<TSource, TValue>(
      int index,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter = null) { }
    public static void RegisterIndexer<TSource, TValue>(
      string key,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter = null) { }
  }
  public readonly struct BindingPathSegment {
    public static BindingPathSegment Member(string memberName) => default;
    public static BindingPathSegment Indexer(int index) => default;
    public static BindingPathSegment Indexer(string key) => default;
  }
  public sealed class TestBindingLifetime :
      Microsoft.UI.Xaml.Markup.IXamlTemplateLifetime {
    public void Initialize() { }
    public void Dispose() { }
  }
  public static class BindingOperations {
    public static Microsoft.UI.Xaml.Markup.IXamlTemplateLifetime BeginBindings() =>
      new TestBindingLifetime();
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      string property,
      Binding binding,
      object? context,
      object? root,
      Microsoft.UI.Xaml.Markup.IXamlTemplateLifetime lifetime) =>
      new object();
    public static object SetBindingWithPath(
      Microsoft.UI.Xaml.DependencyObject target,
      string property,
      Binding binding,
      System.Collections.Generic.IReadOnlyList<BindingPathSegment> path,
      object? context,
      object? root,
      Microsoft.UI.Xaml.Markup.IXamlTemplateLifetime lifetime) =>
      new object();
  }
  public interface ICompiledBindingPathSegment { }
  public interface ICompiledBindings {
    void Initialize();
    void Update();
    void StopTracking();
  }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public sealed class CompiledBindingPathSegment<TSource, TValue> :
      ICompiledBindingPathSegment {
    public CompiledBindingPathSegment(
      string name,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter,
      Microsoft.UI.Xaml.DependencyProperty? property) { }
  }
  public sealed class CompiledBindingOptions {
    public BindingMode Mode { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public System.Action<object, object?>? BindBack { get; set; }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings() =>
      new TestCompiledBindings();
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      Microsoft.UI.Xaml.DependencyProperty property,
      object? source,
      System.Collections.Generic.IReadOnlyList<ICompiledBindingPathSegment> path,
      CompiledBindingOptions options,
      ICompiledBindings bindings) => new object();
  }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement {
    public object? Content { get; set; }
  }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Children")]
  public class StackPanel : Microsoft.UI.Xaml.FrameworkElement {
    public System.Collections.Generic.List<object> Children { get; } = new();
  }
}
namespace Demo {
  public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { }
  public sealed class Item {
    public string Title { get; set; } = "Item";
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.FrameworkElement {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <DataTemplate x:DataType="local:Item">
    <StackPanel>
      <local:BindingTarget Value="{Binding Path=Title, Mode=OneWay}" />
      <local:BindingTarget Value="{x:Bind Title, Mode=OneWay}" />
    </StackPanel>
  </DataTemplate>
</Page>
""";
        var compilation = CSharpCompilation.Create(
            "TemplateBindingGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[]
            {
                new ProGpuXamlSourceGenerator().AsSourceGenerator()
            },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText("MainPage.xaml", xaml)
            },
            parseOptions: CSharpParseOptions.Default);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);

        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(
            Assert.Single(driver.GetRunResult().Results).GeneratedSources);
        var root = generated.SyntaxTree.GetRoot();
        var factory = Assert.Single(
            root.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.ParenthesizedLambdaExpressionSyntax>(),
            lambda => lambda.ParameterList.Parameters.Count == 1 &&
                lambda.ParameterList.Parameters[0].Identifier.ValueText ==
                    "__templateContext");
        var begin = Assert.Single(
            factory.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "CompiledBindingOperations.BeginBindings",
                StringComparison.Ordinal));
        var setBinding = Assert.Single(
            factory.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "CompiledBindingOperations.SetBinding",
                StringComparison.Ordinal));
        var attach = Assert.Single(
            factory.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "XamlTemplateFactory.AttachBindings",
                StringComparison.Ordinal));
        Assert.Empty(begin.ArgumentList.Arguments);
        Assert.Equal(6, setBinding.ArgumentList.Arguments.Count);
        Assert.Equal(
            "__templateBindings",
            setBinding.ArgumentList.Arguments[5].Expression.ToString());
        Assert.Equal(
            "__templateBindings",
            attach.ArgumentList.Arguments[1].Expression.ToString());
        var ordinaryBegin = Assert.Single(
            factory.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => string.Equals(
                invocation.Expression.ToString(),
                "global::Microsoft.UI.Xaml.Data.BindingOperations.BeginBindings",
                StringComparison.Ordinal));
        var ordinarySet = Assert.Single(
            factory.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => string.Equals(
                invocation.Expression.ToString(),
                "global::Microsoft.UI.Xaml.Data.BindingOperations.SetBindingWithPath",
                StringComparison.Ordinal));
        var ordinaryAttach = Assert.Single(
            factory.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "XamlTemplateFactory.AttachLifetime",
                StringComparison.Ordinal));
        Assert.Equal(7, ordinarySet.ArgumentList.Arguments.Count);
        Assert.Equal(
            "__templateLifetime",
            ordinarySet.ArgumentList.Arguments[6].Expression.ToString());
        Assert.Equal(
            "__templateLifetime",
            ordinaryAttach.ArgumentList.Arguments[1].Expression.ToString());
        var registration = Assert.Single(
            root.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().StartsWith(
                "global::Microsoft.UI.Xaml.Data.BindingMemberAccessorRegistry.Register",
                StringComparison.Ordinal));
        Assert.Contains(
            "Register<global::Demo.Item, string>",
            registration.Expression.ToString(),
            StringComparison.Ordinal);
        Assert.Equal("\"Title\"", registration.ArgumentList.Arguments[0].ToString());
        var registrationMethod = Assert.Single(
            root.DescendantNodes().OfType<
                Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText.StartsWith(
                "__RegisterXamlBindingAccessors_",
                StringComparison.Ordinal));
        Assert.Contains(
            registrationMethod.AttributeLists.SelectMany(list => list.Attributes),
            attribute => attribute.Name.ToString().EndsWith(
                "ModuleInitializer",
                StringComparison.Ordinal));
        Assert.True(ordinaryBegin.SpanStart < ordinarySet.SpanStart);
        Assert.True(ordinarySet.SpanStart < ordinaryAttach.SpanStart);
        Assert.True(begin.SpanStart < setBinding.SpanStart);
        Assert.True(setBinding.SpanStart < attach.SpanStart);
    }

    [Fact]
    public void IncrementalGeneratorConsumesAttributedCreateFromStringFactory()
    {
        const string program = """
namespace Windows.Foundation.Metadata {
  [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, Inherited=false)]
  public sealed class CreateFromStringAttribute : System.Attribute {
    public string? MethodName;
  }
}
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class ContentPropertyAttribute : System.Attribute {
    public string? Name { get; set; }
  }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  public class FrameworkElement { public string? Name { get; set; } }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Content))]
  public class Page : Microsoft.UI.Xaml.FrameworkElement {
    public object? Content { get; set; }
  }
}
namespace Demo {
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public readonly struct FactoryValue {
    public FactoryValue(int value) { Value = value; }
    public int Value { get; }
    public static FactoryValue Parse(string value) =>
      new(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
  }
  public sealed class Host : Microsoft.UI.Xaml.FrameworkElement {
    public FactoryValue Value { get; set; }
  }
  public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:Host Value="42" />
</Page>
""";
        var compilation = CSharpCompilation.Create(
            "CreateFromStringGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[]
            {
                new ProGpuXamlSourceGenerator().AsSourceGenerator()
            },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText("MainPage.xaml", xaml)
            },
            parseOptions: CSharpParseOptions.Default);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);
        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity ==
                DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity ==
                DiagnosticSeverity.Error);
        var generatedTree = Assert.Single(
            output.SyntaxTrees,
            tree => tree.FilePath.EndsWith(
                ".g.cs",
                StringComparison.Ordinal));
        Assert.Contains(
            generatedTree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax
                    .InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "FactoryValue.Parse",
                StringComparison.Ordinal));
    }

    [Fact]
    public void IncrementalGeneratorConsumesTemplateToolingAnnotationsAndReportsInvalidShapes()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class ContentPropertyAttribute : System.Attribute {
    public string? Name { get; set; }
  }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class TemplatePartAttribute : System.Attribute {
    public string? Name;
    public System.Type? Type;
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class TemplateVisualStateAttribute : System.Attribute {
    public string? Name;
    public string? GroupName;
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class StyleTypedPropertyAttribute : System.Attribute {
    public string? Property;
    public System.Type? StyleTargetType;
  }
  public class FrameworkElement {
    public string? Name { get; set; }
    public void RegisterName(string name, object value) { }
  }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement {
    public object? Content { get; set; }
  }
  public class Button : Microsoft.UI.Xaml.FrameworkElement { }
}
namespace Demo {
  [Microsoft.UI.Xaml.TemplatePart(
    Name="PART_Content", Type=typeof(Microsoft.UI.Xaml.Controls.Button))]
  [Microsoft.UI.Xaml.TemplateVisualState(Name="Normal", GroupName="CommonStates")]
  [Microsoft.UI.Xaml.StyleTypedProperty(
    Property=nameof(ItemStyle),
    StyleTargetType=typeof(Microsoft.UI.Xaml.Controls.Button))]
  public sealed class ValidControl : Microsoft.UI.Xaml.FrameworkElement {
    public object? ItemStyle { get; set; }
  }
  [Microsoft.UI.Xaml.TemplatePart(Name="PART_Broken")]
  [Microsoft.UI.Xaml.TemplateVisualState(Name="Broken")]
  [Microsoft.UI.Xaml.StyleTypedProperty(
    Property="Missing",
    StyleTargetType=typeof(Microsoft.UI.Xaml.Controls.Button))]
  public sealed class InvalidControl : Microsoft.UI.Xaml.FrameworkElement { }
  public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { }
}
""";
        var compilation = CSharpCompilation.Create(
            "TemplateToolingGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver validDriver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText(
                    "MainPage.xaml",
                    """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"><local:ValidControl /></Page>
""")
            },
            parseOptions: CSharpParseOptions.Default);
        validDriver = validDriver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var validOutput,
            out var validDiagnostics);
        Assert.DoesNotContain(
            validDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            validOutput.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        GeneratorDriver invalidDriver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText(
                    "Invalid.xaml",
                    """
<local:InvalidControl xmlns:local="using:Demo" />
""")
            },
            parseOptions: CSharpParseOptions.Default);
        invalidDriver = invalidDriver.RunGenerators(compilation);
        var diagnostics = invalidDriver.GetRunResult().Diagnostics;
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PGXAML2068");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PGXAML2069");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PGXAML2070");
    }

    [Fact]
    public void IncrementalGeneratorAppliesResolvedMemberBracketAnnotations()
    {
        const string program = """
namespace System.Windows.Markup {
  [System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple=true)]
  public sealed class MarkupExtensionBracketCharactersAttribute : System.Attribute {
    public MarkupExtensionBracketCharactersAttribute(char openingBracket, char closingBracket) { }
  }
}
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } }
  public abstract class MarkupExtension { protected abstract object ProvideValue(); }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  public class FrameworkElement { public void RegisterName(string name, object value) { } }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } }
}
namespace ProGPU.Xaml.Runtime {
  public static class WinUiMarkupExtensionRuntime {
    public static T Evaluate<T>(
      Microsoft.UI.Xaml.Markup.MarkupExtension extension,
      object? targetObject,
      System.Type? targetType,
      string? targetMember,
      object rootObject,
      string? resourceUri) => default!;
  }
}
namespace Demo {
  public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { }
  public sealed class BracketExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    [System.Windows.Markup.MarkupExtensionBracketCharacters('[', ']')]
    [System.Windows.Markup.MarkupExtensionBracketCharacters('(', ')')]
    public string? Path { get; set; }
    public string? Mode { get; set; }
    protected override object ProvideValue() => Path ?? string.Empty;
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"
      Content="{local:Bracket Path=Items[(key,value)=selected], Mode=OneWay}" />
""";
        var compilation = CSharpCompilation.Create(
            "BracketGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[] { new InMemoryAdditionalText("MainPage.xaml", xaml) },
            parseOptions: CSharpParseOptions.Default);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);

        Assert.DoesNotContain(
            generatorDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(
            Assert.Single(driver.GetRunResult().Results).GeneratedSources);
        var pathLiteral = Assert.Single(
            generated.SyntaxTree.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax>(),
            literal => literal.Token.ValueText == "Items[(key,value)=selected]");
        Assert.Equal(
            SyntaxKind.StringLiteralExpression,
            pathLiteral.Kind());
    }

    [Fact]
    public void ThemeResourceUsesValidatedPropertySystemShapeForTypedClrProperty()
    {
        const string program = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  public sealed class DependencyProperty { }
  public class DependencyObject { public void SetValue(DependencyProperty property, object? value) { } }
  public class FrameworkElement : DependencyObject { }
  public sealed class ThemeResource { public ThemeResource(object? root, string key) { } }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } }
  public class TextBlock : Microsoft.UI.Xaml.FrameworkElement {
    public static readonly Microsoft.UI.Xaml.DependencyProperty TextProperty = new();
    public string? Text { get; set; }
  }
}
namespace Demo { public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { } }
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <TextBlock Text="{ThemeResource LabelText}" />
</Page>
""";
        var compilation = CSharpCompilation.Create(
            "PropertySystemResourceHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var textBlock = Assert.IsType<XamlTypeInfo>(
            typeSystem.ResolveType(XamlNamespaces.Presentation2006, "TextBlock"));
        var textMember = Assert.IsType<XamlMemberInfo>(
            typeSystem.ResolveMember(textBlock, XamlNamespaces.Presentation2006, null, "Text"));
        Assert.Equal("TextProperty", textMember.PropertySystemShape?.Identifier.Name);
        Assert.Equal("SetValue", textMember.PropertySystemShape?.Setter.Name);
        Assert.Equal("ProGPU.WinUI", textMember.PropertySystemShape?.ProviderId);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ProGpuXamlSourceGenerator().AsSourceGenerator() },
            additionalTexts: new AdditionalText[] { new InMemoryAdditionalText("MainPage.xaml", xaml) },
            parseOptions: CSharpParseOptions.Default);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var generatorDiagnostics);

        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(Assert.Single(driver.GetRunResult().Results).GeneratedSources);
        var root = generated.SyntaxTree.GetRoot();
        var invocation = Assert.Single(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(), candidate =>
                candidate.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax access &&
                access.Name.Identifier.ValueText == "SetValue");
        Assert.Equal(2, invocation.ArgumentList.Arguments.Count);
        Assert.Equal("TextProperty", ((Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax)
            invocation.ArgumentList.Arguments[0].Expression).Name.Identifier.ValueText);
        var marker = Assert.IsType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(
            invocation.ArgumentList.Arguments[1].Expression);
        Assert.EndsWith("ThemeResource", marker.Type.ToString(), StringComparison.Ordinal);
        Assert.Equal("this", marker.ArgumentList!.Arguments[0].Expression.ToString());
        Assert.DoesNotContain(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>(), assignment =>
                assignment.Left.ToString().EndsWith(".Text", StringComparison.Ordinal));
    }

    [Fact]
    public void AttributeAndShapeRulesProduceProvenanceBearingSchema()
    {
        const string program = """
namespace Meta {
  [System.AttributeUsage(System.AttributeTargets.Property)] public sealed class ContentAttribute : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)] public sealed class RuntimeNameAttribute(string name) : System.Attribute { public string Name { get; } = name; }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)] public sealed class DictionaryKeyAttribute(string name) : System.Attribute { public string Name { get; } = name; }
}
namespace Model {
  [Meta.RuntimeName("Id")] public class Widget { public string? Id { get; set; } [Meta.Content] public Bag Items { get; } = new(); }
  public sealed class Bag { public void Add(Item item) { } }
  [Meta.DictionaryKey("Key")] public sealed class Item { public string? Key { get; set; } }
  public sealed class FancyExtension { }
  public static class Layout { public static int GetSlot(Widget target) => 0; public static void SetSlot(Widget target, int value) { } }
  public static class BrokenLayout { public static void SetSlot(Widget target, int value) { } }
}
""";
        var compilation = CSharpCompilation.Create(
            "SchemaHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile, new TestMetadataProvider());

        var resolvedWidget = typeSystem.ResolveType("using:Model", "Widget");
        Assert.NotNull(resolvedWidget);
        var widget = resolvedWidget!;
        Assert.Equal("Items", widget.ContentMemberName);
        Assert.Equal("Id", widget.RuntimeNameMemberName);
        Assert.Contains(widget.Annotations, annotation =>
            annotation.Semantic == XamlSchemaSemantics.RuntimeNameProperty &&
            annotation.ProviderId == "tests.metadata");

        var resolvedItems = typeSystem.ResolveMember(widget, "using:Model", null, "Items");
        Assert.NotNull(resolvedItems);
        var items = resolvedItems!;
        Assert.Equal(XamlMemberKind.Collection, items.Kind);
        Assert.Equal("Add", items.ValueType.CollectionShape?.AddMethod.Name);
        Assert.Contains(items.Annotations, annotation => annotation.Semantic == XamlSchemaSemantics.ContentProperty);

        var resolvedItem = typeSystem.ResolveType("using:Model", "Item");
        Assert.NotNull(resolvedItem);
        var item = resolvedItem!;
        Assert.Equal("Key", item.DictionaryKeyMemberName);
        Assert.Equal("Model.FancyExtension", typeSystem.ResolveType("using:Model", "Fancy")?.MetadataName);
        var resolvedAttached = typeSystem.ResolveMember(widget, "using:Model", "Layout", "Slot");
        Assert.NotNull(resolvedAttached);
        var attached = resolvedAttached!;
        Assert.Equal(XamlMemberKind.AttachableProperty, attached.Kind);
        Assert.Equal("GetSlot", attached.AttachableShape?.Getter.Name);
        Assert.Equal("tests.metadata", attached.AttachableShape?.ProviderId);
        Assert.Null(typeSystem.ResolveMember(widget, "using:Model", "BrokenLayout", "Slot"));
    }

    [Fact]
    public void AssemblyNamespaceMetadataResolvesTypesAndCompatibilityAliases()
    {
        const string program = """
[assembly: Meta.XmlnsDefinition("urn:widgets", "Model")]
[assembly: Meta.XmlnsPrefix("urn:widgets", "widgets")]
[assembly: Meta.XmlnsCompatibleWith("urn:legacy-widgets", "urn:widgets")]
[assembly: Meta.ResourceId("Model.Widget.xaml", "Views/Widget.xaml", typeof(Model.Widget))]
namespace Meta {
  [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class XmlnsDefinitionAttribute(string xml, string clr) : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class XmlnsPrefixAttribute(string xml, string prefix) : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class XmlnsCompatibleWithAttribute(string oldXml, string newXml) : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)] public sealed class ResourceIdAttribute(string id, string path, System.Type type) : System.Attribute { }
}
namespace Model { public sealed class Widget { } }
""";
        var compilation = CSharpCompilation.Create(
            "NamespaceMetadataHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            new WinUiXamlProfile(),
            new NamespaceMetadataProvider());

        Assert.Equal("Model.Widget", typeSystem.ResolveType("urn:widgets", "Widget")?.MetadataName);
        Assert.Equal("Model.Widget", typeSystem.ResolveType("urn:legacy-widgets", "Widget")?.MetadataName);
        var definition = Assert.Single(typeSystem.NamespaceDefinitions, item => item.XmlNamespace == "urn:widgets");
        Assert.Equal("Model", definition.ClrNamespace);
        Assert.Equal("tests.namespaces", definition.ProviderId);
        Assert.Equal("widgets", Assert.Single(typeSystem.NamespacePrefixes).Prefix);
        Assert.Equal("urn:widgets", Assert.Single(typeSystem.NamespaceCompatibilities).NewNamespace);
        var resourceId = Assert.Single(typeSystem.AssemblyAnnotations,
            annotation => annotation.Semantic == XamlSchemaSemantics.XamlResourceId);
        Assert.Equal("Model.Widget.xaml", resourceId.Value);
        Assert.Equal(3, resourceId.Attribute.ConstructorArguments.Length);
    }

    [Fact]
    public void FrameworkAttributeCatalogIncludesCurrentCompilerRelevantInventories()
    {
        Assert.Contains(XamlSchemaAttributeCatalog.WinUi,
            rule => rule.Semantic == XamlSchemaSemantics.FullXamlMetadataProvider &&
                    rule.Targets == XamlSchemaAttributeTargets.Type);
        Assert.Contains(XamlSchemaAttributeCatalog.WinUi,
            rule => rule.Semantic == XamlSchemaSemantics.Bindable);
        Assert.Contains(XamlSchemaAttributeCatalog.Wpf,
            rule => rule.Semantic == XamlSchemaSemantics.DeferredLoad);
        Assert.Contains(XamlSchemaAttributeCatalog.Common,
            rule => rule.Semantic == XamlSchemaSemantics.MarkupExtensionBracketCharacters);
        Assert.Contains(XamlSchemaAttributeCatalog.Wpf,
            rule => rule.Semantic == XamlSchemaSemantics.RootNamespace);
        Assert.Contains(XamlSchemaAttributeCatalog.Maui,
            rule => rule.Semantic == XamlSchemaSemantics.XamlFilePath);
        Assert.Contains(XamlSchemaAttributeCatalog.Maui,
            rule => rule.Semantic == XamlSchemaSemantics.XamlCompilation &&
                    (rule.Targets & XamlSchemaAttributeTargets.Module) != 0);
        Assert.Contains(XamlSchemaAttributeCatalog.Maui,
            rule => rule.Semantic == XamlSchemaSemantics.XamlResourceId && rule.AllowMultiple);
        Assert.Contains(XamlSchemaAttributeCatalog.Common,
            rule => rule.Semantic == XamlSchemaSemantics.DefaultValue);
        Assert.Contains(XamlSchemaAttributeCatalog.Common,
            rule => rule.Semantic == XamlSchemaSemantics.DesignerSerializationVisibility);
        Assert.Contains(XamlSchemaAttributeCatalog.Common,
            rule => rule.Semantic == XamlSchemaSemantics.Browsable);
        Assert.Contains(XamlSchemaAttributeCatalog.Common,
            rule => rule.Semantic == XamlSchemaSemantics.EditorBrowsable);
        Assert.Contains(XamlSchemaAttributeCatalog.Common,
            rule => rule.Semantic == XamlSchemaSemantics.DesignTimeVisible);
        Assert.Contains(XamlSchemaAttributeCatalog.Wpf,
            rule => rule.Semantic == XamlSchemaSemantics.DesignerSerializationOptions);
        Assert.Contains(XamlSchemaAttributeCatalog.Wpf,
            rule => rule.Semantic == XamlSchemaSemantics.Localizability);
        Assert.True(
            (new WinUiXamlProfile().SymbolShapePolicy.DeclaredFeatures &
             XamlSymbolShapeFeatures.DesignerSerializationMethods) != 0);
    }

    [Fact]
    public void BuildMetadataAnnotationsRetainExactSymbolsAndApplyScopePrecedence()
    {
        const string program = """
[assembly: System.Windows.Markup.RootNamespace("Demo.Root")]
[assembly: Microsoft.Maui.Controls.Xaml.XamlCompilation(
    Microsoft.Maui.Controls.Xaml.XamlCompilationOptions.Skip)]
[module: Microsoft.Maui.Controls.Xaml.XamlCompilation(
    Microsoft.Maui.Controls.Xaml.XamlCompilationOptions.Compile)]
[assembly: Microsoft.Maui.Controls.Xaml.XamlResourceId(
    "Demo.Root.MainPage.xaml",
    "Views/MainPage.xaml",
    typeof(Demo.Root.MainPage))]
namespace System.Windows.Markup {
  [System.AttributeUsage(System.AttributeTargets.Assembly)]
  public sealed class RootNamespaceAttribute(string value) : System.Attribute {
    public string Namespace { get; } = value;
  }
}
namespace Microsoft.Maui.Controls.Xaml {
  [System.Flags]
  public enum XamlCompilationOptions { Skip = 1, Compile = 2 }
  [System.AttributeUsage(
    System.AttributeTargets.Assembly |
    System.AttributeTargets.Module |
    System.AttributeTargets.Class,
    Inherited=false)]
  public sealed class XamlCompilationAttribute(
    XamlCompilationOptions value) : System.Attribute {
    public XamlCompilationOptions XamlCompilationOptions { get; set; } = value;
  }
  [System.AttributeUsage(
    System.AttributeTargets.Class,
    AllowMultiple=false,
    Inherited=false)]
  public sealed class XamlFilePathAttribute(string path) : System.Attribute {
    public string FilePath { get; } = path;
  }
  [System.AttributeUsage(
    System.AttributeTargets.Assembly,
    AllowMultiple=true,
    Inherited=false)]
  public sealed class XamlResourceIdAttribute(
    string resourceId,
    string path,
    System.Type type) : System.Attribute {
    public string ResourceId { get; set; } = resourceId;
    public string Path { get; set; } = path;
    public System.Type Type { get; set; } = type;
  }
}
namespace Microsoft.UI.Xaml.Data {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class BindableAttribute : System.Attribute { }
}
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class FullXamlMetadataProviderAttribute : System.Attribute { }
}
namespace Demo.Root {
  [Microsoft.Maui.Controls.Xaml.XamlCompilation(
    Microsoft.Maui.Controls.Xaml.XamlCompilationOptions.Skip)]
  [Microsoft.Maui.Controls.Xaml.XamlFilePath("Views/MainPage.xaml")]
  public partial class MainPage { }
  [Microsoft.UI.Xaml.Data.Bindable]
  public sealed class ViewModel { }
  [Microsoft.UI.Xaml.Markup.FullXamlMetadataProvider]
  public sealed class MetadataProvider { }
}
""";
        var compilation = CSharpCompilation.Create(
            "BuildMetadataHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            new WinUiXamlProfile());

        var metadata = typeSystem.ResolveDocumentBuildMetadata(
            "/project/Views/MainPage.xaml",
            "MainPage");
        Assert.Equal("Demo.Root.MainPage", metadata.EffectiveClassName);
        Assert.Equal(
            "Demo.Root.MainPage",
            metadata.ClassType?.ToDisplayString());
        Assert.Equal(XamlCompilationMode.Skip, metadata.CompilationMode?.Mode);
        Assert.Equal(XamlCompilationScope.Type, metadata.CompilationMode?.Scope);
        Assert.IsAssignableFrom<INamedTypeSymbol>(
            metadata.CompilationMode?.DeclaredOn);
        Assert.Equal("Views/MainPage.xaml", metadata.FilePath?.FilePath);
        Assert.Equal(
            metadata.ClassType,
            metadata.FilePath?.AssociatedType,
            SymbolEqualityComparer.Default);
        Assert.Equal(
            "Demo.Root.MainPage.xaml",
            metadata.ResourceIdentity?.ResourceId);
        Assert.Equal(
            metadata.ClassType,
            metadata.ResourceIdentity?.AssociatedType,
            SymbolEqualityComparer.Default);
        Assert.Equal("Demo.Root", metadata.RootNamespace?.Namespace);
        Assert.IsAssignableFrom<IAssemblySymbol>(
            metadata.RootNamespace?.Annotation.DeclaredOn);
        Assert.Single(typeSystem.ModuleCompilationModes);
        Assert.IsAssignableFrom<IModuleSymbol>(
            typeSystem.ModuleCompilationModes[0].DeclaredOn);

        var viewModel = typeSystem.ResolveType("using:Demo.Root", "ViewModel");
        Assert.True(viewModel?.IsBindable);
        Assert.Equal(
            "Demo.Root.ViewModel",
            viewModel?.Bindable?.Type?.ToDisplayString());
        var provider = typeSystem.ResolveType(
            "using:Demo.Root",
            "MetadataProvider");
        Assert.True(provider?.IsFullMetadataProvider);
        Assert.Equal(
            "Demo.Root.MetadataProvider",
            provider?.FullMetadataProvider?.Type?.ToDisplayString());

        using var image = new MemoryStream();
        var emitResult = compilation.Emit(image);
        Assert.True(
            emitResult.Success,
            string.Join(Environment.NewLine, emitResult.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "BuildMetadataReferenceHarness",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        var referenced = new RoslynXamlTypeSystem(
            metadataCompilation,
            new WinUiXamlProfile());
        var referencedMetadata = referenced.ResolveDocumentBuildMetadata(
            "/project/Views/MainPage.xaml",
            "Demo.Root.MainPage");
        Assert.Equal(
            XamlCompilationScope.Type,
            referencedMetadata.CompilationMode?.Scope);
        Assert.Equal(
            "Views/MainPage.xaml",
            referencedMetadata.FilePath?.FilePath);
        Assert.Equal(
            "Demo.Root.MainPage.xaml",
            referencedMetadata.ResourceIdentity?.ResourceId);
    }

    [Fact]
    public void GeneratorHonorsBuildMetadataAndReportsMalformedEvidence()
    {
        const string framework = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class ContentPropertyAttribute : System.Attribute {
    public string? Name { get; set; }
  }
}
namespace Microsoft.UI.Xaml.HotReload {
  public interface IHotReloadable { void Reload(HotReloadContext context); }
  public sealed class HotReloadContext { }
}
namespace Microsoft.UI.Xaml {
  public class FrameworkElement {
    public void RegisterName(string name, object value) { }
  }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement {
    public object? Content { get; set; }
  }
}
namespace System.Windows.Markup {
  [System.AttributeUsage(System.AttributeTargets.Assembly)]
  public sealed class RootNamespaceAttribute(string value) : System.Attribute {
    public string Namespace { get; } = value;
  }
}
namespace Microsoft.Maui.Controls.Xaml {
  [System.Flags]
  public enum XamlCompilationOptions {
    Skip = 1,
    Compile = 2,
    Both = 3
  }
  [System.AttributeUsage(
    System.AttributeTargets.Assembly |
    System.AttributeTargets.Module |
    System.AttributeTargets.Class,
    Inherited=false)]
  public sealed class XamlCompilationAttribute(
    XamlCompilationOptions value) : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=false)]
  public sealed class XamlFilePathAttribute(string path) : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=true)]
  public sealed class XamlResourceIdAttribute(
    string resourceId,
    string path,
    System.Type type) : System.Attribute { }
}
""";
        const string validProgram = """
[assembly: System.Windows.Markup.RootNamespace("Demo.Root")]
[assembly: Microsoft.Maui.Controls.Xaml.XamlCompilation(
  Microsoft.Maui.Controls.Xaml.XamlCompilationOptions.Compile)]
namespace Demo.Root {
  [Microsoft.Maui.Controls.Xaml.XamlCompilation(
    Microsoft.Maui.Controls.Xaml.XamlCompilationOptions.Skip)]
  public partial class SkippedPage : Microsoft.UI.Xaml.Controls.Page { }
  public partial class GeneratedPage : Microsoft.UI.Xaml.Controls.Page { }
}
""";
        var compilation = CSharpCompilation.Create(
            "BuildMetadataGeneratorHarness",
            new[]
            {
                CSharpSyntaxTree.ParseText(framework),
                CSharpSyntaxTree.ParseText(validProgram)
            },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var skipped = new InMemoryAdditionalText(
            "/project/SkippedPage.xaml",
            """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="SkippedPage" />
""");
        var generated = new InMemoryAdditionalText(
            "/project/GeneratedPage.xaml",
            """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="GeneratedPage" />
""");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[]
            {
                new ProGpuXamlSourceGenerator().AsSourceGenerator()
            },
            additionalTexts: new AdditionalText[] { skipped, generated },
            parseOptions: CSharpParseOptions.Default,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var output,
            out var diagnostics);
        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            output.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var run = Assert.Single(driver.GetRunResult().Results);
        var source = Assert.Single(run.GeneratedSources);
        Assert.Contains(
            "namespace Demo.Root",
            source.SourceText.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "partial class GeneratedPage",
            source.SourceText.ToString(),
            StringComparison.Ordinal);

        var changedProgram = validProgram.Replace(
            "  public partial class GeneratedPage",
            """
  [Microsoft.Maui.Controls.Xaml.XamlCompilation(
    Microsoft.Maui.Controls.Xaml.XamlCompilationOptions.Skip)]
  public partial class GeneratedPage
""",
            StringComparison.Ordinal);
        var changedCompilation = CSharpCompilation.Create(
            "BuildMetadataGeneratorChangedHarness",
            new[]
            {
                CSharpSyntaxTree.ParseText(framework),
                CSharpSyntaxTree.ParseText(changedProgram)
            },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        driver = driver.RunGenerators(changedCompilation);
        var changedRun = Assert.Single(driver.GetRunResult().Results);
        Assert.Empty(changedRun.GeneratedSources);
        Assert.Contains(
            changedRun.TrackedSteps["XamlBindLowerEmit"]
                .SelectMany(static step => step.Outputs),
            output => output.Reason is IncrementalStepRunReason.Modified or
                IncrementalStepRunReason.New);

        const string invalidProgram = """
[assembly: System.Windows.Markup.RootNamespace("")]
[assembly: Microsoft.Maui.Controls.Xaml.XamlResourceId(
  "",
  "",
  typeof(Demo.BrokenPage))]
namespace Demo {
  [Microsoft.Maui.Controls.Xaml.XamlCompilation(
    Microsoft.Maui.Controls.Xaml.XamlCompilationOptions.Both)]
  [Microsoft.Maui.Controls.Xaml.XamlFilePath("")]
  public partial class BrokenPage : Microsoft.UI.Xaml.Controls.Page { }
}
""";
        var invalidCompilation = CSharpCompilation.Create(
            "InvalidBuildMetadataGeneratorHarness",
            new[]
            {
                CSharpSyntaxTree.ParseText(framework),
                CSharpSyntaxTree.ParseText(invalidProgram)
            },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver invalidDriver = CSharpGeneratorDriver.Create(
            generators: new[]
            {
                new ProGpuXamlSourceGenerator().AsSourceGenerator()
            },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText(
                    "/project/BrokenPage.xaml",
                    """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.BrokenPage" />
""")
            },
            parseOptions: CSharpParseOptions.Default);
        invalidDriver = invalidDriver.RunGenerators(invalidCompilation);
        var invalidDiagnostics = invalidDriver.GetRunResult().Diagnostics;
        Assert.Contains(
            invalidDiagnostics,
            diagnostic => diagnostic.Id == "PGXAML2072");
        Assert.Contains(
            invalidDiagnostics,
            diagnostic => diagnostic.Id == "PGXAML2073");
        Assert.Contains(
            invalidDiagnostics,
            diagnostic => diagnostic.Id == "PGXAML2074");
        Assert.Contains(
            invalidDiagnostics,
            diagnostic => diagnostic.Id == "PGXAML2075");

        const string invalidMarkerProgram = """
namespace Microsoft.UI.Xaml.Data {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class BindableAttribute(string reason) : System.Attribute { }
}
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class FullXamlMetadataProviderAttribute(
    string reason) : System.Attribute { }
}
namespace Demo {
  [Microsoft.UI.Xaml.Data.Bindable("invalid")]
  [Microsoft.UI.Xaml.Markup.FullXamlMetadataProvider("invalid")]
  public sealed class BrokenMetadataControl :
    Microsoft.UI.Xaml.Controls.Page { }
}
""";
        var invalidMarkerCompilation = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(invalidMarkerProgram));
        GeneratorDriver invalidMarkerDriver = CSharpGeneratorDriver.Create(
            generators: new[]
            {
                new ProGpuXamlSourceGenerator().AsSourceGenerator()
            },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText(
                    "/project/BrokenMetadata.xaml",
                    """
<local:BrokenMetadataControl
    xmlns:local="using:Demo" />
""")
            },
            parseOptions: CSharpParseOptions.Default);
        invalidMarkerDriver = invalidMarkerDriver.RunGenerators(
            invalidMarkerCompilation);
        var markerDiagnostics =
            invalidMarkerDriver.GetRunResult().Diagnostics;
        Assert.Contains(
            markerDiagnostics,
            diagnostic => diagnostic.Id == "PGXAML2076");
        Assert.Contains(
            markerDiagnostics,
            diagnostic => diagnostic.Id == "PGXAML2077");
    }

    [Fact]
    public void GeneratorReportsMalformedDesignerSerializationMethodShape()
    {
        const string program = """
namespace Demo {
  public sealed class BrokenControl {
    public string Value { get; set; } = string.Empty;
    private bool ShouldSerializeValue(int argument) => argument != 0;
  }
}
""";
        var compilation = CSharpCompilation.Create(
            "DesignerMethodGeneratorHarness",
            new[] { CSharpSyntaxTree.ParseText(program) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[]
            {
                new ProGpuXamlSourceGenerator().AsSourceGenerator()
            },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryAdditionalText(
                    "/project/BrokenControl.xaml",
                    """
<local:BrokenControl
    xmlns:local="using:Demo"
    Value="test" />
""")
            },
            parseOptions: CSharpParseOptions.Default);
        driver = driver.RunGenerators(compilation);
        Assert.Contains(
            driver.GetRunResult().Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2082");
    }

    [Fact]
    public void ProductionEmitterDoesNotUseRoslynParseBackApis()
    {
        var repository = FindRepositoryRoot();
        foreach (var relativePath in new[]
                 {
                     "src/ProGPU.Xaml.Roslyn",
                     "src/ProGPU.Xaml.SourceGenerator"
                 })
        {
            foreach (var path in Directory.EnumerateFiles(
                         Path.Combine(repository, relativePath), "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(path);
                Assert.DoesNotContain("SyntaxFactory.Parse", source, StringComparison.Ordinal);
                Assert.DoesNotContain("ParseExpression(", source, StringComparison.Ordinal);
                Assert.DoesNotContain("ParseStatement(", source, StringComparison.Ordinal);
                Assert.DoesNotContain("ParseTypeName(", source, StringComparison.Ordinal);
                Assert.DoesNotContain("ParseMemberDeclaration(", source, StringComparison.Ordinal);
            }
        }
    }

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class TestMetadataProvider : IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.metadata";
        public int MetadataPriority => 1000;
        public IReadOnlyList<XamlSchemaAttributeRule> AttributeRules { get; } = new[]
        {
            new XamlSchemaAttributeRule(
                "Meta.ContentAttribute",
                XamlSchemaSemantics.ContentProperty,
                XamlSchemaAttributeTargets.Member,
                inherited: true),
            new XamlSchemaAttributeRule(
                "Meta.RuntimeNameAttribute",
                XamlSchemaSemantics.RuntimeNameProperty,
                XamlSchemaAttributeTargets.Type,
                inherited: true,
                XamlSchemaAttributeValueSource.ConstructorArgument,
                constructorArgumentIndex: 0),
            new XamlSchemaAttributeRule(
                "Meta.DictionaryKeyAttribute",
                XamlSchemaSemantics.DictionaryKeyProperty,
                XamlSchemaAttributeTargets.Type,
                inherited: true,
                XamlSchemaAttributeValueSource.ConstructorArgument,
                constructorArgumentIndex: 0),
        };
        public XamlSymbolShapePolicy SymbolShapePolicy { get; } = new XamlSymbolShapePolicy(
            attachedGetterPrefix: "Get",
            attachedSetterPrefix: "Set",
            runtimeNameFallback: "Name");
    }

    private sealed class NamespaceMetadataProvider : IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.namespaces";
        public int MetadataPriority => 2000;
        public IReadOnlyList<XamlSchemaAttributeRule> AttributeRules { get; } = new[]
        {
            new XamlSchemaAttributeRule("Meta.XmlnsDefinitionAttribute", XamlSchemaSemantics.XmlnsDefinition, XamlSchemaAttributeTargets.Assembly),
            new XamlSchemaAttributeRule("Meta.XmlnsPrefixAttribute", XamlSchemaSemantics.XmlnsPrefix, XamlSchemaAttributeTargets.Assembly),
            new XamlSchemaAttributeRule("Meta.XmlnsCompatibleWithAttribute", XamlSchemaSemantics.XmlnsCompatibleWith, XamlSchemaAttributeTargets.Assembly),
            new XamlSchemaAttributeRule(
                "Meta.ResourceIdAttribute",
                XamlSchemaSemantics.XamlResourceId,
                XamlSchemaAttributeTargets.Assembly,
                valueSource: XamlSchemaAttributeValueSource.ConstructorArgument,
                constructorArgumentIndex: 0,
                allowMultiple: true),
        };
        public XamlSymbolShapePolicy SymbolShapePolicy { get; } = XamlSymbolShapePolicy.Default;
    }

    private sealed class AliasProfileFactory : IXamlFrameworkProfileFactory
    {
        private readonly RoslynXamlExtensionHost _extensionHost;

        public AliasProfileFactory(
            string id,
            RoslynXamlExtensionHost? extensionHost = null)
        {
            Id = id;
            _extensionHost = extensionHost ?? RoslynXamlExtensionHost.Empty;
        }

        public string Id { get; }
        public int ContractVersion => XamlFrameworkContract.CurrentVersion;
        public IRoslynXamlFrameworkProfile CreateProfile() =>
            new AliasProfile(Id, _extensionHost);
    }

    private sealed class AliasProfile :
        IRoslynXamlFrameworkProfile,
        IRoslynXamlExtensionProvider
    {
        private readonly WinUiXamlProfile _inner = new();

        public AliasProfile(
            string id,
            RoslynXamlExtensionHost extensionHost)
        {
            Id = id;
            RoslynExtensionHost = extensionHost;
        }

        public string Id { get; }
        public RoslynXamlExtensionHost RoslynExtensionHost { get; }
        public int ContractVersion => _inner.ContractVersion;
        public XamlFrameworkCapabilities Capabilities => _inner.Capabilities;
        public IReadOnlyList<string> FileExtensions => _inner.FileExtensions;

        public IReadOnlyList<string> GetClrNamespaceCandidates(
            string xamlNamespaceUri) =>
            _inner.GetClrNamespaceCandidates(xamlNamespaceUri);

        public bool TryCreateLiteralExpression(
            XamlTypeInfo targetType,
            string text,
            out Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression) =>
            _inner.TryCreateLiteralExpression(targetType, text, out expression);

        public bool TryCreateMarkupExtensionExpression(
            XamlMarkupExtension extension,
            XamlTypeInfo targetType,
            out Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(
                extension,
                targetType,
                out expression);

        public bool TryCreateMarkupExtensionExpression(
            ProGPU.Xaml.Lowering.XamlIrObject extension,
            XamlTypeInfo targetType,
            out Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(
                extension,
                targetType,
                out expression);

        public bool TryCreateResourceReferenceExpression(
            ProGPU.Xaml.Lowering.XamlIrResourceReference reference,
            XamlTypeInfo targetType,
            Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax lookupRoot,
            Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax resourceKey,
            out Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression) =>
            _inner.TryCreateResourceReferenceExpression(
                reference,
                targetType,
                lookupRoot,
                resourceKey,
                out expression);
    }

    private sealed class GeneratorBoundTextTransform :
        IRoslynXamlBoundDocumentTransformExtension
    {
        private readonly string _from;
        private readonly string _to;

        public GeneratorBoundTextTransform(string from, string to)
        {
            _from = from;
            _to = to;
        }

        public string Id => "tests.generator-bound-text-transform";
        public int ContractVersion => RoslynXamlExtensionHost.CurrentContractVersion;
        public int Version => 1;
        public int Priority => 0;
        public RoslynXamlExtensionCapabilities Capabilities =>
            RoslynXamlExtensionCapabilities.BoundDocumentTransform;
        public RoslynXamlExtensionConflictPolicy ConflictPolicy =>
            RoslynXamlExtensionConflictPolicy.Diagnose;

        public RoslynXamlBoundDocumentTransformResult Transform(
            RoslynXamlBoundDocumentTransformContext context) =>
            new(Rewrite(context.Document.Root));

        private XamlBoundObject? Rewrite(XamlBoundObject? value)
        {
            if (value == null)
                return null;
            var members = ImmutableArray.CreateBuilder<XamlBoundMember>(
                value.Members.Length);
            foreach (var member in value.Members)
            {
                var values = ImmutableArray.CreateBuilder<XamlBoundValue>(
                    member.Values.Length);
                foreach (var child in member.Values)
                    values.Add(Rewrite(child));
                members.Add(new XamlBoundMember(
                    member.Member,
                    member.Origin,
                    values.ToImmutable(),
                    member.SourceSpan,
                    member.StableId));
            }
            return new XamlBoundObject(
                value.Type,
                members.ToImmutable(),
                value.IsRetrieved,
                value.IsMarkupExtension,
                value.SourceSpan,
                value.StableId);
        }

        private XamlBoundValue Rewrite(XamlBoundValue value) =>
            value switch
            {
                XamlBoundText text when string.Equals(
                    text.Text,
                    _from,
                    StringComparison.Ordinal) =>
                    new XamlBoundText(
                        _to,
                        text.IsCData,
                        text.SourceSpan,
                        text.StableId,
                        text.TextSyntax,
                        text.OriginalText),
                XamlBoundObject child => Rewrite(child)!,
                _ => value
            };
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;
        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(
                text,
                Encoding.UTF8,
                SourceHashAlgorithm.Sha256);
        }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class GlobalAnalyzerConfigOptionsProvider :
        AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty =
            new DictionaryAnalyzerConfigOptions(
                new Dictionary<string, string>(StringComparer.Ordinal));
        private readonly AnalyzerConfigOptions _globalOptions;

        public GlobalAnalyzerConfigOptionsProvider(
            IReadOnlyDictionary<string, string> globalOptions)
        {
            _globalOptions = new DictionaryAnalyzerConfigOptions(globalOptions);
        }

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty = new DictionaryAnalyzerConfigOptions(
            new Dictionary<string, string>(StringComparer.Ordinal));
        private readonly string _disabledPath;
        public TestAnalyzerConfigOptionsProvider(string disabledPath) => _disabledPath = disabledPath;
        public override AnalyzerConfigOptions GlobalOptions => Empty;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            string.Equals(textFile.Path, _disabledPath, StringComparison.Ordinal)
                ? new DictionaryAnalyzerConfigOptions(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build_metadata.AdditionalFiles.ProGpuXamlCompile"] = "false"
                })
                : Empty;
    }

    private sealed class LogicalPathAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty = new DictionaryAnalyzerConfigOptions(
            new Dictionary<string, string>(StringComparer.Ordinal));
        private readonly string _path;
        private readonly string _logicalPath;
        public LogicalPathAnalyzerConfigOptionsProvider(string path, string logicalPath)
        { _path = path; _logicalPath = logicalPath; }
        public override AnalyzerConfigOptions GlobalOptions => Empty;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            string.Equals(textFile.Path, _path, StringComparison.Ordinal)
                ? new DictionaryAnalyzerConfigOptions(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build_metadata.AdditionalFiles.ProGpuXamlLogicalPath"] = _logicalPath
                })
                : Empty;
    }

    private sealed class LogicalPathsAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty = new DictionaryAnalyzerConfigOptions(
            new Dictionary<string, string>(StringComparer.Ordinal));
        private readonly IReadOnlyDictionary<string, string> _logicalPaths;
        public LogicalPathsAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> logicalPaths) =>
            _logicalPaths = logicalPaths;
        public override AnalyzerConfigOptions GlobalOptions => Empty;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            _logicalPaths.TryGetValue(textFile.Path, out var logicalPath)
                ? new DictionaryAnalyzerConfigOptions(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build_metadata.AdditionalFiles.ProGpuXamlLogicalPath"] = logicalPath
                })
                : Empty;
    }

    private sealed class DictionaryAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;
        public DictionaryAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values) => _values = values;
        public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }
}
