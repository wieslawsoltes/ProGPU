using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
                entry.Contains("ContractMarkerAttribute", StringComparison.Ordinal) &&
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
        bool explicitTypeLayout = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "type|ProGPU.WinUI.ApiParity.SelfTest.LayoutFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "layout=explicit;charset=ansi;pack=2;size=16",
                    StringComparison.Ordinal));
        bool explicitFieldLayout = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "field|ProGPU.WinUI.ApiParity.SelfTest.LayoutFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=Second;", StringComparison.Ordinal) &&
                entry.Contains("order=0;offset=4", StringComparison.Ordinal)) &&
            surface.Entries.Any(
                static entry =>
                    entry.StartsWith(
                        "field|ProGPU.WinUI.ApiParity.SelfTest.LayoutFixture|",
                        StringComparison.Ordinal) &&
                    entry.Contains("name=First;", StringComparison.Ordinal) &&
                    entry.Contains("order=2;offset=0", StringComparison.Ordinal));
        bool privateFieldLayout = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "type|ProGPU.WinUI.ApiParity.SelfTest.LayoutFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "layoutfields=({order=0;offset=4;type=System.Int16;marshal=-}," +
                    "{order=1;offset=2;type=System.Byte;marshal=-}," +
                    "{order=2;offset=0;type=System.Int32;marshal=-})",
                    StringComparison.Ordinal));
        string? marshalLayoutEntry = surface.Entries.FirstOrDefault(
            static entry => entry.StartsWith(
                "type|ProGPU.WinUI.ApiParity.SelfTest.MarshalLayoutFixture|",
                StringComparison.Ordinal));
        bool nestedPrivateValueTypeLayout = marshalLayoutEntry?.Contains(
            "NestedLayout{kind=struct;layout=sequential;",
            StringComparison.Ordinal) == true &&
            marshalLayoutEntry.Contains("charset=unicode;", StringComparison.Ordinal) &&
            marshalLayoutEntry.Contains(
                "type=System.Int16;marshal=-",
                StringComparison.Ordinal) &&
            marshalLayoutEntry.Contains(
                "type=System.Byte;marshal=-",
                StringComparison.Ordinal);
        bool fieldMarshallingDescriptor = marshalLayoutEntry?.Contains(
            "type=System.String;marshal=",
            StringComparison.Ordinal) == true &&
            !marshalLayoutEntry.Contains(
                "type=System.String;marshal=-",
                StringComparison.Ordinal);
        string? methodMarshallingEntry = surface.Entries.FirstOrDefault(
            static entry =>
                entry.StartsWith(
                    "method|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=MarshalContract;", StringComparison.Ordinal));
        bool methodMarshallingDescriptors =
            methodMarshallingEntry?.Contains(
                "returnmetadata=flags=-;",
                StringComparison.Ordinal) == true &&
            methodMarshallingEntry.Contains("marshal=", StringComparison.Ordinal) &&
            !methodMarshallingEntry.Contains(
                "returnmetadata=flags=-;default=-;marshal=-",
                StringComparison.Ordinal) &&
            !methodMarshallingEntry.Contains(":marshal=-", StringComparison.Ordinal);
        string? accessorMarshallingEntry = surface.Entries.FirstOrDefault(
            static entry =>
                entry.StartsWith(
                    "property|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=AccessorMetadata;", StringComparison.Ordinal));
        int setterMetadata = accessorMarshallingEntry?.IndexOf(
            ";setmetadata=",
            StringComparison.Ordinal) ?? -1;
        string getterMetadata = setterMetadata >= 0
            ? accessorMarshallingEntry![..setterMetadata]
            : string.Empty;
        bool accessorMarshallingDescriptor =
            getterMetadata.Contains("marshal=", StringComparison.Ordinal) &&
            !getterMetadata.Contains("marshal=-", StringComparison.Ordinal);
        string? publicFieldMarshallingEntry = surface.Entries.FirstOrDefault(
            static entry =>
                entry.StartsWith(
                    "field|ProGPU.WinUI.ApiParity.SelfTest.SequentialLayoutClass|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=Name;", StringComparison.Ordinal));
        bool publicFieldMarshallingDescriptor =
            publicFieldMarshallingEntry?.Contains("marshal=", StringComparison.Ordinal) == true &&
            !publicFieldMarshallingEntry.Contains("marshal=-", StringComparison.Ordinal);
        bool callerMemberNameAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.SemanticExtensionFixture.Capture`0(System.String,System.String)->System.Void.parameter[1]",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "System.Runtime.CompilerServices.CallerMemberNameAttribute",
                    StringComparison.Ordinal));
        bool extensionMethodAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.SemanticExtensionFixture.Capture`0(System.String,System.String)->System.Void|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "System.Runtime.CompilerServices.ExtensionAttribute",
                    StringComparison.Ordinal));
        bool genericParameterAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.TrimContract`1()->System.Void.generic[0:T]|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute",
                    StringComparison.Ordinal));
        bool signedAttributeConstructor = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.SignedConstructorAttribute`0()->System.Void|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "ConstructorIdentityAttribute;ctor=(System.Int32)->System.Void;",
                    StringComparison.Ordinal));
        bool unsignedAttributeConstructor = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.UnsignedConstructorAttribute`0()->System.Void|",
                    StringComparison.Ordinal) &&
                entry.Contains(
                    "ConstructorIdentityAttribute;ctor=(System.UInt32)->System.Void;",
                    StringComparison.Ordinal));
        bool repeatedAttributeMultiplicity = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.RepeatedAttribute`0()->System.Void|",
                    StringComparison.Ordinal) &&
                entry.Contains("RepeatedContractAttribute;", StringComparison.Ordinal) &&
                entry.EndsWith(";count=2", StringComparison.Ordinal));
        bool eventAccessorMethodAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.AccessorRaised()->System.EventHandler.add|",
                    StringComparison.Ordinal) &&
                entry.Contains("ContractMarkerAttribute", StringComparison.Ordinal));
        bool eventAccessorReturnAttribute = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "attribute|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture.AccessorRaised()->System.EventHandler.add.return(",
                    StringComparison.Ordinal) &&
                entry.Contains("ParameterContractMarkerAttribute", StringComparison.Ordinal));
        bool eventAccessorParameterMetadata = surface.Entries.Any(
            static entry =>
                entry.StartsWith(
                    "event|ProGPU.WinUI.ApiParity.SelfTest.OverloadFixture|",
                    StringComparison.Ordinal) &&
                entry.Contains("name=AccessorRaised;", StringComparison.Ordinal) &&
                entry.Contains("addmetadata=return=(", StringComparison.Ordinal) &&
                entry.Contains(":System.EventHandler:value:-", StringComparison.Ordinal));

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
            !indexerParameterMetadata ||
            !explicitTypeLayout ||
            !explicitFieldLayout ||
            !privateFieldLayout ||
            !nestedPrivateValueTypeLayout ||
            !fieldMarshallingDescriptor ||
            !methodMarshallingDescriptors ||
            !accessorMarshallingDescriptor ||
            !publicFieldMarshallingDescriptor ||
            !callerMemberNameAttribute ||
            !extensionMethodAttribute ||
            !genericParameterAttribute ||
            !signedAttributeConstructor ||
            !unsignedAttributeConstructor ||
            !repeatedAttributeMultiplicity ||
            !eventAccessorMethodAttribute ||
            !eventAccessorReturnAttribute ||
            !eventAccessorParameterMetadata)
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
                $"indexerParameterMetadata={indexerParameterMetadata}, " +
                $"explicitTypeLayout={explicitTypeLayout}, " +
                $"explicitFieldLayout={explicitFieldLayout}, " +
                $"privateFieldLayout={privateFieldLayout}, " +
                $"nestedPrivateValueTypeLayout={nestedPrivateValueTypeLayout}, " +
                $"fieldMarshallingDescriptor={fieldMarshallingDescriptor}, " +
                $"methodMarshallingDescriptors={methodMarshallingDescriptors}, " +
                $"accessorMarshallingDescriptor={accessorMarshallingDescriptor}, " +
                $"publicFieldMarshallingDescriptor={publicFieldMarshallingDescriptor}, " +
                $"callerMemberNameAttribute={callerMemberNameAttribute}, " +
                $"extensionMethodAttribute={extensionMethodAttribute}, " +
                $"genericParameterAttribute={genericParameterAttribute}, " +
                $"signedAttributeConstructor={signedAttributeConstructor}, " +
                $"unsignedAttributeConstructor={unsignedAttributeConstructor}, " +
                $"repeatedAttributeMultiplicity={repeatedAttributeMultiplicity}, " +
                $"eventAccessorMethodAttribute={eventAccessorMethodAttribute}, " +
                $"eventAccessorReturnAttribute={eventAccessorReturnAttribute}, " +
                $"eventAccessorParameterMetadata={eventAccessorParameterMetadata}.");
        }

        Console.WriteLine(
            "API metadata owner self-test passed for overload owners, semantic attributes, accessor metadata, layout, params, generic constraints, and method/property/event dispatch flags.");
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

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ConstructorIdentityAttribute : Attribute
    {
        public ConstructorIdentityAttribute(int value) => _ = value;

        public ConstructorIdentityAttribute(uint value) => _ = value;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RepeatedContractAttribute(int value) : Attribute
    {
        public int Value { get; } = value;
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
            [return: MarshalAs(UnmanagedType.I4)]
            get => 0;
            [ContractMarker("setter")]
            set => _ = value;
        }

        public virtual void VirtualDispatch()
        {
        }

        [return: MarshalAs(UnmanagedType.I4)]
        public int MarshalContract(
            [MarshalAs(UnmanagedType.LPStr)] string value) => value.Length;

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

        public event EventHandler? AccessorRaised
        {
            [ContractMarker("event-add")]
            [return: ParameterContractMarker("event-add-return")]
            add => _virtualRaised += value;
            [ContractMarker("event-remove")]
            remove => _virtualRaised -= value;
        }

        public void Transform<T>(T value)
            where T : class => _ = value;

        public void Transform<T>(int value)
            where T : struct => _ = value;

        public void TrimContract<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
            T>()
        {
        }

        [ConstructorIdentity(1)]
        public void SignedConstructorAttribute()
        {
        }

        [ConstructorIdentity(1u)]
        public void UnsignedConstructorAttribute()
        {
        }

        [RepeatedContract(7)]
        [RepeatedContract(7)]
        public void RepeatedAttribute()
        {
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 2, Size = 16)]
    public struct LayoutFixture
    {
        [FieldOffset(4)]
        public short Second;

        [FieldOffset(2)]
        private byte _hidden;

        [FieldOffset(0)]
        public int First;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MarshalLayoutFixture
    {
        private NestedLayout _nested;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        private string? _name;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct NestedLayout
        {
            public short Value;
            private byte _tail;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public class SequentialLayoutClass
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string Name = string.Empty;
    }

    public static class SemanticExtensionFixture
    {
        public static void Capture(
            this string value,
            [CallerMemberName] string caller = "")
        {
            _ = value;
            _ = caller;
        }
    }
}
