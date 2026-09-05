using System.Buffers;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace ProGPU.CAD;

public sealed class CadLoadOptions
{
    public const long DefaultMaxInputBytes = 512L * 1024L * 1024L;

    public long MaxInputBytes { get; init; } = DefaultMaxInputBytes;

    public int MaxModelSpaceEntities { get; init; } = 10_000_000;

    public bool Failsafe { get; init; } = true;

    public bool KeepUnknownEntities { get; init; } = true;
}

public sealed class CadSaveOptions
{
    public bool BinaryDxf { get; init; }

    public bool AllowUncertifiedWrite { get; init; }

    /// <summary>
    /// Keeps the session dirty after serialization so a staging caller can
    /// commit the saved generation only after its final destination succeeds.
    /// </summary>
    public bool DeferSavedGenerationCommit { get; init; }
}

public sealed class CadLoadResult
{
    public CadDocumentSession Session { get; }

    public IReadOnlyList<CadDiagnostic> Diagnostics { get; }

    internal CadLoadResult(
        CadDocumentSession session,
        IReadOnlyList<CadDiagnostic> diagnostics)
    {
        Session = session;
        Diagnostics = diagnostics;
    }
}

public sealed class CadSaveResult
{
    private Func<bool>? _commitSavedGeneration;

    public ulong SavedGeneration { get; }

    public IReadOnlyList<CadDiagnostic> Diagnostics { get; }

    public bool RequiresSavedGenerationCommit =>
        Volatile.Read(ref _commitSavedGeneration) is not null;

    internal CadSaveResult(
        ulong savedGeneration,
        IReadOnlyList<CadDiagnostic> diagnostics)
    {
        SavedGeneration = savedGeneration;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Marks a deferred serialized generation as persisted. Returns false when
    /// the result was not deferred, was already committed, or was superseded.
    /// </summary>
    public bool CommitSavedGeneration()
    {
        Func<bool>? commit = Interlocked.Exchange(
            ref _commitSavedGeneration,
            null);
        return commit is not null && commit();
    }

    internal void DeferSavedGenerationCommit(Func<bool> commit)
    {
        _commitSavedGeneration = commit;
    }
}

public interface ICadDocumentStore
{
    ValueTask<CadLoadResult> LoadAsync(
        Stream source,
        CadDocumentFormat format = CadDocumentFormat.Auto,
        CadLoadOptions? options = null,
        string? sourceName = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<CadSaveResult> SaveAsync(
        CadDocumentSession session,
        Stream destination,
        CadDocumentFormat format,
        CadSaveOptions? options = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// ACadSharp-backed DXF/DWG store. Caller-owned streams remain open.
/// </summary>
public sealed class CadDocumentStore : ICadDocumentStore
{
    private const int CopyBufferSize = 128 * 1024;

    public async ValueTask<CadLoadResult> LoadAsync(
        Stream source,
        CadDocumentFormat format = CadDocumentFormat.Auto,
        CadLoadOptions? options = null,
        string? sourceName = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new CadLoadOptions();
        ValidateLoadOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CadOperationProgress(CadOperationStage.Preparing, 0, null));

        PreparedInput prepared = await PrepareInputAsync(
            source,
            options.MaxInputBytes,
            cancellationToken).ConfigureAwait(false);

        try
        {
            CadDocumentFormat resolvedFormat = format == CadDocumentFormat.Auto
                ? DetectFormat(prepared.Stream)
                : format;

            if (resolvedFormat is not (CadDocumentFormat.Dxf or CadDocumentFormat.Dwg))
            {
                throw new NotSupportedException($"CAD format '{resolvedFormat}' is not supported.");
            }

            return await Task.Run(
                () => ReadCore(
                    prepared.Stream,
                    resolvedFormat,
                    options,
                    sourceName,
                    progress,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            prepared.Dispose();
        }
    }

    public async ValueTask<CadSaveResult> SaveAsync(
        CadDocumentSession session,
        Stream destination,
        CadDocumentFormat format,
        CadSaveOptions? options = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        if (format is not (CadDocumentFormat.Dxf or CadDocumentFormat.Dwg))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "A concrete save format is required.");
        }

        options ??= new CadSaveOptions();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CadOperationProgress(CadOperationStage.Preparing, 0, null));

        CadSaveResult result = await Task.Run(
            () => session.Save(
                markSaved: !options.DeferSavedGenerationCommit,
                (document, generation) =>
                WriteCore(
                    document,
                    generation,
                    destination,
                    format,
                    options,
                    progress,
                    cancellationToken)),
            cancellationToken).ConfigureAwait(false);

        if (options.DeferSavedGenerationCommit)
        {
            result.DeferSavedGenerationCommit(
                () => session.TryMarkSaved(result.SavedGeneration));
        }

        return result;
    }

    private static CadLoadResult ReadCore(
        Stream source,
        CadDocumentFormat format,
        CadLoadOptions options,
        string? sourceName,
        IProgress<CadOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source.Position = 0;
        var diagnostics = new List<CadDiagnostic>();

        using var lease = new NonDisposingStream(source);
        using ICadReader reader = CreateReader(lease, format, options);
        reader.OnNotification += (_, args) => diagnostics.Add(ToDiagnostic(args));
        reader.OnProgress += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CadOperationProgress(
                args.Stage == ReadStage.Read
                    ? CadOperationStage.Reading
                    : CadOperationStage.Building,
                args.Current.Handle,
                args.Current.ObjectName));
        };

        CadDocument document = reader.Read();
        cancellationToken.ThrowIfCancellationRequested();

        if (document.Entities.Count > options.MaxModelSpaceEntities)
        {
            throw new InvalidDataException(
                $"The document contains {document.Entities.Count} model-space entities; " +
                $"the configured limit is {options.MaxModelSpaceEntities}.");
        }

        progress?.Report(new CadOperationProgress(CadOperationStage.Completed, 0, null));
        return new CadLoadResult(
            new CadDocumentSession(document, format, sourceName),
            diagnostics.ToArray());
    }

    private static CadSaveResult WriteCore(
        CadDocument document,
        ulong generation,
        Stream destination,
        CadDocumentFormat format,
        CadSaveOptions options,
        IProgress<CadOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CadFormatCapabilities capabilities =
            CadFormatSupport.GetCapabilities(format, document.Header.Version);
        if (!capabilities.CanWrite)
        {
            throw new NotSupportedException(
                $"{format} writing is not supported for {document.Header.Version}.");
        }

        if (!capabilities.IsWriteCertified && !options.AllowUncertifiedWrite)
        {
            throw new InvalidOperationException(
                $"{format} writing for {document.Header.Version} has not completed " +
                "ProGPU.CAD round-trip certification. Set AllowUncertifiedWrite only " +
                "for explicit development or interoperability testing.");
        }

        ValidateLosslessWriteContracts(document, format);

        progress?.Report(new CadOperationProgress(CadOperationStage.Writing, 0, null));
        var diagnostics = new List<CadDiagnostic>();
        using var lease = new NonDisposingStream(destination);

        NotificationEventHandler notification = (_, args) =>
            diagnostics.Add(ToDiagnostic(args));

        switch (format)
        {
            case CadDocumentFormat.Dxf:
                var dxfConfiguration = new DxfWriterConfiguration
                {
                    CloseStream = false,
                    WriteShapes = true,
                };
                DxfWriter.Write(
                    lease,
                    document,
                    options.BinaryDxf,
                    dxfConfiguration,
                    notification);
                break;
            case CadDocumentFormat.Dwg:
                var dwgConfiguration = new DwgWriterConfiguration
                {
                    CloseStream = false,
                    WriteShapes = true,
                };
                DwgWriter.Write(
                    lease,
                    document,
                    dwgConfiguration,
                    notification);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CadOperationProgress(CadOperationStage.Completed, 0, null));
        return new CadSaveResult(generation, diagnostics.ToArray());
    }

    private static void ValidateLosslessWriteContracts(
        CadDocument document,
        CadDocumentFormat format)
    {
        if (format == CadDocumentFormat.Dxf &&
            document.GetCadObjects<Wipeout>().Any(
                wipeout => wipeout.ClipMode == ClipMode.Inside))
        {
            throw new NotSupportedException(
                "CADSAVE001: DXF WIPEOUT records do not encode inverted clipping. " +
                "Save as DWG or change the WIPEOUT to an outside clip before saving.");
        }
    }

    private static ICadReader CreateReader(
        Stream source,
        CadDocumentFormat format,
        CadLoadOptions options)
    {
        switch (format)
        {
            case CadDocumentFormat.Dxf:
                return new DxfReader(source)
                {
                    Configuration = new DxfReaderConfiguration
                    {
                        Failsafe = options.Failsafe,
                        KeepUnknownEntities = options.KeepUnknownEntities,
                        KeepUnknownNonGraphicalObjects = options.KeepUnknownEntities
                    }
                };
            case CadDocumentFormat.Dwg:
                return new DwgReader(source)
                {
                    Configuration = new DwgReaderConfiguration
                    {
                        Failsafe = options.Failsafe,
                        KeepUnknownEntities = options.KeepUnknownEntities,
                        KeepUnknownNonGraphicalObjects = options.KeepUnknownEntities
                    }
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static CadDiagnostic ToDiagnostic(NotificationEventArgs args)
    {
        CadDiagnosticSeverity severity = args.NotificationType switch
        {
            NotificationType.Error => CadDiagnosticSeverity.Error,
            NotificationType.Warning or
            NotificationType.NotImplemented or
            NotificationType.NotSupported => CadDiagnosticSeverity.Warning,
            _ => CadDiagnosticSeverity.Information
        };

        return new CadDiagnostic(
            severity,
            $"ACADSHARP_{args.NotificationType.ToString().ToUpperInvariant()}",
            args.Message);
    }

    private static CadDocumentFormat DetectFormat(Stream source)
    {
        Span<byte> signature = stackalloc byte[6];
        source.Position = 0;
        int count = source.Read(signature);
        source.Position = 0;

        if (count >= 4 &&
            signature[0] == (byte)'A' &&
            signature[1] == (byte)'C' &&
            signature[2] == (byte)'1' &&
            signature[3] == (byte)'0')
        {
            return CadDocumentFormat.Dwg;
        }

        return CadDocumentFormat.Dxf;
    }

    private static async ValueTask<PreparedInput> PrepareInputAsync(
        Stream source,
        long maxInputBytes,
        CancellationToken cancellationToken)
    {
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        if (source.CanSeek)
        {
            if (source.Length > maxInputBytes)
            {
                throw new InvalidDataException(
                    $"The input contains {source.Length} bytes; the configured limit is {maxInputBytes}.");
            }

            return new PreparedInput(source, ownsStream: false);
        }

        var copy = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > maxInputBytes)
                {
                    throw new InvalidDataException(
                        $"The input exceeds the configured limit of {maxInputBytes} bytes.");
                }

                await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            copy.Position = 0;
            return new PreparedInput(copy, ownsStream: true);
        }
        catch
        {
            copy.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateLoadOptions(CadLoadOptions options)
    {
        if (options.MaxInputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxInputBytes,
                "MaxInputBytes must be positive.");
        }

        if (options.MaxModelSpaceEntities <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxModelSpaceEntities,
                "MaxModelSpaceEntities must be positive.");
        }
    }

    private readonly struct PreparedInput : IDisposable
    {
        public Stream Stream { get; }

        private readonly bool _ownsStream;

        public PreparedInput(Stream stream, bool ownsStream)
        {
            Stream = stream;
            _ownsStream = ownsStream;
        }

        public void Dispose()
        {
            if (_ownsStream)
            {
                Stream.Dispose();
            }
        }
    }

    private sealed class NonDisposingStream : Stream
    {
        private readonly Stream _inner;

        public NonDisposingStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            // The caller owns the underlying stream.
            base.Dispose(disposing);
        }
    }
}
