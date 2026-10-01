using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeEditWordBoundaryContractTests
{
    [Fact]
    public void ResultUsesGeneratedFixedWidthReturnByValueContract()
    {
        Assert.Equal(16, Marshal.SizeOf<NativeEditWordBoundaryResult>());
        Assert.Equal(0, Marshal.OffsetOf<NativeEditWordBoundaryResult>(nameof(NativeEditWordBoundaryResult.Status)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<NativeEditWordBoundaryResult>(nameof(NativeEditWordBoundaryResult.ErrorCode)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeEditWordBoundaryResult>(nameof(NativeEditWordBoundaryResult.BoundaryCount)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeEditWordBoundaryResult>(nameof(NativeEditWordBoundaryResult.LeadingContentStart)).ToInt32());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(256)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void InvalidDirectionRejectsBeforeLibraryLoadAndPreservesEntireCallerBuffer(int direction)
    {
        uint[] positions = [17, 23, 41];
        var result = NativeEditWordBoundaryInterop.Resolve("unchanged", direction, positions);
        Assert.Equal(NativeRendererStatus.InvalidArgument, result.Status);
        Assert.Equal(NativeEditWordBoundaryError.InvalidParagraphLevel, result.ErrorCode);
        Assert.Equal(0U, result.BoundaryCount);
        Assert.Equal(0U, result.LeadingContentStart);
        Assert.Equal(new uint[] { 17, 23, 41 }, positions);
    }
}
