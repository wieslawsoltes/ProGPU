using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Syntax;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlInfosetTests
{
    [Fact]
    public void ConvertsObjectsMembersMarkupAndNamespaceSnapshotsWithParentLinks()
    {
        const string source = """
<Page xmlns="urn:test"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:sys="using:System"
      x:Class="Demo.MainPage"
      Title="{}literal">
  <Page.Resources><ResourceDictionary /></Page.Resources>
  <StackPanel Text="{Binding Path={x:Static sys:String.Empty}, Mode=OneWay}" />
</Page>
""";
        var syntax = XamlParser.Parse(SourceText.From(source), "MainPage.xaml").Document;
        var document = new XamlInfosetConverter().Convert(syntax);

        Assert.False(document.HasErrors);
        var root = Assert.IsType<XamlInfosetObject>(document.Root);
        Assert.Equal(new XamlQualifiedName("urn:test", "Page"), root.TypeName);
        Assert.Contains(root.NamespaceMappings, mapping => mapping.Prefix == "x" && mapping.NamespaceUri == XamlNamespaces.Language2006);
        Assert.Contains(root.NamespaceMappings, mapping => mapping.Prefix == "sys" && mapping.NamespaceUri == "using:System");

        var classMember = Assert.Single(root.Members.Where(member => member.Name.LocalName == "Class"));
        Assert.True(classMember.Name.IsDirective);
        Assert.Equal("Demo.MainPage", Assert.IsType<XamlInfosetText>(Assert.Single(classMember.Values)).Text);

        var title = Assert.Single(root.Members.Where(member => member.Name.LocalName == "Title"));
        Assert.Equal("literal", Assert.IsType<XamlInfosetText>(Assert.Single(title.Values)).Text);

        var resources = Assert.Single(root.Members.Where(member => member.Name.LocalName == "Resources"));
        Assert.Equal(XamlMemberOrigin.MemberElement, resources.Origin);
        var dictionary = Assert.IsType<XamlInfosetObject>(Assert.Single(resources.Values));
        Assert.Same(resources, dictionary.ParentMember);
        Assert.Same(root, resources.ParentObject);

        var content = Assert.Single(root.Members.Where(member => member.Name.IsImplicit));
        Assert.Equal(3, content.Values.OfType<XamlInfosetText>().Count());
        Assert.All(
            content.Values.OfType<XamlInfosetText>(),
            value => Assert.True(string.IsNullOrWhiteSpace(value.Text)));
        var panel = Assert.Single(content.Values.OfType<XamlInfosetObject>());
        Assert.Equal("StackPanel", panel.TypeName.LocalName);
        var text = Assert.Single(panel.Members.Where(member => member.Name.LocalName == "Text"));
        var binding = Assert.IsType<XamlInfosetObject>(Assert.Single(text.Values));
        Assert.Equal("Binding", binding.TypeName.LocalName);
        var path = Assert.Single(binding.Members.Where(member => member.Name.LocalName == "Path"));
        var staticExtension = Assert.IsType<XamlInfosetObject>(Assert.Single(path.Values));
        Assert.Equal(XamlNamespaces.Language2006, staticExtension.TypeName.NamespaceUri);
        Assert.Equal("Static", staticExtension.TypeName.LocalName);
        Assert.True(staticExtension.SourceSpan.Start >= source.IndexOf("{Binding", StringComparison.Ordinal));
        Assert.True(staticExtension.SourceSpan.End <= source.IndexOf('"', source.IndexOf("{Binding", StringComparison.Ordinal)));
    }

    [Fact]
    public void RecoveryRetainsGraphWhileStrictModeRejectsDuplicateMembers()
    {
        const string source = """
<Page xmlns="urn:test">
  <Page.Title>one</Page.Title>
  <Page.Title>two</Page.Title>
</Page>
""";
        var syntax = XamlParser.Parse(
            SourceText.From(source),
            "Duplicate.xaml",
            new XamlParseOptions { Mode = XamlParseMode.Recovering }).Document;
        var converter = new XamlInfosetConverter();

        var recovering = converter.Convert(syntax, new XamlInfosetConversionOptions { Mode = XamlParseMode.Recovering });
        Assert.NotNull(recovering.Root);
        Assert.Contains(recovering.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML1202" &&
            diagnostic.Properties["MSXamlSection"] == "6.2.1.3");

        var strict = converter.Convert(syntax, new XamlInfosetConversionOptions { Mode = XamlParseMode.Strict });
        Assert.Null(strict.Root);
        Assert.True(strict.HasErrors);
    }

    [Fact]
    public void ReportsNestedMemberElementsAndNonRootClassWithSectionIds()
    {
        const string source = """
<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Page.Content>
    <Grid x:Class="Invalid.Child">
      <Grid.Content><Grid.Other /></Grid.Content>
    </Grid>
  </Page.Content>
</Page>
""";
        var syntax = XamlParser.Parse(
            SourceText.From(source),
            "Invalid.xaml",
            new XamlParseOptions { Mode = XamlParseMode.Recovering }).Document;
        var document = new XamlInfosetConverter().Convert(
            syntax,
            new XamlInfosetConversionOptions { Mode = XamlParseMode.Recovering });

        Assert.NotNull(document.Root);
        Assert.Contains(document.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML1204" && diagnostic.Properties["MSXamlSection"] == "6.3.1.6");
        Assert.Contains(document.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML1203" && diagnostic.Properties["MSXamlSection"] == "8.6.5");
    }

    [Fact]
    public void IntrinsicDirectiveLocationsComeFromCanonicalSchemaData()
    {
        const string source = """
<Widget xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Arguments="invalid">
  <Widget.Value>first</Widget.Value>
  <x:Arguments><x:Int32>1</x:Int32></x:Arguments>
  <x:Name>also-invalid</x:Name>
</Widget>
""";
        var syntax = XamlParser.Parse(
            SourceText.From(source),
            "DirectiveLocations.xaml",
            new XamlParseOptions { Mode = XamlParseMode.Recovering }).Document;
        var document = new XamlInfosetConverter().Convert(
            syntax,
            new XamlInfosetConversionOptions { Mode = XamlParseMode.Recovering });

        Assert.NotNull(document.Root);
        Assert.Contains(document.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML1209" && diagnostic.Properties["MSXamlSection"] == "7.3.16");
        Assert.Contains(document.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML1209" && diagnostic.GetMessage().Contains("x:Name", StringComparison.Ordinal));
    }
}
