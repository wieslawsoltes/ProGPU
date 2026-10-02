using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

internal static partial class Program
{
    private static void CaptureShaderAxisDecomposition(string directory,string commit,bool unavailable,Stopwatch timer)
    {
        int pureControls=ShaderAxisOracle.VerifyArithmeticControls();
        if (pureControls!=29) throw new InvalidOperationException("Wrong independent axis control inventory.");
        using var sdk = new OriginalAxisSdk(commit);
        int atomicControls=sdk.VerifyAtomicControls();
        var math = new List<object>();
        var observations = new List<object>();
        var failures = new List<string>();
        foreach (AxisInput input in ShaderAxisOracle.Cases())
        {
            CheckPaddingDeadline(timer);
            var measured=sdk.Capture(input.Values);
            try { ShaderAxisOracle.CheckSdk(input,measured.Values,measured.Traits); }
            catch (InvalidOperationException error) { failures.Add(error.Message); }
            math.Add(new { input.Name, OriginalDoubleBits=input.Values.Select(x=>BitConverter.DoubleToInt64Bits(x).ToString("X16")).ToArray(),
                measured.Traits, Values=measured.Values, FloatBits=measured.Values.Select(x=>ShaderAxisOracle.Bits(x).ToString("X8")).ToArray(),
                IndependentDiagonalInverse=ShaderAxisOracle.DiagonalInverse(measured.Values[120],measured.Values[121]),
                ShortcutInverse=new[]{ShaderAxisOracle.Divide(1,measured.Values[120]),ShaderAxisOracle.Divide(1,measured.Values[121])},
                Meaning="Actual installed SDK math, not extraction from wpfgfx or proof of its build header/version." });
            foreach (PaddingOutput output in new[]{PaddingOutput.Constant,PaddingOutput.Input,PaddingOutput.Uv,PaddingOutput.Derivatives})
            {
                string name=input.Name + "-" + output.ToString().ToLowerInvariant();
                var description=new { input.Name,Output=output.ToString(),input.DpiX,input.DpiY,input.ScaleX,input.ScaleY,input.Padded,
                    OriginalBounds=new[]{16.75,16.25,15.5,7.5}, OriginalOffset=new[]{2.0,3.0}, OriginalInput=input.Values,
                    ShaderWords=PaddingWords(output),ShaderRenderMode="SoftwareOnly",SamplingMode="NearestNeighbor",
                    ActualVisualOptions="Aliased/NearestNeighbor",FinalOutputClip=ShaderAxisOracle.OutputClip(input),
                    PixelAssertion=unavailable ? "original-unavailable-input-control" : output==PaddingOutput.Constant
                        ? "independent-original-aliased-constant-coverage" : "observation-only-no-pixel-parity",
                    NativeAdmitted=false,HardwareQualified=false };
                using(var file=new FileStream(Path.Combine(directory,name+".input.json"),FileMode.CreateNew))
                    JsonSerializer.Serialize(file,description,new JsonSerializerOptions{WriteIndented=true});
                byte[] plain=CaptureAxisBitmap(CreateAxisVisual(input,null),input,directory,name+".plain",timer);
                AssertOriginalPlainCapture(plain);
                var retained=CreateAxisVisual(input,output);
                byte[]? first=null;
                var hashes=new List<string>();
                for(int replay=0;replay<3;++replay)
                {
                    byte[] pixels=CaptureAxisBitmap(replay==2 ? CreateAxisVisual(input,output) : retained,
                        input,directory,name+"-replay-"+replay,timer);
                    if(first!=null && !first.AsSpan().SequenceEqual(pixels)) failures.Add(name+": replay pixels differ.");
                    first??=pixels;
                    if(unavailable && !pixels.AsSpan().SequenceEqual(plain)) failures.Add(name+": unavailable source control differs from plain input.");
                    if(!unavailable && output==PaddingOutput.Constant)
                    {
                        var clip=ShaderAxisOracle.OutputClip(input);
                        bool match=true;
                        for(int y=0;y<64 && match;++y) for(int x=0;x<96 && match;++x) for(int channel=0;channel<4;++channel)
                        {
                            int expected=channel==3 ? 255 : !clip.Contains(x,y) ? 0 : channel==0 ? 191 : channel==1 ? 128 : 64;
                            int actual=pixels[(y*96+x)*4+channel];
                            if(actual!=expected) { failures.Add($"{name}/{replay}: ({x},{y})[{channel}]={actual}, expected {expected}."); match=false; break; }
                        }
                    }
                    hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                }
                observations.Add(new{Input=description,Replays=3,Pixels=first,ReplaySha256=hashes,
                    PlainSha256=Convert.ToHexString(SHA256.HashData(plain)),
                    QualifiedConstantCoverage=!unavailable && output==PaddingOutput.Constant,
                    ObservedOnly=output!=PaddingOutput.Constant});
            }
        }
        if(math.Count!=10 || observations.Count!=40 || atomicControls!=3 || pureControls!=29)
            throw new InvalidOperationException("Incomplete original axis inventory.");
        var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(x=>string.Equals(x.ModuleName,"wpfgfx_cor3.dll",StringComparison.OrdinalIgnoreCase))
            .Select(x=>FileIdentity(x.FileName)).ToArray();
        if(modules.Length!=1) throw new InvalidOperationException("Original WPF renderer identity is ambiguous.");
        var receipt=new { Schema=1,SourceCommit=commit,Architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationCore=FileIdentity(typeof(ShaderEffect).Assembly.Location),Producer=FileIdentity(Assembly.GetExecutingAssembly().Location),
            NativeModules=modules,SdkCompanion=sdk.Identity,MathCaseCount=10,MathCases=math,ArithmeticControls=pureControls,
            AtomicControls=atomicControls,CaseCount=40,Cases=observations,Replays=120,OriginalDrawingBaselines=40,
            QualifiedArithmeticCases=failures.Count==0 ? 10 : 0,
            QualifiedConstantCoverageCases=!unavailable && failures.Count==0 ? 10 : 0,
            ObservationOnlyCases=30,QualifiedUvInputDerivativeCases=0,QualifiedNativeCases=0,QualifiedHardwareCases=0,
            CaptureMode=unavailable ? "unsupported-software-control" : "shader-pixels",InvalidShaders=invalidShaders,Failures=failures,
            FloatLayout="world[0:16],scale[16:32],inverseScale[32:48],residual[48:64],final[64:80],sampling[80:96],inverseSampling[96:112],allocation[112:116],paddedLocalLTRB[116:120],scaleXY[120:122],determinant[122],rootDpiXY[123:125],reserved[125:128]",
            TraitLayout="MSC_FULL_VER,DIRECTX_MATH_VERSION,PE_machine,SSE=1/NEON=2,FMA3",
            Qualification="SDK diagonal arithmetic and original software constant coverage only. UV/input/derivative bytes are observations, not parity. SDK provenance is not wpfgfx build provenance." };
        using(var file=new FileStream(Path.Combine(directory,failures.Count==0 ? "shader-axis-decomposition.json" : "shader-axis-decomposition.failed.json"),FileMode.CreateNew))
            JsonSerializer.Serialize(file,receipt,new JsonSerializerOptions{WriteIndented=true});
        if(failures.Count!=0) throw new InvalidOperationException(string.Join(Environment.NewLine,failures));
        Console.WriteLine($"Original axis math: 10 SDK cases; 40 source cases / 120 replays; {(unavailable ? 0 : 10)} constant-coverage cases; 0 UV/input/derivative/native/hardware parity cases.");
    }

    private static byte[] CaptureAxisBitmap(Visual visual,AxisInput input,string directory,string name,Stopwatch timer)
    {
        CheckPaddingDeadline(timer);
        var bitmap=new RenderTargetBitmap(96,64,96*input.DpiX,96*input.DpiY,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Dispatcher.CurrentDispatcher.Invoke(static()=>{},DispatcherPriority.ApplicationIdle);
        if(Volatile.Read(ref invalidShaders)!=0) throw new InvalidOperationException("Original axis shader rejected.");
        var pixels=new byte[96*64*4]; bitmap.CopyPixels(pixels,96*4,0);
        SaveSamplerBitmap(directory,name,bitmap,pixels);
        return pixels;
    }

    private static Visual CreateAxisVisual(AxisInput input,PaddingOutput? output)
    {
        var root=new LocalCaptureParent();
        var background=new DrawingVisual();
        using(var dc=background.RenderOpen()) dc.DrawRectangle(Brushes.Black,null,new(0,0,96/input.DpiX,64/input.DpiY));
        root.Children.Add(background);
        var placement=new LocalCaptureParent{Transform=new TranslateTransform(2,3)};
        var source=new PaddingDrawingVisual{Transform=new MatrixTransform(input.ScaleX,0,0,input.ScaleY,0,0)};
        using(var dc=source.RenderOpen()) dc.DrawRectangle(Brushes.White,null,new(16.75,16.25,15.5,7.5));
        if(VisualTreeHelper.GetContentBounds(source)!=new Rect(16.75,16.25,15.5,7.5))
            throw new InvalidOperationException("Original axis source bounds changed.");
        if(output.HasValue)
        {
            double[] v=input.Values;
            // Reuse the already validated original-only ShaderEffect class;
            // it owns bytecode/constants and takes the same public double pads.
            source.Effect=new OriginalLocalCaptureEffect(new(input.Name,output.Value,input.DpiX,
                v[10],v[11],v[12],v[13],default));
            ((OriginalLocalCaptureEffect)source.Effect).SetPadding(new(input.Name,output.Value,input.DpiX,
                v[10],v[11],v[12],v[13],default));
        }
        placement.Children.Add(source); root.Children.Add(placement); return root;
    }

    private sealed class OriginalAxisSdk : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Probe(IntPtr input,uint inputs,IntPtr output,uint outputs,IntPtr traits,uint traitCount);
        private readonly IntPtr library;
        private readonly Probe probe;
        private readonly Probe affineProbe;
        internal object Identity {get;}
        internal OriginalAxisSdk(string commit)
        {
            string path=Path.Combine(AppContext.BaseDirectory,"OriginalShaderAxisMath.dll");
            Machine expected=RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => Machine.Arm64,
                Architecture.X64 => Machine.Amd64,
                _ => throw new InvalidOperationException("Unsupported original SDK process architecture.")
            };
            using(var stream=File.OpenRead(path)) using(var pe=new PEReader(stream))
            {
                if(pe.PEHeaders.CoffHeader.Machine!=expected) throw new InvalidOperationException("Wrong SDK companion architecture.");
            }
            string provenancePath=Path.Combine(AppContext.BaseDirectory,"OriginalShaderAxisMath.provenance.json");
            if(new FileInfo(provenancePath).Length>1024*1024) throw new InvalidOperationException("SDK provenance exceeds its bound.");
            using var provenance=JsonDocument.Parse(File.ReadAllBytes(provenancePath));
            var build=provenance.RootElement;
            using(var binary=File.OpenRead(path))
            {
                if(build.GetProperty("Schema").GetInt32()!=1 || build.GetProperty("SourceCommit").GetString()!=commit ||
                    build.GetProperty("Architecture").GetString()!=RuntimeInformation.ProcessArchitecture.ToString() ||
                    build.GetProperty("PeMachine").GetUInt16()!=(ushort)expected ||
                    build.GetProperty("Binary").GetProperty("Sha256").GetString()!=Convert.ToHexString(SHA256.HashData(binary)) ||
                    build.GetProperty("Binary").GetProperty("Bytes").GetInt64()!=binary.Length ||
                    build.GetProperty("CompilerBackends").GetArrayLength()!=2 ||
                    build.GetProperty("DirectXHeaders").GetArrayLength()<3)
                    throw new InvalidOperationException("Original SDK provenance does not identify this binary/source/toolchain.");
            }
            Identity=new {Binary=FileIdentity(path),Build=provenance.RootElement.Clone()};
            library=NativeLibrary.Load(path);
            try
            {
                probe=Marshal.GetDelegateForFunctionPointer<Probe>(NativeLibrary.GetExport(library,"OriginalShaderAxisMath"));
                affineProbe=Marshal.GetDelegateForFunctionPointer<Probe>(NativeLibrary.GetExport(library,"OriginalShaderAffineMath"));
            }
            catch { NativeLibrary.Free(library); throw; }
        }
        private int Invoke(double[] input,uint inputCount,float[] output,uint outputCount,uint[] traits,Probe? selected=null)
        {
            var a=GCHandle.Alloc(input,GCHandleType.Pinned);
            try
            {
                var b=GCHandle.Alloc(output,GCHandleType.Pinned);
                try
                {
                    var c=GCHandle.Alloc(traits,GCHandleType.Pinned);
                    try { return (selected??probe)(a.AddrOfPinnedObject(),inputCount,b.AddrOfPinnedObject(),outputCount,c.AddrOfPinnedObject(),5); }
                    finally {c.Free();}
                }
                finally {b.Free();}
            }
            finally {a.Free();}
        }
        internal (float[] Values,uint[] Traits) Capture(double[] input)
        {
            var values=new float[128]; var traits=new uint[5];
            if(Invoke(input,14,values,128,traits)!=1) throw new InvalidOperationException("Original SDK capture failed.");
            return(values,traits);
        }
        internal int VerifyAtomicControls()
        {
            for(int mode=0;mode<3;++mode)
            {
                double[] input=ShaderAxisOracle.Cases()[0].Values;
                var values=Enumerable.Repeat(3.25f,128).ToArray(); var traits=Enumerable.Repeat(777U,5).ToArray();
                if(mode==0) input[0]=double.NaN;
                if(Invoke(input,mode==1 ? 13U : 14U,values,mode==2 ? 127U : 128U,traits)!=0 ||
                    values.Any(x=>x!=3.25f) || traits.Any(x=>x!=777U))
                    throw new InvalidOperationException("SDK companion rejection was not atomic.");
            }
            return 3;
        }
        internal (float[] Values,uint[] Traits) CaptureAffine(double[] input)
        {
            if(input.Length!=24) throw new InvalidOperationException("Wrong affine original input count.");
            var values=new float[156]; var traits=new uint[5];
            if(Invoke(input,24,values,156,traits,affineProbe)!=1) throw new InvalidOperationException("Original affine SDK capture failed.");
            return(values,traits);
        }
        internal int VerifyAffineAtomicControls(double[] original)
        {
            for(int mode=0;mode<4;++mode)
            {
                var input=(double[])original.Clone();
                var values=Enumerable.Repeat(3.25f,156).ToArray(); var traits=Enumerable.Repeat(777U,5).ToArray();
                if(mode==0) input[0]=double.NaN;
                if(mode==3) input[22]=0;
                if(Invoke(input,mode==1?23U:24U,values,mode==2?155U:156U,traits,affineProbe)!=0 ||
                    values.Any(x=>x!=3.25f) || traits.Any(x=>x!=777U))
                    throw new InvalidOperationException("Affine SDK companion rejection was not atomic.");
            }
            return 4;
        }
        public void Dispose()=>NativeLibrary.Free(library);
    }
}
