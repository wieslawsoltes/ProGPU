//! Original managed effect layouts, blend/alpha policy and immutable GPU draws.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha, ImageSampling},
    mask::MaskUniforms,
    path::ExecutionPreference,
    scene::LayerBlend,
};
use wgpu::util::DeviceExt;

/// Exact actual-target mask contract for managed shaders. Sampled masks remain
/// sampled ownership; only `from_vector` records original typed vector coverage.
#[derive(Clone, Debug)]
pub struct ManagedShaderMask {
    target: SampleLattice,
    uniforms: MaskUniforms,
    image: Option<GpuImage>,
    sampling: ImageSampling,
}
impl ManagedShaderMask {
    /// Retain actual sampled coverage and its complete original 96-byte mapping.
    /// Native axis-area kind five must be materialized before this boundary.
    pub fn sampled(
        target: SampleLattice,
        uniforms: MaskUniforms,
        image: GpuImage,
        sampling: ImageSampling,
    ) -> Result<Self, Error> {
        let mask = Self {
            target,
            uniforms,
            image: Some(image),
            sampling,
        };
        mask.validate()?;
        if uniforms.options[0] != 1.0 {
            return Err(Error::InvalidInput);
        }
        Ok(mask)
    }
    /// Retain original rounded-rectangle, rounded-stroke or rectangular-hole
    /// analytic coverage; no texture bounds are used as source geometry proof.
    pub fn analytic(target: SampleLattice, uniforms: MaskUniforms) -> Result<Self, Error> {
        let mask = Self {
            target,
            uniforms,
            image: None,
            sampling: ImageSampling::Linear,
        };
        mask.validate()?;
        if !matches!(uniforms.options[0], 0.0 | 2.0 | 3.0 | 4.0) {
            return Err(Error::InvalidInput);
        }
        Ok(mask)
    }
    /// Use an original path-produced full-target mask without changing its target
    /// origin or interpreting an unrelated alpha image as vector provenance.
    pub fn from_vector(mask: &ShaderVectorMask) -> Result<Self, Error> {
        let target = mask.target();
        Self::sampled(
            target,
            MaskUniforms {
                coordinate1: [
                    1.0 / target.size[0] as f32,
                    1.0 / target.size[1] as f32,
                    0.0,
                    0.0,
                ],
                options: [1.0, 1.0, 0.0, 1.0],
                ..Zeroable::zeroed()
            },
            mask.image().clone(),
            ImageSampling::Nearest,
        )
    }
    /// Exact target identity retained with these original coordinates.
    pub fn target(&self) -> SampleLattice {
        self.target
    }
    /// Exact shader layout, preserved without generic-mask reinterpretation.
    pub fn uniforms(&self) -> &MaskUniforms {
        &self.uniforms
    }
    /// Actual sampled resource, absent for analytic coverage.
    pub fn image(&self) -> Option<&GpuImage> {
        self.image.as_ref()
    }
    /// Original nearest/linear coverage filter.
    pub fn sampling(&self) -> ImageSampling {
        self.sampling
    }
    fn validate(&self) -> Result<(), Error> {
        let u = &self.uniforms;
        if self.target.size.contains(&0)
            || bytemuck::cast_slice::<MaskUniforms, f32>(std::slice::from_ref(u))
                .iter()
                .any(|v| !v.is_finite())
            || !matches!(u.options[0], 0.0 | 1.0 | 2.0 | 3.0 | 4.0)
            || !(0.0..=1.0).contains(&u.options[1])
            || !matches!(
                self.sampling,
                ImageSampling::Nearest | ImageSampling::Linear
            )
            || (u.options[0] == 1.0) != self.image.is_some()
        {
            return Err(Error::InvalidInput);
        }
        if u.options[0] >= 2.0
            && (u.bounds[2] < u.bounds[0]
                || u.bounds[3] < u.bounds[1]
                || u.radii_x.iter().chain(&u.radii_y).any(|r| *r < 0.0)
                || u.options[0] == 3.0 && u.options[2] < 0.0)
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
}

/// One complete actual-target managed effect request. All resources are copied
/// or independently retained before the resulting consumer draw is published.
pub struct ManagedShaderRequest<'a> {
    /// Exact immutable managed parameters and ordered sampler metadata.
    pub parameters: &'a ManagedShaderParameters,
    /// Original output edges, UVs, placement and output opacity.
    pub quad: ManagedShaderQuad,
    /// Actual target storage lattice, including cropped-target origin.
    pub target: SampleLattice,
    /// Actual original 4x4 column-storage projection bytes consumed by WGSL.
    pub projection: [f32; 16],
    /// Actual physical viewport x/y/width/height, not normalized coordinates.
    pub viewport: [f32; 4],
    /// Original managed fixed-function blend contract.
    pub blend: LayerBlend,
    /// Actual output coverage, applied once after the user shader.
    pub mask: Option<&'a ManagedShaderMask>,
}
/// Actual owned resource inventory; counts are separate from timing measurements.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ManagedShaderMetrics {
    /// Complete generated WGSL source retained by the selected program.
    pub shader_source_bytes: u64,
    /// Private immutable parameter, projection and optional mask uniform bytes.
    pub uniform_bytes: u64,
    /// Four consumed vertex records per visible quad.
    pub vertex_bytes: u64,
    /// Original six u32 quad indices per visible draw.
    pub index_bytes: u64,
    /// Actual active user sampler bindings, excluding the optional output mask.
    pub sampler_bindings: u32,
    /// Actual consumer draws, zero for absent textures or fully clipped geometry.
    pub draws: u32,
}
impl ManagedShaderMetrics {
    /// Add complete inventories with atomic overflow rejection.
    pub(crate) fn merge(&mut self, value: Self) -> Result<(), Error> {
        let merged = Self {
            shader_source_bytes: self
                .shader_source_bytes
                .checked_add(value.shader_source_bytes)
                .ok_or(Error::Capacity)?,
            uniform_bytes: self
                .uniform_bytes
                .checked_add(value.uniform_bytes)
                .ok_or(Error::Capacity)?,
            vertex_bytes: self
                .vertex_bytes
                .checked_add(value.vertex_bytes)
                .ok_or(Error::Capacity)?,
            index_bytes: self
                .index_bytes
                .checked_add(value.index_bytes)
                .ok_or(Error::Capacity)?,
            sampler_bindings: self
                .sampler_bindings
                .checked_add(value.sampler_bindings)
                .ok_or(Error::Capacity)?,
            draws: self.draws.checked_add(value.draws).ok_or(Error::Capacity)?,
        };
        *self = merged;
        Ok(())
    }
}
/// Failure belongs to an exact original program/target variant. Changing source
/// bytes or a source key naturally creates an independent preparation attempt.
#[derive(Clone, Debug)]
pub struct ManagedShaderFailure {
    /// Original source generation that failed.
    pub program: Arc<ManagedShaderProgram>,
    /// Actual WebGPU diagnostic from the default compiler options.
    pub message: Arc<str>,
}
#[derive(Clone)]
struct ProgramKey {
    program: Arc<ManagedShaderProgram>,
    registers: Vec<u32>,
    include_mask: bool,
    source_alpha: ImageAlpha,
    pipeline_alpha: ImageAlpha,
    blend: LayerBlend,
}
impl ProgramKey {
    fn matches(&self, other: &Self) -> bool {
        self.program.source() == other.program.source()
            && self.program.key() == other.program.key()
            && self.registers == other.registers
            && self.include_mask == other.include_mask
            && self.source_alpha == other.source_alpha
            && self.pipeline_alpha == other.pipeline_alpha
            && self.blend == other.blend
    }
}
struct ManagedProgram {
    key: ProgramKey,
    source: String,
    projection: wgpu::BindGroupLayout,
    parameters: wgpu::BindGroupLayout,
    sources: wgpu::BindGroupLayout,
    mask: Option<wgpu::BindGroupLayout>,
    pipeline: wgpu::RenderPipeline,
}
struct DrawResources {
    program: Arc<ManagedProgram>,
    projection: wgpu::BindGroup,
    parameters: wgpu::BindGroup,
    sources: wgpu::BindGroup,
    mask: Option<wgpu::BindGroup>,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
}
/// Immutable target-specific managed shader draw with independently retained
/// source generation and GPU program, valid beyond engine-cache eviction.
pub struct PreparedManagedShader {
    owner: Arc<()>,
    resources: Option<DrawResources>,
    parameters: ManagedShaderParameters,
    mask: Option<ManagedShaderMask>,
    target: SampleLattice,
    viewport: [f32; 4],
    metrics: ManagedShaderMetrics,
}
impl PreparedManagedShader {
    /// Exact retained source parameter generation.
    pub fn parameters(&self) -> &ManagedShaderParameters {
        &self.parameters
    }
    /// Exact prepared output coverage generation.
    pub fn mask(&self) -> Option<&ManagedShaderMask> {
        self.mask.as_ref()
    }
    /// Actual target storage lattice used during preparation.
    pub fn target(&self) -> SampleLattice {
        self.target
    }
    /// Actual prepared inventory; drawing never rewrites uniforms or resources.
    pub fn metrics(&self) -> ManagedShaderMetrics {
        self.metrics
    }
    /// Replay without depending on a renderer's replaceable engine cache.
    pub(crate) fn draw_owned(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scissor: Option<[u32; 4]>,
    ) -> Result<ManagedShaderMetrics, Error> {
        let [x, y, w, h] = scissor.unwrap_or([0, 0, self.target.size[0], self.target.size[1]]);
        if w == 0
            || h == 0
            || x.checked_add(w).is_none_or(|v| v > self.target.size[0])
            || y.checked_add(h).is_none_or(|v| v > self.target.size[1])
        {
            return Err(Error::InvalidInput);
        }
        if let Some(r) = &self.resources {
            pass.set_pipeline(&r.program.pipeline);
            pass.set_bind_group(0, &r.projection, &[]);
            pass.set_bind_group(1, &r.parameters, &[]);
            pass.set_bind_group(2, &r.sources, &[]);
            if let Some(mask) = &r.mask {
                pass.set_bind_group(3, mask, &[]);
            }
            pass.set_vertex_buffer(0, r.vertices.slice(..));
            pass.set_index_buffer(r.indices.slice(..), wgpu::IndexFormat::Uint32);
            let [vx, vy, vw, vh] = self.viewport;
            pass.set_viewport(vx, vy, vw, vh, 0.0, 1.0);
            pass.set_scissor_rect(x, y, w, h);
            pass.draw_indexed(0..6, 0, 0..1);
        }
        Ok(self.metrics)
    }
}

/// Original managed shader effect pipeline with lazy device-owned resources.
/// A bounded exact-source cache never evicts an independently retained draw.
pub struct ManagedShaderEngine {
    owner: Arc<()>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: wgpu::TextureFormat,
    sample_count: u32,
    programs: Vec<Arc<ManagedProgram>>,
    failures: Vec<(ProgramKey, ManagedShaderFailure)>,
    last_failure: Option<ManagedShaderFailure>,
    samplers: Vec<(ImageSampling, wgpu::Sampler)>,
    fallback: Option<GpuImage>,
}
impl ManagedShaderEngine {
    /// Original managed blend table, independent of native destination-read modes.
    pub fn supports_blend(blend: LayerBlend) -> bool {
        blend_state(blend, ImageAlpha::Premultiplied).is_ok()
    }
    /// Retain the actual device and attachment contract; no eager pipelines or
    /// forced compiler flags, alternate drivers, or CPU pixel evaluator.
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
        sample_count: u32,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
        ) || !matches!(
            format,
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::Rgba16Float
        ) || !matches!(sample_count, 1 | 2 | 4 | 8 | 16)
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            queue: queue.clone(),
            format,
            sample_count,
            programs: Vec::new(),
            failures: Vec::new(),
            last_failure: None,
            samplers: Vec::new(),
            fallback: None,
        })
    }
    /// Original exact mask binding capacity, using the current device limits.
    pub fn can_bind_mask(device: &wgpu::Device, active_registers: u32) -> bool {
        let limits = device.limits();
        active_registers.checked_add(1).is_some_and(|count| {
            limits.max_bind_groups >= 4
                && limits.max_sampled_textures_per_shader_stage >= count
                && limits.max_samplers_per_shader_stage >= count
        })
    }
    /// Check an original required output mask before its GPU coverage is
    /// materialized. This never infers a mask from texture bounds or alpha.
    pub fn preflight_mask(
        device: &wgpu::Device,
        parameters: &ManagedShaderParameters,
    ) -> Result<(), Error> {
        parameters.validate()?;
        let mut count = 0;
        for image in parameters.sources() {
            if image.device() != device {
                return Err(Error::ResourceDomain);
            }
            count += 1;
        }
        if !Self::can_bind_mask(device, count) {
            return Err(Error::Capacity);
        }
        Ok(())
    }
    /// Complete CPU-only target/resource/layout admission before renderer mutation.
    /// Shader syntax and device pipeline support are admitted later by wgpu.
    pub fn preflight_request(
        device: &wgpu::Device,
        request: &ManagedShaderRequest<'_>,
    ) -> Result<(), Error> {
        request.parameters.validate()?;
        request.quad.validate()?;
        let limits = device.limits();
        let [w, h] = request.target.size;
        let [x, y, vw, vh] = request.viewport;
        if w == 0
            || h == 0
            || w > limits.max_texture_dimension_2d
            || h > limits.max_texture_dimension_2d
            || request
                .projection
                .iter()
                .chain(&request.viewport)
                .any(|v| !v.is_finite())
            || x < 0.0
            || y < 0.0
            || vw <= 0.0
            || vh <= 0.0
            || x + vw > w as f32
            || y + vh > h as f32
        {
            return Err(Error::InvalidInput);
        }
        blend_state(request.blend, ImageAlpha::Premultiplied)?;
        let mut count = 0;
        for image in request.parameters.sources() {
            if image.device() != device {
                return Err(Error::ResourceDomain);
            }
            count += 1;
        }
        if count > limits.max_sampled_textures_per_shader_stage
            || count > limits.max_samplers_per_shader_stage
            || limits.max_bind_groups < 3
            || limits.max_bindings_per_bind_group < count * 2
            || limits.max_uniform_buffer_binding_size < 560
        {
            return Err(Error::Capacity);
        }
        if let Some(mask) = request.mask {
            mask.validate()?;
            if mask.target != request.target
                || mask
                    .image
                    .as_ref()
                    .is_some_and(|image| image.device() != device)
            {
                return Err(Error::ResourceDomain);
            }
            if !Self::can_bind_mask(device, count) {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
    /// Exact last failed original source generation and actual device diagnostic.
    pub fn last_failure(&self) -> Option<&ManagedShaderFailure> {
        self.last_failure.as_ref()
    }
    /// Clear only failed compilation records after an explicit retry decision.
    /// Live successful/prepared resources are independent of this diagnostic cache.
    pub fn clear_failures(&mut self) {
        self.failures.clear();
        self.last_failure = None;
    }

    fn sampler(&mut self, sampling: ImageSampling) -> wgpu::Sampler {
        if let Some((_, sampler)) = self.samplers.iter().find(|(mode, _)| *mode == sampling) {
            return sampler.clone();
        }
        let [mag, min, mut mip] = sampling.filters();
        // Original GetTextureSampler routes Linear/Cubic to _atlasSampler:
        // its mip filter is linear and its maximum LOD is one, not zero.
        if matches!(sampling, ImageSampling::Linear | ImageSampling::Cubic) {
            mip = true;
        }
        let sampler = self.device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ProGPU original managed effect sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: if mag {
                wgpu::FilterMode::Linear
            } else {
                wgpu::FilterMode::Nearest
            },
            min_filter: if min {
                wgpu::FilterMode::Linear
            } else {
                wgpu::FilterMode::Nearest
            },
            mipmap_filter: if mip {
                wgpu::MipmapFilterMode::Linear
            } else {
                wgpu::MipmapFilterMode::Nearest
            },
            lod_min_clamp: 0.0,
            lod_max_clamp: if sampling.uses_mipmaps() { 32.0 } else { 1.0 },
            anisotropy_clamp: 1,
            ..Default::default()
        });
        self.samplers.push((sampling, sampler.clone()));
        sampler
    }
    fn fallback(&mut self) -> Result<GpuImage, Error> {
        if let Some(image) = &self.fallback {
            return Ok(image.clone());
        }
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU managed transparent fallback"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        self.queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &[0, 0, 0, 0],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(4),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
        );
        let image = GpuImage::from_texture(&self.device, &texture, ImageAlpha::Premultiplied)?;
        self.fallback = Some(image.clone());
        Ok(image)
    }
    async fn program(&mut self, key: ProgramKey) -> Result<Arc<ManagedProgram>, Error> {
        if let Some(program) = self.programs.iter().find(|p| p.key.matches(&key)) {
            return Ok(program.clone());
        }
        if let Some((_, failure)) = self
            .failures
            .iter()
            .find(|(failed, _)| failed.matches(&key))
        {
            self.last_failure = Some(failure.clone());
            return Err(Error::UnsupportedExecution);
        }
        let evict = if self.programs.len() >= 64 {
            Some(
                self.programs
                    .iter()
                    .position(|p| Arc::strong_count(p) == 1)
                    .ok_or(Error::Capacity)?,
            )
        } else {
            None
        };
        let source = key.program.assemble(
            &key.registers,
            key.include_mask,
            key.source_alpha,
            key.pipeline_alpha,
        )?;
        let blend = blend_state(key.blend, key.pipeline_alpha)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let projection = uniform_layout(
            &self.device,
            "ProGPU managed projection layout",
            192,
            wgpu::ShaderStages::VERTEX,
        );
        let parameters = uniform_layout(
            &self.device,
            "ProGPU managed effect layout",
            560,
            wgpu::ShaderStages::FRAGMENT,
        );
        let entries: Vec<_> = (0..key.registers.len())
            .flat_map(|slot| {
                [
                    sampler_layout(slot as u32 * 2),
                    texture_layout(slot as u32 * 2 + 1),
                ]
            })
            .collect();
        let sources = self
            .device
            .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("ProGPU managed active registers"),
                entries: &entries,
            });
        let mask = key.include_mask.then(|| {
            self.device
                .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("ProGPU original managed coverage layout"),
                    entries: &[
                        sampler_layout(0),
                        texture_layout(1),
                        uniform_entry(2, 96, wgpu::ShaderStages::FRAGMENT),
                    ],
                })
        });
        let mut layouts = vec![Some(&projection), Some(&parameters), Some(&sources)];
        if let Some(mask) = &mask {
            layouts.push(Some(mask));
        }
        let layout = self
            .device
            .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU managed shader pipeline layout"),
                bind_group_layouts: &layouts,
                immediate_size: 0,
            });
        let module = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some(key.program.key()),
                source: wgpu::ShaderSource::Wgsl(source.clone().into()),
            });
        let pipeline = self.device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:Some("ProGPU original managed shader effect"),layout:Some(&layout),
            vertex:wgpu::VertexState {module:&module,entry_point:Some("vs_main"),compilation_options:Default::default(),
                buffers:&[Some(wgpu::VertexBufferLayout {array_stride:32,step_mode:wgpu::VertexStepMode::Vertex,
                    attributes:&wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2]})]},
            fragment:Some(wgpu::FragmentState {module:&module,entry_point:Some("fs_main"),compilation_options:Default::default(),
                targets:&[Some(wgpu::ColorTargetState {format:self.format,blend:Some(blend),write_mask:wgpu::ColorWrites::ALL})]}),
            primitive:Default::default(),depth_stencil:None,
            multisample:wgpu::MultisampleState {count:self.sample_count,..Default::default()},multiview_mask:None,cache:None,
        });
        if let Some(error) = scope.pop().await {
            let failure = ManagedShaderFailure {
                program: key.program.clone(),
                message: Arc::from(error.to_string()),
            };
            self.last_failure = Some(failure.clone());
            if self.failures.len() == 64 {
                self.failures.remove(0);
            }
            self.failures.push((key, failure));
            return Err(error.into());
        }
        let program = Arc::new(ManagedProgram {
            key,
            source,
            projection,
            parameters,
            sources,
            mask,
            pipeline,
        });
        if let Some(index) = evict {
            self.programs.remove(index);
        }
        self.programs.push(program.clone());
        self.last_failure = None;
        Ok(program)
    }
    /// Prepare the final-device draw, retaining exact sources and private uniform
    /// buffers. No effect output texture is created, filtered or resampled.
    pub async fn prepare(
        &mut self,
        request: ManagedShaderRequest<'_>,
    ) -> Result<PreparedManagedShader, Error> {
        Self::preflight_request(&self.device, &request)?;
        let mut result = PreparedManagedShader {
            owner: self.owner.clone(),
            resources: None,
            parameters: request.parameters.clone(),
            mask: request.mask.cloned(),
            target: request.target,
            viewport: request.viewport,
            metrics: ManagedShaderMetrics::default(),
        };
        let Some(vertices) = request.quad.vertices()? else {
            return Ok(result);
        };
        let Some(primary) = request.parameters.primary() else {
            return Ok(result);
        };
        let registers = request.parameters.active_registers();
        let include_mask = Self::can_bind_mask(&self.device, registers.len() as u32);
        let source_alpha = primary.alpha();
        let pipeline_alpha = if matches!(
            request.blend,
            LayerBlend::DstOver | LayerBlend::Multiply | LayerBlend::Screen
        ) {
            ImageAlpha::Premultiplied
        } else {
            source_alpha
        };
        let program = self
            .program(ProgramKey {
                program: request.parameters.descriptor().program.clone(),
                registers,
                include_mask,
                source_alpha,
                pipeline_alpha,
                blend: request.blend,
            })
            .await?;
        let mut params = request
            .parameters
            .uniform_floats(request.quad.bounds, primary.size());
        let mask_size = request
            .mask
            .and_then(|m| m.image())
            .map_or(request.target.size, GpuImage::size);
        params[137] = (mask_size[0] as f32).max(1.0);
        params[138] = (mask_size[1] as f32).max(1.0);
        params[139] = if request.mask.is_some_and(|m| m.uniforms.options[0] != 0.0) {
            1.0
        } else {
            0.0
        };
        let mut projection = [0.0_f32; 48];
        projection[..16].copy_from_slice(&request.projection);
        // Canonical header declares the two remaining original matrices even
        // though this vertex entry consumes only projection.
        for offset in [16, 32] {
            for diagonal in [0, 5, 10, 15] {
                projection[offset + diagonal] = 1.0;
            }
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let projection_binding = uniform_binding(
            &self.device,
            &program.projection,
            bytemuck::cast_slice(&projection),
        );
        let parameter_binding = uniform_binding(
            &self.device,
            &program.parameters,
            bytemuck::cast_slice(&params),
        );
        let mut views = Vec::with_capacity(program.key.registers.len());
        let mut samplers = Vec::with_capacity(program.key.registers.len());
        for register in &program.key.registers {
            // The immutable parameter generation cannot lose a selected sampler.
            let (image, sampling) = request
                .parameters
                .sampler(*register)
                .ok_or(Error::ResourceDomain)?;
            views.push(image.texture().create_view(&Default::default()));
            samplers.push(self.sampler(sampling));
        }
        let mut bindings = Vec::with_capacity(views.len() * 2);
        for index in 0..views.len() {
            bindings.push(wgpu::BindGroupEntry {
                binding: index as u32 * 2,
                resource: wgpu::BindingResource::Sampler(&samplers[index]),
            });
            bindings.push(wgpu::BindGroupEntry {
                binding: index as u32 * 2 + 1,
                resource: wgpu::BindingResource::TextureView(&views[index]),
            });
        }
        let source_binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU immutable managed samplers"),
            layout: &program.sources,
            entries: &bindings,
        });
        let mask_binding = if let Some(layout) = &program.mask {
            let fallback;
            let image = if let Some(image) = request.mask.and_then(|m| m.image()) {
                image
            } else {
                fallback = self.fallback()?;
                &fallback
            };
            let view = image.texture().create_view(&Default::default());
            let sampler = self.sampler(request.mask.map_or(ImageSampling::Linear, |m| m.sampling));
            let uniforms = request
                .mask
                .map_or_else(MaskUniforms::zeroed, |m| m.uniforms);
            let buffer = self
                .device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("ProGPU immutable managed coverage uniforms"),
                    contents: bytemuck::bytes_of(&uniforms),
                    usage: wgpu::BufferUsages::UNIFORM,
                });
            Some(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable managed coverage"),
                layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: buffer.as_entire_binding(),
                    },
                ],
            }))
        } else {
            None
        };
        let vertices = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU original managed effect quad"),
                contents: bytemuck::cast_slice(&vertices),
                usage: wgpu::BufferUsages::VERTEX,
            });
        let indices = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU original managed effect indices"),
                contents: bytemuck::cast_slice(&[0_u32, 1, 2, 0, 2, 3]),
                usage: wgpu::BufferUsages::INDEX,
            });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        result.metrics = ManagedShaderMetrics {
            shader_source_bytes: program.source.len() as u64,
            uniform_bytes: 560 + 192 + if include_mask { 96 } else { 0 },
            vertex_bytes: 128,
            index_bytes: 24,
            sampler_bindings: program.key.registers.len() as u32,
            draws: 1,
        };
        result.resources = Some(DrawResources {
            program,
            projection: projection_binding,
            parameters: parameter_binding,
            sources: source_binding,
            mask: mask_binding,
            vertices,
            indices,
        });
        Ok(result)
    }
    /// Draw in the caller's existing pass, preserving painter order and binary scissor.
    pub fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        prepared: &PreparedManagedShader,
        scissor: Option<[u32; 4]>,
    ) -> Result<ManagedShaderMetrics, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) {
            return Err(Error::ResourceDomain);
        }
        prepared.draw_owned(pass, scissor)
    }
}

fn uniform_entry(
    binding: u32,
    size: u64,
    visibility: wgpu::ShaderStages,
) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size: std::num::NonZeroU64::new(size),
        },
        count: None,
    }
}
fn sampler_layout(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count: None,
    }
}
fn texture_layout(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    }
}
fn uniform_layout(
    device: &wgpu::Device,
    label: &str,
    size: u64,
    stages: wgpu::ShaderStages,
) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some(label),
        entries: &[uniform_entry(0, size, stages)],
    })
}
fn uniform_binding(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    bytes: &[u8],
) -> wgpu::BindGroup {
    let buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some("ProGPU immutable managed shader uniform block"),
        contents: bytes,
        usage: wgpu::BufferUsages::UNIFORM,
    });
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU immutable managed shader uniforms"),
        layout,
        entries: &[wgpu::BindGroupEntry {
            binding: 0,
            resource: buffer.as_entire_binding(),
        }],
    })
}
// Original RenderPipelineCache.CreateBlendState, independent of native advanced
// compositing. These factors intentionally retain managed straight-alpha policy.
fn blend_state(mode: LayerBlend, alpha: ImageAlpha) -> Result<wgpu::BlendState, Error> {
    use wgpu::{BlendFactor as F, BlendOperation as O};
    let source = if alpha == ImageAlpha::Premultiplied {
        F::One
    } else {
        F::SrcAlpha
    };
    let (cs, cd, co, as_, ad, ao) = match mode {
        LayerBlend::SrcOver => (
            source,
            F::OneMinusSrcAlpha,
            O::Add,
            F::One,
            F::OneMinusSrcAlpha,
            O::Add,
        ),
        LayerBlend::Src => (source, F::Zero, O::Add, F::One, F::Zero, O::Add),
        LayerBlend::Dst => (F::Zero, F::One, O::Add, F::Zero, F::One, O::Add),
        LayerBlend::SrcIn => (F::DstAlpha, F::Zero, O::Add, F::DstAlpha, F::Zero, O::Add),
        LayerBlend::DstIn => (F::Zero, F::SrcAlpha, O::Add, F::Zero, F::SrcAlpha, O::Add),
        LayerBlend::SrcOut => (
            F::OneMinusDstAlpha,
            F::Zero,
            O::Add,
            F::OneMinusDstAlpha,
            F::Zero,
            O::Add,
        ),
        LayerBlend::DstOut => (
            F::Zero,
            F::OneMinusSrcAlpha,
            O::Add,
            F::Zero,
            F::OneMinusSrcAlpha,
            O::Add,
        ),
        LayerBlend::SrcAtop => (
            F::DstAlpha,
            F::OneMinusSrcAlpha,
            O::Add,
            F::DstAlpha,
            F::OneMinusSrcAlpha,
            O::Add,
        ),
        LayerBlend::DstAtop => (
            F::OneMinusDstAlpha,
            F::SrcAlpha,
            O::Add,
            F::OneMinusDstAlpha,
            F::SrcAlpha,
            O::Add,
        ),
        LayerBlend::Xor => (
            F::OneMinusDstAlpha,
            F::OneMinusSrcAlpha,
            O::Add,
            F::OneMinusDstAlpha,
            F::OneMinusSrcAlpha,
            O::Add,
        ),
        LayerBlend::DstOver => (
            F::OneMinusDstAlpha,
            F::One,
            O::Add,
            F::OneMinusDstAlpha,
            F::One,
            O::Add,
        ),
        LayerBlend::Multiply => (
            F::Dst,
            F::OneMinusSrcAlpha,
            O::Add,
            F::One,
            F::OneMinusSrcAlpha,
            O::Add,
        ),
        LayerBlend::Modulate => (F::Dst, F::Zero, O::Add, F::DstAlpha, F::Zero, O::Add),
        LayerBlend::Screen => (
            F::One,
            F::OneMinusSrc,
            O::Add,
            F::One,
            F::OneMinusSrc,
            O::Add,
        ),
        LayerBlend::Darken => (F::One, F::One, O::Min, F::One, F::One, O::Max),
        LayerBlend::Lighten => (F::One, F::One, O::Max, F::One, F::One, O::Max),
        LayerBlend::Exclusion => (
            F::OneMinusDst,
            F::OneMinusSrc,
            O::Add,
            F::One,
            F::OneMinusSrcAlpha,
            O::Add,
        ),
        LayerBlend::Plus => (source, F::One, O::Add, F::One, F::One, O::Add),
        LayerBlend::Clear => (F::Zero, F::Zero, O::Add, F::Zero, F::Zero, O::Add),
        _ => return Err(Error::UnsupportedExecution),
    };
    Ok(wgpu::BlendState {
        color: wgpu::BlendComponent {
            src_factor: cs,
            dst_factor: cd,
            operation: co,
        },
        alpha: wgpu::BlendComponent {
            src_factor: as_,
            dst_factor: ad,
            operation: ao,
        },
    })
}
