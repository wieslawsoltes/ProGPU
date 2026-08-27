using ACadSharp;

namespace ProGPU.CAD;

public delegate TResult CadDocumentRead<out TResult>(CadDocument document);

public delegate TResult CadDocumentCapture<out TResult>(
    CadDocument document,
    ulong contentGeneration);

public delegate void CadDocumentEdit(CadDocument document);

public sealed class CadDocumentChangedEventArgs : EventArgs
{
    public ulong Generation { get; }

    public string Reason { get; }

    internal CadDocumentChangedEventArgs(ulong generation, string reason)
    {
        Generation = generation;
        Reason = reason;
    }
}

/// <summary>
/// Owns one mutable ACadSharp document and publishes monotonic content generations.
/// </summary>
/// <remarks>
/// Callbacks must not retain the supplied document. An edit callback must either
/// complete atomically or restore its own partial mutations before throwing.
/// </remarks>
public sealed class CadDocumentSession
{
    private readonly object _gate = new();
    private readonly CadDocument _document;
    private ulong _contentGeneration;
    private ulong _savedGeneration;

    public event EventHandler<CadDocumentChangedEventArgs>? Changed;

    public CadDocumentFormat SourceFormat { get; }

    public string? SourceName { get; }

    public ulong ContentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _contentGeneration;
            }
        }
    }

    public ulong SavedGeneration
    {
        get
        {
            lock (_gate)
            {
                return _savedGeneration;
            }
        }
    }

    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _contentGeneration != _savedGeneration;
            }
        }
    }

    public CadDocumentSession(
        CadDocument document,
        CadDocumentFormat sourceFormat = CadDocumentFormat.Auto,
        string? sourceName = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        SourceFormat = sourceFormat;
        SourceName = sourceName;
    }

    public static CadDocumentSession CreateNew(
        ACadVersion version = ACadVersion.AC1032) =>
        new(new CadDocument(version));

    public TResult Read<TResult>(CadDocumentRead<TResult> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        lock (_gate)
        {
            return read(_document);
        }
    }

    /// <summary>
    /// Reads the document and its matching content generation under one lock.
    /// </summary>
    /// <remarks>
    /// Use this boundary when producing immutable derived state. It prevents a
    /// snapshot from being tagged with a generation different from its contents.
    /// The callback must not retain the mutable document.
    /// </remarks>
    public TResult Capture<TResult>(CadDocumentCapture<TResult> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        lock (_gate)
        {
            return capture(_document, _contentGeneration);
        }
    }

    public ulong Edit(string reason, CadDocumentEdit edit)
        => EditCore(reason, expectedGeneration: null, edit);

    internal ulong Edit(
        string reason,
        ulong expectedGeneration,
        CadDocumentEdit edit) =>
        EditCore(reason, expectedGeneration, edit);

    private ulong EditCore(
        string reason,
        ulong? expectedGeneration,
        CadDocumentEdit edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(edit);

        ulong generation;
        lock (_gate)
        {
            if (expectedGeneration is ulong expected && _contentGeneration != expected)
            {
                throw new CadEditHistoryDivergedException(expected, _contentGeneration);
            }

            edit(_document);
            generation = checked(++_contentGeneration);
        }

        Changed?.Invoke(this, new CadDocumentChangedEventArgs(generation, reason));
        return generation;
    }

    internal TResult Save<TResult>(
        bool markSaved,
        Func<CadDocument, ulong, TResult> save)
    {
        ArgumentNullException.ThrowIfNull(save);

        lock (_gate)
        {
            TResult result = save(_document, _contentGeneration);
            if (markSaved)
            {
                _savedGeneration = _contentGeneration;
            }
            return result;
        }
    }

    internal bool TryMarkSaved(ulong generation)
    {
        lock (_gate)
        {
            if (generation > _contentGeneration || generation < _savedGeneration)
            {
                return false;
            }

            _savedGeneration = generation;
            return true;
        }
    }
}
