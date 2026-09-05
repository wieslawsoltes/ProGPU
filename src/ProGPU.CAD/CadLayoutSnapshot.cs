using ACadSharp;
using ACadSharp.Objects;

namespace ProGPU.CAD;

/// <summary>
/// Owns one generation-consistent pair of immutable model-space and paper-space snapshots.
/// </summary>
/// <remarks>
/// Layout capture is O(M + P) time and storage for M expanded model-space and P expanded
/// paper-space primitives. Both snapshots are produced under the same document-session read
/// lock, so a renderer can never combine paper geometry with model geometry from another edit.
/// </remarks>
public sealed class CadLayoutSnapshot
{
    public ulong ContentGeneration { get; }

    public string LayoutName { get; }

    public CadDocumentSnapshot ModelSpace { get; }

    public CadDocumentSnapshot PaperSpace { get; }

    internal CadLayoutSnapshot(
        ulong contentGeneration,
        string layoutName,
        CadDocumentSnapshot modelSpace,
        CadDocumentSnapshot paperSpace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutName);
        ArgumentNullException.ThrowIfNull(modelSpace);
        ArgumentNullException.ThrowIfNull(paperSpace);
        if (modelSpace.ContentGeneration != contentGeneration ||
            paperSpace.ContentGeneration != contentGeneration)
        {
            throw new ArgumentException(
                "Layout child snapshots must match the layout content generation.");
        }

        ContentGeneration = contentGeneration;
        LayoutName = layoutName;
        ModelSpace = modelSpace;
        PaperSpace = paperSpace;
    }
}

/// <summary>Captures one paper layout and its referenced model space atomically.</summary>
public sealed class CadLayoutSnapshotCompiler
{
    public CadLayoutSnapshot Compile(
        CadDocumentSession session,
        string layoutName,
        CadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutName);
        options ??= new CadSnapshotOptions();
        CadSnapshotCompiler.ValidateOptions(options);

        return session.Capture((document, generation) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!document.Layouts.TryGet(layoutName, out Layout? layout))
            {
                throw new KeyNotFoundException($"CAD layout '{layoutName}' was not found.");
            }
            if (!layout.IsPaperSpace)
            {
                throw new ArgumentException(
                    $"CAD layout '{layout.Name}' is model space; a paper-space layout is required.",
                    nameof(layoutName));
            }

            string ownedLayoutName = new(layout.Name.AsSpan());
            CadDocumentSnapshot modelSpace = CadSnapshotCompiler.CompileSpace(
                document,
                generation,
                session.SourceName,
                document.ModelSpace,
                options,
                cancellationToken);
            CadDocumentSnapshot paperSpace = CadSnapshotCompiler.CompileSpace(
                document,
                generation,
                session.SourceName,
                layout.AssociatedBlock,
                options,
                cancellationToken);
            return new CadLayoutSnapshot(
                generation,
                ownedLayoutName,
                modelSpace,
                paperSpace);
        });
    }
}
