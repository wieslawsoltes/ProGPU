using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Avalonia.IntegrationTests.SilkNet.Infrastructure;

internal static class AppManager
{
    private static readonly TaskCompletionSource<Dispatcher> s_dispatcherTcs = new();
    private static readonly CancellationTokenSource s_cancellation = new();

    [ModuleInitializer]
    internal static void InitializeAssembly()
    {
        Assembly? Resolve(string assemblyName)
        {
            var name = new AssemblyName(assemblyName).Name;
            if (name != null && name.StartsWith("Silk.NET"))
            {
                var dir = Path.GetDirectoryName(typeof(AppManager).Assembly.Location);
                if (dir != null)
                {
                    var path = Path.Combine(dir, name + ".dll");
                    if (File.Exists(path))
                    {
                        try
                        {
                            return Assembly.LoadFrom(path);
                        }
                        catch
                        {
                            // Ignore load errors
                        }
                    }
                }
            }
            return null;
        }

        IntPtr ResolveUnmanaged(Assembly assembly, string dllName)
        {
            string? mappedName = dllName;
            if (dllName == "glfw" || dllName == "glfw3" || dllName.Contains("glfw"))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var brewPath = "/opt/homebrew/opt/glfw/lib/libglfw.3.dylib";
                    if (File.Exists(brewPath))
                    {
                        if (NativeLibrary.TryLoad(brewPath, out var handle))
                        {
                            return handle;
                        }
                    }
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    mappedName = "glfw3.dll";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    mappedName = "libglfw.3.dylib";
                else
                    mappedName = "libglfw.so";
            }
            else if (dllName == "wgpu_native" || dllName.Contains("wgpu"))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    mappedName = "wgpu_native.dll";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    mappedName = "libwgpu_native.dylib";
                else
                    mappedName = "libwgpu_native.so";
            }

            var dir = Path.GetDirectoryName(typeof(AppManager).Assembly.Location);
            if (dir != null)
            {
                // Determine RID (runtime identifier)
                string rid = "win-x64";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
                }

                var path = Path.Combine(dir, "runtimes", rid, "native", mappedName);
                if (File.Exists(path))
                {
                    if (NativeLibrary.TryLoad(path, out var handle))
                    {
                        return handle;
                    }
                }

                path = Path.Combine(dir, mappedName);
                if (File.Exists(path))
                {
                    if (NativeLibrary.TryLoad(path, out var handle))
                    {
                        return handle;
                    }
                }
            }
            return IntPtr.Zero;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => Resolve(args.Name);

        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
            if (alc != null)
            {
                alc.Resolving += (context, name) => Resolve(name.FullName);
                alc.ResolvingUnmanagedDll += (assembly, dllName) => ResolveUnmanaged(assembly, dllName);
            }
        }
        catch
        {
            // Fallback
        }
    }

    public static Task<Dispatcher> EnsureAppInitializedAsync() => s_dispatcherTcs.Task;

    public static void StartMainLoop()
    {
        var appBuilder = AppBuilder
            .Configure<Application>()
            .UseSilkNet()
            .UseSkia()
            .UseHarfBuzz()
            .SetupWithoutStarting();

        appBuilder.Instance!.Styles.Add(new FluentTheme());

        var dispatcher = Dispatcher.UIThread;
        dispatcher.VerifyAccess();
        s_dispatcherTcs.TrySetResult(dispatcher);
        AvaloniaSynchronizationContext.InstallIfNeeded();

        dispatcher.MainLoop(s_cancellation.Token);
    }

    public static void Stop()
    {
        s_cancellation.Cancel();
        // Signal the dispatcher to wake up and exit
        EnsureAppInitializedAsync().ContinueWith(t => {
            if (t.IsCompletedSuccessfully)
            {
                t.Result.Post(() => {});
            }
        });
    }
}
