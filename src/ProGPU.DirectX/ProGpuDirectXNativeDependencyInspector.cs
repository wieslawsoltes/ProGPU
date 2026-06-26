using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ProGPU.DirectX;

public sealed record ProGpuDirectXNativeImport(
    string AssemblyName,
    string TypeName,
    string MethodName,
    string ModuleName,
    string EntryPoint,
    CallingConvention CallingConvention,
    CharSet CharSet,
    bool SetLastError,
    bool ExactSpelling);

public sealed record ProGpuDirectXNativeModuleHint(
    string AssemblyName,
    string ModuleName,
    string Source);

public sealed record ProGpuDirectXNativeDependencyReport(
    IReadOnlyList<ProGpuDirectXNativeImport> Imports,
    IReadOnlyList<ProGpuDirectXNativeModuleHint> ModuleHints,
    IReadOnlyList<string> NativeModules)
{
    public bool RequiresNativeRuntime => NativeModules.Count > 0;

    public bool RequiresModule(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        return NativeModules.Contains(moduleName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public string DescribeModules()
    {
        return NativeModules.Count == 0 ? "none" : string.Join(", ", NativeModules);
    }
}

public static class ProGpuDirectXNativeDependencyInspector
{
    public static ProGpuDirectXNativeDependencyReport Inspect(params Assembly[] assemblies)
    {
        return Inspect((IEnumerable<Assembly>)assemblies);
    }

    public static ProGpuDirectXNativeDependencyReport Inspect(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var imports = new List<ProGpuDirectXNativeImport>();
        var moduleHints = new List<ProGpuDirectXNativeModuleHint>();
        foreach (var assembly in assemblies)
        {
            if (assembly is null)
            {
                throw new ArgumentException("Native dependency inspection cannot inspect a null assembly.", nameof(assemblies));
            }

            moduleHints.AddRange(InspectModuleHints(assembly));

            foreach (var type in GetLoadableTypes(assembly))
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    var import = method.GetCustomAttribute<DllImportAttribute>();
                    if (import is null)
                    {
                        continue;
                    }

                    var moduleName = NormalizeModuleName(import.Value);
                    if (moduleName.Length == 0)
                    {
                        continue;
                    }

                    imports.Add(new ProGpuDirectXNativeImport(
                        assembly.GetName().Name ?? assembly.FullName ?? string.Empty,
                        type.FullName ?? type.Name,
                        method.Name,
                        moduleName,
                        import.EntryPoint ?? method.Name,
                        import.CallingConvention,
                        import.CharSet,
                        import.SetLastError,
                        import.ExactSpelling));
                }
            }
        }

        var orderedImports = imports
            .OrderBy(static import => import.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static import => import.EntryPoint, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static import => import.TypeName, StringComparer.Ordinal)
            .ThenBy(static import => import.MethodName, StringComparer.Ordinal)
            .ToArray();

        var orderedModuleHints = moduleHints
            .DistinctBy(static hint => (hint.AssemblyName, hint.ModuleName, hint.Source))
            .OrderBy(static hint => hint.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static hint => hint.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static hint => hint.Source, StringComparer.Ordinal)
            .ToArray();

        var nativeModules = orderedImports
            .Select(static import => import.ModuleName)
            .Concat(orderedModuleHints.Select(static hint => hint.ModuleName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static moduleName => moduleName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProGpuDirectXNativeDependencyReport(orderedImports, orderedModuleHints, nativeModules);
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null).Cast<Type>();
        }
    }

    private static IEnumerable<ProGpuDirectXNativeModuleHint> InspectModuleHints(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name ?? assembly.FullName ?? string.Empty;
        if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location))
        {
            return [];
        }

        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var bytes = File.ReadAllBytes(assembly.Location);
            ExtractAsciiModuleNames(bytes, modules);
            ExtractUtf16LeModuleNames(bytes, modules, startOffset: 0);
            ExtractUtf16LeModuleNames(bytes, modules, startOffset: 1);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return modules
            .OrderBy(static moduleName => moduleName, StringComparer.OrdinalIgnoreCase)
            .Select(moduleName => new ProGpuDirectXNativeModuleHint(
                assemblyName,
                moduleName,
                "AssemblyString"));
    }

    private static void ExtractAsciiModuleNames(ReadOnlySpan<byte> bytes, HashSet<string> modules)
    {
        var builder = new StringBuilder();
        foreach (var value in bytes)
        {
            if (value >= 32 && value <= 126)
            {
                AppendModuleHintChar(builder, (char)value, modules);
            }
            else
            {
                FlushModuleHint(builder, modules);
            }
        }

        FlushModuleHint(builder, modules);
    }

    private static void ExtractUtf16LeModuleNames(ReadOnlySpan<byte> bytes, HashSet<string> modules, int startOffset)
    {
        var builder = new StringBuilder();
        for (var i = startOffset; i + 1 < bytes.Length; i += 2)
        {
            var value = bytes[i];
            var high = bytes[i + 1];
            if (high == 0 && value >= 32 && value <= 126)
            {
                AppendModuleHintChar(builder, (char)value, modules);
            }
            else
            {
                FlushModuleHint(builder, modules);
            }
        }

        FlushModuleHint(builder, modules);
    }

    private static void AppendModuleHintChar(StringBuilder builder, char value, HashSet<string> modules)
    {
        if (char.IsWhiteSpace(value) || value is '"' or '\'' or '<' or '>' or '(' or ')' or '[' or ']' or '{' or '}' or ',' or ';')
        {
            FlushModuleHint(builder, modules);
            return;
        }

        builder.Append(value);
        if (builder.Length > 512)
        {
            FlushModuleHint(builder, modules);
        }
    }

    private static void FlushModuleHint(StringBuilder builder, HashSet<string> modules)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var moduleName = NormalizeModuleHint(builder.ToString());
        if (moduleName.Length != 0)
        {
            modules.Add(moduleName);
        }

        builder.Clear();
    }

    private static string NormalizeModuleHint(string value)
    {
        var normalized = value.Trim().TrimEnd('.', ':', '!', '?');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized[0] == '.' || normalized.Contains('*', StringComparison.Ordinal) || normalized.Contains('?', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        normalized = normalized.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            normalized = normalized[(slashIndex + 1)..];
        }

        if (normalized.Length == 0 || normalized[0] == '.' || normalized.Contains('*', StringComparison.Ordinal) || normalized.Contains('?', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return IsNativeModuleName(normalized) ? normalized : string.Empty;
    }

    private static bool IsNativeModuleName(string value)
    {
        return value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".so", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModuleName(string? moduleName)
    {
        return string.IsNullOrWhiteSpace(moduleName) ? string.Empty : moduleName.Trim();
    }
}
