using Xunit;

namespace System.Drawing.Tests;

public sealed class ManagedIdentityCompletionTests
{
    [Fact]
    public void ToolboxBitmapAttributeRetainsOfficialInheritableShape()
    {
        Assert.False(typeof(ToolboxBitmapAttribute).IsSealed);

        var attribute = new DerivedToolboxBitmapAttribute();
        Assert.IsAssignableFrom<ToolboxBitmapAttribute>(attribute);
    }

    [Fact]
    public void CopyPixelOperationHasOfficialRasterOperationValues()
    {
        Assert.Equal(0x00000042, (int)CopyPixelOperation.Blackness);
        Assert.Equal(0x40000000, (int)CopyPixelOperation.CaptureBlt);
        Assert.Equal(0x00550009, (int)CopyPixelOperation.DestinationInvert);
        Assert.Equal(0x00C000CA, (int)CopyPixelOperation.MergeCopy);
        Assert.Equal(0x00BB0226, (int)CopyPixelOperation.MergePaint);
        Assert.Equal(unchecked((int)0x80000000), (int)CopyPixelOperation.NoMirrorBitmap);
        Assert.Equal(0x00330008, (int)CopyPixelOperation.NotSourceCopy);
        Assert.Equal(0x001100A6, (int)CopyPixelOperation.NotSourceErase);
        Assert.Equal(0x00F00021, (int)CopyPixelOperation.PatCopy);
        Assert.Equal(0x005A0049, (int)CopyPixelOperation.PatInvert);
        Assert.Equal(0x00FB0A09, (int)CopyPixelOperation.PatPaint);
        Assert.Equal(0x008800C6, (int)CopyPixelOperation.SourceAnd);
        Assert.Equal(0x00CC0020, (int)CopyPixelOperation.SourceCopy);
        Assert.Equal(0x00440328, (int)CopyPixelOperation.SourceErase);
        Assert.Equal(0x00660046, (int)CopyPixelOperation.SourceInvert);
        Assert.Equal(0x00EE0086, (int)CopyPixelOperation.SourcePaint);
        Assert.Equal(0x00FF0062, (int)CopyPixelOperation.Whiteness);
    }

    private sealed class DerivedToolboxBitmapAttribute : ToolboxBitmapAttribute
    {
        public DerivedToolboxBitmapAttribute()
            : base((string?)null)
        {
        }
    }
}
