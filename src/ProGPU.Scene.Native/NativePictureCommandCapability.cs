namespace ProGPU.Scene.Native;

/// <summary>
/// Describes the native scene compiler's structural lowering route for a
/// managed retained command. Individual payloads remain subject to typed
/// validation and can still fail closed with a detailed compile error.
/// </summary>
public enum NativePictureCommandCapability
{
    Unknown = 0,
    DirectDraw,
    StateScope,
    NestedPicture,
    BuiltInExtension,
    ExplicitlyUnsupported
}

public static partial class GpuPictureNativeSceneCompiler
{
    /// <summary>
    /// Returns the documented structural native-lowering route for
    /// <paramref name="commandType"/> without inspecting a command payload.
    /// </summary>
    public static NativePictureCommandCapability GetCommandCapability(
        RenderCommandType commandType) => commandType switch
    {
        RenderCommandType.DrawRect or
        RenderCommandType.DrawPath or
        RenderCommandType.DrawText or
        RenderCommandType.DrawTexture or
        RenderCommandType.DrawLine or
        RenderCommandType.DrawEllipse or
        RenderCommandType.DrawCircle or
        RenderCommandType.DrawRoundedRect or
        RenderCommandType.DrawBezier or
        RenderCommandType.DrawCubicBezier or
        RenderCommandType.DrawPolyline or
        RenderCommandType.DrawSpline or
        RenderCommandType.FillTriangle or
        RenderCommandType.FillQuad or
        RenderCommandType.DrawLine3D or
        RenderCommandType.DrawAcisSolid or
        RenderCommandType.DrawGpuLineSeries or
        RenderCommandType.DrawGpuScatterSeries or
        RenderCommandType.DrawGlyphRun or
        RenderCommandType.DrawVertexMesh or
        RenderCommandType.DrawPointBatch or
        RenderCommandType.DrawDotGrid =>
            NativePictureCommandCapability.DirectDraw,

        RenderCommandType.DrawHatch =>
            NativePictureCommandCapability.BuiltInExtension,

        RenderCommandType.PushClip or
        RenderCommandType.PopClip or
        RenderCommandType.PushOpacity or
        RenderCommandType.PopOpacity or
        RenderCommandType.PushGeometryClip or
        RenderCommandType.PopGeometryClip or
        RenderCommandType.PushOpacityMask or
        RenderCommandType.PopOpacityMask or
        RenderCommandType.PushBlendMode or
        RenderCommandType.PopBlendMode =>
            NativePictureCommandCapability.StateScope,

        RenderCommandType.DrawPicture =>
            NativePictureCommandCapability.NestedPicture,
        RenderCommandType.DrawExtension =>
            NativePictureCommandCapability.BuiltInExtension,

        // These commands retain live managed/GPU objects or use a shader
        // family that has no pointer-free semantic resource yet. They are
        // rejected transactionally; no command is silently dropped.
        RenderCommandType.DrawStaticDxf or
        RenderCommandType.DrawVisual =>
            NativePictureCommandCapability.ExplicitlyUnsupported,

        _ => NativePictureCommandCapability.Unknown
    };
}
