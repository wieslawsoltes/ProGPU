namespace ProGPU.Tests;

using Xunit;

public class DiagnosticsLoggingSourceTests
{
    [Theory]
    [InlineData("src", "ProGPU.Backend", "WgpuContext.cs", "ProGpuBackendDiagnostics.WriteLine(", "Configuring SwapChain", "Console.WriteLine($\"[WebGPU Context] Configuring SwapChain")]
    [InlineData("src", "ProGPU.Scene", "Extensions/ShaderToyExtensionPipeline.cs", "ProGpuSceneDiagnostics.WriteLine(", "ShaderToy Render", "Console.WriteLine(")]
    [InlineData("src", "ProGPU.Text", "TextLayout.cs", "ProGpuTextDiagnostics.WriteLine(", "Loaded system fallback font", "Console.WriteLine($\"[TextLayout]")]
    [InlineData("src", "ProGPU.Text", "GlyphAtlas.cs", "ProGpuTextDiagnostics.WriteLine(", "GlyphAtlas", "Console.WriteLine(\"[GlyphAtlas]")]
    [InlineData("src", "ProGPU.Vector", "PathAtlas.cs", "ProGpuVectorDiagnostics.WriteLine(", "PathAtlas", "Console.WriteLine(\"[PathAtlas]")]
    [InlineData("src", "ProGPU.WinUI", "Input/InputSystem.cs", "ProGpuWinUiDiagnostics.WriteLine(", "MouseDown at", "Console.WriteLine($\"[InputSystem]")]
    [InlineData("src", "ProGPU.WinUI", "Controls/MarkdownParser.cs", "ProGpuWinUiDiagnostics.WriteLine(", "Clicked hyperlink Uri", "Console.WriteLine($\"[MarkdownParser] Clicked hyperlink")]
    public void NormalLifecycleDiagnosticsStayOptIn(
        string root,
        string project,
        string fileName,
        string expectedDiagnosticsCall,
        string diagnosticMessage,
        string forbiddenDirectConsoleCall)
    {
        string source = File.ReadAllText(FindRepoFile(root, project, fileName));

        Assert.Contains(expectedDiagnosticsCall, source, StringComparison.Ordinal);
        Assert.Contains(diagnosticMessage, source, StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenDirectConsoleCall, source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src", "ProGPU.Backend", "ProGpuBackendDiagnostics.cs", "PROGPU_BACKEND_DIAGNOSTICS")]
    [InlineData("src", "ProGPU.Scene", "ProGpuSceneDiagnostics.cs", "PROGPU_SCENE_DIAGNOSTICS")]
    [InlineData("src", "ProGPU.Text", "ProGpuTextDiagnostics.cs", "PROGPU_TEXT_DIAGNOSTICS")]
    [InlineData("src", "ProGPU.Vector", "ProGpuVectorDiagnostics.cs", "PROGPU_VECTOR_DIAGNOSTICS")]
    [InlineData("src", "ProGPU.WinUI", "Core/ProGpuWinUiDiagnostics.cs", "PROGPU_WINUI_DIAGNOSTICS")]
    public void DiagnosticsHelpersKeepConsoleOutputBehindEnvironmentSwitch(
        string root,
        string project,
        string fileName,
        string environmentVariable)
    {
        string source = File.ReadAllText(FindRepoFile(root, project, fileName));

        Assert.Contains(environmentVariable, source, StringComparison.Ordinal);
        Assert.Contains("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(message)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositorTransientStateSnapshotsUseArrayPool()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Compositor.cs"));

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("private static T[] RentStackSnapshot<T>(Stack<T> stack, out int count)", source, StringComparison.Ordinal);
        Assert.Contains("private static T[] RentListSnapshot<T>(List<T> list, out int count)", source, StringComparison.Ordinal);
        Assert.Contains("CollectionsMarshal.SetCount(list, count)", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.IsReferenceOrContainsReferences<T>()", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(count)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Return(snapshot)", source, StringComparison.Ordinal);
        Assert.Contains("RentStackSnapshot(_clipStack", source, StringComparison.Ordinal);
        Assert.Contains("RentStackSnapshot(_clipScopeIsGeometryMask", source, StringComparison.Ordinal);
        Assert.Contains("RentStackSnapshot(_opacityStack", source, StringComparison.Ordinal);
        Assert.Contains("RentStackSnapshot(_blendModeStack", source, StringComparison.Ordinal);
        Assert.Contains("RentStackSnapshot(_maskStack", source, StringComparison.Ordinal);
        Assert.Contains("RentListSnapshot(_vectorVerticesList", source, StringComparison.Ordinal);
        Assert.Contains("RentListSnapshot(_vectorIndicesList", source, StringComparison.Ordinal);
        Assert.Contains("RentListSnapshot(_textVerticesList", source, StringComparison.Ordinal);
        Assert.Contains("RentListSnapshot(_drawCalls", source, StringComparison.Ordinal);
        Assert.Contains("ReturnListSnapshot(savedVectorVertices", source, StringComparison.Ordinal);
        Assert.Contains("private List<CompositorDrawCall> RentMaskDrawCallList(int capacity)", source, StringComparison.Ordinal);
        Assert.Contains("private void ReturnMaskRenderPassDrawCallLists()", source, StringComparison.Ordinal);
        Assert.Contains("ReturnMaskRenderPassDrawCallLists();", source, StringComparison.Ordinal);
        Assert.Contains("RentMaskDrawCallList(maskDrawCallCount)", source, StringComparison.Ordinal);
        Assert.Contains("private static void AddRemovalItem<T>(ref T[]? buffer, ref int count, int capacity, T item)", source, StringComparison.Ordinal);
        Assert.Contains("private static void ReturnRemovalBuffer<T>(T[]? buffer, int count)", source, StringComparison.Ordinal);
        Assert.Contains("AddRemovalItem(ref keysToRemove", source, StringComparison.Ordinal);
        Assert.Contains("AddRemovalItem(ref detached", source, StringComparison.Ordinal);
        Assert.Contains("AddRemovalItem(ref stale", source, StringComparison.Ordinal);
        Assert.Contains("private void DisposeMaskTexturePool()", source, StringComparison.Ordinal);
        Assert.Contains("var pooledMaskTextures = RentListSnapshot(_maskTexturePool", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_clipStack.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_clipScopeIsGeometryMask.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_opacityStack.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_blendModeStack.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_maskStack.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var savedVectorVertices = _vectorVerticesList.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var dxfSavedVectorVertices = _vectorVerticesList.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var savedTextVertices = _textVerticesList.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var dxfSavedTextVertices = _textVerticesList.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var savedDrawCalls = _drawCalls.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var dxfSavedDrawCalls = _drawCalls.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var savedMaskRenderPasses = _maskRenderPasses.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var dxfSavedMaskRenderPasses = _maskRenderPasses.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var maskDrawCalls = new List<CompositorDrawCall>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("keysToRemove ??= new List<TextureCacheKey>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("keysToRemove ??= new List<GpuTexture>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("detached ??= new List<Visual>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("stale ??= new List<Visual>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_maskTexturePool.ToArray()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualOwnerNotificationsUsePooledSnapshots()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Visual.cs"));

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("Visual[]? owners = null;", source, StringComparison.Ordinal);
        Assert.Contains("owners = ArrayPool<Visual>.Shared.Rent(Math.Max(4, _owners.Count));", source, StringComparison.Ordinal);
        Assert.Contains("Visual[] expandedOwners = ArrayPool<Visual>.Shared.Rent(owners.Length * 2);", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<Visual>.Shared.Return(owners, clearArray: true);", source, StringComparison.Ordinal);
        Assert.Contains("owners![i].Invalidate();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<Visual>? owners", source, StringComparison.Ordinal);
        Assert.DoesNotContain("owners ??= new List<Visual>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("owners.Add(owner);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var owner in owners)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HitTestCacheBuildUsesListSpansWithoutTemporaryArrays()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "GpuRenderCommandHitTestCache.cs"));

        Assert.Contains("using System.Runtime.InteropServices;", source, StringComparison.Ordinal);
        Assert.Contains("CollectionsMarshal.AsSpan(_primitives)", source, StringComparison.Ordinal);
        Assert.Contains("CollectionsMarshal.AsSpan(_pathSegments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_primitives.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_pathSegments.ToArray()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectExtensionCacheCleanupUsesPooledRemovalBuffers()
    {
        string helper = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "PooledRemovalBuffer.cs"));
        string wpfShaderEffect = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "WpfShaderEffectExtensionPipeline.cs"));
        string imageEffect = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "ImageEffectExtensionPipeline.cs"));

        Assert.Contains("internal static class PooledRemovalBuffer", helper, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(Math.Max(1, capacity))", helper, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.IsReferenceOrContainsReferences<T>()", helper, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Return(buffer)", helper, StringComparison.Ordinal);

        Assert.Contains("string[]? keysToRemove = null;", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Add(ref keysToRemove", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Return(keysToRemove, keysToRemoveCount)", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("List<string>? keysToRemove", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("keysToRemove ??= new List<string>();", wpfShaderEffect, StringComparison.Ordinal);

        Assert.Contains("Compositor.TextureCacheKey[]? keysToRemove = null;", imageEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Add(ref keysToRemove", imageEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Return(keysToRemove, keysToRemoveCount)", imageEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("List<Compositor.TextureCacheKey>? keysToRemove", imageEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("keysToRemove ??= new List<Compositor.TextureCacheKey>();", imageEffect, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(pathParts)}.");
    }
}
