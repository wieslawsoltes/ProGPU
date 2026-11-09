//! Shared original filterable and Tier-1 planar image preparation.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    path::{ExecutionPath, ExecutionPreference},
    shaders,
};
use std::{collections::HashMap, sync::Arc};
use wgpu::util::DeviceExt;

/// Actual retained RGB or planar source. Pixel mutation remains with its producer;
/// preparation freezes handles/metadata, and every encode evaluates current pixels.
#[derive(Clone, Debug)]
pub struct BlurInput {
    device: wgpu::Device,
    luma: wgpu::Texture,
    chroma: wgpu::Texture,
    alpha: ImageAlpha,
    conversion: Option<YuvConversion>,
    unfilterable: bool,
}
fn sampled(texture: &wgpu::Texture) -> bool {
    texture.dimension() == wgpu::TextureDimension::D2
        && texture.depth_or_array_layers() == 1
        && texture.sample_count() == 1
        && texture
            .usage()
            .contains(wgpu::TextureUsages::TEXTURE_BINDING)
}
impl BlurInput {
    /// Retain an existing original RGB image without copying its pixels.
    pub fn image(image: &GpuImage) -> Self {
        Self {
            device: image.device().clone(),
            luma: image.texture().clone(),
            chroma: image.texture().clone(),
            alpha: image.alpha(),
            conversion: None,
            unfilterable: false,
        }
    }
    /// Retain actual filterable RGB storage, including the managed RGBA16F path.
    /// Device must be the texture's creating device; wgpu also validates bindings.
    pub fn rgb(
        device: &wgpu::Device,
        texture: &wgpu::Texture,
        alpha: ImageAlpha,
    ) -> Result<Self, Error> {
        if !sampled(texture)
            || !matches!(
                texture.format(),
                wgpu::TextureFormat::R8Unorm
                    | wgpu::TextureFormat::Rgba8Unorm
                    | wgpu::TextureFormat::Bgra8Unorm
                    | wgpu::TextureFormat::Rgba8UnormSrgb
                    | wgpu::TextureFormat::Bgra8UnormSrgb
                    | wgpu::TextureFormat::Rgba16Float
            )
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            device: device.clone(),
            luma: texture.clone(),
            chroma: texture.clone(),
            alpha,
            conversion: None,
            unfilterable: false,
        })
    }
    /// Retain an actual NV12 or normalized MSB-aligned P010 plane pair and its
    /// explicit range/matrix. Shader coordinates use both real plane dimensions.
    pub fn planar(
        device: &wgpu::Device,
        luma: &wgpu::Texture,
        chroma: &wgpu::Texture,
        conversion: YuvConversion,
    ) -> Result<Self, Error> {
        conversion.validate()?;
        let unfilterable = match (luma.format(), chroma.format()) {
            (wgpu::TextureFormat::R8Unorm, wgpu::TextureFormat::Rg8Unorm) => false,
            (wgpu::TextureFormat::R16Unorm, wgpu::TextureFormat::Rg16Unorm) => true,
            _ => return Err(Error::InvalidInput),
        };
        if !sampled(luma) || !sampled(chroma) {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            device: device.clone(),
            luma: luma.clone(),
            chroma: chroma.clone(),
            alpha: ImageAlpha::Straight,
            conversion: Some(conversion),
            unfilterable,
        })
    }
    pub(crate) fn managed_bytes_per_pixel(&self) -> u64 {
        if self.conversion.is_some() || self.luma.format() == wgpu::TextureFormat::Rgba16Float {
            8
        } else if self.luma.format() == wgpu::TextureFormat::R8Unorm {
            1
        } else {
            4
        }
    }
    pub(crate) fn device(&self) -> &wgpu::Device {
        &self.device
    }
    /// Actual luma/RGB storage extent.
    pub fn size(&self) -> [u32; 2] {
        [self.luma.width(), self.luma.height()]
    }
    /// Original source alpha representation; planar decode emits straight alpha.
    pub fn alpha(&self) -> ImageAlpha {
        self.alpha
    }
    /// Whether the actual plane pair requires integer-load reconstruction.
    pub fn is_unfilterable(&self) -> bool {
        self.unfilterable
    }
    /// Frozen original YUV descriptor, absent for RGB input.
    pub fn conversion(&self) -> Option<YuvConversion> {
        self.conversion
    }
    pub(crate) fn views(&self) -> [wgpu::TextureView; 2] {
        [&self.luma, &self.chroma].map(|t| t.create_view(&Default::default()))
    }
}

/// Original source-specific blur storage and parameter arithmetic. The two paths
/// remain distinct: native uses double exp, managed uses original float exp.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BlurStorage {
    /// Original native two RGBA8Unorm intermediates.
    NativeRgba8,
    /// Original managed RGB format, or RGBA16Float after planar decode.
    ManagedSource,
}
/// Actual resources and work; these counts do not qualify pixels or performance.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BlurMetrics {
    /// Original two ordered full-size render passes per encode.
    pub render_passes: u32,
    /// Two immutable 912-byte uniform records.
    pub uniform_bytes: u64,
    /// Actual intermediate/output storage, including the final output owner.
    pub texture_bytes: u64,
    /// Number of distinct intermediate allocations.
    pub texture_allocations: u32,
    /// Actual source-pixel extent processed by both axes.
    pub size: [u32; 2],
    /// Original horizontal integer-load route, not a filterable approximation.
    pub unfilterable_source: bool,
}
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
}
struct Pass {
    pipeline: wgpu::RenderPipeline,
    binding: wgpu::BindGroup,
    target: wgpu::TextureView,
}
/// Owned live program. Encoding rewrites its output from the current source;
/// no stale external content generation is cached or inferred.
pub struct PreparedImageBlur {
    owner: Arc<()>,
    device: wgpu::Device,
    passes: [Pass; 2],
    output: wgpu::Texture,
    alpha: ImageAlpha,
    uniforms: [ImageBlurUniforms; 2],
    metrics: BlurMetrics,
}
impl PreparedImageBlur {
    /// Actual final texture. It becomes usable only after these encoded passes.
    pub fn output(&self) -> &wgpu::Texture {
        &self.output
    }
    /// Output alpha interpretation retained from the actual source policy.
    pub fn alpha(&self) -> ImageAlpha {
        self.alpha
    }
    /// Exact original physical records, independently inspectable.
    pub fn uniforms(&self) -> &[ImageBlurUniforms; 2] {
        &self.uniforms
    }
    /// Actual resource inventory and work per execution.
    pub fn metrics(&self) -> BlurMetrics {
        self.metrics
    }
    pub(crate) fn as_input(&self) -> Result<BlurInput, Error> {
        BlurInput::rgb(&self.device, &self.output, self.alpha)
    }
}
/// Device-owned lazy render pipelines, keyed by actual output format and route.
/// The original algorithm runs on the GPU with no CPU pixel fallback/readback.
pub struct ImageBlurEngine {
    owner: Arc<()>,
    device: wgpu::Device,
    pipelines: HashMap<(wgpu::TextureFormat, bool), Pipeline>,
}
impl ImageBlurEngine {
    /// Retain the device without compiling shaders. Only Automatic/GpuShader
    /// select this original fragment algorithm; incompatible forced routes reject.
    pub fn new(device: &wgpu::Device, preference: ExecutionPreference) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
        ) || device.limits().max_sampled_textures_per_shader_stage < 2
            || device.limits().max_uniform_buffer_binding_size < 912
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            pipelines: HashMap::new(),
        })
    }
    /// Original fragment execution route.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::GpuShader
    }
    /// Number of lazily compiled format/route variants.
    pub fn pipeline_count(&self) -> usize {
        self.pipelines.len()
    }
    async fn pipeline(
        &mut self,
        format: wgpu::TextureFormat,
        unfilterable: bool,
    ) -> Result<Pipeline, Error> {
        if let Some(p) = self.pipelines.get(&(format, unfilterable)) {
            return Ok(p.clone());
        }
        let device = &self.device;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original live image Gaussian shader"),
            source: wgpu::ShaderSource::Wgsl(
                if unfilterable {
                    shaders::IMAGE_BLUR_UNFILTERABLE
                } else {
                    shaders::IMAGE_BLUR
                }
                .into(),
            ),
        });
        let sampler = wgpu::BindGroupLayoutEntry {
            binding: 0,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Sampler(if unfilterable {
                wgpu::SamplerBindingType::NonFiltering
            } else {
                wgpu::SamplerBindingType::Filtering
            }),
            count: None,
        };
        let texture = |binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float {
                    filterable: !unfilterable,
                },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let uniform = wgpu::BindGroupLayoutEntry {
            binding: 3,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size: std::num::NonZeroU64::new(912),
            },
            count: None,
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original live Gaussian bindings"),
            entries: &[sampler, texture(1), texture(2), uniform],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU original live Gaussian layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original live image Gaussian"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let result = Pipeline { pipeline, layout };
        self.pipelines
            .insert((format, unfilterable), result.clone());
        Ok(result)
    }
    /// Prepare both original axes without sampling or submitting the source.
    /// Storage policy also selects the original native/managed parameter builder.
    /// Optional affine RGB rows apply only after the vertical managed pass.
    pub async fn prepare(
        &mut self,
        source: &BlurInput,
        sigma: f32,
        storage: BlurStorage,
        color_transform: Option<[[f32; 4]; 3]>,
    ) -> Result<PreparedImageBlur, Error> {
        if source.device != self.device {
            return Err(Error::ResourceDomain);
        }
        if let Some(rows) = &color_transform {
            finite(rows.as_flattened())?;
        }
        if storage == BlurStorage::NativeRgba8 && color_transform.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        let size = source.size();
        if size.contains(&0)
            || size
                .iter()
                .any(|v| *v > self.device.limits().max_texture_dimension_2d)
        {
            return Err(Error::Capacity);
        }
        let mut uniforms = match storage {
            BlurStorage::NativeRgba8 => ImageBlurUniforms::pair(sigma, size, source.conversion)?,
            BlurStorage::ManagedSource => [
                ImageBlurUniforms::managed(sigma, [1.0 / size[0] as f32, 0.0], source.conversion)?,
                ImageBlurUniforms::managed(sigma, [0.0, 1.0 / size[1] as f32], None)?,
            ],
        };
        if let Some([red, green, blue]) = color_transform {
            uniforms[1].red = red;
            uniforms[1].green = green;
            uniforms[1].blue = blue;
        }
        let format = match storage {
            BlurStorage::NativeRgba8 => wgpu::TextureFormat::Rgba8Unorm,
            BlurStorage::ManagedSource if source.conversion.is_some() => {
                wgpu::TextureFormat::Rgba16Float
            }
            BlurStorage::ManagedSource => source.luma.format(),
        };
        let bpp = match format {
            wgpu::TextureFormat::R8Unorm => 1,
            wgpu::TextureFormat::Rgba16Float => 8,
            _ => 4,
        };
        let bytes = u64::from(size[0]) * u64::from(size[1]) * bpp;
        if bytes > 256 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        let horizontal = self.pipeline(format, source.unfilterable).await?;
        let vertical = self.pipeline(format, false).await?;
        let device = &self.device;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let create = |label| {
            device.create_texture(&wgpu::TextureDescriptor {
                label: Some(label),
                size: wgpu::Extent3d {
                    width: size[0],
                    height: size[1],
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format,
                usage: wgpu::TextureUsages::TEXTURE_BINDING
                    | wgpu::TextureUsages::RENDER_ATTACHMENT,
                view_formats: &[],
            })
        };
        let intermediate = create("ProGPU live image Gaussian intermediate");
        let output = create("ProGPU live image Gaussian output");
        let intermediate = intermediate.create_view(&Default::default());
        let output_view = output.create_view(&Default::default());
        let views = source.views();
        let bind = |p: &Pipeline,
                    views: [&wgpu::TextureView; 2],
                    params: &ImageBlurUniforms,
                    unfilterable: bool| {
            let filter = if unfilterable {
                wgpu::FilterMode::Nearest
            } else {
                wgpu::FilterMode::Linear
            };
            let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU Gaussian clamp sampler"),
                min_filter: filter,
                mag_filter: filter,
                lod_max_clamp: 0.0,
                ..Default::default()
            });
            let buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU original Gaussian 912-byte record"),
                contents: bytemuck::bytes_of(params),
                usage: wgpu::BufferUsages::UNIFORM,
            });
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable image Gaussian binding"),
                layout: &p.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(views[0]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(views[1]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: buffer.as_entire_binding(),
                    },
                ],
            })
        };
        let horizontal_binding = bind(
            &horizontal,
            [&views[0], &views[1]],
            &uniforms[0],
            source.unfilterable,
        );
        let vertical_binding = bind(
            &vertical,
            [&intermediate, &intermediate],
            &uniforms[1],
            false,
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(PreparedImageBlur {
            owner: self.owner.clone(),
            device: device.clone(),
            output,
            alpha: source.alpha,
            uniforms,
            metrics: BlurMetrics {
                render_passes: 2,
                uniform_bytes: 1824,
                texture_bytes: bytes * 2,
                texture_allocations: 2,
                size,
                unfilterable_source: source.unfilterable,
            },
            passes: [
                Pass {
                    pipeline: horizontal.pipeline,
                    binding: horizontal_binding,
                    target: intermediate,
                },
                Pass {
                    pipeline: vertical.pipeline,
                    binding: vertical_binding,
                    target: output_view,
                },
            ],
        })
    }
    /// Encode horizontal then vertical into actual retained targets. No submit,
    /// CPU pixel work, readback, wait or explicit texture destruction is added.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        prepared: &PreparedImageBlur,
    ) -> Result<BlurMetrics, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) {
            return Err(Error::ResourceDomain);
        }
        prepared.encode_owned(encoder);
        Ok(prepared.metrics)
    }
}

impl PreparedImageBlur {
    pub(crate) fn encode_owned(&self, encoder: &mut wgpu::CommandEncoder) {
        for p in &self.passes {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU original image Gaussian axis"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &p.target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_pipeline(&p.pipeline);
            pass.set_bind_group(0, &p.binding, &[]);
            pass.draw(0..3, 0..1);
        }
    }
}
