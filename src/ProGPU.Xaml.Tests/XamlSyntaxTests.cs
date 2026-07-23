using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Editing;
using ProGPU.Xaml.Parsing;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlSyntaxTests
{
    [Fact]
    public void LosslessTokensReconstructDocument()
    {
        const string source = "<?xml version=\"1.0\"?><Page xmlns='urn:test' A=\"&amp;\"><!--c--><![CDATA[t]]></Page>";
        var tree = XamlParser.Parse(SourceText.From(source), "sample.xaml",
            new XamlParseOptions { Mode = XamlParseMode.Recovering });
        var reconstructed = string.Concat(tree.Tokens
            .Where(token => token.Kind != ProGPU.Xaml.Syntax.XamlTokenKind.EndOfFile)
            .Select(token => token.Text));
        Assert.Equal(source, reconstructed);
    }

    [Fact]
    public void AnnotationsDoNotMutatePublishedNode()
    {
        var root = XamlParser.Parse(SourceText.From("<Page/>"), "sample.xaml").GetRoot()!;
        var annotation = new SyntaxAnnotation("test", "value");
        var annotated = root.WithAdditionalAnnotations(annotation);
        Assert.False(root.HasAnnotation(annotation));
        Assert.True(annotated.HasAnnotation(annotation));
        Assert.NotSame(root, annotated);
    }

    [Fact]
    public void BatchedEditorReturnsRoslynTextChangesAndNewTree()
    {
        var tree = XamlParser.Parse(SourceText.From("<Page><Old/></Page>"), "sample.xaml");
        var old = Assert.IsType<ProGPU.Xaml.Syntax.XamlObjectSyntax>(tree.GetRoot()!.Children.Single());
        var editor = new XamlSyntaxEditor(tree);
        editor.ReplaceNode(old, "<New/>");
        var changed = editor.GetChangedTree();
        Assert.Equal("<Page><New/></Page>", changed.GetText().ToString());
        Assert.Single(editor.GetTextChanges());
    }

    [Fact]
    public void FluentGenericXamlParsesUnchanged()
    {
        var repository = FindRepositoryRoot();
        var path = Path.Combine(repository, "external", "microsoft-ui-xaml", "src", "dxaml", "xcp", "dxaml", "themes", "generic.xaml");
        if (!File.Exists(path)) return;
        var source = SourceText.From(File.ReadAllText(path));
        var tree = XamlParser.Parse(source, path);
        Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(source.ToString(), string.Concat(tree.Tokens
            .Where(token => token.Kind != ProGPU.Xaml.Syntax.XamlTokenKind.EndOfFile)
            .Select(token => token.Text)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
