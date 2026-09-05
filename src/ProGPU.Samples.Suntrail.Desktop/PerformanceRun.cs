using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Rendering;

namespace ProGPU.Samples.Suntrail.Desktop;

/// <summary>Bounded, input-driven Release workload. Raw per-frame samples are retained.</summary>
internal sealed class PerformanceRun(string path, int frames, long launched)
{
    private readonly double[] _interval = new double[frames], _cpu = new double[frames], _animation = new double[frames], _compositor = new double[frames];
    private int _frame;
    private long _allocated, _uploaded;
    private double _firstFrame;
    public void Attach(GameView view, Window window)
    {
        var pipeline=(ProceduralPipeline)window.Compositor!.GetDrawingExtension(ProceduralDrawingContextExtensions.Definition)!;
        window.Rendering += (_,delta) =>
        {
            if(_frame==1) _firstFrame=Stopwatch.GetElapsedTime(launched).TotalMilliseconds;
            if(_frame==240) { _allocated=GC.GetTotalAllocatedBytes(true);_uploaded=pipeline.UploadedBytes; }
            int sample=_frame++-240;
            if(sample<0)return;
            if(sample<frames)
            {
                _interval[sample]=delta*1000;var m=window.FrameMetrics;
                _cpu[sample]=m.TotalTimeMs;_animation[sample]=m.AnimationTimeMs;_compositor[sample]=m.CompositorTimeMs;
                return;
            }
            long allocated=GC.GetTotalAllocatedBytes(true)-_allocated;
            long uploaded=pipeline.UploadedBytes-_uploaded;
            var process=Process.GetCurrentProcess();process.Refresh();
            window.WgpuContext!.TryCaptureNativeResourceSnapshot(out var native);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using(var writer=new StreamWriter(path+".csv"))
            {
                writer.WriteLine("frame,interval_ms,host_frame_ms,simulation_and_art_ms,compositor_ms");
                for(int i=0;i<frames;i++)writer.WriteLine(FormattableString.Invariant($"{i},{_interval[i]:F6},{_cpu[i]:F6},{_animation[i]:F6},{_compositor[i]:F6}"));
            }
            Array.Sort(_interval);Array.Sort(_cpu);Array.Sort(_animation);Array.Sort(_compositor);
            double P(double[] data,double p)=>data[Math.Clamp((int)Math.Ceiling(data.Length*p)-1,0,data.Length-1)];
            string result=FormattableString.Invariant($"SUNTRAIL frames={frames} warmup=240 adapter={window.WgpuContext.AdapterName} firstFrameMs={_firstFrame:F2} logicalSize={window.Width}x{window.Height} framebufferSize={window.SilkWindow?.FramebufferSize} occlusion={view.Surface.Batch.EnableBackgroundOcclusion}\nintervalMs p50={P(_interval,.5):F3} p95={P(_interval,.95):F3} p99={P(_interval,.99):F3} max={_interval[^1]:F3}\nhostFrameMs p50={P(_cpu,.5):F3} p95={P(_cpu,.95):F3} p99={P(_cpu,.99):F3}\nsimulationAndArtMs p50={P(_animation,.5):F3} p95={P(_animation,.95):F3} p99={P(_animation,.99):F3}\ncompositorMs p50={P(_compositor,.5):F3} p95={P(_compositor,.95):F3} p99={P(_compositor,.99):F3}\nallocatedBytes={allocated} spriteUploadBytes={uploaded} uploadBytesPerFrame={(double)uploaded/frames:F1} visibleSprites={view.Surface.Batch.Count}\nworkingSetBytes={process.WorkingSet64} privateBytes={process.PrivateMemorySize64} managedHeapBytes={GC.GetTotalMemory(false)} metalAllocatedBytes={native.MetalAllocatedBytes} nativeBuffers={native.Buffers.KeptFromUser} nativeTextures={native.Textures.KeptFromUser}\nlevel={view.Surface.Session.Level.Index+1} gameSeconds={view.Surface.Session.Time:F3} deaths={view.Surface.Session.Deaths}\n");
            File.WriteAllText(path+".txt",result);Console.WriteLine(result);window.Close();
        };
    }
}
