using System;
using System.Numerics;
using ProGPU.Vector;

namespace SkiaSharp;

public enum SKPaintStyle
{
    Fill = 0,
    Stroke = 1,
    StrokeAndFill = 2,
}

public class SKPaint : IDisposable
{
    public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;
    public SKColor Color { get; set; } = SKColors.Black;
    public float StrokeWidth { get; set; } = 1f;
    public float StrokeMiter { get; set; } = 4f;
    public SKStrokeCap StrokeCap { get; set; } = SKStrokeCap.Butt;
    public SKStrokeJoin StrokeJoin { get; set; } = SKStrokeJoin.Miter;
    public SKShader? Shader { get; set; }
    public SKColorFilter? ColorFilter { get; set; }
    public SKImageFilter? ImageFilter { get; set; }
    public SKPathEffect? PathEffect { get; set; }
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;
    public bool IsAntialias { get; set; } = true;
    public SKTypeface? Typeface { get; set; }
    public float TextSize { get; set; } = 12f;

    public SKPaint Clone()
    {
        return new SKPaint
        {
            Style = Style,
            Color = Color,
            StrokeWidth = StrokeWidth,
            StrokeMiter = StrokeMiter,
            StrokeCap = StrokeCap,
            StrokeJoin = StrokeJoin,
            Shader = Shader,
            ColorFilter = ColorFilter,
            ImageFilter = ImageFilter,
            PathEffect = PathEffect,
            BlendMode = BlendMode,
            IsAntialias = IsAntialias,
            Typeface = Typeface,
            TextSize = TextSize
        };
    }

    public Brush? ToBrush()
    {
        if (Style == SKPaintStyle.Stroke) return null;

        if (Shader != null)
        {
            return Shader.ToBrush();
        }
        
        var c = new Vector4(Color.R / 255.0f, Color.G / 255.0f, Color.B / 255.0f, Color.A / 255.0f);
        return new SolidColorBrush(c);
    }

    public Pen? ToPen()
    {
        if (Style == SKPaintStyle.Fill) return null;

        Brush penBrush;
        if (Shader != null)
        {
            penBrush = Shader.ToBrush();
        }
        else
        {
            var c = new Vector4(Color.R / 255.0f, Color.G / 255.0f, Color.B / 255.0f, Color.A / 255.0f);
            penBrush = new SolidColorBrush(c);
        }
        var (dashArray, dashOffset) = MapDashEffect(PathEffect, StrokeWidth);

        return new Pen(
            penBrush,
            StrokeWidth,
            MapStrokeJoin(StrokeJoin),
            StrokeMiter,
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            dashArray,
            dashOffset);
    }

    public void Dispose() { }

    private static PenLineCap MapStrokeCap(SKStrokeCap cap)
    {
        return cap switch
        {
            SKStrokeCap.Round => PenLineCap.Round,
            SKStrokeCap.Square => PenLineCap.Square,
            _ => PenLineCap.Flat
        };
    }

    private static PenLineJoin MapStrokeJoin(SKStrokeJoin join)
    {
        return join switch
        {
            SKStrokeJoin.Round => PenLineJoin.Round,
            SKStrokeJoin.Bevel => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter
        };
    }

    private static (double[]? DashArray, double DashOffset) MapDashEffect(SKPathEffect? pathEffect, float strokeWidth)
    {
        if (pathEffect == null)
        {
            return (null, 0.0);
        }

        if (!float.IsFinite(strokeWidth) || strokeWidth <= 0f)
        {
            throw new NotSupportedException("Dash path effects require a positive finite stroke width.");
        }

        if (pathEffect.Intervals.Length == 0 || (pathEffect.Intervals.Length % 2) != 0)
        {
            throw new NotSupportedException("Dash path effects require an even number of intervals.");
        }

        var dashArray = new double[pathEffect.Intervals.Length];
        for (var i = 0; i < pathEffect.Intervals.Length; i++)
        {
            var interval = pathEffect.Intervals[i];
            if (!float.IsFinite(interval) || interval < 0f)
            {
                throw new NotSupportedException("Dash path effect intervals must be finite and non-negative.");
            }

            dashArray[i] = interval / strokeWidth;
        }

        if (!float.IsFinite(pathEffect.Phase))
        {
            throw new NotSupportedException("Dash path effect phase must be finite.");
        }

        return (dashArray, pathEffect.Phase / strokeWidth);
    }
}

public class SKShader : IDisposable
{
    private readonly Func<Brush> _brushCreator;

    private SKShader(Func<Brush> brushCreator)
    {
        _brushCreator = brushCreator;
    }

    public Brush ToBrush() => _brushCreator();

    public static SKShader CreateColor(SKColor color)
    {
        return new SKShader(() =>
        {
            var c = new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
            return new SolidColorBrush(c);
        });
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() =>
        {
            var stops = new GradientStop[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                var c = new Vector4(colors[i].R / 255.0f, colors[i].G / 255.0f, colors[i].B / 255.0f, colors[i].A / 255.0f);
                float offset = colorPos != null ? colorPos[i] : (float)i / (colors.Length - 1);
                stops[i] = new GradientStop(c, offset);
            }
            return new LinearGradientBrush(new Vector2(start.X, start.Y), new Vector2(end.X, end.Y), stops)
            {
                SpreadMethod = spreadMethod
            };
        });
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() =>
        {
            var stops = new GradientStop[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                var c = new Vector4(colors[i].R / 255.0f, colors[i].G / 255.0f, colors[i].B / 255.0f, colors[i].A / 255.0f);
                float offset = colorPos != null ? colorPos[i] : (float)i / (colors.Length - 1);
                stops[i] = new GradientStop(c, offset);
            }
            return new RadialGradientBrush(new Vector2(center.X, center.Y), radius, stops)
            {
                SpreadMethod = spreadMethod
            };
        });
    }

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        // Conical falls back to radial gradient brush using start center and radius for simplicity
        return CreateRadialGradient(start, startRadius, colors, colorPos, mode);
    }

    public void Dispose() { }

    private static GradientSpreadMethod MapTileMode(SKShaderTileMode mode)
    {
        return mode switch
        {
            SKShaderTileMode.Clamp => GradientSpreadMethod.Pad,
            SKShaderTileMode.Repeat => GradientSpreadMethod.Repeat,
            SKShaderTileMode.Mirror => GradientSpreadMethod.Reflect,
            SKShaderTileMode.Decal => throw new NotSupportedException("SKShaderTileMode.Decal is not supported by ProGPU gradient brushes."),
            _ => GradientSpreadMethod.Pad
        };
    }
}

public class SKColorFilter : IDisposable
{
    public SKColor Color { get; }
    public SKBlendMode Mode { get; }

    private SKColorFilter(SKColor color, SKBlendMode mode)
    {
        Color = color;
        Mode = mode;
    }

    public static SKColorFilter CreateBlendMode(SKColor color, SKBlendMode mode)
    {
        return new SKColorFilter(color, mode);
    }

    public void Dispose() { }
}

public class SKImageFilter : IDisposable
{
    public bool IsBlur { get; }
    public float SigmaX { get; }
    public float SigmaY { get; }
    
    public bool IsDropShadow { get; }
    public float Dx { get; }
    public float Dy { get; }
    public SKColor ShadowColor { get; }

    private SKImageFilter(float sigmaX, float sigmaY)
    {
        IsBlur = true;
        SigmaX = sigmaX;
        SigmaY = sigmaY;
    }

    private SKImageFilter(float dx, float dy, float sigmaX, float sigmaY, SKColor color)
    {
        IsDropShadow = true;
        Dx = dx;
        Dy = dy;
        SigmaX = sigmaX;
        SigmaY = sigmaY;
        ShadowColor = color;
    }

    public static SKImageFilter CreateBlur(float sigmaX, float sigmaY, SKImageFilter? input = null)
    {
        return new SKImageFilter(sigmaX, sigmaY);
    }

    public static SKImageFilter CreateDropShadow(float dx, float dy, float sigmaX, float sigmaY, SKColor color, SKImageFilter? input = null)
    {
        return new SKImageFilter(dx, dy, sigmaX, sigmaY, color);
    }

    public void Dispose() { }
}

public class SKPathEffect : IDisposable
{
    public float[] Intervals { get; }
    public float Phase { get; }

    private SKPathEffect(float[] intervals, float phase)
    {
        Intervals = (float[])intervals.Clone();
        Phase = phase;
    }

    public static SKPathEffect CreateDash(float[] intervals, float phase)
    {
        return new SKPathEffect(intervals, phase);
    }

    public void Dispose() { }
}

public class SKMaskFilter : IDisposable
{
    public float Sigma { get; }

    private SKMaskFilter(float sigma)
    {
        Sigma = sigma;
    }

    public static SKMaskFilter CreateBlur(SKBlurStyle style, float sigma)
    {
        return new SKMaskFilter(sigma);
    }

    public void Dispose() { }
}

public enum SKBlurStyle
{
    Normal = 0,
    Solid = 1,
    Outer = 2,
    Inner = 3,
}
