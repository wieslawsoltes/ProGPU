using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Samples;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public sealed class SampleShellResponsiveTests
{
    [Fact]
    public void HeaderAdaptiveStatesOnlyHideSecondaryContent()
    {
        var header = new Grid();
        var primaryContent = new Border();
        var familySelector = new Border();
        var themeSelector = new Border();
        var subtitle = new Border();
        header.Children.Add(primaryContent);
        header.Children.Add(familySelector);
        header.Children.Add(themeSelector);
        header.Children.Add(subtitle);

        MainWindowController.AttachHeaderWidthStates(
            header,
            familySelector,
            themeSelector,
            subtitle);

        VisualStateManager.UpdateAdaptiveStates(
            header,
            new Vector2(400f, 873f));

        Assert.Equal(Visibility.Visible, primaryContent.Visibility);
        Assert.Equal(Visibility.Collapsed, familySelector.Visibility);
        Assert.Equal(Visibility.Collapsed, themeSelector.Visibility);
        Assert.Equal(Visibility.Collapsed, subtitle.Visibility);

        VisualStateManager.UpdateAdaptiveStates(
            header,
            new Vector2(873f, 400f));

        Assert.Equal(Visibility.Visible, primaryContent.Visibility);
        Assert.Equal(Visibility.Collapsed, familySelector.Visibility);
        Assert.Equal(Visibility.Visible, themeSelector.Visibility);
        Assert.Equal(Visibility.Collapsed, subtitle.Visibility);

        VisualStateManager.UpdateAdaptiveStates(
            header,
            new Vector2(1_280f, 800f));

        Assert.Equal(Visibility.Visible, primaryContent.Visibility);
        Assert.Equal(Visibility.Visible, familySelector.Visibility);
        Assert.Equal(Visibility.Visible, themeSelector.Visibility);
        Assert.Equal(Visibility.Visible, subtitle.Visibility);
    }
}
