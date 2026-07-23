using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples;

public partial class XamlCompilerMarkupPage : Page
{
    public XamlCompilerMarkupPage() => InitializeComponent();
    public static FrameworkElement Create() => new XamlCompilerMarkupPage();
}
