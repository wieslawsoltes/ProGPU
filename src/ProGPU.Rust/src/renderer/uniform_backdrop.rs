//! Original full-size advanced group blend against an explicit uniform backdrop.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    path::ExecutionPath,
    scene::LayerBlend,
};

/// Exact original 32-byte GroupBlend record. Backdrop components are quantized
/// individually using the native clamp/round/divide expression.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct UniformBackdropUniforms {
    /// Premultiplied target clear color after original UNORM8 quantization.
    pub backdrop: [f32; 4],
    /// Original native advanced blend enum value.
    pub blend_mode: u32,
    /// Original typed zero padding.
    pub padding: [u32; 3],
}
const _: () = assert!(size_of::<UniformBackdropUniforms>() == 32);
impl UniformBackdropUniforms {
    /// Preserve the original native uniform-backdrop arithmetic. This input is
    /// already the actual premultiplied clear color, not a straight paint color.
    pub fn new(backdrop: [f32; 4], blend: LayerBlend) -> Result<Self, Error> {
        if backdrop.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        if blend.is_fixed() {
            return Err(Error::UnsupportedExecution);
        }
        let scaled =
            wide::f32x4::from(backdrop.map(|v| v.clamp(0.0, 1.0))) * wide::f32x4::splat(255.0);
        // Native std::round uses ties away from zero; wide::round would select
        // a different tie policy. These four bounded scalar conversions retain it.
        let rounded = scaled.to_array().map(f32::round);
        let backdrop = (wide::f32x4::from(rounded) / wide::f32x4::splat(255.0)).to_array();
        Ok(Self {
            backdrop,
            blend_mode: blend as u32,
            padding: [0; 3],
        })
    }
}
/// Actual resources for the original one-pass final group composition.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct UniformBackdropMetrics {
    /// Original final full-target blend pass.
    pub render_passes: u32,
    /// Actual new RGBA/BGRA output allocation, excluding retained input.
    pub texture_bytes: u64,
    /// Original immutable 32-byte uniform upload.
    pub uniform_bytes: u64,
}
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
}
/// Lazy original uniform-backdrop compositor. This explicit operation owns a
/// fresh target initialized from its supplied clear color; it never infers a
/// uniform destination from an ancestor, old clear or arbitrary live attachment.
pub struct UniformBackdropEngine {
    device: wgpu::Device,
    format: wgpu::TextureFormat,
    owner: Arc<()>,
    pipeline: Option<Pipeline>,
}
/// Immutable source, clear, original blend and owned output generation. Replaying
/// reads current source pixels; dropping a caller's handle does not retire it.
pub struct PreparedUniformBackdrop {
    owner: Arc<()>,
    output: GpuImage,
    pipeline: wgpu::RenderPipeline,
    binding: wgpu::BindGroup,
    clear: [f32; 4],
    scissor: Option<[u32; 4]>,
    metrics: UniformBackdropMetrics,
}
impl PreparedUniformBackdrop {
    /// Actual owned output; encode before consuming its pixels.
    pub fn image(&self) -> &GpuImage {
        &self.output
    }
    /// Transfer output ownership after encoding/submission.
    pub fn into_image(self) -> GpuImage {
        self.output
    }
    /// Actual preparation resources and encoded pass count.
    pub fn metrics(&self) -> UniformBackdropMetrics {
        self.metrics
    }
}
impl UniformBackdropEngine {
    /// Retain the device and exact target format without compiling a pipeline.
    pub fn new(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
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
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            device: device.clone(),
            format,
            owner: Arc::new(()),
            pipeline: None,
        })
    }
    /// Original fragment-stage algorithm; no CPU blend or sampled readback.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::GpuShader
    }
    /// Prepare the original final pass for a full-size premultiplied source.
    /// Source opacity, effects and geometric masks must already be materialized.
    /// An optional physical binary scissor leaves the clear color outside it.
    pub async fn prepare(
        &mut self,
        source: &GpuImage,
        clear: [f32; 4],
        blend: LayerBlend,
        scissor: Option<[u32; 4]>,
    ) -> Result<PreparedUniformBackdrop, Error> {
        let uniforms = UniformBackdropUniforms::new(clear, blend)?;
        let [width, height] = source.size();
        if source.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        if source.alpha() != ImageAlpha::Premultiplied || source.texture().format() != self.format {
            return Err(Error::UnsupportedExecution);
        }
        if let Some([x, y, w, h]) = scissor {
            if x.checked_add(w).is_none_or(|v| v > width)
                || y.checked_add(h).is_none_or(|v| v > height)
            {
                return Err(Error::InvalidInput);
            }
        }
        let bytes = u64::from(width) * u64::from(height) * 4;
        if bytes > 256 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        if self.pipeline.is_none() {
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let module = self
                .device
                .create_shader_module(wgpu::ShaderModuleDescriptor {
                    label: Some("ProGPU canonical GroupBlend"),
                    source: wgpu::ShaderSource::Wgsl(shaders::GROUP_BLEND.into()),
                });
            let layout = self
                .device
                .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("ProGPU original uniform backdrop layout"),
                    entries: &[
                        wgpu::BindGroupLayoutEntry {
                            binding: 0,
                            visibility: wgpu::ShaderStages::FRAGMENT,
                            ty: wgpu::BindingType::Texture {
                                sample_type: wgpu::TextureSampleType::Float { filterable: true },
                                view_dimension: wgpu::TextureViewDimension::D2,
                                multisampled: false,
                            },
                            count: None,
                        },
                        wgpu::BindGroupLayoutEntry {
                            binding: 1,
                            visibility: wgpu::ShaderStages::FRAGMENT,
                            ty: wgpu::BindingType::Buffer {
                                ty: wgpu::BufferBindingType::Uniform,
                                has_dynamic_offset: false,
                                min_binding_size: std::num::NonZeroU64::new(32),
                            },
                            count: None,
                        },
                    ],
                });
            let pipeline_layout =
                self.device
                    .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                        label: Some("ProGPU uniform backdrop pipeline layout"),
                        bind_group_layouts: &[Some(&layout)],
                        immediate_size: 0,
                    });
            let pipeline = self
                .device
                .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                    label: Some("ProGPU original uniform-backdrop group blend"),
                    layout: Some(&pipeline_layout),
                    vertex: wgpu::VertexState {
                        module: &module,
                        entry_point: Some("vs_main"),
                        compilation_options: Default::default(),
                        buffers: &[],
                    },
                    fragment: Some(wgpu::FragmentState {
                        module: &module,
                        entry_point: Some("fs_main"),
                        compilation_options: Default::default(),
                        targets: &[Some(wgpu::ColorTargetState {
                            format: self.format,
                            blend: None,
                            write_mask: wgpu::ColorWrites::ALL,
                        })],
                    }),
                    primitive: Default::default(),
                    depth_stencil: None,
                    multisample: Default::default(),
                    multiview_mask: None,
                    cache: None,
                });
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.pipeline = Some(Pipeline { pipeline, layout });
        }
        let pipeline = self.pipeline.as_ref().ok_or(Error::ResourceDomain)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU owned uniform-backdrop output"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: self.format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let output = GpuImage::from_texture(&self.device, &texture, ImageAlpha::Premultiplied)?;
        let data = advanced::buffer(
            &self.device,
            "ProGPU immutable uniform backdrop record",
            bytemuck::bytes_of(&uniforms),
            wgpu::BufferUsages::UNIFORM,
        );
        let view = source.texture().create_view(&wgpu::TextureViewDescriptor {
            mip_level_count: Some(1),
            ..Default::default()
        });
        let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU retained uniform-backdrop source"),
            layout: &pipeline.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: data.as_entire_binding(),
                },
            ],
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(PreparedUniformBackdrop {
            owner: self.owner.clone(),
            output,
            pipeline: pipeline.pipeline.clone(),
            binding,
            clear,
            scissor,
            metrics: UniformBackdropMetrics {
                render_passes: 1,
                texture_bytes: bytes,
                uniform_bytes: 32,
            },
        })
    }
    /// Encode one original pass. The command retains source/output/uniform owners;
    /// there is no queue submission, callback, polling or forced destruction here.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        prepared: &PreparedUniformBackdrop,
    ) -> Result<UniformBackdropMetrics, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) {
            return Err(Error::ResourceDomain);
        }
        let [r, g, b, a] = prepared.clear.map(f64::from);
        let view = prepared.output.texture().create_view(&Default::default());
        let mut pass = advanced::begin(
            encoder,
            &view,
            wgpu::LoadOp::Clear(wgpu::Color { r, g, b, a }),
            "ProGPU uniform backdrop final pass",
        );
        if let Some([x, y, w, h]) = prepared.scissor {
            if w == 0 || h == 0 {
                return Ok(prepared.metrics);
            }
            pass.set_scissor_rect(x, y, w, h);
        }
        pass.set_pipeline(&prepared.pipeline);
        pass.set_bind_group(0, &prepared.binding, &[]);
        pass.draw(0..3, 0..1);
        Ok(prepared.metrics)
    }
}
