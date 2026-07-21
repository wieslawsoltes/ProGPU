using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.UI.Xaml;

public class StorageFile
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string FileType => System.IO.Path.GetExtension(Path);

    public StorageFile(string path)
    {
        Path = path;
    }

    public async Task<string> ReadTextAsync()
    {
        if (StoragePlatformServices.ReadTextAsync is { } platformRead)
            return await platformRead(Path).ConfigureAwait(false);
        return await File.ReadAllTextAsync(Path);
    }

    public async Task<byte[]> ReadBytesAsync()
    {
        if (StoragePlatformServices.ReadBytesAsync is { } platformRead)
            return await platformRead(Path).ConfigureAwait(false);
        return await File.ReadAllBytesAsync(Path);
    }

    public async Task WriteTextAsync(string text)
    {
        if (StoragePlatformServices.WriteTextAsync is { } platformWrite &&
            await platformWrite(Path, text).ConfigureAwait(false))
        {
            return;
        }
        await File.WriteAllTextAsync(Path, text);
    }

    public async Task WriteBytesAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (StoragePlatformServices.WriteBytesAsync is { } platformWrite &&
            await platformWrite(Path, bytes).ConfigureAwait(false))
        {
            return;
        }
        await File.WriteAllBytesAsync(Path, bytes);
    }

    public static Task<StorageFile> GetFileFromPathAsync(string path)
    {
        return Task.FromResult(new StorageFile(path));
    }
}

/// <summary>Host-provided storage operations for platforms without native process dialogs.</summary>
public static class StoragePlatformServices
{
    public static Func<int, IReadOnlyList<string>?, string?, Task<string?>>? PickPathAsync { get; set; }
    public static Func<string, Task<string>>? ReadTextAsync { get; set; }
    public static Func<string, Task<byte[]>>? ReadBytesAsync { get; set; }
    public static Func<string, string, Task<bool>>? WriteTextAsync { get; set; }
    public static Func<string, byte[], Task<bool>>? WriteBytesAsync { get; set; }
    public static Func<string, Task<IReadOnlyList<string>>>? EnumerateFilesAsync { get; set; }
    public static Func<string, Task<IReadOnlyList<string>>>? EnumerateFoldersAsync { get; set; }
    public static Func<string, string, Task<string>>? CreateFileAsync { get; set; }
    public static Func<string, string, Task<string>>? CreateFolderAsync { get; set; }
}

public class StorageFolder
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);

    public StorageFolder(string path)
    {
        Path = path;
    }

    public async Task<IReadOnlyList<StorageFile>> GetFilesAsync()
    {
        if (StoragePlatformServices.EnumerateFilesAsync is { } platformEnumerate)
        {
            IReadOnlyList<string> paths = await platformEnumerate(Path).ConfigureAwait(false);
            var platformFiles = new List<StorageFile>(paths.Count);
            foreach (string path in paths)
                platformFiles.Add(new StorageFile(path));
            return platformFiles;
        }

        var files = new List<StorageFile>();
        if (Directory.Exists(Path))
        {
            foreach (var file in Directory.GetFiles(Path))
            {
                files.Add(new StorageFile(file));
            }
        }
        return files;
    }

    public async Task<IReadOnlyList<StorageFolder>> GetFoldersAsync()
    {
        if (StoragePlatformServices.EnumerateFoldersAsync is { } platformEnumerate)
        {
            IReadOnlyList<string> paths = await platformEnumerate(Path).ConfigureAwait(false);
            var platformFolders = new List<StorageFolder>(paths.Count);
            foreach (string path in paths)
                platformFolders.Add(new StorageFolder(path));
            return platformFolders;
        }

        var folders = new List<StorageFolder>();
        if (Directory.Exists(Path))
        {
            foreach (var dir in Directory.GetDirectories(Path))
            {
                folders.Add(new StorageFolder(dir));
            }
        }
        return folders;
    }

    public async Task<StorageFile> CreateFileAsync(string desiredName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredName);
        if (StoragePlatformServices.CreateFileAsync is { } platformCreate)
        {
            string path = await platformCreate(Path, desiredName).ConfigureAwait(false);
            return new StorageFile(path);
        }

        var fullPath = System.IO.Path.Combine(Path, desiredName);
        await File.WriteAllTextAsync(fullPath, string.Empty);
        return new StorageFile(fullPath);
    }

    public async Task<StorageFolder> CreateFolderAsync(string desiredName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredName);
        if (StoragePlatformServices.CreateFolderAsync is { } platformCreate)
        {
            string path = await platformCreate(Path, desiredName).ConfigureAwait(false);
            return new StorageFolder(path);
        }

        string fullPath = System.IO.Path.Combine(Path, desiredName);
        Directory.CreateDirectory(fullPath);
        return new StorageFolder(fullPath);
    }

    public static Task<StorageFolder> GetFolderFromPathAsync(string path)
    {
        return Task.FromResult(new StorageFolder(path));
    }
}

public class FileOpenPicker
{
    public List<string> FileTypeFilter { get; } = new();
    public string SuggestedStartLocation { get; set; } = string.Empty;

    public async Task<StorageFile?> PickSingleFileAsync()
    {
        string? result = await StoragePickerHelper.RunPickerAsync(PickerMode.Open, FileTypeFilter);
        return string.IsNullOrEmpty(result) ? null : new StorageFile(result);
    }
}

public class FileSavePicker
{
    public Dictionary<string, IList<string>> FileTypeChoices { get; } = new();
    public string SuggestedFileName { get; set; } = "untitled.txt";
    public string SuggestedStartLocation { get; set; } = string.Empty;

    public async Task<StorageFile?> PickSaveFileAsync()
    {
        var allowedTypes = new List<string>();
        foreach (var choice in FileTypeChoices.Values)
        {
            allowedTypes.AddRange(choice);
        }
        string? result = await StoragePickerHelper.RunPickerAsync(PickerMode.Save, allowedTypes, SuggestedFileName);
        return string.IsNullOrEmpty(result) ? null : new StorageFile(result);
    }
}

public class FolderPicker
{
    public List<string> FileTypeFilter { get; } = new();
    public string SuggestedStartLocation { get; set; } = string.Empty;

    public async Task<StorageFolder?> PickSingleFolderAsync()
    {
        string? result = await StoragePickerHelper.RunPickerAsync(PickerMode.Folder, null);
        return string.IsNullOrEmpty(result) ? null : new StorageFolder(result);
    }
}

internal enum PickerMode
{
    Open,
    Save,
    Folder
}

internal static class StoragePickerHelper
{
    public static async Task<string?> RunPickerAsync(PickerMode mode, List<string>? fileTypes, string? defaultName = null)
    {
        if (StoragePlatformServices.PickPathAsync is { } platformPicker)
            return await platformPicker((int)mode, fileTypes, defaultName).ConfigureAwait(false);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await RunMacPickerAsync(mode, fileTypes, defaultName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await RunWindowsPickerAsync(mode, fileTypes, defaultName);
        }
        else
        {
            return await RunLinuxPickerAsync(mode, fileTypes, defaultName);
        }
    }

    private static async Task<string?> RunMacPickerAsync(PickerMode mode, List<string>? fileTypes, string? defaultName)
    {
        string script = "";
        if (mode == PickerMode.Open)
        {
            var typesStr = "";
            if (fileTypes != null && fileTypes.Count > 0 && !fileTypes.Contains("*"))
            {
                var cleanTypes = new List<string>();
                foreach (var t in fileTypes)
                {
                    var ct = t.Replace(".", "").Trim();
                    if (!string.IsNullOrEmpty(ct)) cleanTypes.Add($"\"{ct}\"");
                }
                if (cleanTypes.Count > 0)
                {
                    typesStr = $"of type {{{string.Join(", ", cleanTypes)}}} ";
                }
            }
            script = $"POSIX path of (choose file {typesStr}with prompt \"Select a file\")";
        }
        else if (mode == PickerMode.Save)
        {
            var df = defaultName ?? "untitled.txt";
            script = $"POSIX path of (choose file name default name \"{df}\" with prompt \"Save As\")";
        }
        else if (mode == PickerMode.Folder)
        {
            script = "POSIX path of (choose folder with prompt \"Select a folder\")";
        }

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e \"{script.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                return output.Trim();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mac File Picker failed: {ex.Message}");
        }
        return null;
    }

    private static async Task<string?> RunWindowsPickerAsync(PickerMode mode, List<string>? fileTypes, string? defaultName)
    {
        var psCommand = new StringBuilder();
        psCommand.Append("Add-Type -AssemblyName System.Windows.Forms; ");
        if (mode == PickerMode.Open)
        {
            psCommand.Append("$f = New-Object System.Windows.Forms.OpenFileDialog; ");
            var filter = "All Files (*.*)|*.*";
            if (fileTypes != null && fileTypes.Count > 0)
            {
                var extensions = string.Join(";", fileTypes);
                filter = $"Selected Files ({extensions})|{extensions}|All Files (*.*)|*.*";
            }
            psCommand.Append($"$f.Filter = '{filter}'; ");
            psCommand.Append("if ($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $f.FileName }");
        }
        else if (mode == PickerMode.Save)
        {
            psCommand.Append("$f = New-Object System.Windows.Forms.SaveFileDialog; ");
            if (!string.IsNullOrEmpty(defaultName))
            {
                psCommand.Append($"$f.FileName = '{defaultName}'; ");
            }
            psCommand.Append("if ($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $f.FileName }");
        }
        else if (mode == PickerMode.Folder)
        {
            psCommand.Append("$f = New-Object System.Windows.Forms.FolderBrowserDialog; ");
            psCommand.Append("if ($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $f.SelectedPath }");
        }

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{psCommand}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                return output.Trim();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Windows PowerShell File Picker failed: {ex.Message}");
        }
        return null;
    }

    private static async Task<string?> RunLinuxPickerAsync(PickerMode mode, List<string>? fileTypes, string? defaultName)
    {
        var args = new List<string> { "--file-selection" };
        if (mode == PickerMode.Open)
        {
            args.Add("--title=\"Select a file\"");
        }
        else if (mode == PickerMode.Save)
        {
            args.Add("--save");
            args.Add("--confirm-overwrite");
            args.Add("--title=\"Save As\"");
            if (!string.IsNullOrEmpty(defaultName))
            {
                args.Add($"--filename=\"{defaultName}\"");
            }
        }
        else if (mode == PickerMode.Folder)
        {
            args.Add("--directory");
            args.Add("--title=\"Select a folder\"");
        }

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "zenity",
                    Arguments = string.Join(" ", args),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                return output.Trim();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Linux Zenity File Picker failed: {ex.Message}");
        }
        return null;
    }
}
