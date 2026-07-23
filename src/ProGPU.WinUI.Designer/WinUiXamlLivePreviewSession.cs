using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.HotReload;
using ProGPU.Xaml.Workspaces;

namespace ProGPU.WinUI.Designer;

public sealed class WinUiXamlLivePreviewResult
{
    internal WinUiXamlLivePreviewResult(
        bool success,
        string message,
        FrameworkElement? root)
    {
        Success = success;
        Message = message;
        Root = root;
    }

    public bool Success { get; }
    public string Message { get; }
    public FrameworkElement? Root { get; }
}

/// <summary>
/// Owns the framework-specific, tooling-only dynamic assembly boundary for a WinUI XAML
/// preview. Callers must obtain explicit user permission before submitting an artifact.
/// A candidate is fully loaded and constructed before the previous tree is replaced.
/// </summary>
public sealed class WinUiXamlLivePreviewSession : IDisposable
{
    private PreviewAssemblyLoadContext? _loadContext;
    private FrameworkElement? _currentRoot;
    private bool _disposed;

    public static bool IsRuntimeSupported =>
        RuntimeFeature.IsDynamicCodeSupported &&
        RuntimeFeature.IsDynamicCodeCompiled;

    public static string RuntimeSupportMessage => IsRuntimeSupported
        ? "Dynamic preview loading is available."
        : "Dynamic preview loading is unavailable on this runtime. Syntax, semantic, IR, and generated-code inspection remain available.";

    public FrameworkElement? CurrentRoot => _currentRoot;

    /// <summary>
    /// Loads and constructs a candidate, then publishes it through the canonical hot-reload
    /// state-transfer pipeline. A load, activation, or replacement failure retains the
    /// previously published root.
    /// </summary>
    public WinUiXamlLivePreviewResult TryUpdate(
        byte[] peImage,
        string qualifiedTypeName,
        Action<FrameworkElement> publish) =>
        TryUpdateCore(
            peImage,
            qualifiedTypeName,
            publish,
            beforePublish: null);

    /// <summary>
    /// Applies an accepted framework-neutral project delta. Metadata coordination runs
    /// only after the candidate assembly has loaded, activated, and passed its typed-root
    /// check, but before canonical state transfer commits the replacement.
    /// </summary>
    public WinUiXamlLivePreviewResult TryApplyProjectDelta(
        RoslynXamlProjectDeltaPlan plan,
        Action<FrameworkElement> publish,
        Action? coordinateMetadataUpdate = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(publish);
        switch (plan.Action)
        {
            case RoslynXamlReloadAction.None:
                return new WinUiXamlLivePreviewResult(
                    success: true,
                    "The accepted project delta does not affect the published preview root.",
                    _currentRoot);
            case RoslynXamlReloadAction.RetainLastGood:
                return Failure(
                    plan.FailureMessage ??
                    "The project delta was rejected; the last good tree was retained.");
            case RoslynXamlReloadAction
                .CoordinateMetadataAndReplaceTarget
                when coordinateMetadataUpdate == null:
                return Failure(
                    "The project delta requires a metadata-update coordinator; the last good tree was retained.");
        }

        if (!plan.TryGetExecutableUpdate(
                out var peImage,
                out var typeName))
        {
            return Failure(
                "The project delta has no accepted executable artifact; the last good tree was retained.");
        }

        return TryUpdateCore(
            peImage,
            typeName,
            publish,
            plan.Action ==
            RoslynXamlReloadAction
                .CoordinateMetadataAndReplaceTarget
                ? coordinateMetadataUpdate
                : null);
    }

    private WinUiXamlLivePreviewResult TryUpdateCore(
        byte[] peImage,
        string qualifiedTypeName,
        Action<FrameworkElement> publish,
        Action? beforePublish)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(peImage);
        if (string.IsNullOrWhiteSpace(qualifiedTypeName))
            throw new ArgumentException(
                "A qualified preview type name is required.",
                nameof(qualifiedTypeName));
        ArgumentNullException.ThrowIfNull(publish);
        if (!IsRuntimeSupported)
            return Failure(RuntimeSupportMessage);
        if (peImage.Length == 0)
            return Failure("The preview assembly image is empty.");

        PreviewCandidate candidate;
        try
        {
            candidate = Materialize(peImage, qualifiedTypeName);
        }
        catch (Exception exception)
        {
            return Failure(
                "Preview activation failed; the last good tree was retained. " +
                exception.GetBaseException().Message);
        }

        var previousContext = _loadContext;
        var previousRoot = _currentRoot;
        var published = false;
        try
        {
            beforePublish?.Invoke();
            published = previousRoot == null
                ? PublishFirst(candidate.Root, publish)
                : HotReloadManager.ReloadElement(
                    previousRoot,
                    () => candidate.Root,
                    publish);
            if (!published)
            {
                candidate.Context.Unload();
                return Failure(
                    "Preview replacement failed; the last good tree was retained.");
            }

            _loadContext = candidate.Context;
            _currentRoot = candidate.Root;
            previousContext?.Unload();
            return new WinUiXamlLivePreviewResult(
                success: true,
                "Preview updated from the accepted Roslyn-generated program.",
                candidate.Root);
        }
        catch (Exception exception)
        {
            if (!published)
                candidate.Context.Unload();
            return Failure(
                "Preview publication failed; the last good tree was retained. " +
                exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Releases the session's collectible context after the caller has detached
    /// <see cref="CurrentRoot"/> from its visual host.
    /// </summary>
    public void Reset()
    {
        _currentRoot = null;
        var context = _loadContext;
        _loadContext = null;
        context?.Unload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    private static bool PublishFirst(
        FrameworkElement root,
        Action<FrameworkElement> publish)
    {
        publish(root);
        root.InvalidateMeasure();
        root.InvalidateArrange();
        root.Invalidate();
        return true;
    }

    private static PreviewCandidate Materialize(
        byte[] peImage,
        string qualifiedTypeName)
    {
        var context = new PreviewAssemblyLoadContext();
        try
        {
            using var stream = new MemoryStream(
                peImage,
                writable: false);
            var assembly = context.LoadFromStream(stream);

            // This is the single tooling-only reflection seam. The compiler, generated
            // program, runtime property access, and normal hot-reload path remain typed
            // and reflection-free. Dynamic-code checks close this path on AOT runtimes.
            var type = assembly.GetType(
                qualifiedTypeName,
                throwOnError: true,
                ignoreCase: false)!;
            var instance = Activator.CreateInstance(type);
            if (instance is not FrameworkElement root)
            {
                throw new InvalidOperationException(
                    $"Preview type '{qualifiedTypeName}' does not construct a WinUI FrameworkElement.");
            }

            return new PreviewCandidate(context, root);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private WinUiXamlLivePreviewResult Failure(string message) =>
        new WinUiXamlLivePreviewResult(
            success: false,
            message,
            _currentRoot);

    private sealed class PreviewAssemblyLoadContext : AssemblyLoadContext
    {
        public PreviewAssemblyLoadContext()
            : base(isCollectible: true)
        {
        }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            null;
    }

    private sealed class PreviewCandidate
    {
        public PreviewCandidate(
            PreviewAssemblyLoadContext context,
            FrameworkElement root)
        {
            Context = context;
            Root = root;
        }

        public PreviewAssemblyLoadContext Context { get; }
        public FrameworkElement Root { get; }
    }
}
