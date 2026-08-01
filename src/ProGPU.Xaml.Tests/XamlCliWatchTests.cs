using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlCliWatchTests
{
    [Fact]
    public async Task WatchCommandPublishesInitialArtifactAndAcceptsNoOpReload()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration =
            new DirectoryInfo(
                AppContext.BaseDirectory)
                .Parent?.Name ??
            throw new InvalidOperationException(
                "The test configuration directory could not be located.");
        var cliAssembly = Path.Combine(
            repositoryRoot,
            "tools",
            "ProGPU.Xaml.Cli",
            "bin",
            configuration,
            "net10.0",
            "ProGPU.Xaml.Cli.dll");
        var project = Path.Combine(
            repositoryRoot,
            "src",
            "ProGPU.Samples",
            "ProGPU.Samples.csproj");
        var xaml = Path.Combine(
            repositoryRoot,
            "src",
            "ProGPU.Samples",
            "Pages",
            "XamlCompilerWelcomePage.xaml");
        Assert.True(
            File.Exists(cliAssembly),
            "The CLI build output was not found at " +
            cliAssembly);
        Assert.True(File.Exists(project));
        Assert.True(File.Exists(xaml));

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ProGPU.Xaml.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(
            temporaryDirectory);
        var output = Path.Combine(
            temporaryDirectory,
            "preview.dll");
        using var process = new Process
        {
            StartInfo =
                CreateStartInfo(
                    cliAssembly,
                    xaml,
                    project,
                    output)
        };
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromMinutes(1));
        try
        {
            Assert.True(process.Start());
            var initialLine =
                await process.StandardOutput
                    .ReadLineAsync(
                        timeout.Token);
            var initial =
                ParseResult(initialLine);
            Assert.Equal("1.0", initial.ProtocolVersion);
            Assert.Equal(
                "Applied",
                initial.Status);
            Assert.Equal(
                "Accepted",
                initial.CommitResult);
            Assert.True(
                initial.ArtifactWritten);
            Assert.Equal(1, initial.Submitted);
            Assert.Equal(1, initial.Completed);
            Assert.Equal(1, initial.Applied);
            Assert.Equal(0, initial.CacheHits);
            Assert.Equal(0, initial.CurrentQueueDepth);
            Assert.Equal(1, initial.MaximumQueueDepth);
            Assert.Equal(
                1,
                initial.AllocationMeasurements);
            Assert.True(
                initial.LastAllocatedBytes >= 0);
            Assert.True(
                initial.AverageDurationMilliseconds >
                0);
            Assert.True(
                initial.P95DurationUpperBoundMilliseconds >=
                initial.MedianDurationUpperBoundMilliseconds);
            Assert.True(
                initial.P99DurationUpperBoundMilliseconds >=
                initial.P95DurationUpperBoundMilliseconds);
            Assert.Equal(
                "InsufficientSamples",
                initial.PerformanceBudgetStatus);
            Assert.True(File.Exists(output));
            var initialImage =
                await File.ReadAllBytesAsync(
                    output,
                    timeout.Token);
            Assert.NotEmpty(initialImage);

            await process.StandardInput
                .WriteLineAsync(
                    "reload".AsMemory(),
                    timeout.Token);
            await process.StandardInput
                .FlushAsync();
            var reloadLine =
                await process.StandardOutput
                    .ReadLineAsync(
                        timeout.Token);
            var reload =
                ParseResult(reloadLine);
            Assert.Equal("1.0", reload.ProtocolVersion);
            Assert.Equal(
                "AcceptedWithoutRuntimeChange",
                reload.Status);
            Assert.Equal(
                "Accepted",
                reload.CommitResult);
            Assert.Equal("None", reload.Mode);
            Assert.Equal("None", reload.Action);
            Assert.False(
                reload.ArtifactWritten);
            Assert.Equal(2, reload.Submitted);
            Assert.Equal(2, reload.Completed);
            Assert.Equal(1, reload.Applied);
            Assert.Equal(1, reload.CacheHits);
            Assert.Equal(0, reload.CurrentQueueDepth);
            Assert.Equal(1, reload.MaximumQueueDepth);
            Assert.Equal(
                2,
                reload.AllocationMeasurements);
            Assert.True(
                reload.LastAllocatedBytes >= 0);
            Assert.True(
                reload.AverageAllocatedBytes >= 0);
            Assert.True(
                reload.P95AllocatedBytesUpperBound >=
                reload.MedianAllocatedBytesUpperBound);
            Assert.True(
                reload.P99AllocatedBytesUpperBound >=
                reload.P95AllocatedBytesUpperBound);
            Assert.Equal(
                "Passed",
                reload.PerformanceBudgetStatus);
            Assert.Equal(
                initial.Generation + 1,
                reload.Generation);
            Assert.Equal(
                initialImage,
                await File.ReadAllBytesAsync(
                    output,
                    timeout.Token));

            process.StandardInput.Close();
            await process.WaitForExitAsync(
                timeout.Token);
            var error =
                await process.StandardError
                    .ReadToEndAsync(
                        timeout.Token);
            Assert.True(
                process.ExitCode == 0,
                "CLI watch failed with exit code " +
                process.ExitCode +
                Environment.NewLine +
                error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Directory.Delete(
                temporaryDirectory,
                recursive: true);
        }
    }

    [Fact]
    public async Task WatchCommandFailsBoundedRunWithInsufficientBudgetSamples()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration =
            new DirectoryInfo(
                AppContext.BaseDirectory)
                .Parent?.Name ??
            throw new InvalidOperationException(
                "The test configuration directory could not be located.");
        var cliAssembly = Path.Combine(
            repositoryRoot,
            "tools",
            "ProGPU.Xaml.Cli",
            "bin",
            configuration,
            "net10.0",
            "ProGPU.Xaml.Cli.dll");
        var project = Path.Combine(
            repositoryRoot,
            "src",
            "ProGPU.Samples",
            "ProGPU.Samples.csproj");
        var xaml = Path.Combine(
            repositoryRoot,
            "src",
            "ProGPU.Samples",
            "Pages",
            "XamlCompilerWelcomePage.xaml");
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ProGPU.Xaml.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(
            temporaryDirectory);
        var output = Path.Combine(
            temporaryDirectory,
            "preview.dll");
        using var process = new Process
        {
            StartInfo =
                CreateStartInfo(
                    cliAssembly,
                    xaml,
                    project,
                    output,
                    maximumUpdates: 1,
                    minimumBudgetSamples: 2)
        };
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromMinutes(1));
        try
        {
            Assert.True(process.Start());
            var result = ParseResult(
                await process.StandardOutput
                    .ReadLineAsync(timeout.Token));
            await process.WaitForExitAsync(
                timeout.Token);
            var error =
                await process.StandardError
                    .ReadToEndAsync(timeout.Token);

            Assert.Equal(
                "InsufficientSamples",
                result.PerformanceBudgetStatus);
            Assert.Equal(1, result.Completed);
            Assert.Equal(1, process.ExitCode);
            Assert.DoesNotContain(
                "PGXAMLCLI",
                error,
                StringComparison.Ordinal);
            Assert.True(File.Exists(output));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Directory.Delete(
                temporaryDirectory,
                recursive: true);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string cliAssembly,
        string xaml,
        string project,
        string output,
        int maximumUpdates = 2,
        int minimumBudgetSamples = 2)
    {
        var startInfo =
            new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        startInfo.ArgumentList.Add(
            cliAssembly);
        startInfo.ArgumentList.Add("watch");
        startInfo.ArgumentList.Add(xaml);
        startInfo.ArgumentList.Add(
            "--project");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add(
            "--output");
        startInfo.ArgumentList.Add(output);
        startInfo.ArgumentList.Add("--stdin");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add(
            "--max-updates");
        startInfo.ArgumentList.Add(
            maximumUpdates.ToString(
                System.Globalization
                    .CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            "--budget-min-samples");
        startInfo.ArgumentList.Add(
            minimumBudgetSamples.ToString(
                System.Globalization
                    .CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            "--max-p95-ms");
        startInfo.ArgumentList.Add("60000");
        startInfo.ArgumentList.Add(
            "--max-p95-allocated-bytes");
        startInfo.ArgumentList.Add(
            long.MaxValue.ToString(
                System.Globalization
                    .CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static WatchResult ParseResult(
        string? line)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(line),
            "The CLI watch process did not produce a JSON result.");
        using var document =
            JsonDocument.Parse(line!);
        var root = document.RootElement;
        var telemetry =
            root.GetProperty("telemetry");
        var performanceBudget =
            root.GetProperty(
                "performanceBudget");
        return new WatchResult(
            root.GetProperty("protocolVersion")
                .GetString()!,
            root.GetProperty("status")
                .GetString()!,
            root.GetProperty("commitResult")
                .GetString()!,
            root.TryGetProperty(
                    "mode",
                    out var mode)
                ? mode.GetString()
                : null,
            root.TryGetProperty(
                    "action",
                    out var action)
                ? action.GetString()
                : null,
            root.GetProperty(
                    "generation")
                .GetInt64(),
            root.GetProperty(
                    "artifactWritten")
                .GetBoolean(),
            telemetry.GetProperty("submitted")
                .GetInt64(),
            telemetry.GetProperty("completed")
                .GetInt64(),
            telemetry.GetProperty("applied")
                .GetInt64(),
            telemetry.GetProperty("cacheHits")
                .GetInt64(),
            telemetry.GetProperty(
                    "currentQueueDepth")
                .GetInt32(),
            telemetry.GetProperty(
                    "maximumQueueDepth")
                .GetInt32(),
            telemetry.GetProperty(
                    "allocationMeasurements")
                .GetInt64(),
            telemetry.GetProperty(
                    "lastAllocatedBytes")
                .GetInt64(),
            telemetry.GetProperty(
                    "averageDurationMilliseconds")
                .GetDouble(),
            telemetry.GetProperty(
                    "medianDurationUpperBoundMilliseconds")
                .GetDouble(),
            telemetry.GetProperty(
                    "p95DurationUpperBoundMilliseconds")
                .GetDouble(),
            telemetry.GetProperty(
                    "p99DurationUpperBoundMilliseconds")
                .GetDouble(),
            telemetry.GetProperty(
                    "averageAllocatedBytes")
                .GetInt64(),
            telemetry.GetProperty(
                    "medianAllocatedBytesUpperBound")
                .GetInt64(),
            telemetry.GetProperty(
                    "p95AllocatedBytesUpperBound")
                .GetInt64(),
            telemetry.GetProperty(
                    "p99AllocatedBytesUpperBound")
                .GetInt64(),
            performanceBudget.GetProperty(
                    "status")
                .GetString()!);
    }

    private static string FindRepositoryRoot()
    {
        var directory =
            new DirectoryInfo(
                AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "Directory.Build.props")) &&
                Directory.Exists(
                    Path.Combine(
                        directory.FullName,
                        "tools",
                        "ProGPU.Xaml.Cli")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The ProGPU repository root could not be located.");
    }

    private sealed record WatchResult(
        string ProtocolVersion,
        string Status,
        string CommitResult,
        string? Mode,
        string? Action,
        long Generation,
        bool ArtifactWritten,
        long Submitted,
        long Completed,
        long Applied,
        long CacheHits,
        int CurrentQueueDepth,
        int MaximumQueueDepth,
        long AllocationMeasurements,
        long LastAllocatedBytes,
        double AverageDurationMilliseconds,
        double MedianDurationUpperBoundMilliseconds,
        double P95DurationUpperBoundMilliseconds,
        double P99DurationUpperBoundMilliseconds,
        long AverageAllocatedBytes,
        long MedianAllocatedBytesUpperBound,
        long P95AllocatedBytesUpperBound,
        long P99AllocatedBytesUpperBound,
        string PerformanceBudgetStatus);
}
