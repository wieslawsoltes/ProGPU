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

    private static Task<bool> WriteBytesAsync(string path, byte[] bytes)
    {
        if (!path.StartsWith(SaveDirectory, StringComparison.Ordinal)) return Task.FromResult(false);
        unsafe
        {
            fixed (byte* source = bytes)
            {
                DownloadBytes(Path.GetFileName(path), (nint)source, bytes.Length);
            }
        }
        return Task.FromResult(true);
    }

    [JSImport("pickStorage", "progpu-browser")]
    private static partial Task<string> PickStorageCoreAsync(int mode, string filters, string defaultName);

    [JSImport("getPickedStorageLength", "progpu-browser")]
    private static partial int GetPickedStorageLength();

    [JSImport("copyPickedStorage", "progpu-browser")]
    private static partial int CopyPickedStorage(nint destination, int length);

    [JSImport("clearPickedStorage", "progpu-browser")]
    private static partial void ClearPickedStorage();

    [JSImport("downloadText", "progpu-browser")]
    private static partial void DownloadText(string name, string text);

    [JSImport("downloadBytes", "progpu-browser")]
    private static partial void DownloadBytes(string name, nint source, int length);
}
