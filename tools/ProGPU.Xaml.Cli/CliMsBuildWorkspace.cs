using Microsoft.CodeAnalysis.MSBuild;

internal static class CliMsBuildWorkspace
{
    public static MSBuildWorkspace Create()
    {
        var globalProperties =
            GetGlobalProperties();
        return globalProperties.Count == 0
            ? MSBuildWorkspace.Create()
            : MSBuildWorkspace.Create(
                globalProperties);
    }

    public static Dictionary<string, string>
        GetGlobalProperties()
    {
        var frameworkDirectory =
            new DirectoryInfo(AppContext.BaseDirectory);
        var configuration =
            frameworkDirectory.Parent?.Name;
        if (!string.Equals(
                configuration,
                "Debug",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                configuration,
                "Release",
                StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = configuration!
        };
    }
}
