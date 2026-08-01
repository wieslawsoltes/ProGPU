using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Syntax;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlReverseProjectionRegistryTests
{
    [Fact]
    public void RegistrySnapshotsAndRunsRulesDeterministically()
    {
        var calls = new List<string>();
        XamlReverseProjectionContext? observedContext = null;
        var low = new CallbackReverseProjectionRule(
            "z-low",
            priority: 10,
            context =>
            {
                calls.Add("low");
                observedContext = context;
                var attribute = Assert.Single(
                    context.SourceTree
                        .GetRoot()!
                        .Attributes);
                return new XamlReverseProjectionRuleResult(
                    new[]
                    {
                        new TextChange(
                            attribute.ValueSpan,
                            "changed")
                    });
            });
        var high = new CallbackReverseProjectionRule(
            "a-high",
            priority: 20,
            _ =>
            {
                calls.Add("high");
                return new XamlReverseProjectionRuleResult();
            });
        var highPeer = new CallbackReverseProjectionRule(
            "b-high",
            priority: 20,
            _ =>
            {
                calls.Add("high-peer");
                return new XamlReverseProjectionRuleResult();
            });
        IXamlReverseProjectionRule[] source =
        [
            low,
            high,
            highPeer
        ];
        var registry =
            XamlReverseProjectionRuleRegistry.Create(source);

        source[0] = new CallbackReverseProjectionRule(
            "replacement",
            priority: 100,
            _ => new XamlReverseProjectionRuleResult());
        high.Id = "mutated";
        high.Priority = -100;

        var fixture = CreateFixture("<Page Value=\"original\" />");
        var result = new XamlReverseProjectionService(
                registry)
            .ApplyEdits(
                fixture.XamlTree,
                fixture.OriginalModel,
                fixture.ChangedModel);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { "a-high", "b-high", "z-low" },
            registry.RuleIds);
        Assert.Equal(
            new[] { "high", "high-peer", "low" },
            calls);
        Assert.Equal(
            "<Page Value=\"changed\" />",
            result.GetChangedText().ToString());
        Assert.Same(
            fixture.XamlTree,
            observedContext!.SourceTree);
        Assert.Null(observedContext.BoundDocument);
        Assert.Same(
            fixture.OriginalModel,
            observedContext.OriginalGeneratedModel);
        Assert.Same(
            fixture.ChangedModel,
            observedContext.ChangedGeneratedModel);
    }

    [Fact]
    public void RegistryRejectsInvalidRegistrations()
    {
        Assert.Same(
            XamlReverseProjectionRuleRegistry.Empty,
            XamlReverseProjectionRuleRegistry.Create(
                Array.Empty<IXamlReverseProjectionRule>()));
        Assert.Throws<ArgumentException>(() =>
            XamlReverseProjectionRuleRegistry.Create(
                new IXamlReverseProjectionRule[] { null! }));
        Assert.Throws<ArgumentException>(() =>
            XamlReverseProjectionRuleRegistry.Create(
                new CallbackReverseProjectionRule(
                    " ",
                    priority: 0,
                    _ =>
                        new XamlReverseProjectionRuleResult())));
        Assert.Throws<ArgumentException>(() =>
            new XamlReverseProjectionRuleResult(
                conflicts:
                [
                    null!
                ]));
        Assert.Throws<ArgumentException>(() =>
            XamlReverseProjectionRuleRegistry.Create(
                new CallbackReverseProjectionRule(
                    "wrong-version",
                    priority: 0,
                    _ =>
                        new XamlReverseProjectionRuleResult(),
                    contractVersion: 2)));
        Assert.Throws<ArgumentException>(() =>
            XamlReverseProjectionRuleRegistry.Create(
                new CallbackReverseProjectionRule(
                    "duplicate",
                    priority: 0,
                    _ =>
                        new XamlReverseProjectionRuleResult()),
                new CallbackReverseProjectionRule(
                    "duplicate",
                    priority: 1,
                    _ =>
                        new XamlReverseProjectionRuleResult())));
    }

    [Fact]
    public void ExternalRuleFailuresRollBackTheTransaction()
    {
        var fixture = CreateFixture("<Page Value=\"original\" />");
        var attribute = Assert.Single(
            fixture.XamlTree.GetRoot()!.Attributes);
        var valid = new CallbackReverseProjectionRule(
            "valid",
            priority: 0,
            _ => new XamlReverseProjectionRuleResult(
                new[]
                {
                    new TextChange(
                        attribute.ValueSpan,
                        "changed")
                }));
        var throwing = new CallbackReverseProjectionRule(
            "throwing",
            priority: 10,
            _ => throw new InvalidOperationException(
                "host-specific detail"));
        var failed = new XamlReverseProjectionService(
                XamlReverseProjectionRuleRegistry.Create(
                    valid,
                    throwing))
            .ApplyEdits(
                fixture.XamlTree,
                fixture.OriginalModel,
                fixture.ChangedModel);

        Assert.False(failed.Succeeded);
        Assert.Empty(failed.Changes);
        Assert.Equal(
            fixture.XamlTree.GetText().ToString(),
            failed.GetChangedText().ToString());
        var failure = Assert.Single(
            failed.Conflicts,
            conflict => conflict.Kind ==
                XamlReverseProjectionConflictKind
                    .ExternalRuleFailed);
        Assert.Contains(
            "'throwing'",
            failure.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "host-specific detail",
            failure.Message,
            StringComparison.Ordinal);

        var nullResult = new CallbackReverseProjectionRule(
            "null-result",
            priority: 0,
            _ => null!);
        var rejectedNull = new XamlReverseProjectionService(
                XamlReverseProjectionRuleRegistry.Create(
                    nullResult))
            .ApplyEdits(
                fixture.XamlTree,
                fixture.OriginalModel,
                fixture.ChangedModel);
        Assert.False(rejectedNull.Succeeded);
        Assert.Empty(rejectedNull.Changes);
        Assert.Contains(
            rejectedNull.Conflicts,
            conflict => conflict.Kind ==
                XamlReverseProjectionConflictKind
                    .InvalidRuleResult);

        var invalid = new CallbackReverseProjectionRule(
            "invalid",
            priority: 0,
            _ => new XamlReverseProjectionRuleResult(
                new[]
                {
                    new TextChange(
                        new TextSpan(
                            fixture.XamlTree
                                .GetText().Length + 1,
                            0),
                        "invalid")
                }));
        var invalidResult = new XamlReverseProjectionService(
                XamlReverseProjectionRuleRegistry.Create(
                    invalid))
            .ApplyEdits(
                fixture.XamlTree,
                fixture.OriginalModel,
                fixture.ChangedModel);

        Assert.False(invalidResult.Succeeded);
        Assert.Empty(invalidResult.Changes);
        Assert.Contains(
            invalidResult.Conflicts,
            conflict => conflict.Kind ==
                XamlReverseProjectionConflictKind
                    .InvalidRuleResult);
    }

    [Fact]
    public void ExternalRuleOverlapsAreRejected()
    {
        var fixture = CreateFixture("<Page Value=\"original\" />");
        var span = Assert.Single(
            fixture.XamlTree.GetRoot()!.Attributes).ValueSpan;
        var registry =
            XamlReverseProjectionRuleRegistry.Create(
                new CallbackReverseProjectionRule(
                    "first",
                    priority: 1,
                    _ => new XamlReverseProjectionRuleResult(
                        new[]
                        {
                            new TextChange(span, "first")
                        })),
                new CallbackReverseProjectionRule(
                    "second",
                    priority: 0,
                    _ => new XamlReverseProjectionRuleResult(
                        new[]
                        {
                            new TextChange(span, "second")
                        })));

        var result = new XamlReverseProjectionService(
                registry)
            .ApplyEdits(
                fixture.XamlTree,
                fixture.OriginalModel,
                fixture.ChangedModel);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Changes);
        Assert.Contains(
            result.Conflicts,
            conflict => conflict.Kind ==
                XamlReverseProjectionConflictKind
                    .OverlappingEdit);
        Assert.Equal(
            fixture.XamlTree.GetText().ToString(),
            result.GetChangedText().ToString());
    }

    private static ReverseProjectionFixture CreateFixture(
        string xaml)
    {
        var xamlTree = XamlParser.Parse(
            SourceText.From(xaml),
            "Rule.xaml");
        var originalTree = CSharpSyntaxTree.ParseText(
            "internal sealed class Projected { int Value = 1; }");
        var changedTree = CSharpSyntaxTree.ParseText(
            "internal sealed class Projected { int Value = 2; }");
        var originalCompilation = CSharpCompilation.Create(
            "Original",
            new[] { originalTree },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        var changedCompilation = CSharpCompilation.Create(
            "Changed",
            new[] { changedTree },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        return new ReverseProjectionFixture(
            xamlTree,
            originalCompilation.GetSemanticModel(
                originalTree),
            changedCompilation.GetSemanticModel(
                changedTree));
    }

    private sealed record ReverseProjectionFixture(
        XamlSyntaxTree XamlTree,
        SemanticModel OriginalModel,
        SemanticModel ChangedModel);
}

internal sealed class CallbackReverseProjectionRule :
    IXamlReverseProjectionRule
{
    private readonly Func<
        XamlReverseProjectionContext,
        XamlReverseProjectionRuleResult> _apply;

    public CallbackReverseProjectionRule(
        string id,
        int priority,
        Func<
            XamlReverseProjectionContext,
            XamlReverseProjectionRuleResult> apply,
        int contractVersion =
            XamlReverseProjectionRuleContract.CurrentVersion)
    {
        Id = id;
        Priority = priority;
        ContractVersion = contractVersion;
        _apply = apply;
    }

    public string Id { get; set; }

    public int ContractVersion { get; }

    public int Priority { get; set; }

    public XamlReverseProjectionRuleResult Apply(
        XamlReverseProjectionContext context) =>
        _apply(context);
}
