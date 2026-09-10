using System.Numerics;

namespace ProGPU.Scene.Native;

public static partial class GpuPictureNativeSceneCompiler
{
    private const int MaximumNestedPictureDepth = 64;

    private readonly record struct FlattenedCommand(
        GpuPicture Picture,
        int CommandIndex,
        Matrix3x2 Transform,
        int OwnerId,
        int SourceCommandIndex,
        RenderCommandType SourceCommandType,
        Matrix4x4 CameraView,
        float DpiScaleMultiplier,
        bool IsBoundary = false);

    private static bool ContainsNestedPicture(GpuPicture picture)
    {
        for (int index = 0; index < picture.CommandCount; index++)
        {
            if (IsNestedPictureCommand(picture.GetCommand(index)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryFlattenPicture(
        GpuPicture picture,
        Matrix3x2 rootTransform,
        List<FlattenedCommand> commands,
        out int sourceCommandCount,
        out NativePictureCompileFailure failure)
    {
        var active = new HashSet<GpuPicture>(ReferenceEqualityComparer.Instance);
        var staticBufferLeases = new List<DxfStaticBuffer.RenderLease>();
        int nextOwnerId = 0;
        sourceCommandCount = 0;
        try
        {
            return TryFlattenPicture(
                picture,
                rootTransform,
                Matrix4x4.Identity,
                1f,
                commands,
                active,
                staticBufferLeases,
                ref nextOwnerId,
                ref sourceCommandCount,
                0,
                -1,
                default,
                out failure);
        }
        finally
        {
            for (int index = staticBufferLeases.Count - 1;
                index >= 0;
                index--)
            {
                staticBufferLeases[index].Dispose();
            }
        }
    }

    private static bool TryFlattenPicture(
        GpuPicture picture,
        Matrix3x2 parentTransform,
        Matrix4x4 parentCameraView,
        float parentDpiScaleMultiplier,
        List<FlattenedCommand> commands,
        HashSet<GpuPicture> active,
        List<DxfStaticBuffer.RenderLease> staticBufferLeases,
        ref int nextOwnerId,
        ref int sourceCommandCount,
        int depth,
        int outerCommandIndex,
        RenderCommandType outerCommandType,
        out NativePictureCompileFailure failure)
    {
        failure = NativePictureCompileFailure.None;
        if (depth >= MaximumNestedPictureDepth)
        {
            failure = new(
                NativePictureCompileError.CapacityExceeded,
                outerCommandIndex,
                outerCommandType);
            return false;
        }
        if (!active.Add(picture))
        {
            failure = new(
                NativePictureCompileError.InvalidState,
                outerCommandIndex,
                outerCommandType);
            return false;
        }

        int ownerId = nextOwnerId++;
        try
        {
            for (int index = 0; index < picture.CommandCount; index++)
            {
                if (sourceCommandCount == int.MaxValue)
                {
                    failure = new(
                        NativePictureCompileError.CapacityExceeded,
                        outerCommandIndex,
                        outerCommandType);
                    return false;
                }
                sourceCommandCount++;
                RenderCommand command = picture.GetCommand(index);
                int sourceIndex = outerCommandIndex >= 0
                    ? outerCommandIndex
                    : index;
                RenderCommandType sourceType = outerCommandIndex >= 0
                    ? outerCommandType
                    : command.Type;
                bool isNestedPicture = IsNestedPictureCommand(command);
                bool supportsGpuTransforms =
                    command.Type == RenderCommandType.DrawPicture;
                if ((!supportsGpuTransforms && command.UseGpuTransforms) ||
                    !TryGetAffine(command.Transform, out Matrix3x2 localTransform))
                {
                    failure = new(
                        NativePictureCompileError.UnsupportedTransform,
                        sourceIndex,
                        sourceType);
                    return false;
                }

                Matrix3x2 transform = localTransform * parentTransform;
                if (isNestedPicture)
                {
                    if (!TryGetNestedPicture(
                            command,
                            out GpuPicture? nestedPicture,
                            out float nestedDpiScaleMultiplier,
                            staticBufferLeases,
                            out NativePictureCompileError nestedError))
                    {
                        failure = new(
                            nestedError,
                            sourceIndex,
                            sourceType);
                        return false;
                    }
                    float dpiScaleMultiplier =
                        parentDpiScaleMultiplier * nestedDpiScaleMultiplier;
                    if (!float.IsFinite(dpiScaleMultiplier) ||
                        dpiScaleMultiplier <= 0f)
                    {
                        failure = new(
                            NativePictureCompileError.InvalidArgument,
                            sourceIndex,
                            sourceType);
                        return false;
                    }
                    Matrix3x2 nestedTransform = transform;
                    Matrix4x4 cameraView = parentCameraView;
                    if (command.UseGpuTransforms)
                    {
                        if (command.CameraView == default ||
                            !IsFinite(command.CameraView))
                        {
                            failure = new(
                                NativePictureCompileError.UnsupportedTransform,
                                sourceIndex,
                                sourceType);
                            return false;
                        }

                        // Match Compositor's late-transform contract: a GPU
                        // picture starts child geometry from its local transform
                        // and replaces the active camera with CameraView followed
                        // by the enclosing ordinary transform.
                        nestedTransform = localTransform;
                        cameraView = command.CameraView *
                            ToMatrix4x4(parentTransform);
                    }
                    if (!TryFlattenPicture(
                            nestedPicture!,
                            nestedTransform,
                            cameraView,
                            dpiScaleMultiplier,
                            commands,
                            active,
                            staticBufferLeases,
                            ref nextOwnerId,
                            ref sourceCommandCount,
                            depth + 1,
                            sourceIndex,
                            sourceType,
                            out failure))
                    {
                        return false;
                    }
                    continue;
                }

                if (parentCameraView != Matrix4x4.Identity &&
                    !IsNative3DCommand(command))
                {
                    if (!TryGetAffine(
                            parentCameraView,
                            out Matrix3x2 cameraTransform))
                    {
                        failure = new(
                            NativePictureCompileError.UnsupportedTransform,
                            sourceIndex,
                            sourceType);
                        return false;
                    }
                    transform *= cameraTransform;
                }

                commands.Add(new(
                    picture,
                    index,
                    transform,
                    ownerId,
                    sourceIndex,
                    sourceType,
                    parentCameraView,
                    parentDpiScaleMultiplier));
            }

            if (outerCommandIndex >= 0)
            {
                commands.Add(new(
                    picture,
                    -1,
                    Matrix3x2.Identity,
                    ownerId,
                    outerCommandIndex,
                    outerCommandType,
                    parentCameraView,
                    parentDpiScaleMultiplier,
                    IsBoundary: true));
            }
            return true;
        }
        finally
        {
            active.Remove(picture);
        }
    }

    private static bool IsNestedPictureCommand(in RenderCommand command) =>
        command.Type is RenderCommandType.DrawPicture or
            RenderCommandType.DrawStaticDxf ||
        command.Type == RenderCommandType.DrawExtension &&
        command.ExtensionId == CompositorBuiltInExtensions.StaticDxf;

    private static bool TryGetNestedPicture(
        in RenderCommand command,
        out GpuPicture? picture,
        out float dpiScaleMultiplier,
        List<DxfStaticBuffer.RenderLease> staticBufferLeases,
        out NativePictureCompileError error)
    {
        picture = null;
        dpiScaleMultiplier = 1f;
        error = NativePictureCompileError.None;
        if (command.Type == RenderCommandType.DrawPicture)
        {
            picture = command.Picture;
            if (picture is null)
            {
                error = NativePictureCompileError.InvalidArgument;
                return false;
            }
            return true;
        }

        object? payload = command.Type == RenderCommandType.DrawStaticDxf
            ? command.StaticBuffer
            : command.DataParam;
        if (payload is not DxfStaticBuffer staticBuffer)
        {
            error = NativePictureCompileError.InvalidArgument;
            return false;
        }

        DxfStaticBuffer.RenderLease renderLease =
            staticBuffer.AcquireRenderLease();
        if (!renderLease.IsAcquired)
        {
            error = NativePictureCompileError.InvalidArgument;
            return false;
        }
        staticBufferLeases.Add(renderLease);
        picture = staticBuffer.NativeSourcePicture;
        if (picture is null)
        {
            error = NativePictureCompileError.InvalidArgument;
            return false;
        }
        dpiScaleMultiplier = staticBuffer.StaticZoom;
        return true;
    }

    private static Matrix4x4 ToMatrix4x4(Matrix3x2 value) => new(
        value.M11,
        value.M12,
        0f,
        0f,
        value.M21,
        value.M22,
        0f,
        0f,
        0f,
        0f,
        1f,
        0f,
        value.M31,
        value.M32,
        0f,
        1f);
}
