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
            RoslynXamlMetadataEditCapabilities.UpdateMethodBody,
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
                "public static int Added() => 4; }"));
        Assert.Equal(
            RoslynXamlMetadataDeltaStatus
                .RejectedUnsupportedEdit,
            shapeChange.Status);
        Assert.Contains(
            shapeChange.Diagnostics,
            static diagnostic =>
                diagnostic.Id == "PGXAML8010");
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
