using Xunit;

namespace System.Drawing.Tests;

public sealed class SystemIconsQualityTests
{
    [Fact]
    public void StockIconIdsHaveOfficialIdentity()
    {
        Assert.Equal(93, Enum.GetValues<StockIconId>().Length);
        Assert.Equal(0u, (uint)StockIconId.DocumentNoAssociation);
        Assert.Equal(13u, (uint)StockIconId.World);
        Assert.Equal(77u, (uint)StockIconId.Shield);
        Assert.Equal(106u, (uint)StockIconId.Settings);
        Assert.Equal(140u, (uint)StockIconId.ClusteredDrive);
    }

    [Fact]
    public void StockIconOptionsHaveOfficialFlags()
    {
        Assert.True(typeof(StockIconOptions).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(0, (int)StockIconOptions.Default);
        Assert.Equal(1, (int)StockIconOptions.SmallIcon);
        Assert.Equal(4, (int)StockIconOptions.ShellIconSize);
        Assert.Equal(0x8000, (int)StockIconOptions.LinkOverlay);
        Assert.Equal(0x10000, (int)StockIconOptions.Selected);
    }

    [Fact]
    public void GetStockIconHonorsRequestedAndOptionSizes()
    {
        using Icon requested = SystemIcons.GetStockIcon(StockIconId.Folder, 19);
        using Icon anotherRequested = SystemIcons.GetStockIcon(StockIconId.Folder, 19);
        using Icon small = SystemIcons.GetStockIcon(StockIconId.Folder, StockIconOptions.SmallIcon);
        using Icon shell = SystemIcons.GetStockIcon(StockIconId.Folder, StockIconOptions.ShellIconSize);

        Assert.Equal(new Size(19, 19), requested.Size);
        Assert.Equal(new Size(16, 16), small.Size);
        Assert.Equal(new Size(32, 32), shell.Size);
        Assert.NotSame(requested, anotherRequested);
    }

    [Fact]
    public void SemanticCatalogProducesOwnedNonEmptyDistinctCategories()
    {
        using Icon folder = SystemIcons.GetStockIcon(StockIconId.Folder, 24);
        using Icon media = SystemIcons.GetStockIcon(StockIconId.MediaBluRay, 24);
        using Icon security = SystemIcons.GetStockIcon(StockIconId.Shield, 24);
        using Bitmap folderBitmap = folder.ToBitmap();
        using Bitmap mediaBitmap = media.ToBitmap();
        using Bitmap securityBitmap = security.ToBitmap();

        (int folderOpaque, int folderHash) = GetPixelSignature(folderBitmap);
        (int mediaOpaque, int mediaHash) = GetPixelSignature(mediaBitmap);
        (int securityOpaque, int securityHash) = GetPixelSignature(securityBitmap);

        Assert.True(folderOpaque > 0);
        Assert.True(mediaOpaque > 0);
        Assert.True(securityOpaque > 0);
        Assert.NotEqual(folderHash, mediaHash);
        Assert.NotEqual(mediaHash, securityHash);
        Assert.NotEqual(folderHash, securityHash);
    }

    [Fact]
    public void EveryStockIconIdProducesRenderablePixels()
    {
        foreach (StockIconId stockIcon in Enum.GetValues<StockIconId>())
        {
            using Icon icon = SystemIcons.GetStockIcon(stockIcon, 16);
            using Bitmap bitmap = icon.ToBitmap();

            Assert.True(
                GetPixelSignature(bitmap).OpaquePixels > 0,
                $"{stockIcon} did not produce visible pixels.");
        }
    }

    [Fact]
    public void OverlayAndSelectionOptionsChangeRenderedPixels()
    {
        using Icon plain = SystemIcons.GetStockIcon(StockIconId.DocumentWithAssociation);
        using Icon decorated = SystemIcons.GetStockIcon(
            StockIconId.DocumentWithAssociation,
            StockIconOptions.LinkOverlay | StockIconOptions.Selected);
        using Bitmap plainBitmap = plain.ToBitmap();
        using Bitmap decoratedBitmap = decorated.ToBitmap();

        Assert.NotEqual(GetPixelSignature(plainBitmap), GetPixelSignature(decoratedBitmap));
    }

    [Fact]
    public void WarmedStockIconCreationHasBoundedAllocation()
    {
        for (int index = 0; index < 32; index++)
        {
            using Icon warmup = SystemIcons.GetStockIcon(StockIconId.Folder, 32);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 32; index++)
        {
            using Icon icon = SystemIcons.GetStockIcon(StockIconId.Folder, 32);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The full parallel drawing suite retains approximately 31 KB per
        // operation while the isolated BenchmarkDotNet process reports 14 KB.
        // Keep this in-process ceiling above that runner overhead while still
        // rejecting a material ownership regression.
        Assert.InRange(allocated, 0, 36_000 * 32);
    }

    [Fact]
    public void GetStockIconValidatesIdentifiersOptionsAndSize()
    {
        Assert.Throws<ArgumentException>(() =>
            SystemIcons.GetStockIcon((StockIconId)14u, StockIconOptions.Default));
        Assert.Throws<ArgumentException>(() =>
            SystemIcons.GetStockIcon(StockIconId.Application, (StockIconOptions)2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SystemIcons.GetStockIcon(StockIconId.Application, 0));
    }

    [Fact]
    public void StaticSystemIconsRemainCachedAndSemanticallyDistinct()
    {
        Assert.Same(SystemIcons.Application, SystemIcons.Application);
        Assert.Same(SystemIcons.Shield, SystemIcons.Shield);
        Assert.NotSame(SystemIcons.Warning, SystemIcons.Shield);

        using Bitmap warning = SystemIcons.Warning.ToBitmap();
        using Bitmap shield = SystemIcons.Shield.ToBitmap();
        Assert.NotEqual(GetPixelSignature(warning), GetPixelSignature(shield));
    }

    private static (int OpaquePixels, int Hash) GetPixelSignature(Bitmap bitmap)
    {
        int opaque = 0;
        var hash = new HashCode();
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                opaque += color.A == 0 ? 0 : 1;
                hash.Add(color.ToArgb());
            }
        }

        return (opaque, hash.ToHashCode());
    }
}
