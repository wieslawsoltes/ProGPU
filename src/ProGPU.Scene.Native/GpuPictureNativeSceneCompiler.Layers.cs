using System.Numerics;
using ProGPU.Backend.Native;
using ProGPU.Vector;

namespace ProGPU.Scene.Native;

public static partial class GpuPictureNativeSceneCompiler
{
    private static bool TryGetGeometryMaskState(
        in RenderCommand command,
        StateSnapshot current,
        List<StateMaskProgram> stateMasks,
        out StateSnapshot next,
        out NativePictureCompileError error)
    {
        next = current;
        error = NativePictureCompileError.None;
        if (command.Picture is not null || command.Path is null ||
            command.Path.IsCombined ||
            command.Path.Figures.Count != 1)
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }
        if (!TryGetAffine(command.Transform, out Matrix3x2 transform))
        {
            error = NativePictureCompileError.UnsupportedTransform;
            return false;
        }
        if (!RoundedRectanglePathGeometry.TryReadCanonicalContour(
                command.Path.Figures[0],
                out RoundedRectanglePathContour contour))
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }

        var mask = new NativeSceneLayerMask(
            new NativeImageRect(
                contour.Left,
                contour.Top,
                contour.Width,
                contour.Height),
            transform,
            contour.CornerRadiiX,
            contour.CornerRadiiY);
        StateMaskProgram program;
        if (current.MaskIndex < 0)
        {
            program = new StateMaskProgram(mask);
        }
        else if (!stateMasks[current.MaskIndex].TryAppend(mask, out program))
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }
        int maskIndex = stateMasks.Count;
        stateMasks.Add(program);
        next = current with { MaskIndex = maskIndex };
        return true;
    }

    private static bool TryGetSolidOpacityMaskState(
        in RenderCommand command,
        StateSnapshot current,
        out StateSnapshot next,
        out NativePictureCompileError error)
    {
        next = current;
        error = NativePictureCompileError.None;
        if (command.Picture is not null || command.Path is not null ||
            command.Brush is not SolidColorBrush solid)
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }
        if (!IsFiniteRect(command.Rect) || command.Rect.IsEmpty ||
            !TryGetAffine(command.Transform, out Matrix3x2 transform) ||
            !IsAxisAlignedClipTransform(transform))
        {
            error = NativePictureCompileError.InvalidState;
            return false;
        }

        float sourceAlpha = solid.Color.W * solid.Opacity;
        if (!float.IsFinite(solid.Color.W) ||
            !float.IsFinite(solid.Opacity) ||
            !float.IsFinite(sourceAlpha))
        {
            error = NativePictureCompileError.InvalidState;
            return false;
        }
        float alpha = Math.Clamp(sourceAlpha, 0f, 1f);
        NativeImageRect clip = TransformBounds(command.Rect, transform);
        if (current.HasClip)
        {
            clip = Intersect(current.ClipRect, clip);
        }
        next = current with
        {
            Opacity = current.Opacity * alpha,
            HasClip = true,
            ClipRect = clip
        };
        return true;
    }
}
