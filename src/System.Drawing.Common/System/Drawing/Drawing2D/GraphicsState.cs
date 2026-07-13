namespace System.Drawing.Drawing2D;

public sealed class GraphicsState : MarshalByRefObject
{
    internal GraphicsState(int stateId)
    {
        StateId = stateId;
    }

    internal int StateId { get; }
}
