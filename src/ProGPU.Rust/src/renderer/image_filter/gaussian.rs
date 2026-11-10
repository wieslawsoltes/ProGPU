//! Exact original managed separable Gaussian records and two canonical passes.
use super::*;
use crate::effects::BlurUniforms;
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::ComputePipeline,
    layout: wgpu::BindGroupLayout,
}
#[derive(Default)]
pub(super) struct GaussianState {
    pipelines: Option<[Pipeline; 2]>,
}
pub(super) struct GaussianPass {
    input: GpuImage,
    temporary: GpuImage,
    output: GpuImage,
    bindings: [wgpu::BindGroup; 2],
    pipelines: [Pipeline; 2],
    groups: [u32; 2],
}
impl GaussianPass {
    pub(super) fn output(&self) -> &GpuImage {
        &self.output
    }
    pub(super) fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        let _owners = (&self.input, &self.temporary);
        for axis in 0..2 {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("ProGPU original image-filter Gaussian axis"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&self.pipelines[axis].pipeline);
            pass.set_bind_group(0, &self.bindings[axis], &[]);
            pass.dispatch_workgroups(self.groups[0], self.groups[1], 1);
        }
    }
}
impl GaussianState {
    pub(super) async fn prepare(
        &mut self,
        device: &wgpu::Device,
        input: &GpuImage,
        parameters: [BlurUniforms; 2],
    ) -> Result<GaussianPass, Error> {
        preflight::gaussian(device, input.size(), &parameters)?;
        if input.device() != device || input.alpha() != ImageAlpha::Premultiplied {
            return Err(Error::ResourceDomain);
        }
        if self.pipelines.is_none() {
            let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
            let pipelines = [
                create(device, shaders::BLUR_HORIZONTAL),
                create(device, shaders::BLUR_VERTICAL),
            ];
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.pipelines = Some(pipelines);
        }
        let pipelines = self
            .pipelines
            .as_ref()
            .ok_or(Error::ResourceDomain)?
            .clone();
        let temporary = output(device, input.size())?;
        let result = output(device, input.size())?;
        let bindings = std::array::from_fn(|axis| {
            let source = if axis == 0 { input } else { &temporary };
            let destination = if axis == 0 { &temporary } else { &result };
            let source = source.texture().create_view(&wgpu::TextureViewDescriptor {
                base_mip_level: 0,
                mip_level_count: Some(1),
                ..Default::default()
            });
            let destination = destination.texture().create_view(&Default::default());
            let params = cf::buffer(
                device,
                "ProGPU immutable image-filter Gaussian params",
                bytemuck::bytes_of(&parameters[axis]),
                wgpu::BufferUsages::UNIFORM,
            );
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU original Gaussian axis resources"),
                layout: &pipelines[axis].layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&source),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&destination),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: params.as_entire_binding(),
                    },
                ],
            })
        });
        Ok(GaussianPass {
            input: input.clone(),
            temporary,
            output: result,
            bindings,
            pipelines,
            groups: input.size().map(|v| v.div_ceil(16)),
        })
    }
}
fn output(device: &wgpu::Device, size: [u32; 2]) -> Result<GpuImage, Error> {
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU private original Gaussian result"),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba8Unorm,
        usage: wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::STORAGE_BINDING
            | wgpu::TextureUsages::COPY_SRC
            | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    GpuImage::from_texture(device, &texture, ImageAlpha::Premultiplied)
}
fn create(device: &wgpu::Device, source: &'static str) -> Pipeline {
    let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original managed Gaussian layout"),
        entries: &[
            wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
                },
                count: None,
            },
            wgpu::BindGroupLayoutEntry {
                binding: 1,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::StorageTexture {
                    access: wgpu::StorageTextureAccess::WriteOnly,
                    format: wgpu::TextureFormat::Rgba8Unorm,
                    view_dimension: wgpu::TextureViewDimension::D2,
                },
                count: None,
            },
            wgpu::BindGroupLayoutEntry {
                binding: 2,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: std::num::NonZeroU64::new(16),
                },
                count: None,
            },
        ],
    });
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("ProGPU unchanged original Gaussian kernel"),
        source: wgpu::ShaderSource::Wgsl(source.into()),
    });
    let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("ProGPU original Gaussian pipeline layout"),
        bind_group_layouts: &[Some(&layout)],
        immediate_size: 0,
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some("ProGPU original Gaussian pass"),
        layout: Some(&pipeline_layout),
        module: &module,
        entry_point: Some("main"),
        compilation_options: Default::default(),
        cache: None,
    });
    Pipeline { pipeline, layout }
}

impl GaussianPass {
    pub(super) fn retain_submitted(&self, queue: &wgpu::Queue) {
        self.input.retain_submitted(queue);
        self.temporary.retain_submitted(queue);
        self.output.retain_submitted(queue);
    }
}
