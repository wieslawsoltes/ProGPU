using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Schema;

namespace ProGPU.Xaml.Lowering;

/// <summary>
/// Framework-neutral IR policy for carrying an exact deferred-factory context type into
/// structured emission without rediscovering a framework member by name in the compiler core.
/// </summary>
public interface IXamlIrDeferredContentContextTypePolicy
{
    bool TryGetDeferredContentContextType(
        XamlIrObject owner,
        XamlIrOperation deferredOperation,
        out XamlTypeInfo contextType);
}

public enum XamlIrObjectKind
{
    ExistingRoot,
    Create,
    Retrieve,
    InvokeMarkupExtension,
    CreateArray,
    InitializeFromText,
    Error
}

public enum XamlIrInitializationMode
{
    BottomUp,
    TopDown
}

public enum XamlIrOperationKind
{
    SetMember,
    SetAttachableMember,
    AddCollectionItem,
    AddDictionaryItem,
    RetrieveMember,
    SubscribeEvent,
    ApplyDirective,
    SetIntrinsicMember,
    SetDeferredContent,
    Error
}

public abstract class XamlIrValue
{
    protected XamlIrValue(TextSpan sourceSpan, ulong stableId)
    {
        SourceSpan = sourceSpan;
        StableId = stableId;
    }
    public TextSpan SourceSpan { get; }
    public ulong StableId { get; }
}

public sealed class XamlIrText : XamlIrValue
{
    public XamlIrText(string text, TextSpan sourceSpan, ulong stableId, Schema.XamlTextSyntaxInfo? textSyntax = null)
        : base(sourceSpan, stableId)
    {
        Text = text ?? string.Empty;
        TextSyntax = textSyntax ?? Schema.XamlTextSyntaxInfo.None;
    }
    public string Text { get; }
    public Schema.XamlTextSyntaxInfo TextSyntax { get; }
}

public sealed class XamlIrType : XamlIrValue
{
    public XamlIrType(XamlBoundTypeReference type, TextSpan sourceSpan, ulong stableId)
        : base(sourceSpan, stableId) => Type = type ?? throw new ArgumentNullException(nameof(type));
    public XamlBoundTypeReference Type { get; }
}

public sealed class XamlIrStaticMember : XamlIrValue
{
    public XamlIrStaticMember(XamlBoundStaticMemberValue value)
        : base(value?.SourceSpan ?? default, value?.StableId ?? 0) => Value = value!;
    public XamlBoundStaticMemberValue Value { get; }
}

public sealed class XamlIrFactoryMethod : XamlIrValue
{
    public XamlIrFactoryMethod(XamlBoundFactoryMethodValue value)
        : base(value?.SourceSpan ?? default, value?.StableId ?? 0) => Value = value!;
    public XamlBoundFactoryMethodValue Value { get; }
}

public sealed class XamlIrNameReference : XamlIrValue
{
    public XamlIrNameReference(XamlBoundNameReferenceValue value)
        : base(value?.SourceSpan ?? default, value?.StableId ?? 0) => Name = value!.Name;
    public string Name { get; }
}

public sealed class XamlIrCompiledBinding : XamlIrValue
{
    public XamlIrCompiledBinding(XamlBoundCompiledBinding binding, XamlIrObject extension)
        : base(binding?.SourceSpan ?? default, binding?.StableId ?? 0)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Extension = extension ?? throw new ArgumentNullException(nameof(extension));
    }

    public XamlBoundCompiledBinding Binding { get; }
    public XamlIrObject Extension { get; }
}

public sealed class XamlIrBinding : XamlIrValue
{
    public XamlIrBinding(XamlBoundBinding binding, XamlIrObject extension)
        : base(binding?.SourceSpan ?? default, binding?.StableId ?? 0)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Extension = extension ?? throw new ArgumentNullException(nameof(extension));
    }

    public XamlBoundBinding Binding { get; }
    public XamlIrObject Extension { get; }
}

public sealed class XamlIrResourceReference : XamlIrValue
{
    public XamlIrResourceReference(
        XamlResourceReferenceInfo reference,
        XamlTypeInfo? resolvedValueType = null,
        XamlResourceKeyInfo? terminalResourceKey = null)
        : base(reference?.SourceSpan ?? default, reference?.StableId ?? 0)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        ResolvedValueType = resolvedValueType;
        TerminalResourceKey = terminalResourceKey ?? reference.ResourceKey;
    }
    public XamlResourceReferenceInfo Reference { get; }
    /// <summary>
    /// Statically known type of the resolved definition. Null represents an external,
    /// unresolved, or variant set without one common value type.
    /// </summary>
    public XamlTypeInfo? ResolvedValueType { get; }
    /// <summary>
    /// Last resource key reached through statically known alias definitions. Framework
    /// profiles can use this evidence to type authoritative platform-resource leaves.
    /// </summary>
    public XamlResourceKeyInfo TerminalResourceKey { get; }
}

public sealed class XamlIrOperation
{
    public XamlIrOperation(
        XamlIrOperationKind kind,
        XamlBoundMemberReference member,
        ImmutableArray<XamlIrValue> values,
        TextSpan sourceSpan,
        ulong stableId,
        Infoset.XamlNamespaceCondition? condition = null)
    {
        Kind = kind;
        Member = member ?? throw new ArgumentNullException(nameof(member));
        Values = values;
        SourceSpan = sourceSpan;
        StableId = stableId;
        Condition = condition;
    }
    public XamlIrOperationKind Kind { get; }
    public XamlBoundMemberReference Member { get; }
    public ImmutableArray<XamlIrValue> Values { get; }
    public TextSpan SourceSpan { get; }
    public ulong StableId { get; }
    public Infoset.XamlNamespaceCondition? Condition { get; }
}

public sealed class XamlIrObject : XamlIrValue
{
    public XamlIrObject(
        XamlIrObjectKind kind,
        XamlBoundTypeReference type,
        ImmutableArray<XamlIrOperation> operations,
        TextSpan sourceSpan,
        ulong stableId,
        Infoset.XamlNamespaceCondition? condition = null)
        : this(
            kind,
            XamlIrInitializationMode.BottomUp,
            type,
            operations,
            sourceSpan,
            stableId,
            condition)
    {
    }

    public XamlIrObject(
        XamlIrObjectKind kind,
        XamlIrInitializationMode initializationMode,
        XamlBoundTypeReference type,
        ImmutableArray<XamlIrOperation> operations,
        TextSpan sourceSpan,
        ulong stableId,
        Infoset.XamlNamespaceCondition? condition = null)
        : base(sourceSpan, stableId)
    {
        Kind = kind;
        InitializationMode = initializationMode;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Operations = operations;
        Condition = condition;
    }
    public XamlIrObjectKind Kind { get; }
    public XamlIrInitializationMode InitializationMode { get; }
    public XamlBoundTypeReference Type { get; }
    public ImmutableArray<XamlIrOperation> Operations { get; }
    public Infoset.XamlNamespaceCondition? Condition { get; }
}

public sealed class XamlConstructionProgram
{
    public XamlConstructionProgram(
        XamlBoundDocument boundDocument,
        XamlIrObject? root,
        ImmutableArray<Diagnostic> diagnostics,
        XamlResourceGraph? resourceGraph = null)
    {
        BoundDocument = boundDocument ?? throw new ArgumentNullException(nameof(boundDocument));
        Root = root;
        Diagnostics = diagnostics;
        ResourceGraph = resourceGraph;
    }
    public XamlBoundDocument BoundDocument { get; }
    public XamlIrObject? Root { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public XamlResourceGraph? ResourceGraph { get; }
}

public sealed class XamlConstructionLowerer
{
    public XamlConstructionProgram Lower(XamlBoundDocument document)
        => Lower(document, resourceGraph: null);

    public XamlConstructionProgram Lower(XamlBoundDocument document, XamlResourceGraph? resourceGraph)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo>? resourceReferences = resourceGraph == null
            ? null
            : resourceGraph.References.ToDictionary(static reference => reference.StableId);
        var resourceValueTypes = resourceGraph == null || document.Root == null
            ? null
            : XamlResourceTypeEvidence.CreateValueTypeIndex(document.Root, resourceGraph);
        var terminalResourceKeys = resourceGraph == null
            ? null
            : CreateTerminalResourceKeyIndex(resourceGraph);
        var root = document.Root == null
            ? null
            : LowerObject(
                document.Root,
                isRoot: true,
                allowTopDown: false,
                resourceReferences,
                resourceValueTypes,
                terminalResourceKeys);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(
            document.Diagnostics.Length + (resourceGraph?.Diagnostics.Length ?? 0));
        diagnostics.AddRange(document.Diagnostics);
        if (resourceGraph != null) diagnostics.AddRange(resourceGraph.Diagnostics);
        return new XamlConstructionProgram(document, root, diagnostics.ToImmutable(), resourceGraph);
    }

    private static XamlIrObject LowerObject(
        XamlBoundObject value,
        bool isRoot,
        bool allowTopDown,
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo>? resourceReferences,
        IReadOnlyDictionary<ulong, XamlTypeInfo>? resourceValueTypes,
        IReadOnlyDictionary<ulong, XamlResourceKeyInfo>? terminalResourceKeys)
    {
        var isArrayIntrinsic = string.Equals(
                value.Type.RequestedName.NamespaceUri,
                ProGPU.Xaml.Syntax.XamlNamespaces.Language2006,
                StringComparison.Ordinal) &&
            (string.Equals(
                 value.Type.RequestedName.LocalName,
                 "Array",
                 StringComparison.Ordinal) ||
             string.Equals(
                 value.Type.RequestedName.LocalName,
                 "ArrayExtension",
                 StringComparison.Ordinal));
        var operations = ImmutableArray.CreateBuilder<XamlIrOperation>(value.Members.Length);
        foreach (var member in XamlBoundMemberOrdering.Order(value.Members))
        {
            var operationKind = ClassifyOperation(member);
            var allowChildTopDown =
                !isArrayIntrinsic &&
                CanPublishChildBeforePopulation(operationKind);
            var values = ImmutableArray.CreateBuilder<XamlIrValue>(member.Values.Length);
            foreach (var child in member.Values)
            {
                values.Add(child switch
                {
                    XamlBoundObject childObject => LowerObjectValue(
                        childObject,
                        allowChildTopDown,
                        resourceReferences,
                        resourceValueTypes,
                        terminalResourceKeys),
                    XamlBoundTypeValue typeValue => new XamlIrType(typeValue.Type, child.SourceSpan, child.StableId),
                    XamlBoundStaticMemberValue staticValue => new XamlIrStaticMember(staticValue),
                    XamlBoundFactoryMethodValue factoryValue => new XamlIrFactoryMethod(factoryValue),
                    XamlBoundNameReferenceValue referenceValue => new XamlIrNameReference(referenceValue),
                    XamlBoundCompiledBinding compiledBinding => new XamlIrCompiledBinding(
                        compiledBinding,
                        LowerObject(
                            compiledBinding.Extension,
                            isRoot: false,
                            allowTopDown: false,
                            resourceReferences,
                            resourceValueTypes,
                            terminalResourceKeys)),
                    XamlBoundBinding binding => new XamlIrBinding(
                        binding,
                        LowerObject(
                            binding.Extension,
                            isRoot: false,
                            allowTopDown: false,
                            resourceReferences,
                            resourceValueTypes,
                            terminalResourceKeys)),
                    XamlBoundText textValue => new XamlIrText(textValue.Text, child.SourceSpan, child.StableId, textValue.TextSyntax),
                    _ => throw new InvalidOperationException("Unsupported bound XAML value kind: " + child.GetType().FullName)
                });
            }
            operations.Add(new XamlIrOperation(
                operationKind,
                member.Member,
                values.ToImmutable(),
                member.SourceSpan,
                member.StableId,
                member.Condition));
        }
        var hasInitializationText = operations.Any(static operation =>
            operation.Kind == XamlIrOperationKind.ApplyDirective &&
            string.Equals(operation.Member.RequestedName.NamespaceUri,
                ProGPU.Xaml.Syntax.XamlNamespaces.Language2006, StringComparison.Ordinal) &&
            string.Equals(operation.Member.RequestedName.LocalName, "Initialization", StringComparison.Ordinal));
        var kind = value.Type.IsError
            ? XamlIrObjectKind.Error
            : isRoot
                ? XamlIrObjectKind.ExistingRoot
                : isArrayIntrinsic
                    ? XamlIrObjectKind.CreateArray
                : hasInitializationText
                    ? XamlIrObjectKind.InitializeFromText
                : value.IsRetrieved
                    ? XamlIrObjectKind.Retrieve
                    : value.IsMarkupExtension
                        ? XamlIrObjectKind.InvokeMarkupExtension
                        : XamlIrObjectKind.Create;
        var initializationMode =
            kind == XamlIrObjectKind.Create &&
            allowTopDown &&
            value.Type.Symbol?.IsUsableDuringInitialization == true
                ? XamlIrInitializationMode.TopDown
                : XamlIrInitializationMode.BottomUp;
        return new XamlIrObject(
            kind,
            initializationMode,
            value.Type,
            operations.ToImmutable(),
            value.SourceSpan,
            value.StableId,
            value.Condition);
    }

    private static XamlIrValue LowerObjectValue(
        XamlBoundObject value,
        bool allowTopDown,
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo>? resourceReferences,
        IReadOnlyDictionary<ulong, XamlTypeInfo>? resourceValueTypes,
        IReadOnlyDictionary<ulong, XamlResourceKeyInfo>? terminalResourceKeys)
    {
        if (resourceReferences != null && resourceReferences.TryGetValue(value.StableId, out var reference))
        {
            XamlTypeInfo? resolvedValueType = null;
            if (resourceValueTypes != null)
                resourceValueTypes.TryGetValue(reference.StableId, out resolvedValueType);
            XamlResourceKeyInfo? terminalResourceKey = null;
            if (terminalResourceKeys != null)
                terminalResourceKeys.TryGetValue(reference.StableId, out terminalResourceKey);
            return new XamlIrResourceReference(
                reference,
                resolvedValueType,
                terminalResourceKey);
        }
        return LowerObject(
            value,
            isRoot: false,
            allowTopDown,
            resourceReferences,
            resourceValueTypes,
            terminalResourceKeys);
    }

    private static IReadOnlyDictionary<ulong, XamlResourceKeyInfo> CreateTerminalResourceKeyIndex(
        XamlResourceGraph graph)
    {
        var references = graph.References.ToDictionary(static reference => reference.StableId);
        var result = new Dictionary<ulong, XamlResourceKeyInfo>();
        foreach (var reference in graph.References)
            result[reference.StableId] = ResolveTerminalResourceKey(
                reference,
                references,
                new HashSet<ulong>());
        return result;
    }

    private static XamlResourceKeyInfo ResolveTerminalResourceKey(
        XamlResourceReferenceInfo reference,
        IReadOnlyDictionary<ulong, XamlResourceReferenceInfo> references,
        ISet<ulong> visiting)
    {
        if (!visiting.Add(reference.StableId)) return reference.ResourceKey;
        try
        {
            IEnumerable<ulong> definitionIds =
                reference.Resolution == XamlResourceResolutionKind.ResolvedConditional
                    ? reference.CandidateDefinitionStableIds
                    : reference.DefinitionStableId.HasValue
                        ? new[] { reference.DefinitionStableId.Value }
                        : reference.CandidateDefinitionStableIds;
            XamlResourceKeyInfo? common = null;
            var hasAlias = false;
            foreach (var definitionId in definitionIds.Distinct())
            {
                if (!references.TryGetValue(definitionId, out var alias) ||
                    alias.Kind != XamlResourceReferenceKind.Static)
                    return reference.ResourceKey;
                var terminal = ResolveTerminalResourceKey(alias, references, visiting);
                hasAlias = true;
                if (common == null)
                    common = terminal;
                else if (!common.Equals(terminal))
                    return reference.ResourceKey;
            }
            return hasAlias && common != null ? common : reference.ResourceKey;
        }
        finally
        {
            visiting.Remove(reference.StableId);
        }
    }

    private static bool CanPublishChildBeforePopulation(
        XamlIrOperationKind kind) =>
        kind == XamlIrOperationKind.SetMember ||
        kind == XamlIrOperationKind.SetAttachableMember ||
        kind == XamlIrOperationKind.AddCollectionItem ||
        kind == XamlIrOperationKind.AddDictionaryItem;

    private static XamlIrOperationKind ClassifyOperation(XamlBoundMember boundMember)
    {
        var member = boundMember.Member;
        switch (member.Kind)
        {
            case XamlBoundReferenceKind.Directive: return XamlIrOperationKind.ApplyDirective;
            case XamlBoundReferenceKind.Intrinsic: return XamlIrOperationKind.SetIntrinsicMember;
            case XamlBoundReferenceKind.CollectionItems: return XamlIrOperationKind.AddCollectionItem;
            case XamlBoundReferenceKind.DictionaryItems: return XamlIrOperationKind.AddDictionaryItem;
            case XamlBoundReferenceKind.Error: return XamlIrOperationKind.Error;
        }
        switch (member.Symbol!.Kind)
        {
            case Schema.XamlMemberKind.DeferredContent: return XamlIrOperationKind.SetDeferredContent;
            case Schema.XamlMemberKind.Collection:
            case Schema.XamlMemberKind.Dictionary:
                if (boundMember.Values.Length == 1 &&
                    IsMarkupExtensionValue(boundMember.Values[0]))
                    return XamlIrOperationKind.SetMember;
                if (boundMember.Values.Length == 1 && boundMember.Values[0] is XamlBoundObject { IsRetrieved: true })
                    return XamlIrOperationKind.RetrieveMember;
                return member.Symbol.Kind == Schema.XamlMemberKind.Collection
                    ? XamlIrOperationKind.AddCollectionItem
                    : XamlIrOperationKind.AddDictionaryItem;
            case Schema.XamlMemberKind.AttachableProperty: return XamlIrOperationKind.SetAttachableMember;
            case Schema.XamlMemberKind.Event: return XamlIrOperationKind.SubscribeEvent;
            default: return member.Symbol.CanWrite ? XamlIrOperationKind.SetMember : XamlIrOperationKind.RetrieveMember;
        }
    }

    private static bool IsMarkupExtensionValue(XamlBoundValue value) =>
        value is XamlBoundObject { IsMarkupExtension: true } or
            XamlBoundBinding or
            XamlBoundCompiledBinding;
}
