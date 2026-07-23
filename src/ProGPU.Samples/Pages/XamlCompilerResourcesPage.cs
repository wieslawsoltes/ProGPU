using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples;

public partial class XamlCompilerResourcesPage : Page
{
    public XamlCompilerResourcesPage() => InitializeComponent();

    public static FrameworkElement Create() => new XamlCompilerResourcesPage();
}
