using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage: ProGPU.AssemblyContractInspector <assembly> [assembly...]");
    return 2;
}

var failed = false;
foreach (var assemblyPath in args)
{
    if (!File.Exists(assemblyPath))
    {
        Console.Error.WriteLine($"Assembly not found: {assemblyPath}");
        failed = true;
        continue;
    }

    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    if (!peReader.HasMetadata)
    {
        Console.Error.WriteLine($"Managed metadata not found: {assemblyPath}");
        failed = true;
        continue;
    }

    var metadata = peReader.GetMetadataReader();
    var violations = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var handle in metadata.TypeReferences)
    {
        var reference = metadata.GetTypeReference(handle);
        var typeNamespace = metadata.GetString(reference.Namespace);
        var typeName = metadata.GetString(reference.Name);
        if (IsRuntimeReflectionType(typeNamespace, typeName))
            violations.Add($"{typeNamespace}.{typeName}");
    }

    if (violations.Count != 0)
    {
        Console.Error.WriteLine(
            $"Runtime-reflection contract failed: {assemblyPath}");
        foreach (var violation in violations)
            Console.Error.WriteLine($"  prohibited TypeRef: {violation}");
        failed = true;
    }
    else
    {
        Console.WriteLine(
            $"Runtime-reflection contract passed: {assemblyPath}");
    }
}

return failed ? 1 : 0;

static bool IsRuntimeReflectionType(string typeNamespace, string typeName)
{
    if (typeNamespace == "System.Reflection")
    {
        // Compiler-emitted assembly metadata attributes are declarative and
        // perform no runtime inspection.
        return !typeName.EndsWith("Attribute", StringComparison.Ordinal);
    }

    if (typeNamespace.StartsWith(
            "System.Reflection.Emit",
            StringComparison.Ordinal))
    {
        return true;
    }

    return (typeNamespace, typeName) switch
    {
        ("System", "Activator") => true,
        ("System.Runtime.Loader", "AssemblyLoadContext") => true,
        ("System.Runtime.CompilerServices", "UnsafeAccessorAttribute") => true,
        _ => false
    };
}
