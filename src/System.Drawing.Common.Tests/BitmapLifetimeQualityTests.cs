using System.Drawing;
using System.Runtime.CompilerServices;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class BitmapLifetimeQualityTests
{
    [Fact]
    public void DisposeToleratesAnObjectWhoseConstructorDidNotInitializeDerivedFields()
    {
        var bitmap = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));

        bitmap.Dispose();
    }
}
