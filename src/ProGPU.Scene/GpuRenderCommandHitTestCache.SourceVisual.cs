using System;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public sealed partial class GpuRenderCommandHitTestCacheBuilder
{
    private Action<Visual>? _sourceEmbeddedVisualObserver;
    /// <summary>
    /// Captures retained source geometry without rendering, allocating raster
    /// targets, calling OnRender, or substituting visual-size rectangles. Every
    /// visible node must publish <see cref="ISourceGeometryHitTestCommands"/>.
    /// Unsupported capture faults publication until Clear; partial indices are
    /// never valid results. Visual opacity is ignored, not actual visibility.
    /// </summary>
    public void AddSourceVisual(Visual visual, Matrix4x4 parentTransform)
        => AddSourceVisual(visual, parentTransform, null, true, true);

    internal void AddSourceVisual(
        Visual visual, Matrix4x4 parentTransform, Vector2? offsetOverride,
        bool includeLocalTransform, bool includeLocalVisualState,
        Action<Visual>? embeddedVisualObserver = null)
    {
        ArgumentNullException.ThrowIfNull(visual);
        if (_imageHitClipDepth != 0)
            return; // the enclosing source image already owns its complete input rectangle
        Action<Visual>? previousObserver = _sourceEmbeddedVisualObserver;
        _sourceEmbeddedVisualObserver = embeddedVisualObserver;
        try
        {
            CaptureSourceVisual(visual, parentTransform, offsetOverride,
                includeLocalTransform, includeLocalVisualState, 0);
        }
        catch
        {
            _sourceCaptureFailed = true;
            throw;
        }
        finally
        {
            _sourceEmbeddedVisualObserver = previousObserver;
        }
    }

    private void CaptureSourceVisual(
        Visual visual, Matrix4x4 parentTransform, Vector2? offsetOverride,
        bool includeLocalTransform, bool includeLocalVisualState, int depth)
    {
        CheckSourceCaptureDepth(depth);
        if (!visual.IsVisible)
            return;
        if (visual is not ISourceGeometryHitTestCommands source)
            throw new NotSupportedException("Retained source input requires typed materialized commands for every visible visual.");
        if (visual.Effect is { PreservesSourceHitGeometry: false } || visual.RequiresLayerCache ||
            visual.OpacityMask != null || visual.OpacityMaskPicture != null)
            throw new NotSupportedException("Source hit-only traversal requires an identity-mapped effect and no required cache source or opacity mask.");

        // CacheAsLayer changes raster reuse/resolution, not source geometry.
        // Required cached-picture sources have a separate refresh/ownership
        // contract and are not admitted by this optional visual-cache policy.

        Matrix4x4 localTransform = includeLocalTransform
            ? offsetOverride.HasValue ? visual.GetLocalTransform(offsetOverride.Value) : visual.GetLocalTransform()
            : Matrix4x4.CreateTranslation(offsetOverride.GetValueOrDefault().X, offsetOverride.GetValueOrDefault().Y, 0);
        Matrix4x4 globalTransform = localTransform * parentTransform;
        int clipDepth = _clipStack.Count;
        if (includeLocalVisualState)
        {
            if (visual.ClipBounds is { } clip)
                PushSourceRectangleClip(clip, globalTransform);
            if (visual.OuterClipBounds is { } outerClip)
                PushSourceRectangleClip(outerClip, parentTransform);
            if (visual.LocalCompositeClip is { } localClip)
                PushSourceCompositeClip(localClip, globalTransform);
            ReadOnlySpan<VisualCompositeClip> outerClips = visual.OuterCompositeClips;
            for (int i = 0; i < outerClips.Length; i++)
                PushSourceCompositeClip(outerClips[i], parentTransform);
            if (visual.GeometryClip is { } geometry)
                PushSourceGeometryClip(geometry, globalTransform);
        }

        if (visual.HasTemplate)
            CaptureSourceChildren(visual, globalTransform, depth);
        DrawingContext commands = source.SourceHitTestCommands;
        CaptureSourceCommands(commands.Commands, commands, visual.HitTestId, globalTransform, depth);
        if (!visual.HasTemplate)
            CaptureSourceChildren(visual, globalTransform, depth);

        while (_clipStack.Count > clipDepth)
            PopClip();
    }

    private void PushSourceCompositeClip(VisualCompositeClip clip, Matrix4x4 transform)
    {
        transform = clip.Transform * transform;
        if (clip.Bounds is { } bounds)
            PushSourceRectangleClip(bounds, transform);
        else if (clip.Geometry is { } geometry)
            PushSourceGeometryClip(geometry, transform);
    }

    private void PushSourceRectangleClip(Rect bounds, Matrix4x4 transform)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y) ||
            !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            throw new NotSupportedException("Source rectangular clips require finite bounds.");
        if (!IsFiniteInvertibleAffine2D(transform))
        {
            _clipStack.Push(ClipState.Empty);
            return;
        }
        if ((transform.M12 == 0 && transform.M21 == 0) ||
            (transform.M11 == 0 && transform.M22 == 0))
        {
            PushClip(bounds, transform);
            return;
        }
        // Only an axis-preserving rectangle can use the existing bounds clip.
        // Other placements retain four real edges through the shared path encoder.
        PushSourceGeometryClip(PrimitivePathGeometry.CreateRectangle(
            bounds.X, bounds.Y, bounds.Width, bounds.Height), transform);
    }

    private void PushSourceGeometryClip(PathGeometry? geometry, Matrix4x4 transform)
    {
        if (geometry == null)
            throw new NotSupportedException("Source geometry clipping requires an actual path.");
        if (!IsFiniteInvertibleAffine2D(transform) ||
            (!geometry.IsCombined && geometry.Figures.Count == 0))
        {
            _clipStack.Push(ClipState.Empty);
            return;
        }
        PushGeometryClip(new RenderCommand { Path = geometry }, transform, requireExact: true);
    }

    private void CaptureSourceChildren(Visual visual, Matrix4x4 transform, int depth)
    {
        if (visual is not ContainerVisual container)
            return;
        var children = container.Children;
        for (int i = 0; i < children.Count; i++)
            CaptureSourceVisual(children[i], transform, null, true, true, depth + 1);
    }

    private void CaptureSourceCommands(
        System.Collections.Generic.IReadOnlyList<RenderCommand> commands,
        IRenderDataProvider provider, int ownerId, Matrix4x4 transform, int depth)
    {
        CheckSourceCaptureDepth(depth);
        int clipDepth = _clipStack.Count;
        int opacityDepth = _opacityStack.Count;
        int imageDepth = _imageHitClipDepth;
        int pointRegionDepth = _pointRegionStack.Count;
        int maskDepth = 0;
        for (int i = 0; i < commands.Count; i++)
        {
            RenderCommand command = commands[i];
            Matrix4x4 resolvedTransform = command.UseGpuTransforms ? command.CameraView * transform : transform;
            if (command.Type != RenderCommandType.DrawPath && command.Transform != default)
                resolvedTransform = command.Transform * resolvedTransform;
            command.UseGpuTransforms = false;
            command.CameraView = default;
            // Internal rendering cannot change a source-declared image rectangle.
            // Still consume its clip terminators through the shared scope machine.
            if (_imageHitClipDepth != 0)
            {
                AddCommand(command, resolvedTransform, provider);
                continue;
            }
            int id = command.HitTestId != 0 ? command.HitTestId : ownerId;
            if (command.SourceHitGeometry.Kind is SourceHitTestGeometryKind.PointRectangleBegin or SourceHitTestGeometryKind.PointRectangleEnd)
            {
                if (command.SourceHitGeometry.Kind == SourceHitTestGeometryKind.PointRectangleEnd && _pointRegionStack.Count <= pointRegionDepth)
                    throw new InvalidOperationException("Source commands cannot pop an enclosing point region.");
                AddCommand(command, resolvedTransform, provider, id);
                continue;
            }
            switch (command.Type)
            {
                case RenderCommandType.DrawPicture:
                    if (command.Picture is { } picture)
                        CaptureSourceCommands(picture.RetainedCommands, picture, id, resolvedTransform, depth + 1);
                    break;
                case RenderCommandType.DrawVisual:
                    if (command.Visual is { } child)
                    {
                        _sourceEmbeddedVisualObserver?.Invoke(child);
                        CaptureSourceVisual(child, resolvedTransform, null, true, true, depth + 1);
                    }
                    break;
                case RenderCommandType.PopClip:
                case RenderCommandType.PopGeometryClip:
                    if (_clipStack.Count <= clipDepth)
                        throw new InvalidOperationException("A source command cannot pop an enclosing visual clip.");
                    AddCommand(command, resolvedTransform, provider);
                    break;
                case RenderCommandType.PopOpacity:
                    if (_opacityStack.Count <= opacityDepth)
                        throw new InvalidOperationException("A source command cannot pop an enclosing opacity scope.");
                    AddCommand(command, resolvedTransform, provider);
                    break;
                case RenderCommandType.PushClip:
                    if (command.IsImageHitTestScope && id == 0)
                    {
                        // Synthetic containers without a source owner are not
                        // input targets, but their image scope still owns its
                        // flattened render contents and matching terminator.
                        _imageHitClipDepth = 1;
                        break;
                    }
                    if (command.IsImageHitTestScope)
                        AddCommand(command, resolvedTransform, provider, id);
                    else
                        PushSourceRectangleClip(command.Rect, resolvedTransform);
                    break;
                case RenderCommandType.PushGeometryClip:
                    PushSourceGeometryClip(command.Path, resolvedTransform);
                    break;
                case RenderCommandType.PushOpacity:
                    AddCommand(command, resolvedTransform, provider, id);
                    break;
                case RenderCommandType.PushOpacityMask:
                    // WPF source drawing masks affect raster alpha, not point
                    // or region input. Do not inspect their brush or bounds.
                    if (++maskDepth >= 256)
                        throw new InvalidOperationException("Source opacity mask nesting exceeded its bounded depth.");
                    break;
                case RenderCommandType.PopOpacityMask:
                    if (maskDepth == 0)
                        throw new InvalidOperationException("Source commands cannot pop an enclosing opacity mask.");
                    maskDepth--;
                    break;
                case RenderCommandType.DrawRect:
                case RenderCommandType.DrawRoundedRect:
                case RenderCommandType.DrawEllipse:
                case RenderCommandType.DrawCircle:
                case RenderCommandType.DrawLine:
                case RenderCommandType.DrawBezier:
                case RenderCommandType.DrawCubicBezier:
                case RenderCommandType.DrawPath:
                case RenderCommandType.DrawTexture:
                case RenderCommandType.FillTriangle:
                case RenderCommandType.FillQuad:
                case RenderCommandType.DrawVertexMesh:
                case RenderCommandType.DrawPointBatch:
                case RenderCommandType.DrawPolyline:
                    if (id != 0)
                        AddCommand(command, resolvedTransform, provider, id);
                    break;
                case RenderCommandType.DrawGlyphRun:
                    if (command.Rect.IsEmpty)
                        throw new NotSupportedException("Source glyph input requires authoritative nonempty ink bounds; position estimates are not source geometry.");
                    if (id != 0)
                        AddCommand(command, resolvedTransform, provider, id);
                    break;
                default:
                    throw new NotSupportedException($"Source hit-only command capture does not support {command.Type}.");
            }
        }
        if (maskDepth != 0 || _clipStack.Count != clipDepth || _opacityStack.Count != opacityDepth || _imageHitClipDepth != imageDepth || _pointRegionStack.Count != pointRegionDepth)
            throw new InvalidOperationException("Source retained command scopes must be balanced.");
    }

    private static void CheckSourceCaptureDepth(int depth)
    {
        if (depth >= 256)
            throw new InvalidOperationException("Source hit-test traversal exceeded its bounded visual/picture depth.");
    }
}
