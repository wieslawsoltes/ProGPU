using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Tooling;

namespace ProGPU.Samples;

public static class XamlPlaygroundPage
{
    private const string InitialSource = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="ProGPU.Samples.Playground.Document">
  <StackPanel Margin="24">
    <TextBlock Text="Hello from the XAML playground" />
  </StackPanel>
</Page>
""";

    private static readonly Lazy<PlaygroundCompilationHost> CompilationHost =
        new(CreateCompilationHost);

    public static FrameworkElement Create()
    {
        var root = new StackPanel { Margin = new Thickness(20), Orientation = Orientation.Vertical };
        root.Children.Add(new TextBlock { FontSize = 22, Text = "XAML Playground" });
        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 12),
            Text = "Edit source and inspect bounded projections of the same lossless syntax and schema-neutral infoset used by builds and the CLI."
        });
        var editor = new TextBox
        {
            Text = InitialSource,
            AcceptsReturn = true,
            Height = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0), Text = "Ready." };
        var syntaxOutput = CreateOutput("Syntax details will appear here.");
        var tokenOutput = CreateOutput("Lossless tokens will appear here.");
        var infosetOutput = CreateOutput("Infoset details will appear here.");
        var boundOutput = CreateOutput("Bound semantic details will appear here.");
        var resourcesOutput = CreateOutput("Resource graph details will appear here.");
        var irOutput = CreateOutput("Construction IR details will appear here.");
        var generatedOutput = CreateOutput("Generated Roslyn C# will appear here.");
        var diagnosticsOutput = CreateOutput("Diagnostics will appear here.");
        var views = new Pivot { Margin = new Thickness(0, 8, 0, 0), Height = 300 };
        views.Items.Add(new PivotItem("Syntax", syntaxOutput));
        views.Items.Add(new PivotItem("Tokens", tokenOutput));
        views.Items.Add(new PivotItem("Infoset", infosetOutput));
        views.Items.Add(new PivotItem("Bound", boundOutput));
        views.Items.Add(new PivotItem("Resources", resourcesOutput));
        views.Items.Add(new PivotItem("IR", irOutput));
        views.Items.Add(new PivotItem("Generated C#", generatedOutput));
        views.Items.Add(new PivotItem("Diagnostics", diagnosticsOutput));
        var inspect = new Button { Margin = new Thickness(0, 12, 0, 0), Content = "Parse and inspect" };
        inspect.Click += (_, _) =>
        {
            var source = SourceText.From(editor.Text);
            var inspection = new XamlDocumentInspectionService().Inspect(
                source,
                "Playground.xaml",
                new XamlDocumentInspectionOptions
                {
                    ParseOptions = new XamlParseOptions
                    {
                        Mode = XamlParseMode.Recovering
                    }
                });
            var statistics = inspection.Statistics;
            status.Text =
                $"Root: {inspection.SyntaxTree.GetRoot()?.QualifiedName ?? "<none>"}; " +
                $"tokens: {statistics.Tokens}; syntax objects: {statistics.SyntaxObjects}; " +
                $"infoset objects: {statistics.InfosetObjects}; errors: {statistics.Errors}.";
            syntaxOutput.Text = Render(inspection.Syntax);
            tokenOutput.Text = Render(inspection.Tokens);
            infosetOutput.Text = Render(inspection.InfosetProjection);
            var compilationHost = CompilationHost.Value;
            if (compilationHost.Compilation == null)
            {
                var unavailable = "Compilation-backed inspection is unavailable: " +
                    compilationHost.Error;
                boundOutput.Text = unavailable;
                resourcesOutput.Text = unavailable;
                irOutput.Text = unavailable;
                generatedOutput.Text = unavailable;
                diagnosticsOutput.Text = inspection.Diagnostics.TotalEntryCount == 0
                    ? unavailable
                    : Render(inspection.Diagnostics) + Environment.NewLine + unavailable;
                return;
            }

            var profile = new WinUiXamlProfile();
            var compiled = new RoslynXamlCompilationInspectionService().Inspect(
                inspection,
                new RoslynXamlTypeSystem(compilationHost.Compilation, profile),
                profile,
                new RoslynXamlCompilationInspectionOptions
                {
                    CompilerOptions = new XamlCompilerOptions
                    {
                        Framework = profile.Id,
                        ResourceUri = "Playground.xaml",
                        Strict = false
                    }
                });
            boundOutput.Text = Render(compiled.Bound);
            resourcesOutput.Text = Render(compiled.Resources);
            irOutput.Text = Render(compiled.Ir);
            generatedOutput.Text = RenderGenerated(compiled);
            diagnosticsOutput.Text = compiled.Diagnostics.TotalEntryCount == 0
                ? "No diagnostics."
                : Render(compiled.Diagnostics);
        };
        root.Children.Add(editor);
        root.Children.Add(inspect);
        root.Children.Add(status);
        root.Children.Add(views);
        return root;
    }

    private static TextBox CreateOutput(string text) => new TextBox
    {
        AcceptsReturn = true,
        Height = 250,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Text = text
    };

    private static string Render(XamlInspectionProjection projection)
    {
        var builder = new StringBuilder();
        foreach (var entry in projection.Entries)
        {
            builder.Append(' ', entry.Depth * 2);
            builder.Append(entry.Kind);
            builder.Append(' ');
            builder.Append(entry.Name);
            if (entry.Value.Length != 0)
                builder.Append(" = ").Append(entry.Value);
            builder.Append(" [").Append(entry.SourceSpan.Start)
                .Append("..").Append(entry.SourceSpan.End).Append(']');
            if (entry.HasStableId)
                builder.Append(" #").Append(entry.StableId!.Value.ToString("x16"));
            builder.AppendLine();
        }
        if (projection.IsTruncated)
            builder.Append("… ").Append(projection.TotalEntryCount - projection.Entries.Length)
                .AppendLine(" more entries omitted by the inspection bound.");
        return builder.ToString();
    }

    private static string RenderGenerated(
        RoslynXamlCompilationInspection inspection)
    {
        if (inspection.CompilationResult.Sources.Count == 0)
            return "No C# was generated. Inspect Diagnostics for the blocking stage.";
        var builder = new StringBuilder();
        foreach (var source in inspection.CompilationResult.Sources)
        {
            builder.Append("// ").AppendLine(source.HintName);
            builder.AppendLine(source.GeneratedSyntaxTree?
                .GetRoot()
                .ToFullString() ?? source.Source);
        }
        return builder.ToString();
    }

    private static PlaygroundCompilationHost CreateCompilationHost()
    {
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var trusted =
                (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ??
                string.Empty;
            foreach (var path in trusted.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(path)) paths.Add(path);
            }
            AddAssemblyPath(paths, typeof(Page).Assembly.Location);
            if (paths.Count == 0)
                return new PlaygroundCompilationHost(
                    null,
                    "this runtime does not expose trusted metadata reference paths.");

            var documentType = SyntaxFactory.ClassDeclaration("Document")
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword))
                .WithBaseList(SyntaxFactory.BaseList(
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                        SyntaxFactory.SimpleBaseType(BuildName(
                            "Microsoft",
                            "UI",
                            "Xaml",
                            "Controls",
                            "Page")))));
            var unit = SyntaxFactory.CompilationUnit().AddMembers(
                SyntaxFactory.NamespaceDeclaration(BuildName(
                        "ProGPU",
                        "Samples",
                        "Playground"))
                    .AddMembers(documentType));
            var compilation = CSharpCompilation.Create(
                "ProGPU.Xaml.Playground",
                new[] { CSharpSyntaxTree.Create(unit) },
                paths.OrderBy(static path => path, StringComparer.Ordinal)
                    .Select(static path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));
            return new PlaygroundCompilationHost(compilation, null);
        }
        catch (Exception exception)
        {
            return new PlaygroundCompilationHost(null, exception.Message);
        }
    }

    private static void AddAssemblyPath(
        ISet<string> paths,
        string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            paths.Add(path);
    }

    private static NameSyntax BuildName(
        string first,
        params string[] remaining)
    {
        NameSyntax result = SyntaxFactory.IdentifierName(first);
        foreach (var part in remaining)
            result = SyntaxFactory.QualifiedName(
                result,
                SyntaxFactory.IdentifierName(part));
        return result;
    }

    private sealed class PlaygroundCompilationHost
    {
        public PlaygroundCompilationHost(
            CSharpCompilation? compilation,
            string? error)
        {
            Compilation = compilation;
            Error = error;
        }

        public CSharpCompilation? Compilation { get; }
        public string? Error { get; }
    }
}
