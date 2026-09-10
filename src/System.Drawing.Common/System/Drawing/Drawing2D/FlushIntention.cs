namespace System.Drawing.Drawing2D;

/// <summary>
/// Specifies whether a graphics flush may return after submission or must wait
/// for the submitted work to complete.
/// </summary>
public enum FlushIntention
{
    /// <summary>Flush all batched rendering operations.</summary>
    Flush = 0,

    /// <summary>Flush all batched rendering operations and wait for completion.</summary>
    Sync = 1,
}
