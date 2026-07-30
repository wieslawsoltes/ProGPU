using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using ProGPU.Media.Editing;
using Windows.Storage;

namespace ProGPU.Browser;

/// <summary>
/// Browser commit adapter for the dependency-free ISO-BMFF fast editor.
/// Compatible H.264/AAC access units remain compressed; the completed MP4 is
/// staged in the browser virtual file system and committed through the typed
/// browser file-handle seam as one transactional write.
/// </summary>
public sealed partial class BrowserFastMediaCompositionExportProvider :
    IMediaCompositionExportProvider,
    IMediaCompositionExportCapabilityProvider
{
    private const int MaximumStagedSourceBytes =
        512 * 1024 * 1024;
    private static int s_nextStagingId;
    private readonly IsoBmffFastMediaCompositionExportProvider
        _inner;

    public BrowserFastMediaCompositionExportProvider(
        int priority = 100)
    {
        Priority = priority;
        _inner =
            new IsoBmffFastMediaCompositionExportProvider(
                priority);
    }

    public string Id =>
        "progpu.browser.isobmff.fast-export";

    public int Priority { get; }

    public bool CanRender(
        MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _inner.CanRender(request);
    }

    public MediaCompositionExportCapabilities GetCapabilities(
        MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanRender(request))
        {
            throw new ArgumentException(
                "The request is not supported by this provider.",
                nameof(request));
        }

        return new MediaCompositionExportCapabilities(
            Id,
            MediaCompositionExportVideoPath.CompressedSampleCopy,
            request.EncodingProfile.AudioSubtype is null
                ? MediaCompositionExportAudioPath.None
                : MediaCompositionExportAudioPath
                    .CompressedSampleCopy,
            HardwareVideoEncoderRequested: false,
            HardwareVideoEncoderGuaranteed: false,
            EffectsBakedOnGpu: false,
            Limitation:
                "Compatible H.264/AAC samples are remuxed without decode " +
                "or encode. The completed compressed container is copied " +
                "once through browser-managed storage to commit the save.");
    }

    public async ValueTask<MediaCompositionExportFailure>
        RenderAsync(
            MediaCompositionExportRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request))
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }

        string stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"progpu-browser-export-{Guid.NewGuid():N}");
        string stagingPath = Path.Combine(
            stagingDirectory,
            Path.GetFileName(request.DestinationPath));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            MediaCompositionExportRequest seekableRequest =
                await StageRemoteSourcesAsync(
                        request,
                        stagingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
            var scaledProgress =
                progress is null
                    ? null
                    : new ScaledProgress(progress);
            MediaCompositionExportFailure result =
                await _inner.RenderAsync(
                        seekableRequest with
                        {
                            DestinationPath =
                                stagingPath
                        },
                        scaledProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (result !=
                MediaCompositionExportFailure.None)
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes =
                await File.ReadAllBytesAsync(
                        stagingPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var destination =
                new StorageFile(
                    request.DestinationPath);
            await destination.WriteBytesAsync(bytes)
                .ConfigureAwait(false);
            progress?.Report(100d);
            return MediaCompositionExportFailure.None;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return MediaCompositionExportFailure.Unknown;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static async Task<MediaCompositionExportRequest>
        StageRemoteSourcesAsync(
            MediaCompositionExportRequest request,
            string stagingDirectory,
            CancellationToken cancellationToken)
    {
        if (!request.Clips.Any(
                static clip =>
                    clip.SourceUri is
                        { IsFile: false }))
        {
            return request;
        }

        var staged =
            new Dictionary<string, Uri>(
                StringComparer.Ordinal);
        var clips =
            new MediaCompositionExportClip[
                request.Clips.Count];
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaCompositionExportClip clip =
                request.Clips[index];
            Uri source = clip.SourceUri!;
            if (source.IsFile)
            {
                clips[index] = clip;
                continue;
            }

            if (!staged.TryGetValue(
                    source.AbsoluteUri,
                    out Uri? stagedUri))
            {
                int stagingId = Interlocked.Increment(
                    ref s_nextStagingId);
                try
                {
                    using CancellationTokenRegistration
                        cancellation =
                            cancellationToken.Register(
                                static id =>
                                    CancelStagedSourceCore(
                                        (int)id!),
                                stagingId);
                    Task<int> completion =
                        BrowserMediaStagingCallbacks.Register(
                            stagingId);
                    int length;
                    try
                    {
                        StartStageSourceCore(
                            stagingId,
                            source.AbsoluteUri,
                            MaximumStagedSourceBytes);
                        length = await completion
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        BrowserMediaStagingCallbacks
                            .Unregister(stagingId);
                    }
                    if (length < 0 ||
                        length >
                            MaximumStagedSourceBytes)
                    {
                        throw new InvalidDataException(
                            "The browser media source exceeds the bounded staging limit.");
                    }

                    byte[] bytes = new byte[length];
                    if (length != 0)
                    {
                        unsafe
                        {
                            fixed (byte* destination =
                                       bytes)
                            {
                                int copied =
                                    CopyStagedSourceCore(
                                        stagingId,
                                        (nint)
                                            destination,
                                        length);
                                if (copied != length)
                                {
                                    throw new IOException(
                                        $"The browser staged {length} media bytes but copied {copied}.");
                                }
                            }
                        }
                    }

                    string path = Path.Combine(
                        stagingDirectory,
                        $"source-{staged.Count:D4}.mp4");
                    await File.WriteAllBytesAsync(
                            path,
                            bytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    stagedUri = new Uri(path);
                    staged.Add(
                        source.AbsoluteUri,
                        stagedUri);
                }
                finally
                {
                    ClearStagedSourceCore(stagingId);
                }
            }

            clips[index] = clip with
            {
                SourceUri = stagedUri
            };
        }

        return request with
        {
            Clips = Array.AsReadOnly(clips)
        };
    }

    private static void TryDeleteDirectory(
        string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup must not hide the export result.
        }
    }

    private sealed class ScaledProgress(
        IProgress<double> target) :
        IProgress<double>
    {
        public void Report(double value) =>
            target.Report(
                Math.Clamp(value, 0d, 100d) *
                0.99d);
    }

    [JSImport(
        "startStageBrowserMediaSource",
        "progpu-browser")]
    private static partial void StartStageSourceCore(
        int stagingId,
        string uri,
        int maximumBytes);

    [JSImport(
        "copyStagedBrowserMediaSource",
        "progpu-browser")]
    private static partial int CopyStagedSourceCore(
        int stagingId,
        nint destination,
        int length);

    [JSImport(
        "clearStagedBrowserMediaSource",
        "progpu-browser")]
    private static partial void ClearStagedSourceCore(
        int stagingId);

    [JSImport(
        "cancelStagedBrowserMediaSource",
        "progpu-browser")]
    private static partial void CancelStagedSourceCore(
        int stagingId);
}

public static partial class BrowserMediaStagingCallbacks
{
    private static readonly ConcurrentDictionary<
        int,
        TaskCompletionSource<int>> s_pending = new();

    internal static Task<int> Register(
        int stagingId)
    {
        var completion =
            new TaskCompletionSource<int>();
        if (!s_pending.TryAdd(
                stagingId,
                completion))
        {
            throw new InvalidOperationException(
                $"Browser media staging operation {stagingId} is already registered.");
        }
        return completion.Task;
    }

    internal static void Unregister(
        int stagingId) =>
        s_pending.TryRemove(stagingId, out _);

    [JSExport]
    public static void DispatchCompletion(
        int stagingId,
        int length)
    {
        if (s_pending.TryGetValue(
                stagingId,
                out TaskCompletionSource<int>?
                    completion))
        {
            completion.TrySetResult(length);
        }
    }
}
