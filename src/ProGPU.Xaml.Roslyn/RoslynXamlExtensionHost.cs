using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProGPU.Xaml.Lowering;
using ProGPU.Xaml.Schema;

namespace ProGPU.Xaml.Roslyn;

[Flags]
public enum RoslynXamlExtensionCapabilities
{
    None = 0,
    MarkupExtensionExpression = 1 << 0
}

public enum RoslynXamlExtensionConflictPolicy
{
    Diagnose,
    CoalesceEquivalent
}

public interface IRoslynXamlExtension
{
    string Id { get; }
    int ContractVersion { get; }
    int Version { get; }
    int Priority { get; }
    RoslynXamlExtensionCapabilities Capabilities { get; }
    RoslynXamlExtensionConflictPolicy ConflictPolicy { get; }
}

/// <summary>
/// Optional user/framework-package lowering seam for a canonical markup-extension IR value.
/// Implementations must construct C# exclusively as Roslyn syntax nodes.
/// </summary>
public interface IRoslynXamlMarkupExtensionExpressionExtension : IRoslynXamlExtension
{
    bool TryCreateExpression(
        RoslynXamlMarkupExtensionExpressionContext context,
        out ExpressionSyntax expression);
}

public sealed class RoslynXamlMarkupExtensionExpressionContext
{
    public RoslynXamlMarkupExtensionExpressionContext(
        XamlIrObject extension,
        XamlTypeInfo targetType,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax? targetObject,
        XamlMemberInfo? targetMember,
        string? resourceUri,
        CancellationToken cancellationToken = default)
    {
        Extension = extension ?? throw new ArgumentNullException(nameof(extension));
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        LookupRoot = lookupRoot ?? throw new ArgumentNullException(nameof(lookupRoot));
        TargetObject = targetObject;
        TargetMember = targetMember;
        ResourceUri = resourceUri;
        CancellationToken = cancellationToken;
    }

    public XamlIrObject Extension { get; }
    public XamlTypeInfo TargetType { get; }
    public ExpressionSyntax LookupRoot { get; }
    public ExpressionSyntax? TargetObject { get; }
    public XamlMemberInfo? TargetMember { get; }
    public string? ResourceUri { get; }
    public CancellationToken CancellationToken { get; }
}

public enum RoslynXamlExtensionResolutionKind
{
    NotHandled,
    Handled,
    Conflict,
    Error
}

public sealed class RoslynXamlExtensionResolution
{
    internal RoslynXamlExtensionResolution(
        RoslynXamlExtensionResolutionKind kind,
        ExpressionSyntax? expression,
        ImmutableArray<string> pluginIds,
        string? message)
    {
        Kind = kind;
        Expression = expression;
        PluginIds = pluginIds;
        Message = message;
    }

    public RoslynXamlExtensionResolutionKind Kind { get; }
    public ExpressionSyntax? Expression { get; }
    public ImmutableArray<string> PluginIds { get; }
    public string? Message { get; }
}

/// <summary>
/// Immutable deterministic host for Roslyn-side compiler extensions. Metadata is snapshotted
/// at registration; handlers are called only for their declared capabilities.
/// </summary>
public sealed class RoslynXamlExtensionHost
{
    public const int CurrentContractVersion = 1;

    private readonly ImmutableArray<IRoslynXamlExtension> _extensions;

    private RoslynXamlExtensionHost(ImmutableArray<IRoslynXamlExtension> extensions)
    {
        _extensions = extensions;
    }

    public static RoslynXamlExtensionHost Empty { get; } =
        Create(Array.Empty<IRoslynXamlExtension>());

    public int ContractVersion => CurrentContractVersion;
    public ImmutableArray<IRoslynXamlExtension> Extensions => _extensions;

    public static RoslynXamlExtensionHost Create(
        IEnumerable<IRoslynXamlExtension> extensions)
    {
        if (extensions == null) throw new ArgumentNullException(nameof(extensions));
        var ordered = new List<IRoslynXamlExtension>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            if (extension == null)
                throw new ArgumentException("A Roslyn XAML extension cannot be null.", nameof(extensions));
            Validate(extension);
            if (!ids.Add(extension.Id))
                throw new ArgumentException(
                    $"Roslyn XAML extension ID '{extension.Id}' is registered more than once.",
                    nameof(extensions));
            ordered.Add(new RegisteredExtension(extension));
        }

        ordered.Sort(static (left, right) =>
        {
            var priority = right.Priority.CompareTo(left.Priority);
            if (priority != 0) return priority;
            var id = StringComparer.Ordinal.Compare(left.Id, right.Id);
            return id != 0 ? id : right.Version.CompareTo(left.Version);
        });
        return new RoslynXamlExtensionHost(ordered.ToImmutableArray());
    }

    public RoslynXamlExtensionResolution ResolveMarkupExtensionExpression(
        RoslynXamlMarkupExtensionExpressionContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        IRoslynXamlExtension? winner = null;
        ExpressionSyntax? winningExpression = null;
        var matchingIds = ImmutableArray.CreateBuilder<string>();

        foreach (var extension in _extensions)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (winner != null && extension.Priority < winner.Priority)
                break;
            if ((extension.Capabilities &
                 RoslynXamlExtensionCapabilities.MarkupExtensionExpression) == 0)
                continue;
            if (extension is not IRoslynXamlMarkupExtensionExpressionExtension expressionExtension)
                return Error(
                    extension.Id,
                    $"Roslyn XAML extension '{extension.Id}' declares markup-expression " +
                    "capability without implementing its contract.");

            ExpressionSyntax expression;
            bool handled;
            try
            {
                handled = expressionExtension.TryCreateExpression(context, out expression!);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Error(
                    extension.Id,
                    $"Roslyn XAML extension '{extension.Id}' failed: {exception.Message}");
            }

            if (!handled) continue;
            if (expression == null)
                return Error(
                    extension.Id,
                    $"Roslyn XAML extension '{extension.Id}' returned a null expression.");

            matchingIds.Add(extension.Id);
            if (winner == null)
            {
                winner = extension;
                winningExpression = expression;
                continue;
            }

            var canCoalesce =
                winner.ConflictPolicy == RoslynXamlExtensionConflictPolicy.CoalesceEquivalent &&
                extension.ConflictPolicy == RoslynXamlExtensionConflictPolicy.CoalesceEquivalent &&
                winningExpression!.IsEquivalentTo(expression);
            if (!canCoalesce)
            {
                return new RoslynXamlExtensionResolution(
                    RoslynXamlExtensionResolutionKind.Conflict,
                    null,
                    matchingIds.ToImmutable(),
                    $"Roslyn XAML extensions '{winner.Id}' and '{extension.Id}' both handled " +
                    $"the markup expression at priority {winner.Priority}.");
            }
        }

        return winner == null
            ? new RoslynXamlExtensionResolution(
                RoslynXamlExtensionResolutionKind.NotHandled,
                null,
                ImmutableArray<string>.Empty,
                null)
            : new RoslynXamlExtensionResolution(
                RoslynXamlExtensionResolutionKind.Handled,
                winningExpression,
                matchingIds.ToImmutable(),
                null);
    }

    private static RoslynXamlExtensionResolution Error(string id, string message) =>
        new RoslynXamlExtensionResolution(
            RoslynXamlExtensionResolutionKind.Error,
            null,
            ImmutableArray.Create(id),
            message);

    private static void Validate(IRoslynXamlExtension extension)
    {
        if (string.IsNullOrWhiteSpace(extension.Id))
            throw new ArgumentException("A Roslyn XAML extension requires a non-empty ID.", nameof(extension));
        if (extension.ContractVersion != CurrentContractVersion)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' requires contract version " +
                $"{extension.ContractVersion}; this host supports {CurrentContractVersion}.",
                nameof(extension));
        if (extension.Version <= 0)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' requires a positive implementation version.",
                nameof(extension));
        if (extension.Capabilities == RoslynXamlExtensionCapabilities.None)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' does not declare a capability.",
                nameof(extension));
        if ((extension.Capabilities &
             ~RoslynXamlExtensionCapabilities.MarkupExtensionExpression) != 0)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' declares unsupported capabilities.",
                nameof(extension));
        if ((extension.Capabilities &
             RoslynXamlExtensionCapabilities.MarkupExtensionExpression) != 0 &&
            extension is not IRoslynXamlMarkupExtensionExpressionExtension)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' declares markup-expression " +
                "capability without implementing its contract.",
                nameof(extension));
    }

    private sealed class RegisteredExtension :
        IRoslynXamlMarkupExtensionExpressionExtension
    {
        private readonly IRoslynXamlExtension _extension;

        public RegisteredExtension(IRoslynXamlExtension extension)
        {
            _extension = extension;
            Id = extension.Id;
            ContractVersion = extension.ContractVersion;
            Version = extension.Version;
            Priority = extension.Priority;
            Capabilities = extension.Capabilities;
            ConflictPolicy = extension.ConflictPolicy;
        }

        public string Id { get; }
        public int ContractVersion { get; }
        public int Version { get; }
        public int Priority { get; }
        public RoslynXamlExtensionCapabilities Capabilities { get; }
        public RoslynXamlExtensionConflictPolicy ConflictPolicy { get; }

        public bool TryCreateExpression(
            RoslynXamlMarkupExtensionExpressionContext context,
            out ExpressionSyntax expression)
            => ((IRoslynXamlMarkupExtensionExpressionExtension)_extension)
                .TryCreateExpression(context, out expression!);
    }
}
