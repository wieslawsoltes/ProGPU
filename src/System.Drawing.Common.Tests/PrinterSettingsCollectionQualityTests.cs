using System.Collections;
using System.Drawing.Printing;
using Xunit;

namespace System.Drawing.Tests;

public sealed class PrinterSettingsCollectionQualityTests
{
    [Fact]
    public void CollectionTypesHaveOfficialBaseAndInterfaceShapes()
    {
        Assert.Equal(typeof(object), typeof(PrinterSettings.StringCollection).BaseType);
        Assert.Equal(typeof(object), typeof(PrinterSettings.PaperSizeCollection).BaseType);
        Assert.Equal(typeof(object), typeof(PrinterSettings.PaperSourceCollection).BaseType);
        Assert.Equal(typeof(object), typeof(PrinterSettings.PrinterResolutionCollection).BaseType);

        Assert.False(typeof(PrinterSettings.StringCollection).IsSealed);
        Assert.False(typeof(PrinterSettings.PaperSizeCollection).IsSealed);
        Assert.False(typeof(PrinterSettings.PaperSourceCollection).IsSealed);
        Assert.False(typeof(PrinterSettings.PrinterResolutionCollection).IsSealed);

        Assert.True(typeof(ICollection).IsAssignableFrom(typeof(PrinterSettings.StringCollection)));
        Assert.True(typeof(IEnumerable<string>).IsAssignableFrom(typeof(PrinterSettings.StringCollection)));
        Assert.True(typeof(ICollection).IsAssignableFrom(typeof(PrinterSettings.PaperSizeCollection)));
        Assert.True(typeof(ICollection).IsAssignableFrom(typeof(PrinterSettings.PaperSourceCollection)));
        Assert.True(typeof(ICollection).IsAssignableFrom(typeof(PrinterSettings.PrinterResolutionCollection)));
    }

    [Fact]
    public void StringCollectionSnapshotsAddsCopiesAndEnumerates()
    {
        string[] source = ["First", "Second"];
        var collection = new PrinterSettings.StringCollection(source);
        source[0] = "Changed";

        Assert.Equal(2, collection.Count);
        Assert.Equal("First", collection[0]);
        Assert.Equal(2, collection.Add("Third"));
        Assert.True(collection.Contains("Second"));
        Assert.Equal(1, collection.IndexOf("Second"));
        Assert.Equal(["First", "Second", "Third"], collection.ToArray());

        var copy = new string[4];
        collection.CopyTo(copy, 1);
        Assert.Null(copy[0]);
        Assert.Equal(["First", "Second", "Third"], copy[1..]);

        ICollection nonGeneric = collection;
        Assert.False(nonGeneric.IsSynchronized);
        Assert.Same(collection, nonGeneric.SyncRoot);
    }

    [Fact]
    public void PaperCollectionsSnapshotAddCopyAndEnumerate()
    {
        var letter = new PaperSize("Letter", 850, 1100);
        var legal = new PaperSize("Legal", 850, 1400);
        PaperSize[] sizes = [letter];
        var sizeCollection = new PrinterSettings.PaperSizeCollection(sizes);
        sizes[0] = legal;
        Assert.Same(letter, sizeCollection[0]);
        Assert.Equal(1, sizeCollection.Add(legal));
        Assert.True(sizeCollection.Contains(legal));
        Assert.Equal(1, sizeCollection.IndexOf(legal));

        var tray = new PaperSource { SourceName = "Tray" };
        var manual = new PaperSource { SourceName = "Manual" };
        var sourceCollection = new PrinterSettings.PaperSourceCollection([tray]);
        Assert.Equal(1, sourceCollection.Add(manual));
        Assert.Equal([tray, manual], sourceCollection.Cast<PaperSource>());

        var low = new PrinterResolution { X = 150, Y = 150 };
        var high = new PrinterResolution { X = 600, Y = 600 };
        var resolutionCollection = new PrinterSettings.PrinterResolutionCollection([low]);
        Assert.Equal(1, resolutionCollection.Add(high));
        var copied = new PrinterResolution[2];
        resolutionCollection.CopyTo(copied, 0);
        Assert.Equal([low, high], copied);
    }

    [Fact]
    public void CollectionsValidateNullConstructionAndAdditions()
    {
        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.StringCollection(null!));
        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.PaperSizeCollection(null!));
        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.PaperSourceCollection(null!));
        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.PrinterResolutionCollection(null!));

        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.StringCollection([]).Add(null!));
        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.PaperSizeCollection([]).Add(null!));
        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.PaperSourceCollection([]).Add(null!));
        Assert.Throws<ArgumentNullException>(() => new PrinterSettings.PrinterResolutionCollection([]).Add(null!));
    }

    [Fact]
    public void PortableInstalledPrinterSnapshotsCannotMutateGlobalState()
    {
        PrinterSettings.StringCollection first = PrinterSettings.InstalledPrinters;
        PrinterSettings.StringCollection second = PrinterSettings.InstalledPrinters;

        Assert.NotSame(first, second);
        Assert.Empty(first);
        Assert.Empty(second);
        first.Add("Local test printer");
        Assert.Empty(PrinterSettings.InstalledPrinters);
    }

    [Fact]
    public void WarmedIndexReadsAllocateNothing()
    {
        var collection = new PrinterSettings.PaperSizeCollection(
            [new PaperSize("Letter", 850, 1100)]);
        _ = collection[0];

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100_000; index++)
        {
            _ = collection[0];
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
