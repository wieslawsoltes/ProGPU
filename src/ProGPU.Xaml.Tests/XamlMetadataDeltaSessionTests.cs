using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Workspaces;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlMetadataDeltaSessionTests
{
    private static readonly CSharpCompilationOptions Options =
        new(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            deterministic: true);

    private static readonly ImmutableArray<MetadataReference> References =
        ImmutableArray.Create<MetadataReference>(
            MetadataReference.CreateFromFile(
                typeof(object).Assembly.Location));

    [Fact]
    public void MethodBodyDeltasCommitAcrossGenerations()
    {
        CSharpCompilation initial = CreateCompilation(
            "public static class Target { " +
            "public static int Value() => 1; }");
        using var session =
            new RoslynXamlMetadataEditSession(initial);

        Assert.Equal(
            RoslynXamlMetadataEditCapabilities.UpdateMethodBody |
            RoslynXamlMetadataEditCapabilities
                .UpdatePropertyAccessor |
            RoslynXamlMetadataEditCapabilities
                .UpdateEventAccessor |
            RoslynXamlMetadataEditCapabilities
                .UpdateConstructorBody |
            RoslynXamlMetadataEditCapabilities
                .UpdateDestructorBody |
            RoslynXamlMetadataEditCapabilities
                .UpdateOperatorBody |
            RoslynXamlMetadataEditCapabilities
                .AddMethodToExistingType |
            RoslynXamlMetadataEditCapabilities
                .AddInstanceConstructorToExistingType,
            session.Capabilities);

        RoslynXamlMetadataDeltaUpdate first =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 2; }"));

        Assert.True(
            first.Status ==
            RoslynXamlMetadataDeltaStatus.Ready,
            string.Join(
                Environment.NewLine,
                first.Diagnostics.Select(
                    static diagnostic =>
                        diagnostic.ToString())));
        Assert.True(first.CanCommit);
        Assert.True(first.HasChanges);
        Assert.NotEmpty(first.MetadataDelta);
        Assert.NotEmpty(first.IlDelta);
        Assert.NotEmpty(first.PdbDelta);
        Assert.NotEmpty(session.InitialPeImage);
        Assert.NotEmpty(session.InitialPdbImage);
        Assert.Single(first.UpdatedMethodTokens);
        using (MetadataReaderProvider provider =
               MetadataReaderProvider.FromMetadataImage(
                   first.MetadataDelta))
        {
            MetadataReader reader = provider.GetMetadataReader();
            Assert.NotEmpty(
                reader.GetEditAndContinueLogEntries());
        }
        Assert.DoesNotContain(
            first.Diagnostics,
            static diagnostic =>
                diagnostic.Severity ==
                DiagnosticSeverity.Error);
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(first));
        Assert.Equal(1, session.Generation);

        RoslynXamlMetadataDeltaUpdate second =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 3; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            second.Status);
        Assert.Equal(1, second.BaselineGeneration);
        Assert.Single(second.UpdatedMethodTokens);
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(second));
        Assert.Equal(2, session.Generation);
    }

    [Fact]
    public void PropertyAndIndexerAccessorBodiesProduceMethodDeltas()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public sealed class Target { " +
                "private int _value; " +
                "public int Value { " +
                "get { return _value + 1; } " +
                "set { _value = value + 1; } } " +
                "public int Doubled => _value * 2; " +
                "public int this[int index] => _value + index; }"));

        RoslynXamlMetadataDeltaUpdate update = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "private int _value; " +
                "public int Value { " +
                "get => _value + 3; " +
                "set => _value = value + 4; } " +
                "public int Doubled => _value * 5; " +
                "public int this[int index] => _value - index; }"));

        Assert.True(
            update.Status == RoslynXamlMetadataDeltaStatus.Ready,
            string.Join(
                Environment.NewLine,
                update.Diagnostics.Select(
                    static diagnostic => diagnostic.ToString())));
        Assert.Equal(4, update.UpdatedMethodTokens.Length);
        Assert.Equal(
            update.UpdatedMethodTokens.Length,
            update.UpdatedMethodTokens.Distinct().Count());
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(update));
        Assert.Equal(1, session.Generation);
    }

    [Fact]
    public void AutoPropertyInitializerChangeRemainsRestartRequired()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; set; } = 1; }"));

        RoslynXamlMetadataDeltaUpdate update = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; set; } = 2; }"));

        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
            update.Status);
        Assert.Contains(
            update.Diagnostics,
            static diagnostic => diagnostic.Id == "PGXAML8010");
        Assert.Equal(0, session.Generation);
    }

    [Fact]
    public void EventAccessorBodiesProduceMethodDeltas()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public sealed class Target { " +
                "private int _count; " +
                "public event System.Action Value { " +
                "add { } remove { } } }"));

        RoslynXamlMetadataDeltaUpdate update = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "private int _count; " +
                "public event System.Action Value { " +
                "add { _count++; } " +
                "remove { _count--; } } }"));

        Assert.True(
            update.Status == RoslynXamlMetadataDeltaStatus.Ready,
            string.Join(
                Environment.NewLine,
                update.Diagnostics.Select(
                    static diagnostic => diagnostic.ToString())));
        Assert.Equal(2, update.UpdatedMethodTokens.Length);
        Assert.Equal(
            update.UpdatedMethodTokens.Length,
            update.UpdatedMethodTokens.Distinct().Count());
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(update));
        Assert.Equal(1, session.Generation);
    }

    [Fact]
    public void EventDeclarationShapeChangeRemainsRestartRequired()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public sealed class Target { " +
                "public event System.Action Value { " +
                "add { } remove { } } }"));

        RoslynXamlMetadataDeltaUpdate update = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public event System.Action<int> Value { " +
                "add { } remove { } } }"));

        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
            update.Status);
        Assert.Contains(
            update.Diagnostics,
            static diagnostic => diagnostic.Id == "PGXAML8010");
        Assert.Equal(0, session.Generation);
    }

    [Fact]
    public void SpecialMethodBodiesProduceExactMethodDeltas()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public sealed class Target { " +
                "public static int State; " +
                "static Target() { State = 0; } " +
                "public Target() { State = 1; } " +
                "~Target() { State = -1; } " +
                "public static Target operator +(Target left, " +
                "Target right) { State = 2; return left; } " +
                "public static explicit operator int(Target value) " +
                "=> 3; }"));

        RoslynXamlMetadataDeltaUpdate update = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public static int State; " +
                "static Target() => State = 5; " +
                "public Target() => State = 10; " +
                "~Target() { State = -10; } " +
                "public static Target operator +(Target left, " +
                "Target right) { State = 20; return right; } " +
                "public static explicit operator int(Target value) " +
                "=> 30; }"));

        Assert.True(
            update.Status == RoslynXamlMetadataDeltaStatus.Ready,
            string.Join(
                Environment.NewLine,
                update.Diagnostics.Select(
                    static diagnostic => diagnostic.ToString())));
        Assert.Equal(5, update.UpdatedMethodTokens.Length);
        Assert.Equal(
            update.UpdatedMethodTokens.Length,
            update.UpdatedMethodTokens.Distinct().Count());
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(update));
    }

    [Fact]
    public void ConstructorInitializerChangeRemainsRestartRequired()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public class Base { public Base(int value) { } } " +
                "public sealed class Target : Base { " +
                "public Target() : base(1) { } }"));

        RoslynXamlMetadataDeltaUpdate update = session.Prepare(
            CreateCompilation(
                "public class Base { public Base(int value) { } } " +
                "public sealed class Target : Base { " +
                "public Target() : base(2) { } }"));

        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
            update.Status);
        Assert.Contains(
            update.Diagnostics,
            static diagnostic => diagnostic.Id == "PGXAML8010");
        Assert.Equal(0, session.Generation);
    }

    [Fact]
    public void OrdinaryMethodInsertionsCommitAndUpdateAcrossGenerations()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public sealed class Target { " +
                "public static int Existing() => 1; }"));

        RoslynXamlMetadataDeltaUpdate inserted = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public static int Existing() => 1; " +
                "public static int Added(int value) => value + 2; " +
                "public int Instance(int value) => value * 3; }"));

        Assert.True(
            inserted.Status == RoslynXamlMetadataDeltaStatus.Ready,
            string.Join(
                Environment.NewLine,
                inserted.Diagnostics.Select(
                    static diagnostic => diagnostic.ToString())));
        Assert.Empty(inserted.UpdatedMethodTokens);
        using (MetadataReaderProvider provider =
               MetadataReaderProvider.FromMetadataImage(
                   inserted.MetadataDelta))
        {
            MetadataReader reader = provider.GetMetadataReader();
            Assert.True(reader.MethodDefinitions.Count >= 2);
            Assert.NotEmpty(reader.GetEditAndContinueLogEntries());
        }
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(inserted));

        RoslynXamlMetadataDeltaUpdate updated = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public static int Existing() => 1; " +
                "public static int Added(int value) => value + 20; " +
                "public int Instance(int value) => value * 30; }"));

        Assert.True(
            updated.Status == RoslynXamlMetadataDeltaStatus.Ready,
            string.Join(
                Environment.NewLine,
                updated.Diagnostics.Select(
                    static diagnostic => diagnostic.ToString())));
        Assert.Equal(2, updated.UpdatedMethodTokens.Length);
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(updated));
        Assert.Equal(2, session.Generation);

        RoslynXamlMetadataDeltaUpdate deleted = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public static int Existing() => 1; " +
                "public int Instance(int value) => value * 30; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
            deleted.Status);
        Diagnostic deletionDiagnostic = Assert.Single(
            deleted.Diagnostics,
            static diagnostic => diagnostic.Id == "PGXAML8010");
        Assert.True(deletionDiagnostic.Location.IsInSource);
        Assert.Equal(
            "Added",
            deletionDiagnostic.Location.SourceTree!.GetText()
                .ToString(deletionDiagnostic.Location.SourceSpan));
        Assert.Equal(2, session.Generation);
    }

    [Fact]
    public void InstanceConstructorInsertionCommitsAndUpdatesAcrossGenerations()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; } " +
                "public Target() { Value = 1; } " +
                "public static int Existing() => new Target().Value; }"));

        RoslynXamlMetadataDeltaUpdate inserted = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; } " +
                "public Target() { Value = 1; } " +
                "public Target(int value) { Value = value + 2; } " +
                "public static int Existing() => new Target(5).Value; }"));

        Assert.True(
            inserted.Status == RoslynXamlMetadataDeltaStatus.Ready,
            string.Join(
                Environment.NewLine,
                inserted.Diagnostics.Select(
                    static diagnostic => diagnostic.ToString())));
        Assert.Single(inserted.UpdatedMethodTokens);
        using (MetadataReaderProvider provider =
               MetadataReaderProvider.FromMetadataImage(
                   inserted.MetadataDelta))
        {
            MetadataReader reader = provider.GetMetadataReader();
            Assert.True(reader.MethodDefinitions.Count >= 1);
            Assert.NotEmpty(reader.GetEditAndContinueLogEntries());
        }
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(inserted));

        RoslynXamlMetadataDeltaUpdate updated = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; } " +
                "public Target() { Value = 1; } " +
                "public Target(int value) { Value = value + 20; } " +
                "public static int Existing() => new Target(5).Value; }"));

        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            updated.Status);
        Assert.Single(updated.UpdatedMethodTokens);
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(updated));
        Assert.Equal(2, session.Generation);

        RoslynXamlMetadataDeltaUpdate deleted = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; } " +
                "public Target() { Value = 1; } " +
                "public static int Existing() => new Target().Value; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
            deleted.Status);
        Assert.Equal(2, session.Generation);
    }

    [Fact]
    public void UnsupportedMethodAndMemberInsertionsRemainRestartRequired()
    {
        const string Initial =
            "public class Target { public static int Existing() => 1; }";

        using (var session = new RoslynXamlMetadataEditSession(
                   CreateCompilation(Initial)))
        {
            RoslynXamlMetadataDeltaUpdate virtualMethod = session.Prepare(
                CreateCompilation(
                    "public class Target { " +
                    "public static int Existing() => 1; " +
                    "public virtual int Added() => 2; }"));
            Assert.Equal(
                RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
                virtualMethod.Status);
            Diagnostic diagnostic = Assert.Single(
                virtualMethod.Diagnostics,
                static item => item.Id == "PGXAML8010");
            Assert.True(diagnostic.Location.IsInSource);
            Assert.Equal(
                "Target.cs",
                diagnostic.Location.SourceTree!.FilePath);
            Assert.Equal(
                "Added",
                diagnostic.Location.SourceTree.GetText()
                    .ToString(diagnostic.Location.SourceSpan));
        }

        using (var session = new RoslynXamlMetadataEditSession(
                   CreateCompilation(Initial)))
        {
            RoslynXamlMetadataDeltaUpdate property = session.Prepare(
                CreateCompilation(
                    "public class Target { " +
                    "public static int Existing() => 1; " +
                    "public int Added => 2; }"));
            Assert.Equal(
                RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
                property.Status);
        }

        using (var session = new RoslynXamlMetadataEditSession(
                   CreateCompilation(Initial)))
        {
            RoslynXamlMetadataDeltaUpdate nestedType = session.Prepare(
                CreateCompilation(
                    "public class Target { " +
                    "public static int Existing() => 1; " +
                    "public sealed class Added { } }"));
            Assert.Equal(
                RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
                nestedType.Status);
        }

        using (var session = new RoslynXamlMetadataEditSession(
                   CreateCompilation(Initial)))
        {
            RoslynXamlMetadataDeltaUpdate staticConstructor = session.Prepare(
                CreateCompilation(
                    "public class Target { " +
                    "public static int Existing() => 1; " +
                    "static Target() { } }"));
            Assert.Equal(
                RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
                staticConstructor.Status);
        }
    }

    [Fact]
    public void ExistingMethodDeclarationChangesRemainRestartRequired()
    {
        using var session = new RoslynXamlMetadataEditSession(
            CreateCompilation(
                "public static class Target { " +
                "[System.Obsolete] " +
                "public static int Value(int value) => value; }"));

        RoslynXamlMetadataDeltaUpdate update = session.Prepare(
            CreateCompilation(
                "public static class Target { " +
                "public static int Value(int input) => input + 1; }"));

        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.RejectedUnsupportedEdit,
            update.Status);
        Assert.Contains(
            update.Diagnostics,
            static diagnostic => diagnostic.Id == "PGXAML8010");
        Diagnostic unsupported = Assert.Single(
            update.Diagnostics,
            static diagnostic => diagnostic.Id == "PGXAML8010");
        Assert.True(unsupported.Location.IsInSource);
        Assert.Equal(
            "Value",
            unsupported.Location.SourceTree!.GetText()
                .ToString(unsupported.Location.SourceSpan));
        Assert.Equal(0, session.Generation);
    }

    [Fact]
    public void RejectedAndStaleCandidatesNeverAdvanceBaseline()
    {
        CSharpCompilation initial = CreateCompilation(
            "public static class Target { " +
            "public static int Value() => 1; }");
        using var session =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate accepted =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 2; }"));
        RoslynXamlMetadataDeltaUpdate stale =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 3; }"));

        Assert.True(
            session.TryCommit(accepted) ==
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            string.Join(
                Environment.NewLine,
                accepted.Diagnostics.Select(
                    static diagnostic =>
                        diagnostic.ToString())));
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.RejectedStale,
            session.TryCommit(stale));

        RoslynXamlMetadataDeltaUpdate shapeChange =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 2; " +
                "public static int Added; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus
                .RejectedUnsupportedEdit,
            shapeChange.Status);
        Assert.Contains(
            shapeChange.Diagnostics,
            static diagnostic =>
                diagnostic.Id == "PGXAML8010");
        Assert.True(
            shapeChange.Diagnostics[0].Location.IsInSource);
        Assert.Equal(
            "Target.cs",
            shapeChange.Diagnostics[0]
                .Location.SourceTree!.FilePath);
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult
                .RejectedInvalidCandidate,
            session.TryCommit(shapeChange));
        Assert.Equal(1, session.Generation);

        RoslynXamlMetadataDeltaUpdate compileError =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => Missing; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus
                .RejectedCompilation,
            compileError.Status);
        Assert.NotEmpty(compileError.Diagnostics);
        Assert.Equal(1, session.Generation);

        RoslynXamlMetadataDeltaUpdate recovered =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 5; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            recovered.Status);
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(recovered));
        Assert.Equal(2, session.Generation);
    }

    [Fact]
    public void TriviaOnlyCandidateProducesTransactionalNoOp()
    {
        using var session =
            new RoslynXamlMetadataEditSession(
                CreateCompilation(
                    "public static class Target { " +
                    "public static int Value() => 1; }"));
        RoslynXamlMetadataDeltaUpdate update =
            session.Prepare(CreateCompilation(
                "// comment\npublic static class Target\n{\n" +
                "    public static int Value() => 1;\n}"));

        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.NoChanges,
            update.Status);
        Assert.True(update.CanCommit);
        Assert.False(update.HasChanges);
        Assert.Empty(update.MetadataDelta);
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult.Accepted,
            session.TryCommit(update));
        Assert.Equal(1, session.Generation);
    }

    [Fact]
    public void ChangedReferenceIdentityIsRejectedBeforeEmission()
    {
        MetadataReference firstReference =
            CreateReference("1.0.0.0");
        MetadataReference secondReference =
            CreateReference("2.0.0.0");
        using var session =
            new RoslynXamlMetadataEditSession(
                CreateCompilation(
                    "public static class Target { " +
                    "public static int Value() => 1; }",
                    firstReference));

        RoslynXamlMetadataDeltaUpdate update =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 2; }",
                secondReference));

        Assert.Equal(
            RoslynXamlMetadataDeltaStatus
                .RejectedUnsupportedEdit,
            update.Status);
        Assert.Contains(
            update.Diagnostics,
            static diagnostic =>
                diagnostic.Id == "PGXAML8010");
        Assert.Equal(0, session.Generation);
    }

    [Fact]
    public void ForeignAndDisposedSessionsFailClosed()
    {
        CSharpCompilation initial = CreateCompilation(
            "public static class Target { " +
            "public static int Value() => 1; }");
        using var first =
            new RoslynXamlMetadataEditSession(initial);
        var second =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate update =
            first.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 2; }"));

        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult
                .RejectedForeignSession,
            second.TryCommit(update));
        second.Dispose();
        Assert.Equal(
            RoslynXamlMetadataDeltaCommitResult
                .RejectedDisposed,
            second.TryCommit(update));
        Assert.Throws<ObjectDisposedException>(
            () => second.Prepare(initial));
    }

    [Fact]
    public void PreparedDeltaAppliesThroughRuntimeMetadataUpdaterWhenEnabled()
    {
        if (!MetadataUpdater.IsSupported)
            return;

        CSharpCompilation initial = CreateCompilation(
            "public static class Target { " +
            "public static int Value() => 1; }");
        using var session =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate update =
            session.Prepare(CreateCompilation(
                "public static class Target { " +
                "public static int Value() => 2; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            update.Status);

        var loadContext = new AssemblyLoadContext(
            "ProGPU.Xaml.MetadataDelta.Test",
            isCollectible: true);
        try
        {
            using var pe = new MemoryStream(
                session.InitialPeImage.ToArray(),
                writable: false);
            using var pdb = new MemoryStream(
                session.InitialPdbImage.ToArray(),
                writable: false);
            Assembly assembly = loadContext.LoadFromStream(
                pe,
                pdb);
            MethodInfo value = assembly.GetType("Target")!
                .GetMethod("Value")!;
            Assert.Equal(1, (int)value.Invoke(null, null)!);

            MetadataUpdater.ApplyUpdate(
                assembly,
                update.MetadataDelta.AsSpan(),
                update.IlDelta.AsSpan(),
                update.PdbDelta.AsSpan());

            Assert.Equal(2, (int)value.Invoke(null, null)!);
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(update));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void PropertyAccessorDeltaAppliesThroughRuntimeWhenEnabled()
    {
        if (!MetadataUpdater.IsSupported)
            return;

        CSharpCompilation initial = CreateCompilation(
            "public sealed class Target { " +
            "public static int Last { get; private set; } " +
            "public int Value { " +
            "get => 1; set { Last = value; } } } ");
        using var session =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate update =
            session.Prepare(CreateCompilation(
                "public sealed class Target { " +
                "public static int Last { get; private set; } " +
                "public int Value { " +
                "get => 7; set { Last = value + 10; } } } "));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            update.Status);
        Assert.Equal(2, update.UpdatedMethodTokens.Length);

        var loadContext = new AssemblyLoadContext(
            "ProGPU.Xaml.PropertyMetadataDelta.Test",
            isCollectible: true);
        try
        {
            using var pe = new MemoryStream(
                session.InitialPeImage.ToArray(),
                writable: false);
            using var pdb = new MemoryStream(
                session.InitialPdbImage.ToArray(),
                writable: false);
            Assembly assembly = loadContext.LoadFromStream(pe, pdb);
            Type targetType = assembly.GetType("Target")!;
            object target = Activator.CreateInstance(targetType)!;
            PropertyInfo value = targetType.GetProperty("Value")!;
            PropertyInfo last = targetType.GetProperty("Last")!;

            Assert.Equal(1, (int)value.GetValue(target)!);
            value.SetValue(target, 2);
            Assert.Equal(2, (int)last.GetValue(null)!);

            MetadataUpdater.ApplyUpdate(
                assembly,
                update.MetadataDelta.AsSpan(),
                update.IlDelta.AsSpan(),
                update.PdbDelta.AsSpan());

            Assert.Equal(7, (int)value.GetValue(target)!);
            value.SetValue(target, 2);
            Assert.Equal(12, (int)last.GetValue(null)!);
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(update));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void EventAccessorDeltaAppliesThroughRuntimeWhenEnabled()
    {
        if (!MetadataUpdater.IsSupported)
            return;

        CSharpCompilation initial = CreateCompilation(
            "public sealed class Target { " +
            "public static int Count { get; private set; } " +
            "public event System.Action Value { " +
            "add { Count += 1; } remove { Count -= 1; } } } ");
        using var session =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate update =
            session.Prepare(CreateCompilation(
                "public sealed class Target { " +
                "public static int Count { get; private set; } " +
                "public event System.Action Value { " +
                "add { Count += 10; } " +
                "remove { Count -= 4; } } } "));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            update.Status);
        Assert.Equal(2, update.UpdatedMethodTokens.Length);

        var loadContext = new AssemblyLoadContext(
            "ProGPU.Xaml.EventMetadataDelta.Test",
            isCollectible: true);
        try
        {
            using var pe = new MemoryStream(
                session.InitialPeImage.ToArray(),
                writable: false);
            using var pdb = new MemoryStream(
                session.InitialPdbImage.ToArray(),
                writable: false);
            Assembly assembly = loadContext.LoadFromStream(pe, pdb);
            Type targetType = assembly.GetType("Target")!;
            object target = Activator.CreateInstance(targetType)!;
            EventInfo value = targetType.GetEvent("Value")!;
            PropertyInfo count = targetType.GetProperty("Count")!;
            Action handler = static () => { };

            value.AddEventHandler(target, handler);
            Assert.Equal(1, (int)count.GetValue(null)!);
            value.RemoveEventHandler(target, handler);
            Assert.Equal(0, (int)count.GetValue(null)!);

            MetadataUpdater.ApplyUpdate(
                assembly,
                update.MetadataDelta.AsSpan(),
                update.IlDelta.AsSpan(),
                update.PdbDelta.AsSpan());

            value.AddEventHandler(target, handler);
            Assert.Equal(10, (int)count.GetValue(null)!);
            value.RemoveEventHandler(target, handler);
            Assert.Equal(6, (int)count.GetValue(null)!);
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(update));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void ConstructorAndOperatorDeltasApplyThroughRuntimeWhenEnabled()
    {
        if (!MetadataUpdater.IsSupported)
            return;

        CSharpCompilation initial = CreateCompilation(
            "public sealed class Target { " +
            "public int Value { get; } " +
            "public Target(int value) { Value = value + 1; } " +
            "public static Target operator +(Target left, Target right) " +
            "=> new Target(left.Value + right.Value); " +
            "public static explicit operator int(Target value) " +
            "=> value.Value + 1; } ");
        using var session =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate update =
            session.Prepare(CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; } " +
                "public Target(int value) { Value = value + 10; } " +
                "public static Target operator +(Target left, " +
                "Target right) => new Target(" +
                "left.Value + right.Value + 20); " +
                "public static explicit operator int(Target value) " +
                "=> value.Value + 30; } "));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            update.Status);
        Assert.Equal(3, update.UpdatedMethodTokens.Length);

        var loadContext = new AssemblyLoadContext(
            "ProGPU.Xaml.SpecialMethodMetadataDelta.Test",
            isCollectible: true);
        try
        {
            using var pe = new MemoryStream(
                session.InitialPeImage.ToArray(),
                writable: false);
            using var pdb = new MemoryStream(
                session.InitialPdbImage.ToArray(),
                writable: false);
            Assembly assembly = loadContext.LoadFromStream(pe, pdb);
            Type targetType = assembly.GetType("Target")!;
            ConstructorInfo constructor = targetType.GetConstructor(
                new[] { typeof(int) })!;
            PropertyInfo value = targetType.GetProperty("Value")!;
            MethodInfo addition = targetType.GetMethod("op_Addition")!;
            MethodInfo conversion = targetType.GetMethod("op_Explicit")!;
            object initialTarget = constructor.Invoke(new object[] { 2 });
            Assert.Equal(3, (int)value.GetValue(initialTarget)!);
            object initialSum = addition.Invoke(
                null,
                new[] { initialTarget, initialTarget })!;
            Assert.Equal(7, (int)value.GetValue(initialSum)!);
            Assert.Equal(
                4,
                (int)conversion.Invoke(null, new[] { initialTarget })!);

            MetadataUpdater.ApplyUpdate(
                assembly,
                update.MetadataDelta.AsSpan(),
                update.IlDelta.AsSpan(),
                update.PdbDelta.AsSpan());

            object updatedTarget = constructor.Invoke(new object[] { 2 });
            Assert.Equal(12, (int)value.GetValue(updatedTarget)!);
            object updatedSum = addition.Invoke(
                null,
                new[] { updatedTarget, updatedTarget })!;
            Assert.Equal(54, (int)value.GetValue(updatedSum)!);
            Assert.Equal(
                42,
                (int)conversion.Invoke(null, new[] { updatedTarget })!);
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(update));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void InsertedMethodsApplyAndUpdateThroughRuntimeWhenEnabled()
    {
        if (!MetadataUpdater.IsSupported)
            return;

        CSharpCompilation initial = CreateCompilation(
            "public sealed class Target { " +
            "public static int Existing() => 1; } ");
        using var session =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate inserted = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public static int Existing() => " +
                "Added(5) + new Target().Instance(5); " +
                "public static int Added(int value) => value + 2; " +
                "public int Instance(int value) => value * 3; } "));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            inserted.Status);

        var loadContext = new AssemblyLoadContext(
            "ProGPU.Xaml.InsertedMethodMetadataDelta.Test",
            isCollectible: true);
        try
        {
            using var pe = new MemoryStream(
                session.InitialPeImage.ToArray(),
                writable: false);
            using var pdb = new MemoryStream(
                session.InitialPdbImage.ToArray(),
                writable: false);
            Assembly assembly = loadContext.LoadFromStream(pe, pdb);
            Type targetType = assembly.GetType("Target")!;
            MethodInfo existing = targetType.GetMethod("Existing")!;
            Assert.Equal(1, (int)existing.Invoke(null, null)!);

            MetadataUpdater.ApplyUpdate(
                assembly,
                inserted.MetadataDelta.AsSpan(),
                inserted.IlDelta.AsSpan(),
                inserted.PdbDelta.AsSpan());
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(inserted));

            Assert.Equal(22, (int)existing.Invoke(null, null)!);

            RoslynXamlMetadataDeltaUpdate updated = session.Prepare(
                CreateCompilation(
                    "public sealed class Target { " +
                    "public static int Existing() => " +
                    "Added(5) + new Target().Instance(5); " +
                    "public static int Added(int value) => value + 20; " +
                    "public int Instance(int value) => value * 30; } "));
            Assert.Equal(
                RoslynXamlMetadataDeltaStatus.Ready,
                updated.Status);
            Assert.Equal(2, updated.UpdatedMethodTokens.Length);

            MetadataUpdater.ApplyUpdate(
                assembly,
                updated.MetadataDelta.AsSpan(),
                updated.IlDelta.AsSpan(),
                updated.PdbDelta.AsSpan());
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(updated));
            Assert.Equal(175, (int)existing.Invoke(null, null)!);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void InsertedConstructorAppliesAndUpdatesThroughRuntimeWhenEnabled()
    {
        if (!MetadataUpdater.IsSupported)
            return;

        CSharpCompilation initial = CreateCompilation(
            "public sealed class Target { " +
            "public int Value { get; } " +
            "public Target() { Value = 1; } " +
            "public static int Existing() => new Target().Value; } ");
        using var session =
            new RoslynXamlMetadataEditSession(initial);
        RoslynXamlMetadataDeltaUpdate inserted = session.Prepare(
            CreateCompilation(
                "public sealed class Target { " +
                "public int Value { get; } " +
                "public Target() { Value = 1; } " +
                "public Target(int value) { Value = value + 2; } " +
                "public static int Existing() => new Target(5).Value; } "));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus.Ready,
            inserted.Status);

        var loadContext = new AssemblyLoadContext(
            "ProGPU.Xaml.InsertedConstructorMetadataDelta.Test",
            isCollectible: true);
        try
        {
            using var pe = new MemoryStream(
                session.InitialPeImage.ToArray(),
                writable: false);
            using var pdb = new MemoryStream(
                session.InitialPdbImage.ToArray(),
                writable: false);
            Assembly assembly = loadContext.LoadFromStream(pe, pdb);
            Type targetType = assembly.GetType("Target")!;
            MethodInfo existing = targetType.GetMethod("Existing")!;
            Assert.Equal(1, (int)existing.Invoke(null, null)!);

            MetadataUpdater.ApplyUpdate(
                assembly,
                inserted.MetadataDelta.AsSpan(),
                inserted.IlDelta.AsSpan(),
                inserted.PdbDelta.AsSpan());
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(inserted));
            Assert.Equal(7, (int)existing.Invoke(null, null)!);
            ConstructorInfo added = targetType.GetConstructor(
                new[] { typeof(int) })!;
            PropertyInfo value = targetType.GetProperty("Value")!;
            object first = added.Invoke(new object[] { 3 });
            Assert.Equal(5, (int)value.GetValue(first)!);

            RoslynXamlMetadataDeltaUpdate updated = session.Prepare(
                CreateCompilation(
                    "public sealed class Target { " +
                    "public int Value { get; } " +
                    "public Target() { Value = 1; } " +
                    "public Target(int value) { Value = value + 20; } " +
                    "public static int Existing() => " +
                    "new Target(5).Value; } "));
            Assert.Equal(
                RoslynXamlMetadataDeltaStatus.Ready,
                updated.Status);
            Assert.Single(updated.UpdatedMethodTokens);

            MetadataUpdater.ApplyUpdate(
                assembly,
                updated.MetadataDelta.AsSpan(),
                updated.IlDelta.AsSpan(),
                updated.PdbDelta.AsSpan());
            Assert.Equal(
                RoslynXamlMetadataDeltaCommitResult.Accepted,
                session.TryCommit(updated));
            Assert.Equal(25, (int)existing.Invoke(null, null)!);
            object second = added.Invoke(new object[] { 3 });
            Assert.Equal(23, (int)value.GetValue(second)!);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static CSharpCompilation CreateCompilation(
        string source,
        MetadataReference? additionalReference = null) =>
        CSharpCompilation.Create(
            "MetadataDeltaFixture",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    SourceText.From(
                        source,
                        System.Text.Encoding.UTF8),
                    path: "Target.cs")
            },
            additionalReference == null ?
                References :
                References.Add(additionalReference),
            Options);

    private static MetadataReference CreateReference(
        string version)
    {
        CSharpCompilation compilation =
            CSharpCompilation.Create(
                "MetadataDeltaReference",
                new[]
                {
                    CSharpSyntaxTree.ParseText(
                        "[assembly: " +
                        "System.Reflection.AssemblyVersion(\"" +
                        version + "\")] public sealed class Marker { }")
                },
                References,
                Options);
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(
            result.Success,
            string.Join(
                Environment.NewLine,
                result.Diagnostics));
        return MetadataReference.CreateFromImage(
            ImmutableArray.CreateRange(image.ToArray()));
    }
}
