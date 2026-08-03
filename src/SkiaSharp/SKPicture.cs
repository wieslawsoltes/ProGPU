using System.Threading;
using ProGPU.Scene;
using ProGPU.Vector;

namespace SkiaSharp;

public partial class SKPicture : SKObject
{
    private static int s_nextUniqueId;

    private GpuPicture? _picture;

    internal SKPicture(GpuPicture picture, SKRect cullRect)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _picture = picture;
        CullRect = cullRect;
        UniqueId = NextUniqueId();
    }

    public uint UniqueId { get; }

    public SKRect CullRect { get; }

    public int ApproximateBytesUsed => GetApproximateBytesUsed(Picture);

    public int ApproximateOperationCount => GetApproximateOperationCount(includeNested: false);

    internal GpuPicture Picture => _picture ?? throw new ObjectDisposedException(nameof(SKPicture));

    public int GetApproximateOperationCount(bool includeNested)
    {
        var picture = Picture;
        if (!includeNested)
        {
            return picture.RetainedCommands.Length;
        }

        long count = 0;
        foreach (var command in picture.RetainedCommands)
        {
            count += command.Type == RenderCommandType.DrawPicture && command.Picture is { } nested
                ? GetApproximateOperationCount(nested, includeNested: true)
                : 1;
            if (count >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)count;
    }

    public void Playback(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.DrawPicture(this);
    }

    public SKShader ToShader() =>
        ToShader(
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            SKFilterMode.Nearest,
            SKMatrix.Identity,
            CullRect);

    public SKShader ToShader(SKShaderTileMode tmx, SKShaderTileMode tmy) =>
        ToShader(tmx, tmy, SKFilterMode.Nearest, SKMatrix.Identity, CullRect);

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterMode filterMode) =>
        ToShader(tmx, tmy, filterMode, SKMatrix.Identity, CullRect);

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKRect tile) =>
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
        SKShader.CreatePicture(
            Picture.Clone(),
            tmx,
            tmy,
            filterMode,
            localMatrix,
            tile);

    private static int GetApproximateOperationCount(GpuPicture picture, bool includeNested)
    {
        if (!includeNested)
        {
            return picture.RetainedCommands.Length;
        }

        long count = 0;
        foreach (var command in picture.RetainedCommands)
        {
            count += command.Type == RenderCommandType.DrawPicture && command.Picture is { } nested
                ? GetApproximateOperationCount(nested, includeNested: true)
                : 1;
            if (count >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)count;
    }

    private static int GetApproximateBytesUsed(GpuPicture picture)
    {
        long bytes = 24;
        bytes += picture.CommandStorageBytes;
        bytes += (long)picture.PointBuffer.Length * sizeof(float) * 2;
        bytes += (long)picture.DoubleBuffer.Length * sizeof(double);
        bytes += (long)picture.Line3DBuffer.Length * sizeof(float) * 6;
        bytes += (long)picture.FloatBuffer.Length * sizeof(float);
        bytes += (long)picture.ImageEffectCount *
            System.Runtime.CompilerServices.Unsafe.SizeOf<ImageEffectCommandData>();

        foreach (var command in picture.RetainedCommands)
        {
            bytes += (long)(command.Text?.Length ?? 0) * sizeof(char);
            bytes += (long)(command.PolylinePoints?.Length ?? 0) * sizeof(float) * 2;
            bytes += (long)(command.SplineKnots?.Length ?? 0) * sizeof(double);
            bytes += (long)(command.SplineWeights?.Length ?? 0) * sizeof(double);
            bytes += (long)(command.GpuPoints?.Length ?? 0) * sizeof(float);
            bytes += (long)(command.GlyphIndices?.Length ?? 0) * sizeof(ushort);
            bytes += (long)(command.GlyphPositions?.Length ?? 0) * sizeof(float) * 2;
            if (command.Type == RenderCommandType.DrawPicture && command.Picture is { } nested)
            {
                bytes += GetApproximateBytesUsed(nested);
            }

            if (bytes >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)bytes;
    }

    private static uint NextUniqueId()
    {
        uint result;
        do
        {
            result = unchecked((uint)Interlocked.Increment(ref s_nextUniqueId));
        }
        while (result == 0);

        return result;
    }

    protected override void DisposeManaged()
    {
        _picture?.Dispose();
        _picture = null;
        base.DisposeManaged();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}

public class SKPictureRecorder : SKObject
{
    private GpuPictureRecorder? _recorder;
    private SKCanvas? _canvas;
    private SKRect _cullRect;

    public SKPictureRecorder()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    public SKCanvas? RecordingCanvas => _canvas;

    public SKCanvas BeginRecording(SKRect cullRect) => BeginRecording(cullRect, useRTree: false);

    public SKCanvas BeginRecording(SKRect cullRect, bool useRTree)
    {
        if (_recorder != null)
        {
            throw new InvalidOperationException("A picture recording is already active.");
        }

        _cullRect = cullRect;
        _recorder = new GpuPictureRecorder();
        var context = _recorder.BeginRecording(
            new Rect(cullRect.Left, cullRect.Top, cullRect.Width, cullRect.Height));
        _canvas = new SKCanvas(context, cullRect.Width, cullRect.Height, isPictureRecording: true);
        return _canvas;
    }

    public SKPicture EndRecording()
    {
        var recorder = _recorder ?? throw new InvalidOperationException("No picture recording is active.");
        _canvas?.CompletePictureRecording();
        var gpuPicture = recorder.EndRecording();
        var cullRect = gpuPicture.RetainedCommands.Length == 0 ? SKRect.Empty : _cullRect;
        var picture = new SKPicture(gpuPicture, cullRect);
        _recorder = null;
        _canvas = null;
        return picture;
    }

    public SKDrawable EndRecordingAsDrawable() => new PictureDrawable(EndRecording());

    protected override void DisposeManaged()
    {
        _canvas?.Dispose();
        _canvas = null;
        _recorder = null;
        base.DisposeManaged();
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    private sealed class PictureDrawable : SKDrawable
    {
        private SKPicture? _picture;

        public PictureDrawable(SKPicture picture)
        {
            _picture = picture;
        }

        protected internal override void OnDraw(SKCanvas canvas) =>
            canvas.DrawPicture(GetPicture());

        protected internal override int OnGetApproximateBytesUsed() =>
            GetPicture().ApproximateBytesUsed;

        protected internal override SKRect OnGetBounds() => GetPicture().CullRect;

        protected internal override SKPicture OnSnapshot()
        {
            var picture = GetPicture();
            return new SKPicture(picture.Picture.Clone(), picture.CullRect);
        }

        protected override void DisposeManaged()
        {
            _picture?.Dispose();
            _picture = null;
            base.DisposeManaged();
        }

        private SKPicture GetPicture() =>
            _picture ?? throw new ObjectDisposedException(nameof(PictureDrawable));
    }
}
