using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;
using ProGPU.Backend;
using ProGPU.Scene;

namespace Avalonia.ProGpu;

/// <summary>
/// Requests a ProGPU blend mode through Avalonia's effect stack.
/// </summary>
public sealed class BlendEffect : IEffect
{
    public BlendEffect(GpuBlendMode blendMode)
    {
        BlendMode = blendMode;
    }

    public GpuBlendMode BlendMode { get; }
}

partial class DrawingContextImpl
{
    [Flags]
    private enum AvaloniaEffectOperations : byte
    {
        None = 0,
        Clip = 1,
        Blend = 2,
        Subtree = 4
    }

    private readonly struct AvaloniaEffectFrame
    {
        internal AvaloniaEffectFrame(
            ProGPU.Scene.DrawingContext parent,
            AvaloniaEffectOperations operations,
            EffectDrawingVisual? subtree,
            Avalonia.Rect? outputClip)
        {
            Parent = parent;
            Operations = operations;
            Subtree = subtree;
            OutputClip = outputClip;
        }

        internal ProGPU.Scene.DrawingContext Parent { get; }
        internal AvaloniaEffectOperations Operations { get; }
        internal EffectDrawingVisual? Subtree { get; }
        internal Avalonia.Rect? OutputClip { get; }
    }

    private sealed class EffectDrawingVisual : DrawingVisual, IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            Context.Clear();
            Effect = null;
            _disposed = true;
        }
    }

    // Most drawing contexts never encounter an effect. Keep the stack lazy so
    // ordinary text, geometry, and bitmap recording allocate no effect state.
    private Stack<AvaloniaEffectFrame>? _avaloniaEffectFrames;

    public void PushEffect(Avalonia.Rect? clipRect, IEffect effect)
    {
        CheckLease();
        ArgumentNullException.ThrowIfNull(effect);

        if (effect is BlendEffect blend)
        {
            DrawingContext.PushBlendMode(blend.BlendMode);
            bool clipped = PushAvaloniaEffectClip(clipRect);
            PushAvaloniaEffectFrame(
                new AvaloniaEffectFrame(
                    DrawingContext,
                    AvaloniaEffectOperations.Blend |
                    (clipped
                        ? AvaloniaEffectOperations.Clip
                        : AvaloniaEffectOperations.None),
                    subtree: null,
                    outputClip: null));
            return;
        }

        if (TryCreateEffectSubtree(
                clipRect,
                effect,
                out EffectDrawingVisual? subtree))
        {
            EffectDrawingVisual retained = subtree ??
                throw new InvalidOperationException(
                    "A supported effect did not create a retained subtree.");
            ProGPU.Scene.DrawingContext parent = DrawingContext;
            DrawingContext = retained.Context;
            if (clipRect is { } subtreeClip)
            {
                PushSkiaDeviceClipState(
                    ToProGpuRect(subtreeClip),
                    isDeviceRect: true);
            }
            PushAvaloniaEffectFrame(
                new AvaloniaEffectFrame(
                    parent,
                    AvaloniaEffectOperations.Subtree,
                    retained,
                    clipRect));
            return;
        }

        bool hasClip = PushAvaloniaEffectClip(clipRect);
        PushAvaloniaEffectFrame(
            new AvaloniaEffectFrame(
                DrawingContext,
                hasClip
                    ? AvaloniaEffectOperations.Clip
                    : AvaloniaEffectOperations.None,
                subtree: null,
                outputClip: null));
    }

    public void PopEffect()
    {
        CheckLease();
        if (_avaloniaEffectFrames is not { Count: > 0 } frames)
            return;

        AvaloniaEffectFrame frame = frames.Pop();
        if ((frame.Operations & AvaloniaEffectOperations.Subtree) != 0)
        {
            CompleteEffectSubtree(frame);
            return;
        }

        if ((frame.Operations & AvaloniaEffectOperations.Clip) != 0)
        {
            DrawingContext.PopClip();
            PopSkiaClipState();
        }
        if ((frame.Operations & AvaloniaEffectOperations.Blend) != 0)
            DrawingContext.PopBlendMode();
    }

    private void PushAvaloniaEffectFrame(AvaloniaEffectFrame frame)
    {
        (_avaloniaEffectFrames ??= new Stack<AvaloniaEffectFrame>())
            .Push(frame);
    }

    private bool PushAvaloniaEffectClip(Avalonia.Rect? clipRect)
    {
        if (clipRect is not { } clip)
            return false;
        ProGPU.Scene.Rect deviceClip = ToProGpuRect(clip);
        DrawingContext.PushClip(deviceClip);
        PushSkiaDeviceClipState(deviceClip, isDeviceRect: true);
        return true;
    }

    private void CompleteEffectSubtree(AvaloniaEffectFrame frame)
    {
        EffectDrawingVisual subtree = frame.Subtree ??
            throw new InvalidOperationException(
                "An effect subtree frame has no retained visual.");
        DrawingContext = frame.Parent;
        DrawingContext.RetainResource(subtree);
        if (frame.OutputClip is { } clip)
            DrawingContext.PushClip(ToProGpuRect(clip));
        DrawingContext.DrawVisual(subtree);
        if (frame.OutputClip.HasValue)
        {
            DrawingContext.PopClip();
            PopSkiaClipState();
        }
    }

    private bool TryCreateEffectSubtree(
        Avalonia.Rect? clipRect,
        IEffect effect,
        out EffectDrawingVisual? subtree)
    {
        Avalonia.Rect outputBounds = clipRect ??
            new Avalonia.Rect(0, 0, _size.Width, _size.Height);
        if (outputBounds.Width <= 0 || outputBounds.Height <= 0)
        {
            subtree = null;
            return false;
        }

        if (effect is IBlurEffect blur)
        {
            float radius = NormalizeBlurRadius(blur.Radius);
            float padding = ComputeAvaloniaEffectPadding(radius);
            subtree = CreateEffectSubtree(
                DeflateEffectBounds(
                    outputBounds,
                    padding,
                    padding,
                    padding,
                    padding),
                padding,
                new ProGPU.Scene.BlurEffect(
                    ConvertAvaloniaBlurRadiusToSigma(radius)));
            return true;
        }

        if (effect is IDropShadowEffect shadow)
        {
            float radius = NormalizeBlurRadius(shadow.BlurRadius);
            float padding = ComputeAvaloniaEffectPadding(radius);
            float offsetX = FiniteSingle(shadow.OffsetX);
            float offsetY = FiniteSingle(shadow.OffsetY);
            Color color = shadow.Color;
            float opacity = Math.Clamp(
                FiniteSingle(shadow.Opacity),
                0f,
                1f);
            subtree = CreateEffectSubtree(
                DeflateEffectBounds(
                    outputBounds,
                    MathF.Max(0f, padding - offsetX),
                    MathF.Max(0f, padding - offsetY),
                    MathF.Max(0f, padding + offsetX),
                    MathF.Max(0f, padding + offsetY)),
                padding,
                new ProGPU.Scene.DropShadowEffect(
                    ConvertAvaloniaBlurRadiusToSigma(radius),
                    new Vector2(offsetX, offsetY),
                    new Vector4(
                        color.R / 255f,
                        color.G / 255f,
                        color.B / 255f,
                        color.A / 255f * opacity)));
            return true;
        }

        subtree = null;
        return false;
    }

    private EffectDrawingVisual CreateEffectSubtree(
        Avalonia.Rect contentBounds,
        float padding,
        EffectBase effect)
    {
        return new EffectDrawingVisual
        {
            // Commands retain Avalonia's target coordinate system. The
            // content bounds constrain effect rasterization independently.
            Size = new Vector2(_size.Width, _size.Height),
            Effect = effect,
            EffectContentBounds = ToProGpuRect(contentBounds),
            EffectRasterPadding = padding
        };
    }

    private static Avalonia.Rect DeflateEffectBounds(
        Avalonia.Rect bounds,
        float left,
        float top,
        float right,
        float bottom)
    {
        double width = bounds.Width - left - right;
        double height = bounds.Height - top - bottom;
        if (width <= 0 || height <= 0)
            return bounds;
        return new Avalonia.Rect(
            bounds.X + left,
            bounds.Y + top,
            width,
            height);
    }

    internal static float ConvertAvaloniaBlurRadiusToSigma(
        float radius) =>
        radius <= 0f
            ? 0f
            : radius / MathF.Sqrt(12f) + 0.5f;

    internal static float ComputeAvaloniaEffectPadding(float radius) =>
        radius <= 0f
            ? 0f
            : MathF.Ceiling(radius) + 1f;

    private float NormalizeBlurRadius(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0f;
        double targetBound =
            Math.Max(1d, Math.Max(_size.Width, _size.Height));
        return (float)Math.Min(value, targetBound * 2d);
    }

    private static float FiniteSingle(double value)
    {
        if (!double.IsFinite(value))
            return 0f;
        return (float)Math.Clamp(
            value,
            -float.MaxValue,
            float.MaxValue);
    }

    private void DiscardUnbalancedEffectScopes()
    {
        if (_avaloniaEffectFrames is not { Count: > 0 } frames)
            return;

        while (frames.TryPop(out AvaloniaEffectFrame frame))
        {
            if ((frame.Operations &
                 AvaloniaEffectOperations.Subtree) != 0)
            {
                DrawingContext = frame.Parent;
                frame.Subtree?.Dispose();
                if (frame.OutputClip.HasValue)
                    PopSkiaClipState();
                continue;
            }

            if ((frame.Operations &
                 AvaloniaEffectOperations.Clip) != 0)
            {
                DrawingContext.PopClip();
                PopSkiaClipState();
            }
            if ((frame.Operations &
                 AvaloniaEffectOperations.Blend) != 0)
            {
                DrawingContext.PopBlendMode();
            }
        }
    }
}
