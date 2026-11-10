//! Original managed visual blur/shadow kernels and owned immutable outputs.
//! Shader sources, entry points, parameter bytes and compiler defaults are unchanged.
use super::*;
use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;
const SOURCES: [&str; 5] = [
    include_str!("../../../../ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl"),
    include_str!("../../../../ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl"),
    include_str!("../../../../ProGPU.Compute/Shaders/DropShadow.wgsl"),
    include_str!("../../../../ProGPU.Compute/Shaders/ShadowBlurHorizontal.wgsl"),
    include_str!("../../../../ProGPU.Compute/Shaders/ShadowBlurVertical.wgsl"),
];
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Blur {
    sigma: f32,
    radius: u32,
    kernel: u32,
    padding: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Shadow {
    offset: [f32; 2],
    padding0: [f32; 2],
    color: [f32; 4],
    radius: [f32; 2],
    source_size: [f32; 2],
    padding1: [f32; 4],
}
const _: () = {
    assert!(size_of::<Blur>() == 16);
    assert!(size_of::<Shadow>() == 64);
};
#[derive(Default)]
pub(super) struct ComputeState {
    pipelines: [Option<wgpu::ComputePipeline>; 5],
}
pub(super) struct ComputedVisual {
    pub image: GpuImage,
    // Original inputs/intermediates and all submitted bindings remain independently owned.
    _source: GpuImage,
    _temporary: Option<GpuImage>,
    _uniform: wgpu::Buffer,
    _bindings: Vec<wgpu::BindGroup>,
    _pipelines: Vec<wgpu::ComputePipeline>,
    pub bytes: u64,
    pub passes: u32,
}
impl ComputeState {
    async fn pipeline(
        &mut self,
        device: &wgpu::Device,
        index: usize,
    ) -> Result<wgpu::ComputePipeline, Error> {
        if let Some(pipeline) = &self.pipelines[index] {
            return Ok(pipeline.clone());
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original visual compute"),
            source: wgpu::ShaderSource::Wgsl(SOURCES[index].into()),
        });
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU visual compute resources"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: false },
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
                        min_binding_size: wgpu::BufferSize::new(if index < 2 { 16 } else { 64 }),
                    },
                    count: None,
                },
            ],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU visual compute layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("ProGPU original visual compute"),
            layout: Some(&pipeline_layout),
            module: &module,
            entry_point: Some("main"),
            compilation_options: Default::default(),
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.pipelines[index] = Some(pipeline.clone());
        Ok(pipeline)
    }
    pub(super) async fn apply(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        source: &GpuImage,
        effect: &CompositionEffectValue,
        dpi: f32,
    ) -> Result<Option<ComputedVisual>, Error> {
        if source.device() != device {
            return Err(Error::ResourceDomain);
        }
        let size = source.size();
        let mut params = Vec::new();
        let (kernels, temporary) = match effect {
            CompositionEffectValue::Blur { radius, kernel } if *radius > 0.01 => {
                let physical = *radius * dpi;
                let value = if physical.is_finite() {
                    physical.max(0.0)
                } else {
                    0.0
                };
                let p = match kernel {
                    VisualBlurKernel::Gaussian => Blur {
                        sigma: value,
                        radius: (value * 3.0).ceil().clamp(0.0, 128.0) as u32,
                        kernel: 0,
                        padding: 0,
                    },
                    VisualBlurKernel::Box => Blur {
                        sigma: 0.0,
                        radius: value.floor().clamp(0.0, 128.0) as u32,
                        kernel: 1,
                        padding: 0,
                    },
                };
                params.extend_from_slice(bytemuck::bytes_of(&p));
                (vec![0, 1], Some(size))
            }
            CompositionEffectValue::DropShadow { radii, color, .. } => {
                let blurred = radii[0] > 0.01 || radii[1] > 0.01;
                let radius = if blurred {
                    radii.map(|v| ((v * dpi) * 2.0).round_ties_even() / 2.0)
                } else {
                    [0.0; 2]
                };
                if radius.iter().chain(color).any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
                let sharp = radius[0] <= 0.01 && radius[1] <= 0.01;
                let p = Shadow {
                    offset: [0.0; 2],
                    padding0: [0.0; 2],
                    color: *color,
                    radius: if sharp { [0.0; 2] } else { radius },
                    source_size: if sharp {
                        [0.0; 2]
                    } else {
                        size.map(|v| v as f32)
                    },
                    padding1: [0.0; 4],
                };
                params.extend_from_slice(bytemuck::bytes_of(&p));
                (
                    if sharp { vec![2] } else { vec![3, 4] },
                    if blurred {
                        Some([size[0].div_ceil(4), size[1]])
                    } else {
                        None
                    },
                )
            }
            _ => return Ok(None),
        };
        let bytes = (u64::from(size[0]) * u64::from(size[1])
            + temporary.map_or(0, |s| u64::from(s[0]) * u64::from(s[1])))
            * 4;
        if bytes > 256 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        let mut pipelines = Vec::new();
        for kernel in &kernels {
            pipelines.push(self.pipeline(device, *kernel).await?);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let output = texture(device, size)?;
        let temporary = temporary.map(|s| texture(device, s)).transpose()?;
        let uniform = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU visual effect original parameters"),
            contents: &params,
            usage: wgpu::BufferUsages::UNIFORM,
        });
        let mut bindings = Vec::new();
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("ProGPU visual compute generation"),
        });
        for (index, pipeline) in pipelines.iter().enumerate() {
            let input = if index == 0 {
                source
            } else {
                temporary.as_ref().ok_or(Error::ResourceDomain)?
            };
            let target = if pipelines.len() == 2 && index == 0 {
                temporary.as_ref().ok_or(Error::ResourceDomain)?
            } else {
                &output
            };
            let input_view = input.texture().create_view(&Default::default());
            let output_view = target.texture().create_view(&Default::default());
            let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable visual compute bindings"),
                layout: &pipeline.get_bind_group_layout(0),
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&input_view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&output_view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: uniform.as_entire_binding(),
                    },
                ],
            });
            {
                let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    label: Some("ProGPU original visual filter pass"),
                    timestamp_writes: None,
                });
                pass.set_pipeline(pipeline);
                pass.set_bind_group(0, &binding, &[]);
                let [w, h] = target.size();
                pass.dispatch_workgroups(w.div_ceil(16), h.div_ceil(16), 1);
            }
            bindings.push(binding);
        }
        let commands = encoder.finish();
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        queue.submit([commands]);
        Ok(Some(ComputedVisual {
            image: output,
            _source: source.clone(),
            _temporary: temporary,
            _uniform: uniform,
            _bindings: bindings,
            _pipelines: pipelines,
            bytes,
            passes: kernels.len() as u32,
        }))
    }
}
fn texture(device: &wgpu::Device, size: [u32; 2]) -> Result<GpuImage, Error> {
    if size.contains(&0)
        || size
            .iter()
            .any(|v| *v > device.limits().max_texture_dimension_2d)
    {
        return Err(Error::Capacity);
    }
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU immutable visual effect texture"),
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
            | wgpu::TextureUsages::COPY_SRC,
        view_formats: &[],
    });
    GpuImage::from_texture(device, &texture, crate::image::ImageAlpha::Premultiplied)
}
