namespace System.Drawing.Drawing2D;

public sealed class GraphicsContainer : MarshalByRefObject
{
    internal GraphicsContainer(int stateId)
    {
        StateId = stateId;
    }

    internal int StateId { get; }
}
