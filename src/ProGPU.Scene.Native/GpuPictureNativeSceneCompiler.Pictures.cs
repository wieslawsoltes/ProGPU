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
        bool IsBoundary = false);

    private static bool ContainsNestedPicture(GpuPicture picture)
    {
        for (int index = 0; index < picture.CommandCount; index++)
        {
            if (picture.GetCommand(index).Type == RenderCommandType.DrawPicture)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryFlattenPicture(
        GpuPicture picture,
        List<FlattenedCommand> commands,
        out int sourceCommandCount,
        out NativePictureCompileFailure failure)
    {
        var active = new HashSet<GpuPicture>(ReferenceEqualityComparer.Instance);
        int nextOwnerId = 0;
        sourceCommandCount = 0;
        return TryFlattenPicture(
            picture,
            Matrix3x2.Identity,
            Matrix4x4.Identity,
            commands,
            active,
            ref nextOwnerId,
            ref sourceCommandCount,
            0,
            -1,
            default,
            out failure);
    }

    private static bool TryFlattenPicture(
        GpuPicture picture,
        Matrix3x2 parentTransform,
        Matrix4x4 parentCameraView,
        List<FlattenedCommand> commands,
        HashSet<GpuPicture> active,
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
                bool isPicture = command.Type == RenderCommandType.DrawPicture;
                if ((!isPicture && command.UseGpuTransforms) ||
                    !TryGetAffine(command.Transform, out Matrix3x2 localTransform))
                {
                    failure = new(
                        NativePictureCompileError.UnsupportedTransform,
                        sourceIndex,
                        sourceType);
                    return false;
                }

                Matrix3x2 transform = localTransform * parentTransform;
                if (isPicture)
                {
                    if (command.Picture is null)
                    {
                        failure = new(
                            NativePictureCompileError.InvalidArgument,
                            sourceIndex,
                            sourceType);
                        return false;
                    }
                    Matrix4x4 cameraView = command.UseGpuTransforms &&
                        command.CameraView != default
                        ? command.CameraView * parentCameraView
                        : parentCameraView;
                    if (!TryFlattenPicture(
                            command.Picture,
                            transform,
                            cameraView,
                            commands,
                            active,
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
                    failure = new(
                        NativePictureCompileError.UnsupportedTransform,
                        sourceIndex,
                        sourceType);
                    return false;
                }

                commands.Add(new(
                    picture,
                    index,
                    transform,
                    ownerId,
                    sourceIndex,
                    sourceType,
                    parentCameraView));
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
                    IsBoundary: true));
            }
            return true;
        }
        finally
        {
            active.Remove(picture);
        }
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
