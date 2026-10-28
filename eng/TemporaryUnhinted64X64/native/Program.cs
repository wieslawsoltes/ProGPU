using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private static long _captureBytes;
    private static void Budget()
    {
        if (Clock.Elapsed.TotalSeconds > 45 || _captureBytes > 16*1024*1024)
            throw new TimeoutException("Original45-second/16MiB capture bound exceeded.");
    }
    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Save(string path, object value)
    { using var file = new FileStream(path, FileMode.CreateNew);JsonSerializer.Serialize(file,value,Json); }
    private static byte[] Raw<T>(T[] values) where T:unmanaged => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static int Main(string[] args)
    {
        string? output = null;
        try
        {
            Require(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted", "Disposable x64 Windows 2025 only.");
            Require(args.Length == 5 && args[0] is "wgpu" or "dawn", "Provider, original inputs, same-run reference, new output and verified closure required.");
            string provider = args[0], input = Path.GetFullPath(args[1]), reference = Path.GetFullPath(args[2]);
            output = Path.GetFullPath(args[3]);
            string temporary = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!)+Path.DirectorySeparatorChar;
            Require(reference.StartsWith(temporary,StringComparison.OrdinalIgnoreCase) && output.StartsWith(temporary,StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(output) && !File.Exists(output), "Fresh owned runner output required.");
            Directory.CreateDirectory(output);
            using JsonDocument closure = JsonDocument.Parse(File.ReadAllBytes(args[4]));
            object[] VerifyNativeImages()
            {
                var loaded=Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .Where(m=>m.ModuleName.StartsWith("progpu_native",StringComparison.OrdinalIgnoreCase) ||
                        provider=="wgpu" && string.Equals(m.ModuleName,"wgpu_native.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
                Require(loaded.Length>0,"Actual native image inventory absent.");
                if(provider=="wgpu")Require(loaded.Any(m=>string.Equals(m.ModuleName,"wgpu_native.dll",StringComparison.OrdinalIgnoreCase)),
                    "Owned Windows WebGPU image absent.");
                return loaded.Select(m=>{
                    string path=Path.GetFullPath(m.FileName), hash=Hash(File.ReadAllBytes(path));
                    Require(closure.RootElement.GetProperty("files").EnumerateArray().Any(f=>
                        string.Equals(f.GetProperty("absolutePath").GetString(),path,StringComparison.OrdinalIgnoreCase) &&
                        f.GetProperty("sha256").GetString()==hash),"Native producer loaded outside the verified closure.");
                    return (object)new {path,sha256=hash};
                }).ToArray();
            }
            using JsonDocument original = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(input,"original64.json")));
            using JsonDocument current = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(reference,"reference.json")));
            var root = original.RootElement;
            Require(root.GetProperty("cases").GetArrayLength()==64 && root.GetProperty("fonts").GetArrayLength()==2 &&
                root.GetProperty("width").GetInt32()==384 && root.GetProperty("height").GetInt32()==64,"Original inventory changed.");
            string[] baseline = root.GetProperty("original72RGBA").EnumerateArray().Select(v=>v.GetString()!).ToArray();
            Require(current.RootElement.GetProperty("cases").EnumerateArray().Select(c=>c.GetProperty("rgbaSha256").GetString()!).SequenceEqual(baseline),
                "Same-run original72-frame baseline changed.");
            using DawnGpuContext? dawn = provider=="dawn" ? DawnGpuContext.CreateSystemWarpOffscreen() : null;
            using WgpuContext? owned = dawn is null ? new WgpuContext { ForceFallbackAdapter=true,
                ComputeExecutionPreference=GpuComputeExecutionPreference.NativeCompute } : null;
            owned?.Initialize(null);
            WgpuContext context = dawn?.Context ?? owned!;
            // Observe the existing API entrypoint's module; do not load another
            // dependency or alter the provider/compiler selected by the workload.
            if(owned is not null)WindowsOwnedRuntimeValidation.Verify(owned);
            var rows = new List<object>();int failedPixels=0;
            for(int fontIndex=0;fontIndex<2;fontIndex++)
            {
                Budget();
                JsonElement font=root.GetProperty("fonts")[fontIndex];string hash=font.GetProperty("sha256").GetString()!;
                JsonElement same=current.RootElement.GetProperty("fonts")[fontIndex];
                Require(same.GetProperty("sha256").GetString()==hash && same.GetProperty("requested").GetString()==font.GetProperty("requested").GetString(),"Source font substituted.");
                byte[] bytes=File.ReadAllBytes(Path.Combine(temporary,"text-raster-font-inputs",hash+".ttf"));
                Require(bytes.Length==font.GetProperty("bytes").GetInt32() && Hash(bytes)==hash,"Original selected font bytes changed.");
                var cases=root.GetProperty("cases").EnumerateArray().Where(c=>c.GetProperty("font").GetInt32()==fontIndex).ToArray();
                Require(cases.Length==32 && cases.Select(c=>c.GetProperty("sourceIndex").GetInt32()).SequenceEqual(Enumerable.Range(0,32)),"Original descriptor order changed.");
                uint[] ids=cases.Select(c=>c.GetProperty("glyphId").GetUInt32()).ToArray();
                using var source = new NativeTextShapingContext(bytes);Array.Clear(bytes);
                _=VerifyNativeImages();
                uint em=checked((uint)(font.GetProperty("points").GetSingle()*96f/72f*64f));
                using var batch=source.CaptureOutlineBatch(0,ids,em,em,NativeFontHintInterpreter.TrueType35,NativeFontOutlinePolicy.Unhinted);
                Require(batch.Counts.Glyphs==32,"All original descriptors required.");
                var rawGlyphs=new NativeHintedGlyph[32];var points=new NativeHintedPoint[batch.Counts.Points];
                var tags=new byte[batch.Counts.Points];var ends=new int[batch.Counts.Contours];batch.CopyTo(rawGlyphs,points,tags,ends);
                using var geometry=batch.CreateGeometry(coverage:NativeHintedCoverage.Monochrome);
                var outlines=new NativeGlyphOutline[geometry.Counts.Outlines];var segments=new NativePathSegment[geometry.Counts.Segments];
                var descriptors=new NativeFontOutlineDescriptor[geometry.Counts.Descriptors];
                // Read the owned generation only after both original owners retire.
                // Rendering copied arrays alone would not establish this lifetime.
                batch.Dispose();source.Dispose();
                geometry.CopyTo(outlines,segments,descriptors);
                Require(descriptors.Length==32,"Geometry discarded original no-ink slots.");
                // This direct API compiles the complete supplied outline inventory,
                // independently of the selected positioned draw. Match its existing
                // binary fixture's exact raster-identity accounting.
                var rasterIdentities=new HashSet<string>(StringComparer.Ordinal);
                foreach(var outline in outlines)
                {
                    var normalized=new NativeGlyphOutline(0,outline.SegmentCount,outline.Minimum,outline.Maximum,outline.RasterScale,outline.SubpixelX);
                    rasterIdentities.Add(Convert.ToHexString(Raw(new[]{normalized}))+Convert.ToHexString(Raw(
                        segments.AsSpan(checked((int)outline.SegmentOffset),checked((int)outline.SegmentCount)).ToArray())));
                }
                Save(Path.Combine(output,$"font-{fontIndex}-raw.json"),new { fontIndex, fontSha256=hash, em26_6=em,
                    policy="Unhinted",coverage="Monochrome",interpreter=35, copiedAfterSourceAndBatchRetirement=true, rawGlyphs,points,tags,ends,
                    glyphRecordBytes=Convert.ToBase64String(Raw(rawGlyphs)),pointRecordBytes=Convert.ToBase64String(Raw(points)),
                    descriptors,segments,distinctRasterIdentities=rasterIdentities.Count,
                    outlines=outlines.Select(o=>new { offset=(ulong)o.SegmentOffset,count=(ulong)o.SegmentCount,o.Minimum,o.Maximum,o.RasterScale,o.SubpixelX }) });
                // One renderer owns the complete batch. Its first frame is cold;
                // every subsequent frame must reuse that exact coverage generation.
                using var renderer=dawn is null ? new NativeCompositor(context,TextureFormat.Rgba8Unorm) : NativeDawnAdapter.CreateCompositor(dawn,TextureFormat.Rgba8Unorm);
                using var target=new GpuTexture(context,384,64,TextureFormat.Rgba8Unorm,TextureUsage.RenderAttachment|TextureUsage.CopySrc,
                    "Original unhinted glyph",alphaMode:GpuTextureAlphaMode.Premultiplied);
                for(int index=0;index<32;index++)
                {
                    Budget();Require(descriptors[index].GlyphIndex==ids[index],"Captured glyph identity changed.");
                    JsonElement row=cases[index];var origin=row.GetProperty("origin").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
                    Require(origin.SequenceEqual(new[]{16f,32f}),"Original integer driver pen changed.");
                    string rawName=row.GetProperty("rgba").GetString()!;Require(Path.GetFileName(rawName)==rawName,"Invalid original frame name.");
                    using var packed=File.OpenRead(Path.Combine(input,rawName));using var gzip=new GZipStream(packed,CompressionMode.Decompress);
                    byte[] expected=new byte[384*64*4];gzip.ReadExactly(expected);Require(gzip.ReadByte()==-1 && Hash(expected)==row.GetProperty("rgbaSha256").GetString(),"Original fullRGBA changed.");
                    NativePositionedGlyph[] positioned=descriptors[index].OutlineIndex==uint.MaxValue ? [] :
                        [new(descriptors[index].OutlineIndex,new(16,32),Vector2.UnitX,Vector2.UnitY,new(0,0,0,1))];
                    byte[]? cold=null;
                    for(int frame=0;frame<2;frame++)
                    {
                        Budget();var before=renderer.GetLastSubmissionToken();
                        var metrics=renderer.RenderGlyphs(target,1f,outlines,segments,positioned,Vector4.One,contentRevision:checked((uint)index+1));
                        var token=renderer.GetLastSubmissionToken();Require(token.IsValid && token.Value>before.Value,"Actual native submission required.");
                        renderer.WaitForSubmission(token);Require(renderer.IsSubmissionComplete(token),"Native work incomplete.");
                        byte[] actual=target.ReadPixels();Require(actual.Length==expected.Length,"Complete original target required.");
                        _captureBytes+=actual.Length;
                        string name=$"{fontIndex}-{index:D2}-{frame}.rgba.gz";
                        using(var file=new FileStream(Path.Combine(output,name),FileMode.CreateNew))
                        using(var zip=new GZipStream(file,CompressionLevel.Optimal))zip.Write(actual);
                        int differences=0;for(int pixel=0;pixel<384*64;pixel++)if(!actual.AsSpan(pixel*4,4).SequenceEqual(expected.AsSpan(pixel*4,4)))differences++;
                        bool warmSame=frame==0 || cold!.AsSpan().SequenceEqual(actual);
                        bool coldGeneration=index==0 && frame==0;
                        bool coverageSame=metrics.GlyphCount==(uint)positioned.Length && (coldGeneration ?
                            metrics.RasterizedGlyphCount==(uint)rasterIdentities.Count && metrics.CoverageStagingBytes>0 :
                            metrics.RasterizedGlyphCount==0 && metrics.CoverageStagingBytes==0);
                        rows.Add(new { fontIndex,index,frame,glyphId=ids[index],outlineIndex=descriptors[index].OutlineIndex,
                            capture=name,rgbaSha256=Hash(actual),differentPixels=differences,warmSame,coldGeneration,coverageSame,metrics.RasterizedGlyphCount,metrics.CoverageStagingBytes });
                        if(differences!=0 || !warmSame || !coverageSame)failedPixels++;
                        if(frame==0)cold=actual;
                    }
                }
            }
            Budget();Require(rows.Count==128,"All64 cold/warm images required.");
            var nativeModules=VerifyNativeImages();
            Save(Path.Combine(output,"result.json"),new {completed=true,qualified=false,provider,backend=context.BackendKind.ToString(),
                nativeModules,elapsedSeconds=Clock.Elapsed.TotalSeconds,rawCaptureBytes=_captureBytes,failedFrames=failedPixels,rows});
            return failedPixels==0?0:2;
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error);
            if(output is not null && Directory.Exists(output))Save(Path.Combine(output,"failure.json"),new {error=error.ToString(),elapsedSeconds=Clock.Elapsed.TotalSeconds});
            return 1;
        }
    }
}
