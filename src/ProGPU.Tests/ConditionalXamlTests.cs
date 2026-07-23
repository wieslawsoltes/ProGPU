using Microsoft.UI.Xaml.Markup;
using Xunit;

namespace ProGPU.Tests;

public sealed class ConditionalXamlTests
{
    [Fact]
    public void StandardPredicatesDispatchToTypedEvaluatorAndNegateExactly()
    {
        var previous = ConditionalXaml.Evaluator;
        try
        {
            ConditionalXaml.Evaluator = new TestEvaluator();

            Assert.True(ConditionalXaml.IsEnabled(
                "IsApiContractPresent",
                "Windows.Foundation.UniversalApiContract",
                "8"));
            Assert.False(ConditionalXaml.IsEnabled(
                "IsApiContractNotPresent",
                "Windows.Foundation.UniversalApiContract",
                "8",
                "0"));
            Assert.True(ConditionalXaml.IsEnabled(
                "IsTypePresent",
                "Microsoft.UI.Xaml.Controls.Button"));
            Assert.False(ConditionalXaml.IsEnabled(
                "IsTypeNotPresent",
                "Microsoft.UI.Xaml.Controls.Button"));
            Assert.True(ConditionalXaml.IsEnabled(
                "IsPropertyPresent",
                "Microsoft.UI.Xaml.Controls.Button",
                "Content"));
            Assert.False(ConditionalXaml.IsEnabled(
                "IsPropertyNotPresent",
                "Microsoft.UI.Xaml.Controls.Button",
                "Content"));
        }
        finally
        {
            ConditionalXaml.Evaluator = previous;
        }
    }

    [Fact]
    public void UnknownAndMalformedPredicatesFailLoudly()
    {
        Assert.Throws<NotSupportedException>(() =>
            ConditionalXaml.IsEnabled("UnknownPredicate", "value"));
        Assert.Throws<FormatException>(() =>
            ConditionalXaml.IsEnabled(
                "IsApiContractPresent",
                "Windows.Foundation.UniversalApiContract",
                "not-a-version"));
        Assert.Throws<FormatException>(() =>
            ConditionalXaml.IsEnabled("IsPropertyPresent", "TypeOnly"));
    }

    private sealed class TestEvaluator : IConditionalXamlPredicateEvaluator
    {
        public bool IsApiContractPresent(
            string contractName,
            ushort majorVersion,
            ushort minorVersion) =>
            contractName == "Windows.Foundation.UniversalApiContract" &&
            majorVersion == 8 &&
            minorVersion == 0;

        public bool IsTypePresent(string typeName) =>
            typeName == "Microsoft.UI.Xaml.Controls.Button";

        public bool IsPropertyPresent(string typeName, string propertyName) =>
            typeName == "Microsoft.UI.Xaml.Controls.Button" &&
            propertyName == "Content";
    }
}
