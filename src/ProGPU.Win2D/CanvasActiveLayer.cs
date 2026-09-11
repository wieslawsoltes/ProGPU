namespace Microsoft.Graphics.Canvas;

public sealed class CanvasActiveLayer : IDisposable
{
    private CanvasDrawingSession? _session;
    private readonly int _token;

    internal CanvasActiveLayer(CanvasDrawingSession session, int token)
    {
        _session = session;
        _token = token;
    }

    public void Dispose()
    {
        CanvasDrawingSession? session = _session;
        if (session is null)
        {
            return;
        }

        session.CloseLayer(_token);
        _session = null;
        GC.SuppressFinalize(this);
    }
}
