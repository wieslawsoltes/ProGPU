using System.Reflection;
using System.Runtime.InteropServices;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

// One owned routing domain for both WebGPUSharp and ProGPU Dawn native imports.
// The live module is process-pinned: P/Invoke caches and borrowed procedure
// pointers must never outlive it. No GPU instance/device is owned here.
internal static class DawnNativeProvider
{
    private const string ImportName = "webgpu_dawn";
    private static readonly object Sync = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool s_resolversInstalled;
    private static Exception? s_resolverFailure;
    private static string? s_configuredPath;
    private static nint s_module;

    internal static void EnsureResolvers()
    {
        lock (Sync)
        {
            if (s_resolverFailure is not null)
            {
                throw new InvalidOperationException("The Dawn native import domain is unavailable.", s_resolverFailure);
            }
            if (s_resolversInstalled) return;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(WebGPU_FFI).Assembly, ResolveImport);
                NativeLibrary.SetDllImportResolver(typeof(DawnNativeProvider).Assembly, ResolveImport);
                s_resolversInstalled = true;
            }
            catch (InvalidOperationException failure)
            {
                // Registration is not replaceable/inspectable. Latch partial
                // failure so even a previously installed first resolver cannot
                // silently route through a conflicting second domain.
                s_resolverFailure = failure;
                throw new InvalidOperationException(
                    "Dawn requires ownership of both native import resolvers. Configure its provider before any FFI use; foreign resolvers are not supported.",
                    failure);
            }
        }
    }

    // Pure path validation, independent of resolver/module selection or native
    // execution. It does not make an ABI claim about the file's contents.
    internal static string ValidateLibraryPath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException("An absolute Dawn provider library path is required.", nameof(absolutePath));
        }
        string path = Path.GetFullPath(absolutePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The explicitly selected Dawn provider library does not exist.", path);
        }
        return path;
    }

    internal static void ConfigureLibrary(string absolutePath)
    {
        string path = ValidateLibraryPath(absolutePath);
        lock (Sync)
        {
            EnsureResolvers();
            if (s_configuredPath is not null)
            {
                if (PathComparer.Equals(s_configuredPath, path)) return; // Verification only.
                throw new InvalidOperationException("The explicit Dawn provider selection is immutable.");
            }
            if (s_module != 0)
            {
                throw new InvalidOperationException("Configure the Dawn provider before its first native FFI use.");
            }
            s_configuredPath = path;
        }
    }

    internal static bool IsAvailable()
    {
        lock (Sync)
        {
            EnsureResolvers();
            if (s_module != 0) return true;
            // A probe does not publish a default module/selection. Explicit
            // choices probe only that exact path and never try another ABI.
            if (!TryLoadSelected(searchPath: null, out nint probe)) return false;
            NativeLibrary.Free(probe); // Temporary probe only; never s_module.
            return true;
        }
    }

    // Borrow only an ALREADY selected module. The exact context's creation must
    // have gone through our resolver; borrowing must not load a second provider.
    internal static nint GetModule()
    {
        lock (Sync)
        {
            EnsureResolvers();
            if (s_module == 0)
            {
                throw new InvalidOperationException("No provider-owned Dawn native imports have selected a module.");
            }
            return s_module;
        }
    }

    private static nint ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ImportName, StringComparison.Ordinal)) return 0;
        lock (Sync)
        {
            EnsureResolvers();
            if (s_module != 0) return s_module;
            if (!TryLoadSelected(searchPath, out nint module))
            {
                throw new DllNotFoundException(
                    $"The selected exact-ABI Dawn provider '{s_configuredPath ?? DefaultLibraryName()}' could not be loaded.");
            }
            // No live-module NativeLibrary.Free: both import domains and every
            // native compositor borrow this ONE process-pinned provider.
            s_module = module;
            return module;
        }
    }

    private static bool TryLoadSelected(DllImportSearchPath? searchPath, out nint module) =>
        s_configuredPath is not null
            // An explicit absolute file never passes through an ambient ALC
            // resolver capable of substituting a different provider module.
            ? NativeLibrary.TryLoad(s_configuredPath, out module)
            : NativeLibrary.TryLoad(DefaultLibraryName(), typeof(WebGPU_FFI).Assembly, searchPath, out module);

    private static string DefaultLibraryName()
    {
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            return "@rpath/webgpu_dawn.framework/webgpu_dawn";
        if (OperatingSystem.IsWindows()) return "webgpu_dawn.dll";
        if (OperatingSystem.IsMacOS()) return "webgpu_dawn.dylib";
        if (OperatingSystem.IsLinux()) return "webgpu_dawn.so";
        return "libwebgpu_dawn.so"; // Existing Android name.
    }
}
