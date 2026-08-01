using System.Reflection;

internal static class MetadataApiSurfaceSelfTests
{
    public static int Run()
    {
        MetadataApiSurface surface = MetadataApiSurface.Read(
            Assembly.GetExecutingAssembly().Location,
            ["ProGPU.WinUI.ApiParity.SelfTest"]);

        string[] attributeOwners = surface.Entries
            .Where(static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.Apply`",
                    StringComparison.Ordinal) &&
                !entry.Contains(".parameter[", StringComparison.Ordinal))
            .Select(GetOwner)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] genericOwners = surface.Entries
            .Where(static entry =>
                entry.StartsWith(
                    "generic|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.Transform`",
                    StringComparison.Ordinal))
            .Select(GetOwner)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] parameterAttributeOwners = surface.Entries
            .Where(static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.Apply`",
                    StringComparison.Ordinal) &&
                entry.Contains(".parameter[0]", StringComparison.Ordinal))
            .Select(GetOwner)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] paramArrayOwners = surface.Entries
            .Where(static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.Collect`",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "System.ParamArrayAttribute",
                    StringComparison.Ordinal))
            .Select(GetOwner)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (attributeOwners.Length != 2 ||
            genericOwners.Length != 2 ||
            parameterAttributeOwners.Length != 2 ||
            paramArrayOwners.Length != 1)
        {
            throw new InvalidOperationException(
                "Method, parameter, and params attributes plus generic constraints must retain their complete overload owner signature. " +
                $"Observed method={attributeOwners.Length}, generic={genericOwners.Length}, " +
                $"parameter={parameterAttributeOwners.Length}, params={paramArrayOwners.Length}.");
        }

        Console.WriteLine(
            "WinUI API metadata owner self-test passed for method/parameter attributes, params, and generic constraints.");
        return 0;
    }

    private static string GetOwner(string entry)
    {
        int firstSeparator = entry.IndexOf('|');
        int secondSeparator = entry.IndexOf('|', firstSeparator + 1);
        return entry[(firstSeparator + 1)..secondSeparator];
    }
}

namespace ProGPU.WinUI.ApiParity.SelfTest
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ContractMarkerAttribute(string identity) : Attribute
    {
        public string Identity { get; } = identity;
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class ParameterContractMarkerAttribute(string identity) : Attribute
    {
        public string Identity { get; } = identity;
    }

    public sealed class OverloadFixture
    {
        [ContractMarker("scalar")]
        public void Apply([ParameterContractMarker("scalar")] int value) => _ = value;

        [ContractMarker("text")]
        public void Apply([ParameterContractMarker("text")] string value) => _ = value;

        public void Collect(params int[] values) => _ = values;

        public void Transform<T>(T value)
            where T : class => _ = value;

        public void Transform<T>(int value)
            where T : struct => _ = value;
    }
}
