using System.Numerics;
using System.Reflection;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

// Synthetic generated views exercise ownership without loading a producer,
// shaping a font, admitting a target or executing any native/GPU operation.
public sealed unsafe class NativeHintedGlyphResourceLeaseTests
{
    [Fact]
    public void IndependentReferenceLeasesHoldOriginalOwnerAcrossDisposal()
    {
        int destroys = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(42, in view, handle =>
        {
            Assert.Equal((nint)42, handle);
            destroys++;
        });
        using var first = resource.AcquireReadLease();
        using var second = resource.AcquireReadLease();
        resource.Dispose();

        Assert.True(resource.IsDisposed);
        Assert.Equal(0, destroys);
        Assert.Throws<ObjectDisposedException>(() => resource.AcquireReadLease());
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Equal(0, first.FontBytes.Length);
        Assert.Equal(0, second.Glyphs.Length);

        NativeHintedGlyphResourceReadLease alias = first;
        first.Dispose();
        alias.Dispose();
        Assert.True(first.IsDisposed);
        Assert.Equal(0, destroys);
        Assert.Throws<ObjectDisposedException>(() => { _ = first.FontBytes.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = first.Counts; });
        Assert.Equal(0, second.Carets.Length);

        second.Dispose();
        second.Dispose();
        resource.Dispose();
        Assert.Equal(1, destroys);
    }

    [Fact]
    public void PublicReaderAndExistingRendererImportShareTheLifetimeCount()
    {
        int destroys = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(43, in view, _ => destroys++);
        _ = resource.AcquireForImport();
        using var lease = resource.AcquireReadLease();
        resource.Dispose();
        resource.EndImport();
        Assert.Equal(0, destroys);
        Assert.Equal(0, lease.Outlines.Length);
        lease.Dispose();
        Assert.Equal(1, destroys);
    }

    [Fact]
    public void BorrowedSpansUseOriginalPointersAndExactCachedMetadata()
    {
        byte* bytes = stackalloc byte[] { 0, 0x80, 0xff };
        int* variations = stackalloc int[] { int.MinValue, 0x12345678 };
        short* normalized = stackalloc short[] { short.MinValue, short.MaxValue };
        NativeTextScalar* pre = stackalloc NativeTextScalar[1];
        NativeTextScalar* post = stackalloc NativeTextScalar[1];
        NativeTextFeature* features = stackalloc NativeTextFeature[1];
        NativeGlyphOutline* outlines = stackalloc NativeGlyphOutline[1];
        NativeHintedGlyphOutlineOwner* outlineOwners = stackalloc NativeHintedGlyphOutlineOwner[1];
        uint* sources = stackalloc uint[] { uint.MaxValue, 0, uint.MaxValue };
        uint* runs = stackalloc uint[] { 0, uint.MaxValue };
        pre[0] = new NativeTextScalar { CodePoint = 0x628, InputIndex = 7, InputLength = 1 };
        post[0] = new NativeTextScalar { CodePoint = 0x301, InputIndex = 12, InputLength = 1 };
        features[0] = new NativeTextFeature { Tag = 0x6c696761, Value = 2, Start = 1, End = 8 };
        outlines[0] = new NativeGlyphOutline((nuint)7, (nuint)3, new Vector2(-2, 1), new Vector2(5, 8), 1.25f, -0.0f);
        outlineOwners[0] = new NativeHintedGlyphOutlineOwner { RunIndex = 4, DescriptorIndex = 9 };
        var view = new NativeMethods.HintedGlyphResourceView
        {
            DpiScale = 1.25f,
            ProjectionPolicy = (uint)NativeHintedProjectionPolicy.ScalarReference,
            Coverage = (uint)NativeHintedCoverage.NonzeroVector,
            SourceDigitBidi = 1,
            ParagraphLevel = 1,
            ShapingDirection = (uint)NativeTextDirection.RightToLeft,
            ShapingFlags = (uint)NativeTextShapeFlags.ZeroMarkAdvances,
            Result = new NativeTextParagraphResult { ContentWidth = 19.75f, ScratchBytesUsed = 1234 },
            Layout = new NativeTextLayoutOptions { Scale = 0.375f, MaximumWidth = 211.5f, Alignment = 2 },
            FontBytes = (nuint)bytes, FontByteCount = 3,
            VariationCoordinates1616 = (nuint)variations, VariationCoordinateCount = 2,
            NormalizedCoordinates = (nuint)normalized, NormalizedCoordinateCount = 2,
            PreContext = (nuint)pre, PreContextCount = 1,
            PostContext = (nuint)post, PostContextCount = 1,
            Features = (nuint)features, FeatureCount = 1,
            Outlines = (nuint)outlines, OutlineOwners = (nuint)outlineOwners, OutlineCount = 1,
            SourceOutlineIndices = (nuint)sources, SourceOutlineCount = 3,
            RunOutlineIndices = (nuint)runs, RunOutlineCount = 2,
        };
        int destroys = 0;
        using var resource = new NativeHintedGlyphResource(44, in view, _ => destroys++);
        using var lease = resource.AcquireReadLease();
        resource.Dispose();

        Assert.Equal((nuint)bytes, Address(lease.FontBytes));
        Assert.Equal((nuint)variations, Address(lease.VariationCoordinates1616));
        Assert.Equal((nuint)normalized, Address(lease.NormalizedCoordinates));
        Assert.Equal((nuint)pre, Address(lease.PreContext));
        Assert.Equal((nuint)post, Address(lease.PostContext));
        Assert.Equal((nuint)features, Address(lease.Features));
        Assert.Equal((nuint)outlines, Address(lease.Outlines));
        Assert.Equal((nuint)outlineOwners, Address(lease.OutlineOwners));
        Assert.Equal((nuint)sources, Address(lease.SourceOutlineIndices));
        Assert.Equal((nuint)runs, Address(lease.RunOutlineIndices));
        Assert.Equal((byte)0xff, lease.FontBytes[2]);
        Assert.Equal(int.MinValue, lease.VariationCoordinates1616[0]);
        Assert.Equal(short.MinValue, lease.NormalizedCoordinates[0]);
        Assert.Equal(0x628U, lease.PreContext[0].CodePoint);
        Assert.Equal(0x301U, lease.PostContext[0].CodePoint);
        Assert.Equal(2U, lease.Features[0].Value);
        Assert.Equal((nuint)7, lease.Outlines[0].SegmentOffset);
        Assert.Equal(9U, lease.OutlineOwners[0].DescriptorIndex);
        Assert.Equal(uint.MaxValue, lease.SourceOutlineIndices[2]);
        Assert.Equal(uint.MaxValue, lease.RunOutlineIndices[1]);
        Assert.Equal(BitConverter.SingleToInt32Bits(view.DpiScale), BitConverter.SingleToInt32Bits(lease.DpiScale));
        Assert.Equal(NativeHintedProjectionPolicy.ScalarReference, lease.Projection);
        Assert.Equal(NativeHintedCoverage.NonzeroVector, lease.Coverage);
        Assert.True(lease.SourceDigitBidi);
        Assert.Equal(1, lease.ParagraphLevel);
        Assert.Equal(NativeTextDirection.RightToLeft, lease.ShapingDirection);
        Assert.Equal(NativeTextShapeFlags.ZeroMarkAdvances, lease.ShapingFlags);
        Assert.Equal(view.Result.ScratchBytesUsed, lease.Result.ScratchBytesUsed);
        Assert.Equal(view.Layout.MaximumWidth, lease.Layout.MaximumWidth);
        Assert.Equal(0, destroys);
        lease.Dispose();
        Assert.Equal(1, destroys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FailedSetupPublishesNoReaderAndBalancesTheRetainedUse(int invalidRange)
    {
        NativeMethods.HintedGlyphResourceView view = default;
        switch (invalidRange)
        {
            case 0: view.FontByteCount = 1; break; // Nonempty null pointer.
            case 1: view.FontBytes = 1; view.FontByteCount = uint.MaxValue; break; // Span length cannot fit.
            case 2: view.FontBytes = nuint.MaxValue; view.FontByteCount = 1; break; // Address overflow.
            case 3: view.PostContextCount = 1; break; // Late setup failure, not only the first range.
            default: throw new ArgumentOutOfRangeException(nameof(invalidRange));
        }
        int destroys = 0;
        using var resource = new NativeHintedGlyphResource(45, in view, _ => destroys++);
        NativeHintedGlyphResourceReadLease? published = null;
        Exception error = Assert.ThrowsAny<Exception>(() => { published = resource.AcquireReadLease(); });
        if (invalidRange is 0 or 3) Assert.IsType<InvalidOperationException>(error);
        else Assert.IsType<OverflowException>(error);
        Assert.Null(published);
        Assert.False(resource.IsDisposed);
        Assert.Equal(0, destroys);
        resource.Dispose();
        Assert.Equal(1, destroys); // No abandoned reader count blocks destruction.
        resource.Dispose();
        Assert.Equal(1, destroys);
    }

    [Fact]
    public void DestroyFaultEndsTheUseOnceButRetainsOwnerForLeaseRetry()
    {
        int attempts = 0;
        var fault = new InvalidOperationException("controlled original destroy failure");
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(46, in view, handle =>
        {
            Assert.Equal((nint)46, handle);
            if (++attempts == 1) throw fault;
        });
        using var lease = resource.AcquireReadLease();
        resource.Dispose();
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => lease.Dispose()));
        Assert.True(lease.IsDisposed);
        Assert.Equal(1, attempts);
        Assert.Throws<ObjectDisposedException>(() => { _ = lease.Outlines.Length; });
        Assert.Throws<ObjectDisposedException>(() => resource.AcquireReadLease());

        lease.Dispose(); // Retry destruction, not EndImport's already-ended count.
        Assert.Equal(2, attempts);
        lease.Dispose();
        resource.Dispose();
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void OwnerCanRetryDestroyBeforeTheEndedLeaseRetries()
    {
        int attempts = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(47, in view, _ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("controlled original destroy failure");
        });
        using var lease = resource.AcquireReadLease();
        resource.Dispose();
        Assert.Throws<InvalidOperationException>(() => lease.Dispose());
        resource.Dispose();
        Assert.Equal(2, attempts);
        lease.Dispose();
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void DisposedReaderReleasesOnlyItsOwnUseWithoutRetiringOwner()
    {
        int destroys = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(48, in view, _ => destroys++);
        using var first = resource.AcquireReadLease();
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, destroys);
        Assert.False(resource.IsDisposed);
        using var second = resource.AcquireReadLease();
        Assert.Equal(0, second.FontSources.Length);
        second.Dispose();
        Assert.Equal(0, destroys);
        resource.Dispose();
        Assert.Equal(1, destroys);
    }

    [Fact]
    public void PublicReaderUsesExistingRecordsWithoutOpaqueOrMutableAdmission()
    {
        Type reader = typeof(NativeHintedGlyphResourceReadLease);
        Assert.True(reader.IsClass);
        Assert.True(reader.IsSealed);
        Assert.Empty(reader.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        PropertyInfo[] spans = reader.GetProperties().Where(property => property.PropertyType.IsGenericType &&
            property.PropertyType.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>)).ToArray();
        Assert.Equal(34, spans.Length);
        Assert.All(reader.GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(reader.GetProperties(), property => Assert.NotEqual(typeof(nint), property.PropertyType));
        Assert.All(reader.GetProperties(), property => Assert.NotEqual(typeof(nuint), property.PropertyType));
        Assert.Equal(typeof(ReadOnlySpan<NativeGlyphOutline>), reader.GetProperty(nameof(NativeHintedGlyphResourceReadLease.Outlines))!.PropertyType);
        Assert.Equal(typeof(ReadOnlySpan<NativePathSegment>), reader.GetProperty(nameof(NativeHintedGlyphResourceReadLease.Segments))!.PropertyType);
        Assert.Equal(typeof(ReadOnlySpan<NativeHintedGlyphOutlineOwner>), reader.GetProperty(nameof(NativeHintedGlyphResourceReadLease.OutlineOwners))!.PropertyType);
    }

    [Fact]
    public void SourceKeepsRetainBeforePublicationAndRetrySeparateFromEndImport()
    {
        string source = ReadSource("src", "ProGPU.Backend.Native", "NativeTextHintedGlyphResourceInterop.cs");
        Assert.Contains("private static readonly Action<nint> NativeDestroy = NativeMethods.DestroyHintedGlyphResource;", source, StringComparison.Ordinal);
        int acquire = source.IndexOf("public NativeHintedGlyphResourceReadLease AcquireReadLease()", StringComparison.Ordinal);
        int retained = source.IndexOf("view = AcquireForImport();", acquire, StringComparison.Ordinal);
        int publication = source.IndexOf("return new NativeHintedGlyphResourceReadLease", acquire, StringComparison.Ordinal);
        Assert.True(retained >= acquire && publication > retained);
        Assert.Contains("catch (Exception error)\n        {\n            try { EndImport(); }", source, StringComparison.Ordinal);
        int validated = source.IndexOf("ValidateRanges(in view);", publication, StringComparison.Ordinal);
        int ownerPublication = source.IndexOf("_owner = owner;", validated, StringComparison.Ordinal);
        Assert.True(validated > publication && ownerPublication > validated);
        int ended = source.IndexOf("Volatile.Write(ref _ended, true);", StringComparison.Ordinal);
        int endImport = source.IndexOf("owner.EndImport();", ended, StringComparison.Ordinal);
        int retry = source.IndexOf("owner.RetryRelease();", endImport, StringComparison.Ordinal);
        int clearOwner = source.IndexOf("_owner = null;", retry, StringComparison.Ordinal);
        Assert.True(ended >= 0 && endImport > ended && retry > endImport && clearOwner > retry);
        Assert.Contains("return new ReadOnlySpan<T>((void*)pointer, checked((int)count));", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToArray(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeSceneGlyphOutline", source, StringComparison.Ordinal);
    }

    private static nuint Address<T>(ReadOnlySpan<T> span) where T : unmanaged
    {
        fixed (T* pointer = span) return (nuint)pointer;
    }

    private static string ReadSource(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        throw new FileNotFoundException($"Could not locate {Path.Combine(parts)}.");
    }
}
