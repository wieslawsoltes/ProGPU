using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;

namespace ProGPU.Browser;

internal static partial class BrowserStorageServices
{
    private const string OpenDirectory = "/tmp/progpu-browser-open";
    private const string SaveDirectory = "/tmp/progpu-browser-save";

    public static void Initialize()
    {
        StoragePlatformServices.PickPathAsync = PickPathAsync;
        StoragePlatformServices.WriteTextAsync = WriteTextAsync;
    }

    private static async Task<string?> PickPathAsync(int mode, IReadOnlyList<string>? fileTypes, string? defaultName)
    {
        var filters = fileTypes == null ? string.Empty : string.Join(',', fileTypes);
        var result = await PickStorageCoreAsync(mode, filters, defaultName ?? string.Empty).ConfigureAwait(false);
        if (string.IsNullOrEmpty(result)) return null;

        if (mode == 0)
        {
            var separator = result.IndexOf('\n');
            if (separator <= 0) return null;
            var name = Uri.UnescapeDataString(result[..separator]);
            var base64 = result[(separator + 1)..];
            Directory.CreateDirectory(OpenDirectory);
            var path = Path.Combine(OpenDirectory, Path.GetFileName(name));
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64)).ConfigureAwait(false);
            return path;
        }

        if (mode == 1)
        {
            Directory.CreateDirectory(SaveDirectory);
            return Path.Combine(SaveDirectory, Path.GetFileName(result));
        }

        return null;
    }

    private static Task<bool> WriteTextAsync(string path, string text)
    {
        if (!path.StartsWith(SaveDirectory, StringComparison.Ordinal)) return Task.FromResult(false);
        DownloadText(Path.GetFileName(path), text);
        return Task.FromResult(true);
    }

    [JSImport("pickStorage", "progpu-browser")]
    private static partial Task<string> PickStorageCoreAsync(int mode, string filters, string defaultName);

    [JSImport("downloadText", "progpu-browser")]
    private static partial void DownloadText(string name, string text);
}
