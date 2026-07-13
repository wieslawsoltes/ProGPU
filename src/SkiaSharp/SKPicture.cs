using System;
using System.Runtime.CompilerServices;
using System.Threading;
using ProGPU.Scene;
using ProGPU.Vector;

namespace SkiaSharp;

public sealed class SKPicture : IDisposable
{
    private static int s_nextUniqueId;

    public IntPtr Handle { get; } = SKObjectHandle.Create();
    private GpuPicture? _picture;
    private readonly uint _uniqueId = unchecked((uint)Interlocked.Increment(ref s_nextUniqueId));

    internal SKPicture(GpuPicture picture, SKRect cullRect)
    {
        _picture = picture;
        CullRect = cullRect;
    }

    public SKRect CullRect { get; }

    public uint UniqueId => _uniqueId;

    public int ApproximateBytesUsed
    {
        get
        {
            var picture = Picture;
            var bytes =
                (long)picture.Commands.Length * Unsafe.SizeOf<RenderCommand>() +
                (long)picture.PointBuffer.Length * Unsafe.SizeOf<System.Numerics.Vector2>() +
                (long)picture.DoubleBuffer.Length * sizeof(double) +
                (long)picture.Line3DBuffer.Length * Unsafe.SizeOf<Line3D>() +
                (long)picture.FloatBuffer.Length * sizeof(float);
            return (int)Math.Min(int.MaxValue, bytes);
        }
    }

    public int ApproximateOperationCount => GetApproximateOperationCount(includeNested: false);

    internal GpuPicture Picture => _picture ?? throw new ObjectDisposedException(nameof(SKPicture));

    public void Playback(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.DrawPicture(this);
    }

    public int GetApproximateOperationCount(bool includeNested) =>
        CountOperations(Picture, includeNested);

    public SKShader ToShader() =>
        ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKFilterMode.Nearest, SKMatrix.Identity, CullRect);

    public SKShader ToShader(SKShaderTileMode tmx, SKShaderTileMode tmy) =>
        ToShader(tmx, tmy, SKFilterMode.Nearest, SKMatrix.Identity, CullRect);

    public SKShader ToShader(SKShaderTileMode tmx, SKShaderTileMode tmy, SKFilterMode filterMode) =>
        ToShader(tmx, tmy, filterMode, SKMatrix.Identity, CullRect);

    public SKShader ToShader(SKShaderTileMode tmx, SKShaderTileMode tmy, SKRect tile) =>
        ToShader(tmx, tmy, SKFilterMode.Nearest, SKMatrix.Identity, tile);

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterMode filterMode,
        SKRect tile) =>
        ToShader(tmx, tmy, filterMode, SKMatrix.Identity, tile);

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKMatrix localMatrix,
        SKRect tile) =>
        ToShader(tmx, tmy, SKFilterMode.Nearest, localMatrix, tile);

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterMode filterMode,
        SKMatrix localMatrix,
        SKRect tile) =>
        SKShader.CreatePicture(Picture.Clone(), tmx, tmy, filterMode, localMatrix, tile);

    public void Dispose()
    {
        _picture?.Dispose();
        _picture = null;
    }

    private static int CountOperations(GpuPicture picture, bool includeNested)
    {
        var count = picture.Commands.Length;
        if (!includeNested)
        {
            return count;
        }

        foreach (var command in picture.Commands)
        {
            if (command.Type == RenderCommandType.DrawPicture && command.Picture is { } nested)
            {
                count = checked(count + CountOperations(nested, includeNested: true));
            }
        }

        return count;
    }
}

public sealed class SKPictureRecorder : IDisposable
{
    private GpuPictureRecorder? _recorder;
    private SKCanvas? _canvas;
    private SKRect _cullRect;

    public SKCanvas? RecordingCanvas => _canvas;

    public SKCanvas BeginRecording(SKRect cullRect)
    {
        if (_recorder != null)
        {
            throw new InvalidOperationException("A picture recording is already active.");
        }

        _cullRect = cullRect;
        _recorder = new GpuPictureRecorder();
        var context = _recorder.BeginRecording(new Rect(cullRect.Left, cullRect.Top, cullRect.Width, cullRect.Height));
        _canvas = new SKCanvas(context, cullRect.Width, cullRect.Height, isPictureRecording: true);
        return _canvas;
    }

    public SKCanvas BeginRecording(SKRect cullRect, bool useRTree) =>
        BeginRecording(cullRect);

    public SKPicture EndRecording()
    {
        var recorder = _recorder ?? throw new InvalidOperationException("No picture recording is active.");
        var picture = new SKPicture(recorder.EndRecording(), _cullRect);
        _recorder = null;
        _canvas = null;
        return picture;
    }

    public SKDrawable EndRecordingAsDrawable() =>
        new SKRecordedPictureDrawable(EndRecording());

    public void Dispose()
    {
        _canvas?.Dispose();
        _canvas = null;
        _recorder = null;
    }
}
