using System.Runtime.InteropServices;

namespace ProGPU.DirectX;

public enum ProGpuDirectXNativeResolverRegistrationStatus
{
    Created,
    Installed,
    Rejected
}

public enum ProGpuDirectXNativeResolverModuleStatus
{
    Ignored,
    FacadeNotConfigured,
    FacadeLoadFailed,
    Resolved
}

public sealed record ProGpuDirectXNativeResolverAttempt(
    string ModuleName,
    ProGpuDirectXNativeModuleKind Kind,
    ProGpuDirectXNativeCompatibilityAction Action,
    ProGpuDirectXNativeResolverModuleStatus Status,
    string? FacadeLibraryPath,
    string? Failure);

public sealed class ProGpuDirectXNativeResolverOptions
{
    public const string DefaultFacadeLibraryPathEnvironmentVariable = "PROGPU_DIRECTX_NATIVE_FACADE_PATH";
    public const string ModuleFacadeLibraryPathsEnvironmentVariable = "PROGPU_DIRECTX_NATIVE_FACADE_MODULES";

    public static ProGpuDirectXNativeResolverOptions Empty { get; } = new();

    private readonly Dictionary<string, string> _moduleFacadeLibraryPaths;

    public ProGpuDirectXNativeResolverOptions(
        string? defaultFacadeLibraryPath = null,
        IReadOnlyDictionary<string, string>? moduleFacadeLibraryPaths = null)
    {
        DefaultFacadeLibraryPath = string.IsNullOrWhiteSpace(defaultFacadeLibraryPath)
            ? null
            : defaultFacadeLibraryPath.Trim();
        _moduleFacadeLibraryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (moduleFacadeLibraryPaths is null)
        {
            return;
        }

        foreach (var pair in moduleFacadeLibraryPaths)
        {
            var moduleName = NormalizeModuleName(pair.Key);
            if (moduleName.Length == 0 || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            _moduleFacadeLibraryPaths[moduleName] = pair.Value.Trim();
        }
    }

    public string? DefaultFacadeLibraryPath { get; }

    public IReadOnlyDictionary<string, string> ModuleFacadeLibraryPaths => _moduleFacadeLibraryPaths;

    public static ProGpuDirectXNativeResolverOptions FromEnvironment()
    {
        var modulePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var encodedModulePaths = Environment.GetEnvironmentVariable(ModuleFacadeLibraryPathsEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(encodedModulePaths))
        {
            foreach (var entry in encodedModulePaths.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = entry.IndexOf('=');
                if (separatorIndex <= 0 || separatorIndex == entry.Length - 1)
                {
                    continue;
                }

                var moduleName = entry[..separatorIndex].Trim();
                var path = entry[(separatorIndex + 1)..].Trim();
                if (moduleName.Length != 0 && path.Length != 0)
                {
                    modulePaths[moduleName] = path;
                }
            }
        }

        return new ProGpuDirectXNativeResolverOptions(
            Environment.GetEnvironmentVariable(DefaultFacadeLibraryPathEnvironmentVariable),
            modulePaths);
    }

    internal string? GetFacadeLibraryPath(string moduleName)
    {
        foreach (var key in GetLookupKeys(moduleName))
        {
            if (_moduleFacadeLibraryPaths.TryGetValue(key, out var path))
            {
                return path;
            }
        }

        return DefaultFacadeLibraryPath;
    }

    private static IEnumerable<string> GetLookupKeys(string moduleName)
    {
        var normalized = NormalizeModuleName(moduleName);
        if (normalized.Length == 0)
        {
            yield break;
        }

        yield return normalized;
        var extension = Path.GetExtension(normalized);
        if (extension.Length == 0)
        {
            yield return normalized + ".dll";
        }
        else
        {
            yield return normalized[..^extension.Length];
        }
    }

    private static string NormalizeModuleName(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return string.Empty;
        }

        var normalized = moduleName.Trim().Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
    }
}

public sealed class ProGpuDirectXNativeResolverRegistration
{
    private readonly object _assembly;
    private readonly ProGpuDirectXNativeResolverOptions _options;
    private readonly Dictionary<string, ProGpuDirectXNativeCompatibilityModule> _modules;
    private readonly Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProGpuDirectXNativeResolverAttempt> _attempts = [];
    private readonly object _gate = new();

    internal ProGpuDirectXNativeResolverRegistration(
        Type anchorType,
        ProGpuDirectXNativeCompatibilityPlan plan,
        ProGpuDirectXNativeResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(anchorType);
        ArgumentNullException.ThrowIfNull(plan);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _assembly = anchorType.Assembly;
        _modules = CreateModuleLookup(plan);
        AssemblyName = anchorType.Assembly.GetName().Name ?? anchorType.Assembly.FullName ?? string.Empty;
    }

    public string AssemblyName { get; }

    public ProGpuDirectXNativeResolverRegistrationStatus Status { get; private set; }

    public string? RegistrationFailure { get; private set; }

    public bool Installed => Status == ProGpuDirectXNativeResolverRegistrationStatus.Installed;

    public IReadOnlyList<ProGpuDirectXNativeResolverAttempt> Attempts
    {
        get
        {
            lock (_gate)
            {
                return _attempts.ToArray();
            }
        }
    }

    public string Describe()
    {
        List<string> parts =
        [
            $"{AssemblyName}: {Status.ToString().ToLowerInvariant()}",
            $"tracked native modules: {_modules.Values.Distinct().Count(module => ShouldResolve(module.Action))}"
        ];

        parts.Add(_options.DefaultFacadeLibraryPath is null
            ? "default facade: not configured"
            : $"default facade: {_options.DefaultFacadeLibraryPath}");

        if (_options.ModuleFacadeLibraryPaths.Count != 0)
        {
            parts.Add($"module facades: {_options.ModuleFacadeLibraryPaths.Count}");
        }

        if (!string.IsNullOrWhiteSpace(RegistrationFailure))
        {
            parts.Add($"registration failure: {RegistrationFailure}");
        }

        var attempts = Attempts;
        if (attempts.Count == 0)
        {
            parts.Add("attempts: none");
        }
        else
        {
            parts.Add("attempts: " + string.Join(
                ", ",
                attempts.Select(static attempt => $"{attempt.ModuleName}={attempt.Status.ToString().ToLowerInvariant()}")));
        }

        return string.Join("; ", parts);
    }

    public IntPtr ResolveForAnchorType(string libraryName, Type anchorType, DllImportSearchPath? searchPath = null)
    {
        ArgumentNullException.ThrowIfNull(anchorType);
        return Resolve(libraryName, anchorType.Assembly, searchPath);
    }

    internal IntPtr Resolve(string libraryName, object assembly, DllImportSearchPath? searchPath)
    {
        if (!ReferenceEquals(assembly, _assembly) || string.IsNullOrWhiteSpace(libraryName))
        {
            return IntPtr.Zero;
        }

        var requestedModuleName = NormalizeModuleName(libraryName);
        if (!TryFindModule(requestedModuleName, out var module) || module is null || !ShouldResolve(module.Action))
        {
            RecordAttempt(new ProGpuDirectXNativeResolverAttempt(
                requestedModuleName,
                module?.Kind ?? ProGpuDirectXNativeModuleKind.Unknown,
                module?.Action ?? ProGpuDirectXNativeCompatibilityAction.Investigate,
                ProGpuDirectXNativeResolverModuleStatus.Ignored,
                FacadeLibraryPath: null,
                Failure: null));
            return IntPtr.Zero;
        }

        var facadeLibraryPath = _options.GetFacadeLibraryPath(requestedModuleName);
        if (string.IsNullOrWhiteSpace(facadeLibraryPath))
        {
            RecordAttempt(new ProGpuDirectXNativeResolverAttempt(
                requestedModuleName,
                module.Kind,
                module.Action,
                ProGpuDirectXNativeResolverModuleStatus.FacadeNotConfigured,
                FacadeLibraryPath: null,
                Failure: null));
            return IntPtr.Zero;
        }

        lock (_gate)
        {
            if (_handles.TryGetValue(facadeLibraryPath, out var cachedHandle))
            {
                _attempts.Add(new ProGpuDirectXNativeResolverAttempt(
                    requestedModuleName,
                    module.Kind,
                    module.Action,
                    ProGpuDirectXNativeResolverModuleStatus.Resolved,
                    facadeLibraryPath,
                    Failure: null));
                return cachedHandle;
            }
        }

        if (TryLoadFacade(facadeLibraryPath, out var handle, out var failure))
        {
            lock (_gate)
            {
                _handles[facadeLibraryPath] = handle;
                _attempts.Add(new ProGpuDirectXNativeResolverAttempt(
                    requestedModuleName,
                    module.Kind,
                    module.Action,
                    ProGpuDirectXNativeResolverModuleStatus.Resolved,
                    facadeLibraryPath,
                    Failure: null));
            }

            return handle;
        }

        RecordAttempt(new ProGpuDirectXNativeResolverAttempt(
            requestedModuleName,
            module.Kind,
            module.Action,
            ProGpuDirectXNativeResolverModuleStatus.FacadeLoadFailed,
            facadeLibraryPath,
            failure));
        return IntPtr.Zero;
    }

    internal void MarkInstalled()
    {
        Status = ProGpuDirectXNativeResolverRegistrationStatus.Installed;
        RegistrationFailure = null;
    }

    internal void MarkRejected(string failure)
    {
        Status = ProGpuDirectXNativeResolverRegistrationStatus.Rejected;
        RegistrationFailure = failure;
    }

    private bool TryFindModule(string moduleName, out ProGpuDirectXNativeCompatibilityModule? module)
    {
        foreach (var key in GetLookupKeys(moduleName))
        {
            if (_modules.TryGetValue(key, out module))
            {
                return true;
            }
        }

        module = null;
        return false;
    }

    private void RecordAttempt(ProGpuDirectXNativeResolverAttempt attempt)
    {
        lock (_gate)
        {
            _attempts.Add(attempt);
        }
    }

    private static bool TryLoadFacade(string facadeLibraryPath, out IntPtr handle, out string? failure)
    {
        try
        {
            if (NativeLibrary.TryLoad(facadeLibraryPath, out handle))
            {
                failure = null;
                return true;
            }

            failure = "NativeLibrary.TryLoad returned false.";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or DllNotFoundException or BadImageFormatException)
        {
            handle = IntPtr.Zero;
            failure = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, ProGpuDirectXNativeCompatibilityModule> CreateModuleLookup(
        ProGpuDirectXNativeCompatibilityPlan plan)
    {
        var modules = new Dictionary<string, ProGpuDirectXNativeCompatibilityModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in plan.Modules)
        {
            foreach (var key in GetLookupKeys(module.ModuleName))
            {
                modules[key] = module;
            }
        }

        return modules;
    }

    private static bool ShouldResolve(ProGpuDirectXNativeCompatibilityAction action)
    {
        return action is ProGpuDirectXNativeCompatibilityAction.ImplementProGpuNativeFacade
            or ProGpuDirectXNativeCompatibilityAction.ImplementHostOsAbstraction;
    }

    private static IEnumerable<string> GetLookupKeys(string moduleName)
    {
        var normalized = NormalizeModuleName(moduleName);
        if (normalized.Length == 0)
        {
            yield break;
        }

        yield return normalized;
        var extension = Path.GetExtension(normalized);
        if (extension.Length == 0)
        {
            yield return normalized + ".dll";
        }
        else
        {
            yield return normalized[..^extension.Length];
        }
    }

    private static string NormalizeModuleName(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return string.Empty;
        }

        var normalized = moduleName.Trim().Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
    }
}

public static class ProGpuDirectXNativeResolver
{
    private static readonly object Gate = new();
    private static readonly Dictionary<object, ProGpuDirectXNativeResolverRegistration> Registrations = [];

    public static ProGpuDirectXNativeResolverRegistration CreateAnchorRegistration(
        Type anchorType,
        ProGpuDirectXNativeCompatibilityPlan plan,
        ProGpuDirectXNativeResolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(anchorType);
        return CreateRegistration(
            anchorType,
            plan,
            options ?? ProGpuDirectXNativeResolverOptions.Empty);
    }

    private static ProGpuDirectXNativeResolverRegistration CreateRegistration(
        Type anchorType,
        ProGpuDirectXNativeCompatibilityPlan plan,
        ProGpuDirectXNativeResolverOptions? options = null)
    {
        return new ProGpuDirectXNativeResolverRegistration(
            anchorType,
            plan,
            options ?? ProGpuDirectXNativeResolverOptions.Empty);
    }

    private static bool TryRegister(
        Type anchorType,
        ProGpuDirectXNativeCompatibilityPlan plan,
        out ProGpuDirectXNativeResolverRegistration registration)
    {
        return TryRegister(anchorType, plan, ProGpuDirectXNativeResolverOptions.Empty, out registration);
    }

    private static bool TryRegister(
        Type anchorType,
        ProGpuDirectXNativeCompatibilityPlan plan,
        ProGpuDirectXNativeResolverOptions? options,
        out ProGpuDirectXNativeResolverRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(anchorType);
        var assembly = anchorType.Assembly;
        lock (Gate)
        {
            if (Registrations.TryGetValue(assembly, out registration!))
            {
                return registration.Installed;
            }

            var createdRegistration = CreateRegistration(anchorType, plan, options);
            try
            {
                NativeLibrary.SetDllImportResolver(
                    assembly,
                    (libraryName, requestedAssembly, searchPath) => createdRegistration.Resolve(libraryName, requestedAssembly, searchPath));
                createdRegistration.MarkInstalled();
            }
            catch (InvalidOperationException ex)
            {
                createdRegistration.MarkRejected(ex.Message);
            }

            Registrations[assembly] = createdRegistration;
            registration = createdRegistration;
            return registration.Installed;
        }
    }

    public static IReadOnlyList<ProGpuDirectXNativeResolverRegistration> RegisterAnchorTypes(
        IEnumerable<Type> anchorTypes,
        ProGpuDirectXNativeCompatibilityPlan plan,
        ProGpuDirectXNativeResolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(anchorTypes);

        var registrations = new List<ProGpuDirectXNativeResolverRegistration>();
        var seenAssemblies = new HashSet<object>();
        foreach (var anchorType in anchorTypes)
        {
            if (anchorType is null)
            {
                throw new ArgumentException("Native resolver anchor registration cannot include a null anchor type.", nameof(anchorTypes));
            }

            var assembly = anchorType.Assembly;
            if (!seenAssemblies.Add(assembly))
            {
                continue;
            }

            TryRegister(anchorType, plan, options, out var registration);
            registrations.Add(registration);
        }

        return registrations
            .OrderBy(static registration => registration.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
