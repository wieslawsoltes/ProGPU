using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Samples;

public static class XamlPlaygroundPage
{
    private const string InitialSource = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StackPanel Margin="24">
    <TextBlock Text="Hello from the XAML playground" />
  </StackPanel>
</Page>
""";

    public static FrameworkElement Create()
    {
        var root = new StackPanel { Margin = new Thickness(20), Orientation = Orientation.Vertical };
        root.Children.Add(new TextBlock { FontSize = 22, Text = "XAML Playground" });
        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 12),
            Text = "Edit source and inspect the same lossless syntax and schema-neutral infoset used by builds and the CLI."
        });
        var editor = new TextBox
        {
            Text = InitialSource,
            AcceptsReturn = true,
            Height = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0), Text = "Ready." };
        var pipeline = new TextBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            AcceptsReturn = true,
            Height = 150,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Text = "Syntax and infoset details will appear here."
        };
        var inspect = new Button { Margin = new Thickness(0, 12, 0, 0), Content = "Parse and inspect" };
        inspect.Click += (_, _) =>
        {
            var source = SourceText.From(editor.Text);
            var tree = XamlParser.Parse(source, "Playground.xaml",
                new XamlParseOptions { Mode = XamlParseMode.Recovering });
            var errors = tree.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            var objects = tree.GetRoot() == null
                ? 0
                : 1 + tree.GetRoot()!.DescendantNodes().Count(node => node is XamlObjectSyntax);
            var infoset = new XamlInfosetConverter().Convert(
                tree.Document,
                new XamlInfosetConversionOptions { Mode = XamlParseMode.Recovering });
            var info = CountInfoset(infoset.Root);
            status.Text = $"Root: {tree.GetRoot()?.QualifiedName ?? "<none>"}; tokens: {tree.Tokens.Length}; objects: {objects}; errors: {errors}.";
            pipeline.Text =
                $"syntax.root = {tree.GetRoot()?.QualifiedName ?? "<none>"}\n" +
                $"infoset.root = {infoset.Root?.TypeName.ToString() ?? "<none>"}\n" +
                $"infoset.objects = {info.Objects}\n" +
                $"infoset.members = {info.Members}\n" +
                $"infoset.textValues = {info.TextValues}\n" +
                $"infoset.diagnostics = {infoset.Diagnostics.Length}";
        };
        root.Children.Add(editor);
        root.Children.Add(inspect);
        root.Children.Add(status);
        root.Children.Add(pipeline);
        return root;
    }

    private static (int Objects, int Members, int TextValues) CountInfoset(XamlInfosetObject? root)
    {
        if (root == null) return default;
        var objects = 1;
        var members = root.Members.Length;
        var textValues = 0;
        foreach (var member in root.Members)
        {
            foreach (var value in member.Values)
            {
                if (value is XamlInfosetObject child)
                {
                    var nested = CountInfoset(child);
                    objects += nested.Objects;
                    members += nested.Members;
                    textValues += nested.TextValues;
                }
                else if (value is XamlInfosetText)
                {
                    textValues++;
                }
            }
        }
        return (objects, members, textValues);
    }
}
