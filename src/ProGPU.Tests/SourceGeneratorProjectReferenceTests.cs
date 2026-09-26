using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ProGPU.Tests;

// Resolve the real direct project edges through the SDK without compiling,
// restoring, or reading ambient package assets. The APK workflow owns actual
// Android builds and normal package resolution.
public sealed class SourceGeneratorProjectReferenceTests
{
    [Theory]
    [InlineData("ProGPU.WinUI.Themes.Fluent", "android-x64")]
    [InlineData("ProGPU.WinUI.Themes.Fluent", "android-arm64")]
    [InlineData("ProGPU.Samples", "android-x64")]
    [InlineData("ProGPU.Samples", "android-arm64")]
    public async Task HostGeneratorIsRidFreeWhileRuntimeReferencesKeepTargetRid(string projectName, string rid)
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "src", projectName, projectName + ".csproj");
        const string configuration = "AndroidGeneratorReferenceCheck";
        using JsonDocument result = await ResolveReferences(root, project, configuration, rid);

        JsonElement properties = result.RootElement.GetProperty("Properties");
        Assert.Equal(rid, properties.GetProperty("RuntimeIdentifier").GetString());
        Assert.Equal(rid, properties.GetProperty("RuntimeIdentifiers").GetString());
        Assert.Equal("true", properties.GetProperty("ProGpuSamplesMobile").GetString());
        JsonElement items = result.RootElement.GetProperty("Items");
        JsonElement generator = Assert.Single(items.GetProperty("Analyzer").EnumerateArray(),
            item => Path.GetFileName(item.GetProperty("Identity").GetString()) == "ProGPU.Xaml.SourceGenerator.dll");
        Assert.Equal("Analyzer", generator.GetProperty("OutputItemType").GetString());
        Assert.Equal("false", generator.GetProperty("ReferenceOutputAssembly").GetString());
        string[] removedProperties = projectName == "ProGPU.Samples"
            ? ["RuntimeIdentifier", "RuntimeIdentifiers", "ProGpuSamplesMobile"]
            : ["RuntimeIdentifier", "RuntimeIdentifiers"];
        Assert.Equal(removedProperties,
            generator.GetProperty("GlobalPropertiesToRemove").GetString()!.Split(';'));
        Assert.Equal(Path.Combine(root, "src", "ProGPU.Xaml.SourceGenerator", "bin", configuration,
            "netstandard2.0", "ProGPU.Xaml.SourceGenerator.dll"),
            Path.GetFullPath(generator.GetProperty("Identity").GetString()!));

        // These analyzer dependencies must resolve beside the same RID-free host
        // closure, not to previously built Debug/Release or Android artifacts.
        foreach (string dependency in new[] { "ProGPU.Xaml", "ProGPU.Xaml.Roslyn" })
        {
            JsonElement analyzer = Assert.Single(items.GetProperty("Analyzer").EnumerateArray(),
                item => Path.GetFileName(item.GetProperty("Identity").GetString()) == dependency + ".dll");
            Assert.Equal(Path.Combine(root, "src", dependency, "bin", configuration,
                "netstandard2.0", dependency + ".dll"),
                Path.GetFullPath(analyzer.GetProperty("Identity").GetString()!, Path.GetDirectoryName(project)!));
        }

        JsonElement runtime = Assert.Single(items.GetProperty("_ResolvedProjectReferencePaths").EnumerateArray(),
            item => Path.GetFileName(item.GetProperty("Identity").GetString()) == "ProGPU.WinUI.dll");
        Assert.Equal(Path.Combine(root, "src", "ProGPU.WinUI", "bin", configuration,
            "net10.0", rid, "ProGPU.WinUI.dll"), Path.GetFullPath(runtime.GetProperty("Identity").GetString()!));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData(null)]
    public async Task SampleMobileSelectionDoesNotCreateSharedDependencyBuildIdentities(string? mobile)
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "src", "ProGPU.Samples", "ProGPU.Samples.csproj");
        string scratch = Path.Combine(Path.GetTempPath(), "progpu-sample-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string probe = Path.Combine(scratch, "SampleReferenceProbe.targets");
            await File.WriteAllTextAsync(probe, """
                <Project>
                  <Target Name="RequireSampleOnlyMobileSelection" BeforeTargets="GetTargetPath"
                          Condition="'$(MSBuildProjectName)' != 'ProGPU.Samples'">
                    <Error Condition="'$(ProGpuSamplesMobile)' != ''"
                           Text="Sample-only mobile selection leaked into shared project $(MSBuildProjectName)." />
                  </Target>
                </Project>
                """);
            using JsonDocument result = await ResolveReferences(root, project,
                "SampleMobileReferenceCheck", "android-x64", mobile, probe);
            JsonElement properties = result.RootElement.GetProperty("Properties");
            Assert.Equal(mobile ?? "", properties.GetProperty("ProGpuSamplesMobile").GetString());
            string[] sources = result.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
                .Select(item => item.GetProperty("Identity").GetString()!.Replace('\\', '/')).ToArray();
            Assert.Equal(mobile != "true", sources.Contains("Pages/MarkdownPage.cs"));
            Assert.Equal(mobile != "true", sources.Contains("Pages/VisualDesignerPage.cs"));
            Assert.Equal(mobile != "true", sources.Contains("Pages/XamlPlaygroundPage.cs"));
            Assert.NotEmpty(result.RootElement.GetProperty("Items").GetProperty("_ResolvedProjectReferencePaths").EnumerateArray());
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task<JsonDocument> ResolveReferences(string root, string project, string configuration, string rid,
        string? mobile = "true", string? referenceProbe = null)
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "msbuild", project, "-nologo", "-verbosity:quiet", "-nodeReuse:false", "-maxCpuCount:1",
            "-target:ResolveProjectReferences", "-p:BuildProjectReferences=false",
            "-p:SkipResolvePackageAssets=true",
            "-p:Configuration=" + configuration,
            "-p:RuntimeIdentifier=" + rid, "-p:RuntimeIdentifiers=" + rid,
            "-getProperty:RuntimeIdentifier,RuntimeIdentifiers,ProGpuSamplesMobile",
            "-getItem:Analyzer,_ResolvedProjectReferencePaths,Compile"
        })
            start.ArgumentList.Add(argument);
        if (mobile is not null)
            start.ArgumentList.Add("-p:ProGpuSamplesMobile=" + mobile);
        if (referenceProbe is not null)
            start.ArgumentList.Add("-p:CustomAfterMicrosoftCommonTargets=" + referenceProbe);
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanupTimeout.Token);
            throw new TimeoutException($"Generator reference resolution exceeded 45 seconds.\n{await stdout}\n{await stderr}");
        }

        string output = await stdout;
        string diagnostics = output + "\n" + await stderr;
        Assert.True(process.ExitCode == 0, diagnostics);
        int jsonStart = output.IndexOf('{');
        Assert.True(jsonStart >= 0, diagnostics);
        return ParseResult(output[jsonStart..]);
    }

    private static JsonDocument ParseResult(string json)
    {
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
        return JsonDocument.ParseValue(ref reader);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "ProGPU.Samples", "ProGPU.Samples.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the ProGPU source-generator consumers.");
    }
}
