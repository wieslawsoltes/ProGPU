using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;

namespace ProGPU.Browser;

internal static partial class BrowserStorageServices
{
    private const string OpenDirectory = "/tmp/progpu-browser-open";
    private const string SaveDirectory = "/tmp/progpu-browser-save";
    private const string FolderDirectory = "/tmp/progpu-browser-folder";

    public static void Initialize()
    {
        StoragePlatformServices.PickPathAsync = PickPathAsync;
        StoragePlatformServices.WriteTextAsync = WriteTextAsync;
        StoragePlatformServices.WriteBytesAsync = WriteBytesAsync;
    }

    private static async Task<string?> PickPathAsync(int mode, IReadOnlyList<string>? fileTypes, string? defaultName)
    {
        var filters = fileTypes == null ? string.Empty : string.Join(',', fileTypes);
        var result = await PickStorageCoreAsync(mode, filters, defaultName ?? string.Empty);
        if (string.IsNullOrEmpty(result)) return null;

        if (mode == 0)
        {
            int length = GetPickedStorageLength();
            if (length < 0) return null;

            var bytes = new byte[length];
            try
            {
                if (length != 0)
                {
                    unsafe
                    {
                        fixed (byte* destination = bytes)
                        {
                            int copied = CopyPickedStorage((nint)destination, length);
                            if (copied != length)
                            {
                                throw new IOException($"The browser selected {length} bytes but copied {copied} bytes.");
                            }
                        }
                    }
                }

                var name = Uri.UnescapeDataString(result);
                Directory.CreateDirectory(OpenDirectory);
                var path = Path.Combine(OpenDirectory, Path.GetFileName(name));
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                return path;
            }
            finally
            {
                ClearPickedStorage();
            }
        }

        if (mode == 1)
        {
            var selection = ParseHandleSelection(result);
            if (selection == null) return null;

            var directory = Path.Combine(SaveDirectory, selection.Value.Token);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, selection.Value.Name);
        }

        if (mode == 2)
        {
            var selection = ParseHandleSelection(result);
            if (selection == null) return null;
            return Path.Combine(FolderDirectory, selection.Value.Token, selection.Value.Name);
        }

        return null;
    }

    private static async Task<bool> WriteTextAsync(string path, string text)
    {
        if (!TryGetSaveSelection(path, out var token, out var name)) return false;
        if (token != "download" && await WritePickedStorageText(token, text)) return true;

        DownloadText(name, text);
        return true;
    }

    private static async Task<bool> WriteBytesAsync(string path, byte[] bytes)
    {
        if (!TryGetSaveSelection(path, out var token, out var name)) return false;
        if (token != "download")
        {
            Task<bool> writeTask;
            unsafe
            {
                fixed (byte* source = bytes)
                {
                    writeTask = WritePickedStorageBytes(token, (nint)source, bytes.Length);
                }
            }

            if (await writeTask) return true;
        }

        unsafe
        {
            fixed (byte* source = bytes)
            {
                DownloadBytes(name, (nint)source, bytes.Length);
            }
        }
        return true;
    }

    private static (string Token, string Name)? ParseHandleSelection(string result)
    {
        int separator = result.IndexOf('\n');
        if (separator <= 0 || separator == result.Length - 1) return null;

        var token = result[..separator];
        var name = Path.GetFileName(Uri.UnescapeDataString(result[(separator + 1)..]));
        if (string.IsNullOrWhiteSpace(token) || Path.GetFileName(token) != token || token is "." or ".." ||
            string.IsNullOrWhiteSpace(name)) return null;
        return (token, name);
    }

    private static bool TryGetSaveSelection(string path, out string token, out string name)
    {
        token = string.Empty;
        name = string.Empty;
        var relative = Path.GetRelativePath(SaveDirectory, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)) return false;

        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts.Any(part => part is "." or "..")) return false;
        token = parts[0];
        name = parts[1];
        return true;
    }

    [JSImport("pickStorage", "progpu-browser")]
    private static partial Task<string> PickStorageCoreAsync(int mode, string filters, string defaultName);

    [JSImport("getPickedStorageLength", "progpu-browser")]
    private static partial int GetPickedStorageLength();

    [JSImport("copyPickedStorage", "progpu-browser")]
    private static partial int CopyPickedStorage(nint destination, int length);

    [JSImport("clearPickedStorage", "progpu-browser")]
    private static partial void ClearPickedStorage();

    [JSImport("writePickedStorageText", "progpu-browser")]
    private static partial Task<bool> WritePickedStorageText(string token, string text);

    [JSImport("writePickedStorageBytes", "progpu-browser")]
    private static partial Task<bool> WritePickedStorageBytes(string token, nint source, int length);

    [JSImport("downloadText", "progpu-browser")]
    private static partial void DownloadText(string name, string text);

    [JSImport("downloadBytes", "progpu-browser")]
    private static partial void DownloadBytes(string name, nint source, int length);
}
