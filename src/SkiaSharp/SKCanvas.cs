using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public class SKCanvas : IDisposable
{
    private static readonly object s_compositorCacheScope = new();
    private static readonly ConditionalWeakTable<SKColorSpace, byte[]> s_toSrgbTables = new();
    private static readonly ConditionalWeakTable<GpuTexture, TextureColorSpace> s_textureColorSpaces = new();
    private static readonly byte[] s_identityColorTable = CreateIdentityColorTable();

    private sealed class TextureColorSpace
    {
        public TextureColorSpace(SKColorSpace value)
        {
            Value = value;
        }

        public SKColorSpace Value { get; }
    }

    private readonly record struct ImageFilterCacheKey(
        GpuTexture Source,
        SKImageFilter Filter,
        bool PreserveSourceColorSpace);

    private readonly struct ShaderColorFilterList
    {
        private readonly SKColorFilter? _first;
        private readonly SKColorFilter? _second;
        private readonly SKColorFilter? _third;
        private readonly SKColorFilter? _fourth;
        private readonly SKColorFilter[]? _overflow;

        private ShaderColorFilterList(
            SKColorFilter? first,
            SKColorFilter? second,
            SKColorFilter? third,
            SKColorFilter? fourth,
            SKColorFilter[]? overflow,
            int count)
        {
            _first = first;
            _second = second;
            _third = third;
            _fourth = fourth;
            _overflow = overflow;
            Count = count;
        }

        public int Count { get; }

        public SKColorFilter this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                if (_overflow != null)
                {
                    return _overflow[index];
                }

                return index switch
                {
                    0 => _first!,
                    1 => _second!,
                    2 => _third!,
                    _ => _fourth!,
                };
            }
        }

        public ShaderColorFilterList Prepend(SKColorFilter filter)
        {
            return Count switch
            {
                0 => new(filter, null, null, null, null, 1),
                1 => new(filter, _first, null, null, null, 2),
                2 => new(filter, _first, _second, null, null, 3),
                3 => new(filter, _first, _second, _third, null, 4),
                _ => CreateOverflow(filter),
            };
        }

        private ShaderColorFilterList CreateOverflow(SKColorFilter filter)
        {
            var overflow = new SKColorFilter[Count + 1];
            overflow[0] = filter;
            for (var index = 0; index < Count; index++)
            {
                overflow[index + 1] = this[index];
            }
            return new(null, null, null, null, overflow, overflow.Length);
        }
    }

    private DrawingContext _context;
    private readonly float _width;
    private readonly float _height;
    private readonly WgpuContext? _gpuContext;
    private readonly Action? _flush;
    private readonly SKBitmap? _bitmap;
    private readonly bool _isPictureRecording;
    private SKSurface? _surface;
    private GRRecordingContext? _recordingContext;
    private SKMatrix _currentMatrix = SKMatrix.Identity;
    private float _currentOpacity = 1f;
    private ClipState _clipState;
    private readonly List<GpuTexture> _ownedLayerTextures = new();
    private List<SKRect>? _cpuReadbackRegions;
    public enum PushKind
    {
        RectClip,
        GeometryClip,
        Opacity
    }

    private readonly record struct ClipState(SKRectI DeviceBounds, bool IsRect)
    {
        public bool IsEmpty => DeviceBounds.Right <= DeviceBounds.Left ||
            DeviceBounds.Bottom <= DeviceBounds.Top;
    }

    private readonly Stack<(
        SKMatrix Matrix,
        float Opacity,
        int PushedScopesCount,
        ClipState ClipState)> _stateStack = new();
    private readonly Stack<PushKind> _pushedScopes = new();
    private readonly Stack<RenderCommand> _activeClipPushes = new();
    private readonly Stack<LayerFrame> _layerStack = new();

    private sealed class LayerFrame
    {
        public LayerFrame(
            DrawingContext parentContext,
            DrawingContext layerContext,
            SKPaint? paint,
            SKImageFilter? backdrop,
            SKCanvasSaveLayerRecFlags flags,
            DrawingContext? previousContext,
            int stateDepth,
            SKRect bounds,
            SKMatrix boundsMatrix,
            RenderCommand[] activeClipPushes)
        {
            ParentContext = parentContext;
            LayerContext = layerContext;
            Paint = paint;
            Backdrop = backdrop;
            Flags = flags;
            PreviousContext = previousContext;
            StateDepth = stateDepth;
            Bounds = bounds;
            BoundsMatrix = boundsMatrix;
            ActiveClipPushes = activeClipPushes;
        }

        public DrawingContext ParentContext { get; }
        public DrawingContext LayerContext { get; }
        public SKPaint? Paint { get; }
        public SKImageFilter? Backdrop { get; }
        public SKCanvasSaveLayerRecFlags Flags { get; }
        public DrawingContext? PreviousContext { get; }
        public int StateDepth { get; }
        public SKRect Bounds { get; }
        public SKMatrix BoundsMatrix { get; }
        public RenderCommand[] ActiveClipPushes { get; }
    }

    public SKMatrix TotalMatrix => _currentMatrix;

    public int SaveCount => _stateStack.Count + 1;

    public SKMatrix44 TotalMatrix44 => SKMatrix44.FromMatrix4x4(_currentMatrix.ToMatrix4x4());

    public SKRect LocalClipBounds => GetLocalClipBounds(out var bounds) ? bounds : SKRect.Empty;

    public SKRectI DeviceClipBounds => GetDeviceClipBounds(out var bounds) ? bounds : SKRectI.Empty;

    public bool IsClipEmpty => _clipState.IsEmpty;

    public bool IsClipRect => !_clipState.IsEmpty && _clipState.IsRect;

    public SKSurface? Surface => _surface;

    public GRRecordingContext? Context => _recordingContext;

    public SKCanvas(
        DrawingContext context,
        float width,
        float height,
        WgpuContext? gpuContext = null,
        Action? flush = null)
        : this(context, width, height, gpuContext, flush, isPictureRecording: false)
    {
    }

    internal SKCanvas(
        DrawingContext context,
        float width,
        float height,
        bool isPictureRecording)
        : this(
            context,
            width,
            height,
            gpuContext: null,
            flush: null,
            isPictureRecording: isPictureRecording)
    {
    }

    private SKCanvas(
        DrawingContext context,
        float width,
        float height,
        WgpuContext? gpuContext,
        Action? flush,
        bool isPictureRecording)
    {
        _context = context;
        _width = width;
        _height = height;
        _gpuContext = gpuContext;
        _flush = flush;
        _isPictureRecording = isPictureRecording;
        _recordingContext = gpuContext == null ? null : new GRContext(gpuContext);
        _clipState = new ClipState(
            new SKRectI(0, 0, ToCanvasExtent(width), ToCanvasExtent(height)),
            IsRect: true);
    }

    public SKCanvas(SKBitmap bitmap)
        : this(
            new DrawingContext(),
            (bitmap ?? throw new ArgumentNullException(nameof(bitmap))).Width,
            bitmap.Height,
            SKContextHelper.GetContext())
    {
        _bitmap = bitmap;
        _recordingContext = null;
        bitmap.AttachCanvas(this);
    }

    internal DrawingContext DrawingContext => _context;

    internal void AttachSurface(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
    }

    internal void AttachRecordingContext(GRRecordingContext? recordingContext)
    {
        _recordingContext = recordingContext;
    }

    internal void DetachSurface(SKSurface surface)
    {
        if (ReferenceEquals(_surface, surface))
        {
            _surface = null;
        }
    }

    internal DrawingContext? CurrentLayerPreviousContext =>
        _layerStack.TryPeek(out var layer) ? layer.PreviousContext : null;

    internal SKPaint? CurrentLayerPaint =>
        _layerStack.TryPeek(out var layer) ? layer.Paint : null;

    internal SKImageFilter? CurrentLayerBackdrop =>
        _layerStack.TryPeek(out var layer) ? layer.Backdrop : null;

    internal SKCanvasSaveLayerRecFlags CurrentLayerFlags =>
        _layerStack.TryPeek(out var layer) ? layer.Flags : SKCanvasSaveLayerRecFlags.None;

    internal SKRect? CurrentLayerBounds =>
        _layerStack.TryPeek(out var layer) ? layer.Bounds : null;

    public void Clear()
    {
        Clear(SKColors.Empty);
    }

    public void Clear(SKColor color) =>
        DrawDeviceColor(new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f), SKBlendMode.Src);

    public void Clear(SKColorF color)
    {
        var clamped = color.Clamp();
        DrawDeviceColor(new Vector4(clamped.Red, clamped.Green, clamped.Blue, clamped.Alpha), SKBlendMode.Src);
    }

    public void DrawColor(SKColor color, SKBlendMode mode = SKBlendMode.Src) =>
        DrawDeviceColor(new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f), mode);

    public void DrawColor(SKColorF color, SKBlendMode mode = SKBlendMode.Src)
    {
        var clamped = color.Clamp();
        DrawDeviceColor(new Vector4(clamped.Red, clamped.Green, clamped.Blue, clamped.Alpha), mode);
    }

    private void DrawDeviceColor(Vector4 color, SKBlendMode mode)
    {
        var blendMode = MapBlendMode(mode);
        var pushedBlendMode = blendMode != GpuBlendMode.SrcOver;
        if (pushedBlendMode)
        {
            _context.PushBlendMode(blendMode);
        }

        try
        {
            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(0f, 0f, _width, _height),
                Brush = new SolidColorBrush(color),
                Transform = Matrix4x4.Identity,
            });
        }
        finally
        {
            if (pushedBlendMode)
            {
                _context.PopBlendMode();
            }
        }
    }

    public void Discard()
    {
        // Retained canvases have no immediate attachment contents to invalidate.
    }

    public int Save()
    {
        var restoreCount = SaveCount;
        _stateStack.Push((_currentMatrix, _currentOpacity, _pushedScopes.Count, _clipState));
        return restoreCount;
    }

    public int SaveLayer(SKRect bounds, SKPaint? paint)
    {
        return SaveLayerCore(
            bounds,
            paint,
            backdrop: null,
            SKCanvasSaveLayerRecFlags.None);
    }

    public int SaveLayer(in SKCanvasSaveLayerRec rec)
    {
        return SaveLayerCore(
            rec.Bounds ?? new SKRect(0, 0, _width, _height),
            rec.Paint,
            rec.Backdrop,
            rec.Flags);
    }

    private int SaveLayerCore(
        SKRect bounds,
        SKPaint? paint,
        SKImageFilter? backdrop,
        SKCanvasSaveLayerRecFlags flags)
    {
        var restoreCount = SaveCount;
        Save();

        var parentContext = _context;
        var layerContext = new DrawingContext();
        DrawingContext? previousContext = null;
        if (IsValidLayerBounds(bounds) &&
            (backdrop != null ||
             (flags & SKCanvasSaveLayerRecFlags.InitializeWithPrevious) != 0))
        {
            previousContext = new DrawingContext();
            previousContext.Append(parentContext);
        }

        _layerStack.Push(new LayerFrame(
            parentContext,
            layerContext,
            paint?.Clone(),
            backdrop,
            flags,
            previousContext,
            _stateStack.Count,
            bounds,
            _currentMatrix,
            SnapshotActiveClipPushes()));
        _context = layerContext;

        return restoreCount;
    }

    public int SaveLayer(SKPaint? paint)
    {
        return SaveLayer(new SKRect(0, 0, _width, _height), paint);
    }

    public int SaveLayer() => SaveLayer(new SKRect(0, 0, _width, _height), null);

    public void Restore()
    {
        if (_stateStack.Count > 0)
        {
            var layerFrame = _layerStack.Count > 0 && _layerStack.Peek().StateDepth == _stateStack.Count
                ? _layerStack.Pop()
                : null;

            var state = _stateStack.Pop();
            _currentMatrix = state.Matrix;
            _currentOpacity = state.Opacity;
            _clipState = state.ClipState;

            // Pop any clips or layers pushed in this save frame
            while (_pushedScopes.Count > state.PushedScopesCount)
            {
                var kind = _pushedScopes.Pop();
                switch (kind)
                {
                    case PushKind.RectClip:
                        _context.PopClip();
                        PopActiveClipScope();
                        break;
                    case PushKind.GeometryClip:
                        _context.PopGeometryClip();
                        PopActiveClipScope();
                        break;
                    case PushKind.Opacity:
                        _context.PopOpacity();
                        break;
                }
            }

            if (layerFrame != null)
            {
                RestoreLayer(layerFrame);
            }
        }
    }

    public void RestoreToCount(int count)
    {
        var targetDepth = Math.Max(1, count) - 1;
        while (_stateStack.Count > targetDepth)
        {
            Restore();
        }
    }

    private void RestoreLayer(LayerFrame layerFrame)
    {
        try
        {
            RestoreLayerCore(layerFrame);
        }
        finally
        {
            layerFrame.LayerContext.Clear();
            layerFrame.PreviousContext?.Clear();
            layerFrame.Paint?.Dispose();
        }
    }

    private void RestoreLayerCore(LayerFrame layerFrame)
    {
        _context = layerFrame.ParentContext;
        var hasSourceGeneratingFilter = layerFrame.Paint?.ImageFilter != null ||
            layerFrame.Paint?.ColorFilter != null ||
            layerFrame.PreviousContext != null;
        if ((!hasSourceGeneratingFilter && layerFrame.LayerContext.Commands.Count == 0) ||
            !IsValidLayerBounds(layerFrame.Bounds))
        {
            return;
        }

        var pushedBlendMode = PushPaintBlendMode(layerFrame.Paint);
        var opacity = layerFrame.Paint?.Color.A / 255f ?? 1f;

        try
        {
            var pushedOpacity = false;
            if (opacity < 1f)
            {
                _context.PushOpacity(opacity);
                pushedOpacity = true;
            }

            try
            {
                var pushedLayerBoundsClip = PushLayerBoundsClip(_context, layerFrame);
                try
                {
                    DrawRestoredLayerTexture(layerFrame, RenderLayerToTexture(layerFrame));
                }
                finally
                {
                    if (pushedLayerBoundsClip)
                    {
                        _context.PopClip();
                    }
                }
            }
            finally
            {
                if (pushedOpacity)
                {
                    _context.PopOpacity();
                }
            }
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    private void DrawRestoredLayerTexture(LayerFrame layerFrame, GpuTexture texture)
    {
        if (layerFrame.Paint?.ImageFilter is { } imageFilter)
        {
            texture = RenderImageFilterGraph(texture, imageFilter, layerFrame.BoundsMatrix.ToMatrix4x4());
        }

        if (layerFrame.Paint?.ColorFilter is { } colorFilter)
        {
            var filteredTexture = RenderColorFilter(texture, colorFilter, cropRect: null);
            if (!ReferenceEquals(filteredTexture, texture))
            {
                ReleaseOwnedLayerTexture(texture);
                texture = filteredTexture;
            }
        }

        DrawRestoredLayerTexture(
            texture,
            new Rect(0f, 0f, texture.Width, texture.Height));
    }

    private void DrawRestoredLayerTexture(GpuTexture texture, Rect rect)
    {
        RetainLayerTextureForDeferredCommand(texture);
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = texture,
            Rect = rect,
            Transform = Matrix4x4.Identity,
            TextureSamplingMode = TextureSamplingMode.Linear
        });
    }

    private void RetainLayerTextureForDeferredCommand(GpuTexture texture)
    {
        _context.RetainResource(texture);
        _ownedLayerTextures.Remove(texture);
    }

    private GpuTexture RenderImageFilterGraph(
        GpuTexture sourceTexture,
        SKImageFilter root,
        Matrix4x4 filterTransform)
    {
        var firstGraphTexture = _ownedLayerTextures.Count;
        var cache = new Dictionary<ImageFilterCacheKey, GpuTexture>();
        GpuTexture? result = null;
        try
        {
            result = EvaluateImageFilter(sourceTexture, root, cache, filterTransform);
            ReleaseGraphIntermediateTextures(firstGraphTexture, result);
            if (!ReferenceEquals(result, sourceTexture))
            {
                ReleaseOwnedLayerTexture(sourceTexture);
            }

            return result;
        }
        catch
        {
            ReleaseGraphIntermediateTextures(firstGraphTexture, keep: null);
            throw;
        }
    }

    private GpuTexture ConvertOwnedFilterTextureToSrgb(
        GpuTexture texture,
        SKColorSpace? sourceColorSpace)
    {
        var converted = ConvertTextureToSrgb(texture, sourceColorSpace);
        if (!ReferenceEquals(converted, texture))
        {
            ReleaseOwnedLayerTexture(texture);
        }

        return converted;
    }

    private GpuTexture ConvertTextureToSrgb(
        GpuTexture texture,
        SKColorSpace? sourceColorSpace)
    {
        if (sourceColorSpace == null ||
            sourceColorSpace.TransferFunction == SKColorSpaceTransferFn.Srgb)
        {
            return texture;
        }

        var table = s_toSrgbTables.GetValue(sourceColorSpace, CreateToSrgbColorTable);
        using var colorFilter = SKColorFilter.CreateTable(
            s_identityColorTable,
            table,
            table,
            table);
        return RenderColorFilter(texture, colorFilter, cropRect: null);
    }

    private GpuTexture ConvertImageTextureToSrgb(
        GpuTexture texture,
        SKColorSpace? sourceColorSpace)
    {
        var converted = ConvertTextureToSrgb(texture, sourceColorSpace);
        if (!ReferenceEquals(converted, texture))
        {
            RetainLayerTextureForDeferredCommand(converted);
        }

        return converted;
    }

    private static bool HasLinearImageFilterSource(SKImageFilter filter)
    {
        var visited = new HashSet<SKImageFilter>();
        var pending = new Stack<SKImageFilter>();
        pending.Push(filter);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (current.Kind == SKImageFilter.FilterKind.Image &&
                current.Parameters is SKImageFilter.ImageData imageData)
            {
                return imageData.Image.ColorSpace?.IsLinear == true;
            }

            if (current.Kind == SKImageFilter.FilterKind.Picture &&
                current.Parameters is SKImageFilter.PictureData pictureData &&
                TryGetPictureImageColorSpace(pictureData.Picture.Picture, out var pictureColorSpace))
            {
                return pictureColorSpace.IsLinear;
            }

            if (current.Input != null)
            {
                pending.Push(current.Input);
            }

            switch (current.Parameters)
            {
                case SKImageFilter.ComposeData compose:
                    pending.Push(compose.Outer);
                    pending.Push(compose.Inner);
                    break;
                case SKImageFilter.ArithmeticData arithmetic:
                    PushOptional(pending, arithmetic.Background);
                    PushOptional(pending, arithmetic.Foreground);
                    break;
                case SKImageFilter.BlendModeData blend:
                    PushOptional(pending, blend.Background);
                    PushOptional(pending, blend.Foreground);
                    break;
                case SKImageFilter.DisplacementData displacement:
                    pending.Push(displacement.Displacement);
                    break;
                case SKImageFilter[] merge:
                    foreach (var input in merge)
                    {
                        PushOptional(pending, input);
                    }
                    break;
            }
        }

        return false;

        static void PushOptional(Stack<SKImageFilter> pending, SKImageFilter? filter)
        {
            if (filter != null)
            {
                pending.Push(filter);
            }
        }
    }

    private static bool TryGetPictureImageColorSpace(
        GpuPicture picture,
        out SKColorSpace colorSpace)
    {
        SKColorSpace? candidate = null;
        foreach (var command in picture.Commands)
        {
            switch (command.Type)
            {
                case RenderCommandType.DrawTexture:
                    if (command.Texture == null ||
                        !s_textureColorSpaces.TryGetValue(command.Texture, out var textureColorSpace) ||
                        candidate != null && candidate.TransferFunction != textureColorSpace.Value.TransferFunction)
                    {
                        colorSpace = null!;
                        return false;
                    }

                    candidate = textureColorSpace.Value;
                    break;
                case RenderCommandType.DrawPicture:
                    if (command.Picture == null ||
                        !TryGetPictureImageColorSpace(command.Picture, out var nestedColorSpace) ||
                        candidate != null && candidate.TransferFunction != nestedColorSpace.TransferFunction)
                    {
                        colorSpace = null!;
                        return false;
                    }

                    candidate = nestedColorSpace;
                    break;
                case RenderCommandType.PushClip:
                case RenderCommandType.PopClip:
                case RenderCommandType.PushGeometryClip:
                case RenderCommandType.PopGeometryClip:
                case RenderCommandType.PushOpacity:
                case RenderCommandType.PopOpacity:
                case RenderCommandType.PushBlendMode:
                case RenderCommandType.PopBlendMode:
                    break;
                default:
                    colorSpace = null!;
                    return false;
            }
        }

        colorSpace = candidate!;
        return candidate != null;
    }

    private static byte[] CreateIdentityColorTable()
    {
        var table = new byte[256];
        for (var index = 0; index < table.Length; index++)
        {
            table[index] = (byte)index;
        }

        return table;
    }

    private static byte[] CreateToSrgbColorTable(SKColorSpace sourceColorSpace)
    {
        var table = new byte[256];
        for (var index = 0; index < table.Length; index++)
        {
            var encoded = index / 255f;
            var linear = Math.Clamp(sourceColorSpace.TransferFunction.Transform(encoded), 0f, 1f);
            var srgb = linear <= 0.0031308f
                ? linear * 12.92f
                : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
            table[index] = (byte)Math.Clamp((int)MathF.Round(srgb * 255f), 0, 255);
        }

        return table;
    }

    private void ReleaseGraphIntermediateTextures(int firstGraphTexture, GpuTexture? keep)
    {
        for (var i = _ownedLayerTextures.Count - 1; i >= firstGraphTexture; i--)
        {
            var texture = _ownedLayerTextures[i];
            if (ReferenceEquals(texture, keep))
            {
                continue;
            }

            _ownedLayerTextures.RemoveAt(i);
            texture.Dispose();
        }
    }

    private void ReleaseOwnedLayerTexture(GpuTexture texture)
    {
        if (_ownedLayerTextures.Remove(texture))
        {
            texture.Dispose();
        }
    }

    private GpuTexture EvaluateImageFilter(
        GpuTexture sourceTexture,
        SKImageFilter filter,
        Dictionary<ImageFilterCacheKey, GpuTexture> cache,
        Matrix4x4 filterTransform,
        bool preserveSourceColorSpace = false)
    {
        var cacheKey = new ImageFilterCacheKey(sourceTexture, filter, preserveSourceColorSpace);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        GpuTexture result;
        switch (filter.Kind)
        {
            case SKImageFilter.FilterKind.Blur:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                var blur = (SKImageFilter.BlurData)filter.Parameters!;
                result = RenderBlur(
                    input,
                    blur.SigmaX * GetAxisScale(filterTransform, Vector2.UnitX),
                    blur.SigmaY * GetAxisScale(filterTransform, Vector2.UnitY));
                break;
            }
            case SKImageFilter.FilterKind.Compose:
            {
                var compose = (SKImageFilter.ComposeData)filter.Parameters!;
                var inner = EvaluateImageFilter(
                    sourceTexture,
                    compose.Inner,
                    cache,
                    filterTransform,
                    preserveSourceColorSpace);
                result = EvaluateImageFilter(
                    inner,
                    compose.Outer,
                    cache,
                    filterTransform,
                    preserveSourceColorSpace);
                break;
            }
            case SKImageFilter.FilterKind.DropShadow:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderDropShadow(
                    input,
                    (SKImageFilter.DropShadowData)filter.Parameters!,
                    filterTransform);
                break;
            }
            case SKImageFilter.FilterKind.ColorFilter:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderColorFilter(input, (SKColorFilter)filter.Parameters!, cropRect: null);
                break;
            }
            case SKImageFilter.FilterKind.Offset:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                var offset = (SKImageFilter.OffsetData)filter.Parameters!;
                var transformedOffset = TransformFilterVector(
                    new Vector2(offset.Dx, offset.Dy),
                    filterTransform);
                result = RenderFilterPass(
                    "SKImageFilter Offset",
                    input.Width,
                    input.Height,
                    context => context.DrawTexture(
                        input,
                        new Rect(transformedOffset.X, transformedOffset.Y, input.Width, input.Height)));
                break;
            }
            case SKImageFilter.FilterKind.Dilate:
            case SKImageFilter.FilterKind.Erode:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                var morphology = (SKImageFilter.MorphologyData)filter.Parameters!;
                result = RenderMorphology(
                    input,
                    morphology.RadiusX * GetAxisScale(filterTransform, Vector2.UnitX),
                    morphology.RadiusY * GetAxisScale(filterTransform, Vector2.UnitY),
                    dilate: filter.Kind == SKImageFilter.FilterKind.Dilate);
                break;
            }
            case SKImageFilter.FilterKind.Merge:
            {
                var filters = (SKImageFilter?[])filter.Parameters!;
                var inputs = new GpuTexture[filters.Length];
                var width = sourceTexture.Width;
                var height = sourceTexture.Height;
                for (var i = 0; i < filters.Length; i++)
                {
                    inputs[i] = EvaluateOptionalInput(
                        sourceTexture,
                        filters[i],
                        cache,
                        filterTransform,
                        preserveSourceColorSpace);
                    width = Math.Max(width, inputs[i].Width);
                    height = Math.Max(height, inputs[i].Height);
                }
                result = RenderFilterPass(
                    "SKImageFilter Merge",
                    width,
                    height,
                    context =>
                    {
                        for (var i = 0; i < inputs.Length; i++)
                        {
                            context.DrawTexture(
                                inputs[i],
                                new Rect(0f, 0f, inputs[i].Width, inputs[i].Height));
                        }
                    });
                break;
            }
            case SKImageFilter.FilterKind.Arithmetic:
            {
                var arithmetic = (SKImageFilter.ArithmeticData)filter.Parameters!;
                var background = EvaluateOptionalInput(
                    sourceTexture,
                    arithmetic.Background,
                    cache,
                    filterTransform,
                    preserveSourceColorSpace);
                var foreground = EvaluateOptionalInput(sourceTexture, arithmetic.Foreground, cache, filterTransform, preserveSourceColorSpace);
                result = RenderArithmeticComposite(background, foreground, arithmetic);
                break;
            }
            case SKImageFilter.FilterKind.DisplacementMap:
            {
                var displacement = (SKImageFilter.DisplacementData)filter.Parameters!;
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                var preserveDisplacementColorSpace = preserveSourceColorSpace ||
                    HasLinearImageFilterSource(displacement.Displacement);
                var displacementInput = EvaluateImageFilter(
                    sourceTexture,
                    displacement.Displacement,
                    cache,
                    filterTransform,
                    preserveDisplacementColorSpace);
                result = RenderDisplacementMap(
                    input,
                    displacementInput,
                    displacement,
                    filterTransform);
                break;
            }
            case SKImageFilter.FilterKind.MatrixConvolution:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderMatrixConvolution(
                    input,
                    (SKImageFilter.MatrixConvolutionData)filter.Parameters!,
                    filter.CropRect,
                    filterTransform);
                break;
            }
            case SKImageFilter.FilterKind.MatrixTransform:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderMatrixTransform(
                    input,
                    (SKImageFilter.MatrixTransformData)filter.Parameters!,
                    filterTransform);
                break;
            }
            case SKImageFilter.FilterKind.Magnifier:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                input = ApplyFilterCrop(input, filter.CropRect, filterTransform);
                result = RenderMagnifier(
                    input,
                    (SKImageFilter.MagnifierData)filter.Parameters!,
                    filterTransform,
                    filter.CropRect);
                break;
            }
            case SKImageFilter.FilterKind.DistantLitDiffuse:
            case SKImageFilter.FilterKind.DistantLitSpecular:
            case SKImageFilter.FilterKind.PointLitDiffuse:
            case SKImageFilter.FilterKind.PointLitSpecular:
            case SKImageFilter.FilterKind.SpotLitDiffuse:
            case SKImageFilter.FilterKind.SpotLitSpecular:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderImageLighting(input, filter.Kind, filter.Parameters!, filterTransform);
                break;
            }
            case SKImageFilter.FilterKind.BlendMode:
            {
                var blend = (SKImageFilter.BlendModeData)filter.Parameters!;
                var background = EvaluateOptionalInput(
                    sourceTexture,
                    blend.Background,
                    cache,
                    filterTransform,
                    preserveSourceColorSpace);
                var foreground = EvaluateOptionalInput(sourceTexture, blend.Foreground, cache, filterTransform, preserveSourceColorSpace);
                if (blend.Blender?.Arithmetic is { } arithmetic)
                {
                    result = RenderArithmeticComposite(
                        background,
                        foreground,
                        new SKImageFilter.ArithmeticData(
                            arithmetic.K1,
                            arithmetic.K2,
                            arithmetic.K3,
                            arithmetic.K4,
                            arithmetic.EnforcePremul,
                            null,
                            null));
                }
                else
                {
                    var mode = blend.Mode;
                    if (!mode.HasValue &&
                        blend.Blender?.TryGetBlendMode(out var blenderMode) == true)
                    {
                        mode = blenderMode;
                    }

                    result = RenderImageBlend(
                        background,
                        foreground,
                        mode ?? throw new NotSupportedException(
                            "The SKBlender image filter is not supported."));
                }
                break;
            }
            case SKImageFilter.FilterKind.Image:
            {
                var image = (SKImageFilter.ImageData)filter.Parameters!;
                result = RenderFilterPass(
                    "SKImageFilter Image",
                    sourceTexture.Width,
                    sourceTexture.Height,
                    context => context.DrawTexture(
                        image.Image.Texture,
                        ToRect(image.Destination),
                        ToRect(image.Source),
                        Matrix4x4.Identity,
                        MapSampling(image.Sampling),
                        MapCubicSampling(image.Sampling)));
                if (!preserveSourceColorSpace)
                {
                    result = ConvertOwnedFilterTextureToSrgb(result, image.Image.ColorSpace);
                }
                break;
            }
            case SKImageFilter.FilterKind.Picture:
            {
                var picture = (SKImageFilter.PictureData)filter.Parameters!;
                var pictureTransform = filterTransform == default
                    ? Matrix4x4.Identity
                    : filterTransform;
                result = RenderFilterPass(
                    "SKImageFilter Picture",
                    sourceTexture.Width,
                    sourceTexture.Height,
                    context =>
                    {
                        context.Commands.Add(new RenderCommand
                        {
                            Type = RenderCommandType.PushClip,
                            Rect = ToRect(picture.TargetRect),
                            Transform = pictureTransform
                        });
                        context.DrawPictureTransformed(picture.Picture.Picture, pictureTransform);
                        context.PopClip();
                    });
                if (!preserveSourceColorSpace &&
                    TryGetPictureImageColorSpace(picture.Picture.Picture, out var pictureColorSpace))
                {
                    result = ConvertOwnedFilterTextureToSrgb(result, pictureColorSpace);
                }
                break;
            }
            case SKImageFilter.FilterKind.Shader:
            {
                var shader = (SKImageFilter.ShaderData)filter.Parameters!;
                result = RenderShaderFilter(
                    shader.Shader,
                    filterTransform,
                    sourceTexture.Width,
                    sourceTexture.Height);
                break;
            }
            case SKImageFilter.FilterKind.Tile:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderTile(input, (SKImageFilter.TileData)filter.Parameters!);
                break;
            }
            default:
                result = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                break;
        }

        if (filter.Kind != SKImageFilter.FilterKind.Magnifier)
        {
            result = ApplyFilterCrop(result, filter.CropRect, filterTransform);
        }
        cache[cacheKey] = result;
        return result;
    }

    private GpuTexture EvaluateOptionalInput(
        GpuTexture sourceTexture,
        SKImageFilter? input,
        Dictionary<ImageFilterCacheKey, GpuTexture> cache,
        Matrix4x4 filterTransform,
        bool preserveSourceColorSpace) =>
        input == null
            ? sourceTexture
            : EvaluateImageFilter(sourceTexture, input, cache, filterTransform, preserveSourceColorSpace);

    private GpuTexture RenderColorFilter(GpuTexture input, SKColorFilter colorFilter, SKRect? cropRect)
    {
        if (colorFilter.TryGetCompose(out var outer, out var inner))
        {
            var innerTexture = RenderColorFilter(input, inner, cropRect: null);
            GpuTexture? result = null;
            try
            {
                result = RenderColorFilter(innerTexture, outer, cropRect);
                return result;
            }
            finally
            {
                if (!ReferenceEquals(innerTexture, input) &&
                    !ReferenceEquals(innerTexture, result))
                {
                    ReleaseOwnedLayerTexture(innerTexture);
                }
            }
        }

        if (colorFilter.TryGetLerp(out var weight, out var filter0, out var filter1))
        {
            GpuTexture? color0 = null;
            GpuTexture? color1 = null;
            GpuTexture? result = null;
            try
            {
                color0 = RenderColorFilter(input, filter0, cropRect: null);
                color1 = RenderColorFilter(input, filter1, cropRect: null);
                if (ReferenceEquals(color0, color1))
                {
                    result = color0;
                    return result;
                }

                result = RenderArithmeticComposite(
                    color0,
                    color1,
                    new SKImageFilter.ArithmeticData(
                        0f,
                        weight,
                        1f - weight,
                        0f,
                        EnforcePremultipliedColor: true,
                        Background: null,
                        Foreground: null));
                return result;
            }
            finally
            {
                if (color0 != null &&
                    !ReferenceEquals(color0, input) &&
                    !ReferenceEquals(color0, result))
                {
                    ReleaseOwnedLayerTexture(color0);
                }
                if (color1 != null &&
                    !ReferenceEquals(color1, input) &&
                    !ReferenceEquals(color1, color0) &&
                    !ReferenceEquals(color1, result))
                {
                    ReleaseOwnedLayerTexture(color1);
                }
            }
        }

        if (colorFilter.TryGetHslaColorMatrix(out var hslaMatrix))
        {
            return RenderNonlinearColorFilter(
                input,
                hslaMatrix.Span,
                hsla: true,
                grayscale: false,
                invertStyle: 0u,
                contrast: 0f);
        }

        if (colorFilter.TryGetHighContrast(out var highContrast))
        {
            return RenderNonlinearColorFilter(
                input,
                ReadOnlySpan<float>.Empty,
                hsla: false,
                highContrast.Grayscale,
                (uint)highContrast.InvertStyle,
                highContrast.Contrast);
        }

        if (colorFilter.TryGetBlendColor(out var blendColor, out var blendMode))
        {
            if (blendMode == SKBlendMode.Dst)
            {
                return ApplyFilterCrop(input, cropRect);
            }

            var foreground = RenderFilterPass(
                "SKColorFilter Blend Color",
                input.Width,
                input.Height,
                context => context.DrawRectangle(
                    new SolidColorBrush(ToVector4(blendColor)),
                    null,
                    new Rect(0f, 0f, input.Width, input.Height)));
            try
            {
                return RenderImageBlend(input, foreground, blendMode);
            }
            finally
            {
                ReleaseOwnedLayerTexture(foreground);
            }
        }

        if (colorFilter.TryGetColorTables(out var alpha, out var red, out var green, out var blue))
        {
            var context = GetGpuContext();
            var destination = CreateOwnedFilterTexture(context, "SKImageFilter Color Table", storage: true);
            GetCompositorForContext(context).ApplyColorTable(
                input,
                destination,
                alpha.Span,
                red.Span,
                green.Span,
                blue.Span);
            return destination;
        }

        if (colorFilter.IsLumaColor)
        {
            return RenderFilterPass(
                "SKImageFilter Luminance To Alpha",
                input.Width,
                input.Height,
                context => context.DrawImageWithEffect(
                    input,
                    new Rect(0f, 0f, input.Width, input.Height),
                    luminanceToAlpha: true));
        }

        if (!colorFilter.TryGetImageEffectColorMatrix(out var matrix))
        {
            return ApplyFilterCrop(input, cropRect);
        }

        return RenderFilterPass(
            "SKImageFilter Color Matrix",
            input.Width,
            input.Height,
            context => context.DrawImageWithEffect(
                input,
                new Rect(0f, 0f, input.Width, input.Height),
                colorMatrix: matrix));
    }

    private GpuTexture RenderNonlinearColorFilter(
        GpuTexture input,
        ReadOnlySpan<float> matrix,
        bool hsla,
        bool grayscale,
        uint invertStyle,
        float contrast)
    {
        var context = GetGpuContext();
        var destination = CreateOwnedFilterTexture(
            context,
            hsla ? "SKColorFilter HSLA Matrix" : "SKColorFilter High Contrast",
            storage: true,
            width: input.Width,
            height: input.Height);
        GetCompositorForContext(context).ApplyNonlinearColorFilter(
            input,
            destination,
            matrix,
            hsla,
            grayscale,
            invertStyle,
            contrast);
        return destination;
    }

    private GpuTexture RenderBlur(GpuTexture input, float sigmaX, float sigmaY)
    {
        if ((!float.IsFinite(sigmaX) || sigmaX <= 0.01f) &&
            (!float.IsFinite(sigmaY) || sigmaY <= 0.01f))
        {
            return input;
        }

        var context = GetGpuContext();
        var temporary = CreateOwnedFilterTexture(context, "SKImageFilter Blur Temporary", storage: true);
        var destination = CreateOwnedFilterTexture(context, "SKImageFilter Blur Destination", storage: true);
        GetCompositorForContext(context).ApplyGaussianBlur(
            input,
            temporary,
            destination,
            sigmaX,
            sigmaY);
        return destination;
    }

    private GpuTexture RenderDropShadow(
        GpuTexture input,
        SKImageFilter.DropShadowData shadow,
        Matrix4x4 filterTransform)
    {
        var color = ToVector4(shadow.Color);
        var shadowTexture = RenderFilterPass(
            "SKImageFilter Shadow Color",
            input.Width,
            input.Height,
            context => context.DrawImageWithEffect(
                input,
                new Rect(0f, 0f, input.Width, input.Height),
                colorMatrix: new ImageEffectColorMatrix(
                    Vector4.Zero,
                    Vector4.Zero,
                    Vector4.Zero,
                    new Vector4(0f, 0f, 0f, color.W),
                    new Vector4(color.X, color.Y, color.Z, 0f))));
        var blurredShadow = RenderBlur(
            shadowTexture,
            shadow.SigmaX * GetAxisScale(filterTransform, Vector2.UnitX),
            shadow.SigmaY * GetAxisScale(filterTransform, Vector2.UnitY));
        var offset = TransformFilterVector(
            new Vector2(shadow.Dx, shadow.Dy),
            filterTransform);

        return RenderFilterPass(
            shadow.ShadowOnly
                ? "SKImageFilter Shadow Only Composite"
                : "SKImageFilter Shadow Composite",
            input.Width,
            input.Height,
            drawing =>
            {
                drawing.DrawTexture(
                    blurredShadow,
                    new Rect(offset.X, offset.Y, blurredShadow.Width, blurredShadow.Height));
                if (!shadow.ShadowOnly)
                {
                    drawing.DrawTexture(input, new Rect(0f, 0f, input.Width, input.Height));
                }
            });
    }

    private GpuTexture RenderMorphology(
        GpuTexture input,
        float radiusX,
        float radiusY,
        bool dilate)
    {
        var context = GetGpuContext();
        var temporary = CreateOwnedFilterTexture(context, "SKImageFilter Morphology Temporary", storage: true);
        var destination = CreateOwnedFilterTexture(context, "SKImageFilter Morphology Destination", storage: true);
        GetCompositorForContext(context).ApplyMorphology(
            input,
            temporary,
            destination,
            radiusX,
            radiusY,
            dilate);
        return destination;
    }

    private GpuTexture RenderImageBlend(
        GpuTexture background,
        GpuTexture foreground,
        SKBlendMode blendMode)
    {
        var context = GetGpuContext();
        var destination = CreateOwnedFilterTexture(
            context,
            "SKImageFilter Blend Destination",
            storage: true,
            width: Math.Max(background.Width, foreground.Width),
            height: Math.Max(background.Height, foreground.Height));
        GetCompositorForContext(context).ApplyImageBlend(
            background,
            foreground,
            destination,
            MapBlendMode(blendMode),
            linearRgb: false);
        return destination;
    }

    private GpuTexture RenderArithmeticComposite(
        GpuTexture background,
        GpuTexture foreground,
        SKImageFilter.ArithmeticData arithmetic)
    {
        var context = GetGpuContext();
        var destination = CreateOwnedFilterTexture(
            context,
            "SKImageFilter Arithmetic Destination",
            storage: true,
            width: Math.Max(background.Width, foreground.Width),
            height: Math.Max(background.Height, foreground.Height));
        GetCompositorForContext(context).ApplyArithmeticComposite(
            background,
            foreground,
            destination,
            arithmetic.K1,
            arithmetic.K2,
            arithmetic.K3,
            arithmetic.K4,
            arithmetic.EnforcePremultipliedColor);
        return destination;
    }

    private GpuTexture RenderDisplacementMap(
        GpuTexture input,
        GpuTexture displacementInput,
        SKImageFilter.DisplacementData displacement,
        Matrix4x4 filterTransform)
    {
        var context = GetGpuContext();
        var destination = CreateOwnedFilterTexture(
            context,
            "SKImageFilter Displacement Map Destination",
            storage: true);
        GetCompositorForContext(context).ApplyDisplacementMap(
            input,
            displacementInput,
            destination,
            CreateFilterVectorTransform(displacement.Scale, filterTransform),
            (uint)displacement.XChannel,
            (uint)displacement.YChannel);
        return destination;
    }

    private GpuTexture RenderMatrixConvolution(
        GpuTexture input,
        SKImageFilter.MatrixConvolutionData convolution,
        SKRect? cropRect,
        Matrix4x4 filterTransform)
    {
        var kernelWidth = convolution.KernelSize.Width;
        var kernelHeight = convolution.KernelSize.Height;
        if (kernelWidth is <= 0 or > 64 ||
            kernelHeight is <= 0 or > 64 ||
            convolution.Kernel.Length < kernelWidth * kernelHeight)
        {
            return input;
        }

        var tileMode = convolution.TileMode;
        var tileOriginX = 0;
        var tileOriginY = 0;
        var tileWidth = (int)Math.Min(input.Width, (uint)int.MaxValue);
        var tileHeight = (int)Math.Min(input.Height, (uint)int.MaxValue);
        if (tileMode != SKShaderTileMode.Decal &&
            !TryGetFilterCropPixelBounds(
                cropRect,
                filterTransform,
                input.Width,
                input.Height,
                out tileOriginX,
                out tileOriginY,
                out tileWidth,
                out tileHeight))
        {
            tileMode = SKShaderTileMode.Decal;
        }

        var context = GetGpuContext();
        var destination = CreateOwnedFilterTexture(
            context,
            "SKImageFilter Matrix Convolution Destination",
            storage: true);
        GetCompositorForContext(context).ApplyMatrixConvolution(
            input,
            destination,
            kernelWidth,
            kernelHeight,
            convolution.Kernel,
            convolution.Gain,
            convolution.Bias,
            convolution.KernelOffset.X,
            convolution.KernelOffset.Y,
            (uint)tileMode,
            convolution.ConvolveAlpha,
            tileOriginX,
            tileOriginY,
            tileWidth,
            tileHeight);
        return destination;
    }

    private GpuTexture RenderMatrixTransform(
        GpuTexture input,
        SKImageFilter.MatrixTransformData matrixTransform,
        Matrix4x4 filterTransform)
    {
        if (matrixTransform.Matrix.IsIdentity)
        {
            return input;
        }

        if (!TryCreateImageFilterDeviceTransform(
                matrixTransform.Matrix,
                filterTransform,
                out var deviceTransform))
        {
            return RenderFilterPass(
                "SKImageFilter Matrix Transform Empty",
                input.Width,
                input.Height,
                static _ => { });
        }

        return RenderFilterPass(
            "SKImageFilter Matrix Transform",
            input.Width,
            input.Height,
            context => context.DrawTexture(
                input,
                new Rect(0f, 0f, input.Width, input.Height),
                new Rect(0f, 0f, input.Width, input.Height),
                deviceTransform,
                MapSampling(matrixTransform.Sampling),
                MapCubicSampling(matrixTransform.Sampling)));
    }

    internal static bool TryCreateImageFilterDeviceTransform(
        SKMatrix matrix,
        Matrix4x4 filterTransform,
        out Matrix4x4 deviceTransform)
    {
        var layerTransform = filterTransform == default
            ? Matrix4x4.Identity
            : filterTransform;
        if (!Matrix4x4.Invert(layerTransform, out var inverseLayerTransform))
        {
            deviceTransform = default;
            return false;
        }

        deviceTransform = inverseLayerTransform *
            matrix.ToMatrix4x4() *
            layerTransform;
        return IsFinite(deviceTransform);
    }

    private GpuTexture RenderMagnifier(
        GpuTexture input,
        SKImageFilter.MagnifierData magnifier,
        Matrix4x4 filterTransform,
        SKRect? inputCropRect)
    {
        if (magnifier.ZoomAmount <= 1f)
        {
            return input;
        }

        var layerTransform = filterTransform == default
            ? Matrix4x4.Identity
            : filterTransform;
        var lensBounds = MapRectToBounds(magnifier.LensBounds, layerTransform);
        if (!IsValidLayerBounds(lensBounds))
        {
            return RenderFilterPass(
                "SKImageFilter Magnifier Empty",
                input.Width,
                input.Height,
                static _ => { });
        }

        var textureBounds = new SKRect(0f, 0f, input.Width, input.Height);
        var visibleLensBounds = Intersect(lensBounds, textureBounds);
        var availableInputBounds = inputCropRect is { } cropRect && IsValidLayerBounds(cropRect)
            ? Intersect(MapRectToBounds(cropRect, layerTransform), textureBounds)
            : textureBounds;
        var outputBounds = Intersect(visibleLensBounds, availableInputBounds);
        if (!IsValidLayerBounds(visibleLensBounds) ||
            !IsValidLayerBounds(availableInputBounds) ||
            !IsValidLayerBounds(outputBounds))
        {
            return RenderFilterPass(
                "SKImageFilter Magnifier Empty",
                input.Width,
                input.Height,
                static _ => { });
        }

        var zoomTransform = CreateMagnifierZoomTransform(
            lensBounds,
            visibleLensBounds,
            availableInputBounds,
            magnifier.ZoomAmount);

        var insetX = magnifier.Inset * GetAxisScale(layerTransform, Vector2.UnitX);
        var insetY = magnifier.Inset * GetAxisScale(layerTransform, Vector2.UnitY);
        var inverseInset = new Vector2(
            insetX > 0f && float.IsFinite(insetX) ? 1f / insetX : 0f,
            insetY > 0f && float.IsFinite(insetY) ? 1f / insetY : 0f);
        var samplingMode = magnifier.Sampling.UseCubic
            ? 2u
            : magnifier.Sampling.Filter == SKFilterMode.Nearest &&
              !magnifier.Sampling.IsAniso &&
              magnifier.Sampling.Mipmap == SKMipmapMode.None
                ? 0u
                : 1u;
        var cubic = MapCubicSampling(magnifier.Sampling) ?? Vector2.Zero;

        var context = GetGpuContext();
        var destination = CreateOwnedFilterTexture(
            context,
            "SKImageFilter Magnifier Destination",
            storage: true,
            input.Width,
            input.Height);
        GetCompositorForContext(context).ApplyMagnifier(
            input,
            destination,
            new Vector4(
                lensBounds.Left,
                lensBounds.Top,
                lensBounds.Right,
                lensBounds.Bottom),
            new Vector4(
                outputBounds.Left,
                outputBounds.Top,
                outputBounds.Right,
                outputBounds.Bottom),
            zoomTransform,
            inverseInset,
            samplingMode,
            cubic);
        return destination;
    }

    internal static Vector4 CreateMagnifierZoomTransform(
        SKRect lensBounds,
        SKRect visibleLensBounds,
        SKRect availableInputBounds,
        float zoomAmount)
    {
        var inverseZoom = 1f / zoomAmount;
        var centerX = Math.Clamp(
            (lensBounds.Left + lensBounds.Right) * 0.5f,
            availableInputBounds.Left,
            availableInputBounds.Right);
        var centerY = Math.Clamp(
            (lensBounds.Top + lensBounds.Bottom) * 0.5f,
            availableInputBounds.Top,
            availableInputBounds.Bottom);
        var translateX = centerX * (1f - inverseZoom);
        var translateY = centerY * (1f - inverseZoom);
        if (!Contains(availableInputBounds, visibleLensBounds))
        {
            var visibleSourceBounds = new SKRect(
                translateX + inverseZoom * visibleLensBounds.Left,
                translateY + inverseZoom * visibleLensBounds.Top,
                translateX + inverseZoom * visibleLensBounds.Right,
                translateY + inverseZoom * visibleLensBounds.Bottom);
            if (availableInputBounds.Width >= visibleSourceBounds.Width &&
                availableInputBounds.Height >= visibleSourceBounds.Height)
            {
                var fittedLeft = visibleSourceBounds.Left < availableInputBounds.Left
                    ? availableInputBounds.Left
                    : Math.Min(visibleSourceBounds.Right, availableInputBounds.Right) -
                      visibleSourceBounds.Width;
                var fittedTop = visibleSourceBounds.Top < availableInputBounds.Top
                    ? availableInputBounds.Top
                    : Math.Min(visibleSourceBounds.Bottom, availableInputBounds.Bottom) -
                      visibleSourceBounds.Height;
                translateX = fittedLeft - inverseZoom * visibleLensBounds.Left;
                translateY = fittedTop - inverseZoom * visibleLensBounds.Top;
            }
        }

        return new Vector4(translateX, translateY, inverseZoom, inverseZoom);
    }

    private static SKRect Intersect(SKRect first, SKRect second) =>
        new(
            Math.Max(first.Left, second.Left),
            Math.Max(first.Top, second.Top),
            Math.Min(first.Right, second.Right),
            Math.Min(first.Bottom, second.Bottom));

    private static bool Contains(SKRect outer, SKRect inner) =>
        outer.Left <= inner.Left &&
        outer.Top <= inner.Top &&
        outer.Right >= inner.Right &&
        outer.Bottom >= inner.Bottom;

    private static bool TryGetFilterCropPixelBounds(
        SKRect? cropRect,
        Matrix4x4 filterTransform,
        uint textureWidth,
        uint textureHeight,
        out int left,
        out int top,
        out int width,
        out int height)
    {
        left = 0;
        top = 0;
        width = 0;
        height = 0;
        if (cropRect is not { } crop || !IsValidLayerBounds(crop))
        {
            return false;
        }

        var transform = filterTransform == default ? Matrix4x4.Identity : filterTransform;
        var bounds = MapRectToBounds(crop, transform);
        if (!IsValidLayerBounds(bounds) ||
            bounds.Right <= 0f ||
            bounds.Bottom <= 0f ||
            bounds.Left >= textureWidth ||
            bounds.Top >= textureHeight)
        {
            return false;
        }

        const double coordinateLimit = 1_000_000_000d;
        left = (int)Math.Clamp(Math.Floor(bounds.Left), -coordinateLimit, coordinateLimit);
        top = (int)Math.Clamp(Math.Floor(bounds.Top), -coordinateLimit, coordinateLimit);
        var right = (int)Math.Clamp(Math.Ceiling(bounds.Right), -coordinateLimit, coordinateLimit);
        var bottom = (int)Math.Clamp(Math.Ceiling(bounds.Bottom), -coordinateLimit, coordinateLimit);
        width = right - left;
        height = bottom - top;
        return width is > 0 and <= 1_000_000_000 &&
            height is > 0 and <= 1_000_000_000;
    }

    private GpuTexture RenderImageLighting(
        GpuTexture input,
        SKImageFilter.FilterKind kind,
        object parameters,
        Matrix4x4 filterTransform)
    {
        Vector3 lightPosition;
        Vector3 lightTarget;
        Vector4 lightColor;
        float spotExponent;
        float surfaceScale;
        float lightingConstant;
        float shininess;
        float cutoffAngle;
        uint lightType;

        switch (parameters)
        {
            case SKImageFilter.DistantLightData distant:
                lightPosition = ToVector3(distant.Direction);
                lightTarget = Vector3.Zero;
                lightColor = ToVector4(distant.Color);
                spotExponent = 0f;
                surfaceScale = distant.SurfaceScale;
                lightingConstant = distant.Constant;
                shininess = distant.Shininess;
                cutoffAngle = 90f;
                lightType = 0u;
                break;
            case SKImageFilter.PointLightData point:
                lightPosition = ToVector3(point.Location);
                lightTarget = Vector3.Zero;
                lightColor = ToVector4(point.Color);
                spotExponent = 0f;
                surfaceScale = point.SurfaceScale;
                lightingConstant = point.Constant;
                shininess = point.Shininess;
                cutoffAngle = 90f;
                lightType = 1u;
                break;
            case SKImageFilter.SpotLightData spot:
                lightPosition = ToVector3(spot.Location);
                lightTarget = ToVector3(spot.Target);
                lightColor = ToVector4(spot.Color);
                spotExponent = spot.SpecularExponent;
                surfaceScale = spot.SurfaceScale;
                lightingConstant = spot.Constant;
                shininess = spot.Shininess;
                cutoffAngle = spot.CutoffAngle;
                lightType = 2u;
                break;
            default:
                return input;
        }

        TransformImageLight(
            ref lightPosition,
            ref lightTarget,
            ref surfaceScale,
            lightType,
            filterTransform);

        var specular = kind is
            SKImageFilter.FilterKind.DistantLitSpecular or
            SKImageFilter.FilterKind.PointLitSpecular or
            SKImageFilter.FilterKind.SpotLitSpecular;
        var context = GetGpuContext();
        var destination = CreateOwnedFilterTexture(
            context,
            "SKImageFilter Lighting Destination",
            storage: true);
        GetCompositorForContext(context).ApplyImageLighting(
            input,
            destination,
            lightPosition,
            lightType,
            lightTarget,
            spotExponent,
            lightColor,
            surfaceScale,
            lightingConstant,
            shininess,
            cutoffAngle,
            specular);
        return destination;
    }

    private static void TransformImageLight(
        ref Vector3 lightPosition,
        ref Vector3 lightTarget,
        ref float surfaceScale,
        uint lightType,
        Matrix4x4 transform)
    {
        if (transform == default)
        {
            transform = Matrix4x4.Identity;
        }

        var xScale = new Vector2(transform.M11, transform.M12).Length();
        var yScale = new Vector2(transform.M21, transform.M22).Length();
        var zScale = MathF.Sqrt(MathF.Max(xScale * yScale, 0f));
        if (!float.IsFinite(zScale) || zScale <= float.Epsilon)
        {
            zScale = 1f;
        }

        if (lightType == 0u)
        {
            lightPosition = Vector3.TransformNormal(
                new Vector3(lightPosition.X, lightPosition.Y, lightPosition.Z * zScale),
                transform);
        }
        else
        {
            lightPosition = TransformImageLightPoint(lightPosition, transform, zScale);
            if (lightType == 2u)
            {
                lightTarget = TransformImageLightPoint(lightTarget, transform, zScale);
            }
        }

        surfaceScale *= zScale;
    }

    private static Vector3 TransformImageLightPoint(
        Vector3 point,
        Matrix4x4 transform,
        float zScale)
    {
        var transformed = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return new Vector3(transformed, point.Z * zScale);
    }

    private GpuTexture RenderShaderFilter(
        SKShader? shader,
        Matrix4x4 filterTransform,
        uint width,
        uint height)
    {
        if (shader == null)
        {
            return RenderFilterPass(
                "SKImageFilter Empty Shader",
                width,
                height,
                static _ => { });
        }

        return RenderFilterPass(
            "SKImageFilter Shader",
            width,
            height,
            context =>
            {
                if (shader.PerlinNoise != null && shader.ToBrush() is PerlinNoiseBrush perlinNoise)
                {
                    var transform = filterTransform == default
                        ? Matrix4x4.Identity
                        : filterTransform;
                    if (Matrix4x4.Invert(transform, out var inverse))
                    {
                        perlinNoise.CoordinateTransform = inverse * perlinNoise.CoordinateTransform;
                    }
                    context.DrawRectangle(
                        perlinNoise,
                        null,
                        new Rect(0f, 0f, width, height));
                    return;
                }

                using var canvas = new SKCanvas(context, width, height, GetGpuContext());
                using var paint = new SKPaint { Shader = shader };
                canvas.DrawPaint(paint);
            });
    }

    private GpuTexture RenderTile(GpuTexture input, SKImageFilter.TileData tile)
    {
        if (tile.Source.Width <= 0f || tile.Source.Height <= 0f ||
            tile.Destination.Width <= 0f || tile.Destination.Height <= 0f)
        {
            return input;
        }

        return RenderFilterPass(
            "SKImageFilter Tile",
            input.Width,
            input.Height,
            context =>
            {
                context.PushClip(ToRect(tile.Destination));
                var startX = (int)MathF.Floor(
                    (tile.Destination.Left - tile.Source.Left) / tile.Source.Width);
                var endX = (int)MathF.Ceiling(
                    (tile.Destination.Right - tile.Source.Left) / tile.Source.Width);
                var startY = (int)MathF.Floor(
                    (tile.Destination.Top - tile.Source.Top) / tile.Source.Height);
                var endY = (int)MathF.Ceiling(
                    (tile.Destination.Bottom - tile.Source.Top) / tile.Source.Height);
                for (var y = startY; y < endY; y++)
                {
                    for (var x = startX; x < endX; x++)
                    {
                        context.DrawTexture(
                            input,
                            new Rect(
                                tile.Source.Left + x * tile.Source.Width,
                                tile.Source.Top + y * tile.Source.Height,
                                tile.Source.Width,
                                tile.Source.Height),
                            ToRect(tile.Source),
                            Matrix4x4.Identity,
                            TextureSamplingMode.Linear);
                    }
                }
                context.PopClip();
            });
    }

    private GpuTexture ApplyFilterCrop(
        GpuTexture input,
        SKRect? cropRect,
        Matrix4x4 filterTransform = default)
    {
        if (cropRect is not { } crop || !IsValidLayerBounds(crop) || IsFullCanvasLayerBounds(crop))
        {
            return input;
        }

        return RenderFilterPass(
            "SKImageFilter Crop",
            input.Width,
            input.Height,
            context =>
            {
                context.Commands.Add(new RenderCommand
                {
                    Type = RenderCommandType.PushClip,
                    Rect = ToRect(crop),
                    Transform = filterTransform == default ? Matrix4x4.Identity : filterTransform
                });
                context.DrawTexture(input, new Rect(0f, 0f, input.Width, input.Height));
                context.PopClip();
            });
    }

    private GpuTexture RenderFilterPass(
        string label,
        uint width,
        uint height,
        Action<DrawingContext> record)
    {
        var context = GetGpuContext();
        var texture = CreateOwnedFilterTexture(context, label, storage: false, width, height);
        var visual = new DrawingVisual { Size = new Vector2(width, height) };
        record(visual.Context);

        try
        {
            GetCompositorForContext(context).RenderOffscreen(
                visual,
                width,
                height,
                texture,
                padding: 0f,
                dpiScale: 1f,
                clearColor: Vector4.Zero);
            return texture;
        }
        catch
        {
            _ownedLayerTextures.Remove(texture);
            texture.Dispose();
            throw;
        }
        finally
        {
            visual.Context.Clear();
        }
    }

    private GpuTexture CreateOwnedFilterTexture(
        WgpuContext context,
        string label,
        bool storage,
        uint? width = null,
        uint? height = null)
    {
        var usage = TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst;
        usage |= storage ? TextureUsage.StorageBinding : TextureUsage.RenderAttachment;
        var texture = new GpuTexture(
            context,
            width ?? GetTextureWidth(),
            height ?? GetTextureHeight(),
            TextureFormat.Rgba8Unorm,
            usage,
            label,
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        _ownedLayerTextures.Add(texture);
        return texture;
    }

    private WgpuContext GetGpuContext() =>
        _gpuContext != null && !_gpuContext.IsDisposed
            ? _gpuContext
            : SKContextHelper.GetContext();

    private uint GetTextureWidth() => (uint)Math.Max(1d, Math.Ceiling(_width));

    private uint GetTextureHeight() => (uint)Math.Max(1d, Math.Ceiling(_height));

    private static Rect ToRect(SKRect rect) =>
        new(rect.Left, rect.Top, rect.Width, rect.Height);

    private static TextureSamplingMode MapSampling(SKSamplingOptions sampling) =>
        sampling.UseCubic
            ? TextureSamplingMode.Cubic
            : sampling.IsAniso || sampling.Mipmap != SKMipmapMode.None
                ? TextureSamplingMode.LinearMipmap
                : sampling.Filter == SKFilterMode.Nearest
                    ? TextureSamplingMode.Nearest
                    : TextureSamplingMode.Linear;

    private static TextureSamplingMode MapFilterMode(SKFilterMode filterMode) =>
        filterMode == SKFilterMode.Nearest
            ? TextureSamplingMode.Nearest
            : TextureSamplingMode.Linear;

    private static Vector2? MapCubicSampling(SKSamplingOptions sampling) =>
        sampling.UseCubic &&
        float.IsFinite(sampling.Cubic.B) &&
        float.IsFinite(sampling.Cubic.C)
            ? new Vector2(sampling.Cubic.B, sampling.Cubic.C)
            : null;

    private static byte MapMaxAnisotropy(SKSamplingOptions sampling) =>
        sampling.IsAniso
            ? (byte)Math.Clamp(sampling.MaxAniso, 1, 16)
            : (byte)1;

    private static Vector4 ToVector4(SKColor color)
    {
        return new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
    }

    private static Vector3 ToVector3(SKPoint3 point) => new(point.X, point.Y, point.Z);

    private GpuTexture RenderLayerToTexture(LayerFrame layerFrame)
    {
        var context = _gpuContext != null && !_gpuContext.IsDisposed
            ? _gpuContext
            : SKContextHelper.GetContext();
        var transformedBounds = MapRectToBounds(
            layerFrame.Bounds,
            layerFrame.BoundsMatrix.ToMatrix4x4());
        var textureWidth = GetRequiredTextureExtent(_width, transformedBounds.Right);
        var textureHeight = GetRequiredTextureExtent(_height, transformedBounds.Bottom);
        var textureFormat = GetSaveLayerTextureFormat(layerFrame.Flags);
        var texture = new GpuTexture(
            context,
            textureWidth,
            textureHeight,
            textureFormat,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKCanvas SaveLayer Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        var visual = new DrawingVisual { Size = new Vector2(textureWidth, textureHeight) };
        GpuTexture? initialTexture = null;
        var textureRetained = false;
        try
        {
            if (layerFrame.PreviousContext != null)
            {
                initialTexture = RenderPreviousLayerToTexture(
                    layerFrame,
                    context,
                    textureWidth,
                    textureHeight,
                    textureFormat);
                if (layerFrame.Backdrop != null)
                {
                    initialTexture = RenderImageFilterGraph(
                        initialTexture,
                        layerFrame.Backdrop,
                        layerFrame.BoundsMatrix.ToMatrix4x4());
                }
            }

            ReplayActiveClipPushes(visual.Context, layerFrame.ActiveClipPushes);
            var pushedLayerBoundsClip = PushLayerBoundsClip(visual.Context, layerFrame);
            if (initialTexture != null)
            {
                visual.Context.DrawTexture(
                    initialTexture,
                    new Rect(0f, 0f, initialTexture.Width, initialTexture.Height));
            }
            visual.Context.Append(layerFrame.LayerContext);
            if (pushedLayerBoundsClip)
            {
                visual.Context.PopClip();
            }
            PopReplayedClipPushes(visual.Context, layerFrame.ActiveClipPushes);

            GetCompositorForContext(context, textureFormat).RenderOffscreen(
                visual,
                textureWidth,
                textureHeight,
                texture,
                padding: 0f,
                dpiScale: 1f);

            layerFrame.LayerContext.Clear();
            _ownedLayerTextures.Add(texture);
            textureRetained = true;
            return texture;
        }
        finally
        {
            visual.Context.Clear();
            if (initialTexture != null)
            {
                ReleaseOwnedLayerTexture(initialTexture);
            }

            if (!textureRetained)
            {
                layerFrame.LayerContext.Clear();
                texture.Dispose();
            }
        }
    }

    private GpuTexture RenderPreviousLayerToTexture(
        LayerFrame layerFrame,
        WgpuContext context,
        uint textureWidth,
        uint textureHeight,
        TextureFormat textureFormat)
    {
        var texture = new GpuTexture(
            context,
            textureWidth,
            textureHeight,
            textureFormat,
            TextureUsage.RenderAttachment |
            TextureUsage.CopySrc |
            TextureUsage.CopyDst |
            TextureUsage.TextureBinding,
            "SKCanvas SaveLayer Previous Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        var visual = new DrawingVisual { Size = new Vector2(textureWidth, textureHeight) };

        var retained = false;
        try
        {
            if (_surface?.TryGetLayerBackdropTexture(out var backingTexture) == true)
            {
                visual.Context.DrawTexture(
                    backingTexture,
                    new Rect(0f, 0f, backingTexture.Width, backingTexture.Height));
            }

            visual.Context.Append(layerFrame.PreviousContext!);
            GetCompositorForContext(context, textureFormat).RenderOffscreen(
                visual,
                textureWidth,
                textureHeight,
                texture,
                padding: 0f,
                dpiScale: 1f,
                clearColor: Vector4.Zero);

            _ownedLayerTextures.Add(texture);
            retained = true;
            return texture;
        }
        finally
        {
            visual.Context.Clear();
            if (!retained)
            {
                texture.Dispose();
            }
        }
    }

    private static uint GetRequiredTextureExtent(float canvasExtent, float transformedExtent)
    {
        var extent = MathF.Max(canvasExtent, transformedExtent);
        if (!float.IsFinite(extent) || extent <= 0f)
        {
            return 1u;
        }

        return (uint)Math.Min(Math.Ceiling(extent), uint.MaxValue);
    }

    internal static TextureFormat GetSaveLayerTextureFormat(SKCanvasSaveLayerRecFlags flags) =>
        (flags & SKCanvasSaveLayerRecFlags.F16ColorType) != 0
            ? TextureFormat.Rgba16float
            : TextureFormat.Rgba8Unorm;

    private bool PushLayerBoundsClip(DrawingContext context, LayerFrame layerFrame)
    {
        if (IsFullCanvasLayerBounds(layerFrame.Bounds))
        {
            return false;
        }

        context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = new Rect(
                layerFrame.Bounds.Left,
                layerFrame.Bounds.Top,
                layerFrame.Bounds.Width,
                layerFrame.Bounds.Height),
            Transform = layerFrame.BoundsMatrix.ToMatrix4x4()
        });
        return true;
    }

    private RenderCommand[] SnapshotActiveClipPushes()
    {
        var clips = _activeClipPushes.ToArray();
        Array.Reverse(clips);
        return clips;
    }

    private static void ReplayActiveClipPushes(DrawingContext context, IReadOnlyList<RenderCommand> clipPushes)
    {
        for (int i = 0; i < clipPushes.Count; i++)
        {
            context.Commands.Add(clipPushes[i]);
        }
    }

    private static void PopReplayedClipPushes(DrawingContext context, IReadOnlyList<RenderCommand> clipPushes)
    {
        for (int i = clipPushes.Count - 1; i >= 0; i--)
        {
            switch (clipPushes[i].Type)
            {
                case RenderCommandType.PushClip:
                    context.PopClip();
                    break;
                case RenderCommandType.PushGeometryClip:
                    context.PopGeometryClip();
                    break;
            }
        }
    }

    private void PushRectClipScope(SKRect rect, Matrix4x4 transform)
    {
        var command = new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = new Rect(rect.Left, rect.Top, rect.Width, rect.Height),
            Transform = transform
        };
        _context.Commands.Add(command);
        _pushedScopes.Push(PushKind.RectClip);
        _activeClipPushes.Push(command);
    }

    private void PushGeometryClipScope(PathGeometry geometry, Matrix4x4 transform)
    {
        var command = new RenderCommand
        {
            Type = RenderCommandType.PushGeometryClip,
            Path = geometry,
            Transform = transform
        };
        _context.Commands.Add(command);
        _pushedScopes.Push(PushKind.GeometryClip);
        _activeClipPushes.Push(command);
    }

    private void PopActiveClipScope()
    {
        if (_activeClipPushes.Count > 0)
        {
            _activeClipPushes.Pop();
        }
    }

    private void GetPathDeviceBounds(SKPath path, out SKRect bounds, out bool isRect)
    {
        var transform = _currentMatrix.ToMatrix4x4();
        if (IsAxisAligned2DTransform(transform) && TryGetRectGeometry(path.Geometry, out var rect))
        {
            bounds = _currentMatrix.MapRect(rect);
            isRect = true;
            return;
        }

        var tightBounds = path.TightBounds;
        bounds = IsFiniteNonEmpty(tightBounds)
            ? _currentMatrix.MapRect(tightBounds)
            : SKRect.Empty;
        isRect = false;
    }

    private void UpdateClipForIntersection(SKRect deviceBounds, bool isRect)
    {
        if (_clipState.IsEmpty)
        {
            return;
        }

        var incomingBounds = ToDeviceBounds(deviceBounds, isRect);
        var current = _clipState.DeviceBounds;
        var intersection = new SKRectI(
            Math.Max(current.Left, incomingBounds.Left),
            Math.Max(current.Top, incomingBounds.Top),
            Math.Min(current.Right, incomingBounds.Right),
            Math.Min(current.Bottom, incomingBounds.Bottom));
        if (intersection.Right <= intersection.Left || intersection.Bottom <= intersection.Top)
        {
            _clipState = new ClipState(SKRectI.Empty, IsRect: false);
            return;
        }

        _clipState = new ClipState(intersection, _clipState.IsRect && isRect);
    }

    private void UpdateClipForDifference(SKRect deviceBounds, bool isRect)
    {
        if (_clipState.IsEmpty)
        {
            return;
        }

        var excluded = ToDeviceBounds(deviceBounds, isRect);
        var current = _clipState.DeviceBounds;
        if (excluded.Right <= current.Left || excluded.Bottom <= current.Top ||
            excluded.Left >= current.Right || excluded.Top >= current.Bottom)
        {
            return;
        }

        if (!_clipState.IsRect || !isRect)
        {
            _clipState = new ClipState(current, IsRect: false);
            return;
        }

        if (excluded.Left <= current.Left && excluded.Right >= current.Right &&
            excluded.Top <= current.Top && excluded.Bottom >= current.Bottom)
        {
            _clipState = new ClipState(SKRectI.Empty, IsRect: false);
            return;
        }

        if (excluded.Top <= current.Top && excluded.Bottom >= current.Bottom)
        {
            if (excluded.Left <= current.Left)
            {
                _clipState = CreateRectClipState(
                    excluded.Right,
                    current.Top,
                    current.Right,
                    current.Bottom);
                return;
            }

            if (excluded.Right >= current.Right)
            {
                _clipState = CreateRectClipState(
                    current.Left,
                    current.Top,
                    excluded.Left,
                    current.Bottom);
                return;
            }
        }

        if (excluded.Left <= current.Left && excluded.Right >= current.Right)
        {
            if (excluded.Top <= current.Top)
            {
                _clipState = CreateRectClipState(
                    current.Left,
                    excluded.Bottom,
                    current.Right,
                    current.Bottom);
                return;
            }

            if (excluded.Bottom >= current.Bottom)
            {
                _clipState = CreateRectClipState(
                    current.Left,
                    current.Top,
                    current.Right,
                    excluded.Top);
                return;
            }
        }

        _clipState = new ClipState(current, IsRect: false);
    }

    private static ClipState CreateRectClipState(int left, int top, int right, int bottom) =>
        right > left && bottom > top
            ? new ClipState(new SKRectI(left, top, right, bottom), IsRect: true)
            : new ClipState(SKRectI.Empty, IsRect: false);

    private static SKRectI ToDeviceBounds(SKRect bounds, bool roundToNearest)
    {
        if (!IsFiniteNonEmpty(bounds))
        {
            return SKRectI.Empty;
        }

        return roundToNearest
            ? new SKRectI(
                RoundDeviceCoordinate(bounds.Left),
                RoundDeviceCoordinate(bounds.Top),
                RoundDeviceCoordinate(bounds.Right),
                RoundDeviceCoordinate(bounds.Bottom))
            : new SKRectI(
                FloorDeviceCoordinate(bounds.Left),
                FloorDeviceCoordinate(bounds.Top),
                CeilingDeviceCoordinate(bounds.Right),
                CeilingDeviceCoordinate(bounds.Bottom));
    }

    private static bool IsFiniteNonEmpty(SKRect bounds) =>
        float.IsFinite(bounds.Left) &&
        float.IsFinite(bounds.Top) &&
        float.IsFinite(bounds.Right) &&
        float.IsFinite(bounds.Bottom) &&
        bounds.Right > bounds.Left &&
        bounds.Bottom > bounds.Top;

    private static int ToCanvasExtent(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            return 0;
        }

        return CeilingDeviceCoordinate(value);
    }

    private static int RoundDeviceCoordinate(float value) =>
        ClampDeviceCoordinate(MathF.Round(value, MidpointRounding.AwayFromZero));

    private static int FloorDeviceCoordinate(float value) =>
        ClampDeviceCoordinate(MathF.Floor(value));

    private static int CeilingDeviceCoordinate(float value) =>
        ClampDeviceCoordinate(MathF.Ceiling(value));

    private static int ClampDeviceCoordinate(float value) =>
        value <= int.MinValue
            ? int.MinValue
            : value >= int.MaxValue
                ? int.MaxValue
                : (int)value;

    private static bool IsValidLayerBounds(SKRect bounds)
    {
        return float.IsFinite(bounds.Left) &&
            float.IsFinite(bounds.Top) &&
            float.IsFinite(bounds.Right) &&
            float.IsFinite(bounds.Bottom) &&
            bounds.Width > 0f &&
            bounds.Height > 0f;
    }

    private bool IsFullCanvasLayerBounds(SKRect bounds)
    {
        return MathF.Abs(bounds.Left) < 0.0001f &&
            MathF.Abs(bounds.Top) < 0.0001f &&
            MathF.Abs(bounds.Width - _width) < 0.0001f &&
            MathF.Abs(bounds.Height - _height) < 0.0001f;
    }

    private static Compositor GetCompositorForContext(
        WgpuContext context,
        TextureFormat renderFormat = TextureFormat.Rgba8Unorm)
    {
        return SharedCompositorCache.GetOrCreate(context, renderFormat, s_compositorCacheScope);
    }

    private static void RemoveCachedCompositor(WgpuContext context)
    {
        SharedCompositorCache.Remove(context, s_compositorCacheScope);
    }

    private static GpuBlendMode MapBlendMode(SKBlendMode blendMode)
    {
        return blendMode switch
        {
            SKBlendMode.Clear => GpuBlendMode.Clear,
            SKBlendMode.Src => GpuBlendMode.Src,
            SKBlendMode.Dst => GpuBlendMode.Dst,
            SKBlendMode.SrcIn => GpuBlendMode.SrcIn,
            SKBlendMode.DstIn => GpuBlendMode.DstIn,
            SKBlendMode.SrcOut => GpuBlendMode.SrcOut,
            SKBlendMode.DstOut => GpuBlendMode.DstOut,
            SKBlendMode.SrcATop => GpuBlendMode.SrcAtop,
            SKBlendMode.DstATop => GpuBlendMode.DstAtop,
            SKBlendMode.Xor => GpuBlendMode.Xor,
            SKBlendMode.DstOver => GpuBlendMode.DstOver,
            SKBlendMode.Plus => GpuBlendMode.Plus,
            SKBlendMode.Modulate => GpuBlendMode.Modulate,
            SKBlendMode.Screen => GpuBlendMode.Screen,
            SKBlendMode.Multiply => GpuBlendMode.Multiply,
            SKBlendMode.Darken => GpuBlendMode.Darken,
            SKBlendMode.Lighten => GpuBlendMode.Lighten,
            SKBlendMode.Exclusion => GpuBlendMode.Exclusion,
            SKBlendMode.Overlay => GpuBlendMode.Overlay,
            SKBlendMode.ColorDodge => GpuBlendMode.ColorDodge,
            SKBlendMode.ColorBurn => GpuBlendMode.ColorBurn,
            SKBlendMode.HardLight => GpuBlendMode.HardLight,
            SKBlendMode.SoftLight => GpuBlendMode.SoftLight,
            SKBlendMode.Difference => GpuBlendMode.Difference,
            SKBlendMode.Hue => GpuBlendMode.Hue,
            SKBlendMode.Saturation => GpuBlendMode.Saturation,
            SKBlendMode.Color => GpuBlendMode.Color,
            SKBlendMode.Luminosity => GpuBlendMode.Luminosity,
            _ => GpuBlendMode.SrcOver
        };
    }

    private bool PushPaintBlendMode(SKPaint? paint)
    {
        if (paint?.Blender?.IsArithmetic == true)
        {
            throw new NotSupportedException(
                "Arithmetic SKBlender rendering requires destination-sampling compositor support.");
        }

        var blendMode = MapBlendMode(paint?.BlendMode ?? SKBlendMode.SrcOver);
        if (blendMode == GpuBlendMode.SrcOver)
        {
            return false;
        }

        _context.PushBlendMode(blendMode);
        return true;
    }

    private void PopPaintBlendMode(bool pushedBlendMode)
    {
        if (pushedBlendMode)
        {
            _context.PopBlendMode();
        }
    }

    public void Translate(float dx, float dy)
    {
        _currentMatrix.TransX += dx * _currentMatrix.ScaleX + dy * _currentMatrix.SkewX;
        _currentMatrix.TransY += dx * _currentMatrix.SkewY + dy * _currentMatrix.ScaleY;
    }

    public void Translate(SKPoint point)
    {
        if (!point.IsEmpty)
        {
            Translate(point.X, point.Y);
        }
    }

    public void Scale(float scale)
    {
        if (scale != 1f)
        {
            Scale(scale, scale);
        }
    }

    public void Scale(float sx, float sy)
    {
        _currentMatrix.ScaleX *= sx;
        _currentMatrix.SkewY *= sx;
        _currentMatrix.SkewX *= sy;
        _currentMatrix.ScaleY *= sy;
    }

    public void Scale(SKPoint size)
    {
        if (!size.IsEmpty)
        {
            Scale(size.X, size.Y);
        }
    }

    public void Scale(float sx, float sy, float px, float py)
    {
        if (sx == 1f && sy == 1f)
        {
            return;
        }

        Translate(px, py);
        Scale(sx, sy);
        Translate(-px, -py);
    }

    public void RotateDegrees(float degrees)
    {
        if ((double)degrees % 360d != 0d)
        {
            Concat(SKMatrix.CreateRotationDegrees(degrees));
        }
    }

    public void RotateDegrees(float degrees, float px, float py)
    {
        if ((double)degrees % 360d == 0d)
        {
            return;
        }

        Translate(px, py);
        RotateDegrees(degrees);
        Translate(-px, -py);
    }

    public void RotateRadians(float radians)
    {
        if ((double)radians % (Math.PI * 2d) != 0d)
        {
            Concat(SKMatrix.CreateRotation(radians));
        }
    }

    public void RotateRadians(float radians, float px, float py)
    {
        if ((double)radians % (Math.PI * 2d) == 0d)
        {
            return;
        }

        Translate(px, py);
        RotateRadians(radians);
        Translate(-px, -py);
    }

    public void Skew(float sx, float sy)
    {
        if (sx != 0f || sy != 0f)
        {
            Concat(SKMatrix.CreateSkew(sx, sy));
        }
    }

    public void Skew(SKPoint skew)
    {
        if (!skew.IsEmpty)
        {
            Skew(skew.X, skew.Y);
        }
    }

    public void SetMatrix(SKMatrix matrix)
    {
        _currentMatrix = matrix;
    }

    public void SetMatrix(in SKMatrix matrix)
    {
        _currentMatrix = matrix;
    }

    public void SetMatrix(in SKMatrix44 matrix)
    {
        _currentMatrix = SKMatrix.FromMatrix4x4(matrix.ToMatrix4x4());
    }

    public void ResetMatrix()
    {
        _currentMatrix = SKMatrix.Identity;
    }

    public void Concat(in SKMatrix matrix)
    {
        _currentMatrix = SKMatrix.Concat(_currentMatrix, matrix);
    }

    public void Concat(in SKMatrix44 matrix)
    {
        var matrix2D = SKMatrix.FromMatrix4x4(matrix.ToMatrix4x4());
        _currentMatrix = SKMatrix.Concat(_currentMatrix, matrix2D);
    }

    public bool GetLocalClipBounds(out SKRect bounds)
    {
        if (_clipState.IsEmpty || !_currentMatrix.TryInvert(out var inverse))
        {
            bounds = SKRect.Empty;
            return false;
        }

        var device = _clipState.DeviceBounds;
        var outsetDeviceBounds = new SKRect(
            device.Left - 1f,
            device.Top - 1f,
            device.Right + 1f,
            device.Bottom + 1f);
        bounds = inverse.MapRect(outsetDeviceBounds);
        if (!IsFiniteNonEmpty(bounds))
        {
            bounds = SKRect.Empty;
            return false;
        }

        return true;
    }

    public bool GetDeviceClipBounds(out SKRectI bounds)
    {
        if (_clipState.IsEmpty)
        {
            bounds = SKRectI.Empty;
            return false;
        }

        bounds = _clipState.DeviceBounds;
        return true;
    }

    public bool QuickReject(SKRect rect)
    {
        if (rect.IsEmpty || _clipState.IsEmpty)
        {
            return true;
        }

        var deviceBounds = _currentMatrix.MapRect(rect);
        var clip = _clipState.DeviceBounds;
        return deviceBounds.Right <= clip.Left ||
            deviceBounds.Bottom <= clip.Top ||
            deviceBounds.Left >= clip.Right ||
            deviceBounds.Top >= clip.Bottom;
    }

    public bool QuickReject(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return QuickReject(path.Bounds);
    }

    public void ClipRect(SKRect rect, SKClipOperation operation = SKClipOperation.Intersect, bool antialias = true)
    {
        var transform = _currentMatrix.ToMatrix4x4();
        var isDeviceRect = IsAxisAligned2DTransform(transform);
        var deviceBounds = _currentMatrix.MapRect(rect);
        if (operation == SKClipOperation.Difference)
        {
            UpdateClipForDifference(deviceBounds, isDeviceRect);
            var excluded = CreateRectGeometry(rect).CreateTransformed(transform);
            PushGeometryClipScope(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            return;
        }

        UpdateClipForIntersection(deviceBounds, isDeviceRect);
        PushRectClipScope(rect, transform);
    }

    public void ClipPath(SKPath? path, SKClipOperation operation = SKClipOperation.Intersect, bool antialias = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        GetPathDeviceBounds(path, out var deviceBounds, out var isDeviceRect);
        if (operation == SKClipOperation.Difference)
        {
            if (IsInverseFillType(path.FillType))
            {
                UpdateClipForIntersection(deviceBounds, isDeviceRect);
                PushGeometryClipScope(path.Geometry, _currentMatrix.ToMatrix4x4());
            }
            else
            {
                UpdateClipForDifference(deviceBounds, isDeviceRect);
                var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
                PushGeometryClipScope(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            }
            return;
        }

        if (IsInverseFillType(path.FillType))
        {
            UpdateClipForDifference(deviceBounds, isDeviceRect);
            var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
            PushGeometryClipScope(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            return;
        }

        var transform = _currentMatrix.ToMatrix4x4();
        if (IsAxisAligned2DTransform(transform) && TryGetRectGeometry(path.Geometry, out var rect))
        {
            UpdateClipForIntersection(deviceBounds, isDeviceRect);
            PushRectClipScope(rect, transform);
            return;
        }

        UpdateClipForIntersection(deviceBounds, isDeviceRect);
        PushGeometryClipScope(path.Geometry, transform);
    }

    public void ClipRoundRect(
        SKRoundRect? rect,
        SKClipOperation operation = SKClipOperation.Intersect,
        bool antialias = false)
    {
        ArgumentNullException.ThrowIfNull(rect);
        using var path = new SKPath();
        path.AddRoundRect(rect);
        ClipPath(path, operation, antialias);
    }

    public void ClipRegion(SKRegion region, SKClipOperation operation = SKClipOperation.Intersect)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (operation == SKClipOperation.Intersect && _context.Commands.Count == 0 && _cpuReadbackRegions == null)
        {
            _cpuReadbackRegions = new List<SKRect>(region.Rects.Count);
            foreach (var rect in region.Rects)
            {
                _cpuReadbackRegions.Add(MapRectToBounds(
                    new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    _currentMatrix.ToMatrix4x4()));
            }
        }

        using var path = CreateRegionPath(region);
        ClipPath(path, operation, antialias: false);
    }

    internal SKRect[]? TakeCpuReadbackRegions()
    {
        if (_cpuReadbackRegions == null)
        {
            return null;
        }

        var regions = _cpuReadbackRegions.ToArray();
        _cpuReadbackRegions = null;
        return regions;
    }

    public void DrawAnnotation(SKRect rect, string key, SKData? value)
    {
        // Raster canvases intentionally ignore document-only annotation metadata.
    }

    public void DrawUrlAnnotation(SKRect rect, SKData? value)
    {
        // Raster canvases intentionally ignore document-only annotation metadata.
    }

    public SKData DrawUrlAnnotation(SKRect rect, string value)
    {
        var data = SKData.FromCString(value);
        DrawUrlAnnotation(rect, data);
        return data;
    }

    public void DrawNamedDestinationAnnotation(SKPoint point, SKData? value)
    {
        // Raster canvases intentionally ignore document-only annotation metadata.
    }

    public SKData DrawNamedDestinationAnnotation(SKPoint point, string value)
    {
        var data = SKData.FromCString(value);
        DrawNamedDestinationAnnotation(point, data);
        return data;
    }

    public void DrawLinkDestinationAnnotation(SKRect rect, SKData? value)
    {
        // Raster canvases intentionally ignore document-only annotation metadata.
    }

    public SKData DrawLinkDestinationAnnotation(SKRect rect, string value)
    {
        var data = SKData.FromCString(value);
        DrawLinkDestinationAnnotation(rect, data);
        return data;
    }

    private static SKRect MapRectToBounds(SKRect rect, Matrix4x4 matrix)
    {
        var topLeft = Vector2.Transform(new Vector2(rect.Left, rect.Top), matrix);
        var topRight = Vector2.Transform(new Vector2(rect.Right, rect.Top), matrix);
        var bottomRight = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), matrix);
        var bottomLeft = Vector2.Transform(new Vector2(rect.Left, rect.Bottom), matrix);
        return new SKRect(
            MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomRight.X, bottomLeft.X)),
            MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomRight.Y, bottomLeft.Y)),
            MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomRight.X, bottomLeft.X)),
            MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomRight.Y, bottomLeft.Y)));
    }

    private void DrawPictureCore(SKPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var sourcePicture = picture.Picture;
        if (_isPictureRecording)
        {
            _context.DrawPictureTransformed(sourcePicture, _currentMatrix.ToMatrix4x4());
            return;
        }

        var playbackPicture = CreateColorManagedPictureForPlayback(
            sourcePicture,
            new Dictionary<GpuPicture, GpuPicture>(),
            new Dictionary<GpuTexture, GpuTexture>());
        if (!ReferenceEquals(playbackPicture, sourcePicture))
        {
            _context.RetainResource(sourcePicture.Clone());
        }

        _context.DrawPictureTransformed(playbackPicture, _currentMatrix.ToMatrix4x4());
    }

    private GpuPicture CreateColorManagedPictureForPlayback(
        GpuPicture picture,
        Dictionary<GpuPicture, GpuPicture> pictureCache,
        Dictionary<GpuTexture, GpuTexture> textureCache)
    {
        if (pictureCache.TryGetValue(picture, out var cached))
        {
            return cached;
        }

        pictureCache[picture] = picture;
        RenderCommand[]? convertedCommands = null;
        var commands = picture.Commands;
        for (var index = 0; index < commands.Length; index++)
        {
            var command = commands[index];
            if (command.Type == RenderCommandType.DrawTexture &&
                command.Texture is { } texture &&
                s_textureColorSpaces.TryGetValue(texture, out var textureColorSpace))
            {
                if (!textureCache.TryGetValue(texture, out var convertedTexture))
                {
                    convertedTexture = ConvertImageTextureToSrgb(texture, textureColorSpace.Value);
                    textureCache[texture] = convertedTexture;
                }

                if (!ReferenceEquals(convertedTexture, texture))
                {
                    convertedCommands ??= (RenderCommand[])commands.Clone();
                    command.Texture = convertedTexture;
                    convertedCommands[index] = command;
                }
            }
            else if (command.Type == RenderCommandType.DrawPicture && command.Picture is { } nestedPicture)
            {
                var convertedPicture = CreateColorManagedPictureForPlayback(
                    nestedPicture,
                    pictureCache,
                    textureCache);
                if (!ReferenceEquals(convertedPicture, nestedPicture))
                {
                    convertedCommands ??= (RenderCommand[])commands.Clone();
                    command.Picture = convertedPicture;
                    convertedCommands[index] = command;
                }
            }
        }

        if (convertedCommands == null)
        {
            return picture;
        }

        var converted = new GpuPicture(
            convertedCommands,
            picture.PointBuffer,
            picture.DoubleBuffer,
            picture.Line3DBuffer,
            picture.FloatBuffer);
        pictureCache[picture] = converted;
        return converted;
    }

    public void DrawPicture(SKPicture picture, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var pushedBlendMode = PushPaintBlendMode(paint);
        var pushedOpacity = false;
        try
        {
            var opacity = paint?.Color.A / 255f ?? 1f;
            if (opacity < 1f)
            {
                _context.PushOpacity(opacity);
                pushedOpacity = true;
            }

            DrawPictureCore(picture);
        }
        finally
        {
            if (pushedOpacity)
            {
                _context.PopOpacity();
            }

            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawPicture(SKPicture picture, float x, float y, SKPaint? paint = null)
    {
        var matrix = SKMatrix.CreateTranslation(x, y);
        DrawPicture(picture, in matrix, paint);
    }

    public void DrawPicture(SKPicture picture, SKPoint point, SKPaint? paint = null) =>
        DrawPicture(picture, point.X, point.Y, paint);

    public void DrawPicture(SKPicture picture, in SKMatrix matrix, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var previousMatrix = _currentMatrix;
        try
        {
            _currentMatrix = SKMatrix.Concat(previousMatrix, matrix);
            DrawPicture(picture, paint);
        }
        finally
        {
            _currentMatrix = previousMatrix;
        }
    }

    public void DrawDrawable(SKDrawable drawable, in SKMatrix matrix) =>
        drawable.Draw(this, in matrix);

    public void DrawDrawable(SKDrawable drawable, SKPoint point) =>
        drawable.Draw(this, point.X, point.Y);

    public void DrawDrawable(SKDrawable drawable, float x, float y) =>
        drawable.Draw(this, x, y);

    public void DrawLine(float x0, float y0, float x1, float y1, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(x0, y0);
        path.LineTo(x1, y1);
        DrawPath(path, paint);
    }

    public void DrawLine(SKPoint point0, SKPoint point1, SKPaint paint) =>
        DrawLine(point0.X, point0.Y, point1.X, point1.Y, paint);

    public void DrawArc(
        SKRect oval,
        float startAngle,
        float sweepAngle,
        bool useCenter,
        SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        if (oval.IsEmpty || sweepAngle == 0f)
        {
            return;
        }

        var radiusX = oval.Width * 0.5f;
        var radiusY = oval.Height * 0.5f;
        var center = new SKPoint(oval.MidX, oval.MidY);
        var startRadians = startAngle * (MathF.PI / 180f);
        var clampedSweep = Math.Clamp(sweepAngle, -360f, 360f);
        var start = new SKPoint(
            center.X + MathF.Cos(startRadians) * radiusX,
            center.Y + MathF.Sin(startRadians) * radiusY);

        using var path = new SKPath();
        if (MathF.Abs(clampedSweep) >= 360f)
        {
            path.AddOval(
                oval,
                clampedSweep >= 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise);
        }
        else
        {
            if (useCenter)
            {
                path.MoveTo(center);
                path.LineTo(start);
            }
            else
            {
                path.MoveTo(start);
            }

            var endRadians = (startAngle + clampedSweep) * (MathF.PI / 180f);
            path.ArcTo(
                radiusX,
                radiusY,
                0f,
                MathF.Abs(clampedSweep) > 180f ? SKPathArcSize.Large : SKPathArcSize.Small,
                clampedSweep >= 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                center.X + MathF.Cos(endRadians) * radiusX,
                center.Y + MathF.Sin(endRadians) * radiusY);
            if (useCenter)
            {
                path.Close();
            }
        }

        DrawPath(path, paint);
    }

    public void DrawPoints(SKPointMode mode, SKPoint[] points, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        ArgumentNullException.ThrowIfNull(points);
        DrawPointsCore(mode, points, paint);
    }

    private void DrawPointsCore(SKPointMode mode, ReadOnlySpan<SKPoint> points, SKPaint paint)
    {
        if (points.Length == 0)
        {
            return;
        }

        using var path = new SKPath();
        switch (mode)
        {
            case SKPointMode.Points:
                foreach (var point in points)
                {
                    path.MoveTo(point);
                    path.LineTo(point);
                }
                break;
            case SKPointMode.Lines:
                for (var index = 0; index + 1 < points.Length; index += 2)
                {
                    path.MoveTo(points[index]);
                    path.LineTo(points[index + 1]);
                }
                break;
            case SKPointMode.Polygon:
                path.MoveTo(points[0]);
                for (var index = 1; index < points.Length; index++)
                {
                    path.LineTo(points[index]);
                }
                break;
            default:
                return;
        }

        using var strokePaint = paint.Clone();
        strokePaint.Style = SKPaintStyle.Stroke;
        DrawPath(path, strokePaint);
    }

    public void DrawPoint(SKPoint point, SKPaint paint) => DrawPoint(point.X, point.Y, paint);

    public void DrawPoint(float x, float y, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        Span<SKPoint> point = stackalloc SKPoint[1];
        point[0] = new SKPoint(x, y);
        DrawPointsCore(SKPointMode.Points, point, paint);
    }

    public void DrawPoint(SKPoint point, SKColor color) => DrawPoint(point.X, point.Y, color);

    public void DrawPoint(float x, float y, SKColor color)
    {
        using var paint = new SKPaint
        {
            Color = color,
            BlendMode = SKBlendMode.Src,
        };
        DrawPoint(x, y, paint);
    }

    public void DrawRect(float x, float y, float w, float h, SKPaint paint)
    {
        var rect = new SKRect(x, y, x + w, y + h);
        if (TryDrawSpecialShader(CreateRectGeometry(rect), rect, paint))
        {
            return;
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            var brush = paint.ToBrush();
            var pen = paint.ToLocalPen(GetCurrentStrokeScale());
            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(x, y, w, h),
                Brush = brush,
                Pen = pen,
                Transform = _currentMatrix.ToMatrix4x4(),
                IsEdgeAliased = !paint.IsAntialias,
                IsPenThicknessLocal = true
            });
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawRect(SKRect rect, SKPaint paint) => DrawRect(rect.Left, rect.Top, rect.Width, rect.Height, paint);

    public void DrawRoundRect(SKRoundRect? rect, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(rect);
        if (HasSpecialShader(paint.Shader))
    {
            using var clipPath = new SKPath();
            clipPath.AddRoundRect(rect);
            if (TryDrawSpecialShader(clipPath.Geometry, rect.Rect, paint))
            {
                return;
            }
        }

        if (!TryGetUniformRadii(rect, out var radiusX, out var radiusY))
        {
            using var path = new SKPath();
            path.AddRoundRect(rect);
            DrawPath(path, paint);
            return;
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            var brush = paint.ToBrush();
            var pen = paint.ToLocalPen(GetCurrentStrokeScale());
            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                Rect = new Rect(rect.Rect.Left, rect.Rect.Top, rect.Rect.Width, rect.Rect.Height),
                RadiusX = radiusX,
                RadiusY = radiusY,
                Brush = brush,
                Pen = pen,
                Transform = _currentMatrix.ToMatrix4x4(),
                IsEdgeAliased = !paint.IsAntialias,
                IsPenThicknessLocal = true
            });
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawRoundRect(SKRect rect, float rx, float ry, SKPaint paint)
    {
        DrawRoundRect(new SKRoundRect(rect, rx, ry), paint);
    }

    public void DrawRoundRect(float x, float y, float width, float height, float rx, float ry, SKPaint paint) =>
        DrawRoundRect(new SKRect(x, y, x + width, y + height), rx, ry, paint);

    public void DrawRoundRect(SKRect rect, SKSize radius, SKPaint paint) =>
        DrawRoundRect(rect, radius.Width, radius.Height, paint);

    private static bool TryGetUniformRadii(SKRoundRect rect, out float radiusX, out float radiusY)
    {
        radiusX = rect.CornerRadii[0].X;
        radiusY = rect.CornerRadii[0].Y;
        for (int i = 1; i < rect.CornerRadii.Length; i++)
        {
            if (MathF.Abs(rect.CornerRadii[i].X - radiusX) > 0.0001f ||
                MathF.Abs(rect.CornerRadii[i].Y - radiusY) > 0.0001f)
            {
                return false;
            }
        }

        return true;
    }

    public void DrawOval(SKRect rect, SKPaint paint)
    {
        if (HasSpecialShader(paint.Shader))
        {
            using var clipPath = new SKPath();
            AddOvalPath(clipPath, rect);
            if (TryDrawSpecialShader(clipPath.Geometry, rect, paint))
            {
                return;
            }
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            var brush = paint.ToBrush();
            var pen = paint.ToLocalPen(GetCurrentStrokeScale());
            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawEllipse,
                Position2 = new Vector2(rect.MidX, rect.MidY),
                RadiusX = rect.Width / 2f,
                RadiusY = rect.Height / 2f,
                Brush = brush,
                Pen = pen,
                Transform = _currentMatrix.ToMatrix4x4(),
                IsEdgeAliased = !paint.IsAntialias,
                IsPenThicknessLocal = true
            });
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawOval(float cx, float cy, float rx, float ry, SKPaint paint) =>
        DrawOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), paint);

    public void DrawOval(SKPoint center, SKSize radius, SKPaint paint) =>
        DrawOval(center.X, center.Y, radius.Width, radius.Height, paint);

    public void DrawCircle(float cx, float cy, float radius, SKPaint paint)
    {
        if (HasSpecialShader(paint.Shader))
        {
            using var clipPath = new SKPath();
            clipPath.AddCircle(cx, cy, radius);
            var bounds = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
            if (TryDrawSpecialShader(clipPath.Geometry, bounds, paint))
            {
                return;
            }
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            var brush = paint.ToBrush();
            var pen = paint.ToLocalPen(GetCurrentStrokeScale());
            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawCircle,
                Position2 = new Vector2(cx, cy),
                RadiusX = radius,
                Brush = brush,
                Pen = pen,
                Transform = _currentMatrix.ToMatrix4x4(),
                IsEdgeAliased = !paint.IsAntialias,
                IsPenThicknessLocal = true
            });
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawCircle(SKPoint center, float radius, SKPaint paint) =>
        DrawCircle(center.X, center.Y, radius, paint);

    public void DrawRoundRectDifference(SKRoundRect outer, SKRoundRect inner, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        using var outerPath = new SKPath();
        using var innerPath = new SKPath();
        outerPath.AddRoundRect(outer);
        innerPath.AddRoundRect(inner);
        using var difference = outerPath.Op(innerPath, SKPathOp.Difference);
        DrawPath(difference, paint);
    }

    public void DrawRegion(SKRegion region, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(region);
        using var path = CreateRegionPath(region);
        DrawPath(path, paint);
    }

    public void DrawPaint(SKPaint paint)
    {
        DrawRect(new SKRect(0f, 0f, _width, _height), paint);
    }

    public void DrawPath(SKPath path, SKPaint paint)
    {
        if (paint.PathEffect is { IsDash: false } pathEffect)
        {
            var applied = pathEffect.TryApply(path, GetCurrentStrokeScale(), out var effectedPath);
            if (applied)
            {
                using (effectedPath)
                using (var effectedPaint = paint.Clone())
                {
                    effectedPaint.PathEffect = null;
                    DrawPath(effectedPath, effectedPaint);
                }
                return;
            }
            effectedPath.Dispose();
        }

        if (TryDrawSpecialShader(path.Geometry, path.Bounds, paint))
        {
            return;
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            var brush = paint.ToBrush();
            var pen = paint.ToPen(GetCurrentStrokeScale());

            if (IsInverseFillType(path.FillType))
            {
                if (brush != null)
                {
                    var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
                    AddDrawPathCommand(CreateCanvasDifferenceGeometry(excluded), brush, null, Matrix4x4.Identity, !paint.IsAntialias);
                }

                if (pen != null)
                {
                    AddDrawPathCommand(path.Geometry, null, pen, _currentMatrix.ToMatrix4x4(), !paint.IsAntialias);
                }

                return;
            }

            AddDrawPathCommand(path.Geometry, brush, pen, _currentMatrix.ToMatrix4x4(), !paint.IsAntialias);
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawVertices(
        SKVertexMode vmode,
        SKPoint[] vertices,
        SKColor[] colors,
        SKPaint paint)
    {
        using var copy = SKVertices.CreateCopy(vmode, vertices, colors);
        DrawVertices(copy, SKBlendMode.Modulate, paint);
    }

    public void DrawVertices(
        SKVertexMode vmode,
        SKPoint[] vertices,
        SKPoint[] texs,
        SKColor[] colors,
        SKPaint paint)
    {
        using var copy = SKVertices.CreateCopy(vmode, vertices, texs, colors);
        DrawVertices(copy, SKBlendMode.Modulate, paint);
    }

    public void DrawVertices(
        SKVertexMode vmode,
        SKPoint[] vertices,
        SKPoint[] texs,
        SKColor[] colors,
        ushort[] indices,
        SKPaint paint)
    {
        using var copy = SKVertices.CreateCopy(vmode, vertices, texs, colors, indices);
        DrawVertices(copy, SKBlendMode.Modulate, paint);
    }

    public void DrawVertices(
        SKVertexMode vmode,
        SKPoint[] vertices,
        SKPoint[] texs,
        SKColor[] colors,
        SKBlendMode mode,
        ushort[] indices,
        SKPaint paint)
    {
        using var copy = SKVertices.CreateCopy(vmode, vertices, texs, colors, indices);
        DrawVertices(copy, mode, paint);
    }

    public void DrawVertices(SKVertices vertices, SKBlendMode mode, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(paint);
        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            _context.DrawVertexMesh(
                paint.ToFillBrush(),
                vertices.Mesh,
                (VertexColorBlendMode)mode,
                _currentMatrix.ToMatrix4x4(),
                !paint.IsAntialias);
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawPatch(
        SKPoint[] cubics,
        SKColor[]? colors,
        SKPoint[]? texCoords,
        SKPaint paint) =>
        DrawPatch(cubics, colors, texCoords, SKBlendMode.Modulate, paint);

    public void DrawPatch(
        SKPoint[] cubics,
        SKColor[]? colors,
        SKPoint[]? texCoords,
        SKBlendMode mode,
        SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(cubics);
        if (cubics.Length != 12)
        {
            throw new ArgumentException("Cubics must have a length of 12.", nameof(cubics));
        }
        if (colors != null && colors.Length != 4)
        {
            throw new ArgumentException("Colors must have a length of 4.", nameof(colors));
        }
        if (texCoords != null && texCoords.Length != 4)
        {
            throw new ArgumentException(
                "Texture coordinates must have a length of 4.",
                nameof(texCoords));
        }
        ArgumentNullException.ThrowIfNull(paint);

        var transform = _currentMatrix.ToMatrix4x4();
        var mesh = SKPatchLayout.CreateMesh(cubics, colors, texCoords, transform);
        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            _context.DrawVertexMesh(
                paint.ToFillBrush(),
                mesh,
                (VertexColorBlendMode)mode,
                transform,
                !paint.IsAntialias);
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    private void AddDrawPathCommand(
        PathGeometry path,
        Brush? brush,
        Pen? pen,
        Matrix4x4 transform,
        bool isEdgeAliased = false)
    {
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            Brush = brush,
            Pen = pen,
            Transform = transform,
            IsEdgeAliased = isEdgeAliased
        });
    }

    private float GetCurrentStrokeScale()
    {
        return TransformMetrics.GetStrokeScale(_currentMatrix.ToMatrix4x4());
    }

    private PathGeometry CreateCanvasDifferenceGeometry(PathGeometry excluded)
    {
        return new PathGeometry
        {
            IsCombined = true,
            PathA = CreateCanvasBoundsGeometry(),
            PathB = excluded,
            Op = (int)SKPathOp.Difference,
            FillRule = FillRule.Nonzero
        };
    }

    private PathGeometry CreateCanvasBoundsGeometry()
    {
        return CreateRectGeometry(new SKRect(0f, 0f, _width, _height));
    }

    private static PathGeometry CreateRectGeometry(SKRect rect)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure(new Vector2(rect.Left, rect.Top), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(rect.Right, rect.Top)));
        figure.Segments.Add(new LineSegment(new Vector2(rect.Right, rect.Bottom)));
        figure.Segments.Add(new LineSegment(new Vector2(rect.Left, rect.Bottom)));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static bool TryGetRectGeometry(PathGeometry geometry, out SKRect rect)
    {
        rect = default;
        if (geometry.IsCombined || geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = geometry.Figures[0];
        if (!figure.IsClosed || figure.Segments.Count is < 3 or > 8)
        {
            return false;
        }

        Span<Vector2> points = stackalloc Vector2[9];
        var count = 1;
        points[0] = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            Vector2 point;
            if (segment is LineSegment line)
            {
                point = line.Point;
            }
            else if (segment is ArcSegment arc &&
                     (NearlyEqual(arc.Size.X, 0f) || NearlyEqual(arc.Size.Y, 0f)))
            {
                point = arc.Point;
            }
            else
            {
                return false;
            }

            if (!NearlyEqual(point, points[count - 1]))
            {
                points[count++] = point;
            }
        }

        if (count > 1 && NearlyEqual(points[count - 1], points[0]))
        {
            count--;
        }

        if (count != 4)
        {
            return false;
        }

        var minX = points[0].X;
        var minY = points[0].Y;
        var maxX = minX;
        var maxY = minY;
        for (var i = 1; i < count; i++)
        {
            minX = MathF.Min(minX, points[i].X);
            minY = MathF.Min(minY, points[i].Y);
            maxX = MathF.Max(maxX, points[i].X);
            maxY = MathF.Max(maxY, points[i].Y);
        }

        if (maxX <= minX || maxY <= minY)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % count];
            var isCorner = (NearlyEqual(current.X, minX) || NearlyEqual(current.X, maxX))
                && (NearlyEqual(current.Y, minY) || NearlyEqual(current.Y, maxY));
            var isAxisAlignedEdge = NearlyEqual(current.X, next.X) != NearlyEqual(current.Y, next.Y);
            if (!isCorner || !isAxisAlignedEdge)
            {
                return false;
            }
        }

        rect = new SKRect(minX, minY, maxX, maxY);
        return true;
    }

    private static bool IsAxisAligned2DTransform(Matrix4x4 transform)
    {
        const float epsilon = 0.0001f;
        return MathF.Abs(transform.M12) <= epsilon && MathF.Abs(transform.M21) <= epsilon;
    }

    private static bool NearlyEqual(Vector2 left, Vector2 right)
    {
        return NearlyEqual(left.X, right.X) && NearlyEqual(left.Y, right.Y);
    }

    private static bool NearlyEqual(float left, float right)
    {
        return MathF.Abs(left - right) <= 0.0001f;
    }

    private static SKPath CreateRegionPath(SKRegion region)
    {
        var path = new SKPath();
        foreach (var rect in region.Rects)
        {
            path.AddRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
        }

        return path;
    }

    private static void AddOvalPath(SKPath path, SKRect rect)
    {
        var radiusX = rect.Width / 2f;
        var radiusY = rect.Height / 2f;
        var centerX = rect.MidX;
        var centerY = rect.MidY;
        path.MoveTo(centerX - radiusX, centerY);
        path.ArcTo(radiusX, radiusY, 0f, SKPathArcSize.Large, SKPathDirection.Clockwise, centerX + radiusX, centerY);
        path.ArcTo(radiusX, radiusY, 0f, SKPathArcSize.Large, SKPathDirection.Clockwise, centerX - radiusX, centerY);
        path.Close();
    }

    private bool TryDrawSpecialShader(PathGeometry clipGeometry, SKRect targetBounds, SKPaint paint)
    {
        var shader = paint.Shader;
        if (!HasSpecialShader(shader))
        {
            return false;
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        var pushedOpacity = false;
        try
        {
            var opacity = paint.Color.A / 255f;
            if (opacity < 1f)
            {
                _context.PushOpacity(opacity);
                pushedOpacity = true;
            }

            if (paint.Style == SKPaintStyle.Fill)
            {
                DrawShaderLayer(shader!, clipGeometry, targetBounds, paint, drawAsFill: false);
            }
            else
            {
                using var sourcePath = new SKPath();
                sourcePath.Geometry.FillRule = clipGeometry.FillRule;
                foreach (var figure in clipGeometry.Figures)
                {
                    sourcePath.Geometry.Figures.Add(figure);
                }

                using var fillPath = paint.GetFillPath(sourcePath);
                if (fillPath != null)
                {
                    DrawShaderLayer(shader!, fillPath.Geometry, fillPath.Bounds, paint, drawAsFill: true);
                }
            }
        }
        finally
        {
            if (pushedOpacity)
            {
                _context.PopOpacity();
            }

            PopPaintBlendMode(pushedBlendMode);
        }

        return true;
    }

    private static bool HasSpecialShader(SKShader? shader)
    {
        if (shader == null)
        {
            return false;
        }

        if (shader.ColorFilter != null)
        {
            return true;
        }

        if (shader.LocalMatrix is { } localMatrix)
        {
            return HasSpecialShader(localMatrix.Shader);
        }

        return shader.Picture != null || shader.Image != null || shader.Composed != null;
    }

    private void DrawShaderLayer(
        SKShader shader,
        PathGeometry clipGeometry,
        SKRect targetBounds,
        SKPaint paint,
        bool drawAsFill)
    {
        DrawShaderLayer(
            shader,
            clipGeometry,
            targetBounds,
            paint,
            drawAsFill,
            SKMatrix.Identity,
            shaderColorFilters: default);
    }

    private void DrawShaderLayer(
        SKShader shader,
        PathGeometry clipGeometry,
        SKRect targetBounds,
        SKPaint paint,
        bool drawAsFill,
        SKMatrix inheritedLocalMatrix,
        ShaderColorFilterList shaderColorFilters)
    {
        if (shader.LocalMatrix is { } localMatrix)
        {
            DrawShaderLayer(
                localMatrix.Shader,
                clipGeometry,
                targetBounds,
                paint,
                drawAsFill,
                SKMatrix.Concat(inheritedLocalMatrix, localMatrix.Matrix),
                shaderColorFilters);
            return;
        }

        if (shader.ColorFilter is { } colorFilter)
        {
            DrawShaderLayer(
                colorFilter.Shader,
                clipGeometry,
                targetBounds,
                paint,
                drawAsFill,
                inheritedLocalMatrix,
                shaderColorFilters.Prepend(colorFilter.Filter));
            return;
        }

        if (shader.Composed is { } composed)
        {
            if (shaderColorFilters.Count == 0 &&
                paint.ColorFilter == null &&
                !HasShaderColorFilter(composed.Destination) &&
                !HasShaderColorFilter(composed.Source) &&
                TryCreateComposedConicalBrush(composed, out var conicalBrush) &&
                SKShader.ApplyLocalMatrix(conicalBrush, inheritedLocalMatrix))
            {
                var style = drawAsFill ? SKPaintStyle.Fill : paint.Style;
                var conicalFill = style == SKPaintStyle.Stroke ? null : conicalBrush;
                var conicalPen = style == SKPaintStyle.Fill
                    ? null
                    : paint.ToPen(conicalBrush, GetCurrentStrokeScale());
                AddDrawPathCommand(
                    clipGeometry,
                    conicalFill,
                    conicalPen,
                    _currentMatrix.ToMatrix4x4(),
                    !paint.IsAntialias);
                return;
            }

            DrawComposedShaderLayer(
                composed,
                clipGeometry,
                paint,
                inheritedLocalMatrix,
                shaderColorFilters);
            return;
        }

        if (shaderColorFilters.Count != 0)
        {
            DrawFilteredShaderLayer(
                shader,
                clipGeometry,
                paint,
                inheritedLocalMatrix,
                shaderColorFilters);
            return;
        }

        if (shader.Picture is { } picture)
        {
            DrawTiledPicture(
                picture.Picture,
                picture.TileRect,
                picture.TileModeX,
                picture.TileModeY,
                picture.FilterMode,
                SKMatrix.Concat(inheritedLocalMatrix, picture.LocalMatrix),
                shaderColorFilter: null,
                paint.ColorFilter,
                clipGeometry,
                targetBounds);
            return;
        }

        if (shader.Image is { } image)
        {
            DrawTiledImage(
                image,
                SKMatrix.Concat(inheritedLocalMatrix, image.LocalMatrix),
                shaderColorFilter: null,
                paint.ColorFilter,
                clipGeometry,
                targetBounds);
            return;
        }

        var brush = shader.ToBrush();
        if (!SKShader.ApplyLocalMatrix(brush, inheritedLocalMatrix))
        {
            return;
        }
        var shaderStyle = drawAsFill ? SKPaintStyle.Fill : paint.Style;
        var fill = shaderStyle == SKPaintStyle.Stroke ? null : brush;
        var pen = shaderStyle == SKPaintStyle.Fill
            ? null
            : paint.ToPen(brush, GetCurrentStrokeScale());
        AddDrawPathCommand(
            clipGeometry,
            fill,
            pen,
            _currentMatrix.ToMatrix4x4(),
            !paint.IsAntialias);
    }

    private void DrawComposedShaderLayer(
        SKShader.ComposedShaderData composed,
        PathGeometry clipGeometry,
        SKPaint paint,
        SKMatrix inheritedLocalMatrix,
        ShaderColorFilterList shaderColorFilters)
    {
        var destination = RenderShaderLayerTexture(
            composed.Destination,
            paint.IsAntialias,
            inheritedLocalMatrix);
        GpuTexture? source = null;
        GpuTexture? result = null;
        try
        {
            source = RenderShaderLayerTexture(
                composed.Source,
                paint.IsAntialias,
                inheritedLocalMatrix);
            result = composed.Arithmetic is { } arithmetic
                ? RenderArithmeticComposite(
                    destination,
                    source,
                    new SKImageFilter.ArithmeticData(
                        arithmetic.K1,
                        arithmetic.K2,
                        arithmetic.K3,
                        arithmetic.K4,
                        arithmetic.EnforcePremul,
                        null,
                        null))
                : RenderImageBlend(
                    destination,
                    source,
                    composed.BlendMode ?? SKBlendMode.SrcOver);

            result = ApplyShaderColorFilters(result, shaderColorFilters);
            result = ApplyShaderColorFilter(result, paint.ColorFilter);
            DrawShaderTextureResult(result, clipGeometry);
            result = null;
        }
        finally
        {
            ReleaseOwnedLayerTexture(destination);
            if (source != null)
            {
                ReleaseOwnedLayerTexture(source);
            }
            if (result != null)
            {
                ReleaseOwnedLayerTexture(result);
            }
        }
    }

    private void DrawFilteredShaderLayer(
        SKShader shader,
        PathGeometry clipGeometry,
        SKPaint paint,
        SKMatrix inheritedLocalMatrix,
        ShaderColorFilterList shaderColorFilters)
    {
        GpuTexture? result = RenderShaderLayerTexture(
            shader,
            paint.IsAntialias,
            inheritedLocalMatrix);
        try
        {
            result = ApplyShaderColorFilters(result, shaderColorFilters);
            result = ApplyShaderColorFilter(result, paint.ColorFilter);
            DrawShaderTextureResult(result, clipGeometry);
            result = null;
        }
        finally
        {
            if (result != null)
            {
                ReleaseOwnedLayerTexture(result);
            }
        }
    }

    private GpuTexture RenderShaderLayerTexture(
        SKShader shader,
        bool isAntialias,
        SKMatrix inheritedLocalMatrix)
    {
        var coverageBounds = GetShaderLayerCoverageBounds();
        var coverageGeometry = CreateRectGeometry(coverageBounds);
        return RenderFilterPass(
            "SKShader Layer",
            GetTextureWidth(),
            GetTextureHeight(),
            context =>
            {
                using var canvas = new SKCanvas(context, _width, _height, GetGpuContext());
                canvas.SetMatrix(_currentMatrix);
                using var layerPaint = new SKPaint
                {
                    Color = SKColors.White,
                    Style = SKPaintStyle.Fill,
                    IsAntialias = isAntialias,
                };
                canvas.DrawShaderLayer(
                    shader,
                    coverageGeometry,
                    coverageBounds,
                    layerPaint,
                    drawAsFill: true,
                    inheritedLocalMatrix: inheritedLocalMatrix,
                    shaderColorFilters: default);
            });
    }

    private SKRect GetShaderLayerCoverageBounds()
    {
        var deviceBounds = new SKRect(0f, 0f, _width, _height);
        return _currentMatrix.TryInvert(out var inverse)
            ? inverse.MapRect(deviceBounds)
            : deviceBounds;
    }

    private GpuTexture ApplyShaderColorFilters(
        GpuTexture texture,
        ShaderColorFilterList colorFilters)
    {
        for (var index = 0; index < colorFilters.Count; index++)
        {
            texture = ApplyShaderColorFilter(texture, colorFilters[index]);
        }

        return texture;
    }

    private GpuTexture ApplyShaderColorFilter(GpuTexture texture, SKColorFilter? colorFilter)
    {
        if (colorFilter == null)
        {
            return texture;
        }

        var filtered = RenderColorFilter(texture, colorFilter, cropRect: null);
        if (!ReferenceEquals(filtered, texture))
        {
            ReleaseOwnedLayerTexture(texture);
        }
        return filtered;
    }

    private void DrawShaderTextureResult(GpuTexture texture, PathGeometry clipGeometry)
    {
        _context.PushGeometryClip(clipGeometry, _currentMatrix.ToMatrix4x4());
        try
        {
            DrawRestoredLayerTexture(
                texture,
                new Rect(0f, 0f, texture.Width, texture.Height));
        }
        finally
        {
            _context.PopGeometryClip();
        }
    }

    private static bool HasShaderColorFilter(SKShader shader)
    {
        if (shader.ColorFilter != null)
        {
            return true;
        }

        if (shader.LocalMatrix is { } localMatrix)
        {
            return HasShaderColorFilter(localMatrix.Shader);
        }

        return false;
    }

    private static bool TryCreateComposedConicalBrush(
        SKShader.ComposedShaderData composed,
        out TwoPointConicalGradientBrush brush)
    {
        brush = null!;
        if (composed.Arithmetic != null || composed.BlendMode != SKBlendMode.SrcOver)
        {
            return false;
        }
        Brush destination;
        Brush source;
        try
        {
            destination = composed.Destination.ToBrush();
            source = composed.Source.ToBrush();
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (destination is not SolidColorBrush solid || source is not TwoPointConicalGradientBrush conical)
        {
            return false;
        }

        var destinationColor = ApplyOpacity(solid.Color, solid.Opacity);
        for (var i = 0; i < conical.Stops.Length; i++)
        {
            var stop = conical.Stops[i];
            stop.Color = SourceOver(ApplyOpacity(stop.Color, conical.Opacity), destinationColor);
            conical.Stops[i] = stop;
        }

        conical.Opacity = 1f;
        conical.OutsideColor = destinationColor;
        brush = conical;
        return true;
    }

    private static Vector4 ApplyOpacity(Vector4 color, float opacity)
    {
        color.W *= Math.Clamp(opacity, 0f, 1f);
        return color;
    }

    private static Vector4 SourceOver(Vector4 source, Vector4 destination)
    {
        var sourceAlpha = Math.Clamp(source.W, 0f, 1f);
        var destinationAlpha = Math.Clamp(destination.W, 0f, 1f);
        var alpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);
        if (alpha <= 0f)
        {
            return Vector4.Zero;
        }

        var rgb = (new Vector3(source.X, source.Y, source.Z) * sourceAlpha
            + new Vector3(destination.X, destination.Y, destination.Z)
            * destinationAlpha * (1f - sourceAlpha)) / alpha;
        return new Vector4(rgb, alpha);
    }

    private void DrawTiledPicture(
        GpuPicture picture,
        SKRect tileRect,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKFilterMode filterMode,
        SKMatrix shaderMatrix,
        SKColorFilter? shaderColorFilter,
        SKColorFilter? paintColorFilter,
        PathGeometry clipGeometry,
        SKRect targetBounds)
    {
        if (tileRect.Width <= 0f || tileRect.Height <= 0f || targetBounds.Width <= 0f || targetBounds.Height <= 0f)
        {
            return;
        }

        var localMatrix = shaderMatrix.ToMatrix4x4();
        var texture = RasterizePictureTile(
            picture,
            tileRect,
            localMatrix * _currentMatrix.ToMatrix4x4());
        texture = ApplyTextureColorFilter(texture, shaderColorFilter);
        texture = ApplyTextureColorFilter(texture, paintColorFilter);
        GetPictureShaderBounds(targetBounds, localMatrix, out var minX, out var minY, out var maxX, out var maxY);
        GetTileRange(tileModeX, minX, maxX, tileRect.Width, out var startX, out var endX);
        GetTileRange(tileModeY, minY, maxY, tileRect.Height, out var startY, out var endY);
        LimitTileRange(ref startX, ref endX);
        LimitTileRange(ref startY, ref endY);
        _context.PushGeometryClip(clipGeometry, _currentMatrix.ToMatrix4x4());
        try
        {
            for (var y = startY; y <= endY; y++)
            {
                for (var x = startX; x <= endX; x++)
                {
                    var placement = CreateTilePlacement(tileRect, x, y, tileModeX, tileModeY);
                    var pictureTransform = placement * localMatrix * _currentMatrix.ToMatrix4x4();
                    _context.Commands.Add(new RenderCommand
                    {
                        Type = RenderCommandType.DrawTexture,
                        Texture = texture,
                        Rect = new Rect(tileRect.Left, tileRect.Top, tileRect.Width, tileRect.Height),
                        SrcRect = new Rect(0f, 0f, texture.Width, texture.Height),
                        Transform = pictureTransform,
                        TextureSamplingMode = MapFilterMode(filterMode)
                    });
                }
            }
        }
        finally
        {
            _context.PopGeometryClip();
        }
    }

    private GpuTexture RasterizePictureTile(
        GpuPicture picture,
        SKRect tileRect,
        Matrix4x4 pictureToDevice)
    {
        const float maxPictureTileArea = 2048f * 2048f;
        var scaleX = GetAxisScale(pictureToDevice, Vector2.UnitX);
        var scaleY = GetAxisScale(pictureToDevice, Vector2.UnitY);
        var scaledWidth = tileRect.Width * scaleX;
        var scaledHeight = tileRect.Height * scaleY;
        var scaledArea = scaledWidth * scaledHeight;
        if (scaledArea > maxPictureTileArea)
        {
            var clampScale = MathF.Sqrt(maxPictureTileArea / scaledArea);
            scaledWidth *= clampScale;
            scaledHeight *= clampScale;
        }

        const uint maxPictureTileDimension = 8192;
        var width = (uint)Math.Clamp(Math.Ceiling(scaledWidth), 1d, maxPictureTileDimension);
        var height = (uint)Math.Clamp(Math.Ceiling(scaledHeight), 1d, maxPictureTileDimension);
        var context = _gpuContext != null && !_gpuContext.IsDisposed
            ? _gpuContext
            : SKContextHelper.GetContext();
        var texture = new GpuTexture(
            context,
            width,
            height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKPicture Shader Tile",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        var rasterTransform = Matrix4x4.CreateTranslation(-tileRect.Left, -tileRect.Top, 0f)
            * Matrix4x4.CreateScale(width / tileRect.Width, height / tileRect.Height, 1f);
        var visual = new DrawingVisual { Size = new Vector2(width, height) };
        visual.Context.DrawPictureTransformed(picture, rasterTransform);

        var retained = false;
        try
        {
            try
            {
                GetCompositorForContext(context).RenderOffscreen(
                    visual,
                    width,
                    height,
                    texture,
                    padding: 0f,
                    dpiScale: 1f,
                    clearColor: Vector4.Zero);
            }
            finally
            {
                visual.Context.Clear();
            }

            _context.RetainResource(texture);
            retained = true;
            return texture;
        }
        finally
        {
            if (!retained)
            {
                texture.Dispose();
            }
        }
    }

    private static float GetAxisScale(Matrix4x4 transform, Vector2 axis)
    {
        var transformed = Vector2.TransformNormal(axis, transform);
        var scale = transformed.Length();
        return float.IsFinite(scale) && scale > 0f ? scale : 1f;
    }

    private static bool IsFinite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
        float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
        float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    private static Vector2 TransformFilterVector(Vector2 vector, Matrix4x4 transform)
    {
        if (transform == default)
        {
            return vector;
        }

        var transformed = Vector2.TransformNormal(vector, transform);
        return float.IsFinite(transformed.X) && float.IsFinite(transformed.Y)
            ? transformed
            : vector;
    }

    private static Vector4 CreateFilterVectorTransform(float scale, Matrix4x4 transform)
    {
        var xAxis = TransformFilterVector(new Vector2(scale, 0f), transform);
        var yAxis = TransformFilterVector(new Vector2(0f, scale), transform);
        return new Vector4(xAxis.X, xAxis.Y, yAxis.X, yAxis.Y);
    }

    private void DrawTiledImage(
        SKShader.ImageShaderData imageShader,
        SKMatrix shaderMatrix,
        SKColorFilter? shaderColorFilter,
        SKColorFilter? paintColorFilter,
        PathGeometry clipGeometry,
        SKRect targetBounds)
    {
        var tileRect = imageShader.TileRect;
        if (tileRect.Width <= 0f || tileRect.Height <= 0f || targetBounds.Width <= 0f || targetBounds.Height <= 0f)
        {
            return;
        }

        var localMatrix = shaderMatrix.ToMatrix4x4();
        GetPictureShaderBounds(targetBounds, localMatrix, out var minX, out var minY, out var maxX, out var maxY);
        GetTileRange(imageShader.TileModeX, minX, maxX, tileRect.Width, out var startX, out var endX);
        GetTileRange(imageShader.TileModeY, minY, maxY, tileRect.Height, out var startY, out var endY);
        LimitTileRange(ref startX, ref endX);
        LimitTileRange(ref startY, ref endY);

        var texture = RetainImageTexture(imageShader.Image);
        if (!_isPictureRecording)
        {
            texture = ConvertImageTextureToSrgb(texture, imageShader.Image.ColorSpace);
        }
        texture = ApplyTextureColorFilter(texture, shaderColorFilter);
        texture = ApplyTextureColorFilter(texture, paintColorFilter);
        var samplingMode = MapSampling(imageShader.Sampling);
        var maxAnisotropy = MapMaxAnisotropy(imageShader.Sampling);
        var cubicCoefficients = MapCubicSampling(imageShader.Sampling);
        _context.PushGeometryClip(clipGeometry, _currentMatrix.ToMatrix4x4());
        try
        {
            for (var y = startY; y <= endY; y++)
            {
                for (var x = startX; x <= endX; x++)
                {
                    var placement = CreateTilePlacement(
                        tileRect,
                        x,
                        y,
                        imageShader.TileModeX,
                        imageShader.TileModeY);
                    _context.Commands.Add(new RenderCommand
                    {
                        Type = RenderCommandType.DrawTexture,
                        Texture = texture,
                        Rect = new Rect(0f, 0f, imageShader.Image.Width, imageShader.Image.Height),
                        SrcRect = new Rect(0f, 0f, imageShader.Image.Width, imageShader.Image.Height),
                        Transform = placement * localMatrix * _currentMatrix.ToMatrix4x4(),
                        TextureSamplingMode = samplingMode,
                        TextureMaxAnisotropy = maxAnisotropy,
                        TextureCubicCoefficients = cubicCoefficients.GetValueOrDefault(),
                        HasTextureCubicCoefficients = cubicCoefficients.HasValue
                    });
                }
            }
        }
        finally
        {
            _context.PopGeometryClip();
        }
    }

    private GpuTexture ApplyTextureColorFilter(GpuTexture texture, SKColorFilter? colorFilter)
    {
        if (colorFilter == null)
        {
            return texture;
        }

        var filteredTexture = RenderColorFilter(texture, colorFilter, cropRect: null);
        if (ReferenceEquals(filteredTexture, texture))
        {
            return texture;
        }

        RetainLayerTextureForDeferredCommand(filteredTexture);
        return filteredTexture;
    }

    private static void GetPictureShaderBounds(
        SKRect targetBounds,
        Matrix4x4 localMatrix,
        out float minX,
        out float minY,
        out float maxX,
        out float maxY)
    {
        if (!Matrix4x4.Invert(localMatrix, out var inverse))
        {
            minX = targetBounds.Left;
            minY = targetBounds.Top;
            maxX = targetBounds.Right;
            maxY = targetBounds.Bottom;
            return;
        }

        var topLeft = Vector2.Transform(new Vector2(targetBounds.Left, targetBounds.Top), inverse);
        var topRight = Vector2.Transform(new Vector2(targetBounds.Right, targetBounds.Top), inverse);
        var bottomRight = Vector2.Transform(new Vector2(targetBounds.Right, targetBounds.Bottom), inverse);
        var bottomLeft = Vector2.Transform(new Vector2(targetBounds.Left, targetBounds.Bottom), inverse);
        minX = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomRight.X, bottomLeft.X));
        minY = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomRight.Y, bottomLeft.Y));
        maxX = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomRight.X, bottomLeft.X));
        maxY = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomRight.Y, bottomLeft.Y));
    }

    private static void GetTileRange(
        SKShaderTileMode tileMode,
        float minimum,
        float maximum,
        float tileSize,
        out int start,
        out int end)
    {
        if (tileMode is SKShaderTileMode.Repeat or SKShaderTileMode.Mirror)
        {
            start = (int)MathF.Floor(minimum / tileSize) - 1;
            end = (int)MathF.Floor(maximum / tileSize) + 1;
            return;
        }

        start = 0;
        end = 0;
    }

    private static void LimitTileRange(ref int start, ref int end)
    {
        const int maxTileCountPerAxis = 128;
        var count = (long)end - start + 1;
        if (count <= maxTileCountPerAxis)
        {
            return;
        }

        var center = start + (int)(count / 2);
        start = center - maxTileCountPerAxis / 2;
        end = start + maxTileCountPerAxis - 1;
    }

    private static Matrix4x4 CreateTilePlacement(
        SKRect tileRect,
        int tileX,
        int tileY,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY)
    {
        var mirrorX = tileModeX == SKShaderTileMode.Mirror && (tileX & 1) != 0;
        var mirrorY = tileModeY == SKShaderTileMode.Mirror && (tileY & 1) != 0;
        var scaleX = mirrorX ? -1f : 1f;
        var scaleY = mirrorY ? -1f : 1f;
        var translateX = mirrorX
            ? tileRect.Left + (tileX + 1) * tileRect.Width
            : tileX * tileRect.Width - tileRect.Left;
        var translateY = mirrorY
            ? tileRect.Top + (tileY + 1) * tileRect.Height
            : tileY * tileRect.Height - tileRect.Top;

        return Matrix4x4.CreateScale(scaleX, scaleY, 1f)
            * Matrix4x4.CreateTranslation(translateX, translateY, 0f);
    }

    private static bool IsInverseFillType(SKPathFillType fillType)
    {
        return fillType is SKPathFillType.InverseWinding or SKPathFillType.InverseEvenOdd;
    }

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawImage(SKImage image, SKRect source, SKRect dest, SKPaint? paint = null)
    {
        DrawImage(
            image,
            source,
            dest,
            paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default,
            paint);
    }

    private void DrawImageCore(
        SKImage image,
        SKRect source,
        SKRect dest,
        TextureSamplingMode samplingMode,
        SKPaint? paint,
        Vector2? cubicCoefficients = null,
        byte maxAnisotropy = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        var opacity = paint != null ? paint.Color.A / 255f : 1f;
        var retainedTexture = RetainImageTexture(
            image,
            samplingMode == TextureSamplingMode.LinearMipmap);
        if (!_isPictureRecording)
        {
            retainedTexture = ConvertImageTextureToSrgb(retainedTexture, image.ColorSpace);
        }
        if (paint?.ColorFilter is { } colorFilter)
        {
            var filteredTexture = RenderColorFilter(retainedTexture, colorFilter, cropRect: null);
            if (!ReferenceEquals(filteredTexture, retainedTexture))
            {
                RetainLayerTextureForDeferredCommand(filteredTexture);
                retainedTexture = filteredTexture;
            }
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        var pushedOpacity = false;
        var pushedEdgeClip = false;
        try
        {
            if (opacity < 1f)
            {
                _context.PushOpacity(opacity);
                pushedOpacity = true;
            }

            if (paint?.IsAntialias != false)
            {
                _context.PushGeometryClip(CreateRectGeometry(dest), _currentMatrix.ToMatrix4x4());
                pushedEdgeClip = true;
            }

            var rasterExtension = pushedEdgeClip ? 0.5f : 0f;
            var sourceExtensionX = dest.Width != 0f
                ? rasterExtension * source.Width / dest.Width
                : 0f;
            var sourceExtensionY = dest.Height != 0f
                ? rasterExtension * source.Height / dest.Height
                : 0f;

            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = retainedTexture,
                // Rasterize through the trailing pixel center; the exact clip supplies edge coverage.
                Rect = new Rect(dest.Left, dest.Top, dest.Width + rasterExtension, dest.Height + rasterExtension),
                SrcRect = new Rect(
                    source.Left,
                    source.Top,
                    source.Width + sourceExtensionX,
                    source.Height + sourceExtensionY),
                Transform = _currentMatrix.ToMatrix4x4(),
                TextureSamplingMode = samplingMode,
                TextureMaxAnisotropy = maxAnisotropy,
                TextureCubicCoefficients = cubicCoefficients.GetValueOrDefault(),
                HasTextureCubicCoefficients = cubicCoefficients.HasValue,
                IsEdgeAliased = paint is { IsAntialias: false }
            });

        }
        finally
        {
            if (pushedEdgeClip)
            {
                _context.PopGeometryClip();
            }

            if (pushedOpacity)
            {
                _context.PopOpacity();
            }

            PopPaintBlendMode(pushedBlendMode);
        }
    }

    private void DrawImagePatchesCore(
        SKImage image,
        TexturePatch[] patches,
        SKRect destination,
        TextureSamplingMode samplingMode,
        SKPaint? paint,
        Vector2? cubicCoefficients = null,
        byte maxAnisotropy = 1)
    {
        if (patches.Length == 0)
        {
            return;
        }

        var retainedTexture = RetainImageTexture(
            image,
            samplingMode == TextureSamplingMode.LinearMipmap);
        if (!_isPictureRecording)
        {
            retainedTexture = ConvertImageTextureToSrgb(retainedTexture, image.ColorSpace);
        }
        if (paint?.ColorFilter is { } colorFilter)
        {
            var filteredTexture = RenderColorFilter(retainedTexture, colorFilter, cropRect: null);
            if (!ReferenceEquals(filteredTexture, retainedTexture))
            {
                RetainLayerTextureForDeferredCommand(filteredTexture);
                retainedTexture = filteredTexture;
            }
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        var pushedOpacity = false;
        try
        {
            var opacity = paint?.Color.Alpha / 255f ?? 1f;
            if (opacity < 1f)
            {
                _context.PushOpacity(opacity);
                pushedOpacity = true;
            }

            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = retainedTexture,
                TexturePatches = patches,
                Rect = ToRect(destination),
                Transform = _currentMatrix.ToMatrix4x4(),
                TextureSamplingMode = samplingMode,
                TextureMaxAnisotropy = maxAnisotropy,
                TextureCubicCoefficients = cubicCoefficients.GetValueOrDefault(),
                HasTextureCubicCoefficients = cubicCoefficients.HasValue,
                IsEdgeAliased = paint is { IsAntialias: false }
            });
        }
        finally
        {
            if (pushedOpacity)
            {
                _context.PopOpacity();
            }

            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawImage(
        SKImage image,
        SKRect source,
        SKRect dest,
        SKSamplingOptions sampling,
        SKPaint? paint = null)
    {
        var samplingMode = MapSampling(sampling);
        DrawImageCore(
            image,
            source,
            dest,
            samplingMode,
            paint,
            MapCubicSampling(sampling),
            MapMaxAnisotropy(sampling));
    }

    public void DrawImage(
        SKImage image,
        SKRect destination,
        SKSamplingOptions sampling,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImage(
            image,
            new SKRect(0f, 0f, image.Width, image.Height),
            destination,
            sampling,
            paint);
    }

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawImage(SKImage image, float x, float y, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImage(
            image,
            new SKRect(0f, 0f, image.Width, image.Height),
            new SKRect(x, y, x + image.Width, y + image.Height),
            paint);
    }

    public void DrawImage(SKImage image, float x, float y, SKSamplingOptions sampling, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImage(
            image,
            new SKRect(0f, 0f, image.Width, image.Height),
            new SKRect(x, y, x + image.Width, y + image.Height),
            sampling,
            paint);
    }

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawImage(SKImage image, SKPoint point, SKPaint? paint = null) =>
        DrawImage(image, point.X, point.Y, paint);

    public void DrawImage(SKImage image, SKPoint point, SKSamplingOptions sampling, SKPaint? paint = null) =>
        DrawImage(image, point.X, point.Y, sampling, paint);

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawImage(SKImage image, SKRect destination, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImage(
            image,
            new SKRect(0f, 0f, image.Width, image.Height),
            destination,
            paint);
    }

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawAtlas(
        SKImage atlas,
        SKRect[] sprites,
        SKRotationScaleMatrix[] transforms,
        SKPaint? paint = null) =>
        DrawAtlasCore(
            atlas,
            sprites,
            transforms,
            colors: null,
            SKBlendMode.Dst,
            paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default,
            cullRect: null,
            paint);

    public void DrawAtlas(
        SKImage atlas,
        SKRect[] sprites,
        SKRotationScaleMatrix[] transforms,
        SKSamplingOptions sampling,
        SKPaint? paint = null) =>
        DrawAtlasCore(
            atlas,
            sprites,
            transforms,
            colors: null,
            SKBlendMode.Dst,
            sampling,
            cullRect: null,
            paint);

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawAtlas(
        SKImage atlas,
        SKRect[] sprites,
        SKRotationScaleMatrix[] transforms,
        SKColor[]? colors,
        SKBlendMode mode,
        SKPaint? paint = null) =>
        DrawAtlasCore(
            atlas,
            sprites,
            transforms,
            colors,
            mode,
            paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default,
            cullRect: null,
            paint);

    public void DrawAtlas(
        SKImage atlas,
        SKRect[] sprites,
        SKRotationScaleMatrix[] transforms,
        SKColor[]? colors,
        SKBlendMode mode,
        SKSamplingOptions sampling,
        SKPaint? paint = null) =>
        DrawAtlasCore(atlas, sprites, transforms, colors, mode, sampling, cullRect: null, paint);

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawAtlas(
        SKImage atlas,
        SKRect[] sprites,
        SKRotationScaleMatrix[] transforms,
        SKColor[]? colors,
        SKBlendMode mode,
        SKRect cullRect,
        SKPaint? paint = null) =>
        DrawAtlasCore(
            atlas,
            sprites,
            transforms,
            colors,
            mode,
            paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default,
            cullRect,
            paint);

    public void DrawAtlas(
        SKImage atlas,
        SKRect[] sprites,
        SKRotationScaleMatrix[] transforms,
        SKColor[]? colors,
        SKBlendMode mode,
        SKSamplingOptions sampling,
        SKRect cullRect,
        SKPaint? paint = null) =>
        DrawAtlasCore(atlas, sprites, transforms, colors, mode, sampling, cullRect, paint);

    private void DrawAtlasCore(
        SKImage atlas,
        SKRect[] sprites,
        SKRotationScaleMatrix[] transforms,
        SKColor[]? colors,
        SKBlendMode mode,
        SKSamplingOptions sampling,
        SKRect? cullRect,
        SKPaint? paint)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(transforms);
        if (transforms.Length != sprites.Length)
        {
            throw new ArgumentException(
                "The number of transforms must match the number of sprites.",
                nameof(transforms));
        }
        if (colors != null && colors.Length != sprites.Length)
        {
            throw new ArgumentException(
                "The number of colors must match the number of sprites.",
                nameof(colors));
        }

        var patches = SKAtlasLayout.CreatePatches(
            sprites,
            transforms,
            colors,
            mode,
            paint?.ColorFilter,
            out var computedBounds);
        DrawImagePatchesCore(
            atlas,
            patches,
            cullRect ?? computedBounds,
            MapSampling(sampling),
            paint,
            MapCubicSampling(sampling),
            MapMaxAnisotropy(sampling));
    }

    public void DrawBitmapNinePatch(
        SKBitmap bitmap,
        SKRectI center,
        SKRect destination,
        SKPaint? paint = null) =>
        DrawBitmapNinePatch(bitmap, center, destination, SKFilterMode.Nearest, paint);

    public void DrawBitmapNinePatch(
        SKBitmap bitmap,
        SKRectI center,
        SKRect destination,
        SKFilterMode filterMode,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImageNinePatch(image, center, destination, filterMode, paint);
    }

    public void DrawImageNinePatch(
        SKImage image,
        SKRectI center,
        SKRect destination,
        SKPaint? paint = null) =>
        DrawImageNinePatch(image, center, destination, SKFilterMode.Nearest, paint);

    public void DrawImageNinePatch(
        SKImage image,
        SKRectI center,
        SKRect destination,
        SKFilterMode filterMode = SKFilterMode.Nearest,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (center.Left < 0 ||
            center.Top < 0 ||
            center.Right > image.Width ||
            center.Bottom > image.Height)
        {
            throw new ArgumentException(
                "Center rectangle must be contained inside the image bounds.",
                nameof(center));
        }

        if (!SKLatticeLayout.TryCreateNinePatch(
                image.Width,
                image.Height,
                center,
                destination,
                out var patches))
        {
            return;
        }

        DrawImagePatchesCore(image, patches, destination, MapFilterMode(filterMode), paint);
    }

    public void DrawBitmapLattice(
        SKBitmap bitmap,
        int[] xDivs,
        int[] yDivs,
        SKRect destination,
        SKPaint? paint = null) =>
        DrawBitmapLattice(bitmap, xDivs, yDivs, destination, SKFilterMode.Nearest, paint);

    public void DrawBitmapLattice(
        SKBitmap bitmap,
        int[] xDivs,
        int[] yDivs,
        SKRect destination,
        SKFilterMode filterMode,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImageLattice(image, xDivs, yDivs, destination, filterMode, paint);
    }

    public void DrawImageLattice(
        SKImage image,
        int[] xDivs,
        int[] yDivs,
        SKRect destination,
        SKPaint? paint = null) =>
        DrawImageLattice(image, xDivs, yDivs, destination, SKFilterMode.Nearest, paint);

    public void DrawImageLattice(
        SKImage image,
        int[] xDivs,
        int[] yDivs,
        SKRect destination,
        SKFilterMode filterMode,
        SKPaint? paint = null) =>
        DrawImageLattice(
            image,
            new SKLattice { XDivs = xDivs, YDivs = yDivs },
            destination,
            filterMode,
            paint);

    public void DrawBitmapLattice(
        SKBitmap bitmap,
        SKLattice lattice,
        SKRect destination,
        SKPaint? paint = null) =>
        DrawBitmapLattice(bitmap, lattice, destination, SKFilterMode.Nearest, paint);

    public void DrawBitmapLattice(
        SKBitmap bitmap,
        SKLattice lattice,
        SKRect destination,
        SKFilterMode filterMode,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImageLattice(image, lattice, destination, filterMode, paint);
    }

    public void DrawImageLattice(
        SKImage image,
        SKLattice lattice,
        SKRect destination,
        SKPaint? paint = null) =>
        DrawImageLattice(image, lattice, destination, SKFilterMode.Nearest, paint);

    public void DrawImageLattice(
        SKImage image,
        SKLattice lattice,
        SKRect destination,
        SKFilterMode filterMode,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (lattice.XDivs == null)
        {
            throw new ArgumentNullException("XDivs");
        }
        if (lattice.YDivs == null)
        {
            throw new ArgumentNullException("YDivs");
        }

        if (!SKLatticeLayout.TryCreateLattice(
                image.Width,
                image.Height,
                lattice,
                destination,
                paint?.ColorFilter,
                out var patches))
        {
            return;
        }

        DrawImagePatchesCore(image, patches, destination, MapFilterMode(filterMode), paint);
    }

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawBitmap(SKBitmap bitmap, SKPoint point, SKPaint? paint = null) =>
        DrawBitmap(bitmap, point.X, point.Y, paint);

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawBitmap(SKBitmap bitmap, float x, float y, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImage(
            image,
            x,
            y,
            paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default,
            paint);
    }

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawBitmap(SKBitmap bitmap, SKRect destination, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImage(
            image,
            destination,
            paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default,
            paint);
    }

    [Obsolete("Use the overload with SKSamplingOptions instead.")]
    public void DrawBitmap(SKBitmap bitmap, SKRect source, SKRect destination, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImage(
            image,
            source,
            destination,
            paint?.GetLegacyFilterQualitySampling() ?? SKSamplingOptions.Default,
            paint);
    }

    public void DrawBitmap(
        SKBitmap bitmap,
        SKPoint point,
        SKSamplingOptions sampling,
        SKPaint? paint = null) =>
        DrawBitmap(bitmap, point.X, point.Y, sampling, paint);

    public void DrawBitmap(
        SKBitmap bitmap,
        float x,
        float y,
        SKSamplingOptions sampling,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImage(image, x, y, sampling, paint);
    }

    public void DrawBitmap(
        SKBitmap bitmap,
        SKRect destination,
        SKSamplingOptions sampling,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImage(image, destination, sampling, paint);
    }

    public void DrawBitmap(
        SKBitmap bitmap,
        SKRect source,
        SKRect destination,
        SKSamplingOptions sampling,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        DrawImage(image, source, destination, sampling, paint);
    }

    public void DrawSurface(SKSurface surface, SKPoint point, SKPaint? paint = null) =>
        DrawSurface(surface, point.X, point.Y, paint);

    public void DrawSurface(SKSurface surface, float x, float y, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface.Draw(this, x, y, paint);
    }

    public void DrawSurface(
        SKSurface surface,
        SKPoint point,
        SKSamplingOptions sampling,
        SKPaint? paint = null) =>
        DrawSurface(surface, point.X, point.Y, sampling, paint);

    public void DrawSurface(
        SKSurface surface,
        float x,
        float y,
        SKSamplingOptions sampling,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface.Draw(this, x, y, sampling, paint);
    }

    private GpuTexture RetainImageTexture(SKImage image, bool generateMipmaps = false)
    {
        var source = image.Texture;
        var currentContext = WgpuContext.Current;
        var targetContext = _gpuContext != null && !_gpuContext.IsDisposed
            ? _gpuContext
            : currentContext != null && !currentContext.IsDisposed
                ? currentContext
                : source.Context;
        if (!ReferenceEquals(source.Context, targetContext))
        {
            throw new InvalidOperationException(
                "SKCanvas.DrawImage cannot draw an SKImage from a different WebGPU context. " +
                "Create the image in the same GRContext/SKSurface context before recording the draw.");
        }

        var mipLevelCount = generateMipmaps
            ? CalculateMipLevelCount(source.Width, source.Height)
            : source.MipLevelCount;
        var usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc;
        if (generateMipmaps)
        {
            usage |= TextureUsage.RenderAttachment;
        }

        var retainedTexture = new GpuTexture(
            targetContext,
            source.Width,
            source.Height,
            source.Format,
            usage,
            "SKCanvas DrawImage Retained Source Texture",
            alphaMode: source.AlphaMode,
            mipLevelCount: mipLevelCount);
        if (image.ColorSpace is { } imageColorSpace)
        {
            s_textureColorSpaces.Add(retainedTexture, new TextureColorSpace(imageColorSpace));
        }
        if (retainedTexture.MipLevelCount == source.MipLevelCount)
        {
        retainedTexture.CopyFrom(source);
        }
        else
        {
            retainedTexture.CopyBaseLevelFrom(source);
            retainedTexture.GenerateMipmaps2DLinear();
        }
        _context.RetainResource(retainedTexture);
        return retainedTexture;
    }

    private static uint CalculateMipLevelCount(uint width, uint height)
    {
        var dimension = Math.Max(width, height);
        uint count = 1;
        while (dimension > 1)
        {
            dimension /= 2;
            count++;
        }

        return count;
    }

    public void DrawTextBlob(SKTextBlob textBlob, float x, float y, SKPaint paint)
    {
        if (paint.Shader != null ||
            paint.Style != SKPaintStyle.Fill ||
            textBlob.HasEmboldenedRuns)
        {
            using var textPath = CreateTextBlobPath(textBlob, x, y);
            DrawPath(textPath, paint);
            return;
        }

        var brush = paint.ToBrush();
        if (brush == null)
        {
            return;
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            foreach (var run in textBlob.Runs)
            {
                if (run.RotationScaleMatrices is { } matrices)
                {
                    for (var i = 0; i < run.GlyphIndices.Length; i++)
                    {
                        var transform = matrices[i].ToMatrix().ToMatrix4x4()
                            * Matrix4x4.CreateTranslation(x, y, 0f)
                            * _currentMatrix.ToMatrix4x4();
                        _context.DrawTransformedGlyphRun(
                            new[] { run.GlyphIndices[i] },
                            new[] { Vector2.Zero },
                            run.Font.Typeface.Font,
                            run.Font.Size,
                            brush,
                            Vector2.Zero,
                            transform,
                            isBold: run.Font.Embolden,
                            fontScaleX: GetFiniteFontScaleX(run.Font),
                            fontSkewX: GetFiniteFontSkewX(run.Font),
                            useVectorGlyphRendering: true);
                    }

                    continue;
                }

                var positions = new Vector2[run.GlyphPositions.Length];
                for (int i = 0; i < positions.Length; i++)
                {
                    positions[i] = new Vector2(run.GlyphPositions[i].X, run.GlyphPositions[i].Y);
                }

                _context.DrawTransformedGlyphRun(
                    run.GlyphIndices,
                    positions,
                    run.Font.Typeface.Font,
                    run.Font.Size,
                    brush,
                    new Vector2(x, y),
                    _currentMatrix.ToMatrix4x4(),
                    isBold: run.Font.Embolden,
                    fontScaleX: GetFiniteFontScaleX(run.Font),
                    fontSkewX: GetFiniteFontSkewX(run.Font),
                    useVectorGlyphRendering: true
                );
            }
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    private static SKPath CreateTextBlobPath(SKTextBlob textBlob, float x, float y)
    {
        var result = new SKPath();
        foreach (var run in textBlob.Runs)
        {
            for (var i = 0; i < run.GlyphIndices.Length; i++)
            {
                using var glyphPath = run.Font.GetGlyphPath(run.GlyphIndices[i]);
                if (glyphPath == null)
                {
                    continue;
                }

                if (run.RotationScaleMatrices is { } matrices)
                {
                    var matrix = matrices[i].ToMatrix();
                    matrix.TransX += x;
                    matrix.TransY += y;
                    AddTextBlobGlyphPath(result, glyphPath, matrix);
                }
                else
                {
                    var position = run.GlyphPositions[i];
                    AddTextBlobGlyphPath(
                        result,
                        glyphPath,
                        x + position.X,
                        y + position.Y);
                }
            }
        }

        return result;
    }

    private static void AddTextBlobGlyphPath(
        SKPath destination,
        SKPath glyphPath,
        float x,
        float y) =>
        destination.AddPath(glyphPath, x, y);

    private static void AddTextBlobGlyphPath(
        SKPath destination,
        SKPath glyphPath,
        SKMatrix placement) =>
        destination.AddPath(glyphPath, placement);

    private static float GetFiniteFontScaleX(SKFont font) =>
        float.IsFinite(font.ScaleX) ? font.ScaleX : 1f;

    private static float GetFiniteFontSkewX(SKFont font) =>
        float.IsFinite(font.SkewX) ? font.SkewX : 0f;

    public void DrawText(SKTextBlob textBlob, float x, float y, SKPaint paint)
    {
        DrawTextBlob(textBlob, x, y, paint);
    }

    public void DrawText(
        string text,
        float x,
        float y,
        SKTextAlign textAlign,
        SKFont font,
        SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(paint);

        var glyphs = new List<ushort>(text.Length);
        var positions = new List<SKPoint>(text.Length);
        var advance = 0f;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = font.Typeface.Font.GetGlyphIndex((uint)rune.Value);
            glyphs.Add(glyph);
            positions.Add(new SKPoint(advance, 0f));
            advance += font.Typeface.Font.GetAdvanceWidth(glyph, font.Size) * font.ScaleX;
        }

        var alignOffset = textAlign switch
        {
            SKTextAlign.Center => -advance * 0.5f,
            SKTextAlign.Right => -advance,
            _ => 0f,
        };
        for (var i = 0; i < positions.Count; i++)
        {
            var position = positions[i];
            positions[i] = new SKPoint(position.X + alignOffset, position.Y);
        }

        if (glyphs.Count == 0)
        {
            return;
        }

        using var blob = new SKTextBlob(font, glyphs.ToArray(), positions.ToArray());
        DrawTextBlob(blob, x, y, paint);
    }

    [Obsolete("Use DrawText(string text, SKPoint p, SKTextAlign textAlign, SKFont font, SKPaint paint) instead.", true)]
    public void DrawText(string text, SKPoint point, SKPaint paint) =>
        DrawText(text, point, paint.GetLegacyTextAlign(), paint.GetLegacyFont(), paint);

    [Obsolete("Use DrawText(string text, float x, float y, SKTextAlign textAlign, SKFont font, SKPaint paint) instead.", true)]
    public void DrawText(string text, float x, float y, SKPaint paint) =>
        DrawText(text, x, y, paint.GetLegacyTextAlign(), paint.GetLegacyFont(), paint);

    [Obsolete("Use DrawText(string text, SKPoint p, SKTextAlign textAlign, SKFont font, SKPaint paint) instead.")]
    public void DrawText(string text, SKPoint point, SKFont font, SKPaint paint) =>
        DrawText(text, point, paint.GetLegacyTextAlign(), font, paint);

    public void DrawText(
        string text,
        SKPoint point,
        SKTextAlign textAlign,
        SKFont font,
        SKPaint paint) =>
        DrawText(text, point.X, point.Y, textAlign, font, paint);

    [Obsolete("Use DrawText(string text, float x, float y, SKTextAlign textAlign, SKFont font, SKPaint paint) instead.")]
    public void DrawText(string text, float x, float y, SKFont font, SKPaint paint) =>
        DrawText(text, x, y, paint.GetLegacyTextAlign(), font, paint);

    [Obsolete("Use DrawTextOnPath(string text, SKPath path, float hOffset, float vOffset, SKTextAlign textAlign, SKFont font, SKPaint paint) instead.", true)]
    public void DrawTextOnPath(string text, SKPath path, SKPoint offset, SKPaint paint) =>
        DrawTextOnPath(text, path, offset, warpGlyphs: true, paint);

    [Obsolete("Use DrawTextOnPath(string text, SKPath path, float hOffset, float vOffset, SKTextAlign textAlign, SKFont font, SKPaint paint) instead.", true)]
    public void DrawTextOnPath(string text, SKPath path, float hOffset, float vOffset, SKPaint paint) =>
        DrawTextOnPath(text, path, new SKPoint(hOffset, vOffset), warpGlyphs: true, paint);

    [Obsolete("Use DrawTextOnPath(string text, SKPath path, SKPoint offset, bool warpGlyphs, SKTextAlign textAlign, SKFont font, SKPaint paint) instead.", true)]
    public void DrawTextOnPath(
        string text,
        SKPath path,
        SKPoint offset,
        bool warpGlyphs,
        SKPaint paint) =>
        DrawTextOnPath(text, path, offset, warpGlyphs, paint.GetLegacyFont(), paint);

    [Obsolete("Use the overload with SKTextAlign parameter instead.")]
    public void DrawTextOnPath(
        string text,
        SKPath path,
        SKPoint offset,
        SKFont font,
        SKPaint paint) =>
        DrawTextOnPath(text, path, offset, warpGlyphs: true, paint.GetLegacyTextAlign(), font, paint);

    public void DrawTextOnPath(
        string text,
        SKPath path,
        SKPoint offset,
        SKTextAlign textAlign,
        SKFont font,
        SKPaint paint) =>
        DrawTextOnPath(text, path, offset, warpGlyphs: true, textAlign, font, paint);

    [Obsolete("Use the overload with SKTextAlign parameter instead.")]
    public void DrawTextOnPath(
        string text,
        SKPath path,
        float hOffset,
        float vOffset,
        SKFont font,
        SKPaint paint) =>
        DrawTextOnPath(text, path, new SKPoint(hOffset, vOffset), warpGlyphs: true, paint.GetLegacyTextAlign(), font, paint);

    public void DrawTextOnPath(
        string text,
        SKPath path,
        float hOffset,
        float vOffset,
        SKTextAlign textAlign,
        SKFont font,
        SKPaint paint) =>
        DrawTextOnPath(
            text,
            path,
            new SKPoint(hOffset, vOffset),
            warpGlyphs: true,
            textAlign,
            font,
            paint);

    [Obsolete("Use the overload with SKTextAlign parameter instead.")]
    public void DrawTextOnPath(
        string text,
        SKPath path,
        SKPoint offset,
        bool warpGlyphs,
        SKFont font,
        SKPaint paint) =>
        DrawTextOnPath(text, path, offset, warpGlyphs, paint.GetLegacyTextAlign(), font, paint);

    public void DrawTextOnPath(
        string text,
        SKPath path,
        SKPoint offset,
        bool warpGlyphs,
        SKTextAlign textAlign,
        SKFont font,
        SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(paint);
        if (warpGlyphs)
        {
            using var textPath = font.GetTextPathOnPath(text, path, textAlign, offset);
            DrawPath(textPath, paint);
            return;
        }

        using var textBlob = SKTextBlob.CreatePathPositioned(text, font, path, textAlign, offset);
        if (textBlob != null)
        {
            DrawText(textBlob, 0f, 0f, paint);
        }
    }

    public void Flush()
    {
        if (_bitmap != null)
        {
            FlushToBitmap();
        }
        else
        {
            _flush?.Invoke();
        }
    }

    private void FlushToBitmap()
    {
        if (_bitmap == null || _context.Commands.Count == 0)
        {
            return;
        }

        using var surface = SKSurface.Create(_bitmap.Info, _bitmap.GetPixels(), _bitmap.RowBytes);
        surface.Canvas.DrawingContext.Append(_context);
        surface.Flush();
        _context.Clear();
    }

    internal void ReleaseLayerTexturesAfterFlush()
    {
        foreach (var texture in _ownedLayerTextures)
        {
            texture.Dispose();
        }

        _ownedLayerTextures.Clear();
    }

    private void ReleaseUnrestoredLayers()
    {
        while (_layerStack.TryPop(out var layer))
        {
            layer.LayerContext.Clear();
            layer.PreviousContext?.Clear();
            layer.Paint?.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            Flush();
        }
        finally
        {
            _bitmap?.DetachCanvas(this);
            ReleaseUnrestoredLayers();
            ReleaseLayerTexturesAfterFlush();
            _surface = null;
        }
    }
}
