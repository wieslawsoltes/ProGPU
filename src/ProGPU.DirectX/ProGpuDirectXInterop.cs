namespace ProGPU.DirectX;

public sealed record ProGpuDirectXNativeHandle(
    string Kind,
    IntPtr Handle,
    bool OwnsHandle = false);

public interface IProGpuDirectXNativeInterop
{
    bool TryGetNativeHandle(ProGpuDirectXResource resource, out ProGpuDirectXNativeHandle handle);
    bool TryGetShaderHandle(ProGpuDirectXShader shader, out ProGpuDirectXNativeHandle handle);
    bool TryGetGraphicsPipelineHandle(ProGpuDirectXGraphicsPipeline pipeline, out ProGpuDirectXNativeHandle handle);
    bool TryGetComputePipelineHandle(ProGpuDirectXComputePipeline pipeline, out ProGpuDirectXNativeHandle handle);
}

public sealed unsafe class ProGpuDirectXBackendInterop : IProGpuDirectXNativeInterop
{
    public bool TryGetNativeHandle(ProGpuDirectXResource resource, out ProGpuDirectXNativeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(resource);

        switch (resource)
        {
            case ProGpuDirectXTexture2D { BackendTexture.TexturePtr: not null } texture:
                handle = new ProGpuDirectXNativeHandle("WebGPU.Texture", (IntPtr)texture.BackendTexture.TexturePtr);
                return true;
            case ProGpuDirectXBuffer { BackendBuffer.BufferPtr: not null } buffer:
                handle = new ProGpuDirectXNativeHandle("WebGPU.Buffer", (IntPtr)buffer.BackendBuffer.BufferPtr);
                return true;
            default:
                handle = new ProGpuDirectXNativeHandle("None", IntPtr.Zero);
                return false;
        }
    }

    public bool TryGetShaderHandle(ProGpuDirectXShader shader, out ProGpuDirectXNativeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(shader);

        if (shader.BackendShaderModuleHandle != IntPtr.Zero)
        {
            handle = new ProGpuDirectXNativeHandle("WebGPU.ShaderModule", shader.BackendShaderModuleHandle);
            return true;
        }

        handle = new ProGpuDirectXNativeHandle("None", IntPtr.Zero);
        return false;
    }

    public bool TryGetGraphicsPipelineHandle(ProGpuDirectXGraphicsPipeline pipeline, out ProGpuDirectXNativeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        if (pipeline.BackendPipelineHandle != IntPtr.Zero)
        {
            handle = new ProGpuDirectXNativeHandle("WebGPU.RenderPipeline", pipeline.BackendPipelineHandle);
            return true;
        }

        handle = new ProGpuDirectXNativeHandle("None", IntPtr.Zero);
        return false;
    }

    public bool TryGetComputePipelineHandle(ProGpuDirectXComputePipeline pipeline, out ProGpuDirectXNativeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        if (pipeline.BackendPipelineHandle != IntPtr.Zero)
        {
            handle = new ProGpuDirectXNativeHandle("WebGPU.ComputePipeline", pipeline.BackendPipelineHandle);
            return true;
        }

        handle = new ProGpuDirectXNativeHandle("None", IntPtr.Zero);
        return false;
    }
}
