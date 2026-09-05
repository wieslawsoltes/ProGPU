namespace ProGPU.CAD;

/// <summary>
/// Identifies one requested immutable-scene publication for one document
/// generation. Tickets are created only by <see cref="CadScenePublicationGate"/>.
/// </summary>
public readonly struct CadScenePublicationTicket
{
    internal CadScenePublicationGate? Owner { get; }
    internal CadDocumentSession? Session { get; }
    internal ulong RequestId { get; }

    /// <summary>The document generation that preparation must reproduce.</summary>
    public ulong ContentGeneration { get; }

    /// <summary>Whether this value was issued by a publication gate.</summary>
    public bool IsValid => Owner is not null;

    internal CadScenePublicationTicket(
        CadScenePublicationGate owner,
        CadDocumentSession session,
        ulong requestId,
        ulong contentGeneration)
    {
        Owner = owner;
        Session = session;
        RequestId = requestId;
        ContentGeneration = contentGeneration;
    }
}

/// <summary>
/// Arbitrates background-prepared immutable CAD scene publication.
/// </summary>
/// <remarks>
/// Begin is O(1). IsCurrent and TryPublish are O(1), allocate no storage, and
/// never expose the mutable ACadSharp document. A newer request, a different
/// session, an edited document, an invalidated gate, or a prepared generation
/// mismatch rejects the complete result before the caller swaps any retained
/// state. One ticket may publish successfully at most once.
/// </remarks>
public sealed class CadScenePublicationGate
{
    private readonly object _gate = new();
    private CadDocumentSession? _session;
    private ulong _requestId;
    private ulong _publishedRequestId;

    /// <summary>
    /// Starts one publication request at the session's current generation.
    /// Any earlier ticket from this gate becomes stale.
    /// </summary>
    public CadScenePublicationTicket Begin(CadDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ulong generation = session.ContentGeneration;

        lock (_gate)
        {
            _session = session;
            _requestId = NextRequestId(_requestId);
            _publishedRequestId = 0;
            return new CadScenePublicationTicket(
                this,
                session,
                _requestId,
                generation);
        }
    }

    /// <summary>
    /// Invalidates every outstanding ticket without changing the document.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _session = null;
            _requestId = NextRequestId(_requestId);
            _publishedRequestId = 0;
        }
    }

    /// <summary>
    /// Returns whether a ticket is the latest request and its document has not
    /// advanced. A successfully published ticket remains current but cannot be
    /// published a second time.
    /// </summary>
    public bool IsCurrent(CadScenePublicationTicket ticket)
    {
        lock (_gate)
        {
            return MatchesCurrentRequest(ticket) &&
                ticket.Session!.ContentGeneration == ticket.ContentGeneration;
        }
    }

    /// <summary>
    /// Atomically validates and publishes one fully prepared immutable result.
    /// The callback should only exchange bounded retained-state references and
    /// must not edit the document session.
    /// </summary>
    public bool TryPublish(
        CadScenePublicationTicket ticket,
        ulong preparedContentGeneration,
        Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);

        lock (_gate)
        {
            if (!MatchesCurrentRequest(ticket) ||
                _publishedRequestId == ticket.RequestId ||
                preparedContentGeneration != ticket.ContentGeneration)
            {
                return false;
            }

            bool published = ticket.Session!.TryPublishGeneration(
                ticket.ContentGeneration,
                publish);
            if (published)
            {
                _publishedRequestId = ticket.RequestId;
            }
            return published;
        }
    }

    private bool MatchesCurrentRequest(CadScenePublicationTicket ticket) =>
        ReferenceEquals(ticket.Owner, this) &&
        ReferenceEquals(ticket.Session, _session) &&
        ticket.RequestId != 0 &&
        ticket.RequestId == _requestId;

    private static ulong NextRequestId(ulong current)
    {
        ulong next = unchecked(current + 1);
        return next == 0 ? 1 : next;
    }
}
