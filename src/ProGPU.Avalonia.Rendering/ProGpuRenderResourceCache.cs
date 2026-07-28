using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGpuVisual = ProGPU.Scene.Visual;
using RenderOptions = Avalonia.Media.RenderOptions;
#if !AVALONIA11
using TextOptions = Avalonia.Media.TextOptions;
#endif

namespace Avalonia.ProGpu;

/// <summary>
/// Bounded owner for per-target recording, style, picture, scene, readback,
/// and intermediate texture resources.
/// </summary>
internal sealed class OffscreenTextureCache : IDisposable
{
    private const int MaximumPictures = 2048;
    private const int MaximumStyles = 256;
    private const int MaximumDrawingStates = 4;

    private readonly object _recordingGate = new();
    private readonly object _resourceGate = new();
    private readonly bool _requireNativeCompositionScene;
    private readonly Dictionary<BrushKey, SolidColorBrush> _brushes = new();
    private readonly Dictionary<PenKey, Pen> _pens = new();
    private readonly Dictionary<long, PictureEntry> _pictures = new();
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    private readonly Dictionary<long, AvaloniaCompositionScene> _scenes = new();
#endif
    private DrawingContext? _spareRecordingContext;
    private readonly Stack<AvaloniaDrawingState> _drawingStates = new();
    private RecordedCommandVisual? _recordedVisual;
    private long _pictureHits;
    private long _pictureMisses;
    private long _pictureCompilations;
    private bool _disposed;

    public OffscreenTextureCache(
        bool requireNativeCompositionScene = false)
    {
        _requireNativeCompositionScene = requireNativeCompositionScene;
        WgpuContext.Disposing += OnContextDisposing;
    }

    public GpuTexture? CachedTexture;
    public GpuTextureReadbackBuffer? CachedReadbackBuffer;
    public uint CachedWidth;
    public uint CachedHeight;
    public bool IsTextureFresh = true;

    internal bool HasCachedReadbackBuffer =>
        CachedReadbackBuffer is not null;
    internal bool RequireNativeCompositionScene =>
        _requireNativeCompositionScene;
    internal long CompositionPictureHits =>
        Interlocked.Read(ref _pictureHits);
    internal long CompositionPictureMisses =>
        Interlocked.Read(ref _pictureMisses);
    internal long CompositionPictureCompilations =>
        Interlocked.Read(ref _pictureCompilations);

    internal int CompositionPictureCount
    {
        get
        {
            lock (_resourceGate)
                return _pictures.Count;
        }
    }

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    internal int CompositionSceneCount =>
        SumScenes(static scene => 1);
    internal int CompositionSceneNodeCount =>
        SumScenes(static scene => scene.NodeCount);
    internal int CompositionFallbackNodeCount =>
        SumScenes(static scene => scene.FallbackNodeCount);
    internal int CompositionCustomVisualNodeCount =>
        SumScenes(static scene => scene.CustomVisualNodeCount);
    internal long CompositionCustomVisualCompilations =>
        SumScenesLong(static scene => scene.CustomVisualCompilationCount);
    internal long CompositionSceneFullSynchronizations =>
        SumScenesLong(static scene => scene.FullSynchronizationCount);
    internal long CompositionSceneIncrementalSynchronizations =>
        SumScenesLong(static scene => scene.IncrementalSynchronizationCount);
    internal long CompositionTopologySynchronizations =>
        SumScenesLong(static scene => scene.TopologySynchronizationCount);
    internal long CompositionAdornerSynchronizations =>
        SumScenesLong(static scene => scene.AdornerSynchronizationCount);
    internal long CompositionSceneUnchangedReuses =>
        SumScenesLong(static scene => scene.UnchangedReuseCount);
    internal long CompositionLayoutClipSynchronizations =>
        SumScenesLong(static scene => scene.LayoutClipSynchronizationCount);
    internal long CompositionGeometryClipSynchronizations =>
        SumScenesLong(static scene => scene.GeometryClipSynchronizationCount);
    internal long CompositionBitmapCacheSynchronizations =>
        SumScenesLong(static scene => scene.BitmapCacheSynchronizationCount);
    internal long CompositionEffectSynchronizations =>
        SumScenesLong(static scene => scene.EffectSynchronizationCount);
    internal long CompositionOpacityMaskSynchronizations =>
        SumScenesLong(static scene => scene.OpacityMaskSynchronizationCount);
    internal long CompositionInheritedDrawingOptionsSynchronizations =>
        SumScenesLong(
            static scene =>
                scene.InheritedDrawingOptionsSynchronizationCount);
    internal long CompositionComplexAppearanceSynchronizations =>
        SumScenesLong(
            static scene => scene.ComplexAppearanceSynchronizationCount);
#else
    internal int CompositionSceneCount => 0;
    internal int CompositionSceneNodeCount => 0;
    internal int CompositionFallbackNodeCount => 0;
    internal int CompositionCustomVisualNodeCount => 0;
    internal long CompositionCustomVisualCompilations => 0;
    internal long CompositionSceneFullSynchronizations => 0;
    internal long CompositionSceneIncrementalSynchronizations => 0;
    internal long CompositionTopologySynchronizations => 0;
    internal long CompositionAdornerSynchronizations => 0;
    internal long CompositionSceneUnchangedReuses => 0;
    internal long CompositionLayoutClipSynchronizations => 0;
    internal long CompositionGeometryClipSynchronizations => 0;
    internal long CompositionBitmapCacheSynchronizations => 0;
    internal long CompositionEffectSynchronizations => 0;
    internal long CompositionOpacityMaskSynchronizations => 0;
    internal long CompositionInheritedDrawingOptionsSynchronizations => 0;
    internal long CompositionComplexAppearanceSynchronizations => 0;
#endif

    public DrawingContext RentRecordingContext()
    {
        lock (_recordingGate)
        {
            ThrowIfDisposed();
            DrawingContext context =
                _spareRecordingContext ?? new DrawingContext();
            _spareRecordingContext = null;
            return context;
        }
    }

    public void ReturnRecordingContext(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Clear();
        lock (_recordingGate)
        {
            if (_disposed || _spareRecordingContext is not null)
                return;
            _spareRecordingContext = context;
        }
    }

    internal AvaloniaDrawingState RentDrawingState()
    {
        lock (_recordingGate)
        {
            ThrowIfDisposed();
            return _drawingStates.Count > 0
                ? _drawingStates.Pop()
                : new AvaloniaDrawingState();
        }
    }

    internal void ReturnDrawingState(AvaloniaDrawingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Clear();
        lock (_recordingGate)
        {
            if (_disposed ||
                _drawingStates.Count >= MaximumDrawingStates ||
                !state.CanRetain)
            {
                return;
            }
            _drawingStates.Push(state);
        }
    }

    internal int DrawingStatePoolCount
    {
        get
        {
            lock (_recordingGate)
                return _drawingStates.Count;
        }
    }

    internal ProGpuVisual GetOrUpdateRecordedVisual(
        DrawingContext context,
        Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_recordedVisual is null ||
            !ReferenceEquals(_recordedVisual.Commands, context))
        {
            _recordedVisual = new RecordedCommandVisual(context);
        }

        _recordedVisual.Size = size;
        _recordedVisual.UpdateSnapshot();
        return _recordedVisual;
    }

    internal bool TryGetCompositionPicture(
        long id,
        ulong revision,
        out GpuPicture? picture)
    {
        lock (_resourceGate)
        {
            if (_pictures.TryGetValue(id, out PictureEntry entry) &&
                entry.Revision == revision)
            {
                Interlocked.Increment(ref _pictureHits);
                picture = entry.Picture;
                return true;
            }
        }

        Interlocked.Increment(ref _pictureMisses);
        picture = null;
        return false;
    }

    internal void StoreCompositionPicture(
        long id,
        ulong revision,
        GpuPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        lock (_resourceGate)
        {
            ThrowIfDisposed();
            if (_pictures.Remove(id, out PictureEntry replaced))
                replaced.Picture.Dispose();

            if (_pictures.Count >= MaximumPictures)
            {
                ClearPictures();
            }

            _pictures.Add(id, new PictureEntry(revision, picture));
            Interlocked.Increment(ref _pictureCompilations);
        }
    }

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    internal AvaloniaCompositionScene GetOrCreateCompositionScene(
        long targetId)
    {
        lock (_resourceGate)
        {
            ThrowIfDisposed();
            if (!_scenes.TryGetValue(targetId, out var scene))
            {
                scene = new AvaloniaCompositionScene(
                    _requireNativeCompositionScene);
                _scenes.Add(targetId, scene);
            }
            return scene;
        }
    }

    internal void RemoveCompositionScene(long targetId)
    {
        lock (_resourceGate)
        {
            if (_scenes.Remove(targetId, out var scene))
                scene.Dispose();
        }
    }
#endif

    public SolidColorBrush GetSolidBrush(
        byte red,
        byte green,
        byte blue,
        byte alpha,
        float opacity)
    {
        var key = new BrushKey(PackColor(red, green, blue, alpha), opacity);
        lock (_resourceGate)
        {
            ThrowIfDisposed();
            if (_brushes.TryGetValue(key, out SolidColorBrush? brush))
                return brush;
            TrimStylesIfNeeded();
            brush = new SolidColorBrush(new Vector4(
                red / 255f,
                green / 255f,
                blue / 255f,
                alpha / 255f))
            {
                Opacity = opacity
            };
            _brushes.Add(key, brush);
            return brush;
        }
    }

    public Pen GetSolidPen(
        byte red,
        byte green,
        byte blue,
        byte alpha,
        float opacity,
        float thickness,
        PenLineJoin lineJoin,
        float miterLimit,
        PenLineCap lineCap)
    {
        var brushKey =
            new BrushKey(PackColor(red, green, blue, alpha), opacity);
        var key =
            new PenKey(brushKey, thickness, lineJoin, miterLimit, lineCap);
        lock (_resourceGate)
        {
            ThrowIfDisposed();
            if (_pens.TryGetValue(key, out Pen? pen))
                return pen;
            TrimStylesIfNeeded();
            SolidColorBrush brush = GetSolidBrush(
                red,
                green,
                blue,
                alpha,
                opacity);
            pen = new Pen(
                brush,
                thickness,
                lineJoin,
                miterLimit,
                lineCap,
                lineCap,
                lineCap);
            _pens.Add(key, pen);
            return pen;
        }
    }

    public void Invalidate(WgpuContext? context)
    {
        CachedTexture?.Dispose();
        CachedTexture = null;
        CachedReadbackBuffer?.Dispose();
        CachedReadbackBuffer = null;
        CachedWidth = 0;
        CachedHeight = 0;
        IsTextureFresh = true;
        lock (_resourceGate)
        {
            ClearPictures();
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
            ClearScenes();
#endif
        }
    }

    public void Dispose()
    {
        WgpuContext.Disposing -= OnContextDisposing;
        lock (_recordingGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _spareRecordingContext?.Clear();
            _spareRecordingContext = null;
            _drawingStates.Clear();
            _recordedVisual = null;
        }

        Invalidate(CachedTexture?.Context);
        lock (_resourceGate)
        {
            _brushes.Clear();
            _pens.Clear();
        }
    }

    private void OnContextDisposing(WgpuContext context)
    {
        if (CachedTexture is null ||
            ReferenceEquals(CachedTexture.Context, context))
        {
            Invalidate(context);
        }
    }

    private void TrimStylesIfNeeded()
    {
        if (_brushes.Count + _pens.Count < MaximumStyles)
            return;
        _brushes.Clear();
        _pens.Clear();
    }

    private void ClearPictures()
    {
        foreach (PictureEntry entry in _pictures.Values)
            entry.Picture.Dispose();
        _pictures.Clear();
    }

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    private void ClearScenes()
    {
        foreach (AvaloniaCompositionScene scene in _scenes.Values)
            scene.Dispose();
        _scenes.Clear();
    }

    private int SumScenes(Func<AvaloniaCompositionScene, int> selector)
    {
        lock (_resourceGate)
        {
            int result = 0;
            foreach (AvaloniaCompositionScene scene in _scenes.Values)
                result += selector(scene);
            return result;
        }
    }

    private long SumScenesLong(
        Func<AvaloniaCompositionScene, long> selector)
    {
        lock (_resourceGate)
        {
            long result = 0;
            foreach (AvaloniaCompositionScene scene in _scenes.Values)
                result += selector(scene);
            return result;
        }
    }
#endif

    private static uint PackColor(
        byte red,
        byte green,
        byte blue,
        byte alpha) =>
        (uint)red << 24 |
        (uint)green << 16 |
        (uint)blue << 8 |
        alpha;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class RecordedCommandVisual :
        ProGpuVisual,
        IOwnedRenderCommandCache
    {
        private RectangleCommandSnapshot[] _snapshot = [];
        private int _snapshotCount;
        private bool _hasSupportedSnapshot;

        public RecordedCommandVisual(DrawingContext commands)
        {
            Commands = commands;
        }

        public DrawingContext Commands { get; }

        public void UpdateSnapshot()
        {
            List<RenderCommand> commands = Commands.Commands;
            int count = commands.Count;
            if (_snapshot.Length < count)
            {
                int capacity = Math.Max(8, _snapshot.Length);
                while (capacity < count)
                    capacity *= 2;
                Array.Resize(ref _snapshot, capacity);
            }

            bool supported = true;
            bool changed =
                !_hasSupportedSnapshot ||
                _snapshotCount != count;
            for (int index = 0; index < count; index++)
            {
                RenderCommand command = commands[index];
                if (command.Type != RenderCommandType.DrawRect ||
                    command.Pen is not null ||
                    command.Brush is not SolidColorBrush brush)
                {
                    supported = false;
                    break;
                }

                var next = new RectangleCommandSnapshot(
                    command.Rect,
                    command.Transform,
                    brush.Color,
                    brush.Opacity,
                    command.HitTestId);
                if (!changed && _snapshot[index] != next)
                    changed = true;
                _snapshot[index] = next;
            }

            if (!supported || changed)
                Invalidate();
            _hasSupportedSnapshot = supported;
            _snapshotCount = supported ? count : 0;
        }

        public DrawingContext GetOrUpdateRenderCommandCache() => Commands;
    }

    private readonly record struct RectangleCommandSnapshot(
        ProGPU.Scene.Rect Bounds,
        Matrix4x4 Transform,
        Vector4 Color,
        float Opacity,
        int HitTestId);

    private readonly record struct BrushKey(uint Color, float Opacity);

    private readonly record struct PenKey(
        BrushKey Brush,
        float Thickness,
        PenLineJoin Join,
        float MiterLimit,
        PenLineCap Cap);

    private readonly record struct PictureEntry(
        ulong Revision,
        GpuPicture Picture);
}

internal sealed class AvaloniaDrawingState
{
    private const int MaximumRetainedCapacity = 64;

    internal readonly Stack<double> OpacityFrames = new();
    internal readonly Stack<bool> GeometryClipFrames = new();
    internal readonly Stack<RenderOptions> RenderOptionFrames = new();
    internal readonly Stack<RenderCommandPresentationDependencies>
        RenderOptionDependencyFrames = new();
#if !AVALONIA11
    internal readonly Stack<TextOptions> TextOptionFrames = new();
    internal readonly Stack<RenderCommandPresentationDependencies>
        TextOptionDependencyFrames = new();
#endif

    internal bool CanRetain =>
        OpacityFrames.EnsureCapacity(0) <= MaximumRetainedCapacity &&
        GeometryClipFrames.EnsureCapacity(0) <= MaximumRetainedCapacity &&
        RenderOptionFrames.EnsureCapacity(0) <= MaximumRetainedCapacity &&
        RenderOptionDependencyFrames.EnsureCapacity(0) <= MaximumRetainedCapacity
#if !AVALONIA11
        && TextOptionFrames.EnsureCapacity(0) <= MaximumRetainedCapacity
        && TextOptionDependencyFrames.EnsureCapacity(0) <= MaximumRetainedCapacity
#endif
        ;

    internal void Clear()
    {
        OpacityFrames.Clear();
        GeometryClipFrames.Clear();
        RenderOptionFrames.Clear();
        RenderOptionDependencyFrames.Clear();
#if !AVALONIA11
        TextOptionFrames.Clear();
        TextOptionDependencyFrames.Clear();
#endif
    }
}
