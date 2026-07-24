using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Internal;
using Xunit.Sdk;
using Xunit.v3;

namespace Avalonia.IntegrationTests.SilkNet.Infrastructure;

internal sealed class AvaloniaTestFrameworkExecutor(IXunitTestAssembly testAssembly)
    : ITestFrameworkExecutor
{
    public async ValueTask RunTestCases(
        IReadOnlyCollection<ITestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationToken? cancellationToken = null)
    {
        var seed = executionOptions.Seed() ?? testAssembly.ModuleVersionID.GetHashCode();
        Randomizer.Seed = seed == int.MinValue ? int.MaxValue : Math.Abs(seed);
        var executor = new XunitTestFrameworkExecutor(testAssembly);

        // Wait for AppManager to initialize the app and dispatcher (initialized by Program.Main)
        await AppManager.EnsureAppInitializedAsync().ConfigureAwait(false);

        using (new PreserveWorkingFolder(testAssembly))
        using (new InvariantCultureScope())
        {
            await executor
                .RunTestCases(
                    testCases.Cast<IXunitTestCase>().ToArray(),
                    executionMessageSink,
                    executionOptions,
                    cancellationToken.GetValueOrDefault())
                .ConfigureAwait(false);
        }
    }
}
