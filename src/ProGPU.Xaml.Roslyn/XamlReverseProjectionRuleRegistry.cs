using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Roslyn;

public static class XamlReverseProjectionRuleContract
{
    public const int CurrentVersion = 1;
}

public sealed class XamlReverseProjectionContext
{
    internal XamlReverseProjectionContext(
        XamlSyntaxTree sourceTree,
        XamlBoundDocument? boundDocument,
        SemanticModel originalGeneratedModel,
        SemanticModel changedGeneratedModel)
    {
        SourceTree = sourceTree;
        BoundDocument = boundDocument;
        OriginalGeneratedModel = originalGeneratedModel;
        ChangedGeneratedModel = changedGeneratedModel;
    }

    public XamlSyntaxTree SourceTree { get; }

    public XamlBoundDocument? BoundDocument { get; }

    public SemanticModel OriginalGeneratedModel { get; }

    public SemanticModel ChangedGeneratedModel { get; }
}

public sealed class XamlReverseProjectionRuleResult
{
    public XamlReverseProjectionRuleResult(
        IEnumerable<TextChange>? changes = null,
        IEnumerable<XamlReverseProjectionConflict>? conflicts = null)
    {
        Changes = changes?.ToImmutableArray() ??
            ImmutableArray<TextChange>.Empty;
        Conflicts = conflicts?.ToImmutableArray() ??
            ImmutableArray<XamlReverseProjectionConflict>.Empty;

        if (Conflicts.Any(static conflict => conflict == null))
        {
            throw new ArgumentException(
                "Reverse-projection rule conflicts cannot contain null.",
                nameof(conflicts));
        }
    }

    public ImmutableArray<TextChange> Changes { get; }

    public ImmutableArray<XamlReverseProjectionConflict> Conflicts { get; }
}

public interface IXamlReverseProjectionRule
{
    string Id { get; }

    int ContractVersion { get; }

    int Priority { get; }

    XamlReverseProjectionRuleResult Apply(
        XamlReverseProjectionContext context);
}

public sealed class XamlReverseProjectionRuleRegistry
{
    private readonly ImmutableArray<
        XamlReverseProjectionRuleRegistration> _registrations;

    private XamlReverseProjectionRuleRegistry(
        ImmutableArray<XamlReverseProjectionRuleRegistration>
            registrations)
    {
        _registrations = registrations;
        RuleIds = registrations
            .Select(static registration => registration.Id)
            .ToImmutableArray();
    }

    public static XamlReverseProjectionRuleRegistry Empty { get; } =
        new(
            ImmutableArray<
                XamlReverseProjectionRuleRegistration>.Empty);

    public ImmutableArray<string> RuleIds { get; }

    public int Count => _registrations.Length;

    public static XamlReverseProjectionRuleRegistry Create(
        params IXamlReverseProjectionRule[] rules) =>
        Create((IEnumerable<IXamlReverseProjectionRule>)rules);

    public static XamlReverseProjectionRuleRegistry Create(
        IEnumerable<IXamlReverseProjectionRule> rules)
    {
        if (rules == null)
            throw new ArgumentNullException(nameof(rules));

        var registrations = new List<
            XamlReverseProjectionRuleRegistration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule == null)
            {
                throw new ArgumentException(
                    "Reverse-projection rules cannot contain null.",
                    nameof(rules));
            }

            var id = rule.Id;
            var contractVersion = rule.ContractVersion;
            var priority = rule.Priority;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    "A reverse-projection rule ID cannot be empty.",
                    nameof(rules));
            }
            if (contractVersion !=
                XamlReverseProjectionRuleContract.CurrentVersion)
            {
                throw new ArgumentException(
                    $"Reverse-projection rule '{id}' uses unsupported contract version {contractVersion}.",
                    nameof(rules));
            }
            if (!ids.Add(id))
            {
                throw new ArgumentException(
                    $"Duplicate reverse-projection rule ID '{id}'.",
                    nameof(rules));
            }

            registrations.Add(
                new XamlReverseProjectionRuleRegistration(
                    id,
                    priority,
                    rule));
        }

        return registrations.Count == 0
            ? Empty
            : new XamlReverseProjectionRuleRegistry(
                registrations
                    .OrderByDescending(
                        static registration =>
                            registration.Priority)
                    .ThenBy(
                        static registration =>
                            registration.Id,
                        StringComparer.Ordinal)
                    .ToImmutableArray());
    }

    internal ImmutableArray<
        XamlReverseProjectionRuleRegistration> Registrations =>
        _registrations;
}

internal sealed class XamlReverseProjectionRuleRegistration
{
    public XamlReverseProjectionRuleRegistration(
        string id,
        int priority,
        IXamlReverseProjectionRule rule)
    {
        Id = id;
        Priority = priority;
        Rule = rule;
    }

    public string Id { get; }

    public int Priority { get; }

    public IXamlReverseProjectionRule Rule { get; }
}
