using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.IntegrationTests.SilkNet.Infrastructure;

namespace Avalonia.IntegrationTests.SilkNet
{
    public class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            int exitCode = 0;
            var testTask = Task.Run(async () =>
            {
                // Wait for the dispatcher loop to be initialized and running
                await AppManager.EnsureAppInitializedAsync().ConfigureAwait(false);

                try
                {
                    if (args.Any(arg => arg == "-automated" || arg == "@@"))
                    {
                        exitCode = Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args).GetAwaiter().GetResult();
                    }
                    else
                    {
                        exitCode = Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    exitCode = -1;
                }
                finally
                {
                    AppManager.Stop();
                }
            });

            // Start the main loop on the main thread
            AppManager.StartMainLoop();

            // Wait for tests to finish
            testTask.GetAwaiter().GetResult();

            return exitCode;
        }
    }
}
