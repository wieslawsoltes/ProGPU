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
    public void WgpuContextPendingResourceCleanupUsesPooledSnapshots()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "WgpuContext.cs"));

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("private readonly HashSet<IntPtr> _pendingSnapshotSeen = new();", source, StringComparison.Ordinal);
        Assert.Contains("PooledResourcePointerSnapshot buffers = default;", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<IntPtr>.Shared.Rent(pending.Count)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<IntPtr>.Shared.Return(snapshot)", source, StringComparison.Ordinal);
        Assert.Contains("private readonly struct PooledResourcePointerSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("public ReadOnlySpan<IntPtr> Span", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var bg in bindGroups.Span)", source, StringComparison.Ordinal);
        Assert.Contains("finally\n            {\n                buffers.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var snapshot = new List<IntPtr>(pending.Count)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return snapshot.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var seen = new HashSet<IntPtr>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var bg in bindGroups)\n", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WgpuContextFirstActiveLookupAvoidsSnapshotArrays()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "WgpuContext.cs"));
        string gpuHitTesting = File.ReadAllText(FindRepoFile("src", "ProGPU.Vector", "GpuHitTesting.cs"));
        string pathOps = File.ReadAllText(FindRepoFile("src", "ProGPU.Vector", "PathOpGeometrySolver.cs"));
        string presentationCoreGpuProvider = File.ReadAllText(FindRepoFile("src", "PresentationCore", "GpuProvider.cs"));
        string systemDrawingGpuProvider = File.ReadAllText(FindRepoFile("src", "System.Drawing.Common", "GpuProvider.cs"));
        string skiaSharp = File.ReadAllText(FindRepoFile("src", "SkiaSharp", "SkiaSharp.cs"));
        string avaloniaHost = File.ReadAllText(FindRepoFile("src", "ProGPU.Avalonia", "ProGpuHostControl.cs"));

        Assert.Contains("using System.Diagnostics.CodeAnalysis;", source, StringComparison.Ordinal);
        Assert.Contains("public static bool TryGetFirstActiveContext([NotNullWhen(true)] out WgpuContext? context)", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < _activeContexts.Count; i++)", source, StringComparison.Ordinal);
        Assert.Contains("var active = _activeContexts[i];", source, StringComparison.Ordinal);
        Assert.Contains("context = active;", source, StringComparison.Ordinal);
        Assert.Contains("return _activeContexts.ToArray();", source, StringComparison.Ordinal);

        Assert.Contains("WgpuContext.TryGetFirstActiveContext(out var activeContext)", gpuHitTesting, StringComparison.Ordinal);
        Assert.DoesNotContain("var activeContexts = WgpuContext.ActiveContexts;", gpuHitTesting, StringComparison.Ordinal);
        Assert.Contains("WgpuContext.TryGetFirstActiveContext(out var activeContext)", pathOps, StringComparison.Ordinal);
        Assert.DoesNotContain("var active = WgpuContext.ActiveContexts;", pathOps, StringComparison.Ordinal);
        Assert.Contains("WgpuContext.TryGetFirstActiveContext(out var active)", presentationCoreGpuProvider, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var active in WgpuContext.ActiveContexts)", presentationCoreGpuProvider, StringComparison.Ordinal);
        Assert.Contains("WgpuContext.TryGetFirstActiveContext(out var active)", systemDrawingGpuProvider, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var active in WgpuContext.ActiveContexts)", systemDrawingGpuProvider, StringComparison.Ordinal);
        Assert.Contains("WgpuContext.TryGetFirstActiveContext(out var ctx)", skiaSharp, StringComparison.Ordinal);
        Assert.DoesNotContain("var active = WgpuContext.ActiveContexts;", skiaSharp, StringComparison.Ordinal);
        Assert.Contains("WgpuContext.TryGetFirstActiveContext(out var context);", avaloniaHost, StringComparison.Ordinal);
        Assert.DoesNotContain("var active = WgpuContext.ActiveContexts;", avaloniaHost, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositorTransientStateSnapshotsUseArrayPool()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Compositor.cs"));
        string seriesBuffer = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "GpuSeriesBuffer.cs"));
        string acisPipeline = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "AcisSolidExtensionPipeline.cs"));
        string lineSeriesPipeline = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "GpuLineSeriesExtensionPipeline.cs"));
        string scatterSeriesPipeline = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "GpuScatterSeriesExtensionPipeline.cs"));

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
        Assert.Contains("private readonly Stack<List<CompositorDrawCall>> _drawCallListPool = new();", source, StringComparison.Ordinal);
        Assert.Contains("private List<CompositorDrawCall> RentDrawCallList(int capacity)", source, StringComparison.Ordinal);
        Assert.Contains("private List<CompositorDrawCall> RentMaskDrawCallList(int capacity)", source, StringComparison.Ordinal);
        Assert.Contains("private void ReturnMaskRenderPassDrawCallLists()", source, StringComparison.Ordinal);
        Assert.Contains("ReturnMaskRenderPassDrawCallLists();", source, StringComparison.Ordinal);
        Assert.Contains("RentMaskDrawCallList(maskDrawCallCount)", source, StringComparison.Ordinal);
        Assert.Contains("var staticDrawCallList = RentDrawCallList(commands.Count)", source, StringComparison.Ordinal);
        Assert.Contains("var staticDrawCallList = RentDrawCallList(context.Commands.Count)", source, StringComparison.Ordinal);
        Assert.Contains("ReturnDrawCallList(staticDrawCalls)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var staticDrawCalls = new List<CompositorDrawCall>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_maskDrawCallListPool", source, StringComparison.Ordinal);
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
        Assert.Contains("public void Upload(ReadOnlySpan<float> interleavedCoords, int pointsCount)", seriesBuffer, StringComparison.Ordinal);
        Assert.Contains("Buffer.Write(interleavedCoords)", seriesBuffer, StringComparison.Ordinal);
        Assert.Contains("cachedBuffer.Upload(cachedBuffer.CachedInterleaved.AsSpan(0, requiredLength), pointsCount)", source, StringComparison.Ordinal);
        Assert.Contains("cachedBuffer.Upload(cachedBuffer.CachedInterleaved.AsSpan(0, requiredLength), pointsCount)", lineSeriesPipeline, StringComparison.Ordinal);
        Assert.Contains("cachedBuffer.Upload(cachedBuffer.CachedInterleaved.AsSpan(0, requiredLength), pointsCount)", scatterSeriesPipeline, StringComparison.Ordinal);
        Assert.Contains("tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 2), pointsCount)", source, StringComparison.Ordinal);
        Assert.Contains("tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 3), pointsCount)", source, StringComparison.Ordinal);
        Assert.Contains("var array = ArrayPool<float>.Shared.Rent(pointsCount * 3)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<float>.Shared.Return(array)", source, StringComparison.Ordinal);
        Assert.Contains("tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 2), pointsCount)", lineSeriesPipeline, StringComparison.Ordinal);
        Assert.Contains("tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 3), pointsCount)", scatterSeriesPipeline, StringComparison.Ordinal);
        Assert.Contains("var array = ArrayPool<float>.Shared.Rent(pointsCount * 3)", scatterSeriesPipeline, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<float>.Shared.Return(array)", scatterSeriesPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("var array = new float[pointsCount * 2];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var array = new float[pointsCount * 3];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var array = new float[pointsCount * 2];", lineSeriesPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("var array = new float[pointsCount * 3];", scatterSeriesPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("cachedBuffer.Upload(cachedBuffer.CachedInterleaved, pointsCount)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cachedBuffer.Upload(cachedBuffer.CachedInterleaved, pointsCount)", lineSeriesPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("cachedBuffer.Upload(cachedBuffer.CachedInterleaved, pointsCount)", scatterSeriesPipeline, StringComparison.Ordinal);
        Assert.Contains("private static void AddDashedLineFigures(", source, StringComparison.Ordinal);
        Assert.Contains("DashPattern.Advance(\n                intervals,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pattern.TryCreateLineSegments(", source, StringComparison.Ordinal);
        Assert.Contains("cmd.Edges3D is { } edges", acisPipeline, StringComparison.Ordinal);
        Assert.Contains("ReadOnlySpan<Line3D>.Empty", acisPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.Edges3D ?? new List<Line3D>()", acisPipeline, StringComparison.Ordinal);
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
    public void GpuHitTestIndexBuilderUsesPooledPrimitiveBuckets()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.Vector", "GpuHitTesting.cs"));

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("PrimitiveIndexBucket retained = default;", source, StringComparison.Ordinal);
        Assert.Contains("PrimitiveIndexBucket child0 = default;", source, StringComparison.Ordinal);
        Assert.Contains("AddChildPrimitive(ref child0, ref child1, ref child2, ref child3, childIndex, primitiveIndex)", source, StringComparison.Ordinal);
        Assert.Contains("private struct PrimitiveIndexBucket : IPrimitiveIndexSource, IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<int>.Shared.Rent(InitialCapacity)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<int>.Shared.Rent(items.Length * 2)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<int>.Shared.Return(items)", source, StringComparison.Ordinal);
        Assert.Contains("CountNonEmpty(in child0, in child1, in child2, in child3)", source, StringComparison.Ordinal);
        Assert.Contains("AddChildNodeSlot(in child0)", source, StringComparison.Ordinal);
        Assert.Contains("FillChildNode(child0NodeIndex, 0, in child0, min, max, center, depth);", source, StringComparison.Ordinal);
        Assert.Contains("retained.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("child0.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("private readonly struct RootPrimitiveIndices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<int>? retained", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<int>? child0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("retained ??= [];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly struct ListPrimitiveIndices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ListPrimitiveIndices(childPrimitives)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<int>", source, StringComparison.Ordinal);
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

    [Fact]
    public void EffectPipelineLayoutsUseStackBackedDescriptors()
    {
        string wpfShaderEffect = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "WpfShaderEffectExtensionPipeline.cs"));
        string imageEffect = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "ImageEffectExtensionPipeline.cs"));
        string shaderToy = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "ShaderToyExtensionPipeline.cs"));
        string line3D = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "Line3DExtensionPipeline.cs"));
        string customGrid = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "CustomGridExtensionPipeline.cs"));
        string acisSolid = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "AcisSolidExtensionPipeline.cs"));
        string hatch = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "HatchExtensionPipeline.cs"));
        string mesh3D = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", "Mesh3DExtensionPipeline.cs"));
        string pipelineCache = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "RenderPipelineCache.cs"));

        AssertStackBackedLayout(wpfShaderEffect, 3, "VectorVertex");
        AssertStackBackedLayout(imageEffect, 3, "VectorVertex");
        AssertStackBackedLayout(shaderToy, 3, "VectorVertex");
        AssertStackBackedLayout(line3D, 8, "VectorVertex");
        AssertStackBackedLayout(customGrid, 8, "VectorVertex");
        AssertStackBackedLayout(acisSolid, 8, "VectorVertex");
        AssertStackBackedLayout(hatch, 8, "VectorVertex");
        AssertStackBackedLayout(mesh3D, 2, "GpuVertex3D");

        Assert.Contains("ReadOnlySpan<VertexBufferLayout> vertexBufferLayouts", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("fixed (VertexBufferLayout* pLayouts = vertexBufferLayouts)", pipelineCache, StringComparison.Ordinal);

        static void AssertStackBackedLayout(string source, int attributeCount, string vertexType)
        {
            Assert.Contains($"Span<VertexAttribute> attrs = stackalloc VertexAttribute[{attributeCount}];", source, StringComparison.Ordinal);
            Assert.Contains("Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];", source, StringComparison.Ordinal);
            Assert.Contains($"ArrayStride = (uint)Unsafe.SizeOf<{vertexType}>()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new VertexBufferLayout[]", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Marshal.AllocHGlobal(Marshal.SizeOf<VertexAttribute>()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes)", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PathAtlasCleanupUsesPooledRemovalBuffers()
    {
        string helper = File.ReadAllText(FindRepoFile("src", "ProGPU.Vector", "PooledRemovalBuffer.cs"));
        string pathAtlas = File.ReadAllText(FindRepoFile("src", "ProGPU.Vector", "PathAtlas.cs"));

        Assert.Contains("internal static class PooledRemovalBuffer", helper, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(Math.Max(1, capacity))", helper, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.IsReferenceOrContainsReferences<T>()", helper, StringComparison.Ordinal);

        Assert.Contains("PathInfo[]? activePaths = null;", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Add(ref activePaths", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Return(activePaths, activePathCount)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("var activePaths = new List<PathInfo>();", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("private void ClearAtlasTexture()", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("byte[] clearData = ArrayPool<byte>.Shared.Rent(clearByteCount);", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("clearData.AsSpan(0, clearByteCount).Clear();", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Return(clearData)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("byte[] clearData = new byte[_atlasSize * _atlasSize * 4];", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("nint[]? bindGroupsToRelease = null;", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Add(ref bindGroupsToRelease", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Return(bindGroupsToRelease, bindGroupToReleaseCount)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("nint[]? layoutsToRelease = null;", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Add(ref layoutsToRelease", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Return(layoutsToRelease, layoutToReleaseCount)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("var bindGroupsToRelease = new List<nint>();", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("var layoutsToRelease = new List<nint>();", pathAtlas, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectXBufferReadbackUsesCallerOwnedBuffers()
    {
        string gpuBuffer = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "GpuBuffer.cs"));
        string resources = File.ReadAllText(FindRepoFile("src", "ProGPU.DirectX", "ProGpuDirectXResources.cs"));
        string deviceContext = File.ReadAllText(FindRepoFile("src", "ProGPU.DirectX", "ProGpuDirectXDeviceContext.cs"));

        Assert.Contains("public void ReadBytes(Span<byte> destination, uint offsetBytes = 0)", gpuBuffer, StringComparison.Ordinal);
        Assert.Contains("ReadBytes(bytes, offsetBytes);", gpuBuffer, StringComparison.Ordinal);
        Assert.Contains("MapReadBuffer(BufferPtr, mappedRange.OffsetBytes, mappedRange.SizeBytes, mappedRange.LeadingBytes, destination", gpuBuffer, StringComparison.Ordinal);
        Assert.Contains("MapReadBuffer(readbackBuffer, 0, copyRange.SizeBytes, copyRange.LeadingBytes, destination", gpuBuffer, StringComparison.Ordinal);
        Assert.Contains("new ReadOnlySpan<byte>(", gpuBuffer, StringComparison.Ordinal);
        Assert.DoesNotContain("private byte[] MapReadBuffer", gpuBuffer, StringComparison.Ordinal);
        Assert.DoesNotContain("return mappedBytes.AsSpan", gpuBuffer, StringComparison.Ordinal);
        Assert.DoesNotContain("return readbackBytes.AsSpan", gpuBuffer, StringComparison.Ordinal);

        Assert.Contains("public void ReadBytes(Span<byte> destination, uint offsetBytes = 0)", resources, StringComparison.Ordinal);
        Assert.Contains("public unsafe void Read<T>(Span<T> destination, uint offsetBytes = 0)", resources, StringComparison.Ordinal);
        Assert.Contains("_backendBuffer.ReadBytes(destination, offsetBytes);", resources, StringComparison.Ordinal);
        Assert.Contains("_backendBuffer.ReadBytes(writeShadowSpan, offsetBytes);", resources, StringComparison.Ordinal);
        Assert.Contains("internal void ReadWriteShadowBytes(Span<byte> destination, uint offsetBytes)", resources, StringComparison.Ordinal);
        Assert.DoesNotContain("var bytes = _backendBuffer.ReadBytes(offsetBytes, sizeInBytes);", resources, StringComparison.Ordinal);

        Assert.Contains("using System.Buffers;", deviceContext, StringComparison.Ordinal);
        Assert.Contains("sourceIndexBuffer.ReadWriteShadowBytes(sourceBytes, offsetBytes);", deviceContext, StringComparison.Ordinal);
        Assert.Contains("sourceIndexBuffer.ReadWriteShadowBytes(MemoryMarshal.AsBytes(result.AsSpan()), offsetBytes);", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();", deviceContext, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectXTextureReadbackUsesCallerOwnedBuffers()
    {
        string texture = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "GpuTexture.cs"));
        string readback = File.ReadAllText(FindRepoFile("src", "ProGPU.Backend", "GpuTextureReadbackBuffer.cs"));
        string resources = File.ReadAllText(FindRepoFile("src", "ProGPU.DirectX", "ProGpuDirectXResources.cs"));

        Assert.Contains("public void ReadPixels(\n        Span<byte> destination", texture, StringComparison.Ordinal);
        Assert.Contains("ReadPixels(unpaddedPixels, mipLevel);", texture, StringComparison.Ordinal);
        Assert.Contains("originDepthOrArrayLayer", texture, StringComparison.Ordinal);
        Assert.Contains("depthOrArrayLayers: 1", resources, StringComparison.Ordinal);
        Assert.Contains("uint sourceWidth = GetMipDimension(texture.Width, mipLevel);", readback, StringComparison.Ordinal);
        Assert.Contains("width = width == 0 ? sourceWidth : width;", readback, StringComparison.Ordinal);
        Assert.Contains("public void ReadPixels(Span<byte> destination)", resources, StringComparison.Ordinal);
        Assert.Contains("texture.ReadPixels(\n            _writeShadow.AsSpan(", resources, StringComparison.Ordinal);
        Assert.Contains("private void ReadBackendSubresourceIntoWriteShadow", resources, StringComparison.Ordinal);
        Assert.Contains("Origin = new Origin3D { X = 0, Y = 0, Z = originDepthOrArrayLayer }", readback, StringComparison.Ordinal);
        Assert.DoesNotContain("var pixels = texture.ReadPixels(subresourceInfo.MipLevel);", resources, StringComparison.Ordinal);
        Assert.DoesNotContain("var sourceOffset = checked((int)(subresourceInfo.ArraySlice * subresourceInfo.SizeInBytes));", resources, StringComparison.Ordinal);
        Assert.DoesNotContain("pixels.AsSpan(sourceOffset", resources, StringComparison.Ordinal);
        Assert.DoesNotContain("pixels.AsSpan(0, checked((int)subresourceInfo.SizeInBytes))", resources, StringComparison.Ordinal);
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
