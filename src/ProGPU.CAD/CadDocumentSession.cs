using ACadSharp;

namespace ProGPU.CAD;

public delegate TResult CadDocumentRead<out TResult>(CadDocument document);

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

    public ulong Edit(string reason, CadDocumentEdit edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(edit);

        ulong generation;
        lock (_gate)
        {
            edit(_document);
            generation = checked(++_contentGeneration);
        }

        Changed?.Invoke(this, new CadDocumentChangedEventArgs(generation, reason));
        return generation;
    }

    internal TResult Save<TResult>(Func<CadDocument, ulong, TResult> save)
    {
        ArgumentNullException.ThrowIfNull(save);

        lock (_gate)
        {
            TResult result = save(_document, _contentGeneration);
            _savedGeneration = _contentGeneration;
            return result;
        }
    }
}
