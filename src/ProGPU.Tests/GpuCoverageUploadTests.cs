using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class GpuCoverageUploadTests
{
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 512u)]
    [InlineData(255u, 512u)]
    [InlineData(256u, 512u)]
    [InlineData(511u, 512u)]
    [InlineData(512u, 512u)]
    [InlineData(11008u, 11264u)]
    [InlineData(33024u, 33280u)]
    [InlineData(55040u, 55296u)]
    [InlineData(4294966784u, 4294966784u)]
    public void CopyPlacementUses512BytesWithoutChangingRowPitch(uint offset, uint expected)
    {
        Assert.Equal(expected, GpuCoverageUpload.AlignCopyOffset(offset));
        Assert.Equal(256u, GpuCoverageUpload.GetBytesPerRow(17));
        Assert.Equal(11008u, GpuCoverageUpload.GetRequiredBytes(17, 43));
        Assert.Equal(0u, expected % GpuCoverageUpload.CopyOffsetAlignment);
        Assert.InRange(expected - offset, 0u, 511u);
    }

    [Theory]
    [InlineData(4294966785u)]
    [InlineData(uint.MaxValue)]
    public void CopyPlacementOverflowIsRejected(uint offset) =>
        Assert.Throws<OverflowException>(() => GpuCoverageUpload.AlignCopyOffset(offset));
}
