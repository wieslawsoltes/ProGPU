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
                    StringComparison.Ordinal))
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

        if (attributeOwners.Length != 2 || genericOwners.Length != 2)
        {
            throw new InvalidOperationException(
                "Method attributes and generic constraints must retain their complete overload owner signature.");
        }

        Console.WriteLine(
            "WinUI API metadata owner self-test passed for attributes and generic constraints.");
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

    public sealed class OverloadFixture
    {
        [ContractMarker("scalar")]
        public void Apply(int value) => _ = value;

        [ContractMarker("text")]
        public void Apply(string value) => _ = value;

        public void Transform<T>(T value)
            where T : class => _ = value;

        public void Transform<T>(int value)
            where T : struct => _ = value;
    }
}
