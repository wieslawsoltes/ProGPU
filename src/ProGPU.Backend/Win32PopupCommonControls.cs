using System.Runtime.InteropServices;

namespace ProGPU.Backend;

// GetWindowSubclass is a named export only in common-controls v6. The host may
// have no visual-styles manifest, so do not bind any member of this family via
// its default comctl32 import. Resolve all four from one explicitly owned module.
internal static unsafe partial class Win32PopupCommonControls
{
    private static readonly Api s_api = Load();

    internal static int Set(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint data) =>
        s_api.Set == null ? 0 : s_api.Set(window, callback, id, data);

    internal static int Get(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, out nuint data)
    {
        data = 0;
        if (s_api.Get == null) return 0;
        fixed (nuint* result = &data) return s_api.Get(window, callback, id, result);
    }

    internal static int Remove(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id) =>
        s_api.Remove == null ? 0 : s_api.Remove(window, callback, id);

    internal static nint Next(nint window, uint message, nuint wParam, nint lParam) =>
        s_api.Next(window, message, wParam, lParam);

    private struct Api
    {
        // Retain this module for the process lifetime: native windows can still
        // dispatch our callbacks after the configuration call has returned.
        internal nint Module;
        internal delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>, nuint, nuint, int> Set;
        internal delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>, nuint, nuint*, int> Get;
        internal delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>, nuint, int> Remove;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Next;
    }

    private static Api Load()
    {
        if (!OperatingSystem.IsWindows()) return default;
        nint context = -1;
        nint module = 0;
        nuint cookie = 0;
        bool activated = false;
        string? temporaryManifest = null;
        try
        {
#pragma warning disable IL3000 // Bundled assemblies use the exact embedded manifest below.
            string source = typeof(Win32PopupCommonControls).Assembly.Location;
#pragma warning restore IL3000
            var description = new ActivationContext { Size = (uint)sizeof(ActivationContext) };
            if (source.Length != 0)
            {
                description.Flags = 0x008; // ACTCTX_FLAG_RESOURCE_NAME_VALID
                description.ResourceName = 2; // DLL isolation-aware manifest resource.
            }
            else
            {
                // Single-file and NativeAOT have no independently addressable PE.
                // Extract only our own manifest, once, and delete it after parsing.
                temporaryManifest = Path.Combine(Path.GetTempPath(), "progpu-popup-" + Guid.NewGuid().ToString("N") + ".manifest");
                using Stream manifest = typeof(Win32PopupCommonControls).Assembly.GetManifestResourceStream(
                    "ProGPU.Backend.Win32PopupCommonControls.manifest")!;
                if (manifest == null) return default;
                using (var output = new FileStream(temporaryManifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    manifest.CopyTo(output);
                source = temporaryManifest;
            }
            fixed (char* path = source)
            {
                description.Source = path;
                context = CreateActCtxW(ref description);
            }
            if (context == -1 || ActivateActCtx(context, out cookie) == 0) return default;
            activated = true;
            module = LoadLibraryW("comctl32.dll");
            if (module == 0 ||
                !NativeLibrary.TryGetExport(module, "SetWindowSubclass", out nint set) ||
                !NativeLibrary.TryGetExport(module, "GetWindowSubclass", out nint get) ||
                !NativeLibrary.TryGetExport(module, "RemoveWindowSubclass", out nint remove) ||
                !NativeLibrary.TryGetExport(module, "DefSubclassProc", out nint next)) return default;
            var api = new Api
            {
                Module = module,
                Set = (delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>, nuint, nuint, int>)set,
                Get = (delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>, nuint, nuint*, int>)get,
                Remove = (delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>, nuint, int>)remove,
                Next = (delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)next
            };
            module = 0; // Transfer the one complete module reference atomically.
            return api;
        }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
        finally
        {
            if (module != 0) NativeLibrary.Free(module);
            if (activated) _ = DeactivateActCtx(0, cookie);
            if (context != -1) ReleaseActCtx(context);
            if (temporaryManifest != null)
            {
                try { File.Delete(temporaryManifest); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationContext
    {
        internal uint Size;
        internal uint Flags;
        internal char* Source;
        internal ushort ProcessorArchitecture;
        internal ushort LanguageId;
        internal nint AssemblyDirectory;
        internal nint ResourceName;
        internal nint ApplicationName;
        internal nint Module;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateActCtxW(ref ActivationContext context);
    [LibraryImport("kernel32.dll")]
    private static partial int ActivateActCtx(nint context, out nuint cookie);
    [LibraryImport("kernel32.dll")]
    private static partial int DeactivateActCtx(uint flags, nuint cookie);
    [LibraryImport("kernel32.dll")]
    private static partial void ReleaseActCtx(nint context);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryW(string name);
}
