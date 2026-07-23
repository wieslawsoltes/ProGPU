using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples;

public partial class XamlCompilerWelcomePage : Page
{
    public XamlCompilerWelcomePage() => InitializeComponent();
    public static FrameworkElement Create() => new XamlCompilerWelcomePage();
    public string CompilerMessage =>
        "This text is read through a Roslyn-symbol-bound, reflection-free x:Bind path.";
    public XamlCompilerPreviewItem PreviewItem { get; } =
        new("DataTemplate x:DataType supplies this compiled-binding source.");

    private void OnActionClick(object? sender, EventArgs e) =>
        StatusText.Text = "Generated event hookup executed.";
}

public sealed record XamlCompilerPreviewItem(string Title);
