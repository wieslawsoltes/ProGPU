using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using System.Reflection;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

public sealed class AutomationProviderContractTests
{
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
}
