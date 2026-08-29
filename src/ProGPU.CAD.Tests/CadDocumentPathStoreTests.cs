using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadDocumentPathStoreTests
{
    [Theory]
    [InlineData(".dxf", CadDocumentFormat.Dxf)]
    [InlineData(".dwg", CadDocumentFormat.Dwg)]
    public async Task AutoFormatSaveAndLoadRoundTripFromPath(
        string extension,
        CadDocumentFormat expectedFormat)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string path = Path.Combine(directory.FullName, $"drawing{extension}");
            CadDocumentSession session = CreateEditedSession();
            var stages = new List<CadOperationStage>();
            var progress = new InlineProgress<CadOperationProgress>(
                value => stages.Add(value.Stage));
            var store = new CadDocumentPathStore();

            CadSaveResult save = await store.SaveAsync(
                session,
                path,
                options: new CadSaveOptions
                {
                    AllowUncertifiedWrite = true,
                },
                progress: progress);

            Assert.Equal(1UL, save.SavedGeneration);
            Assert.False(save.RequiresSavedGenerationCommit);
            Assert.False(session.IsDirty);
            Assert.True(new FileInfo(path).Length > 0);
            Assert.Equal(CadOperationStage.Preparing, stages[0]);
            Assert.Contains(CadOperationStage.Writing, stages);
            Assert.Equal(CadOperationStage.Completed, stages[^1]);
            Assert.Equal(
                1,
                stages.Count(stage => stage == CadOperationStage.Completed));
            AssertNoStagingFiles(directory);

            CadLoadResult loaded = await store.LoadAsync(path);

            Assert.Equal(expectedFormat, loaded.Session.SourceFormat);
            Assert.Equal(Path.GetFullPath(path), loaded.Session.SourceName);
            Assert.Equal(
                1,
                loaded.Session.Read(document => document.Entities.Count));
            Assert.False(loaded.Session.IsDirty);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SaveReplacesExistingFileAndCommitsLatestSerializedGeneration()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string path = Path.Combine(directory.FullName, "drawing.dxf");
            byte[] marker = [1, 3, 5, 7, 9];
            await File.WriteAllBytesAsync(path, marker);
            CadDocumentSession session = CreateEditedSession();
            var store = new CadDocumentPathStore();

            await store.SaveAsync(
                session,
                path,
                options: new CadSaveOptions
                {
                    AllowUncertifiedWrite = true,
                });
            session.Edit(
                "Add newer circle",
                document => document.Entities.Add(new Circle(XYZ.Zero, 4)));
            CadSaveResult second = await store.SaveAsync(
                session,
                path,
                options: new CadSaveOptions
                {
                    AllowUncertifiedWrite = true,
                });

            Assert.Equal(2UL, second.SavedGeneration);
            Assert.Equal(2UL, session.SavedGeneration);
            Assert.False(session.IsDirty);
            byte[] persisted = await File.ReadAllBytesAsync(path);
            Assert.False(marker.SequenceEqual(persisted));
            CadLoadResult loaded = await store.LoadAsync(path);
            Assert.Equal(
                2,
                loaded.Session.Read(document => document.Entities.Count));
            AssertNoStagingFiles(directory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EditAfterSerializationRemainsDirtyAfterPathCommit()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string path = Path.Combine(directory.FullName, "drawing.dxf");
            CadDocumentSession session = CreateEditedSession();
            var store = new CadDocumentPathStore(
                new EditAfterSerializationStore());

            CadSaveResult save = await store.SaveAsync(
                session,
                path,
                options: new CadSaveOptions
                {
                    AllowUncertifiedWrite = true,
                });

            Assert.Equal(1UL, save.SavedGeneration);
            Assert.Equal(1UL, session.SavedGeneration);
            Assert.Equal(2UL, session.ContentGeneration);
            Assert.True(session.IsDirty);
            CadLoadResult loaded = await new CadDocumentPathStore().LoadAsync(path);
            Assert.Equal(
                1,
                loaded.Session.Read(document => document.Entities.Count));
            AssertNoStagingFiles(directory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SerializationFailurePreservesDestinationAndDirtyGeneration()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string path = Path.Combine(directory.FullName, "drawing.dxf");
            byte[] marker = [2, 4, 6, 8];
            await File.WriteAllBytesAsync(path, marker);
            CadDocumentSession session = CreateEditedSession();
            var store = new CadDocumentPathStore();

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await store.SaveAsync(session, path));

            Assert.Equal(marker, await File.ReadAllBytesAsync(path));
            Assert.True(session.IsDirty);
            Assert.Equal(0UL, session.SavedGeneration);
            AssertNoStagingFiles(directory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CancellationBeforeCommitPreservesDestinationAndCleansStaging()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string path = Path.Combine(directory.FullName, "drawing.dxf");
            byte[] marker = [10, 20, 30, 40];
            await File.WriteAllBytesAsync(path, marker);
            CadDocumentSession session = CreateEditedSession();
            using var cancellation = new CancellationTokenSource();
            var stages = new List<CadOperationStage>();
            var progress = new InlineProgress<CadOperationProgress>(value =>
            {
                stages.Add(value.Stage);
                if (value.Stage == CadOperationStage.Writing)
                {
                    cancellation.Cancel();
                }
            });
            var store = new CadDocumentPathStore();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await store.SaveAsync(
                    session,
                    path,
                    options: new CadSaveOptions
                    {
                        AllowUncertifiedWrite = true,
                    },
                    progress: progress,
                    cancellationToken: cancellation.Token));

            Assert.Contains(CadOperationStage.Writing, stages);
            Assert.DoesNotContain(CadOperationStage.Completed, stages);
            Assert.Equal(marker, await File.ReadAllBytesAsync(path));
            Assert.True(session.IsDirty);
            Assert.Equal(0UL, session.SavedGeneration);
            AssertNoStagingFiles(directory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CommitFailurePreservesDestinationDirectoryAndCleansStaging()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string path = Path.Combine(directory.FullName, "drawing.dxf");
            Directory.CreateDirectory(path);
            CadDocumentSession session = CreateEditedSession();
            var stages = new List<CadOperationStage>();
            var progress = new InlineProgress<CadOperationProgress>(
                value => stages.Add(value.Stage));
            var store = new CadDocumentPathStore();

            IOException exception = await Assert.ThrowsAsync<IOException>(
                async () => await store.SaveAsync(
                    session,
                    path,
                    options: new CadSaveOptions
                    {
                        AllowUncertifiedWrite = true,
                    },
                    progress: progress));

            Assert.Contains("identifies a directory", exception.Message);
            Assert.True(Directory.Exists(path));
            Assert.Contains(CadOperationStage.Writing, stages);
            Assert.DoesNotContain(CadOperationStage.Completed, stages);
            Assert.True(session.IsDirty);
            Assert.Equal(0UL, session.SavedGeneration);
            AssertNoStagingFiles(directory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AutoSaveRejectsUnknownExtensionBeforeCreatingAFile()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string path = Path.Combine(directory.FullName, "drawing.cad");
            CadDocumentSession session = CreateEditedSession();
            var store = new CadDocumentPathStore();

            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
                async () => await store.SaveAsync(
                    session,
                    path,
                    options: new CadSaveOptions
                    {
                        AllowUncertifiedWrite = true,
                    }));

            Assert.Contains(".dxf or .dwg", exception.Message);
            Assert.False(File.Exists(path));
            Assert.True(session.IsDirty);
            AssertNoStagingFiles(directory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SaveRequiresAnExistingDestinationDirectory()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            "progpu-cad-path-");
        try
        {
            string missingDirectory = Path.Combine(directory.FullName, "missing");
            string path = Path.Combine(missingDirectory, "drawing.dxf");
            CadDocumentSession session = CreateEditedSession();
            var store = new CadDocumentPathStore();

            await Assert.ThrowsAsync<DirectoryNotFoundException>(
                async () => await store.SaveAsync(
                    session,
                    path,
                    options: new CadSaveOptions
                    {
                        AllowUncertifiedWrite = true,
                    }));

            Assert.False(Directory.Exists(missingDirectory));
            Assert.True(session.IsDirty);
            AssertNoStagingFiles(directory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static CadDocumentSession CreateEditedSession()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(
            ACadVersion.AC1032);
        session.Edit(
            "Add line",
            document => document.Entities.Add(
                new Line(new XYZ(1, 2, 3), new XYZ(4, 5, 6))));
        return session;
    }

    private static void AssertNoStagingFiles(DirectoryInfo directory) =>
        Assert.Empty(directory.EnumerateFiles(".progpu-cad-*.tmp"));

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        internal InlineProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }

    private sealed class EditAfterSerializationStore : ICadDocumentStore
    {
        private readonly CadDocumentStore _inner = new();

        public ValueTask<CadLoadResult> LoadAsync(
            Stream source,
            CadDocumentFormat format = CadDocumentFormat.Auto,
            CadLoadOptions? options = null,
            string? sourceName = null,
            IProgress<CadOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(
                source,
                format,
                options,
                sourceName,
                progress,
                cancellationToken);

        public async ValueTask<CadSaveResult> SaveAsync(
            CadDocumentSession session,
            Stream destination,
            CadDocumentFormat format,
            CadSaveOptions? options = null,
            IProgress<CadOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CadSaveResult result = await _inner.SaveAsync(
                session,
                destination,
                format,
                options,
                progress,
                cancellationToken);
            session.Edit(
                "Concurrent newer edit",
                document => document.Entities.Add(new Circle(XYZ.Zero, 8)));
            return result;
        }
    }
}
