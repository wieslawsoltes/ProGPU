using Microsoft.UI.Xaml;
using Xunit;

namespace ProGPU.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class StoragePickerPlatformServiceCollection
{
    public const string CollectionName = "Storage picker platform service";
}

[Collection(StoragePickerPlatformServiceCollection.CollectionName)]
public sealed class StoragePickerPlatformServiceTests
{
    [Fact]
    public async Task PickerApisForwardModeFiltersAndSuggestedNameToHost()
    {
        var previousPicker = StoragePlatformServices.PickPathAsync;
        var requests = new List<(int Mode, IReadOnlyList<string>? Types, string? Name)>();
        try
        {
            StoragePlatformServices.PickPathAsync = (mode, types, name) =>
            {
                requests.Add((mode, types, name));
                return Task.FromResult<string?>(mode switch
                {
                    0 => "/picked/input.txt",
                    1 => "/picked/output.json",
                    2 => "/picked/folder",
                    _ => null
                });
            };

            var open = new FileOpenPicker();
            open.FileTypeFilter.Add(".txt");
            StorageFile? opened = await open.PickSingleFileAsync();

            var save = new FileSavePicker { SuggestedFileName = "output.json" };
            save.FileTypeChoices.Add("JSON", [".json"]);
            StorageFile? saved = await save.PickSaveFileAsync();

            StorageFolder? folder = await new FolderPicker().PickSingleFolderAsync();

            Assert.Equal("/picked/input.txt", opened?.Path);
            Assert.Equal("/picked/output.json", saved?.Path);
            Assert.Equal("/picked/folder", folder?.Path);
            Assert.Collection(
                requests,
                request =>
                {
                    Assert.Equal(0, request.Mode);
                    Assert.Equal([".txt"], request.Types);
                    Assert.Null(request.Name);
                },
                request =>
                {
                    Assert.Equal(1, request.Mode);
                    Assert.Equal([".json"], request.Types);
                    Assert.Equal("output.json", request.Name);
                },
                request =>
                {
                    Assert.Equal(2, request.Mode);
                    Assert.Null(request.Types);
                    Assert.Null(request.Name);
                });
        }
        finally
        {
            StoragePlatformServices.PickPathAsync = previousPicker;
        }
    }

    [Fact]
    public async Task StorageFileUsesHostCoordinatedWriteWhenAvailable()
    {
        var previousTextWriter = StoragePlatformServices.WriteTextAsync;
        var previousByteWriter = StoragePlatformServices.WriteBytesAsync;
        string? writtenPath = null;
        string? writtenText = null;
        byte[]? writtenBytes = null;
        try
        {
            StoragePlatformServices.WriteTextAsync = (path, text) =>
            {
                writtenPath = path;
                writtenText = text;
                return Task.FromResult(true);
            };
            StoragePlatformServices.WriteBytesAsync = (path, bytes) =>
            {
                writtenPath = path;
                writtenBytes = bytes;
                return Task.FromResult(true);
            };

            var file = new StorageFile("/security-scoped/document.txt");
            await file.WriteTextAsync("text");
            Assert.Equal("/security-scoped/document.txt", writtenPath);
            Assert.Equal("text", writtenText);

            await file.WriteBytesAsync([1, 2, 3]);
            Assert.Equal([1, 2, 3], writtenBytes);
        }
        finally
        {
            StoragePlatformServices.WriteTextAsync = previousTextWriter;
            StoragePlatformServices.WriteBytesAsync = previousByteWriter;
        }
    }

    [Fact]
    public async Task StorageObjectsUseHostBackedReadEnumerationAndCreationWhenAvailable()
    {
        var previousTextReader = StoragePlatformServices.ReadTextAsync;
        var previousByteReader = StoragePlatformServices.ReadBytesAsync;
        var previousFileEnumerator = StoragePlatformServices.EnumerateFilesAsync;
        var previousFolderEnumerator = StoragePlatformServices.EnumerateFoldersAsync;
        var previousFileCreator = StoragePlatformServices.CreateFileAsync;
        var previousFolderCreator = StoragePlatformServices.CreateFolderAsync;
        var requests = new List<string>();
        try
        {
            StoragePlatformServices.ReadTextAsync = path =>
            {
                requests.Add($"read-text:{path}");
                return Task.FromResult("host text");
            };
            StoragePlatformServices.ReadBytesAsync = path =>
            {
                requests.Add($"read-bytes:{path}");
                return Task.FromResult<byte[]>([4, 5, 6]);
            };
            StoragePlatformServices.EnumerateFilesAsync = path =>
            {
                requests.Add($"files:{path}");
                return Task.FromResult<IReadOnlyList<string>>(["content://tree/root/document/one.txt"]);
            };
            StoragePlatformServices.EnumerateFoldersAsync = path =>
            {
                requests.Add($"folders:{path}");
                return Task.FromResult<IReadOnlyList<string>>(["content://tree/root/document/child"]);
            };
            StoragePlatformServices.CreateFileAsync = (path, name) =>
            {
                requests.Add($"create-file:{path}:{name}");
                return Task.FromResult($"{path}/document/{name}");
            };
            StoragePlatformServices.CreateFolderAsync = (path, name) =>
            {
                requests.Add($"create-folder:{path}:{name}");
                return Task.FromResult($"{path}/tree/{name}");
            };

            const string filePath = "content://documents/document/source.txt";
            var file = new StorageFile(filePath);
            Assert.Equal("host text", await file.ReadTextAsync());
            Assert.Equal([4, 5, 6], await file.ReadBytesAsync());

            const string folderPath = "content://documents/tree/root";
            var folder = new StorageFolder(folderPath);
            Assert.Equal("one.txt", Assert.Single(await folder.GetFilesAsync()).Name);
            Assert.Equal("child", Assert.Single(await folder.GetFoldersAsync()).Name);
            Assert.Equal("created.txt", (await folder.CreateFileAsync("created.txt")).Name);
            Assert.Equal("created", (await folder.CreateFolderAsync("created")).Name);

            Assert.Equal(
                [
                    $"read-text:{filePath}",
                    $"read-bytes:{filePath}",
                    $"files:{folderPath}",
                    $"folders:{folderPath}",
                    $"create-file:{folderPath}:created.txt",
                    $"create-folder:{folderPath}:created"
                ],
                requests);
        }
        finally
        {
            StoragePlatformServices.ReadTextAsync = previousTextReader;
            StoragePlatformServices.ReadBytesAsync = previousByteReader;
            StoragePlatformServices.EnumerateFilesAsync = previousFileEnumerator;
            StoragePlatformServices.EnumerateFoldersAsync = previousFolderEnumerator;
            StoragePlatformServices.CreateFileAsync = previousFileCreator;
            StoragePlatformServices.CreateFolderAsync = previousFolderCreator;
        }
    }
}
