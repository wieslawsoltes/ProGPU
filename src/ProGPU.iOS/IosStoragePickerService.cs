using Foundation;
using Microsoft.UI.Xaml;
using UniformTypeIdentifiers;
using UIKit;

namespace ProGPU.iOS;

/// <summary>
/// Bridges the path-shaped WinUI storage contract to UIKit's document browser.
/// The service owns security-scoped URL access until the window host shuts down.
/// </summary>
internal sealed class IosStoragePickerService : IDisposable
{
    private const int OpenMode = 0;
    private const int SaveMode = 1;
    private const int FolderMode = 2;

    private readonly UIViewController _owner;
    private readonly SemaphoreSlim _pickerGate = new(1, 1);
    private readonly object _leaseLock = new();
    private readonly Dictionary<string, NSUrl> _securityScopedUrls = new(StringComparer.Ordinal);
    private UIDocumentPickerViewController? _activePicker;
    private TaskCompletionSource<string?>? _activeCompletion;
    private bool _disposed;

    public IosStoragePickerService(UIViewController owner) =>
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> PickPathAsync(
        int mode,
        IReadOnlyList<string>? fileTypes,
        string? suggestedFileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mode is not (OpenMode or SaveMode or FolderMode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        await _pickerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await PresentPickerAsync(mode, fileTypes, suggestedFileName).ConfigureAwait(false);
        }
        finally
        {
            _pickerGate.Release();
        }
    }

    public Task<bool> WriteTextAsync(string path, string text) =>
        Task.FromResult(CoordinateExternalWrite(path, coordinatedPath =>
            File.WriteAllText(coordinatedPath, text)));

    public Task<bool> WriteBytesAsync(string path, byte[] bytes) =>
        Task.FromResult(CoordinateExternalWrite(path, coordinatedPath =>
            File.WriteAllBytes(coordinatedPath, bytes)));

    private Task<string?> PresentPickerAsync(
        int mode,
        IReadOnlyList<string>? fileTypes,
        string? suggestedFileName)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                completion.TrySetCanceled();
                return;
            }

            string? temporaryDirectory = null;
            try
            {
                UIDocumentPickerViewController picker;
                if (mode == OpenMode)
                {
                    picker = new UIDocumentPickerViewController(ResolveContentTypes(fileTypes), asCopy: true);
                }
                else if (mode == FolderMode)
                {
                    picker = new UIDocumentPickerViewController([UTTypes.Folder], asCopy: false);
                }
                else
                {
                    string fileName = ResolveSuggestedFileName(suggestedFileName, fileTypes);
                    temporaryDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "ProGPU.Pickers",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temporaryDirectory);
                    string temporaryPath = Path.Combine(temporaryDirectory, fileName);
                    File.WriteAllBytes(temporaryPath, []);
                    picker = new UIDocumentPickerViewController(
                        [NSUrl.FromFilename(temporaryPath)],
                        asCopy: false);
                }

                picker.AllowsMultipleSelection = false;
                picker.ShouldShowFileExtensions = true;

                EventHandler<UIDocumentPickedAtUrlsEventArgs>? pickedMany = null;
                EventHandler<UIDocumentPickedEventArgs>? pickedOne = null;
                EventHandler? cancelled = null;

                void Finish(NSUrl? selectedUrl)
                {
                    picker.DidPickDocumentAtUrls -= pickedMany;
                    picker.DidPickDocument -= pickedOne;
                    picker.WasCancelled -= cancelled;
                    if (ReferenceEquals(_activePicker, picker))
                    {
                        _activePicker = null;
                        _activeCompletion = null;
                    }

                    string? path = selectedUrl?.Path;
                    if (!string.IsNullOrEmpty(path) && mode != OpenMode)
                    {
                        RetainSecurityScope(path, selectedUrl!);
                    }

                    CleanupTemporaryDirectory(temporaryDirectory, path);
                    completion.TrySetResult(string.IsNullOrEmpty(path) ? null : path);
                }

                pickedMany = (_, args) => Finish(args.Urls.FirstOrDefault());
                pickedOne = (_, args) => Finish(args.Url);
                cancelled = (_, _) => Finish(null);
                picker.DidPickDocumentAtUrls += pickedMany;
                picker.DidPickDocument += pickedOne;
                picker.WasCancelled += cancelled;

                _activePicker = picker;
                _activeCompletion = completion;
                GetPresenter(_owner).PresentViewController(picker, animated: true, completionHandler: null);
            }
            catch (Exception exception)
            {
                CleanupTemporaryDirectory(temporaryDirectory, selectedPath: null);
                _activePicker = null;
                _activeCompletion = null;
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private static UIViewController GetPresenter(UIViewController owner)
    {
        UIViewController presenter = owner;
        while (presenter.PresentedViewController is { } presented && !presented.IsBeingDismissed)
        {
            presenter = presented;
        }
        return presenter;
    }

    private static UTType[] ResolveContentTypes(IReadOnlyList<string>? fileTypes)
    {
        if (fileTypes == null || fileTypes.Count == 0) return [UTTypes.Item];

        var result = new List<UTType>(fileTypes.Count);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawType in fileTypes)
        {
            string extension = NormalizeExtension(rawType);
            if (extension.Length == 0 || extension == "*") return [UTTypes.Item];

            UTType? type = UTType.CreateFromExtension(extension);
            if (type != null && identifiers.Add(type.Identifier)) result.Add(type);
        }
        return result.Count == 0 ? [UTTypes.Item] : [.. result];
    }

    private static string ResolveSuggestedFileName(
        string? suggestedFileName,
        IReadOnlyList<string>? fileTypes)
    {
        string fileName = Path.GetFileName(suggestedFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "untitled";
        fileName = fileName.Replace('\0', '_');

        if (!Path.HasExtension(fileName) && fileTypes != null)
        {
            foreach (string rawType in fileTypes)
            {
                string extension = NormalizeExtension(rawType);
                if (extension.Length == 0 || extension == "*") continue;
                fileName += "." + extension;
                break;
            }
        }
        return fileName;
    }

    private static string NormalizeExtension(string? fileType)
    {
        string extension = fileType?.Trim() ?? string.Empty;
        if (extension is "*" or "*.*") return "*";
        if (extension.StartsWith("*.", StringComparison.Ordinal)) extension = extension[2..];
        else if (extension.StartsWith(".", StringComparison.Ordinal)) extension = extension[1..];
        return extension.Trim();
    }

    private void RetainSecurityScope(string path, NSUrl url)
    {
        if (!url.StartAccessingSecurityScopedResource()) return;
        lock (_leaseLock)
        {
            if (_securityScopedUrls.Remove(path, out NSUrl? previous))
            {
                previous.StopAccessingSecurityScopedResource();
                previous.Dispose();
            }
            _securityScopedUrls.Add(path, url);
        }
    }

    private bool CoordinateExternalWrite(string path, Action<string> writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writer);
        lock (_leaseLock)
        {
            if (!_securityScopedUrls.ContainsKey(path)) return false;
        }

        using var url = NSUrl.FromFilename(path);
        using var coordinator = new NSFileCoordinator(filePresenterOrNil: null);
        Exception? writeException = null;
        coordinator.CoordinateWrite(
            url,
            NSFileCoordinatorWritingOptions.ForReplacing,
            out NSError coordinationError,
            coordinatedUrl =>
            {
                try
                {
                    writer(coordinatedUrl.Path ?? path);
                }
                catch (Exception exception)
                {
                    writeException = exception;
                }
            });
        if (writeException != null) throw writeException;
        if (coordinationError != null) throw new NSErrorException(coordinationError);
        return true;
    }

    private static void CleanupTemporaryDirectory(string? directory, string? selectedPath)
    {
        if (string.IsNullOrEmpty(directory)) return;
        if (!string.IsNullOrEmpty(selectedPath) &&
            selectedPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The document provider may still be completing the move. The app's
            // temporary directory is reclaimed by iOS if immediate cleanup loses the race.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeCompletion?.TrySetResult(null);
        UIDocumentPickerViewController? activePicker = _activePicker;
        _activePicker = null;
        _activeCompletion = null;
        if (activePicker != null)
        {
            UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                activePicker.DismissViewController(animated: false, completionHandler: activePicker.Dispose);
            });
        }

        lock (_leaseLock)
        {
            foreach (NSUrl url in _securityScopedUrls.Values)
            {
                url.StopAccessingSecurityScopedResource();
                url.Dispose();
            }
            _securityScopedUrls.Clear();
        }
    }
}
