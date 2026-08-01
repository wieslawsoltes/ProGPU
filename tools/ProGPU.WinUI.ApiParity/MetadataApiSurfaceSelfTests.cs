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
                    StringComparison.Ordinal) &&
                !entry.Contains(".get.", StringComparison.Ordinal) &&
                !entry.Contains(".set.", StringComparison.Ordinal))
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
        bool virtualMethodFlags = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "method|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=VirtualDispatch;", StringComparison.Ordinal) &&
                entry.Contains("virtual=True", StringComparison.Ordinal) &&
                entry.Contains("newslot=True", StringComparison.Ordinal));
        bool staticEventFlags = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "event|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=StaticRaised;", StringComparison.Ordinal) &&
                entry.Contains("addstatic=True", StringComparison.Ordinal) &&
                entry.Contains("removestatic=True", StringComparison.Ordinal));
        bool virtualEventFlags = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "event|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=VirtualRaised;", StringComparison.Ordinal) &&
                entry.Contains("addvirtual=True", StringComparison.Ordinal) &&
                entry.Contains("addnewslot=True", StringComparison.Ordinal) &&
                entry.Contains("removevirtual=True", StringComparison.Ordinal) &&
                entry.Contains("removenewslot=True", StringComparison.Ordinal));
        bool accessorMethodAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.AccessorMetadata()->System.Int32.get|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "ContractMarkerAttribute",
                    StringComparison.Ordinal));
        bool accessorReturnAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.AccessorMetadata()->System.Int32.get.return(",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "ParameterContractMarkerAttribute",
                    StringComparison.Ordinal));
        bool indexerParameterAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.Item(System.Int32)->System.Int32.get.parameter[0](System.Int32:index)|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "ParameterContractMarkerAttribute",
                    StringComparison.Ordinal));
        bool indexerParameterMetadata = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "property|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=Item;index=(System.Int32);", StringComparison.Ordinal) &&
                entry.Contains(
                    "getmetadata=return=(",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    ":System.Int32:index:-",
                    StringComparison.Ordinal));

        if (attributeOwners.Length != 2 ||
            genericOwners.Length != 2 ||
            parameterAttributeOwners.Length != 2 ||
            paramArrayOwners.Length != 1 ||
            propertyAttributeOwners.Length != 2 ||
            !staticPropertyFlags ||
            !virtualPropertyFlags ||
            !virtualMethodFlags ||
            !staticEventFlags ||
            !virtualEventFlags ||
            !accessorMethodAttribute ||
            !accessorReturnAttribute ||
            !indexerParameterAttribute ||
            !indexerParameterMetadata)
        {
            throw new InvalidOperationException(
                "Method, parameter, params, and property attributes plus generic constraints must retain their complete overload owner signature. " +
                $"Observed method={attributeOwners.Length}, generic={genericOwners.Length}, " +
                $"parameter={parameterAttributeOwners.Length}, params={paramArrayOwners.Length}, " +
                $"property={propertyAttributeOwners.Length}, " +
                $"staticPropertyFlags={staticPropertyFlags}, " +
                $"virtualPropertyFlags={virtualPropertyFlags}, " +
                $"virtualMethodFlags={virtualMethodFlags}, " +
                $"staticEventFlags={staticEventFlags}, " +
                $"virtualEventFlags={virtualEventFlags}, " +
                $"accessorMethodAttribute={accessorMethodAttribute}, " +
                $"accessorReturnAttribute={accessorReturnAttribute}, " +
                $"indexerParameterAttribute={indexerParameterAttribute}, " +
                $"indexerParameterMetadata={indexerParameterMetadata}.");
        }

        Console.WriteLine(
            "WinUI API metadata owner self-test passed for overload owners, semantic attributes, accessor metadata, params, generic constraints, and method/property/event dispatch flags.");
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

    [AttributeUsage(
        AttributeTargets.Parameter |
        AttributeTargets.ReturnValue)]
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
        public int this[
            [ParameterContractMarker("integer-index-parameter")]
            int index] => index;

        [ContractMarker("text-index")]
        public int this[string index] => index.Length;

        public static int StaticValue { get; set; }

        public virtual int VirtualValue { get; set; }

        public int AccessorMetadata
        {
            [ContractMarker("getter")]
            [return: ParameterContractMarker("getter-return")]
            get => 0;
            [ContractMarker("setter")]
            set => _ = value;
        }

        public virtual void VirtualDispatch()
        {
        }

        private static EventHandler? s_staticRaised;
        private EventHandler? _virtualRaised;

        public static event EventHandler? StaticRaised
        {
            add => s_staticRaised += value;
            remove => s_staticRaised -= value;
        }

        public virtual event EventHandler? VirtualRaised
        {
            add => _virtualRaised += value;
            remove => _virtualRaised -= value;
        }

        public void Transform<T>(T value)
            where T : class => _ = value;

        public void Transform<T>(int value)
            where T : struct => _ = value;
    }
}
