using ProGPU.Backend;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
if (args.Length != 4 || args[0] is not ("baseline" or "wide") || args[1] is not ("timing" or "coverage")) throw new ArgumentException();
string root=Path.GetFullPath(args[2]);
string Read(string kind) => File.ReadAllText(Path.Combine(root,args[0]+"-"+kind+".wgsl"));
if (args[1] == "coverage") {
 new ProGPU.Tests.OriginalPathCoverageProbe { OriginalShader=File.ReadAllText(Path.Combine(root,"reference-path.wgsl")), CandidateShader=Read("path") }.SinglePathEntryMatchesEveryOrdinaryCoverageWordAndUntouchedSlot();
 int glyphs=OriginalGlyphCoverageProbe.Run(File.ReadAllText(Path.Combine(root,"reference-glyph.wgsl")),Read("glyph"));
 File.WriteAllText(args[3],JsonSerializer.Serialize(new{passed=true,originalPathCases=128,originalGlyphCases=glyphs,qualified=false,variant=args[0]}));return;
}
using var context=new WgpuContext();context.Initialize(null);
if (OperatingSystem.IsWindows() && (context.AdapterBackendType.ToString() != "D3D12" || context.SelectedDx12ShaderCompiler != WgpuDx12ShaderCompiler.Fxc)) throw new Exception("Original default D3D12/FXC required.");
using var cache=new RenderPipelineCache(context);var results=new List<object>();
unsafe {
 foreach(string kind in new[]{"path","glyph"}) {
  string source=Read(kind);long start=Stopwatch.GetTimestamp();
  var shader=cache.GetOrCreateShader(kind,source);double moduleMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
  start=Stopwatch.GetTimestamp();var pipeline=cache.GetOrCreateComputePipeline(kind,shader,kind=="path"?"cs_main_single_path":"cs_main");
  if(pipeline==null)throw new Exception("Pipeline creation failed.");double pipelineMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
  results.Add(new{kind,moduleMs,pipelineMs,sourceSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))});
  Console.WriteLine($"{args[0]} {kind}: module={moduleMs:0.000}ms pipeline={pipelineMs:0.000}ms");
 }
}
File.WriteAllText(args[3],JsonSerializer.Serialize(new{diagnosticOnly=true,qualified=false,variant=args[0],architecture=RuntimeInformation.ProcessArchitecture.ToString(),os=RuntimeInformation.OSDescription,provider=context.BackendKind.ToString(),backend=context.AdapterBackendType.ToString(),adapter=context.AdapterName,compiler=context.SelectedDx12ShaderCompiler.ToString(),results},new JsonSerializerOptions{WriteIndented=true}));
