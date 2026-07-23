using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Diagnostics;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Roslyn;

public sealed class XamlSemanticBindingOptions
{
    public bool Strict { get; set; } = true;
    public int MaximumDiagnostics { get; set; } = 1024;
}

/// <summary>Resolves a schema-neutral XAML infoset to canonical Roslyn-backed schema descriptors.</summary>
public sealed class XamlSemanticBinder
{
    private readonly XamlTypeNameParser _typeNameParser = new XamlTypeNameParser();
    private readonly XamlMarkupExtensionParser _markupExtensionParser = new XamlMarkupExtensionParser();
    private readonly XamlBindingPathParser _bindingPathParser = new XamlBindingPathParser();
    private XamlInfosetDocument _document = null!;
    private IXamlTypeSystem _typeSystem = null!;
    private XamlSemanticBindingOptions _options = null!;
    private CancellationToken _cancellationToken;
    private ImmutableArray<Diagnostic>.Builder _diagnostics = null!;
    private Dictionary<ulong, ImmutableArray<XamlBoundTypeValue>> _typeArgumentCache = null!;
    private XamlTypeInfo? _rootClassType;
    private XamlCompiledBindingMode _activeCompiledBindingMode;
    private XamlTypeInfo? _activeCompiledBindingSourceType;
    private XamlCompiledBindingSourceKind _activeCompiledBindingSourceKind;
    private XamlTypeInfo? _activeLexicalDataType;
    private Dictionary<ulong, ResolvedBindingSource> _resolvedBindingSources = null!;

    public XamlBoundDocument Bind(
        XamlInfosetDocument document,
        IXamlTypeSystem typeSystem,
        XamlSemanticBindingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _typeSystem = typeSystem ?? throw new ArgumentNullException(nameof(typeSystem));
        _options = options ?? new XamlSemanticBindingOptions();
        _cancellationToken = cancellationToken;
        _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        _typeArgumentCache = new Dictionary<ulong, ImmutableArray<XamlBoundTypeValue>>();
        _rootClassType = ResolveRootClassType(document);
        _activeCompiledBindingMode =
            (typeSystem as IXamlCompiledBindingPolicy)?.DefaultCompiledBindingMode ??
            XamlCompiledBindingMode.OneTime;
        _activeCompiledBindingSourceType = _rootClassType;
        _activeCompiledBindingSourceKind = XamlCompiledBindingSourceKind.Root;
        _activeLexicalDataType = null;
        _resolvedBindingSources = new Dictionary<ulong, ResolvedBindingSource>();

        XamlBoundObject? root = null;
        if (document.Root != null && typeSystem is IXamlSymbolShapeConflictProvider conflictProvider)
            ReportSymbolShapeConflicts(conflictProvider.SymbolShapeConflicts, document.Root.SourceSpan);
        if (document.Root != null && !(_options.Strict && document.HasErrors))
        {
            root = BindObject(document.Root);
            ValidateDocumentSemantics(root);
            root = RewriteResolvedBindings(root);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(document.Diagnostics.Length + _diagnostics.Count);
        diagnostics.AddRange(document.Diagnostics);
        diagnostics.AddRange(_diagnostics);
        var dictionaryKeyDirectiveAliases = typeSystem is IXamlDictionaryKeyDirectivePolicy keyPolicy
            ? keyPolicy.DictionaryKeyDirectiveAliases.ToImmutableArray()
            : ImmutableArray<string>.Empty;
        return new XamlBoundDocument(
            document,
            root,
            diagnostics.ToImmutable(),
            dictionaryKeyDirectiveAliases,
            _rootClassType);
    }

    /// <summary>
    /// Immutably enriches ordinary bindings whose explicit Source is a resource reference
    /// after lexical resource resolution has established one exact Roslyn value type.
    /// Resource values and markup extensions are never constructed or invoked.
    /// </summary>
    public XamlBoundDocument EnrichResourceBindingSources(
        XamlBoundDocument document,
        XamlResourceGraph resourceGraph,
        IXamlTypeSystem typeSystem,
        XamlSemanticBindingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (resourceGraph == null) throw new ArgumentNullException(nameof(resourceGraph));
        if (typeSystem == null) throw new ArgumentNullException(nameof(typeSystem));
        if (document.Root == null) return document;

        _document = document.Infoset;
        _typeSystem = typeSystem;
        _options = options ?? new XamlSemanticBindingOptions();
        _cancellationToken = cancellationToken;
        _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var valueTypes = XamlResourceTypeEvidence.CreateValueTypeIndex(
            document.Root,
            resourceGraph,
            ResolveExternalResourceType);
        var references = resourceGraph.References.ToDictionary(
            static reference => reference.StableId);
        var root = RewriteResourceBindingSources(
            document.Root,
            references,
            valueTypes);
        if (ReferenceEquals(root, document.Root) && _diagnostics.Count == 0)
            return document;

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(
            document.Diagnostics.Length + _diagnostics.Count);
        diagnostics.AddRange(document.Diagnostics);
        diagnostics.AddRange(_diagnostics);
        return new XamlBoundDocument(
            document.Infoset,
            root,
            diagnostics.ToImmutable(),
            document.DictionaryKeyDirectiveAliases,
            document.RootClassType);
    }

    private XamlTypeInfo? ResolveExternalResourceType(string typeName)
    {
        var separator = typeName.IndexOf('|');
        if (separator > 0 && separator < typeName.Length - 1)
            return _typeSystem.ResolveType(
                typeName.Substring(0, separator),
                typeName.Substring(separator + 1));
        return (_typeSystem as IXamlMetadataTypeResolver)?.ResolveMetadataType(typeName);
    }

    private XamlTypeInfo? ResolveRootClassType(XamlInfosetDocument document)
    {
        var className = document.Root?.Members.FirstOrDefault(member =>
            member.Name.IsDirective &&
            string.Equals(member.Name.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
            string.Equals(member.Name.LocalName, "Class", StringComparison.Ordinal))?
            .Values.OfType<XamlInfosetText>().FirstOrDefault()?.Text.Trim();
        if (string.IsNullOrEmpty(className)) return null;
        if (_typeSystem is IXamlMetadataTypeResolver resolver)
        {
            var resolved = resolver.ResolveMetadataType(className!);
            if (resolved != null) return resolved;
        }
        return null;
    }

    private void ReportSymbolShapeConflicts(
        IReadOnlyList<XamlSymbolShapeConflictInfo> conflicts,
        TextSpan span)
    {
        foreach (var conflict in conflicts)
        {
            var scope = conflict.Key == null
                ? conflict.Feature.ToString()
                : conflict.Feature + " key '" + conflict.Key + "'";
            var evidence = string.Join(
                ", ",
                conflict.Candidates.Select(candidate =>
                    "'" + candidate.ProviderId + "' (priority " +
                    candidate.ProviderPriority + ") = '" + candidate.Value + "'"));
            Error(
                "PGXAML2049",
                "Symbol-shape configuration conflict for " + scope + ": " + evidence + ".",
                span,
                "5.2.1.1");
        }
    }

    private XamlBoundObject BindObject(XamlInfosetObject value)
    {
        var previousMode = _activeCompiledBindingMode;
        var previousSourceType = _activeCompiledBindingSourceType;
        var previousSourceKind = _activeCompiledBindingSourceKind;
        var previousDataType = _activeLexicalDataType;
        var policy = _typeSystem as IXamlCompiledBindingPolicy;
        if (!string.IsNullOrEmpty(policy?.DefaultCompiledBindingModeDirective))
        {
            var directive = value.Members.FirstOrDefault(member =>
                string.Equals(member.Name.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                string.Equals(
                    member.Name.LocalName,
                    policy!.DefaultCompiledBindingModeDirective,
                    StringComparison.Ordinal));
            var text = directive?.Values.OfType<XamlInfosetText>().FirstOrDefault()?.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                if (Enum.TryParse<XamlCompiledBindingMode>(text, ignoreCase: false, out var mode) &&
                    mode != XamlCompiledBindingMode.Default)
                    _activeCompiledBindingMode = mode;
                else
                    Error(
                        "PGXAML2117",
                        $"Default compiled-binding mode '{text}' must be OneTime, OneWay, or TwoWay.",
                        directive!.SourceSpan,
                        "EXT-004");
            }
        }
        if (!string.IsNullOrEmpty(policy?.CompiledBindingDataTypeDirective))
        {
            var directive = value.Members.FirstOrDefault(member =>
                string.Equals(member.Name.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                string.Equals(
                    member.Name.LocalName,
                    policy!.CompiledBindingDataTypeDirective,
                    StringComparison.Ordinal));
            var text = directive?.Values.OfType<XamlInfosetText>().FirstOrDefault()?.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var parsed = _typeNameParser.Parse(text!);
                var resolved = !parsed.HasErrors && parsed.Types.Length == 1
                    ? ResolveTypeName(parsed.Types[0], value.NamespaceMappings)
                    : null;
                if (resolved == null)
                    Error(
                        "PGXAML2120",
                        $"Binding data type '{text}' could not be resolved.",
                        directive!.SourceSpan,
                        "EXT-004");
                else
                {
                    _activeLexicalDataType = resolved;
                    _activeCompiledBindingSourceType = resolved;
                    _activeCompiledBindingSourceKind =
                        XamlCompiledBindingSourceKind.Context;
                }
            }
        }
        try
        {
            return BindObjectCore(value);
        }
        finally
        {
            _activeCompiledBindingMode = previousMode;
            _activeCompiledBindingSourceType = previousSourceType;
            _activeCompiledBindingSourceKind = previousSourceKind;
            _activeLexicalDataType = previousDataType;
        }
    }

    private XamlBoundObject BindObjectCore(XamlInfosetObject value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var schemaReprojected = TryReprojectMarkupExtension(value);
        if (schemaReprojected != null)
            return BindObject(schemaReprojected);

        var typeArguments = GetTypeArguments(value);
        var type = typeArguments.IsDefault
            ? _typeSystem.ResolveType(value.TypeName.NamespaceUri, value.TypeName.LocalName)
            : typeArguments.IsEmpty || typeArguments.Any(static argument => argument.Type.Symbol == null)
                ? null
                : _typeSystem.ResolveType(
                value.TypeName.NamespaceUri,
                value.TypeName.LocalName,
                typeArguments.Select(static argument => argument.Type.Symbol!)
                    .ToArray());
        XamlBoundTypeReference typeReference;
        if (type == null)
        {
            var diagnostic = Error(
                typeArguments.IsDefault ? "PGXAML2001" : "PGXAML2032",
                typeArguments.IsDefault
                    ? $"The XAML type '{value.TypeName.DisplayName}' could not be resolved in namespace '{value.TypeName.NamespaceUri}'."
                    : $"Type '{value.TypeName.DisplayName}' is not a generic type matching the supplied x:TypeArguments or its constraints are not satisfied.",
                value.SourceSpan,
                typeArguments.IsDefault ? "6.3.2.9" : "6.3.1.9");
            typeReference = XamlBoundTypeReference.Error(value.TypeName, diagnostic);
        }
        else
        {
            typeReference = XamlBoundTypeReference.Resolved(value.TypeName, type);
            ReportUsageAnnotations(type.Annotations, value.TypeName.DisplayName, value.SourceSpan);
            ReportSchemaAnnotationConflicts(type.Annotations, value.TypeName.DisplayName, value.SourceSpan);
            ReportSetValueHandlerShape(type.MarkupExtensionSetHandler, type.MetadataName, value.SourceSpan);
            ReportSetValueHandlerShape(type.TypeConverterSetHandler, type.MetadataName, value.SourceSpan);
            ReportMarkupExtensionReceiverShape(
                type.MarkupExtensionReceiver,
                type.MetadataName,
                value.SourceSpan);
            ReportValueSerializerShape(type.ValueSerializer, type.MetadataName, value.SourceSpan);
            ReportBooleanSchemaInfo(type.TrimSurroundingWhitespace, type.MetadataName, value.SourceSpan);
            ReportBooleanSchemaInfo(type.WhitespaceSignificantCollection, type.MetadataName, value.SourceSpan);
            ReportBooleanSchemaInfo(type.UsableDuringInitialization, type.MetadataName, value.SourceSpan);
            ReportBooleanSchemaInfo(type.Ambient, type.MetadataName, value.SourceSpan);
            ReportDeferringLoaderShape(type.DeferringLoader, type.MetadataName, value.SourceSpan);
            ReportAliasedMemberShape(type.NameScopeProperty, type.MetadataName, value.SourceSpan);
            ReportAliasedMemberShape(type.XmlLanguageProperty, type.MetadataName, value.SourceSpan);
            ReportAliasedMemberShape(type.UidProperty, type.MetadataName, value.SourceSpan);
            ReportNameScopeShape(type.NameScopeShape, type.MetadataName, value.SourceSpan);
            ReportMarkupExtensionOptionSelector(
                type.MarkupExtensionOptionSelector,
                type.MetadataName,
                value.SourceSpan);
            ReportListSplit(type.ListSplit, type.MetadataName, value.SourceSpan);
            ReportTemplateParts(type.TemplateParts, type.MetadataName, value.SourceSpan);
            ReportTemplateVisualStates(
                type.TemplateVisualStates,
                type.MetadataName,
                value.SourceSpan);
            ReportStyleTypedProperties(
                type.StyleTypedProperties,
                type.MetadataName,
                value.SourceSpan);
            ReportCompilationMode(type.CompilationMode, type.MetadataName, value.SourceSpan);
            ReportFilePath(type.FilePath, type.MetadataName, value.SourceSpan);
            ReportTypeMarker(
                type.Bindable,
                type.MetadataName,
                "bindable",
                "PGXAML2076",
                value.SourceSpan);
            ReportTypeMarker(
                type.FullMetadataProvider,
                type.MetadataName,
                "full XAML metadata provider",
                "PGXAML2077",
                value.SourceSpan);
            ReportBrowsableMetadata(
                type.Browsable,
                type.MetadataName,
                XamlSchemaSemantics.Browsable,
                value.SourceSpan);
            ReportEditorBrowsableMetadata(
                type.EditorBrowsable,
                type.MetadataName,
                value.SourceSpan);
            ReportBrowsableMetadata(
                type.DesignTimeVisible,
                type.MetadataName,
                XamlSchemaSemantics.DesignTimeVisible,
                value.SourceSpan);
            ReportLocalizabilityMetadata(
                type.Localizability,
                type.MetadataName,
                value.SourceSpan);
            ReportContentWrapperShapes(type.ContentWrappers, type.MetadataName, value.SourceSpan);
            if (value.IsMarkupExtension &&
                !string.Equals(value.TypeName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                !type.IsMarkupExtension)
                Error(
                    "PGXAML2046",
                    type.MarkupExtensionShape?.Error ??
                    $"Type '{type.MetadataName}' does not satisfy the registered markup-extension base, interface, or ProvideValue shape.",
                    value.SourceSpan,
                    "7.3.18");
        }

        var members = ImmutableArray.CreateBuilder<XamlBoundMember>(value.Members.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.Members)
        {
            if (IsWhitespaceOnlyImplicitMember(member))
                continue;
            var bound = BindMember(member, typeReference, value);
            if (member.Origin == XamlMemberOrigin.ImplicitContent && bound.Values.IsEmpty)
                continue;
            var conditionalIdentity = bound.Member.Identity + "\0" +
                (bound.Condition?.OriginalNamespaceUri ?? string.Empty);
            if (!bound.Member.IsError && !identities.Add(conditionalIdentity))
            {
                Error(
                    "PGXAML2010",
                    $"Member '{bound.Member.RequestedName.DisplayName}' resolves to a member already supplied on this object.",
                    bound.SourceSpan,
                    "6.2.1.3");
            }
            members.Add(bound);
        }

        if (type != null) ValidateAliasedMembers(type, members);
        if (type != null) ValidateMemberDependencies(type, members);
        var result = new XamlBoundObject(
            typeReference,
            members.ToImmutable(),
            value.IsRetrieved,
            value.IsMarkupExtension,
            value.SourceSpan,
            value.StableId,
            value.TypeName.Condition);
        ValidateIntrinsicObject(result);
        ValidateConstruction(result);
        return result;
    }

    private XamlInfosetObject? TryReprojectMarkupExtension(XamlInfosetObject value)
    {
        if (!value.IsMarkupExtension || value.MarkupText == null)
            return null;

        var resolver = new SemanticMarkupBracketPairResolver(
            _typeSystem,
            value.NamespaceMappings);
        var text = SourceText.From(value.MarkupText);
        var result = _markupExtensionParser.Parse(
            text,
            new TextSpan(0, text.Length),
            _document.Path,
            new XamlMarkupParseOptions
            {
                BracketPairResolver = resolver
            },
            _cancellationToken);
        if (!resolver.HasResolvedPairs)
            return null;
        if (result.HasErrors || result.Root == null)
        {
            var message = result.Diagnostics.FirstOrDefault(
                static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)?.GetMessage() ??
                "Invalid markup extension after applying its member delimiter policy.";
            Error(
                "PGXAML1101",
                message,
                value.SourceSpan,
                "8.6.7.1");
            return null;
        }

        return XamlInfosetConverter.ProjectMarkupExtension(
            value,
            result.Root);
    }

    private sealed class SemanticMarkupBracketPairResolver : IXamlMarkupBracketPairResolver
    {
        private static readonly IReadOnlyDictionary<char, char> EmptyPairs =
            new Dictionary<char, char>();

        private readonly IXamlTypeSystem _typeSystem;
        private readonly ImmutableArray<XamlNamespaceMapping> _namespaceMappings;
        private readonly Dictionary<string, IReadOnlyDictionary<char, char>> _cache =
            new Dictionary<string, IReadOnlyDictionary<char, char>>(StringComparer.Ordinal);

        public SemanticMarkupBracketPairResolver(
            IXamlTypeSystem typeSystem,
            ImmutableArray<XamlNamespaceMapping> namespaceMappings)
        {
            _typeSystem = typeSystem;
            _namespaceMappings = namespaceMappings;
        }

        public bool HasResolvedPairs { get; private set; }

        public IReadOnlyDictionary<char, char> GetBracketPairs(
            string extensionName,
            string memberName)
        {
            var key = extensionName + "\0" + memberName;
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var colon = extensionName.IndexOf(':');
            var prefix = colon < 0
                ? string.Empty
                : extensionName.Substring(0, colon);
            var localName = colon < 0
                ? extensionName
                : extensionName.Substring(colon + 1);
            var namespaceUri = ResolveNamespace(prefix, _namespaceMappings);
            var type = _typeSystem.ResolveType(namespaceUri, localName);
            if (type?.ExpressionRole == XamlExpressionRole.CompiledBinding &&
                _typeSystem is IXamlCompiledBindingPolicy compiledBindingPolicy &&
                (string.IsNullOrEmpty(memberName) ||
                 string.Equals(memberName, "Path", StringComparison.Ordinal)))
            {
                var compiledPairs =
                    compiledBindingPolicy.CompiledBindingPathBracketPairs;
                if (compiledPairs.Count != 0)
                    HasResolvedPairs = true;
                _cache.Add(key, compiledPairs);
                return compiledPairs;
            }
            var member = type == null
                ? null
                : _typeSystem.ResolveMember(
                    type,
                    type.NamespaceUri,
                    ownerTypeName: null,
                    memberName);
            var pairs = member == null
                ? EmptyPairs
                : XamlMarkupBracketPolicy.CreatePairs(member);
            if (pairs.Count != 0)
                HasResolvedPairs = true;
            _cache.Add(key, pairs);
            return pairs;
        }
    }

    private ImmutableArray<XamlBoundTypeValue> GetTypeArguments(XamlInfosetObject value)
    {
        var member = value.Members.FirstOrDefault(candidate =>
            candidate.Name.IsDirective && string.Equals(candidate.Name.LocalName, "TypeArguments", StringComparison.Ordinal));
        if (member == null) return default;
        if (_typeArgumentCache.TryGetValue(member.StableId, out var cached)) return cached;
        var builder = ImmutableArray.CreateBuilder<XamlBoundTypeValue>();
        var text = member.Values.OfType<XamlInfosetText>().SingleOrDefault();
        if (text == null)
        {
            Error("PGXAML2030", "x:TypeArguments requires one type-name text value.", member.SourceSpan, "6.3.1.9");
            cached = builder.ToImmutable();
            _typeArgumentCache.Add(member.StableId, cached);
            return cached;
        }
        var parsed = _typeNameParser.Parse(text.Text);
        if (parsed.HasErrors)
            Error("PGXAML2030", "x:TypeArguments does not match the intrinsic type-argument text syntax.", text.SourceSpan, "7.4.16");
        for (var index = 0; index < parsed.Types.Length; index++)
        {
            var syntax = parsed.Types[index];
            var resolved = ResolveTypeName(syntax, value.NamespaceMappings);
            var requestedNamespace = ResolveNamespace(syntax.Prefix, value.NamespaceMappings);
            var requested = new XamlQualifiedName(requestedNamespace, syntax.Name, syntax.Prefix);
            XamlBoundTypeReference reference;
            if (resolved == null)
            {
                var diagnostic = Error("PGXAML2031",
                    $"Type argument '{syntax.QualifiedName}' could not be resolved or violates generic constraints.",
                    text.SourceSpan,
                    "6.3.2.9");
                reference = XamlBoundTypeReference.Error(requested, diagnostic);
            }
            else reference = XamlBoundTypeReference.Resolved(requested, resolved);
            builder.Add(new XamlBoundTypeValue(
                reference,
                text.SourceSpan,
                text.StableId ^ ((ulong)(index + 1) * 11400714819323198485UL)));
        }
        cached = builder.ToImmutable();
        _typeArgumentCache.Add(member.StableId, cached);
        return cached;
    }

    private XamlTypeInfo? ResolveTypeName(
        XamlTypeNameSyntax syntax,
        ImmutableArray<XamlNamespaceMapping> mappings)
    {
        var namespaceUri = ResolveNamespace(syntax.Prefix, mappings);
        if (syntax.TypeArguments.IsEmpty) return _typeSystem.ResolveType(namespaceUri, syntax.Name);
        var arguments = new List<XamlTypeInfo>(syntax.TypeArguments.Length);
        foreach (var child in syntax.TypeArguments)
        {
            var argument = ResolveTypeName(child, mappings);
            if (argument == null) return null;
            arguments.Add(argument);
        }
        return _typeSystem.ResolveType(namespaceUri, syntax.Name, arguments);
    }

    private static string ResolveNamespace(string prefix, ImmutableArray<XamlNamespaceMapping> mappings)
    {
        foreach (var mapping in mappings)
            if (string.Equals(mapping.Prefix, prefix, StringComparison.Ordinal))
                return NormalizeConditionalNamespace(mapping.NamespaceUri);
        return string.Empty;
    }

    private static string NormalizeConditionalNamespace(string namespaceUri) =>
        XamlNamespaceCondition.TryParse(namespaceUri, out var condition)
            ? condition!.BaseNamespaceUri
            : namespaceUri;

    private void ValidateConstruction(XamlBoundObject value)
    {
        var type = value.Type.Symbol;
        if (type == null ||
            string.Equals(value.Type.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal)) return;
        XamlBoundMember? arguments = null;
        XamlBoundFactoryMethodValue? factory = null;
        foreach (var member in value.Members)
        {
            if (member.Member.Kind != XamlBoundReferenceKind.Directive) continue;
            if (string.Equals(member.Member.RequestedName.LocalName, "Arguments", StringComparison.Ordinal) ||
                string.Equals(member.Member.RequestedName.LocalName, "PositionalParameters", StringComparison.Ordinal))
                arguments = member;
            else if (string.Equals(member.Member.RequestedName.LocalName, "FactoryMethod", StringComparison.Ordinal))
                factory = member.Values.OfType<XamlBoundFactoryMethodValue>().SingleOrDefault();
        }
        if (factory != null)
        {
            if (factory.Method != null)
                ValidateCallArguments(type, arguments, factory.Method.Parameters, factory.Method.Name);
            return;
        }
        if (arguments == null)
        {
            var hasInitializationText = value.Members.Any(static member =>
                string.Equals(member.Member.RequestedName.LocalName, "Initialization", StringComparison.Ordinal));
            if (!type.IsDefaultConstructible && !hasInitializationText)
                Error("PGXAML2024",
                    $"Type '{type.MetadataName}' is not default constructible and requires x:Arguments or x:FactoryMethod.",
                    value.SourceSpan,
                    "6.2.2.3");
            return;
        }

        var matching = type.Constructors.Where(constructor => constructor.Arity == arguments.Values.Length).ToArray();
        if (matching.Length != 1)
        {
            Error("PGXAML2025",
                $"Type '{type.MetadataName}' has {matching.Length} public constructors accepting {arguments.Values.Length} arguments; exactly one is required.",
                arguments.SourceSpan,
                "6.2.2.4");
            return;
        }
        for (var index = 0; index < matching[0].Parameters.Count; index++)
        {
            var parameter = matching[0].Parameters[index];
            var span = index < arguments.Values.Length
                ? arguments.Values[index].SourceSpan
                : arguments.SourceSpan;
            ReportDataTypeInheritance(
                parameter.DataTypeInheritance,
                parameter.Symbol.Name,
                span);
        }
        if (matching[0].Symbol != null)
            ValidateCallArguments(type, arguments, matching[0].Symbol!.Parameters, ".ctor");
    }

    private void ValidateCallArguments(
        XamlTypeInfo constructedType,
        XamlBoundMember? arguments,
        IReadOnlyList<IParameterSymbol> parameters,
        string callableName)
    {
        if (arguments == null) return;
        for (var index = 0; index < arguments.Values.Length && index < parameters.Count; index++)
        {
            if (arguments.Values[index] is not XamlBoundObject objectValue || objectValue.Type.Symbol == null) continue;
            var parameterType = DescribeParameterType(parameters[index].Type, constructedType.NamespaceUri);
            if (!_typeSystem.IsAssignable(objectValue.Type.Symbol, parameterType))
                Error("PGXAML2026",
                    $"Construction argument type '{objectValue.Type.Symbol.MetadataName}' is not assignable to parameter '{parameters[index].Name}' of '{callableName}'.",
                    objectValue.SourceSpan,
                    "6.2.2.4");
        }
    }

    private static XamlTypeInfo DescribeParameterType(ITypeSymbol symbol, string namespaceUri) => new XamlTypeInfo(
        namespaceUri,
        symbol.Name,
        symbol,
        symbol.ToDisplayString(),
        symbol.IsValueType,
        symbol.TypeKind == TypeKind.Enum,
        symbol.NullableAnnotation == NullableAnnotation.Annotated || !symbol.IsValueType,
        false,
        false,
        null);

    private void ValidateIntrinsicObject(XamlBoundObject value)
    {
        if (!string.Equals(value.Type.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal))
            return;
        var localName = value.Type.RequestedName.LocalName;
        if (string.Equals(localName, "Type", StringComparison.Ordinal) ||
            string.Equals(localName, "TypeExtension", StringComparison.Ordinal))
        {
            var count = value.Members.SelectMany(member => EnumerateValues<XamlBoundTypeValue>(member.Values))
                .Count(candidate => candidate.Type.Symbol != null);
            if (count != 1)
                Error("PGXAML2019", "x:Type must specify exactly one resolvable type value.", value.SourceSpan, "6.2.2.8");
            return;
        }
        if (string.Equals(localName, "Static", StringComparison.Ordinal) ||
            string.Equals(localName, "StaticExtension", StringComparison.Ordinal))
        {
            var count = value.Members.SelectMany(member => EnumerateValues<XamlBoundStaticMemberValue>(member.Values))
                .Count(candidate => candidate.Member != null);
            if (count != 1)
                Error("PGXAML2020", "x:Static must specify exactly one resolvable public static member.", value.SourceSpan, "6.2.2.9");
            return;
        }
        if (!string.Equals(localName, "Array", StringComparison.Ordinal) &&
            !string.Equals(localName, "ArrayExtension", StringComparison.Ordinal)) return;

        XamlBoundTypeValue? elementType = null;
        XamlBoundMember? items = null;
        foreach (var member in value.Members)
        {
            if (member.Member.Kind == XamlBoundReferenceKind.Intrinsic &&
                string.Equals(member.Member.RequestedName.LocalName, "Type", StringComparison.Ordinal))
                elementType = FindValue<XamlBoundTypeValue>(member.Values);
            else if (member.Member.Kind == XamlBoundReferenceKind.CollectionItems)
                items = member;
        }
        if (elementType?.Type.Symbol == null)
        {
            Error("PGXAML2021", "x:Array requires exactly one resolvable Type member.", value.SourceSpan, "6.2.2.10");
            return;
        }
        if (items == null) return;
        foreach (var item in items.Values.OfType<XamlBoundObject>())
        {
            if (item.Type.Symbol != null && !_typeSystem.IsAssignable(item.Type.Symbol, elementType.Type.Symbol))
                Error("PGXAML2022",
                    $"Array item type '{item.Type.Symbol.MetadataName}' is not assignable to element type '{elementType.Type.Symbol.MetadataName}'.",
                    item.SourceSpan,
                    "6.2.2.10");
        }
    }

    private static T? FindValue<T>(ImmutableArray<XamlBoundValue> values) where T : XamlBoundValue =>
        EnumerateValues<T>(values).FirstOrDefault();

    private static IEnumerable<T> EnumerateValues<T>(ImmutableArray<XamlBoundValue> values) where T : XamlBoundValue
    {
        foreach (var value in values)
        {
            if (value is T match) yield return match;
            if (value is XamlBoundObject nested)
                foreach (var descendant in nested.Members.SelectMany(member => EnumerateValues<T>(member.Values)))
                    yield return descendant;
        }
    }

    private XamlBoundMember BindMember(
        XamlInfosetMember value,
        XamlBoundTypeReference parentType,
        XamlInfosetObject parentObject)
    {
        var memberReference = BindMemberReference(value, parentType);
        var values = ImmutableArray.CreateBuilder<XamlBoundValue>(value.Values.Length);
        foreach (var child in value.Values)
        {
            if (child is XamlInfosetObject childObject)
            {
                var boundObject = BindObject(childObject);
                values.Add(boundObject.Type.Symbol?.ExpressionRole switch
                {
                    XamlExpressionRole.Binding => BindBinding(
                        boundObject,
                        parentType.Symbol),
                    XamlExpressionRole.CompiledBinding => BindCompiledBinding(
                        boundObject,
                        memberReference,
                        childObject.NamespaceMappings),
                    _ => boundObject
                });
            }
            else
            {
                var text = (XamlInfosetText)child;
                if (IsIntrinsicPositionalMember(parentObject, value, "Type", "TypeExtension"))
                    values.Add(BindTypeValue(text, parentObject));
                else if (IsArrayIntrinsicMember(parentType, value, "Type"))
                    values.Add(BindTypeValue(text, parentObject));
                else if (IsIntrinsicPositionalMember(parentObject, value, "Static", "StaticExtension"))
                    values.Add(BindStaticMemberValue(text, parentObject));
                else if (IsIntrinsicPositionalMember(parentObject, value, "Reference", "ReferenceExtension"))
                    values.Add(new XamlBoundNameReferenceValue(text.Text.Trim(), text.SourceSpan, text.StableId));
                else if (value.Name.IsDirective &&
                         string.Equals(value.Name.LocalName, "FactoryMethod", StringComparison.Ordinal))
                    values.Add(BindFactoryMethodValue(text, parentObject, parentType));
                else if (value.Name.IsDirective &&
                         string.Equals(value.Name.LocalName, "TypeArguments", StringComparison.Ordinal))
                    values.AddRange(GetTypeArguments(parentObject));
                else if (string.Equals(
                             memberReference.Symbol?.ValueType.MetadataName,
                             "System.Type",
                             StringComparison.Ordinal))
                    values.Add(BindTypeValue(text, parentObject));
                else
                    values.Add(BindTextValue(text, memberReference, parentType));
            }
        }

        values = NormalizeElementContentWhitespace(
            value,
            parentObject,
            parentType,
            memberReference,
            values);
        values = ExpandAttributedListText(memberReference, values);
        values = ApplyContentWrappers(parentType, memberReference, values);

        if (memberReference.Symbol is { CanWrite: false } resolvedMember &&
            (resolvedMember.Kind == XamlMemberKind.Collection || resolvedMember.Kind == XamlMemberKind.Dictionary) &&
            values.Count == 1 && values[0] is XamlBoundObject retrievedCandidate &&
            !HasExplicitDictionaryKey(retrievedCandidate) &&
            retrievedCandidate.Type.Symbol != null &&
            _typeSystem.IsAssignable(retrievedCandidate.Type.Symbol, resolvedMember.ValueType))
        {
            values[0] = new XamlBoundObject(
                retrievedCandidate.Type,
                retrievedCandidate.Members,
                true,
                retrievedCandidate.IsMarkupExtension,
                retrievedCandidate.SourceSpan,
                retrievedCandidate.StableId,
                retrievedCandidate.Condition);
        }

        if (memberReference.Symbol != null)
        {
            ReportUsageAnnotations(
                memberReference.Symbol.Annotations,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportSchemaAnnotationConflicts(
                memberReference.Symbol.Annotations,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportValueSerializerShape(
                memberReference.Symbol.ValueSerializer,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportContentWrapperShapes(
                memberReference.Symbol.ValueType.ContentWrappers,
                memberReference.Symbol.ValueType.MetadataName,
                value.SourceSpan);
            ReportConstructorArgumentShape(
                memberReference.Symbol.ConstructorArgument,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportBooleanSchemaInfo(
                memberReference.Symbol.Ambient,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportDeferringLoaderShape(
                memberReference.Symbol.DeferringLoader,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportMarkupBracketPairs(
                memberReference.Symbol.MarkupExtensionBracketCharacters,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportMarkupExtensionOption(
                memberReference.Symbol.MarkupExtensionOption,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportDataTypeSource(
                memberReference.Symbol.DataTypeSource,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportDataTypeInheritance(
                memberReference.Symbol.DataTypeInheritance,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportItemsDataTypeInheritance(
                memberReference.Symbol.ItemsDataTypeInheritance,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportBindingAssignment(
                memberReference.Symbol.BindingAssignment,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportAttachedPropertyBrowseRules(
                memberReference.Symbol.AttachedPropertyBrowseRules,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportDefaultValueMetadata(
                memberReference.Symbol.DefaultValue,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportDesignerSerializationMetadata(
                memberReference.Symbol.DesignerSerializationVisibility,
                memberReference.Symbol.DesignerSerializationOptions,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportBrowsableMetadata(
                memberReference.Symbol.Browsable,
                memberReference.Symbol.Name,
                XamlSchemaSemantics.Browsable,
                value.SourceSpan);
            ReportEditorBrowsableMetadata(
                memberReference.Symbol.EditorBrowsable,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportLocalizabilityMetadata(
                memberReference.Symbol.Localizability,
                memberReference.Symbol.Name,
                value.SourceSpan);
            ReportDesignerSerializationMethods(
                memberReference.Symbol.SerializationMethods,
                memberReference.Symbol.Name,
                value.SourceSpan);
            foreach (var child in values)
            {
                if (child is not XamlBoundObject childObject || childObject.Type.Symbol == null) continue;
                if (childObject.IsMarkupExtension)
                {
                    var extensionType = childObject.Type.Symbol;
                    var hasDeclaredReturnType = extensionType.Annotations.Any(static annotation =>
                        string.Equals(annotation.Semantic, XamlSchemaSemantics.MarkupExtensionReturnType, StringComparison.Ordinal));
                    if (hasDeclaredReturnType && extensionType.ReturnValueType != null &&
                        memberReference.Symbol.Kind == XamlMemberKind.Property &&
                        !_typeSystem.IsAssignable(extensionType.ReturnValueType, memberReference.Symbol.ValueType.Symbol))
                    {
                        Error(
                            "PGXAML2047",
                            $"Markup extension '{extensionType.MetadataName}' declares return type " +
                            $"'{extensionType.ReturnValueType.ToDisplayString()}', which is not assignable to member " +
                            $"'{memberReference.Symbol.Name}' of type '{memberReference.Symbol.ValueType.MetadataName}'.",
                            child.SourceSpan,
                            "7.3.18");
                    }
                    continue;
                }
                if (memberReference.Symbol.Kind == XamlMemberKind.Property &&
                    !_typeSystem.IsAssignable(childObject.Type.Symbol, memberReference.Symbol.ValueType))
                {
                    Error(
                        "PGXAML2011",
                        $"Object of type '{childObject.Type.Symbol.MetadataName}' is not assignable to member " +
                        $"'{memberReference.Symbol.Name}' of type '{memberReference.Symbol.ValueType.MetadataName}'.",
                        child.SourceSpan,
                        "6.3.2.1");
                }
            }
        }

        if (values.Count > 1 &&
            memberReference.Kind != XamlBoundReferenceKind.CollectionItems &&
            memberReference.Kind != XamlBoundReferenceKind.DictionaryItems &&
            memberReference.Symbol?.Kind != XamlMemberKind.Collection &&
            memberReference.Symbol?.Kind != XamlMemberKind.Dictionary &&
            memberReference.Symbol?.Kind != XamlMemberKind.DeferredContent &&
            !(memberReference.Kind == XamlBoundReferenceKind.Directive &&
              (string.Equals(value.Name.LocalName, "PositionalParameters", StringComparison.Ordinal) ||
               string.Equals(value.Name.LocalName, "DirectiveChildren", StringComparison.Ordinal) ||
               string.Equals(value.Name.LocalName, "Arguments", StringComparison.Ordinal) ||
               string.Equals(value.Name.LocalName, "TypeArguments", StringComparison.Ordinal))))
        {
            Error("PGXAML2023",
                $"Member '{value.Name.DisplayName}' cannot contain multiple values.",
                value.SourceSpan,
                "6.3.1.2");
        }

        return new XamlBoundMember(
            memberReference,
            value.Origin,
            values.ToImmutable(),
            value.SourceSpan,
            value.StableId,
            value.Name.Condition);
    }

    private XamlBoundValue BindBinding(
        XamlBoundObject extension,
        XamlTypeInfo? targetObjectType,
        XamlTypeInfo? resolvedSourceType = null,
        XamlBindingSourceKind? resolvedSourceKind = null)
    {
        var path = GetBindingText(extension, "Path") ??
                   GetBindingPositionalText(extension) ??
                   string.Empty;
        path = path.Trim();
        var source = resolvedSourceType != null &&
                     resolvedSourceKind != null
            ? (
                Type: (XamlTypeInfo?)resolvedSourceType,
                Kind: resolvedSourceKind.Value)
            : ResolveBindingSource(
                extension,
                targetObjectType);
        var sourceType = source.Type;
        var syntax = path.Length == 0 || path == "."
            ? null
            : _bindingPathParser.Parse(path);
        if (sourceType == null || syntax == null)
        {
            return new XamlBoundBinding(
                extension,
                sourceType,
                source.Kind,
                path,
                syntax,
                ImmutableArray<XamlBindingPathAccessor>.Empty,
                extension.SourceSpan,
                extension.StableId);
        }

        if (syntax.HasErrors ||
            syntax.Steps.Any(static step =>
                step.Kind is not (
                    XamlBindingPathStepKind.Member or
                    XamlBindingPathStepKind.IntegerIndexer or
                    XamlBindingPathStepKind.StringIndexer)))
        {
            Error(
                "PGXAML2130",
                $"Typed ordinary-binding path '{path}' currently requires CLR members and " +
                "constant integer or string indexers.",
                extension.SourceSpan,
                "EXT-004");
            return new XamlBoundBinding(
                extension,
                sourceType,
                source.Kind,
                path,
                syntax,
                ImmutableArray<XamlBindingPathAccessor>.Empty,
                extension.SourceSpan,
                extension.StableId);
        }

        var accessors = ImmutableArray.CreateBuilder<XamlBindingPathAccessor>(
            syntax.Steps.Length);
        var currentType = sourceType.Symbol;
        for (var index = 0; index < syntax.Steps.Length; index++)
        {
            var step = syntax.Steps[index];
            if (step.Kind is
                XamlBindingPathStepKind.IntegerIndexer or
                XamlBindingPathStepKind.StringIndexer)
            {
                var indexer = ResolveCompiledBindingIndexer(
                    currentType,
                    step.Kind);
                if (indexer == null)
                {
                    Error(
                        "PGXAML2132",
                        $"Typed ordinary-binding indexer '{step.ValueToken.Text}' was not found " +
                        $"uniquely on '{currentType.ToDisplayString()}'.",
                        extension.SourceSpan,
                        "EXT-004");
                    break;
                }

                accessors.Add(new XamlBindingPathAccessor(
                    step.Kind == XamlBindingPathStepKind.IntegerIndexer
                        ? XamlBindingPathAccessorKind.IntegerIndexer
                        : XamlBindingPathAccessorKind.StringIndexer,
                    indexer,
                    currentType,
                    indexer.Type,
                    indexer.SetMethod != null &&
                    IsAccessibleFromGeneratedClass(indexer.SetMethod),
                    step.IntegerValue,
                    step.StringValue));
                currentType = indexer.Type;
                continue;
            }

            var memberName = step.ValueToken.Text;
            var member = ResolveCompiledBindingMember(currentType, memberName);
            if (member == null)
            {
                Error(
                    "PGXAML2131",
                    $"Typed ordinary-binding member '{memberName}' was not found uniquely on " +
                    $"'{currentType.ToDisplayString()}'.",
                    extension.SourceSpan,
                    "EXT-004");
                break;
            }

            ITypeSymbol valueType;
            bool canWrite;
            if (member is IPropertySymbol property)
            {
                valueType = property.Type;
                canWrite = property.SetMethod is { IsInitOnly: false } &&
                           IsAccessibleFromGeneratedClass(property.SetMethod);
            }
            else
            {
                var field = (IFieldSymbol)member;
                valueType = field.Type;
                canWrite = !field.IsReadOnly && !field.IsConst;
            }
            accessors.Add(new XamlBindingPathAccessor(
                XamlBindingPathAccessorKind.Member,
                member,
                currentType,
                valueType,
                canWrite));
            currentType = valueType;
        }

        return new XamlBoundBinding(
            extension,
            sourceType,
            source.Kind,
            path,
            syntax,
            accessors.ToImmutable(),
            extension.SourceSpan,
            extension.StableId);
    }

    private (XamlTypeInfo? Type, XamlBindingSourceKind Kind)
        ResolveBindingSource(
            XamlBoundObject extension,
            XamlTypeInfo? targetObjectType)
    {
        var sourceMember = FindBindingMember(extension, "Source");
        var relativeSourceMember = FindBindingMember(
            extension,
            "RelativeSource");
        var elementNameMember = FindBindingMember(extension, "ElementName");
        var hasElementName =
            elementNameMember?.Values.OfType<XamlBoundText>()
                .Any(value => !string.IsNullOrWhiteSpace(value.Text)) == true;
        var selectorCount =
            (sourceMember == null ? 0 : 1) +
            (relativeSourceMember == null ? 0 : 1) +
            (hasElementName ? 1 : 0);
        if (selectorCount > 1)
            return (null, XamlBindingSourceKind.Unknown);

        if (sourceMember != null)
        {
            var staticValue = FindValue<XamlBoundStaticMemberValue>(
                sourceMember.Values);
            var staticType = staticValue?.Member switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => null
            };
            if (staticType != null &&
                _typeSystem is IXamlSymbolTypeResolver symbolTypes &&
                symbolTypes.ResolveSymbolType(staticType) is { } staticSource)
            {
                return (
                    staticSource,
                    XamlBindingSourceKind.ExplicitStaticMember);
            }

            var sourceObject = sourceMember.Values
                .OfType<XamlBoundObject>()
                .FirstOrDefault(value => !value.IsMarkupExtension);
            if (sourceObject?.Type.Symbol != null)
            {
                return (
                    sourceObject.Type.Symbol,
                    XamlBindingSourceKind.ExplicitValue);
            }

            if (sourceMember.Values.OfType<XamlBoundText>().Any())
            {
                return (
                    _typeSystem.ResolveType(
                        XamlNamespaces.Language2006,
                        "String"),
                    XamlBindingSourceKind.ExplicitValue);
            }

            return (null, XamlBindingSourceKind.Unknown);
        }

        if (relativeSourceMember != null)
        {
            var relativeSource = relativeSourceMember.Values
                .OfType<XamlBoundObject>()
                .SingleOrDefault();
            var mode = relativeSource == null
                ? null
                : GetBindingText(relativeSource, "Mode") ??
                  GetBindingPositionalText(relativeSource);
            if (string.Equals(
                    mode?.Trim(),
                    "Self",
                    StringComparison.Ordinal) &&
                targetObjectType is { } selfType)
            {
                return (
                    selfType,
                    XamlBindingSourceKind.RelativeSelf);
            }

            return (null, XamlBindingSourceKind.Unknown);
        }

        if (hasElementName)
            return (null, XamlBindingSourceKind.Unknown);

        return _activeLexicalDataType == null
            ? (null, XamlBindingSourceKind.Unknown)
            : (
                _activeLexicalDataType,
                XamlBindingSourceKind.LexicalDataType);
    }

    private static XamlBoundMember? FindBindingMember(
        XamlBoundObject extension,
        string memberName) =>
        extension.Members.FirstOrDefault(member =>
            string.Equals(
                member.Member.Symbol?.Name ??
                member.Member.RequestedName.LocalName,
                memberName,
                StringComparison.Ordinal));

    private XamlBoundValue BindCompiledBinding(
        XamlBoundObject extension,
        XamlBoundMemberReference targetMember,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings)
    {
        if (_activeCompiledBindingSourceType == null)
        {
            Error(
                "PGXAML2110",
                "A compiled binding requires a resolvable x:Class root or x:DataType scope.",
                extension.SourceSpan,
                "EXT-004");
            return extension;
        }
        if (targetMember.Symbol?.Kind == XamlMemberKind.Event)
            return BindCompiledEventBinding(
                extension,
                targetMember,
                namespaceMappings);

        var path = GetBindingText(extension, "Path") ??
                   GetBindingPositionalText(extension) ??
                   string.Empty;
        path = path.Trim();
        var syntax = path.Length == 0 ? null : _bindingPathParser.Parse(path);
        if (syntax?.HasErrors == true)
        {
            Error(
                "PGXAML2112",
                $"Compiled-binding path '{path}' is not valid member-access syntax.",
                extension.SourceSpan,
                "EXT-004");
            return new XamlBoundCompiledBinding(
                extension,
                _activeCompiledBindingSourceType,
                path,
                ImmutableArray<XamlCompiledBindingPathSegment>.Empty,
                ParseCompiledBindingMode(extension),
                bindBackMethod: null,
                extension.SourceSpan,
                extension.StableId,
                sourceKind: _activeCompiledBindingSourceKind);
        }

        var segments = ImmutableArray.CreateBuilder<XamlCompiledBindingPathSegment>();
        ITypeSymbol currentType = _activeCompiledBindingSourceType.Symbol;
        XamlCompiledBindingFunction? function = null;
        if (syntax != null)
        {
            for (var stepIndex = 0; stepIndex < syntax.Steps.Length; stepIndex++)
            {
                var step = syntax.Steps[stepIndex];
                if (step.Kind == XamlBindingPathStepKind.FunctionCall)
                {
                    if (stepIndex != syntax.Steps.Length - 1)
                    {
                        Error(
                            "PGXAML2123",
                            "A compiled-binding function call must be the terminal expression.",
                            extension.SourceSpan,
                            "EXT-004");
                        break;
                    }
                    function = BindCompiledBindingFunction(
                        _activeCompiledBindingSourceType.Symbol,
                        currentType,
                        segments.ToImmutable(),
                        step,
                        namespaceMappings);
                    segments.Clear();
                    if (function == null)
                    {
                        Error(
                            "PGXAML2123",
                            $"Compiled-binding function '{step.MemberName}' is missing, ambiguous, " +
                            "or has incompatible arguments.",
                            extension.SourceSpan,
                            "EXT-004");
                    }
                    else
                    {
                        currentType = function.Method.ReturnType;
                    }
                    break;
                }
                var boundSteps = BindCompiledBindingSteps(
                    currentType,
                    step,
                    namespaceMappings);
                if (boundSteps.IsEmpty)
                {
                    if (step.Kind == XamlBindingPathStepKind.Member)
                    {
                        Error(
                            "PGXAML2113",
                            $"Compiled-binding member '{step.ValueToken.Text}' was not found " +
                            $"unambiguously on '{currentType.ToDisplayString()}'.",
                            extension.SourceSpan,
                            "EXT-004");
                    }
                    else if (step.Kind is
                             XamlBindingPathStepKind.IntegerIndexer or
                             XamlBindingPathStepKind.StringIndexer)
                    {
                        Error(
                            "PGXAML2121",
                            $"Compiled-binding indexer '{step.ValueToken.Text}' is not supported " +
                            $"unambiguously by '{currentType.ToDisplayString()}'. Integer indexers " +
                            "require IList<T>; string indexers require IDictionary<string, T>.",
                            extension.SourceSpan,
                            "EXT-004");
                    }
                    else
                    {
                        Error(
                            "PGXAML2122",
                            $"Compiled-binding cast or qualified member '{step.TypeName}' " +
                            $"could not be resolved for '{currentType.ToDisplayString()}'.",
                            extension.SourceSpan,
                            "EXT-004");
                    }
                    break;
                }

                foreach (var segment in boundSteps)
                {
                    segments.Add(segment);
                    currentType = segment.ValueType;
                }
            }
        }

        var mode = ParseCompiledBindingMode(extension);
        if (mode == XamlCompiledBindingMode.Default)
            mode = _activeCompiledBindingMode;
        var bindBackName = GetBindingText(extension, "BindBack")?.Trim();
        var bindBack = string.IsNullOrEmpty(bindBackName)
            ? null
            : ResolveBindBackMethod(
                _activeCompiledBindingSourceType.Symbol,
                bindBackName!,
                currentType);
        if (!string.IsNullOrEmpty(bindBackName) && bindBack == null)
        {
            Error(
                "PGXAML2114",
                $"BindBack method '{bindBackName}' must resolve unambiguously to a one-parameter method.",
                extension.SourceSpan,
                "EXT-004");
        }

        var result = new XamlBoundCompiledBinding(
            extension,
            _activeCompiledBindingSourceType,
            path,
            segments.ToImmutable(),
            mode,
            bindBack,
            extension.SourceSpan,
            extension.StableId,
            sourceKind: _activeCompiledBindingSourceKind,
            function: function);
        if (mode == XamlCompiledBindingMode.TwoWay && !result.CanWrite)
        {
            Error(
                "PGXAML2115",
                $"TwoWay compiled-binding path '{path}' is not writable and has no valid BindBack method.",
                extension.SourceSpan,
                "EXT-004");
        }
        return result;
    }

    private XamlBoundValue BindCompiledEventBinding(
        XamlBoundObject extension,
        XamlBoundMemberReference targetMember,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings)
    {
        var path = GetBindingText(extension, "Path") ??
                   GetBindingPositionalText(extension) ??
                   string.Empty;
        path = path.Trim();
        var syntax = _bindingPathParser.Parse(path);
        if (syntax.HasErrors ||
            syntax.Steps.IsEmpty ||
            syntax.Steps[syntax.Steps.Length - 1].Kind != XamlBindingPathStepKind.Member)
        {
            Error(
                "PGXAML2111",
                $"Compiled event-binding path '{path}' must end in a method name.",
                extension.SourceSpan,
                "EXT-004");
            return extension;
        }

        var segments = ImmutableArray.CreateBuilder<XamlCompiledBindingPathSegment>();
        ITypeSymbol currentType = _activeCompiledBindingSourceType!.Symbol;
        for (var index = 0; index < syntax.Steps.Length - 1; index++)
        {
            var step = syntax.Steps[index];
            var boundSteps = BindCompiledBindingSteps(
                currentType,
                step,
                namespaceMappings);
            if (boundSteps.IsEmpty)
            {
                Error(
                    step.Kind switch
                    {
                        XamlBindingPathStepKind.Member => "PGXAML2113",
                        XamlBindingPathStepKind.IntegerIndexer or
                            XamlBindingPathStepKind.StringIndexer => "PGXAML2121",
                        _ => "PGXAML2122"
                    },
                    $"Compiled event-binding step '{step.ValueToken.Text}' was not found unambiguously on " +
                    $"'{currentType.ToDisplayString()}'.",
                    extension.SourceSpan,
                    "EXT-004");
                return extension;
            }
            foreach (var segment in boundSteps)
            {
                segments.Add(segment);
                currentType = segment.ValueType;
            }
        }

        var methodName = syntax.Steps[syntax.Steps.Length - 1].ValueToken.Text;
        var eventSymbol = targetMember.Symbol?.Symbol as IEventSymbol;
        var method = eventSymbol == null
            ? null
            : ResolveEventHandlerMethod(currentType, methodName, eventSymbol);
        if (method == null)
        {
            Error(
                "PGXAML2118",
                $"Compiled event-binding method '{methodName}' is missing, overloaded, or incompatible " +
                $"with event '{targetMember.Symbol?.Name}'.",
                extension.SourceSpan,
                "EXT-004");
        }
        if (GetBindingText(extension, "Mode") != null ||
            GetBindingText(extension, "BindBack") != null)
        {
            Error(
                "PGXAML2119",
                "Compiled event binding does not accept Mode or BindBack.",
                extension.SourceSpan,
                "EXT-004");
        }
        return new XamlBoundCompiledBinding(
            extension,
            _activeCompiledBindingSourceType,
            path,
            segments.ToImmutable(),
            XamlCompiledBindingMode.OneTime,
            bindBackMethod: null,
            extension.SourceSpan,
            extension.StableId,
            XamlCompiledBindingKind.Event,
            method,
            _activeCompiledBindingSourceKind);
    }

    private IMethodSymbol? ResolveEventHandlerMethod(
        ITypeSymbol sourceType,
        string methodName,
        IEventSymbol targetEvent)
    {
        if (sourceType is not INamedTypeSymbol named ||
            targetEvent.Type is not INamedTypeSymbol delegateType ||
            delegateType.DelegateInvokeMethod is not { } invoke)
            return null;
        var candidates = new List<IMethodSymbol>();
        for (var current = named; current != null; current = current.BaseType)
        {
            candidates.AddRange(current.GetMembers(methodName)
                .OfType<IMethodSymbol>()
                .Where(static method =>
                    !method.IsStatic &&
                    !method.IsGenericMethod &&
                    method.MethodKind == MethodKind.Ordinary));
            if (candidates.Count != 0) break;
        }
        if (candidates.Count != 1) return null;
        var candidate = candidates[0];
        if (!candidate.ReturnsVoid ||
            !IsAccessibleFromGeneratedClass(candidate))
            return null;
        if (candidate.Parameters.Length == 0) return candidate;
        if (candidate.Parameters.Length != invoke.Parameters.Length) return null;
        for (var index = 0; index < candidate.Parameters.Length; index++)
        {
            if (candidate.Parameters[index].RefKind != RefKind.None ||
                invoke.Parameters[index].RefKind != RefKind.None ||
                !_typeSystem.IsAssignable(
                    invoke.Parameters[index].Type,
                    candidate.Parameters[index].Type))
                return null;
        }
        return candidate;
    }

    private static string? GetBindingText(XamlBoundObject extension, string memberName) =>
        extension.Members.FirstOrDefault(member =>
                string.Equals(member.Member.Symbol?.Name, memberName, StringComparison.Ordinal) ||
                string.Equals(member.Member.RequestedName.LocalName, memberName, StringComparison.Ordinal))?
            .Values.OfType<XamlBoundText>().FirstOrDefault()?.Text;

    private static string? GetBindingPositionalText(XamlBoundObject extension) =>
        extension.Members.FirstOrDefault(member =>
                member.Member.Kind == XamlBoundReferenceKind.Directive &&
                string.Equals(
                    member.Member.RequestedName.LocalName,
                    "PositionalParameters",
                    StringComparison.Ordinal))?
            .Values.OfType<XamlBoundText>().FirstOrDefault()?.Text;

    private XamlCompiledBindingMode ParseCompiledBindingMode(XamlBoundObject extension)
    {
        var text = GetBindingText(extension, "Mode")?.Trim();
        if (string.IsNullOrEmpty(text)) return XamlCompiledBindingMode.Default;
        if (Enum.TryParse<XamlCompiledBindingMode>(text, ignoreCase: false, out var mode) &&
            mode != XamlCompiledBindingMode.Default)
            return mode;
        Error(
            "PGXAML2116",
            $"Compiled-binding mode '{text}' must be OneTime, OneWay, or TwoWay.",
            extension.SourceSpan,
            "EXT-004");
        return XamlCompiledBindingMode.Default;
    }

    private ISymbol? ResolveCompiledBindingMember(ITypeSymbol sourceType, string memberName)
    {
        if (sourceType is not INamedTypeSymbol named) return null;
        ISymbol? result = null;
        for (var current = named; current != null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(memberName))
            {
                if (candidate is IPropertySymbol
                    {
                        IsStatic: false,
                        IsIndexer: false,
                        GetMethod: not null
                    } property)
                {
                    if (!IsAccessibleFromGeneratedClass(property.GetMethod)) continue;
                }
                else if (candidate is IFieldSymbol { IsStatic: false } field)
                {
                    if (!IsAccessibleFromGeneratedClass(field)) continue;
                }
                else continue;
                if (result != null) return null;
                result = candidate;
            }
            if (result != null) return result;
        }
        return result;
    }

    private ImmutableArray<XamlCompiledBindingPathSegment> BindCompiledBindingSteps(
        ITypeSymbol sourceType,
        XamlBindingPathStepSyntax step,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings)
    {
        if (step.Kind == XamlBindingPathStepKind.Member)
        {
            var member = ResolveCompiledBindingMember(sourceType, step.ValueToken.Text);
            if (member == null)
                return ImmutableArray<XamlCompiledBindingPathSegment>.Empty;
            if (member is IPropertySymbol property)
            {
                return ImmutableArray.Create(new XamlCompiledBindingPathSegment(
                    property,
                    sourceType,
                    property.Type,
                    property.SetMethod is { IsInitOnly: false } &&
                    IsAccessibleFromGeneratedClass(property.SetMethod)));
            }
            var field = (IFieldSymbol)member;
            return ImmutableArray.Create(new XamlCompiledBindingPathSegment(
                field,
                sourceType,
                field.Type,
                !field.IsReadOnly &&
                !field.IsConst &&
                IsAccessibleFromGeneratedClass(field)));
        }

        if (step.Kind == XamlBindingPathStepKind.Cast)
        {
            var targetType = ResolveCompiledBindingType(
                step.TypeName,
                namespaceMappings);
            if (targetType == null ||
                _typeSystem is not IXamlSymbolConversionService conversions ||
                !conversions.HasExplicitConversion(sourceType, targetType.Symbol))
                return ImmutableArray<XamlCompiledBindingPathSegment>.Empty;
            return ImmutableArray.Create(new XamlCompiledBindingPathSegment(
                XamlCompiledBindingPathSegmentKind.Cast,
                targetType.Symbol,
                sourceType,
                targetType.Symbol,
                canWrite: false));
        }

        if (step.Kind == XamlBindingPathStepKind.QualifiedMember)
        {
            var ownerType = ResolveCompiledBindingType(
                step.TypeName,
                namespaceMappings);
            if (ownerType == null ||
                _typeSystem is not IXamlSymbolTypeResolver symbolTypes ||
                symbolTypes.ResolveSymbolType(sourceType) is not { } objectType)
                return ImmutableArray<XamlCompiledBindingPathSegment>.Empty;
            var attached = _typeSystem.ResolveMember(
                objectType,
                ownerType.NamespaceUri,
                ownerType.Name,
                step.MemberName!);
            if (attached?.Kind == XamlMemberKind.AttachableProperty &&
                attached.AttachableShape?.Getter is { } getter)
            {
                var setter = attached.AttachableShape.Setter;
                return ImmutableArray.Create(new XamlCompiledBindingPathSegment(
                    XamlCompiledBindingPathSegmentKind.AttachedMember,
                    getter,
                    sourceType,
                    getter.ReturnType,
                    setter != null &&
                    IsAccessibleFromGeneratedClass(setter),
                    setterMethod: setter,
                    attachedMemberName: step.MemberName));
            }

            if (_typeSystem is not IXamlSymbolConversionService legacyConversions ||
                !legacyConversions.HasExplicitConversion(sourceType, ownerType.Symbol))
                return ImmutableArray<XamlCompiledBindingPathSegment>.Empty;
            var member = ResolveCompiledBindingMember(
                ownerType.Symbol,
                step.MemberName!);
            if (member == null)
                return ImmutableArray<XamlCompiledBindingPathSegment>.Empty;
            var cast = new XamlCompiledBindingPathSegment(
                XamlCompiledBindingPathSegmentKind.Cast,
                ownerType.Symbol,
                sourceType,
                ownerType.Symbol,
                canWrite: false);
            XamlCompiledBindingPathSegment memberSegment;
            if (member is IPropertySymbol property)
            {
                memberSegment = new XamlCompiledBindingPathSegment(
                    property,
                    ownerType.Symbol,
                    property.Type,
                    property.SetMethod is { IsInitOnly: false } &&
                    IsAccessibleFromGeneratedClass(property.SetMethod));
            }
            else
            {
                var field = (IFieldSymbol)member;
                memberSegment = new XamlCompiledBindingPathSegment(
                    field,
                    ownerType.Symbol,
                    field.Type,
                    !field.IsReadOnly &&
                    !field.IsConst &&
                    IsAccessibleFromGeneratedClass(field));
            }
            return ImmutableArray.Create(cast, memberSegment);
        }

        var indexer = ResolveCompiledBindingIndexer(sourceType, step.Kind);
        if (indexer == null)
            return ImmutableArray<XamlCompiledBindingPathSegment>.Empty;
        return ImmutableArray.Create(new XamlCompiledBindingPathSegment(
            step.Kind == XamlBindingPathStepKind.IntegerIndexer
                ? XamlCompiledBindingPathSegmentKind.IntegerIndexer
                : XamlCompiledBindingPathSegmentKind.StringIndexer,
            indexer,
            sourceType,
            indexer.Type,
            indexer.SetMethod != null &&
            IsAccessibleFromGeneratedClass(indexer.SetMethod),
            step.IntegerValue,
            step.StringValue));
    }

    private XamlCompiledBindingFunction? BindCompiledBindingFunction(
        ITypeSymbol rootType,
        ITypeSymbol ownerType,
        ImmutableArray<XamlCompiledBindingPathSegment> ownerPath,
        XamlBindingPathStepSyntax step,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings)
    {
        if (step.Kind != XamlBindingPathStepKind.FunctionCall ||
            string.IsNullOrWhiteSpace(step.MemberName))
            return null;

        if (step.IsStaticFunction)
        {
            var staticOwner = ResolveCompiledBindingType(
                step.TypeName,
                namespaceMappings);
            if (staticOwner == null) return null;
            ownerType = staticOwner.Symbol;
            ownerPath = ImmutableArray<XamlCompiledBindingPathSegment>.Empty;
        }

        var rawArguments =
            ImmutableArray.CreateBuilder<BoundCompiledFunctionArgument>(step.Arguments.Length);
        foreach (var argument in step.Arguments)
        {
            if (!TryBindCompiledFunctionArgument(
                    rootType,
                    argument,
                    namespaceMappings,
                    out var boundArgument))
                return null;
            rawArguments.Add(boundArgument);
        }

        var candidates = GetCompiledBindingFunctionCandidates(
            ownerType,
            step.MemberName!,
            step.IsStaticFunction,
            rawArguments.Count);
        IMethodSymbol? selected = null;
        var selectedScore = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var score = 0;
            var compatible = true;
            for (var index = 0; index < candidate.Parameters.Length; index++)
            {
                var valueType = rawArguments[index].ValueType;
                var parameterType = candidate.Parameters[index].Type;
                if (SymbolEqualityComparer.IncludeNullability.Equals(
                        valueType,
                        parameterType))
                    continue;
                if (!_typeSystem.IsAssignable(valueType, parameterType))
                {
                    compatible = false;
                    break;
                }
                score++;
            }
            if (!compatible || score > selectedScore) continue;
            if (score == selectedScore)
            {
                selected = null;
                continue;
            }
            selected = candidate;
            selectedScore = score;
        }
        if (selected == null) return null;

        var arguments =
            ImmutableArray.CreateBuilder<XamlCompiledBindingFunctionArgument>(
                rawArguments.Count);
        for (var index = 0; index < rawArguments.Count; index++)
        {
            var raw = rawArguments[index];
            arguments.Add(new XamlCompiledBindingFunctionArgument(
                raw.Kind,
                selected.Parameters[index],
                raw.ValueType,
                raw.PathSegments,
                raw.LiteralValue));
        }
        return new XamlCompiledBindingFunction(
            selected,
            ownerPath,
            arguments.ToImmutable());
    }

    private bool TryBindCompiledFunctionArgument(
        ITypeSymbol rootType,
        XamlBindingFunctionArgumentSyntax argument,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings,
        out BoundCompiledFunctionArgument result)
    {
        result = default;
        if (_typeSystem is not IXamlMetadataTypeResolver metadata)
            return false;
        if (argument.Kind == XamlBindingFunctionArgumentKind.StringLiteral)
        {
            var type = metadata.ResolveMetadataType("System.String")?.Symbol;
            if (type == null) return false;
            result = new BoundCompiledFunctionArgument(
                XamlCompiledBindingFunctionArgumentKind.String,
                type,
                ImmutableArray<XamlCompiledBindingPathSegment>.Empty,
                argument.StringValue ?? string.Empty);
            return true;
        }
        if (argument.Kind == XamlBindingFunctionArgumentKind.BooleanLiteral)
        {
            if (!string.Equals(
                    ResolveNamespace(
                        argument.NamespacePrefix ?? string.Empty,
                        namespaceMappings),
                    XamlNamespaces.Language2006,
                    StringComparison.Ordinal))
                return false;
            var type = metadata.ResolveMetadataType("System.Boolean")?.Symbol;
            if (type == null) return false;
            result = new BoundCompiledFunctionArgument(
                XamlCompiledBindingFunctionArgumentKind.Boolean,
                type,
                ImmutableArray<XamlCompiledBindingPathSegment>.Empty,
                argument.BooleanValue);
            return true;
        }
        if (argument.Kind == XamlBindingFunctionArgumentKind.NumericLiteral)
        {
            object value;
            string metadataName;
            if (int.TryParse(
                    argument.Text,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var integer))
            {
                value = integer;
                metadataName = "System.Int32";
            }
            else if (double.TryParse(
                         argument.Text,
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out var floatingPoint) &&
                     !double.IsInfinity(floatingPoint) &&
                     !double.IsNaN(floatingPoint))
            {
                value = floatingPoint;
                metadataName = "System.Double";
            }
            else
            {
                return false;
            }
            var type = metadata.ResolveMetadataType(metadataName)?.Symbol;
            if (type == null) return false;
            result = new BoundCompiledFunctionArgument(
                XamlCompiledBindingFunctionArgumentKind.Number,
                type,
                ImmutableArray<XamlCompiledBindingPathSegment>.Empty,
                value);
            return true;
        }

        var syntax = _bindingPathParser.Parse(argument.Text);
        if (syntax.HasErrors ||
            syntax.Steps.IsEmpty ||
            syntax.Steps.Any(static candidate =>
                candidate.Kind == XamlBindingPathStepKind.FunctionCall))
            return false;
        var path = ImmutableArray.CreateBuilder<XamlCompiledBindingPathSegment>();
        var currentType = rootType;
        foreach (var step in syntax.Steps)
        {
            var boundSteps = BindCompiledBindingSteps(
                currentType,
                step,
                namespaceMappings);
            if (boundSteps.IsEmpty) return false;
            foreach (var segment in boundSteps)
            {
                path.Add(segment);
                currentType = segment.ValueType;
            }
        }
        result = new BoundCompiledFunctionArgument(
            XamlCompiledBindingFunctionArgumentKind.Path,
            currentType,
            path.ToImmutable(),
            literalValue: null);
        return true;
    }

    private IEnumerable<IMethodSymbol> GetCompiledBindingFunctionCandidates(
        ITypeSymbol ownerType,
        string methodName,
        bool isStatic,
        int argumentCount)
    {
        if (ownerType is not INamedTypeSymbol named)
            yield break;
        for (var current = named; current != null; current = current.BaseType)
        {
            var foundOnCurrentType = false;
            foreach (var method in current.GetMembers(methodName).OfType<IMethodSymbol>())
            {
                if (method.IsStatic != isStatic ||
                    method.IsGenericMethod ||
                    method.MethodKind != MethodKind.Ordinary ||
                    method.ReturnsVoid ||
                    method.Parameters.Length != argumentCount ||
                    method.Parameters.Any(static parameter =>
                        parameter.RefKind != RefKind.None) ||
                    !IsAccessibleFromGeneratedClass(method))
                    continue;
                foundOnCurrentType = true;
                yield return method;
            }
            if (foundOnCurrentType)
                yield break;
        }
    }

    private readonly struct BoundCompiledFunctionArgument
    {
        public BoundCompiledFunctionArgument(
            XamlCompiledBindingFunctionArgumentKind kind,
            ITypeSymbol valueType,
            ImmutableArray<XamlCompiledBindingPathSegment> pathSegments,
            object? literalValue)
        {
            Kind = kind;
            ValueType = valueType;
            PathSegments = pathSegments;
            LiteralValue = literalValue;
        }

        public XamlCompiledBindingFunctionArgumentKind Kind { get; }
        public ITypeSymbol ValueType { get; }
        public ImmutableArray<XamlCompiledBindingPathSegment> PathSegments { get; }
        public object? LiteralValue { get; }
    }

    private XamlTypeInfo? ResolveCompiledBindingType(
        string? typeName,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        var parsed = _typeNameParser.Parse(typeName!);
        return !parsed.HasErrors && parsed.Types.Length == 1
            ? ResolveTypeName(parsed.Types[0], namespaceMappings)
            : null;
    }

    private IPropertySymbol? ResolveCompiledBindingIndexer(
        ITypeSymbol sourceType,
        XamlBindingPathStepKind stepKind)
    {
        if (sourceType is not INamedTypeSymbol named ||
            _typeSystem is not IXamlMetadataTypeResolver resolver)
            return null;
        var metadataName = stepKind == XamlBindingPathStepKind.IntegerIndexer
            ? "System.Collections.Generic.IList`1"
            : "System.Collections.Generic.IDictionary`2";
        var definition = resolver.ResolveMetadataType(metadataName)?.Symbol as INamedTypeSymbol;
        if (definition == null) return null;

        INamedTypeSymbol? contract = null;
        if (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, definition))
            contract = named;
        foreach (var candidate in named.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, definition))
                continue;
            if (contract != null &&
                !SymbolEqualityComparer.Default.Equals(contract, candidate))
                return null;
            contract = candidate;
        }
        if (contract == null ||
            (stepKind == XamlBindingPathStepKind.StringIndexer &&
             (contract.TypeArguments.Length != 2 ||
              contract.TypeArguments[0].SpecialType != SpecialType.System_String)))
            return null;

        IPropertySymbol? result = null;
        foreach (var property in contract.GetMembers().OfType<IPropertySymbol>())
        {
            if (!property.IsIndexer ||
                property.GetMethod == null ||
                property.Parameters.Length != 1)
                continue;
            var parameterType = property.Parameters[0].Type;
            if (stepKind == XamlBindingPathStepKind.IntegerIndexer
                    ? parameterType.SpecialType != SpecialType.System_Int32
                    : parameterType.SpecialType != SpecialType.System_String)
                continue;
            if (!IsAccessibleFromGeneratedClass(property.GetMethod))
                continue;
            if (result != null) return null;
            result = property;
        }
        return result;
    }

    private IMethodSymbol? ResolveBindBackMethod(
        ITypeSymbol sourceType,
        string methodName,
        ITypeSymbol valueType)
    {
        if (sourceType is not INamedTypeSymbol named || methodName.IndexOf('.') >= 0)
            return null;
        IMethodSymbol? result = null;
        for (var current = named; current != null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(methodName).OfType<IMethodSymbol>())
            {
                if (candidate.IsStatic || candidate.IsGenericMethod ||
                    candidate.Parameters.Length != 1 ||
                    candidate.MethodKind != MethodKind.Ordinary ||
                    !IsAccessibleFromGeneratedClass(candidate))
                    continue;
                if (!SymbolEqualityComparer.Default.Equals(candidate.Parameters[0].Type, valueType) &&
                    !_typeSystem.IsAssignable(valueType, candidate.Parameters[0].Type))
                    continue;
                if (result != null) return null;
                result = candidate;
            }
            if (result != null) return result;
        }
        return null;
    }

    private bool IsAccessibleFromGeneratedClass(ISymbol symbol)
    {
        if (_rootClassType != null)
        {
            return (_typeSystem as IXamlSymbolAccessibilityService)?
                .IsAccessibleWithin(symbol, _rootClassType.Symbol) != false;
        }

        for (ISymbol? current = symbol;
             current != null;
             current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (
                Accessibility.Public or
                Accessibility.NotApplicable))
                return false;
        }
        return true;
    }

    private static ImmutableArray<XamlBoundValue>.Builder ExpandAttributedListText(
        XamlBoundMemberReference member,
        ImmutableArray<XamlBoundValue>.Builder values)
    {
        var split = member.Symbol?.ValueType.ListSplit;
        if (split?.IsValid != true ||
            member.Symbol?.Kind != XamlMemberKind.Collection ||
            values.Count != 1 ||
            values[0] is not XamlBoundText text)
            return values;

        var items = split.Split(text.Text);
        var result = ImmutableArray.CreateBuilder<XamlBoundValue>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            result.Add(new XamlBoundText(
                item.Text,
                text.IsCData,
                new TextSpan(
                    text.SourceSpan.Start + item.SourceSpan.Start,
                    item.SourceSpan.Length),
                DeriveStableId(
                    text.StableId,
                    0x4C4953544954454DUL ^ (ulong)(index + 1)),
                XamlTextSyntaxInfo.None,
                item.RawText));
        }
        return result;
    }

    private ImmutableArray<XamlBoundValue>.Builder ApplyContentWrappers(
        XamlBoundTypeReference parentType,
        XamlBoundMemberReference member,
        ImmutableArray<XamlBoundValue>.Builder values)
    {
        var collectionType = member.Symbol?.ValueType;
        if (member.Kind == XamlBoundReferenceKind.CollectionItems ||
            member.Kind == XamlBoundReferenceKind.DictionaryItems)
            collectionType = parentType.Symbol;
        if (collectionType == null ||
            collectionType.ContentWrappers.Count == 0 ||
            collectionType.CollectionShape == null)
            return values;

        var itemType = collectionType.CollectionShape.ItemType;
        var stringType = _typeSystem.ResolveType(XamlNamespaces.Language2006, "String");
        var result = ImmutableArray.CreateBuilder<XamlBoundValue>(values.Count);
        foreach (var value in values)
        {
            ITypeSymbol? foreignType = value switch
            {
                XamlBoundObject objectValue when !objectValue.IsMarkupExtension =>
                    objectValue.Type.Symbol?.Symbol,
                XamlBoundText => stringType?.Symbol,
                _ => null
            };
            if (foreignType == null || _typeSystem.IsAssignable(foreignType, itemType))
            {
                result.Add(value);
                continue;
            }

            var applicable = collectionType.ContentWrappers
                .Where(shape => shape.IsValid &&
                    shape.ContentValueType != null &&
                    (value is XamlBoundText
                        ? _typeSystem.IsAssignable(foreignType, shape.ContentValueType) ||
                          shape.ContentMember!.TextSyntax.Kind != XamlTextSyntaxKind.None &&
                          shape.ContentMember.TextSyntax.Kind != XamlTextSyntaxKind.Error
                        : _typeSystem.IsAssignable(foreignType, shape.ContentValueType)))
                .ToArray();
            var selected = SelectContentWrapper(applicable, foreignType);
            if (selected == null)
            {
                if (applicable.Length == 0)
                {
                    Error(
                        "PGXAML2053",
                        $"Content of type '{foreignType.ToDisplayString()}' is not assignable to collection " +
                        $"item type '{itemType.ToDisplayString()}', and no declared content wrapper accepts it.",
                        value.SourceSpan,
                        "5.2.2.3");
                }
                else
                {
                    Error(
                        "PGXAML2054",
                        $"Content of type '{foreignType.ToDisplayString()}' has multiple equally applicable " +
                        $"content wrappers on '{collectionType.MetadataName}': " +
                        string.Join(", ", applicable.Select(shape => shape.WrapperType!.MetadataName)) + ".",
                        value.SourceSpan,
                        "5.2.2.3");
                }
                result.Add(value);
                continue;
            }

            result.Add(CreateContentWrapper(selected, value));
        }
        return result;
    }

    private XamlContentWrapperShapeInfo? SelectContentWrapper(
        IReadOnlyList<XamlContentWrapperShapeInfo> candidates,
        ITypeSymbol foreignType)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var exact = candidates.Where(candidate =>
            SymbolEqualityComparer.IncludeNullability.Equals(
                candidate.ContentValueType,
                foreignType)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return null;

        XamlContentWrapperShapeInfo? winner = null;
        foreach (var candidate in candidates)
        {
            var dominated = false;
            foreach (var other in candidates)
            {
                if (ReferenceEquals(candidate, other)) continue;
                if (_typeSystem.IsAssignable(other.ContentValueType!, candidate.ContentValueType!) &&
                    !_typeSystem.IsAssignable(candidate.ContentValueType!, other.ContentValueType!))
                {
                    dominated = true;
                    break;
                }
            }
            if (dominated) continue;
            if (winner != null) return null;
            winner = candidate;
        }
        return winner;
    }

    private static XamlBoundObject CreateContentWrapper(
        XamlContentWrapperShapeInfo shape,
        XamlBoundValue value)
    {
        var wrapperType = shape.WrapperType!;
        var contentMember = shape.ContentMember!;
        XamlBoundValue wrappedValue = value;
        if (value is XamlBoundText text)
        {
            wrappedValue = new XamlBoundText(
                text.Text,
                text.IsCData,
                text.SourceSpan,
                text.StableId,
                contentMember.TextSyntax,
                text.OriginalText);
        }
        var requestedType = new XamlQualifiedName(
            wrapperType.NamespaceUri,
            wrapperType.Name);
        var requestedMember = new XamlInfosetMemberName(
            wrapperType.NamespaceUri,
            contentMember.Name,
            isImplicit: true);
        var member = new XamlBoundMember(
            XamlBoundMemberReference.Resolved(requestedMember, contentMember),
            XamlMemberOrigin.ImplicitContent,
            ImmutableArray.Create(wrappedValue),
            value.SourceSpan,
            DeriveStableId(value.StableId, 0x434F4E54454E5455UL));
        return new XamlBoundObject(
            XamlBoundTypeReference.Resolved(requestedType, wrapperType),
            ImmutableArray.Create(member),
            isRetrieved: false,
            isMarkupExtension: false,
            value.SourceSpan,
            DeriveStableId(value.StableId, 0x5752415050455255UL));
    }

    private static ulong DeriveStableId(ulong value, ulong salt)
    {
        unchecked
        {
            var hash = (value ^ salt) * 1099511628211UL;
            return hash ^ (hash >> 32);
        }
    }

    private static ImmutableArray<XamlBoundValue>.Builder NormalizeElementContentWhitespace(
        XamlInfosetMember sourceMember,
        XamlInfosetObject parentObject,
        XamlBoundTypeReference parentType,
        XamlBoundMemberReference member,
        ImmutableArray<XamlBoundValue>.Builder values)
    {
        if (sourceMember.Origin != XamlMemberOrigin.ImplicitContent &&
            sourceMember.Origin != XamlMemberOrigin.MemberElement)
            return values;

        var collectionType = member.Symbol?.ValueType;
        if (member.Kind == XamlBoundReferenceKind.CollectionItems ||
            member.Kind == XamlBoundReferenceKind.DictionaryItems)
            collectionType = parentType.Symbol;
        var isCollection = collectionType?.IsCollection == true ||
            member.Symbol?.Kind == XamlMemberKind.Collection ||
            member.Symbol?.Kind == XamlMemberKind.Dictionary;
        var isWhitespaceSignificant = collectionType?.IsWhitespaceSignificantCollection == true;
        var preserve = IsXmlSpacePreserved(parentObject);
        var containsObjects = values.Any(static value => value is XamlBoundObject);
        var result = ImmutableArray.CreateBuilder<XamlBoundValue>(values.Count);

        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not XamlBoundText text)
            {
                result.Add(values[index]);
                continue;
            }

            var normalized = preserve ? text.Text : CollapseXamlWhitespace(text.Text);
            if (!preserve)
            {
                if (index == 0) normalized = TrimXamlWhitespaceStart(normalized);
                if (index == values.Count - 1) normalized = TrimXamlWhitespaceEnd(normalized);
            }

            if (index > 0 &&
                values[index - 1] is XamlBoundObject previousObject &&
                previousObject.Type.Symbol?.ShouldTrimSurroundingWhitespace == true)
                normalized = TrimXamlWhitespaceStart(normalized);
            if (index + 1 < values.Count &&
                values[index + 1] is XamlBoundObject nextObject &&
                nextObject.Type.Symbol?.ShouldTrimSurroundingWhitespace == true)
                normalized = TrimXamlWhitespaceEnd(normalized);

            if (normalized.Length == 0 ||
                (IsOnlyXamlWhitespace(normalized) &&
                 ((isCollection && !isWhitespaceSignificant) ||
                  (!isCollection && containsObjects))))
                continue;

            result.Add(new XamlBoundText(
                normalized,
                text.IsCData,
                text.SourceSpan,
                text.StableId,
                text.TextSyntax,
                text.OriginalText));
        }

        return result;
    }

    private static bool IsWhitespaceOnlyImplicitMember(XamlInfosetMember member)
    {
        if (member.Origin != XamlMemberOrigin.ImplicitContent || member.Values.IsEmpty) return false;
        foreach (var value in member.Values)
            if (value is not XamlInfosetText text || !string.IsNullOrWhiteSpace(text.Text))
                return false;
        return true;
    }

    private static bool IsXmlSpacePreserved(XamlInfosetObject value)
    {
        for (var current = value; current != null; current = current.ParentMember?.ParentObject)
        {
            var member = current.FindMember(XamlNamespaces.Xml, "space");
            var text = member?.Values.OfType<XamlInfosetText>().FirstOrDefault()?.Text;
            if (text == null) continue;
            return string.Equals(text, "preserve", StringComparison.Ordinal);
        }
        return false;
    }

    private static string CollapseXamlWhitespace(string value)
    {
        if (value.Length == 0) return value;
        var requiresNormalization = false;
        var previousWasWhitespace = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (!IsXamlWhitespace(current))
            {
                previousWasWhitespace = false;
                continue;
            }
            if (current != '\u0020' || previousWasWhitespace)
                requiresNormalization = true;
            previousWasWhitespace = true;
        }
        if (!requiresNormalization) return value;

        var result = new System.Text.StringBuilder(value.Length);
        previousWasWhitespace = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (IsXamlWhitespace(current))
            {
                if (!previousWasWhitespace) result.Append(' ');
                previousWasWhitespace = true;
            }
            else
            {
                result.Append(current);
                previousWasWhitespace = false;
            }
        }
        return result.ToString();
    }

    private static string TrimXamlWhitespaceStart(string value)
    {
        var index = 0;
        while (index < value.Length && IsXamlWhitespace(value[index])) index++;
        return index == 0 ? value : value.Substring(index);
    }

    private static string TrimXamlWhitespaceEnd(string value)
    {
        var index = value.Length;
        while (index > 0 && IsXamlWhitespace(value[index - 1])) index--;
        return index == value.Length ? value : value.Substring(0, index);
    }

    private static bool IsOnlyXamlWhitespace(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (!IsXamlWhitespace(value[index])) return false;
        return true;
    }

    private static bool IsXamlWhitespace(char value) =>
        value == '\u0020' || value == '\u0009' || value == '\u000A' || value == '\u000D';

    private static bool HasExplicitDictionaryKey(XamlBoundObject value) =>
        value.Members.Any(static member =>
            member.Member.Kind == XamlBoundReferenceKind.Directive &&
            string.Equals(member.Member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
            string.Equals(member.Member.RequestedName.LocalName, "Key", StringComparison.Ordinal));

    private XamlBoundText BindTextValue(
        XamlInfosetText text,
        XamlBoundMemberReference member,
        XamlBoundTypeReference parentType)
    {
        var isInitialization = string.Equals(member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
            string.Equals(member.RequestedName.LocalName, "Initialization", StringComparison.Ordinal);
        var syntax = member.Symbol?.TextSyntax ??
            (member.Kind == XamlBoundReferenceKind.Intrinsic || isInitialization
                ? parentType.Symbol?.TextSyntax
                : null) ?? XamlTextSyntaxInfo.None;
        if (syntax.Kind == XamlTextSyntaxKind.Error)
            Error("PGXAML2041",
                syntax.Error ?? $"The text syntax for member '{member.RequestedName.DisplayName}' is invalid.",
                text.SourceSpan,
                member.Kind == XamlBoundReferenceKind.Intrinsic || isInitialization ? "6.2.2.5" : "6.3.2.4");
        var targetType = member.Symbol?.ValueType ?? (isInitialization ? parentType.Symbol : null);
        if (targetType != null)
        {
            var isValid =
                (syntax.Kind != XamlTextSyntaxKind.Intrinsic ||
                 XamlIntrinsicTextSyntax.IsValid(targetType, text.Text)) &&
                (syntax.Kind != XamlTextSyntaxKind.Enumeration ||
                 XamlIntrinsicTextSyntax.IsValidEnumeration(targetType, text.Text));
            if (!isValid &&
                _typeSystem is IXamlTextValuePolicy policy &&
                policy.TryValidateTextValue(targetType, text.Text, out var policyValid))
                isValid = policyValid;
            if (!isValid)
                Error("PGXAML2042",
                    $"Text '{text.Text}' does not satisfy the text syntax for '{targetType.MetadataName}'.",
                    text.SourceSpan,
                    isInitialization ? "6.2.2.5" : "6.3.2.4");
        }
        return new XamlBoundText(text.Text, text.IsCData, text.SourceSpan, text.StableId, syntax);
    }

    private XamlBoundTypeValue BindTypeValue(XamlInfosetText text, XamlInfosetObject parentObject)
    {
        var raw = text.Text.Trim();
        var separator = raw.IndexOf(':');
        var prefix = separator < 0 ? string.Empty : raw.Substring(0, separator);
        var localName = separator < 0 ? raw : raw.Substring(separator + 1);
        var namespaceUri = string.Empty;
        foreach (var mapping in parentObject.NamespaceMappings)
        {
            if (string.Equals(mapping.Prefix, prefix, StringComparison.Ordinal))
            {
                namespaceUri = NormalizeConditionalNamespace(mapping.NamespaceUri);
                break;
            }
        }
        var requested = new XamlQualifiedName(namespaceUri, localName, prefix);
        var symbol = localName.Length == 0 ? null : _typeSystem.ResolveType(namespaceUri, localName);
        XamlBoundTypeReference reference;
        if (symbol == null)
        {
            var diagnostic = Error(
                "PGXAML2017",
                $"The x:Type argument '{text.Text}' could not be resolved.",
                text.SourceSpan,
                "7.5.2");
            reference = XamlBoundTypeReference.Error(requested, diagnostic);
        }
        else
        {
            reference = XamlBoundTypeReference.Resolved(requested, symbol);
        }
        return new XamlBoundTypeValue(reference, text.SourceSpan, text.StableId);
    }

    private XamlBoundStaticMemberValue BindStaticMemberValue(XamlInfosetText text, XamlInfosetObject parentObject)
    {
        var raw = text.Text.Trim();
        var memberSeparator = raw.LastIndexOf('.');
        var typeText = memberSeparator <= 0 ? string.Empty : raw.Substring(0, memberSeparator);
        var memberName = memberSeparator < 0 || memberSeparator == raw.Length - 1
            ? string.Empty
            : raw.Substring(memberSeparator + 1);
        var prefixSeparator = typeText.IndexOf(':');
        var prefix = prefixSeparator < 0 ? string.Empty : typeText.Substring(0, prefixSeparator);
        var localTypeName = prefixSeparator < 0 ? typeText : typeText.Substring(prefixSeparator + 1);
        var namespaceUri = string.Empty;
        foreach (var mapping in parentObject.NamespaceMappings)
        {
            if (string.Equals(mapping.Prefix, prefix, StringComparison.Ordinal))
            {
                namespaceUri = NormalizeConditionalNamespace(mapping.NamespaceUri);
                break;
            }
        }
        var type = localTypeName.Length == 0 ? null : _typeSystem.ResolveType(namespaceUri, localTypeName);
        ISymbol? selected = null;
        var ambiguous = false;
        if (type?.Symbol is INamedTypeSymbol named)
        {
            foreach (var candidate in named.GetMembers(memberName))
            {
                var applicable = candidate switch
                {
                    IFieldSymbol field => field.IsStatic && field.DeclaredAccessibility == Accessibility.Public,
                    IPropertySymbol property => property.IsStatic && property.GetMethod?.DeclaredAccessibility == Accessibility.Public,
                    _ => false
                };
                if (!applicable) continue;
                if (selected != null)
                {
                    ambiguous = true;
                    break;
                }
                selected = candidate;
            }
        }
        if (selected != null && !ambiguous)
            return new XamlBoundStaticMemberValue(selected, raw, null, text.SourceSpan, text.StableId);

        var diagnostic = Error(
            "PGXAML2018",
            ambiguous
                ? $"The x:Static argument '{text.Text}' resolves ambiguously."
                : $"The x:Static argument '{text.Text}' does not identify an accessible public static field or property.",
            text.SourceSpan,
            "7.4.2");
        return new XamlBoundStaticMemberValue(null, raw, diagnostic, text.SourceSpan, text.StableId);
    }

    private XamlBoundFactoryMethodValue BindFactoryMethodValue(
        XamlInfosetText text,
        XamlInfosetObject parentObject,
        XamlBoundTypeReference parentType)
    {
        var raw = text.Text.Trim();
        var separator = raw.LastIndexOf('.');
        var ownerText = separator < 0 ? string.Empty : raw.Substring(0, separator);
        var methodName = separator < 0 ? raw : raw.Substring(separator + 1);
        var owner = parentType.Symbol;
        if (ownerText.Length != 0)
        {
            var prefixSeparator = ownerText.IndexOf(':');
            var prefix = prefixSeparator < 0 ? string.Empty : ownerText.Substring(0, prefixSeparator);
            var localName = prefixSeparator < 0 ? ownerText : ownerText.Substring(prefixSeparator + 1);
            var namespaceUri = string.Empty;
            foreach (var mapping in parentObject.NamespaceMappings)
                if (string.Equals(mapping.Prefix, prefix, StringComparison.Ordinal))
                {
                    namespaceUri = NormalizeConditionalNamespace(mapping.NamespaceUri);
                    break;
                }
            owner = _typeSystem.ResolveType(namespaceUri, localName);
        }
        var argumentCount = parentObject.Members
            .Where(member => member.Name.IsDirective && string.Equals(member.Name.LocalName, "Arguments", StringComparison.Ordinal))
            .Select(member => member.Values.Length)
            .DefaultIfEmpty(0)
            .Single();
        IMethodSymbol? selected = null;
        var ambiguous = false;
        if (owner?.Symbol is INamedTypeSymbol named && parentType.Symbol != null)
        {
            foreach (var candidate in named.GetMembers(methodName).OfType<IMethodSymbol>())
            {
                if (!candidate.IsStatic || candidate.IsGenericMethod || candidate.ReturnsVoid ||
                    candidate.DeclaredAccessibility != Accessibility.Public || candidate.Parameters.Length != argumentCount) continue;
                var returnType = DescribeParameterType(candidate.ReturnType, parentType.Symbol.NamespaceUri);
                if (!_typeSystem.IsAssignable(returnType, parentType.Symbol)) continue;
                if (selected != null) { ambiguous = true; break; }
                selected = candidate;
            }
        }
        if (selected != null && !ambiguous)
            return new XamlBoundFactoryMethodValue(selected, raw, null, text.SourceSpan, text.StableId);
        var diagnostic = Error("PGXAML2027",
            ambiguous
                ? $"Factory method '{text.Text}' is ambiguous for {argumentCount} arguments."
                : $"Factory method '{text.Text}' is not an accessible public static method returning '{parentType.Symbol?.MetadataName}'.",
            text.SourceSpan,
            "7.3.17");
        return new XamlBoundFactoryMethodValue(null, raw, diagnostic, text.SourceSpan, text.StableId);
    }

    private static bool IsIntrinsicPositionalMember(
        XamlInfosetObject parentObject,
        XamlInfosetMember member,
        string firstName,
        string secondName) =>
        parentObject.IsMarkupExtension &&
        string.Equals(parentObject.TypeName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
        (string.Equals(parentObject.TypeName.LocalName, firstName, StringComparison.Ordinal) ||
         string.Equals(parentObject.TypeName.LocalName, secondName, StringComparison.Ordinal)) &&
        member.Name.IsDirective &&
        string.Equals(member.Name.LocalName, "PositionalParameters", StringComparison.Ordinal);

    private XamlBoundMemberReference BindMemberReference(
        XamlInfosetMember value,
        XamlBoundTypeReference parentType)
    {
        if (IsArrayIntrinsicMember(parentType, value, "Type"))
            return XamlBoundMemberReference.Synthetic(value.Name, XamlBoundReferenceKind.Intrinsic);

        if (parentType.Symbol != null && value.Name.IsDirective)
        {
            string? aliasedMember = null;
            if (string.Equals(value.Name.NamespaceUri, XamlNamespaces.Xml, StringComparison.Ordinal) &&
                string.Equals(value.Name.LocalName, "lang", StringComparison.Ordinal))
                aliasedMember = parentType.Symbol.XmlLanguageMemberName;
            else if (string.Equals(value.Name.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                string.Equals(value.Name.LocalName, "Uid", StringComparison.Ordinal))
                aliasedMember = parentType.Symbol.UidMemberName;
            if (!string.IsNullOrEmpty(aliasedMember))
            {
                var projected = _typeSystem.ResolveMember(
                    parentType.Symbol,
                    parentType.Symbol.NamespaceUri,
                    null,
                    aliasedMember!);
                if (projected != null) return XamlBoundMemberReference.Resolved(value.Name, projected);
                var diagnostic = Error(
                    "PGXAML2037",
                    $"Directive '{value.Name.DisplayName}' aliases missing member '{aliasedMember}' on '{parentType.Symbol.MetadataName}'.",
                    value.SourceSpan,
                    "5.4.2");
                return XamlBoundMemberReference.Error(value.Name, diagnostic);
            }
        }

        if (value.Name.IsDirective)
            return XamlBoundMemberReference.Synthetic(value.Name, XamlBoundReferenceKind.Directive);

        if (_typeSystem is IXamlDialectDirectiveResolver directiveResolver &&
            directiveResolver.TryResolveDirective(value.Name.NamespaceUri, value.Name.LocalName, out var directive))
        {
            if (directive!.AllowedLocation == XamlAllowedLocation.AttributeOnly &&
                value.Origin != XamlMemberOrigin.Attribute && value.Origin != XamlMemberOrigin.Directive)
            {
                var diagnostic = Error(
                    "PGXAML2045",
                    $"Framework directive '{value.Name.DisplayName}' is valid only as an attribute.",
                    value.SourceSpan,
                    "6.3.1");
                return XamlBoundMemberReference.Error(value.Name, diagnostic);
            }
            return XamlBoundMemberReference.Synthetic(value.Name, XamlBoundReferenceKind.Directive);
        }

        if (IsToolingNamespace(value.Name.NamespaceUri))
            return XamlBoundMemberReference.Synthetic(value.Name, XamlBoundReferenceKind.Directive);

        if (parentType.Symbol == null)
        {
            var diagnostic = Error(
                "PGXAML2009",
                $"Member '{value.Name.DisplayName}' cannot be resolved because its owner type is unresolved.",
                value.SourceSpan,
                "6.3.2.2");
            return XamlBoundMemberReference.Error(value.Name, diagnostic);
        }

        if (value.Name.IsImplicit)
        {
            if (parentType.Symbol.IsDictionary)
                return XamlBoundMemberReference.Synthetic(value.Name, XamlBoundReferenceKind.DictionaryItems);
            if (parentType.Symbol.IsCollection)
                return XamlBoundMemberReference.Synthetic(value.Name, XamlBoundReferenceKind.CollectionItems);
            if (parentType.Symbol.TextSyntax.Kind != XamlTextSyntaxKind.None)
            {
                var initialization = new XamlInfosetMemberName(
                    XamlNamespaces.Language2006,
                    "Initialization",
                    "x",
                    isDirective: true);
                return XamlBoundMemberReference.Synthetic(initialization, XamlBoundReferenceKind.Directive);
            }
            if (string.IsNullOrEmpty(parentType.Symbol.ContentMemberName))
            {
                var diagnostic = Error(
                    "PGXAML2003",
                    $"Type '{parentType.Symbol.MetadataName}' does not declare a content member.",
                    value.SourceSpan,
                    "5.2.1.2");
                return XamlBoundMemberReference.Error(value.Name, diagnostic);
            }

            var content = _typeSystem.ResolveMember(
                parentType.Symbol,
                parentType.Symbol.NamespaceUri,
                null,
                parentType.Symbol.ContentMemberName!);
            if (content != null) return XamlBoundMemberReference.Resolved(value.Name, content);
            var contentDiagnostic = Error(
                "PGXAML2004",
                $"Content member '{parentType.Symbol.ContentMemberName}' could not be resolved on '{parentType.Symbol.MetadataName}'.",
                value.SourceSpan,
                "5.2.1.2");
            return XamlBoundMemberReference.Error(value.Name, contentDiagnostic);
        }

        var member = _typeSystem.ResolveMember(
            parentType.Symbol,
            value.Name.NamespaceUri,
            value.Name.OwnerTypeName,
            value.Name.LocalName);
        if (member != null) return XamlBoundMemberReference.Resolved(value.Name, member);

        var diagnosticError = Error(
            "PGXAML2002",
            $"Member '{value.Name.DisplayName}' was not found on '{parentType.Symbol.MetadataName}'.",
            value.SourceSpan,
            "6.3.2.2");
        return XamlBoundMemberReference.Error(value.Name, diagnosticError);
    }

    private static bool IsArrayIntrinsicMember(
        XamlBoundTypeReference parentType,
        XamlInfosetMember member,
        string memberName) =>
        string.Equals(parentType.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
        (string.Equals(parentType.RequestedName.LocalName, "Array", StringComparison.Ordinal) ||
         string.Equals(parentType.RequestedName.LocalName, "ArrayExtension", StringComparison.Ordinal)) &&
        string.Equals(member.Name.LocalName, memberName, StringComparison.Ordinal);

    private void ValidateAliasedMembers(
        XamlTypeInfo type,
        ImmutableArray<XamlBoundMember>.Builder members)
    {
        var hasXName = false;
        var hasRuntimeName = false;
        foreach (var member in members)
        {
            if (member.Member.Kind == XamlBoundReferenceKind.Directive &&
                string.Equals(member.Member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                string.Equals(member.Member.RequestedName.LocalName, "Name", StringComparison.Ordinal))
                hasXName = true;
            if (member.Member.Kind != XamlBoundReferenceKind.Directive &&
                member.Member.Symbol != null &&
                _typeSystem.IsAssignable(type, member.Member.Symbol.DeclaringType) &&
                string.Equals(member.Member.Symbol.Name, type.RuntimeNameMemberName, StringComparison.Ordinal))
                hasRuntimeName = true;
        }
        if (hasXName && hasRuntimeName)
        {
            Error(
                "PGXAML2012",
                $"Type '{type.MetadataName}' cannot set both x:Name and its runtime name member '{type.RuntimeNameMemberName}'.",
                members[0].SourceSpan,
                "6.2.2.1");
        }
    }

    private void ValidateMemberDependencies(
        XamlTypeInfo type,
        ImmutableArray<XamlBoundMember>.Builder members)
    {
        var supplied = new Dictionary<string, XamlBoundMember>(StringComparer.Ordinal);
        foreach (var member in members)
            if (member.Member.Symbol != null) supplied[member.Member.Symbol.Name] = member;

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in supplied)
        {
            foreach (var dependencyInfo in pair.Value.Member.Symbol!.Dependencies)
            {
                if (!dependencyInfo.IsValid)
                {
                    Error("PGXAML2033",
                        $"Member '{pair.Key}' has invalid dependency metadata: {dependencyInfo.Error}",
                        pair.Value.SourceSpan,
                        "5.4.2");
                    continue;
                }
                var dependency = dependencyInfo.Dependency!.Name;
                if (!supplied.ContainsKey(dependency)) continue;
                if (!edges.TryGetValue(pair.Key, out var dependencies))
                    edges.Add(pair.Key, dependencies = new List<string>());
                if (!dependencies.Contains(dependency)) dependencies.Add(dependency);
            }
        }

        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var name in supplied.Keys)
            if (HasDependencyCycle(name, edges, state))
            {
                Error("PGXAML2034",
                    $"Member dependency metadata on '{type.MetadataName}' contains a cycle involving '{name}'.",
                    supplied[name].SourceSpan,
                    "5.4.2");
                break;
            }
    }

    private static bool HasDependencyCycle(
        string name,
        Dictionary<string, List<string>> edges,
        Dictionary<string, byte> state)
    {
        if (state.TryGetValue(name, out var existing)) return existing == 1;
        state[name] = 1;
        if (edges.TryGetValue(name, out var dependencies))
            foreach (var dependency in dependencies)
                if (HasDependencyCycle(dependency, edges, state)) return true;
        state[name] = 2;
        return false;
    }

    private void ReportUsageAnnotations(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string displayName,
        TextSpan span)
    {
        foreach (var annotation in annotations)
        {
            if (string.Equals(annotation.Semantic, XamlSchemaSemantics.Obsolete, StringComparison.Ordinal))
            {
                var isError = annotation.Attribute.ConstructorArguments.Length > 1 &&
                    annotation.Attribute.ConstructorArguments[1].Value is bool value && value;
                Report(
                    "PGXAML2035",
                    isError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    $"XAML symbol '{displayName}' is obsolete" +
                    (string.IsNullOrWhiteSpace(annotation.Value) ? "." : ": " + annotation.Value),
                    span,
                    specificationSection: null);
            }
            else if (string.Equals(annotation.Semantic, XamlSchemaSemantics.Experimental, StringComparison.Ordinal))
            {
                Report(
                    "PGXAML2036",
                    DiagnosticSeverity.Warning,
                    $"XAML symbol '{displayName}' is experimental" +
                    (string.IsNullOrWhiteSpace(annotation.Value) ? "." : $" ({annotation.Value})."),
                    span,
                    specificationSection: null);
            }
        }
    }

    private void ReportSetValueHandlerShape(
        XamlSetValueHandlerShapeInfo? shape,
        string displayName,
        TextSpan span)
    {
        if (shape == null || shape.IsValid) return;
        Report(
            "PGXAML2048",
            DiagnosticSeverity.Error,
            $"XAML set-value handler '{displayName}.{shape.HandlerName}' is invalid: {shape.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportMarkupExtensionReceiverShape(
        XamlMarkupExtensionReceiverShapeInfo? shape,
        string displayName,
        TextSpan span)
    {
        if (shape == null || shape.IsValid) return;
        Report(
            "PGXAML2083",
            DiagnosticSeverity.Error,
            $"Markup-extension receiver '{displayName}' is invalid: {shape.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportValueSerializerShape(
        XamlValueSerializerShapeInfo? shape,
        string displayName,
        TextSpan span)
    {
        if (shape == null || shape.IsValid) return;
        Report(
            "PGXAML2050",
            DiagnosticSeverity.Error,
            $"Value serializer for '{displayName}' is invalid: {shape.Error}",
            span,
            "5.4.1.1");
    }

    private void ReportBooleanSchemaInfo(
        XamlSchemaBooleanInfo? info,
        string displayName,
        TextSpan span)
    {
        if (info == null || info.IsValid) return;
        Report(
            "PGXAML2051",
            DiagnosticSeverity.Error,
            $"Boolean XAML schema metadata '{info.Semantic}' for '{displayName}' is invalid: {info.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportContentWrapperShapes(
        IReadOnlyList<XamlContentWrapperShapeInfo> shapes,
        string displayName,
        TextSpan span)
    {
        foreach (var shape in shapes)
        {
            if (shape.IsValid) continue;
            Report(
                "PGXAML2052",
                DiagnosticSeverity.Error,
                $"Content wrapper metadata for '{displayName}' is invalid: {shape.Error}",
                span,
                "5.2.2.3");
        }
    }

    private void ReportConstructorArgumentShape(
        XamlConstructorArgumentShapeInfo? shape,
        string displayName,
        TextSpan span)
    {
        if (shape == null || shape.IsValid) return;
        Report(
            "PGXAML2055",
            DiagnosticSeverity.Error,
            $"Constructor-argument metadata for '{displayName}' is invalid: {shape.Error}",
            span,
            "5.4.2");
    }

    private void ReportDeferringLoaderShape(
        XamlDeferringLoaderShapeInfo? shape,
        string displayName,
        TextSpan span)
    {
        if (shape == null || shape.IsValid) return;
        Report(
            "PGXAML2056",
            DiagnosticSeverity.Error,
            $"Deferred-load metadata for '{displayName}' is invalid: {shape.Error}",
            span,
            "5.5");
    }

    private void ReportMarkupBracketPairs(
        IReadOnlyList<XamlMarkupBracketPairInfo> pairs,
        string displayName,
        TextSpan span)
    {
        foreach (var pair in pairs)
        {
            if (pair.IsValid) continue;
            Report(
                "PGXAML2057",
                DiagnosticSeverity.Error,
                $"Markup-extension bracket metadata for '{displayName}' is invalid: {pair.Error}",
                span,
                "7.3.4");
        }
    }

    private void ReportAliasedMemberShape(
        XamlAliasedMemberShapeInfo? shape,
        string displayName,
        TextSpan span)
    {
        if (shape == null || shape.IsValid) return;
        Report(
            "PGXAML2058",
            DiagnosticSeverity.Error,
            $"Aliased-member metadata '{shape.Semantic}' for '{displayName}' is invalid: {shape.Error}",
            span,
            "5.4.2");
    }

    private void ReportNameScopeShape(
        XamlNameScopeShapeInfo? shape,
        string displayName,
        TextSpan span)
    {
        if (shape == null || shape.IsValid) return;
        Report(
            "PGXAML2060",
            DiagnosticSeverity.Error,
            $"Namescope ownership shape for '{displayName}' is invalid: {shape.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportMarkupExtensionOption(
        XamlMarkupExtensionOptionInfo? option,
        string displayName,
        TextSpan span)
    {
        if (option == null || option.IsValid) return;
        Report(
            "PGXAML2061",
            DiagnosticSeverity.Error,
            $"Markup-extension option metadata for '{displayName}' is invalid: {option.Error}",
            span,
            "7.3.18");
    }

    private void ReportMarkupExtensionOptionSelector(
        XamlMarkupExtensionOptionSelectorShapeInfo? selector,
        string displayName,
        TextSpan span)
    {
        if (selector == null || selector.IsValid) return;
        Report(
            "PGXAML2062",
            DiagnosticSeverity.Error,
            $"Markup-extension option selector for '{displayName}' is invalid: {selector.Error}",
            span,
            "7.3.18");
    }

    private void ReportListSplit(
        XamlListSplitInfo? listSplit,
        string displayName,
        TextSpan span)
    {
        if (listSplit == null || listSplit.IsValid) return;
        Report(
            "PGXAML2063",
            DiagnosticSeverity.Error,
            $"List-string metadata for '{displayName}' is invalid: {listSplit.Error}",
            span,
            "6.3.2.4");
    }

    private void ReportDataTypeSource(
        XamlDataTypeSourceInfo? source,
        string displayName,
        TextSpan span)
    {
        if (source == null || source.IsValid) return;
        Report(
            "PGXAML2064",
            DiagnosticSeverity.Error,
            $"Compiled-binding data-type source for '{displayName}' is invalid: {source.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportDataTypeInheritance(
        XamlDataTypeInheritanceInfo? inheritance,
        string displayName,
        TextSpan span)
    {
        if (inheritance == null || inheritance.IsValid) return;
        Report(
            "PGXAML2065",
            DiagnosticSeverity.Error,
            $"Compiled-binding data-type inheritance for '{displayName}' is invalid: {inheritance.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportItemsDataTypeInheritance(
        XamlItemsDataTypeInheritanceInfo? inheritance,
        string displayName,
        TextSpan span)
    {
        if (inheritance == null || inheritance.IsValid) return;
        Report(
            "PGXAML2066",
            DiagnosticSeverity.Error,
            $"Compiled-binding item data-type inheritance for '{displayName}' is invalid: {inheritance.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportBindingAssignment(
        XamlBindingAssignmentInfo? assignment,
        string displayName,
        TextSpan span)
    {
        if (assignment == null || assignment.IsValid) return;
        Report(
            "PGXAML2067",
            DiagnosticSeverity.Error,
            $"Binding-object assignment metadata for '{displayName}' is invalid: {assignment.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportTemplateParts(
        IReadOnlyList<XamlTemplatePartInfo> parts,
        string displayName,
        TextSpan span)
    {
        foreach (var part in parts)
        {
            if (part.IsValid) continue;
            Report(
                "PGXAML2068",
                DiagnosticSeverity.Error,
                $"Template-part metadata for '{displayName}' is invalid: {part.Error}",
                span,
                "5.2.1.1");
        }
    }

    private void ReportTemplateVisualStates(
        IReadOnlyList<XamlTemplateVisualStateInfo> states,
        string displayName,
        TextSpan span)
    {
        foreach (var state in states)
        {
            if (state.IsValid) continue;
            Report(
                "PGXAML2069",
                DiagnosticSeverity.Error,
                $"Template visual-state metadata for '{displayName}' is invalid: {state.Error}",
                span,
                "5.2.1.1");
        }
    }

    private void ReportStyleTypedProperties(
        IReadOnlyList<XamlStyleTypedPropertyInfo> properties,
        string displayName,
        TextSpan span)
    {
        foreach (var property in properties)
        {
            if (property.IsValid) continue;
            Report(
                "PGXAML2070",
                DiagnosticSeverity.Error,
                $"Style-typed property metadata for '{displayName}' is invalid: {property.Error}",
                span,
                "5.2.1.1");
        }
    }

    private void ReportAttachedPropertyBrowseRules(
        IReadOnlyList<XamlAttachedPropertyBrowseRuleInfo> rules,
        string displayName,
        TextSpan span)
    {
        foreach (var rule in rules)
        {
            if (rule.IsValid) continue;
            Report(
                "PGXAML2071",
                DiagnosticSeverity.Error,
                $"Attached-property browse metadata for '{displayName}' is invalid: {rule.Error}",
                span,
                "5.3.1.1");
        }
    }

    private void ReportCompilationMode(
        XamlCompilationModeInfo? mode,
        string displayName,
        TextSpan span)
    {
        if (mode == null || mode.IsValid) return;
        Report(
            "PGXAML2072",
            DiagnosticSeverity.Error,
            $"XAML compilation-mode metadata for '{displayName}' is invalid: {mode.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportFilePath(
        XamlFilePathInfo? filePath,
        string displayName,
        TextSpan span)
    {
        if (filePath == null || filePath.IsValid) return;
        Report(
            "PGXAML2073",
            DiagnosticSeverity.Error,
            $"XAML file-path metadata for '{displayName}' is invalid: {filePath.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportTypeMarker(
        XamlTypeMarkerInfo? marker,
        string displayName,
        string markerName,
        string diagnosticId,
        TextSpan span)
    {
        if (marker == null || marker.IsValid) return;
        Report(
            diagnosticId,
            DiagnosticSeverity.Error,
            $"{markerName} metadata for '{displayName}' is invalid: {marker.Error}",
            span,
            "5.2.1.1");
    }

    private void ReportDefaultValueMetadata(
        XamlDefaultValueInfo? info,
        string displayName,
        TextSpan span)
    {
        if (info == null || info.IsValid) return;
        Report(
            "PGXAML2078",
            DiagnosticSeverity.Error,
            $"Default-value metadata for '{displayName}' is invalid: {info.Error}",
            span,
            "5.4.2");
    }

    private void ReportDesignerSerializationMetadata(
        XamlDesignerSerializationVisibilityInfo? visibility,
        XamlDesignerSerializationOptionsInfo? options,
        string displayName,
        TextSpan span)
    {
        if (visibility != null && !visibility.IsValid)
            Report(
                "PGXAML2079",
                DiagnosticSeverity.Error,
                $"Designer serialization visibility for '{displayName}' is invalid: {visibility.Error}",
                span,
                "5.4.2");
        if (options != null && !options.IsValid)
            Report(
                "PGXAML2079",
                DiagnosticSeverity.Error,
                $"Designer serialization options for '{displayName}' are invalid: {options.Error}",
                span,
                "5.4.2");
    }

    private void ReportBrowsableMetadata(
        XamlBrowsableInfo? info,
        string displayName,
        string semantic,
        TextSpan span)
    {
        if (info == null || info.IsValid) return;
        Report(
            "PGXAML2080",
            DiagnosticSeverity.Error,
            $"Designer discovery metadata '{semantic}' for '{displayName}' is invalid: {info.Error}",
            span,
            "5.4.2");
    }

    private void ReportEditorBrowsableMetadata(
        XamlEditorBrowsableInfo? info,
        string displayName,
        TextSpan span)
    {
        if (info == null || info.IsValid) return;
        Report(
            "PGXAML2080",
            DiagnosticSeverity.Error,
            $"Editor-browsable metadata for '{displayName}' is invalid: {info.Error}",
            span,
            "5.4.2");
    }

    private void ReportLocalizabilityMetadata(
        XamlLocalizabilityInfo? info,
        string displayName,
        TextSpan span)
    {
        if (info == null || info.IsValid) return;
        Report(
            "PGXAML2081",
            DiagnosticSeverity.Error,
            $"Localizability metadata for '{displayName}' is invalid: {info.Error}",
            span,
            "5.4.2");
    }

    private void ReportDesignerSerializationMethods(
        XamlDesignerSerializationMethodsInfo? info,
        string displayName,
        TextSpan span)
    {
        if (info == null || info.IsValid) return;
        Report(
            "PGXAML2082",
            DiagnosticSeverity.Error,
            $"Designer serialization methods for '{displayName}' are invalid: {info.Error}",
            span,
            "5.4.2");
    }

    private void ReportSchemaAnnotationConflicts(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string displayName,
        TextSpan span)
    {
        for (var leftIndex = 0; leftIndex < annotations.Count; leftIndex++)
        {
            var left = annotations[leftIndex];
            if (left.AllowMultiple) continue;
            for (var rightIndex = leftIndex + 1; rightIndex < annotations.Count; rightIndex++)
            {
                var right = annotations[rightIndex];
                if (right.AllowMultiple ||
                    !string.Equals(left.Semantic, right.Semantic, StringComparison.Ordinal) ||
                    left.ProviderPriority != right.ProviderPriority ||
                    left.InheritanceDepth != right.InheritanceDepth ||
                    string.Equals(left.Value, right.Value, StringComparison.Ordinal)) continue;
                Error(
                    "PGXAML2038",
                    $"XAML symbol '{displayName}' has conflicting '{left.Semantic}' metadata from " +
                    $"'{left.ProviderId}:{left.Attribute.AttributeClass?.ToDisplayString()}' and " +
                    $"'{right.ProviderId}:{right.Attribute.AttributeClass?.ToDisplayString()}'.",
                    span,
                    "5.4.2");
                break;
            }
        }
    }

    private XamlBoundObject RewriteResolvedBindings(
        XamlBoundObject value)
    {
        var members = ImmutableArray.CreateBuilder<XamlBoundMember>(
            value.Members.Length);
        var changed = false;
        foreach (var member in value.Members)
        {
            var values = ImmutableArray.CreateBuilder<XamlBoundValue>(
                member.Values.Length);
            var memberChanged = false;
            foreach (var child in member.Values)
            {
                XamlBoundValue rewritten = child;
                if (child is XamlBoundBinding binding &&
                    _resolvedBindingSources.TryGetValue(
                        binding.StableId,
                        out var source))
                {
                    rewritten = BindBinding(
                        binding.Extension,
                        targetObjectType: null,
                        resolvedSourceType: source.Type,
                        resolvedSourceKind: source.Kind);
                }
                else if (child is XamlBoundObject childObject)
                {
                    rewritten = RewriteResolvedBindings(childObject);
                }

                memberChanged |= !ReferenceEquals(rewritten, child);
                values.Add(rewritten);
            }

            if (memberChanged)
            {
                members.Add(new XamlBoundMember(
                    member.Member,
                    member.Origin,
                    values.ToImmutable(),
                    member.SourceSpan,
                    member.StableId,
                    member.Condition));
                changed = true;
            }
            else
            {
                members.Add(member);
            }
        }

        return changed
            ? new XamlBoundObject(
                value.Type,
                members.ToImmutable(),
                value.IsRetrieved,
                value.IsMarkupExtension,
                value.SourceSpan,
                value.StableId,
                value.Condition)
            : value;
    }

    private XamlBoundObject RewriteResourceBindingSources(
        XamlBoundObject value,
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo> references,
        IReadOnlyDictionary<ulong, XamlTypeInfo> valueTypes)
    {
        var members = ImmutableArray.CreateBuilder<XamlBoundMember>(
            value.Members.Length);
        var changed = false;
        foreach (var member in value.Members)
        {
            var values = ImmutableArray.CreateBuilder<XamlBoundValue>(
                member.Values.Length);
            var memberChanged = false;
            foreach (var child in member.Values)
            {
                XamlBoundValue rewritten = child;
                if (child is XamlBoundBinding binding &&
                    TryGetExplicitResourceSource(
                        binding,
                        references,
                        valueTypes,
                        out var sourceType))
                {
                    rewritten = BindBinding(
                        binding.Extension,
                        targetObjectType: null,
                        resolvedSourceType: sourceType,
                        resolvedSourceKind: XamlBindingSourceKind.ExplicitResource);
                }
                else if (child is XamlBoundObject childObject)
                {
                    rewritten = RewriteResourceBindingSources(
                        childObject,
                        references,
                        valueTypes);
                }

                memberChanged |= !ReferenceEquals(rewritten, child);
                values.Add(rewritten);
            }

            if (memberChanged)
            {
                members.Add(new XamlBoundMember(
                    member.Member,
                    member.Origin,
                    values.ToImmutable(),
                    member.SourceSpan,
                    member.StableId,
                    member.Condition));
                changed = true;
            }
            else
            {
                members.Add(member);
            }
        }

        return changed
            ? new XamlBoundObject(
                value.Type,
                members.ToImmutable(),
                value.IsRetrieved,
                value.IsMarkupExtension,
                value.SourceSpan,
                value.StableId,
                value.Condition)
            : value;
    }

    private static bool TryGetExplicitResourceSource(
        XamlBoundBinding binding,
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo> references,
        IReadOnlyDictionary<ulong, XamlTypeInfo> valueTypes,
        out XamlTypeInfo sourceType)
    {
        sourceType = null!;
        if (binding.SourceKind != XamlBindingSourceKind.Unknown)
            return false;

        var sourceMember = FindBindingMember(binding.Extension, "Source");
        if (sourceMember == null ||
            FindBindingMember(binding.Extension, "RelativeSource") != null)
            return false;
        var elementNameMember = FindBindingMember(binding.Extension, "ElementName");
        if (elementNameMember?.Values.OfType<XamlBoundText>().Any(
                value => !string.IsNullOrWhiteSpace(value.Text)) == true)
            return false;

        foreach (var value in sourceMember.Values)
        {
            if (value is XamlBoundObject sourceObject &&
                references.ContainsKey(sourceObject.StableId) &&
                valueTypes.TryGetValue(sourceObject.StableId, out sourceType))
                return true;
        }
        return false;
    }

    private void ValidateDocumentSemantics(XamlBoundObject root)
    {
        var names = new Dictionary<string, XamlBoundObject>(StringComparer.Ordinal);
        var references = new List<NameReferenceUse>();
        var elementBindings = new List<ElementNameBindingUse>();
        var templatedParentBindings = new List<XamlBoundBinding>();
        var hasClass = HasDirective(root, "Class");
        ValidateObjectSemantics(
            root,
            parentMember: null,
            hasClass,
            names,
            references,
            elementBindings,
            templatedParentBindings);
        ResolveNameReferences(names, references);
        ResolveElementNameBindings(names, elementBindings);
    }

    private void ResolveNameReferences(
        Dictionary<string, XamlBoundObject> names,
        List<NameReferenceUse> references)
    {
        foreach (var use in references)
        {
            if (string.IsNullOrWhiteSpace(use.Reference.Name) || !names.TryGetValue(use.Reference.Name, out var target))
            {
                Error("PGXAML2039", $"x:Reference name '{use.Reference.Name}' was not found in the current XAML namescope.",
                    use.Reference.SourceSpan, "6.2.2.11");
                continue;
            }
            if (use.TargetType != null && target.Type.Symbol != null &&
                !_typeSystem.IsAssignable(target.Type.Symbol, use.TargetType))
                Error("PGXAML2040",
                    $"Referenced object '{use.Reference.Name}' of type '{target.Type.Symbol.MetadataName}' is not assignable to '{use.TargetType.MetadataName}'.",
                    use.Reference.SourceSpan, "6.2.2.11");
        }
    }

    private void ResolveElementNameBindings(
        Dictionary<string, XamlBoundObject> names,
        List<ElementNameBindingUse> bindings)
    {
        foreach (var use in bindings)
        {
            if (!names.TryGetValue(use.Name, out var target) ||
                target.Type.Symbol == null)
                continue;

            _resolvedBindingSources[use.Binding.StableId] =
                new ResolvedBindingSource(
                    target.Type.Symbol,
                    XamlBindingSourceKind.ElementName);
        }
    }

    private void ResolveTemplatedParentBindings(
        XamlTypeInfo contextType,
        List<XamlBoundBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            _resolvedBindingSources[binding.StableId] =
                new ResolvedBindingSource(
                    contextType,
                    XamlBindingSourceKind.RelativeTemplatedParent);
        }
    }

    private void ValidateObjectSemantics(
        XamlBoundObject value,
        XamlBoundMemberReference? parentMember,
        bool rootHasClass,
        Dictionary<string, XamlBoundObject> names,
        List<NameReferenceUse> references,
        List<ElementNameBindingUse> elementBindings,
        List<XamlBoundBinding> templatedParentBindings)
    {
        var ownsNestedNameScope =
            parentMember != null &&
            value.Type.Symbol?.IsNameScope == true;
        var activeNames = ownsNestedNameScope
            ? new Dictionary<string, XamlBoundObject>(StringComparer.Ordinal)
            : names;
        var activeReferences = ownsNestedNameScope
            ? new List<NameReferenceUse>()
            : references;
        var activeElementBindings = ownsNestedNameScope
            ? new List<ElementNameBindingUse>()
            : elementBindings;

        if (string.Equals(value.Type.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
            (string.Equals(value.Type.RequestedName.LocalName, "Reference", StringComparison.Ordinal) ||
             string.Equals(value.Type.RequestedName.LocalName, "ReferenceExtension", StringComparison.Ordinal)))
        {
            var reference = value.Members.SelectMany(static member => member.Values)
                .OfType<XamlBoundNameReferenceValue>().SingleOrDefault();
            if (reference != null)
                activeReferences.Add(new NameReferenceUse(
                    reference,
                    GetReferenceTargetType(parentMember)));
        }
        string? objectName = null;
        var hasFieldModifier = false;
        foreach (var member in value.Members)
        {
            if (member.Member.Kind == XamlBoundReferenceKind.DictionaryItems ||
                (member.Member.Symbol?.Kind == XamlMemberKind.Dictionary &&
                 !member.Values.OfType<XamlBoundObject>().Any(static child => child.IsRetrieved)))
                ValidateDictionaryItems(value, member);

            if (member.Member.Kind == XamlBoundReferenceKind.Directive &&
                string.Equals(member.Member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal))
            {
                if (string.Equals(member.Member.RequestedName.LocalName, "Name", StringComparison.Ordinal))
                    objectName = FirstText(member);
                else if (string.Equals(member.Member.RequestedName.LocalName, "FieldModifier", StringComparison.Ordinal))
                    hasFieldModifier = true;
                else if (string.Equals(member.Member.RequestedName.LocalName, "Key", StringComparison.Ordinal) &&
                         parentMember?.Kind != XamlBoundReferenceKind.DictionaryItems &&
                         parentMember?.Symbol?.Kind != XamlMemberKind.Dictionary)
                {
                    Error("PGXAML2015", "x:Key is valid only for an object in dictionary content.",
                        member.SourceSpan, "6.3.2.7");
                }
            }

            if (member.Member.Symbol != null)
            {
                if (member.Member.Symbol.Kind == XamlMemberKind.Event && !rootHasClass)
                {
                    Error("PGXAML2014", "Event members require x:Class on the document root.",
                        member.SourceSpan, "6.2.1.2");
                }
                if (value.Type.Symbol?.RuntimeNameMemberName != null &&
                    string.Equals(member.Member.Symbol.Name, value.Type.Symbol.RuntimeNameMemberName, StringComparison.Ordinal))
                    objectName ??= FirstText(member);
            }

            foreach (var binding in member.Values.OfType<XamlBoundBinding>())
            {
                if (TryGetUnambiguousElementName(binding, out var elementName))
                {
                    activeElementBindings.Add(
                        new ElementNameBindingUse(
                            binding,
                            elementName));
                }
                if (_typeSystem is
                        IXamlDeferredContentContextTypePolicy
                            deferredContextPolicy &&
                    deferredContextPolicy
                        .IsDeferredContentContextSource(binding))
                    templatedParentBindings.Add(binding);
            }

            if (StartsNestedNameScope(member))
            {
                var nestedNames = new Dictionary<string, XamlBoundObject>(StringComparer.Ordinal);
                var nestedReferences = new List<NameReferenceUse>();
                var nestedElementBindings = new List<ElementNameBindingUse>();
                var nestedTemplatedParentBindings =
                    new List<XamlBoundBinding>();
                foreach (var child in member.Values)
                    if (child is XamlBoundObject childObject)
                        ValidateObjectSemantics(
                            childObject,
                            member.Member,
                            rootHasClass,
                            nestedNames,
                            nestedReferences,
                            nestedElementBindings,
                            nestedTemplatedParentBindings);
                ResolveNameReferences(nestedNames, nestedReferences);
                ResolveElementNameBindings(
                    nestedNames,
                    nestedElementBindings);
                if (_typeSystem is
                        IXamlDeferredContentContextTypePolicy contextPolicy &&
                    contextPolicy.TryGetDeferredContentContextType(
                        value,
                        member,
                        out var contextType))
                {
                    ResolveTemplatedParentBindings(
                        contextType,
                        nestedTemplatedParentBindings);
                }
            }
            else
            {
                foreach (var child in member.Values)
                    if (child is XamlBoundObject childObject)
                        ValidateObjectSemantics(
                            childObject,
                            member.Member,
                            rootHasClass,
                            activeNames,
                            activeReferences,
                            activeElementBindings,
                            templatedParentBindings);
            }
        }

        if (hasFieldModifier && string.IsNullOrEmpty(objectName))
        {
            Error("PGXAML2016", "x:FieldModifier requires x:Name or the runtime name member on the same object.",
                value.SourceSpan, "6.3.2.8");
        }
        if (!string.IsNullOrEmpty(objectName))
        {
            if (activeNames.TryGetValue(objectName!, out _))
            {
                Error("PGXAML2013", $"Name '{objectName}' is already defined in this XAML namescope.",
                    value.SourceSpan, "6.3.2.6");
            }
            else
            {
                activeNames.Add(objectName!, value);
            }
        }
        if (ownsNestedNameScope)
        {
            ResolveNameReferences(activeNames, activeReferences);
            ResolveElementNameBindings(
                activeNames,
                activeElementBindings);
        }
    }

    private static bool TryGetUnambiguousElementName(
        XamlBoundBinding binding,
        out string elementName)
    {
        elementName = string.Empty;
        if (FindBindingMember(binding.Extension, "Source") != null ||
            FindBindingMember(binding.Extension, "RelativeSource") != null)
            return false;
        var member = FindBindingMember(binding.Extension, "ElementName");
        var text = member?.Values.OfType<XamlBoundText>()
            .Select(static value => value.Text.Trim())
            .FirstOrDefault(static value => value.Length != 0);
        if (text == null) return false;
        elementName = text;
        return true;
    }

    private static bool StartsNestedNameScope(XamlBoundMember member)
    {
        if (member.Member.Symbol?.Kind == XamlMemberKind.DeferredContent)
            return true;

        var annotations = member.Member.Symbol?.Annotations;
        if (annotations == null) return false;
        for (var index = 0; index < annotations.Count; index++)
        {
            var semantic = annotations[index].Semantic;
            if (string.Equals(semantic, XamlSchemaSemantics.TemplateContent, StringComparison.Ordinal) ||
                string.Equals(semantic, XamlSchemaSemantics.NameScopeProperty, StringComparison.Ordinal) ||
                string.Equals(semantic, XamlSchemaSemantics.ControlTemplateScope, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static XamlTypeInfo? GetReferenceTargetType(XamlBoundMemberReference? parentMember)
    {
        var member = parentMember?.Symbol;
        if (member == null) return null;
        if ((member.Kind == XamlMemberKind.Collection || member.Kind == XamlMemberKind.Dictionary) &&
            member.ValueType.CollectionShape != null)
            return DescribeParameterType(member.ValueType.CollectionShape.ItemType, member.ValueType.NamespaceUri);
        return member.ValueType;
    }

    private readonly struct NameReferenceUse
    {
        public NameReferenceUse(XamlBoundNameReferenceValue reference, XamlTypeInfo? targetType)
        {
            Reference = reference;
            TargetType = targetType;
        }
        public XamlBoundNameReferenceValue Reference { get; }
        public XamlTypeInfo? TargetType { get; }
    }

    private readonly struct ElementNameBindingUse
    {
        public ElementNameBindingUse(
            XamlBoundBinding binding,
            string name)
        {
            Binding = binding;
            Name = name;
        }

        public XamlBoundBinding Binding { get; }
        public string Name { get; }
    }

    private readonly struct ResolvedBindingSource
    {
        public ResolvedBindingSource(
            XamlTypeInfo type,
            XamlBindingSourceKind kind)
        {
            Type = type;
            Kind = kind;
        }

        public XamlTypeInfo Type { get; }
        public XamlBindingSourceKind Kind { get; }
    }

    private void ValidateDictionaryItems(XamlBoundObject owner, XamlBoundMember member)
    {
        var keys = new Dictionary<XamlResourceKeyInfo, TextSpan>();
        var dictionaryType = member.Member.Symbol?.ValueType ?? owner.Type.Symbol;
        var dictionaryKeyType = dictionaryType?.CollectionShape?.KeyType;
        foreach (var item in member.Values.OfType<XamlBoundObject>())
        {
            var key = XamlResourceKeyFactory.GetDictionaryKey(
                item,
                dictionaryKeyType,
                (_typeSystem as IXamlDictionaryKeyDirectivePolicy)?.DictionaryKeyDirectiveAliases);
            if (key == null || (key.Kind == XamlResourceKeyKind.Text && string.IsNullOrEmpty(key.Text)))
            {
                // A declared key member with an invalid or unresolved value already has
                // its own binding diagnostic. Do not misreport that as a missing key.
                if (HasDeclaredDictionaryKey(item)) continue;
                Error("PGXAML2028",
                    $"Dictionary item '{item.Type.RequestedName.DisplayName}' requires x:Key or its declared dictionary-key member.",
                    item.SourceSpan,
                    "6.3.1.4");
                continue;
            }
            if (key.IsKnownNull)
            {
                Error("PGXAML2044",
                    $"Resource key '{key.Text}' is a compile-time null value; dictionary keys cannot be null.",
                    key.SourceSpan,
                    "6.3.1.4");
                continue;
            }
            if (key.Kind != XamlResourceKeyKind.Text &&
                key.ValueType != null && dictionaryKeyType != null &&
                !_typeSystem.IsAssignable(key.ValueType, dictionaryKeyType))
            {
                Error("PGXAML2043",
                    $"Resource key '{key.Text}' has value type '{key.ValueType.ToDisplayString()}', which is not assignable to dictionary key type '{dictionaryKeyType.ToDisplayString()}'.",
                    key.SourceSpan,
                    "6.3.1.4");
                continue;
            }
            if (keys.ContainsKey(key))
                Error("PGXAML2029", $"Dictionary key '{key.Text}' is duplicated in the same dictionary.", item.SourceSpan, "6.3.1.4");
            else
                keys.Add(key, item.SourceSpan);
        }
    }

    private static bool HasDeclaredDictionaryKey(XamlBoundObject item)
    {
        var implicitName = item.Type.Symbol?.DictionaryKeyMemberName;
        foreach (var member in item.Members)
        {
            if (member.Member.Kind == XamlBoundReferenceKind.Directive &&
                string.Equals(member.Member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                string.Equals(member.Member.RequestedName.LocalName, "Key", StringComparison.Ordinal))
                return true;
            if (implicitName != null && member.Member.Symbol != null &&
                string.Equals(member.Member.Symbol.Name, implicitName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool HasDirective(XamlBoundObject value, string localName)
    {
        foreach (var member in value.Members)
        {
            if (member.Member.Kind == XamlBoundReferenceKind.Directive &&
                string.Equals(member.Member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                string.Equals(member.Member.RequestedName.LocalName, localName, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string? FirstText(XamlBoundMember member)
    {
        foreach (var value in member.Values)
            if (value is XamlBoundText text) return text.Text;
        return null;
    }

    private Diagnostic Error(string id, string message, TextSpan span, string section)
        => Report(id, DiagnosticSeverity.Error, message, span, section);

    private Diagnostic Report(
        string id,
        DiagnosticSeverity severity,
        string message,
        TextSpan span,
        string? specificationSection)
    {
        var diagnostic = XamlDiagnostics.Create(
            id,
            severity,
            message,
            _document.Path,
            _document.SourceText,
            span,
            specificationSection);
        if (_diagnostics.Count < _options.MaximumDiagnostics) _diagnostics.Add(diagnostic);
        return diagnostic;
    }

    private static bool IsToolingNamespace(string namespaceUri) =>
        namespaceUri.IndexOf("markup-compatibility", StringComparison.OrdinalIgnoreCase) >= 0 ||
        namespaceUri.IndexOf("expression/blend", StringComparison.OrdinalIgnoreCase) >= 0;
}
