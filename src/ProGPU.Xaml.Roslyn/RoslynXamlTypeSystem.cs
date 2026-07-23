using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Roslyn;

public sealed class RoslynXamlTypeSystem :
    IXamlTypeSystem,
    IXamlNamespaceMetadataResolver,
    IXamlBuildMetadataResolver,
    IXamlDialectDirectiveResolver,
    IXamlTextValuePolicy,
    IXamlDictionaryKeyDirectivePolicy,
    IXamlSymbolShapeConflictProvider,
    IXamlMetadataTypeResolver,
    IXamlSymbolTypeResolver,
    IXamlSymbolConversionService,
    IXamlSymbolAccessibilityService,
    IXamlCompiledBindingPolicy,
    IXamlDeferredContentContextTypePolicy
{
    private static readonly SymbolDisplayFormat FullyQualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private static readonly IReadOnlyDictionary<char, char>
        EmptyCompiledBindingBracketPairs =
            new Dictionary<char, char>();

    private readonly Compilation _compilation;
    private readonly IXamlFrameworkProfile _profile;
    private readonly IReadOnlyList<RuleRegistration> _attributeRules;
    private readonly IReadOnlyList<ShapePolicyRegistration> _shapePolicies;
    private readonly XamlSymbolShapePolicy _shapePolicy;
    private readonly IReadOnlyList<XamlSymbolShapeConflictInfo> _symbolShapeConflicts;
    private readonly IReadOnlyList<XamlSyntheticTypeDefinition> _syntheticTypes;
    private readonly IReadOnlyDictionary<string, XamlDialectDirectiveDefinition> _dialectDirectives;
    private readonly Dictionary<string, XamlTypeInfo?> _typeCache = new Dictionary<string, XamlTypeInfo?>(StringComparer.Ordinal);
    private readonly Dictionary<string, INamedTypeSymbol> _symbolByMetadataName = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
    private readonly Dictionary<string, XamlSyntheticTypeDefinition> _syntheticByMetadataName = new Dictionary<string, XamlSyntheticTypeDefinition>(StringComparer.Ordinal);
    private readonly IReadOnlyList<XamlNamespaceDefinitionInfo> _namespaceDefinitions;
    private readonly IReadOnlyList<XamlNamespacePrefixInfo> _namespacePrefixes;
    private readonly IReadOnlyList<XamlNamespaceCompatibilityInfo> _namespaceCompatibilities;
    private readonly IReadOnlyList<XamlSchemaAnnotationInfo> _assemblyAnnotations;
    private readonly IReadOnlyList<XamlSchemaAnnotationInfo> _moduleAnnotations;
    private readonly IReadOnlyList<XamlCompilationModeInfo> _assemblyCompilationModes;
    private readonly IReadOnlyList<XamlCompilationModeInfo> _moduleCompilationModes;
    private readonly IReadOnlyList<XamlRootNamespaceInfo> _rootNamespaces;
    private readonly IReadOnlyList<XamlResourceIdInfo> _resourceIdentities;

    public IReadOnlyList<string> DictionaryKeyDirectiveAliases =>
        (_profile as IXamlDictionaryKeyDirectivePolicy)?.DictionaryKeyDirectiveAliases ??
        Array.Empty<string>();

    public XamlCompiledBindingMode DefaultCompiledBindingMode =>
        (_profile as IXamlCompiledBindingPolicy)?.DefaultCompiledBindingMode ??
        XamlCompiledBindingMode.OneTime;

    public string? DefaultCompiledBindingModeDirective =>
        (_profile as IXamlCompiledBindingPolicy)?.DefaultCompiledBindingModeDirective;

    public string? CompiledBindingDataTypeDirective =>
        (_profile as IXamlCompiledBindingPolicy)?.CompiledBindingDataTypeDirective;

    public IReadOnlyDictionary<char, char> CompiledBindingPathBracketPairs =>
        (_profile as IXamlCompiledBindingPolicy)?.CompiledBindingPathBracketPairs ??
        EmptyCompiledBindingBracketPairs;

    public bool TryGetDeferredContentContextType(
        XamlBoundObject owner,
        XamlBoundMember deferredMember,
        out XamlTypeInfo contextType)
    {
        if (_profile is IXamlDeferredContentContextTypePolicy policy)
            return policy.TryGetDeferredContentContextType(
                owner,
                deferredMember,
                out contextType);
        contextType = null!;
        return false;
    }

    public bool IsDeferredContentContextSource(
        XamlBoundBinding binding) =>
        _profile is IXamlDeferredContentContextTypePolicy policy &&
        policy.IsDeferredContentContextSource(binding);

    public bool TryValidateTextValue(XamlTypeInfo targetType, string text, out bool isValid)
    {
        if (_profile is IXamlTextValuePolicy policy)
            return policy.TryValidateTextValue(targetType, text, out isValid);
        isValid = false;
        return false;
    }

    public RoslynXamlTypeSystem(
        Compilation compilation,
        IXamlFrameworkProfile profile,
        params IXamlSchemaMetadataProvider[] additionalMetadataProviders)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        var providers = new List<IXamlSchemaMetadataProvider>();
        if (profile is IXamlSchemaMetadataProvider profileMetadata) providers.Add(profileMetadata);
        if (additionalMetadataProviders != null) providers.AddRange(additionalMetadataProviders);
        providers.Sort((left, right) =>
        {
            var priority = right.MetadataPriority.CompareTo(left.MetadataPriority);
            return priority != 0
                ? priority
                : string.Compare(left.MetadataProviderId, right.MetadataProviderId, StringComparison.Ordinal);
        });

        var rules = new List<RuleRegistration>();
        foreach (var provider in providers)
        {
            foreach (var rule in provider.AttributeRules)
            {
                rules.Add(new RuleRegistration(provider.MetadataProviderId, provider.MetadataPriority, rule));
            }
        }
        _attributeRules = rules;
        var shapePolicies = providers.Select(provider => new ShapePolicyRegistration(
            provider.MetadataProviderId,
            provider.MetadataPriority,
            provider.SymbolShapePolicy)).ToArray();
        _shapePolicies = shapePolicies;
        _shapePolicy = ComposeShapePolicies(shapePolicies, out _symbolShapeConflicts);
        _syntheticTypes = profile is IXamlSyntheticSchemaProvider synthetic
            ? synthetic.SyntheticTypes
            : Array.Empty<XamlSyntheticTypeDefinition>();
        _dialectDirectives = IndexDialectDirectives(profile);
        IndexAssemblyNamespaceMetadata(
            out _namespaceDefinitions,
            out _namespacePrefixes,
            out _namespaceCompatibilities);
        _assemblyAnnotations = IndexAssemblyAnnotations();
        _moduleAnnotations = IndexModuleAnnotations();
        _assemblyCompilationModes = FindCompilationModes(
            _assemblyAnnotations,
            XamlCompilationScope.Assembly);
        _moduleCompilationModes = FindCompilationModes(
            _moduleAnnotations,
            XamlCompilationScope.Module);
        _rootNamespaces = FindRootNamespaces(_assemblyAnnotations);
        _resourceIdentities = FindResourceIdentities(_assemblyAnnotations);
    }

    private static XamlSymbolShapePolicy ComposeShapePolicies(
        IReadOnlyList<ShapePolicyRegistration> registrations,
        out IReadOnlyList<XamlSymbolShapeConflictInfo> conflicts)
    {
        var conflictList = new List<XamlSymbolShapeConflictInfo>();

        void RecordScalarConflict(
            XamlSymbolShapeFeatures feature,
            Func<XamlSymbolShapePolicy, string> canonicalValue)
        {
            var declared = registrations.Where(registration =>
                (registration.Policy.DeclaredFeatures & feature) != 0).ToArray();
            if (declared.Length < 2) return;
            var priority = declared[0].Priority;
            var winning = declared.TakeWhile(registration => registration.Priority == priority).ToArray();
            if (winning.Length < 2 ||
                winning.Select(registration => canonicalValue(registration.Policy))
                    .Distinct(StringComparer.Ordinal).Count() < 2) return;
            conflictList.Add(new XamlSymbolShapeConflictInfo(
                feature,
                key: null,
                winning.Select(registration => new XamlSymbolShapeConflictCandidate(
                    registration.ProviderId,
                    registration.Priority,
                    canonicalValue(registration.Policy))).ToArray()));
        }

        void RecordMapConflicts<TValue>(
            XamlSymbolShapeFeatures feature,
            Func<XamlSymbolShapePolicy, IReadOnlyDictionary<string, TValue>> selector,
            Func<TValue, string> canonicalValue)
        {
            var keys = registrations
                .Where(registration => (registration.Policy.DeclaredFeatures & feature) != 0)
                .SelectMany(registration => selector(registration.Policy).Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static key => key, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var declarations = registrations.Where(registration =>
                    (registration.Policy.DeclaredFeatures & feature) != 0 &&
                    selector(registration.Policy).ContainsKey(key)).ToArray();
                if (declarations.Length < 2) continue;
                var priority = declarations[0].Priority;
                var winning = declarations.TakeWhile(registration =>
                    registration.Priority == priority).ToArray();
                if (winning.Length < 2 ||
                    winning.Select(registration =>
                            canonicalValue(selector(registration.Policy)[key]))
                        .Distinct(StringComparer.Ordinal).Count() < 2) continue;
                conflictList.Add(new XamlSymbolShapeConflictInfo(
                    feature,
                    key,
                    winning.Select(registration => new XamlSymbolShapeConflictCandidate(
                        registration.ProviderId,
                        registration.Priority,
                        canonicalValue(selector(registration.Policy)[key]))).ToArray()));
            }
        }

        IReadOnlyList<string> MergeList(
            XamlSymbolShapeFeatures feature,
            Func<XamlSymbolShapePolicy, IReadOnlyList<string>> selector,
            params string[] fallback)
        {
            var result = new List<string>();
            foreach (var registration in registrations)
            {
                if ((registration.Policy.DeclaredFeatures & feature) == 0) continue;
                foreach (var value in selector(registration.Policy))
                    if (!result.Contains(value, StringComparer.Ordinal)) result.Add(value);
            }
            foreach (var value in fallback)
                if (!result.Contains(value, StringComparer.Ordinal)) result.Add(value);
            return result;
        }

        IReadOnlyDictionary<string, TValue> MergeDictionary<TValue>(
            XamlSymbolShapeFeatures feature,
            Func<XamlSymbolShapePolicy, IReadOnlyDictionary<string, TValue>> selector)
        {
            var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
            foreach (var registration in registrations)
            {
                if ((registration.Policy.DeclaredFeatures & feature) == 0) continue;
                foreach (var pair in selector(registration.Policy))
                    if (!result.ContainsKey(pair.Key)) result.Add(pair.Key, pair.Value);
            }
            return result;
        }

        XamlSymbolShapePolicy? First(XamlSymbolShapeFeatures feature) =>
            registrations.FirstOrDefault(registration =>
                (registration.Policy.DeclaredFeatures & feature) != 0)?.Policy;

        var accessor = First(XamlSymbolShapeFeatures.AttachedAccessorPrefixes);
        var collectionInference = First(XamlSymbolShapeFeatures.CollectionInference);
        var propertySystem = First(XamlSymbolShapeFeatures.PropertySystem);
        var getterOnly = First(XamlSymbolShapeFeatures.GetterOnlyAttachedCollections);
        var serviceDeclaration = First(XamlSymbolShapeFeatures.MarkupExtensionServiceDeclaration);
        var nameScopeDuckTyping = First(XamlSymbolShapeFeatures.NameScopeDuckTyping);
        var designerSerializationMethods =
            First(XamlSymbolShapeFeatures.DesignerSerializationMethods);
        var markupExtensionReceivers =
            First(XamlSymbolShapeFeatures.MarkupExtensionReceivers);
        RecordScalarConflict(
            XamlSymbolShapeFeatures.AttachedAccessorPrefixes,
            static policy => policy.AttachedGetterPrefix + "|" + policy.AttachedSetterPrefix);
        RecordScalarConflict(
            XamlSymbolShapeFeatures.RuntimeNameFallback,
            static policy => policy.RuntimeNameFallback ?? "<null>");
        RecordScalarConflict(
            XamlSymbolShapeFeatures.CollectionInference,
            static policy => policy.InferCollectionsFromAddMethods ? "true" : "false");
        RecordScalarConflict(
            XamlSymbolShapeFeatures.PropertySystem,
            static policy =>
                (policy.PropertyIdentifierSuffix ?? "<null>") + "|" +
                (policy.PropertyIdentifierTypeMetadataName ?? "<null>") + "|" +
                (policy.PropertySetterMethodName ?? "<null>"));
        RecordScalarConflict(
            XamlSymbolShapeFeatures.GetterOnlyAttachedCollections,
            static policy => policy.InferGetterOnlyAttachedCollections ? "true" : "false");
        RecordScalarConflict(
            XamlSymbolShapeFeatures.NameScopeDuckTyping,
            static policy => policy.InferNameScopeFromMethods ? "true" : "false");
        RecordScalarConflict(
            XamlSymbolShapeFeatures.DesignerSerializationMethods,
            static policy =>
                (policy.InferDesignerSerializationMethods ? "true" : "false") + "|" +
                policy.ShouldSerializeMethodPrefix + "|" +
                policy.ResetMethodPrefix);
        RecordScalarConflict(
            XamlSymbolShapeFeatures.MarkupExtensionReceivers,
            static policy =>
                string.Join(",", policy.MarkupExtensionReceiverInterfaceMetadataNames) + ";" +
                string.Join(",", policy.MarkupExtensionReceiverMethodNames) + ";" +
                string.Join(",", policy.MarkupExtensionReceiverMarkupExtensionTypeMetadataNames) + ";" +
                string.Join(",", policy.MarkupExtensionReceiverServiceProviderTypeMetadataNames) + ";" +
                (policy.InferMarkupExtensionReceiversFromMethods ? "true" : "false"));
        RecordMapConflicts(
            XamlSymbolShapeFeatures.ImplicitDictionaryKeys,
            static policy => policy.ImplicitDictionaryKeyMembers,
            static value => value);
        RecordMapConflicts(
            XamlSymbolShapeFeatures.ResourceMemberRoles,
            static policy => policy.ResourceMemberRoles,
            static value => value.ToString());
        RecordMapConflicts(
            XamlSymbolShapeFeatures.PseudoContentMembers,
            static policy => policy.PseudoContentMembers,
            static value =>
                value.Name + "|" + value.ValueTypeMetadataName + "|" +
                value.Kind + "|" + value.Semantic);
        conflicts = conflictList;
        return new XamlSymbolShapePolicy(
            markupExtensionSuffixes: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionSuffixes,
                static policy => policy.MarkupExtensionSuffixes,
                "Extension"),
            collectionAddMethodNames: MergeList(
                XamlSymbolShapeFeatures.CollectionAddMethods,
                static policy => policy.CollectionAddMethodNames,
                "Add"),
            addChildInterfaceMetadataNames: MergeList(
                XamlSymbolShapeFeatures.AddChildInterfaces,
                static policy => policy.AddChildInterfaceMetadataNames),
            attachedGetterPrefix: accessor?.AttachedGetterPrefix ?? "Get",
            attachedSetterPrefix: accessor?.AttachedSetterPrefix ?? "Set",
            runtimeNameFallback: First(XamlSymbolShapeFeatures.RuntimeNameFallback)?.RuntimeNameFallback,
            inferCollectionsFromAddMethods: collectionInference?.InferCollectionsFromAddMethods ?? true,
            propertyIdentifierSuffix: propertySystem?.PropertyIdentifierSuffix,
            propertyIdentifierTypeMetadataName: propertySystem?.PropertyIdentifierTypeMetadataName,
            propertySetterMethodName: propertySystem?.PropertySetterMethodName,
            implicitDictionaryKeyMembers: MergeDictionary(
                XamlSymbolShapeFeatures.ImplicitDictionaryKeys,
                static policy => policy.ImplicitDictionaryKeyMembers),
            resourceMemberRoles: MergeDictionary(
                XamlSymbolShapeFeatures.ResourceMemberRoles,
                static policy => policy.ResourceMemberRoles),
            profileTextSyntaxTypeMetadataNames: MergeList(
                XamlSymbolShapeFeatures.ProfileTextSyntaxTypes,
                static policy => policy.ProfileTextSyntaxTypeMetadataNames),
            pseudoContentMembers: MergeDictionary(
                XamlSymbolShapeFeatures.PseudoContentMembers,
                static policy => policy.PseudoContentMembers),
            inferGetterOnlyAttachedCollections: getterOnly?.InferGetterOnlyAttachedCollections ?? false,
            markupExtensionBaseTypeMetadataNames: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionBaseTypes,
                static policy => policy.MarkupExtensionBaseTypeMetadataNames),
            markupExtensionInterfaceMetadataNames: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionInterfaces,
                static policy => policy.MarkupExtensionInterfaceMetadataNames),
            markupExtensionProvideValueMethodNames: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionCallableNames,
                static policy => policy.MarkupExtensionProvideValueMethodNames,
                "ProvideValue"),
            markupExtensionServiceProviderTypeMetadataNames: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionServiceProviderTypes,
                static policy => policy.MarkupExtensionServiceProviderTypeMetadataNames),
            markupExtensionAvailableServiceTypeMetadataNames: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionAvailableServices,
                static policy => policy.MarkupExtensionAvailableServiceTypeMetadataNames),
            requireMarkupExtensionServiceDeclaration:
                serviceDeclaration?.RequireMarkupExtensionServiceDeclaration ?? false,
            setValueHandlerEventArgsTypeMetadataNames: MergeDictionary(
                XamlSymbolShapeFeatures.SetValueHandlerEventArgs,
                static policy => policy.SetValueHandlerEventArgsTypeMetadataNames),
            valueSerializerBaseTypeMetadataNames: MergeList(
                XamlSymbolShapeFeatures.ValueSerializerBaseTypes,
                static policy => policy.ValueSerializerBaseTypeMetadataNames),
            valueSerializerContextTypeMetadataNames: MergeList(
                XamlSymbolShapeFeatures.ValueSerializerContextTypes,
                static policy => policy.ValueSerializerContextTypeMetadataNames),
            valueSerializerCanConvertToStringMethodNames: MergeList(
                XamlSymbolShapeFeatures.ValueSerializerCanConvertToStringNames,
                static policy => policy.ValueSerializerCanConvertToStringMethodNames),
            valueSerializerConvertToStringMethodNames: MergeList(
                XamlSymbolShapeFeatures.ValueSerializerConvertToStringNames,
                static policy => policy.ValueSerializerConvertToStringMethodNames),
            nameScopeInterfaceMetadataNames: MergeList(
                XamlSymbolShapeFeatures.NameScopeInterfaces,
                static policy => policy.NameScopeInterfaceMetadataNames),
            nameScopeRegisterMethodNames: MergeList(
                XamlSymbolShapeFeatures.NameScopeRegisterNames,
                static policy => policy.NameScopeRegisterMethodNames,
                "RegisterName"),
            nameScopeUnregisterMethodNames: MergeList(
                XamlSymbolShapeFeatures.NameScopeUnregisterNames,
                static policy => policy.NameScopeUnregisterMethodNames,
                "UnregisterName"),
            nameScopeFindMethodNames: MergeList(
                XamlSymbolShapeFeatures.NameScopeFindNames,
                static policy => policy.NameScopeFindMethodNames,
                "FindName",
                "FindByName"),
            inferNameScopeFromMethods:
                nameScopeDuckTyping?.InferNameScopeFromMethods ?? false,
            markupExtensionOptionSelectorMethodNames: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionOptionSelectorNames,
                static policy => policy.MarkupExtensionOptionSelectorMethodNames),
            markupExtensionOptionSelectorServiceProviderTypeMetadataNames: MergeList(
                XamlSymbolShapeFeatures.MarkupExtensionOptionSelectorServiceProviderTypes,
                static policy => policy.MarkupExtensionOptionSelectorServiceProviderTypeMetadataNames),
            inferDesignerSerializationMethods:
                designerSerializationMethods?.InferDesignerSerializationMethods ?? false,
            shouldSerializeMethodPrefix:
                designerSerializationMethods?.ShouldSerializeMethodPrefix,
            resetMethodPrefix:
                designerSerializationMethods?.ResetMethodPrefix,
            markupExtensionReceiverInterfaceMetadataNames:
                markupExtensionReceivers?.MarkupExtensionReceiverInterfaceMetadataNames,
            markupExtensionReceiverMethodNames:
                markupExtensionReceivers?.MarkupExtensionReceiverMethodNames,
            markupExtensionReceiverMarkupExtensionTypeMetadataNames:
                markupExtensionReceivers?.MarkupExtensionReceiverMarkupExtensionTypeMetadataNames,
            markupExtensionReceiverServiceProviderTypeMetadataNames:
                markupExtensionReceivers?.MarkupExtensionReceiverServiceProviderTypeMetadataNames,
            inferMarkupExtensionReceiversFromMethods:
                markupExtensionReceivers?.InferMarkupExtensionReceiversFromMethods);
    }

    private string GetShapeProviderId(XamlSymbolShapeFeatures feature)
    {
        foreach (var registration in _shapePolicies)
            if ((registration.Policy.DeclaredFeatures & feature) != 0)
                return registration.ProviderId;
        return "core.default-shapes";
    }

    public IReadOnlyList<XamlNamespaceDefinitionInfo> NamespaceDefinitions => _namespaceDefinitions;
    public IReadOnlyList<XamlNamespacePrefixInfo> NamespacePrefixes => _namespacePrefixes;
    public IReadOnlyList<XamlNamespaceCompatibilityInfo> NamespaceCompatibilities => _namespaceCompatibilities;
    public IReadOnlyList<XamlSchemaAnnotationInfo> AssemblyAnnotations => _assemblyAnnotations;
    public IReadOnlyList<XamlCompilationModeInfo> AssemblyCompilationModes => _assemblyCompilationModes;
    public IReadOnlyList<XamlCompilationModeInfo> ModuleCompilationModes => _moduleCompilationModes;
    public IReadOnlyList<XamlRootNamespaceInfo> RootNamespaces => _rootNamespaces;
    public IReadOnlyList<XamlResourceIdInfo> ResourceIdentities => _resourceIdentities;
    public IReadOnlyList<XamlSymbolShapeConflictInfo> SymbolShapeConflicts => _symbolShapeConflicts;

    public XamlDocumentBuildMetadata ResolveDocumentBuildMetadata(
        string documentPath,
        string? className)
    {
        if (documentPath == null) throw new ArgumentNullException(nameof(documentPath));

        var requestedClassName = string.IsNullOrWhiteSpace(className)
            ? null
            : className!.Trim();
        var rootNamespace = _rootNamespaces.FirstOrDefault(info =>
            SymbolEqualityComparer.Default.Equals(info.Assembly, _compilation.Assembly));
        var effectiveClassName = requestedClassName;
        INamedTypeSymbol? classType = null;
        if (requestedClassName != null)
        {
            classType = _compilation.GetTypeByMetadataName(requestedClassName);
            if (classType == null &&
                rootNamespace?.IsValid == true &&
                !string.IsNullOrWhiteSpace(rootNamespace.Namespace))
            {
                var rootedName = rootNamespace.Namespace + "." + requestedClassName;
                classType = _compilation.GetTypeByMetadataName(rootedName);
                effectiveClassName = classType == null
                    ? rootedName
                    : GetMetadataName(classType);
            }
            else if (classType != null)
            {
                effectiveClassName = GetMetadataName(classType);
            }
        }

        var errors = new List<XamlBuildMetadataIssue>();
        if (rootNamespace != null && !rootNamespace.IsValid && rootNamespace.Error != null)
            errors.Add(new XamlBuildMetadataIssue(
                XamlBuildMetadataIssueKind.RootNamespace,
                rootNamespace.Error));

        XamlCompilationModeInfo? compilationMode = null;
        XamlFilePathInfo? filePath = null;
        if (classType != null)
        {
            var typeAnnotations = GetAnnotations(
                classType,
                XamlSchemaAttributeTargets.Type,
                includeInherited: false);
            compilationMode = FindCompilationMode(
                typeAnnotations,
                XamlCompilationScope.Type);
            filePath = FindFilePath(classType, typeAnnotations);
            if (filePath != null && !filePath.IsValid && filePath.Error != null)
                errors.Add(new XamlBuildMetadataIssue(
                    XamlBuildMetadataIssueKind.FilePath,
                    filePath.Error));
        }

        if (compilationMode == null && classType?.ContainingModule != null)
        {
            compilationMode = _moduleCompilationModes.FirstOrDefault(info =>
                SymbolEqualityComparer.Default.Equals(
                    info.DeclaredOn,
                    classType.ContainingModule));
        }
        if (compilationMode == null)
        {
            var assembly = classType?.ContainingAssembly ?? _compilation.Assembly;
            compilationMode = _assemblyCompilationModes.FirstOrDefault(info =>
                SymbolEqualityComparer.Default.Equals(info.DeclaredOn, assembly));
        }
        if (compilationMode != null &&
            !compilationMode.IsValid &&
            compilationMode.Error != null)
        {
            errors.Add(new XamlBuildMetadataIssue(
                XamlBuildMetadataIssueKind.CompilationMode,
                compilationMode.Error));
        }

        var resourceCandidates = _resourceIdentities.Where(info =>
        {
            if (classType != null)
                return SymbolEqualityComparer.Default.Equals(
                    info.AssociatedType,
                    classType);
            return info.IsValid && PathsMatch(documentPath, info.Path!);
        }).ToArray();
        XamlResourceIdInfo? resourceIdentity = null;
        if (resourceCandidates.Length == 1)
        {
            resourceIdentity = resourceCandidates[0];
            if (!resourceIdentity.IsValid && resourceIdentity.Error != null)
                errors.Add(new XamlBuildMetadataIssue(
                    XamlBuildMetadataIssueKind.ResourceIdentity,
                    resourceIdentity.Error));
        }
        else if (resourceCandidates.Length > 1)
        {
            errors.Add(new XamlBuildMetadataIssue(
                XamlBuildMetadataIssueKind.ResourceIdentity,
                $"Multiple XAML resource identities match '{requestedClassName ?? documentPath}'."));
        }

        return new XamlDocumentBuildMetadata(
            documentPath,
            requestedClassName,
            effectiveClassName,
            classType,
            compilationMode,
            filePath,
            resourceIdentity,
            rootNamespace,
            errors);
    }

    public bool TryResolveDirective(
        string namespaceUri,
        string name,
        out XamlDialectDirectiveDefinition? definition) =>
        _dialectDirectives.TryGetValue(namespaceUri + "\0" + name, out definition);

    private static IReadOnlyDictionary<string, XamlDialectDirectiveDefinition> IndexDialectDirectives(
        IXamlFrameworkProfile profile)
    {
        var result = new Dictionary<string, XamlDialectDirectiveDefinition>(StringComparer.Ordinal);
        if (profile is not IXamlDialectDirectiveProvider provider) return result;
        foreach (var directive in provider.DialectDirectives)
        {
            var key = directive.NamespaceUri + "\0" + directive.Name;
            if (result.ContainsKey(key))
                throw new InvalidOperationException($"Dialect directive '{directive.NamespaceUri}:{directive.Name}' is duplicated.");
            result.Add(key, directive);
        }
        return result;
    }

    public XamlTypeInfo? ResolveType(string namespaceUri, string localName)
    {
        var cacheKey = namespaceUri + "|" + localName;
        if (_typeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var intrinsic = ResolveIntrinsic(namespaceUri, localName);
        if (intrinsic != null)
        {
            _typeCache[cacheKey] = intrinsic;
            return intrinsic;
        }

        var syntheticType = ResolveSyntheticType(namespaceUri, localName);
        if (syntheticType != null)
        {
            _typeCache[cacheKey] = syntheticType;
            return syntheticType;
        }

        var candidates = GetNamespaceCandidates(namespaceUri);
        for (var index = 0; index < candidates.Count; index++)
        {
            var symbol = ResolveCandidate(candidates[index], localName);
            if (symbol == null)
            {
                for (var suffixIndex = 0; suffixIndex < _shapePolicy.MarkupExtensionSuffixes.Count; suffixIndex++)
                {
                    var suffix = _shapePolicy.MarkupExtensionSuffixes[suffixIndex];
                    if (localName.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    symbol = ResolveCandidate(candidates[index], localName + suffix);
                    if (symbol != null) break;
                }
            }

            if (symbol != null)
            {
                var result = DescribeType(namespaceUri, localName, symbol);
                _typeCache[cacheKey] = result;
                return result;
            }
        }

        _typeCache[cacheKey] = null;
        return null;
    }

    public XamlTypeInfo? ResolveMetadataType(string metadataName)
    {
        if (string.IsNullOrWhiteSpace(metadataName)) return null;
        var symbol = _compilation.GetTypeByMetadataName(metadataName.Trim());
        if (symbol == null || symbol.TypeKind == TypeKind.Error) return null;
        return DescribeType(
            "clr-namespace:" + symbol.ContainingNamespace.ToDisplayString(),
            symbol.Name,
            symbol);
    }

    public XamlTypeInfo? ResolveSymbolType(ITypeSymbol symbol)
    {
        if (symbol == null) throw new ArgumentNullException(nameof(symbol));
        if (symbol.TypeKind == TypeKind.Error) return null;
        return DescribeType(
            "clr-namespace:" + symbol.ContainingNamespace.ToDisplayString(),
            symbol.Name,
            symbol);
    }

    public bool HasExplicitConversion(ITypeSymbol sourceType, ITypeSymbol targetType)
    {
        if (sourceType == null) throw new ArgumentNullException(nameof(sourceType));
        if (targetType == null) throw new ArgumentNullException(nameof(targetType));
        return _compilation is CSharpCompilation csharp &&
               csharp.ClassifyConversion(sourceType, targetType).Exists;
    }

    public bool IsAccessibleWithin(ISymbol symbol, ITypeSymbol withinType)
    {
        if (symbol == null) throw new ArgumentNullException(nameof(symbol));
        if (withinType == null) throw new ArgumentNullException(nameof(withinType));
        return _compilation.IsSymbolAccessibleWithin(symbol, withinType);
    }

    public XamlTypeInfo? ResolveType(
        string namespaceUri,
        string localName,
        IReadOnlyList<XamlTypeInfo> typeArguments)
    {
        if (typeArguments == null) throw new ArgumentNullException(nameof(typeArguments));
        if (typeArguments.Count == 0) return ResolveType(namespaceUri, localName);
        var cacheKey = namespaceUri + "|" + localName + "<" +
            string.Join(",", typeArguments.Select(static argument => argument.MetadataName)) + ">";
        if (_typeCache.TryGetValue(cacheKey, out var cached)) return cached;
        foreach (var candidate in GetNamespaceCandidates(namespaceUri))
        {
            var definition = ResolveCandidate(candidate, localName + "`" + typeArguments.Count);
            if (definition == null)
            {
                foreach (var suffix in _shapePolicy.MarkupExtensionSuffixes)
                {
                    definition = ResolveCandidate(candidate, localName + suffix + "`" + typeArguments.Count);
                    if (definition != null) break;
                }
            }
            if (definition == null || definition.Arity != typeArguments.Count) continue;
            var symbols = typeArguments.Select(static argument => argument.Symbol).ToArray();
            if (!SatisfiesGenericConstraints(definition, symbols)) continue;
            var constructed = definition.Construct(symbols);
            var result = DescribeType(namespaceUri, localName, constructed);
            _typeCache[cacheKey] = result;
            return result;
        }
        _typeCache[cacheKey] = null;
        return null;
    }

    public XamlMemberInfo? ResolveMember(
        XamlTypeInfo objectType,
        string memberNamespaceUri,
        string? ownerTypeName,
        string memberName)
    {
        if (_syntheticByMetadataName.TryGetValue(objectType.MetadataName, out var synthetic))
        {
            for (var index = 0; index < synthetic.Members.Count; index++)
            {
                var definition = synthetic.Members[index];
                if (!string.Equals(definition.Name, memberName, StringComparison.Ordinal)) continue;
                var valueSymbol = _compilation.GetTypeByMetadataName(definition.ValueTypeMetadataName);
                if (valueSymbol == null) return null;
                var valueType = DescribeType(memberNamespaceUri, valueSymbol.Name, valueSymbol);
                return new XamlMemberInfo(
                    definition.Name,
                    definition.Name,
                    null,
                    objectType,
                    valueType,
                    definition.Kind,
                    definition.CanWrite,
                    textSyntax: valueType.TextSyntax,
                    identity: objectType.MetadataName + "." + definition.Name);
            }
            return null;
        }

        if (!_symbolByMetadataName.TryGetValue(objectType.MetadataName, out var objectSymbol))
        {
            objectSymbol = _compilation.GetTypeByMetadataName(objectType.MetadataName);
            if (objectSymbol == null)
            {
                return null;
            }
        }

        if (TryGetPseudoContentMember(objectSymbol, memberName, out var pseudo))
        {
            var valueSymbol = _compilation.GetTypeByMetadataName(pseudo.ValueTypeMetadataName);
            if (valueSymbol == null) return null;
            var valueType = DescribeType(memberNamespaceUri, valueSymbol.Name, valueSymbol);
            return new XamlMemberInfo(
                pseudo.Name,
                pseudo.Name,
                null,
                objectType,
                valueType,
                pseudo.Kind,
                canWrite: false,
                identity: "pseudo-member:" + objectType.MetadataName + ":" + pseudo.Name,
                syntheticSemantic: pseudo.Semantic);
        }

        if (!string.IsNullOrEmpty(ownerTypeName))
        {
            var ownerMatchesObjectType = string.Equals(ownerTypeName, objectType.Name, StringComparison.Ordinal);
            var owner = ResolveType(memberNamespaceUri, ownerTypeName!);
            if (owner == null || !_symbolByMetadataName.TryGetValue(owner.MetadataName, out var ownerSymbol))
            {
                return null;
            }

            IMethodSymbol? selectedSetter = null;
            IMethodSymbol? selectedGetter = null;
            for (var current = ownerSymbol; current != null && selectedSetter == null; current = current.BaseType)
            {
                var methods = current.GetMembers(_shapePolicy.AttachedSetterPrefix + memberName);
                var applicable = new List<IMethodSymbol>();
                for (var index = 0; index < methods.Length; index++)
                {
                    if (methods[index] is IMethodSymbol method &&
                        method.IsStatic && method.Parameters.Length == 2 &&
                        method.ReturnsVoid &&
                        method.Parameters[0].RefKind == RefKind.None &&
                        method.Parameters[1].RefKind == RefKind.None &&
                        IsAccessible(method.DeclaredAccessibility) &&
                        IsAssignableTo(objectSymbol, method.Parameters[0].Type))
                    {
                        applicable.Add(method);
                    }
                }
                selectedSetter = SelectMostSpecificReceiver(applicable, objectSymbol);
                if (applicable.Count > 0 && selectedSetter == null) return null;
            }

            if (selectedSetter != null)
            {
                for (var current = ownerSymbol; current != null && selectedGetter == null; current = current.BaseType)
                {
                    var methods = current.GetMembers(_shapePolicy.AttachedGetterPrefix + memberName);
                    var applicable = new List<IMethodSymbol>();
                    for (var index = 0; index < methods.Length; index++)
                    {
                        if (methods[index] is IMethodSymbol method &&
                            method.IsStatic && method.Parameters.Length == 1 &&
                            !method.ReturnsVoid && method.RefKind == RefKind.None &&
                            method.Parameters[0].RefKind == RefKind.None &&
                            IsAccessible(method.DeclaredAccessibility) &&
                            IsAssignableTo(objectSymbol, method.Parameters[0].Type) &&
                            SymbolEqualityComparer.IncludeNullability.Equals(method.ReturnType, selectedSetter.Parameters[1].Type))
                        {
                            applicable.Add(method);
                        }
                    }
                    selectedGetter = SelectMostSpecificReceiver(applicable, objectSymbol);
                    if (applicable.Count > 0 && selectedGetter == null) return null;
                }
                if (selectedGetter == null) return null;
                var valueType = DescribeType(memberNamespaceUri, selectedSetter.Parameters[1].Type.Name, selectedSetter.Parameters[1].Type);
                var annotations = GetAttachedMemberAnnotations(selectedGetter, selectedSetter);
                return new XamlMemberInfo(
                    memberName,
                    memberName,
                    selectedSetter,
                    owner,
                    valueType,
                    XamlMemberKind.AttachableProperty,
                    true,
                    owner.CSharpName + "." + selectedSetter.Name,
                    annotations,
                    new XamlAttachedMemberShapeInfo(
                        selectedGetter,
                        selectedSetter,
                        GetShapeProviderId(XamlSymbolShapeFeatures.AttachedAccessorPrefixes)),
                    GetTextSyntax(selectedSetter.Parameters[1].Type, annotations, valueType.TextSyntax),
                    valueSerializer: FindValueSerializerShape(annotations),
                    ambient: FindBooleanSchemaInfo(
                        annotations,
                        XamlSchemaSemantics.Ambient,
                        presenceValue: true),
                    dataTypeSource: FindDataTypeSource(annotations),
                    dataTypeInheritance: FindDataTypeInheritance(annotations),
                    itemsDataTypeInheritance: FindItemsDataTypeInheritance(annotations),
                    bindingAssignment: FindBindingAssignment(annotations),
                    attachedPropertyBrowseRules:
                        FindAttachedPropertyBrowseRules(annotations, selectedGetter),
                    defaultValue: FindDefaultValue(annotations),
                    designerSerializationVisibility:
                        FindDesignerSerializationVisibility(annotations),
                    designerSerializationOptions:
                        FindDesignerSerializationOptions(annotations),
                    browsable: FindBrowsable(
                        annotations,
                        XamlSchemaSemantics.Browsable,
                        parameterlessDefault: null),
                    editorBrowsable: FindEditorBrowsable(annotations),
                    localizability: FindLocalizability(annotations));
            }

            if (_shapePolicy.InferGetterOnlyAttachedCollections)
            {
                for (var current = ownerSymbol; current != null; current = current.BaseType)
                {
                    var getters = new List<IMethodSymbol>();
                    foreach (var candidate in current.GetMembers(_shapePolicy.AttachedGetterPrefix + memberName))
                    {
                        if (candidate is IMethodSymbol method && method.IsStatic &&
                            method.Parameters.Length == 1 && !method.ReturnsVoid &&
                            method.Parameters[0].RefKind == RefKind.None &&
                            IsAccessible(method.DeclaredAccessibility) &&
                            IsAssignableTo(objectSymbol, method.Parameters[0].Type))
                            getters.Add(method);
                    }
                    if (getters.Count == 0) continue;
                    var getter = SelectMostSpecificReceiver(getters, objectSymbol);
                    if (getter == null) return null;
                    var valueType = DescribeType(memberNamespaceUri, getter.ReturnType.Name, getter.ReturnType);
                    if (!valueType.IsCollection && !valueType.IsDictionary) return null;
                    var kind = valueType.IsDictionary ? XamlMemberKind.Dictionary : XamlMemberKind.Collection;
                    var annotations = GetAnnotations(
                        getter,
                        XamlSchemaAttributeTargets.Member,
                        includeInherited: false);
                    return new XamlMemberInfo(
                        memberName,
                        memberName,
                        getter,
                        owner,
                        valueType,
                        kind,
                        canWrite: false,
                        annotations: annotations,
                        attachableShape: new XamlAttachedMemberShapeInfo(
                            getter,
                            null,
                            GetShapeProviderId(XamlSymbolShapeFeatures.GetterOnlyAttachedCollections)),
                        textSyntax: valueType.TextSyntax,
                        valueSerializer: FindValueSerializerShape(annotations),
                        ambient: FindBooleanSchemaInfo(
                            annotations,
                            XamlSchemaSemantics.Ambient,
                            presenceValue: true),
                        dataTypeSource: FindDataTypeSource(annotations),
                        dataTypeInheritance: FindDataTypeInheritance(annotations),
                        itemsDataTypeInheritance: FindItemsDataTypeInheritance(annotations),
                        bindingAssignment: FindBindingAssignment(annotations),
                        attachedPropertyBrowseRules:
                            FindAttachedPropertyBrowseRules(annotations, getter),
                        defaultValue: FindDefaultValue(annotations),
                        designerSerializationVisibility:
                            FindDesignerSerializationVisibility(annotations),
                        designerSerializationOptions:
                            FindDesignerSerializationOptions(annotations),
                        browsable: FindBrowsable(
                            annotations,
                            XamlSchemaSemantics.Browsable,
                            parameterlessDefault: null),
                        editorBrowsable: FindEditorBrowsable(annotations),
                        localizability: FindLocalizability(annotations));
                }
            }

            if (!ownerMatchesObjectType) return null;
        }

        for (var current = objectSymbol; current != null; current = current.BaseType)
        {
            var members = current.GetMembers(memberName);
            for (var index = 0; index < members.Length; index++)
            {
                if (members[index] is IPropertySymbol property && IsAccessible(property.DeclaredAccessibility))
                {
                    var valueType = DescribeType(memberNamespaceUri, property.Type.Name, property.Type);
                    var annotations = GetAnnotations(property, XamlSchemaAttributeTargets.Member, includeInherited: true);
                    var canWrite = property.SetMethod != null && IsAccessible(property.SetMethod.DeclaredAccessibility);
                    var deferringLoader = FindDeferringLoaderShape(annotations);
                    var defaultValue = FindDefaultValue(annotations);
                    var serializationMethods =
                        FindDesignerSerializationMethods(property, defaultValue);
                    var kind = deferringLoader != null || valueType.DeferringLoader != null
                        ? XamlMemberKind.DeferredContent
                        : valueType.IsDictionary
                        ? XamlMemberKind.Dictionary
                        : valueType.IsCollection
                            ? XamlMemberKind.Collection
                            : XamlMemberKind.Property;
                    return new XamlMemberInfo(
                        memberName,
                        property.Name,
                        property,
                        objectType,
                        valueType,
                        kind,
                        canWrite,
                        annotations: annotations,
                        textSyntax: GetTextSyntax(property.Type, annotations, valueType.TextSyntax),
                        propertySystemShape: FindPropertySystemShape(objectSymbol, property),
                        resourceRole: GetResourceMemberRole(property.Name),
                        valueSerializer: FindValueSerializerShape(annotations),
                        constructorArgument: FindConstructorArgumentShape(
                            property.ContainingType,
                            property,
                            annotations),
                        ambient: FindBooleanSchemaInfo(
                            annotations,
                            XamlSchemaSemantics.Ambient,
                            presenceValue: true),
                        deferringLoader: deferringLoader,
                        markupExtensionBracketCharacters:
                            FindMarkupExtensionBracketPairs(annotations),
                        dependencies: FindMemberDependencies(
                            property.ContainingType,
                            annotations),
                        markupExtensionOption:
                            FindMarkupExtensionOption(annotations),
                        dataTypeSource: FindDataTypeSource(annotations),
                        dataTypeInheritance: FindDataTypeInheritance(annotations),
                        itemsDataTypeInheritance: FindItemsDataTypeInheritance(annotations),
                        bindingAssignment: FindBindingAssignment(annotations),
                        defaultValue: defaultValue,
                        designerSerializationVisibility:
                            FindDesignerSerializationVisibility(annotations),
                        designerSerializationOptions:
                            FindDesignerSerializationOptions(annotations),
                        browsable: FindBrowsable(
                            annotations,
                            XamlSchemaSemantics.Browsable,
                            parameterlessDefault: null),
                        editorBrowsable: FindEditorBrowsable(annotations),
                        localizability: FindLocalizability(annotations),
                        serializationMethods: serializationMethods);
                }

                if (members[index] is IEventSymbol @event && IsAccessible(@event.DeclaredAccessibility))
                {
                    var annotations = GetAnnotations(
                        @event,
                        XamlSchemaAttributeTargets.Member,
                        includeInherited: true);
                    return new XamlMemberInfo(
                        memberName,
                        @event.Name,
                        @event,
                        objectType,
                        DescribeType(memberNamespaceUri, @event.Type.Name, @event.Type),
                        XamlMemberKind.Event,
                        true,
                        annotations: annotations,
                        defaultValue: FindDefaultValue(annotations),
                        designerSerializationVisibility:
                            FindDesignerSerializationVisibility(annotations),
                        designerSerializationOptions:
                            FindDesignerSerializationOptions(annotations),
                        browsable: FindBrowsable(
                            annotations,
                            XamlSchemaSemantics.Browsable,
                            parameterlessDefault: null),
                        editorBrowsable: FindEditorBrowsable(annotations),
                        localizability: FindLocalizability(annotations));
                }
            }
        }

        // WinRT XAML permits an attached member to omit its owner qualifier when
        // it is applied to an instance of that owner (for example a ScrollViewer
        // setting one of ScrollViewer's own attached scrolling properties).
        if (string.IsNullOrEmpty(ownerTypeName))
            return ResolveMember(objectType, memberNamespaceUri, objectType.Name, memberName);

        return null;
    }

    private IReadOnlyList<XamlSchemaAnnotationInfo> GetAttachedMemberAnnotations(
        IMethodSymbol getter,
        IMethodSymbol? setter)
    {
        var result = new List<XamlSchemaAnnotationInfo>();
        AddAccessorAnnotations(
            result,
            GetAnnotations(getter, XamlSchemaAttributeTargets.Member, includeInherited: false));
        if (setter != null)
            AddAccessorAnnotations(
                result,
                GetAnnotations(setter, XamlSchemaAttributeTargets.Member, includeInherited: false));
        return result;
    }

    private static void AddAccessorAnnotations(
        List<XamlSchemaAnnotationInfo> result,
        IReadOnlyList<XamlSchemaAnnotationInfo> additions)
    {
        foreach (var annotation in additions)
        {
            var duplicate = result.Any(existing =>
                string.Equals(existing.Semantic, annotation.Semantic, StringComparison.Ordinal) &&
                string.Equals(existing.Value, annotation.Value, StringComparison.Ordinal) &&
                existing.ProviderPriority == annotation.ProviderPriority);
            if (!duplicate) result.Add(annotation);
        }
    }

    private static XamlConstructorArgumentShapeInfo? FindConstructorArgumentShape(
        INamedTypeSymbol declaringType,
        IPropertySymbol property,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var annotation = annotations.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Semantic,
                XamlSchemaSemantics.ConstructorArgument,
                StringComparison.Ordinal));
        if (annotation == null) return null;
        var argumentName = annotation.ValueConstant?.Value as string ?? string.Empty;
        var candidates = declaringType.InstanceConstructors
            .Where(candidate =>
                candidate.DeclaredAccessibility == Accessibility.Public &&
                candidate.Parameters.Length == 1 &&
                candidate.Parameters[0].RefKind == RefKind.None &&
                string.Equals(
                    candidate.Parameters[0].Name,
                    argumentName,
                    StringComparison.Ordinal))
            .OrderBy(static candidate => candidate.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();
        string? error = null;
        IMethodSymbol? constructor = null;
        IParameterSymbol? parameter = null;
        if (argumentName.Length == 0)
        {
            error = "ConstructorArgumentAttribute must name a non-empty constructor parameter.";
        }
        else if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                 property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
        {
            error = "Property '" + property.ToDisplayString() +
                "' must have public get and set accessors for constructor-argument serialization.";
        }
        else
        {
            var matches = candidates.Where(candidate =>
                SymbolEqualityComparer.Default.Equals(
                    candidate.Parameters[0].Type,
                    property.Type)).ToArray();
            if (matches.Length == 1)
            {
                constructor = matches[0];
                parameter = constructor.Parameters[0];
            }
            else if (matches.Length == 0)
            {
                error = "Type '" + declaringType.ToDisplayString() +
                    "' has no public one-argument constructor parameter named '" +
                    argumentName + "' with property type '" +
                    property.Type.ToDisplayString() + "'.";
            }
            else
            {
                error = "Constructor argument '" + argumentName + "' on type '" +
                    declaringType.ToDisplayString() + "' is ambiguous.";
            }
        }
        return new XamlConstructorArgumentShapeInfo(
            annotation,
            argumentName,
            constructor,
            parameter,
            candidates,
            annotation.ProviderId,
            error);
    }

    private static IReadOnlyList<XamlMemberDependencyInfo> FindMemberDependencies(
        INamedTypeSymbol declaringType,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var result = new List<XamlMemberDependencyInfo>();
        foreach (var annotation in annotations)
        {
            if (!string.Equals(
                    annotation.Semantic,
                    XamlSchemaSemantics.DependsOn,
                    StringComparison.Ordinal))
                continue;

            var declaredName = annotation.ValueConstant?.Value as string;
            if (string.IsNullOrWhiteSpace(declaredName) ||
                declaredName!.IndexOf('.') >= 0)
            {
                result.Add(new XamlMemberDependencyInfo(
                    annotation,
                    declaredName,
                    null,
                    "A dependency must be a non-empty simple unqualified property name."));
                continue;
            }

            var candidates = declaringType.GetMembers(declaredName)
                .OfType<IPropertySymbol>()
                .Where(static property =>
                    !property.IsStatic &&
                    property.DeclaredAccessibility == Accessibility.Public)
                .ToArray();
            result.Add(candidates.Length == 1
                ? new XamlMemberDependencyInfo(
                    annotation,
                    declaredName,
                    candidates[0],
                    error: null)
                : new XamlMemberDependencyInfo(
                    annotation,
                    declaredName,
                    candidates.FirstOrDefault(),
                    candidates.Length == 0
                        ? $"Dependency '{declaredName}' is not a public instance property declared on '{GetMetadataName(declaringType)}'."
                        : $"Dependency '{declaredName}' is ambiguous on '{GetMetadataName(declaringType)}'."));
        }
        return result;
    }

    private static XamlDataTypeSourceInfo? FindDataTypeSource(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.DataType,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        var annotation = matches[0];
        var property = annotation.DeclaredOn as IPropertySymbol;
        string? error = null;
        if (matches.Length != 1)
            error = "Data-type source metadata is ambiguous at the winning precedence.";
        else if (property == null)
            error = "Data-type source metadata must be declared on a property.";
        else if (property.IsStatic ||
                 property.GetMethod == null ||
                 !IsAccessible(property.DeclaredAccessibility) ||
                 !IsAccessible(property.GetMethod.DeclaredAccessibility))
            error = "A data-type source must be a readable public instance property.";
        return new XamlDataTypeSourceInfo(annotation, property, error);
    }

    private static XamlDataTypeInheritanceInfo? FindDataTypeInheritance(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.InheritDataType,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        var annotation = matches[0];
        var target = annotation.DeclaredOn is IPropertySymbol or IParameterSymbol
            ? annotation.DeclaredOn
            : null;
        XamlDataTypeScopeKind? scopeKind = annotation.ValueConstant?.Value switch
        {
            int value when value == (int)XamlDataTypeScopeKind.Style =>
                XamlDataTypeScopeKind.Style,
            int value when value == (int)XamlDataTypeScopeKind.ControlTemplate =>
                XamlDataTypeScopeKind.ControlTemplate,
            _ => null
        };
        string? error = null;
        if (matches.Length != 1)
            error = "Data-type inheritance metadata is ambiguous at the winning precedence.";
        else if (target == null)
            error = "Data-type inheritance metadata must be declared on a property or constructor parameter.";
        else if (annotation.Attribute.ConstructorArguments.Length != 1 ||
                 !annotation.ValueConstant.HasValue ||
                 annotation.ValueConstant.Value.Kind == TypedConstantKind.Error)
            error = "Data-type inheritance metadata requires one scope-kind enum constant.";
        else if (!scopeKind.HasValue)
            error = "Data-type inheritance scope kind must be Style (1) or ControlTemplate (2).";
        return new XamlDataTypeInheritanceInfo(
            annotation,
            target,
            scopeKind,
            annotation.ValueConstant,
            error);
    }

    private static XamlItemsDataTypeInheritanceInfo? FindItemsDataTypeInheritance(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.InheritDataTypeFromItems,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        var annotation = matches[0];
        var target = annotation.DeclaredOn as IPropertySymbol;
        var propertyName = annotation.ValueConstant?.Value as string;
        ITypeSymbol? declaredAncestorType = null;
        foreach (var argument in annotation.Attribute.NamedArguments)
            if (string.Equals(argument.Key, "AncestorType", StringComparison.Ordinal))
                declaredAncestorType = argument.Value.Value as ITypeSymbol;
        var lookupType = declaredAncestorType as INamedTypeSymbol ??
            target?.ContainingType;
        IPropertySymbol? itemsProperty = null;
        if (lookupType != null && !string.IsNullOrWhiteSpace(propertyName))
        {
            for (var current = lookupType; current != null; current = current.BaseType)
            {
                var candidates = current.GetMembers(propertyName!)
                    .OfType<IPropertySymbol>()
                    .Where(static property =>
                        !property.IsStatic &&
                        property.GetMethod != null &&
                        property.DeclaredAccessibility == Accessibility.Public &&
                        property.GetMethod.DeclaredAccessibility == Accessibility.Public)
                    .ToArray();
                if (candidates.Length == 1)
                {
                    itemsProperty = candidates[0];
                    break;
                }
                if (candidates.Length > 1) break;
            }
        }

        string? error = null;
        if (matches.Length != 1)
            error = "Item data-type inheritance metadata is ambiguous at the winning precedence.";
        else if (target == null)
            error = "Item data-type inheritance metadata must be declared on a property.";
        else if (annotation.Attribute.ConstructorArguments.Length != 1 ||
                 string.IsNullOrWhiteSpace(propertyName) ||
                 propertyName!.IndexOf('.') >= 0)
            error = "Item data-type inheritance requires one non-empty simple ancestor property name.";
        else if (declaredAncestorType != null &&
                 declaredAncestorType is not INamedTypeSymbol)
            error = "AncestorType must name a class, struct, interface, or record type.";
        else if (lookupType == null)
            error = "The ancestor lookup type could not be resolved.";
        else if (itemsProperty == null)
            error = "Ancestor items property '" + propertyName +
                "' is not a readable public instance property on '" +
                GetMetadataName(lookupType) + "'.";
        return new XamlItemsDataTypeInheritanceInfo(
            annotation,
            target,
            propertyName,
            declaredAncestorType,
            lookupType,
            itemsProperty,
            error);
    }

    private static XamlBindingAssignmentInfo? FindBindingAssignment(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.AssignBinding,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        var annotation = matches[0];
        var target = annotation.DeclaredOn is IPropertySymbol or IMethodSymbol
            ? annotation.DeclaredOn
            : null;
        string? error = null;
        if (matches.Length != 1)
            error = "Binding-assignment metadata is ambiguous at the winning precedence.";
        else if (target == null)
            error = "Binding-assignment metadata must be declared on a property or method.";
        return new XamlBindingAssignmentInfo(annotation, target, error);
    }

    private static IReadOnlyList<XamlAttachedPropertyBrowseRuleInfo>
        FindAttachedPropertyBrowseRules(
            IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
            IMethodSymbol getter)
    {
        var result = new List<XamlAttachedPropertyBrowseRuleInfo>();
        foreach (var annotation in annotations)
        {
            if (!string.Equals(
                    annotation.Semantic,
                    XamlSchemaSemantics.AttachedPropertyBrowseRule,
                    StringComparison.Ordinal))
                continue;
            var attributeName = annotation.Attribute.AttributeClass == null
                ? string.Empty
                : GetMetadataName(annotation.Attribute.AttributeClass);
            var kind = attributeName.EndsWith(
                    ".AttachedPropertyBrowsableForChildrenAttribute",
                    StringComparison.Ordinal)
                ? XamlAttachedPropertyBrowseRuleKind.Children
                : attributeName.EndsWith(
                    ".AttachedPropertyBrowsableWhenAttributePresentAttribute",
                    StringComparison.Ordinal)
                    ? XamlAttachedPropertyBrowseRuleKind.AttributePresent
                    : XamlAttachedPropertyBrowseRuleKind.TargetType;
            ITypeSymbol? constraintType = null;
            if (kind != XamlAttachedPropertyBrowseRuleKind.Children &&
                annotation.Attribute.ConstructorArguments.Length == 1)
                constraintType =
                    annotation.Attribute.ConstructorArguments[0].Value as ITypeSymbol;
            var includeDescendants =
                GetNamedConstant(annotation.Attribute, "IncludeDescendants")?.Value
                    is true;
            string? error = null;
            if (!SymbolEqualityComparer.Default.Equals(
                    annotation.DeclaredOn,
                    getter))
                error = "Attached-property browse metadata must be declared on the getter accessor.";
            else if (!getter.IsStatic ||
                     getter.Parameters.Length != 1 ||
                     getter.ReturnsVoid ||
                     getter.DeclaredAccessibility != Accessibility.Public)
                error = "Attached-property browse metadata requires a public static one-parameter getter.";
            else if (kind != XamlAttachedPropertyBrowseRuleKind.Children &&
                     (annotation.Attribute.ConstructorArguments.Length != 1 ||
                      constraintType == null ||
                      constraintType.TypeKind == TypeKind.Error))
                error = kind == XamlAttachedPropertyBrowseRuleKind.TargetType
                    ? "A target-type browse rule requires one resolvable type."
                    : "An attribute-presence browse rule requires one resolvable attribute type.";
            result.Add(new XamlAttachedPropertyBrowseRuleInfo(
                annotation,
                kind,
                getter,
                constraintType,
                includeDescendants,
                error));
        }
        return result;
    }

    private static XamlMarkupExtensionOptionInfo? FindMarkupExtensionOption(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.MarkupExtensionOption,
                StringComparison.Ordinal) ||
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.MarkupExtensionDefaultOption,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
            return null;

        var annotation = matches[0];
        var isDefault = string.Equals(
            annotation.Semantic,
            XamlSchemaSemantics.MarkupExtensionDefaultOption,
            StringComparison.Ordinal);
        var priority = 0;
        foreach (var argument in annotation.Attribute.NamedArguments)
        {
            if (!string.Equals(argument.Key, "Priority", StringComparison.Ordinal))
                continue;
            if (argument.Value.Value is int value)
                priority = value;
            else
                return new XamlMarkupExtensionOptionInfo(
                    annotation,
                    isDefault,
                    annotation.ValueConstant,
                    priority,
                    "Markup-extension option Priority must be an Int32 constant.");
        }
        if (matches.Length != 1)
            return new XamlMarkupExtensionOptionInfo(
                annotation,
                isDefault,
                annotation.ValueConstant,
                priority,
                "A markup-extension option property cannot be both a default and a keyed option.");
        if (isDefault)
        {
            return annotation.Attribute.ConstructorArguments.Length == 0
                ? new XamlMarkupExtensionOptionInfo(
                    annotation,
                    true,
                    null,
                    priority,
                    error: null)
                : new XamlMarkupExtensionOptionInfo(
                    annotation,
                    true,
                    null,
                    priority,
                    "The default-option attribute must not declare an option value.");
        }
        if (annotation.Attribute.ConstructorArguments.Length != 1 ||
            !annotation.ValueConstant.HasValue ||
            annotation.ValueConstant.Value.Kind == TypedConstantKind.Error ||
            annotation.ValueConstant.Value.Kind == TypedConstantKind.Array ||
            annotation.ValueConstant.Value.IsNull)
            return new XamlMarkupExtensionOptionInfo(
                annotation,
                false,
                annotation.ValueConstant,
                priority,
                "A markup-extension option requires one non-null scalar constant value.");
        return new XamlMarkupExtensionOptionInfo(
            annotation,
            false,
            annotation.ValueConstant,
            priority,
            error: null);
    }

    private XamlMarkupExtensionOptionSelectorShapeInfo? FindMarkupExtensionOptionSelectorShape(
        INamedTypeSymbol type)
    {
        var options = new List<XamlMarkupExtensionOptionInfo>();
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                var option = FindMarkupExtensionOption(
                    GetAnnotations(
                        property,
                        XamlSchemaAttributeTargets.Member,
                        includeInherited: true));
                if (option != null) options.Add(option);
            }
        }
        if (options.Count == 0) return null;

        var registrations = _shapePolicies.Where(registration =>
            (registration.Policy.DeclaredFeatures &
             XamlSymbolShapeFeatures.MarkupExtensionOptionSelectorNames) != 0).ToArray();
        if (registrations.Length == 0) return null;

        var matches = new List<(
            ShapePolicyRegistration Registration,
            IMethodSymbol[] Candidates,
            IMethodSymbol[] Valid)>();
        foreach (var registration in registrations)
        {
            var policy = registration.Policy;
            var candidates = policy.MarkupExtensionOptionSelectorMethodNames
                .SelectMany(name => GetNearestNamedMethods(type, name))
                .GroupBy(
                    method => method.GetDocumentationCommentId() ?? method.ToDisplayString(),
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0) continue;
            var valid = candidates.Where(method =>
                IsMarkupExtensionOptionSelectorCandidate(method, policy, options)).ToArray();
            matches.Add((registration, candidates, valid));
        }

        if (matches.Count == 0)
        {
            var registration = registrations[0];
            return new XamlMarkupExtensionOptionSelectorShapeInfo(
                null,
                Array.Empty<IMethodSymbol>(),
                options,
                null,
                null,
                registration.ProviderId,
                $"No registered static option-selector method was found for option-bearing markup extension '{GetMetadataName(type)}'.");
        }

        var priority = matches[0].Registration.Priority;
        var winners = matches.Where(match =>
            match.Registration.Priority == priority).ToArray();
        var candidatesAtPriority = winners
            .SelectMany(match => match.Candidates)
            .GroupBy(
                method => method.GetDocumentationCommentId() ?? method.ToDisplayString(),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();
        var validAtPriority = winners
            .SelectMany(match => match.Valid)
            .GroupBy(
                method => method.GetDocumentationCommentId() ?? method.ToDisplayString(),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var providerId = string.Join(
            ", ",
            winners.Select(match => match.Registration.ProviderId)
                .Distinct(StringComparer.Ordinal));
        if (validAtPriority.Length == 0)
            return new XamlMarkupExtensionOptionSelectorShapeInfo(
                null,
                candidatesAtPriority,
                options,
                null,
                null,
                providerId,
                "Option selector requires one accessible static Boolean method with an option parameter and, optionally, a registered service-provider parameter; every keyed option constant must be implicitly convertible to the option parameter type.");

        var preferredArity = validAtPriority.Any(static method =>
            method.Parameters.Length == 2) ? 2 : 1;
        var preferred = validAtPriority.Where(method =>
            method.Parameters.Length == preferredArity).ToArray();
        var selected = SelectMostSpecificOptionSelector(preferred);
        if (selected == null)
            return new XamlMarkupExtensionOptionSelectorShapeInfo(
                null,
                candidatesAtPriority,
                options,
                null,
                null,
                providerId,
                $"Option selector is ambiguous between {string.Join(
                    ", ",
                    preferred.Select(static method => method.ToDisplayString()))}.");

        return new XamlMarkupExtensionOptionSelectorShapeInfo(
            selected,
            candidatesAtPriority,
            options,
            selected.Parameters[selected.Parameters.Length - 1].Type,
            selected.Parameters.Length == 2 ? selected.Parameters[0].Type : null,
            providerId,
            error: null);
    }

    private bool IsMarkupExtensionOptionSelectorCandidate(
        IMethodSymbol method,
        XamlSymbolShapePolicy policy,
        IReadOnlyList<XamlMarkupExtensionOptionInfo> options)
    {
        if (!policy.MarkupExtensionOptionSelectorMethodNames.Contains(
                method.Name,
                StringComparer.Ordinal) ||
            !method.IsStatic ||
            method.IsGenericMethod ||
            method.MethodKind != MethodKind.Ordinary ||
            method.DeclaredAccessibility != Accessibility.Public ||
            method.ReturnType.SpecialType != SpecialType.System_Boolean ||
            method.Parameters.Length < 1 ||
            method.Parameters.Length > 2 ||
            method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
            return false;
        if (method.Parameters.Length == 2 &&
            !policy.MarkupExtensionOptionSelectorServiceProviderTypeMetadataNames.Contains(
                GetMetadataName(method.Parameters[0].Type),
                StringComparer.Ordinal))
            return false;

        var optionType = method.Parameters[method.Parameters.Length - 1].Type;
        foreach (var option in options)
        {
            if (!option.IsValid ||
                option.IsDefault ||
                !option.OptionValue.HasValue ||
                option.OptionValue.Value.Type == null)
                continue;
            if (!HasImplicitConversion(option.OptionValue.Value.Type, optionType))
                return false;
        }
        return true;
    }

    private IMethodSymbol? SelectMostSpecificOptionSelector(
        IReadOnlyList<IMethodSymbol> candidates)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        IMethodSymbol? best = null;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var candidateType = candidate.Parameters[candidate.Parameters.Length - 1].Type;
            var dominated = false;
            for (var otherIndex = 0; otherIndex < candidates.Count; otherIndex++)
            {
                if (candidateIndex == otherIndex) continue;
                var other = candidates[otherIndex];
                var otherType = other.Parameters[other.Parameters.Length - 1].Type;
                if (HasImplicitConversion(otherType, candidateType) &&
                    !HasImplicitConversion(candidateType, otherType))
                {
                    dominated = true;
                    break;
                }
            }
            if (dominated) continue;
            if (best != null) return null;
            best = candidate;
        }
        return best;
    }

    private IMethodSymbol? SelectMostSpecificReceiver(
        IReadOnlyList<IMethodSymbol> candidates,
        ITypeSymbol receiverType)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        IMethodSymbol? exact = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (!SymbolEqualityComparer.IncludeNullability.Equals(
                    candidates[index].Parameters[0].Type, receiverType)) continue;
            if (exact != null) return null;
            exact = candidates[index];
        }
        if (exact != null) return exact;

        IMethodSymbol? best = null;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidateParameter = candidates[candidateIndex].Parameters[0].Type;
            var isDominated = false;
            for (var otherIndex = 0; otherIndex < candidates.Count; otherIndex++)
            {
                if (otherIndex == candidateIndex) continue;
                var otherParameter = candidates[otherIndex].Parameters[0].Type;
                if (HasImplicitConversion(otherParameter, candidateParameter) &&
                    !HasImplicitConversion(candidateParameter, otherParameter))
                {
                    isDominated = true;
                    break;
                }
            }
            if (isDominated) continue;
            if (best != null) return null;
            best = candidates[candidateIndex];
        }
        return best;
    }

    private bool HasImplicitConversion(ITypeSymbol source, ITypeSymbol target)
    {
        if (SymbolEqualityComparer.IncludeNullability.Equals(source, target)) return true;
        if (_compilation is CSharpCompilation csharpCompilation)
            return csharpCompilation.ClassifyConversion(source, target).IsImplicit;
        return IsAssignableTo(source, target);
    }

    private bool TryGetPseudoContentMember(
        INamedTypeSymbol objectSymbol,
        string memberName,
        out XamlPseudoMemberDefinition definition)
    {
        for (var current = objectSymbol; current != null; current = current.BaseType)
        {
            if (!_shapePolicy.PseudoContentMembers.TryGetValue(GetMetadataName(current), out var candidate) ||
                !string.Equals(candidate.Name, memberName, StringComparison.Ordinal)) continue;
            definition = candidate;
            return true;
        }
        definition = null!;
        return false;
    }

    private XamlResourceMemberRole GetResourceMemberRole(string memberName) =>
        _shapePolicy.ResourceMemberRoles.TryGetValue(memberName, out var role)
            ? role
            : XamlResourceMemberRole.None;

    private XamlPropertySystemShapeInfo? FindPropertySystemShape(
        INamedTypeSymbol receiverType,
        IPropertySymbol property)
    {
        var identifierSuffix = _shapePolicy.PropertyIdentifierSuffix;
        var identifierTypeName = _shapePolicy.PropertyIdentifierTypeMetadataName;
        var setterName = _shapePolicy.PropertySetterMethodName;
        if (string.IsNullOrEmpty(identifierSuffix) ||
            string.IsNullOrEmpty(identifierTypeName) ||
            string.IsNullOrEmpty(setterName)) return null;

        var identifierType = _compilation.GetTypeByMetadataName(identifierTypeName!);
        if (identifierType == null) return null;
        var identifiers = new List<ISymbol>();
        foreach (var candidate in property.ContainingType.GetMembers(property.Name + identifierSuffix))
        {
            if (candidate is IFieldSymbol field && field.IsStatic &&
                IsAccessible(field.DeclaredAccessibility) &&
                SymbolEqualityComparer.Default.Equals(field.Type, identifierType))
            {
                identifiers.Add(field);
            }
            else if (candidate is IPropertySymbol identifierProperty && identifierProperty.IsStatic &&
                     identifierProperty.GetMethod != null &&
                     IsAccessible(identifierProperty.GetMethod.DeclaredAccessibility) &&
                     SymbolEqualityComparer.Default.Equals(identifierProperty.Type, identifierType))
            {
                identifiers.Add(identifierProperty);
            }
        }
        if (identifiers.Count != 1) return null;

        IMethodSymbol? setter = null;
        for (var current = receiverType; current != null && setter == null; current = current.BaseType)
        {
            var candidates = new List<IMethodSymbol>();
            foreach (var candidate in current.GetMembers(setterName!))
            {
                if (candidate is IMethodSymbol method && !method.IsStatic && method.ReturnsVoid &&
                    method.Parameters.Length == 2 &&
                    method.Parameters[0].RefKind == RefKind.None &&
                    method.Parameters[1].RefKind == RefKind.None &&
                    IsAccessible(method.DeclaredAccessibility) &&
                    SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, identifierType) &&
                    method.Parameters[1].Type.SpecialType == SpecialType.System_Object)
                {
                    candidates.Add(method);
                }
            }
            if (candidates.Count > 1) return null;
            if (candidates.Count == 1) setter = candidates[0];
        }
        return setter == null
            ? null
            : new XamlPropertySystemShapeInfo(
                identifiers[0],
                setter,
                GetShapeProviderId(XamlSymbolShapeFeatures.PropertySystem));
    }

    public bool IsAssignable(XamlTypeInfo sourceType, XamlTypeInfo targetType)
    {
        if (sourceType == null) throw new ArgumentNullException(nameof(sourceType));
        if (targetType == null) throw new ArgumentNullException(nameof(targetType));
        if (SymbolEqualityComparer.IncludeNullability.Equals(sourceType.Symbol, targetType.Symbol)) return true;
        if (_compilation is CSharpCompilation csharpCompilation)
            return csharpCompilation.ClassifyConversion(sourceType.Symbol, targetType.Symbol).IsImplicit;
        return IsAssignableTo(sourceType.Symbol, targetType.Symbol);
    }

    public bool IsAssignable(ITypeSymbol sourceType, ITypeSymbol targetType)
    {
        if (sourceType == null) throw new ArgumentNullException(nameof(sourceType));
        if (targetType == null) throw new ArgumentNullException(nameof(targetType));
        if (SymbolEqualityComparer.IncludeNullability.Equals(sourceType, targetType)) return true;
        if (_compilation is CSharpCompilation csharpCompilation)
            return csharpCompilation.ClassifyConversion(sourceType, targetType).IsImplicit;
        return IsAssignableTo(sourceType, targetType);
    }

    private bool SatisfiesGenericConstraints(INamedTypeSymbol definition, IReadOnlyList<ITypeSymbol> arguments)
    {
        for (var index = 0; index < definition.TypeParameters.Length; index++)
        {
            var parameter = definition.TypeParameters[index];
            var argument = arguments[index];
            if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType) return false;
            if ((parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint) && !argument.IsValueType) return false;
            if (parameter.HasNotNullConstraint && argument.NullableAnnotation == NullableAnnotation.Annotated) return false;
            if (parameter.HasConstructorConstraint && !argument.IsValueType)
            {
                if (argument is not INamedTypeSymbol named || named.IsAbstract ||
                    !named.InstanceConstructors.Any(static constructor =>
                        constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public)) return false;
            }
            foreach (var constraint in parameter.ConstraintTypes)
            {
                if (constraint.TypeKind == TypeKind.TypeParameter) continue;
                if (_compilation is CSharpCompilation csharp)
                {
                    if (!csharp.ClassifyConversion(argument, constraint).IsImplicit) return false;
                }
                else if (!IsAssignableTo(argument, constraint)) return false;
            }
        }
        return true;
    }

    private IReadOnlyList<NamespaceCandidate> GetNamespaceCandidates(string namespaceUri)
    {
        const string usingPrefix = "using:";
        const string clrPrefix = "clr-namespace:";
        if (namespaceUri.StartsWith(usingPrefix, StringComparison.Ordinal))
        {
            return new[] { new NamespaceCandidate(namespaceUri.Substring(usingPrefix.Length), null) };
        }

        if (namespaceUri.StartsWith(clrPrefix, StringComparison.Ordinal))
        {
            var value = namespaceUri.Substring(clrPrefix.Length);
            var separator = value.IndexOf(';');
            var clrNamespace = separator < 0 ? value : value.Substring(0, separator);
            IAssemblySymbol? assembly = null;
            if (separator >= 0)
            {
                const string assemblyPrefix = "assembly=";
                var qualifier = value.Substring(separator + 1).Trim();
                if (qualifier.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    assembly = FindAssembly(qualifier.Substring(assemblyPrefix.Length));
                }
            }
            return new[] { new NamespaceCandidate(clrNamespace, assembly) };
        }

        var result = new List<NamespaceCandidate>();
        AddAssemblyNamespaceCandidates(namespaceUri, result, new HashSet<string>(StringComparer.Ordinal));
        foreach (var clrNamespace in _profile.GetClrNamespaceCandidates(namespaceUri))
        {
            AddCandidate(result, new NamespaceCandidate(clrNamespace, null));
        }
        return result;
    }

    private INamedTypeSymbol? ResolveCandidate(NamespaceCandidate candidate, string localName)
    {
        var metadataName = candidate.ClrNamespace.Length == 0 ? localName : candidate.ClrNamespace + "." + localName;
        return candidate.Assembly?.GetTypeByMetadataName(metadataName) ?? _compilation.GetTypeByMetadataName(metadataName);
    }

    private void AddAssemblyNamespaceCandidates(
        string namespaceUri,
        List<NamespaceCandidate> result,
        HashSet<string> visited)
    {
        if (!visited.Add(namespaceUri)) return;
        for (var index = 0; index < _namespaceDefinitions.Count; index++)
        {
            var definition = _namespaceDefinitions[index];
            if (string.Equals(definition.XmlNamespace, namespaceUri, StringComparison.Ordinal))
                AddCandidate(result, new NamespaceCandidate(definition.ClrNamespace, definition.Assembly));
        }
        for (var index = 0; index < _namespaceCompatibilities.Count; index++)
        {
            var compatibility = _namespaceCompatibilities[index];
            if (string.Equals(compatibility.OldNamespace, namespaceUri, StringComparison.Ordinal))
                AddAssemblyNamespaceCandidates(compatibility.NewNamespace, result, visited);
        }
    }

    private static void AddCandidate(List<NamespaceCandidate> result, NamespaceCandidate candidate)
    {
        for (var index = 0; index < result.Count; index++)
        {
            if (string.Equals(result[index].ClrNamespace, candidate.ClrNamespace, StringComparison.Ordinal) &&
                SymbolEqualityComparer.Default.Equals(result[index].Assembly, candidate.Assembly)) return;
        }
        result.Add(candidate);
    }

    private IAssemblySymbol? FindAssembly(string name)
    {
        if (string.Equals(_compilation.AssemblyName, name, StringComparison.OrdinalIgnoreCase)) return _compilation.Assembly;
        foreach (var assembly in _compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (string.Equals(assembly.Identity.Name, name, StringComparison.OrdinalIgnoreCase)) return assembly;
        }
        return null;
    }

    private XamlTypeInfo? ResolveIntrinsic(string namespaceUri, string localName)
    {
        if (!string.Equals(namespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal))
        {
            return null;
        }

        SpecialType specialType;
        switch (localName)
        {
            case "Object": specialType = SpecialType.System_Object; break;
            case "String": specialType = SpecialType.System_String; break;
            case "Boolean": specialType = SpecialType.System_Boolean; break;
            case "Byte": specialType = SpecialType.System_Byte; break;
            case "Int16": specialType = SpecialType.System_Int16; break;
            case "Int32": specialType = SpecialType.System_Int32; break;
            case "Int64": specialType = SpecialType.System_Int64; break;
            case "Single": specialType = SpecialType.System_Single; break;
            case "Double": specialType = SpecialType.System_Double; break;
            case "Decimal": specialType = SpecialType.System_Decimal; break;
            case "Char": specialType = SpecialType.System_Char; break;
            default:
                return ResolveIntrinsicObject(namespaceUri, localName);
        }

        var symbol = _compilation.GetSpecialType(specialType);
        return DescribeType(namespaceUri, localName, symbol);
    }

    private XamlTypeInfo? ResolveSyntheticType(string namespaceUri, string localName)
    {
        for (var index = 0; index < _syntheticTypes.Count; index++)
        {
            var definition = _syntheticTypes[index];
            if (!string.Equals(definition.NamespaceUri, namespaceUri, StringComparison.Ordinal) ||
                !string.Equals(definition.Name, localName, StringComparison.Ordinal)) continue;
            var objectType = _compilation.GetSpecialType(SpecialType.System_Object);
            var returnType = string.IsNullOrWhiteSpace(definition.ReturnTypeMetadataName)
                ? objectType
                : _compilation.GetTypeByMetadataName(definition.ReturnTypeMetadataName!) ?? objectType;
            var constructors = new List<XamlConstructorInfo>();
            foreach (var constructor in definition.Constructors)
            {
                var argumentTypes = new List<ITypeSymbol>(constructor.Count);
                foreach (var argumentMetadataName in constructor)
                {
                    var argumentType = _compilation.GetTypeByMetadataName(argumentMetadataName);
                    if (argumentType != null) argumentTypes.Add(argumentType);
                }
                if (argumentTypes.Count == constructor.Count)
                    constructors.Add(new XamlConstructorInfo(argumentTypes));
            }
            var metadataName = "synthetic:" + _profile.Id + ":" + localName;
            var result = new XamlTypeInfo(
                namespaceUri,
                localName,
                objectType,
                metadataName,
                isValueType: false,
                isEnum: false,
                isNullable: true,
                isCollection: false,
                isDictionary: false,
                contentMemberName: null,
                isDefaultConstructible: constructors.Any(static constructor => constructor.Arity == 0),
                constructors: constructors,
                textSyntax: XamlTextSyntaxInfo.None,
                isMarkupExtension: definition.IsMarkupExtension,
                returnValueType: returnType,
                resourceReferenceRole: definition.ResourceReferenceRole,
                expressionRole: definition.ExpressionRole);
            _syntheticByMetadataName[metadataName] = definition;
            return result;
        }
        return null;
    }

    private XamlTypeInfo? ResolveIntrinsicObject(string namespaceUri, string localName)
    {
        ITypeSymbol? symbol;
        switch (localName)
        {
            case "Uri": symbol = _compilation.GetTypeByMetadataName("System.Uri"); break;
            case "TimeSpan": symbol = _compilation.GetTypeByMetadataName("System.TimeSpan"); break;
            case "Type":
            case "TypeExtension": symbol = _compilation.GetTypeByMetadataName("System.Type"); break;
            case "Array":
            case "ArrayExtension": symbol = _compilation.GetTypeByMetadataName("System.Array"); break;
            case "Null":
            case "NullExtension":
            case "Static":
            case "StaticExtension":
            case "Reference":
            case "ReferenceExtension":
            case "MarkupExtension": symbol = _compilation.GetSpecialType(SpecialType.System_Object); break;
            default: return null;
        }
        if (symbol == null || symbol.TypeKind == TypeKind.Error) return null;
        var described = DescribeType(namespaceUri, localName, symbol);
        return new XamlTypeInfo(
            namespaceUri,
            localName,
            described.Symbol,
            "x:" + localName,
            described.IsValueType,
            described.IsEnum,
            described.IsNullable,
            described.IsCollection,
            described.IsDictionary,
            described.ContentMemberName,
            described.RuntimeNameMemberName,
            described.DictionaryKeyMemberName,
            described.CollectionShape,
            described.Annotations,
            described.EnumValues,
            described.IsDefaultConstructible,
            described.Constructors,
            described.IsGeneric,
            described.GenericArity,
            described.NameScopeMemberName,
            described.XmlLanguageMemberName,
            described.UidMemberName,
            described.TextSyntax,
            described.IsMarkupExtension,
            described.ReturnValueType,
            trimSurroundingWhitespace: described.TrimSurroundingWhitespace,
            whitespaceSignificantCollection: described.WhitespaceSignificantCollection,
            usableDuringInitialization: described.UsableDuringInitialization,
            contentWrappers: described.ContentWrappers,
            ambient: described.Ambient,
            deferringLoader: described.DeferringLoader,
            nameScopeProperty: described.NameScopeProperty,
            xmlLanguageProperty: described.XmlLanguageProperty,
            uidProperty: described.UidProperty,
            nameScopeShape: described.NameScopeShape,
            compilationMode: described.CompilationMode,
            filePath: described.FilePath,
            bindable: described.Bindable,
            fullMetadataProvider: described.FullMetadataProvider,
            browsable: described.Browsable,
            editorBrowsable: described.EditorBrowsable,
            designTimeVisible: described.DesignTimeVisible,
            localizability: described.Localizability);
    }

    private XamlTypeInfo DescribeType(
        string namespaceUri,
        string name,
        ITypeSymbol symbol,
        bool includeContentWrappers = true)
    {
        var named = symbol as INamedTypeSymbol;
        var metadataName = GetMetadataName(symbol);
        var csharpName = symbol.ToDisplayString(FullyQualifiedFormat);
        var enumValues = new List<XamlEnumValueInfo>();
        var constructors = new List<XamlConstructorInfo>();
        if (symbol.TypeKind == TypeKind.Enum && named != null)
        {
            var fields = named.GetMembers();
            for (var index = 0; index < fields.Length; index++)
            {
                if (fields[index] is IFieldSymbol field && field.HasConstantValue)
                {
                    enumValues.Add(new XamlEnumValueInfo(field.Name, field));
                }
            }
        }
        if (named != null && !named.IsAbstract)
        {
            foreach (var constructor in named.InstanceConstructors)
            {
                if (constructor.DeclaredAccessibility == Accessibility.Public)
                    constructors.Add(new XamlConstructorInfo(
                        constructor,
                        constructor.Parameters.Select(parameter =>
                            new XamlConstructorParameterInfo(
                                parameter,
                                FindDataTypeInheritance(
                                    GetAnnotations(
                                        parameter,
                                        XamlSchemaAttributeTargets.Parameter,
                                        includeInherited: false))))
                            .ToArray()));
            }
            constructors.Sort(static (left, right) =>
            {
                var arity = left.Arity.CompareTo(right.Arity);
                return arity != 0
                    ? arity
                    : string.Compare(left.Symbol?.ToDisplayString(), right.Symbol?.ToDisplayString(), StringComparison.Ordinal);
            });
        }
        var isDefaultConstructible = symbol.IsValueType ||
            constructors.Any(constructor => constructor.Arity == 0);

        var annotations = named == null
            ? Array.Empty<XamlSchemaAnnotationInfo>()
            : GetAnnotations(named, XamlSchemaAttributeTargets.Type, includeInherited: true);
        var collectionShape = FindCollectionShape(symbol);
        var isDictionary = Implements(symbol, "System.Collections.IDictionary") ||
                           ImplementsGeneric(symbol, "System.Collections.Generic.IDictionary<TKey, TValue>");
        isDictionary = isDictionary || (collectionShape?.IsDictionary ?? false);
        var isCollection = isDictionary ||
                           Implements(symbol, "System.Collections.IList") ||
                           ImplementsGeneric(symbol, "System.Collections.Generic.ICollection<T>") ||
                           collectionShape != null;
        var contentMember = named == null ? null : GetAliasedMemberName(
            named,
            annotations,
            XamlSchemaSemantics.ContentProperty,
            allowMemberAnnotation: true);
        var runtimeNameMember = named == null ? null : GetAliasedMemberName(
            named,
            annotations,
            XamlSchemaSemantics.RuntimeNameProperty,
            allowMemberAnnotation: false) ?? _shapePolicy.RuntimeNameFallback;
        var dictionaryKeyMember = named == null ? null : GetAliasedMemberName(
            named,
            annotations,
            XamlSchemaSemantics.DictionaryKeyProperty,
            allowMemberAnnotation: false);
        if (dictionaryKeyMember == null && named != null &&
            _shapePolicy.ImplicitDictionaryKeyMembers.TryGetValue(metadataName, out var implicitKeyMember) &&
            named.GetMembers(implicitKeyMember).OfType<IPropertySymbol>().Any(property =>
                !property.IsStatic &&
                property.DeclaredAccessibility == Accessibility.Public &&
                property.SetMethod?.DeclaredAccessibility == Accessibility.Public))
            dictionaryKeyMember = implicitKeyMember;
        var nameScopeProperty = named == null
            ? null
            : FindAliasedMemberShape(
                named,
                annotations,
                XamlSchemaSemantics.NameScopeProperty,
                allowAttachableOwner: true,
                requiresWrite: false);
        var xmlLanguageProperty = named == null
            ? null
            : FindAliasedMemberShape(
                named,
                annotations,
                XamlSchemaSemantics.XmlLanguageProperty,
                allowAttachableOwner: false,
                requiresWrite: true);
        var uidProperty = named == null
            ? null
            : FindAliasedMemberShape(
                named,
                annotations,
                XamlSchemaSemantics.UidProperty,
                allowAttachableOwner: false,
                requiresWrite: true);
        var nameScopeShape = named == null
            ? null
            : FindNameScopeShape(named);
        var nameScopeMember = nameScopeProperty?.DeclaredName;
        var xmlLanguageMember = xmlLanguageProperty?.DeclaredName;
        var uidMember = uidProperty?.DeclaredName;
        var markupExtensionShape = named == null
            ? null
            : FindMarkupExtensionShape(named, name, annotations);
        var markupExtensionOptionSelector = named == null
            ? null
            : FindMarkupExtensionOptionSelectorShape(named);
        var markupExtensionSetHandler = named == null
            ? null
            : FindSetValueHandlerShape(
                named,
                annotations,
                XamlSchemaSemantics.SetMarkupExtensionHandler);
        var markupExtensionReceiver = named == null
            ? null
            : FindMarkupExtensionReceiverShape(named);
        var typeConverterSetHandler = named == null
            ? null
            : FindSetValueHandlerShape(
                named,
                annotations,
                XamlSchemaSemantics.SetTypeConverterHandler);
        var valueSerializer = named == null
            ? null
            : FindValueSerializerShape(annotations);
        var trimSurroundingWhitespace = FindBooleanSchemaInfo(
            annotations,
            XamlSchemaSemantics.TrimSurroundingWhitespace,
            presenceValue: true);
        var whitespaceSignificantCollection = FindBooleanSchemaInfo(
            annotations,
            XamlSchemaSemantics.WhitespaceSignificantCollection,
            presenceValue: true);
        var usableDuringInitialization = FindBooleanSchemaInfo(
            annotations,
            XamlSchemaSemantics.UsableDuringInitialization,
            presenceValue: null);
        var ambient = FindBooleanSchemaInfo(
            annotations,
            XamlSchemaSemantics.Ambient,
            presenceValue: true);
        var deferringLoader = FindDeferringLoaderShape(annotations);
        var listSplit = FindListSplitInfo(
            symbol,
            annotations,
            isCollection || isDictionary);
        var templateParts = named == null
            ? Array.Empty<XamlTemplatePartInfo>()
            : FindTemplateParts(named, annotations);
        var templateVisualStates = named == null
            ? Array.Empty<XamlTemplateVisualStateInfo>()
            : FindTemplateVisualStates(named, annotations);
        var styleTypedProperties = named == null
            ? Array.Empty<XamlStyleTypedPropertyInfo>()
            : FindStyleTypedProperties(named, annotations);
        var compilationMode = named == null
            ? null
            : FindCompilationMode(annotations, XamlCompilationScope.Type);
        var filePath = named == null
            ? null
            : FindFilePath(named, annotations);
        var bindable = named == null
            ? null
            : FindTypeMarker(
                named,
                annotations,
                XamlSchemaSemantics.Bindable);
        var fullMetadataProvider = named == null
            ? null
            : FindTypeMarker(
                named,
                annotations,
                XamlSchemaSemantics.FullXamlMetadataProvider);
        var browsable = named == null
            ? null
            : FindBrowsable(
                annotations,
                XamlSchemaSemantics.Browsable,
                parameterlessDefault: null);
        var editorBrowsable = named == null
            ? null
            : FindEditorBrowsable(annotations);
        var designTimeVisible = named == null
            ? null
            : FindBrowsable(
                annotations,
                XamlSchemaSemantics.DesignTimeVisible,
                parameterlessDefault: false);
        var localizability = named == null
            ? null
            : FindLocalizability(annotations);
        var contentWrappers = includeContentWrappers
            ? FindContentWrapperShapes(namespaceUri, symbol, collectionShape, annotations)
            : Array.Empty<XamlContentWrapperShapeInfo>();
        var isMarkupExtension = markupExtensionShape?.IsValid == true;
        var returnValueType = markupExtensionShape == null
            ? null
            : GetMarkupExtensionReturnType(annotations, markupExtensionShape);

        var result = new XamlTypeInfo(
            namespaceUri,
            name,
            symbol,
            metadataName,
            symbol.IsValueType,
            symbol.TypeKind == TypeKind.Enum,
            symbol.NullableAnnotation == NullableAnnotation.Annotated || !symbol.IsValueType,
            isCollection,
            isDictionary,
            contentMember,
            runtimeNameMember,
            dictionaryKeyMember,
            collectionShape,
            annotations,
            enumValues.ToArray(),
            isDefaultConstructible,
            constructors.ToArray(),
            named?.IsGenericType == true,
            named?.Arity ?? 0,
            nameScopeMember,
            xmlLanguageMember,
            uidMember,
            GetTextSyntax(symbol, annotations, fallback: null),
            isMarkupExtension,
            returnValueType,
            markupExtensionShape: markupExtensionShape,
            markupExtensionSetHandler: markupExtensionSetHandler,
            typeConverterSetHandler: typeConverterSetHandler,
            valueSerializer: valueSerializer,
            trimSurroundingWhitespace: trimSurroundingWhitespace,
            whitespaceSignificantCollection: whitespaceSignificantCollection,
            usableDuringInitialization: usableDuringInitialization,
            contentWrappers: contentWrappers,
            ambient: ambient,
            deferringLoader: deferringLoader,
            nameScopeProperty: nameScopeProperty,
            xmlLanguageProperty: xmlLanguageProperty,
            uidProperty: uidProperty,
            nameScopeShape: nameScopeShape,
            markupExtensionOptionSelector: markupExtensionOptionSelector,
            listSplit: listSplit,
            templateParts: templateParts,
            templateVisualStates: templateVisualStates,
            styleTypedProperties: styleTypedProperties,
            compilationMode: compilationMode,
            filePath: filePath,
            bindable: bindable,
            fullMetadataProvider: fullMetadataProvider,
            browsable: browsable,
            editorBrowsable: editorBrowsable,
            designTimeVisible: designTimeVisible,
            localizability: localizability,
            markupExtensionReceiver: markupExtensionReceiver);

        if (named != null && !_symbolByMetadataName.ContainsKey(metadataName))
        {
            _symbolByMetadataName.Add(metadataName, named);
        }

        return result;
    }

    private static IReadOnlyList<XamlTemplatePartInfo> FindTemplateParts(
        INamedTypeSymbol type,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation => string.Equals(
            annotation.Semantic,
            XamlSchemaSemantics.TemplatePart,
            StringComparison.Ordinal)).ToArray();
        var result = new List<XamlTemplatePartInfo>(matches.Length);
        foreach (var annotation in matches)
        {
            var name = GetNamedConstant(annotation.Attribute, "Name")?.Value as string;
            var partType = GetNamedConstant(annotation.Attribute, "Type")?.Value as ITypeSymbol;
            if (annotation.Attribute.ConstructorArguments.Length == 2)
            {
                name ??= annotation.Attribute.ConstructorArguments[0].Value as string;
                partType ??= annotation.Attribute.ConstructorArguments[1].Value as ITypeSymbol;
            }
            var requiredConstant = GetNamedConstant(annotation.Attribute, "IsRequired");
            var isRequired = requiredConstant?.Value as bool?;
            string? error = null;
            if (annotation.DeclaredOn is not INamedTypeSymbol)
                error = "Template-part metadata must be declared on a type.";
            else if (string.IsNullOrWhiteSpace(name))
                error = "Template-part metadata requires a non-empty part name.";
            else if (partType == null || partType.TypeKind == TypeKind.Error)
                error = "Template-part metadata requires a resolvable part type.";
            else if (matches.Count(candidate =>
                         candidate.InheritanceDepth == annotation.InheritanceDepth &&
                         string.Equals(
                             GetTemplatePartName(candidate.Attribute),
                             name,
                             StringComparison.Ordinal)) > 1)
                error = "Template-part name '" + name +
                    "' is declared more than once at the same inheritance depth.";
            result.Add(new XamlTemplatePartInfo(
                annotation,
                annotation.DeclaredOn as INamedTypeSymbol,
                name,
                partType,
                isRequired,
                error));
        }
        return result;
    }

    private static IReadOnlyList<XamlTemplateVisualStateInfo> FindTemplateVisualStates(
        INamedTypeSymbol type,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation => string.Equals(
            annotation.Semantic,
            XamlSchemaSemantics.TemplateVisualState,
            StringComparison.Ordinal)).ToArray();
        var result = new List<XamlTemplateVisualStateInfo>(matches.Length);
        foreach (var annotation in matches)
        {
            var name = GetNamedConstant(annotation.Attribute, "Name")?.Value as string;
            var group = GetNamedConstant(annotation.Attribute, "GroupName")?.Value as string;
            string? error = null;
            if (annotation.DeclaredOn is not INamedTypeSymbol)
                error = "Template visual-state metadata must be declared on a type.";
            else if (string.IsNullOrWhiteSpace(name))
                error = "Template visual-state metadata requires a non-empty state name.";
            else if (string.IsNullOrWhiteSpace(group))
                error = "Template visual-state metadata requires a non-empty group name.";
            else if (matches.Count(candidate =>
                         candidate.InheritanceDepth == annotation.InheritanceDepth &&
                         string.Equals(
                             GetNamedConstant(candidate.Attribute, "Name")?.Value as string,
                             name,
                             StringComparison.Ordinal) &&
                         string.Equals(
                             GetNamedConstant(candidate.Attribute, "GroupName")?.Value as string,
                             group,
                             StringComparison.Ordinal)) > 1)
                error = "Template visual state '" + group + "." + name +
                    "' is declared more than once at the same inheritance depth.";
            result.Add(new XamlTemplateVisualStateInfo(
                annotation,
                annotation.DeclaredOn as INamedTypeSymbol,
                name,
                group,
                error));
        }
        return result;
    }

    private static IReadOnlyList<XamlStyleTypedPropertyInfo> FindStyleTypedProperties(
        INamedTypeSymbol type,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation => string.Equals(
            annotation.Semantic,
            XamlSchemaSemantics.StyleTypedProperty,
            StringComparison.Ordinal)).ToArray();
        var result = new List<XamlStyleTypedPropertyInfo>(matches.Length);
        foreach (var annotation in matches)
        {
            var propertyName =
                GetNamedConstant(annotation.Attribute, "Property")?.Value as string;
            var targetType =
                GetNamedConstant(annotation.Attribute, "StyleTargetType")?.Value as ITypeSymbol;
            var property = string.IsNullOrWhiteSpace(propertyName)
                ? null
                : FindPublicInstanceProperty(type, propertyName!);
            string? error = null;
            if (annotation.DeclaredOn is not INamedTypeSymbol)
                error = "Style-typed property metadata must be declared on a type.";
            else if (string.IsNullOrWhiteSpace(propertyName))
                error = "Style-typed property metadata requires a non-empty property name.";
            else if (property == null)
                error = "Style-typed property '" + propertyName +
                    "' is not a public instance property on '" + GetMetadataName(type) + "'.";
            else if (targetType == null || targetType.TypeKind == TypeKind.Error)
                error = "Style-typed property metadata requires a resolvable target type.";
            else if (matches.Count(candidate =>
                         candidate.InheritanceDepth == annotation.InheritanceDepth &&
                         string.Equals(
                             GetNamedConstant(candidate.Attribute, "Property")?.Value as string,
                             propertyName,
                             StringComparison.Ordinal)) > 1)
                error = "Style-typed property '" + propertyName +
                    "' is declared more than once at the same inheritance depth.";
            result.Add(new XamlStyleTypedPropertyInfo(
                annotation,
                annotation.DeclaredOn as INamedTypeSymbol,
                propertyName,
                property,
                targetType,
                error));
        }
        return result;
    }

    private static string? GetTemplatePartName(AttributeData attribute)
    {
        var named = GetNamedConstant(attribute, "Name")?.Value as string;
        return named ?? (attribute.ConstructorArguments.Length == 2
            ? attribute.ConstructorArguments[0].Value as string
            : null);
    }

    private static TypedConstant? GetNamedConstant(
        AttributeData attribute,
        string name)
    {
        foreach (var argument in attribute.NamedArguments)
            if (string.Equals(argument.Key, name, StringComparison.Ordinal))
                return argument.Value;
        return null;
    }

    private static IPropertySymbol? FindPublicInstanceProperty(
        INamedTypeSymbol type,
        string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var candidates = current.GetMembers(name).OfType<IPropertySymbol>()
                .Where(static property =>
                    !property.IsStatic &&
                    property.DeclaredAccessibility == Accessibility.Public)
                .ToArray();
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1) return null;
        }
        return null;
    }

    private static XamlListSplitInfo? FindListSplitInfo(
        ITypeSymbol type,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        bool isCollection)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.ListSeparator,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        var annotation = matches[0];
        var separators = new List<string> { "," };
        var splitOptions = 3;
        string? error = matches.Length == 1
            ? null
            : "List-splitting metadata is ambiguous at the winning precedence.";
        foreach (var argument in annotation.Attribute.NamedArguments)
        {
            if (string.Equals(argument.Key, "Separators", StringComparison.Ordinal))
            {
                if (argument.Value.IsNull)
                {
                    separators.Clear();
                    separators.Add(",");
                    continue;
                }
                if (argument.Value.Kind != TypedConstantKind.Array)
                {
                    error = "List Separators must be a string array constant.";
                    continue;
                }
                separators.Clear();
                foreach (var item in argument.Value.Values)
                {
                    if (item.Value is string separator)
                        separators.Add(separator);
                    else
                        error = "Every list separator must be a non-null string.";
                }
            }
            else if (string.Equals(argument.Key, "SplitOptions", StringComparison.Ordinal))
            {
                if (argument.Value.Value is int value)
                    splitOptions = value;
                else
                    error = "List SplitOptions must be a System.StringSplitOptions constant.";
            }
        }
        if (!isCollection)
            error = $"List-splitting metadata can only annotate collection types; '{GetMetadataName(type)}' is not a collection.";
        else if (separators.Count == 0 ||
                 separators.Any(static separator => string.IsNullOrEmpty(separator)))
            error = "List-splitting metadata requires at least one non-empty separator.";
        else if ((splitOptions & ~3) != 0)
            error = "List SplitOptions contains unsupported flags.";
        return new XamlListSplitInfo(
            annotation,
            type,
            separators,
            splitOptions,
            error);
    }

    private XamlDeferringLoaderShapeInfo? FindDeferringLoaderShape(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.DeferredLoad,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        var annotation = matches[0];
        if (matches.Length != 1)
            return InvalidDeferringLoader(
                annotation,
                "Deferred-load metadata is ambiguous at the winning precedence.");

        var arguments = annotation.Attribute.ConstructorArguments;
        if (arguments.Length != 2)
            return InvalidDeferringLoader(
                annotation,
                "The deferred-load attribute must supply loader and content types.");

        var loaderSymbol = ResolveDeferredType(
            arguments[0],
            out var loaderTypeName);
        var contentType = ResolveDeferredType(
            arguments[1],
            out var contentTypeName);
        if (loaderSymbol is not INamedTypeSymbol loaderType)
            return new XamlDeferringLoaderShapeInfo(
                annotation,
                null,
                contentType,
                null,
                null,
                null,
                Array.Empty<IMethodSymbol>(),
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                loaderTypeName,
                contentTypeName,
                "Deferred loader type '" + (loaderTypeName ?? "<missing>") +
                "' could not be resolved as a CLR named type.");
        if (contentType == null || contentType.TypeKind == TypeKind.Error)
            return new XamlDeferringLoaderShapeInfo(
                annotation,
                loaderType,
                null,
                null,
                null,
                null,
                Array.Empty<IMethodSymbol>(),
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                loaderTypeName,
                contentTypeName,
                "Deferred content type '" + (contentTypeName ?? "<missing>") +
                "' could not be resolved.");

        var constructor = loaderType.InstanceConstructors.SingleOrDefault(candidate =>
            candidate.DeclaredAccessibility == Accessibility.Public &&
            candidate.Parameters.Length == 0);
        var loadCandidates = GetNearestNamedMethods(loaderType, "Load");
        var saveCandidates = GetNearestNamedMethods(loaderType, "Save");
        IMethodSymbol? loadMethod = null;
        IMethodSymbol? saveMethod = null;
        string? error = null;
        if (loaderType.TypeKind != TypeKind.Class ||
            loaderType.IsAbstract ||
            loaderType.DeclaredAccessibility != Accessibility.Public)
        {
            error = "Deferred loader '" + GetMetadataName(loaderType) +
                "' must be a public, non-abstract class.";
        }
        else if (constructor == null)
        {
            error = "Deferred loader '" + GetMetadataName(loaderType) +
                "' must expose one public parameterless constructor.";
        }
        else
        {
            var serviceProvider = _compilation.GetTypeByMetadataName(
                "System.IServiceProvider");
            var pairs = new List<(IMethodSymbol Load, IMethodSymbol Save)>();
            if (serviceProvider != null)
            {
                foreach (var load in loadCandidates)
                foreach (var save in saveCandidates)
                    if (IsDeferringLoaderPair(
                            load,
                            save,
                            contentType,
                            serviceProvider))
                        pairs.Add((load, save));
            }

            if (pairs.Count == 1)
            {
                loadMethod = pairs[0].Load;
                saveMethod = pairs[0].Save;
            }
            else if (pairs.Count == 0)
            {
                error = "Deferred loader '" + GetMetadataName(loaderType) +
                    "' must expose one compatible Load(reader, IServiceProvider) / " +
                    "Save(content, IServiceProvider) method pair.";
            }
            else
            {
                error = "Deferred loader '" + GetMetadataName(loaderType) +
                    "' exposes multiple compatible Load/Save method pairs.";
            }
        }

        return new XamlDeferringLoaderShapeInfo(
            annotation,
            loaderType,
            contentType,
            constructor,
            loadMethod,
            saveMethod,
            loadCandidates,
            saveCandidates,
            annotation.ProviderId,
            loaderTypeName,
            contentTypeName,
            error);
    }

    private XamlDeferringLoaderShapeInfo InvalidDeferringLoader(
        XamlSchemaAnnotationInfo annotation,
        string error) =>
        new(
            annotation,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<IMethodSymbol>(),
            Array.Empty<IMethodSymbol>(),
            annotation.ProviderId,
            null,
            null,
            error);

    private ITypeSymbol? ResolveDeferredType(
        TypedConstant constant,
        out string? declaredName)
    {
        if (constant.Value is ITypeSymbol symbol)
        {
            declaredName = GetMetadataName(symbol);
            return symbol;
        }
        if (constant.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            declaredName = null;
            return null;
        }

        declaredName = text.Trim();
        var metadataName = declaredName;
        if (metadataName.StartsWith("global::", StringComparison.Ordinal))
            metadataName = metadataName.Substring("global::".Length);
        var assemblySeparator = metadataName.IndexOf(',');
        if (assemblySeparator >= 0)
            metadataName = metadataName.Substring(0, assemblySeparator).Trim();
        return _compilation.GetTypeByMetadataName(metadataName);
    }

    private static IReadOnlyList<IMethodSymbol> GetNearestNamedMethods(
        INamedTypeSymbol type,
        string name)
    {
        var result = new List<IMethodSymbol>();
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (result.Any(existing => HaveSameCallableSignature(existing, method)))
                    continue;
                result.Add(method);
            }
        }
        result.Sort(static (left, right) =>
            string.Compare(
                left.ToDisplayString(),
                right.ToDisplayString(),
                StringComparison.Ordinal));
        return result;
    }

    private static bool HaveSameCallableSignature(
        IMethodSymbol left,
        IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Parameters.Length != right.Parameters.Length)
            return false;
        for (var index = 0; index < left.Parameters.Length; index++)
            if (left.Parameters[index].RefKind != right.Parameters[index].RefKind ||
                !SymbolEqualityComparer.IncludeNullability.Equals(
                    left.Parameters[index].Type,
                    right.Parameters[index].Type))
                return false;
        return true;
    }

    private bool IsDeferringLoaderPair(
        IMethodSymbol load,
        IMethodSymbol save,
        ITypeSymbol contentType,
        ITypeSymbol serviceProvider)
    {
        if (load.IsStatic || save.IsStatic ||
            load.MethodKind != MethodKind.Ordinary ||
            save.MethodKind != MethodKind.Ordinary ||
            load.IsGenericMethod || save.IsGenericMethod ||
            load.DeclaredAccessibility != Accessibility.Public ||
            save.DeclaredAccessibility != Accessibility.Public ||
            load.ReturnsVoid || save.ReturnsVoid ||
            load.Parameters.Length != 2 ||
            save.Parameters.Length != 2)
            return false;
        foreach (var parameter in load.Parameters)
            if (parameter.RefKind != RefKind.None) return false;
        foreach (var parameter in save.Parameters)
            if (parameter.RefKind != RefKind.None) return false;
        if (!SymbolEqualityComparer.IncludeNullability.Equals(
                load.Parameters[1].Type,
                serviceProvider) ||
            !SymbolEqualityComparer.IncludeNullability.Equals(
                save.Parameters[1].Type,
                serviceProvider) ||
            !SymbolEqualityComparer.IncludeNullability.Equals(
                load.Parameters[0].Type,
                save.ReturnType) ||
            !IsAssignableTo(contentType, save.Parameters[0].Type))
            return false;
        return load.ReturnType.SpecialType == SpecialType.System_Object ||
               IsAssignableTo(load.ReturnType, contentType);
    }

    private IReadOnlyList<XamlContentWrapperShapeInfo> FindContentWrapperShapes(
        string namespaceUri,
        ITypeSymbol collectionType,
        XamlCollectionShapeInfo? collectionShape,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var wrapperAnnotations = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.ContentWrapper,
                StringComparison.Ordinal)).ToArray();
        if (wrapperAnnotations.Length == 0)
            return Array.Empty<XamlContentWrapperShapeInfo>();

        var result = new List<XamlContentWrapperShapeInfo>(wrapperAnnotations.Length);
        foreach (var annotation in wrapperAnnotations)
        {
            if (annotation.ValueConstant?.Value is not INamedTypeSymbol wrapperSymbol)
            {
                result.Add(new XamlContentWrapperShapeInfo(
                    annotation,
                    wrapperType: null,
                    constructor: null,
                    contentMember: null,
                    annotation.ProviderId,
                    "The content-wrapper attribute must supply a CLR type."));
                continue;
            }

            if (collectionShape == null)
            {
                result.Add(new XamlContentWrapperShapeInfo(
                    annotation,
                    wrapperType: null,
                    constructor: null,
                    contentMember: null,
                    annotation.ProviderId,
                    "Collection '" + GetMetadataName(collectionType) +
                    "' has no unique insertion item type against which to validate wrappers."));
                continue;
            }

            var wrapperType = DescribeType(
                namespaceUri,
                wrapperSymbol.Name,
                wrapperSymbol,
                includeContentWrappers: false);
            var constructor = wrapperSymbol.InstanceConstructors.SingleOrDefault(candidate =>
                candidate.DeclaredAccessibility == Accessibility.Public &&
                candidate.Parameters.Length == 0);
            string? error = null;
            if (wrapperSymbol.TypeKind != TypeKind.Class ||
                wrapperSymbol.IsAbstract ||
                wrapperSymbol.DeclaredAccessibility != Accessibility.Public ||
                constructor == null)
            {
                error = "Content wrapper '" + GetMetadataName(wrapperSymbol) +
                    "' must be a public, non-abstract class with one public parameterless constructor.";
            }
            else if (!IsAssignableTo(wrapperSymbol, collectionShape.ItemType))
            {
                error = "Content wrapper '" + GetMetadataName(wrapperSymbol) +
                    "' is not assignable to collection item type '" +
                    GetMetadataName(collectionShape.ItemType) + "'.";
            }

            XamlMemberInfo? contentMember = null;
            if (error == null)
            {
                if (string.IsNullOrWhiteSpace(wrapperType.ContentMemberName))
                {
                    error = "Content wrapper '" + wrapperType.MetadataName +
                        "' must declare a content member.";
                }
                else
                {
                    contentMember = ResolveMember(
                        wrapperType,
                        namespaceUri,
                        ownerTypeName: null,
                        wrapperType.ContentMemberName!);
                    if (contentMember == null ||
                        (!contentMember.CanWrite &&
                         contentMember.Kind != XamlMemberKind.Collection &&
                         contentMember.Kind != XamlMemberKind.Dictionary))
                    {
                        error = "Content wrapper '" + wrapperType.MetadataName +
                            "' has no writable or mutable content member named '" +
                            wrapperType.ContentMemberName + "'.";
                    }
                }
            }

            result.Add(new XamlContentWrapperShapeInfo(
                annotation,
                wrapperType,
                constructor,
                contentMember,
                annotation.ProviderId,
                error));
        }

        return result;
    }

    private static XamlSchemaBooleanInfo? FindBooleanSchemaInfo(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string semantic,
        bool? presenceValue)
    {
        var annotation = annotations.FirstOrDefault(candidate =>
            string.Equals(candidate.Semantic, semantic, StringComparison.Ordinal));
        if (annotation == null) return null;
        if (presenceValue.HasValue)
            return new XamlSchemaBooleanInfo(semantic, presenceValue.Value, annotation);
        if (annotation.ValueConstant?.Value is bool value)
            return new XamlSchemaBooleanInfo(semantic, value, annotation);
        return new XamlSchemaBooleanInfo(
            semantic,
            value: false,
            annotation,
            "Attribute '" + annotation.Attribute.AttributeClass?.ToDisplayString() +
            "' must supply a Boolean schema value.");
    }

    private static IReadOnlyList<XamlMarkupBracketPairInfo>
        FindMarkupExtensionBracketPairs(
            IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.MarkupExtensionBracketCharacters,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
            return Array.Empty<XamlMarkupBracketPairInfo>();

        var parsed = new List<(XamlSchemaAnnotationInfo Annotation, char Open, char Close, string? Error)>(
            matches.Length);
        foreach (var annotation in matches)
        {
            var arguments = annotation.Attribute.ConstructorArguments;
            if (arguments.Length != 2 ||
                arguments[0].Value is not char opening ||
                arguments[1].Value is not char closing)
            {
                parsed.Add((
                    annotation,
                    '\0',
                    '\0',
                    "A markup bracket declaration must supply two Char constructor arguments."));
                continue;
            }
            if (IsReservedMarkupDelimiter(opening) ||
                IsReservedMarkupDelimiter(closing))
            {
                parsed.Add((
                    annotation,
                    opening,
                    closing,
                    "Markup bracket characters cannot reuse whitespace or reserved " +
                    "markup-extension grammar characters."));
                continue;
            }
            parsed.Add((annotation, opening, closing, null));
        }

        var result = new List<XamlMarkupBracketPairInfo>(parsed.Count);
        foreach (var item in parsed)
        {
            var error = item.Error;
            if (error == null && parsed.Any(other =>
                    other.Error == null &&
                    other.Open == item.Open &&
                    other.Close != item.Close))
            {
                error = "Opening markup bracket '" + item.Open +
                    "' maps to more than one closing character.";
            }
            result.Add(new XamlMarkupBracketPairInfo(
                item.Annotation,
                item.Open,
                item.Close,
                error));
        }
        return result;
    }

    private static bool IsReservedMarkupDelimiter(char value) =>
        char.IsWhiteSpace(value) ||
        value == '{' ||
        value == '}' ||
        value == ',' ||
        value == '=' ||
        value == '\'' ||
        value == '"' ||
        value == '\\';

    private XamlValueSerializerShapeInfo? FindValueSerializerShape(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var serializerAnnotations = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.ValueSerializer,
                StringComparison.Ordinal)).ToArray();
        if (serializerAnnotations.Length == 0) return null;
        var annotation = serializerAnnotations[0];
        if (serializerAnnotations.Length != 1)
            return new XamlValueSerializerShapeInfo(
                annotation,
                null,
                null,
                null,
                null,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                isSuppressed: false,
                "Value-serializer metadata is ambiguous at the winning precedence.");
        if (annotation.ValueConstant.HasValue && annotation.ValueConstant.Value.IsNull)
            return new XamlValueSerializerShapeInfo(
                annotation,
                null,
                null,
                null,
                null,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                isSuppressed: true,
                error: null);

        var serializer = ResolveConverterType(annotation);
        if (serializer == null)
            return new XamlValueSerializerShapeInfo(
                annotation,
                null,
                null,
                null,
                null,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                isSuppressed: false,
                $"Value serializer '{annotation.Value}' could not be resolved.");

        const XamlSymbolShapeFeatures contractFeatures =
            XamlSymbolShapeFeatures.ValueSerializerBaseTypes |
            XamlSymbolShapeFeatures.ValueSerializerContextTypes |
            XamlSymbolShapeFeatures.ValueSerializerCanConvertToStringNames |
            XamlSymbolShapeFeatures.ValueSerializerConvertToStringNames;
        var registration = _shapePolicies.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId, annotation.ProviderId, StringComparison.Ordinal) &&
            (candidate.Policy.DeclaredFeatures & contractFeatures) == contractFeatures);
        if (registration == null)
            return new XamlValueSerializerShapeInfo(
                annotation,
                serializer,
                null,
                null,
                null,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                isSuppressed: false,
                $"Metadata provider '{annotation.ProviderId}' does not declare a complete value-serializer contract.");

        var policy = registration.Policy;
        var derivesFromRegisteredBase = false;
        for (var current = serializer; current != null; current = current.BaseType)
        {
            if (!policy.ValueSerializerBaseTypeMetadataNames.Contains(
                    GetMetadataName(current.OriginalDefinition),
                    StringComparer.Ordinal)) continue;
            derivesFromRegisteredBase = true;
            break;
        }
        var constructor = serializer.InstanceConstructors.SingleOrDefault(candidate =>
            candidate.Parameters.Length == 0 &&
            candidate.DeclaredAccessibility == Accessibility.Public);
        if (serializer.IsAbstract ||
            serializer.DeclaredAccessibility != Accessibility.Public ||
            !derivesFromRegisteredBase ||
            constructor == null)
            return new XamlValueSerializerShapeInfo(
                annotation,
                serializer,
                constructor,
                null,
                null,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                isSuppressed: false,
                $"Value serializer '{GetMetadataName(serializer)}' must be public, non-abstract, derive from a registered serializer base, and have one public parameterless constructor.");

        var candidates = new List<IMethodSymbol>();
        var candidateIdentities = new HashSet<string>(StringComparer.Ordinal);
        (IMethodSymbol? Method, string? Error) SelectMethod(
            IReadOnlyList<string> methodNames,
            SpecialType returnType,
            string contractName)
        {
            for (var current = serializer; current != null; current = current.BaseType)
            {
                var applicable = new List<IMethodSymbol>();
                foreach (var methodName in methodNames)
                {
                    foreach (var method in current.GetMembers(methodName).OfType<IMethodSymbol>())
                    {
                        var identity = method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                        if (candidateIdentities.Add(identity)) candidates.Add(method);
                        if (method.IsStatic ||
                            method.IsGenericMethod ||
                            method.MethodKind != MethodKind.Ordinary ||
                            method.DeclaredAccessibility != Accessibility.Public ||
                            method.Parameters.Length != 2 ||
                            method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None) ||
                            method.Parameters[0].Type.SpecialType != SpecialType.System_Object ||
                            method.ReturnType.SpecialType != returnType ||
                            !policy.ValueSerializerContextTypeMetadataNames.Contains(
                                GetMetadataName(method.Parameters[1].Type),
                                StringComparer.Ordinal))
                            continue;
                        applicable.Add(method);
                    }
                }
                if (applicable.Count == 1) return (applicable[0], null);
                if (applicable.Count > 1)
                    return (null,
                        $"Value-serializer {contractName} callable is ambiguous between " +
                        string.Join(", ", applicable.Select(static method => method.ToDisplayString())) + ".");
            }
            return (null,
                $"No public instance {contractName} callable matches the registered value-serializer contract.");
        }

        var canConvert = SelectMethod(
            policy.ValueSerializerCanConvertToStringMethodNames,
            SpecialType.System_Boolean,
            "CanConvertToString");
        var convert = SelectMethod(
            policy.ValueSerializerConvertToStringMethodNames,
            SpecialType.System_String,
            "ConvertToString");
        candidates.Sort(static (left, right) =>
            string.Compare(left.ToDisplayString(), right.ToDisplayString(), StringComparison.Ordinal));
        var error = canConvert.Error ?? convert.Error;
        if (error == null &&
            !SymbolEqualityComparer.Default.Equals(
                canConvert.Method!.Parameters[1].Type,
                convert.Method!.Parameters[1].Type))
            error = "Value-serializer capability and conversion methods must use the same context type.";
        return new XamlValueSerializerShapeInfo(
            annotation,
            serializer,
            constructor,
            error == null ? canConvert.Method : null,
            error == null ? convert.Method : null,
            candidates,
            annotation.ProviderId,
            isSuppressed: false,
            error);
    }

    private XamlSetValueHandlerShapeInfo? FindSetValueHandlerShape(
        INamedTypeSymbol type,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string semantic)
    {
        var matchingAnnotations = annotations.Where(annotation =>
            string.Equals(annotation.Semantic, semantic, StringComparison.Ordinal)).ToArray();
        if (matchingAnnotations.Length == 0) return null;
        var annotation = matchingAnnotations[0];
        var handlerName = annotation.Value?.Trim() ?? string.Empty;
        if (matchingAnnotations.Length != 1)
            return new XamlSetValueHandlerShapeInfo(
                semantic,
                handlerName,
                annotation,
                null,
                null,
                false,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                $"Set-value handler metadata for '{GetMetadataName(type)}' is ambiguous.");
        if (handlerName.Length == 0)
            return new XamlSetValueHandlerShapeInfo(
                semantic,
                handlerName,
                annotation,
                null,
                null,
                false,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                $"Set-value handler metadata for '{GetMetadataName(type)}' must name a callback method.");
        var handlerPolicy = _shapePolicies.FirstOrDefault(registration =>
            string.Equals(registration.ProviderId, annotation.ProviderId, StringComparison.Ordinal) &&
            (registration.Policy.DeclaredFeatures & XamlSymbolShapeFeatures.SetValueHandlerEventArgs) != 0);
        if (handlerPolicy == null ||
            !handlerPolicy.Policy.SetValueHandlerEventArgsTypeMetadataNames.TryGetValue(
                semantic,
                out var eventArgsMetadataName))
            return new XamlSetValueHandlerShapeInfo(
                semantic,
                handlerName,
                annotation,
                null,
                null,
                false,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                $"Metadata provider '{annotation.ProviderId}' does not declare an event-args type for semantic '{semantic}'.");

        var eventArgsType = _compilation.GetTypeByMetadataName(eventArgsMetadataName);
        if (eventArgsType == null)
            return new XamlSetValueHandlerShapeInfo(
                semantic,
                handlerName,
                annotation,
                null,
                null,
                false,
                Array.Empty<IMethodSymbol>(),
                annotation.ProviderId,
                $"Set-value handler event-args type '{eventArgsMetadataName}' could not be resolved.");

        var candidates = new List<IMethodSymbol>();
        var valid = new List<IMethodSymbol>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var inheritanceDepth = 0;
        var validDepth = int.MaxValue;
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers(handlerName).OfType<IMethodSymbol>())
            {
                var signature = method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                if (!signatures.Add(signature)) continue;
                candidates.Add(method);
                if (!method.IsStatic ||
                    method.ReturnsVoid == false ||
                    method.IsGenericMethod ||
                    method.MethodKind != MethodKind.Ordinary ||
                    method.Parameters.Length != 2 ||
                    method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None) ||
                    method.Parameters[0].Type.SpecialType != SpecialType.System_Object ||
                    !SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, eventArgsType))
                    continue;
                if (inheritanceDepth < validDepth)
                {
                    valid.Clear();
                    validDepth = inheritanceDepth;
                }
                if (inheritanceDepth == validDepth) valid.Add(method);
            }
            inheritanceDepth++;
        }
        candidates.Sort(static (left, right) =>
            string.Compare(left.ToDisplayString(), right.ToDisplayString(), StringComparison.Ordinal));
        valid.Sort(static (left, right) =>
            string.Compare(left.ToDisplayString(), right.ToDisplayString(), StringComparison.Ordinal));
        if (valid.Count != 1)
        {
            var detail = valid.Count == 0
                ? "No static void callback matches (object, " + eventArgsMetadataName + ")."
                : "Multiple callbacks match the registered EventHandler<TEventArgs> contract.";
            return new XamlSetValueHandlerShapeInfo(
                semantic,
                handlerName,
                annotation,
                eventArgsType,
                null,
                false,
                candidates,
                annotation.ProviderId,
                detail);
        }
        return new XamlSetValueHandlerShapeInfo(
            semantic,
            handlerName,
            annotation,
            eventArgsType,
            valid[0],
            _compilation.IsSymbolAccessibleWithin(valid[0], _compilation.Assembly),
            candidates,
            annotation.ProviderId,
            error: null);
    }

    private XamlMarkupExtensionShapeInfo? FindMarkupExtensionShape(
        INamedTypeSymbol type,
        string requestedName,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var requiredServices = GetMarkupExtensionRequiredServices(annotations);
        var acceptsEmptyServiceProvider = annotations.Any(static annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.AcceptEmptyServiceProvider,
                StringComparison.Ordinal));
        var identityMatches = new List<MarkupExtensionIdentityMatch>();
        var selectedPriority = int.MinValue;
        foreach (var registration in _shapePolicies)
        {
            if (selectedPriority != int.MinValue && registration.Priority < selectedPriority) break;
            var match = MatchMarkupExtensionIdentity(
                type,
                requestedName,
                registration,
                forceDefaultSuffix: false);
            if (match == null) continue;
            if (selectedPriority == int.MinValue) selectedPriority = registration.Priority;
            identityMatches.Add(match);
        }
        if (identityMatches.Count == 0)
        {
            var fallback = new ShapePolicyRegistration(
                "core.default-shapes",
                int.MinValue,
                XamlSymbolShapePolicy.Default);
            var match = MatchMarkupExtensionIdentity(
                type,
                requestedName,
                fallback,
                forceDefaultSuffix: true);
            if (match == null) return null;
            identityMatches.Add(match);
        }
        if (identityMatches.Count > 1)
        {
            var first = identityMatches[0];
            var providers = string.Join(", ", identityMatches.Select(static match => match.ProviderId));
            if (identityMatches.Skip(1).All(match =>
                    HaveEquivalentMarkupExtensionContracts(first, match)))
            {
                identityMatches.Clear();
                identityMatches.Add(new MarkupExtensionIdentityMatch(
                    first.IdentityKind,
                    first.Identity,
                    first.IdentitySymbol,
                    first.Policy,
                    providers,
                    first.Error));
            }
            else
            {
                return new XamlMarkupExtensionShapeInfo(
                    first.IdentityKind,
                    first.Identity,
                    first.IdentitySymbol,
                    null,
                    Array.Empty<IMethodSymbol>(),
                    requiredServices,
                    acceptsEmptyServiceProvider,
                    providers,
                    $"Markup-extension identity is ambiguous between equal-priority providers: {providers}.");
            }
        }

        var selectedIdentity = identityMatches[0];
        if (selectedIdentity.Error != null)
            return new XamlMarkupExtensionShapeInfo(
                selectedIdentity.IdentityKind,
                selectedIdentity.Identity,
                selectedIdentity.IdentitySymbol,
                null,
                Array.Empty<IMethodSymbol>(),
                requiredServices,
                acceptsEmptyServiceProvider,
                selectedIdentity.ProviderId,
                selectedIdentity.Error);
        var identityKind = selectedIdentity.IdentityKind;
        var identity = selectedIdentity.Identity;
        var identitySymbol = selectedIdentity.IdentitySymbol;
        var identityPolicy = selectedIdentity.Policy;
        var identityProviderId = selectedIdentity.ProviderId;

        var candidates = new List<IMethodSymbol>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        if (identityKind == XamlMarkupExtensionIdentityKind.Interface)
        {
            var interfaces = new[] { identitySymbol! }.Concat(identitySymbol!.AllInterfaces);
            foreach (var @interface in interfaces)
            {
                foreach (var method in @interface.GetMembers().OfType<IMethodSymbol>())
                    AddMarkupExtensionCandidate(
                        method,
                        identityPolicy,
                        allowProtected: false,
                        candidates,
                        signatures);
            }
        }
        else
        {
            var allowProtected = identityKind == XamlMarkupExtensionIdentityKind.BaseType;
            for (var current = type; current != null; current = current.BaseType)
                foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
                    AddMarkupExtensionCandidate(
                        method,
                        identityPolicy,
                        allowProtected,
                        candidates,
                        signatures);
        }

        candidates.Sort(static (left, right) =>
            string.Compare(left.ToDisplayString(), right.ToDisplayString(), StringComparison.Ordinal));
        if (candidates.Count == 0)
            return new XamlMarkupExtensionShapeInfo(
                identityKind,
                identity,
                identitySymbol,
                null,
                candidates,
                requiredServices,
                acceptsEmptyServiceProvider,
                identityProviderId,
                $"No accessible instance method satisfies the registered markup-extension callable contract for '{GetMetadataName(type)}'.");

        var preferredArity = candidates.Any(static candidate => candidate.Parameters.Length == 1) ? 1 : 0;
        var preferred = candidates.Where(candidate => candidate.Parameters.Length == preferredArity).ToArray();
        if (preferred.Length > 1 && preferredArity == 1)
        {
            preferred = preferred.Where(candidate =>
                !preferred.Any(other =>
                    !SymbolEqualityComparer.Default.Equals(candidate, other) &&
                    IsAssignableTo(other.Parameters[0].Type, candidate.Parameters[0].Type) &&
                    !IsAssignableTo(candidate.Parameters[0].Type, other.Parameters[0].Type))).ToArray();
        }

        if (preferred.Length != 1)
        {
            var methods = preferred.Select(static method => method.ToDisplayString()).ToArray();
            return new XamlMarkupExtensionShapeInfo(
                identityKind,
                identity,
                identitySymbol,
                null,
                candidates,
                requiredServices,
                acceptsEmptyServiceProvider,
                identityProviderId,
                $"Markup-extension callable shape is ambiguous between {string.Join(", ", methods)}.");
        }

        string? serviceError = null;
        if (identityPolicy.RequireMarkupExtensionServiceDeclaration &&
            !acceptsEmptyServiceProvider &&
            requiredServices.Count == 0)
            serviceError = "Markup extension must declare either required services or acceptance of an empty service provider.";
        else if (acceptsEmptyServiceProvider && requiredServices.Count != 0)
            serviceError = "Markup extension cannot both require services and accept an empty service provider.";
        else
        {
            var missingServices = requiredServices.Where(service =>
                !identityPolicy.MarkupExtensionAvailableServiceTypeMetadataNames.Contains(
                    GetMetadataName(service), StringComparer.Ordinal)).ToArray();
            if (missingServices.Length != 0)
                serviceError = "Markup extension requires services not supplied by the selected profile: " +
                    string.Join(", ", missingServices.Select(GetMetadataName)) + ".";
        }

        return new XamlMarkupExtensionShapeInfo(
            identityKind,
            identity,
            identitySymbol,
            serviceError == null ? preferred[0] : null,
            candidates,
            requiredServices,
            acceptsEmptyServiceProvider,
            identityProviderId,
            serviceError);
    }

    private MarkupExtensionIdentityMatch? MatchMarkupExtensionIdentity(
        INamedTypeSymbol type,
        string requestedName,
        ShapePolicyRegistration registration,
        bool forceDefaultSuffix)
    {
        var policy = registration.Policy;
        if ((policy.DeclaredFeatures & XamlSymbolShapeFeatures.MarkupExtensionBaseTypes) != 0)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var currentIdentity = GetMetadataName(current.OriginalDefinition);
                if (policy.MarkupExtensionBaseTypeMetadataNames.Contains(
                        currentIdentity,
                        StringComparer.Ordinal))
                    return new MarkupExtensionIdentityMatch(
                        XamlMarkupExtensionIdentityKind.BaseType,
                        currentIdentity,
                        current,
                        policy,
                        registration.ProviderId,
                        error: null);
            }
        }

        if ((policy.DeclaredFeatures & XamlSymbolShapeFeatures.MarkupExtensionInterfaces) != 0)
        {
            var interfaceMatches = type.AllInterfaces
                .Where(@interface => policy.MarkupExtensionInterfaceMetadataNames.Contains(
                    GetMetadataName(@interface.OriginalDefinition),
                    StringComparer.Ordinal))
                .OrderBy(@interface => GetMetadataName(@interface.OriginalDefinition), StringComparer.Ordinal)
                .ToArray();
            if (interfaceMatches.Length != 0)
            {
                var mostSpecific = interfaceMatches.Where(candidate =>
                    !interfaceMatches.Any(other =>
                        !SymbolEqualityComparer.Default.Equals(candidate, other) &&
                        IsAssignableTo(other, candidate) &&
                        !IsAssignableTo(candidate, other))).ToArray();
                if (mostSpecific.Length != 1)
                {
                    var names = mostSpecific.Select(candidate =>
                        GetMetadataName(candidate.OriginalDefinition)).ToArray();
                    return new MarkupExtensionIdentityMatch(
                        XamlMarkupExtensionIdentityKind.Interface,
                        string.Join(", ", names),
                        null,
                        policy,
                        registration.ProviderId,
                        $"Markup-extension interface identity is ambiguous between {string.Join(", ", names)}.");
                }
                return new MarkupExtensionIdentityMatch(
                    XamlMarkupExtensionIdentityKind.Interface,
                    GetMetadataName(mostSpecific[0].OriginalDefinition),
                    mostSpecific[0],
                    policy,
                    registration.ProviderId,
                    error: null);
            }
        }

        if (forceDefaultSuffix ||
            (policy.DeclaredFeatures & XamlSymbolShapeFeatures.MarkupExtensionSuffixes) != 0)
        {
            var suffix = policy.MarkupExtensionSuffixes
                .Where(candidate =>
                    type.Name.EndsWith(candidate, StringComparison.Ordinal) &&
                    (string.Equals(type.Name, requestedName, StringComparison.Ordinal) ||
                     string.Equals(type.Name, requestedName + candidate, StringComparison.Ordinal)))
                .OrderByDescending(static candidate => candidate.Length)
                .ThenBy(static candidate => candidate, StringComparer.Ordinal)
                .FirstOrDefault();
            if (suffix != null)
                return new MarkupExtensionIdentityMatch(
                    XamlMarkupExtensionIdentityKind.Suffix,
                    suffix,
                    null,
                    policy,
                    registration.ProviderId,
                    error: null);
        }
        return null;
    }

    private static bool HaveEquivalentMarkupExtensionContracts(
        MarkupExtensionIdentityMatch left,
        MarkupExtensionIdentityMatch right)
    {
        static bool SetEquals(IReadOnlyList<string> leftValues, IReadOnlyList<string> rightValues) =>
            new HashSet<string>(leftValues, StringComparer.Ordinal).SetEquals(rightValues);

        return left.IdentityKind == right.IdentityKind &&
            string.Equals(left.Identity, right.Identity, StringComparison.Ordinal) &&
            string.Equals(left.Error, right.Error, StringComparison.Ordinal) &&
            SetEquals(
                left.Policy.MarkupExtensionProvideValueMethodNames,
                right.Policy.MarkupExtensionProvideValueMethodNames) &&
            SetEquals(
                left.Policy.MarkupExtensionServiceProviderTypeMetadataNames,
                right.Policy.MarkupExtensionServiceProviderTypeMetadataNames) &&
            SetEquals(
                left.Policy.MarkupExtensionAvailableServiceTypeMetadataNames,
                right.Policy.MarkupExtensionAvailableServiceTypeMetadataNames) &&
            left.Policy.RequireMarkupExtensionServiceDeclaration ==
                right.Policy.RequireMarkupExtensionServiceDeclaration;
    }

    private static IReadOnlyList<ITypeSymbol> GetMarkupExtensionRequiredServices(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var result = new List<ITypeSymbol>();
        for (var index = 0; index < annotations.Count; index++)
        {
            var annotation = annotations[index];
            if (!string.Equals(annotation.Semantic, XamlSchemaSemantics.RequireService, StringComparison.Ordinal) ||
                !annotation.ValueConstant.HasValue) continue;
            var constant = annotation.ValueConstant.Value;
            if (constant.Kind == TypedConstantKind.Array)
            {
                foreach (var item in constant.Values)
                    if (item.Value is ITypeSymbol service &&
                        !result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, service)))
                        result.Add(service);
            }
            else if (constant.Value is ITypeSymbol service &&
                     !result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, service)))
            {
                result.Add(service);
            }
        }
        result.Sort(static (left, right) =>
            string.Compare(GetMetadataName(left), GetMetadataName(right), StringComparison.Ordinal));
        return result;
    }

    private void AddMarkupExtensionCandidate(
        IMethodSymbol method,
        XamlSymbolShapePolicy policy,
        bool allowProtected,
        List<IMethodSymbol> candidates,
        HashSet<string> signatures)
    {
        if (!policy.MarkupExtensionProvideValueMethodNames.Contains(method.Name, StringComparer.Ordinal) ||
            method.IsStatic || method.ReturnsVoid || method.IsGenericMethod ||
            method.MethodKind != MethodKind.Ordinary || method.Parameters.Length > 1 ||
            method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None)) return;
        var accessible = method.DeclaredAccessibility == Accessibility.Public ||
            (allowProtected &&
             (method.DeclaredAccessibility == Accessibility.Protected ||
              method.DeclaredAccessibility == Accessibility.ProtectedOrInternal));
        if (!accessible) return;
        if (method.Parameters.Length == 1 &&
            !policy.MarkupExtensionServiceProviderTypeMetadataNames.Contains(
                GetMetadataName(method.Parameters[0].Type), StringComparer.Ordinal)) return;
        var signature = method.Name + "|" +
            (method.Parameters.Length == 0 ? string.Empty : GetMetadataName(method.Parameters[0].Type));
        if (signatures.Add(signature)) candidates.Add(method);
    }

    private ITypeSymbol? GetMarkupExtensionReturnType(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        XamlMarkupExtensionShapeInfo shape)
    {
        for (var index = 0; index < annotations.Count; index++)
        {
            var annotation = annotations[index];
            if (string.Equals(annotation.Semantic, XamlSchemaSemantics.MarkupExtensionReturnType, StringComparison.Ordinal) &&
                annotation.ValueConstant?.Value is ITypeSymbol declaredType)
                return declaredType;
        }

        return shape.ProvideValueMethod?.ReturnType;
    }

    private XamlTextSyntaxInfo GetTextSyntax(
        ITypeSymbol valueType,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        XamlTextSyntaxInfo? fallback)
    {
        if (valueType is INamedTypeSymbol { IsGenericType: true } nullable &&
            nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            return GetTextSyntax(nullable.TypeArguments[0], annotations, fallback);
        var createFromStringAnnotations = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.CreateFromString,
                StringComparison.Ordinal)).ToArray();
        var converterAnnotations = annotations.Where(annotation =>
            string.Equals(
                annotation.Semantic,
                XamlSchemaSemantics.TypeConverter,
                StringComparison.Ordinal)).ToArray();
        if (createFromStringAnnotations.Length != 0 &&
            converterAnnotations.Length != 0)
        {
            return new XamlTextSyntaxInfo(
                XamlTextSyntaxKind.Error,
                annotation: createFromStringAnnotations[0],
                error:
                    "A type cannot select both create-from-string and type-converter text construction at the same precedence.");
        }
        if (createFromStringAnnotations.Length != 0)
        {
            var annotation = createFromStringAnnotations[0];
            var shape = FindStaticCreateFromStringShape(
                valueType,
                annotation);
            return new XamlTextSyntaxInfo(
                shape.IsValid
                    ? XamlTextSyntaxKind.CreateFromString
                    : XamlTextSyntaxKind.Error,
                converterType: shape.FactoryType,
                annotation: annotation,
                error: shape.Error,
                createFromStringShape: shape);
        }
        for (var index = 0; index < annotations.Count; index++)
        {
            var annotation = annotations[index];
            if (!string.Equals(annotation.Semantic, XamlSchemaSemantics.TypeConverter, StringComparison.Ordinal)) continue;
            var converter = ResolveConverterType(annotation);
            if (converter == null)
                return new XamlTextSyntaxInfo(XamlTextSyntaxKind.Error, annotation: annotation,
                    error: $"Type converter '{annotation.Value}' could not be resolved.");
            var converterBase = _compilation.GetTypeByMetadataName("System.ComponentModel.TypeConverter");
            var constructor = converter.InstanceConstructors.SingleOrDefault(static candidate =>
                candidate.Parameters.Length == 0 &&
                candidate.DeclaredAccessibility == Accessibility.Public);
            IMethodSymbol? conversionMethod = null;
            for (var current = converter; current != null && conversionMethod == null; current = current.BaseType)
            {
                var methods = current.GetMembers("ConvertFromInvariantString")
                    .OfType<IMethodSymbol>()
                    .Where(static method =>
                        !method.IsStatic &&
                        !method.IsGenericMethod &&
                        method.MethodKind == MethodKind.Ordinary &&
                        method.DeclaredAccessibility == Accessibility.Public &&
                        method.Parameters.Length == 1 &&
                        method.Parameters[0].RefKind == RefKind.None &&
                        method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
                        method.ReturnType.SpecialType == SpecialType.System_Object)
                    .ToArray();
                if (methods.Length == 1) conversionMethod = methods[0];
                else if (methods.Length > 1)
                    return new XamlTextSyntaxInfo(
                        XamlTextSyntaxKind.Error,
                        converterType: converter,
                        annotation: annotation,
                        error: $"Type converter '{GetMetadataName(converter)}' has an ambiguous ConvertFromInvariantString contract.");
            }
            var constructible = !converter.IsAbstract &&
                converter.DeclaredAccessibility == Accessibility.Public &&
                constructor != null &&
                conversionMethod != null;
            if (converterBase == null || !IsAssignableTo(converter, converterBase) || !constructible)
                return new XamlTextSyntaxInfo(XamlTextSyntaxKind.Error, converterType: converter, annotation: annotation,
                    error: $"Type converter '{GetMetadataName(converter)}' must be a public, non-abstract TypeConverter with a public parameterless constructor and a public ConvertFromInvariantString(string) method.");
            return new XamlTextSyntaxInfo(
                XamlTextSyntaxKind.TypeConverter,
                converterType: converter,
                annotation: annotation,
                createFromStringShape: new XamlCreateFromStringShapeInfo(
                    XamlCreateFromStringInvocationKind.ConverterInstance,
                    valueType as INamedTypeSymbol,
                    converter,
                    constructor,
                    conversionMethod,
                    conversionMethod!.Name,
                    new[] { conversionMethod },
                    annotation,
                    annotation.ProviderId,
                    error: null));
        }

        if (fallback != null && fallback.Kind != XamlTextSyntaxKind.None) return fallback;
        if (valueType.TypeKind == TypeKind.Enum)
            return new XamlTextSyntaxInfo(XamlTextSyntaxKind.Enumeration);
        var intrinsic = XamlIntrinsicTextSyntax.Create(valueType);
        if (intrinsic.Kind != XamlTextSyntaxKind.None) return intrinsic;
        var metadataName = GetMetadataName(valueType);
        if (_shapePolicy.ProfileTextSyntaxTypeMetadataNames.Contains(metadataName, StringComparer.Ordinal))
            return new XamlTextSyntaxInfo(XamlTextSyntaxKind.Profile);
        return XamlTextSyntaxInfo.None;
    }

    private XamlCreateFromStringShapeInfo FindStaticCreateFromStringShape(
        ITypeSymbol valueType,
        XamlSchemaAnnotationInfo annotation)
    {
        var targetType = valueType as INamedTypeSymbol;
        var requestedName = annotation.Value ?? string.Empty;
        INamedTypeSymbol? factoryType = targetType;
        var methodName = requestedName;
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var separator = requestedName.LastIndexOf('.');
            if (separator > 0)
            {
                var factoryName = requestedName.Substring(0, separator);
                methodName = requestedName.Substring(separator + 1);
                factoryType = _compilation.GetTypeByMetadataName(factoryName);
                if (factoryType == null && targetType != null)
                    factoryType = ResolveNestedFactoryType(
                        targetType,
                        factoryName);
            }
        }

        var candidates = factoryType == null ||
                         string.IsNullOrWhiteSpace(methodName)
            ? Array.Empty<IMethodSymbol>()
            : FindNearestNamedMethods(factoryType, methodName);
        var valid = candidates.Where(method =>
            method.MethodKind == MethodKind.Ordinary &&
            method.IsStatic &&
            !method.IsGenericMethod &&
            method.DeclaredAccessibility == Accessibility.Public &&
            _compilation.IsSymbolAccessibleWithin(
                method,
                _compilation.Assembly) &&
            !method.ReturnsVoid &&
            method.Parameters.Length == 1 &&
            method.Parameters[0].RefKind == RefKind.None &&
            method.Parameters[0].Type.SpecialType ==
                SpecialType.System_String &&
            targetType != null &&
            _compilation.ClassifyConversion(
                method.ReturnType,
                targetType).Exists).ToArray();

        string? error = null;
        IMethodSymbol? selected = null;
        if (targetType == null)
            error = "Create-from-string metadata requires a named target type.";
        else if (string.IsNullOrWhiteSpace(requestedName) ||
                 string.IsNullOrWhiteSpace(methodName))
            error =
                "Create-from-string metadata requires a non-empty method name.";
        else if (factoryType == null)
            error =
                $"Create-from-string factory type in '{requestedName}' could not be resolved.";
        else if (valid.Length == 1)
            selected = valid[0];
        else if (valid.Length == 0)
            error =
                $"Create-from-string method '{requestedName}' must be one public static, non-generic method accepting one String value and returning a value convertible to '{GetMetadataName(targetType)}'.";
        else
            error =
                $"Create-from-string method '{requestedName}' is ambiguous.";

        return new XamlCreateFromStringShapeInfo(
            XamlCreateFromStringInvocationKind.StaticMethod,
            targetType,
            factoryType,
            constructor: null,
            selected,
            requestedName,
            candidates,
            annotation,
            annotation.ProviderId,
            error);
    }

    private static INamedTypeSymbol? ResolveNestedFactoryType(
        INamedTypeSymbol targetType,
        string requestedName)
    {
        var current = targetType;
        foreach (var segment in requestedName.Split('.'))
        {
            if (string.IsNullOrWhiteSpace(segment))
                return null;
            var matches = current.GetTypeMembers(segment);
            if (matches.Length != 1)
                return null;
            current = matches[0];
        }
        return current;
    }

    private static IReadOnlyList<IMethodSymbol> FindNearestNamedMethods(
        INamedTypeSymbol type,
        string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var candidates = current.GetMembers(name)
                .OfType<IMethodSymbol>()
                .ToArray();
            if (candidates.Length != 0)
                return candidates;
        }
        return Array.Empty<IMethodSymbol>();
    }

    private INamedTypeSymbol? ResolveConverterType(XamlSchemaAnnotationInfo annotation)
    {
        if (annotation.ValueConstant?.Value is INamedTypeSymbol symbol) return symbol;
        if (string.IsNullOrWhiteSpace(annotation.Value)) return null;
        var metadataName = annotation.Value!;
        var separator = metadataName.IndexOf(',');
        if (separator >= 0) metadataName = metadataName.Substring(0, separator).Trim();
        return _compilation.GetTypeByMetadataName(metadataName);
    }

    private static bool Implements(ITypeSymbol symbol, string interfaceName)
    {
        if (string.Equals(symbol.ToDisplayString(), interfaceName, StringComparison.Ordinal))
        {
            return true;
        }

        var interfaces = symbol.AllInterfaces;
        for (var index = 0; index < interfaces.Length; index++)
        {
            if (string.Equals(interfaces[index].ToDisplayString(), interfaceName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsGeneric(ITypeSymbol symbol, string interfaceName)
    {
        var interfaces = symbol.AllInterfaces;
        for (var index = 0; index < interfaces.Length; index++)
        {
            if (string.Equals(interfaces[index].OriginalDefinition.ToDisplayString(), interfaceName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private XamlNameScopeShapeInfo? FindNameScopeShape(
        INamedTypeSymbol type)
    {
        var interfaceMatches = new List<(ShapePolicyRegistration Registration, INamedTypeSymbol Interface)>();
        foreach (var registration in _shapePolicies)
        {
            var policy = registration.Policy;
            if ((policy.DeclaredFeatures & XamlSymbolShapeFeatures.NameScopeInterfaces) == 0)
                continue;
            foreach (var match in type.AllInterfaces.Where(@interface =>
                policy.NameScopeInterfaceMetadataNames.Contains(
                    GetMetadataName(@interface.OriginalDefinition),
                    StringComparer.Ordinal)))
                interfaceMatches.Add((registration, match));
        }
        if (interfaceMatches.Count != 0)
        {
            var winningPriority = interfaceMatches[0].Registration.Priority;
            var winners = interfaceMatches
                .Where(match => match.Registration.Priority == winningPriority)
                .ToArray();
            var identities = winners
                .Select(match => GetMetadataName(match.Interface.OriginalDefinition))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (identities.Length != 1)
                return new XamlNameScopeShapeInfo(
                    XamlNameScopeIdentityKind.Interface,
                    winners[0].Interface,
                    null,
                    null,
                    null,
                    winners.SelectMany(match => GetInterfaceMethods(match.Interface)).ToArray(),
                    winners[0].Registration.ProviderId,
                    $"Type '{GetMetadataName(type)}' matches incompatible equal-priority namescope interfaces: {string.Join(", ", identities)}.");
            var selected = winners[0];
            return CreateNameScopeShape(
                XamlNameScopeIdentityKind.Interface,
                selected.Interface,
                GetInterfaceMethods(selected.Interface),
                selected.Registration.Policy,
                selected.Registration.ProviderId,
                requirePublic: false);
        }

        var duckMatches = new List<(ShapePolicyRegistration Registration, IMethodSymbol[] Candidates)>();
        foreach (var registration in _shapePolicies)
        {
            var policy = registration.Policy;
            if ((policy.DeclaredFeatures & XamlSymbolShapeFeatures.NameScopeDuckTyping) == 0 ||
                !policy.InferNameScopeFromMethods)
                continue;
            var methodNames = policy.NameScopeRegisterMethodNames
                .Concat(policy.NameScopeUnregisterMethodNames)
                .Concat(policy.NameScopeFindMethodNames)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var candidates = methodNames
                .SelectMany(name => GetNearestNamedMethods(type, name))
                .GroupBy(
                    method => method.GetDocumentationCommentId() ?? method.ToDisplayString(),
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (candidates.Length == 0)
                continue;
            duckMatches.Add((registration, candidates));
        }
        if (duckMatches.Count != 0)
        {
            var winningPriority = duckMatches[0].Registration.Priority;
            var winners = duckMatches
                .Where(match => match.Registration.Priority == winningPriority)
                .ToArray();
            var contracts = winners.Select(match =>
                    string.Join("|", match.Registration.Policy.NameScopeRegisterMethodNames) + ";" +
                    string.Join("|", match.Registration.Policy.NameScopeUnregisterMethodNames) + ";" +
                    string.Join("|", match.Registration.Policy.NameScopeFindMethodNames))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (contracts.Length != 1)
                return new XamlNameScopeShapeInfo(
                    XamlNameScopeIdentityKind.DuckMethods,
                    type,
                    null,
                    null,
                    null,
                    winners.SelectMany(match => match.Candidates).ToArray(),
                    winners[0].Registration.ProviderId,
                    $"Type '{GetMetadataName(type)}' matches incompatible equal-priority namescope duck contracts.");
            var selected = winners[0];
            return CreateNameScopeShape(
                XamlNameScopeIdentityKind.DuckMethods,
                type,
                selected.Candidates,
                selected.Registration.Policy,
                selected.Registration.ProviderId,
                requirePublic: true);
        }
        return null;
    }

    private XamlMarkupExtensionReceiverShapeInfo? FindMarkupExtensionReceiverShape(
        INamedTypeSymbol type)
    {
        var interfaceMatches =
            new List<(ShapePolicyRegistration Registration, INamedTypeSymbol Interface)>();
        foreach (var registration in _shapePolicies)
        {
            var policy = registration.Policy;
            if ((policy.DeclaredFeatures & XamlSymbolShapeFeatures.MarkupExtensionReceivers) == 0)
                continue;
            foreach (var match in type.AllInterfaces.Where(@interface =>
                policy.MarkupExtensionReceiverInterfaceMetadataNames.Contains(
                    GetMetadataName(@interface.OriginalDefinition),
                    StringComparer.Ordinal)))
                interfaceMatches.Add((registration, match));
        }

        if (interfaceMatches.Count != 0)
        {
            var winningPriority = interfaceMatches[0].Registration.Priority;
            var winners = interfaceMatches
                .Where(match => match.Registration.Priority == winningPriority)
                .ToArray();
            var contracts = winners.Select(match =>
                    GetMetadataName(match.Interface.OriginalDefinition) + ";" +
                    CanonicalMarkupExtensionReceiverContract(match.Registration.Policy))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (contracts.Length != 1)
            {
                return new XamlMarkupExtensionReceiverShapeInfo(
                    XamlMarkupExtensionReceiverIdentityKind.Interface,
                    winners[0].Interface,
                    null,
                    null,
                    null,
                    winners.SelectMany(match => GetInterfaceMethods(match.Interface))
                        .OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal)
                        .ToArray(),
                    winners[0].Registration.ProviderId,
                    $"Type '{GetMetadataName(type)}' matches incompatible equal-priority markup-extension receiver interface contracts.");
            }

            var selected = winners[0];
            return CreateMarkupExtensionReceiverShape(
                XamlMarkupExtensionReceiverIdentityKind.Interface,
                selected.Interface,
                GetInterfaceMethods(selected.Interface),
                selected.Registration.Policy,
                selected.Registration.ProviderId,
                requirePublic: false);
        }

        var duckMatches =
            new List<(ShapePolicyRegistration Registration, IMethodSymbol[] Candidates)>();
        foreach (var registration in _shapePolicies)
        {
            var policy = registration.Policy;
            if ((policy.DeclaredFeatures & XamlSymbolShapeFeatures.MarkupExtensionReceivers) == 0 ||
                !policy.InferMarkupExtensionReceiversFromMethods)
                continue;
            var candidates = policy.MarkupExtensionReceiverMethodNames
                .SelectMany(name => GetNearestNamedMethods(type, name))
                .GroupBy(
                    method => method.GetDocumentationCommentId() ?? method.ToDisplayString(),
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length != 0)
                duckMatches.Add((registration, candidates));
        }

        if (duckMatches.Count == 0)
            return null;

        var duckPriority = duckMatches[0].Registration.Priority;
        var duckWinners = duckMatches
            .Where(match => match.Registration.Priority == duckPriority)
            .ToArray();
        var duckContracts = duckWinners
            .Select(match => CanonicalMarkupExtensionReceiverContract(match.Registration.Policy))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (duckContracts.Length != 1)
        {
            return new XamlMarkupExtensionReceiverShapeInfo(
                XamlMarkupExtensionReceiverIdentityKind.DuckMethod,
                type,
                null,
                null,
                null,
                duckWinners.SelectMany(match => match.Candidates)
                    .OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal)
                    .ToArray(),
                duckWinners[0].Registration.ProviderId,
                $"Type '{GetMetadataName(type)}' matches incompatible equal-priority markup-extension receiver duck contracts.");
        }

        var duckSelected = duckWinners[0];
        return CreateMarkupExtensionReceiverShape(
            XamlMarkupExtensionReceiverIdentityKind.DuckMethod,
            type,
            duckSelected.Candidates,
            duckSelected.Registration.Policy,
            duckSelected.Registration.ProviderId,
            requirePublic: true);
    }

    private XamlMarkupExtensionReceiverShapeInfo CreateMarkupExtensionReceiverShape(
        XamlMarkupExtensionReceiverIdentityKind identityKind,
        ITypeSymbol identityType,
        IEnumerable<IMethodSymbol> availableMethods,
        XamlSymbolShapePolicy policy,
        string providerId,
        bool requirePublic)
    {
        var stringType = _compilation.GetSpecialType(SpecialType.System_String);
        var markupExtensionTypes = policy.MarkupExtensionReceiverMarkupExtensionTypeMetadataNames
            .Select(_compilation.GetTypeByMetadataName)
            .Where(static type => type != null)
            .Cast<ITypeSymbol>()
            .ToArray();
        var serviceProviderTypes = policy.MarkupExtensionReceiverServiceProviderTypeMetadataNames
            .Select(_compilation.GetTypeByMetadataName)
            .Where(static type => type != null)
            .Cast<ITypeSymbol>()
            .ToArray();
        var candidates = availableMethods
            .Where(method =>
                policy.MarkupExtensionReceiverMethodNames.Contains(
                    method.Name,
                    StringComparer.Ordinal))
            .GroupBy(
                method => method.GetDocumentationCommentId() ?? method.ToDisplayString(),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();
        var valid = candidates.Where(method =>
            method.MethodKind == MethodKind.Ordinary &&
            !method.IsStatic &&
            !method.IsGenericMethod &&
            method.ReturnsVoid &&
            (!requirePublic ||
             (method.DeclaredAccessibility == Accessibility.Public &&
              _compilation.IsSymbolAccessibleWithin(method, _compilation.Assembly))) &&
            method.Parameters.Length == 3 &&
            method.Parameters.All(static parameter => parameter.RefKind == RefKind.None) &&
            SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, stringType) &&
            markupExtensionTypes.Any(type =>
                SymbolEqualityComparer.Default.Equals(type, method.Parameters[1].Type)) &&
            serviceProviderTypes.Any(type =>
                SymbolEqualityComparer.Default.Equals(type, method.Parameters[2].Type)))
            .ToArray();

        string? error = null;
        if (policy.MarkupExtensionReceiverMarkupExtensionTypeMetadataNames.Count == 0 ||
            markupExtensionTypes.Length == 0)
            error = "The receiver provider must declare at least one resolvable markup-extension parameter type.";
        else if (policy.MarkupExtensionReceiverServiceProviderTypeMetadataNames.Count == 0 ||
                 serviceProviderTypes.Length == 0)
            error = "The receiver provider must declare at least one resolvable service-provider parameter type.";
        else if (valid.Length != 1)
            error =
                "Markup-extension receiver shape requires exactly one accessible instance void " +
                "callable with (String, registered markup-extension, registered service-provider) " +
                $"parameters; found {valid.Length}.";

        var selected = error == null ? valid[0] : null;
        return new XamlMarkupExtensionReceiverShapeInfo(
            identityKind,
            identityType,
            selected,
            selected?.Parameters[1].Type,
            selected?.Parameters[2].Type,
            candidates,
            providerId,
            error);
    }

    private static string CanonicalMarkupExtensionReceiverContract(
        XamlSymbolShapePolicy policy) =>
        string.Join(",", policy.MarkupExtensionReceiverMethodNames) + ";" +
        string.Join(",", policy.MarkupExtensionReceiverMarkupExtensionTypeMetadataNames) + ";" +
        string.Join(",", policy.MarkupExtensionReceiverServiceProviderTypeMetadataNames) + ";" +
        (policy.InferMarkupExtensionReceiversFromMethods ? "true" : "false");

    private XamlNameScopeShapeInfo CreateNameScopeShape(
        XamlNameScopeIdentityKind identityKind,
        ITypeSymbol identityType,
        IEnumerable<IMethodSymbol> availableMethods,
        XamlSymbolShapePolicy policy,
        string providerId,
        bool requirePublic)
    {
        var stringType = _compilation.GetSpecialType(SpecialType.System_String);
        var objectType = _compilation.GetSpecialType(SpecialType.System_Object);
        var candidates = availableMethods
            .Where(method =>
                !method.IsStatic &&
                !method.IsGenericMethod &&
                (!requirePublic || method.DeclaredAccessibility == Accessibility.Public))
            .GroupBy(
                method => method.GetDocumentationCommentId() ?? method.ToDisplayString(),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();
        var registerCandidates = candidates.Where(method =>
            policy.NameScopeRegisterMethodNames.Contains(method.Name, StringComparer.Ordinal) &&
            method.ReturnsVoid &&
            method.Parameters.Length == 2 &&
            method.Parameters.All(static parameter => parameter.RefKind == RefKind.None) &&
            SymbolEqualityComparer.IncludeNullability.Equals(method.Parameters[0].Type, stringType) &&
            SymbolEqualityComparer.IncludeNullability.Equals(method.Parameters[1].Type, objectType))
            .ToArray();
        var unregisterCandidates = candidates.Where(method =>
            policy.NameScopeUnregisterMethodNames.Contains(method.Name, StringComparer.Ordinal) &&
            method.ReturnsVoid &&
            method.Parameters.Length == 1 &&
            method.Parameters[0].RefKind == RefKind.None &&
            SymbolEqualityComparer.IncludeNullability.Equals(method.Parameters[0].Type, stringType))
            .ToArray();
        var findCandidates = candidates.Where(method =>
            policy.NameScopeFindMethodNames.Contains(method.Name, StringComparer.Ordinal) &&
            !method.ReturnsVoid &&
            method.Parameters.Length == 1 &&
            method.Parameters[0].RefKind == RefKind.None &&
            SymbolEqualityComparer.IncludeNullability.Equals(method.Parameters[0].Type, stringType))
            .ToArray();
        var register = registerCandidates.Length == 1 ? registerCandidates[0] : null;
        var unregister = unregisterCandidates.Length == 1 ? unregisterCandidates[0] : null;
        var find = findCandidates.Length == 1 ? findCandidates[0] : null;
        string? error = null;
        if (register == null || unregister == null || find == null)
        {
            error =
                $"Namescope shape requires one register(String, Object), one unregister(String), and one non-void find(String) method; found " +
                $"{registerCandidates.Length}, {unregisterCandidates.Length}, and {findCandidates.Length}.";
        }
        return new XamlNameScopeShapeInfo(
            identityKind,
            identityType,
            register,
            unregister,
            find,
            candidates,
            providerId,
            error);
    }

    private static IEnumerable<IMethodSymbol> GetInterfaceMethods(
        INamedTypeSymbol @interface)
    {
        foreach (var method in @interface.GetMembers().OfType<IMethodSymbol>())
            yield return method;
        foreach (var inherited in @interface.AllInterfaces)
        foreach (var method in inherited.GetMembers().OfType<IMethodSymbol>())
            yield return method;
    }

    private XamlAliasedMemberShapeInfo? FindAliasedMemberShape(
        INamedTypeSymbol attributedType,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string semantic,
        bool allowAttachableOwner,
        bool requiresWrite)
    {
        var matches = annotations.Where(annotation =>
            string.Equals(annotation.Semantic, semantic, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
            return null;

        var annotation = matches[0];
        var declaredName = annotation.Value;
        if (matches.Length != 1)
            return InvalidAliasedMemberShape(
                annotation,
                semantic,
                declaredName,
                "Aliased-member metadata is ambiguous at the winning precedence.");
        if (string.IsNullOrWhiteSpace(declaredName))
            return InvalidAliasedMemberShape(
                annotation,
                semantic,
                declaredName,
                "The aliased member name must be a non-empty string.");

        var arguments = annotation.Attribute.ConstructorArguments;
        ITypeSymbol? attachedOwner = null;
        if (arguments.Length > 1)
        {
            if (!allowAttachableOwner)
                return InvalidAliasedMemberShape(
                    annotation,
                    semantic,
                    declaredName,
                    "This aliased-member contract does not permit an attachable owner type.");
            attachedOwner = arguments[1].Value as ITypeSymbol;
            if (attachedOwner == null || attachedOwner.TypeKind == TypeKind.Error)
                return InvalidAliasedMemberShape(
                    annotation,
                    semantic,
                    declaredName,
                    "The attachable namescope owner must be an exact resolvable Type constant.");
        }

        if (attachedOwner == null)
        {
            var properties = GetNearestAliasedProperties(
                attributedType,
                declaredName!).ToArray();
            if (properties.Length != 1)
                return InvalidAliasedMemberShape(
                    annotation,
                    semantic,
                    declaredName,
                    properties.Length == 0
                        ? $"Member '{declaredName}' is not a public readable instance property on '{GetMetadataName(attributedType)}'."
                        : $"Member '{declaredName}' is ambiguous on '{GetMetadataName(attributedType)}'.");
            var property = properties[0];
            if (requiresWrite &&
                property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                return new XamlAliasedMemberShapeInfo(
                    annotation,
                    semantic,
                    declaredName,
                    null,
                    property,
                    null,
                    null,
                    $"Member '{declaredName}' must have a public setter for this XAML language alias.");
            return new XamlAliasedMemberShapeInfo(
                annotation,
                semantic,
                declaredName,
                null,
                property,
                null,
                null,
                error: null);
        }

        if (attachedOwner is not INamedTypeSymbol ownerType)
            return InvalidAliasedMemberShape(
                annotation,
                semantic,
                declaredName,
                "The attachable namescope owner must be a named CLR type.");

        var getters = GetNearestNamedMethods(ownerType, "Get" + declaredName)
            .Where(method =>
                method.IsStatic &&
                !method.IsGenericMethod &&
                method.DeclaredAccessibility == Accessibility.Public &&
                !method.ReturnsVoid &&
                method.Parameters.Length == 1 &&
                method.Parameters[0].RefKind == RefKind.None &&
                IsAssignableTo(attributedType, method.Parameters[0].Type))
            .ToArray();
        var setters = GetNearestNamedMethods(ownerType, "Set" + declaredName)
            .Where(method =>
                method.IsStatic &&
                !method.IsGenericMethod &&
                method.DeclaredAccessibility == Accessibility.Public &&
                method.ReturnsVoid &&
                method.Parameters.Length == 2 &&
                method.Parameters.All(static parameter => parameter.RefKind == RefKind.None) &&
                IsAssignableTo(attributedType, method.Parameters[0].Type))
            .ToArray();
        var getter = SelectMostSpecificReceiver(getters, attributedType);
        var setter = SelectMostSpecificReceiver(setters, attributedType);
        if ((getters.Length != 0 && getter == null) ||
            (setters.Length != 0 && setter == null))
            return new XamlAliasedMemberShapeInfo(
                annotation,
                semantic,
                declaredName,
                ownerType,
                null,
                getter,
                setter,
                $"Attachable member '{GetMetadataName(ownerType)}.{declaredName}' is ambiguous.");
        if (getter == null && setter == null)
            return new XamlAliasedMemberShapeInfo(
                annotation,
                semantic,
                declaredName,
                ownerType,
                null,
                null,
                null,
                $"Attachable member '{GetMetadataName(ownerType)}.{declaredName}' has no applicable public static getter or setter.");
        if (getter != null &&
            setter != null &&
            !SymbolEqualityComparer.IncludeNullability.Equals(
                getter.ReturnType,
                setter.Parameters[1].Type))
            return new XamlAliasedMemberShapeInfo(
                annotation,
                semantic,
                declaredName,
                ownerType,
                null,
                getter,
                setter,
                $"Attachable member '{GetMetadataName(ownerType)}.{declaredName}' has incompatible getter and setter value types.");
        return new XamlAliasedMemberShapeInfo(
            annotation,
            semantic,
            declaredName,
            ownerType,
            null,
            getter,
            setter,
            error: null);
    }

    private static IEnumerable<IPropertySymbol> GetNearestAliasedProperties(
        INamedTypeSymbol type,
        string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var properties = current.GetMembers(name)
                .OfType<IPropertySymbol>()
                .Where(static property =>
                    !property.IsStatic &&
                    property.DeclaredAccessibility == Accessibility.Public &&
                    property.GetMethod?.DeclaredAccessibility == Accessibility.Public)
                .ToArray();
            if (properties.Length == 0)
                continue;
            foreach (var property in properties)
                yield return property;
            yield break;
        }
    }

    private static XamlAliasedMemberShapeInfo InvalidAliasedMemberShape(
        XamlSchemaAnnotationInfo annotation,
        string semantic,
        string? declaredName,
        string error) =>
        new(
            annotation,
            semantic,
            declaredName,
            null,
            null,
            null,
            null,
            error);

    private string? GetAliasedMemberName(
        INamedTypeSymbol symbol,
        IReadOnlyList<XamlSchemaAnnotationInfo> typeAnnotations,
        string semantic,
        bool allowMemberAnnotation)
    {
        for (var index = 0; index < typeAnnotations.Count; index++)
        {
            var annotation = typeAnnotations[index];
            if (string.Equals(annotation.Semantic, semantic, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(annotation.Value))
            {
                return annotation.Value;
            }
        }

        if (!allowMemberAnnotation) return null;
        for (var current = symbol; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                var annotations = GetAnnotations(member, XamlSchemaAttributeTargets.Member, includeInherited: false);
                for (var index = 0; index < annotations.Count; index++)
                {
                    if (string.Equals(annotations[index].Semantic, semantic, StringComparison.Ordinal))
                    {
                        return member.Name;
                    }
                }
            }
        }
        return null;
    }

    private IReadOnlyList<XamlSchemaAnnotationInfo> GetAnnotations(
        ISymbol symbol,
        XamlSchemaAttributeTargets target,
        bool includeInherited)
    {
        var result = new List<XamlSchemaAnnotationInfo>();
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        var selected = new Dictionary<string, XamlSchemaAnnotationInfo>(StringComparer.Ordinal);
        var current = symbol;
        var inheritanceDepth = 0;
        while (current != null)
        {
            foreach (var attribute in current.GetAttributes())
            {
                var attributeName = attribute.AttributeClass == null
                    ? string.Empty
                    : GetMetadataName(attribute.AttributeClass);
                for (var index = 0; index < _attributeRules.Count; index++)
                {
                    var registration = _attributeRules[index];
                    var rule = registration.Rule;
                    if ((rule.Targets & target) == 0 ||
                        !string.Equals(rule.AttributeMetadataName, attributeName, StringComparison.Ordinal) ||
                        (inheritanceDepth != 0 && !rule.Inherited))
                    {
                        continue;
                    }
                    var annotationConstant = GetRuleValue(attribute, rule);
                    var annotationValue = FormatRuleValue(annotationConstant);
                    var candidate = new XamlSchemaAnnotationInfo(
                        rule.Semantic,
                        attribute,
                        current,
                        registration.ProviderId,
                        registration.Priority,
                        annotationValue,
                        annotationConstant,
                        inheritanceDepth,
                        rule.AllowMultiple);
                    if (rule.AllowMultiple)
                    {
                        var identity = rule.Semantic + "\0" + attributeName + "\0" +
                            FormatAttributeIdentity(attribute);
                        if (repeated.Add(identity)) result.Add(candidate);
                    }
                    else if (!selected.TryGetValue(rule.Semantic, out var winner))
                    {
                        selected.Add(rule.Semantic, candidate);
                        result.Add(candidate);
                    }
                    else if (winner.ProviderPriority == candidate.ProviderPriority &&
                             winner.InheritanceDepth == candidate.InheritanceDepth &&
                             !string.Equals(winner.Value, candidate.Value, StringComparison.Ordinal))
                    {
                        // Retain equally authoritative incompatible evidence so the binder can
                        // report it at each XAML use site instead of silently choosing a provider.
                        result.Add(candidate);
                    }
                }
            }

            if (!includeInherited) break;
            current = GetInheritedAnnotationTarget(current);
            if (current == null) break;
            inheritanceDepth++;
        }

        return result;
    }

    private static ISymbol? GetInheritedAnnotationTarget(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol named => named.BaseType,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            IMethodSymbol method => method.OverriddenMethod,
            _ => null
        };

    private static TypedConstant? GetRuleValue(AttributeData attribute, XamlSchemaAttributeRule rule)
    {
        TypedConstant value;
        switch (rule.ValueSource)
        {
            case XamlSchemaAttributeValueSource.ConstructorArgument:
                if (rule.ConstructorArgumentIndex < 0 ||
                    rule.ConstructorArgumentIndex >= attribute.ConstructorArguments.Length) return null;
                value = attribute.ConstructorArguments[rule.ConstructorArgumentIndex];
                break;
            case XamlSchemaAttributeValueSource.NamedArgument:
                var found = false;
                value = default;
                foreach (var pair in attribute.NamedArguments)
                {
                    if (!string.Equals(pair.Key, rule.NamedArgument, StringComparison.Ordinal)) continue;
                    value = pair.Value;
                    found = true;
                    break;
                }
                if (!found) return null;
                break;
            default:
                return null;
        }
        return value;
    }

    private static string? FormatRuleValue(TypedConstant? optional)
    {
        if (!optional.HasValue) return null;
        var value = optional.Value;
        if (value.Kind == TypedConstantKind.Array)
            return "[" + string.Join(",", value.Values.Select(static item => FormatRuleValue(item))) + "]";
        if (value.Value is ITypeSymbol type) return GetMetadataName(type);
        return value.Value?.ToString();
    }

    private static string FormatAttributeIdentity(AttributeData attribute)
    {
        var constructor = string.Join(
            "\u001f",
            attribute.ConstructorArguments.Select(static argument =>
                FormatRuleValue(argument) ?? "<null>"));
        var named = string.Join(
            "\u001f",
            attribute.NamedArguments
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair =>
                    pair.Key + "=" + (FormatRuleValue(pair.Value) ?? "<null>")));
        return constructor + "\u001e" + named;
    }

    private XamlCollectionShapeInfo? FindCollectionShape(ITypeSymbol symbol)
    {
        if (!_shapePolicy.InferCollectionsFromAddMethods || symbol is not INamedTypeSymbol named) return null;

        var classMethods = new List<IMethodSymbol>();
        for (var current = named; current != null; current = current.BaseType)
        {
            foreach (var methodName in _shapePolicy.CollectionAddMethodNames)
            {
                foreach (var member in current.GetMembers(methodName))
                {
                    if (member is not IMethodSymbol method || method.IsStatic ||
                        !IsAccessible(method.DeclaredAccessibility)) continue;
                    if (IsValidInsertionMethod(method)) classMethods.Add(method);
                }
            }
        }

        var classShape = SelectUniqueCollectionShape(classMethods);
        if (classShape != null || classMethods.Count != 0) return classShape;

        var interfaceMethods = new List<IMethodSymbol>();
        foreach (var candidateType in named.AllInterfaces)
        {
            foreach (var methodName in _shapePolicy.CollectionAddMethodNames)
            {
                foreach (var member in candidateType.GetMembers(methodName))
                {
                    if (member is IMethodSymbol method && IsValidInsertionMethod(method))
                    {
                        AddUniqueSignature(interfaceMethods, method);
                    }
                }
            }
        }

        var interfaceShape = SelectUniqueCollectionShape(interfaceMethods);
        if (interfaceShape != null || interfaceMethods.Count != 0) return interfaceShape;

        foreach (var contract in _shapePolicy.AddChildInterfaceMetadataNames)
        {
            foreach (var candidateType in named.AllInterfaces)
            {
                if (!string.Equals(GetMetadataName(candidateType.OriginalDefinition), contract, StringComparison.Ordinal)) continue;
                var addChildMethods = candidateType.GetMembers("AddChild").OfType<IMethodSymbol>()
                    .Where(method => IsValidInsertionMethod(method) && method.Parameters.Length == 1)
                    .ToArray();
                if (addChildMethods.Length == 1) return new XamlCollectionShapeInfo(addChildMethods[0], isDictionary: false);
                if (addChildMethods.Length > 1) return null;
            }
        }

        return null;
    }

    private static bool IsValidInsertionMethod(IMethodSymbol method)
    {
        if (method.IsStatic || method.IsGenericMethod || method.MethodKind != MethodKind.Ordinary ||
            (method.Parameters.Length != 1 && method.Parameters.Length != 2)) return false;
        for (var index = 0; index < method.Parameters.Length; index++)
        {
            if (method.Parameters[index].RefKind != RefKind.None) return false;
        }
        return true;
    }

    private static XamlCollectionShapeInfo? SelectUniqueCollectionShape(IReadOnlyList<IMethodSymbol> methods)
    {
        var dictionary = methods.Where(method => method.Parameters.Length == 2).ToArray();
        if (dictionary.Length == 1) return new XamlCollectionShapeInfo(dictionary[0], isDictionary: true);
        if (dictionary.Length > 1) return null;
        var collection = methods.Where(method => method.Parameters.Length == 1).ToArray();
        return collection.Length == 1 ? new XamlCollectionShapeInfo(collection[0], isDictionary: false) : null;
    }

    private static void AddUniqueSignature(List<IMethodSymbol> methods, IMethodSymbol candidate)
    {
        foreach (var method in methods)
        {
            if (method.Parameters.Length != candidate.Parameters.Length) continue;
            var equal = true;
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (!SymbolEqualityComparer.Default.Equals(method.Parameters[index].Type, candidate.Parameters[index].Type))
                {
                    equal = false;
                    break;
                }
            }
            if (equal) return;
        }
        methods.Add(candidate);
    }

    private static string GetMetadataName(ITypeSymbol symbol)
    {
        if (symbol is IArrayTypeSymbol array)
        {
            return GetMetadataName(array.ElementType) + "[]";
        }

        if (!(symbol is INamedTypeSymbol named))
        {
            return symbol.ToDisplayString();
        }

        var parts = new Stack<string>();
        for (var current = named; current != null; current = current.ContainingType)
        {
            parts.Push(current.MetadataName);
        }

        var typeName = string.Join("+", parts.ToArray());
        var namespaceName = named.ContainingNamespace?.ToDisplayString();
        var result = string.IsNullOrEmpty(namespaceName) ? typeName : namespaceName + "." + typeName;
        if (named.IsGenericType && named.TypeArguments.Length != 0 &&
            named.TypeArguments.Any(argument => argument.TypeKind != TypeKind.TypeParameter))
            result += "[" + string.Join(",", named.TypeArguments.Select(GetMetadataName)) + "]";
        return result;
    }

    private static bool IsAccessible(Accessibility accessibility) =>
        accessibility == Accessibility.Public;

    private static bool IsAssignableTo(ITypeSymbol source, ITypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(source, target)) return true;
        if (source is not INamedTypeSymbol named) return false;
        for (var current = named.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target)) return true;
        }
        foreach (var @interface in named.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(@interface, target)) return true;
        }
        return false;
    }

    private void IndexAssemblyNamespaceMetadata(
        out IReadOnlyList<XamlNamespaceDefinitionInfo> definitions,
        out IReadOnlyList<XamlNamespacePrefixInfo> prefixes,
        out IReadOnlyList<XamlNamespaceCompatibilityInfo> compatibilities)
    {
        var definitionList = new List<XamlNamespaceDefinitionInfo>();
        var prefixList = new List<XamlNamespacePrefixInfo>();
        var compatibilityList = new List<XamlNamespaceCompatibilityInfo>();
        var assemblies = new List<IAssemblySymbol> { _compilation.Assembly };
        assemblies.AddRange(_compilation.SourceModule.ReferencedAssemblySymbols);
        foreach (var assembly in assemblies)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                var attributeName = attribute.AttributeClass == null
                    ? string.Empty
                    : GetMetadataName(attribute.AttributeClass);
                for (var ruleIndex = 0; ruleIndex < _attributeRules.Count; ruleIndex++)
                {
                    var registration = _attributeRules[ruleIndex];
                    var rule = registration.Rule;
                    if ((rule.Targets & XamlSchemaAttributeTargets.Assembly) == 0 ||
                        !string.Equals(rule.AttributeMetadataName, attributeName, StringComparison.Ordinal)) continue;
                    if (!TryGetStringConstructorPair(attribute, out var first, out var second)) continue;
                    switch (rule.Semantic)
                    {
                        case XamlSchemaSemantics.XmlnsDefinition:
                            definitionList.Add(new XamlNamespaceDefinitionInfo(
                                first,
                                second,
                                assembly,
                                attribute,
                                registration.ProviderId,
                                registration.Priority));
                            break;
                        case XamlSchemaSemantics.XmlnsPrefix:
                            prefixList.Add(new XamlNamespacePrefixInfo(
                                first, second, assembly, attribute, registration.ProviderId));
                            break;
                        case XamlSchemaSemantics.XmlnsCompatibleWith:
                            compatibilityList.Add(new XamlNamespaceCompatibilityInfo(
                                first, second, assembly, attribute, registration.ProviderId));
                            break;
                    }
                }
            }
        }

        definitionList.Sort(static (left, right) =>
        {
            var priority = right.ProviderPriority.CompareTo(left.ProviderPriority);
            if (priority != 0) return priority;
            var xml = string.Compare(left.XmlNamespace, right.XmlNamespace, StringComparison.Ordinal);
            if (xml != 0) return xml;
            var clr = string.Compare(left.ClrNamespace, right.ClrNamespace, StringComparison.Ordinal);
            return clr != 0 ? clr : string.Compare(left.Assembly.Identity.Name, right.Assembly.Identity.Name, StringComparison.Ordinal);
        });
        prefixes = prefixList;
        compatibilities = compatibilityList;
        definitions = definitionList;
    }

    private IReadOnlyList<XamlSchemaAnnotationInfo> IndexAssemblyAnnotations()
    {
        var result = new List<XamlSchemaAnnotationInfo>();
        var assemblies = new List<IAssemblySymbol> { _compilation.Assembly };
        assemblies.AddRange(_compilation.SourceModule.ReferencedAssemblySymbols);
        foreach (var assembly in assemblies)
        {
            result.AddRange(GetAnnotations(
                assembly,
                XamlSchemaAttributeTargets.Assembly,
                includeInherited: false));
        }
        result.Sort(static (left, right) =>
        {
            var priority = right.ProviderPriority.CompareTo(left.ProviderPriority);
            if (priority != 0) return priority;
            var semantic = string.Compare(left.Semantic, right.Semantic, StringComparison.Ordinal);
            if (semantic != 0) return semantic;
            var assembly = string.Compare(
                left.DeclaredOn.ContainingAssembly?.Identity.Name,
                right.DeclaredOn.ContainingAssembly?.Identity.Name,
                StringComparison.Ordinal);
            return assembly != 0 ? assembly : string.Compare(left.Value, right.Value, StringComparison.Ordinal);
        });
        return result;
    }

    private IReadOnlyList<XamlSchemaAnnotationInfo> IndexModuleAnnotations()
    {
        var result = new List<XamlSchemaAnnotationInfo>();
        var assemblies = new List<IAssemblySymbol> { _compilation.Assembly };
        assemblies.AddRange(_compilation.SourceModule.ReferencedAssemblySymbols);
        foreach (var assembly in assemblies)
        {
            foreach (var module in assembly.Modules)
            {
                result.AddRange(GetAnnotations(
                    module,
                    XamlSchemaAttributeTargets.Module,
                    includeInherited: false));
            }
        }
        result.Sort(static (left, right) =>
        {
            var priority = right.ProviderPriority.CompareTo(left.ProviderPriority);
            if (priority != 0) return priority;
            var semantic = string.Compare(
                left.Semantic,
                right.Semantic,
                StringComparison.Ordinal);
            if (semantic != 0) return semantic;
            return string.Compare(
                left.DeclaredOn.ToDisplayString(),
                right.DeclaredOn.ToDisplayString(),
                StringComparison.Ordinal);
        });
        return result;
    }

    private static IReadOnlyList<XamlCompilationModeInfo> FindCompilationModes(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        XamlCompilationScope scope)
    {
        var result = new List<XamlCompilationModeInfo>();
        for (var index = 0; index < annotations.Count; index++)
        {
            if (!string.Equals(
                    annotations[index].Semantic,
                    XamlSchemaSemantics.XamlCompilation,
                    StringComparison.Ordinal))
            {
                continue;
            }
            result.Add(CreateCompilationMode(annotations[index], scope));
        }
        return result;
    }

    private static XamlCompilationModeInfo? FindCompilationMode(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        XamlCompilationScope scope)
    {
        for (var index = 0; index < annotations.Count; index++)
        {
            if (string.Equals(
                    annotations[index].Semantic,
                    XamlSchemaSemantics.XamlCompilation,
                    StringComparison.Ordinal))
            {
                return CreateCompilationMode(annotations[index], scope);
            }
        }
        return null;
    }

    private static XamlCompilationModeInfo CreateCompilationMode(
        XamlSchemaAnnotationInfo annotation,
        XamlCompilationScope scope)
    {
        var arguments = annotation.Attribute.ConstructorArguments;
        if (arguments.Length != 1)
        {
            return new XamlCompilationModeInfo(
                annotation,
                scope,
                null,
                null,
                null,
                "A XAML compilation-mode annotation must have exactly one enum argument.");
        }

        var constant = arguments[0];
        if (constant.Type?.TypeKind != TypeKind.Enum ||
            constant.Value == null)
        {
            return new XamlCompilationModeInfo(
                annotation,
                scope,
                null,
                null,
                constant,
                "The XAML compilation-mode argument must be an enum constant.");
        }

        long rawValue;
        try
        {
            rawValue = Convert.ToInt64(
                constant.Value,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return new XamlCompilationModeInfo(
                annotation,
                scope,
                null,
                null,
                constant,
                "The XAML compilation-mode enum value is not representable as Int64.");
        }

        XamlCompilationMode? mode = rawValue switch
        {
            1 => XamlCompilationMode.Skip,
            2 => XamlCompilationMode.Compile,
            _ => null
        };
        return new XamlCompilationModeInfo(
            annotation,
            scope,
            mode,
            rawValue,
            constant,
            mode.HasValue
                ? null
                : $"XAML compilation-mode value '{rawValue}' is not a singular Compile or Skip value.");
    }

    private static IReadOnlyList<XamlRootNamespaceInfo> FindRootNamespaces(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var result = new List<XamlRootNamespaceInfo>();
        foreach (var annotation in annotations)
        {
            if (!string.Equals(
                    annotation.Semantic,
                    XamlSchemaSemantics.RootNamespace,
                    StringComparison.Ordinal))
            {
                continue;
            }
            var assembly = annotation.DeclaredOn as IAssemblySymbol;
            var arguments = annotation.Attribute.ConstructorArguments;
            var value = arguments.Length == 1
                ? arguments[0].Value as string
                : null;
            var error = assembly == null
                ? "A root-namespace annotation must be declared on an assembly."
                : arguments.Length != 1 || string.IsNullOrWhiteSpace(value)
                    ? "A root-namespace annotation must have one non-empty string argument."
                    : null;
            result.Add(new XamlRootNamespaceInfo(
                annotation,
                assembly,
                value,
                error));
        }
        return result;
    }

    private static IReadOnlyList<XamlResourceIdInfo> FindResourceIdentities(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var result = new List<XamlResourceIdInfo>();
        foreach (var annotation in annotations)
        {
            if (!string.Equals(
                    annotation.Semantic,
                    XamlSchemaSemantics.XamlResourceId,
                    StringComparison.Ordinal))
            {
                continue;
            }
            var assembly = annotation.DeclaredOn as IAssemblySymbol;
            var arguments = annotation.Attribute.ConstructorArguments;
            var resourceId = arguments.Length == 3
                ? arguments[0].Value as string
                : null;
            var path = arguments.Length == 3
                ? arguments[1].Value as string
                : null;
            var type = arguments.Length == 3
                ? arguments[2].Value as INamedTypeSymbol
                : null;
            string? error = null;
            if (assembly == null)
                error = "A XAML resource identity must be declared on an assembly.";
            else if (arguments.Length != 3 ||
                     string.IsNullOrWhiteSpace(resourceId) ||
                     string.IsNullOrWhiteSpace(path) ||
                     type == null)
            {
                error = "A XAML resource identity must have non-empty resource-id and path strings followed by a type.";
            }
            result.Add(new XamlResourceIdInfo(
                annotation,
                assembly,
                resourceId,
                path,
                type,
                error));
        }
        return result;
    }

    private static XamlFilePathInfo? FindFilePath(
        INamedTypeSymbol type,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        foreach (var annotation in annotations)
        {
            if (!string.Equals(
                    annotation.Semantic,
                    XamlSchemaSemantics.XamlFilePath,
                    StringComparison.Ordinal))
            {
                continue;
            }
            var arguments = annotation.Attribute.ConstructorArguments;
            var path = arguments.Length == 1
                ? arguments[0].Value as string
                : null;
            return new XamlFilePathInfo(
                annotation,
                type,
                path,
                arguments.Length == 1 && !string.IsNullOrWhiteSpace(path)
                    ? null
                    : "A XAML file-path annotation must have one non-empty string argument.");
        }
        return null;
    }

    private static XamlTypeMarkerInfo? FindTypeMarker(
        INamedTypeSymbol type,
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string semantic)
    {
        foreach (var annotation in annotations)
        {
            if (!string.Equals(annotation.Semantic, semantic, StringComparison.Ordinal))
                continue;
            return new XamlTypeMarkerInfo(
                annotation,
                type,
                annotation.Attribute.ConstructorArguments.Length == 0
                    ? null
                    : $"Marker annotation '{semantic}' must use a parameterless constructor.");
        }
        return null;
    }

    private static XamlDefaultValueInfo? FindDefaultValue(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var annotation = FindSemanticAnnotation(
            annotations,
            XamlSchemaSemantics.DefaultValue);
        if (annotation == null) return null;
        var arguments = annotation.Attribute.ConstructorArguments;
        if (arguments.Length == 1)
        {
            var value = arguments[0];
            return new XamlDefaultValueInfo(
                annotation,
                annotation.DeclaredOn,
                value,
                null,
                null,
                value.Kind == TypedConstantKind.Error
                    ? "The default-value argument is not a valid attribute constant."
                    : null);
        }
        if (arguments.Length == 2)
        {
            var conversionType = arguments[0].Value as ITypeSymbol;
            var text = arguments[1].Value as string;
            string? error = null;
            if (conversionType == null || conversionType.TypeKind == TypeKind.Error)
                error = "The text-converted default value requires a resolvable conversion type.";
            else if (arguments[1].Kind == TypedConstantKind.Error ||
                     (!arguments[1].IsNull && text == null))
                error = "The text-converted default value requires a string constant.";
            return new XamlDefaultValueInfo(
                annotation,
                annotation.DeclaredOn,
                null,
                conversionType,
                text,
                error);
        }
        return new XamlDefaultValueInfo(
            annotation,
            annotation.DeclaredOn,
            null,
            null,
            null,
            "A default-value annotation must use either the value constructor or the type-and-text constructor.");
    }

    private XamlDesignerSerializationMethodsInfo?
        FindDesignerSerializationMethods(
            IPropertySymbol property,
            XamlDefaultValueInfo? defaultValue)
    {
        if (!_shapePolicy.InferDesignerSerializationMethods)
            return null;
        var shouldCandidates = GetNearestNamedMethods(
            property.ContainingType,
            _shapePolicy.ShouldSerializeMethodPrefix + property.Name);
        var resetCandidates = GetNearestNamedMethods(
            property.ContainingType,
            _shapePolicy.ResetMethodPrefix + property.Name);
        if (shouldCandidates.Count == 0 && resetCandidates.Count == 0)
            return null;

        var shouldMatches = shouldCandidates.Where(static method =>
            !method.IsStatic &&
            !method.IsGenericMethod &&
            method.MethodKind == MethodKind.Ordinary &&
            method.Parameters.Length == 0 &&
            method.ReturnType.SpecialType == SpecialType.System_Boolean)
            .ToArray();
        var resetMatches = resetCandidates.Where(static method =>
            !method.IsStatic &&
            !method.IsGenericMethod &&
            method.MethodKind == MethodKind.Ordinary &&
            method.Parameters.Length == 0 &&
            method.ReturnsVoid)
            .ToArray();
        var should = shouldMatches.Length == 1
            ? shouldMatches[0]
            : null;
        var reset = resetMatches.Length == 1
            ? resetMatches[0]
            : null;
        string? error = null;
        if (shouldCandidates.Count != 0 && shouldMatches.Length != 1)
            error = $"Method '{_shapePolicy.ShouldSerializeMethodPrefix}{property.Name}' must have one unambiguous instance, non-generic, parameterless Boolean shape.";
        else if (resetCandidates.Count != 0 && resetMatches.Length != 1)
            error = $"Method '{_shapePolicy.ResetMethodPrefix}{property.Name}' must have one unambiguous instance, non-generic, parameterless void shape.";
        else if (defaultValue != null)
            error = "DefaultValue metadata must not be combined with ShouldSerialize/Reset serialization methods.";

        return new XamlDesignerSerializationMethodsInfo(
            property,
            should,
            reset,
            shouldCandidates,
            resetCandidates,
            GetShapeProviderId(
                XamlSymbolShapeFeatures.DesignerSerializationMethods),
            error);
    }

    private static XamlDesignerSerializationVisibilityInfo?
        FindDesignerSerializationVisibility(
            IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var annotation = FindSemanticAnnotation(
            annotations,
            XamlSchemaSemantics.DesignerSerializationVisibility);
        if (annotation == null) return null;
        var arguments = annotation.Attribute.ConstructorArguments;
        var constant = arguments.Length == 1
            ? (TypedConstant?)arguments[0]
            : null;
        var rawValue = constant.HasValue &&
            TryGetEnumInt64(constant.Value, out var raw)
                ? raw
                : (long?)null;
        var visibility = rawValue switch
        {
            0 => XamlDesignerSerializationVisibility.Hidden,
            1 => XamlDesignerSerializationVisibility.Visible,
            2 => XamlDesignerSerializationVisibility.Content,
            _ => (XamlDesignerSerializationVisibility?)null
        };
        return new XamlDesignerSerializationVisibilityInfo(
            annotation,
            annotation.DeclaredOn,
            visibility,
            constant,
            arguments.Length != 1
                ? "Designer serialization visibility requires one enum argument."
                : !rawValue.HasValue
                    ? "Designer serialization visibility requires an enum constant."
                    : !visibility.HasValue
                        ? $"Designer serialization visibility value '{rawValue.Value}' is not defined."
                        : null);
    }

    private static XamlDesignerSerializationOptionsInfo?
        FindDesignerSerializationOptions(
            IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var annotation = FindSemanticAnnotation(
            annotations,
            XamlSchemaSemantics.DesignerSerializationOptions);
        if (annotation == null) return null;
        var arguments = annotation.Attribute.ConstructorArguments;
        var constant = arguments.Length == 1
            ? (TypedConstant?)arguments[0]
            : null;
        var rawValue = constant.HasValue &&
            TryGetEnumInt64(constant.Value, out var raw)
                ? raw
                : (long?)null;
        string? error = null;
        if (arguments.Length != 1)
            error = "Designer serialization options require one enum argument.";
        else if (!rawValue.HasValue)
            error = "Designer serialization options require an enum constant.";
        else if ((rawValue.Value & ~1L) != 0)
            error = $"Designer serialization options value '{rawValue.Value}' contains unsupported flags.";
        return new XamlDesignerSerializationOptionsInfo(
            annotation,
            annotation.DeclaredOn,
            rawValue.HasValue && (rawValue.Value & 1L) != 0,
            rawValue,
            constant,
            error);
    }

    private static XamlBrowsableInfo? FindBrowsable(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string semantic,
        bool? parameterlessDefault)
    {
        var annotation = FindSemanticAnnotation(annotations, semantic);
        if (annotation == null) return null;
        var arguments = annotation.Attribute.ConstructorArguments;
        if (arguments.Length == 0 && parameterlessDefault.HasValue)
            return new XamlBrowsableInfo(
                annotation,
                annotation.DeclaredOn,
                parameterlessDefault.Value,
                null,
                null);
        var constant = arguments.Length == 1
            ? (TypedConstant?)arguments[0]
            : null;
        var value = constant?.Value as bool?;
        return new XamlBrowsableInfo(
            annotation,
            annotation.DeclaredOn,
            value,
            constant,
            arguments.Length == 1 && value.HasValue
                ? null
                : $"Annotation '{semantic}' requires one Boolean argument" +
                  (parameterlessDefault.HasValue ? " or its parameterless constructor." : "."));
    }

    private static XamlEditorBrowsableInfo? FindEditorBrowsable(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var annotation = FindSemanticAnnotation(
            annotations,
            XamlSchemaSemantics.EditorBrowsable);
        if (annotation == null) return null;
        var arguments = annotation.Attribute.ConstructorArguments;
        if (arguments.Length == 0)
            return new XamlEditorBrowsableInfo(
                annotation,
                annotation.DeclaredOn,
                XamlEditorBrowsableState.Always,
                null,
                null);
        var constant = arguments.Length == 1
            ? (TypedConstant?)arguments[0]
            : null;
        var rawValue = constant.HasValue &&
            TryGetEnumInt64(constant.Value, out var raw)
                ? raw
                : (long?)null;
        var state = rawValue switch
        {
            0 => XamlEditorBrowsableState.Always,
            1 => XamlEditorBrowsableState.Never,
            2 => XamlEditorBrowsableState.Advanced,
            _ => (XamlEditorBrowsableState?)null
        };
        return new XamlEditorBrowsableInfo(
            annotation,
            annotation.DeclaredOn,
            state,
            constant,
            arguments.Length != 1
                ? "Editor-browsable metadata requires zero or one enum argument."
                : !rawValue.HasValue
                    ? "Editor-browsable metadata requires an enum constant."
                    : !state.HasValue
                        ? $"Editor-browsable value '{rawValue.Value}' is not defined."
                        : null);
    }

    private static XamlLocalizabilityInfo? FindLocalizability(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations)
    {
        var annotation = FindSemanticAnnotation(
            annotations,
            XamlSchemaSemantics.Localizability);
        if (annotation == null) return null;
        var arguments = annotation.Attribute.ConstructorArguments;
        var categoryConstant = arguments.Length == 1
            ? (TypedConstant?)arguments[0]
            : null;
        var categoryRaw = categoryConstant.HasValue &&
            TryGetEnumInt64(categoryConstant.Value, out var categoryValue)
                ? categoryValue
                : (long?)null;
        var category = categoryRaw.HasValue &&
            categoryRaw.Value >= 0 &&
            categoryRaw.Value <= 17
                ? (XamlLocalizationCategory?)categoryRaw.Value
                : null;

        var readabilityConstant = GetNamedConstant(
            annotation.Attribute,
            "Readability");
        var readabilityRaw = !readabilityConstant.HasValue
            ? 1L
            : TryGetEnumInt64(readabilityConstant.Value, out var readabilityValue)
                ? readabilityValue
                : (long?)null;
        var readability = readabilityRaw.HasValue &&
            readabilityRaw.Value >= 0 &&
            readabilityRaw.Value <= 2
                ? (XamlLocalizationReadability?)readabilityRaw.Value
                : null;

        var modifiabilityConstant = GetNamedConstant(
            annotation.Attribute,
            "Modifiability");
        var modifiabilityRaw = !modifiabilityConstant.HasValue
            ? 1L
            : TryGetEnumInt64(modifiabilityConstant.Value, out var modifiabilityValue)
                ? modifiabilityValue
                : (long?)null;
        var modifiability = modifiabilityRaw.HasValue &&
            modifiabilityRaw.Value >= 0 &&
            modifiabilityRaw.Value <= 2
                ? (XamlLocalizationModifiability?)modifiabilityRaw.Value
                : null;

        string? error = null;
        if (arguments.Length != 1)
            error = "Localizability metadata requires one category enum argument.";
        else if (!category.HasValue)
            error = "Localizability category must be a defined value from 0 through 17.";
        else if (!readability.HasValue)
            error = "Localizability Readability must be a defined enum value from 0 through 2.";
        else if (!modifiability.HasValue)
            error = "Localizability Modifiability must be a defined enum value from 0 through 2.";
        return new XamlLocalizabilityInfo(
            annotation,
            annotation.DeclaredOn,
            category,
            readability,
            modifiability,
            categoryConstant,
            readabilityConstant,
            modifiabilityConstant,
            error);
    }

    private static XamlSchemaAnnotationInfo? FindSemanticAnnotation(
        IReadOnlyList<XamlSchemaAnnotationInfo> annotations,
        string semantic)
    {
        for (var index = 0; index < annotations.Count; index++)
            if (string.Equals(
                    annotations[index].Semantic,
                    semantic,
                    StringComparison.Ordinal))
                return annotations[index];
        return null;
    }

    private static bool TryGetEnumInt64(
        TypedConstant constant,
        out long value)
    {
        value = 0;
        if (constant.Type?.TypeKind != TypeKind.Enum ||
            constant.Value == null)
            return false;
        try
        {
            value = Convert.ToInt64(
                constant.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PathsMatch(string documentPath, string declaredPath)
    {
        var document = NormalizePath(documentPath);
        var declared = NormalizePath(declaredPath);
        return string.Equals(document, declared, StringComparison.OrdinalIgnoreCase) ||
               document.EndsWith("/" + declared, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool TryGetStringConstructorPair(AttributeData attribute, out string first, out string second)
    {
        first = string.Empty;
        second = string.Empty;
        if (attribute.ConstructorArguments.Length < 2 ||
            attribute.ConstructorArguments[0].Value is not string firstValue ||
            attribute.ConstructorArguments[1].Value is not string secondValue ||
            string.IsNullOrWhiteSpace(firstValue) || string.IsNullOrWhiteSpace(secondValue)) return false;
        first = firstValue;
        second = secondValue;
        return true;
    }

    private readonly struct NamespaceCandidate
    {
        public NamespaceCandidate(string clrNamespace, IAssemblySymbol? assembly)
        {
            ClrNamespace = clrNamespace;
            Assembly = assembly;
        }
        public string ClrNamespace { get; }
        public IAssemblySymbol? Assembly { get; }
    }

    private sealed class RuleRegistration
    {
        public RuleRegistration(string providerId, int priority, XamlSchemaAttributeRule rule)
        {
            ProviderId = providerId;
            Priority = priority;
            Rule = rule;
        }

        public string ProviderId { get; }
        public int Priority { get; }
        public XamlSchemaAttributeRule Rule { get; }
    }

    private sealed class ShapePolicyRegistration
    {
        public ShapePolicyRegistration(
            string providerId,
            int priority,
            XamlSymbolShapePolicy policy)
        {
            ProviderId = providerId;
            Priority = priority;
            Policy = policy;
        }

        public string ProviderId { get; }
        public int Priority { get; }
        public XamlSymbolShapePolicy Policy { get; }
    }

    private sealed class MarkupExtensionIdentityMatch
    {
        public MarkupExtensionIdentityMatch(
            XamlMarkupExtensionIdentityKind identityKind,
            string identity,
            INamedTypeSymbol? identitySymbol,
            XamlSymbolShapePolicy policy,
            string providerId,
            string? error)
        {
            IdentityKind = identityKind;
            Identity = identity;
            IdentitySymbol = identitySymbol;
            Policy = policy;
            ProviderId = providerId;
            Error = error;
        }

        public XamlMarkupExtensionIdentityKind IdentityKind { get; }
        public string Identity { get; }
        public INamedTypeSymbol? IdentitySymbol { get; }
        public XamlSymbolShapePolicy Policy { get; }
        public string ProviderId { get; }
        public string? Error { get; }
    }
}
