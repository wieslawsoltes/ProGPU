using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public partial struct NativeHintedTextLineFrame { }

public sealed unsafe partial class NativeHintedGlyphResource
{
    private readonly NativeMethods.HintedTextLineFramesView? _lineFrames;

    private static NativeMethods.HintedTextLineFramesView BorrowLineFrames(nint handle, uint count)
    {
        NativeMethods.HintedTextLineFramesView view = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.BorrowHintedTextLineFrames(handle, &view), "writer line frames borrow");
        NativeHintedGlyphResourceReadLease.ValidateLineFrames(in view, count);
        return view;
    }
}

public sealed unsafe partial class NativeHintedGlyphResourceReadLease
{
    private readonly NativeMethods.HintedTextLineFramesView? _lineFrames;
    public bool HasLineFrames { get { EnsureLive(); return _lineFrames.HasValue; } }
    /// <summary>Original writer double tops and measured source ascents under this same generation lease.</summary>
    public ReadOnlySpan<NativeHintedTextLineFrame> LineFrames
    {
        get
        {
            EnsureLive();
            if (_lineFrames is not { } view) throw new NotSupportedException("The original resource has no retained writer frames.");
            return Read<NativeHintedTextLineFrame>(view.Frames, view.LineCount);
        }
    }
    internal static void ValidateLineFrames(in NativeMethods.HintedTextLineFramesView view, uint count)
    {
        if (view.AbiVersion != NativeMethods.AbiVersion || view.StructSize != sizeof(NativeMethods.HintedTextLineFramesView) ||
            view.Reserved != 0 || view.LineCount != count)
            throw new InvalidOperationException("Writer frames do not cover the original line generation.");
        ValidateRange<NativeHintedTextLineFrame>(view.Frames, view.LineCount);
    }

    /// <summary>
    /// Reuses original retained selection geometry in the actual writer line's
    /// local frame. Caller outputs and tails remain unchanged on every failure.
    /// No shaping, line-prefix reconstruction or new interaction geometry.
    /// </summary>
    public int GetLineSelection(int lineIndex, int start, int end, Span<NativeTextRectangle> rectangles)
    {
        lock (_gate)
        {
            EnsureLive();
            var frames = LineFrames;
            if ((uint)lineIndex >= (uint)frames.Length) throw new ArgumentOutOfRangeException(nameof(lineIndex));
            var frame = frames[lineIndex];
            if (frame.Flags != 1 || !double.IsFinite(frame.Top) || frame.Top < 0 || !float.IsFinite((float)frame.Top))
                throw new NotSupportedException("Selection requires an original measured horizontal writer frame.");
            var boxes = Boxes;
            int first = 0;
            while (first < boxes.Length && boxes[first].LineIndex < lineIndex) first++;
            int last = first;
            while (last < boxes.Length && boxes[last].LineIndex == lineIndex) last++;
            var temporary = ArrayPool<NativeTextRectangle>.Shared.Rent(rectangles.Length);
            try
            {
                NativeHintedParagraph.ThrowForStatus(NativeTextInteractionInterop.GetSelection(boxes.Slice(first, last - first),
                    start, end, temporary.AsSpan(0, rectangles.Length), out uint written), "retained line selection");
                var selected = temporary.AsSpan(0, checked((int)written));
                // Interaction publishes the writer's float frame, while the
                // retained double top remains available for original layout.
                TranslateSelectionToLineFrame((float)frame.Top, selected);
                selected.CopyTo(rectangles);
                return selected.Length;
            }
            finally { ArrayPool<NativeTextRectangle>.Shared.Return(temporary); }
        }
    }

    internal static void TranslateSelectionToLineFrame(float top, Span<NativeTextRectangle> selected)
    {
        if (!float.IsFinite(top)) throw new ArgumentOutOfRangeException(nameof(top));
        foreach (var item in selected)
            if (!float.IsFinite(item.Y - top)) throw new NotSupportedException("The original selection does not fit its line frame.");
        for (int i = 0; i < selected.Length; i++) selected[i].Y -= top;
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_borrow_line_frames")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BorrowHintedTextLineFrames(nint resource, HintedTextLineFramesView* view);
}
