namespace ProGPU.DirectX;

public enum ProGpuDirectXNativeModuleKind
{
    Unknown,
    Direct3D,
    D3DCompiler,
    Dxgi,
    Win32System,
    SciChartLicensing,
    SciChartVisualXccelerator,
    ManagedAssemblyHint,
    Direct2D,
    DirectWrite,
    WindowsImagingComponent,
    Win2D
}

public enum ProGpuDirectXNativeCompatibilityAction
{
    Investigate,
    ImplementProGpuNativeFacade,
    ImplementHostOsAbstraction,
    ManagedAssemblyReferenceOnly,
    UseWindowsNativeGraphicsInterop
}

public sealed record ProGpuDirectXNativeCompatibilityModule(
    string ModuleName,
    ProGpuDirectXNativeModuleKind Kind,
    ProGpuDirectXNativeCompatibilityAction Action,
    string Reason);

public sealed record ProGpuDirectXNativeCompatibilityPlan(
    IReadOnlyList<ProGpuDirectXNativeCompatibilityModule> Modules)
{
    public bool RequiresProGpuNativeFacade =>
        Modules.Any(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade);

    public bool RequiresHostOsAbstraction =>
        Modules.Any(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction);

    public bool RequiresWindowsNativeGraphicsInterop =>
        Modules.Any(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.UseWindowsNativeGraphicsInterop);

    public IReadOnlyList<string> ProGpuNativeFacadeModules =>
        Modules
            .Where(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade)
            .Select(static module => module.ModuleName)
            .ToArray();

    public IReadOnlyList<string> HostOsAbstractionModules =>
        Modules
            .Where(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction)
            .Select(static module => module.ModuleName)
            .ToArray();

    public IReadOnlyList<string> ManagedAssemblyHints =>
        Modules
            .Where(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.ManagedAssemblyReferenceOnly)
            .Select(static module => module.ModuleName)
            .ToArray();

    public IReadOnlyList<string> WindowsNativeGraphicsInteropModules =>
        Modules
            .Where(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.UseWindowsNativeGraphicsInterop)
            .Select(static module => module.ModuleName)
            .ToArray();

    public IReadOnlyList<string> UnknownModules =>
        Modules
            .Where(static module => module.Action == ProGpuDirectXNativeCompatibilityAction.Investigate)
            .Select(static module => module.ModuleName)
            .ToArray();

    public string DescribeRequiredActions()
    {
        if (Modules.Count == 0)
        {
            return "none";
        }

        List<string> parts = [];
        AddModuleGroup(parts, "ProGPU native facade", ProGpuNativeFacadeModules);
        AddModuleGroup(parts, "host OS abstraction", HostOsAbstractionModules);
        AddModuleGroup(parts, "Windows native graphics interop", WindowsNativeGraphicsInteropModules);
        AddModuleGroup(parts, "managed assembly hints", ManagedAssemblyHints);
        AddModuleGroup(parts, "investigate", UnknownModules);
        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    private static void AddModuleGroup(List<string> parts, string name, IReadOnlyList<string> modules)
    {
        if (modules.Count == 0)
        {
            return;
        }

        parts.Add($"{name}: {string.Join(", ", modules)}");
    }
}

public static class ProGpuDirectXNativeCompatibilityPlanner
{
    private static readonly HashSet<string> Win32SystemModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "advapi32",
        "advapi32.dll",
        "cabinet",
        "cabinet.dll",
        "gdi32",
        "gdi32.dll",
        "kernel32",
        "kernel32.dll",
        "mscoree",
        "mscoree.dll",
        "msvcrt",
        "msvcrt.dll",
        "ole32",
        "ole32.dll",
        "rpcrt4",
        "rpcrt4.dll",
        "symsrv",
        "symsrv.dll",
        "user32",
        "user32.dll"
    };

    public static ProGpuDirectXNativeCompatibilityPlan Create(ProGpuDirectXNativeDependencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var modules = report.NativeModules
            .Select(Classify)
            .OrderBy(static module => module.Action)
            .ThenBy(static module => module.Kind)
            .ThenBy(static module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProGpuDirectXNativeCompatibilityPlan(modules);
    }

    public static ProGpuDirectXNativeCompatibilityModule Classify(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return CreateUnknown(string.Empty, "Empty native module name.");
        }

        var normalized = NormalizeModuleName(moduleName);
        var comparisonName = normalized.ToLowerInvariant();

        if (comparisonName.Contains("vxccelengine", StringComparison.Ordinal))
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.SciChartVisualXccelerator,
                ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade,
                "SciChart Visual Xccelerator native-engine payload needs a ProGPU-backed ABI facade.");
        }

        if (comparisonName.Contains("abtlicensingnative", StringComparison.Ordinal)
            || comparisonName == "scichartcorenative")
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.SciChartLicensing,
                ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade,
                "SciChart licensing native entry points need a cross-platform ProGPU-hosted facade.");
        }

        if (comparisonName is "d3d9.dll" or "d3d10.dll" or "d3d11.dll" or "d3d12.dll")
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.Direct3D,
                ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade,
                "Direct3D device/resource entry points should route to ProGPU.DirectX/WebGPU.");
        }

        if (comparisonName == "dxgi.dll")
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.Dxgi,
                ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade,
                "DXGI factory/adapter/swap-chain entry points should route to ProGPU windowing and WebGPU surfaces.");
        }

        if (comparisonName.Contains("d3dcompiler_", StringComparison.Ordinal) && comparisonName.EndsWith(".dll", StringComparison.Ordinal))
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.D3DCompiler,
                ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade,
                "D3DCompiler entry points should feed the ProGPU HLSL/bytecode translation path.");
        }

        if (comparisonName == "d2d1.dll")
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.Direct2D,
                ProGpuDirectXNativeCompatibilityAction.UseWindowsNativeGraphicsInterop,
                "Direct2D is a Windows COM API. Use the operating-system runtime through typed DXGI shared-surface interop; portable drawing belongs in the ProGPU Canvas API, not a d2d1.dll facade.");
        }

        if (comparisonName == "dwrite.dll")
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.DirectWrite,
                ProGpuDirectXNativeCompatibilityAction.UseWindowsNativeGraphicsInterop,
                "DirectWrite is a Windows COM API. Windows hosts may use it through a typed boundary; portable text remains in ProGPU's shaping and glyph pipeline.");
        }

        if (comparisonName == "windowscodecs.dll")
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.WindowsImagingComponent,
                ProGpuDirectXNativeCompatibilityAction.UseWindowsNativeGraphicsInterop,
                "WIC is a Windows COM API. Windows hosts may use the system decoder through typed texture interop; portable image decoding must not impersonate windowscodecs.dll.");
        }

        if (comparisonName == "microsoft.graphics.canvas.dll")
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.Win2D,
                ProGpuDirectXNativeCompatibilityAction.UseWindowsNativeGraphicsInterop,
                "The native Win2D WinRT component requires Windows Direct2D/Direct3D interop. Use the real component on Windows; portable source compatibility belongs in the ProGPU Canvas API.");
        }

        if (Win32SystemModules.Contains(comparisonName))
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.Win32System,
                ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction,
                "Win32 system calls should be mapped to local OS services or a narrow compatibility abstraction.");
        }

        if (IsManagedAssemblyHint(comparisonName))
        {
            return new ProGpuDirectXNativeCompatibilityModule(
                normalized,
                ProGpuDirectXNativeModuleKind.ManagedAssemblyHint,
                ProGpuDirectXNativeCompatibilityAction.ManagedAssemblyReferenceOnly,
                "This looks like a managed SciChart assembly reference rather than a native facade target.");
        }

        return CreateUnknown(normalized, "Unknown native module hint; inspect real call sites before designing a facade.");
    }

    private static ProGpuDirectXNativeCompatibilityModule CreateUnknown(string moduleName, string reason)
    {
        return new ProGpuDirectXNativeCompatibilityModule(
            moduleName,
            ProGpuDirectXNativeModuleKind.Unknown,
            ProGpuDirectXNativeCompatibilityAction.Investigate,
            reason);
    }

    private static string NormalizeModuleName(string moduleName)
    {
        var normalized = moduleName.Trim().Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
    }

    private static bool IsManagedAssemblyHint(string comparisonName)
    {
        return comparisonName.StartsWith("scichart.", StringComparison.Ordinal)
            && comparisonName.EndsWith(".dll", StringComparison.Ordinal);
    }
}
