using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Fonts.Inter;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Silk.NET.Input;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiSetupNavigationTests : IDisposable
{
    private readonly WindowInputState _previous = InputSystem.Current;

    private static readonly string[] SetupButtons =
    [
        "HmiSetupTags", "HmiSetupConnections", "HmiSetupComponents", "HmiSetupBindings", "HmiSetupHelp"
    ];

    public HmiSetupNavigationTests() => InputSystem.Current = new WindowInputState();

    public void Dispose()
    {
        try
        {
            InputSystem.SetFocus(null);
        }
        finally
        {
            InputSystem.Current = _previous;
        }
    }

    [Theory]
    [InlineData(640f)]
    [InlineData(1600f)]
    public void SetupCommandsAreVisibleInTheExistingWrappingHeader(float width)
    {
        using var host = CreateHost(width);
        var row = Find<WrapPanel>(host, "HmiSetupNavigation");
        Assert.Equal(Orientation.Horizontal, row.Orientation);
        Assert.Equal(Visibility.Visible, row.Visibility);
        Assert.True(row.Size.X > 0);
        Assert.True(row.Size.Y > 0);
        Assert.All(SetupButtons, name =>
        {
            var button = Find<Button>(host, name);
            Assert.Same(row, button.Parent);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.IsEnabled);
            Assert.True(button.Size.X > 0);
            Assert.True(button.Size.Y > 0);
            Assert.NotEmpty(Assert.IsType<TextBlock>(button.Content).Text);
        });
        Assert.Null(host.Runtime);
        Assert.Null(host.ConnectionFactory);
        Assert.Null(host.WriteAuthorizer);
    }

    [Theory]
    [InlineData("HmiSetupTags", "Tags", 640f)]
    [InlineData("HmiSetupConnections", "Connections", 640f)]
    [InlineData("HmiSetupHelp", "Help", 640f)]
    [InlineData("HmiSetupTags", "Tags", 1600f)]
    [InlineData("HmiSetupConnections", "Connections", 1600f)]
    [InlineData("HmiSetupHelp", "Help", 1600f)]
    public void DataShortcutRevealsTheOwnedPageEvenAfterReorderingAndRenaming(string command, string header, float width)
    {
        using var host = CreateHost(width);
        var tabs = Find<Pivot>(host, "HmiProjectDataTabs");
        PivotItem page = DisplacePage(tabs, header);
        object? originalContent = page.Content;
        var area = Assert.IsType<Grid>(tabs.Parent);
        string original = host.Session.ExportJson();
        host.SetDataPanelHeight(140);
        host.SetDataPanelsVisible(false);

        Press(Find<Button>(host, command));
        Layout(host, width);

        Assert.True(host.AreDataPanelsVisible);
        Assert.True(area.Height >= 360);
        Assert.Same(page, tabs.Items[tabs.SelectedIndex]);
        Assert.Same(originalContent, page.Content);
        Assert.Equal(original, host.Session.ExportJson());
        Assert.False(host.Session.IsDirty);
        Assert.False(host.Session.CanUndo);
        Assert.Null(host.Runtime);
    }

    [Theory]
    [InlineData("HmiSetupTags")]
    [InlineData("HmiSetupConnections")]
    [InlineData("HmiSetupHelp")]
    public void DataShortcutPreservesAnAlreadyLargerPanel(string command)
    {
        using var host = CreateHost(1600);
        var area = Assert.IsType<Grid>(Find<Pivot>(host, "HmiProjectDataTabs").Parent);
        host.SetDataPanelHeight(520);
        host.SetDataPanelsVisible(false);
        Press(Find<Button>(host, command));
        Assert.True(host.AreDataPanelsVisible);
        Assert.Equal(520f, area.Height);
    }

    [Theory]
    [InlineData(640f, SplitViewDisplayMode.Overlay)]
    [InlineData(1600f, SplitViewDisplayMode.Inline)]
    public void ComponentAndBindingShortcutsRevealExactPagesWithoutLosingSelection(float width, SplitViewDisplayMode mode)
    {
        using var host = CreateHost(width);
        var library = Find<ResponsiveSplitView>(host, "HmiComponentLibraryPane");
        var inspector = Find<ResponsiveSplitView>(host, "HmiPropertyPane");
        var libraryTabs = Find<Pivot>(host, "HmiComponentLibraryTabs");
        var propertyTabs = Find<Pivot>(host, "HmiPropertyTabs");
        PivotItem components = DisplacePage(libraryTabs, "Components");
        PivotItem properties = DisplacePage(propertyTabs, "HMI");
        HmiControl selected = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().First();
        host.Selection.SelectRange([selected]);
        string before = host.Session.ExportJson();
        Assert.Equal(mode, library.DisplayMode);
        Assert.Equal(mode, inspector.DisplayMode);
        library.IsPaneOpen = false;
        inspector.IsPaneOpen = true;

        Press(Find<Button>(host, "HmiSetupComponents"));
        Layout(host, width);
        Assert.True(library.IsPaneOpen);
        Assert.Equal(mode == SplitViewDisplayMode.Inline, inspector.IsPaneOpen);
        Assert.Same(components, libraryTabs.Items[libraryTabs.SelectedIndex]);

        Press(Find<Button>(host, "HmiSetupBindings"));
        Layout(host, width);
        Assert.True(inspector.IsPaneOpen);
        Assert.Equal(mode == SplitViewDisplayMode.Inline, library.IsPaneOpen);
        Assert.Same(properties, propertyTabs.Items[propertyTabs.SelectedIndex]);
        Assert.Same(selected, Assert.Single(host.Selection.Selection));
        Assert.Equal(before, host.Session.ExportJson());
        Assert.False(host.Session.CanUndo);
    }

    [Fact]
    public void NavigationPreservesDirtyDocumentSelectionAndBothHistoryBranches()
    {
        using var host = CreateHost(1600);
        string original = host.Session.ExportJson();
        host.Session.Edit("First edit", project => project.Name = "Unsaved setup");
        string firstEdit = host.Session.ExportJson();
        host.Session.Edit("Second edit", project => project.Name = "Redo target");
        string secondEdit = host.Session.ExportJson();
        host.Session.Undo();
        HmiControl selected = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().First();
        host.Selection.SelectRange([selected]);
        var controls = host.WorkspaceCanvas.DesignSurface.Children.ToArray();

        foreach (string command in SetupButtons)
        {
            Press(Find<Button>(host, command));
            Assert.Equal(firstEdit, host.Session.ExportJson());
            Assert.True(host.Session.IsDirty);
            Assert.True(host.Session.CanUndo);
            Assert.True(host.Session.CanRedo);
            Assert.Same(selected, Assert.Single(host.Selection.Selection));
            Assert.Equal(controls, host.WorkspaceCanvas.DesignSurface.Children.ToArray());
        }

        host.Session.Redo();
        Assert.Equal(secondEdit, host.Session.ExportJson());
        host.Session.Undo();
        Assert.Equal(firstEdit, host.Session.ExportJson());
        host.Session.Undo();
        Assert.Equal(original, host.Session.ExportJson());
        Assert.False(host.Session.IsDirty);
        Assert.False(host.Session.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewNavigationRetainsRuntimeAndCannotConnectOrAuthorizeWrites(bool allowLocalWrites)
    {
        using var host = CreateHost(640);
        host.Session.Edit("Unsaved setup", project => project.Name = "Navigation during preview");
        string original = host.Session.ExportJson();
        int connections = 0, authorizations = 0;
        Func<HmiConnectionProfile, IHmiConnection> factory = _ =>
        {
            connections++;
            throw new InvalidOperationException("Navigation must not create equipment transports.");
        };
        HmiWriteAuthorizer authorizer = (_, _) =>
        {
            authorizations++;
            return ValueTask.FromResult(false);
        };
        host.ConnectionFactory = factory;
        host.WriteAuthorizer = authorizer;
        host.StartPreview(allowLocalWrites, automaticTicks: false);
        HmiRuntime runtime = Assert.IsType<HmiRuntime>(host.Runtime);
        host.AdvancePreview(TimeSpan.FromSeconds(1));
        DateTimeOffset now = runtime.Now;
        HmiTagSample sample = runtime.Read("Pump.Running");
        foreach (string command in SetupButtons)
        {
            Press(Find<Button>(host, command));
            Assert.True(host.IsPreviewing);
            Assert.Same(runtime, host.Runtime);
            Assert.True(runtime.IsRunning);
            Assert.Equal(allowLocalWrites, runtime.AllowLocalWrites);
            Assert.Equal(now, runtime.Now);
            Assert.Equal(sample, runtime.Read("Pump.Running"));
            Assert.Same(factory, host.ConnectionFactory);
            Assert.Same(authorizer, host.WriteAuthorizer);
            Assert.Null(host.ConnectionDiagnostics);
            Assert.Equal(0, connections);
            Assert.Equal(0, authorizations);
            Assert.Equal(original, host.Session.ExportJson());
            Assert.True(host.Session.IsDirty);
        }

        // The revealed table's existing edit guard still rejects a real click
        // during preview; navigation is not permission to edit live design data.
        Press(Find<Button>(host, "HmiSetupTags"));
        PivotItem tags = Find<Pivot>(host, "HmiProjectDataTabs").Items.Single(item => Equals(item.Header, "Tags"));
        var addTag = Assert.Single(Descendants(tags).OfType<Button>(),
            button => button.Content is TextBlock label && label.Text == "Add tag");
        string? error = null;
        host.Error += message => error = message;
        Press(addTag);
        Assert.Equal("Stop simulation before changing the design.", error);
        Assert.Equal(original, host.Session.ExportJson());
        Assert.Same(runtime, host.Runtime);
        Assert.True(runtime.IsRunning);
    }

    private static HmiDesignerHost CreateHost(float width)
    {
        var host = new HmiDesignerHost(null, InterFontFamily.Regular);
        Layout(host, width);
        return host;
    }

    private static void Layout(HmiDesignerHost host, float width)
    {
        host.Measure(new Vector2(width, 1000));
        host.Arrange(new Rect(0, 0, width, 1000));
    }

    private static PivotItem DisplacePage(Pivot tabs, string header)
    {
        PivotItem page = Assert.Single(tabs.Items, item => Equals(item.Header, header));
        tabs.Items.Move(tabs.Items.IndexOf(page), tabs.Items.Count - 1);
        page.Header = "Renamed owned page";
        tabs.Items.Insert(0, new PivotItem(header, new Grid()));
        tabs.SelectedIndex = 0;
        return page;
    }

    private static void Press(Button button)
    {
        Assert.True(button.IsEnabled);
        var input = new KeyRoutedEventArgs { Key = Key.Enter };
        button.OnKeyDown(input);
        Assert.True(input.Handled);
    }

    private static T Find<T>(Visual root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants(root).OfType<T>(), element => element.Name == name);

    private static IEnumerable<Visual> Descendants(Visual root)
    {
        yield return root;
        if (root is ContainerVisual container)
            foreach (Visual child in container.Children)
                foreach (Visual descendant in Descendants(child))
                    yield return descendant;
    }
}
