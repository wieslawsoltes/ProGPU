using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Serialization;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Roslyn;

public enum XamlReverseProjectionConflictKind
{
    StaleXaml,
    MissingProjection,
    AmbiguousProjection,
    UnsupportedEdit,
    SymbolChanged,
    MissingXamlOrigin,
    OverlappingEdit,
    SerializationPolicyChanged,
    InconsistentProjection
}

public sealed class XamlReverseProjectionConflict
{
    public XamlReverseProjectionConflict(XamlReverseProjectionConflictKind kind, ulong stableNodeId, string message)
    {
        Kind = kind;
        StableNodeId = stableNodeId;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }
    public XamlReverseProjectionConflictKind Kind { get; }
    public ulong StableNodeId { get; }
    public string Message { get; }
}

public sealed class XamlReverseProjectionResult
{
    internal XamlReverseProjectionResult(
        XamlSyntaxTree sourceTree,
        ImmutableArray<TextChange> changes,
        ImmutableArray<XamlReverseProjectionConflict> conflicts)
    {
        SourceTree = sourceTree;
        Changes = changes;
        Conflicts = conflicts;
    }
    public XamlSyntaxTree SourceTree { get; }
    public ImmutableArray<TextChange> Changes { get; }
    public ImmutableArray<XamlReverseProjectionConflict> Conflicts { get; }
    public bool Succeeded => Conflicts.IsEmpty;
    public SourceText GetChangedText() => Succeeded
        ? SourceTree.GetText().WithChanges(Changes)
        : SourceTree.GetText();
    public XamlSyntaxTree GetChangedTree() => SourceTree.WithChangedText(GetChangedText());
}

/// <summary>
/// Applies only registered, unambiguous generated-C#-to-XAML-attribute inversions.
/// Projection annotations identify nodes; semantic models prove member and handler identity.
/// Text matching is never used.
/// </summary>
public sealed class XamlReverseProjectionService
{
    public XamlReverseProjectionResult ApplyNameEdits(
        XamlSyntaxTree xamlTree,
        SemanticModel originalGeneratedModel,
        SemanticModel changedGeneratedModel)
    {
        if (xamlTree == null) throw new ArgumentNullException(nameof(xamlTree));
        if (originalGeneratedModel == null)
            throw new ArgumentNullException(nameof(originalGeneratedModel));
        if (changedGeneratedModel == null)
            throw new ArgumentNullException(nameof(changedGeneratedModel));

        var conflicts =
            ImmutableArray.CreateBuilder<XamlReverseProjectionConflict>();
        var changes = ImmutableArray.CreateBuilder<TextChange>();
        var originalEntries = XamlProjectionMap
            .Read(originalGeneratedModel.SyntaxTree)
            .Where(static entry => entry.Kind == XamlProjectionKind.Name)
            .ToArray();
        var changedEntries = XamlProjectionMap
            .Read(changedGeneratedModel.SyntaxTree)
            .Where(static entry => entry.Kind == XamlProjectionKind.Name)
            .GroupBy(
                static entry => ProjectionKey(entry),
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var checksum = RoslynXamlSourceChecksum.ComputeHex(
            xamlTree.GetText());
        var originalNames =
            new Dictionary<ulong, HashSet<string>>();
        var proposedNames =
            new Dictionary<ulong, HashSet<string>>();
        var representativeEntries =
            new Dictionary<ulong, XamlProjectionEntry>();

        foreach (var original in originalEntries)
        {
            representativeEntries[original.StableNodeId] = original;
            if (!string.Equals(
                    original.Checksum,
                    checksum,
                    StringComparison.Ordinal))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.StaleXaml,
                    original,
                    "The XAML checksum no longer matches the generated name projection."));
                continue;
            }
            if (!changedEntries.TryGetValue(
                    ProjectionKey(original),
                    out var candidates))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.MissingProjection,
                    original,
                    "The edited C# tree removed the registered name projection annotation."));
                continue;
            }
            if (candidates.Length != 1)
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.AmbiguousProjection,
                    original,
                    "The edited C# tree contains multiple nodes for one XAML name projection."));
                continue;
            }

            var changed = candidates[0];
            if (TryGetRuntimeNameAssignment(
                    original.GeneratedNode,
                    out var originalRuntimeAssignment,
                    out var originalRuntimeName) &&
                TryGetRuntimeNameAssignment(
                    changed.GeneratedNode,
                    out var changedRuntimeAssignment,
                    out var changedRuntimeName))
            {
                var originalMember = originalGeneratedModel
                    .GetSymbolInfo(originalRuntimeAssignment.Left)
                    .Symbol;
                var changedMember = changedGeneratedModel
                    .GetSymbolInfo(changedRuntimeAssignment.Left)
                    .Symbol;
                if (originalMember is not IPropertySymbol ||
                    changedMember is not IPropertySymbol ||
                    !SameSymbolIdentity(originalMember, changedMember) ||
                    !MatchesMemberId(original.MemberId, originalMember) ||
                    !originalRuntimeAssignment.Left.IsEquivalentTo(
                        changedRuntimeAssignment.Left))
                {
                    conflicts.Add(Conflict(
                        XamlReverseProjectionConflictKind.SymbolChanged,
                        original,
                        "The edited runtime-name assignment no longer targets the projected Roslyn property symbol."));
                    continue;
                }

                AddNameEvidence(
                    originalNames,
                    original.StableNodeId,
                    originalRuntimeName.Token.ValueText);
                if (!originalRuntimeName.IsEquivalentTo(
                        changedRuntimeName))
                {
                    AddNameEvidence(
                        proposedNames,
                        original.StableNodeId,
                        changedRuntimeName.Token.ValueText);
                }
                continue;
            }

            if (TryGetGeneratedFieldPublication(
                    original.GeneratedNode,
                    out var originalFieldAssignment,
                    out var originalFieldAccess) &&
                TryGetGeneratedFieldPublication(
                    changed.GeneratedNode,
                    out var changedFieldAssignment,
                    out var changedFieldAccess))
            {
                var originalField = originalGeneratedModel
                    .GetSymbolInfo(originalFieldAccess)
                    .Symbol as IFieldSymbol;
                var changedField = changedGeneratedModel
                    .GetSymbolInfo(changedFieldAccess)
                    .Symbol as IFieldSymbol;
                if (originalField == null ||
                    changedField == null ||
                    !HasEquivalentGeneratedFieldShape(
                        originalField,
                        changedField) ||
                    !originalFieldAssignment.Right.IsEquivalentTo(
                        changedFieldAssignment.Right))
                {
                    conflicts.Add(Conflict(
                        XamlReverseProjectionConflictKind.SymbolChanged,
                        original,
                        "The edited generated-field publication no longer has the projected field and object shape."));
                    continue;
                }

                AddNameEvidence(
                    originalNames,
                    original.StableNodeId,
                    originalField.Name);
                if (!string.Equals(
                        originalField.Name,
                        changedField.Name,
                        StringComparison.Ordinal))
                {
                    AddNameEvidence(
                        proposedNames,
                        original.StableNodeId,
                        changedField.Name);
                }
                continue;
            }

            if (!original.GeneratedNode.IsEquivalentTo(
                    changed.GeneratedNode))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.UnsupportedEdit,
                    original,
                    "Only generated field identifiers or runtime-name string literals can be reversed to x:Name."));
            }
        }

        foreach (var proposal in proposedNames)
        {
            var representative = representativeEntries[proposal.Key];
            if (proposal.Value.Count != 1)
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.InconsistentProjection,
                    representative,
                    "Multiple generated name projections propose different x:Name values."));
                continue;
            }

            var replacement = proposal.Value.Single();
            if (!SyntaxFacts.IsValidIdentifier(replacement))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.UnsupportedEdit,
                    representative,
                    $"Generated name '{replacement}' is not a valid XAML code-behind identifier."));
                continue;
            }

            var attribute = FindAttribute(
                xamlTree.GetRoot(),
                proposal.Key);
            if (attribute == null ||
                !string.Equals(
                    attribute.NamespaceUri,
                    XamlNamespaces.Language2006,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    attribute.LocalName,
                    "Name",
                    StringComparison.Ordinal))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.MissingXamlOrigin,
                    representative,
                    "The projected stable node is not an x:Name attribute in the current XAML tree."));
                continue;
            }

            if (!originalNames.TryGetValue(
                    proposal.Key,
                    out var originals) ||
                originals.Count != 1 ||
                !originals.Contains(attribute.Value))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.InconsistentProjection,
                    representative,
                    "The generated name evidence does not agree with the current x:Name value."));
                continue;
            }

            if (changes.Any(change =>
                    change.Span.OverlapsWith(attribute.ValueSpan)))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.OverlappingEdit,
                    representative,
                    "Multiple generated edits map to overlapping x:Name values."));
                continue;
            }
            changes.Add(new TextChange(
                attribute.ValueSpan,
                EscapeXmlAttributeValue(
                    replacement,
                    GetQuote(xamlTree.GetText(), attribute))));
        }

        if (conflicts.Count != 0)
            changes.Clear();
        return new XamlReverseProjectionResult(
            xamlTree,
            changes
                .OrderBy(static change => change.Span.Start)
                .ToImmutableArray(),
            conflicts.ToImmutable());
    }

    public XamlReverseProjectionResult ApplyEventHandlerEdits(
        XamlSyntaxTree xamlTree,
        SemanticModel originalGeneratedModel,
        SemanticModel changedGeneratedModel)
    {
        if (xamlTree == null) throw new ArgumentNullException(nameof(xamlTree));
        if (originalGeneratedModel == null)
            throw new ArgumentNullException(nameof(originalGeneratedModel));
        if (changedGeneratedModel == null)
            throw new ArgumentNullException(nameof(changedGeneratedModel));

        var conflicts =
            ImmutableArray.CreateBuilder<XamlReverseProjectionConflict>();
        var changes = ImmutableArray.CreateBuilder<TextChange>();
        var originalEntries = XamlProjectionMap
            .Read(originalGeneratedModel.SyntaxTree)
            .Where(static entry =>
                entry.Kind == XamlProjectionKind.Event)
            .ToArray();
        var changedEntries = XamlProjectionMap
            .Read(changedGeneratedModel.SyntaxTree)
            .Where(static entry =>
                entry.Kind == XamlProjectionKind.Event)
            .GroupBy(
                static entry => ProjectionKey(entry),
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var checksum = RoslynXamlSourceChecksum.ComputeHex(
            xamlTree.GetText());

        foreach (var original in originalEntries)
        {
            if (!string.Equals(
                    original.Checksum,
                    checksum,
                    StringComparison.Ordinal))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.StaleXaml,
                    original,
                    "The XAML checksum no longer matches the generated projection."));
                continue;
            }
            if (!changedEntries.TryGetValue(
                    ProjectionKey(original),
                    out var candidates))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.MissingProjection,
                    original,
                    "The edited C# tree removed the registered event projection annotation."));
                continue;
            }
            if (candidates.Length != 1)
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.AmbiguousProjection,
                    original,
                    "The edited C# tree contains multiple nodes for one XAML event projection."));
                continue;
            }

            var changed = candidates[0];
            if (!TryGetSimpleEventSubscription(
                    original.GeneratedNode,
                    out var originalAssignment,
                    out var originalHandler) ||
                !TryGetSimpleEventSubscription(
                    changed.GeneratedNode,
                    out var changedAssignment,
                    out var changedHandler))
            {
                if (!original.GeneratedNode.IsEquivalentTo(
                        changed.GeneratedNode))
                {
                    conflicts.Add(Conflict(
                        XamlReverseProjectionConflictKind.UnsupportedEdit,
                        original,
                        "Only direct 'this.Handler' event subscriptions can be reversed."));
                }
                continue;
            }

            var originalEvent = originalGeneratedModel
                .GetSymbolInfo(originalAssignment.Left)
                .Symbol;
            var changedEvent = changedGeneratedModel
                .GetSymbolInfo(changedAssignment.Left)
                .Symbol;
            if (originalEvent is not IEventSymbol ||
                changedEvent is not IEventSymbol ||
                !SameSymbolIdentity(originalEvent, changedEvent) ||
                !MatchesMemberId(original.MemberId, originalEvent))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.SymbolChanged,
                    original,
                    "The edited subscription no longer targets the projected Roslyn event symbol."));
                continue;
            }

            var originalMethod = originalGeneratedModel
                .GetSymbolInfo(originalHandler)
                .Symbol as IMethodSymbol;
            var changedMethod = changedGeneratedModel
                .GetSymbolInfo(changedHandler)
                .Symbol as IMethodSymbol;
            if (originalMethod == null ||
                changedMethod == null ||
                originalMethod.IsStatic ||
                changedMethod.IsStatic ||
                originalMethod.MethodKind != MethodKind.Ordinary ||
                changedMethod.MethodKind != MethodKind.Ordinary)
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.UnsupportedEdit,
                    original,
                    "The edited event handler must resolve to one ordinary instance method."));
                continue;
            }
            if (SameSymbolIdentity(originalMethod, changedMethod))
                continue;
            if (!SyntaxFacts.IsValidIdentifier(changedMethod.Name))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.UnsupportedEdit,
                    original,
                    "The edited event handler name is not a valid XAML code-behind identifier."));
                continue;
            }

            var attribute = FindAttribute(
                xamlTree.GetRoot(),
                original.StableNodeId);
            if (attribute == null)
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.MissingXamlOrigin,
                    original,
                    "The projected event stable node is not an attribute in the current XAML tree."));
                continue;
            }
            if (changes.Any(change =>
                    change.Span.OverlapsWith(attribute.ValueSpan)))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.OverlappingEdit,
                    original,
                    "Multiple C# edits map to overlapping XAML attribute values."));
                continue;
            }
            changes.Add(new TextChange(
                attribute.ValueSpan,
                changedMethod.Name));
        }

        if (conflicts.Count != 0)
            changes.Clear();
        return new XamlReverseProjectionResult(
            xamlTree,
            changes
                .OrderBy(static change => change.Span.Start)
                .ToImmutableArray(),
            conflicts.ToImmutable());
    }

    public XamlReverseProjectionResult ApplyLiteralEdits(
        XamlBoundDocument boundDocument,
        XamlSyntaxTree xamlTree,
        SemanticModel originalGeneratedModel,
        SemanticModel changedGeneratedModel)
    {
        if (boundDocument == null)
            throw new ArgumentNullException(nameof(boundDocument));
        return ApplyLiteralEdits(
            xamlTree,
            originalGeneratedModel,
            changedGeneratedModel,
            boundDocument.SerializationPlans);
    }

    public XamlReverseProjectionResult ApplyLiteralEdits(
        XamlSyntaxTree xamlTree,
        SemanticModel originalGeneratedModel,
        SemanticModel changedGeneratedModel)
        => ApplyLiteralEdits(
            xamlTree,
            originalGeneratedModel,
            changedGeneratedModel,
            serializationPlans: null);

    private static XamlReverseProjectionResult ApplyLiteralEdits(
        XamlSyntaxTree xamlTree,
        SemanticModel originalGeneratedModel,
        SemanticModel changedGeneratedModel,
        XamlSerializationPlanGraph? serializationPlans)
    {
        if (xamlTree == null) throw new ArgumentNullException(nameof(xamlTree));
        if (originalGeneratedModel == null) throw new ArgumentNullException(nameof(originalGeneratedModel));
        if (changedGeneratedModel == null) throw new ArgumentNullException(nameof(changedGeneratedModel));

        var conflicts = ImmutableArray.CreateBuilder<XamlReverseProjectionConflict>();
        var changes = ImmutableArray.CreateBuilder<TextChange>();
        var originalEntries = XamlProjectionMap.Read(originalGeneratedModel.SyntaxTree)
            .Where(static entry => entry.Kind == XamlProjectionKind.Literal)
            .ToArray();
        var changedEntries = XamlProjectionMap.Read(changedGeneratedModel.SyntaxTree)
            .Where(static entry => entry.Kind == XamlProjectionKind.Literal)
            .GroupBy(static entry => ProjectionKey(entry), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var checksum = RoslynXamlSourceChecksum.ComputeHex(
            xamlTree.GetText());
        var serializationMembers = serializationPlans == null
            ? null
            : BuildSerializationMemberIndex(serializationPlans);

        foreach (var original in originalEntries)
        {
            if (!string.Equals(original.Checksum, checksum, StringComparison.Ordinal))
            {
                conflicts.Add(Conflict(XamlReverseProjectionConflictKind.StaleXaml, original,
                    "The XAML checksum no longer matches the generated projection."));
                continue;
            }
            if (!changedEntries.TryGetValue(ProjectionKey(original), out var candidates))
            {
                conflicts.Add(Conflict(XamlReverseProjectionConflictKind.MissingProjection, original,
                    "The edited C# tree removed the registered projection annotation."));
                continue;
            }
            if (candidates.Length != 1)
            {
                conflicts.Add(Conflict(XamlReverseProjectionConflictKind.AmbiguousProjection, original,
                    "The edited C# tree contains multiple nodes for one XAML projection."));
                continue;
            }

            var changed = candidates[0];
            if (!TryGetSimpleLiteralAssignment(original.GeneratedNode, out var originalAssignment, out var originalLiteral) ||
                !TryGetSimpleLiteralAssignment(changed.GeneratedNode, out var changedAssignment, out var changedLiteral))
            {
                if (!original.GeneratedNode.IsEquivalentTo(changed.GeneratedNode))
                    conflicts.Add(Conflict(XamlReverseProjectionConflictKind.UnsupportedEdit, original,
                        "Only simple property assignments whose right side remains a C# literal can be reversed."));
                continue;
            }
            if (originalLiteral.IsEquivalentTo(changedLiteral)) continue;
            if (serializationMembers != null &&
                serializationMembers.TryGetValue(
                    original.StableNodeId,
                    out var plannedMembers) &&
                (plannedMembers.Length != 1 ||
                 plannedMembers[0].Disposition !=
                 XamlSerializationDisposition.Attribute))
            {
                conflicts.Add(Conflict(
                    XamlReverseProjectionConflictKind.SerializationPolicyChanged,
                    original,
                    "The canonical serialization plan no longer represents this member as one writable XAML attribute."));
                continue;
            }

            var originalSymbol = originalGeneratedModel.GetSymbolInfo(originalAssignment.Left).Symbol;
            var changedSymbol = changedGeneratedModel.GetSymbolInfo(changedAssignment.Left).Symbol;
            if (originalSymbol == null || changedSymbol == null ||
                !SameSymbolIdentity(originalSymbol, changedSymbol) ||
                !MatchesMemberId(original.MemberId, originalSymbol))
            {
                conflicts.Add(Conflict(XamlReverseProjectionConflictKind.SymbolChanged, original,
                    "The edited assignment no longer targets the projected Roslyn member symbol. " +
                    $"Expected '{original.MemberId ?? "<none>"}', original '{originalSymbol?.ToDisplayString() ?? "<unresolved>"}', " +
                    $"changed '{changedSymbol?.ToDisplayString() ?? "<unresolved>"}'."));
                continue;
            }

            var attribute = FindAttribute(xamlTree.GetRoot(), original.StableNodeId);
            if (attribute == null)
            {
                conflicts.Add(Conflict(XamlReverseProjectionConflictKind.MissingXamlOrigin, original,
                    "The projected stable node is not an attribute in the current XAML tree."));
                continue;
            }
            if (!TrySerializeLiteral(changedLiteral, out var replacement))
            {
                conflicts.Add(Conflict(XamlReverseProjectionConflictKind.UnsupportedEdit, original,
                    "The edited literal kind has no registered XAML inverse."));
                continue;
            }
            replacement = EscapeXmlAttributeValue(replacement, GetQuote(xamlTree.GetText(), attribute));
            if (changes.Any(change => change.Span.OverlapsWith(attribute.ValueSpan)))
            {
                conflicts.Add(Conflict(XamlReverseProjectionConflictKind.OverlappingEdit, original,
                    "Multiple C# edits map to overlapping XAML attribute values."));
                continue;
            }
            changes.Add(new TextChange(attribute.ValueSpan, replacement));
        }

        if (conflicts.Count != 0) changes.Clear();
        return new XamlReverseProjectionResult(
            xamlTree,
            changes.OrderBy(static change => change.Span.Start).ToImmutableArray(),
            conflicts.ToImmutable());
    }

    private static IReadOnlyDictionary<ulong, XamlMemberSerializationPlan[]>
        BuildSerializationMemberIndex(XamlSerializationPlanGraph plans)
    {
        // Literal projections are annotated with the stable ID of the bound value,
        // while structural projections can retain the enclosing member ID. Index
        // both identities so reverse edits cannot bypass the canonical save policy.
        return plans.Objects.Values
            .SelectMany(static plan => plan.Members)
            .SelectMany(static member =>
                member.Source.Values
                    .Select(value => new KeyValuePair<ulong, XamlMemberSerializationPlan>(
                        value.StableId,
                        member))
                    .Prepend(new KeyValuePair<ulong, XamlMemberSerializationPlan>(
                        member.Source.StableId,
                        member)))
            .GroupBy(static entry => entry.Key)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static entry => entry.Value).ToArray());
    }

    private static bool TryGetSimpleLiteralAssignment(
        SyntaxNodeOrToken nodeOrToken,
        out AssignmentExpressionSyntax assignment,
        out LiteralExpressionSyntax literal)
    {
        assignment = null!;
        literal = null!;
        var node = nodeOrToken.AsNode();
        if (node is not LiteralExpressionSyntax value ||
            node.Parent is not AssignmentExpressionSyntax candidate ||
            !candidate.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
            !ReferenceEquals(candidate.Right, node)) return false;
        assignment = candidate;
        literal = value;
        return true;
    }

    private static bool TryGetSimpleEventSubscription(
        SyntaxNodeOrToken nodeOrToken,
        out AssignmentExpressionSyntax assignment,
        out MemberAccessExpressionSyntax handler)
    {
        assignment = null!;
        handler = null!;
        var node = nodeOrToken.AsNode();
        if (node is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax candidate
            } ||
            !candidate.IsKind(SyntaxKind.AddAssignmentExpression) ||
            candidate.Right is not MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax
            } memberAccess)
        {
            return false;
        }
        assignment = candidate;
        handler = memberAccess;
        return true;
    }

    private static bool TryGetRuntimeNameAssignment(
        SyntaxNodeOrToken nodeOrToken,
        out AssignmentExpressionSyntax assignment,
        out LiteralExpressionSyntax name)
    {
        assignment = null!;
        name = null!;
        var node = nodeOrToken.AsNode();
        if (node is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax candidate
            } ||
            !candidate.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
            candidate.Right is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return false;
        }
        assignment = candidate;
        name = literal;
        return true;
    }

    private static bool TryGetGeneratedFieldPublication(
        SyntaxNodeOrToken nodeOrToken,
        out AssignmentExpressionSyntax assignment,
        out MemberAccessExpressionSyntax field)
    {
        assignment = null!;
        field = null!;
        var node = nodeOrToken.AsNode();
        if (node is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax candidate
            } ||
            !candidate.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
            candidate.Left is not MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax
            } memberAccess)
        {
            return false;
        }
        assignment = candidate;
        field = memberAccess;
        return true;
    }

    private static bool HasEquivalentGeneratedFieldShape(
        IFieldSymbol original,
        IFieldSymbol changed) =>
        !original.IsStatic &&
        !changed.IsStatic &&
        original.IsReadOnly == changed.IsReadOnly &&
        original.IsConst == changed.IsConst &&
        original.DeclaredAccessibility == changed.DeclaredAccessibility &&
        string.Equals(
            original.Type.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat),
            changed.Type.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat),
            StringComparison.Ordinal) &&
        string.Equals(
            original.ContainingType.GetDocumentationCommentId(),
            changed.ContainingType.GetDocumentationCommentId(),
            StringComparison.Ordinal) &&
        string.Equals(
            original.ContainingAssembly.Identity.ToString(),
            changed.ContainingAssembly.Identity.ToString(),
            StringComparison.Ordinal) &&
        HasEquivalentGeneratedFieldDeclaration(
            original,
            changed);

    private static bool HasEquivalentGeneratedFieldDeclaration(
        IFieldSymbol original,
        IFieldSymbol changed)
    {
        if (original.DeclaringSyntaxReferences.Length != 1 ||
            changed.DeclaringSyntaxReferences.Length != 1 ||
            original.DeclaringSyntaxReferences[0].GetSyntax() is not
                VariableDeclaratorSyntax originalDeclarator ||
            changed.DeclaringSyntaxReferences[0].GetSyntax() is not
                VariableDeclaratorSyntax changedDeclarator ||
            originalDeclarator.Parent is not
                VariableDeclarationSyntax originalDeclaration ||
            changedDeclarator.Parent is not
                VariableDeclarationSyntax changedDeclaration ||
            originalDeclaration.Parent is not
                FieldDeclarationSyntax originalField ||
            changedDeclaration.Parent is not
                FieldDeclarationSyntax changedField)
        {
            return false;
        }

        return string.Equals(
                originalDeclarator.SyntaxTree.FilePath,
                changedDeclarator.SyntaxTree.FilePath,
                StringComparison.Ordinal) &&
            originalDeclarator.SpanStart ==
                changedDeclarator.SpanStart &&
            originalDeclaration.Variables.Count == 1 &&
            changedDeclaration.Variables.Count == 1 &&
            originalDeclaration.Type.IsEquivalentTo(
                changedDeclaration.Type) &&
            EquivalentNodes(
                originalDeclarator.Initializer,
                changedDeclarator.Initializer) &&
            originalField.AttributeLists.Count == 0 &&
            changedField.AttributeLists.Count == 0 &&
            originalField.Modifiers
                .Select(static token => token.Kind())
                .SequenceEqual(
                    changedField.Modifiers.Select(
                        static token => token.Kind()));
    }

    private static bool EquivalentNodes(
        SyntaxNode? original,
        SyntaxNode? changed) =>
        original == null
            ? changed == null
            : changed != null &&
              original.IsEquivalentTo(changed);

    private static void AddNameEvidence(
        IDictionary<ulong, HashSet<string>> evidence,
        ulong stableNodeId,
        string name)
    {
        if (!evidence.TryGetValue(stableNodeId, out var names))
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            evidence.Add(stableNodeId, names);
        }
        names.Add(name);
    }

    private static bool TrySerializeLiteral(LiteralExpressionSyntax literal, out string value)
    {
        switch (literal.Kind())
        {
            case SyntaxKind.StringLiteralExpression:
            case SyntaxKind.CharacterLiteralExpression:
                value = literal.Token.ValueText;
                return true;
            case SyntaxKind.TrueLiteralExpression: value = "True"; return true;
            case SyntaxKind.FalseLiteralExpression: value = "False"; return true;
            case SyntaxKind.NullLiteralExpression: value = "{x:Null}"; return true;
            case SyntaxKind.NumericLiteralExpression:
                value = Convert.ToString(literal.Token.Value, CultureInfo.InvariantCulture) ?? literal.Token.ValueText;
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private static bool MatchesMemberId(string? memberId, ISymbol symbol) =>
        string.IsNullOrEmpty(memberId) ||
        string.Equals(memberId, symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(), StringComparison.Ordinal);

    private static bool SameSymbolIdentity(ISymbol left, ISymbol right) =>
        left.Kind == right.Kind &&
        string.Equals(left.GetDocumentationCommentId(), right.GetDocumentationCommentId(), StringComparison.Ordinal) &&
        string.Equals(left.ContainingAssembly?.Identity.ToString(), right.ContainingAssembly?.Identity.ToString(), StringComparison.Ordinal);

    private static XamlAttributeSyntax? FindAttribute(XamlObjectSyntax? root, ulong stableId)
    {
        if (root == null) return null;
        foreach (var attribute in root.Attributes)
            if (attribute.StableId == stableId) return attribute;
        foreach (var child in root.Children.OfType<XamlObjectSyntax>())
        {
            var found = FindAttribute(child, stableId);
            if (found != null) return found;
        }
        return null;
    }

    private static char GetQuote(SourceText text, XamlAttributeSyntax attribute)
    {
        var position = attribute.ValueSpan.Start - 1;
        return position >= 0 && position < text.Length && text[position] == '\'' ? '\'' : '"';
    }

    private static string EscapeXmlAttributeValue(string value, char quote)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '"' when quote == '"': builder.Append("&quot;"); break;
                case '\'' when quote == '\'': builder.Append("&apos;"); break;
                default: builder.Append(character); break;
            }
        }
        return builder.ToString();
    }

    private static string ProjectionKey(XamlProjectionEntry entry) =>
        entry.StableNodeId.ToString("x16", CultureInfo.InvariantCulture) + ":" +
        ((int)entry.Kind).ToString(CultureInfo.InvariantCulture) + ":" + (entry.MemberId ?? string.Empty);

    private static XamlReverseProjectionConflict Conflict(
        XamlReverseProjectionConflictKind kind,
        XamlProjectionEntry entry,
        string message) => new XamlReverseProjectionConflict(kind, entry.StableNodeId, message);

}
