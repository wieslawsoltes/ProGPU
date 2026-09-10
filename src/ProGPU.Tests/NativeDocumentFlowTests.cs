using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class NativeDocumentFlowTests
{
    [Fact]
    public void NativeRowAndCellDescriptorsMatchNeutralBorrowedSpans()
    {
        Assert.Equal(24, Unsafe.SizeOf<NativeDocumentRow>());
        Assert.Equal(24, Unsafe.SizeOf<PortableDocumentRow>());
        Assert.Equal(16, Marshal.OffsetOf<NativeDocumentRow>(nameof(NativeDocumentRow.CellSpacing)).ToInt32());
        Assert.Equal(16, Unsafe.SizeOf<NativeDocumentCell>());
        Assert.Equal(16, Unsafe.SizeOf<PortableDocumentCell>());
        Span<PortableDocumentRow> rows = [new() { BlockIndex = 2, ColumnStart = 3, ColumnCount = 4, Reserved = 0, CellSpacing = 5.25 }];
        var row = MemoryMarshal.Cast<PortableDocumentRow, NativeDocumentRow>(rows)[0];
        Assert.Equal(2U, row.BlockIndex); Assert.Equal(3U, row.ColumnStart);
        Assert.Equal(4U, row.ColumnCount); Assert.Equal(0U, row.Reserved); Assert.Equal(5.25, row.CellSpacing);
        Span<PortableDocumentCell> cells = [new() { BlockIndex = 6, RowIndex = 7, ColumnStart = 8, ColumnCount = 9 }];
        var cell = MemoryMarshal.Cast<PortableDocumentCell, NativeDocumentCell>(cells)[0];
        Assert.Equal(6U, cell.BlockIndex); Assert.Equal(7U, cell.RowIndex);
        Assert.Equal(8U, cell.ColumnStart); Assert.Equal(9U, cell.ColumnCount);
    }
    [Fact]
    public void NeutralDocumentSpansPreserveEveryWireFieldWithoutRepacking()
    {
        Assert.Equal(Unsafe.SizeOf<NativeDocumentBlock>(), Unsafe.SizeOf<PortableDocumentBlock>());
        Assert.Equal(Unsafe.SizeOf<NativeDocumentLine>(), Unsafe.SizeOf<PortableDocumentLine>());
        Assert.Equal(Unsafe.SizeOf<NativeDocumentBox>(), Unsafe.SizeOf<PortableDocumentBox>());
        Assert.Equal(Unsafe.SizeOf<NativeDocumentLinePosition>(), Unsafe.SizeOf<PortableDocumentLinePosition>());
        Span<PortableDocumentBlock> input = [new() {
            ParentIndex = 1, SubtreeEnd = 2, LineStart = 3, LineCount = 4,
            MarginLeft = 5.25, MarginTop = 6.25, MarginRight = 7.25, MarginBottom = 8.25,
            InsetLeft = 9.25, InsetTop = 10.25, InsetRight = 11.25, InsetBottom = 12.25 }];
        ref var wire = ref MemoryMarshal.Cast<PortableDocumentBlock, NativeDocumentBlock>(input)[0];
        Assert.Equal(1U, wire.ParentIndex); Assert.Equal(2U, wire.SubtreeEnd);
        Assert.Equal(3U, wire.LineStart); Assert.Equal(4U, wire.LineCount);
        Assert.Equal(5.25, wire.MarginLeft); Assert.Equal(6.25, wire.MarginTop);
        Assert.Equal(7.25, wire.MarginRight); Assert.Equal(8.25, wire.MarginBottom);
        Assert.Equal(9.25, wire.InsetLeft); Assert.Equal(10.25, wire.InsetTop);
        Assert.Equal(11.25, wire.InsetRight); Assert.Equal(12.25, wire.InsetBottom);
        Span<PortableDocumentLine> lines = [new() { Width = 13.25, Height = 14.25 }];
        var metrics = MemoryMarshal.Cast<PortableDocumentLine, NativeDocumentLine>(lines);
        Assert.Equal(13.25, metrics[0].Width); Assert.Equal(14.25, metrics[0].Height);
        Span<PortableDocumentBox> boxes = [new() { X = 15.25, Y = 16.25, Width = 17.25, Height = 18.25 }];
        var nativeBoxes = MemoryMarshal.Cast<PortableDocumentBox, NativeDocumentBox>(boxes);
        Assert.Equal(15.25, nativeBoxes[0].X); Assert.Equal(16.25, nativeBoxes[0].Y);
        Assert.Equal(17.25, nativeBoxes[0].Width); Assert.Equal(18.25, nativeBoxes[0].Height);
        nativeBoxes[0].Height = 19.25;
        Assert.Equal(19.25, boxes[0].Height);
        Span<PortableDocumentLinePosition> positions = [new() { X = 20.25, Y = 21.25 }];
        var nativePositions = MemoryMarshal.Cast<PortableDocumentLinePosition, NativeDocumentLinePosition>(positions);
        Assert.Equal(20.25, nativePositions[0].X); Assert.Equal(21.25, nativePositions[0].Y);
    }

    [Fact]
    public void GeneratedDoublePrecisionLayoutsMatchCAbi()
    {
        Assert.Equal(24, Unsafe.SizeOf<NativeDocumentObject>());
        Assert.Equal(24, Unsafe.SizeOf<PortableDocumentObject>());
        Assert.Equal(8, Marshal.OffsetOf<NativeDocumentObject>(nameof(NativeDocumentObject.Width)).ToInt32());
        Span<PortableDocumentObject> objects = [new() { BlockIndex = 11, Reserved = 0, Width = 12.25, Height = 13.25 }];
        var objectWire = MemoryMarshal.Cast<PortableDocumentObject, NativeDocumentObject>(objects)[0];
        Assert.Equal(11U, objectWire.BlockIndex); Assert.Equal(0U, objectWire.Reserved);
        Assert.Equal(12.25, objectWire.Width); Assert.Equal(13.25, objectWire.Height);
        Assert.Equal(40, Unsafe.SizeOf<NativeDocumentFragmentLine>());
        Assert.Equal(40, Unsafe.SizeOf<PortableDocumentFragmentLine>());
        Assert.Equal(16, Unsafe.SizeOf<NativeDocumentFragmentPosition>());
        Assert.Equal(16, Unsafe.SizeOf<PortableDocumentFragmentPosition>());
        Assert.Equal(16, Unsafe.SizeOf<NativeDocumentPaginationResult>());
        Span<PortableDocumentFragmentLine> fragment = [new() { AllowBreakBefore = 1, ForceColumnBefore = 2,
            ForcePageBefore = 3, Reserved = 4, Height = 5.25, SpaceBefore = 6.25, LeadingSpace = 7.25 }];
        var native = MemoryMarshal.Cast<PortableDocumentFragmentLine, NativeDocumentFragmentLine>(fragment)[0];
        Assert.Equal(1U, native.AllowBreakBefore); Assert.Equal(2U, native.ForceColumnBefore);
        Assert.Equal(3U, native.ForcePageBefore); Assert.Equal(4U, native.Reserved);
        Assert.Equal(5.25, native.Height); Assert.Equal(6.25, native.SpaceBefore); Assert.Equal(7.25, native.LeadingSpace);
        Span<PortableDocumentFragmentPosition> placed = [new() { Page = 8, Column = 9, Y = 10.25 }];
        var position = MemoryMarshal.Cast<PortableDocumentFragmentPosition, NativeDocumentFragmentPosition>(placed)[0];
        Assert.Equal(8U, position.Page); Assert.Equal(9U, position.Column); Assert.Equal(10.25, position.Y);
        Assert.Equal(80, Unsafe.SizeOf<NativeDocumentBlock>());
        Assert.Equal(16, Marshal.OffsetOf<NativeDocumentBlock>(nameof(NativeDocumentBlock.MarginLeft)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<NativeDocumentBlock>(nameof(NativeDocumentBlock.InsetBottom)).ToInt32());
        Assert.Equal(16, Unsafe.SizeOf<NativeDocumentLine>());
        Assert.Equal(32, Unsafe.SizeOf<NativeDocumentBox>());
        Assert.Equal(16, Unsafe.SizeOf<NativeDocumentLinePosition>());
        Assert.Equal(32, Unsafe.SizeOf<NativeDocumentFlowResult>());
        Assert.Equal(16, Marshal.OffsetOf<NativeDocumentFlowResult>(nameof(NativeDocumentFlowResult.Width)).ToInt32());
        Assert.Equal(uint.MaxValue, NativeDocumentFlow.NoParent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void BadWidthsFailBeforeLoadingNativeCode(double width)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.ResolveWidthsWithRows([], width, [], [], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.ArrangeWithRows([], width, [], [], [], [], [], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.ArrangeWithObjects([], width, [], [], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.ResolveWidths([], width, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.Arrange([], width, [], [], []));
    }

    [Fact]
    public void BadCapacitiesAndBackendFailBeforeLoadingNativeCode()
    {
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.ResolveWidthsWithRows([new()], 100, [], [], [], []));
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.ArrangeWithRows([], 100, [new()], [], [], [], [], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.ArrangeWithRows([], 100, [], [], [], [], [], [], [], (NativeMilBackend)99));
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.ArrangeWithObjects([new()], 100, [], [], [], []));
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.ArrangeWithObjects([], 100, [new()], [], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.ArrangeWithObjects([], 100, [], [], [], [], (NativeMilBackend)99));
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.Paginate([new()], 100, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.Paginate([], 0, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.Paginate([], double.NaN, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.Paginate([], 100, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.Paginate([], 100, 1025, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.Paginate([], 100, 1, [], (NativeMilBackend)99));
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.ResolveWidths([new()], 100, []));
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.Arrange([new()], 100, [], [], []));
        Assert.Throws<ArgumentException>(() => NativeDocumentFlow.Arrange([], 100, [new()], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.ResolveWidths([], 100, [], (NativeMilBackend)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDocumentFlow.Arrange([], 100, [], [], [], (NativeMilBackend)99));
    }
}
