using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

// Fields are generated from the authoritative C header, not duplicated here.
public partial struct NativeHintedGlyphFontSource { }
public partial struct NativeHintedGlyphRunSlice { }
public partial struct NativeHintedGlyphOutlineOwner { }
public partial struct NativeMilHintedGlyphBinding { }

/// <summary>
/// Target-independent original hinted geometry, format, interaction and font
/// bytes. Its native owner stays in the producer library; renderer imports
/// receive only immutable flat records under a destruction-excluding lease.
/// This explicit resource does not select source Display rendering.
/// </summary>
public sealed unsafe class NativeHintedGlyphResource : IDisposable
{
    private readonly object _gate = new();
    private readonly NativeMethods.HintedGlyphResourceView _view;
    private nint _handle;
    private int _uses;
    private bool _disposed;

    internal NativeHintedGlyphResource(nint handle, float dpiScale,
        NativeHintedProjectionPolicy projection, NativeHintedCoverage coverage)
    {
        NativeMethods.HintedGlyphResourceView view = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.BorrowHintedGlyphResource(handle, &view), "glyph resource borrow");
        if (view.StructSize != (uint)sizeof(NativeMethods.HintedGlyphResourceView) ||
            view.AbiVersion != NativeMethods.AbiVersion ||
            BitConverter.SingleToInt32Bits(view.DpiScale) != BitConverter.SingleToInt32Bits(dpiScale) ||
            view.ProjectionPolicy != (uint)projection || view.Coverage != (uint)coverage)
            throw new InvalidOperationException("Native hinted geometry changed its exact prepared execution contract.");
        _view = view;
        // Ownership transfers last. The factory destroys the raw handle if
        // borrowing or validation throws before this nonthrowing assignment.
        _handle = handle;
    }

    public float DpiScale => _view.DpiScale;
    public NativeHintedProjectionPolicy Projection => (NativeHintedProjectionPolicy)_view.ProjectionPolicy;
    public NativeHintedCoverage Coverage => (NativeHintedCoverage)_view.Coverage;
    public NativeHintedParagraphCounts Counts => _view.Counts;
    public NativeTextParagraphResult Result => _view.Result;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    // Unlike a mutable shaping context, cached flat data needs no long-held
    // monitor. Concurrent readers acquire short lifetime-counted leases, so
    // reversed multi-resource import order cannot deadlock. No native call or
    // opaque handle is returned to the renderer here.
    internal NativeMethods.HintedGlyphResourceView AcquireForImport()
    {
        lock (_gate)
        {
            if (_disposed || _handle == 0) throw new ObjectDisposedException(nameof(NativeHintedGlyphResource));
            _uses = checked(_uses + 1);
            return _view;
        }
    }

    internal void EndImport()
    {
        lock (_gate)
        {
            System.Diagnostics.Debug.Assert(_uses > 0);
            _uses--;
            ReleaseIfUnused();
        }
    }

    private void ReleaseIfUnused()
    {
        if (!_disposed || _uses != 0 || _handle == 0) return;
        NativeMethods.DestroyHintedGlyphResource(_handle);
        _handle = 0;
        GC.SuppressFinalize(this);
    }

    ~NativeHintedGlyphResource()
    {
        try { Dispose(); }
        catch { /* Finalization cannot surface a native-module teardown fault. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseIfUnused();
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_prepare_glyph_resource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PrepareHintedGlyphResource(nint paragraph,
        HintedGlyphResourceRequest* request, nint* resource);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_borrow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BorrowHintedGlyphResource(nint resource,
        HintedGlyphResourceView* view);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyHintedGlyphResource(nint resource);
}
