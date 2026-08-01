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
        string[] propertyAttributeOwners = surface.Entries
            .Where(static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.Item(",
                    StringComparison.Ordinal))
            .Select(GetOwner)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool staticPropertyFlags = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "property|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=StaticValue;", StringComparison.Ordinal) &&
                entry.Contains("getstatic=True", StringComparison.Ordinal) &&
                entry.Contains("setstatic=True", StringComparison.Ordinal));
        bool virtualPropertyFlags = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "property|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=VirtualValue;", StringComparison.Ordinal) &&
                entry.Contains("getvirtual=True", StringComparison.Ordinal) &&
                entry.Contains("getnewslot=True", StringComparison.Ordinal) &&
                entry.Contains("setvirtual=True", StringComparison.Ordinal) &&
                entry.Contains("setnewslot=True", StringComparison.Ordinal));

        if (attributeOwners.Length != 2 ||
            genericOwners.Length != 2 ||
            parameterAttributeOwners.Length != 2 ||
            paramArrayOwners.Length != 1 ||
            propertyAttributeOwners.Length != 2 ||
            !staticPropertyFlags ||
            !virtualPropertyFlags)
        {
            throw new InvalidOperationException(
                "Method, parameter, params, and property attributes plus generic constraints must retain their complete overload owner signature. " +
                $"Observed method={attributeOwners.Length}, generic={genericOwners.Length}, " +
                $"parameter={parameterAttributeOwners.Length}, params={paramArrayOwners.Length}, " +
                $"property={propertyAttributeOwners.Length}, " +
                $"staticFlags={staticPropertyFlags}, virtualFlags={virtualPropertyFlags}.");
        }

        Console.WriteLine(
            "WinUI API metadata owner self-test passed for method/parameter/property attributes, property accessor flags, params, and generic constraints.");
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
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
    public sealed class ContractMarkerAttribute(string identity) : Attribute
    {
        public string Identity { get; } = identity;
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class ParameterContractMarkerAttribute(string identity) : Attribute
    {
        public string Identity { get; } = identity;
    }

    public class OverloadFixture
    {
        [ContractMarker("scalar")]
        public void Apply([ParameterContractMarker("scalar")] int value) => _ = value;

        [ContractMarker("text")]
        public void Apply([ParameterContractMarker("text")] string value) => _ = value;

        public void Collect(params int[] values) => _ = values;

        [ContractMarker("integer-index")]
        public int this[int index] => index;

        [ContractMarker("text-index")]
        public int this[string index] => index.Length;

        public static int StaticValue { get; set; }

        public virtual int VirtualValue { get; set; }

        public void Transform<T>(T value)
            where T : class => _ = value;

        public void Transform<T>(int value)
            where T : struct => _ = value;
    }
}
