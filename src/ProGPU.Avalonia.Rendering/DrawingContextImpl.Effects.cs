using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;
using ProGPU.Backend;
using ProGPU.Scene;

namespace Avalonia.ProGpu;

public class BlendEffect : IEffect
{
    public GpuBlendMode BlendMode { get; }

    public BlendEffect(GpuBlendMode blendMode)
    {
        BlendMode = blendMode;
    }
}

partial class DrawingContextImpl
{
    private enum EffectScopeKind
    {
        Passthrough,
        Blend,
        Retained
    }

    private readonly struct EffectScope
    {
        internal EffectScope(
            EffectScopeKind kind,
            ProGPU.Scene.DrawingContext owner,
            RetainedEffectVisual? visual,
            bool hasClip)
        {
            Kind = kind;
            Owner = owner;
            Visual = visual;
            HasClip = hasClip;
        }

        internal EffectScopeKind Kind { get; }
        internal ProGPU.Scene.DrawingContext Owner { get; }
        internal RetainedEffectVisual? Visual { get; }
        internal bool HasClip { get; }
    }

    private sealed class RetainedEffectVisual : DrawingVisual, IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Context.Clear();
            Effect = null;
        }
    }

    private readonly Stack<EffectScope> _effectScopes = new();

    public void PushEffect(Avalonia.Rect? effectClipRect, IEffect effect)
    {
        CheckLease();
        ArgumentNullException.ThrowIfNull(effect);

        if (effect is BlendEffect blendEffect)
        {
            DrawingContext.PushBlendMode(blendEffect.BlendMode);
            bool hasBlendClip = PushEffectClip(effectClipRect);
            _effectScopes.Push(
                new EffectScope(
                    EffectScopeKind.Blend,
                    DrawingContext,
                    visual: null,
                    hasBlendClip));
            return;
        }

        if (TryCreateRetainedEffectVisual(
                effectClipRect,
                effect,
                out RetainedEffectVisual? visual))
        {
            ProGPU.Scene.DrawingContext owner = DrawingContext;
            DrawingContext = visual!.Context;
            _effectScopes.Push(
                new EffectScope(
                    EffectScopeKind.Retained,
                    owner,
                    visual,
                    hasClip: false));
            return;
        }

        bool hasClip = PushEffectClip(effectClipRect);
        _effectScopes.Push(
            new EffectScope(
                EffectScopeKind.Passthrough,
                DrawingContext,
                visual: null,
                hasClip));
    }

    public void PopEffect()
    {
        CheckLease();
        if (_effectScopes.Count == 0)
            return;

        EffectScope scope = _effectScopes.Pop();
        switch (scope.Kind)
        {
            case EffectScopeKind.Blend:
                if (scope.HasClip)
                    DrawingContext.PopClip();
                DrawingContext.PopBlendMode();
                break;

            case EffectScopeKind.Retained:
                RetainedEffectVisual visual = scope.Visual!;
                DrawingContext = scope.Owner;
                DrawingContext.RetainResource(visual);
                DrawingContext.DrawVisual(visual);
                break;

            default:
                if (scope.HasClip)
                    DrawingContext.PopClip();
                break;
        }
    }

    private bool PushEffectClip(Avalonia.Rect? effectClipRect)
    {
        if (!effectClipRect.HasValue)
            return false;

        DrawingContext.PushClip(ToProGpuRect(effectClipRect.Value));
        return true;
    }

    private bool TryCreateRetainedEffectVisual(
        Avalonia.Rect? effectClipRect,
        IEffect effect,
        out RetainedEffectVisual? visual)
    {
        Avalonia.Rect outputBounds = effectClipRect ??
            new Avalonia.Rect(0, 0, _size.Width, _size.Height);
        if (outputBounds.Width <= 0 || outputBounds.Height <= 0)
        {
            visual = null;
            return false;
        }

        if (effect is IBlurEffect blur)
        {
            float radius = NormalizeEffectRadius(blur.Radius);
            float padding = GetEffectPadding(radius);
            visual = CreateRetainedEffectVisual(
                DeflateOutputBounds(
                    outputBounds,
                    padding,
                    padding,
                    padding,
                    padding),
                padding,
                new ProGPU.Scene.BlurEffect(
                    EffectRadiusToSigma(radius)));
            return true;
        }

        if (effect is IDropShadowEffect shadow)
        {
            float radius = NormalizeEffectRadius(shadow.BlurRadius);
            float padding = GetEffectPadding(radius);
            float offsetX = NormalizeEffectValue(shadow.OffsetX);
            float offsetY = NormalizeEffectValue(shadow.OffsetY);
            Color color = shadow.Color;
            float alpha = Math.Clamp(
                color.A / 255f * NormalizeEffectOpacity(shadow.Opacity),
                0f,
                1f);
            visual = CreateRetainedEffectVisual(
                DeflateOutputBounds(
                    outputBounds,
                    MathF.Max(0f, padding - offsetX),
                    MathF.Max(0f, padding - offsetY),
                    MathF.Max(0f, padding + offsetX),
                    MathF.Max(0f, padding + offsetY)),
                padding,
                new ProGPU.Scene.DropShadowEffect(
                    EffectRadiusToSigma(radius),
                    new Vector2(offsetX, offsetY),
                    new Vector4(
                        color.R / 255f,
                        color.G / 255f,
                        color.B / 255f,
                        alpha)));
            return true;
        }

        visual = null;
        return false;
    }

    private RetainedEffectVisual CreateRetainedEffectVisual(
        Avalonia.Rect contentBounds,
        float padding,
        EffectBase effect)
    {
        ProGPU.Scene.Rect transformedBounds =
            ToProGpuRect(contentBounds);
        return new RetainedEffectVisual
        {
            Size = transformedBounds.Size,
            Effect = effect,
            EffectContentBounds = transformedBounds,
            EffectRasterPadding = padding
        };
    }

    private static Avalonia.Rect DeflateOutputBounds(
        Avalonia.Rect outputBounds,
        float left,
        float top,
        float right,
        float bottom)
    {
        double width = Math.Max(
            0d,
            outputBounds.Width - left - right);
        double height = Math.Max(
            0d,
            outputBounds.Height - top - bottom);
        if (width <= 0d || height <= 0d)
            return outputBounds;

        return new Avalonia.Rect(
            outputBounds.X + left,
            outputBounds.Y + top,
            width,
            height);
    }

    internal static float EffectRadiusToSigma(float radius) =>
        radius > 0f
            ? radius * 0.2886751345948129f + 0.5f
            : 0f;

    internal static float GetEffectPadding(float radius) =>
        radius > 0f
            ? MathF.Ceiling(radius) + 1f
            : 0f;

    private static float NormalizeEffectRadius(double value) =>
        double.IsFinite(value) && value > 0d
            ? (float)value
            : 0f;

    private static float NormalizeEffectValue(double value) =>
        double.IsFinite(value)
            ? (float)value
            : 0f;

    private static float NormalizeEffectOpacity(double value) =>
        double.IsFinite(value)
            ? (float)value
            : 0f;

    private void DiscardUnbalancedEffectScopes()
    {
        while (_effectScopes.Count > 0)
        {
            EffectScope scope = _effectScopes.Pop();
            switch (scope.Kind)
            {
                case EffectScopeKind.Retained:
                    DrawingContext = scope.Owner;
                    scope.Visual?.Dispose();
                    break;

                case EffectScopeKind.Blend:
                    if (scope.HasClip)
                        DrawingContext.PopClip();
                    DrawingContext.PopBlendMode();
                    break;

                default:
                    if (scope.HasClip)
                        DrawingContext.PopClip();
                    break;
            }
        }
    }
}
