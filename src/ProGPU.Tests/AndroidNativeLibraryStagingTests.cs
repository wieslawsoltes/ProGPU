using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ProGPU.Tests;

// These are executable MSBuild metadata tests, not native binary or APK tests.
// Every .so below is a text marker and must never be loaded or deployed.
public sealed class AndroidNativeLibraryStagingTests
{
    public static IEnumerable<object[]> NativeLayouts()
    {
        foreach (string family in new[] { "wgpu", "dawn", "engine" })
        foreach (string layout in new[] { "source", "package", "runtimes" })
        foreach (string rid in new[] { "android-arm64", "android-x64" })
            yield return [family, layout, rid];
    }

    [Theory]
    [MemberData(nameof(NativeLayouts))]
    public async Task StagesExactRequestedFamilyAndAbi(string family, string layout, string rid)
    {
        using StagingFixture fixture = new();
        fixture.Properties["RuntimeIdentifier"] = rid;
        string expected = fixture.AddLibrary(family, layout, rid);
        fixture.AddLibrary(family, layout, OtherRid(rid));
        fixture.AddMarker(expected + ".backup.so");

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        AssertLibrary(Assert.Single(result.Items), expected, rid);
        if (family == "engine")
            Assert.Contains("warning PROGPUANDROID001", result.Output, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("PROGPUANDROID", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("package")]
    public async Task HigherPriorityLayoutWinsWhenLowerPriorityLayoutsAlsoExist(string preferredLayout)
    {
        using StagingFixture fixture = new();
        fixture.Properties["RuntimeIdentifier"] = "android-x64";
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = "true";
        List<string> expected = [];
        foreach (string family in new[] { "wgpu", "dawn", "engine" })
        {
            expected.Add(fixture.AddLibrary(family, preferredLayout, "android-x64"));
            fixture.AddLibrary(family, "package", "android-x64");
            fixture.AddLibrary(family, "runtimes", "android-x64");
        }

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        Assert.Equal(3, result.Items.Length);
        foreach (string path in expected)
            AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, path)), path, "android-x64");
        Assert.DoesNotContain("PROGPUANDROID", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrictMediaRejectsWrongAbiDawnEvenWithRequestedWgpuAndEngine()
    {
        using StagingFixture fixture = new();
        fixture.Properties["RuntimeIdentifier"] = "android-x64";
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = "true";
        fixture.AddLibrary("dawn", "source", "android-arm64");
        fixture.AddLibrary("wgpu", "source", "android-x64");
        fixture.AddLibrary("engine", "package", "android-x64");
        string existing = fixture.AddMarker(Path.Combine(fixture.Root, "caller", "libcaller.so"));
        fixture.AddExistingLibrary(existing, "android-x64", "caller-owned");

        BuildResult result = await fixture.RunAsync();

        result.RequireFailure("PROGPUANDROID002");
        Assert.Contains("android-x64 (x86_64)", result.Output, StringComparison.Ordinal);
        Assert.Contains("libwebgpu_dawn.so", result.Output, StringComparison.Ordinal);
        NativeItem retained = Assert.Single(result.Items);
        AssertLibrary(retained, existing, "android-x64");
        Assert.Equal("caller-owned", retained.Ownership);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialMultiRidPayloadReportsTheMissingAbi(bool strict)
    {
        using StagingFixture fixture = new();
        fixture.Properties["RuntimeIdentifiers"] = "android-arm64;android-x64";
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = strict.ToString();
        string expected = fixture.AddLibrary("dawn", "package", "android-x64");

        BuildResult result = await fixture.RunAsync();

        Assert.Contains("android-arm64 (arm64-v8a)", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("for android-x64 (x86_64)", result.Output, StringComparison.Ordinal);
        if (strict)
        {
            result.RequireFailure("PROGPUANDROID002");
            Assert.Empty(result.Items);
        }
        else
        {
            result.RequireSuccess();
            Assert.Contains("warning PROGPUANDROID001", result.Output, StringComparison.Ordinal);
            AssertLibrary(Assert.Single(result.Items), expected, "android-x64");
        }
    }

    [Fact]
    public async Task PackageEngineDefaultIsResolvedWithLateAndroidPlatformAndRid()
    {
        using StagingFixture fixture = new();
        fixture.Properties["TargetPlatformIdentifier"] = string.Empty;
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = "true";
        fixture.LateProperties["TargetPlatformIdentifier"] = "android";
        fixture.LateProperties["RuntimeIdentifier"] = "android-x64";
        string dawn = fixture.AddLibrary("dawn", "source", "android-x64");
        string engine = fixture.AddDefaultPackageEngine("android-x64");
        fixture.AddDefaultPackageEngine("android-arm64");

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        Assert.Equal(2, result.Items.Length);
        AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, dawn)), dawn, "android-x64");
        AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, engine)), engine, "android-x64");
        Assert.DoesNotContain("PROGPUANDROID", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task OuterBuildUsesPluralRidsAndPerRidPublishUsesSingular(bool innerBuild, int expectedCount)
    {
        using StagingFixture fixture = new();
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = "true";
        fixture.Properties["_ComputeFilesToPublishForRuntimeIdentifiers"] = innerBuild.ToString();
        fixture.AddLibrary("dawn", "source", "android-arm64");
        string x64 = fixture.AddLibrary("dawn", "source", "android-x64");

        // Exercise CLI escaping too: MSBuild otherwise interprets semicolons as
        // property separators before the target ever receives the plural list.
        BuildResult result = await fixture.RunAsync(
            "-p:RuntimeIdentifier=android-x64",
            "-p:RuntimeIdentifiers=android-arm64%3Bandroid-x64");

        result.RequireSuccess();
        Assert.Equal(expectedCount, result.Items.Length);
        AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, x64)), x64, "android-x64");
        if (!innerBuild)
            Assert.Single(result.Items, item => item.RuntimeIdentifier == "android-arm64" && item.Abi == "arm64-v8a");
        Assert.DoesNotContain("PROGPUANDROID", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RidlessLibraryStagesBothSupportedAbis()
    {
        using StagingFixture fixture = new();
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = "true";
        string arm64 = fixture.AddLibrary("dawn", "source", "android-arm64");
        string x64 = fixture.AddLibrary("dawn", "source", "android-x64");

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        Assert.Equal(2, result.Items.Length);
        AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, arm64)), arm64, "android-arm64");
        AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, x64)), x64, "android-x64");
    }

    [Fact]
    public async Task DuplicateRequestedRidsDoNotDuplicateNativeItems()
    {
        using StagingFixture fixture = new();
        fixture.Properties["RuntimeIdentifiers"] = "android-x64;android-arm64;android-x64;android-arm64";
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = "true";
        string arm64 = fixture.AddLibrary("dawn", "package", "android-arm64");
        string x64 = fixture.AddLibrary("dawn", "package", "android-x64");

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        Assert.Equal(2, result.Items.Length);
        AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, arm64)), arm64, "android-arm64");
        AssertLibrary(Assert.Single(result.Items, item => SamePath(item.Path, x64)), x64, "android-x64");
    }

    [Theory]
    [InlineData("android-x86")]
    [InlineData("linux-x64")]
    public async Task UnsupportedRidFailsExplicitly(string rid)
    {
        using StagingFixture fixture = new();
        fixture.Properties["RuntimeIdentifier"] = rid;

        BuildResult result = await fixture.RunAsync();

        result.RequireFailure("PROGPUANDROID003");
        Assert.Contains($"RuntimeIdentifier '{rid}'", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ExistingNativeItemsArePreservedWithoutDuplicateStaging()
    {
        using StagingFixture fixture = new();
        fixture.Properties["RuntimeIdentifier"] = "android-x64";
        string dawn = fixture.AddLibrary("dawn", "source", "android-x64");
        string caller = fixture.AddMarker(Path.Combine(fixture.Root, "caller", "libcaller.so"));
        fixture.AddExistingLibrary(dawn, "android-x64", "already-staged");
        fixture.AddExistingLibrary(caller, "android-arm64", "caller-owned");

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        Assert.Equal(2, result.Items.Length);
        NativeItem staged = Assert.Single(result.Items, item => SamePath(item.Path, dawn));
        AssertLibrary(staged, dawn, "android-x64");
        Assert.Equal("already-staged", staged.Ownership);
        NativeItem retained = Assert.Single(result.Items, item => SamePath(item.Path, caller));
        AssertLibrary(retained, caller, "android-arm64");
        Assert.Equal("caller-owned", retained.Ownership);
    }

    [Fact]
    public async Task NonAndroidTargetDoesNotStageOrValidateAndroidLibraries()
    {
        using StagingFixture fixture = new();
        fixture.Properties["TargetPlatformIdentifier"] = "linux";
        fixture.Properties["RuntimeIdentifier"] = "linux-x64";
        fixture.Properties["ProGpuRequireZeroCopyMedia"] = "true";
        fixture.AddLibrary("wgpu", "source", "android-x64");
        string existing = fixture.AddMarker(Path.Combine(fixture.Root, "caller", "libcaller.so"));
        fixture.AddExistingLibrary(existing, "android-arm64", "caller-owned");

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        AssertLibrary(Assert.Single(result.Items), existing, "android-arm64");
        Assert.DoesNotContain("PROGPUANDROID", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("android-arm64", false)]
    [InlineData("android-x64", false)]
    [InlineData("android-arm64", true)]
    [InlineData("android-x64", true)]
    public async Task ScheduledStagingPrecedesAndroidLibraryCategorization(string rid, bool application)
    {
        using StagingFixture fixture = new() { ObserveScheduledBuild = true };
        fixture.Properties["TargetPlatformIdentifier"] = string.Empty;
        fixture.Properties["AndroidApplication"] = application.ToString();
        fixture.LateProperties["TargetPlatformIdentifier"] = "android";
        fixture.LateProperties["RuntimeIdentifier"] = rid;
        List<string> expected = [];
        foreach (string family in new[] { "wgpu", "dawn", "engine" })
        {
            expected.Add(fixture.AddLibrary(family, "source", rid));
            fixture.AddLibrary(family, "source", OtherRid(rid));
        }
        string caller = fixture.AddMarker(Path.Combine(fixture.Root, "caller", "libcaller.so"));
        fixture.AddExistingLibrary(caller, rid, "caller-owned");

        BuildResult result = await fixture.RunAsync();

        result.RequireSuccess();
        Assert.Equal(4, result.CategorizedItems.Length);
        Assert.Equal(result.Items, result.CategorizedItems);
        foreach (string path in expected)
            AssertLibrary(Assert.Single(result.CategorizedItems, item => SamePath(item.Path, path)), path, rid);
        Assert.Equal("caller-owned", Assert.Single(result.CategorizedItems, item => SamePath(item.Path, caller)).Ownership);
        Assert.DoesNotContain("PROGPUANDROID", result.Output, StringComparison.Ordinal);
    }

    private static string Abi(string rid) => rid == "android-arm64" ? "arm64-v8a" : "x86_64";
    private static string OtherRid(string rid) => rid == "android-arm64" ? "android-x64" : "android-arm64";
    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void AssertLibrary(NativeItem item, string path, string rid)
    {
        Assert.True(SamePath(item.Path, path), $"Expected '{path}', got '{item.Path}'.");
        Assert.Equal(Abi(rid), item.Abi);
        Assert.Equal(rid, item.RuntimeIdentifier);
    }

    private sealed record NativeItem(string Path, string Abi, string RuntimeIdentifier, string Ownership);

    private sealed record BuildResult(int ExitCode, string Output, NativeItem[] Items, NativeItem[] CategorizedItems)
    {
        public void RequireSuccess() => Assert.True(ExitCode == 0, Output);

        public void RequireFailure(string code)
        {
            Assert.True(ExitCode != 0, Output);
            Assert.Contains($"error {code}", Output, StringComparison.Ordinal);
        }
    }

    private sealed class StagingFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "progpu android metadata " + Guid.NewGuid().ToString("N"));
        public Dictionary<string, string> Properties { get; } = new()
        {
            ["TargetPlatformIdentifier"] = "android",
            ["RuntimeIdentifier"] = string.Empty,
            ["RuntimeIdentifiers"] = string.Empty,
            ["ProGpuRequireZeroCopyMedia"] = "false",
            ["ProGpuWgpuNativeAndroidRoot"] = string.Empty,
            ["ProGpuDawnAndroidRoot"] = string.Empty,
            ["ProGpuNativeDawnAndroidRoot"] = string.Empty,
            ["_ComputeFilesToPublishForRuntimeIdentifiers"] = "false"
        };
        public Dictionary<string, string> LateProperties { get; } = [];
        public bool ObserveScheduledBuild { get; init; }
        private readonly List<XElement> _existingItems = [];
        private readonly string _targets;
        private readonly string _repositoryRoot;

        public StagingFixture()
        {
            _repositoryRoot = FindRepositoryRoot();
            _targets = Path.Combine(Root, "package", "buildTransitive", "ProGPU.Android.targets");
            Directory.CreateDirectory(Path.GetDirectoryName(_targets)!);
            // An exact copy recreates the package-relative runtimes directory
            // without writing fixtures into the repository or a NuGet cache.
            File.Copy(Path.Combine(_repositoryRoot, "src", "ProGPU.Android", "buildTransitive", "ProGPU.Android.targets"), _targets);
        }

        public string AddLibrary(string family, string layout, string rid)
        {
            (string property, string file) = family switch
            {
                "wgpu" => ("ProGpuWgpuNativeAndroidRoot", "libwgpu_native.so"),
                "dawn" => ("ProGpuDawnAndroidRoot", "libwebgpu_dawn.so"),
                "engine" => ("ProGpuNativeDawnAndroidRoot", "libprogpu_native_dawn.so"),
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            };
            string root = Path.Combine(Root, family);
            Properties[property] = root;
            return AddMarker(layout switch
            {
                "source" => Path.Combine(root, "lib", Abi(rid), file),
                "package" => Path.Combine(root, "runtimes", rid, "native", file),
                "runtimes" => Path.Combine(root, rid, "native", file),
                _ => throw new ArgumentOutOfRangeException(nameof(layout))
            });
        }

        public string AddDefaultPackageEngine(string rid) => AddMarker(
            Path.Combine(Root, "package", "runtimes", rid, "native", "libprogpu_native_dawn.so"));

        public string AddMarker(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "ProGPU MSBuild metadata-only test marker. NOT A NATIVE BINARY.\n");
            return path;
        }

        public void AddExistingLibrary(string path, string rid, string ownership) => _existingItems.Add(
            new XElement("AndroidNativeLibrary", new XAttribute("Include", path),
                new XElement("Abi", Abi(rid)), new XElement("RuntimeIdentifier", rid),
                new XElement("FixtureOwnership", ownership)));

        public async Task<BuildResult> RunAsync(params string[] arguments)
        {
            string project = Path.Combine(Root, "staging.proj");
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup", Properties.Select(pair => new XElement(pair.Key, pair.Value))),
                new XElement("ItemGroup", _existingItems),
                new XElement("Import", new XAttribute("Project", _targets)),
                new XElement("Target", new XAttribute("Name", "SetLateAndroidProperties"),
                    new XElement("PropertyGroup", LateProperties.Select(pair => new XElement(pair.Key, pair.Value)))),
                // Observe item availability at the Android SDK's first library
                // consumer, before its later checks/PrepareForBuild. This is an
                // original scheduling probe, not a substitute Android packager.
                new XElement("Target", new XAttribute("Name", "_CategorizeAndroidLibraries"),
                    new XElement("ItemGroup", new XElement("FixtureCategorizedNativeLibrary",
                        new XAttribute("Include", "@(AndroidNativeLibrary)")))),
                new XElement("Target", new XAttribute("Name", "PrepareForBuild")),
                new XElement("Target", new XAttribute("Name", "_CheckProjectItems")),
                new XElement("Target", new XAttribute("Name", "_BuildLibraryImportsCache"))))
                .Save(project);

            ProcessStartInfo start = new()
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkingDirectory = _repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in new[]
            {
                "msbuild", project, "-nologo", "-verbosity:quiet", "-nodeReuse:false", "-maxCpuCount:1",
                ObserveScheduledBuild
                    ? "-target:SetLateAndroidProperties,_CategorizeAndroidLibraries,_CheckProjectItems,PrepareForBuild,_BuildLibraryImportsCache"
                    : "-target:SetLateAndroidProperties,ProGpuStageAndroidNativeLibraries",
                "-getItem:AndroidNativeLibrary,FixtureCategorizedNativeLibrary"
            })
                start.ArgumentList.Add(argument);
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
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
                throw new TimeoutException($"Android staging MSBuild exceeded 45 seconds.\n{await stdout}\n{await stderr}");
            }

            string standardOutput = await stdout;
            string output = standardOutput + "\n" + await stderr;
            // -getItem also publishes actual item state after a failed target.
            // Never fabricate an empty array for failure/atomicity assertions.
            return new(process.ExitCode, output,
                ReadItems(standardOutput, output, "AndroidNativeLibrary"),
                ReadItems(standardOutput, output, "FixtureCategorizedNativeLibrary"));
        }

        private static NativeItem[] ReadItems(string standardOutput, string diagnosticOutput, string itemName)
        {
            int jsonStart = standardOutput.IndexOf('{');
            Assert.True(jsonStart >= 0, diagnosticOutput);
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(standardOutput[jsonStart..]));
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            return json.RootElement.GetProperty("Items").GetProperty(itemName)
                .EnumerateArray().Select(item => new NativeItem(
                    item.GetProperty("Identity").GetString()!,
                    item.GetProperty("Abi").GetString()!,
                    item.GetProperty("RuntimeIdentifier").GetString()!,
                    item.TryGetProperty("FixtureOwnership", out JsonElement ownership) ? ownership.GetString()! : string.Empty))
                .ToArray();
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string FindRepositoryRoot()
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "ProGPU.Android", "buildTransitive", "ProGPU.Android.targets")))
                    return directory.FullName;
            throw new DirectoryNotFoundException("Could not locate the ProGPU Android staging target.");
        }
    }
}
