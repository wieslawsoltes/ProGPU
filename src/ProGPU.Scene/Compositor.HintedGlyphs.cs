using System.Numerics;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    // The original GlyphAtlas/Text.wgsl contract owns coverage and composition.
    // This seam adds original physical records, never a font, layout or snap.
    private void CompileHintedGlyphCommand(RenderCommand command, Matrix4x4 transform)
    {
        HintedGlyphGeometry geometry = command.HintedGlyphGeometry ??
            throw new InvalidOperationException("A hinted glyph command requires its original retained geometry.");
        ValidateHintedReplayTarget(geometry.DpiScale, _currentDpiScale, transform,
            ActiveCompilationContext?.StaticZoom ?? 1f);
        var effectiveTransform = new Matrix3x2(transform.M11, transform.M12,
            transform.M21, transform.M22, transform.M41, transform.M42);
        if (_useGpuTransformsActive || !HintedGlyphCommandGeometry.TryValidate(
            command, _currentDpiScale, effectiveTransform, out int start, out int count))
            throw new NotSupportedException("Original hinted replay requires its admitted range, source ink domain, paint mode and target frame.");
        HintedGlyphCommandGeometry.TryGetInkBounds(command, out _, out bool hasInk);
        if (count == 0 || !hasInk || command.Brush is null) return;

        // Static DXF uses independent retained raster storage, not the atlas
        // residency generation checked by ordinary retained scene playback.
        if (ActiveCompilationContext?.RetainedGlyphBuilder != null)
            throw new NotSupportedException("Original hinted atlas coverage is not admitted to static DXF raster storage.");
        if (ActiveCompilationContext != null && command.Brush is not SolidColorBrush)
            throw new NotSupportedException("Static text-buffer recompilation cannot retain spatial hinted paint and mask storage.");

        RetainHintedGeometryForCompiledReplay(geometry);
        command.TextRenderingMode = ResolveCachedTextRenderingMode(command.TextRenderingMode, _suppressCachedClearType);
        if (ActiveCompilationContext != null && !ActiveCompilationContext.IsRecompiling &&
            ActiveCompilationContext.RetainedGlyphBuilder == null)
            _compiledTextRecords.Add(new StaticTextRecord { Command = command, Transform = transform });

        if (command.Brush is SolidColorBrush)
        {
            CompileHintedGlyphAtlasInstances(command, transform, geometry, start, count);
            return;
        }

        CompileHintedGlyphDirectPaint(command, transform, geometry, start, count);
    }

    internal static Rect GetHintedPaintStorageBounds(RenderCommand command,
        HintedGlyphGeometry geometry, int start, int count)
    {
        ReadOnlySpan<HintedGlyphOccurrence> occurrences = geometry.RenderOccurrences;
        ReadOnlySpan<GpuGlyphRecord> outlines = geometry.RenderOutlines;
        int segmentCount = geometry.RenderSegments.Length;
        float inverseDpi = 1f / geometry.DpiScale;
        Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        int end = checked(start + count);
        for (int index = start; index < end; index++)
        {
            HintedGlyphOccurrence occurrence = occurrences[index];
            if (occurrence.OutlineIndex == uint.MaxValue) continue;
            GpuGlyphRecord record = outlines[checked((int)occurrence.OutlineIndex)];
            GlyphAtlas.ValidateHintedRasterRecord(in record, segmentCount);
            var bounds = GlyphAtlas.GetHintedRasterBounds(in record);
            Vector2 position = occurrence.Position + command.Position;
            // Match the canonical Text vertex's physical bearing/size divided
            // by target DPI, without snapping the original writer position.
            Vector2 offset = new Vector2(bounds.XStart, bounds.YStart) * inverseDpi;
            Vector2 extent = new Vector2(bounds.Width, bounds.Height) * inverseDpi;
            minimum = Vector2.Min(minimum, position + offset);
            maximum = Vector2.Max(maximum, position + (offset + extent));
        }
        Vector2 size = maximum - minimum;
        if (!float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y) ||
            !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) ||
            !float.IsFinite(size.X) || !float.IsFinite(size.Y))
            throw new InvalidOperationException("Original hinted raster storage overflows the admitted logical frame.");
        return new Rect(minimum, size);
    }

    private void CompileHintedGlyphDirectPaint(RenderCommand command, Matrix4x4 transform,
        HintedGlyphGeometry geometry, int start, int count)
    {
        // One original positioned occurrence owns one original paint, including
        // overlaps. No white-coverage union or R8 intermediate changes gamma,
        // fractional filtering, opacity order or the final target rounding.
        CommitPendingDrawCalls();
        GpuHintedGlyphPaint paint;
        GpuTexture? texture = null;
        var sampling = TextureSamplingMode.Linear;
        var addressU = TextureAddressMode.Clamp;
        var addressV = TextureAddressMode.Clamp;
        var alphaMode = GpuTextureAlphaMode.Straight;
        if (command.Brush is GpuTextureBrush textureBrush)
        {
            paint = CreateHintedTexturePaint(textureBrush, command.Rect, transform);
            texture = textureBrush.Texture!;
            sampling = textureBrush.SamplingMode;
            addressU = textureBrush.AddressModeU;
            addressV = textureBrush.AddressModeV;
            alphaMode = texture.AlphaMode;
        }
        else
        {
            paint = new GpuHintedGlyphPaint
            {
                Kind = GpuHintedGlyphPaint.RegisteredMaterial,
                BrushIndex = checked((uint)RegisterBrush(command.Brush)),
                SourceOffsetOpacity = new Vector4(transform.M41, transform.M42, 0f, 0f)
                // RegisterBrush already owns original state/brush opacity.
            };
        }

        _hintedGlyphPaints ??= new List<GpuHintedGlyphPaint>();
        _hintedGlyphPaints.EnsureCapacity(checked(_hintedGlyphPaints.Count + 1));
        bool separateDestinationComposites = texture != null && RequiresDestinationSampling(_activeBlendMode);
        _drawCalls.EnsureCapacity(checked(_drawCalls.Count + (separateDestinationComposites ? count : 1)));
        uint paintIndex = checked((uint)_hintedGlyphPaints.Count);
        int textStart = _textVerticesList.Count;
        _hintedGlyphPaints.Add(paint);
        try
        {
            CompileHintedGlyphAtlasInstances(command, transform, geometry, start, count, paintIndex);
            int instanceCount = _textVerticesList.Count - textStart;
            if (instanceCount == 0)
            {
                _hintedGlyphPaints.RemoveAt(checked((int)paintIndex));
                return;
            }
            var drawCall = new CompositorDrawCall
            {
                Type = DrawCallType.HintedGlyphPaint,
                IndexStart = checked((uint)textStart), IndexCount = checked((uint)instanceCount),
                Texture = texture, TextureSamplingMode = sampling,
                TextureAddressModeU = addressU, TextureAddressModeV = addressV,
                TextureAlphaMode = alphaMode, BlendMode = _activeBlendMode,
                ClipRect = _activeClipRect,
                MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek().Texture : null,
                MaskBindGroupOverride = GetActiveMaskBindGroupOverride()
            };
            if (separateDestinationComposites)
            {
                drawCall.IndexCount = 1;
                for (int index = 0; index < instanceCount; index++)
                {
                    drawCall.IndexStart = checked((uint)(textStart + index));
                    _drawCalls.Add(drawCall);
                }
            }
            else _drawCalls.Add(drawCall);
        }
        catch
        {
            _textVerticesList.RemoveRange(textStart, _textVerticesList.Count - textStart);
            _hintedGlyphPaints.RemoveAt(checked((int)paintIndex));
            throw;
        }
        finally
        {
            _pendingTextStart = checked((uint)_textVerticesList.Count);
            _currentBatchType = BatchType.None;
        }
    }

    private GpuHintedGlyphPaint CreateHintedTexturePaint(GpuTextureBrush brush,
        Rect originalSourceDomain, Matrix4x4 transform)
    {
        if (!brush.TryCreateTextureCommand(originalSourceDomain, out RenderCommand sourcePaint))
            throw new NotSupportedException("The retained texture brush requires its original live texture and admitted mapping.");
        GpuTexture texture = sourcePaint.Texture!;
        Matrix4x4 paintTransform = brush.ExtendToFillBounds ? transform : sourcePaint.Transform * transform;
        Vector2 p0 = Vector2.Transform(new Vector2(sourcePaint.Rect.X, sourcePaint.Rect.Y), paintTransform);
        Vector2 p1 = Vector2.Transform(new Vector2(sourcePaint.Rect.Right, sourcePaint.Rect.Y), paintTransform);
        Vector2 p2 = Vector2.Transform(new Vector2(sourcePaint.Rect.Right, sourcePaint.Rect.Bottom), paintTransform);
        Vector2 p3 = Vector2.Transform(new Vector2(sourcePaint.Rect.X, sourcePaint.Rect.Bottom), paintTransform);
        if (sourcePaint.SnapTextureToPixels)
        {
            p0 = TexturePaintMapping.SnapPoint(p0, _currentDpiScale);
            p1 = TexturePaintMapping.SnapPoint(p1, _currentDpiScale);
            p2 = TexturePaintMapping.SnapPoint(p2, _currentDpiScale);
            p3 = TexturePaintMapping.SnapPoint(p3, _currentDpiScale);
        }
        if (!float.IsFinite(p0.X) || !float.IsFinite(p0.Y) || !float.IsFinite(p1.X) || !float.IsFinite(p1.Y) ||
            !float.IsFinite(p2.X) || !float.IsFinite(p2.Y) || !float.IsFinite(p3.X) || !float.IsFinite(p3.Y))
            throw new InvalidOperationException("Original hinted texture placement overflows its admitted frame.");
        uint flags = (uint)sourcePaint.TextureSamplingMode << GpuHintedGlyphPaint.SamplingModeShift;
        if (texture.AlphaMode == GpuTextureAlphaMode.Premultiplied) flags |= GpuHintedGlyphPaint.PremultipliedTexture;
        if (texture.AlphaMode == GpuTextureAlphaMode.Opaque) flags |= GpuHintedGlyphPaint.OpaqueTexture;
        if (!brush.ExtendToFillBounds) flags |= GpuHintedGlyphPaint.BoundedTexture;
        if (sourcePaint.TextureSamplingMode == TextureSamplingMode.Cubic) flags |= GpuHintedGlyphPaint.CubicTexture;
        Vector2 coefficients = ResolveImageSamplingCoefficients(_imageSamplingPath,
            sourcePaint.TextureSamplingMode, new Vector2(0f, 0.5f));
        return new GpuHintedGlyphPaint
        {
            Kind = GpuHintedGlyphPaint.TextureMaterial, Flags = flags,
            SourceOffsetOpacity = new Vector4(transform.M41, transform.M42,
                sourcePaint.TextureOpacity * _activeOpacity, 0f),
            UVBounds = new Vector4(sourcePaint.SrcRect.X / texture.Width, sourcePaint.SrcRect.Y / texture.Height,
                sourcePaint.SrcRect.Right / texture.Width, sourcePaint.SrcRect.Bottom / texture.Height),
            TextureQuad01 = new Vector4(p0, p1.X, p1.Y), TextureQuad23 = new Vector4(p2, p3.X, p3.Y),
            Sampling = new Vector4(coefficients, (float)sourcePaint.TextureAddressModeU, (float)sourcePaint.TextureAddressModeV)
        };
    }

    private void RetainHintedGeometryForCompiledReplay(HintedGlyphGeometry geometry)
    {
        foreach (RetainedResourceLease retained in _frameRetainedResources)
            if (ReferenceEquals(retained.Identity, geometry)) return;
        _frameRetainedResources.EnsureCapacity(checked(_frameRetainedResources.Count + 1));
        IDisposable sourceUse = geometry.RetainForReplay();
        RetainedResourceLease? lease = null;
        try
        {
            lease = RetainedResourceLease.Create(sourceUse, geometry);
            _frameRetainedResources.Add(lease);
        }
        catch (Exception failure)
        {
            // A key/reference to managed arrays is never source-owner admission.
            // Only a published frame lease may outlive the calling picture.
            try
            {
                if (lease != null) lease.Dispose();
                else sourceUse.Dispose();
            }
            catch (Exception cleanup)
            {
                try { failure.Data["HintedGlyphReplayCleanupFailure"] = cleanup; }
                catch { } // Reporting is best-effort; preserve the setup error.
            }
            throw;
        }
    }

    internal static void ValidateHintedReplayTarget(float originalDpiScale, float targetDpiScale,
        Matrix4x4 transform, float staticZoom)
    {
        if (!float.IsFinite(originalDpiScale) || originalDpiScale <= 0f ||
            BitConverter.SingleToInt32Bits(originalDpiScale) != BitConverter.SingleToInt32Bits(targetDpiScale))
            throw new NotSupportedException("Original hinted coverage requires the exact prepared target DPI.");
        if (!float.IsFinite(transform.M41) || !float.IsFinite(transform.M42) ||
            transform != Matrix4x4.CreateTranslation(transform.M41, transform.M42, 0f) || staticZoom != 1f)
            throw new NotSupportedException("Original hinted coverage requires an identity basis and finite logical translation.");
    }

    private void CompileHintedGlyphAtlasInstances(RenderCommand command, Matrix4x4 transform,
        HintedGlyphGeometry geometry, int start, int count, uint? paintIndex = null)
    {
        bool sharedStyle = paintIndex is null && ActiveCompilationContext is null;
        float style = sharedStyle ? RegisterTextStyle(command.Brush, command.TextRenderingMode) : -1f;
        Vector4 color = (command.Brush as SolidColorBrush)?.Color ?? Vector4.One;
        if (!sharedStyle && paintIndex is null) color.W *= (command.Brush?.Opacity ?? 1f) * _activeOpacity;
        EnsureTextVertexCapacity(count);
        ReadOnlySpan<HintedGlyphOccurrence> occurrences = geometry.RenderOccurrences;
        ReadOnlySpan<GpuGlyphRecord> outlines = geometry.RenderOutlines;
        Vector2 translation = new(transform.M41, transform.M42);
        float inverseDpi = 1f / geometry.DpiScale;
        int end = checked(start + count);
        for (int index = start; index < end; index++)
        {
            HintedGlyphOccurrence occurrence = occurrences[index];
            if (occurrence.OutlineIndex == uint.MaxValue) continue;
            GpuGlyphRecord record = outlines[checked((int)occurrence.OutlineIndex)];
            Vector2 position = occurrence.Position + command.Position + translation;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
                throw new InvalidOperationException("Original hinted placement overflows the admitted logical frame.");
            var bounds = GlyphAtlas.GetHintedRasterBounds(in record);
            if (_activeClipRect is Rect clip)
            {
                float left = position.X + bounds.XStart * inverseDpi;
                float top = position.Y + bounds.YStart * inverseDpi;
                if (left + bounds.Width * inverseDpi <= clip.X || left >= clip.Right ||
                    top + bounds.Height * inverseDpi <= clip.Y || top >= clip.Bottom)
                    continue;
            }

            if (paintIndex is null) SwitchBatch(BatchType.Text);
            GlyphInfo info = _atlas.GetOrCreateHintedGlyph(geometry, occurrence.OutlineIndex);
            _textVerticesList.Add(new GlyphInstance
            {
                SnappedLogicalPos = position, // Original writer position; deliberately unsnapped.
                BasisX = Vector2.UnitX, BasisY = Vector2.UnitY,
                BearSize = new Vector4(info.BearX, info.BearY, info.Width, info.Height),
                TexCoords = new Vector4(info.X, info.Y, info.X + info.Width, info.Y + info.Height),
                Color = color,
                // Text.wgsl already divides physical bearings/extents by DPI.
                ScaleBoldItalicUseMvp = new Vector4(1f, 0f, 0f,
                    EncodeTextFlags(ActiveCompilationContext != null,
                        sharedStyle ? TextRenderingMode.Grayscale : command.TextRenderingMode, false)),
                BrushIndex = style, Padding = paintIndex.HasValue ? BitConverter.UInt32BitsToSingle(paintIndex.Value) : 0f
            });
            if (!sharedStyle && paintIndex is null) _legacyTextVertexCount++;
        }
    }

}
