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

        // This extent owns storage only. The original source ink/brush domain
        // remains command.Rect; neither the producer records nor paint mapping
        // is normalized to the padded raster allocation.
        Rect paintStorage = command.Brush is SolidColorBrush ? default :
            GetHintedPaintStorageBounds(command, geometry, start, count);
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

        // Spatial paint samples the unchanged original brush mapping over all
        // padded glyph coverage. A source-ink rectangle must not crop filtered
        // fringe or contribute a second shape-coverage/AA term.
        PushHintedGlyphCoverageMask(command, transform, geometry, start, count, paintStorage);
        Exception? paintFailure = null;
        try
        {
            CompileHintedGlyphSpatialPaint(command, paintStorage, transform);
        }
        catch (Exception failure) { paintFailure = failure; throw; }
        finally
        {
            if (paintFailure is null) PopOpacityMaskValue();
            else
            {
                try { PopOpacityMaskValue(); }
                catch (Exception cleanup)
                {
                    try { paintFailure.Data["HintedGlyphPaintMaskCleanupFailure"] = cleanup; }
                    catch { }
                }
            }
        }
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

    private void CompileHintedGlyphSpatialPaint(RenderCommand command, Rect storageBounds,
        Matrix4x4 transform)
    {
        if (command.Brush is GpuTextureBrush textureBrush)
        {
            // The canonical texture brush extrapolates SourceRect from its
            // original DestinationRect/Transform, never stretches it to storage.
            if (textureBrush.ExtendToFillBounds && textureBrush.SnapToPixels)
            {
                if (!textureBrush.TryCreateTextureCommand(command.Rect, out RenderCommand sourcePaint))
                    throw new NotSupportedException("The retained texture brush requires its original live texture and admitted mapping.");
                if (TexturePaintMapping.TryExtendSnappedTextureCommand(sourcePaint, storageBounds, transform,
                    _currentDpiScale, out RenderCommand storagePaint))
                    CompileTextureCommand(storagePaint, transform);
                return;
            }
            CompileTextureBrushRectangle(textureBrush, storageBounds, transform);
            return;
        }

        CompileFillQuadCommand(new RenderCommand
        {
            Type = RenderCommandType.FillQuad,
            Rect = command.Rect, // Preserve the authoritative source metadata.
            Position = new Vector2(storageBounds.X, storageBounds.Y),
            Position2 = new Vector2(storageBounds.Right, storageBounds.Y),
            Position3 = new Vector2(storageBounds.Right, storageBounds.Bottom),
            Position4 = new Vector2(storageBounds.X, storageBounds.Bottom),
            Brush = command.Brush,
            IsEdgeAliased = true // The glyph mask alone owns shape coverage/AA.
        }, transform, clampVerticesToClip: false);
        // Draw-call scissoring still owns the original clip. Moving only quad
        // vertices to a clip edge would distort their absolute brush coordinates.
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
        HintedGlyphGeometry geometry, int start, int count)
    {
        bool sharedStyle = ActiveCompilationContext is null;
        float style = sharedStyle ? RegisterTextStyle(command.Brush, command.TextRenderingMode) : -1f;
        Vector4 color = (command.Brush as SolidColorBrush)?.Color ?? Vector4.One;
        if (!sharedStyle) color.W *= (command.Brush?.Opacity ?? 1f) * _activeOpacity;
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

            SwitchBatch(BatchType.Text);
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
                BrushIndex = style, Padding = 0f
            });
            if (!sharedStyle) _legacyTextVertexCount++;
        }
    }

    private void PushHintedGlyphCoverageMask(RenderCommand command, Matrix4x4 transform,
        HintedGlyphGeometry geometry, int start, int count, Rect storageBounds)
    {
        _currentFrameOpacityMaskDemand++;
        _peakOpacityMaskDemand = Math.Max(_peakOpacityMaskDemand, _currentFrameOpacityMaskDemand);
        CommitPendingDrawCalls();
        int drawStart = _drawCalls.Count;
        int textStart = _textVerticesList.Count;
        int legacyStart = _legacyTextVertexCount;
        uint pendingTextStart = _pendingTextStart;
        int styleStart = _activeTextStyles.Count;
        _maskTexturePool.EnsureCapacity(checked(_maskTexturePool.Count + 1));
        var savedState = ResetStateForMaskCompilation();
        List<CompositorDrawCall>? maskDraws = null;
        GpuTexture? maskTexture = null;
        bool published = false;
        Exception? setupFailure = null;
        try
        {
            try
            {
                RenderCommand coverage = command;
                coverage.Brush = GeometryMaskCoverageBrush;
                CompileHintedGlyphAtlasInstances(coverage, transform, geometry, start, count);
                CommitPendingDrawCalls();
            }
            finally { RestoreStateAfterMaskCompilation(savedState); }
            int drawCount = _drawCalls.Count - drawStart;
            maskDraws = RentMaskDrawCallList(drawCount);
            for (int index = drawStart; index < _drawCalls.Count; index++) maskDraws.Add(_drawCalls[index]);
            _drawCalls.RemoveRange(drawStart, drawCount);
            MaskPixelBounds bounds = QuantizeMaskStorageBounds(
                IntersectWithActiveMask(GetBoundedMaskPixelBounds(storageBounds, transform)));
            maskTexture = RentMaskTexture(bounds);
            MaskTextureState? previous = _maskStack.Count > 0 ? _maskStack.Peek() : null;
            _maskRenderPasses.EnsureCapacity(checked(_maskRenderPasses.Count + 1));
            _maskStack.EnsureCapacity(checked(_maskStack.Count + 1));
            _maskRenderPasses.Add(new MaskRenderPassInfo
            {
                MaskTexture = maskTexture, PreviousMaskTexture = previous?.Texture,
                PreviousMaskBindGroupOverride = previous?.AnalyticResource?.BindGroupPtr ?? 0,
                Bounds = bounds, TargetWidth = CurrentMaskTargetPixelWidthUInt,
                TargetHeight = CurrentMaskTargetPixelHeightUInt, DrawCalls = maskDraws
            });
            _maskStack.Push(new MaskTextureState(maskTexture, bounds, null));
            published = true;
        }
        catch (Exception failure) { setupFailure = failure; throw; }
        finally
        {
            if (!published)
            {
                // Unpublished white coverage must never become ordinary paint
                // if a caller handles the setup error and continues compiling.
                _drawCalls.RemoveRange(drawStart, _drawCalls.Count - drawStart);
                _textVerticesList.RemoveRange(textStart, _textVerticesList.Count - textStart);
                _legacyTextVertexCount = legacyStart;
                _pendingTextStart = pendingTextStart;
                _currentBatchType = BatchType.None;
                _activeTextStyles.RemoveRange(styleStart, _activeTextStyles.Count - styleStart);
                // Texture capacity was reserved before any setup. Return that
                // native owner before a managed list-pool growth can fault.
                if (maskTexture != null) _maskTexturePool.Add(maskTexture);
                if (maskDraws != null)
                {
                    try { ReturnMaskDrawCallList(maskDraws); }
                    catch (Exception cleanup)
                    {
                        try { if (setupFailure != null) setupFailure.Data["HintedGlyphMaskListCleanupFailure"] = cleanup; }
                        catch { }
                    }
                }
            }
        }
    }
}
