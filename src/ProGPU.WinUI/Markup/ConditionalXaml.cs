using System;
using System.Globalization;
using System.Threading;

namespace Microsoft.UI.Xaml.Markup;

/// <summary>
/// Typed capability boundary used by compiled conditional XAML. Hosts can provide operating
/// system or platform metadata without coupling the framework-neutral compiler to WinRT.
/// </summary>
public interface IConditionalXamlPredicateEvaluator
{
    bool IsApiContractPresent(string contractName, ushort majorVersion, ushort minorVersion);

    bool IsTypePresent(string typeName);

    bool IsPropertyPresent(string typeName, string propertyName);
}

/// <summary>Executes the standard WinUI conditional-XAML predicate vocabulary.</summary>
public static class ConditionalXaml
{
    private static IConditionalXamlPredicateEvaluator _evaluator =
        EmptyConditionalXamlPredicateEvaluator.Instance;

    public static IConditionalXamlPredicateEvaluator Evaluator
    {
        get => Volatile.Read(ref _evaluator);
        set => Volatile.Write(
            ref _evaluator,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static bool IsEnabled(string method, params string[] arguments)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        var evaluator = Evaluator;
        switch (method)
        {
            case "IsApiContractPresent":
                ParseApiContract(arguments, out var contract, out var major, out var minor);
                return evaluator.IsApiContractPresent(contract, major, minor);
            case "IsApiContractNotPresent":
                ParseApiContract(arguments, out contract, out major, out minor);
                return !evaluator.IsApiContractPresent(contract, major, minor);
            case "IsTypePresent":
                RequireArity(method, arguments, 1);
                return evaluator.IsTypePresent(arguments[0]);
            case "IsTypeNotPresent":
                RequireArity(method, arguments, 1);
                return !evaluator.IsTypePresent(arguments[0]);
            case "IsPropertyPresent":
                RequireArity(method, arguments, 2);
                return evaluator.IsPropertyPresent(arguments[0], arguments[1]);
            case "IsPropertyNotPresent":
                RequireArity(method, arguments, 2);
                return !evaluator.IsPropertyPresent(arguments[0], arguments[1]);
            default:
                throw new NotSupportedException(
                    $"Conditional XAML predicate '{method}' is not supported.");
        }
    }

    private static void ParseApiContract(
        string[] arguments,
        out string contractName,
        out ushort majorVersion,
        out ushort minorVersion)
    {
        if (arguments.Length != 2 && arguments.Length != 3)
            throw new FormatException(
                "An API-contract predicate requires a contract name, major version, and optional minor version.");
        contractName = arguments[0];
        majorVersion = ParseVersion(arguments[1]);
        minorVersion = arguments.Length == 3 ? ParseVersion(arguments[2]) : (ushort)0;
    }

    private static ushort ParseVersion(string value)
    {
        if (!ushort.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result))
            throw new FormatException(
                $"Conditional XAML version '{value}' is not an unsigned 16-bit integer.");
        return result;
    }

    private static void RequireArity(
        string method,
        string[] arguments,
        int expected)
    {
        if (arguments.Length != expected)
            throw new FormatException(
                $"Conditional XAML predicate '{method}' requires {expected} argument(s).");
    }

    private sealed class EmptyConditionalXamlPredicateEvaluator :
        IConditionalXamlPredicateEvaluator
    {
        public static readonly EmptyConditionalXamlPredicateEvaluator Instance = new();

        public bool IsApiContractPresent(
            string contractName,
            ushort majorVersion,
            ushort minorVersion) => false;

        public bool IsTypePresent(string typeName) => false;

        public bool IsPropertyPresent(string typeName, string propertyName) => false;
    }
}
