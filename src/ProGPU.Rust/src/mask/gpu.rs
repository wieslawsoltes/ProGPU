//! Original ClipCompose pipelines and submission-owned mask programs.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    path::{ExecutionPath, ExecutionPreference, PathRasterizer},
    shaders,
};
use std::sync::Arc;
use wgpu::util::DeviceExt;

/// Actual source and original target-local sampling record for one composition.
#[derive(Clone, Debug)]
pub struct SampledMaskInput {
    /// Actual retained same-device sampled source.
    pub image: GpuImage,
    /// Original target-local affine/red-or-alpha/opacity record.
    pub uniforms: MaskUniforms,
    /// Original nearest/linear filtering.
    pub sampling: MaskSampling,
    /// Ordered original intersection or complement.
    pub operation: MaskOperation,
}
/// Actual mask preparation resources and encoded work, not performance estimates.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct MaskCompositionMetrics {
    /// Number of original ordered components.
    pub nodes: u32,
    /// Actual render passes per program execution.
    pub render_passes: u32,
    /// Actual node/accumulation storage, including the final output.
    pub texture_bytes: u64,
    /// Actual unique-path R8 atlas storage.
    pub atlas_bytes: u64,
    /// Actual aligned compose and source sampling records.
    pub uniform_bytes: u64,
    /// Actual retained vertex/index buffer bytes.
    pub geometry_bytes: u64,
}
#[derive(Clone)]
struct Pipelines {
    layout: wgpu::BindGroupLayout,
    child: wgpu::BindGroupLayout,
    raw: wgpu::RenderPipeline,
    path: wgpu::RenderPipeline,
    sampled: wgpu::RenderPipeline,
}
struct PathProgram {
    pipeline: wgpu::RenderPipeline,
    compose: wgpu::RenderPipeline,
    binding: wgpu::BindGroup,
    accumulation: [wgpu::BindGroup; 2],
    node: wgpu::TextureView,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    count: u32,
}
enum Program {
    Paths(Box<PathProgram>),
    Sampled {
        pipeline: wgpu::RenderPipeline,
        bindings: Vec<(wgpu::BindGroup, wgpu::BindGroup)>,
    },
}
/// Frozen executable mask program. The actual output belongs to this generation;
/// another preparation never overwrites it. Encoding is explicit and GPU-only.
pub struct PreparedMask {
    owner: Arc<()>,
    output: GpuImage,
    targets: [wgpu::TextureView; 2],
    program: Program,
    metrics: MaskCompositionMetrics,
}
impl PreparedMask {
    /// Actual output; consume only after the program has been encoded.
    pub fn image(&self) -> &GpuImage {
        &self.output
    }
    /// Transfer the output owner; encoded work retains its own dependencies.
    pub fn into_image(self) -> GpuImage {
        self.output
    }
    /// Actual preparation resources and per-execution work.
    pub fn metrics(&self) -> MaskCompositionMetrics {
        self.metrics
    }
    pub(crate) fn encode_sampled_step(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        index: usize,
    ) -> Result<(), Error> {
        let Program::Sampled { pipeline, bindings } = &self.program else {
            return Err(Error::ResourceDomain);
        };
        let (binding, child) = bindings.get(index).ok_or(Error::ResourceDomain)?;
        let mut pass = begin(encoder, &self.targets[index & 1]);
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, binding, &[(index * 256) as u32]);
        pass.set_bind_group(1, child, &[]);
        pass.draw(0..3, 0..1);
        Ok(())
    }
    pub(crate) fn encode_owned(&self, encoder: &mut wgpu::CommandEncoder) {
        match &self.program {
            Program::Paths(program) => {
                let PathProgram {
                    pipeline,
                    compose,
                    binding,
                    accumulation,
                    node,
                    vertices,
                    indices,
                    count,
                } = program.as_ref();
                for index in 0..*count {
                    {
                        let mut pass = begin(encoder, node);
                        pass.set_pipeline(pipeline);
                        pass.set_bind_group(0, binding, &[index * 256]);
                        pass.set_vertex_buffer(0, vertices.slice(..));
                        pass.set_index_buffer(indices.slice(..), wgpu::IndexFormat::Uint32);
                        pass.draw_indexed(0..6, (index * 4) as i32, 0..1);
                    }
                    let target = (index & 1) as usize;
                    let mut pass = begin(encoder, &self.targets[target]);
                    pass.set_pipeline(compose);
                    pass.set_bind_group(0, &accumulation[target], &[index * 256]);
                    pass.draw(0..3, 0..1);
                }
            }
            Program::Sampled { pipeline, bindings } => {
                for (index, (binding, child)) in bindings.iter().enumerate() {
                    let mut pass = begin(encoder, &self.targets[index & 1]);
                    pass.set_pipeline(pipeline);
                    pass.set_bind_group(0, binding, &[(index * 256) as u32]);
                    pass.set_bind_group(1, child, &[]);
                    pass.draw(0..3, 0..1);
                }
            }
        }
    }
}

/// Lazy original path/retained sampled-mask GPU execution. Compute rasterization
/// and render composition keep separate exact algorithms and explicit policies.
pub struct MaskComposer {
    owner: Arc<()>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    pipelines: Option<Pipelines>,
    rasterizer: Option<PathRasterizer>,
    path_preference: ExecutionPreference,
    signed_winding_policy: crate::path::SignedWindingPolicy,
}
impl MaskComposer {
    /// Retain the device/queue without compiling shaders; incompatible forced routes reject.
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            queue: queue.clone(),
            pipelines: None,
            rasterizer: None,
            path_preference: ExecutionPreference::Automatic,
            signed_winding_policy: Default::default(),
        })
    }
    /// Original fragment-stage composition route; path rasterization uses native compute.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::GpuShader
    }
    /// Configure the independent original compute rasterizer before first use.
    /// No shader/CPU approximation is selected for incompatible forced routes.
    pub fn configure_path_rasterization(
        &mut self,
        preference: ExecutionPreference,
    ) -> Result<(), Error> {
        if self.rasterizer.is_some() {
            return Err(Error::ResourceDomain);
        }
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::NativeCompute
        ) {
            return Err(Error::UnsupportedExecution);
        }
        self.path_preference = preference;
        Ok(())
    }
    /// Select original inline/staged signed execution for future clip generations.
    pub fn set_signed_winding_policy(&mut self, policy: crate::path::SignedWindingPolicy) {
        self.signed_winding_policy = policy;
        if let Some(rasterizer) = &mut self.rasterizer {
            rasterizer.set_signed_winding_policy(policy);
        }
    }
    /// Actual path-raster route, absent until a clip atlas has been requested.
    pub fn path_execution_path(&self) -> Option<ExecutionPath> {
        self.rasterizer.as_ref().map(PathRasterizer::execution_path)
    }
    fn validate(&self, size: [u32; 2], count: usize, targets: u64) -> Result<(), Error> {
        let limits = self.device.limits();
        if size.contains(&0) || count == 0 || count > 65536 {
            return Err(Error::InvalidInput);
        }
        if size.iter().any(|v| *v > limits.max_texture_dimension_2d)
            || u64::from(size[0]) * u64::from(size[1]) * targets > 512 * 1024 * 1024
            || count as u64 * 256 > limits.max_buffer_size
        {
            return Err(Error::Capacity);
        }
        if limits.max_sampled_textures_per_shader_stage < 3
            || limits.max_bind_groups < 2
            || limits.min_uniform_buffer_offset_alignment > 256
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
    async fn pipelines(&mut self) -> Result<Pipelines, Error> {
        if let Some(p) = &self.pipelines {
            return Ok(p.clone());
        }
        let d = &self.device;
        let scope = d.push_error_scope(wgpu::ErrorFilter::Validation);
        let sampler = |binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
            count: None,
        };
        let texture = |binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let uniform = |binding, size, dynamic| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: dynamic,
                min_binding_size: std::num::NonZeroU64::new(size),
            },
            count: None,
        };
        let layout = d.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original ClipCompose layout"),
            entries: &[sampler(0), texture(1), texture(2), uniform(3, 32, true)],
        });
        let child = d.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original sampled-mask child layout"),
            entries: &[sampler(0), texture(1), uniform(2, 96, false)],
        });
        let module = d.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical ClipCompose"),
            source: wgpu::ShaderSource::Wgsl(shaders::CLIP_COMPOSE.into()),
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2];
        let buffers = [Some(wgpu::VertexBufferLayout {
            array_stride: 16,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &attributes,
        })];
        let make = |path, sampled| {
            let layouts = if sampled {
                vec![Some(&layout), Some(&child)]
            } else {
                vec![Some(&layout)]
            };
            let pl = d.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU retained mask pipeline layout"),
                bind_group_layouts: &layouts,
                immediate_size: 0,
            });
            d.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("ProGPU original retained mask pipeline"),
                layout: Some(&pl),
                vertex: wgpu::VertexState {
                    module: &module,
                    entry_point: Some(if path { "vs_path" } else { "vs_compose" }),
                    compilation_options: Default::default(),
                    buffers: if path { &buffers } else { &[] },
                },
                fragment: Some(wgpu::FragmentState {
                    module: &module,
                    entry_point: Some(if path {
                        "fs_path"
                    } else if sampled {
                        "fs_compose_sampled"
                    } else {
                        "fs_compose"
                    }),
                    compilation_options: Default::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: wgpu::TextureFormat::R8Unorm,
                        blend: None,
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                primitive: Default::default(),
                depth_stencil: None,
                multisample: Default::default(),
                multiview_mask: None,
                cache: None,
            })
        };
        let p = Pipelines {
            raw: make(false, false),
            path: make(true, false),
            sampled: make(false, true),
            layout,
            child,
        };
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.pipelines = Some(p.clone());
        Ok(p)
    }
    /// Original shared-atlas path transfer followed by ordered intersect/difference.
    /// The atlas is rasterized in one submission; encode performs the retained
    /// two-pass-per-node program with exact integer-load admission preserved.
    pub async fn prepare_clip(&mut self, plan: &ClipPlan) -> Result<PreparedMask, Error> {
        self.validate(plan.size(), plan.uniforms.len(), 3)?;
        if plan.atlas_size() > self.device.limits().max_texture_dimension_2d
            || u64::from(plan.size()[0]) * u64::from(plan.size()[1]) * 3
                + u64::from(plan.atlas_size()).pow(2)
                > 512 * 1024 * 1024
        {
            return Err(Error::Capacity);
        }
        let p = self.pipelines().await?;
        let mut rasterizer = match self.rasterizer.take() {
            Some(r) => r,
            None => PathRasterizer::new(&self.device, &self.queue, self.path_preference).await?,
        };
        let requests = plan
            .rasters
            .iter()
            .map(|(p, r)| (p, *r))
            .collect::<Vec<_>>();
        rasterizer.set_signed_winding_policy(self.signed_winding_policy);
        rasterizer.prepare_programs(&requests).await?;
        let atlas = rasterizer.rasterize_atlas(&requests)?;
        self.rasterizer = Some(rasterizer);
        let d = &self.device;
        let scope = d.push_error_scope(wgpu::ErrorFilter::Validation);
        let node = texture(d, plan.size(), "ProGPU retained clip node");
        let targets = [
            texture(d, plan.size(), "ProGPU clip accumulation A"),
            texture(d, plan.size(), "ProGPU clip accumulation B"),
        ];
        let views = targets
            .each_ref()
            .map(|t| t.create_view(&Default::default()));
        let node = node.create_view(&Default::default());
        let atlas_view = atlas.texture().create_view(&Default::default());
        let uniform = uniform_buffer(d, &plan.uniforms);
        let sampler = sample(d, MaskSampling::Linear);
        let binding = compose_binding(d, &p.layout, &sampler, &atlas_view, &atlas_view, &uniform);
        let accumulation = std::array::from_fn(|index| {
            compose_binding(d, &p.layout, &sampler, &node, &views[1 - index], &uniform)
        });
        let vertices = buffer(
            d,
            bytemuck::cast_slice(&plan.vertices),
            wgpu::BufferUsages::VERTEX,
        );
        let indices = buffer(
            d,
            bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
            wgpu::BufferUsages::INDEX,
        );
        let count = plan.uniforms.len() as u32;
        let output = GpuImage::from_texture(
            d,
            &targets[((count - 1) & 1) as usize],
            ImageAlpha::Straight,
        )?;
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(PreparedMask {
            owner: self.owner.clone(),
            output,
            targets: views,
            metrics: MaskCompositionMetrics {
                nodes: count,
                render_passes: count * 2,
                texture_bytes: u64::from(plan.size()[0]) * u64::from(plan.size()[1]) * 3,
                atlas_bytes: u64::from(plan.atlas_size()).pow(2),
                uniform_bytes: u64::from(count) * 256,
                geometry_bytes: vertices.size() + indices.size(),
            },
            program: Program::Paths(Box::new(PathProgram {
                pipeline: p.path,
                compose: p.raw,
                binding,
                accumulation,
                node,
                vertices,
                indices,
                count,
            })),
        })
    }
    /// Compose actual sampled red/alpha children directly, preserving their
    /// affine records, filtering and opacity without intermediate extraction.
    pub async fn prepare_sampled(
        &mut self,
        inputs: &[SampledMaskInput],
        size: [u32; 2],
    ) -> Result<PreparedMask, Error> {
        self.validate(size, inputs.len(), 2)?;
        if inputs.len() > 64 {
            return Err(Error::Capacity);
        }
        for input in inputs {
            if input.image.device() != &self.device {
                return Err(Error::ResourceDomain);
            }
            if input.uniforms.options[0] != 1.0
                || !(0.0..=1.0).contains(&input.uniforms.options[1])
                || !matches!(input.uniforms.options[2], 0.0 | 1.0)
                || !matches!(input.uniforms.options[3], 0.0 | 1.0 | 2.0)
                || bytemuck::cast_slice::<_, f32>(std::slice::from_ref(&input.uniforms))
                    .iter()
                    .any(|v| !v.is_finite())
            {
                return Err(Error::InvalidInput);
            }
        }
        let p = self.pipelines().await?;
        let d = &self.device;
        let scope = d.push_error_scope(wgpu::ErrorFilter::Validation);
        let targets = [
            texture(d, size, "ProGPU sampled-mask accumulation A"),
            texture(d, size, "ProGPU sampled-mask accumulation B"),
        ];
        let views = targets
            .each_ref()
            .map(|t| t.create_view(&Default::default()));
        let records = inputs
            .iter()
            .enumerate()
            .map(|(index, input)| ComposeUniforms {
                operation: input.operation as u32,
                first: u32::from(index == 0),
                width: size[0],
                height: size[1],
                pixel_mapping: [0; 4],
            })
            .collect::<Vec<_>>();
        let uniform = uniform_buffer(d, &records);
        let samplers = [
            sample(d, MaskSampling::Nearest),
            sample(d, MaskSampling::Linear),
        ];
        let mut bindings = Vec::new();
        for (index, input) in inputs.iter().enumerate() {
            let view = input.image.texture().create_view(&Default::default());
            let compose = compose_binding(
                d,
                &p.layout,
                &samplers[1],
                &view,
                &views[1 - (index & 1)],
                &uniform,
            );
            let params = buffer(
                d,
                bytemuck::bytes_of(&input.uniforms),
                wgpu::BufferUsages::UNIFORM,
            );
            let child = d.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable sampled-mask child"),
                layout: &p.child,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(
                            &samplers[usize::from(input.sampling == MaskSampling::Linear)],
                        ),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: params.as_entire_binding(),
                    },
                ],
            });
            bindings.push((compose, child));
        }
        let output =
            GpuImage::from_texture(d, &targets[(inputs.len() - 1) & 1], ImageAlpha::Straight)?;
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(PreparedMask {
            owner: self.owner.clone(),
            output,
            targets: views,
            program: Program::Sampled {
                pipeline: p.sampled,
                bindings,
            },
            metrics: MaskCompositionMetrics {
                nodes: inputs.len() as u32,
                render_passes: inputs.len() as u32,
                texture_bytes: u64::from(size[0]) * u64::from(size[1]) * 2,
                uniform_bytes: inputs.len() as u64 * (256 + 96),
                ..Default::default()
            },
        })
    }
    /// Encode one retained program without submission or CPU pixel work.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        prepared: &PreparedMask,
    ) -> Result<MaskCompositionMetrics, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) {
            return Err(Error::ResourceDomain);
        }
        prepared.encode_owned(encoder);
        Ok(prepared.metrics)
    }
}
fn sample(d: &wgpu::Device, mode: MaskSampling) -> wgpu::Sampler {
    let filter = if mode == MaskSampling::Linear {
        wgpu::FilterMode::Linear
    } else {
        wgpu::FilterMode::Nearest
    };
    d.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("ProGPU clip sampling"),
        min_filter: filter,
        mag_filter: filter,
        lod_max_clamp: 0.0,
        ..Default::default()
    })
}
fn buffer(d: &wgpu::Device, data: &[u8], usage: wgpu::BufferUsages) -> wgpu::Buffer {
    d.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some("ProGPU retained mask data"),
        contents: data,
        usage,
    })
}
fn uniform_buffer(d: &wgpu::Device, records: &[ComposeUniforms]) -> wgpu::Buffer {
    let mut bytes = vec![0; records.len() * 256];
    for (slot, record) in bytes.chunks_exact_mut(256).zip(records) {
        slot[..32].copy_from_slice(bytemuck::bytes_of(record));
    }
    buffer(d, &bytes, wgpu::BufferUsages::UNIFORM)
}
fn texture(d: &wgpu::Device, size: [u32; 2], label: &str) -> wgpu::Texture {
    d.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::R8Unorm,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::RENDER_ATTACHMENT,
        view_formats: &[],
    })
}
fn compose_binding(
    d: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
    node: &wgpu::TextureView,
    previous: &wgpu::TextureView,
    uniform: &wgpu::Buffer,
) -> wgpu::BindGroup {
    d.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU original dynamic mask composition"),
        layout,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(node),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: wgpu::BindingResource::TextureView(previous),
            },
            wgpu::BindGroupEntry {
                binding: 3,
                resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer: uniform,
                    offset: 0,
                    size: std::num::NonZeroU64::new(32),
                }),
            },
        ],
    })
}

fn begin<'a>(
    encoder: &'a mut wgpu::CommandEncoder,
    view: &wgpu::TextureView,
) -> wgpu::RenderPass<'a> {
    encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some("ProGPU retained mask composition"),
        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
            view,
            depth_slice: None,
            resolve_target: None,
            ops: wgpu::Operations {
                load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                store: wgpu::StoreOp::Store,
            },
        })],
        ..Default::default()
    })
}
impl MaskCompositionMetrics {
    pub(crate) fn merge(&mut self, other: Self) -> Result<(), Error> {
        macro_rules! add {
            ($field:ident) => {
                self.$field = self
                    .$field
                    .checked_add(other.$field)
                    .ok_or(Error::Capacity)?;
            };
        }
        add!(nodes);
        add!(render_passes);
        add!(texture_bytes);
        add!(atlas_bytes);
        add!(uniform_bytes);
        add!(geometry_bytes);
        Ok(())
    }
}
