using System;
using ProGPU.Scene;
using ProGPU.Vector;

namespace SkiaSharp;

public sealed class SKPicture : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    private GpuPicture? _picture;

    internal SKPicture(GpuPicture picture, SKRect cullRect)
    {
        _picture = picture;
        CullRect = cullRect;
    }

    public SKRect CullRect { get; }

    internal GpuPicture Picture => _picture ?? throw new ObjectDisposedException(nameof(SKPicture));

    public void Playback(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.DrawPicture(this);
    }

    public SKShader ToShader(
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix,
        SKRect tileRect)
    {
        return SKShader.CreatePicture(Picture.Clone(), tileModeX, tileModeY, localMatrix, tileRect);
    }

    public SKShader ToShader(SKShaderTileMode tileModeX, SKShaderTileMode tileModeY)
    {
        return ToShader(tileModeX, tileModeY, SKMatrix.Identity, CullRect);
    }

    public void Dispose()
    {
        _picture?.Dispose();
        _picture = null;
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

    public SKPicture EndRecording()
    {
        var recorder = _recorder ?? throw new InvalidOperationException("No picture recording is active.");
        var picture = new SKPicture(recorder.EndRecording(), _cullRect);
        _recorder = null;
        _canvas = null;
        return picture;
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        _canvas = null;
        _recorder = null;
    }
}
