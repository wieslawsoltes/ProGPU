using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Browser;

/// <summary>
/// Minimal retained scene that exercises the same IWebGpuApi seam as the production renderer.
/// It remains intentionally small so browser-backend bring-up failures are isolated from gallery UI.
/// </summary>
public unsafe sealed class BrowserSmokeScene : IDisposable
{
    private static readonly string ShaderSource = ShaderResource.Load(typeof(BrowserSmokeScene), "BrowserSmoke.wgsl");
    private readonly BrowserWebGpuApi _api = new();
    private ShaderModule* _shader;
    private RenderPipeline* _pipeline;
    private bool _initialized;

    public void Initialize(string canvasFormat)
    {
        if (_initialized) return;

        var shaderPointer = Marshal.StringToCoTaskMemUTF8(ShaderSource);
        var vertexEntryPointer = Marshal.StringToCoTaskMemUTF8("vsMain");
        var fragmentEntryPointer = Marshal.StringToCoTaskMemUTF8("fsMain");
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
                Code = (byte*)shaderPointer
            };
            var shaderDescriptor = new ShaderModuleDescriptor { NextInChain = &wgsl.Chain };
            _shader = _api.DeviceCreateShaderModule(BrowserWebGpuApi.DeviceHandle, &shaderDescriptor);

            var vertex = new VertexState
            {
                Module = _shader,
                EntryPoint = (byte*)vertexEntryPointer
            };
            var target = new ColorTargetState
            {
                Format = ParseCanvasFormat(canvasFormat),
                WriteMask = ColorWriteMask.All
            };
            var fragment = new FragmentState
            {
                Module = _shader,
                EntryPoint = (byte*)fragmentEntryPointer,
                TargetCount = 1,
                Targets = &target
            };
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Vertex = vertex,
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None
                },
                Multisample = new MultisampleState
                {
                    Count = 1,
                    Mask = uint.MaxValue
                },
                Fragment = &fragment
            };
            _pipeline = _api.DeviceCreateRenderPipeline(BrowserWebGpuApi.DeviceHandle, &pipelineDescriptor);
            _api.Flush();
            _initialized = true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(shaderPointer);
            Marshal.FreeCoTaskMem(vertexEntryPointer);
            Marshal.FreeCoTaskMem(fragmentEntryPointer);
        }
    }

    public void Render(float pulse)
    {
        if (!_initialized) throw new InvalidOperationException("The browser smoke scene is not initialized.");

        var surfaceTexture = new SurfaceTexture();
        _api.SurfaceGetCurrentTexture(BrowserWebGpuApi.SurfaceHandle, &surfaceTexture);
        var view = _api.TextureCreateView(surfaceTexture.Texture, null);
        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = _api.DeviceCreateCommandEncoder(BrowserWebGpuApi.DeviceHandle, &encoderDescriptor);
        var colorAttachment = new RenderPassColorAttachment
        {
            View = view,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            DepthSlice = uint.MaxValue,
            ClearValue = new Color { R = 0.012, G = 0.017, B = 0.036 + pulse * 0.018, A = 1.0 }
        };
        var passDescriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment
        };
        var pass = _api.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
        _api.RenderPassEncoderSetPipeline(pass, _pipeline);
        _api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
        _api.RenderPassEncoderEnd(pass);
        var commandBufferDescriptor = new CommandBufferDescriptor();
        var commandBuffer = _api.CommandEncoderFinish(encoder, &commandBufferDescriptor);
        _api.QueueSubmit(BrowserWebGpuApi.QueueHandle, 1, &commandBuffer);

        _api.RenderPassEncoderRelease(pass);
        _api.CommandBufferRelease(commandBuffer);
        _api.CommandEncoderRelease(encoder);
        _api.TextureViewRelease(view);
        _api.TextureRelease(surfaceTexture.Texture);
        _api.SurfacePresent(BrowserWebGpuApi.SurfaceHandle);
    }

    public void Dispose()
    {
        if (_pipeline != null) _api.RenderPipelineRelease(_pipeline);
        if (_shader != null) _api.ShaderModuleRelease(_shader);
        _api.Dispose();
        _pipeline = null;
        _shader = null;
        _initialized = false;
    }

    private static TextureFormat ParseCanvasFormat(string format) => format switch
    {
        "bgra8unorm" => TextureFormat.Bgra8Unorm,
        "rgba8unorm" => TextureFormat.Rgba8Unorm,
        _ => throw new NotSupportedException($"The browser canvas format '{format}' is unsupported.")
    };
}
