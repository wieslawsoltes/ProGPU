using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed unsafe class NativeHintedLineFrameTests
{
    [Fact]
    public void WriterFramesBorrowOriginalStorageAndRetainOwner()
    {
        NativePositionedTextLine line = new() { Height = 17, BaselineY = 43.5f };
        float penOrigin = 9;
        NativeHintedTextLineFrame frame = new() { Top = 35.125, BaselineOffset = 8.375f, Flags = 1 };
        var view = new NativeMethods.HintedGlyphResourceView
        { Counts = new() { LineCount = 1 }, Lines = (nuint)(&line), LineOrigins = (nuint)(&penOrigin) };
        var frames = Frames((nuint)(&frame), 1);
        int released = 0;
        using var owner = new NativeHintedGlyphResource(1, in view, _ => released++, lineFrames: frames);
        using var reader = owner.AcquireReadLease();
        owner.Dispose(); Assert.Equal(0, released);
        Assert.True(reader.HasLineFrames);
        Assert.Equal((nuint)(&frame), (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(reader.LineFrames)));
        Assert.Equal(35.125, reader.LineFrames[0].Top); Assert.Equal(8.375f, reader.LineFrames[0].BaselineOffset);
        reader.Dispose(); Assert.Equal(1, released);
        Assert.Throws<ObjectDisposedException>(() => { _ = reader.LineFrames.Length; });
    }

    [Fact]
    public void MissingFramesAreNotInferredFromBaselines()
    {
        NativeMethods.HintedGlyphResourceView view = default;
        using var owner = new NativeHintedGlyphResource(1, in view, _ => { });
        using var reader = owner.AcquireReadLease();
        Assert.False(reader.HasLineFrames);
        Assert.Throws<NotSupportedException>(() => { _ = reader.LineFrames.Length; });
        Assert.Throws<NotSupportedException>(() => reader.GetLineSelection(0, 0, 1, []));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidFrameHeadersPublishNoReader(int invalid)
    {
        NativeMethods.HintedGlyphResourceView view = default;
        var frames = Frames(0, 0);
        switch (invalid)
        { case 0: frames.AbiVersion++; break; case 1: frames.StructSize--; break; case 2: frames.Reserved = 1; break; case 3: frames.LineCount = 1; break; }
        using var owner = new NativeHintedGlyphResource(1, in view, _ => { }, lineFrames: frames);
        Assert.Throws<InvalidOperationException>(() => owner.AcquireReadLease());
    }

    [Fact]
    public void SelectionTranslationUsesPublishedFloatFrameAndPreservesOtherFields()
    {
        NativeTextRectangle[] values = [new() { X = 7, Y = 20.5f, Width = 11, Height = 13 }, new() { X = 5, Y = 21.25f, Width = 9, Height = 4 }];
        NativeHintedGlyphResourceReadLease.TranslateSelectionToLineFrame(20.5f, values);
        Assert.Equal(0, values[0].Y); Assert.Equal(0.75f, values[1].Y);
        Assert.Equal(7, values[0].X); Assert.Equal(11, values[0].Width); Assert.Equal(13, values[0].Height);
    }

    [Fact]
    public void LaterInvalidSelectionDoesNotPartiallyTranslateEarlierOutput()
    {
        NativeTextRectangle[] values = [new() { Y = 20 }, new() { Y = float.PositiveInfinity }];
        Assert.Throws<NotSupportedException>(() => NativeHintedGlyphResourceReadLease.TranslateSelectionToLineFrame(15, values));
        Assert.Equal(20, values[0].Y); Assert.Equal(float.PositiveInfinity, values[1].Y);
    }

    private static NativeMethods.HintedTextLineFramesView Frames(nuint frames, uint count) => new()
    { AbiVersion = NativeMethods.AbiVersion, StructSize = (uint)sizeof(NativeMethods.HintedTextLineFramesView), Frames = frames, LineCount = count };
}
