using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Schema;

namespace ProGPU.Xaml.Resources;

/// <summary>
/// Derives exact resource value types from the canonical resource graph without constructing
/// framework objects or invoking markup extensions. Evidence is published only when every
/// reachable variant resolves to the same Roslyn type symbol.
/// </summary>
public static class XamlResourceTypeEvidence
{
    public static IReadOnlyDictionary<ulong, XamlTypeInfo> CreateValueTypeIndex(
        XamlBoundObject root,
        XamlResourceGraph graph,
        Func<string, XamlTypeInfo?>? externalTypeResolver = null)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        if (graph == null) throw new ArgumentNullException(nameof(graph));

        var objects = new Dictionary<ulong, XamlBoundObject>();
        IndexObjects(root, objects);
        var references = graph.References.ToDictionary(static reference => reference.StableId);
        var result = new Dictionary<ulong, XamlTypeInfo>();
        foreach (var reference in graph.References)
        {
            var valueType = ResolveReferenceValueType(
                reference,
                objects,
                references,
                externalTypeResolver,
                new HashSet<ulong>());
            if (valueType != null)
                result[reference.StableId] = valueType;
        }
        return result;
    }

    private static XamlTypeInfo? ResolveReferenceValueType(
        XamlResourceReferenceInfo reference,
        IReadOnlyDictionary<ulong, XamlBoundObject> objects,
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo> references,
        Func<string, XamlTypeInfo?>? externalTypeResolver,
        ISet<ulong> visiting)
    {
        if (!visiting.Add(reference.StableId)) return null;
        try
        {
            if (reference.Resolution == XamlResourceResolutionKind.ResolvedExternal)
                return ResolveExternalValueType(
                    reference.ExternalCandidates,
                    externalTypeResolver);
            if (reference.Resolution is not (
                    XamlResourceResolutionKind.Resolved or
                    XamlResourceResolutionKind.ResolvedConditional))
                return null;

            IEnumerable<ulong> candidateIds =
                reference.Resolution == XamlResourceResolutionKind.ResolvedConditional
                    ? reference.CandidateDefinitionStableIds
                    : reference.DefinitionStableId.HasValue
                        ? new[] { reference.DefinitionStableId.Value }
                        : reference.CandidateDefinitionStableIds;
            XamlTypeInfo? commonType = null;
            var hasCandidate = false;
            foreach (var candidateId in candidateIds.Distinct())
            {
                var candidateType = ResolveDefinitionValueType(
                    candidateId,
                    objects,
                    references,
                    externalTypeResolver,
                    visiting);
                if (candidateType == null) return null;
                hasCandidate = true;
                if (commonType == null)
                    commonType = candidateType;
                else if (!SymbolEqualityComparer.Default.Equals(
                             commonType.Symbol,
                             candidateType.Symbol))
                    return null;
            }
            return hasCandidate ? commonType : null;
        }
        finally
        {
            visiting.Remove(reference.StableId);
        }
    }

    private static XamlTypeInfo? ResolveDefinitionValueType(
        ulong definitionStableId,
        IReadOnlyDictionary<ulong, XamlBoundObject> objects,
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo> references,
        Func<string, XamlTypeInfo?>? externalTypeResolver,
        ISet<ulong> visiting)
    {
        if (references.TryGetValue(definitionStableId, out var alias))
            return ResolveReferenceValueType(
                alias,
                objects,
                references,
                externalTypeResolver,
                visiting);
        return objects.TryGetValue(definitionStableId, out var value)
            ? value.Type.Symbol
            : null;
    }

    private static XamlTypeInfo? ResolveExternalValueType(
        IEnumerable<XamlExternalResourceDefinition> candidates,
        Func<string, XamlTypeInfo?>? externalTypeResolver)
    {
        if (externalTypeResolver == null) return null;
        XamlTypeInfo? commonType = null;
        var hasCandidate = false;
        foreach (var candidate in candidates)
        {
            var candidateType = externalTypeResolver(candidate.TypeName);
            if (candidateType == null) return null;
            hasCandidate = true;
            if (commonType == null)
                commonType = candidateType;
            else if (!SymbolEqualityComparer.Default.Equals(
                         commonType.Symbol,
                         candidateType.Symbol))
                return null;
        }
        return hasCandidate ? commonType : null;
    }

    private static void IndexObjects(
        XamlBoundObject value,
        IDictionary<ulong, XamlBoundObject> result)
    {
        result[value.StableId] = value;
        foreach (var member in value.Members)
            foreach (var child in member.Values)
            {
                if (child is XamlBoundObject childObject)
                    IndexObjects(childObject, result);
                else if (child is XamlBoundBinding binding)
                    IndexObjects(binding.Extension, result);
                else if (child is XamlBoundCompiledBinding compiled)
                    IndexObjects(compiled.Extension, result);
            }
    }
}
