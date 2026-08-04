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
    public void HeaderKeepsTitleIntrinsicAndGivesRemainderToSubtitle()
    {
        var header = new Grid();

        MainWindowController.ConfigureHeaderColumns(header);

        Assert.Collection(
            header.ColumnDefinitions,
            column =>
            {
                Assert.Equal(GridUnitType.Absolute, column.Width.UnitType);
                Assert.Equal(45f, column.Width.Value);
            },
            column => Assert.Equal(
                GridUnitType.Auto,
                column.Width.UnitType),
            column => Assert.Equal(
                GridUnitType.Auto,
                column.Width.UnitType),
            column => Assert.Equal(
                GridUnitType.Auto,
                column.Width.UnitType),
            column => Assert.Equal(
                GridUnitType.Star,
                column.Width.UnitType));
    }

    [Fact]
    public void BasicInputIsDefaultWhileExplicitSelectionsStillWin()
    {
        var basic = new NavigationViewItem("Basic Input", "A");
        var mesh = new NavigationViewItem("3D Mesh Viewer", "B");
        var media = new NavigationViewItem("GPU Media Player", "C");
        NavigationViewItem[] pages = [basic, mesh, media];

        Assert.Same(
            basic,
            MainWindowController.ResolveInitialPage(
                pages,
                basic,
                selectedCategory: null,
                requestedPage: null));
        Assert.Same(
            mesh,
            MainWindowController.ResolveInitialPage(
                pages,
                basic,
                selectedCategory: "3d mesh viewer",
                requestedPage: null));
        Assert.Same(
            media,
            MainWindowController.ResolveInitialPage(
                pages,
                basic,
                selectedCategory: "3D Mesh Viewer",
                requestedPage: "gpu media player"));
    }

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
