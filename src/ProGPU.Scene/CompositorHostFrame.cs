using System;
using System.Numerics;

namespace ProGPU.Scene;

/// <summary>
/// Describes one framework host frame after logical size, DPI, render target size, and viewport
/// have been normalized for the ProGPU compositor.
/// </summary>
public readonly record struct CompositorHostFrame(
    float LogicalWidth,
    float LogicalHeight,
    uint RenderTargetWidth,
    uint RenderTargetHeight,
    RenderTargetViewport RenderTargetViewport,
    float DpiScaleX,
    float DpiScaleY)
{
    public bool IsValid =>
        float.IsFinite(LogicalWidth) &&
        float.IsFinite(LogicalHeight) &&
        LogicalWidth > 0f &&
        LogicalHeight > 0f &&
        RenderTargetWidth > 0 &&
        RenderTargetHeight > 0 &&
        RenderTargetViewport.IsValid &&
        float.IsFinite(DpiScaleX) &&
        float.IsFinite(DpiScaleY) &&
        DpiScaleX > 0f &&
        DpiScaleY > 0f;

    public float DpiScale => DpiScaleX == DpiScaleY
        ? DpiScaleX
        : MathF.Max(DpiScaleX, DpiScaleY);

    public uint LogicalPixelWidth => RoundPositiveToUInt(LogicalWidth);

    public uint LogicalPixelHeight => RoundPositiveToUInt(LogicalHeight);

    public Vector2 LogicalSize => new(LogicalWidth, LogicalHeight);

    public static CompositorHostFrame FromLogicalSize(double logicalWidth, double logicalHeight, double dpiScale)
    {
        return FromLogicalSize(
            (float)logicalWidth,
            (float)logicalHeight,
            (float)dpiScale,
            (float)dpiScale);
    }

    public static CompositorHostFrame FromLogicalSize(float logicalWidth, float logicalHeight, float dpiScale)
    {
        return FromLogicalSize(logicalWidth, logicalHeight, dpiScale, dpiScale);
    }

    public static CompositorHostFrame FromLogicalSize(
        float logicalWidth,
        float logicalHeight,
        float dpiScaleX,
        float dpiScaleY,
        RenderTargetViewport? renderTargetViewport = null)
    {
        logicalWidth = NormalizePositive(logicalWidth, 1f);
        logicalHeight = NormalizePositive(logicalHeight, 1f);
        dpiScaleX = NormalizePositive(dpiScaleX, 1f);
        dpiScaleY = NormalizePositive(dpiScaleY, 1f);

        uint renderTargetWidth = RoundPositiveToUInt(logicalWidth * dpiScaleX);
        uint renderTargetHeight = RoundPositiveToUInt(logicalHeight * dpiScaleY);
        var viewport = (renderTargetViewport ?? RenderTargetViewport.Full(renderTargetWidth, renderTargetHeight))
            .Clamp(renderTargetWidth, renderTargetHeight);

        return new CompositorHostFrame(
            logicalWidth,
            logicalHeight,
            renderTargetWidth,
            renderTargetHeight,
            viewport,
            dpiScaleX,
            dpiScaleY);
    }

    public static CompositorHostFrame FromRenderTarget(
        uint renderTargetWidth,
        uint renderTargetHeight,
        float dpiScale,
        RenderTargetViewport? renderTargetViewport = null)
    {
        return FromRenderTarget(
            renderTargetWidth,
            renderTargetHeight,
            dpiScale,
            dpiScale,
            renderTargetViewport);
    }

    public static CompositorHostFrame FromRenderTarget(
        uint renderTargetWidth,
        uint renderTargetHeight,
        float dpiScaleX,
        float dpiScaleY,
        RenderTargetViewport? renderTargetViewport = null)
    {
        renderTargetWidth = Math.Max(1u, renderTargetWidth);
        renderTargetHeight = Math.Max(1u, renderTargetHeight);
        dpiScaleX = NormalizePositive(dpiScaleX, 1f);
        dpiScaleY = NormalizePositive(dpiScaleY, 1f);

        float logicalWidth = renderTargetWidth / dpiScaleX;
        float logicalHeight = renderTargetHeight / dpiScaleY;
        var viewport = (renderTargetViewport ?? RenderTargetViewport.Full(renderTargetWidth, renderTargetHeight))
            .Clamp(renderTargetWidth, renderTargetHeight);

        return new CompositorHostFrame(
            logicalWidth,
            logicalHeight,
            renderTargetWidth,
            renderTargetHeight,
            viewport,
            dpiScaleX,
            dpiScaleY);
    }

    private static float NormalizePositive(float value, float fallback)
    {
        return float.IsFinite(value) && value > 0f ? value : fallback;
    }

    private static uint RoundPositiveToUInt(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            return 1u;
        }

        float rounded = MathF.Ceiling(value);
        if (rounded >= uint.MaxValue)
        {
            return uint.MaxValue;
        }

        return Math.Max(1u, (uint)rounded);
    }
}
