using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Lowering;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;
using System.Text.Json;

namespace ProGPU.Xaml.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "parse" => ParseCommand(args),
                "corpus" => CorpusCommand(args),
                "inspect" => await InspectCommandAsync(args),
                "compile" => await CompileCommandAsync(args),
                "project" => await ProjectCommandAsync(args),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PGXAMLCLI0001: " + exception.Message);
            return 2;
        }
    }

    private static int ParseCommand(string[] args)
    {
        if (args.Length < 2)
        {
            return MissingArgument("parse requires a XAML file path");
        }

        var document = ParseFile(Path.GetFullPath(args[1]));
        if (HasOption(args, "--json"))
        {
            WriteJson(new
            {
                command = "parse",
                path = document.Path,
                root = document.Root?.QualifiedName,
                elements = CountObjects(document.Root),
                diagnostics = ProjectDiagnostics(document.Diagnostics)
            });
            return document.HasErrors ? 1 : 0;
        }
        PrintDocumentSummary(document);
        return document.HasErrors ? 1 : 0;
    }

    private static int CorpusCommand(string[] args)
    {
        if (args.Length < 2)
        {
            return MissingArgument("corpus requires a XAML file or directory path");
        }

        var path = Path.GetFullPath(args[1]);
        var files = File.Exists(path)
            ? new[] { path }
            : Directory.EnumerateFiles(path, "*.xaml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(path, "*.axaml", SearchOption.AllDirectories))
                .Where(static file => !IsBuildOutput(file))
                .OrderBy(static file => file, StringComparer.Ordinal)
                .ToArray();

        var json = HasOption(args, "--json");
        var errors = 0;
        long objects = 0;
        var results = new List<object>();
        foreach (var file in files)
        {
            var document = ParseFile(file);
            if (document.HasErrors)
            {
                errors++;
                if (!json) PrintDiagnostics(document.Diagnostics);
            }
            objects += CountObjects(document.Root);
            results.Add(new
            {
                path = file,
                root = document.Root?.QualifiedName,
                elements = CountObjects(document.Root),
                diagnostics = ProjectDiagnostics(document.Diagnostics)
            });
        }

        if (json)
        {
            WriteJson(new { command = "corpus", files = results, fileCount = files.Length, elements = objects, errorFileCount = errors });
        }
        else
        {
            Console.WriteLine($"Parsed {files.Length} XAML files, {objects} object/member elements, {errors} files with errors.");
        }
        return errors == 0 ? 0 : 1;
    }

    private static async Task<int> InspectCommandAsync(string[] args)
    {
        if (args.Length < 4 || !TryGetOption(args, "--project", out var projectPath))
        {
            return MissingArgument("inspect requires <file> --project <project.csproj>");
        }

        var file = Path.GetFullPath(args[1]);
        var project = await OpenProjectAsync(Path.GetFullPath(projectPath!));
        var compilation = await project.GetCompilationAsync() ??
            throw new InvalidOperationException("Roslyn did not produce a compilation for " + projectPath + ".");
        compilation = RoslynXamlHostCompilation.WithoutGeneratedXamlTrees(
            compilation);
        var syntax = ParseFile(file);
        var infoset = new XamlInfosetConverter().Convert(syntax);
        var profile = GetProfile(args);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var semantic = new RoslynXamlSemanticManifestCompiler().Compile(
            infoset,
            typeSystem,
            profile,
            file,
            strict: true);
        var resourceGraph = new XamlResourceGraphBuilder().Build(
            semantic.BoundDocument);
        var program = new XamlConstructionLowerer().Lower(
            semantic.BoundDocument,
            resourceGraph);
        var diagnostics = program.Diagnostics
            .GroupBy(static diagnostic => diagnostic.ToString(), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        var report = new
        {
            command = "inspect",
            path = file,
            syntax = new { root = syntax.Root?.QualifiedName, elements = CountObjects(syntax.Root), tokens = syntax.SyntaxTree.Tokens.Length },
            infoset = DescribeInfoset(infoset.Root),
            bound = DescribeBound(semantic.BoundDocument.Root),
            resources = DescribeResources(resourceGraph),
            ir = DescribeIr(program.Root),
            diagnostics = ProjectDiagnostics(diagnostics)
        };
        if (HasOption(args, "--json")) WriteJson(report);
        else
        {
            PrintDiagnostics(diagnostics);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        return diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ? 1 : 0;
    }

    private static async Task<int> CompileCommandAsync(string[] args)
    {
        if (args.Length < 4 || !TryGetOption(args, "--project", out var projectPath))
        {
            return MissingArgument("compile requires <file> --project <project.csproj> [--output <directory>]");
        }

        var file = Path.GetFullPath(args[1]);
        projectPath = Path.GetFullPath(projectPath!);
        var output = TryGetOption(args, "--output", out var outputValue)
            ? Path.GetFullPath(outputValue!)
            : Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "ProGPU.Xaml.Cli");

        var project = await OpenProjectAsync(projectPath);
        var compilation = await project.GetCompilationAsync() ??
            throw new InvalidOperationException("Roslyn did not produce a compilation for " + projectPath + ".");
        return CompileFiles(compilation, new[] { file }, output, HasOption(args, "--json"), GetProfile(args), Path.GetDirectoryName(projectPath));
    }

    private static async Task<int> ProjectCommandAsync(string[] args)
    {
        if (args.Length < 2)
        {
            return MissingArgument("project requires <project.csproj> [--output <directory>]");
        }

        var projectPath = Path.GetFullPath(args[1]);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var output = TryGetOption(args, "--output", out var outputValue)
            ? Path.GetFullPath(outputValue!)
            : Path.Combine(projectDirectory, "obj", "ProGPU.Xaml.Cli");
        var project = await OpenProjectAsync(projectPath);
        var compilation = await project.GetCompilationAsync() ??
            throw new InvalidOperationException("Roslyn did not produce a compilation for " + projectPath + ".");
        var files = Directory.EnumerateFiles(projectDirectory, "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(projectDirectory, "*.axaml", SearchOption.AllDirectories))
            .Where(static file => !IsBuildOutput(file))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
        return CompileFiles(compilation, files, output, HasOption(args, "--json"), GetProfile(args), projectDirectory);
    }

    private static int CompileFiles(
        Compilation compilation,
        IReadOnlyList<string> files,
        string outputDirectory,
        bool json,
        IRoslynXamlFrameworkProfile profile,
        string? resourceRoot)
    {
        compilation = RoslynXamlHostCompilation.WithoutGeneratedXamlTrees(
            compilation);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var emitter = new CSharpXamlEmitter();
        var converter = new XamlInfosetConverter();
        var errors = 0;
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = new List<object>();
        var parsedFiles = files.Select(file =>
        {
            var syntax = XamlParser.Parse(
                SourceText.From(File.ReadAllText(file)),
                file,
                new XamlParseOptions { Mode = XamlParseMode.Strict }).Document;
            var infoset = converter.Convert(
                syntax,
                new XamlInfosetConversionOptions { Mode = XamlParseMode.Strict });
            var hostResourceUri = resourceRoot == null
                ? file
                : Path.GetRelativePath(resourceRoot, file);
            var semantic = new RoslynXamlSemanticManifestCompiler().Compile(
                infoset,
                typeSystem,
                profile,
                hostResourceUri,
                strict: true);
            return (
                File: file,
                Infoset: infoset,
                Manifest: semantic.Manifest,
                ResourceUri: semantic.ResourceUri);
        }).ToArray();
        var resourceIndex = new XamlResourceProjectIndex(parsedFiles.Select(static input => input.Manifest));

        foreach (var parsed in parsedFiles)
        {
            var file = parsed.File;
            var result = emitter.Emit(
                parsed.Infoset,
                typeSystem,
                profile,
                new XamlCompilerOptions
                {
                    Framework = profile.Id,
                    ResourceUri = parsed.ResourceUri,
                    ResourceDependencies = resourceIndex.GetDependencySlice(file),
                    Strict = true
                });
            if (!json) PrintDiagnostics(result.Diagnostics);
            if (result.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                errors++;
            }

            foreach (var source in result.Sources)
            {
                if (!artifacts.TryAdd(source.HintName, source.Source))
                {
                    Console.Error.WriteLine($"PGXAMLCLI0002: Generated hint name collision: {source.HintName}.");
                    errors++;
                }
            }
            inputs.Add(new
            {
                path = file,
                status = result.Diagnostics.Any(static diagnostic =>
                        diagnostic.Severity == DiagnosticSeverity.Error)
                    ? "failed"
                    : result.WasSkipped
                        ? "skipped"
                        : "compiled",
                className = result.BuildMetadata?.EffectiveClassName,
                rootNamespace = result.BuildMetadata?.RootNamespace?.Namespace,
                filePath = result.BuildMetadata?.FilePath?.FilePath,
                resource = result.BuildMetadata?.ResourceIdentity == null
                    ? null
                    : new
                    {
                        id = result.BuildMetadata.ResourceIdentity.ResourceId,
                        result.BuildMetadata.ResourceIdentity.Path,
                        type = result.BuildMetadata.ResourceIdentity.AssociatedType?
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    },
                compilationMode = result.BuildMetadata?.CompilationMode == null
                    ? null
                    : new
                    {
                        mode = result.BuildMetadata.CompilationMode.Mode?.ToString(),
                        scope = result.BuildMetadata.CompilationMode.Scope.ToString(),
                        rawValue = result.BuildMetadata.CompilationMode.RawValue,
                        provider = result.BuildMetadata.CompilationMode.ProviderId
                    },
                diagnostics = ProjectDiagnostics(result.Diagnostics),
                sources = result.Sources.Select(static source => new { source.HintName, length = source.Source.Length }).ToArray()
            });
        }

        if (errors == 0)
        {
            artifacts.Add("progpu-xaml.manifest.json", JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                framework = profile.Id,
                inputs,
                artifacts = artifacts.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray()
            }, JsonOptions));
            WriteArtifactsTransactionally(outputDirectory, artifacts);
        }
        if (json)
        {
            WriteJson(new
            {
                command = "compile",
                framework = profile.Id,
                outputDirectory,
                inputs,
                generatedArtifactCount = errors == 0 ? artifacts.Count - 1 : 0,
                errorInputCount = errors,
                committed = errors == 0
            });
        }
        else
        {
            Console.WriteLine($"Compiled {files.Count} XAML files; generated {(errors == 0 ? artifacts.Count - 1 : 0)} C# files in {outputDirectory}; {errors} inputs with errors.");
        }
        return errors == 0 ? 0 : 1;
    }

    private static void WriteArtifactsTransactionally(
        string outputDirectory,
        IReadOnlyDictionary<string, string> artifacts)
    {
        var fullOutput = Path.GetFullPath(outputDirectory);
        var parent = Path.GetDirectoryName(fullOutput) ??
            throw new InvalidOperationException("The output directory must have a parent directory.");
        Directory.CreateDirectory(parent);
        var transactionId = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        var staging = Path.Combine(parent, ".progpu-xaml-stage-" + transactionId);
        var backup = Path.Combine(parent, ".progpu-xaml-backup-" + transactionId);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);
        try
        {
            foreach (var artifact in artifacts)
            {
                File.WriteAllText(Path.Combine(staging, artifact.Key), artifact.Value);
            }

            Directory.CreateDirectory(fullOutput);
            var committed = new List<string>();
            try
            {
                foreach (var artifact in artifacts)
                {
                    var destination = Path.Combine(fullOutput, artifact.Key);
                    if (File.Exists(destination)) File.Move(destination, Path.Combine(backup, artifact.Key));
                    File.Move(Path.Combine(staging, artifact.Key), destination);
                    committed.Add(artifact.Key);
                }
            }
            catch
            {
                foreach (var name in committed)
                {
                    var destination = Path.Combine(fullOutput, name);
                    if (File.Exists(destination)) File.Delete(destination);
                }
                foreach (var artifact in artifacts)
                {
                    var saved = Path.Combine(backup, artifact.Key);
                    if (File.Exists(saved)) File.Move(saved, Path.Combine(fullOutput, artifact.Key));
                }
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
    }

    private static async Task<Project> OpenProjectAsync(string projectPath)
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        return await OpenProjectCoreAsync(projectPath);
    }

    private static async Task<Project> OpenProjectCoreAsync(string projectPath)
    {
        var workspace = MSBuildWorkspace.Create();
        // The CLI consumes referenced assemblies for their Roslyn symbols; it does not edit
        // dependency projects. Prefer their built metadata so analyzer-only project graphs do
        // not become application project references in the standalone workspace.
        workspace.LoadMetadataForReferencedProjects = true;
        workspace.RegisterWorkspaceFailedHandler(eventArgs =>
            Console.Error.WriteLine("workspace: " + eventArgs.Diagnostic.Message));
        return await workspace.OpenProjectAsync(projectPath);
    }

    private static XamlDocumentSyntax ParseFile(string path) =>
        new XamlXmlParser().Parse(path, File.ReadAllText(path));

    private static void PrintDocumentSummary(XamlDocumentSyntax document)
    {
        PrintDiagnostics(document.Diagnostics);
        Console.WriteLine(
            $"{document.Path}: root={document.Root?.QualifiedName ?? "<none>"}, " +
            $"elements={CountObjects(document.Root)}, diagnostics={document.Diagnostics.Length}.");
    }

    private static long CountObjects(XamlObjectSyntax? node)
    {
        if (node == null)
        {
            return 0;
        }

        long count = 1;
        foreach (var child in node.Children)
        {
            if (child is XamlObjectSyntax objectChild)
            {
                count += CountObjects(objectChild);
            }
        }
        return count;
    }

    private static void PrintDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var writer = diagnostic.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;
            writer.WriteLine(diagnostic);
        }
    }

    private static object? DescribeInfoset(XamlInfosetObject? root) => root == null ? null : new
    {
        type = root.TypeName.ToString(),
        root.StableId,
        root.IsMarkupExtension,
        root.IsRetrieved,
        members = root.Members.Select(member => new
        {
            name = member.Name.ToString(),
            origin = member.Origin.ToString(),
            member.StableId,
            values = member.Values.Length
        }).ToArray()
    };

    private static object? DescribeBound(XamlBoundObject? root) => root == null ? null : new
    {
        requestedType = root.Type.RequestedName.ToString(),
        resolvedType = root.Type.Symbol?.MetadataName,
        root.StableId,
        isError = root.Type.IsError,
        members = root.Members.Select(member => new
        {
            requestedMember = member.Member.RequestedName.ToString(),
            resolvedMember = member.Member.Symbol?.Symbol?.ToDisplayString(),
            kind = member.Member.Kind.ToString(),
            operationValues = member.Values.Length
        }).ToArray()
    };

    private static object? DescribeIr(XamlIrObject? root) => root == null ? null : new
    {
        kind = root.Kind.ToString(),
        type = root.Type.Symbol?.MetadataName ?? root.Type.RequestedName.ToString(),
        root.StableId,
        operations = root.Operations.Select(operation => new
        {
            kind = operation.Kind.ToString(),
            member = operation.Member.RequestedName.ToString(),
            operation.StableId,
            values = operation.Values.Length
        }).ToArray()
    };

    private static object DescribeResources(XamlResourceGraph graph) => new
    {
        scopes = graph.Scopes.Select(scope => new
        {
            scope.Id,
            scope.ParentId,
            scope.OwnerStableId
        }).ToArray(),
        definitions = graph.Definitions.Select(definition => new
        {
            definition.Key,
            definition.ScopeId,
            definition.ValueStableId,
            definition.Ordinal
        }).ToArray(),
        references = graph.References.Select(reference => new
        {
            reference.Key,
            kind = reference.Kind.ToString(),
            reference.ScopeId,
            reference.StableId,
            resolution = reference.Resolution.ToString(),
            reference.DefinitionStableId
        }).ToArray()
    };

    private static object[] ProjectDiagnostics(IEnumerable<Diagnostic> diagnostics) => diagnostics.Select(diagnostic =>
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        return (object)new
        {
            id = diagnostic.Id,
            severity = diagnostic.Severity.ToString(),
            message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            path = lineSpan.Path,
            startLine = lineSpan.StartLinePosition.Line,
            startCharacter = lineSpan.StartLinePosition.Character,
            endLine = lineSpan.EndLinePosition.Line,
            endCharacter = lineSpan.EndLinePosition.Character
        };
    }).ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static bool HasOption(string[] args, string name) =>
        Array.Exists(args, value => string.Equals(value, name, StringComparison.Ordinal));

    private static IRoslynXamlFrameworkProfile GetProfile(string[] args)
    {
        var id = TryGetOption(args, "--framework", out var value) ? value! : "WinUI";
        if (XamlFrameworkProfileRegistry.BuiltIn.TryCreate(id, out var profile)) return profile!;
        throw new InvalidOperationException($"Framework profile '{id}' is not registered in this CLI package.");
    }

    private static bool TryGetOption(string[] args, string name, out string? value)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                value = args[index + 1];
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine("Unknown command: " + command);
        PrintHelp();
        return 2;
    }

    private static int MissingArgument(string message)
    {
        Console.Error.WriteLine(message);
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ProGPU XAML compiler");
        Console.WriteLine("  progpu-xaml parse <file>");
        Console.WriteLine("  progpu-xaml corpus <file-or-directory> [--json]");
        Console.WriteLine("  progpu-xaml inspect <file> --project <project.csproj> [--framework <id>] [--json]");
        Console.WriteLine("  progpu-xaml compile <file> --project <project.csproj> [--framework <id>] [--output <directory>] [--json]");
        Console.WriteLine("  progpu-xaml project <project.csproj> [--framework <id>] [--output <directory>] [--json]");
    }
}
