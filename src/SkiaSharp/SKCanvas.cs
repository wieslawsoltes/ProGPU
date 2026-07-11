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
        SKImageFilter Filter,
        bool PreserveSourceColorSpace);

    private DrawingContext _context;
    private readonly float _width;
    private readonly float _height;
    private readonly WgpuContext? _gpuContext;
    private readonly Action? _flush;
    private readonly SKBitmap? _bitmap;
    private readonly bool _isPictureRecording;
    private SKMatrix _currentMatrix = SKMatrix.Identity;
    private float _currentOpacity = 1f;
    private readonly List<GpuTexture> _ownedLayerTextures = new();
    private List<SKRect>? _cpuReadbackRegions;
    public enum PushKind
    {
        RectClip,
        GeometryClip,
        Opacity
    }

    private readonly Stack<(SKMatrix Matrix, float Opacity, int PushedScopesCount)> _stateStack = new();
    private readonly Stack<PushKind> _pushedScopes = new();
    private readonly Stack<RenderCommand> _activeClipPushes = new();
    private readonly Stack<LayerFrame> _layerStack = new();

    private sealed class LayerFrame
    {
        public LayerFrame(
            DrawingContext parentContext,
            DrawingContext layerContext,
            SKPaint? paint,
            int stateDepth,
            SKRect bounds,
            SKMatrix boundsMatrix,
            RenderCommand[] activeClipPushes)
        {
            ParentContext = parentContext;
            LayerContext = layerContext;
            Paint = paint;
            StateDepth = stateDepth;
            Bounds = bounds;
            BoundsMatrix = boundsMatrix;
            ActiveClipPushes = activeClipPushes;
        }

        public DrawingContext ParentContext { get; }
        public DrawingContext LayerContext { get; }
        public SKPaint? Paint { get; }
        public int StateDepth { get; }
        public SKRect Bounds { get; }
        public SKMatrix BoundsMatrix { get; }
        public RenderCommand[] ActiveClipPushes { get; }
    }

    public SKMatrix TotalMatrix
    {
        get => _currentMatrix;
        set => SetMatrix(value);
    }

    public SKMatrix44 TotalMatrix44 => SKMatrix44.FromMatrix4x4(_currentMatrix.ToMatrix4x4());

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
    }

    public SKCanvas(SKBitmap bitmap)
        : this(
            new DrawingContext(),
            (bitmap ?? throw new ArgumentNullException(nameof(bitmap))).Width,
            bitmap.Height,
            SKContextHelper.GetContext())
    {
        _bitmap = bitmap;
        bitmap.AttachCanvas(this);
    }

    internal DrawingContext Context => _context;

    public void Clear()
    {
        Clear(SKColors.Transparent);
    }

    public void Clear(SKColor color)
    {
        var c = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        var brush = new SolidColorBrush(c);
        _context.PushBlendMode(GpuBlendMode.Src);
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0, 0, _width, _height),
            Brush = brush,
            Transform = Matrix4x4.Identity // Clear is always in identity screen space
        });
        _context.PopBlendMode();
    }

    public int Save()
    {
        var restoreCount = _stateStack.Count;
        _stateStack.Push((_currentMatrix, _currentOpacity, _pushedScopes.Count));
        return restoreCount;
    }

    public int SaveLayer(SKRect bounds, SKPaint? paint)
    {
        var restoreCount = _stateStack.Count;
        Save();

        var parentContext = _context;
        var layerContext = new DrawingContext();
        _layerStack.Push(new LayerFrame(
            parentContext,
            layerContext,
            paint?.Clone(),
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
        count = Math.Max(0, count);
        while (_stateStack.Count > count)
        {
            Restore();
        }
    }

    private void RestoreLayer(LayerFrame layerFrame)
    {
        _context = layerFrame.ParentContext;
        var hasSourceGeneratingFilter = layerFrame.Paint?.ImageFilter != null ||
            layerFrame.Paint?.ColorFilter != null;
        if ((!hasSourceGeneratingFilter && layerFrame.LayerContext.Commands.Count == 0) ||
            !IsValidLayerBounds(layerFrame.Bounds))
        {
            layerFrame.LayerContext.Clear();
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
        SKImageFilter? current = filter;
        while (current != null && visited.Add(current))
        {
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

            current = current.Input;
        }

        return false;
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
        var cacheKey = new ImageFilterCacheKey(filter, preserveSourceColorSpace);
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
                result = RenderBlur(input, blur.SigmaX, blur.SigmaY);
                break;
            }
            case SKImageFilter.FilterKind.DropShadow:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderDropShadow(input, (SKImageFilter.DropShadowData)filter.Parameters!);
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
                result = RenderFilterPass(
                    "SKImageFilter Offset",
                    input.Width,
                    input.Height,
                    context => context.DrawTexture(
                        input,
                        new Rect(offset.Dx, offset.Dy, input.Width, input.Height)));
                break;
            }
            case SKImageFilter.FilterKind.Dilate:
            case SKImageFilter.FilterKind.Erode:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                var morphology = (SKImageFilter.MorphologyData)filter.Parameters!;
                result = RenderMorphology(
                    input,
                    morphology,
                    dilate: filter.Kind == SKImageFilter.FilterKind.Dilate);
                break;
            }
            case SKImageFilter.FilterKind.Merge:
            {
                var filters = (SKImageFilter[])filter.Parameters!;
                var inputs = new GpuTexture[filters.Length];
                var width = sourceTexture.Width;
                var height = sourceTexture.Height;
                for (var i = 0; i < filters.Length; i++)
                {
                    inputs[i] = EvaluateImageFilter(sourceTexture, filters[i], cache, filterTransform, preserveSourceColorSpace);
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
                var background = EvaluateImageFilter(sourceTexture, arithmetic.Background, cache, filterTransform, preserveSourceColorSpace);
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
                result = RenderDisplacementMap(input, displacementInput, displacement);
                break;
            }
            case SKImageFilter.FilterKind.MatrixConvolution:
            {
                var input = EvaluateOptionalInput(sourceTexture, filter.Input, cache, filterTransform, preserveSourceColorSpace);
                result = RenderMatrixConvolution(
                    input,
                    (SKImageFilter.MatrixConvolutionData)filter.Parameters!);
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
                var background = EvaluateImageFilter(sourceTexture, blend.Background, cache, filterTransform, preserveSourceColorSpace);
                var foreground = EvaluateOptionalInput(sourceTexture, blend.Foreground, cache, filterTransform, preserveSourceColorSpace);
                result = RenderImageBlend(background, foreground, blend.Mode);
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
                        MapSampling(image.Sampling)));
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

        result = ApplyFilterCrop(result, filter.CropRect, filterTransform);
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

    private GpuTexture RenderDropShadow(GpuTexture input, SKImageFilter.DropShadowData shadow)
    {
        var context = GetGpuContext();
        var temporary = CreateOwnedFilterTexture(context, "SKImageFilter Shadow Temporary", storage: true);
        var shadowTexture = CreateOwnedFilterTexture(context, "SKImageFilter Shadow", storage: true);
        GetCompositorForContext(context).ApplyDropShadow(
            input,
            temporary,
            shadowTexture,
            Vector2.Zero,
            ToVector4(shadow.Color),
            MathF.Max(shadow.SigmaX, shadow.SigmaY));

        return RenderFilterPass(
            "SKImageFilter Shadow Composite",
            input.Width,
            input.Height,
            drawing =>
            {
                drawing.DrawTexture(
                    shadowTexture,
                    new Rect(shadow.Dx, shadow.Dy, shadowTexture.Width, shadowTexture.Height));
                drawing.DrawTexture(input, new Rect(0f, 0f, input.Width, input.Height));
            });
    }

    private GpuTexture RenderMorphology(
        GpuTexture input,
        SKImageFilter.MorphologyData morphology,
        bool dilate)
    {
        var context = GetGpuContext();
        var temporary = CreateOwnedFilterTexture(context, "SKImageFilter Morphology Temporary", storage: true);
        var destination = CreateOwnedFilterTexture(context, "SKImageFilter Morphology Destination", storage: true);
        GetCompositorForContext(context).ApplyMorphology(
            input,
            temporary,
            destination,
            morphology.RadiusX,
            morphology.RadiusY,
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
            storage: true);
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
            storage: true);
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
        SKImageFilter.DisplacementData displacement)
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
            displacement.Scale,
            (uint)displacement.XChannel,
            (uint)displacement.YChannel);
        return destination;
    }

    private GpuTexture RenderMatrixConvolution(
        GpuTexture input,
        SKImageFilter.MatrixConvolutionData convolution)
    {
        var kernelWidth = convolution.KernelSize.Width;
        var kernelHeight = convolution.KernelSize.Height;
        if (kernelWidth is <= 0 or > 64 ||
            kernelHeight is <= 0 or > 64 ||
            convolution.Kernel.Length < kernelWidth * kernelHeight)
        {
            return input;
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
            (uint)convolution.TileMode,
            convolution.ConvolveAlpha);
        return destination;
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
        SKShader shader,
        Matrix4x4 filterTransform,
        uint width,
        uint height)
    {
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
            : sampling.MipmapMode != SKMipmapMode.None
                ? TextureSamplingMode.LinearMipmap
                : sampling.FilterMode == SKFilterMode.Nearest
                    ? TextureSamplingMode.Nearest
                    : TextureSamplingMode.Linear;

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
        var texture = new GpuTexture(
            context,
            textureWidth,
            textureHeight,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKCanvas SaveLayer Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        var visual = new DrawingVisual { Size = new Vector2(textureWidth, textureHeight) };
        ReplayActiveClipPushes(visual.Context, layerFrame.ActiveClipPushes);
        var pushedLayerBoundsClip = PushLayerBoundsClip(visual.Context, layerFrame);
        visual.Context.Append(layerFrame.LayerContext);
        if (pushedLayerBoundsClip)
        {
            visual.Context.PopClip();
        }
        PopReplayedClipPushes(visual.Context, layerFrame.ActiveClipPushes);

        var textureRetained = false;
        try
        {
            try
            {
                GetCompositorForContext(context).RenderOffscreen(
                    visual,
                    textureWidth,
                    textureHeight,
                    texture,
                    padding: 0f,
                    dpiScale: 1f);
            }
            finally
            {
                visual.Context.Clear();
            }

            layerFrame.LayerContext.Clear();
            _ownedLayerTextures.Add(texture);
            textureRetained = true;
            return texture;
        }
        finally
        {
            if (!textureRetained)
            {
                layerFrame.LayerContext.Clear();
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

    private static Compositor GetCompositorForContext(WgpuContext context)
    {
        return SharedCompositorCache.GetOrCreate(context, TextureFormat.Rgba8Unorm, s_compositorCacheScope);
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

    public void Scale(float sx, float sy)
    {
        _currentMatrix.ScaleX *= sx;
        _currentMatrix.SkewY *= sx;
        _currentMatrix.SkewX *= sy;
        _currentMatrix.ScaleY *= sy;
    }

    public void SetMatrix(SKMatrix matrix)
    {
        _currentMatrix = matrix;
    }

    public void SetMatrix(SKMatrix44 matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
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

    public bool QuickReject(SKRect rect)
    {
        if (rect.IsEmpty)
        {
            return true;
        }

        var matrix = _currentMatrix.ToMatrix4x4();
        var p0 = Vector2.Transform(new Vector2(rect.Left, rect.Top), matrix);
        var p1 = Vector2.Transform(new Vector2(rect.Right, rect.Top), matrix);
        var p2 = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), matrix);
        var p3 = Vector2.Transform(new Vector2(rect.Left, rect.Bottom), matrix);
        var min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
        var max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
        return max.X <= 0f || max.Y <= 0f || min.X >= _width || min.Y >= _height;
    }

    public bool QuickReject(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return QuickReject(path.Bounds);
    }

    public void ClipRect(SKRect rect, SKClipOperation operation = SKClipOperation.Intersect, bool antialias = true)
    {
        if (operation == SKClipOperation.Difference)
        {
            var excluded = CreateRectGeometry(rect).CreateTransformed(_currentMatrix.ToMatrix4x4());
            PushGeometryClipScope(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            return;
        }

        PushRectClipScope(rect, _currentMatrix.ToMatrix4x4());
    }

    public void ClipPath(SKPath? path, SKClipOperation operation = SKClipOperation.Intersect, bool antialias = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (operation == SKClipOperation.Difference)
        {
            if (IsInverseFillType(path.FillType))
            {
                PushGeometryClipScope(path.Geometry, _currentMatrix.ToMatrix4x4());
            }
            else
            {
                var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
                PushGeometryClipScope(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            }
            return;
        }

        if (IsInverseFillType(path.FillType))
        {
            var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
            PushGeometryClipScope(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            return;
        }

        var transform = _currentMatrix.ToMatrix4x4();
        if (IsAxisAligned2DTransform(transform) && TryGetRectGeometry(path.Geometry, out var rect))
        {
            PushRectClipScope(rect, transform);
            return;
        }

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

    public void DrawPicture(SKPicture picture)
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

    public void DrawPicture(SKPicture picture, SKPaint? paint)
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

            DrawPicture(picture);
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

    public void DrawLine(float x0, float y0, float x1, float y1, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(x0, y0);
        path.LineTo(x1, y1);
        DrawPath(path, paint);
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
        if (shader == null || (shader.Picture == null && shader.Image == null && shader.Composed == null))
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
                DrawShaderLayer(shader, clipGeometry, targetBounds, paint, drawAsFill: false);
            }
            else
            {
                using var sourcePath = new SKPath();
                sourcePath.Geometry.FillRule = clipGeometry.FillRule;
                foreach (var figure in clipGeometry.Figures)
                {
                    sourcePath.Geometry.Figures.Add(figure);
                }

                using var fillPath = new SKPath();
                if (paint.GetFillPath(sourcePath, fillPath))
                {
                    DrawShaderLayer(shader, fillPath.Geometry, fillPath.Bounds, paint, drawAsFill: true);
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
        return shader != null
            && (shader.Picture != null || shader.Image != null || shader.Composed != null);
    }

    private void DrawShaderLayer(
        SKShader shader,
        PathGeometry clipGeometry,
        SKRect targetBounds,
        SKPaint paint,
        bool drawAsFill)
    {
        if (shader.Composed is { } composed)
        {
            if (TryCreateComposedConicalBrush(composed, out var conicalBrush))
            {
                SKShader.ApplyColorFilter(conicalBrush, paint.ColorFilter);
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

            DrawShaderLayer(composed.Destination, clipGeometry, targetBounds, paint, drawAsFill);
            DrawShaderLayer(composed.Source, clipGeometry, targetBounds, paint, drawAsFill);
            return;
        }

        if (shader.Picture is { } picture)
        {
            DrawTiledPicture(
                picture.Picture,
                picture.TileRect,
                picture.TileModeX,
                picture.TileModeY,
                picture.LocalMatrix,
                shader.ColorFilter,
                paint.ColorFilter,
                clipGeometry,
                targetBounds);
            return;
        }

        if (shader.Image is { } image)
        {
            DrawTiledImage(image, shader.ColorFilter, paint.ColorFilter, clipGeometry, targetBounds);
            return;
        }

        var brush = shader.ToBrush();
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

    private static bool TryCreateComposedConicalBrush(
        SKShader.ComposedShaderData composed,
        out TwoPointConicalGradientBrush brush)
    {
        brush = null!;
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
                        TextureSamplingMode = TextureSamplingMode.Nearest
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

    private void DrawTiledImage(
        SKShader.ImageShaderData imageShader,
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

        var localMatrix = imageShader.LocalMatrix.ToMatrix4x4();
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
                        TextureSamplingMode = TextureSamplingMode.Linear
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

    public void DrawImage(SKImage image, SKRect source, SKRect dest, SKPaint? paint)
    {
        DrawImageCore(image, source, dest, TextureSamplingMode.Linear, paint);
    }

    private void DrawImageCore(
        SKImage image,
        SKRect source,
        SKRect dest,
        TextureSamplingMode samplingMode,
        SKPaint? paint)
    {
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

    public void DrawImage(
        SKImage image,
        SKRect source,
        SKRect dest,
        SKSamplingOptions sampling,
        SKPaint? paint)
    {
        var samplingMode = sampling.UseCubic
            ? TextureSamplingMode.Cubic
            : sampling.MipmapMode != SKMipmapMode.None
                ? TextureSamplingMode.LinearMipmap
            : sampling.FilterMode == SKFilterMode.Nearest
                ? TextureSamplingMode.Nearest
                : TextureSamplingMode.Linear;
        DrawImageCore(image, source, dest, samplingMode, paint);
    }

    public void DrawImage(
        SKImage image,
        SKRect destination,
        SKSamplingOptions sampling,
        SKPaint? paint) =>
        DrawImage(
            image,
            new SKRect(0f, 0f, image.Width, image.Height),
            destination,
            sampling,
            paint);

    public void DrawImage(SKImage image, float x, float y, SKPaint? paint)
    {
        DrawImage(image, new SKRect(0, 0, image.Width, image.Height), new SKRect(x, y, x + image.Width, y + image.Height), paint);
    }

    public void DrawImage(SKImage image, SKRect destination)
    {
        using var paint = new SKPaint();
        DrawImage(
            image,
            new SKRect(0f, 0f, image.Width, image.Height),
            destination,
            paint);
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
        if (paint.Shader != null || paint.Style != SKPaintStyle.Fill)
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
                        _context.DrawGlyphRun(
                            new[] { run.GlyphIndices[i] },
                            new[] { Vector2.Zero },
                            run.Font.Typeface.Font,
                            run.Font.Size,
                            brush,
                            Vector2.Zero,
                            transform,
                            useVectorGlyphRendering: true);
                    }

                    continue;
                }

                var positions = new Vector2[run.GlyphPositions.Length];
                for (int i = 0; i < positions.Length; i++)
                {
                    positions[i] = new Vector2(run.GlyphPositions[i].X, run.GlyphPositions[i].Y);
                }

                _context.DrawGlyphRun(
                    run.GlyphIndices,
                    positions,
                    run.Font.Typeface.Font,
                    run.Font.Size,
                    brush,
                    new Vector2(x, y),
                    _currentMatrix.ToMatrix4x4(),
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
                    result.AddPath(glyphPath, matrix);
                }
                else
                {
                    var position = run.GlyphPositions[i];
                    result.AddPath(glyphPath, x + position.X, y + position.Y);
                }
            }
        }

        return result;
    }

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

    public void DrawText(string text, float x, float y, SKFont font, SKPaint paint) =>
        DrawText(text, x, y, SKTextAlign.Left, font, paint);

    public void DrawTextOnPath(
        string text,
        SKPath path,
        float hOffset,
        float vOffset,
        SKTextAlign textAlign,
        SKFont font,
        SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var textPath = font.GetTextPathOnPath(text, path, textAlign, new SKPoint(hOffset, vOffset));
        DrawPath(textPath, paint);
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
        surface.Canvas.Context.Append(_context);
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

    public void Dispose()
    {
        try
        {
            Flush();
        }
        finally
        {
            _bitmap?.DetachCanvas(this);
            ReleaseLayerTexturesAfterFlush();
        }
    }
}
