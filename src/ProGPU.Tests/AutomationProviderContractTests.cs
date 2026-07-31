using Microsoft.UI.Xaml.Automation.Provider;
using Xunit;

namespace ProGPU.Tests;

public sealed class AutomationProviderContractTests
{
    [Fact]
    public void SelectedProviderInterfacesMatchOfficialShape()
    {
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
}
