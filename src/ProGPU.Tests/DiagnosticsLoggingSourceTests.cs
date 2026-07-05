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
        string source = ReadSource(root, project, fileName);

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
        string source = ReadSource(root, project, fileName);

        Assert.Contains(environmentVariable, source, StringComparison.Ordinal);
        Assert.Contains("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(message)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProGpuPackScriptCleansAndAuditsVersionedPackageArtifacts()
    {
        string source = ReadSource("eng", "progpu-pack.sh");

        Assert.Contains("\"${package_output}\"/*.${package_version}.nupkg", source, StringComparison.Ordinal);
        Assert.Contains("\"${package_output}\"/*.${package_version}.snupkg", source, StringComparison.Ordinal);
        Assert.Contains("is_expected_package_artifact()", source, StringComparison.Ordinal);
        Assert.Contains("\"${file_name}\" == \"${package_id}.${package_version}.nupkg\"", source, StringComparison.Ordinal);
        Assert.Contains("\"${file_name}\" == \"${package_id}.${package_version}.snupkg\"", source, StringComparison.Ordinal);
        Assert.Contains("Expected symbol package was not produced:", source, StringComparison.Ordinal);
        Assert.Contains("Unexpected package artifact in output:", source, StringComparison.Ordinal);
        Assert.Contains("find \"${package_output}\" -maxdepth 1 -type f", source, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts/packages/Release/*.nupkg", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfShimProjectsUseInRepoStrongNameKey()
    {
        string directoryBuildProps = ReadSource("Directory.Build.props");
        string windowsBaseProject = ReadSource("src", "WindowsBase", "WindowsBase.csproj");
        string presentationCoreProject = ReadSource("src", "PresentationCore", "PresentationCore.csproj");

        Assert.Contains("<ProGPUStrongNameKeyFile>$(MSBuildThisFileDirectory)eng/ProGPU.snk</ProGPUStrongNameKeyFile>", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("<SignAssembly Condition=\"'$(SignAssembly)' == ''\">true</SignAssembly>", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("<AssemblyOriginatorKeyFile Condition=\"'$(AssemblyOriginatorKeyFile)' == ''\">$(ProGPUStrongNameKeyFile)</AssemblyOriginatorKeyFile>", directoryBuildProps, StringComparison.Ordinal);
        Assert.DoesNotContain("35MSSharedLib1024.snk", windowsBaseProject, StringComparison.Ordinal);
        Assert.DoesNotContain("35MSSharedLib1024.snk", presentationCoreProject, StringComparison.Ordinal);
        Assert.DoesNotContain("WpfMicrosoftSharedKeyFile", windowsBaseProject, StringComparison.Ordinal);
        Assert.DoesNotContain("WpfMicrosoftSharedKeyFile", presentationCoreProject, StringComparison.Ordinal);
        Assert.DoesNotContain("<PublicSign>true</PublicSign>", windowsBaseProject, StringComparison.Ordinal);
        Assert.DoesNotContain("<PublicSign>true</PublicSign>", presentationCoreProject, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWorkflowUsesExplicitRuntimeNativeWebGpuTestLanes()
    {
        string workflow = ReadSource(".github", "workflows", "build.yml");

        Assert.Contains("rid: linux-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("rid: osx-arm64", workflow, StringComparison.Ordinal);
        Assert.Contains("rid: win-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("Install Linux WebGPU dependencies", workflow, StringComparison.Ordinal);
        Assert.Contains("libvulkan1", workflow, StringComparison.Ordinal);
        Assert.Contains("mesa-vulkan-drivers", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet restore src/ProGPU.Tests/ProGPU.Tests.csproj --runtime ${{ matrix.rid }}", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build src/ProGPU.Tests/ProGPU.Tests.csproj --configuration Release --runtime ${{ matrix.rid }}", workflow, StringComparison.Ordinal);
        Assert.Contains("LD_LIBRARY_PATH", workflow, StringComparison.Ordinal);
        Assert.Contains("DYLD_LIBRARY_PATH", workflow, StringComparison.Ordinal);
        Assert.Contains("$testArgs = @(", workflow, StringComparison.Ordinal);
        Assert.Contains("\"test\",", workflow, StringComparison.Ordinal);
        Assert.Contains("\"src/ProGPU.Tests/ProGPU.Tests.csproj\",", workflow, StringComparison.Ordinal);
        Assert.Contains("\"--runtime\",", workflow, StringComparison.Ordinal);
        Assert.Contains("\"${{ matrix.rid }}\",", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($IsWindows)", workflow, StringComparison.Ordinal);
        Assert.Contains("\"FullyQualifiedName~DiagnosticsLoggingSourceTests|FullyQualifiedName~StrongNameSigningTests|FullyQualifiedName~WindowsDpiAwarenessTests\"", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet @testArgs", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("name: progpu-packages-${{ matrix.rid }}", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/packages/Release/*.nupkg", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/packages/Release/*.snupkg", workflow, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedSubmodulesUsePublicGitHubUrlsForCiCheckout()
    {
        string gitmodules = ReadSource(".gitmodules");

        Assert.Contains("url = https://github.com/wieslawsoltes/microsoft-ui-xaml.git", gitmodules, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/wieslawsoltes/GitHub", gitmodules, StringComparison.Ordinal);
        Assert.DoesNotContain("url = ../", gitmodules, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuHitTestingShaderChecksResultCapacityBeforeResultListRead()
    {
        string source = ReadSource("src", "ProGPU.Vector", "GpuHitTesting.cs");

        Assert.Contains("if (count >= capacity) {\n            break;\n        }\n\n        if (results[count + 1u].hit == 0u) {", source, StringComparison.Ordinal);
        Assert.Contains("Buffer = deviceIndex.ResultListBuffer.BufferPtr, Offset = 0, Size = deviceIndex.ResultListBuffer.Size", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (count >= capacity || results[count + 1u].hit == 0u)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Buffer = deviceIndex.ResultListBuffer.BufferPtr, Offset = 0, Size = checked((uint)(initialResults.Length * resultSize))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TextLayoutUsesSingleGlyphBufferForWrappingAndAlignment()
    {
        string source = ReadSource("src", "ProGPU.Text", "TextLayout.cs");

        Assert.Contains("private readonly struct LineRange", source, StringComparison.Ordinal);
        Assert.Contains("public int Start { get; }", source, StringComparison.Ordinal);
        Assert.Contains("public int Count { get; }", source, StringComparison.Ordinal);
        Assert.Contains("private static int EstimateGlyphCapacity(string text)", source, StringComparison.Ordinal);
        Assert.Contains("private static int EstimateLineCapacity(string text)", source, StringComparison.Ordinal);
        Assert.Contains("private static void AddLineRange(List<LineRange> lines, int start, int end)", source, StringComparison.Ordinal);
        Assert.Contains("lines.Add(new LineRange(start, end - start));", source, StringComparison.Ordinal);
        Assert.Contains("int estimatedGlyphCapacity = EstimateGlyphCapacity(Text);", source, StringComparison.Ordinal);
        Assert.Contains("Glyphs.EnsureCapacity(estimatedGlyphCapacity);", source, StringComparison.Ordinal);
        Assert.Contains("var lines = new List<LineRange>(EstimateLineCapacity(Text));", source, StringComparison.Ordinal);
        Assert.Contains("int currentLineStart = 0;", source, StringComparison.Ordinal);
        Assert.Contains("for (int pathIndex = 0; pathIndex < FallbackFontPaths.Length; pathIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var path = FallbackFontPaths[pathIndex];", source, StringComparison.Ordinal);
        Assert.Contains("for (int fallbackIndex = 0; fallbackIndex < _fallbackFonts.Count; fallbackIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var fbFont = _fallbackFonts[fallbackIndex];", source, StringComparison.Ordinal);
        Assert.Contains("lastWordStartIndex = Glyphs.Count;", source, StringComparison.Ordinal);
        Assert.Contains("if (lastWordStartIndex > currentLineStart)", source, StringComparison.Ordinal);
        Assert.Contains("int wrapStartIndex = lastWordStartIndex;", source, StringComparison.Ordinal);
        Assert.Contains("int previousLineEnd = Glyphs.Count;", source, StringComparison.Ordinal);
        Assert.Contains("AddLineRange(lines, currentLineStart, wrapStartIndex);", source, StringComparison.Ordinal);
        Assert.Contains("for (int wrapIndex = wrapStartIndex; wrapIndex < previousLineEnd; wrapIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var wg = Glyphs[wrapIndex];", source, StringComparison.Ordinal);
        Assert.Contains("Glyphs.Add(remapped);", source, StringComparison.Ordinal);
        Assert.Contains("Glyphs.RemoveRange(wrapStartIndex, wrapCount);", source, StringComparison.Ordinal);
        Assert.Contains("currentLineStart = wrapStartIndex;", source, StringComparison.Ordinal);
        Assert.Contains("lastWordStartIndex = currentLineStart;", source, StringComparison.Ordinal);
        Assert.Contains("AddLineRange(lines, currentLineStart, Glyphs.Count);", source, StringComparison.Ordinal);
        Assert.Contains("for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var line = lines[lineIndex];", source, StringComparison.Ordinal);
        Assert.Contains("int lineEnd = line.Start + line.Count;", source, StringComparison.Ordinal);
        Assert.Contains("for (int glyphIndex = line.Start; glyphIndex < lineEnd; glyphIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var g = Glyphs[glyphIndex];", source, StringComparison.Ordinal);
        Assert.Contains("Glyphs[glyphIndex] = remap;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var path in FallbackFontPaths)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var fbFont in _fallbackFonts)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private const int DefaultLineGlyphCapacity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateLineGlyphList", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var currentLine =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("currentLine.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("currentLine.GetRange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var wg in wrappedGlyphs)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var line in lines)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var g in line)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyphs.AddRange(line)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyphs.Add(line[glyphIndex])", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<List<TextRunGlyph>>()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<List<TextRunGlyph>>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<TextRunGlyph>()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<TextRunGlyph>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GlyphAtlasUsesIndexedBatchAndOutlineTraversal()
    {
        string source = ReadSource("src", "ProGPU.Text", "GlyphAtlas.cs");

        Assert.Contains("int batchBufferCount = _batchBuffers.Count;", source, StringComparison.Ordinal);
        Assert.Contains("for (int bufferIndex = 0; bufferIndex < batchBufferCount; bufferIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var buffer = _batchBuffers[bufferIndex];", source, StringComparison.Ordinal);
        Assert.Contains("int batchBindGroupCount = _batchBindGroups.Count;", source, StringComparison.Ordinal);
        Assert.Contains("for (int bindGroupIndex = 0; bindGroupIndex < batchBindGroupCount; bindGroupIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var bg = _batchBindGroups[bindGroupIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var outlineFigures = outline.Figures;", source, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < outlineFigures.Count; figureIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var figure = outlineFigures[figureIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var figureSegments = figure.Segments;", source, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var segment = figureSegments[segmentIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var fontGpuDataEnumerator = _fontGpuData.Values.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("while (fontGpuDataEnumerator.MoveNext())", source, StringComparison.Ordinal);
        Assert.Contains("var data = fontGpuDataEnumerator.Current;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var buffer in _batchBuffers)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var bg in _batchBindGroups)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var figure in outline.Figures)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var segment in figure.Segments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var data in _fontGpuData.Values)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WgpuContextPendingResourceCleanupUsesPooledSnapshots()
    {
        string source = ReadSource("src", "ProGPU.Backend", "WgpuContext.cs");

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("private readonly HashSet<IntPtr> _pendingSnapshotSeen = new();", source, StringComparison.Ordinal);
        Assert.Contains("PooledResourcePointerSnapshot buffers = default;", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<IntPtr>.Shared.Rent(pendingCount)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<IntPtr>.Shared.Return(snapshot)", source, StringComparison.Ordinal);
        Assert.Contains("private readonly struct PooledResourcePointerSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("public ReadOnlySpan<IntPtr> Span", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseBindGroups(bindGroups.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseTextureViews(views.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseTextures(textures.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseBuffers(buffers.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseBindGroupLayouts(layouts.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleasePipelineLayouts(pipeLayouts.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseRenderPipelines(renderPipes.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseComputePipelines(computePipes.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseSamplers(samplers.Span);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseShaderModules(shaders.Span);", source, StringComparison.Ordinal);
        Assert.Contains("private void ReleaseBindGroups(ReadOnlySpan<IntPtr> bindGroups)", source, StringComparison.Ordinal);
        Assert.Contains("private void ReleaseBuffers(ReadOnlySpan<IntPtr> buffers)", source, StringComparison.Ordinal);
        Assert.Contains("for (var index = 0; index < bindGroups.Length; index++)", source, StringComparison.Ordinal);
        Assert.Contains("for (var index = 0; index < buffers.Length; index++)", source, StringComparison.Ordinal);
        Assert.Contains("var pendingCount = pending.Count;", source, StringComparison.Ordinal);
        Assert.Contains("for (var pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var ptr = pending[pendingIndex];", source, StringComparison.Ordinal);
        Assert.Contains("finally\n            {\n                buffers.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var snapshot = new List<IntPtr>(pending.Count)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return snapshot.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var seen = new HashSet<IntPtr>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var bg in bindGroups.Span)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var view in views.Span)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var tex in textures.Span)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var buf in buffers.Span)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var ptr in pending)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var bg in bindGroups)\n", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WgpuContextFirstActiveLookupAvoidsSnapshotArrays()
    {
        string source = ReadSource("src", "ProGPU.Backend", "WgpuContext.cs");
        string gpuHitTesting = ReadSource("src", "ProGPU.Vector", "GpuHitTesting.cs");
        string pathOps = ReadSource("src", "ProGPU.Vector", "PathOpGeometrySolver.cs");
        string presentationCoreGpuProvider = ReadSource("src", "PresentationCore", "GpuProvider.cs");
        string systemDrawingGpuProvider = ReadSource("src", "System.Drawing.Common", "GpuProvider.cs");
        string skiaSharp = ReadSource("src", "SkiaSharp", "SkiaSharp.cs");
        string avaloniaHost = ReadSource("src", "ProGPU.Avalonia", "ProGpuHostControl.cs");

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
    public void GpuTextureMipGeneratorCacheDisposesPipelinesWithoutForeach()
    {
        string source = ReadSource("src", "ProGPU.Backend", "GpuTexture.cs");

        Assert.Contains("private void QueuePipelinesForDisposal()", source, StringComparison.Ordinal);
        Assert.Contains("var pipelineEnumerator = _pipelines.Values.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("while (pipelineEnumerator.MoveNext())", source, StringComparison.Ordinal);
        Assert.Contains("_context.QueueRenderPipelineDisposal(pipelineEnumerator.Current);", source, StringComparison.Ordinal);
        Assert.Contains("QueuePipelinesForDisposal();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _pipelines.Values)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeAcceleratorTracksTransientBindGroupsWithoutListSnapshots()
    {
        string source = ReadSource("src", "ProGPU.Compute", "ComputeAccelerator.cs");

        Assert.Contains("Span<nint> bindGroupsToRelease = stackalloc nint[iterations * 2];", source, StringComparison.Ordinal);
        Assert.Contains("var bindGroupToReleaseCount = 0;", source, StringComparison.Ordinal);
        Assert.Contains("RunBlurPass(encoder, _blurHorizPipeline, blurHLayout, hInput, temp, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);", source, StringComparison.Ordinal);
        Assert.Contains("RunBlurPass(encoder, _blurVertPipeline, blurVLayout, temp, destination, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);", source, StringComparison.Ordinal);
        Assert.Contains("RunShadowHPass(encoder, _shadowBlurHorizPipeline, shadowHLayout, source, temp, paramsBuffer, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);", source, StringComparison.Ordinal);
        Assert.Contains("RunBlurPass(encoder, _blurHorizPipeline, blurHLayout, destination, temp, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseBindGroups(bindGroupsToRelease[..bindGroupToReleaseCount]);", source, StringComparison.Ordinal);
        Assert.Contains("private static void TrackBindGroupForRelease(Span<nint> bindGroupsToRelease, ref int count, BindGroup* bindGroup)", source, StringComparison.Ordinal);
        Assert.Contains("bindGroupsToRelease[count++] = (nint)bindGroup;", source, StringComparison.Ordinal);
        Assert.Contains("private void ReleaseBindGroups(ReadOnlySpan<nint> bindGroupsToRelease)", source, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < bindGroupsToRelease.Length; i++)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Collections.Generic;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<nint>()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("bindGroupsToRelease.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var bgPtr in bindGroupsToRelease)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCommandRecordingSnapshotsUseExplicitCopies()
    {
        string source = ReadSource("src", "ProGPU.Scene", "RenderCommand.cs");

        Assert.Contains("CopyList(_recordingContext.Commands)", source, StringComparison.Ordinal);
        Assert.Contains("CopyList(_recordingContext.PointBuffer)", source, StringComparison.Ordinal);
        Assert.Contains("CopyList(_recordingContext.DoubleBuffer)", source, StringComparison.Ordinal);
        Assert.Contains("CopyList(_recordingContext.Line3DBuffer)", source, StringComparison.Ordinal);
        Assert.Contains("CopyList(_recordingContext.FloatBuffer)", source, StringComparison.Ordinal);
        Assert.Contains("private static T[] CopyList<T>(List<T> values)", source, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < result.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("result[i] = values[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < resources.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("resources[i].Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("var otherCommands = other.Commands;", source, StringComparison.Ordinal);
        Assert.Contains("int otherCommandCount = otherCommands.Count;", source, StringComparison.Ordinal);
        Assert.Contains("for (int commandIndex = 0; commandIndex < otherCommandCount; commandIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var cmd = otherCommands[commandIndex];", source, StringComparison.Ordinal);
        Assert.Contains("AppendList(PointBuffer, other.PointBuffer);", source, StringComparison.Ordinal);
        Assert.Contains("AppendList(DoubleBuffer, other.DoubleBuffer);", source, StringComparison.Ordinal);
        Assert.Contains("AppendList(Line3DBuffer, other.Line3DBuffer);", source, StringComparison.Ordinal);
        Assert.Contains("AppendList(FloatBuffer, other.FloatBuffer);", source, StringComparison.Ordinal);
        Assert.Contains("AppendArray(_retainedResources, retainedResources);", source, StringComparison.Ordinal);
        Assert.Contains("private static void AppendList<T>(List<T> destination, List<T> source)", source, StringComparison.Ordinal);
        Assert.Contains("private static void AppendArray<T>(List<T> destination, T[] source)", source, StringComparison.Ordinal);
        Assert.Contains("destination.EnsureCapacity(checked(destination.Count + sourceCount));", source, StringComparison.Ordinal);
        Assert.Contains("for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("destination.Add(source[sourceIndex]);", source, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < _retainedResources.Count; i++)", source, StringComparison.Ordinal);
        Assert.Contains("_retainedResources[i].Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_recordingContext.Commands.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_recordingContext.PointBuffer.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_recordingContext.DoubleBuffer.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_recordingContext.Line3DBuffer.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_recordingContext.FloatBuffer.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cmd in other.Commands)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var resource in resources)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var resource in _retainedResources)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PointBuffer.AddRange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleBuffer.AddRange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Line3DBuffer.AddRange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FloatBuffer.AddRange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_retainedResources.AddRange", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositorTransientStateSnapshotsUseArrayPool()
    {
        string source = ReadSource("src", "ProGPU.Scene", "Compositor.cs");
        string dxfStaticBuffer = ReadSource("src", "ProGPU.Scene", "DxfStaticBuffer.cs");
        string seriesBuffer = ReadSource("src", "ProGPU.Backend", "GpuSeriesBuffer.cs");
        string acisPipeline = ReadSource("src", "ProGPU.Scene", "Extensions", "AcisSolidExtensionPipeline.cs");
        string hatchPipeline = ReadSource("src", "ProGPU.Scene", "Extensions", "HatchExtensionPipeline.cs");
        string lineSeriesPipeline = ReadSource("src", "ProGPU.Scene", "Extensions", "GpuLineSeriesExtensionPipeline.cs");
        string scatterSeriesPipeline = ReadSource("src", "ProGPU.Scene", "Extensions", "GpuScatterSeriesExtensionPipeline.cs");

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("private static T[] RentListSnapshot<T>(List<T> list, out int count)", source, StringComparison.Ordinal);
        Assert.Contains("CollectionsMarshal.SetCount(list, count)", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.IsReferenceOrContainsReferences<T>()", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(count)", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Return(snapshot)", source, StringComparison.Ordinal);
        Assert.Contains("private SmallValueStack<Rect> _clipStack;", source, StringComparison.Ordinal);
        Assert.Contains("private SmallValueStack<bool> _clipScopeIsGeometryMask;", source, StringComparison.Ordinal);
        Assert.Contains("private SmallValueStack<float> _opacityStack;", source, StringComparison.Ordinal);
        Assert.Contains("private SmallValueStack<GpuBlendMode> _blendModeStack;", source, StringComparison.Ordinal);
        Assert.Contains("private SmallValueStack<GpuTexture> _maskStack;", source, StringComparison.Ordinal);
        Assert.Contains("private static T[] RentStackSnapshot<T>(in SmallValueStack<T> stack, out int count)", source, StringComparison.Ordinal);
        Assert.Contains("private static void RestoreStack<T>(ref SmallValueStack<T> stack, T[] snapshot, int count)", source, StringComparison.Ordinal);
        Assert.Contains("private struct SmallValueStack<T> : IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(Math.Max(InitialArrayCapacity, capacity))", source, StringComparison.Ordinal);
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
        Assert.Contains("private SmallValueStack<List<CompositorDrawCall>> _drawCallListPool;", source, StringComparison.Ordinal);
        Assert.Contains("private List<CompositorDrawCall> RentDrawCallList(int capacity)", source, StringComparison.Ordinal);
        Assert.Contains("private List<CompositorDrawCall> RentMaskDrawCallList(int capacity)", source, StringComparison.Ordinal);
        Assert.Contains("private void ReturnMaskRenderPassDrawCallLists()", source, StringComparison.Ordinal);
        Assert.Contains("ReturnMaskRenderPassDrawCallLists();", source, StringComparison.Ordinal);
        Assert.Contains("private void ReturnPendingMaskTexturesToPool()", source, StringComparison.Ordinal);
        Assert.Contains("ReturnPendingMaskTexturesToPool();", source, StringComparison.Ordinal);
        Assert.Contains("RentMaskDrawCallList(maskDrawCallCount)", source, StringComparison.Ordinal);
        Assert.Contains("var maskTextureCount = _masksToReturnToPool.Count;\n        for (var maskTextureIndex = 0; maskTextureIndex < maskTextureCount; maskTextureIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("_maskTexturePool.Add(_masksToReturnToPool[maskTextureIndex]);", source, StringComparison.Ordinal);
        Assert.Contains("var maskPassCount = _maskRenderPasses.Count;\n        for (var maskPassIndex = 0; maskPassIndex < maskPassCount; maskPassIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var maskPass = _maskRenderPasses[maskPassIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var maskDrawCalls = maskPass.DrawCalls;\n            var maskDrawCallCount = maskDrawCalls.Count;", source, StringComparison.Ordinal);
        Assert.Contains("var dc = maskDrawCalls[drawCallIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var staticDrawCallList = RentDrawCallList(commands.Count)", source, StringComparison.Ordinal);
        Assert.Contains("var staticDrawCallList = RentDrawCallList(context.Commands.Count)", source, StringComparison.Ordinal);
        Assert.Contains("ReturnDrawCallList(staticDrawCalls)", source, StringComparison.Ordinal);
        Assert.Contains("var drawCallCount = _drawCalls.Count;\n            for (var drawCallIndex = 0; drawCallIndex < drawCallCount; drawCallIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var dc = _drawCalls[drawCallIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var drawCalls = sb.DrawCalls;\n        for (var drawCallIndex = 0; drawCallIndex < drawCalls.Length; drawCallIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var dc = drawCalls[drawCallIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var diagnosticCommands = diagContext.Commands;", source, StringComparison.Ordinal);
        Assert.Contains("var cmd = diagnosticCommands[commandIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var commands = ctx.Commands;\n            var commandCount = commands.Count;", source, StringComparison.Ordinal);
        Assert.Contains("var commands = picture.Commands;\n        for (var commandIndex = 0; commandIndex < commands.Length; commandIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var commands = context.Commands;\n            var commandCount = commands.Count;", source, StringComparison.Ordinal);
        Assert.Contains("var textRecords = staticBuffer.TextRecords;\n            for (var recordIndex = 0; recordIndex < textRecords.Length; recordIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("staticBuffer.UpdateTextBuffer(CollectionsMarshal.AsSpan(_textVerticesList));", source, StringComparison.Ordinal);
        Assert.Contains("public void UpdateTextBuffer(ReadOnlySpan<GlyphInstance> textVertices)", dxfStaticBuffer, StringComparison.Ordinal);
        Assert.Contains("UpdateTextBuffer((ReadOnlySpan<GlyphInstance>)textVertices);", dxfStaticBuffer, StringComparison.Ordinal);
        Assert.Contains("uint requiredBytes = checked((uint)textVertexCount * (uint)Marshal.SizeOf<GlyphInstance>());", dxfStaticBuffer, StringComparison.Ordinal);
        Assert.Contains("_textVertexBufferBack.Write(textVertices);", dxfStaticBuffer, StringComparison.Ordinal);
        Assert.Contains("var extensionStateEnumerator = _extensionStates.Values.GetEnumerator();", dxfStaticBuffer, StringComparison.Ordinal);
        Assert.Contains("while (extensionStateEnumerator.MoveNext())", dxfStaticBuffer, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var state in _extensionStates.Values)", dxfStaticBuffer, StringComparison.Ordinal);
        Assert.Contains("var layoutGlyphs = layout.Glyphs;", source, StringComparison.Ordinal);
        Assert.Contains("var layoutGlyphCount = layoutGlyphs.Count;", source, StringComparison.Ordinal);
        Assert.Contains("for (int glyphIndex = 0; glyphIndex < layoutGlyphCount; glyphIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var runGlyph = layoutGlyphs[glyphIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var extensionCount = _registeredExtensions.Count;", source, StringComparison.Ordinal);
        Assert.Contains("var ext = _registeredExtensions[extensionIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var pathFigures = cmd.Path.Figures;", source, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < pathFigures.Count; figureIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var sourceFigures = source.Figures;", source, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < sourceFigures.Count; figureIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("for (int dashIndex = 0; dashIndex < quadraticSegments.Length; dashIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("for (int dashIndex = 0; dashIndex < cubicSegments.Length; dashIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("for (int dashIndex = 0; dashIndex < arcSegments.Length; dashIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("for (int layerIndex = 0; layerIndex < colorLayers.Count; layerIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var layer = colorLayers[layerIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var layerOutlineFigures = layerOutline.Figures;", source, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < layerOutlineFigures.Count; figureIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("var fig = layerOutlineFigures[figureIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var seg = figureSegments[segmentIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var pathFigures = cmd.Path.Figures;", hatchPipeline, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < pathFigures.Count; figureIndex++)", hatchPipeline, StringComparison.Ordinal);
        Assert.Contains("var figureSegments = figure.Segments;", hatchPipeline, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)", hatchPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("var staticDrawCalls = new List<CompositorDrawCall>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var dc in _drawCalls)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var dc in sb.DrawCalls)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cmd in diagContext.Commands)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cmd in ctx.Commands)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cmd in picture.Commands)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cmd in commands)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cmd in context.Commands)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var record in staticBuffer.TextRecords)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("staticBuffer.UpdateTextBuffer(_textVerticesList.ToArray())", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var runGlyph in layout.Glyphs)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var maskPass in _maskRenderPasses)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var dc in maskPass.DrawCalls)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var tex in _masksToReturnToPool)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var kvp in _persistentTextureBindGroups)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var key in _persistentTextureBindGroups.Keys)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var key in cache.Keys)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var fe in _effectTextures.Keys)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var tuple in _effectTextures.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var entry in _allocatedLayerTextures)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cachedBg in _persistentTextureBindGroups.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var bg in _maskBindGroups.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var bg in _maskBindGroupsOffscreen.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var ext in _registeredExtensions)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var figure in cmd.Path.Figures)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var figure in source.Figures)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var segment in figure.Segments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var dashSegment in quadraticSegments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var dashSegment in cubicSegments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var dashSegment in arcSegments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var layer in colorLayers)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var fig in layerOutline.Figures)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var seg in fig.Segments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var figure in cmd.Path.Figures)", hatchPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var segment in figure.Segments)", hatchPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("_maskDrawCallListPool", source, StringComparison.Ordinal);
        Assert.Contains("private static void AddRemovalItem<T>(ref T[]? buffer, ref int count, int capacity, T item)", source, StringComparison.Ordinal);
        Assert.Contains("private static void ReturnRemovalBuffer<T>(T[]? buffer, int count)", source, StringComparison.Ordinal);
        Assert.Contains("AddRemovalItem(ref keysToRemove", source, StringComparison.Ordinal);
        Assert.Contains("AddRemovalItem(ref detached", source, StringComparison.Ordinal);
        Assert.Contains("AddRemovalItem(ref stale", source, StringComparison.Ordinal);
        Assert.Contains("var bindGroupEnumerator = _persistentTextureBindGroups.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("var key = bindGroupEnumerator.Current.Key;", source, StringComparison.Ordinal);
        Assert.Contains("var maskBindGroupEnumerator = cache.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("var effectTextureEnumerator = _effectTextures.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("var layerTextureEnumerator = _allocatedLayerTextures.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("var effectTextureEnumerator = _effectTextures.Values.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("var allocatedLayerTextureEnumerator = _allocatedLayerTextures.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("var cachedBindGroupEnumerator = _persistentTextureBindGroups.Values.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("var offscreenMaskBindGroupEnumerator = _maskBindGroupsOffscreen.Values.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("private void DisposeMaskTexturePool()", source, StringComparison.Ordinal);
        Assert.Contains("var pooledMaskTextures = RentListSnapshot(_maskTexturePool", source, StringComparison.Ordinal);
        Assert.Contains("_clipStack.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_clipScopeIsGeometryMask.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_opacityStack.Push(_activeOpacity);", source, StringComparison.Ordinal);
        Assert.Contains("_activeOpacity = _opacityStack.Pop();", source, StringComparison.Ordinal);
        Assert.Contains("RestoreStack(ref _clipStack", source, StringComparison.Ordinal);
        Assert.Contains("RestoreStack(ref _clipScopeIsGeometryMask", source, StringComparison.Ordinal);
        Assert.Contains("RestoreStack(ref _opacityStack", source, StringComparison.Ordinal);
        Assert.Contains("_opacityStack.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("RestoreStack(ref _blendModeStack", source, StringComparison.Ordinal);
        Assert.Contains("_blendModeStack.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("RestoreStack(ref _maskStack", source, StringComparison.Ordinal);
        Assert.Contains("_maskStack.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_drawCallListPool.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<Rect> _clipStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<bool> _clipScopeIsGeometryMask", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<float> _opacityStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<GpuBlendMode> _blendModeStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<GpuTexture> _maskStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<List<CompositorDrawCall>> _drawCallListPool", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<Rect>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<bool>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<float>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<GpuBlendMode>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<GpuTexture>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<List<CompositorDrawCall>>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static T[] RentStackSnapshot<T>(Stack<T>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static void RestoreStack<T>(Stack<T>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreStack(_clipStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreStack(_clipScopeIsGeometryMask", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreStack(_opacityStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreStack(_blendModeStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreStack(_maskStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_clipStack.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_clipScopeIsGeometryMask.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_opacityStack.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var opacity in _opacityStack)", source, StringComparison.Ordinal);
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
        string source = ReadSource("src", "ProGPU.Scene", "Visual.cs");

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("Visual[]? owners = null;", source, StringComparison.Ordinal);
        Assert.Contains("owners = ArrayPool<Visual>.Shared.Rent(Math.Max(4, _owners.Count));", source, StringComparison.Ordinal);
        Assert.Contains("Visual[] expandedOwners = ArrayPool<Visual>.Shared.Rent(owners.Length * 2);", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<Visual>.Shared.Return(owners, clearArray: true);", source, StringComparison.Ordinal);
        Assert.Contains("owners![i].Invalidate();", source, StringComparison.Ordinal);
        Assert.Contains("new(StringComparer.OrdinalIgnoreCase)", source, StringComparison.Ordinal);
        Assert.Contains("var activeAnimationEnumerator = _activeAnimations.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("while (activeAnimationEnumerator.MoveNext())", source, StringComparison.Ordinal);
        Assert.Contains("var kvp = activeAnimationEnumerator.Current;", source, StringComparison.Ordinal);
        Assert.Contains("IsAnimationProperty(propertyName, \"opacity\")", source, StringComparison.Ordinal);
        Assert.Contains("private static bool IsAnimationProperty(string propertyName, string expected)", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < _children.Count; i++)", source, StringComparison.Ordinal);
        Assert.Contains("_children[i].Parent = null;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<Visual>? owners", source, StringComparison.Ordinal);
        Assert.DoesNotContain("owners ??= new List<Visual>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("owners.Add(owner);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var owner in owners)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var kvp in _activeAnimations)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("propertyName.ToLowerInvariant()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var child in _children)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HitTestCacheBuildUsesListSpansWithoutTemporaryArrays()
    {
        string source = ReadSource("src", "ProGPU.Scene", "GpuRenderCommandHitTestCache.cs");
        string compositor = ReadSource("src", "ProGPU.Scene", "Compositor.cs");

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("using System.Runtime.InteropServices;", source, StringComparison.Ordinal);
        Assert.Contains("public sealed class GpuRenderCommandHitTestCacheBuilder : IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("CollectionsMarshal.AsSpan(_primitives)", source, StringComparison.Ordinal);
        Assert.Contains("CollectionsMarshal.AsSpan(_pathSegments)", source, StringComparison.Ordinal);
        Assert.Contains("uint startSegment = AppendPathSegments(segments);", source, StringComparison.Ordinal);
        Assert.Contains("private uint AppendPathSegments(ReadOnlySpan<GpuPathSegment> segments)", source, StringComparison.Ordinal);
        Assert.Contains("_pathSegments.EnsureCapacity(checked(startSegment + segments.Length));", source, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("_pathSegments.Add(segments[segmentIndex]);", source, StringComparison.Ordinal);
        Assert.Contains("private SmallValueStack<ClipState> _clipStack;", source, StringComparison.Ordinal);
        Assert.Contains("private SmallValueStack<float> _opacityStack;", source, StringComparison.Ordinal);
        Assert.Contains("private struct SmallValueStack<T> : IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.IsReferenceOrContainsReferences<T>()", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(Math.Max(InitialArrayCapacity, capacity))", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(Math.Max(capacity, items.Length * 2))", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Return(items, RuntimeHelpers.IsReferenceOrContainsReferences<T>())", source, StringComparison.Ordinal);
        Assert.Contains("_clipStack.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_opacityStack.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_hitTestCacheBuilder.Dispose();", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("_primitives.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_pathSegments.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_pathSegments.AddRange(segments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<ClipState> _clipStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Stack<float> _opacityStack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<ClipState>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Stack<float>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("items = new T[Math.Max(InitialArrayCapacity, capacity)]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var larger = new T[Math.Max(capacity, items.Length * 2)]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorDashHelpersAvoidTemporaryListMaterialization()
    {
        string dashPattern = ReadSource("src", "ProGPU.Vector", "DashPattern.cs");
        string bezierSegments = ReadSource("src", "ProGPU.Vector", "BezierSegmentGeometry.cs");
        string arcSegments = ReadSource("src", "ProGPU.Vector", "ArcSegmentGeometry.cs");

        Assert.Contains("private static int CountLineSegments(", dashPattern, StringComparison.Ordinal);
        Assert.Contains("private static void FillLineSegments(", dashPattern, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Collections.Generic;", dashPattern, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<LineDashSegment>", dashPattern, StringComparison.Ordinal);
        Assert.DoesNotContain("segments.ToArray()", dashPattern, StringComparison.Ordinal);

        Assert.Contains("private static bool TryPrepareDashSegments(", bezierSegments, StringComparison.Ordinal);
        Assert.Contains("private static int CountDashParameterSpans(", bezierSegments, StringComparison.Ordinal);
        Assert.Contains("private static int FillQuadraticBezierDashSegments(", bezierSegments, StringComparison.Ordinal);
        Assert.Contains("private static int FillCubicBezierDashSegments(", bezierSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Collections.Generic;", bezierSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<DashParameterSpan>", bezierSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<QuadraticBezierDashSegment>", bezierSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<CubicBezierDashSegment>", bezierSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly struct DashParameterSpan", bezierSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("parameterSpans = spans.ToArray()", bezierSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("segments.ToArray()", bezierSegments, StringComparison.Ordinal);

        Assert.Contains("private static int CountArcDashSpans(", arcSegments, StringComparison.Ordinal);
        Assert.Contains("private static int FillArcDashSegments(", arcSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Collections.Generic;", arcSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<ArcDashSegment>", arcSegments, StringComparison.Ordinal);
        Assert.DoesNotContain("dashSegments = segments.ToArray()", arcSegments, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuHitTestIndexBuilderUsesPooledPrimitiveBuckets()
    {
        string source = ReadSource("src", "ProGPU.Vector", "GpuHitTesting.cs");

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
        Assert.Contains("private const int MaxPreallocatedNodeCapacity = 65_536;", source, StringComparison.Ordinal);
        Assert.Contains("Nodes = new List<GpuHitTestNode>(EstimateNodeCapacity(primitives.Length, maxPrimitivesPerNode));", source, StringComparison.Ordinal);
        Assert.Contains("PrimitiveIndices = new List<uint>(primitives.Length);", source, StringComparison.Ordinal);
        Assert.Contains("int childIndex = FindContainingChild(primitive.BoundsMin, primitive.BoundsMax, center);", source, StringComparison.Ordinal);
        Assert.Contains("bool fitsLeft = primitiveMax.X <= center.X;", source, StringComparison.Ordinal);
        Assert.Contains("bool fitsBottom = primitiveMin.Y >= center.Y;", source, StringComparison.Ordinal);
        Assert.Contains("CopyList(builder.Nodes)", source, StringComparison.Ordinal);
        Assert.Contains("CopyList(builder.PrimitiveIndices)", source, StringComparison.Ordinal);
        Assert.Contains("private static T[] CopyList<T>(List<T> values)", source, StringComparison.Ordinal);
        Assert.Contains("array[i] = values[i];", source, StringComparison.Ordinal);
        Assert.Contains("retained.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("child0.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("private readonly struct RootPrimitiveIndices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<int>? retained", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<int>? child0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("retained ??= [];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly struct ListPrimitiveIndices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ListPrimitiveIndices(childPrimitives)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("builder.Nodes.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("builder.PrimitiveIndices.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<int>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindContainingChild(primitive.BoundsMin, primitive.BoundsMax, min, max, center)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("for (int i = 0; i < 4; i++)\n            {\n                var child = GetChildBounds(i, nodeMin, nodeMax, center);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectExtensionCacheCleanupUsesPooledRemovalBuffers()
    {
        string helper = ReadSource("src", "ProGPU.Scene", "Extensions", "PooledRemovalBuffer.cs");
        string wpfShaderEffect = ReadSource("src", "ProGPU.Scene", "Extensions", "WpfShaderEffectExtensionPipeline.cs");
        string imageEffect = ReadSource("src", "ProGPU.Scene", "Extensions", "ImageEffectExtensionPipeline.cs");

        Assert.Contains("internal static class PooledRemovalBuffer", helper, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(Math.Max(1, capacity))", helper, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.IsReferenceOrContainsReferences<T>()", helper, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Return(buffer)", helper, StringComparison.Ordinal);

        Assert.Contains("string[]? keysToRemove = null;", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("var textureBindGroupEnumerator = _textureBindGroups.GetEnumerator();", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("while (textureBindGroupEnumerator.MoveNext())", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("var kvp = textureBindGroupEnumerator.Current;", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Add(ref keysToRemove", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Return(keysToRemove, keysToRemoveCount)", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < _pool.Count; i++)", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("var resource = _pool[i];", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("var sourceLayoutEnumerator = _sourceLayouts.Values.GetEnumerator();", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("while (sourceLayoutEnumerator.MoveNext())", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("var layout = sourceLayoutEnumerator.Current;", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("var textureBindGroupValueEnumerator = _textureBindGroups.Values.GetEnumerator();", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("while (textureBindGroupValueEnumerator.MoveNext())", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("var cached = textureBindGroupValueEnumerator.Current;", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("List<string>? keysToRemove", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("keysToRemove ??= new List<string>();", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var kvp in _textureBindGroups)", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var resource in _pool)", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var layout in _sourceLayouts.Values)", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cached in _textureBindGroups.Values)", wpfShaderEffect, StringComparison.Ordinal);

        Assert.Contains("Compositor.TextureCacheKey[]? keysToRemove = null;", imageEffect, StringComparison.Ordinal);
        Assert.Contains("var textureBindGroupEnumerator = _textureBindGroups.GetEnumerator();", imageEffect, StringComparison.Ordinal);
        Assert.Contains("while (textureBindGroupEnumerator.MoveNext())", imageEffect, StringComparison.Ordinal);
        Assert.Contains("var kvp = textureBindGroupEnumerator.Current;", imageEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Add(ref keysToRemove", imageEffect, StringComparison.Ordinal);
        Assert.Contains("PooledRemovalBuffer.Return(keysToRemove, keysToRemoveCount)", imageEffect, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < _pool.Count; i++)", imageEffect, StringComparison.Ordinal);
        Assert.Contains("var resource = _pool[i];", imageEffect, StringComparison.Ordinal);
        Assert.Contains("var textureBindGroupValueEnumerator = _textureBindGroups.Values.GetEnumerator();", imageEffect, StringComparison.Ordinal);
        Assert.Contains("while (textureBindGroupValueEnumerator.MoveNext())", imageEffect, StringComparison.Ordinal);
        Assert.Contains("var cached = textureBindGroupValueEnumerator.Current;", imageEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("List<Compositor.TextureCacheKey>? keysToRemove", imageEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("keysToRemove ??= new List<Compositor.TextureCacheKey>();", imageEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var kvp in _textureBindGroups)", imageEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var resource in _pool)", imageEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cached in _textureBindGroups.Values)", imageEffect, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectPipelineLayoutsUseStackBackedDescriptors()
    {
        string compositor = ReadSource("src", "ProGPU.Scene", "Compositor.cs");
        string wpfShaderEffect = ReadSource("src", "ProGPU.Scene", "Extensions", "WpfShaderEffectExtensionPipeline.cs");
        string imageEffect = ReadSource("src", "ProGPU.Scene", "Extensions", "ImageEffectExtensionPipeline.cs");
        string shaderToy = ReadSource("src", "ProGPU.Scene", "Extensions", "ShaderToyExtensionPipeline.cs");
        string line3D = ReadSource("src", "ProGPU.Scene", "Extensions", "Line3DExtensionPipeline.cs");
        string customGrid = ReadSource("src", "ProGPU.Scene", "Extensions", "CustomGridExtensionPipeline.cs");
        string acisSolid = ReadSource("src", "ProGPU.Scene", "Extensions", "AcisSolidExtensionPipeline.cs");
        string hatch = ReadSource("src", "ProGPU.Scene", "Extensions", "HatchExtensionPipeline.cs");
        string mesh3D = ReadSource("src", "ProGPU.Scene", "Extensions", "Mesh3DExtensionPipeline.cs");
        string scatterSeries = ReadSource("src", "ProGPU.Scene", "Extensions", "GpuScatterSeriesExtensionPipeline.cs");
        string pipelineCache = ReadSource("src", "ProGPU.Backend", "RenderPipelineCache.cs");

        Assert.Contains("Span<VertexAttribute> vectorAttrs = stackalloc VertexAttribute[8];", compositor, StringComparison.Ordinal);
        Assert.Contains("Span<VertexBufferLayout> vectorVertexLayouts = stackalloc VertexBufferLayout[1];", compositor, StringComparison.Ordinal);
        Assert.Contains("Span<VertexAttribute> textAttrs = stackalloc VertexAttribute[8];", compositor, StringComparison.Ordinal);
        Assert.Contains("Span<VertexBufferLayout> textVertexLayouts = stackalloc VertexBufferLayout[1];", compositor, StringComparison.Ordinal);
        Assert.Contains("Span<VertexAttribute> scatterAttrs = stackalloc VertexAttribute[2];", compositor, StringComparison.Ordinal);
        Assert.Contains("Span<VertexBufferLayout> scatterVertexLayouts = stackalloc VertexBufferLayout[1];", compositor, StringComparison.Ordinal);
        Assert.Contains("Span<VertexBufferLayout> vertexLayouts = stackalloc VertexBufferLayout[1];", compositor, StringComparison.Ordinal);
        Assert.Contains("ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>()", compositor, StringComparison.Ordinal);
        Assert.Contains("ArrayStride = (uint)Unsafe.SizeOf<GlyphInstance>()", compositor, StringComparison.Ordinal);
        Assert.Contains("ArrayStride = (uint)Unsafe.SizeOf<Vector3>()", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("var vertexAttribs = new VertexAttribute[]", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("var textVertexAttribs = new VertexAttribute[]", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("var scatterAttribs = new VertexAttribute[]", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("VertexBufferLayout[] layouts", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("new[] { layoutDesc }", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("new[] { textLayoutDesc }", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("new[] { scatterLayoutDesc }", compositor, StringComparison.Ordinal);

        AssertStackBackedLayout(wpfShaderEffect, 3, "VectorVertex");
        Assert.Contains("Registers = CopyActiveRegisters(activeRegisters),", wpfShaderEffect, StringComparison.Ordinal);
        Assert.Contains("private static int[] CopyActiveRegisters(ReadOnlySpan<int> activeRegisters)", wpfShaderEffect, StringComparison.Ordinal);
        Assert.DoesNotContain("Registers = activeRegisters.ToArray()", wpfShaderEffect, StringComparison.Ordinal);
        AssertStackBackedLayout(imageEffect, 3, "VectorVertex");
        AssertStackBackedLayout(shaderToy, 3, "VectorVertex");
        AssertStackBackedLayout(line3D, 8, "VectorVertex");
        AssertStackBackedLayout(customGrid, 8, "VectorVertex");
        AssertStackBackedLayout(acisSolid, 8, "VectorVertex");
        AssertStackBackedLayout(hatch, 8, "VectorVertex");
        AssertStackBackedLayout(mesh3D, 2, "GpuVertex3D");
        AssertStackBackedLayout(scatterSeries, 2, "Vector3");

        Assert.Contains("ReadOnlySpan<VertexBufferLayout> vertexBufferLayouts", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("fixed (VertexBufferLayout* pLayouts = vertexBufferLayouts)", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("var renderPipelineEnumerator = _renderPipelines.Values.GetEnumerator();", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("renderPipelineEnumerator.Current", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("var computePipelineEnumerator = _computePipelines.Values.GetEnumerator();", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("computePipelineEnumerator.Current", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("var shaderModuleEnumerator = _shaders.Values.GetEnumerator();", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("shaderModuleEnumerator.Current", pipelineCache, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var p in _renderPipelines.Values)", pipelineCache, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var p in _computePipelines.Values)", pipelineCache, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var s in _shaders.Values)", pipelineCache, StringComparison.Ordinal);

        static void AssertStackBackedLayout(string source, int attributeCount, string vertexType)
        {
            Assert.Contains($"Span<VertexAttribute> attrs = stackalloc VertexAttribute[{attributeCount}];", source, StringComparison.Ordinal);
            Assert.Contains("Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];", source, StringComparison.Ordinal);
            Assert.Contains($"ArrayStride = (uint)Unsafe.SizeOf<{vertexType}>()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new VertexAttribute[]", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new VertexBufferLayout[]", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new[] {", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Marshal.AllocHGlobal(Marshal.SizeOf<VertexAttribute>()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes)", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PathGeometryHitTestingUsesIndexedTraversal()
    {
        string source = ReadSource("src", "ProGPU.Vector", "PathGeometryHitTesting.cs");

        Assert.Contains("var figures = geometry.Figures;", source, StringComparison.Ordinal);
        Assert.Contains("var polygons = new List<Vector2[]>(Math.Max(1, figures.Count));", source, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("PathFigure figure = figures[figureIndex];", source, StringComparison.Ordinal);
        Assert.Contains("for (int pointIndex = 0; pointIndex < polygon.Length; pointIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("Vector2 candidate = polygon[pointIndex];", source, StringComparison.Ordinal);
        Assert.Contains("for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("Vector2[] polygon = polygons[polygonIndex];", source, StringComparison.Ordinal);
        Assert.Contains("var segments = figure.Segments;", source, StringComparison.Ordinal);
        Assert.Contains("var points = new List<Vector2>(EstimateFigurePointCapacity(segments));", source, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("PathSegment segment = segments[segmentIndex];", source, StringComparison.Ordinal);
        Assert.Contains("private static int EstimateFigurePointCapacity(List<PathSegment> segments)", source, StringComparison.Ordinal);
        Assert.Contains("private static Vector2[] CopyPoints(List<Vector2> points)", source, StringComparison.Ordinal);
        Assert.Contains("result[pointIndex] = points[pointIndex];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (PathFigure figure in geometry.Figures)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (Vector2 candidate in polygon)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (Vector2[] polygon in polygons)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (PathSegment segment in figure.Segments)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("polygon = points.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("points[^1]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PolygonGeometryBoundsUsesIndexedSpanTraversal()
    {
        string source = ReadSource("src", "ProGPU.Vector", "PolygonGeometryBounds.cs");

        Assert.Contains("for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("Vector2 point = points[pointIndex];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (Vector2 point in points)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PathAtlasCleanupUsesPooledRemovalBuffers()
    {
        string helper = ReadSource("src", "ProGPU.Vector", "PooledRemovalBuffer.cs");
        string pathGeometry = ReadSource("src", "ProGPU.Vector", "PathGeometry.cs");
        string pathAtlas = ReadSource("src", "ProGPU.Vector", "PathAtlas.cs");
        string pathOps = ReadSource("src", "ProGPU.Vector", "PathOpGeometrySolver.cs");

        Assert.Contains("internal static class PooledRemovalBuffer", helper, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<T>.Shared.Rent(Math.Max(1, capacity))", helper, StringComparison.Ordinal);
        Assert.Contains("RuntimeHelpers.IsReferenceOrContainsReferences<T>()", helper, StringComparison.Ordinal);

        Assert.Contains("var figures = Figures;", pathGeometry, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)", pathGeometry, StringComparison.Ordinal);
        Assert.Contains("var figureSegments = figure.Segments;", pathGeometry, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)", pathGeometry, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var figure in Figures)", pathGeometry, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var segment in figure.Segments)", pathGeometry, StringComparison.Ordinal);

        Assert.Contains("PathInfo[]? activePaths = null;", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("var pathEnumerator = _paths.GetEnumerator();", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("while (pathEnumerator.MoveNext())", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("var kvp = pathEnumerator.Current;", pathAtlas, StringComparison.Ordinal);
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
        Assert.Contains("for (int i = 0; i < _pendingPaths.Count; i++)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("var info = _pendingPaths[i];", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("for (int i = 0; i < _tempBuffers.Count; i++)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("_tempBuffers[i].Dispose();", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var kvp in _paths)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var info in _pendingPaths)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var buffer in _tempBuffers)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("var figures = path.Figures;", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("var segments = new List<GpuPathSegment>(EstimateSegmentCapacity(figures));", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("return (records, CopySegments(segments));", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("private static int EstimateSegmentCapacity(List<PathFigure> figures)", pathAtlas, StringComparison.Ordinal);
        Assert.Contains("private static GpuPathSegment[] CopySegments(List<GpuPathSegment> segments)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("var segments = new List<GpuPathSegment>();", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var figure in path.Figures)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var segment in figure.Segments)", pathAtlas, StringComparison.Ordinal);
        Assert.DoesNotContain("return (records, segments.ToArray());", pathAtlas, StringComparison.Ordinal);

        Assert.Contains("var figures = path.Figures;", pathOps, StringComparison.Ordinal);
        Assert.Contains("for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)", pathOps, StringComparison.Ordinal);
        Assert.Contains("var segments = new List<GpuPathSegment>(EstimateSegmentCapacity(figures));", pathOps, StringComparison.Ordinal);
        Assert.Contains("for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)", pathOps, StringComparison.Ordinal);
        Assert.Contains("return (records, CopySegments(segments));", pathOps, StringComparison.Ordinal);
        Assert.Contains("private static int EstimateSegmentCapacity(List<PathFigure> figures)", pathOps, StringComparison.Ordinal);
        Assert.Contains("private static GpuPathSegment[] CopySegments(List<GpuPathSegment> segments)", pathOps, StringComparison.Ordinal);
        Assert.DoesNotContain("var segments = new List<GpuPathSegment>();", pathOps, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var figure in path.Figures)", pathOps, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var segment in figure.Segments)", pathOps, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var seg in figure.Segments)", pathOps, StringComparison.Ordinal);
        Assert.DoesNotContain("return (records, segments.ToArray());", pathOps, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectXBufferReadbackUsesCallerOwnedBuffers()
    {
        string gpuBuffer = ReadSource("src", "ProGPU.Backend", "GpuBuffer.cs");
        string resources = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXResources.cs");
        string deviceContext = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXDeviceContext.cs");
        string bindings = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXBindings.cs");
        string pipelines = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXPipelines.cs");
        string shaderBytecode = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXShaderBytecode.cs");
        string hlslTranslator = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXHlslTranslator.cs");
        string frontFacingEmulation = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXFrontFacingEmulation.cs");

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
        Assert.Contains("for (var sliceIndex = 0; sliceIndex < _backendArraySliceTextures.Length; sliceIndex++)", resources, StringComparison.Ordinal);
        Assert.Contains("_backendArraySliceTextures[sliceIndex].Resize(width, height);", resources, StringComparison.Ordinal);
        Assert.Contains("_backendArraySliceTextures[sliceIndex].Dispose();", resources, StringComparison.Ordinal);
        Assert.DoesNotContain("var bytes = _backendBuffer.ReadBytes(offsetBytes, sizeInBytes);", resources, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var texture in _backendArraySliceTextures)", resources, StringComparison.Ordinal);

        Assert.Contains("using System.Buffers;", deviceContext, StringComparison.Ordinal);
        Assert.Contains("private const int WireframeSourceIndexStackByteLimit = 16 * 1024;", deviceContext, StringComparison.Ordinal);
        Assert.Contains("private const int VertexBufferSlotStackLimit = 16;", deviceContext, StringComparison.Ordinal);
        Assert.Contains("private static uint[] CreateWireframeLineIndicesFromIndexBuffer(", deviceContext, StringComparison.Ordinal);
        Assert.Contains("Span<byte> sourceBytes = sizeInBytes <= WireframeSourceIndexStackByteLimit", deviceContext, StringComparison.Ordinal);
        Assert.Contains("stackalloc byte[sizeInBytes]", deviceContext, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<byte>.Shared.Rent(sizeInBytes)", deviceContext, StringComparison.Ordinal);
        Assert.Contains("sourceIndexBuffer.ReadWriteShadowBytes(sourceBytes, offsetBytes);", deviceContext, StringComparison.Ordinal);
        Assert.Contains("WriteWireframeLineIndices(topology, source, lineIndices);", deviceContext, StringComparison.Ordinal);
        Assert.Contains("WriteTriangleEdges(lineIndices, ref write, (uint)i, (uint)(i + 1), (uint)(i + 2));", deviceContext, StringComparison.Ordinal);
        Assert.Contains("private void ClearRecordedCommandResources()", deviceContext, StringComparison.Ordinal);
        Assert.Contains("private void ExecuteGpuBackedCommands(ProGPU.Backend.WgpuContext context)", deviceContext, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < _commands.Count; i++)", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var command = _commands[i];", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var requirementCount = requirements.Count;\n        for (var requirementIndex = 0; requirementIndex < requirementCount; requirementIndex++)", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var requirement = requirements[requirementIndex];", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var vertexBufferEnumerator = _vertexBuffers.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var vertexBufferBindingEnumerator = _vertexBufferBindings.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("if ((stages & DxShaderStageFlags.Vertex) != 0)", deviceContext, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < entries.Count; i++)", deviceContext, StringComparison.Ordinal);
        Assert.Contains("entries[i] = entry with", deviceContext, StringComparison.Ordinal);
        Assert.Contains("entries.Sort(static (left, right) =>", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var entryCount = entries.Count;\n        for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)", bindings, StringComparison.Ordinal);
        Assert.Contains("var entry = entries[entryIndex];", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var entry in entries)", bindings, StringComparison.Ordinal);
        Assert.Contains("var constantBufferEnumerator = _constantBuffers.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var shaderResourceViewEnumerator = _shaderResourceViews.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var samplerEnumerator = _samplers.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var unorderedAccessViewEnumerator = _unorderedAccessViews.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var cacheEnumerator = _pipelineBindGroupCache.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var cachedBindGroupEnumerator = _pipelineBindGroupCache.Values.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var wireframeIndexBufferEnumerator = _wireframeIndexBuffers.Values.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("wireframeIndexBufferEnumerator.Current.Dispose();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("var dynamicGraphicsPipelineEnumerator = _dynamicGraphicsPipelines.Values.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("dynamicGraphicsPipelineEnumerator.Current.Dispose();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("return entries;\n    }\n\n    private void AddStageBindings", deviceContext, StringComparison.Ordinal);
        Assert.Contains("Span<uint> sortedSlots = slotCount <= VertexBufferSlotStackLimit", deviceContext, StringComparison.Ordinal);
        Assert.Contains("stackalloc uint[slotCount]", deviceContext, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<uint>.Shared.Rent(slotCount)", deviceContext, StringComparison.Ordinal);
        Assert.Contains("CopySortedVertexBufferSlots(vertexBufferBindings, sortedSlots);", deviceContext, StringComparison.Ordinal);
        Assert.Contains("CopySortedVertexBufferSlots(vertexBuffers, sortedSlots);", deviceContext, StringComparison.Ordinal);
        Assert.Contains("private static void CopySortedVertexBufferSlots<TValue>(", deviceContext, StringComparison.Ordinal);
        Assert.Contains("using var sourceEnumerator = source.GetEnumerator();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("slots.Sort();", deviceContext, StringComparison.Ordinal);
        Assert.Contains("private const int BackendInputSlotStackLimit = 16;", pipelines, StringComparison.Ordinal);
        Assert.Contains("Span<uint> inputSlots = elementCount <= BackendInputSlotStackLimit", pipelines, StringComparison.Ordinal);
        Assert.Contains("stackalloc uint[elementCount]", pipelines, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<uint>.Shared.Rent(elementCount)", pipelines, StringComparison.Ordinal);
        Assert.Contains("InsertSortedUniqueInputSlot(inputSlots, ref inputSlotCount", pipelines, StringComparison.Ordinal);
        Assert.Contains("private static void InsertSortedUniqueInputSlot(Span<uint> slots, ref int slotCount, uint inputSlot)", pipelines, StringComparison.Ordinal);
        Assert.Contains("for (var shift = slotCount; shift > i; shift--)", pipelines, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(descriptor.Bytecode.Span)", pipelines, StringComparison.Ordinal);
        Assert.Contains("var elements = CopyInputElements(descriptor.Elements);", pipelines, StringComparison.Ordinal);
        Assert.Contains("private static DxInputElementDescriptor[] CopyInputElements(IReadOnlyList<DxInputElementDescriptor> source)", pipelines, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < Elements.Count; i++)\n        {\n            var element = Elements[i];", pipelines, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < elements.Count; i++)\n        {\n            var element = elements[i];", pipelines, StringComparison.Ordinal);
        Assert.Contains("var builder = new StringBuilder(elements.Count * 64);", pipelines, StringComparison.Ordinal);
        Assert.Contains("for (var index = 0; index < elements.Count; index++)", pipelines, StringComparison.Ordinal);
        Assert.Contains("builder.Append('|');", pipelines, StringComparison.Ordinal);
        Assert.Contains(".Append(element.InstanceDataStepRate)", pipelines, StringComparison.Ordinal);
        Assert.Contains("return builder.ToString();", pipelines, StringComparison.Ordinal);
        Assert.Contains("private static IReadOnlyList<DxReflectedShaderBindingRequirement> CombineReflectedBindingRequirements(\n        ProGpuDirectXShader vertexShader,\n        ProGpuDirectXShader? pixelShader)", pipelines, StringComparison.Ordinal);
        Assert.Contains("CopyReflectedBindingRequirements(vertexRequirements, requirements, ref write);", pipelines, StringComparison.Ordinal);
        Assert.Contains("CopyReflectedBindingRequirements(pixelRequirements, requirements, ref write);", pipelines, StringComparison.Ordinal);
        Assert.Contains("Array.Sort(requirements, CompareReflectedBindingRequirements);", pipelines, StringComparison.Ordinal);
        Assert.Contains("private static int CompareReflectedBindingRequirements(", pipelines, StringComparison.Ordinal);
        Assert.Contains("return vertexShader.ReflectedBindingRequirementsSupported &&", pipelines, StringComparison.Ordinal);
        Assert.Contains("public bool HasDxilProgram => ContainsChunk(\"DXIL\");", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("public bool HasTokenizedProgram => ContainsChunk(\"SHDR\", \"SHEX\");", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("public bool HasInputSignature => InputSignature.Count > 0 || ContainsChunk(\"ISGN\", \"ISG1\");", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("private bool ContainsChunk(string fourCC)", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("private bool ContainsChunk(string firstFourCC, string secondFourCC)", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("var resourceCount = ResourceBindings.Count;\n        for (var resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++)", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("var resource = ResourceBindings[resourceIndex];", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("var inputSignatureCount = InputSignature.Count;\n        for (var parameterIndex = 0; parameterIndex < inputSignatureCount; parameterIndex++)", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("var parameter = InputSignature[parameterIndex];", shaderBytecode, StringComparison.Ordinal);
        Assert.Contains("var matches = s_cbufferRegex.Matches(source);", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("var usedSrvRegisters = CreateExplicitRegisterSet(pendingResources, HlslResourceRegisterGroup.ShaderResource);", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("pendingResources.Sort(static (left, right) => left.Match.Index.CompareTo(right.Match.Index));", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("resources.Sort(static (left, right) =>", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("private static HashSet<uint> CreateExplicitRegisterSet(MatchCollection matches)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("private static HashSet<uint> CreateExplicitRegisterSet(\n        IReadOnlyList<PendingHlslShaderResource> resources,", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("var components = new List<string>(rows);", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("var vectorComponents = new List<string>((int)componentCount);", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("var arguments = new string[rawArguments.Count];", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("var semanticLocations = CreateParameterSemanticLocationMap(parameters);", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("for (var parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("private static Dictionary<string, uint> CreateUserSemanticLocationMap(IReadOnlyList<HlslField> fields)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("private static Dictionary<string, uint> CreateParameterSemanticLocationMap(IReadOnlyList<HlslParameter> parameters)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("private static void AddUserSemanticLocation(", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("for (var constantBufferIndex = 0; constantBufferIndex < constantBuffers.Count; constantBufferIndex++)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("for (var fieldIndex = 0; fieldIndex < constantBuffer.Fields.Count; fieldIndex++)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("for (var resourceIndex = 0; resourceIndex < shaderResources.Count; resourceIndex++)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("private static bool TryGetConstantBufferField(", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("private static string CreateRepeatedExpressionList(string expression, int count)", hlslTranslator, StringComparison.Ordinal);
        Assert.Contains("var names = GetDistinctGroupValues(matches, \"name\");", frontFacingEmulation, StringComparison.Ordinal);
        Assert.Contains("for (var nameIndex = 0; nameIndex < names.Count; nameIndex++)", frontFacingEmulation, StringComparison.Ordinal);
        Assert.Contains("private static List<string> GetDistinctGroupValues(MatchCollection matches, string groupName)", frontFacingEmulation, StringComparison.Ordinal);
        Assert.DoesNotContain("private static uint[] ReadSourceIndices(", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("var sourceIndices = ReadSourceIndices(", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceIndexBuffer.ReadWriteShadowBytes(MemoryMarshal.AsBytes(result.AsSpan()), offsetBytes);", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("var indices = new uint[checked((int)vertexCount)]", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var command in _commands)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var requirement in requirements)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("_vertexBuffers.ToDictionary", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("_vertexBufferBindings.ToDictionary", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("private static IEnumerable<DxShaderStage> EnumerateStages(", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var stage in EnumerateStages(stages))", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pair in _constantBuffers)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pair in _shaderResourceViews)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pair in _samplers)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pair in _unorderedAccessViews)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var candidate in _pipelineBindGroupCache.Keys)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var cached in _pipelineBindGroupCache.Values)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var entry in _wireframeIndexBuffers.Values)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _dynamicGraphicsPipelines.Values)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pair in source)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain("return entries.ToArray();", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(entry => entry.NativeBinding)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(pair => pair.Key)", deviceContext, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(element => element.InputSlot)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("descriptor.Bytecode.ToArray()", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("descriptor.Elements.ToArray()", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var element in Elements)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var element in elements)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("elements.Select(\n                (e, index)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".Distinct()", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(slot => slot)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("params ProGpuDirectXShader?[] shaders", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".SelectMany(shader => shader!.ReflectedBindingRequirements)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(requirement => requirement.NativeBinding)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".ThenBy(requirement => requirement.Stage)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".ThenBy(requirement => requirement.Kind)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".ThenBy(requirement => requirement.Slot)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("shaders.All(shader => shader is null || shader.ReflectedBindingRequirementsSupported)", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(shader => shader is { ReflectedBindingRequirementsSupported: false })", pipelines, StringComparison.Ordinal);
        Assert.DoesNotContain("Chunks.Any", shaderBytecode, StringComparison.Ordinal);
        Assert.DoesNotContain("Chunks.FirstOrDefault", shaderBytecode, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var resource in ResourceBindings)", shaderBytecode, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var parameter in InputSignature)", shaderBytecode, StringComparison.Ordinal);
        Assert.DoesNotContain("s_cbufferRegex.Matches(source).Cast<Match>().ToArray()", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(resource => IsShaderResourceSlotKind(resource.Kind))", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(resource => IsUnorderedAccessSlotKind(resource.Kind))", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(resource => IsSamplerSlotKind(resource.Kind))", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingResources.OrderBy", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(resource => resource.Kind)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("fields.Max", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.Range(0, rows)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.Range(0, (int)componentCount)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(argument => TranslateExpression(argument", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("parameters.Select(parameter => parameter.Semantic)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("parameters.Select(parameter =>", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(field => field.Semantic)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var semantic in semantics", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var constantBuffer in constantBuffers)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var field in constantBuffer.Fields)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var resource in shaderResources)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var parameter in function.Parameters)", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.Repeat", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstOrDefault", hlslTranslator, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(match => match.Groups[\"name\"].Value)", frontFacingEmulation, StringComparison.Ordinal);
        Assert.DoesNotContain(".Distinct(StringComparer.Ordinal)", frontFacingEmulation, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToArray();\n        var constant", frontFacingEmulation, StringComparison.Ordinal);
        Assert.DoesNotContain("return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();", deviceContext, StringComparison.Ordinal);
    }

    [Fact]
    public void SciChartPrimitiveUploadsUseCallerSpansBeforeDurableHistoryCopies()
    {
        string source = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXSciChart.cs");

        Assert.Contains("var pointSpan = points[..count];", source, StringComparison.Ordinal);
        Assert.Contains("var vertexBuffer = CreatePolygonFillVertexBuffer(pointSpan, brush, out var submittedVertexCount);", source, StringComparison.Ordinal);
        Assert.Contains("var lineSpan = lines[..count];", source, StringComparison.Ordinal);
        Assert.Contains("var vertexBuffer = CreateAreaFillVertexBuffer(lineSpan, brush, gradientRotationAngle, out var submittedVertexCount);", source, StringComparison.Ordinal);
        Assert.Contains("var lineVertices = CreateLineVertices(pointSpan, pen.ColorArgb);", source, StringComparison.Ordinal);
        Assert.Contains("CopyPrimitivePoints(pointSpan)", source, StringComparison.Ordinal);
        Assert.Contains("points.CopyTo(copiedPoints);", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < points.Length; i++)\n        {\n            var point = points[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < indices.Count; i++)\n        {\n            var index = indices[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < lines.Length; i++)\n        {\n            var line = lines[i];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var point in points)\n        {\n            if (!HasFinitePoint(point))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var index in indices)\n        {\n            if (index == previousIndex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var line in lines)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedPoints = points[..count].ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedLines = lines[..count].ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatePolygonFillVertexBuffer(copiedPoints", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateAreaFillVertexBuffer(copiedLines", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateLineVertices(copiedPoints", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SciChart3DUploadsUseCallerSpansBeforeDurableHistoryCopies()
    {
        string source = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXSciChart.cs");

        Assert.Contains("var vertexSpan = vertices;", source, StringComparison.Ordinal);
        Assert.Contains("var indexSpan = indices;", source, StringComparison.Ordinal);
        Assert.Contains("var heightSpan = heights;", source, StringComparison.Ordinal);
        Assert.Contains("var vertexBuffer = CreateVertexBuffer(vertexSpan);", source, StringComparison.Ordinal);
        Assert.Contains("var indexBuffer = CreateIndexBuffer(indexSpan);", source, StringComparison.Ordinal);
        Assert.Contains("var vertices = CreateSurfaceMeshVertices(heightSpan", source, StringComparison.Ordinal);
        Assert.Contains("var yRange = ResolveWaterfallHeightRange(heightSpan", source, StringComparison.Ordinal);
        Assert.Contains("var vertices = CreateWaterfallVertices(heightSpan", source, StringComparison.Ordinal);
        Assert.Contains("private ProGpuDirectXBuffer CreateVertexBuffer(ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices)", source, StringComparison.Ordinal);
        Assert.Contains("CopyVertices(vertexSpan)", source, StringComparison.Ordinal);
        Assert.Contains("CopyIndices(indexSpan)", source, StringComparison.Ordinal);
        Assert.Contains("CopyHeights(heightSpan)", source, StringComparison.Ordinal);
        Assert.Contains("vertices.CopyTo(copiedVertices);", source, StringComparison.Ordinal);
        Assert.Contains("indices.CopyTo(copiedIndices);", source, StringComparison.Ordinal);
        Assert.Contains("heights.CopyTo(copiedHeights);", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < points.Length; i++)\n        {\n            var point = points[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < heights.Length; i++)\n        {\n            var height = heights[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < vertices.Length; i++)\n        {\n            var vertex = vertices[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < indices.Length; i++)\n        {\n            var index = indices[i];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedVertices = vertices.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedIndices = indices.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedHeights = heights.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateIndexBuffer(copiedIndices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateSurfaceMeshVertices(copiedHeights", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWaterfallVertices(copiedHeights", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var point in points)\n        {\n            ValidateXyzPoint(point);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var height in heights)\n        {\n            minimum = Math.Min(minimum, height);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var height in heights)\n        {\n            minHeight = Math.Min(minHeight, height);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var height in heights)\n        {\n            if (!float.IsFinite(height))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var vertex in vertices)\n        {\n            if (!float.IsFinite(vertex.X)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var index in indices)\n        {\n            if (index >= vertexCount)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SciChart2DBatchUploadsUseCallerSpansBeforeDurableHistoryCopies()
    {
        string source = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXSciChart.cs");

        Assert.Contains("var vertexSpan = vertices[..count];", source, StringComparison.Ordinal);
        Assert.Contains("var vertexSpan = vertices.Slice(startIndex, count);", source, StringComparison.Ordinal);
        Assert.Contains("CreateLineBatchVertexBuffer(\n            vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateMountainFillVertexBuffer(\n            vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateBandFillVertexBuffer(\n            vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateBandLineVertices(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateColumnFillVertexBuffer(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateColumnStrokeVertexBuffer(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateRectBatchVertexBuffer(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateSpriteBatchVertexBuffer(\n            vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("DrawColoredSpritesCore(\n            sprite,\n            vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateCandleFillVertexBuffer(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateCandleStrokeVertexBuffer(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateOhlcStrokeVertexBuffer(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CreateBatchedTextureVertexBuffer(vertexSpan", source, StringComparison.Ordinal);
        Assert.Contains("CopySpan(vertexSpan)", source, StringComparison.Ordinal);
        Assert.Contains("private static T[] CopySpan<T>(ReadOnlySpan<T> values)", source, StringComparison.Ordinal);
        Assert.Contains("values.CopyTo(copiedValues);", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < vertices.Length; i++)\n        {\n            var source = vertices[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < vertices.Length; i++)\n        {\n            var vertex = vertices[i];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var source in vertices)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var vertex in vertices)\n        {\n            if (!TryGetLinePoint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedVertices = vertices[..count].ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedVertices = vertices.Slice(startIndex, count).ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateLineBatchVertexBuffer(\n            copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateMountainFillVertexBuffer(\n            copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateBandFillVertexBuffer(\n            copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateBandLineVertices(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateColumnFillVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateColumnStrokeVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateRectBatchVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateSpriteBatchVertexBuffer(\n            copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateColoredSpriteInstanceBuffer(\n            copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCandleFillVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCandleStrokeVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateOhlcStrokeVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateBatchedTextureVertexBuffer(copiedVertices", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SciChartRenderContextsDisposeCachesWithoutForeach()
    {
        string source = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXSciChart.cs");

        Assert.Contains("for (var resourceIndex = 0; resourceIndex < _transientResources.Count; resourceIndex++)", source, StringComparison.Ordinal);
        Assert.Contains("_transientResources[resourceIndex].Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("private static void DisposeCachedPipelines<TKey>(Dictionary<TKey, ProGpuDirectXGraphicsPipeline> pipelines)", source, StringComparison.Ordinal);
        Assert.Contains("var pipelineEnumerator = pipelines.Values.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("pipelineEnumerator.Current.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("private static void DisposeCachedSamplers<TKey>(Dictionary<TKey, ProGpuDirectXSamplerState> samplers)", source, StringComparison.Ordinal);
        Assert.Contains("var samplerEnumerator = samplers.Values.GetEnumerator();", source, StringComparison.Ordinal);
        Assert.Contains("samplerEnumerator.Current.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("DisposeCachedPipelines(_texturePipelines);", source, StringComparison.Ordinal);
        Assert.Contains("DisposeCachedPipelines(_heightContourPipelines);", source, StringComparison.Ordinal);
        Assert.Contains("DisposeCachedSamplers(_samplers);", source, StringComparison.Ordinal);
        Assert.Contains("DisposeCachedPipelines(_pipelines);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var resource in _transientResources)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _texturePipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _linePipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _columnFillPipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _textureVertexPipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _spritePipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _instancedSpritePipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _shapedHeatmapPipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _heightContourPipelines.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var sampler in _samplers.Values)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pipeline in _pipelines.Values)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SciChartVerticalPixelUploadsUseCallerSpansAndPooledScratch()
    {
        string source = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXSciChart.cs");

        Assert.Contains("using System.Buffers;", source, StringComparison.Ordinal);
        Assert.Contains("ReadOnlySpan<int> yCoordinates,\n        ReadOnlySpan<int> pixelColorsArgb", source, StringComparison.Ordinal);
        Assert.Contains("CreateVerticalPixelVertexBuffer(\n            xLeft,\n            xRight,\n            yCoordinates,\n            pixelColorsArgb", source, StringComparison.Ordinal);
        Assert.Contains("var uploadColors = ArrayPool<int>.Shared.Rent(pixelColorsArgb.Length);", source, StringComparison.Ordinal);
        Assert.Contains("CopyVerticalPixelColors(pixelColorsArgb, opacity, yAxisIsFlipped, uploadColorSpan);", source, StringComparison.Ordinal);
        Assert.Contains("pixelsTexture.SetData(uploadColorSpan);", source, StringComparison.Ordinal);
        Assert.Contains("ArrayPool<int>.Shared.Return(uploadColors);", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < pixelColorsArgb.Length; i++)\n        {\n            var colorArgb = pixelColorsArgb[i];", source, StringComparison.Ordinal);
        Assert.Contains("yCoordinates.IsEmpty ? null : CopySpan(yCoordinates)", source, StringComparison.Ordinal);
        Assert.Contains("CopySpan(pixelColorsArgb)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedCoordinates = yCoordinates.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var copiedColors = pixelColorsArgb.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var sourceColors = pixelColorsArgb.ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var colorArgb in pixelColorsArgb)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateVerticalPixelVertexBuffer(\n            xLeft,\n            xRight,\n            copiedCoordinates,\n            copiedColors", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectXTextureReadbackUsesCallerOwnedBuffers()
    {
        string texture = ReadSource("src", "ProGPU.Backend", "GpuTexture.cs");
        string readback = ReadSource("src", "ProGPU.Backend", "GpuTextureReadbackBuffer.cs");
        string resources = ReadSource("src", "ProGPU.DirectX", "ProGpuDirectXResources.cs");

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

    private static string ReadSource(params string[] pathParts)
    {
        return File.ReadAllText(FindRepoFile(pathParts)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
