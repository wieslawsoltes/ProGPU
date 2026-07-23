using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.HotReload;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Designer;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Workspaces;
using Xunit;

namespace ProGPU.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HotReloadCollection
{
    public const string Name = "HotReload";
}

[Collection(HotReloadCollection.Name)]
public sealed class HotReloadTests : IDisposable
{
    public HotReloadTests()
    {
        HotReloadManager.IsEnabled = true;
        DrainDispatcher();
        InputSystem.SetFocus(null);
    }

    [Fact]
    public void WinUiAssemblyRegistersStandardMetadataUpdateHandler()
    {
        var attribute = Assert.Single(
            typeof(FrameworkElement).Assembly
                .GetCustomAttributes(typeof(MetadataUpdateHandlerAttribute), inherit: false)
                .Cast<MetadataUpdateHandlerAttribute>());

        Assert.Equal(typeof(HotReloadManager), attribute.HandlerType);
        Assert.NotNull(typeof(HotReloadManager).GetMethod(nameof(HotReloadManager.ClearCache)));
        Assert.NotNull(typeof(HotReloadManager).GetMethod(nameof(HotReloadManager.UpdateApplication)));
    }

    [Fact]
    public void ReplacesUpdatedElementAndPreservesInteractiveStateFocusAndAttachedLayout()
    {
        var oldElement = new FactoryElement("old")
        {
            Name = "card",
            DataContext = new object(),
            CustomValue = 41
        };
        oldElement.Editor.Text = "user edited text";
        oldElement.Editor.CaretIndex = 4;
        oldElement.Editor.SelectionStart = 2;
        oldElement.Editor.SelectionLength = 3;
        oldElement.Toggle.IsChecked = true;
        Grid.SetRow(oldElement, 3);

        var root = new Grid();
        root.AddChild(oldElement);
        InputSystem.SetFocus(oldElement.Editor);

        using var rootRegistration = HotReloadManager.RegisterRoot(root);
        using var factoryRegistration = HotReloadManager.RegisterFactory(() => new FactoryElement("new"));

        HotReloadManager.RequestUpdate(typeof(FactoryElement));
        UIThread.RunPending();

        var replacement = Assert.IsType<FactoryElement>(Assert.Single(root.Children));
        Assert.NotSame(oldElement, replacement);
        Assert.Equal("new", replacement.Marker);
        Assert.Same(oldElement.DataContext, replacement.DataContext);
        Assert.Equal("user edited text", replacement.Editor.Text);
        Assert.Equal(4, replacement.Editor.CaretIndex);
        Assert.Equal(2, replacement.Editor.SelectionStart);
        Assert.Equal(3, replacement.Editor.SelectionLength);
        Assert.True(replacement.Toggle.IsChecked);
        Assert.Equal(41, replacement.CustomValue);
        Assert.Equal(3, Grid.GetRow(replacement));
        Assert.Equal(1, oldElement.UnloadedCount);
        Assert.Equal(1, replacement.LoadingCount);

        UIThread.RunPending();
        Assert.Same(replacement.Editor, InputSystem.FocusedElement);
        Assert.Equal(1, HotReloadManager.LastResult.ReplacedElements);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void ReplaceableEmbeddedRootCanRecreateTheRegisteredRoot()
    {
        FrameworkElement root = new EmbeddedRootOriginal { Marker = "old" };
        var version = 0;
        using var rootRegistration = HotReloadManager.RegisterRoot(
            root,
            replacement => root = replacement);
        using var factoryRegistration = HotReloadManager.RegisterFactory(
            () => new EmbeddedRootOriginal { Marker = $"new-{++version}" });

        HotReloadManager.RequestUpdate(typeof(EmbeddedRootOriginal));
        UIThread.RunPending();

        var firstReplacement = Assert.IsType<EmbeddedRootOriginal>(root);
        Assert.Equal("new-1", firstReplacement.Marker);
        Assert.Equal(1, HotReloadManager.LastResult.ReplacedElements);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);

        HotReloadManager.RequestUpdate(typeof(EmbeddedRootOriginal));
        UIThread.RunPending();

        var secondReplacement = Assert.IsType<EmbeddedRootOriginal>(root);
        Assert.NotSame(firstReplacement, secondReplacement);
        Assert.Equal("new-2", secondReplacement.Marker);
        Assert.Equal(1, HotReloadManager.LastResult.ReplacedElements);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void RuntimeReplacementTypeMapsBackToTheOriginalLiveType()
    {
        var oldElement = new MappedOriginalElement();
        var root = new Grid();
        root.AddChild(oldElement);
        using var rootRegistration = HotReloadManager.RegisterRoot(root);
        using var factoryRegistration = HotReloadManager.RegisterFactory(
            () => new MappedReplacementElement());

        HotReloadManager.RequestUpdate(typeof(MappedReplacementElement));
        UIThread.RunPending();

        Assert.IsType<MappedReplacementElement>(Assert.Single(root.Children));
        Assert.Contains(typeof(MappedReplacementElement), HotReloadManager.LastResult.UpdatedTypes);
        Assert.Equal(1, HotReloadManager.LastResult.ReplacedElements);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void ReloadableElementRebuildsInPlace()
    {
        var element = new InPlaceElement();
        var root = new Grid();
        root.AddChild(element);
        using var rootRegistration = HotReloadManager.RegisterRoot(root);

        HotReloadManager.RequestUpdate(typeof(InPlaceElement));
        UIThread.RunPending();

        Assert.Same(element, Assert.Single(root.Children));
        Assert.Equal(1, element.ReloadCount);
        Assert.Equal(1, HotReloadManager.LastResult.ReloadedElements);
        Assert.Equal(0, HotReloadManager.LastResult.ReplacedElements);
    }

    [Fact]
    public void FailedInPlaceReloadRestoresCapturedStateAndFocus()
    {
        var element = new FailingInPlaceElement();
        element.Editor.Text = "user value";
        element.Editor.CaretIndex = 3;
        var root = new Grid();
        root.AddChild(element);
        InputSystem.SetFocus(element.Editor);
        using var rootRegistration = HotReloadManager.RegisterRoot(root);

        HotReloadManager.RequestUpdate(typeof(FailingInPlaceElement));
        UIThread.RunPending();

        Assert.Same(element, Assert.Single(root.Children));
        Assert.Equal("user value", element.Editor.Text);
        Assert.Equal(3, element.Editor.CaretIndex);
        Assert.Same(element.Editor, InputSystem.FocusedElement);
        Assert.Equal(0, HotReloadManager.LastResult.ReloadedElements);
        Assert.Equal(1, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void StaticNavigationPageFactoryIsReinvokedWithoutLosingPageState()
    {
        FactoryPageOwner.Version = 1;
        var navigation = new NavigationView();
        var item = new NavigationViewItem("Factory page", "", FactoryPageOwner.Create);
        navigation.MenuItems.Add(item);
        navigation.SelectedItem = item;

        var oldPage = Assert.IsType<Grid>(navigation.Content);
        var oldEditor = Assert.IsType<TextBox>(FindByName(oldPage, "editor"));
        oldEditor.Text = "keep me";

        var root = new Grid();
        root.AddChild(navigation);
        using var rootRegistration = HotReloadManager.RegisterRoot(root);

        FactoryPageOwner.Version = 2;
        HotReloadManager.RequestUpdate(typeof(FactoryPageOwner));
        UIThread.RunPending();

        var newPage = Assert.IsType<Grid>(navigation.Content);
        Assert.NotSame(oldPage, newPage);
        Assert.Equal("version 2", Assert.IsType<TextBlock>(FindByName(newPage, "version")).Text);
        Assert.Equal("keep me", Assert.IsType<TextBox>(FindByName(newPage, "editor")).Text);
        Assert.Same(item, navigation.SelectedItem);
        Assert.Equal(1, HotReloadManager.LastResult.RefreshedFactories);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void ActivationFailureRetainsExistingTreeAndReportsFailure()
    {
        var element = new RequiredConstructorElement("existing");
        var root = new Grid();
        root.AddChild(element);
        using var rootRegistration = HotReloadManager.RegisterRoot(root);

        HotReloadManager.RequestUpdate(typeof(RequiredConstructorElement));
        UIThread.RunPending();

        Assert.Same(element, Assert.Single(root.Children));
        Assert.True(element.IsDirty);
        Assert.Equal(1, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void RuntimeCallbacksCoalesceBeforeTheNextUiTurn()
    {
        var element = new InPlaceElement();
        var root = new Grid();
        root.AddChild(element);
        using var rootRegistration = HotReloadManager.RegisterRoot(root);
        var completions = 0;
        void OnCompleted(HotReloadResult _) => completions++;
        HotReloadManager.UpdateCompleted += OnCompleted;
        try
        {
            HotReloadManager.ClearCache([typeof(InPlaceElement)]);
            HotReloadManager.UpdateApplication([typeof(InPlaceElement)]);
            HotReloadManager.UpdateApplication([typeof(InPlaceElement)]);

            UIThread.RunPending();

            Assert.Equal(1, completions);
            Assert.Equal(1, element.ReloadCount);
            Assert.Contains(typeof(InPlaceElement), HotReloadManager.LastResult.UpdatedTypes);
        }
        finally
        {
            HotReloadManager.UpdateCompleted -= OnCompleted;
        }
    }

    [Fact]
    public void UnrelatedRuntimeCacheClearDoesNotRecursivelyRefreshThemes()
    {
        var updatedElement = new InPlaceElement();
        var unaffectedSibling = new Border();
        var root = new Grid();
        root.AddChild(updatedElement);
        root.AddChild(unaffectedSibling);
        root.IsThemeDirty = false;
        updatedElement.IsThemeDirty = false;
        unaffectedSibling.IsThemeDirty = false;
        using var rootRegistration = HotReloadManager.RegisterRoot(root);

        HotReloadManager.ClearCache([typeof(InPlaceElement)]);
        HotReloadManager.UpdateApplication([typeof(InPlaceElement)]);
        UIThread.RunPending();

        Assert.False(root.IsThemeDirty);
        Assert.False(unaffectedSibling.IsThemeDirty);
        Assert.False(updatedElement.IsThemeDirty);
        Assert.Equal(1, updatedElement.ReloadCount);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void ThemeInfrastructureUpdateRefreshesTheRegisteredTree()
    {
        var child = new Border();
        var root = new Grid();
        root.AddChild(child);
        root.IsThemeDirty = false;
        child.IsThemeDirty = false;
        using var rootRegistration = HotReloadManager.RegisterRoot(root);

        HotReloadManager.RequestUpdate(typeof(ThemeManager));
        UIThread.RunPending();

        Assert.True(root.IsThemeDirty);
        Assert.True(child.IsThemeDirty);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void NullRuntimeTypeListPerformsConservativeAllTypesRefresh()
    {
        var element = new InPlaceElement();
        var root = new Grid();
        root.AddChild(element);
        using var rootRegistration = HotReloadManager.RegisterRoot(root);

        HotReloadManager.ClearCache(null);
        HotReloadManager.UpdateApplication(null);
        UIThread.RunPending();

        Assert.Equal(1, element.ReloadCount);
        Assert.Empty(HotReloadManager.LastResult.UpdatedTypes);
        Assert.Equal(1, HotReloadManager.LastResult.ReloadedElements);
        Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
    }

    [Fact]
    public void WindowShellReloadContributesToGenerationDiagnostics()
    {
        var window = new Window();
        var oldEditor = new TextBox { Name = "editor", Text = "preserved" };
        var oldRoot = new Grid();
        oldRoot.AddChild(oldEditor);
        window.Content = oldRoot;
        WindowManager.Register(window);

        using var handler = HotReloadManager.RegisterUpdateHandler(context =>
        {
            if (!context.IsTypeUpdated(typeof(FactoryPageOwner)))
            {
                return;
            }

            HotReloadManager.ReloadWindowContent(window, () =>
            {
                var root = new Grid();
                root.AddChild(new TextBox { Name = "editor", Text = "default" });
                return root;
            });
        });

        try
        {
            HotReloadManager.RequestUpdate(typeof(FactoryPageOwner));
            UIThread.RunPending();

            var newRoot = Assert.IsType<Grid>(window.Content);
            Assert.NotSame(oldRoot, newRoot);
            Assert.Equal("preserved", Assert.IsType<TextBox>(FindByName(newRoot, "editor")).Text);
            Assert.Equal(1, HotReloadManager.LastResult.ReplacedElements);
            Assert.Equal(0, HotReloadManager.LastResult.FailedElements);
        }
        finally
        {
            WindowManager.Unregister(window);
        }
    }

    [Fact]
    public void EmbeddedReloadUsesCanonicalStateTransferAndRetainsOldTreeOnFactoryFailure()
    {
        FrameworkElement content = new Grid();
        var oldRoot = (Grid)content;
        oldRoot.AddChild(new TextBox
        {
            Name = "editor",
            Text = "preserved"
        });

        var replaced = HotReloadManager.ReloadElement(
            oldRoot,
            () =>
            {
                var replacement = new Grid();
                replacement.AddChild(new TextBox
                {
                    Name = "editor",
                    Text = "default"
                });
                return replacement;
            },
            replacement => content = replacement);

        Assert.True(replaced);
        var replacementRoot = Assert.IsType<Grid>(content);
        Assert.NotSame(oldRoot, replacementRoot);
        Assert.Equal(
            "preserved",
            Assert.IsType<TextBox>(
                FindByName(replacementRoot, "editor")).Text);

        replaced = HotReloadManager.ReloadElement(
            replacementRoot,
            () => throw new InvalidOperationException("activation failed"),
            replacement => content = replacement);

        Assert.False(replaced);
        Assert.Same(replacementRoot, content);
    }

    [Fact]
    public void LivePreviewSessionTransfersStateAndRetainsLastGoodAssembly()
    {
        Assert.True(WinUiXamlLivePreviewSession.IsRuntimeSupported);
        var image = File.ReadAllBytes(
            typeof(HotReloadTests).Assembly.Location);
        FrameworkElement? published = null;
        using var session = new WinUiXamlLivePreviewSession();

        var first = session.TryUpdate(
            image,
            typeof(PreviewRoot).FullName!,
            replacement => published = replacement);

        Assert.True(first.Success, first.Message);
        var firstRoot = Assert.IsAssignableFrom<FrameworkElement>(published);
        var firstEditor = Assert.IsType<TextBox>(
            FindByName(firstRoot, "editor"));
        firstEditor.Text = "user value";

        var second = session.TryUpdate(
            image,
            typeof(PreviewRoot).FullName!,
            replacement => published = replacement);

        Assert.True(second.Success, second.Message);
        var secondRoot = Assert.IsAssignableFrom<FrameworkElement>(published);
        Assert.NotSame(firstRoot, secondRoot);
        Assert.Equal(
            "user value",
            Assert.IsType<TextBox>(
                FindByName(secondRoot, "editor")).Text);

        var failed = session.TryUpdate(
            image,
            "ProGPU.Tests.MissingPreviewRoot",
            replacement => published = replacement);

        Assert.False(failed.Success);
        Assert.Same(secondRoot, published);
        Assert.Same(secondRoot, session.CurrentRoot);

        published = null;
        session.Reset();
    }

    [Fact]
    public async Task LivePreviewSessionAppliesAcceptedProjectDeltaAfterMetadataCoordination()
    {
        const string baselineXaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="DeltaPreview.Root">
  <TextBox x:Name="editor" Text="baseline" />
</Grid>
""";
        const string changedXaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="DeltaPreview.Root">
  <TextBox x:Name="editor" Text="changed" />
</Grid>
""";
        using var fixture =
            CreateDeltaPreviewProject(baselineXaml);
        var coordinator =
            CreateDeltaPreviewCoordinator();
        var baselineUpdate =
            await coordinator.PrepareAsync(
                fixture.Project,
                fixture.XamlDocumentId);
        var changedProject = fixture.Project.Solution
            .WithAdditionalDocumentText(
                fixture.XamlDocumentId,
                SourceText.From(changedXaml),
                PreservationMode.PreserveIdentity)
            .GetProject(fixture.Project.Id)!;
        FrameworkElement? published = null;
        using var session =
            new WinUiXamlLivePreviewSession();
        var initial =
            await session.ApplyProjectUpdateAsync(
                coordinator,
                baselineUpdate,
                replacement =>
                    published = replacement);
        Assert.Equal(
            RoslynXamlProjectCommitResult.Accepted,
            initial);
        var originalRoot =
            Assert.IsAssignableFrom<FrameworkElement>(
                published);
        Assert.IsType<TextBox>(
                FindByName(originalRoot, "editor"))
            .Text = "user state";

        var changedUpdate =
            await coordinator.PrepareAsync(
                changedProject,
                fixture.XamlDocumentId);
        var xamlPlan = changedUpdate.Delta!;
        Assert.Equal(
            RoslynXamlReloadAction.ReplaceTarget,
            xamlPlan.Action);
        var applied =
            await session.ApplyProjectUpdateAsync(
                coordinator,
                changedUpdate,
                replacement =>
                    published = replacement);
        Assert.Equal(
            RoslynXamlProjectCommitResult.Accepted,
            applied);
        var xamlReplacement =
            Assert.IsAssignableFrom<FrameworkElement>(
                published);
        Assert.NotSame(originalRoot, xamlReplacement);
        Assert.Equal(
            "user state",
            Assert.IsType<TextBox>(
                    FindByName(
                        xamlReplacement,
                        "editor"))
                .Text);

        var codeDocument = changedProject.GetDocument(
            fixture.CodeDocumentId)!;
        var code = await codeDocument.GetTextAsync();
        var metadataProject = changedProject.Solution
            .WithDocumentText(
                codeDocument.Id,
                code.WithChanges(
                    new TextChange(
                        new TextSpan(code.Length, 0),
                        Environment.NewLine +
                        "public sealed class MetadataMarker { }" +
                        Environment.NewLine)))
            .GetProject(changedProject.Id)!;
        var metadataUpdate =
            await coordinator.PrepareAsync(
                metadataProject,
                fixture.XamlDocumentId);
        var metadataPlan = metadataUpdate.Delta!;
        Assert.Equal(
            RoslynXamlReloadAction
                .CoordinateMetadataAndReplaceTarget,
            metadataPlan.Action);

        var missingCoordinator =
            await session.ApplyProjectUpdateAsync(
                coordinator,
                metadataUpdate,
                replacement => published = replacement);
        Assert.Equal(
            RoslynXamlProjectCommitResult
                .RejectedPublication,
            missingCoordinator);
        Assert.Same(xamlReplacement, published);

        var coordinatorCalled = false;
        var failedCoordinator =
            await session.ApplyProjectUpdateAsync(
                coordinator,
                metadataUpdate,
                replacement => published = replacement,
                () =>
                {
                    coordinatorCalled = true;
                    throw new InvalidOperationException(
                        "metadata rejected");
                });
        Assert.True(coordinatorCalled);
        Assert.Equal(
            RoslynXamlProjectCommitResult
                .RejectedPublication,
            failedCoordinator);
        Assert.Same(xamlReplacement, published);

        var successfulCoordinationCount = 0;
        var metadataApplied =
            await session.ApplyProjectUpdateAsync(
                coordinator,
                metadataUpdate,
                replacement => published = replacement,
                () =>
                    successfulCoordinationCount++);
        Assert.Equal(
            RoslynXamlProjectCommitResult.Accepted,
            metadataApplied);
        Assert.Equal(1, successfulCoordinationCount);
        Assert.Equal(3, coordinator.Generation);
        Assert.NotSame(xamlReplacement, published);
        Assert.Equal(
            "user state",
            Assert.IsType<TextBox>(
                    FindByName(
                        Assert.IsAssignableFrom<
                            FrameworkElement>(published),
                        "editor"))
                .Text);

        published = null;
        session.Reset();
    }

    public void Dispose()
    {
        DrainDispatcher();
        InputSystem.SetFocus(null);
    }

    private static FrameworkElement? FindByName(FrameworkElement element, string name)
    {
        if (element.Name == name) return element;
        foreach (var child in element.Children)
        {
            if (child is FrameworkElement frameworkElement && FindByName(frameworkElement, name) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static void DrainDispatcher()
    {
        UIThread.RunPending();
        UIThread.RunPending();
    }

    private static DeltaPreviewProjectFixture
        CreateDeltaPreviewProject(string xaml)
    {
        const string code = """
namespace DeltaPreview;

public partial class Root : global::Microsoft.UI.Xaml.Controls.Grid
{
    public Root()
    {
        InitializeComponent();
    }
}
""";
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var codeDocumentId =
            DocumentId.CreateNewId(projectId);
        var xamlDocumentId =
            DocumentId.CreateNewId(projectId);
        var references = ((string?)AppContext.GetData(
                "TRUSTED_PLATFORM_ASSEMBLIES") ??
            string.Empty)
            .Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries)
            .Append(
                typeof(FrameworkElement).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(
                static path =>
                    MetadataReference.CreateFromFile(path));
        var solution = workspace.CurrentSolution
            .AddProject(
                ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "DeltaPreview",
                    "DeltaPreview",
                    LanguageNames.CSharp,
                    compilationOptions:
                        new CSharpCompilationOptions(
                            OutputKind
                                .DynamicallyLinkedLibrary),
                    parseOptions:
                        new CSharpParseOptions(
                            LanguageVersion.Preview)))
            .AddMetadataReferences(projectId, references)
            .AddDocument(
                codeDocumentId,
                "Root.cs",
                SourceText.From(code))
            .AddAdditionalDocument(
                xamlDocumentId,
                "Root.xaml",
                SourceText.From(xaml),
                folders: new[] { "Views" });
        return new DeltaPreviewProjectFixture(
            workspace,
            solution.GetProject(projectId)!,
            codeDocumentId,
            xamlDocumentId);
    }

    private static RoslynXamlProjectPreviewCoordinator
        CreateDeltaPreviewCoordinator() =>
        new RoslynXamlProjectPreviewCoordinator(
            new WinUiXamlProfile(),
            new RoslynXamlProjectPreviewOptions
            {
                InspectionOptions =
                    new RoslynXamlCompilationInspectionOptions
                    {
                        CompilerOptions =
                            new XamlCompilerOptions
                            {
                                Strict = true
                            }
                    }
            });

    private sealed class DeltaPreviewProjectFixture :
        IDisposable
    {
        public DeltaPreviewProjectFixture(
            AdhocWorkspace workspace,
            Project project,
            DocumentId codeDocumentId,
            DocumentId xamlDocumentId)
        {
            Workspace = workspace;
            Project = project;
            CodeDocumentId = codeDocumentId;
            XamlDocumentId = xamlDocumentId;
        }

        public AdhocWorkspace Workspace { get; }
        public Project Project { get; }
        public DocumentId CodeDocumentId { get; }
        public DocumentId XamlDocumentId { get; }

        public void Dispose() => Workspace.Dispose();
    }

    private sealed class FactoryElement : Grid, IHotReloadStateful
    {
        public FactoryElement(string marker)
        {
            Marker = marker;
            Editor = new TextBox { Name = "editor", Text = marker };
            Toggle = new CheckBox { Name = "toggle" };
            AddChild(Editor);
            AddChild(Toggle);
            Loading += (_, _) => LoadingCount++;
            Unloaded += (_, _) => UnloadedCount++;
        }

        public string Marker { get; }
        public TextBox Editor { get; }
        public CheckBox Toggle { get; }
        public int CustomValue { get; set; }
        public int LoadingCount { get; private set; }
        public int UnloadedCount { get; private set; }

        public object CaptureHotReloadState() => CustomValue;

        public void RestoreHotReloadState(object? state)
        {
            CustomValue = Assert.IsType<int>(state);
        }
    }

    public sealed class PreviewRoot : Grid
    {
        public PreviewRoot()
        {
            AddChild(new TextBox
            {
                Name = "editor",
                Text = "default"
            });
        }
    }

    private sealed class InPlaceElement : Grid, IHotReloadable
    {
        public int ReloadCount { get; private set; }

        public void Reload(HotReloadContext context)
        {
            Assert.True(context.IsTypeUpdated(typeof(InPlaceElement)));
            ReloadCount++;
            ClearChildren();
            AddChild(new TextBlock { Text = $"reload {ReloadCount}" });
        }
    }

    private sealed class RequiredConstructorElement(string marker) : Grid
    {
        public string Marker { get; } = marker;
    }

    private sealed class FailingInPlaceElement : Grid, IHotReloadable
    {
        public FailingInPlaceElement()
        {
            Editor = new TextBox { Name = "editor" };
            AddChild(Editor);
        }

        public TextBox Editor { get; }

        public void Reload(HotReloadContext context)
        {
            Editor.Text = "transient reload value";
            Editor.CaretIndex = 0;
            throw new InvalidOperationException("Expected reload failure.");
        }
    }

    private sealed class EmbeddedRootOriginal : Grid
    {
        public string Marker { get; init; } = string.Empty;
    }

    private sealed class MappedOriginalElement : Grid
    {
    }

    [MetadataUpdateOriginalType(typeof(MappedOriginalElement))]
    private sealed class MappedReplacementElement : Grid
    {
    }

    private static class FactoryPageOwner
    {
        public static int Version { get; set; }

        public static FrameworkElement Create()
        {
            var page = new Grid();
            page.AddChild(new TextBlock { Name = "version", Text = $"version {Version}" });
            page.AddChild(new TextBox { Name = "editor", Text = "initial" });
            return page;
        }
    }
}
