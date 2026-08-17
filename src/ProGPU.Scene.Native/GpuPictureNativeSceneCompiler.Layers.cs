using System.Numerics;
using ProGPU.Backend;
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
            command.Path.IsCombined)
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }
        if (!TryGetAffine(command.Transform, out Matrix3x2 transform))
        {
            error = NativePictureCompileError.UnsupportedTransform;
            return false;
        }
        if (MathF.Abs(transform.GetDeterminant()) <= 0.000001f)
        {
            error = NativePictureCompileError.UnsupportedTransform;
            return false;
        }
        VectorMaskNode? parent = current.MaskIndex >= 0
            ? stateMasks[current.MaskIndex].VectorMask
            : null;
        if ((parent?.Count ?? 0) >= 64)
        {
            error = NativePictureCompileError.CapacityExceeded;
            return false;
        }
        RoundedRectanglePathContour contour = default;
        bool canonical = command.Path.Figures.Count == 1 &&
            RoundedRectanglePathGeometry.TryReadCanonicalContour(
                command.Path.Figures[0],
                out contour);
        VectorMaskNode retainedVectorMask;
        if (canonical)
        {
            retainedVectorMask = new VectorMaskNode(
                parent,
                command.Path,
                transform);
        }
        else
        {
            if (!TryCompileVectorMaskGeometry(
                    command.Path,
                    transform,
                    out NativeSceneClipPath clipPath,
                    out NativePathSegment[] segments,
                    out error))
            {
                return false;
            }
            retainedVectorMask = new VectorMaskNode(
                parent,
                clipPath,
                segments);
        }

        bool requiresVector = !canonical ||
            (current.MaskIndex >= 0 &&
                stateMasks[current.MaskIndex].Count == 0) ||
            (current.MaskIndex >= 0 &&
                stateMasks[current.MaskIndex].Count >=
                    NativeSceneLayerMaskChain.MaximumMaskCount);
        if (requiresVector && !TryCompileVectorMaskChain(
                retainedVectorMask,
                out error))
        {
            return false;
        }

        StateMaskProgram program;
        if (canonical)
        {
            var mask = new NativeSceneLayerMask(
                new NativeImageRect(
                    contour.Left,
                    contour.Top,
                    contour.Width,
                    contour.Height),
                transform,
                contour.CornerRadiiX,
                contour.CornerRadiiY);
            if (current.MaskIndex < 0)
            {
                program = new StateMaskProgram(mask, retainedVectorMask);
            }
            else if (!stateMasks[current.MaskIndex].TryAppend(
                    mask,
                    retainedVectorMask,
                    out program))
            {
                program = new StateMaskProgram(retainedVectorMask);
            }
        }
        else
        {
            program = new StateMaskProgram(retainedVectorMask);
        }
        int maskIndex = stateMasks.Count;
        stateMasks.Add(program);
        next = current with { MaskIndex = maskIndex };
        return true;
    }

    private static bool TryCompileVectorMaskChain(
        VectorMaskNode node,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        VectorMaskNode? current = node;
        while (current is not null)
        {
            if (current.Segments is null)
            {
                if (current.Geometry is null ||
                    !TryCompileVectorMaskGeometry(
                        current.Geometry,
                        current.Transform,
                        out NativeSceneClipPath path,
                        out NativePathSegment[] segments,
                        out error))
                {
                    return false;
                }
                current.SetCompiled(path, segments);
            }
            current = current.Parent;
        }
        return true;
    }

    private static bool TryCompileVectorMaskGeometry(
        PathGeometry path,
        Matrix3x2 transform,
        out NativeSceneClipPath result,
        out NativePathSegment[] segments,
        out NativePictureCompileError error)
    {
        result = default;
        segments = [];
        error = NativePictureCompileError.None;

        (_, GpuPathSegment[] sourceSegments) = PathAtlas.CompileFillPath(
            path,
            out float minimumX,
            out float minimumY,
            out float maximumX,
            out float maximumY);
        if (sourceSegments.Length == 0 || !float.IsFinite(minimumX) ||
            !float.IsFinite(minimumY) || !float.IsFinite(maximumX) ||
            !float.IsFinite(maximumY) || maximumX <= minimumX ||
            maximumY <= minimumY)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }

        segments = GC.AllocateUninitializedArray<NativePathSegment>(
            sourceSegments.Length);
        for (int index = 0; index < sourceSegments.Length; index++)
        {
            ref readonly GpuPathSegment source = ref sourceSegments[index];
            bool arc = source.SegmentType == (uint)NativePathSegmentKind.Arc;
            if (source.SegmentType > (uint)NativePathSegmentKind.Arc ||
                !IsFinite(source.P0) || !IsFinite(source.P1) ||
                !IsFinite(source.P2) || !IsFinite(source.P3) ||
                (arc
                    ? source.P3.X <= 0f || source.P3.Y <= 0f ||
                        !float.IsFinite(BitConverter.Int32BitsToSingle(
                            unchecked((int)source.Pad0))) ||
                        !float.IsFinite(BitConverter.Int32BitsToSingle(
                            unchecked((int)source.Pad1))) ||
                        !float.IsFinite(BitConverter.Int32BitsToSingle(
                            unchecked((int)source.Pad2)))
                    : source.Pad0 != 0U || source.Pad1 != 0U ||
                        source.Pad2 != 0U))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            segments[index] = new NativePathSegment(
                (NativePathSegmentKind)source.SegmentType,
                source.P0,
                source.P1,
                source.P2,
                source.P3,
                source.Pad0,
                source.Pad1,
                source.Pad2);
        }

        uint sampleGrid = PathAtlas.StandardCoverageSampleGrid;
        result = new NativeSceneClipPath(
            0U,
            checked((ulong)segments.Length),
            new Vector2(minimumX, minimumY),
            new Vector2(maximumX, maximumY),
            transform,
            NativeClipOperation.Intersect,
            path.FillRule == FillRule.EvenOdd
                ? NativeFillRule.EvenOdd
                : NativeFillRule.NonZero,
            sampleGrid);
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
