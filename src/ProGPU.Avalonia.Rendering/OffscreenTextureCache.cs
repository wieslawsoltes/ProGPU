using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGpuRect = ProGPU.Scene.Rect;
using ProGpuVisual = ProGPU.Scene.Visual;

namespace Avalonia.ProGpu
{
    internal class OffscreenTextureCache : IDisposable
    {
        private const int MaximumRetainedCompositionPictures = 2048;
        private const int MaximumSolidStyleCacheEntries = 256;
        private readonly object _compositionPictureLock = new();
        private readonly bool _requireNativeCompositionScene;
        public GpuTexture? CachedTexture;
        public GpuTextureReadbackBuffer? CachedReadbackBuffer;
        public uint CachedWidth;
        public uint CachedHeight;
        public bool IsTextureFresh = true;
        internal bool HasCachedReadbackBuffer =>
            CachedReadbackBuffer != null;
        private readonly object _recordingContextLock = new();
        private readonly object _solidStyleCacheLock = new();
        private readonly Dictionary<SolidBrushKey, SolidColorBrush> _solidBrushes = new();
        private readonly Dictionary<SolidPenKey, Pen> _solidPens = new();
        private readonly Dictionary<long, RetainedCompositionPicture> _compositionPictures = new();
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        private readonly Dictionary<long, AvaloniaCompositionScene> _compositionScenes = new();
#endif
        private DrawingContext? _recordingContext;
        private long _compositionPictureHits;
        private long _compositionPictureMisses;
        private long _compositionPictureCompilations;
        private RetainedRecordedVisual? _recordedVisual;
        private bool _disposed;

        private sealed record RetainedCompositionPicture(ulong Revision, GpuPicture Picture);

        private readonly record struct SolidBrushKey(uint Color, float Opacity);

        private readonly record struct SolidPenKey(
            SolidBrushKey Brush,
            float Thickness,
            PenLineJoin LineJoin,
            float MiterLimit,
            PenLineCap LineCap);

        public OffscreenTextureCache(
            bool requireNativeCompositionScene = false)
        {
            _requireNativeCompositionScene = requireNativeCompositionScene;
            WgpuContext.Disposing += OnContextDisposing;
        }

        internal int CompositionPictureCount
        {
            get
            {
                lock (_compositionPictureLock)
                    return _compositionPictures.Count;
            }
        }

        internal bool RequireNativeCompositionScene =>
            _requireNativeCompositionScene;

        internal long CompositionPictureHits => Interlocked.Read(ref _compositionPictureHits);
        internal long CompositionPictureMisses => Interlocked.Read(ref _compositionPictureMisses);
        internal long CompositionPictureCompilations => Interlocked.Read(ref _compositionPictureCompilations);
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        internal int CompositionSceneCount
        {
            get
            {
                lock (_compositionPictureLock)
                    return _compositionScenes.Count;
            }
        }

        internal int CompositionSceneNodeCount =>
            SumCompositionScene(static scene => scene.NodeCount);
        internal int CompositionFallbackNodeCount =>
            SumCompositionScene(static scene => scene.FallbackNodeCount);
        internal int CompositionCustomVisualNodeCount =>
            SumCompositionScene(static scene => scene.CustomVisualNodeCount);
        internal long CompositionCustomVisualCompilations =>
            SumCompositionSceneLong(
                static scene => scene.CustomVisualCompilationCount);
        internal long CompositionSceneFullSynchronizations =>
            SumCompositionSceneLong(static scene => scene.FullSynchronizationCount);
        internal long CompositionSceneIncrementalSynchronizations =>
            SumCompositionSceneLong(static scene => scene.IncrementalSynchronizationCount);
        internal long CompositionSceneUnchangedReuses =>
            SumCompositionSceneLong(static scene => scene.UnchangedReuseCount);

        private int SumCompositionScene(Func<AvaloniaCompositionScene, int> selector)
        {
            lock (_compositionPictureLock)
            {
                int total = 0;
                foreach (AvaloniaCompositionScene scene in _compositionScenes.Values)
                    total += selector(scene);
                return total;
            }
        }

        private long SumCompositionSceneLong(Func<AvaloniaCompositionScene, long> selector)
        {
            lock (_compositionPictureLock)
            {
                long total = 0;
                foreach (AvaloniaCompositionScene scene in _compositionScenes.Values)
                    total += selector(scene);
                return total;
            }
        }
#endif

        private void OnContextDisposing(WgpuContext context)
        {
            if (CachedTexture?.Context == context)
            {
                Invalidate(context);
            }
        }

        public DrawingContext RentRecordingContext()
        {
            lock (_recordingContextLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var context = _recordingContext;
                _recordingContext = null;
                return context ?? new DrawingContext();
            }
        }

        internal ProGpuVisual GetOrUpdateRecordedVisual(
            DrawingContext context,
            Vector2 size)
        {
            if (_recordedVisual == null ||
                !ReferenceEquals(_recordedVisual.Context, context))
            {
                _recordedVisual = new RetainedRecordedVisual(context);
            }

            _recordedVisual.Size = size;
            _recordedVisual.UpdateCommandSnapshot();
            return _recordedVisual;
        }

        internal bool TryGetCompositionPicture(long id, ulong revision, out GpuPicture? picture)
        {
            lock (_compositionPictureLock)
            {
                if (_compositionPictures.TryGetValue(id, out var cached) &&
                    cached.Revision == revision)
                {
                    Interlocked.Increment(ref _compositionPictureHits);
                    picture = cached.Picture;
                    return true;
                }
            }

            Interlocked.Increment(ref _compositionPictureMisses);
            picture = null;
            return false;
        }

        internal void StoreCompositionPicture(long id, ulong revision, GpuPicture picture)
        {
            ArgumentNullException.ThrowIfNull(picture);

            lock (_compositionPictureLock)
            {
                if (_compositionPictures.Remove(id, out var replaced))
                    replaced.Picture.Dispose();

                if (_compositionPictures.Count >= MaximumRetainedCompositionPictures)
                    ClearCompositionPictures();

                _compositionPictures.Add(id, new RetainedCompositionPicture(revision, picture));
                Interlocked.Increment(ref _compositionPictureCompilations);
            }
        }

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        internal AvaloniaCompositionScene GetOrCreateCompositionScene(long targetId)
        {
            lock (_compositionPictureLock)
            {
                if (!_compositionScenes.TryGetValue(targetId, out AvaloniaCompositionScene? scene))
                {
                    scene = new AvaloniaCompositionScene(
                        _requireNativeCompositionScene);
                    _compositionScenes.Add(targetId, scene);
                }

                return scene;
            }
        }

        internal void RemoveCompositionScene(long targetId)
        {
            lock (_compositionPictureLock)
            {
                if (_compositionScenes.Remove(targetId, out AvaloniaCompositionScene? scene))
                    scene.Dispose();
            }
        }

        private void ClearCompositionScenes()
        {
            foreach (AvaloniaCompositionScene scene in _compositionScenes.Values)
                scene.Dispose();
            _compositionScenes.Clear();
        }
#endif

        private void ClearCompositionPictures()
        {
            foreach (var cached in _compositionPictures.Values)
                cached.Picture.Dispose();
            _compositionPictures.Clear();
        }

        public void ReturnRecordingContext(DrawingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Clear();

            lock (_recordingContextLock)
            {
                if (_disposed || _recordingContext != null)
                {
                    return;
                }

                _recordingContext = context;
            }
        }

        public SolidColorBrush GetSolidBrush(byte red, byte green, byte blue, byte alpha, float opacity)
        {
            var key = new SolidBrushKey(PackColor(red, green, blue, alpha), opacity);
            lock (_solidStyleCacheLock)
            {
                if (_solidBrushes.TryGetValue(key, out var brush))
                {
                    return brush;
                }

                if (_solidBrushes.Count >= MaximumSolidStyleCacheEntries)
                {
                    _solidBrushes.Clear();
                }

                brush = new SolidColorBrush(new Vector4(
                    red / 255.0f,
                    green / 255.0f,
                    blue / 255.0f,
                    alpha / 255.0f))
                {
                    Opacity = opacity
                };
                _solidBrushes.Add(key, brush);
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
            var brushKey = new SolidBrushKey(PackColor(red, green, blue, alpha), opacity);
            var key = new SolidPenKey(brushKey, thickness, lineJoin, miterLimit, lineCap);
            lock (_solidStyleCacheLock)
            {
                if (_solidPens.TryGetValue(key, out var pen))
                {
                    return pen;
                }

                if (_solidPens.Count >= MaximumSolidStyleCacheEntries)
                {
                    _solidPens.Clear();
                }

                var brush = GetSolidBrush(red, green, blue, alpha, opacity);
                pen = new Pen(
                    brush,
                    thickness,
                    lineJoin,
                    miterLimit,
                    lineCap,
                    lineCap,
                    lineCap);
                _solidPens.Add(key, pen);
                return pen;
            }
        }

        private static uint PackColor(byte red, byte green, byte blue, byte alpha) =>
            ((uint)red << 24) | ((uint)green << 16) | ((uint)blue << 8) | alpha;

        public void Invalidate(WgpuContext? context)
        {
            if (CachedTexture != null)
            {
                CachedTexture.Dispose();
                CachedTexture = null;
            }
            CachedReadbackBuffer?.Dispose();
            CachedReadbackBuffer = null;
            CachedWidth = 0;
            CachedHeight = 0;
            IsTextureFresh = true;

            lock (_compositionPictureLock)
            {
                ClearCompositionPictures();
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
                ClearCompositionScenes();
#endif
            }
        }

        public void Dispose()
        {
            WgpuContext.Disposing -= OnContextDisposing;
            var context = CachedTexture?.Context ?? WgpuContext.Current;
            Invalidate(context);

            lock (_recordingContextLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _recordingContext?.Clear();
                _recordingContext = null;
                _recordedVisual = null;
            }

            lock (_solidStyleCacheLock)
            {
                _solidBrushes.Clear();
                _solidPens.Clear();
            }
        }

        private sealed class RetainedRecordedVisual : ProGpuVisual, IOwnedRenderCommandCache
        {
            private HostCommandKey[] _snapshot = Array.Empty<HostCommandKey>();
            private int _snapshotCount;
            private bool _snapshotSupported;

            internal RetainedRecordedVisual(DrawingContext context)
            {
                Context = context;
            }

            internal DrawingContext Context { get; }

            internal void UpdateCommandSnapshot()
            {
                var commands = Context.Commands;
                int count = commands.Count;
                bool supported = EnsureSnapshotCapacity(count);
                bool changed = !_snapshotSupported || _snapshotCount != count;
                for (int index = 0; supported && index < count; index++)
                {
                    if (!HostCommandKey.TryCreate(commands[index], out HostCommandKey key))
                    {
                        supported = false;
                        break;
                    }

                    if (!changed && _snapshot[index] != key)
                        changed = true;
                    _snapshot[index] = key;
                }

                if (!supported || changed)
                    Invalidate();

                _snapshotSupported = supported;
                _snapshotCount = supported ? count : 0;
            }

            private bool EnsureSnapshotCapacity(int count)
            {
                if (_snapshot.Length >= count)
                    return true;
                int capacity = Math.Max(8, _snapshot.Length);
                while (capacity < count)
                    capacity *= 2;
                Array.Resize(ref _snapshot, capacity);
                return true;
            }

            public DrawingContext GetOrUpdateRenderCommandCache() => Context;
        }

        private readonly record struct HostCommandKey(
            RenderCommandType Type,
            ProGpuRect Rect,
            Matrix4x4 Transform,
            ProGpuVisual? Visual,
            Vector4 Color,
            float Opacity,
            int Integer)
        {
            internal static bool TryCreate(
                RenderCommand command,
                out HostCommandKey key)
            {
                switch (command.Type)
                {
                    case RenderCommandType.DrawRect
                        when command.Pen == null &&
                             command.Brush is SolidColorBrush brush:
                        key = new HostCommandKey(
                            command.Type,
                            command.Rect,
                            command.Transform,
                            null,
                            brush.Color,
                            brush.Opacity,
                            0);
                        return true;
                    case RenderCommandType.DrawVisual:
                        key = new HostCommandKey(
                            command.Type,
                            default,
                            command.Transform,
                            command.Visual,
                            default,
                            0f,
                            0);
                        return true;
                    case RenderCommandType.PushClip:
                        key = new HostCommandKey(
                            command.Type,
                            command.Rect,
                            command.Transform,
                            null,
                            default,
                            0f,
                            0);
                        return true;
                    case RenderCommandType.PushBlendMode:
                        key = new HostCommandKey(
                            command.Type,
                            default,
                            default,
                            null,
                            default,
                            0f,
                            command.IntParam);
                        return true;
                    case RenderCommandType.PopClip:
                    case RenderCommandType.PopBlendMode:
                        key = new HostCommandKey(
                            command.Type,
                            default,
                            default,
                            null,
                            default,
                            0f,
                            0);
                        return true;
                    default:
                        key = default;
                        return false;
                }
            }
        }
    }
}
