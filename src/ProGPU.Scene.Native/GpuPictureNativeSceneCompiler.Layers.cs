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
        if (command.Picture is not null || command.Path is null)
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
        if (command.Path.CombinedQueryKind == CombinedPathQueryKind.Empty ||
            (!command.Path.IsCombined && command.Path.Figures.Count == 0))
        {
            next = current with
            {
                HasClip = true,
                ClipRect = default
            };
            return true;
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
                    out NativeScenePathBooleanNode[] booleanNodes,
                    out error))
            {
                return false;
            }
            retainedVectorMask = new VectorMaskNode(
                parent,
                clipPath,
                segments,
                booleanNodes);
        }

        bool requiresVector = !canonical ||
            (current.MaskIndex >= 0 &&
                stateMasks[current.MaskIndex].Kind ==
                    StateMaskProgramKind.Vector) ||
            (current.MaskIndex >= 0 &&
                stateMasks[current.MaskIndex].BrushMasks is not null) ||
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
        BrushMaskNode? activeBrushes = current.MaskIndex >= 0
            ? stateMasks[current.MaskIndex].BrushMasks
            : null;
        if (activeBrushes is not null)
        {
            if (checked(activeBrushes.Count + 1) >
                NativeSceneLayerCompositeMask.MaximumComponentCount)
            {
                error = NativePictureCompileError.CapacityExceeded;
                return false;
            }
            program = new StateMaskProgram(
                retainedVectorMask,
                activeBrushes);
        }
        else if (canonical)
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
                        out NativeScenePathBooleanNode[] booleanNodes,
                        out error))
                {
                    return false;
                }
                current.SetCompiled(path, segments, booleanNodes);
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
        out NativeScenePathBooleanNode[] booleanNodes,
        out NativePictureCompileError error)
    {
        result = default;
        segments = [];
        booleanNodes = [];
        error = NativePictureCompileError.None;

        if (path.IsCombined)
        {
            return TryCompileBooleanVectorMaskGeometry(
                path,
                transform,
                out result,
                out segments,
                out booleanNodes,
                out error);
        }

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
            if (!IsValidCompiledPathSegment(in source))
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

    private static bool TryCompileBooleanVectorMaskGeometry(
        PathGeometry path,
        Matrix3x2 transform,
        out NativeSceneClipPath result,
        out NativePathSegment[] segments,
        out NativeScenePathBooleanNode[] booleanNodes,
        out NativePictureCompileError error)
    {
        result = default;
        segments = [];
        booleanNodes = [];
        error = NativePictureCompileError.None;
        if (!IsAcyclicCombinedGeometry(path) ||
            !path.TryGetBounds(out Vector2 minimum, out Vector2 maximum) ||
            !IsFinite(minimum) || !IsFinite(maximum) ||
            maximum.X <= minimum.X || maximum.Y <= minimum.Y)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }

        var segmentBuilder = new List<NativePathSegment>();
        var programBuilder = new List<NativeScenePathBooleanNode>();
        int stackDepth = 0;
        int maximumStackDepth = 0;
        if (!TryAppendBooleanVectorNode(
                path,
                segmentBuilder,
                programBuilder,
                0,
                ref stackDepth,
                ref maximumStackDepth,
                out error) ||
            stackDepth != 1 || maximumStackDepth > 16 ||
            programBuilder.Count is 0 or > 63 || segmentBuilder.Count == 0)
        {
            if (error == NativePictureCompileError.None)
                error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }

        segments = segmentBuilder.ToArray();
        booleanNodes = programBuilder.ToArray();
        result = new NativeSceneClipPath(
            0U,
            checked((ulong)segments.Length),
            minimum,
            maximum,
            transform,
            NativeClipOperation.Intersect,
            NativeFillRule.NonZero,
            PathAtlas.StandardCoverageSampleGrid,
            0U,
            checked((ulong)booleanNodes.Length));
        return true;
    }

    private static bool IsAcyclicCombinedGeometry(PathGeometry root)
    {
        var active = new HashSet<PathGeometry>(
            ReferenceEqualityComparer.Instance);
        return Visit(root, active, 0);

        static bool Visit(
            PathGeometry path,
            HashSet<PathGeometry> active,
            int depth)
        {
            if (!path.IsCombined)
                return true;
            if (depth >= 63 || !active.Add(path))
                return false;
            bool valid =
                (path.PathA is null || Visit(path.PathA, active, depth + 1)) &&
                (path.PathB is null || Visit(path.PathB, active, depth + 1));
            active.Remove(path);
            return valid;
        }
    }

    private static bool TryAppendBooleanVectorNode(
        PathGeometry path,
        List<NativePathSegment> segments,
        List<NativeScenePathBooleanNode> program,
        int recursionDepth,
        ref int stackDepth,
        ref int maximumStackDepth,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (program.Count >= 63 || recursionDepth >= 63)
        {
            error = NativePictureCompileError.CapacityExceeded;
            return false;
        }
        if (path.IsCombined)
        {
            switch (path.CombinedQueryKind)
            {
                case CombinedPathQueryKind.Empty:
                    if (stackDepth == 16) return false;
                    program.Add(new NativeScenePathBooleanNode(
                        0U, 0U, Vector2.Zero, Vector2.Zero,
                        NativeFillRule.NonZero,
                        NativePathBooleanNodeKind.Empty));
                    maximumStackDepth = Math.Max(maximumStackDepth, ++stackDepth);
                    return true;
                case CombinedPathQueryKind.ResultOperandA:
                    return path.PathA is not null && TryAppendBooleanVectorNode(
                        path.PathA, segments, program, recursionDepth + 1,
                        ref stackDepth,
                        ref maximumStackDepth, out error);
                case CombinedPathQueryKind.ResultOperandB:
                    return path.PathB is not null && TryAppendBooleanVectorNode(
                        path.PathB, segments, program, recursionDepth + 1,
                        ref stackDepth,
                        ref maximumStackDepth, out error);
            }
            if (path.PathA is null || path.PathB is null ||
                (uint)path.Op > 4U ||
                !TryAppendBooleanVectorNode(
                    path.PathA, segments, program, recursionDepth + 1,
                    ref stackDepth,
                    ref maximumStackDepth, out error) ||
                !TryAppendBooleanVectorNode(
                    path.PathB, segments, program, recursionDepth + 1,
                    ref stackDepth,
                    ref maximumStackDepth, out error) ||
                stackDepth < 2 || program.Count >= 63)
            {
                return false;
            }
            program.Add(new NativeScenePathBooleanNode(
                0U, 0U, Vector2.Zero, Vector2.Zero,
                NativeFillRule.NonZero,
                (NativePathBooleanNodeKind)((uint)path.Op + 2U)));
            stackDepth--;
            return true;
        }

        if (path.Figures.Count == 0)
        {
            if (stackDepth == 16)
            {
                error = NativePictureCompileError.CapacityExceeded;
                return false;
            }
            program.Add(new NativeScenePathBooleanNode(
                0U, 0U, Vector2.Zero, Vector2.Zero,
                NativeFillRule.NonZero,
                NativePathBooleanNodeKind.Empty));
            maximumStackDepth = Math.Max(maximumStackDepth, ++stackDepth);
            return true;
        }

        (GpuPathRecord[] records, GpuPathSegment[] sourceSegments) =
            PathAtlas.CompileFillPath(
                path,
                out float minimumX,
                out float minimumY,
                out float maximumX,
                out float maximumY);
        if (records.Length != 1 || sourceSegments.Length == 0 ||
            stackDepth == 16 || !float.IsFinite(minimumX) ||
            !float.IsFinite(minimumY) || !float.IsFinite(maximumX) ||
            !float.IsFinite(maximumY) || maximumX <= minimumX ||
            maximumY <= minimumY)
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }
        ulong segmentOffset = checked((ulong)segments.Count);
        for (int index = 0; index < sourceSegments.Length; index++)
        {
            ref readonly GpuPathSegment source = ref sourceSegments[index];
            if (!IsValidCompiledPathSegment(in source))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            segments.Add(new NativePathSegment(
                (NativePathSegmentKind)source.SegmentType,
                source.P0, source.P1, source.P2, source.P3,
                source.Pad0, source.Pad1, source.Pad2));
        }
        program.Add(new NativeScenePathBooleanNode(
            segmentOffset,
            checked((ulong)sourceSegments.Length),
            new Vector2(minimumX, minimumY),
            new Vector2(maximumX, maximumY),
            path.FillRule == FillRule.EvenOdd
                ? NativeFillRule.EvenOdd
                : NativeFillRule.NonZero,
            NativePathBooleanNodeKind.Leaf));
        maximumStackDepth = Math.Max(maximumStackDepth, ++stackDepth);
        return true;
    }

    private static bool IsValidCompiledPathSegment(
        in GpuPathSegment source)
    {
        bool arc = source.SegmentType == (uint)NativePathSegmentKind.Arc;
        return source.SegmentType <= (uint)NativePathSegmentKind.Arc &&
            IsFinite(source.P0) && IsFinite(source.P1) &&
            IsFinite(source.P2) && IsFinite(source.P3) &&
            (arc
                ? source.P3.X > 0f && source.P3.Y > 0f &&
                    float.IsFinite(BitConverter.Int32BitsToSingle(
                        unchecked((int)source.Pad0))) &&
                    float.IsFinite(BitConverter.Int32BitsToSingle(
                        unchecked((int)source.Pad1))) &&
                    float.IsFinite(BitConverter.Int32BitsToSingle(
                        unchecked((int)source.Pad2)))
                : source.Pad0 == 0U && source.Pad1 == 0U &&
                    source.Pad2 == 0U);
    }

    private static bool TryGetOpacityMaskState(
        in RenderCommand command,
        StateSnapshot current,
        List<StateMaskProgram> stateMasks,
        out StateSnapshot next,
        out NativePictureCompileError error)
    {
        next = current;
        error = NativePictureCompileError.None;
        if (command.Picture is not null || command.Path is not null ||
            command.Brush is null)
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }
        if (!IsFiniteRect(command.Rect) || command.Rect.IsEmpty ||
            !TryGetAffine(command.Transform, out Matrix3x2 transform))
        {
            error = NativePictureCompileError.InvalidState;
            return false;
        }

        if (command.Brush is SolidColorBrush solid &&
            IsAxisAlignedClipTransform(transform))
        {
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

        float determinant = transform.GetDeterminant();
        if (!float.IsFinite(determinant) ||
            MathF.Abs(determinant) <= 0.000001f ||
            !Matrix3x2.Invert(transform, out Matrix3x2 inverse) ||
            !IsFinite(inverse))
        {
            error = NativePictureCompileError.UnsupportedTransform;
            return false;
        }

        var snapshot = new NativeBrushTableBuilder();
        if (!snapshot.TrySnapshot(
                command.Brush,
                out NativeSceneBrush nativeBrush,
                out NativeSceneGradientStop[] stops,
                out error))
        {
            return false;
        }
        var mask = new NativeSceneLayerBrushMask(
            new NativeImageRect(
                command.Rect.X,
                command.Rect.Y,
                command.Rect.Width,
                command.Rect.Height),
            transform,
            in nativeBrush,
            checked((uint)stops.Length));
        StateMaskProgram program;
        if (current.MaskIndex < 0)
        {
            program = new StateMaskProgram(in mask, stops);
        }
        else
        {
            StateMaskProgram active = stateMasks[current.MaskIndex];
            var brushNode = new BrushMaskNode(
                active.BrushMasks,
                in mask,
                stops);
            uint vectorComponent = active.VectorMask is null ? 0U : 1U;
            if (checked((uint)brushNode.Count + vectorComponent) >
                NativeSceneLayerCompositeMask.MaximumComponentCount)
            {
                error = NativePictureCompileError.CapacityExceeded;
                return false;
            }
            if (active.Kind == StateMaskProgramKind.Analytic &&
                !TryCompileVectorMaskChain(active.VectorMask!, out error))
            {
                return false;
            }
            program = new StateMaskProgram(
                active.VectorMask,
                brushNode);
        }
        int maskIndex = stateMasks.Count;
        stateMasks.Add(program);
        next = current with { MaskIndex = maskIndex };
        return true;
    }
}
