using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using System.Globalization;
using System.Reflection;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

public sealed class AutomationProviderContractTests
{
    [Fact]
    public void TransformAndWindowProvidersMatchOfficialShape()
    {
        AssertReadOnlyProperties(
            typeof(ITransformProvider),
            (nameof(ITransformProvider.CanMove), typeof(bool)),
            (nameof(ITransformProvider.CanResize), typeof(bool)),
            (nameof(ITransformProvider.CanRotate), typeof(bool)));
        AssertDeclaredMethods(
            typeof(ITransformProvider),
            new ExpectedMethod(
                nameof(ITransformProvider.Move),
                typeof(void),
                typeof(double),
                typeof(double)),
            new ExpectedMethod(
                nameof(ITransformProvider.Resize),
                typeof(void),
                typeof(double),
                typeof(double)),
            new ExpectedMethod(
                nameof(ITransformProvider.Rotate),
                typeof(void),
                typeof(double)));

        Assert.Equal(
            new[] { typeof(ITransformProvider) },
            typeof(ITransformProvider2).GetInterfaces());
        AssertReadOnlyProperties(
            typeof(ITransformProvider2),
            (nameof(ITransformProvider2.CanZoom), typeof(bool)),
            (nameof(ITransformProvider2.MaxZoom), typeof(double)),
            (nameof(ITransformProvider2.MinZoom), typeof(double)),
            (nameof(ITransformProvider2.ZoomLevel),
                typeof(double)));
        AssertDeclaredMethods(
            typeof(ITransformProvider2),
            new ExpectedMethod(
                nameof(ITransformProvider2.Zoom),
                typeof(void),
                typeof(double)),
            new ExpectedMethod(
                nameof(ITransformProvider2.ZoomByUnit),
                typeof(void),
                typeof(ZoomUnit)));

        AssertReadOnlyProperties(
            typeof(IWindowProvider),
            (nameof(IWindowProvider.InteractionState),
                typeof(WindowInteractionState)),
            (nameof(IWindowProvider.IsModal), typeof(bool)),
            (nameof(IWindowProvider.IsTopmost), typeof(bool)),
            (nameof(IWindowProvider.Maximizable), typeof(bool)),
            (nameof(IWindowProvider.Minimizable), typeof(bool)),
            (nameof(IWindowProvider.VisualState),
                typeof(WindowVisualState)));
        AssertDeclaredMethods(
            typeof(IWindowProvider),
            new ExpectedMethod(
                nameof(IWindowProvider.Close),
                typeof(void)),
            new ExpectedMethod(
                nameof(IWindowProvider.SetVisualState),
                typeof(void),
                typeof(WindowVisualState)),
            new ExpectedMethod(
                nameof(IWindowProvider.WaitForInputIdle),
                typeof(bool),
                typeof(int)));

        Type[] selectedContracts =
        [
            typeof(ITransformProvider),
            typeof(ITransformProvider2),
            typeof(IWindowProvider),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    [Fact]
    public void TransformAndWindowProviderEnumsMatchOfficialValues()
    {
        AssertEnumValues(
            typeof(WindowInteractionState),
            (nameof(WindowInteractionState.Running), 0),
            (nameof(WindowInteractionState.Closing), 1),
            (nameof(
                WindowInteractionState
                    .ReadyForUserInteraction), 2),
            (nameof(
                WindowInteractionState
                    .BlockedByModalWindow), 3),
            (nameof(WindowInteractionState.NotResponding), 4));
        AssertEnumValues(
            typeof(WindowVisualState),
            (nameof(WindowVisualState.Normal), 0),
            (nameof(WindowVisualState.Maximized), 1),
            (nameof(WindowVisualState.Minimized), 2));
        AssertEnumValues(
            typeof(ZoomUnit),
            (nameof(ZoomUnit.NoAmount), 0),
            (nameof(ZoomUnit.LargeDecrement), 1),
            (nameof(ZoomUnit.SmallDecrement), 2),
            (nameof(ZoomUnit.LargeIncrement), 3),
            (nameof(ZoomUnit.SmallIncrement), 4));

        AssertWinUiContractVersion(
            typeof(WindowInteractionState));
        AssertWinUiContractVersion(
            typeof(WindowVisualState));
        AssertWinUiContractVersion(typeof(ZoomUnit));
    }

    [Fact]
    public void ScrollAndSelectionProvidersMatchOfficialShape()
    {
        AssertReadOnlyProperties(
            typeof(IScrollProvider),
            (nameof(IScrollProvider.HorizontallyScrollable),
                typeof(bool)),
            (nameof(IScrollProvider.HorizontalScrollPercent),
                typeof(double)),
            (nameof(IScrollProvider.HorizontalViewSize),
                typeof(double)),
            (nameof(IScrollProvider.VerticallyScrollable),
                typeof(bool)),
            (nameof(IScrollProvider.VerticalScrollPercent),
                typeof(double)),
            (nameof(IScrollProvider.VerticalViewSize),
                typeof(double)));
        AssertDeclaredMethods(
            typeof(IScrollProvider),
            new ExpectedMethod(
                nameof(IScrollProvider.Scroll),
                typeof(void),
                typeof(ScrollAmount),
                typeof(ScrollAmount)),
            new ExpectedMethod(
                nameof(IScrollProvider.SetScrollPercent),
                typeof(void),
                typeof(double),
                typeof(double)));

        AssertReadOnlyProperties(
            typeof(ISelectionProvider),
            (nameof(ISelectionProvider.CanSelectMultiple),
                typeof(bool)),
            (nameof(ISelectionProvider.IsSelectionRequired),
                typeof(bool)));
        AssertDeclaredMethods(
            typeof(ISelectionProvider),
            new ExpectedMethod(
                nameof(ISelectionProvider.GetSelection),
                typeof(IRawElementProviderSimple[])));

        AssertReadOnlyProperties(
            typeof(ISelectionItemProvider),
            (nameof(ISelectionItemProvider.IsSelected),
                typeof(bool)),
            (nameof(ISelectionItemProvider.SelectionContainer),
                typeof(IRawElementProviderSimple)));
        AssertDeclaredMethods(
            typeof(ISelectionItemProvider),
            new ExpectedMethod(
                nameof(ISelectionItemProvider.AddToSelection),
                typeof(void)),
            new ExpectedMethod(
                nameof(
                    ISelectionItemProvider
                        .RemoveFromSelection),
                typeof(void)),
            new ExpectedMethod(
                nameof(ISelectionItemProvider.Select),
                typeof(void)));

        Type[] selectedContracts =
        [
            typeof(IScrollProvider),
            typeof(ISelectionItemProvider),
            typeof(ISelectionProvider),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    [Fact]
    public void ScrollAmountMatchesOfficialValues()
    {
        AssertEnumValues(
            typeof(ScrollAmount),
            (nameof(ScrollAmount.LargeDecrement), 0),
            (nameof(ScrollAmount.SmallDecrement), 1),
            (nameof(ScrollAmount.NoAmount), 2),
            (nameof(ScrollAmount.LargeIncrement), 3),
            (nameof(ScrollAmount.SmallIncrement), 4));
        AssertWinUiContractVersion(typeof(ScrollAmount));
    }

    [Fact]
    public void GridAndTableProviderInterfacesMatchOfficialShape()
    {
        AssertReadOnlyProperties(
            typeof(IGridProvider),
            (nameof(IGridProvider.ColumnCount), typeof(int)),
            (nameof(IGridProvider.RowCount), typeof(int)));
        AssertDeclaredMethods(
            typeof(IGridProvider),
            new ExpectedMethod(
                nameof(IGridProvider.GetItem),
                typeof(IRawElementProviderSimple),
                typeof(int),
                typeof(int)));

        AssertReadOnlyProperties(
            typeof(IGridItemProvider),
            (nameof(IGridItemProvider.Column), typeof(int)),
            (nameof(IGridItemProvider.ColumnSpan), typeof(int)),
            (nameof(IGridItemProvider.ContainingGrid),
                typeof(IRawElementProviderSimple)),
            (nameof(IGridItemProvider.Row), typeof(int)),
            (nameof(IGridItemProvider.RowSpan), typeof(int)));
        AssertDeclaredMethods(typeof(IGridItemProvider));

        AssertReadOnlyProperties(
            typeof(ITableProvider),
            (nameof(ITableProvider.RowOrColumnMajor),
                typeof(RowOrColumnMajor)));
        AssertDeclaredMethods(
            typeof(ITableProvider),
            new ExpectedMethod(
                nameof(ITableProvider.GetColumnHeaders),
                typeof(IRawElementProviderSimple[])),
            new ExpectedMethod(
                nameof(ITableProvider.GetRowHeaders),
                typeof(IRawElementProviderSimple[])));

        Type[] selectedContracts =
        [
            typeof(IGridItemProvider),
            typeof(IGridProvider),
            typeof(ITableProvider),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    [Fact]
    public void RowOrColumnMajorMatchesOfficialValues()
    {
        AssertEnumValues(
            typeof(RowOrColumnMajor),
            (nameof(RowOrColumnMajor.RowMajor), 0),
            (nameof(RowOrColumnMajor.ColumnMajor), 1),
            (nameof(RowOrColumnMajor.Indeterminate), 2));
        AssertWinUiContractVersion(typeof(RowOrColumnMajor));
    }

    [Fact]
    public void StatefulProviderInterfacesMatchOfficialShape()
    {
        AssertReadOnlyProperties(
            typeof(IExpandCollapseProvider),
            (nameof(
                IExpandCollapseProvider.ExpandCollapseState),
                typeof(ExpandCollapseState)));
        AssertDeclaredMethods(
            typeof(IExpandCollapseProvider),
            new ExpectedMethod(
                nameof(IExpandCollapseProvider.Collapse),
                typeof(void)),
            new ExpectedMethod(
                nameof(IExpandCollapseProvider.Expand),
                typeof(void)));

        AssertReadOnlyProperties(
            typeof(IRangeValueProvider),
            (nameof(IRangeValueProvider.IsReadOnly),
                typeof(bool)),
            (nameof(IRangeValueProvider.LargeChange),
                typeof(double)),
            (nameof(IRangeValueProvider.Maximum),
                typeof(double)),
            (nameof(IRangeValueProvider.Minimum),
                typeof(double)),
            (nameof(IRangeValueProvider.SmallChange),
                typeof(double)),
            (nameof(IRangeValueProvider.Value),
                typeof(double)));
        AssertDeclaredMethods(
            typeof(IRangeValueProvider),
            new ExpectedMethod(
                nameof(IRangeValueProvider.SetValue),
                typeof(void),
                typeof(double)));

        AssertReadOnlyProperties(
            typeof(IToggleProvider),
            (nameof(IToggleProvider.ToggleState),
                typeof(ToggleState)));
        AssertDeclaredMethods(
            typeof(IToggleProvider),
            new ExpectedMethod(
                nameof(IToggleProvider.Toggle),
                typeof(void)));

        AssertReadOnlyProperties(
            typeof(IValueProvider),
            (nameof(IValueProvider.IsReadOnly),
                typeof(bool)),
            (nameof(IValueProvider.Value),
                typeof(string)));
        AssertDeclaredMethods(
            typeof(IValueProvider),
            new ExpectedMethod(
                nameof(IValueProvider.SetValue),
                typeof(void),
                typeof(string)));

        Type[] selectedContracts =
        [
            typeof(IExpandCollapseProvider),
            typeof(IRangeValueProvider),
            typeof(IToggleProvider),
            typeof(IValueProvider),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    [Fact]
    public void StatefulProviderEnumsMatchOfficialValues()
    {
        AssertEnumValues(
            typeof(ExpandCollapseState),
            (nameof(ExpandCollapseState.Collapsed), 0),
            (nameof(ExpandCollapseState.Expanded), 1),
            (nameof(
                ExpandCollapseState.PartiallyExpanded), 2),
            (nameof(ExpandCollapseState.LeafNode), 3));
        AssertEnumValues(
            typeof(ToggleState),
            (nameof(ToggleState.Off), 0),
            (nameof(ToggleState.On), 1),
            (nameof(ToggleState.Indeterminate), 2));

        AssertWinUiContractVersion(
            typeof(ExpandCollapseState));
        AssertWinUiContractVersion(typeof(ToggleState));
    }

    [Fact]
    public void SelectedProviderInterfacesMatchOfficialShape()
    {
        AssertParameterlessMethod(
            typeof(IInvokeProvider),
            nameof(IInvokeProvider.Invoke),
            typeof(void));
        AssertParameterlessMethod(
            typeof(IObjectModelProvider),
            nameof(IObjectModelProvider.GetUnderlyingObjectModel),
            typeof(object));
        AssertParameterlessMethod(
            typeof(IScrollItemProvider),
            nameof(IScrollItemProvider.ScrollIntoView),
            typeof(void));
        AssertReadOnlyProperties(
            typeof(IDropTargetProvider),
            (nameof(IDropTargetProvider.DropEffect),
                typeof(string)),
            (nameof(IDropTargetProvider.DropEffects),
                typeof(string[])));
        AssertMethods(
            typeof(ITableItemProvider),
            (nameof(ITableItemProvider.GetColumnHeaderItems),
                typeof(IRawElementProviderSimple[])),
            (nameof(ITableItemProvider.GetRowHeaderItems),
                typeof(IRawElementProviderSimple[])));
        AssertReadOnlyProperties(
            typeof(ITextChildProvider),
            (nameof(ITextChildProvider.TextContainer),
                typeof(IRawElementProviderSimple)),
            (nameof(ITextChildProvider.TextRange),
                typeof(ITextRangeProvider)));
        AssertParameterlessMethod(
            typeof(IVirtualizedItemProvider),
            nameof(IVirtualizedItemProvider.Realize),
            typeof(void));

        Type[] selectedContracts =
        [
            typeof(IDropTargetProvider),
            typeof(IInvokeProvider),
            typeof(IObjectModelProvider),
            typeof(IScrollItemProvider),
            typeof(ITableItemProvider),
            typeof(ITextChildProvider),
            typeof(IVirtualizedItemProvider),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    [Fact]
    public void AnnotationDragAndMultipleViewProvidersMatchOfficialShape()
    {
        AssertReadOnlyProperties(
            typeof(IAnnotationProvider),
            (nameof(IAnnotationProvider.AnnotationTypeId),
                typeof(int)),
            (nameof(IAnnotationProvider.AnnotationTypeName),
                typeof(string)),
            (nameof(IAnnotationProvider.Author), typeof(string)),
            (nameof(IAnnotationProvider.DateTime), typeof(string)),
            (nameof(IAnnotationProvider.Target),
                typeof(IRawElementProviderSimple)));
        AssertDeclaredMethods(typeof(IAnnotationProvider));

        AssertReadOnlyProperties(
            typeof(IDragProvider),
            (nameof(IDragProvider.DropEffect), typeof(string)),
            (nameof(IDragProvider.DropEffects), typeof(string[])),
            (nameof(IDragProvider.IsGrabbed), typeof(bool)));
        AssertDeclaredMethods(
            typeof(IDragProvider),
            new ExpectedMethod(
                nameof(IDragProvider.GetGrabbedItems),
                typeof(IRawElementProviderSimple[])));

        AssertReadOnlyProperties(
            typeof(IMultipleViewProvider),
            (nameof(IMultipleViewProvider.CurrentView),
                typeof(int)));
        AssertDeclaredMethods(
            typeof(IMultipleViewProvider),
            new ExpectedMethod(
                nameof(IMultipleViewProvider.GetSupportedViews),
                typeof(int[])),
            new ExpectedMethod(
                nameof(IMultipleViewProvider.GetViewName),
                typeof(string),
                typeof(int)),
            new ExpectedMethod(
                nameof(IMultipleViewProvider.SetCurrentView),
                typeof(void),
                typeof(int)));

        Type[] selectedContracts =
        [
            typeof(IAnnotationProvider),
            typeof(IDragProvider),
            typeof(IMultipleViewProvider),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    [Fact]
    public void DockAndCustomNavigationProvidersMatchOfficialShape()
    {
        AssertReadOnlyProperties(
            typeof(IDockProvider),
            (nameof(IDockProvider.DockPosition),
                typeof(DockPosition)));
        AssertDeclaredMethods(
            typeof(IDockProvider),
            new ExpectedMethod(
                nameof(IDockProvider.SetDockPosition),
                typeof(void),
                typeof(DockPosition)));

        AssertReadOnlyProperties(
            typeof(ICustomNavigationProvider));
        AssertDeclaredMethods(
            typeof(ICustomNavigationProvider),
            new ExpectedMethod(
                nameof(
                    ICustomNavigationProvider
                        .NavigateCustom),
                typeof(object),
                typeof(
                    AutomationNavigationDirection)));

        AssertWinUiContractVersion(
            typeof(IDockProvider));
        AssertWinUiContractVersion(
            typeof(ICustomNavigationProvider));
    }

    [Fact]
    public void DockAndCustomNavigationEnumsMatchOfficialValues()
    {
        AssertEnumValues(
            typeof(DockPosition),
            (nameof(DockPosition.Top), 0),
            (nameof(DockPosition.Left), 1),
            (nameof(DockPosition.Bottom), 2),
            (nameof(DockPosition.Right), 3),
            (nameof(DockPosition.Fill), 4),
            (nameof(DockPosition.None), 5));
        AssertEnumValues(
            typeof(AutomationNavigationDirection),
            (nameof(
                AutomationNavigationDirection.Parent), 0),
            (nameof(
                AutomationNavigationDirection
                    .NextSibling), 1),
            (nameof(
                AutomationNavigationDirection
                    .PreviousSibling), 2),
            (nameof(
                AutomationNavigationDirection
                    .FirstChild), 3),
            (nameof(
                AutomationNavigationDirection
                    .LastChild), 4));

        AssertWinUiContractVersion(
            typeof(DockPosition));
        AssertWinUiContractVersion(
            typeof(AutomationNavigationDirection));
    }

    [Fact]
    public void SpreadsheetAndStylesProvidersMatchOfficialShape()
    {
        AssertReadOnlyProperties(
            typeof(ISpreadsheetItemProvider),
            (nameof(
                ISpreadsheetItemProvider.Formula),
                typeof(string)));
        AssertDeclaredMethods(
            typeof(ISpreadsheetItemProvider),
            new ExpectedMethod(
                nameof(
                    ISpreadsheetItemProvider
                        .GetAnnotationObjects),
                typeof(IRawElementProviderSimple[])),
            new ExpectedMethod(
                nameof(
                    ISpreadsheetItemProvider
                        .GetAnnotationTypes),
                typeof(AnnotationType[])));

        AssertReadOnlyProperties(
            typeof(ISpreadsheetProvider));
        AssertDeclaredMethods(
            typeof(ISpreadsheetProvider),
            new ExpectedMethod(
                nameof(
                    ISpreadsheetProvider
                        .GetItemByName),
                typeof(IRawElementProviderSimple),
                typeof(string)));

        AssertReadOnlyProperties(
            typeof(IStylesProvider),
            (nameof(
                IStylesProvider.ExtendedProperties),
                typeof(string)),
            (nameof(IStylesProvider.FillColor),
                typeof(Windows.UI.Color)),
            (nameof(
                IStylesProvider.FillPatternColor),
                typeof(Windows.UI.Color)),
            (nameof(
                IStylesProvider.FillPatternStyle),
                typeof(string)),
            (nameof(IStylesProvider.Shape),
                typeof(string)),
            (nameof(IStylesProvider.StyleId),
                typeof(int)),
            (nameof(IStylesProvider.StyleName),
                typeof(string)));
        AssertDeclaredMethods(
            typeof(IStylesProvider));

        AssertWinUiContractVersion(
            typeof(ISpreadsheetItemProvider));
        AssertWinUiContractVersion(
            typeof(ISpreadsheetProvider));
        AssertWinUiContractVersion(
            typeof(IStylesProvider));
    }

    [Fact]
    public void AnnotationTypeMatchesOfficialValues()
    {
        AssertEnumValues(
            typeof(AnnotationType),
            (nameof(AnnotationType.Unknown), 60000),
            (nameof(AnnotationType.SpellingError), 60001),
            (nameof(AnnotationType.GrammarError), 60002),
            (nameof(AnnotationType.Comment), 60003),
            (nameof(AnnotationType.FormulaError), 60004),
            (nameof(AnnotationType.TrackChanges), 60005),
            (nameof(AnnotationType.Header), 60006),
            (nameof(AnnotationType.Footer), 60007),
            (nameof(AnnotationType.Highlighted), 60008),
            (nameof(AnnotationType.Endnote), 60009),
            (nameof(AnnotationType.Footnote), 60010),
            (nameof(AnnotationType.InsertionChange), 60011),
            (nameof(AnnotationType.DeletionChange), 60012),
            (nameof(AnnotationType.MoveChange), 60013),
            (nameof(AnnotationType.FormatChange), 60014),
            (nameof(AnnotationType.UnsyncedChange), 60015),
            (nameof(
                AnnotationType.EditingLockedChange),
                60016),
            (nameof(AnnotationType.ExternalChange), 60017),
            (nameof(
                AnnotationType.ConflictingChange),
                60018),
            (nameof(AnnotationType.Author), 60019),
            (nameof(
                AnnotationType.AdvancedProofingIssue),
                60020),
            (nameof(
                AnnotationType.DataValidationError),
                60021),
            (nameof(
                AnnotationType.CircularReferenceError),
                60022));

        AssertWinUiContractVersion(
            typeof(AnnotationType));
    }

    [Fact]
    public void TextAndSynchronizedInputProvidersMatchOfficialShape()
    {
        AssertDeclaredMethods(
            typeof(ISynchronizedInputProvider),
            new ExpectedMethod(
                nameof(ISynchronizedInputProvider.Cancel),
                typeof(void)),
            new ExpectedMethod(
                nameof(
                    ISynchronizedInputProvider
                        .StartListening),
                typeof(void),
                typeof(SynchronizedInputType)));

        Assert.Equal(
            new[] { typeof(ITextProvider) },
            typeof(ITextEditProvider)
                .GetInterfaces());
        AssertDeclaredMethods(
            typeof(ITextEditProvider),
            new ExpectedMethod(
                nameof(
                    ITextEditProvider
                        .GetActiveComposition),
                typeof(ITextRangeProvider)),
            new ExpectedMethod(
                nameof(
                    ITextEditProvider
                        .GetConversionTarget),
                typeof(ITextRangeProvider)));

        Assert.Equal(
            new[] { typeof(ITextProvider) },
            typeof(ITextProvider2)
                .GetInterfaces());
        AssertDeclaredMethods(
            typeof(ITextProvider2),
            new ExpectedMethod(
                nameof(ITextProvider2.GetCaretRange),
                typeof(ITextRangeProvider),
                typeof(bool).MakeByRefType()),
            new ExpectedMethod(
                nameof(
                    ITextProvider2
                        .RangeFromAnnotation),
                typeof(ITextRangeProvider),
                typeof(IRawElementProviderSimple)));

        Assert.Equal(
            new[] { typeof(ITextRangeProvider) },
            typeof(ITextRangeProvider2)
                .GetInterfaces());
        AssertDeclaredMethods(
            typeof(ITextRangeProvider2),
            new ExpectedMethod(
                nameof(
                    ITextRangeProvider2
                        .ShowContextMenu),
                typeof(void)));

        Assert.Empty(
            typeof(ITextRangeProvider)
                .GetProperties());
        var getText = Assert.Single(
            typeof(ITextRangeProvider)
                .GetMethods(),
            static method =>
                method.Name ==
                nameof(ITextRangeProvider.GetText));
        var maxLength = Assert.Single(
            getText.GetParameters());
        Assert.Equal(typeof(int),
            maxLength.ParameterType);
        Assert.False(maxLength.IsOptional);

        Type[] selectedContracts =
        [
            typeof(ISynchronizedInputProvider),
            typeof(ITextEditProvider),
            typeof(ITextProvider),
            typeof(ITextProvider2),
            typeof(ITextRangeProvider),
            typeof(ITextRangeProvider2),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    [Fact]
    public void SynchronizedInputTypeMatchesOfficialValues()
    {
        AssertEnumValues(
            typeof(SynchronizedInputType),
            (nameof(SynchronizedInputType.KeyUp), 1),
            (nameof(SynchronizedInputType.KeyDown), 2),
            (nameof(SynchronizedInputType.LeftMouseUp), 4),
            (nameof(SynchronizedInputType.LeftMouseDown), 8),
            (nameof(SynchronizedInputType.RightMouseUp), 16),
            (nameof(SynchronizedInputType.RightMouseDown), 32));

        AssertWinUiContractVersion(
            typeof(SynchronizedInputType));
    }

    [Fact]
    public void AutomationPeerAndItemContainerMatchOfficialShape()
    {
        Assert.Equal(
            typeof(DependencyObject),
            typeof(AutomationPeer).BaseType);
        Assert.False(typeof(AutomationPeer).IsAbstract);

        string[] wrapperCorePairs =
        [
            nameof(AutomationPeer.GetAcceleratorKey),
            nameof(AutomationPeer.GetAccessKey),
            nameof(AutomationPeer.GetAnnotations),
            nameof(AutomationPeer.GetAutomationControlType),
            nameof(AutomationPeer.GetAutomationId),
            nameof(AutomationPeer.GetBoundingRectangle),
            nameof(AutomationPeer.GetChildren),
            nameof(AutomationPeer.GetClassName),
            nameof(AutomationPeer.GetClickablePoint),
            nameof(AutomationPeer.GetControlledPeers),
            nameof(AutomationPeer.GetCulture),
            nameof(AutomationPeer.GetElementFromPoint),
            nameof(AutomationPeer.GetFocusedElement),
            nameof(AutomationPeer.GetFullDescription),
            nameof(AutomationPeer.GetHeadingLevel),
            nameof(AutomationPeer.GetHelpText),
            nameof(AutomationPeer.GetItemStatus),
            nameof(AutomationPeer.GetItemType),
            nameof(AutomationPeer.GetLabeledBy),
            nameof(AutomationPeer.GetLandmarkType),
            nameof(AutomationPeer.GetLevel),
            nameof(AutomationPeer.GetLiveSetting),
            nameof(AutomationPeer.GetLocalizedControlType),
            nameof(AutomationPeer.GetLocalizedLandmarkType),
            nameof(AutomationPeer.GetName),
            nameof(AutomationPeer.GetOrientation),
            nameof(AutomationPeer.GetPattern),
            nameof(AutomationPeer.GetPeerFromPoint),
            nameof(AutomationPeer.GetPositionInSet),
            nameof(AutomationPeer.GetSizeOfSet),
            nameof(AutomationPeer.HasKeyboardFocus),
            nameof(AutomationPeer.IsContentElement),
            nameof(AutomationPeer.IsControlElement),
            nameof(AutomationPeer.IsDataValidForForm),
            nameof(AutomationPeer.IsDialog),
            nameof(AutomationPeer.IsEnabled),
            nameof(AutomationPeer.IsKeyboardFocusable),
            nameof(AutomationPeer.IsOffscreen),
            nameof(AutomationPeer.IsPassword),
            nameof(AutomationPeer.IsPeripheral),
            nameof(AutomationPeer.IsRequiredForForm),
            nameof(AutomationPeer.Navigate),
            nameof(AutomationPeer.SetFocus),
            nameof(AutomationPeer.ShowContextMenu),
        ];
        foreach (var wrapperName in wrapperCorePairs)
        {
            MethodInfo wrapper = Assert.Single(
                typeof(AutomationPeer).GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly),
                method => method.Name == wrapperName);
            Assert.False(wrapper.IsVirtual);

            MethodInfo core = Assert.Single(
                typeof(AutomationPeer).GetMethods(
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly),
                method => method.Name == wrapperName + "Core");
            Assert.True(core.IsFamily);
            Assert.True(core.IsVirtual);
        }

        AssertDeclaredMethods(
            typeof(IItemContainerProvider),
            new ExpectedMethod(
                nameof(IItemContainerProvider.FindItemByProperty),
                typeof(IRawElementProviderSimple),
                typeof(IRawElementProviderSimple),
                typeof(AutomationProperty),
                typeof(object)));
        AssertWinUiContractVersion(
            typeof(IItemContainerProvider));

        Assert.True(typeof(IRawElementProviderSimple).IsSealed);
        Assert.Equal(
            typeof(DependencyObject),
            typeof(IRawElementProviderSimple).BaseType);
        Assert.Empty(
            typeof(IRawElementProviderSimple).GetProperties(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly));
        Assert.Empty(
            typeof(IRawElementProviderSimple).GetConstructors(
                BindingFlags.Public |
                BindingFlags.Instance));
    }

    [Fact]
    public void AutomationPeerDispatchNavigationAndProviderIdentityAreTypedAndBounded()
    {
        var parent = new ProbeAutomationPeer("parent");
        var first = new ProbeAutomationPeer("first");
        var second = new ProbeAutomationPeer("second");
        parent.ChildPeers.Add(first);
        parent.ChildPeers.Add(second);
        first.SetParent(parent);
        second.SetParent(parent);

        Assert.Equal("first", first.GetName());
        Assert.Equal(
            AutomationControlType.Button,
            first.GetAutomationControlType());
        Assert.Same(parent, first.GetParent());
        Assert.Same(parent, first.Navigate(
            AutomationNavigationDirection.Parent));
        Assert.Same(first, parent.Navigate(
            AutomationNavigationDirection.FirstChild));
        Assert.Same(second, parent.Navigate(
            AutomationNavigationDirection.LastChild));
        Assert.Same(second, first.Navigate(
            AutomationNavigationDirection.NextSibling));
        Assert.Same(first, second.Navigate(
            AutomationNavigationDirection.PreviousSibling));
        Assert.Null(first.Navigate(
            AutomationNavigationDirection.PreviousSibling));
        Assert.Null(second.Navigate(
            AutomationNavigationDirection.NextSibling));

        first.SetFocus();
        first.ShowContextMenu();
        Assert.True(first.FocusRequested);
        Assert.True(first.ContextMenuRequested);

        IRawElementProviderSimple provider =
            first.GetProvider(first);
        Assert.Same(provider, first.GetProvider(first));
        Assert.Same(first, first.GetPeer(provider));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0;
             iteration < 1_000_000;
             iteration++)
        {
            provider = first.GetProvider(first);
        }

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Same(first, first.GetPeer(provider));

        RawElementProviderRuntimeId firstId =
            AutomationPeer.GenerateRawElementProviderRuntimeId();
        RawElementProviderRuntimeId secondId =
            AutomationPeer.GenerateRawElementProviderRuntimeId();
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, new RawElementProviderRuntimeId(
            firstId.Part1,
            firstId.Part2));

        var owner = new Microsoft.UI.Xaml.Controls.Button
        {
            IsEnabled = true,
            Visibility = Visibility.Visible,
        };
        var frameworkPeer =
            new FrameworkElementAutomationPeer(owner);
        Assert.Equal(nameof(Microsoft.UI.Xaml.Controls.Button),
            frameworkPeer.GetClassName());
        Assert.True(frameworkPeer.IsContentElement());
        Assert.True(frameworkPeer.IsControlElement());
        Assert.True(frameworkPeer.IsEnabled());
        Assert.False(frameworkPeer.IsOffscreen());
        owner.Visibility = Visibility.Collapsed;
        Assert.True(frameworkPeer.IsOffscreen());
    }

    [Fact]
    public void AutomationPeerEventsAndAnnotationsPreserveTypedState()
    {
        var peer = new ProbeAutomationPeer("event-source");
        var child = new ProbeAutomationPeer("child");
        var property = new AutomationProperty(17);
        var changedData = new[] { "composition" };
        var automationEvents = new List<AutomationEvents>();
        AutomationPeer? notificationPeer = null;
        AutomationPeer? propertyPeer = null;
        AutomationPeer? structurePeer = null;
        AutomationPeer? textPeer = null;
        AutomationPeer? invalidatedPeer = null;

        try
        {
            AutomationPeerEventRuntime.ListenerProbe =
                eventId => eventId ==
                    AutomationEvents.AutomationFocusChanged;
            AutomationPeerEventRuntime.PeerInvalidated =
                source => invalidatedPeer = source;
            AutomationPeerEventRuntime.AutomationEventRaised =
                (_, eventId) => automationEvents.Add(eventId);
            AutomationPeerEventRuntime.NotificationEventRaised =
                (source, _, _, _, _) => notificationPeer = source;
            AutomationPeerEventRuntime.PropertyChangedEventRaised =
                (source, actualProperty, oldValue, newValue) =>
                {
                    propertyPeer = source;
                    Assert.Same(property, actualProperty);
                    Assert.Equal("old", oldValue);
                    Assert.Equal("new", newValue);
                };
            AutomationPeerEventRuntime.StructureChangedEventRaised =
                (source, changeType, actualChild) =>
                {
                    structurePeer = source;
                    Assert.Equal(
                        AutomationStructureChangeType.ChildAdded,
                        changeType);
                    Assert.Same(child, actualChild);
                };
            AutomationPeerEventRuntime.TextEditTextChangedEventRaised =
                (source, changeType, actualData) =>
                {
                    textPeer = source;
                    Assert.Equal(
                        AutomationTextEditChangeType.Composition,
                        changeType);
                    Assert.Same(changedData, actualData);
                };

            Assert.True(AutomationPeer.ListenerExists(
                AutomationEvents.AutomationFocusChanged));
            peer.InvalidatePeer();
            peer.RaiseAutomationEvent(
                AutomationEvents.AutomationFocusChanged);
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                "complete",
                "activity");
            peer.RaisePropertyChangedEvent(
                property,
                "old",
                "new");
            peer.RaiseStructureChangedEvent(
                AutomationStructureChangeType.ChildAdded,
                child);
            peer.RaiseTextEditTextChangedEvent(
                AutomationTextEditChangeType.Composition,
                changedData);

            Assert.Same(peer, invalidatedPeer);
            Assert.Same(peer, notificationPeer);
            Assert.Same(peer, propertyPeer);
            Assert.Same(peer, structurePeer);
            Assert.Same(peer, textPeer);
            Assert.Equal(
                [
                    AutomationEvents.AutomationFocusChanged,
                    AutomationEvents.PropertyChanged,
                    AutomationEvents.StructureChanged,
                    AutomationEvents.TextEditTextChanged,
                ],
                automationEvents);
        }
        finally
        {
            AutomationPeerEventRuntime.Reset();
        }

        var annotation = new AutomationPeerAnnotation(
            AnnotationType.Comment,
            peer);
        Assert.Equal(AnnotationType.Comment, annotation.Type);
        Assert.Same(peer, annotation.Peer);
        Assert.Same(
            AutomationPeerAnnotation.TypeProperty,
            AutomationPeerAnnotation.TypeProperty);
        Assert.Same(
            AutomationPeerAnnotation.PeerProperty,
            AutomationPeerAnnotation.PeerProperty);
    }

    [Fact]
    public void AutomationAttachedPropertiesAndIdentifiersAreExactStableAndAllocationFree()
    {
        string[] attachedPropertyNames =
        [
            "AcceleratorKeyProperty",
            "AccessKeyProperty",
            "AccessibilityViewProperty",
            "AnnotationsProperty",
            "AutomationControlTypeProperty",
            "AutomationIdProperty",
            "ControlledPeersProperty",
            "CultureProperty",
            "DescribedByProperty",
            "FlowsFromProperty",
            "FlowsToProperty",
            "FullDescriptionProperty",
            "HeadingLevelProperty",
            "HelpTextProperty",
            "IsDataValidForFormProperty",
            "IsDialogProperty",
            "IsPeripheralProperty",
            "IsRequiredForFormProperty",
            "ItemStatusProperty",
            "ItemTypeProperty",
            "LabeledByProperty",
            "LandmarkTypeProperty",
            "LevelProperty",
            "LiveSettingProperty",
            "LocalizedControlTypeProperty",
            "LocalizedLandmarkTypeProperty",
            "NameProperty",
            "PositionInSetProperty",
            "SizeOfSetProperty",
        ];
        PropertyInfo[] attachedProperties =
            typeof(AutomationProperties)
                .GetProperties(
                    BindingFlags.Public |
                    BindingFlags.Static)
                .OrderBy(property => property.Name)
                .ToArray();
        Assert.Equal(
            attachedPropertyNames.Order(),
            attachedProperties.Select(property => property.Name));
        Assert.All(
            attachedProperties,
            property =>
            {
                Assert.Equal(
                    typeof(DependencyProperty),
                    property.PropertyType);
                Assert.True(property.CanRead);
                Assert.False(property.CanWrite);
                var dependencyProperty =
                    Assert.IsType<DependencyProperty>(
                        property.GetValue(null));
                Assert.Same(
                    dependencyProperty,
                    property.GetValue(null));
                Assert.True(dependencyProperty.IsAttached);
                Assert.Equal(
                    typeof(AutomationProperties),
                    dependencyProperty.OwnerType);
                Assert.Equal(
                    property.Name[..^"Property".Length],
                    dependencyProperty.Name);
            });
        Assert.Empty(
            typeof(AutomationProperties).GetFields(
                BindingFlags.Public |
                BindingFlags.Static));

        PropertyInfo[] identifiers =
            typeof(AutomationElementIdentifiers)
                .GetProperties(
                    BindingFlags.Public |
                    BindingFlags.Static)
                .OrderBy(property => property.Name)
                .ToArray();
        Assert.Equal(39, identifiers.Length);
        Assert.All(
            identifiers,
            property => Assert.Equal(
                typeof(AutomationProperty),
                property.PropertyType));
        AutomationProperty[] identifierValues = identifiers
            .Select(property =>
                Assert.IsType<AutomationProperty>(
                    property.GetValue(null)))
            .ToArray();
        Assert.Equal(
            identifierValues.Length,
            identifierValues.Distinct().Count());
        Assert.All(
            identifiers,
            property => Assert.Same(
                property.GetValue(null),
                property.GetValue(null)));
        Assert.True(typeof(AutomationElementIdentifiers).IsSealed);
        Assert.False(typeof(AutomationElementIdentifiers).IsAbstract);
        Assert.Empty(
            typeof(AutomationElementIdentifiers).GetFields(
                BindingFlags.Public |
                BindingFlags.Static));

        var element = new Microsoft.UI.Xaml.Controls.Button();
        var secondElement =
            new Microsoft.UI.Xaml.Controls.Button();
        Assert.Equal(string.Empty,
            AutomationProperties.GetAcceleratorKey(element));
        Assert.Equal(string.Empty,
            AutomationProperties.GetAccessKey(element));
        Assert.Equal(AccessibilityView.Content,
            AutomationProperties.GetAccessibilityView(element));
        Assert.Equal(AutomationControlType.Button,
            AutomationProperties.GetAutomationControlType(element));
        Assert.Equal(string.Empty,
            AutomationProperties.GetAutomationId(element));
        Assert.Equal(CultureInfo.CurrentUICulture.LCID,
            AutomationProperties.GetCulture(element));
        Assert.Equal(AutomationHeadingLevel.None,
            AutomationProperties.GetHeadingLevel(element));
        Assert.False(
            AutomationProperties.GetIsDataValidForForm(element));
        Assert.False(AutomationProperties.GetIsDialog(element));
        Assert.False(AutomationProperties.GetIsPeripheral(element));
        Assert.False(
            AutomationProperties.GetIsRequiredForForm(element));
        Assert.Equal(AutomationLandmarkType.None,
            AutomationProperties.GetLandmarkType(element));
        Assert.Equal(-1, AutomationProperties.GetLevel(element));
        Assert.Equal(AutomationLiveSetting.Off,
            AutomationProperties.GetLiveSetting(element));
        Assert.Equal(-1,
            AutomationProperties.GetPositionInSet(element));
        Assert.Equal(-1,
            AutomationProperties.GetSizeOfSet(element));

        IList<AutomationAnnotation> annotations =
            AutomationProperties.GetAnnotations(element);
        Assert.Same(
            annotations,
            AutomationProperties.GetAnnotations(element));
        Assert.NotSame(
            annotations,
            AutomationProperties.GetAnnotations(secondElement));
        Assert.Same(
            AutomationProperties.GetControlledPeers(element),
            AutomationProperties.GetControlledPeers(element));
        Assert.Same(
            AutomationProperties.GetDescribedBy(element),
            AutomationProperties.GetDescribedBy(element));
        Assert.Same(
            AutomationProperties.GetFlowsFrom(element),
            AutomationProperties.GetFlowsFrom(element));
        Assert.Same(
            AutomationProperties.GetFlowsTo(element),
            AutomationProperties.GetFlowsTo(element));

        AutomationProperties.SetName(element, "Submit");
        AutomationProperties.SetAutomationId(element, "submit");
        AutomationProperties.SetPositionInSet(element, 2);
        AutomationProperties.SetSizeOfSet(element, 5);
        AutomationProperties.SetLevel(element, 3);
        AutomationProperties.SetHeadingLevel(
            element,
            AutomationHeadingLevel.Level2);
        Assert.Equal("Submit", AutomationProperties.GetName(element));
        Assert.Equal("submit",
            AutomationProperties.GetAutomationId(element));
        Assert.Equal(2,
            AutomationProperties.GetPositionInSet(element));
        Assert.Equal(5,
            AutomationProperties.GetSizeOfSet(element));
        Assert.Equal(3, AutomationProperties.GetLevel(element));
        Assert.Equal(AutomationHeadingLevel.Level2,
            AutomationProperties.GetHeadingLevel(element));

        var annotation = new AutomationAnnotation(
            AnnotationType.Comment,
            element);
        Assert.Equal(AnnotationType.Comment, annotation.Type);
        Assert.Same(element, annotation.Element);
        Assert.Same(
            AutomationAnnotation.TypeProperty,
            AutomationAnnotation.TypeProperty);
        Assert.Same(
            AutomationAnnotation.ElementProperty,
            AutomationAnnotation.ElementProperty);
        Assert.Equal(
            AnnotationType.Unknown,
            new AutomationAnnotation().Type);

        _ = AutomationProperties.GetName(element);
        _ = AutomationProperties.GetAnnotations(element);
        _ = AutomationElementIdentifiers.NameProperty;
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0;
             iteration < 1_000_000;
             iteration++)
        {
            _ = AutomationProperties.GetName(element);
            _ = AutomationProperties.GetLevel(element);
            _ = AutomationProperties.GetAnnotations(element);
            _ = AutomationElementIdentifiers.NameProperty;
        }

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        AssertWinUiContractVersion(typeof(AutomationProperties));
        AssertWinUiContractVersion(
            typeof(AutomationElementIdentifiers));
        AssertWinUiContractVersion(typeof(AutomationAnnotation));
    }

    [Fact]
    public void AutomationPeerEnumsMatchOfficialValues()
    {
        AssertEnumValues(
            typeof(AutomationHeadingLevel),
            (nameof(AutomationHeadingLevel.None), 0),
            (nameof(AutomationHeadingLevel.Level1), 1),
            (nameof(AutomationHeadingLevel.Level2), 2),
            (nameof(AutomationHeadingLevel.Level3), 3),
            (nameof(AutomationHeadingLevel.Level4), 4),
            (nameof(AutomationHeadingLevel.Level5), 5),
            (nameof(AutomationHeadingLevel.Level6), 6),
            (nameof(AutomationHeadingLevel.Level7), 7),
            (nameof(AutomationHeadingLevel.Level8), 8),
            (nameof(AutomationHeadingLevel.Level9), 9));
        AssertEnumValues(
            typeof(AutomationLandmarkType),
            (nameof(AutomationLandmarkType.None), 0),
            (nameof(AutomationLandmarkType.Custom), 1),
            (nameof(AutomationLandmarkType.Form), 2),
            (nameof(AutomationLandmarkType.Main), 3),
            (nameof(AutomationLandmarkType.Navigation), 4),
            (nameof(AutomationLandmarkType.Search), 5));
        AssertEnumValues(
            typeof(AutomationLiveSetting),
            (nameof(AutomationLiveSetting.Off), 0),
            (nameof(AutomationLiveSetting.Polite), 1),
            (nameof(AutomationLiveSetting.Assertive), 2));
        AssertEnumValues(
            typeof(AutomationOrientation),
            (nameof(AutomationOrientation.None), 0),
            (nameof(AutomationOrientation.Horizontal), 1),
            (nameof(AutomationOrientation.Vertical), 2));
        AssertEnumValues(
            typeof(AutomationTextEditChangeType),
            (nameof(AutomationTextEditChangeType.None), 0),
            (nameof(AutomationTextEditChangeType.AutoCorrect), 1),
            (nameof(AutomationTextEditChangeType.Composition), 2),
            (nameof(
                AutomationTextEditChangeType.CompositionFinalized),
                3));

        Type[] selectedContracts =
        [
            typeof(AutomationEvents),
            typeof(AutomationHeadingLevel),
            typeof(AutomationLandmarkType),
            typeof(AutomationLiveSetting),
            typeof(AutomationNotificationKind),
            typeof(AutomationNotificationProcessing),
            typeof(AutomationOrientation),
            typeof(AutomationStructureChangeType),
            typeof(AutomationTextEditChangeType),
            typeof(RawElementProviderRuntimeId),
            typeof(AutomationPeerAnnotation),
            typeof(AutomationPeer),
        ];
        foreach (var contract in selectedContracts)
        {
            AssertWinUiContractVersion(contract);
        }
    }

    private static void AssertDeclaredMethods(
        Type interfaceType,
        params ExpectedMethod[] expected)
    {
        var methods = interfaceType
            .GetMethods()
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name)
            .ToArray();
        Assert.Equal(
            expected
                .OrderBy(item => item.Name)
                .Select(item => item.Name),
            methods.Select(method => method.Name));
        foreach (var item in expected)
        {
            var method = Assert.Single(
                methods,
                candidate => candidate.Name == item.Name);
            Assert.Equal(item.ReturnType, method.ReturnType);
            Assert.Equal(
                item.ParameterTypes,
                method
                    .GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .ToArray());
        }
    }

    private static void AssertEnumValues(
        Type enumType,
        params (string Name, int Value)[] expected)
    {
        Assert.True(enumType.IsEnum);
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(enumType));
        Assert.Equal(
            expected.Select(item => item.Name),
            Enum.GetNames(enumType));
        Assert.Equal(
            expected.Select(item => item.Value),
            Enum
                .GetValues(enumType)
                .Cast<object>()
                .Select(Convert.ToInt32));
    }

    private static void AssertReadOnlyProperties(
        Type interfaceType,
        params (string Name, Type Type)[] expected)
    {
        var properties = interfaceType
            .GetProperties()
            .OrderBy(property => property.Name)
            .ToArray();
        Assert.Equal(
            expected
                .OrderBy(item => item.Name)
                .Select(item => item.Name),
            properties.Select(property => property.Name));
        foreach (var item in expected)
        {
            var property = Assert.Single(
                properties,
                candidate => candidate.Name == item.Name);
            Assert.Equal(item.Type, property.PropertyType);
            Assert.True(property.CanRead);
            Assert.False(property.CanWrite);
        }
    }

    private static void AssertMethods(
        Type interfaceType,
        params (string Name, Type ReturnType)[] expected)
    {
        var methods = interfaceType
            .GetMethods()
            .OrderBy(method => method.Name)
            .ToArray();
        Assert.Equal(
            expected
                .OrderBy(item => item.Name)
                .Select(item => item.Name),
            methods.Select(method => method.Name));
        foreach (var item in expected)
        {
            var method = Assert.Single(
                methods,
                candidate => candidate.Name == item.Name);
            Assert.Equal(item.ReturnType, method.ReturnType);
            Assert.Empty(method.GetParameters());
        }
    }

    private static void AssertParameterlessMethod(
        Type interfaceType,
        string methodName,
        Type returnType)
    {
        var method = Assert.Single(interfaceType.GetMethods());
        Assert.Equal(methodName, method.Name);
        Assert.Equal(returnType, method.ReturnType);
        Assert.Empty(method.GetParameters());
        Assert.Empty(interfaceType.GetProperties());
        Assert.Empty(interfaceType.GetEvents());
    }

    private static void AssertWinUiContractVersion(Type type)
    {
        CustomAttributeData attribute = Assert.Single(
            type.GetCustomAttributesData(),
            static candidate =>
                candidate.AttributeType ==
                typeof(ContractVersionAttribute));
        Assert.Equal(
            "Microsoft.UI.Xaml.WinUIContract",
            Assert.IsType<string>(
                attribute.ConstructorArguments[0].Value));
        Assert.Equal(
            0x00010000U,
            Assert.IsType<uint>(
                attribute.ConstructorArguments[1].Value));
    }

    private sealed record ExpectedMethod(
        string Name,
        Type ReturnType,
        params Type[] ParameterTypes);

    private sealed class ProbeAutomationPeer : AutomationPeer
    {
        private readonly string _name;

        public ProbeAutomationPeer(string name) =>
            _name = name;

        public List<AutomationPeer> ChildPeers { get; } = [];

        public bool FocusRequested { get; private set; }

        public bool ContextMenuRequested { get; private set; }

        public IRawElementProviderSimple GetProvider(
            AutomationPeer peer) =>
            ProviderFromPeer(peer);

        public AutomationPeer GetPeer(
            IRawElementProviderSimple provider) =>
            PeerFromProvider(provider);

        protected override string GetNameCore() =>
            _name;

        protected override AutomationControlType
            GetAutomationControlTypeCore() =>
            AutomationControlType.Button;

        protected override IList<AutomationPeer>
            GetChildrenCore() =>
            ChildPeers;

        protected override void SetFocusCore() =>
            FocusRequested = true;

        protected override void ShowContextMenuCore() =>
            ContextMenuRequested = true;
    }
}
