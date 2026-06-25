namespace ProGPU.DirectX;

public enum ProGpuDirectXSciChartTextureFormat
{
    Bgra8,
    Float32,
    UInt32,
    Int32
}

public enum ProGpuDirectXSciChartTextureFiltering
{
    Point,
    Linear
}

public sealed record ProGpuDirectXSciChartTextureDraw(
    ProGpuDirectXSciChartTexture2D Texture,
    DxRect ViewportRect,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    bool IsUniform);

public readonly record struct ProGpuDirectXSciChartTextureVertex(
    float X,
    float Y,
    float U,
    float V,
    uint ColorArgb);

public readonly record struct ProGpuDirectXSciChartVertexTransform(bool SwapAxis = false);

public sealed record ProGpuDirectXSciChartTextureVertexDraw(
    ProGpuDirectXSciChartTexture2D Texture,
    IReadOnlyList<ProGpuDirectXSciChartTextureVertex> Vertices,
    ProGpuDirectXSciChartVertexTransform Transform,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartShapedHeatmapDraw(
    ProGpuDirectXSciChartTexture2D HeightsTexture,
    ProGpuDirectXSciChartTexture2D GradientTexture,
    IReadOnlyList<ProGpuDirectXSciChartTextureVertex> Vertices,
    double ColorMapMin,
    double ColorMapMax,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartHeightTextureContoursDraw(
    ProGpuDirectXSciChartTexture2D HeightsTexture,
    ProGpuDirectXSciChartTexture2D ContourTexture,
    DxRect ViewportRect,
    DxColor Color,
    float ZMin,
    float ZMax,
    float ZStep,
    float StrokeThickness,
    float Opacity,
    DxRect? ClipRect);

public sealed class ProGpuDirectXSciChartTexture2D : IDisposable
{
    private ProGpuDirectXBuffer? _floatDataBuffer;
    private bool _isDisposed;

    internal ProGpuDirectXSciChartTexture2D(
        ProGpuDirectXTexture2D resource,
        ProGpuDirectXSciChartTextureFormat textureFormat)
    {
        Resource = resource;
        TextureFormat = textureFormat;
    }

    public ProGpuDirectXTexture2D Resource { get; }

    public uint Width => Resource.Width;

    public uint Height => Resource.Height;

    public ProGpuDirectXSciChartTextureFormat TextureFormat { get; }

    public ulong Generation => Resource.Generation;

    public void SetData(ReadOnlySpan<int> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Bgra8)
        {
            throw new InvalidOperationException("SciChart int texture data is supported only for Bgra8 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
    }

    public void SetFloatData(ReadOnlySpan<float> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new InvalidOperationException("SciChart float texture data is supported only for Float32 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
        var buffer = GetOrCreateFloatDataBuffer();
        buffer.Write(colorData[..ExpectedElementCount]);
    }

    public void SetUIntData(ReadOnlySpan<uint> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.UInt32)
        {
            throw new InvalidOperationException("SciChart uint texture data is supported only for UInt32 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
    }

    public void SetIntData(ReadOnlySpan<int> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Int32)
        {
            throw new InvalidOperationException("SciChart signed int texture data is supported only for Int32 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
    }

    public byte[] ReadPixels()
    {
        ThrowIfDisposed();
        return Resource.ReadPixels();
    }

    private void ValidateElementCount(int elementCount)
    {
        if (elementCount < ExpectedElementCount)
        {
            throw new ArgumentException($"Texture data contains {elementCount} element(s), expected at least {ExpectedElementCount}.");
        }
    }

    private int ExpectedElementCount => checked((int)(Width * Height));

    internal ProGpuDirectXBuffer GetFloatDataBuffer()
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new InvalidOperationException("Only SciChart Float32 textures have a height data buffer.");
        }

        return GetOrCreateFloatDataBuffer();
    }

    private ProGpuDirectXBuffer GetOrCreateFloatDataBuffer()
    {
        if (_floatDataBuffer is { IsDisposed: false } buffer)
        {
            return buffer;
        }

        buffer = Resource.Device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(ExpectedElementCount * sizeof(float))),
            Usage = DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = sizeof(float),
            Label = $"SciChartFloatTextureData {Width}x{Height}"
        });
        buffer.Write(new float[ExpectedElementCount]);
        _floatDataBuffer = buffer;
        return buffer;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed || Resource.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXSciChartTexture2D));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Resource.Dispose();
        _floatDataBuffer?.Dispose();
        _isDisposed = true;
    }
}

public sealed class ProGpuDirectXSciChartRenderContext2D : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly ProGpuDirectXDeviceContext _context;
    private readonly List<IDisposable> _transientResources = new();
    private readonly List<ProGpuDirectXSciChartTextureDraw> _textureDraws = new();
    private readonly List<ProGpuDirectXSciChartTextureVertexDraw> _textureVertexDraws = new();
    private readonly List<ProGpuDirectXSciChartShapedHeatmapDraw> _shapedHeatmapDraws = new();
    private readonly List<ProGpuDirectXSciChartHeightTextureContoursDraw> _heightTextureContourDraws = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _texturePipelines = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _textureVertexPipelines = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _shapedHeatmapPipelines = new();
    private readonly Dictionary<DxResourceFormat, ProGpuDirectXGraphicsPipeline> _heightContourPipelines = new();
    private readonly Dictionary<ProGpuDirectXSciChartTextureFiltering, ProGpuDirectXSamplerState> _samplers = new();
    private ProGpuDirectXShader? _textureVertexShader;
    private ProGpuDirectXShader? _texturePixelShader;
    private ProGpuDirectXInputLayout? _textureInputLayout;
    private ProGpuDirectXShader? _batchedTextureVertexShader;
    private ProGpuDirectXShader? _batchedTexturePixelShader;
    private ProGpuDirectXInputLayout? _batchedTextureInputLayout;
    private ProGpuDirectXShader? _shapedHeatmapPixelShader;
    private ProGpuDirectXShader? _heightContourPixelShader;
    private DxRect? _clipRect;
    private bool _isDisposed;

    public ProGpuDirectXSciChartRenderContext2D(
        ProGpuDirectXDevice device,
        uint width,
        uint height,
        DxResourceFormat renderTargetFormat = DxResourceFormat.R8G8B8A8Unorm)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = device.CreateImmediateContext();
        RenderTarget = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = width,
            Height = height,
            Format = renderTargetFormat,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read,
            Label = "SciChartRenderTarget"
        });
    }

    public ProGpuDirectXDevice Device => _device;

    public ProGpuDirectXDeviceContext ImmediateContext => _context;

    public ProGpuDirectXTexture2D RenderTarget { get; }

    public IReadOnlyList<ProGpuDirectXSciChartTextureDraw> TextureDraws => _textureDraws;

    public IReadOnlyList<ProGpuDirectXSciChartTextureVertexDraw> TextureVertexDraws => _textureVertexDraws;

    public IReadOnlyList<ProGpuDirectXSciChartShapedHeatmapDraw> ShapedHeatmapDraws => _shapedHeatmapDraws;

    public IReadOnlyList<ProGpuDirectXSciChartHeightTextureContoursDraw> HeightTextureContourDraws => _heightTextureContourDraws;

    public ProGpuDirectXSciChartTexture2D CreateTexture(
        uint width,
        uint height,
        ProGpuDirectXSciChartTextureFormat textureFormat = ProGpuDirectXSciChartTextureFormat.Bgra8)
    {
        ThrowIfDisposed();
        var resource = _device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = width,
            Height = height,
            Format = ToDxFormat(textureFormat),
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopySource | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read,
            Label = "SciChartTexture"
        });

        return new ProGpuDirectXSciChartTexture2D(resource, textureFormat);
    }

    public void Clear(DxColor color)
    {
        ThrowIfDisposed();
        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.ClearRenderTarget(RenderTarget, color);
    }

    public void BeginFrame()
    {
        ThrowIfDisposed();
        _textureDraws.Clear();
        _textureVertexDraws.Clear();
        _shapedHeatmapDraws.Clear();
        _heightTextureContourDraws.Clear();
        _clipRect = null;
    }

    public void SetClipRect(DxRect? clipRect)
    {
        ThrowIfDisposed();
        if (clipRect is { } rect)
        {
            ValidateDrawRect(rect);
            _clipRect = ClampClipRect(rect);
        }
        else
        {
            _clipRect = null;
        }
    }

    public void DrawTexture(
        ProGpuDirectXSciChartTexture2D texture,
        DxRect viewportRect,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear,
        bool isUniform = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        ValidateDrawRect(viewportRect);
        ValidateDrawableTexture(texture);

        var effectiveRect = isUniform
            ? FitUniform(texture, viewportRect)
            : viewportRect;
        if (HasEmptyClip)
        {
            return;
        }

        var vertexBuffer = CreateTextureQuadVertexBuffer(effectiveRect, texture);
        var shaderResourceView = _device.CreateShaderResourceView(texture.Resource);
        var sampler = GetSampler(filtering);
        var pipeline = GetTexturePipeline(RenderTarget.Descriptor.Format, filtering);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, shaderResourceView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(6);

        _textureDraws.Add(new ProGpuDirectXSciChartTextureDraw(texture, effectiveRect, filtering, isUniform));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(shaderResourceView);
    }

    public void DrawTextureVertices(
        ReadOnlySpan<ProGpuDirectXSciChartTextureVertex> vertices,
        int startIndex,
        int count,
        ProGpuDirectXSciChartTexture2D texture,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        ValidateDrawableTexture(texture);
        ValidateVertexRange(vertices.Length, startIndex, count);

        if (HasEmptyClip)
        {
            return;
        }

        var copiedVertices = vertices.Slice(startIndex, count).ToArray();
        var vertexBuffer = CreateBatchedTextureVertexBuffer(copiedVertices, transform);
        var shaderResourceView = _device.CreateShaderResourceView(texture.Resource);
        var sampler = GetSampler(filtering);
        var pipeline = GetBatchedTexturePipeline(RenderTarget.Descriptor.Format, filtering);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, shaderResourceView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(checked((uint)count));

        _textureVertexDraws.Add(new ProGpuDirectXSciChartTextureVertexDraw(
            texture,
            copiedVertices,
            transform,
            filtering,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(shaderResourceView);
    }

    public void DrawShapedHeatmap(
        ReadOnlySpan<ProGpuDirectXSciChartTextureVertex> vertices,
        int startIndex,
        int count,
        double colorMapMin,
        double colorMapMax,
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D gradientTexture,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(heightsTexture);
        ArgumentNullException.ThrowIfNull(gradientTexture);
        ValidateShapedHeatmapTextures(heightsTexture, gradientTexture);
        ValidateColorMapRange(colorMapMin, colorMapMax);
        ValidateVertexRange(vertices.Length, startIndex, count);

        if (HasEmptyClip)
        {
            return;
        }

        var copiedVertices = vertices.Slice(startIndex, count).ToArray();
        var vertexBuffer = CreateBatchedTextureVertexBuffer(copiedVertices, default);
        var heightsView = _device.CreateShaderResourceView(
            heightsTexture.GetFloatDataBuffer(),
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                Format = DxResourceFormat.R32Float,
                ElementCount = checked(heightsTexture.Width * heightsTexture.Height),
                ElementStrideInBytes = sizeof(float),
                Label = "SciChart Shaped Heatmap Heights"
            });
        var gradientView = _device.CreateShaderResourceView(gradientTexture.Resource);
        var constants = CreateShapedHeatmapConstants(colorMapMin, colorMapMax, heightsTexture.Width, heightsTexture.Height);
        var sampler = GetSampler(filtering);
        var pipeline = GetShapedHeatmapPipeline(RenderTarget.Descriptor.Format, filtering);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, heightsView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, gradientView);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, constants);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(checked((uint)count));

        _shapedHeatmapDraws.Add(new ProGpuDirectXSciChartShapedHeatmapDraw(
            heightsTexture,
            gradientTexture,
            copiedVertices,
            colorMapMin,
            colorMapMax,
            filtering,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(heightsView);
        _transientResources.Add(gradientView);
        _transientResources.Add(constants);
    }

    public void DrawHeightTextureContours(
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D contourTexture,
        DxRect viewportRect,
        DxColor color,
        float zMin,
        float zMax,
        float zStep,
        float strokeThickness,
        float opacity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(heightsTexture);
        ArgumentNullException.ThrowIfNull(contourTexture);
        ValidateDrawRect(viewportRect);
        ValidateHeightTextureContourInputs(heightsTexture, contourTexture, color, zMin, zMax, zStep, strokeThickness, opacity);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexBuffer = CreateTextureQuadVertexBuffer(viewportRect, heightsTexture);
        var heightsView = _device.CreateShaderResourceView(
            heightsTexture.GetFloatDataBuffer(),
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                Format = DxResourceFormat.R32Float,
                ElementCount = checked(heightsTexture.Width * heightsTexture.Height),
                ElementStrideInBytes = sizeof(float),
                Label = "SciChart Height Contour Heights"
            });
        var contourView = _device.CreateShaderResourceView(contourTexture.Resource);
        var constants = CreateHeightTextureContourConstants(
            zMin,
            zMax,
            zStep,
            strokeThickness,
            opacity,
            heightsTexture.Width,
            heightsTexture.Height,
            color);
        var sampler = GetSampler(ProGpuDirectXSciChartTextureFiltering.Linear);
        var pipeline = GetHeightTextureContourPipeline(RenderTarget.Descriptor.Format);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, heightsView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, contourView);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, constants);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(6);

        _heightTextureContourDraws.Add(new ProGpuDirectXSciChartHeightTextureContoursDraw(
            heightsTexture,
            contourTexture,
            viewportRect,
            color,
            zMin,
            zMax,
            zStep,
            strokeThickness,
            opacity,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(heightsView);
        _transientResources.Add(contourView);
        _transientResources.Add(constants);
    }

    public void Flush(bool clearRecordedCommands = true)
    {
        ThrowIfDisposed();
        _context.Flush(clearRecordedCommands);
        DisposeTransientResources();
    }

    public byte[] ReadTargetPixels()
    {
        ThrowIfDisposed();
        return RenderTarget.ReadPixels();
    }

    private DxRect FullRenderTargetRect =>
        new(0, 0, checked((int)RenderTarget.Width), checked((int)RenderTarget.Height));

    private bool HasEmptyClip => _clipRect is { Width: <= 0 } or { Height: <= 0 };

    private ProGpuDirectXBuffer CreateTextureQuadVertexBuffer(
        DxRect viewportRect,
        ProGpuDirectXSciChartTexture2D texture)
    {
        var left = PixelXToNdc(viewportRect.X);
        var right = PixelXToNdc(viewportRect.X + viewportRect.Width);
        var top = PixelYToNdc(viewportRect.Y);
        var bottom = PixelYToNdc(viewportRect.Y + viewportRect.Height);
        ReadOnlySpan<float> vertices =
        [
            left,  top,    0f, 0f,
            right, top,    1f, 0f,
            right, bottom, 1f, 1f,
            left,  top,    0f, 0f,
            right, bottom, 1f, 1f,
            left,  bottom, 0f, 1f
        ];

        var vertexBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(vertices.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = $"SciChartTextureQuad {texture.Width}x{texture.Height}"
        });
        vertexBuffer.Write(vertices);
        return vertexBuffer;
    }

    private ProGpuDirectXBuffer CreateBatchedTextureVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartTextureVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform)
    {
        var vertexData = new float[checked(vertices.Length * 8)];
        for (var i = 0; i < vertices.Length; i++)
        {
            var source = vertices[i];
            var x = transform.SwapAxis ? source.Y : source.X;
            var y = transform.SwapAxis ? source.X : source.Y;
            var offset = i * 8;
            vertexData[offset] = PixelXToNdc(x);
            vertexData[offset + 1] = PixelYToNdc(y);
            vertexData[offset + 2] = source.U;
            vertexData[offset + 3] = source.V;
            WriteColorArgb(vertexData, offset + 4, source.ColorArgb);
        }

        var vertexBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(vertexData.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 32,
            Label = $"SciChartTextureVertices {vertices.Length}"
        });
        vertexBuffer.Write(vertexData);
        return vertexBuffer;
    }

    private ProGpuDirectXBuffer CreateShapedHeatmapConstants(
        double colorMapMin,
        double colorMapMax,
        uint heightTextureWidth,
        uint heightTextureHeight)
    {
        var min = (float)colorMapMin;
        var invRange = 1f / (float)(colorMapMax - colorMapMin);
        ReadOnlySpan<float> constants =
        [
            min,
            invRange,
            heightTextureWidth,
            heightTextureHeight
        ];

        var buffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(constants.Length * sizeof(float))),
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = "SciChart Shaped Heatmap Constants"
        });
        buffer.Write(constants);
        return buffer;
    }

    private ProGpuDirectXBuffer CreateHeightTextureContourConstants(
        float zMin,
        float zMax,
        float zStep,
        float strokeThickness,
        float opacity,
        uint heightTextureWidth,
        uint heightTextureHeight,
        DxColor color)
    {
        ReadOnlySpan<float> constants =
        [
            zMin,
            zMax,
            zStep,
            strokeThickness,
            opacity,
            heightTextureWidth,
            heightTextureHeight,
            0f,
            color.R,
            color.G,
            color.B,
            color.A
        ];

        var buffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(constants.Length * sizeof(float))),
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = "SciChart Height Contour Constants"
        });
        buffer.Write(constants);
        return buffer;
    }

    private float PixelXToNdc(int x)
    {
        return (x / (float)RenderTarget.Width * 2f) - 1f;
    }

    private float PixelYToNdc(int y)
    {
        return 1f - (y / (float)RenderTarget.Height * 2f);
    }

    private float PixelXToNdc(float x)
    {
        return (x / RenderTarget.Width * 2f) - 1f;
    }

    private float PixelYToNdc(float y)
    {
        return 1f - (y / RenderTarget.Height * 2f);
    }

    private ProGpuDirectXGraphicsPipeline GetTexturePipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_texturePipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        _textureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = TextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Texture Vertex"
        });
        _texturePixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateTexturePixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Texture Pixel"
        });
        _textureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Texture Quad Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                }
            ]
        });

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = _textureVertexShader,
            PixelShader = _texturePixelShader,
            InputLayout = _textureInputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            Label = $"SciChart Texture Pipeline {renderTargetFormat} {filtering}"
        });
        _texturePipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetBatchedTexturePipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_textureVertexPipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        _batchedTextureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = BatchedTextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Batched Texture Vertex"
        });
        _batchedTexturePixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateBatchedTexturePixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Batched Texture Pixel"
        });
        _batchedTextureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Batched Texture Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 16,
                    ShaderLocation = 2
                }
            ]
        });

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = _batchedTextureVertexShader,
            PixelShader = _batchedTexturePixelShader,
            InputLayout = _batchedTextureInputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            Label = $"SciChart Batched Texture Pipeline {renderTargetFormat} {filtering}"
        });
        _textureVertexPipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetShapedHeatmapPipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_shapedHeatmapPipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        EnsureBatchedTextureVertexResources();
        _shapedHeatmapPixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateShapedHeatmapPixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Shaped Heatmap Pixel"
        });
        var vertexShader = _batchedTextureVertexShader ?? throw new InvalidOperationException("SciChart heatmap vertex shader was not initialized.");
        var pixelShader = _shapedHeatmapPixelShader ?? throw new InvalidOperationException("SciChart heatmap pixel shader was not initialized.");
        var inputLayout = _batchedTextureInputLayout ?? throw new InvalidOperationException("SciChart heatmap input layout was not initialized.");

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            Label = $"SciChart Shaped Heatmap Pipeline {renderTargetFormat} {filtering}"
        });
        _shapedHeatmapPipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetHeightTextureContourPipeline(DxResourceFormat renderTargetFormat)
    {
        if (_heightContourPipelines.TryGetValue(renderTargetFormat, out var pipeline))
        {
            return pipeline;
        }

        _textureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = TextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Texture Vertex"
        });
        _heightContourPixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateHeightTextureContourPixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Height Contour Pixel"
        });
        _textureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Texture Quad Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                }
            ]
        });
        var vertexShader = _textureVertexShader ?? throw new InvalidOperationException("SciChart contour vertex shader was not initialized.");
        var pixelShader = _heightContourPixelShader ?? throw new InvalidOperationException("SciChart contour pixel shader was not initialized.");
        var inputLayout = _textureInputLayout ?? throw new InvalidOperationException("SciChart contour input layout was not initialized.");

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None },
            Label = $"SciChart Height Contour Pipeline {renderTargetFormat}"
        });
        _heightContourPipelines[renderTargetFormat] = pipeline;
        return pipeline;
    }

    private void EnsureBatchedTextureVertexResources()
    {
        _batchedTextureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = BatchedTextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Batched Texture Vertex"
        });
        _batchedTextureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Batched Texture Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 16,
                    ShaderLocation = 2
                }
            ]
        });
    }

    private ProGpuDirectXSamplerState GetSampler(ProGpuDirectXSciChartTextureFiltering filtering)
    {
        if (_samplers.TryGetValue(filtering, out var sampler))
        {
            return sampler;
        }

        sampler = _device.CreateSamplerState(new DxSamplerDescriptor
        {
            Filter = filtering == ProGpuDirectXSciChartTextureFiltering.Point
                ? DxFilter.MinMagMipPoint
                : DxFilter.MinMagMipLinear,
            AddressU = DxTextureAddressMode.Clamp,
            AddressV = DxTextureAddressMode.Clamp,
            Label = $"SciChart Texture Sampler {filtering}"
        });
        _samplers[filtering] = sampler;
        return sampler;
    }

    private static DxResourceFormat ToDxFormat(ProGpuDirectXSciChartTextureFormat textureFormat)
    {
        return textureFormat switch
        {
            ProGpuDirectXSciChartTextureFormat.Bgra8 => DxResourceFormat.B8G8R8A8Unorm,
            ProGpuDirectXSciChartTextureFormat.Float32 => DxResourceFormat.R32Float,
            ProGpuDirectXSciChartTextureFormat.UInt32 => DxResourceFormat.R32UInt,
            ProGpuDirectXSciChartTextureFormat.Int32 => DxResourceFormat.R32SInt,
            _ => throw new ArgumentOutOfRangeException(nameof(textureFormat), textureFormat, null)
        };
    }

    private DxRect FitUniform(ProGpuDirectXSciChartTexture2D texture, DxRect destination)
    {
        var sourceAspect = texture.Width / (float)texture.Height;
        var destinationAspect = destination.Width / (float)destination.Height;
        if (Math.Abs(sourceAspect - destinationAspect) < 0.0001f)
        {
            return destination;
        }

        if (destinationAspect > sourceAspect)
        {
            var width = checked((int)MathF.Round(destination.Height * sourceAspect));
            var x = destination.X + ((destination.Width - width) / 2);
            return destination with { X = x, Width = width };
        }
        else
        {
            var height = checked((int)MathF.Round(destination.Width / sourceAspect));
            var y = destination.Y + ((destination.Height - height) / 2);
            return destination with { Y = y, Height = height };
        }
    }

    private DxRect ClampClipRect(DxRect rect)
    {
        var left = Math.Clamp(rect.X, 0, checked((int)RenderTarget.Width));
        var top = Math.Clamp(rect.Y, 0, checked((int)RenderTarget.Height));
        var right = Math.Clamp(rect.X + rect.Width, left, checked((int)RenderTarget.Width));
        var bottom = Math.Clamp(rect.Y + rect.Height, top, checked((int)RenderTarget.Height));
        return new DxRect(left, top, right - left, bottom - top);
    }

    private static void ValidateDrawRect(DxRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), "SciChart texture draw rectangles must be non-empty.");
        }
    }

    private static void ValidateDrawableTexture(ProGpuDirectXSciChartTexture2D texture)
    {
        if (texture.TextureFormat is not (ProGpuDirectXSciChartTextureFormat.Bgra8 or ProGpuDirectXSciChartTextureFormat.Float32))
        {
            throw new NotSupportedException("SciChart texture drawing currently supports Bgra8 and Float32 sampled textures; integer textures are reserved for compute/height-map paths.");
        }
    }

    private static void ValidateShapedHeatmapTextures(
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D gradientTexture)
    {
        if (heightsTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new NotSupportedException("SciChart shaped heatmap requires a Float32 heights texture.");
        }

        if (gradientTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Bgra8)
        {
            throw new NotSupportedException("SciChart shaped heatmap requires a Bgra8 gradient texture.");
        }
    }

    private static void ValidateColorMapRange(double colorMapMin, double colorMapMax)
    {
        if (!double.IsFinite(colorMapMin) || !double.IsFinite(colorMapMax) || colorMapMax <= colorMapMin)
        {
            throw new ArgumentOutOfRangeException(nameof(colorMapMax), "SciChart shaped heatmap color map range must be finite and increasing.");
        }
    }

    private static void ValidateHeightTextureContourInputs(
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D contourTexture,
        DxColor color,
        float zMin,
        float zMax,
        float zStep,
        float strokeThickness,
        float opacity)
    {
        if (heightsTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new NotSupportedException("SciChart height contours require a Float32 heights texture.");
        }

        if (contourTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Bgra8)
        {
            throw new NotSupportedException("SciChart height contours require a Bgra8 contour texture.");
        }

        if (!float.IsFinite(zMin) || !float.IsFinite(zMax) || !float.IsFinite(zStep) || zMax <= zMin || zStep <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(zStep), "SciChart height contours require finite zMin/zMax and a positive zStep.");
        }

        if (!float.IsFinite(strokeThickness) || strokeThickness <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(strokeThickness), "SciChart height contour stroke thickness must be finite and positive.");
        }

        if (!float.IsFinite(opacity) || opacity < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), "SciChart height contour opacity must be finite and non-negative.");
        }

        if (!float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B) || !float.IsFinite(color.A))
        {
            throw new ArgumentOutOfRangeException(nameof(color), "SciChart height contour color must contain finite channels.");
        }
    }

    private static void ValidateVertexRange(int vertexLength, int startIndex, int count)
    {
        if (startIndex < 0 || startIndex > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), "SciChart vertex start index is outside the supplied vertex span.");
        }

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart vertex draws require at least one vertex.");
        }

        if (count > vertexLength - startIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart vertex count exceeds the supplied vertex span.");
        }
    }

    private static void WriteColorArgb(float[] vertexData, int offset, uint colorArgb)
    {
        vertexData[offset] = ((colorArgb >> 16) & 0xFF) / 255f;
        vertexData[offset + 1] = ((colorArgb >> 8) & 0xFF) / 255f;
        vertexData[offset + 2] = (colorArgb & 0xFF) / 255f;
        vertexData[offset + 3] = ((colorArgb >> 24) & 0xFF) / 255f;
    }

    private static string CreateTexturePixelShader()
    {
        return $$"""
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var SourceTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    return textureSample(SourceTexture, SourceSampler, input.uv);
}
""";
    }

    private static string CreateBatchedTexturePixelShader()
    {
        return $$"""
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var SourceTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    return textureSample(SourceTexture, SourceSampler, input.uv) * input.color;
}
""";
    }

    private static string CreateShapedHeatmapPixelShader()
    {
        return $$"""
struct HeatmapParams {
    colorMapMin: f32,
    colorMapInvRange: f32,
    heightTextureWidth: f32,
    heightTextureHeight: f32,
};

@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetConstantBufferBinding(DxShaderStage.Pixel, 0)}}) var<uniform> Heatmap: HeatmapParams;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var<storage, read> Heights: array<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 1)}}) var GradientTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    let heightSize = vec2<i32>(i32(Heatmap.heightTextureWidth), i32(Heatmap.heightTextureHeight));
    let heightCoord = clamp(
        vec2<i32>(input.uv * vec2<f32>(heightSize)),
        vec2<i32>(0, 0),
        heightSize - vec2<i32>(1, 1));
    let heightIndex = u32(heightCoord.y * heightSize.x + heightCoord.x);
    let height = Heights[heightIndex];
    let gradientU = clamp((height - Heatmap.colorMapMin) * Heatmap.colorMapInvRange, 0.0, 1.0);
    return textureSample(GradientTexture, SourceSampler, vec2<f32>(gradientU, 0.5)) * input.color;
}
""";
    }

    private static string CreateHeightTextureContourPixelShader()
    {
        return $$"""
struct ContourParams {
    zMin: f32,
    zMax: f32,
    zStep: f32,
    strokeThickness: f32,
    opacity: f32,
    heightTextureWidth: f32,
    heightTextureHeight: f32,
    _pad0: f32,
    color: vec4<f32>,
};

@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetConstantBufferBinding(DxShaderStage.Pixel, 0)}}) var<uniform> Contour: ContourParams;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var<storage, read> Heights: array<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 1)}}) var ContourTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

fn load_height(coord: vec2<i32>, heightSize: vec2<i32>) -> f32 {
    let clampedCoord = clamp(coord, vec2<i32>(0, 0), heightSize - vec2<i32>(1, 1));
    let heightIndex = u32(clampedCoord.y * heightSize.x + clampedCoord.x);
    return Heights[heightIndex];
}

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    let heightSize = vec2<i32>(i32(Contour.heightTextureWidth), i32(Contour.heightTextureHeight));
    let centerCoord = clamp(
        vec2<i32>(input.uv * vec2<f32>(heightSize)),
        vec2<i32>(0, 0),
        heightSize - vec2<i32>(1, 1));
    let height = load_height(centerCoord, heightSize);
    if (height < Contour.zMin || height > Contour.zMax) {
        discard;
    }

    let contourIndex = round((height - Contour.zMin) / Contour.zStep);
    let contourHeight = Contour.zMin + contourIndex * Contour.zStep;
    if (contourHeight < Contour.zMin || contourHeight > Contour.zMax) {
        discard;
    }

    let heightDx = abs(load_height(centerCoord + vec2<i32>(1, 0), heightSize) - height);
    let heightDy = abs(load_height(centerCoord + vec2<i32>(0, 1), heightSize) - height);
    let heightPerPixel = max(max(heightDx, heightDy), Contour.zStep * 0.001);
    let threshold = heightPerPixel * max(Contour.strokeThickness, 1.0);
    let distanceToContour = abs(height - contourHeight);
    var lineAlpha = 1.0 - smoothstep(threshold, threshold * 2.0, distanceToContour);
    let contourMask = textureSample(ContourTexture, SourceSampler, input.uv);
    lineAlpha = lineAlpha * clamp(Contour.opacity, 0.0, 1.0) * Contour.color.a * contourMask.a;
    if (lineAlpha <= 0.001) {
        discard;
    }

    return vec4<f32>(Contour.color.rgb, lineAlpha);
}
""";
    }

    private static string TextureVertexShader => """
struct VertexIn {
    @location(0) position: vec2<f32>,
    @location(1) uv: vec2<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    output.position = vec4<f32>(input.position, 0.0, 1.0);
    output.uv = input.uv;
    return output;
}
""";

    private static string BatchedTextureVertexShader => """
struct VertexIn {
    @location(0) position: vec2<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    output.position = vec4<f32>(input.position, 0.0, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}
""";

    private void DisposeTransientResources()
    {
        foreach (var resource in _transientResources)
        {
            resource.Dispose();
        }

        _transientResources.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXSciChartRenderContext2D));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        DisposeTransientResources();
        foreach (var pipeline in _texturePipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _textureVertexPipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _shapedHeatmapPipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _heightContourPipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var sampler in _samplers.Values)
        {
            sampler.Dispose();
        }

        _textureVertexShader?.Dispose();
        _texturePixelShader?.Dispose();
        _batchedTextureVertexShader?.Dispose();
        _batchedTexturePixelShader?.Dispose();
        _shapedHeatmapPixelShader?.Dispose();
        _heightContourPixelShader?.Dispose();
        _context.Dispose();
        RenderTarget.Dispose();
        _isDisposed = true;
    }
}
