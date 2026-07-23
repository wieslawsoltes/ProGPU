using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Diagnostics;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Resources;

public enum XamlResourceReferenceKind { Static, Theme }
public enum XamlResourceResolutionKind { Resolved, ResolvedConditional, ResolvedExternal, Unresolved, ForwardReferenceDisallowed }
public enum XamlResourceScopeKind { Lexical, ThemePartition }

public sealed class XamlResourceScopeInfo
{
    public XamlResourceScopeInfo(
        ulong id,
        ulong? parentId,
        ulong ownerStableId,
        XamlResourceScopeKind kind = XamlResourceScopeKind.Lexical,
        XamlResourceKeyInfo? partitionKey = null)
    { Id = id; ParentId = parentId; OwnerStableId = ownerStableId; Kind = kind; PartitionKey = partitionKey; }
    public ulong Id { get; }
    public ulong? ParentId { get; }
    public ulong OwnerStableId { get; }
    public XamlResourceScopeKind Kind { get; }
    public XamlResourceKeyInfo? PartitionKey { get; }
}

public sealed class XamlResourceDefinitionInfo
{
    public XamlResourceDefinitionInfo(XamlResourceKeyInfo resourceKey, ulong scopeId, ulong valueStableId, TextSpan sourceSpan, int ordinal)
    { ResourceKey = resourceKey; ScopeId = scopeId; ValueStableId = valueStableId; SourceSpan = sourceSpan; Ordinal = ordinal; }
    public XamlResourceKeyInfo ResourceKey { get; }
    public string Key => ResourceKey.Text;
    public ulong ScopeId { get; }
    public ulong ValueStableId { get; }
    public TextSpan SourceSpan { get; }
    public int Ordinal { get; }
}

public sealed class XamlResourceReferenceInfo
{
    public XamlResourceReferenceInfo(
        XamlResourceKeyInfo resourceKey, XamlResourceReferenceKind kind, ulong scopeId, ulong stableId, TextSpan sourceSpan,
        XamlResourceResolutionKind resolution, ulong? definitionStableId, string? providerPath = null,
        ImmutableArray<ulong> candidateDefinitionStableIds = default,
        ImmutableArray<XamlExternalResourceDefinition> externalCandidates = default)
    {
        ResourceKey = resourceKey;
        Kind = kind;
        ScopeId = scopeId;
        StableId = stableId;
        SourceSpan = sourceSpan;
        Resolution = resolution;
        DefinitionStableId = definitionStableId;
        ProviderPath = providerPath;
        CandidateDefinitionStableIds = candidateDefinitionStableIds.IsDefault
            ? definitionStableId.HasValue ? ImmutableArray.Create(definitionStableId.Value) : ImmutableArray<ulong>.Empty
            : candidateDefinitionStableIds;
        ExternalCandidates = externalCandidates.IsDefault
            ? ImmutableArray<XamlExternalResourceDefinition>.Empty
            : externalCandidates;
    }
    public XamlResourceKeyInfo ResourceKey { get; }
    public string Key => ResourceKey.Text;
    public XamlResourceReferenceKind Kind { get; }
    public ulong ScopeId { get; }
    public ulong StableId { get; }
    public TextSpan SourceSpan { get; }
    public XamlResourceResolutionKind Resolution { get; }
    public ulong? DefinitionStableId { get; }
    public string? ProviderPath { get; }
    /// <summary>All variant candidates for a conditional resource resolution.</summary>
    public ImmutableArray<ulong> CandidateDefinitionStableIds { get; }
    public ImmutableArray<XamlExternalResourceDefinition> ExternalCandidates { get; }
}

public sealed class XamlResourceGraph
{
    public XamlResourceGraph(
        XamlBoundDocument document,
        ImmutableArray<XamlResourceScopeInfo> scopes,
        ImmutableArray<XamlResourceDefinitionInfo> definitions,
        ImmutableArray<XamlResourceReferenceInfo> references,
        ImmutableArray<Diagnostic> diagnostics)
    { Document = document; Scopes = scopes; Definitions = definitions; References = references; Diagnostics = diagnostics; }
    public XamlBoundDocument Document { get; }
    public ImmutableArray<XamlResourceScopeInfo> Scopes { get; }
    public ImmutableArray<XamlResourceDefinitionInfo> Definitions { get; }
    public ImmutableArray<XamlResourceReferenceInfo> References { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public XamlResourceGraph WithDocument(XamlBoundDocument document) =>
        new XamlResourceGraph(
            document ?? throw new ArgumentNullException(nameof(document)),
            Scopes,
            Definitions,
            References,
            Diagnostics);
}

/// <summary>Builds deterministic lexical resource scopes without framework runtime objects.</summary>
public sealed class XamlResourceGraphBuilder
{
    private const ulong ScopeSalt = 0xD6E8FEB86659FD93UL;

    public XamlResourceGraph Build(XamlBoundDocument document)
        => Build(document, resourceDependencies: null);

    public XamlResourceGraph Build(
        XamlBoundDocument document,
        XamlResourceDependencySlice? resourceDependencies)
        => Build(document, resourceDependencies, allowStaticResourceForwardReferences: false);

    public XamlResourceGraph Build(
        XamlBoundDocument document,
        XamlResourceDependencySlice? resourceDependencies,
        bool allowStaticResourceForwardReferences)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        var scopes = new List<XamlResourceScopeInfo>();
        var definitions = new List<XamlResourceDefinitionInfo>();
        var pending = new List<PendingReference>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddImportDiagnostics(document, resourceDependencies, diagnostics);
        if (document.Root == null)
            return new XamlResourceGraph(document, scopes.ToImmutableArray(), definitions.ToImmutableArray(),
                ImmutableArray<XamlResourceReferenceInfo>.Empty, diagnostics.ToImmutable());

        var rootScope = CreateScope(document.Root.StableId, null, scopes);
        if (document.Root.Type.Symbol?.IsDictionary == true)
            VisitDictionaryItems(
                document.Root,
                rootScope,
                scopes,
                definitions,
                pending,
                ImmutableArray<ulong>.Empty,
                document.DictionaryKeyDirectiveAliases);
        else
            VisitObject(
                document.Root,
                rootScope,
                scopes,
                definitions,
                pending,
                ImmutableArray<ulong>.Empty,
                document.DictionaryKeyDirectiveAliases);
        var scopeById = scopes.ToDictionary(static scope => scope.Id);
        var definitionsByScopeAndKey = IndexDefinitions(definitions);
        var themeDefinitionsByParentAndKey = IndexThemeDefinitions(definitions, scopeById);
        var externalDefinitionsByOwnerAndKey = resourceDependencies == null
            ? null
            : IndexExternalDefinitions(resourceDependencies.ExternalDefinitions);
        var references = ImmutableArray.CreateBuilder<XamlResourceReferenceInfo>(pending.Count);
        foreach (var reference in pending)
        {
            var resolution = Resolve(reference, definitionsByScopeAndKey, themeDefinitionsByParentAndKey, scopeById,
                out var definition, out var conditionalDefinitions);
            string? providerPath = null;
            var externalCandidates = ImmutableArray<XamlExternalResourceDefinition>.Empty;
            if (resolution == XamlResourceResolutionKind.Unresolved && resourceDependencies != null)
            {
                externalCandidates = ResolveExternal(reference, externalDefinitionsByOwnerAndKey!, scopeById);
                if (!externalCandidates.IsDefaultOrEmpty)
                {
                    resolution = XamlResourceResolutionKind.ResolvedExternal;
                    providerPath = externalCandidates[0].ProviderPath;
                }
            }
            if (resolution == XamlResourceResolutionKind.ForwardReferenceDisallowed &&
                allowStaticResourceForwardReferences)
                resolution = XamlResourceResolutionKind.Resolved;
            references.Add(new XamlResourceReferenceInfo(
                reference.ResourceKey, reference.Kind, reference.ScopeId, reference.StableId, reference.SourceSpan,
                resolution, definition?.ValueStableId, providerPath,
                conditionalDefinitions.Select(static candidate => candidate.ValueStableId).ToImmutableArray(),
                externalCandidates));
            if (resolution == XamlResourceResolutionKind.ForwardReferenceDisallowed)
                diagnostics.Add(XamlDiagnostics.Create(
                    "PGXAML4002", DiagnosticSeverity.Error,
                    $"StaticResource '{reference.ResourceKey.Text}' is declared later in the same lexical resource chain.",
                    document.Infoset.Path, document.Infoset.SourceText, reference.SourceSpan));
        }
        return new XamlResourceGraph(document, scopes.ToImmutableArray(), definitions.ToImmutableArray(),
            references.ToImmutable(), diagnostics.ToImmutable());
    }

    private static ImmutableArray<XamlExternalResourceDefinition> ResolveExternal(
        PendingReference reference,
        IReadOnlyDictionary<ExternalLookupKey, List<XamlExternalResourceDefinition>> definitions,
        IReadOnlyDictionary<ulong, XamlResourceScopeInfo> scopes)
    {
        var activePartition = FindContainingPartition(reference.ScopeId, scopes);
        // Search the nearest lexical owner first. The manifest array already carries reverse
        // merged-dictionary precedence within an owner, but document order must never let an
        // outer import override a nearer element Resources scope.
        ulong? current = reference.ScopeId;
        while (current != null && scopes.TryGetValue(current.Value, out var scope))
        {
            var candidate = FindExternalCandidates(
                reference,
                definitions,
                scope.OwnerStableId,
                activePartition);
            if (!candidate.IsDefaultOrEmpty) return candidate;
            current = scope.ParentId;
        }

        // Scope owner zero is the compatibility contract for manually constructed slices.
        return FindExternalCandidates(reference, definitions, 0, activePartition);
    }

    private static XamlResourceKeyInfo? FindContainingPartition(
        ulong scopeId,
        IReadOnlyDictionary<ulong, XamlResourceScopeInfo> scopes)
    {
        var current = (ulong?)scopeId;
        while (current.HasValue && scopes.TryGetValue(current.Value, out var scope))
        {
            if (scope.Kind == XamlResourceScopeKind.ThemePartition) return scope.PartitionKey;
            current = scope.ParentId;
        }
        return null;
    }

    private static ImmutableArray<XamlExternalResourceDefinition> FindExternalCandidates(
        PendingReference reference,
        IReadOnlyDictionary<ExternalLookupKey, List<XamlExternalResourceDefinition>> definitions,
        ulong ownerStableId,
        XamlResourceKeyInfo? activePartition)
    {
        if (!definitions.TryGetValue(
                new ExternalLookupKey(ownerStableId, reference.ResourceKey.Identity),
                out var matching))
            return ImmutableArray<XamlExternalResourceDefinition>.Empty;
        // Both StaticResource and ThemeResource may enter the active theme partition.
        // Their difference is lifetime: static lookup snapshots once while theme lookup
        // is re-evaluated when the active theme changes.
        if (activePartition != null || matching.Exists(static candidate =>
                candidate.Partition?.Kind == XamlResourcePartitionKind.Theme))
        {
            var themed = ImmutableArray.CreateBuilder<XamlExternalResourceDefinition>();
            var seenPartitions = new HashSet<XamlResourceManifestPartition>();
            for (var index = 0; index < matching.Count; index++)
            {
                var candidate = matching[index];
                if (candidate.Partition?.Kind != XamlResourcePartitionKind.Theme)
                    continue;
                if (activePartition == null ||
                    string.Equals(candidate.Partition.Key.Identity, activePartition.Identity, StringComparison.Ordinal))
                {
                    if (seenPartitions.Add(candidate.Partition)) themed.Add(candidate);
                    if (activePartition != null) break;
                }
            }
            if (themed.Count != 0) return themed.ToImmutable();
        }
        for (var index = 0; index < matching.Count; index++)
        {
            var candidate = matching[index];
            if (candidate.Partition == null)
                return ImmutableArray.Create(candidate);
        }
        return ImmutableArray<XamlExternalResourceDefinition>.Empty;
    }

    private static void AddImportDiagnostics(
        XamlBoundDocument document,
        XamlResourceDependencySlice? dependencies,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (dependencies == null) return;
        var currentPath = XamlResourceDocumentManifest.CanonicalPath(document.Infoset.Path);
        for (var index = 0; index < dependencies.ProviderIssues.Length; index++)
        {
            var issue = dependencies.ProviderIssues[index];
            if (!string.Equals(issue.DocumentPath, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            diagnostics.Add(XamlDiagnostics.Create(
                "PGXAML4007",
                DiagnosticSeverity.Error,
                $"Compiled XAML resource URI '{issue.ResourceUri}' is published by {issue.CandidateDocumentPaths.Length} documents; every provider URI must be unique.",
                issue.DocumentPath,
                issue.SourceSpan,
                issue.LineSpan));
        }
        for (var index = 0; index < dependencies.ImportIssues.Length; index++)
        {
            var issue = dependencies.ImportIssues[index];
            if (!string.Equals(issue.ImportingDocumentPath, currentPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var id = issue.Kind switch
            {
                XamlResourceImportIssueKind.Missing => "PGXAML4003",
                XamlResourceImportIssueKind.Ambiguous => "PGXAML4004",
                XamlResourceImportIssueKind.Cycle => "PGXAML4005",
                _ => throw new InvalidOperationException("Unknown resource import issue kind.")
            };
            var message = issue.Kind switch
            {
                XamlResourceImportIssueKind.Missing =>
                    $"ResourceDictionary Source '{issue.Import.Source}' does not resolve to a compiled XAML provider.",
                XamlResourceImportIssueKind.Ambiguous =>
                    $"ResourceDictionary Source '{issue.Import.Source}' matches {issue.CandidatePaths.Length} compiled XAML providers; use an unambiguous project-relative or component-qualified URI.",
                XamlResourceImportIssueKind.Cycle =>
                    $"ResourceDictionary Source '{issue.Import.Source}' participates in a compiled resource-provider cycle.",
                _ => throw new InvalidOperationException("Unknown resource import issue kind.")
            };
            diagnostics.Add(XamlDiagnostics.Create(
                id,
                DiagnosticSeverity.Error,
                message,
                issue.ImportingDocumentPath,
                issue.Import.SourceSpan,
                issue.Import.LineSpan));
        }
    }

    private static ulong CreateScope(
        ulong owner,
        ulong? parent,
        ICollection<XamlResourceScopeInfo> scopes,
        XamlResourceScopeKind kind = XamlResourceScopeKind.Lexical,
        XamlResourceKeyInfo? partitionKey = null)
    {
        var id = owner ^ ScopeSalt ^ ((ulong)scopes.Count * 0x9E3779B97F4A7C15UL);
        scopes.Add(new XamlResourceScopeInfo(id, parent, owner, kind, partitionKey));
        return id;
    }

    private static void VisitObject(
        XamlBoundObject value,
        ulong inheritedScope,
        List<XamlResourceScopeInfo> scopes,
        List<XamlResourceDefinitionInfo> definitions,
        List<PendingReference> references,
        ImmutableArray<ulong> containingDefinitionIds,
        IReadOnlyList<string> dictionaryKeyDirectiveAliases)
    {
        var resourceMembers = value.Members.Where(IsResourcesMember).ToArray();
        var scope = resourceMembers.Length == 0 ? inheritedScope : CreateScope(value.StableId, inheritedScope, scopes);
        if (TryGetReference(value, out var key, out var kind))
            references.Add(new PendingReference(
                key!, kind, scope, value.StableId, value.SourceSpan, definitions.Count, containingDefinitionIds));

        foreach (var resourceMember in resourceMembers)
        foreach (var resourceValue in resourceMember.Values.OfType<XamlBoundObject>())
        {
            if (resourceValue.Type.Symbol?.IsDictionary == true)
                VisitDictionaryItems(
                    resourceValue,
                    scope,
                    scopes,
                    definitions,
                    references,
                    containingDefinitionIds,
                    dictionaryKeyDirectiveAliases);
            else
                VisitResourceItem(
                    resourceValue,
                    scope,
                    scopes,
                    definitions,
                    references,
                    containingDefinitionIds,
                    dictionaryKeyDirectiveAliases: dictionaryKeyDirectiveAliases);
        }

        foreach (var member in value.Members)
        {
            if (IsResourcesMember(member)) continue;
            foreach (var child in member.Values)
            {
                var childObject = child switch
                {
                    XamlBoundObject direct => direct,
                    XamlBoundBinding binding => binding.Extension,
                    XamlBoundCompiledBinding compiled => compiled.Extension,
                    _ => null
                };
                if (childObject != null)
                    VisitObject(
                        childObject,
                        scope,
                        scopes,
                        definitions,
                        references,
                        containingDefinitionIds,
                        dictionaryKeyDirectiveAliases);
            }
        }
    }

    private static void VisitDictionaryItems(
        XamlBoundObject dictionary,
        ulong scope,
        List<XamlResourceScopeInfo> scopes,
        List<XamlResourceDefinitionInfo> definitions,
        List<PendingReference> references,
        ImmutableArray<ulong> containingDefinitionIds,
        IReadOnlyList<string> dictionaryKeyDirectiveAliases)
    {
        var dictionaryKeyType = dictionary.Type.Symbol?.CollectionShape?.KeyType;
        foreach (var item in EnumerateDirectDictionaryItems(dictionary))
            VisitResourceItem(
                item,
                scope,
                scopes,
                definitions,
                references,
                containingDefinitionIds,
                dictionaryKeyType,
                dictionaryKeyDirectiveAliases);

        foreach (var member in dictionary.Members)
        {
            var role = GetResourceRole(member);
            if (role == XamlResourceMemberRole.MergedDictionaries)
            {
                foreach (var merged in EnumerateRetrievedItems(member))
                {
                    if (merged.Type.Symbol?.IsDictionary == true)
                        VisitDictionaryItems(
                            merged,
                            scope,
                            scopes,
                            definitions,
                            references,
                            containingDefinitionIds,
                            dictionaryKeyDirectiveAliases);
                    else
                        VisitObject(
                            merged,
                            scope,
                            scopes,
                            definitions,
                            references,
                            containingDefinitionIds,
                            dictionaryKeyDirectiveAliases);
                }
            }
            else if (role == XamlResourceMemberRole.ThemeDictionaries)
            {
                foreach (var partition in EnumerateRetrievedItems(member))
                {
                    if (partition.Type.Symbol?.IsDictionary != true) continue;
                    var partitionKey = XamlResourceKeyFactory.GetDictionaryKey(
                        partition,
                        dictionaryKeyType,
                        dictionaryKeyDirectiveAliases);
                    if (partitionKey == null) continue;
                    var partitionScope = CreateScope(
                        partition.StableId,
                        scope,
                        scopes,
                        XamlResourceScopeKind.ThemePartition,
                        partitionKey);
                    VisitDictionaryItems(
                        partition,
                        partitionScope,
                        scopes,
                        definitions,
                        references,
                        containingDefinitionIds,
                        dictionaryKeyDirectiveAliases);
                }
            }
        }
    }

    private static void VisitResourceItem(
        XamlBoundObject item,
        ulong scope,
        List<XamlResourceScopeInfo> scopes,
        List<XamlResourceDefinitionInfo> definitions,
        List<PendingReference> references,
        ImmutableArray<ulong> containingDefinitionIds,
        ITypeSymbol? dictionaryKeyType = null,
        IReadOnlyList<string>? dictionaryKeyDirectiveAliases = null)
    {
        var itemKey = XamlResourceKeyFactory.GetDictionaryKey(
            item,
            dictionaryKeyType,
            dictionaryKeyDirectiveAliases);
        if (itemKey == null) return;
        definitions.Add(new XamlResourceDefinitionInfo(
            itemKey, scope, item.StableId, item.SourceSpan, definitions.Count));
        var containing = containingDefinitionIds.Add(item.StableId);
        if (item.Type.Symbol?.IsDictionary == true)
        {
            var nestedScope = CreateScope(item.StableId, scope, scopes);
            VisitDictionaryItems(
                item,
                nestedScope,
                scopes,
                definitions,
                references,
                containing,
                dictionaryKeyDirectiveAliases ?? Array.Empty<string>());
        }
        else
        {
            VisitObject(
                item,
                scope,
                scopes,
                definitions,
                references,
                containing,
                dictionaryKeyDirectiveAliases ?? Array.Empty<string>());
        }
    }

    private static Dictionary<ResourceLookupKey, List<XamlResourceDefinitionInfo>> IndexDefinitions(
        IReadOnlyList<XamlResourceDefinitionInfo> definitions)
    {
        var result = new Dictionary<ResourceLookupKey, List<XamlResourceDefinitionInfo>>();
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            var key = new ResourceLookupKey(definition.ScopeId, definition.ResourceKey);
            if (!result.TryGetValue(key, out var values))
            {
                values = new List<XamlResourceDefinitionInfo>();
                result.Add(key, values);
            }
            values.Add(definition);
        }
        return result;
    }

    private static Dictionary<ResourceLookupKey, List<XamlResourceDefinitionInfo>> IndexThemeDefinitions(
        IReadOnlyList<XamlResourceDefinitionInfo> definitions,
        IReadOnlyDictionary<ulong, XamlResourceScopeInfo> scopes)
    {
        var result = new Dictionary<ResourceLookupKey, List<XamlResourceDefinitionInfo>>();
        foreach (var definition in definitions)
        {
            var scope = scopes[definition.ScopeId];
            if (scope.Kind != XamlResourceScopeKind.ThemePartition || !scope.ParentId.HasValue) continue;
            var lookup = new ResourceLookupKey(scope.ParentId.Value, definition.ResourceKey);
            if (!result.TryGetValue(lookup, out var values))
            {
                values = new List<XamlResourceDefinitionInfo>();
                result.Add(lookup, values);
            }
            values.Add(definition);
        }
        foreach (var values in result.Values)
            values.Sort((left, right) =>
            {
                var partition = string.Compare(
                    scopes[left.ScopeId].PartitionKey?.Identity,
                    scopes[right.ScopeId].PartitionKey?.Identity,
                    StringComparison.Ordinal);
                return partition != 0 ? partition : left.Ordinal.CompareTo(right.Ordinal);
            });
        return result;
    }

    private static Dictionary<ExternalLookupKey, List<XamlExternalResourceDefinition>> IndexExternalDefinitions(
        ImmutableArray<XamlExternalResourceDefinition> definitions)
    {
        var result = new Dictionary<ExternalLookupKey, List<XamlExternalResourceDefinition>>();
        foreach (var definition in definitions)
        {
            var key = new ExternalLookupKey(
                definition.ConsumerScopeOwnerStableId,
                definition.ResourceKey.Identity);
            if (!result.TryGetValue(key, out var values))
            {
                values = new List<XamlExternalResourceDefinition>();
                result.Add(key, values);
            }
            values.Add(definition);
        }
        return result;
    }

    private static IEnumerable<XamlBoundObject> EnumerateDirectDictionaryItems(XamlBoundObject dictionary) =>
        dictionary.Members.Where(member => member.Member.Kind == XamlBoundReferenceKind.DictionaryItems)
            .SelectMany(static member => member.Values).OfType<XamlBoundObject>();

    private static IEnumerable<XamlBoundObject> EnumerateRetrievedItems(XamlBoundMember member)
    {
        foreach (var value in member.Values.OfType<XamlBoundObject>())
        {
            if (!value.IsRetrieved)
            {
                yield return value;
                continue;
            }
            foreach (var item in value.Members
                         .Where(static candidate => candidate.Member.Kind == XamlBoundReferenceKind.CollectionItems ||
                                                    candidate.Member.Kind == XamlBoundReferenceKind.DictionaryItems)
                         .SelectMany(static candidate => candidate.Values)
                         .OfType<XamlBoundObject>())
                yield return item;
        }
    }

    private static bool IsResourcesMember(XamlBoundMember member) =>
        GetResourceRole(member) == XamlResourceMemberRole.LexicalResources;

    private static XamlResourceMemberRole GetResourceRole(XamlBoundMember member)
    {
        var symbol = member.Member.Symbol;
        if (symbol == null) return XamlResourceMemberRole.None;
        if (symbol.ResourceRole != XamlResourceMemberRole.None) return symbol.ResourceRole;
        return symbol.IsAmbient && symbol.ValueType.IsDictionary
            ? XamlResourceMemberRole.LexicalResources
            : XamlResourceMemberRole.None;
    }

    private static bool TryGetReference(XamlBoundObject value, out XamlResourceKeyInfo? key, out XamlResourceReferenceKind kind)
    {
        key = null;
        kind = default;
        switch (value.Type.Symbol?.ResourceReferenceRole)
        {
            case XamlResourceReferenceRole.Static:
                kind = XamlResourceReferenceKind.Static;
                break;
            case XamlResourceReferenceRole.Dynamic:
                kind = XamlResourceReferenceKind.Theme;
                break;
            default:
                return false;
        }
        key = XamlResourceKeyFactory.GetReferenceKey(value);
        return key != null && (key.Kind != XamlResourceKeyKind.Text || !string.IsNullOrEmpty(key.Text));
    }

    private static XamlResourceResolutionKind Resolve(
        PendingReference reference,
        IReadOnlyDictionary<ResourceLookupKey, List<XamlResourceDefinitionInfo>> definitions,
        IReadOnlyDictionary<ResourceLookupKey, List<XamlResourceDefinitionInfo>> themeDefinitions,
        IReadOnlyDictionary<ulong, XamlResourceScopeInfo> scopes,
        out XamlResourceDefinitionInfo? resolved,
        out ImmutableArray<XamlResourceDefinitionInfo> conditional)
    {
        resolved = null;
        conditional = ImmutableArray<XamlResourceDefinitionInfo>.Empty;
        var originPartition = FindContainingPartition(reference.ScopeId, scopes);
        var scopeId = (ulong?)reference.ScopeId;
        var sawForward = false;
        XamlResourceDefinitionInfo? forward = null;
        while (scopeId.HasValue)
        {
            var currentScope = scopes[scopeId.Value];
            if (originPartition == null &&
                currentScope.Kind != XamlResourceScopeKind.ThemePartition)
            {
                var themeCandidates = FindThemePartitionCandidates(
                    currentScope.Id,
                    reference,
                    themeDefinitions);
                if (!themeCandidates.IsDefaultOrEmpty)
                {
                    conditional = themeCandidates;
                    resolved = themeCandidates[0];
                    return XamlResourceResolutionKind.ResolvedConditional;
                }
            }
            if (definitions.TryGetValue(new ResourceLookupKey(scopeId.Value, reference.ResourceKey), out var candidates))
            {
                XamlResourceDefinitionInfo? scopeForward = null;
                for (var index = candidates.Count - 1; index >= 0; index--)
                {
                    var candidate = candidates[index];
                    if (reference.ContainingDefinitionIds.Contains(candidate.ValueStableId)) continue;
                    if (reference.Kind == XamlResourceReferenceKind.Theme ||
                        candidate.Ordinal < reference.VisibleDefinitionCount)
                    { resolved = candidate; return XamlResourceResolutionKind.Resolved; }
                    sawForward = true;
                    // Keep the earliest later definition in the nearest lexical scope.
                    scopeForward = candidate;
                }
                if (forward == null) forward = scopeForward;
            }
            scopeId = currentScope.ParentId;
        }
        if (sawForward) resolved = forward;
        return sawForward && reference.Kind == XamlResourceReferenceKind.Static
            ? XamlResourceResolutionKind.ForwardReferenceDisallowed
            : XamlResourceResolutionKind.Unresolved;
    }

    private static ImmutableArray<XamlResourceDefinitionInfo> FindThemePartitionCandidates(
        ulong lexicalScopeId,
        PendingReference reference,
        IReadOnlyDictionary<ResourceLookupKey, List<XamlResourceDefinitionInfo>> definitions)
    {
        var result = ImmutableArray.CreateBuilder<XamlResourceDefinitionInfo>();
        if (!definitions.TryGetValue(new ResourceLookupKey(lexicalScopeId, reference.ResourceKey), out var values))
            return result.ToImmutable();
        for (var index = 0; index < values.Count; index++)
        {
            var candidate = values[index];
            if (index + 1 < values.Count && values[index + 1].ScopeId == candidate.ScopeId) continue;
            if (!reference.ContainingDefinitionIds.Contains(candidate.ValueStableId))
            {
                result.Add(candidate);
            }
        }
        return result.ToImmutable();
    }

    private readonly struct PendingReference
    {
        public PendingReference(
            XamlResourceKeyInfo resourceKey,
            XamlResourceReferenceKind kind,
            ulong scopeId,
            ulong stableId,
            TextSpan sourceSpan,
            int visibleDefinitionCount,
            ImmutableArray<ulong> containingDefinitionIds)
        {
            ResourceKey = resourceKey;
            Kind = kind;
            ScopeId = scopeId;
            StableId = stableId;
            SourceSpan = sourceSpan;
            VisibleDefinitionCount = visibleDefinitionCount;
            ContainingDefinitionIds = containingDefinitionIds;
        }
        public XamlResourceKeyInfo ResourceKey { get; }
        public XamlResourceReferenceKind Kind { get; }
        public ulong ScopeId { get; }
        public ulong StableId { get; }
        public TextSpan SourceSpan { get; }
        public int VisibleDefinitionCount { get; }
        public ImmutableArray<ulong> ContainingDefinitionIds { get; }
    }

    private readonly struct ResourceLookupKey : IEquatable<ResourceLookupKey>
    {
        public ResourceLookupKey(ulong scopeId, XamlResourceKeyInfo key)
        {
            ScopeId = scopeId;
            Key = key;
        }
        private ulong ScopeId { get; }
        private XamlResourceKeyInfo Key { get; }
        public bool Equals(ResourceLookupKey other) => ScopeId == other.ScopeId &&
            Key.Equals(other.Key);
        public override bool Equals(object? obj) => obj is ResourceLookupKey other && Equals(other);
        public override int GetHashCode() => unchecked((ScopeId.GetHashCode() * 397) ^
            Key.GetHashCode());
    }

    private readonly struct ExternalLookupKey : IEquatable<ExternalLookupKey>
    {
        public ExternalLookupKey(ulong ownerStableId, string identity)
        {
            OwnerStableId = ownerStableId;
            Identity = identity;
        }
        private ulong OwnerStableId { get; }
        private string Identity { get; }
        public bool Equals(ExternalLookupKey other) => OwnerStableId == other.OwnerStableId &&
            string.Equals(Identity, other.Identity, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is ExternalLookupKey other && Equals(other);
        public override int GetHashCode() => unchecked((OwnerStableId.GetHashCode() * 397) ^
            StringComparer.Ordinal.GetHashCode(Identity));
    }
}
