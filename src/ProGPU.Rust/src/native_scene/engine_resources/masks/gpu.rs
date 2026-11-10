//! Immutable original GPU mask work; providers run only before encoding.
use super::*;
use crate::native_scene::engine_gpu::{
    NativeGpuUniforms, NativeMaskBinding, NativePipelineCache, NativeRenderPipeline,
};
use std::{future::Future, pin::Pin};
use wgpu::util::DeviceExt;
mod clip;
mod compose;
mod paint;
mod picture_cache;
/// The command engine must render this exact owned nested source into the
/// supplied frame/format. Returned textures retain all actual producer leases.
/// Preflight excludes the output allocation already counted by the mask request.
pub trait NativePictureMaskResolver {
    /// A complete native runtime owns the original bounded raster cache itself.
    /// Other providers retain the existing mask-engine cache behavior.
    fn owns_raster_cache(&self) -> bool {
        false
    }
    fn preflight(
        &self,
        source: &NativePictureMaskSource,
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
    ) -> Result<NativeMaskMetrics, Error>;
    fn prepare<'a>(
        &'a mut self,
        source: NativePictureMaskSource,
        device: &'a wgpu::Device,
        queue: &'a wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Pin<Box<dyn Future<Output = Result<NativePictureMaskCapture, Error>> + 'a>>;
}
/// Completed exact-frame nested capture and actual child preparation work.
/// Child metrics exclude this capture's output texture, counted by the mask.
pub struct NativePictureMaskCapture {
    pub texture: NativeImageTexture,
    pub metrics: NativeMaskMetrics,
    /// Actual newly allocated output bytes; zero on a retained raster hit.
    pub output_texture_bytes: u64,
}
enum Program {
    None,
    Paint(paint::PreparedPaint),
    Composite {
        children: Vec<PreparedNode>,
        program: compose::PreparedCompose,
    },
}
struct PreparedNode {
    texture: NativeImageTexture,
    binding: Arc<NativeMaskBinding>,
    program: Program,
    metrics: NativeMaskMetrics,
}
impl PreparedNode {
    fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        match &self.program {
            Program::None => {}
            Program::Paint(p) => p.encode(encoder),
            Program::Composite { children, program } => {
                for child in children {
                    child.encode(encoder);
                }
                program.encode(encoder, children);
            }
        }
    }
    fn retain_submitted(&self, queue: &wgpu::Queue) {
        self.texture.retain_submitted(queue);
        if let Program::Composite { children, .. } = &self.program {
            for child in children {
                child.retain_submitted(queue);
            }
        }
    }
}
pub struct PreparedNativeMask {
    request: Arc<NativeMaskRequest>,
    node: PreparedNode,
    metrics: NativeMaskMetrics,
}
impl PreparedNativeMask {
    pub fn request(&self) -> &Arc<NativeMaskRequest> {
        &self.request
    }
    pub fn binding(&self) -> &Arc<NativeMaskBinding> {
        &self.node.binding
    }
    pub fn source(&self) -> &NativeImageTexture {
        &self.node.texture
    }
    pub fn metrics(&self) -> NativeMaskMetrics {
        self.metrics
    }
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        self.node.encode(encoder)
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        self.node.retain_submitted(queue);
        let owner = self.request.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
pub struct NativeMaskEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    target_format: wgpu::TextureFormat,
    engine_flags: u64,
    pipelines: Option<NativePipelineCache>,
    raster: super::super::path::NativePathPageEngine,
    clip_pipelines: Option<clip::Pipelines>,
    dummy: Option<NativeImageTexture>,
    samplers: Option<[wgpu::Sampler; 2]>,
    composition: Option<compose::Pipelines>,
    pictures: picture_cache::PictureCache,
    clips: Option<clip::Cache>,
    clip_atlas_size: u32,
}
impl NativeMaskEngine {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_format: wgpu::TextureFormat,
        engine_flags: u64,
    ) -> Result<Self, Error> {
        if !matches!(
            target_format,
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
        ) {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            target_format,
            engine_flags,
            pipelines: None,
            raster: super::super::path::NativePathPageEngine::new(device),
            clip_pipelines: None,
            dummy: None,
            samplers: None,
            composition: None,
            pictures: Default::default(),
            clips: None,
            clip_atlas_size: 1024,
        })
    }
    pub fn preflight(
        &self,
        request: &NativeMaskRequest,
        resolver: &dyn NativePictureMaskResolver,
    ) -> Result<NativeMaskMetrics, Error> {
        let mut metrics = request.preflight()?;
        let limits = self.device.limits();
        if request
            .target
            .size()
            .into_iter()
            .any(|n| n > limits.max_texture_dimension_2d)
            || limits.max_bind_groups < 4
            || limits.max_vertex_attributes < 8
            || limits.max_vertex_buffer_array_stride < 96
            || limits.max_uniform_buffer_binding_size < 288
            || limits.max_storage_buffer_binding_size < 256
        {
            return Err(Error::Capacity);
        }
        preflight_resources(
            &request.node,
            request,
            resolver,
            &self.device,
            self.target_format,
            self.clip_atlas_size,
            &mut metrics,
        )?;
        Ok(metrics)
    }
    async fn initialize(&mut self) -> Result<(), Error> {
        if self.pipelines.is_none() {
            self.pipelines =
                Some(NativePipelineCache::new(&self.device, wgpu::TextureFormat::R8Unorm).await?);
        }
        if self.samplers.is_none() {
            self.samplers = Some([sampler(&self.device, false), sampler(&self.device, true)]);
        }
        if self.dummy.is_none() {
            let texture = texture(&self.device, [1, 1], wgpu::TextureUsages::COPY_DST);
            self.queue.write_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: &texture,
                    mip_level: 0,
                    origin: wgpu::Origin3d::ZERO,
                    aspect: wgpu::TextureAspect::All,
                },
                &[255],
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(1),
                    rows_per_image: Some(1),
                },
                texture.size(),
            );
            self.dummy = Some(owned(&self.device, &texture)?);
        }
        Ok(())
    }
    /// Admit all source/device costs before allocation or source acquisition.
    /// Vector compute/transfer and nested picture captures complete here; owned
    /// brush/composite render passes remain explicit in `PreparedNativeMask::encode`.
    pub async fn prepare(
        &mut self,
        request: Arc<NativeMaskRequest>,
        resolver: &mut dyn NativePictureMaskResolver,
    ) -> Result<PreparedNativeMask, Error> {
        self.preflight(&request, resolver)?;
        let cold = self.dummy.is_none();
        self.initialize().await?;
        let node = self.prepare_node(&request.node, &request, resolver).await?;
        let mut metrics = node.metrics;
        if cold {
            metrics.add(NativeMaskMetrics {
                texture_bytes: 1,
                texture_allocation_bytes: 1,
                texture_allocations: 1,
                texture_upload_bytes: 1,
                ..Default::default()
            })?;
        }
        Ok(PreparedNativeMask {
            request,
            node,
            metrics,
        })
    }
    fn prepare_node<'a>(
        &'a mut self,
        node: &'a MaskNode,
        request: &'a NativeMaskRequest,
        resolver: &'a mut dyn NativePictureMaskResolver,
    ) -> Pin<Box<dyn Future<Output = Result<PreparedNode, Error>> + 'a>> {
        Box::pin(async move {
            let mut metrics = NativeMaskMetrics::default();
            let (texture, uniforms, chain, sampling, program) = match node {
                MaskNode::Analytic(source) => (
                    self.dummy.as_ref().ok_or(Error::ResourceDomain)?.clone(),
                    sampling::analytic(source, request.target)?,
                    None,
                    MaskSampling::Linear,
                    Program::None,
                ),
                MaskNode::Chain(source) => {
                    let primary = sampling::analytic(&source.masks[0], request.target)?;
                    let mut chain = [MaskUniforms::zeroed(); 3];
                    for index in 1..source.mask_count as usize {
                        chain[index - 1] =
                            sampling::analytic(&source.masks[index], request.target)?;
                    }
                    (
                        self.dummy.as_ref().ok_or(Error::ResourceDomain)?.clone(),
                        primary,
                        Some(chain),
                        MaskSampling::Linear,
                        Program::None,
                    )
                }
                MaskNode::Coverage(source, bytes) => {
                    metrics.texture_bytes = u64::from(source.width) * u64::from(source.height);
                    metrics.texture_allocation_bytes = metrics.texture_bytes;
                    metrics.texture_allocations = 1;
                    metrics.texture_upload_bytes = bytes.len() as u64;
                    if source.width > self.device.limits().max_texture_dimension_2d
                        || source.height > self.device.limits().max_texture_dimension_2d
                    {
                        return Err(Error::Capacity);
                    }
                    let texture = texture(
                        &self.device,
                        [source.width, source.height],
                        wgpu::TextureUsages::COPY_DST,
                    );
                    self.queue.write_texture(
                        wgpu::TexelCopyTextureInfo {
                            texture: &texture,
                            mip_level: 0,
                            origin: wgpu::Origin3d::ZERO,
                            aspect: wgpu::TextureAspect::All,
                        },
                        bytes,
                        wgpu::TexelCopyBufferLayout {
                            offset: 0,
                            bytes_per_row: Some(source.row_bytes),
                            rows_per_image: Some(source.height),
                        },
                        texture.size(),
                    );
                    (
                        owned(&self.device, &texture)?,
                        sampling::coverage(source, request.target)?,
                        None,
                        if source.sampling == IMAGE_SAMPLING_NEAREST {
                            MaskSampling::Nearest
                        } else {
                            MaskSampling::Linear
                        },
                        Program::None,
                    )
                }
                MaskNode::Vector(source) => {
                    let revision = clip::revision(request)?;
                    let (texture, actual) = if let Some(cache) = self
                        .clips
                        .as_ref()
                        .filter(|c| c.matches(revision, request.target))
                    {
                        (
                            cache.prepared.texture.clone(),
                            NativeMaskMetrics {
                                texture_bytes: cache.prepared.metrics.texture_bytes,
                                clip_cache_hits: 1,
                                ..Default::default()
                            },
                        )
                    } else {
                        let plan = Arc::new(super::clip::plan_with_atlas(
                            source,
                            request.target,
                            self.clip_atlas_size,
                        )?);
                        let atlas_size = plan.atlas_size();
                        let mut prepared = clip::prepare(self, plan, request.target.size()).await?;
                        prepared.texture = retain_source(prepared.texture, request.scene.clone())?;
                        let prepared = Arc::new(prepared);
                        let mut encoder =
                            self.device
                                .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                                    label: Some("Native immutable completed clip mask"),
                                });
                        prepared.encode(&mut encoder);
                        self.queue.submit([encoder.finish()]);
                        prepared.retain_submitted(&self.queue);
                        let texture = prepared.texture.clone();
                        let mut metrics = prepared.metrics;
                        metrics.submission_count = metrics
                            .submission_count
                            .checked_add(1)
                            .ok_or(Error::Capacity)?;
                        self.clip_atlas_size = atlas_size;
                        self.clips = Some(clip::Cache::new(revision, request, prepared));
                        (texture, metrics)
                    };
                    metrics = actual;
                    (
                        texture,
                        sampling::full(request.target.size(), source.header.opacity),
                        None,
                        MaskSampling::Linear,
                        Program::None,
                    )
                }
                MaskNode::Brush(source) => {
                    let plan = brush::compile_brush(source, request)?;
                    let opacity = plan.opacity;
                    metrics.geometry_upload_bytes = plan.byte_cost();
                    metrics.uniform_upload_bytes = 224;
                    metrics.texture_bytes =
                        u64::from(request.target.size()[0]) * u64::from(request.target.size()[1]);
                    metrics.texture_allocation_bytes = metrics.texture_bytes;
                    metrics.texture_allocations = 1;
                    metrics.render_passes = 1;
                    let prepared = paint::prepare(self, plan, request.target).await?;
                    (
                        prepared.texture.clone(),
                        sampling::full(request.target.size(), opacity),
                        None,
                        MaskSampling::Linear,
                        Program::Paint(prepared),
                    )
                }
                MaskNode::Geometry(source) => {
                    let plan = brush::compile_geometry(source, request)?;
                    let opacity = plan.opacity;
                    metrics.geometry_upload_bytes = plan.byte_cost();
                    metrics.uniform_upload_bytes = 224;
                    metrics.texture_bytes =
                        u64::from(request.target.size()[0]) * u64::from(request.target.size()[1]);
                    metrics.texture_allocation_bytes = metrics.texture_bytes;
                    metrics.texture_allocations = 1;
                    metrics.render_passes = 1;
                    let prepared = paint::prepare(self, plan, request.target).await?;
                    (
                        prepared.texture.clone(),
                        sampling::full(request.target.size(), opacity),
                        None,
                        MaskSampling::Linear,
                        Program::Paint(prepared),
                    )
                }
                MaskNode::Picture(descriptor, scene) => {
                    let (source, uniforms) = sampling::picture(descriptor, scene.clone(), request)?;
                    metrics.texture_bytes =
                        u64::from(source.frame.width) * u64::from(source.frame.height) * 4;
                    let texture = if let Some(texture) = (!resolver.owns_raster_cache())
                        .then(|| self.pictures.find(&source, self.engine_flags))
                        .flatten()
                    {
                        metrics.picture_cache_hits = 1;
                        metrics.texture_bytes = 0;
                        texture
                    } else {
                        let capture = resolver
                            .prepare(
                                source.clone(),
                                &self.device,
                                &self.queue,
                                self.target_format,
                            )
                            .await?;
                        let texture = retain_source(capture.texture, source.scene.clone())?;
                        if texture.device() != &self.device
                            || texture.size() != [source.frame.width, source.frame.height]
                            || texture.texture().format() != self.target_format
                        {
                            return Err(Error::ResourceDomain);
                        }
                        metrics.texture_bytes = capture.output_texture_bytes;
                        metrics.texture_allocation_bytes = capture.output_texture_bytes;
                        metrics.texture_allocations = u64::from(capture.output_texture_bytes != 0);
                        if !resolver.owns_raster_cache() {
                            metrics.picture_captures = 1;
                        }
                        let mut child = capture.metrics;
                        child.submission_count = 0;
                        metrics.add(child)?;
                        if !resolver.owns_raster_cache() {
                            self.pictures
                                .retain(source, self.engine_flags, texture.clone());
                        }
                        texture
                    };
                    (texture, uniforms, None, MaskSampling::Linear, Program::None)
                }
                MaskNode::Composite { opacity, children } => {
                    let mut prepared = allocate(children.len())?;
                    for child in children.iter() {
                        prepared.push(self.prepare_node(child, request, resolver).await?);
                    }
                    metrics.texture_bytes = u64::from(request.target.size()[0])
                        * u64::from(request.target.size()[1])
                        * 2;
                    metrics.texture_allocation_bytes = metrics.texture_bytes;
                    metrics.texture_allocations = 2;
                    metrics.uniform_upload_bytes = children.len() as u64 * 256;
                    metrics.render_passes = children.len() as u32;
                    for child in &prepared {
                        metrics.add(child.metrics)?;
                    }
                    let program = compose::prepare(self, &prepared, request.target.size()).await?;
                    (
                        program.texture.clone(),
                        sampling::full(request.target.size(), *opacity),
                        None,
                        MaskSampling::Linear,
                        Program::Composite {
                            children: prepared,
                            program,
                        },
                    )
                }
            };
            let texture = retain_source(texture, request.scene.clone())?;
            let pipelines = self.pipelines.as_ref().ok_or(Error::ResourceDomain)?;
            let sampler = &self.samplers.as_ref().ok_or(Error::ResourceDomain)?
                [usize::from(sampling == MaskSampling::Linear)];
            let binding = Arc::new(
                NativeMaskBinding::prepare(pipelines, texture.clone(), sampler, uniforms, chain)
                    .await?,
            );
            metrics.uniform_upload_bytes += binding.upload_bytes();
            Ok(PreparedNode {
                texture,
                binding,
                program,
                metrics,
            })
        })
    }
}
fn preflight_resources(
    node: &MaskNode,
    request: &NativeMaskRequest,
    resolver: &dyn NativePictureMaskResolver,
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    clip_atlas_size: u32,
    metrics: &mut NativeMaskMetrics,
) -> Result<(), Error> {
    let limits = device.limits();
    let buffers = |bytes: u64| {
        if bytes > limits.max_buffer_size {
            Err(Error::Capacity)
        } else {
            Ok(())
        }
    };
    let paint = |plan: brush::BrushPlan| -> Result<(), Error> {
        buffers(plan.vertices.len() as u64 * 56)?;
        buffers(plan.indices.len() as u64 * 4)?;
        let stops = (plan.stops.len() as u64 * 32).max(32);
        buffers(stops)?;
        if stops > u64::from(limits.max_storage_buffer_binding_size) {
            return Err(Error::Capacity);
        }
        Ok(())
    };
    match node {
        MaskNode::Picture(descriptor, scene) => {
            let (source, _) = sampling::picture(descriptor, scene.clone(), request)?;
            if source.frame.width > limits.max_texture_dimension_2d
                || source.frame.height > limits.max_texture_dimension_2d
            {
                return Err(Error::Capacity);
            }
            metrics.add(resolver.preflight(&source, device, format)?)?;
        }
        MaskNode::Coverage(source, _) => {
            if source.width > limits.max_texture_dimension_2d
                || source.height > limits.max_texture_dimension_2d
            {
                return Err(Error::Capacity);
            }
        }
        MaskNode::Brush(source) => paint(brush::compile_brush(source, request)?)?,
        MaskNode::Geometry(source) => paint(brush::compile_geometry(source, request)?)?,
        MaskNode::Vector(source) => {
            let minimum = super::clip::plan(source, request.target)?.atlas_size();
            let plan = super::clip::plan_with_atlas(source, request.target, clip_atlas_size)?;
            metrics.texture_bytes = metrics
                .texture_bytes
                .checked_sub(u64::from(minimum).pow(2))
                .and_then(|n| n.checked_add(u64::from(plan.atlas_size()).pow(2)))
                .ok_or(Error::Capacity)?;
            let raster =
                super::super::path::NativePathPageEngine::new(device).preflight_plan(&plan)?;
            metrics.geometry_upload_bytes = metrics
                .geometry_upload_bytes
                .checked_add(raster.outline_upload_bytes)
                .ok_or(Error::Capacity)?;
            metrics.uniform_upload_bytes = metrics
                .uniform_upload_bytes
                .checked_add(raster.uniform_upload_bytes)
                .ok_or(Error::Capacity)?;
            metrics.compute_passes = metrics
                .compute_passes
                .checked_add(u32::try_from(raster.compute_passes).map_err(|_| Error::Capacity)?)
                .ok_or(Error::Capacity)?;
            metrics.coverage_staging_bytes = metrics
                .coverage_staging_bytes
                .checked_add(raster.coverage_staging_bytes)
                .ok_or(Error::Capacity)?;
            if plan.atlas_size() > limits.max_texture_dimension_2d {
                return Err(Error::Capacity);
            }
            buffers(plan.uniforms().len() as u64 * 256)?;
            buffers(plan.vertices().len() as u64 * 16)?;
            if limits.max_sampled_textures_per_shader_stage < 3
                || limits.min_uniform_buffer_offset_alignment > 256
            {
                return Err(Error::UnsupportedExecution);
            }
        }
        MaskNode::Composite { children, .. } => {
            buffers(children.len() as u64 * 256)?;
            if limits.max_sampled_textures_per_shader_stage < 3
                || limits.min_uniform_buffer_offset_alignment > 256
            {
                return Err(Error::UnsupportedExecution);
            }
            for child in children.iter() {
                preflight_resources(
                    child,
                    request,
                    resolver,
                    device,
                    format,
                    clip_atlas_size,
                    metrics,
                )?;
            }
        }
        _ => {}
    }
    Ok(())
}
fn sampler(device: &wgpu::Device, linear: bool) -> wgpu::Sampler {
    device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("Native mask source sampling"),
        mag_filter: if linear {
            wgpu::FilterMode::Linear
        } else {
            wgpu::FilterMode::Nearest
        },
        min_filter: if linear {
            wgpu::FilterMode::Linear
        } else {
            wgpu::FilterMode::Nearest
        },
        ..Default::default()
    })
}
fn texture(device: &wgpu::Device, size: [u32; 2], usage: wgpu::TextureUsages) -> wgpu::Texture {
    device.create_texture(&wgpu::TextureDescriptor {
        label: Some("Native immutable mask storage"),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::R8Unorm,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | usage,
        view_formats: &[],
    })
}
fn owned(device: &wgpu::Device, texture: &wgpu::Texture) -> Result<NativeImageTexture, Error> {
    NativeImageTexture::from_view(
        device,
        texture,
        &texture.create_view(&Default::default()),
        [texture.width(), texture.height()],
        None,
    )
}
fn buffer(
    device: &wgpu::Device,
    label: &str,
    bytes: &[u8],
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    if bytes.is_empty() {
        return device.create_buffer(&wgpu::BufferDescriptor {
            label: Some(label),
            size: 4,
            usage,
            mapped_at_creation: false,
        });
    }
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents: bytes,
        usage,
    })
}
fn begin<'a>(
    encoder: &'a mut wgpu::CommandEncoder,
    view: &'a wgpu::TextureView,
) -> wgpu::RenderPass<'a> {
    encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some("Native retained mask production"),
        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
            view,
            depth_slice: None,
            resolve_target: None,
            ops: wgpu::Operations {
                load: wgpu::LoadOp::Clear(wgpu::Color {
                    r: 0.0,
                    g: 0.0,
                    b: 0.0,
                    a: 1.0,
                }),
                store: wgpu::StoreOp::Store,
            },
        })],
        ..Default::default()
    })
}

fn retain_source(
    texture: NativeImageTexture,
    source: Arc<NativeScene>,
) -> Result<NativeImageTexture, Error> {
    NativeImageTexture::from_view(
        texture.device(),
        texture.texture(),
        texture.view(),
        texture.size(),
        Some(Arc::new((texture.clone(), source))),
    )
}
