using System.Numerics;

namespace ProGPU.Scene;

/// <summary>
/// Reads conservative, clip-aware world bounds from a retained two-dimensional
/// <see cref="GpuPicture"/> without rendering it or materializing its command
/// array. Nested pictures are traversed through their typed retained command
/// storage and share the normal hit-test primitive bounds implementation.
/// </summary>
public static class GpuPictureBounds
{
    private const int MaximumNestedPictureDepth = 64;

    public static bool TryGetBounds(
        GpuPicture picture,
        out Rect bounds) =>
        TryGetBounds(picture, Matrix4x4.Identity, out bounds);

    public static bool TryGetBounds(
        GpuPicture picture,
        Matrix4x4 transform,
        out Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(picture);
        bounds = default;
        if (!IsFiniteAffine2D(transform))
        {
            return false;
        }

        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        var active = new HashSet<GpuPicture>(
            ReferenceEqualityComparer.Instance);
        if (!TryAddPicture(
                picture,
                transform,
                builder,
                active,
                depth: 0))
        {
            return false;
        }

        if (builder.TryGetBounds(out Vector2 minimum, out Vector2 maximum))
        {
            bounds = new Rect(
                minimum.X,
                minimum.Y,
                maximum.X - minimum.X,
                maximum.Y - minimum.Y);
        }
        return true;
    }

    private static bool TryAddPicture(
        GpuPicture picture,
        Matrix4x4 parentTransform,
        GpuRenderCommandHitTestCacheBuilder builder,
        HashSet<GpuPicture> active,
        int depth)
    {
        if (depth >= MaximumNestedPictureDepth || !active.Add(picture))
        {
            return false;
        }

        try
        {
            int clipDepth = 0;
            int opacityDepth = 0;
            int opacityMaskDepth = 0;
            int blendDepth = 0;
            for (int index = 0; index < picture.CommandCount; index++)
            {
                RenderCommand command = picture.GetCommand(index);
                if (command.UseGpuTransforms ||
                    !IsSupportedTwoDimensionalCommand(command.Type))
                {
                    return false;
                }

                Matrix4x4 commandTransform = command.Transform == default
                    ? parentTransform
                    : command.Transform * parentTransform;
                if (!IsFiniteAffine2D(commandTransform))
                {
                    return false;
                }

                if (command.Type == RenderCommandType.DrawPicture)
                {
                    if (command.Picture is null ||
                        !TryAddPicture(
                            command.Picture,
                            commandTransform,
                            builder,
                            active,
                            depth + 1))
                    {
                        return false;
                    }
                    continue;
                }

                if (!TryUpdateStateDepth(
                        command.Type,
                        ref clipDepth,
                        ref opacityDepth,
                        ref opacityMaskDepth,
                        ref blendDepth))
                {
                    return false;
                }

                // DrawPath composes its command-local transform inside the
                // shared bounds/hit-test lowering. Other commands receive the
                // fully composed transform, matching Compositor replay.
                Matrix4x4 hitTestTransform =
                    command.Type == RenderCommandType.DrawPath
                        ? parentTransform
                        : commandTransform;
                builder.AddCommand(
                    command,
                    hitTestTransform,
                    picture);
            }
            return clipDepth == 0 &&
                opacityDepth == 0 &&
                opacityMaskDepth == 0 &&
                blendDepth == 0;
        }
        finally
        {
            active.Remove(picture);
        }
    }

    private static bool IsSupportedTwoDimensionalCommand(
        RenderCommandType type) => type is
        RenderCommandType.DrawRect or
        RenderCommandType.DrawPath or
        RenderCommandType.DrawText or
        RenderCommandType.DrawTexture or
        RenderCommandType.PushClip or
        RenderCommandType.PopClip or
        RenderCommandType.PushOpacity or
        RenderCommandType.PopOpacity or
        RenderCommandType.DrawLine or
        RenderCommandType.DrawEllipse or
        RenderCommandType.DrawCircle or
        RenderCommandType.DrawRoundedRect or
        RenderCommandType.DrawBezier or
        RenderCommandType.DrawCubicBezier or
        RenderCommandType.DrawPolyline or
        RenderCommandType.FillTriangle or
        RenderCommandType.FillQuad or
        RenderCommandType.DrawPicture or
        RenderCommandType.PushGeometryClip or
        RenderCommandType.PopGeometryClip or
        RenderCommandType.PushOpacityMask or
        RenderCommandType.PopOpacityMask or
        RenderCommandType.PushBlendMode or
        RenderCommandType.PopBlendMode or
        RenderCommandType.DrawGlyphRun or
        RenderCommandType.DrawVertexMesh or
        RenderCommandType.DrawPointBatch or
        RenderCommandType.DrawDotGrid;

    private static bool TryUpdateStateDepth(
        RenderCommandType type,
        ref int clipDepth,
        ref int opacityDepth,
        ref int opacityMaskDepth,
        ref int blendDepth)
    {
        switch (type)
        {
            case RenderCommandType.PushClip:
            case RenderCommandType.PushGeometryClip:
                clipDepth++;
                break;
            case RenderCommandType.PopClip:
            case RenderCommandType.PopGeometryClip:
                if (clipDepth == 0)
                {
                    return false;
                }
                clipDepth--;
                break;
            case RenderCommandType.PushOpacity:
                opacityDepth++;
                break;
            case RenderCommandType.PopOpacity:
                if (opacityDepth == 0)
                {
                    return false;
                }
                opacityDepth--;
                break;
            case RenderCommandType.PushOpacityMask:
                opacityMaskDepth++;
                break;
            case RenderCommandType.PopOpacityMask:
                if (opacityMaskDepth == 0)
                {
                    return false;
                }
                opacityMaskDepth--;
                break;
            case RenderCommandType.PushBlendMode:
                blendDepth++;
                break;
            case RenderCommandType.PopBlendMode:
                if (blendDepth == 0)
                {
                    return false;
                }
                blendDepth--;
                break;
        }
        return true;
    }

    private static bool IsFiniteAffine2D(in Matrix4x4 value) =>
        float.IsFinite(value.M11) &&
        float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) &&
        float.IsFinite(value.M22) &&
        float.IsFinite(value.M41) &&
        float.IsFinite(value.M42) &&
        value.M13 == 0f && value.M14 == 0f &&
        value.M23 == 0f && value.M24 == 0f &&
        value.M31 == 0f && value.M32 == 0f &&
        value.M33 == 1f && value.M34 == 0f &&
        value.M43 == 0f && value.M44 == 1f;
}
