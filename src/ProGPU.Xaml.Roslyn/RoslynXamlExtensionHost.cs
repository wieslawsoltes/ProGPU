using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Diagnostics;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Lowering;
using ProGPU.Xaml.Schema;

namespace ProGPU.Xaml.Roslyn;

/// <summary>
/// Immutable deterministic host for Roslyn-side compiler extensions. Metadata is snapshotted
/// at registration; handlers are called only for their declared capabilities.
/// </summary>
public sealed class RoslynXamlExtensionHost
{
    public const int DefaultMaximumTransformedBoundNodes = 4 * 1024 * 1024;
    public const int DefaultMaximumTransformedIrNodes = 4 * 1024 * 1024;

    public const int CurrentContractVersion = 1;

    private readonly ImmutableArray<IRoslynXamlExtension> _extensions;
    private readonly int _maximumTransformedBoundNodes;
    private readonly int _maximumTransformedIrNodes;

    private RoslynXamlExtensionHost(
        ImmutableArray<IRoslynXamlExtension> extensions,
        int maximumTransformedBoundNodes,
        int maximumTransformedIrNodes)
    {
        _extensions = extensions;
        _maximumTransformedBoundNodes = maximumTransformedBoundNodes;
        _maximumTransformedIrNodes = maximumTransformedIrNodes;
    }

    public static RoslynXamlExtensionHost Empty { get; } =
        Create(Array.Empty<IRoslynXamlExtension>());

    public int ContractVersion => CurrentContractVersion;
    public ImmutableArray<IRoslynXamlExtension> Extensions => _extensions;

    public static RoslynXamlExtensionHost Create(
        IEnumerable<IRoslynXamlExtension> extensions) =>
        Create(extensions, options: null);

    public static RoslynXamlExtensionHost Create(
        IEnumerable<IRoslynXamlExtension> extensions,
        RoslynXamlExtensionHostOptions? options)
    {
        if (extensions == null) throw new ArgumentNullException(nameof(extensions));
        var maximumTransformedBoundNodes =
            options?.MaximumTransformedBoundNodes ??
            DefaultMaximumTransformedBoundNodes;
        if (maximumTransformedBoundNodes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The transformed bound-node limit must be positive.");
        var maximumTransformedIrNodes =
            options?.MaximumTransformedIrNodes ??
            DefaultMaximumTransformedIrNodes;
        if (maximumTransformedIrNodes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The transformed IR node limit must be positive.");
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

        ordered.Sort(CompareExtensions);
        return new RoslynXamlExtensionHost(
            ordered.ToImmutableArray(),
            maximumTransformedBoundNodes,
            maximumTransformedIrNodes);
    }

    /// <summary>
    /// Combines independently validated extension hosts using the same global deterministic
    /// ordering as registration. Duplicate IDs are rejected and the strictest transformed
    /// bound-tree and IR node limits are retained.
    /// </summary>
    public static RoslynXamlExtensionHost Compose(
        RoslynXamlExtensionHost first,
        RoslynXamlExtensionHost second)
    {
        if (first == null) throw new ArgumentNullException(nameof(first));
        if (second == null) throw new ArgumentNullException(nameof(second));
        if (first._extensions.IsEmpty)
            return second._maximumTransformedBoundNodes <=
                   first._maximumTransformedBoundNodes &&
                   second._maximumTransformedIrNodes <= first._maximumTransformedIrNodes
                ? second
                : new RoslynXamlExtensionHost(
                    second._extensions,
                    Math.Min(
                        first._maximumTransformedBoundNodes,
                        second._maximumTransformedBoundNodes),
                    Math.Min(
                        first._maximumTransformedIrNodes,
                        second._maximumTransformedIrNodes));
        if (second._extensions.IsEmpty)
            return first._maximumTransformedBoundNodes <=
                   second._maximumTransformedBoundNodes &&
                   first._maximumTransformedIrNodes <= second._maximumTransformedIrNodes
                ? first
                : new RoslynXamlExtensionHost(
                    first._extensions,
                    Math.Min(
                        first._maximumTransformedBoundNodes,
                        second._maximumTransformedBoundNodes),
                    Math.Min(
                        first._maximumTransformedIrNodes,
                        second._maximumTransformedIrNodes));

        var combined = new List<IRoslynXamlExtension>(
            first._extensions.Length + second._extensions.Length);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddRegistered(first._extensions, combined, ids);
        AddRegistered(second._extensions, combined, ids);
        combined.Sort(CompareExtensions);
        return new RoslynXamlExtensionHost(
            combined.ToImmutableArray(),
            Math.Min(
                first._maximumTransformedBoundNodes,
                second._maximumTransformedBoundNodes),
            Math.Min(
                first._maximumTransformedIrNodes,
                second._maximumTransformedIrNodes));
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

    public XamlBoundDocument ApplyBoundDocumentTransforms(
        RoslynXamlBoundDocumentTransformContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var current = context.Document;
        foreach (var extension in _extensions)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if ((extension.Capabilities &
                 RoslynXamlExtensionCapabilities.BoundDocumentTransform) == 0)
                continue;
            if (extension is not IRoslynXamlBoundDocumentTransformExtension transform)
            {
                current = AppendBoundDiagnostic(
                    current,
                    CreateBoundTransformHostDiagnostic(
                        current,
                        "PGXAML2135",
                        $"Roslyn XAML extension '{extension.Id}' declares bound-document " +
                        "transform capability without implementing its contract."));
                continue;
            }

            RoslynXamlBoundDocumentTransformResult result;
            try
            {
                result = transform.Transform(
                    new RoslynXamlBoundDocumentTransformContext(
                        current,
                        context.TypeSystem,
                        context.FrameworkId,
                        context.ResourceUri,
                        context.Strict,
                        context.CancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                current = AppendBoundDiagnostic(
                    current,
                    CreateBoundTransformHostDiagnostic(
                        current,
                        "PGXAML2135",
                        $"Roslyn XAML extension '{extension.Id}' failed while transforming " +
                        $"the bound document: {exception.Message}"));
                continue;
            }

            string? validationError = null;
            if (result == null ||
                !TryValidateTransformedBoundRoot(
                    current,
                    result.Root,
                    _maximumTransformedBoundNodes,
                    out validationError))
            {
                current = AppendBoundDiagnostic(
                    current,
                    CreateBoundTransformHostDiagnostic(
                        current,
                        "PGXAML2136",
                        $"Roslyn XAML extension '{extension.Id}' returned an invalid bound " +
                        $"document: {validationError ?? "the result was null."}"));
                continue;
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(
                current.Diagnostics.Length + result.Issues.Length);
            diagnostics.AddRange(current.Diagnostics);
            foreach (var issue in result.Issues)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!TryCreateBoundIssueDiagnostic(current, issue, out var diagnostic))
                {
                    diagnostics.Add(CreateBoundTransformHostDiagnostic(
                        current,
                        "PGXAML2136",
                        $"Roslyn XAML extension '{extension.Id}' returned an invalid " +
                        "bound-transform issue."));
                    continue;
                }
                diagnostics.Add(diagnostic!);
            }
            current = new XamlBoundDocument(
                current.Infoset,
                result.Root,
                diagnostics.ToImmutable(),
                current.DictionaryKeyDirectiveAliases,
                current.RootClassType);
        }
        return current;
    }

    public ImmutableArray<Diagnostic> ValidateBoundDocument(
        RoslynXamlBoundDocumentValidationContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var extension in _extensions)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if ((extension.Capabilities &
                 RoslynXamlExtensionCapabilities.BoundDocumentValidation) == 0)
                continue;
            if (extension is not IRoslynXamlBoundDocumentValidatorExtension validator)
            {
                diagnostics.Add(CreateValidationHostDiagnostic(
                    context,
                    "PGXAML2133",
                    $"Roslyn XAML extension '{extension.Id}' declares bound-document " +
                    "validation capability without implementing its contract."));
                continue;
            }

            try
            {
                var issues = validator.Validate(context);
                if (issues == null)
                {
                    diagnostics.Add(CreateValidationHostDiagnostic(
                        context,
                        "PGXAML2134",
                        $"Roslyn XAML extension '{extension.Id}' returned a null validation sequence."));
                    continue;
                }

                foreach (var issue in issues)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (issue == null ||
                        issue.SourceSpan.Start < 0 ||
                        issue.SourceSpan.End > context.Document.Infoset.SourceText.Length)
                    {
                        diagnostics.Add(CreateValidationHostDiagnostic(
                            context,
                            "PGXAML2134",
                            $"Roslyn XAML extension '{extension.Id}' returned an invalid validation issue."));
                        continue;
                    }

                    diagnostics.Add(XamlDiagnostics.Create(
                        issue.Id,
                        issue.Severity,
                        issue.Message,
                        context.Document.Infoset.Path,
                        context.Document.Infoset.SourceText,
                        issue.SourceSpan,
                        issue.SpecificationSection));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add(CreateValidationHostDiagnostic(
                    context,
                    "PGXAML2133",
                    $"Roslyn XAML extension '{extension.Id}' failed during bound-document " +
                    $"validation: {exception.Message}"));
            }
        }
        return diagnostics.ToImmutable();
    }

    public XamlConstructionProgram ApplyConstructionProgramTransforms(
        RoslynXamlConstructionTransformContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var current = context.Program;
        foreach (var extension in _extensions)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if ((extension.Capabilities &
                 RoslynXamlExtensionCapabilities.ConstructionProgramTransform) == 0)
                continue;
            if (extension is not IRoslynXamlConstructionProgramTransformExtension transform)
            {
                current = AppendProgramDiagnostic(
                    current,
                    CreateProgramHostDiagnostic(
                        current,
                        "PGXAML3052",
                        $"Roslyn XAML extension '{extension.Id}' declares construction-program " +
                        "transform capability without implementing its contract."));
                continue;
            }

            RoslynXamlConstructionTransformResult result;
            try
            {
                result = transform.Transform(
                    new RoslynXamlConstructionTransformContext(
                        current,
                        context.FrameworkId,
                        context.ResourceUri,
                        context.Strict,
                        context.CancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                current = AppendProgramDiagnostic(
                    current,
                    CreateProgramHostDiagnostic(
                        current,
                        "PGXAML3052",
                        $"Roslyn XAML extension '{extension.Id}' failed while transforming " +
                        $"construction IR: {exception.Message}"));
                continue;
            }

            string? validationError = null;
            if (result == null ||
                !TryValidateTransformedRoot(
                    current,
                    result.Root,
                    _maximumTransformedIrNodes,
                    out validationError))
            {
                current = AppendProgramDiagnostic(
                    current,
                    CreateProgramHostDiagnostic(
                        current,
                        "PGXAML3053",
                        $"Roslyn XAML extension '{extension.Id}' returned invalid construction IR: " +
                        (validationError ?? "the result was null.")));
                continue;
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(
                current.Diagnostics.Length + result.Issues.Length);
            diagnostics.AddRange(current.Diagnostics);
            foreach (var issue in result.Issues)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!TryCreateIssueDiagnostic(current, issue, out var diagnostic))
                {
                    diagnostics.Add(CreateProgramHostDiagnostic(
                        current,
                        "PGXAML3053",
                        $"Roslyn XAML extension '{extension.Id}' returned an invalid " +
                        "construction-transform issue."));
                    continue;
                }
                diagnostics.Add(diagnostic!);
            }
            current = new XamlConstructionProgram(
                current.BoundDocument,
                result.Root,
                diagnostics.ToImmutable(),
                current.ResourceGraph);
        }
        return current;
    }

    private static RoslynXamlExtensionResolution Error(string id, string message) =>
        new RoslynXamlExtensionResolution(
            RoslynXamlExtensionResolutionKind.Error,
            null,
            ImmutableArray.Create(id),
            message);

    private static Diagnostic CreateValidationHostDiagnostic(
        RoslynXamlBoundDocumentValidationContext context,
        string id,
        string message)
    {
        var span = context.Document.Root?.SourceSpan ?? default;
        return XamlDiagnostics.Create(
            id,
            DiagnosticSeverity.Error,
            message,
            context.Document.Infoset.Path,
            context.Document.Infoset.SourceText,
            span,
            "EXT-004");
    }

    private static bool TryValidateTransformedRoot(
        XamlConstructionProgram program,
        XamlIrObject? candidate,
        int maximumTransformedIrNodes,
        out string? error)
    {
        var original = program.Root;
        if (original == null || candidate == null)
        {
            error = original == null && candidate == null
                ? null
                : "the transform changed whether the compilation unit has a root.";
            return error == null;
        }
        if (candidate.StableId != original.StableId ||
            candidate.SourceSpan != original.SourceSpan ||
            candidate.Kind != original.Kind ||
            !ReferenceEquals(candidate.Type, original.Type) ||
            !Equals(candidate.Condition, original.Condition))
        {
            error = "the compilation-unit root identity, span, kind, type, or condition changed.";
            return false;
        }

        var sourceLength = program.BoundDocument.Infoset.SourceText.Length;
        var stack = new List<XamlIrValue> { candidate };
        var visited = new HashSet<XamlIrValue>();
        var count = 0;
        while (stack.Count != 0)
        {
            var last = stack.Count - 1;
            var value = stack[last];
            stack.RemoveAt(last);
            if (!visited.Add(value)) continue;
            count++;
            if (count > maximumTransformedIrNodes)
            {
                error = $"the transformed IR exceeds {maximumTransformedIrNodes} nodes.";
                return false;
            }
            if (!IsInSource(value.SourceSpan, sourceLength))
            {
                error = "a transformed IR value has an out-of-document source span.";
                return false;
            }

            if (value is XamlIrObject objectValue)
            {
                if (objectValue.Operations.IsDefault ||
                    !IsValidCondition(objectValue.Condition))
                {
                    error = "a transformed IR object has invalid operations or condition data.";
                    return false;
                }
                foreach (var operation in objectValue.Operations)
                {
                    if (operation == null ||
                        operation.Values.IsDefault ||
                        !IsValidCondition(operation.Condition) ||
                        !IsInSource(operation.SourceSpan, sourceLength))
                    {
                        error = "a transformed IR operation is null, incomplete, or out of source.";
                        return false;
                    }
                    foreach (var child in operation.Values)
                    {
                        if (child == null)
                        {
                            error = "a transformed IR operation contains a null value.";
                            return false;
                        }
                        stack.Add(child);
                    }
                }
            }
            else if (value is XamlIrBinding binding)
            {
                stack.Add(binding.Extension);
            }
            else if (value is XamlIrCompiledBinding compiledBinding)
            {
                stack.Add(compiledBinding.Extension);
            }
        }
        error = null;
        return true;
    }

    private static bool TryValidateTransformedBoundRoot(
        XamlBoundDocument document,
        XamlBoundObject? candidate,
        int maximumTransformedBoundNodes,
        out string? error)
    {
        var original = document.Root;
        if (original == null || candidate == null)
        {
            error = original == null && candidate == null
                ? null
                : "the transform changed whether the document has a root.";
            return error == null;
        }
        if (candidate.StableId != original.StableId ||
            candidate.SourceSpan != original.SourceSpan ||
            !ReferenceEquals(candidate.Type, original.Type) ||
            candidate.IsRetrieved != original.IsRetrieved ||
            candidate.IsMarkupExtension != original.IsMarkupExtension ||
            !Equals(candidate.Condition, original.Condition))
        {
            error = "the document root identity, span, type, construction role, or condition changed.";
            return false;
        }

        var sourceLength = document.Infoset.SourceText.Length;
        var stack = new List<XamlBoundValue> { candidate };
        var visited = new HashSet<XamlBoundValue>();
        var count = 0;
        while (stack.Count != 0)
        {
            var last = stack.Count - 1;
            var value = stack[last];
            stack.RemoveAt(last);
            if (!visited.Add(value)) continue;
            count++;
            if (count > maximumTransformedBoundNodes)
            {
                error =
                    $"the transformed bound document exceeds {maximumTransformedBoundNodes} nodes.";
                return false;
            }
            if (!IsInSource(value.SourceSpan, sourceLength))
            {
                error = "a transformed bound value has an out-of-document source span.";
                return false;
            }

            if (value is XamlBoundObject objectValue)
            {
                if (objectValue.Members.IsDefault ||
                    !IsValidCondition(objectValue.Condition))
                {
                    error = "a transformed bound object has invalid members or condition data.";
                    return false;
                }
                foreach (var member in objectValue.Members)
                {
                    count++;
                    if (count > maximumTransformedBoundNodes)
                    {
                        error =
                            $"the transformed bound document exceeds {maximumTransformedBoundNodes} nodes.";
                        return false;
                    }
                    if (member == null ||
                        member.Member == null ||
                        member.Values.IsDefault ||
                        !IsValidCondition(member.Condition) ||
                        !IsInSource(member.SourceSpan, sourceLength))
                    {
                        error =
                            "a transformed bound member is null, incomplete, or out of source.";
                        return false;
                    }
                    foreach (var child in member.Values)
                    {
                        if (child == null)
                        {
                            error = "a transformed bound member contains a null value.";
                            return false;
                        }
                        stack.Add(child);
                    }
                }
            }
            else if (value is XamlBoundBinding binding)
            {
                stack.Add(binding.Extension);
            }
            else if (value is XamlBoundCompiledBinding compiledBinding)
            {
                stack.Add(compiledBinding.Extension);
            }
        }
        error = null;
        return true;
    }

    private static bool IsValidCondition(XamlNamespaceCondition? condition)
    {
        if (condition == null) return true;
        return XamlNamespaceCondition.TryParse(
                condition.OriginalNamespaceUri,
                out var reparsed) &&
            string.Equals(
                reparsed!.BaseNamespaceUri,
                condition.BaseNamespaceUri,
                StringComparison.Ordinal) &&
            string.Equals(
                reparsed.Method,
                condition.Method,
                StringComparison.Ordinal) &&
            reparsed.Arguments.SequenceEqual(condition.Arguments);
    }

    private static bool TryCreateIssueDiagnostic(
        XamlConstructionProgram program,
        RoslynXamlValidationIssue? issue,
        out Diagnostic? diagnostic)
    {
        diagnostic = null;
        if (issue == null ||
            !IsInSource(
                issue.SourceSpan,
                program.BoundDocument.Infoset.SourceText.Length))
            return false;
        diagnostic = XamlDiagnostics.Create(
            issue.Id,
            issue.Severity,
            issue.Message,
            program.BoundDocument.Infoset.Path,
            program.BoundDocument.Infoset.SourceText,
            issue.SourceSpan,
            issue.SpecificationSection);
        return true;
    }

    private static bool TryCreateBoundIssueDiagnostic(
        XamlBoundDocument document,
        RoslynXamlValidationIssue? issue,
        out Diagnostic? diagnostic)
    {
        diagnostic = null;
        if (issue == null ||
            !IsInSource(issue.SourceSpan, document.Infoset.SourceText.Length))
            return false;
        diagnostic = XamlDiagnostics.Create(
            issue.Id,
            issue.Severity,
            issue.Message,
            document.Infoset.Path,
            document.Infoset.SourceText,
            issue.SourceSpan,
            issue.SpecificationSection);
        return true;
    }

    private static Diagnostic CreateBoundTransformHostDiagnostic(
        XamlBoundDocument document,
        string id,
        string message) =>
        XamlDiagnostics.Create(
            id,
            DiagnosticSeverity.Error,
            message,
            document.Infoset.Path,
            document.Infoset.SourceText,
            document.Root?.SourceSpan ?? default,
            "EXT-004");

    private static XamlBoundDocument AppendBoundDiagnostic(
        XamlBoundDocument document,
        Diagnostic diagnostic)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(
            document.Diagnostics.Length + 1);
        diagnostics.AddRange(document.Diagnostics);
        diagnostics.Add(diagnostic);
        return new XamlBoundDocument(
            document.Infoset,
            document.Root,
            diagnostics.ToImmutable(),
            document.DictionaryKeyDirectiveAliases,
            document.RootClassType);
    }

    private static bool IsInSource(Microsoft.CodeAnalysis.Text.TextSpan span, int sourceLength) =>
        span.Start >= 0 && span.End <= sourceLength;

    private static Diagnostic CreateProgramHostDiagnostic(
        XamlConstructionProgram program,
        string id,
        string message) =>
        XamlDiagnostics.Create(
            id,
            DiagnosticSeverity.Error,
            message,
            program.BoundDocument.Infoset.Path,
            program.BoundDocument.Infoset.SourceText,
            program.Root?.SourceSpan ?? default,
            "EXT-004");

    private static XamlConstructionProgram AppendProgramDiagnostic(
        XamlConstructionProgram program,
        Diagnostic diagnostic)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(
            program.Diagnostics.Length + 1);
        diagnostics.AddRange(program.Diagnostics);
        diagnostics.Add(diagnostic);
        return new XamlConstructionProgram(
            program.BoundDocument,
            program.Root,
            diagnostics.ToImmutable(),
            program.ResourceGraph);
    }

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
             ~(RoslynXamlExtensionCapabilities.MarkupExtensionExpression |
               RoslynXamlExtensionCapabilities.BoundDocumentValidation |
               RoslynXamlExtensionCapabilities.ConstructionProgramTransform |
               RoslynXamlExtensionCapabilities.BoundDocumentTransform)) != 0)
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
        if ((extension.Capabilities &
             RoslynXamlExtensionCapabilities.BoundDocumentValidation) != 0 &&
            extension is not IRoslynXamlBoundDocumentValidatorExtension)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' declares bound-document validation " +
                "capability without implementing its contract.",
                nameof(extension));
        if ((extension.Capabilities &
             RoslynXamlExtensionCapabilities.ConstructionProgramTransform) != 0 &&
            extension is not IRoslynXamlConstructionProgramTransformExtension)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' declares construction-program " +
                "transform capability without implementing its contract.",
                nameof(extension));
        if ((extension.Capabilities &
             RoslynXamlExtensionCapabilities.BoundDocumentTransform) != 0 &&
            extension is not IRoslynXamlBoundDocumentTransformExtension)
            throw new ArgumentException(
                $"Roslyn XAML extension '{extension.Id}' declares bound-document transform " +
                "capability without implementing its contract.",
                nameof(extension));
    }

    private static void AddRegistered(
        ImmutableArray<IRoslynXamlExtension> source,
        List<IRoslynXamlExtension> destination,
        HashSet<string> ids)
    {
        foreach (var extension in source)
        {
            if (!ids.Add(extension.Id))
                throw new ArgumentException(
                    $"Roslyn XAML extension ID '{extension.Id}' is contributed by more than one host.");
            destination.Add(extension);
        }
    }

    private static int CompareExtensions(
        IRoslynXamlExtension left,
        IRoslynXamlExtension right)
    {
        var priority = right.Priority.CompareTo(left.Priority);
        if (priority != 0) return priority;
        var id = StringComparer.Ordinal.Compare(left.Id, right.Id);
        return id != 0 ? id : right.Version.CompareTo(left.Version);
    }

    private sealed class RegisteredExtension :
        IRoslynXamlMarkupExtensionExpressionExtension,
        IRoslynXamlBoundDocumentValidatorExtension,
        IRoslynXamlConstructionProgramTransformExtension,
        IRoslynXamlBoundDocumentTransformExtension
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

        public IEnumerable<RoslynXamlValidationIssue> Validate(
            RoslynXamlBoundDocumentValidationContext context) =>
            ((IRoslynXamlBoundDocumentValidatorExtension)_extension)
                .Validate(context);

        public RoslynXamlConstructionTransformResult Transform(
            RoslynXamlConstructionTransformContext context) =>
            ((IRoslynXamlConstructionProgramTransformExtension)_extension)
                .Transform(context);

        public RoslynXamlBoundDocumentTransformResult Transform(
            RoslynXamlBoundDocumentTransformContext context) =>
            ((IRoslynXamlBoundDocumentTransformExtension)_extension)
                .Transform(context);
    }
}
