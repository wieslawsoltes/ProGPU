using System.Threading;

namespace SkiaSharp;

public class SKDrawable : SKObject
{
    private int _generationId = 1;

    protected SKDrawable()
        : this(owns: true)
    {
    }

    protected SKDrawable(bool owns)
        : base(SKObjectHandle.Create(), owns)
    {
    }

    public uint GenerationId => unchecked((uint)Volatile.Read(ref _generationId));

    public SKRect Bounds => OnGetBounds();

    public int ApproximateBytesUsed => OnGetApproximateBytesUsed();

    public void Draw(SKCanvas canvas, in SKMatrix matrix)
    {
        canvas.Save();
        try
        {
            canvas.Concat(matrix);
            OnDraw(canvas);
        }
        finally
        {
            canvas.Restore();
        }
    }

    public void Draw(SKCanvas canvas, float x, float y)
    {
        var matrix = SKMatrix.CreateTranslation(x, y);
        Draw(canvas, in matrix);
    }

    public SKPicture Snapshot() => OnSnapshot();

    public void NotifyDrawingChanged() => Interlocked.Increment(ref _generationId);

    protected internal virtual void OnDraw(SKCanvas canvas)
    {
    }

    protected internal virtual int OnGetApproximateBytesUsed() => 0;

    protected internal virtual SKRect OnGetBounds() => SKRect.Empty;

    protected internal virtual SKPicture OnSnapshot()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(Bounds);
        Draw(canvas, 0f, 0f);
        return recorder.EndRecording();
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
