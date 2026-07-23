using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples;

public partial class XamlCompilerLayoutPage : Page
{
    public XamlCompilerLayoutPage() => InitializeComponent();
    public static FrameworkElement Create() => new XamlCompilerLayoutPage();
}
