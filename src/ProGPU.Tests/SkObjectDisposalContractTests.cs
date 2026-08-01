using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkObjectDisposalContractTests
{
    [Fact]
    public void OfficialOwnershipTypesDeclareTheirProtectedDisposalHooks()
    {
        AssertOverride<SKAbstractManagedStream>("Dispose", typeof(bool));
        AssertOverride<SKAbstractManagedStream>("DisposeNative");
        AssertOverride<SKAbstractManagedWStream>("Dispose", typeof(bool));
        AssertOverride<SKAbstractManagedWStream>("DisposeNative");
        AssertOverride<SKBitmap>("DisposeNative");
        AssertOverride<SKCodec>("DisposeNative");
        AssertOverride<SKColorFilter>("Dispose", typeof(bool));
        AssertOverride<SKColorSpace>("Dispose", typeof(bool));
        AssertOverride<SKColorSpaceIccProfile>("DisposeNative");
        AssertOverride<SKDrawable>("Dispose", typeof(bool));
        AssertOverride<SKDrawable>("DisposeNative");
        AssertOverride<SKDynamicMemoryWStream>("DisposeNative");
        AssertOverride<SKFileStream>("Dispose", typeof(bool));
        AssertOverride<SKFileStream>("DisposeNative");
        AssertOverride<SKFileWStream>("DisposeNative");
        AssertOverride<SKFontStyle>("Dispose", typeof(bool));
        AssertOverride<SKFontStyle>("DisposeNative");
        AssertOverride<SKImageFilter>("Dispose", typeof(bool));
        AssertOverride<SKManagedStream>("Dispose", typeof(bool));
        AssertOverride<SKManagedWStream>("DisposeManaged");
        AssertOverride<SKPaint>("DisposeNative");
        AssertOverride<SKPathEffect>("Dispose", typeof(bool));
        AssertOverride<SKPathMeasure>("Dispose", typeof(bool));
        AssertOverride<SKPathMeasure>("DisposeNative");
        AssertOverride<SKPicture>("Dispose", typeof(bool));
        AssertOverride<SKPictureRecorder>("Dispose", typeof(bool));
        AssertOverride<SKPictureRecorder>("DisposeNative");
        AssertOverride<SKSurfaceProperties>("Dispose", typeof(bool));
        AssertOverride<SKSurfaceProperties>("DisposeNative");
        AssertOverride<SKTextBlob>("Dispose", typeof(bool));
        AssertOverride<SKTextBlobBuilder>("DisposeNative");
    }

    [Fact]
    public void DrawableOwnsConstructorPreservesInheritedLifetimeContract()
    {
        var constructor = typeof(SKDrawable).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool)],
            modifiers: null);

        Assert.NotNull(constructor);
        Assert.True(constructor!.IsFamily);

        var drawable = new BorrowedDrawable();
        Assert.False(drawable.OwnsNativeHandle);
        Assert.NotEqual(IntPtr.Zero, drawable.Handle);

        drawable.Dispose();
        Assert.Equal(IntPtr.Zero, drawable.Handle);
    }

    private static void AssertOverride<T>(string name, params Type[] parameters)
    {
        var method = typeof(T).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            parameters,
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(T), method!.DeclaringType);
        Assert.True(method.IsVirtual);
        Assert.False(method.IsFinal);
    }

    private sealed class BorrowedDrawable : SKDrawable
    {
        public BorrowedDrawable()
            : base(owns: false)
        {
        }

        public bool OwnsNativeHandle => OwnsHandle;
    }
}
