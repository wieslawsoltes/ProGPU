using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.SourceGeneration;

[Generator(LanguageNames.CSharp)]
public sealed class ProGpuXamlSourceGenerator : IIncrementalGenerator
{
    private readonly XamlFrameworkProfileRegistry _profiles;

    public ProGpuXamlSourceGenerator()
        : this(XamlFrameworkProfileRegistry.BuiltIn)
    {
    }

    /// <summary>
    /// Creates an analyzer entry point over an explicit immutable profile registry. Framework
    /// packages can use this constructor from their own generator facade without assembly scans.
    /// </summary>
    public ProGpuXamlSourceGenerator(XamlFrameworkProfileRegistry profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var profiles = _profiles;
        var inputs = context.AdditionalTextsProvider
            .Where(static file => IsXamlPath(file.Path))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(static input => ShouldCompile(input.Left, input.Right))
            .Select(static (input, cancellationToken) => new XamlInput(
                input.Left.Path,
                GetLogicalPath(input.Left, input.Right),
                input.Left.GetText(cancellationToken) ?? SourceText.From(string.Empty)))
            .WithTrackingName("XamlInputs");

        var options = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
            GeneratorOptions.Create(provider.GlobalOptions));

        var parsedInputs = inputs.Combine(options).Select(static (input, cancellationToken) =>
        {
            var xamlInput = input.Left;
            var generatorOptions = input.Right;
            var mode = generatorOptions.Strict ? XamlParseMode.Strict : XamlParseMode.Recovering;
            var syntax = XamlParser.Parse(
                xamlInput.Text,
                xamlInput.Path,
                new XamlParseOptions { Mode = mode },
                cancellationToken).Document;
            var infoset = new XamlInfosetConverter().Convert(
                syntax,
                new XamlInfosetConversionOptions { Mode = mode },
                cancellationToken);
            return new ParsedXamlInput(xamlInput, generatorOptions.Strict, infoset);
        }).WithTrackingName("XamlParseAndInfoset");

        var resourceInputs = parsedInputs.Select(static (input, _) =>
            new ResourceParsedXamlInput(
                input,
                new XamlResourceDocumentManifestBuilder().Build(input.Infoset, input.LogicalPath)))
            .WithTrackingName("XamlResourceManifest");
        var semanticResourceInputs = resourceInputs.Combine(context.CompilationProvider).Combine(options)
            .Select((input, cancellationToken) =>
            {
                var resourceInput = input.Left.Left;
                if (!profiles.TryCreate(input.Right.Framework, out var profile))
                    return new SemanticResourceParsedXamlInput(
                        resourceInput.Input,
                        resourceInput.Manifest,
                        resourceInput.Input.LogicalPath);
                var typeSystem = new RoslynXamlTypeSystem(input.Left.Right, profile!);
                try
                {
                    var semantic = new RoslynXamlSemanticManifestCompiler().Compile(
                        resourceInput.Input.Infoset,
                        typeSystem,
                        profile!,
                        resourceInput.Input.LogicalPath,
                        input.Right.Strict,
                        cancellationToken);
                    return new SemanticResourceParsedXamlInput(
                        resourceInput.Input,
                        semantic.Manifest,
                        semantic.ResourceUri);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Final per-document emission owns provider-composition diagnostics.
                    // Preserve the compilation-independent manifest for dependency slicing.
                    return new SemanticResourceParsedXamlInput(
                        resourceInput.Input,
                        resourceInput.Manifest,
                        resourceInput.Input.LogicalPath);
                }
            })
            .WithTrackingName("XamlSemanticResourceManifest");
        var resourceIndex = semanticResourceInputs.Select(static (input, _) => input.Manifest)
            .Collect().Select(static (manifests, _) =>
            new XamlResourceProjectIndex(manifests));
        var dependencyInputs = semanticResourceInputs.Combine(resourceIndex).Select(static (input, _) =>
            new DependentParsedXamlInput(
                input.Left.Input,
                input.Right.GetDependencySlice(input.Left.Input.Path),
                input.Left.ResourceUri))
            .WithTrackingName("XamlResourceDependencySlice");

        var pipeline = dependencyInputs.Combine(context.CompilationProvider).Combine(options)
            .WithTrackingName("XamlBindLowerEmit");
        context.RegisterSourceOutput(pipeline, (productionContext, input) =>
        {
            var dependentInput = input.Left.Left;
            var xamlInput = dependentInput.Input;
            var compilation = input.Left.Right;
            var generatorOptions = input.Right;
            try
            {
                Execute(
                    productionContext,
                    dependentInput,
                    compilation,
                    generatorOptions,
                    profiles);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                productionContext.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "PGXAML9000",
                        "XAML compiler failure",
                        "The XAML compiler failed for '{0}': {1}",
                        "ProGPU.Xaml",
                        DiagnosticSeverity.Error,
                        true),
                    Location.None,
                    xamlInput.Path,
                    exception.Message));
            }
        });
    }

    private static void Execute(
        SourceProductionContext context,
        DependentParsedXamlInput dependentInput,
        Compilation compilation,
        GeneratorOptions generatorOptions,
        XamlFrameworkProfileRegistry profiles)
    {
        var input = dependentInput.Input;
        if (!generatorOptions.Enabled)
        {
            return;
        }

        if (!profiles.TryCreate(generatorOptions.Framework, out var selectedProfile))
        {
            var installedProfiles = profiles.ProfileIds.Count == 0
                ? "<none>"
                : string.Join(", ", profiles.ProfileIds);
            context.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor(
                    "PGXAML0002",
                    "Unknown XAML framework profile",
                    "XAML framework profile '{0}' is not registered for this generator package. Installed profiles: {1}.",
                    "ProGPU.Xaml",
                    DiagnosticSeverity.Error,
                    true),
                Location.None,
                generatorOptions.Framework,
                installedProfiles));
            return;
        }

        var profile = selectedProfile!;
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var emitter = new CSharpXamlEmitter();
        var result = emitter.Emit(
            input.Infoset,
            typeSystem,
            profile,
            new XamlCompilerOptions
            {
                Framework = profile.Id,
                ResourceUri = dependentInput.ResourceUri,
                ResourceDependencies = dependentInput.ResourceDependencies,
                Strict = generatorOptions.Strict,
                EmitHotReloadHooks = generatorOptions.EmitHotReloadHooks,
                EmitSourceComments = generatorOptions.EmitSourceComments,
                StaticResourceForwardReferenceMode =
                    generatorOptions.StaticResourceForwardReferenceMode
            },
            context.CancellationToken);

        for (var index = 0; index < result.Diagnostics.Count; index++)
        {
            context.ReportDiagnostic(result.Diagnostics[index]);
        }

        for (var index = 0; index < result.Sources.Count; index++)
        {
            context.AddSource(
                result.Sources[index].HintName,
                SourceText.From(
                    result.Sources[index].Source,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
        }
    }

    private static bool IsXamlPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".axaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldCompile(AdditionalText file, AnalyzerConfigOptionsProvider optionsProvider)
    {
        var options = optionsProvider.GetOptions(file);
        return !options.TryGetValue("build_metadata.AdditionalFiles.ProGpuXamlCompile", out var value) ||
               !bool.TryParse(value, out var enabled) || enabled;
    }

    private static string GetLogicalPath(AdditionalText file, AnalyzerConfigOptionsProvider optionsProvider)
    {
        var options = optionsProvider.GetOptions(file);
        return options.TryGetValue("build_metadata.AdditionalFiles.ProGpuXamlLogicalPath", out var value) &&
               !string.IsNullOrWhiteSpace(value)
            ? value
            : file.Path;
    }

    private sealed class XamlInput : IEquatable<XamlInput>
    {
        public XamlInput(string path, string logicalPath, SourceText text)
        {
            Path = path;
            LogicalPath = logicalPath;
            Text = text;
            Checksum = Convert.ToBase64String(text.GetChecksum().ToArray());
        }

        public string Path { get; }
        public string LogicalPath { get; }
        public SourceText Text { get; }
        private string Checksum { get; }
        public bool Equals(XamlInput? other) => other != null &&
            StringComparer.Ordinal.Equals(Path, other.Path) &&
            StringComparer.Ordinal.Equals(LogicalPath, other.LogicalPath) &&
            StringComparer.Ordinal.Equals(Checksum, other.Checksum);
        public override bool Equals(object? obj) => Equals(obj as XamlInput);
        public override int GetHashCode() => unchecked(
            (((StringComparer.Ordinal.GetHashCode(Path) * 397) ^ StringComparer.Ordinal.GetHashCode(LogicalPath)) * 397) ^
            StringComparer.Ordinal.GetHashCode(Checksum));
    }

    private sealed class ParsedXamlInput : IEquatable<ParsedXamlInput>
    {
        public ParsedXamlInput(XamlInput input, bool strict, XamlInfosetDocument infoset)
        {
            Input = input;
            Strict = strict;
            Infoset = infoset;
        }

        private XamlInput Input { get; }
        private bool Strict { get; }
        public string Path => Input.Path;
        public string LogicalPath => Input.LogicalPath;
        public XamlInfosetDocument Infoset { get; }
        public bool Equals(ParsedXamlInput? other) => other != null &&
            Strict == other.Strict && Input.Equals(other.Input);
        public override bool Equals(object? obj) => Equals(obj as ParsedXamlInput);
        public override int GetHashCode() => unchecked((Input.GetHashCode() * 397) ^ Strict.GetHashCode());
    }

    private sealed class DependentParsedXamlInput : IEquatable<DependentParsedXamlInput>
    {
        public DependentParsedXamlInput(
            ParsedXamlInput input,
            XamlResourceDependencySlice resourceDependencies,
            string resourceUri)
        {
            Input = input;
            ResourceDependencies = resourceDependencies;
            ResourceUri = resourceUri;
        }
        public ParsedXamlInput Input { get; }
        public XamlResourceDependencySlice ResourceDependencies { get; }
        public string ResourceUri { get; }
        public bool Equals(DependentParsedXamlInput? other) => other != null &&
            Input.Equals(other.Input) &&
            ResourceDependencies.Equals(other.ResourceDependencies) &&
            StringComparer.Ordinal.Equals(ResourceUri, other.ResourceUri);
        public override bool Equals(object? obj) => Equals(obj as DependentParsedXamlInput);
        public override int GetHashCode() => unchecked(
            (((Input.GetHashCode() * 397) ^ ResourceDependencies.GetHashCode()) * 397) ^
            StringComparer.Ordinal.GetHashCode(ResourceUri));
    }

    private sealed class ResourceParsedXamlInput : IEquatable<ResourceParsedXamlInput>
    {
        public ResourceParsedXamlInput(ParsedXamlInput input, XamlResourceDocumentManifest manifest)
        { Input = input; Manifest = manifest; }
        public ParsedXamlInput Input { get; }
        public XamlResourceDocumentManifest Manifest { get; }
        public bool Equals(ResourceParsedXamlInput? other) => other != null &&
            Input.Equals(other.Input) && Manifest.Equals(other.Manifest);
        public override bool Equals(object? obj) => Equals(obj as ResourceParsedXamlInput);
        public override int GetHashCode() => unchecked((Input.GetHashCode() * 397) ^ Manifest.GetHashCode());
    }

    private sealed class SemanticResourceParsedXamlInput : IEquatable<SemanticResourceParsedXamlInput>
    {
        public SemanticResourceParsedXamlInput(
            ParsedXamlInput input,
            XamlResourceDocumentManifest manifest,
            string resourceUri)
        {
            Input = input;
            Manifest = manifest;
            ResourceUri = resourceUri;
        }
        public ParsedXamlInput Input { get; }
        public XamlResourceDocumentManifest Manifest { get; }
        public string ResourceUri { get; }
        public bool Equals(SemanticResourceParsedXamlInput? other) => other != null &&
            Input.Equals(other.Input) &&
            Manifest.Equals(other.Manifest) &&
            StringComparer.Ordinal.Equals(ResourceUri, other.ResourceUri);
        public override bool Equals(object? obj) => Equals(obj as SemanticResourceParsedXamlInput);
        public override int GetHashCode() => unchecked(
            (((Input.GetHashCode() * 397) ^ Manifest.GetHashCode()) * 397) ^
            StringComparer.Ordinal.GetHashCode(ResourceUri));
    }

    private sealed class GeneratorOptions : IEquatable<GeneratorOptions>
    {
        private GeneratorOptions(
            bool enabled,
            string framework,
            bool strict,
            bool emitHotReloadHooks,
            bool emitSourceComments,
            XamlStaticResourceForwardReferenceMode staticResourceForwardReferenceMode)
        {
            Enabled = enabled;
            Framework = framework;
            Strict = strict;
            EmitHotReloadHooks = emitHotReloadHooks;
            EmitSourceComments = emitSourceComments;
            StaticResourceForwardReferenceMode = staticResourceForwardReferenceMode;
        }

        public bool Enabled { get; }
        public string Framework { get; }
        public bool Strict { get; }
        public bool EmitHotReloadHooks { get; }
        public bool EmitSourceComments { get; }
        public XamlStaticResourceForwardReferenceMode StaticResourceForwardReferenceMode { get; }
        public bool Equals(GeneratorOptions? other) => other != null &&
            Enabled == other.Enabled && Strict == other.Strict &&
            EmitHotReloadHooks == other.EmitHotReloadHooks &&
            EmitSourceComments == other.EmitSourceComments &&
            StaticResourceForwardReferenceMode == other.StaticResourceForwardReferenceMode &&
            StringComparer.OrdinalIgnoreCase.Equals(Framework, other.Framework);
        public override bool Equals(object? obj) => Equals(obj as GeneratorOptions);
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Enabled.GetHashCode();
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Framework);
                hash = (hash * 397) ^ Strict.GetHashCode();
                hash = (hash * 397) ^ EmitHotReloadHooks.GetHashCode();
                hash = (hash * 397) ^ EmitSourceComments.GetHashCode();
                return (hash * 397) ^ StaticResourceForwardReferenceMode.GetHashCode();
            }
        }

        public static GeneratorOptions Create(AnalyzerConfigOptions options)
        {
            return new GeneratorOptions(
                GetBoolean(options, "build_property.ProGpuXamlEnabled", true),
                GetString(options, "build_property.ProGpuXamlFramework", "WinUI"),
                GetBoolean(options, "build_property.ProGpuXamlStrict", true),
                GetBoolean(options, "build_property.ProGpuXamlEmitHotReloadHooks", true),
                GetBoolean(options, "build_property.ProGpuXamlEmitSourceComments", true),
                GetStaticResourceForwardReferenceMode(options));
        }

        private static XamlStaticResourceForwardReferenceMode GetStaticResourceForwardReferenceMode(
            AnalyzerConfigOptions options)
        {
            var value = GetString(
                options,
                "build_property.ProGpuXamlStaticResourceForwardReferenceMode",
                nameof(XamlStaticResourceForwardReferenceMode.Error));
            return Enum.TryParse<XamlStaticResourceForwardReferenceMode>(
                value,
                ignoreCase: true,
                out var result)
                ? result
                : XamlStaticResourceForwardReferenceMode.Error;
        }

        private static string GetString(AnalyzerConfigOptions options, string key, string fallback)
        {
            return options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        private static bool GetBoolean(AnalyzerConfigOptions options, string key, bool fallback)
        {
            return options.TryGetValue(key, out var value) && bool.TryParse(value, out var result)
                ? result
                : fallback;
        }
    }
}
