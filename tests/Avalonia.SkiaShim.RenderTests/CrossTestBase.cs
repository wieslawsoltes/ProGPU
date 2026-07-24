#nullable enable

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Skia.RenderTests.CrossUI;
using CrossUI;
using Xunit;

namespace Avalonia.Skia.RenderTests;

class CrossFactAttribute : FactAttribute
{
    public CrossFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
    }
}

class CrossTheoryAttribute : TheoryAttribute
{
    public CrossTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
    }
}

public class CrossTestBase : IDisposable
{
    private readonly string _groupName;

    public CrossTestBase(string groupName)
    {
        TestRenderHelper.BeginTest();
        _groupName = groupName;
    }

    protected void RenderAndCompare(
        CrossControl root,
        [CallerMemberName] string? testName = null,
        double dpi = 96)
    {
        ArgumentException.ThrowIfNullOrEmpty(testName, nameof(testName));
        var directory = Path.Combine(
            TestRenderHelper.GetTestsDirectory(),
            "TestFiles",
            "CrossTests",
            _groupName);
        Directory.CreateDirectory(directory);

        var pathBase = Path.Combine(directory, testName);
        var renderPath = pathBase + ".skiashim.out.png";
        var expectedPath = pathBase + ".wpf.png";
        var control = new AvaloniaCrossControl(root);
        TestRenderHelper.RenderToFile(control, renderPath, false, dpi);
        TestRenderHelper.AssertCompareImages(renderPath, expectedPath);
    }

    public void Dispose()
    {
        TestRenderHelper.EndTest();
    }
}
