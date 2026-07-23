using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Resources;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlResourceProjectIndexTests
{
    [Fact]
    public void ManifestAndProjectIndexTrackOnlyDirectAndTransitiveProviders()
    {
        var page = Manifest("/project/Pages/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Page.Resources><ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="../Themes/Colors.xaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary></Page.Resources>
  <TextBlock Text="{StaticResource Accent}" />
</Page>
""");
        var colors = Manifest("/project/Themes/Colors.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="ms-appx:///Themes/Base.xaml" />
  </ResourceDictionary.MergedDictionaries>
  <SolidColorBrush x:Key="Accent" Color="Red" />
</ResourceDictionary>
""");
        var baseTheme = Manifest("/project/Themes/Base.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="BaseBrush" Color="Black" />
</ResourceDictionary>
""");
        var unrelated = Manifest("/project/Themes/Unrelated.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="Other" Color="Blue" />
</ResourceDictionary>
""");

        Assert.Equal("Accent", Assert.Single(colors.Definitions).Key);
        Assert.Equal("Accent", Assert.Single(page.References).Key);
        var slice = new XamlResourceProjectIndex(new[] { page, colors, baseTheme, unrelated })
            .GetDependencySlice(page.DocumentPath);
        Assert.Equal(new[] { page.DocumentPath, baseTheme.DocumentPath, colors.DocumentPath },
            slice.ProviderPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Accent", "BaseBrush" },
            slice.ExternalDefinitions.Select(static definition => definition.Key));
        Assert.Equal(colors.DocumentPath, slice.ExternalDefinitions[0].ProviderPath);
        Assert.Equal(baseTheme.DocumentPath, slice.ExternalDefinitions[1].ProviderPath);

        var changedUnrelated = Manifest("/project/Themes/Unrelated.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="using:System">
  <sys:String x:Key="Different">value</sys:String>
</ResourceDictionary>
""");
        var unrelatedSlice = new XamlResourceProjectIndex(new[] { page, colors, baseTheme, changedUnrelated })
            .GetDependencySlice(page.DocumentPath);
        Assert.Equal(slice, unrelatedSlice);

        var changedColors = Manifest("/project/Themes/Colors.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="Accent" Color="Red" />
  <SolidColorBrush x:Key="AccentSecondary" Color="Green" />
</ResourceDictionary>
""");
        var changedProviderSlice = new XamlResourceProjectIndex(new[] { page, changedColors, baseTheme, unrelated })
            .GetDependencySlice(page.DocumentPath);
        Assert.NotEqual(slice, changedProviderSlice);
    }

    [Fact]
    public void ExternalDefinitionsPreserveLocalThenReverseMergedPrecedence()
    {
        var page = Manifest("/project/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/First.xaml" />
    <ResourceDictionary Source="Themes/Second.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var first = Manifest("/project/Themes/First.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="Shared">first</x:String>
</ResourceDictionary>
""");
        var second = Manifest("/project/Themes/Second.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="Shared">second</x:String>
  <ResourceDictionary x:Key="Nested"><x:String x:Key="Hidden">private</x:String></ResourceDictionary>
</ResourceDictionary>
""");

        var definitions = new XamlResourceProjectIndex(new[] { page, first, second })
            .GetDependencySlice(page.DocumentPath).ExternalDefinitions;

        Assert.Equal(new[] { second.DocumentPath, first.DocumentPath },
            definitions.Where(static definition => definition.Key == "Shared")
                .Select(static definition => definition.ProviderPath));
        Assert.Contains(definitions, definition => definition.Key == "Nested");
        Assert.DoesNotContain(definitions, definition => definition.Key == "Hidden");
    }

    [Fact]
    public void NestedDictionarySourceDoesNotLeakThroughImportedProvider()
    {
        var page = Manifest("/project/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Composite.xaml" />
  </ResourceDictionary.MergedDictionaries>
</ResourceDictionary>
""");
        var composite = Manifest("/project/Themes/Composite.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Visible.xaml" />
  </ResourceDictionary.MergedDictionaries>
  <ResourceDictionary x:Key="PrivateScope" Source="Hidden.xaml" />
</ResourceDictionary>
""");
        var visible = Manifest("/project/Themes/Visible.xaml", DictionaryWithKey("Visible"));
        var hidden = Manifest("/project/Themes/Hidden.xaml", DictionaryWithKey("Hidden"));

        var slice = new XamlResourceProjectIndex(new[] { page, composite, visible, hidden })
            .GetDependencySlice(page.DocumentPath);

        Assert.Contains(slice.ExternalDefinitions, definition => definition.Key == "PrivateScope");
        Assert.Contains(slice.ExternalDefinitions, definition => definition.Key == "Visible");
        Assert.DoesNotContain(slice.ExternalDefinitions, definition => definition.Key == "Hidden");
        Assert.Contains(hidden.DocumentPath, slice.ProviderPaths);
        var hiddenImport = Assert.Single(composite.ImportEntries, import => import.Source == "Hidden.xaml");
        Assert.False(hiddenImport.IsProviderVisible);
        Assert.NotEqual(Assert.Single(composite.ImportEntries, import => import.Source == "Visible.xaml").ScopeIdentity,
            hiddenImport.ScopeIdentity);
    }

    [Fact]
    public void TypedResourceKeysPreservePortableIdentityAcrossProjectManifests()
    {
        var page = Manifest("/project/MainPage.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      Content="{StaticResource {x:Type local:Widget}}">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Typed.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var provider = Manifest("/project/Themes/Typed.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Type local:Widget}">typed</x:String>
</ResourceDictionary>
""");

        var definition = Assert.Single(provider.Definitions);
        var reference = Assert.Single(page.References);
        Assert.Equal(XamlResourceManifestKeyKind.Type, definition.ResourceKey.Kind);
        Assert.Equal("type:global::Demo.Widget", definition.ResourceKey.Identity);
        Assert.Equal(definition.ResourceKey.Identity, reference.ResourceKey.Identity);

        var external = Assert.Single(new XamlResourceProjectIndex(new[] { page, provider })
            .GetDependencySlice(page.DocumentPath).ExternalDefinitions);
        Assert.Equal(definition.ResourceKey.Identity, external.ResourceKey.Identity);
    }

    [Fact]
    public void MissingImportPreservesItsSourceIdentityAndLocation()
    {
        const string source = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Missing.xaml" />
  </ResourceDictionary.MergedDictionaries>
</ResourceDictionary>
""";
        var manifest = Manifest("/project/Main.xaml", source);

        var import = Assert.Single(manifest.ImportEntries);
        Assert.Equal("Themes/Missing.xaml", import.Source);
        Assert.Equal("Themes/Missing.xaml", source.Substring(import.SourceSpan.Start, import.SourceSpan.Length));
        Assert.Equal(2, import.LineSpan.Start.Line);
        Assert.NotEqual(0UL, import.StableId);

        var slice = new XamlResourceProjectIndex(new[] { manifest }).GetDependencySlice(manifest.DocumentPath);
        var issue = Assert.Single(slice.ImportIssues);
        Assert.Equal(XamlResourceImportIssueKind.Missing, issue.Kind);
        Assert.Equal(manifest.DocumentPath, issue.ImportingDocumentPath);
        Assert.Equal(import, issue.Import);
        Assert.Empty(issue.CandidatePaths);
    }

    [Fact]
    public void AmbiguousSuffixImportReportsAllCandidatesInStableOrder()
    {
        var importer = Manifest("/project/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="Themes/Shared.xaml" />
</ResourceDictionary>
""");
        var first = Manifest("/packages/A/Themes/Shared.xaml", DictionaryWithKey("First"));
        var second = Manifest("/packages/B/Themes/Shared.xaml", DictionaryWithKey("Second"));

        var issue = Assert.Single(new XamlResourceProjectIndex(new[] { importer, second, first })
            .GetDependencySlice(importer.DocumentPath).ImportIssues);

        Assert.Equal(XamlResourceImportIssueKind.Ambiguous, issue.Kind);
        Assert.Equal(new[] { first.DocumentPath, second.DocumentPath }, issue.CandidatePaths);
    }

    [Fact]
    public void CycleReportsEachParticipatingImportWithoutRecursingForever()
    {
        var first = Manifest("/project/First.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="Second.xaml" />
</ResourceDictionary>
""");
        var second = Manifest("/project/Second.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="First.xaml" />
</ResourceDictionary>
""");

        var slice = new XamlResourceProjectIndex(new[] { first, second }).GetDependencySlice(first.DocumentPath);

        Assert.Equal(new[] { first.DocumentPath, second.DocumentPath }, slice.ProviderPaths);
        Assert.Equal(2, slice.ImportIssues.Length);
        Assert.All(slice.ImportIssues, issue => Assert.Equal(XamlResourceImportIssueKind.Cycle, issue.Kind));
        Assert.Contains(slice.ImportIssues, issue => issue.ImportingDocumentPath == first.DocumentPath);
        Assert.Contains(slice.ImportIssues, issue => issue.ImportingDocumentPath == second.DocumentPath);
    }

    [Fact]
    public void ImportLocationChangesDoNotInvalidateEquivalentDependencySlice()
    {
        var compact = Manifest("/project/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ResourceDictionary Source="Missing.xaml" /></ResourceDictionary>
""");
        var expanded = Manifest("/project/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">

  <ResourceDictionary Source="Missing.xaml" />
</ResourceDictionary>
""");

        var compactSlice = new XamlResourceProjectIndex(new[] { compact }).GetDependencySlice(compact.DocumentPath);
        var expandedSlice = new XamlResourceProjectIndex(new[] { expanded }).GetDependencySlice(expanded.DocumentPath);

        Assert.Equal(Assert.Single(compact.ImportEntries).ScopeIdentity,
            Assert.Single(expanded.ImportEntries).ScopeIdentity);
        Assert.NotEqual(Assert.Single(compact.ImportEntries).ScopeOwnerStableId,
            Assert.Single(expanded.ImportEntries).ScopeOwnerStableId);
        Assert.Equal(compactSlice.Fingerprint, expandedSlice.Fingerprint);
        Assert.NotEqual(compactSlice, expandedSlice);
        Assert.NotEqual(compactSlice.ImportIssues[0].Import.SourceSpan, expandedSlice.ImportIssues[0].Import.SourceSpan);
    }

    [Fact]
    public void MovingImportToDifferentLexicalScopeInvalidatesDependencyFingerprint()
    {
        var pageScope = Manifest("/project/Main.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Missing.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var elementScope = Manifest("/project/Main.xaml", """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <StackPanel><StackPanel.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Missing.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></StackPanel.Resources></StackPanel>
</Page>
""");

        Assert.NotEqual(Assert.Single(pageScope.ImportEntries).ScopeIdentity,
            Assert.Single(elementScope.ImportEntries).ScopeIdentity);
        Assert.NotEqual(
            new XamlResourceProjectIndex(new[] { pageScope }).GetDependencySlice(pageScope.DocumentPath).Fingerprint,
            new XamlResourceProjectIndex(new[] { elementScope }).GetDependencySlice(elementScope.DocumentPath).Fingerprint);
    }

    [Fact]
    public void LogicalResourceUrisResolveLinkedProvidersIndependentlyOfPhysicalPaths()
    {
        var page = Manifest("/checkout/App/Pages/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="../Themes/Colors.xaml" />
</ResourceDictionary>
""", "Pages/Main.xaml");
        var linked = Manifest(
            "/shared/package-content/Colors.xaml",
            DictionaryWithKey("Accent"),
            "Themes/Colors.xaml");

        var slice = new XamlResourceProjectIndex(new[] { page, linked }).GetDependencySlice(page.DocumentPath);

        Assert.Contains(linked.DocumentPath, slice.ProviderPaths);
        Assert.Empty(slice.ImportIssues);
        Assert.Equal("/Themes/Colors.xaml", linked.ResourceUri);
    }

    [Fact]
    public void QualifiedResourceAuthoritiesDoNotCollapseToPathSuffixes()
    {
        var importer = Manifest("/checkout/App/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="avares://PackageA/Themes/Colors.xaml" />
</ResourceDictionary>
""", "avares://App/Views/Main.xaml");
        var packageA = Manifest("/packages/A/Themes/Colors.xaml", DictionaryWithKey("PackageA"),
            "avares://PackageA/Themes/Colors.xaml");
        var packageB = Manifest("/packages/B/Themes/Colors.xaml", DictionaryWithKey("PackageB"),
            "avares://PackageB/Themes/Colors.xaml");

        var slice = new XamlResourceProjectIndex(new[] { importer, packageB, packageA })
            .GetDependencySlice(importer.DocumentPath);

        Assert.Empty(slice.ImportIssues);
        Assert.Contains(packageA.DocumentPath, slice.ProviderPaths);
        Assert.DoesNotContain(packageB.DocumentPath, slice.ProviderPaths);
        Assert.Equal("avares://PackageA/Themes/Colors.xaml", packageA.ResourceUri);

        var wrongAuthority = Manifest("/checkout/App/Missing.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="avares://Missing/Themes/Colors.xaml" />
</ResourceDictionary>
""", "avares://App/Views/Missing.xaml");
        var missing = new XamlResourceProjectIndex(new[] { wrongAuthority, packageA })
            .GetDependencySlice(wrongAuthority.DocumentPath);
        Assert.Equal(XamlResourceImportIssueKind.Missing, Assert.Single(missing.ImportIssues).Kind);
        Assert.DoesNotContain(packageA.DocumentPath, missing.ProviderPaths);
    }

    [Fact]
    public void RelativeImportsStayWithinQualifiedAuthority()
    {
        var importer = Manifest("/checkout/App/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="../Themes/Colors.xaml" />
</ResourceDictionary>
""", "avares://PackageA/Views/Main.xaml");
        var sameAuthority = Manifest("/packages/A/Colors.xaml", DictionaryWithKey("Same"),
            "avares://PackageA/Themes/Colors.xaml");
        var otherAuthority = Manifest("/packages/B/Colors.xaml", DictionaryWithKey("Other"),
            "avares://PackageB/Themes/Colors.xaml");

        var slice = new XamlResourceProjectIndex(new[] { importer, otherAuthority, sameAuthority })
            .GetDependencySlice(importer.DocumentPath);

        Assert.Empty(slice.ImportIssues);
        Assert.Contains(sameAuthority.DocumentPath, slice.ProviderPaths);
        Assert.DoesNotContain(otherAuthority.DocumentPath, slice.ProviderPaths);
    }

    [Theory]
    [InlineData("/Library;component/Themes/Colors.xaml")]
    [InlineData("pack://application:,,,/Library;component/Themes/Colors.xaml")]
    public void WpfComponentUrisResolveOnlyMatchingAssembly(string source)
    {
        var importer = Manifest("/checkout/App/Main.xaml", $$"""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="{{source}}" />
</ResourceDictionary>
""", "/App/Main.xaml");
        var library = Manifest("/packages/Library/Themes/Colors.xaml", DictionaryWithKey("Library"),
            "/Library;component/Themes/Colors.xaml");
        var other = Manifest("/packages/Other/Themes/Colors.xaml", DictionaryWithKey("Other"),
            "/Other;component/Themes/Colors.xaml");

        var slice = new XamlResourceProjectIndex(new[] { importer, other, library })
            .GetDependencySlice(importer.DocumentPath);

        Assert.Empty(slice.ImportIssues);
        Assert.Contains(library.DocumentPath, slice.ProviderPaths);
        Assert.DoesNotContain(other.DocumentPath, slice.ProviderPaths);
        Assert.Equal("component://Library/Themes/Colors.xaml", library.ResourceUri);
    }

    [Fact]
    public void WpfApplicationPackUriCanResolveCurrentProjectLogicalProvider()
    {
        var importer = Manifest("/checkout/App/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="pack://application:,,,/Themes/Colors.xaml" />
</ResourceDictionary>
""", "/Main.xaml");
        var provider = Manifest("/checkout/App/Themes/Colors.xaml", DictionaryWithKey("Local"),
            "/Themes/Colors.xaml");

        var slice = new XamlResourceProjectIndex(new[] { importer, provider })
            .GetDependencySlice(importer.DocumentPath);

        Assert.Empty(slice.ImportIssues);
        Assert.Contains(provider.DocumentPath, slice.ProviderPaths);
    }

    [Fact]
    public void MsAppxAuthorityWinsOverUnqualifiedCurrentPackageFallback()
    {
        var importer = Manifest("/checkout/App/Main.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="ms-appx://Contoso/Themes/Colors.xaml" />
</ResourceDictionary>
""", "/Main.xaml");
        var qualified = Manifest("/packages/Contoso/Colors.xaml", DictionaryWithKey("Qualified"),
            "ms-appx://Contoso/Themes/Colors.xaml");
        var unqualified = Manifest("/checkout/App/Themes/Colors.xaml", DictionaryWithKey("Local"),
            "/Themes/Colors.xaml");

        var slice = new XamlResourceProjectIndex(new[] { importer, unqualified, qualified })
            .GetDependencySlice(importer.DocumentPath);

        Assert.Empty(slice.ImportIssues);
        Assert.Contains(qualified.DocumentPath, slice.ProviderPaths);
        Assert.DoesNotContain(unqualified.DocumentPath, slice.ProviderPaths);
    }

    [Fact]
    public void DuplicateLogicalProviderUriIsReportedForEveryPublishingDocument()
    {
        var first = Manifest("/checkout/A/Colors.xaml", DictionaryWithKey("First"), "Themes/Colors.xaml");
        var second = Manifest("/checkout/B/Colors.xaml", DictionaryWithKey("Second"), "./Themes/Colors.xaml");
        var index = new XamlResourceProjectIndex(new[] { second, first });

        var firstIssue = Assert.Single(index.GetDependencySlice(first.DocumentPath).ProviderIssues);
        var secondIssue = Assert.Single(index.GetDependencySlice(second.DocumentPath).ProviderIssues);

        Assert.Equal("/Themes/Colors.xaml", firstIssue.ResourceUri);
        Assert.Equal(new[] { first.DocumentPath, second.DocumentPath }, firstIssue.CandidateDocumentPaths);
        Assert.Equal(firstIssue.CandidateDocumentPaths.ToArray(), secondIssue.CandidateDocumentPaths.ToArray());
        Assert.Equal(0, firstIssue.LineSpan.Start.Line);
    }

    [Fact]
    public void ClassBackedDictionaryDoesNotPublishAResourceProviderIdentity()
    {
        var importer = Manifest("/checkout/App.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <ResourceDictionary Source="Themes/Colors.xaml" />
</ResourceDictionary>
""", "App.xaml");
        var classless = Manifest(
            "/checkout/CompiledColors.xaml",
            DictionaryWithKey("Compiled"),
            "Themes/Colors.xaml");
        var classBacked = Manifest("/checkout/CodeBehindColors.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="Demo.CodeBehindColors" />
""", "Themes/Colors.xaml");

        var slice = new XamlResourceProjectIndex(new[] { importer, classBacked, classless })
            .GetDependencySlice(importer.DocumentPath);

        Assert.True(classless.IsCompiledResourceProvider);
        Assert.False(classBacked.IsCompiledResourceProvider);
        Assert.Empty(slice.ProviderIssues);
        Assert.Empty(slice.ImportIssues);
        Assert.Contains(classless.DocumentPath, slice.ProviderPaths);
        Assert.DoesNotContain(classBacked.DocumentPath, slice.ProviderPaths);
    }

    private static string DictionaryWithKey(string key) => $$"""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="{{key}}">value</x:String>
</ResourceDictionary>
""";

    private static XamlResourceDocumentManifest Manifest(string path, string source, string? resourceUri = null)
    {
        var syntax = XamlParser.Parse(SourceText.From(source), path).Document;
        var infoset = new XamlInfosetConverter().Convert(syntax);
        Assert.DoesNotContain(infoset.Diagnostics,
            diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        return resourceUri == null
            ? new XamlResourceDocumentManifestBuilder().Build(infoset)
            : new XamlResourceDocumentManifestBuilder().Build(infoset, resourceUri);
    }
}
