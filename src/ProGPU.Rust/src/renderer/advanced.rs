//! Original bounded source resolve, destination-aware blend and parent replace.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    scene::{AdvancedImage, LayerBlend},
};

const ALIGNMENT: u64 = 256;

/// Actual destination-aware resources and work for one prepared target.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct AdvancedBlendMetrics {
    /// Nonempty destination-aware operations, preserving painter order.
    pub operations: u32,
    /// Three original passes per operation plus the final root copy.
    /// Ordinary drawing spans are counted separately by the scene.
    pub render_passes: u32,
    /// Actual root, output and maximum bounded source texture storage.
    pub texture_bytes: u64,
    /// Immutable sampling slots, resolve frames and copy frame bytes uploaded.
    pub uniform_bytes: u64,
    /// Original resolve/copy quad vertices and shared six-index payload.
    pub geometry_bytes: u64,
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
pub(super) struct SamplingUniforms {
    pub(super) source_origin: [f32; 2],
    pub(super) source_extent: [f32; 2],
    pub(super) blend_mode: u32,
    pub(super) operation_kind: u32,
    pub(super) raster_operation_code: u32,
    pub(super) pattern_kind: u32,
    pub(super) pattern_color: [f32; 4],
    pub(super) pattern_background_color: [f32; 4],
    pub(super) pattern_origin: [f32; 2],
    pub(super) pattern_mask_low: u32,
    pub(super) pattern_mask_high: u32,
    pub(super) pattern_flags: u32,
    pub(super) padding: u32,
    pub(super) pattern_texture_extent: [f32; 2],
}
const _: () = {
    assert!(size_of::<SamplingUniforms>() == 96);
    assert!(std::mem::offset_of!(SamplingUniforms, operation_kind) == 20);
    assert!(std::mem::offset_of!(SamplingUniforms, pattern_color) == 32);
    assert!(std::mem::offset_of!(SamplingUniforms, pattern_texture_extent) == 88);
};

#[derive(Clone)]
pub(super) struct AdvancedState {
    pub(super) layout: wgpu::BindGroupLayout,
    pub(super) pipeline: wgpu::RenderPipeline,
    pub(super) copy: wgpu::RenderPipeline,
    masked: Option<wgpu::RenderPipeline>,
    raster: [Option<wgpu::RenderPipeline>; 2],
}
impl AdvancedState {
    pub(super) async fn new(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
    ) -> Result<Self, Error> {
        let copy = image::pipeline(device, format, false, LayerBlend::Src, false).await?;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut entries: Vec<_> = (0..3)
            .map(|binding| wgpu::BindGroupLayoutEntry {
                binding,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float {
                        filterable: binding != 2,
                    },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
                },
                count: None,
            })
            .collect();
        entries.push(wgpu::BindGroupLayoutEntry {
            binding: 3,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size: std::num::NonZeroU64::new(96),
            },
            count: None,
        });
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original advanced blend layout"),
            entries: &entries,
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU original advanced blend pipeline layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical AdvancedBlend.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::ADVANCED_BLEND.into()),
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU destination-aware blend"),
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
        Ok(Self {
            layout,
            pipeline,
            copy,
            masked: None,
            raster: [None, None],
        })
    }
}
struct Operation {
    draw_index: usize,
    state_index: Option<usize>,
    source_binding: usize,
    resolve_frame: wgpu::BindGroup,
    blend_binding: wgpu::BindGroup,
    scissor: [u32; 4],
    first_vertex: u64,
    source_range: Option<std::ops::Range<u32>>,
}
struct HostOperation {
    draw_index: usize,
    state_index: Option<usize>,
    backdrop: usize,
    source: wgpu::BindGroup,
    scissor: [u32; 4],
}
#[derive(Clone, Copy)]
enum OrderedOperation {
    Blend(usize),
    Backdrop(usize),
}
pub(super) struct PreparedAdvanced {
    state: AdvancedState,
    root: wgpu::TextureView,
    pub(super) root_texture: wgpu::Texture,
    source: wgpu::TextureView,
    output: wgpu::TextureView,
    root_copy: wgpu::BindGroup,
    output_copy: wgpu::BindGroup,
    copy_frame: wgpu::BindGroup,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    copy_vertex: u64,
    operations: Vec<Operation>,
    hosts: Vec<HostOperation>,
    order: Vec<OrderedOperation>,
    pub metrics: AdvancedBlendMetrics,
}

pub(super) fn buffer(
    device: &wgpu::Device,
    label: &str,
    contents: &[u8],
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents,
        usage,
    })
}
pub(super) fn frame_binding(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    uniforms: Uniforms,
) -> wgpu::BindGroup {
    let buffer = buffer(
        device,
        "ProGPU immutable advanced resolve/copy frame",
        bytemuck::bytes_of(&uniforms),
        wgpu::BufferUsages::UNIFORM,
    );
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU advanced frame binding"),
        layout: &pipeline.get_bind_group_layout(0),
        entries: &[wgpu::BindGroupEntry {
            binding: 0,
            resource: buffer.as_entire_binding(),
        }],
    })
}
pub(super) fn quad(size: [u32; 2], dpi: f32, opacity: f32) -> [VectorVertex; 4] {
    // Original append_semantic_layer_quad for source == target. Preserve its
    // zero origin, separate width/DPI expressions and explicit premult flag.
    let x0 = 0.0 / dpi;
    let y0 = 0.0 / dpi;
    let x1 = x0 + size[0] as f32 / dpi;
    let y1 = y0 + size[1] as f32 / dpi;
    [
        ([x0, y0], [0.0, 0.0]),
        ([x1, y0], [1.0, 0.0]),
        ([x1, y1], [1.0, 1.0]),
        ([x0, y1], [0.0, 1.0]),
    ]
    .map(|(position, texture_coordinate)| VectorVertex {
        position,
        color: [opacity, 1.0, 0.0, opacity],
        texture_coordinate,
        stroke_thickness: 1.0,
        ..VectorVertex::zeroed()
    })
}
pub(super) fn begin<'a>(
    encoder: &'a mut wgpu::CommandEncoder,
    target: &wgpu::TextureView,
    load: wgpu::LoadOp<wgpu::Color>,
    label: &'static str,
) -> wgpu::RenderPass<'a> {
    encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some(label),
        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
            view: target,
            depth_slice: None,
            resolve_target: None,
            ops: wgpu::Operations {
                load,
                store: wgpu::StoreOp::Store,
            },
        })],
        ..Default::default()
    })
}

impl Renderer {
    pub(super) async fn prepare_advanced(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        let mut pending = Vec::<(usize, Option<usize>, &AdvancedImage, [u32; 4], [u32; 4])>::new();
        let mut host_pending = Vec::new();
        let mut mask_index = None;
        let mut source_size = [0u32; 2];
        for (index, draw) in compiled.draws.iter().enumerate() {
            match draw {
                DrawRange::State(index) => mask_index = Some(*index),
                DrawRange::BackdropMaterial(backdrop)
                    if prepared
                        .backdrops
                        .as_ref()
                        .is_some_and(|v| v.is_host(*backdrop)) =>
                {
                    let scissor = mask_index
                        .and_then(|i| prepared.masks.as_ref().and_then(|m| m.states.get(i)))
                        .map_or([0, 0, frame.width, frame.height], |s| s.scissor);
                    if scissor[2] != 0 && scissor[3] != 0 {
                        host_pending.try_reserve(1).map_err(|_| Error::Capacity)?;
                        host_pending.push((index, mask_index, *backdrop, scissor));
                    }
                }
                DrawRange::Advanced(draw) => {
                    if draw.blend.is_fixed() && draw.raster_operation.is_none() {
                        return Err(Error::ResourceDomain);
                    }
                    let bounds = match draw.bounds {
                        Some(bounds) => bounds,
                        None => raster_bounds(
                            compiled,
                            draw.source_range.as_ref().ok_or(Error::ResourceDomain)?,
                            frame,
                        )?,
                    };
                    let [x, y, w, h] = bounds;
                    if w == 0 || h == 0 {
                        continue;
                    }
                    let [ox, oy] = frame.origin;
                    if w == 0
                        || h == 0
                        || x < ox
                        || y < oy
                        || u64::from(x) + u64::from(w) > u64::from(ox) + u64::from(frame.width)
                        || u64::from(y) + u64::from(h) > u64::from(oy) + u64::from(frame.height)
                    {
                        return Err(Error::ResourceDomain);
                    }
                    let [cx, cy, cw, ch] = if let Some(index) = mask_index {
                        prepared
                            .masks
                            .as_ref()
                            .and_then(|m| m.states.get(index))
                            .ok_or(Error::ResourceDomain)?
                            .scissor
                    } else {
                        [0, 0, frame.width, frame.height]
                    };
                    let sx = x - ox;
                    let sy = y - oy;
                    let left = cx.saturating_sub(sx).min(w);
                    let top = cy.saturating_sub(sy).min(h);
                    let right = (cx + cw).saturating_sub(sx).min(w);
                    let bottom = (cy + ch).saturating_sub(sy).min(h);
                    let scissor = [
                        left,
                        top,
                        right.saturating_sub(left),
                        bottom.saturating_sub(top),
                    ];
                    if scissor[2] == 0 || scissor[3] == 0 {
                        continue;
                    }
                    source_size[0] = source_size[0].max(w);
                    source_size[1] = source_size[1].max(h);
                    pending.try_reserve(1).map_err(|_| Error::Capacity)?;
                    pending.push((index, mask_index, draw, scissor, bounds));
                }
                _ => {}
            }
        }
        if pending.is_empty() && host_pending.is_empty() {
            return Ok(());
        }
        source_size = source_size.map(|v| v.max(1));
        let limits = self.device.limits();
        let count = u32::try_from(pending.len()).map_err(|_| Error::Capacity)?;
        let uniform_bytes = u64::from(count) * ALIGNMENT;
        let mut texture_bytes = advanced_texture_bytes([frame.width, frame.height], source_size)?;
        if prepared.groups.root.is_some() {
            texture_bytes -= u64::from(frame.width) * u64::from(frame.height) * 4;
        }
        if u64::from(count) + host_pending.len() as u64 > 16 * 1024
            || uniform_bytes > limits.max_buffer_size
            || limits.min_uniform_buffer_offset_alignment == 0
            || !ALIGNMENT.is_multiple_of(u64::from(limits.min_uniform_buffer_offset_alignment))
            || limits.max_sampled_textures_per_shader_stage < 3
        {
            return Err(Error::Capacity);
        }
        let needs_mask = pending.iter().any(|(_, index, draw, _, _)| {
            draw.raster_operation.is_none()
                && index.is_some_and(|i| {
                    prepared
                        .masks
                        .as_ref()
                        .is_some_and(|m| m.states[i].binding.is_some())
                })
        });
        let mut state = match self.advanced.take() {
            Some(state) => state,
            None => AdvancedState::new(&self.device, self.format).await?,
        };
        if needs_mask && state.masked.is_none() {
            state.masked = Some(
                image::pipeline(&self.device, self.format, false, LayerBlend::Src, true).await?,
            );
        }
        for masked in [false, true] {
            if pending.iter().any(|(_, index, draw, _, _)| {
                draw.raster_operation.is_some()
                    && index.is_some_and(|i| {
                        prepared
                            .masks
                            .as_ref()
                            .is_some_and(|m| m.states[i].binding.is_some())
                    }) == masked
            }) && state.raster[usize::from(masked)].is_none()
            {
                state.raster[usize::from(masked)] = Some(
                    image::pipeline(
                        &self.device,
                        self.format,
                        false,
                        LayerBlend::SrcOver,
                        masked,
                    )
                    .await?,
                );
            }
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = self.populate_advanced(
            &state,
            frame,
            pending,
            source_size,
            texture_bytes,
            uniform_bytes,
            prepared.groups.root.as_ref(),
            prepared.images.as_ref(),
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let mut result = result?;
        result
            .hosts
            .try_reserve_exact(host_pending.len())
            .map_err(|_| Error::Capacity)?;
        result
            .order
            .try_reserve_exact(host_pending.len())
            .map_err(|_| Error::Capacity)?;
        for (draw_index, state_index, backdrop, scissor) in host_pending {
            let source = prepared
                .backdrops
                .as_ref()
                .ok_or(Error::ResourceDomain)?
                .host_binding(&self.device, backdrop, &result.root)?;
            result.hosts.push(HostOperation {
                draw_index,
                state_index,
                backdrop,
                source,
                scissor,
            });
        }
        result
            .order
            .extend((0..result.hosts.len()).map(OrderedOperation::Backdrop));
        result.order.sort_by_key(|operation| match operation {
            OrderedOperation::Blend(i) => result.operations[*i].draw_index,
            OrderedOperation::Backdrop(i) => result.hosts[*i].draw_index,
        });
        result.metrics.operations = result
            .metrics
            .operations
            .checked_add(result.hosts.len() as u32)
            .ok_or(Error::Capacity)?;
        result.metrics.render_passes = result
            .metrics
            .render_passes
            .checked_add(result.hosts.len() as u32 * 3)
            .ok_or(Error::Capacity)?;
        prepared.advanced = Some(result);
        self.advanced = Some(state);
        Ok(())
    }

    #[allow(clippy::too_many_arguments)] // Exact root owner and original bounded pass inventories.
    fn populate_advanced(
        &self,
        state: &AdvancedState,
        frame: RenderFrame,
        pending: Vec<(usize, Option<usize>, &AdvancedImage, [u32; 4], [u32; 4])>,
        source_size: [u32; 2],
        texture_bytes: u64,
        uniform_bytes: u64,
        root_owner: Option<&GroupTarget>,
        images: Option<&image::PreparedImages>,
    ) -> Result<PreparedAdvanced, Error> {
        let device = &self.device;
        let create = |size: [u32; 2], label| -> Result<GpuImage, Error> {
            let texture = device.create_texture(&wgpu::TextureDescriptor {
                label: Some(label),
                size: wgpu::Extent3d {
                    width: size[0],
                    height: size[1],
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
            GpuImage::from_texture(device, &texture, ImageAlpha::Premultiplied)
        };
        let root = match root_owner {
            Some(owner) => owner.image.clone(),
            None => create(
                [frame.width, frame.height],
                "ProGPU owned destination-aware root",
            )?,
        };
        let output = create([frame.width, frame.height], "ProGPU owned advanced output")?;
        let source = create(source_size, "ProGPU bounded advanced source")?;
        let root_texture = root.texture().clone();
        let root = root.texture().create_view(&Default::default());
        let output = output.texture().create_view(&Default::default());
        let source = source.texture().create_view(&Default::default());
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ProGPU original linear layer copy"),
            min_filter: wgpu::FilterMode::Linear,
            mag_filter: wgpu::FilterMode::Linear,
            lod_max_clamp: 0.0,
            ..Default::default()
        });
        let copy_binding = |view: &wgpu::TextureView| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU advanced copy image"),
                layout: &state.copy.get_bind_group_layout(1),
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(view),
                    },
                ],
            })
        };
        let root_copy = copy_binding(&root);
        let output_copy = copy_binding(&output);
        let copy_frame = frame_binding(
            device,
            &state.copy,
            frame
                .frame
                .uniforms(device.limits().max_texture_dimension_2d)?,
        );
        let mut data = Vec::new();
        data.try_reserve_exact(uniform_bytes as usize)
            .map_err(|_| Error::Capacity)?;
        data.resize(uniform_bytes as usize, 0u8);
        for (index, (_, _, draw, _, bounds)) in pending.iter().enumerate() {
            let [x, y, w, h] = *bounds;
            let mut uniforms = SamplingUniforms {
                // Preserve native subtraction AFTER each integer-to-float conversion.
                source_origin: [
                    x as f32 - frame.origin[0] as f32,
                    y as f32 - frame.origin[1] as f32,
                ],
                source_extent: [w as f32, h as f32],
                blend_mode: draw.blend as u32,
                ..SamplingUniforms::zeroed()
            };
            if let Some(operation) = &draw.raster_operation {
                raster_uniforms(&mut uniforms, operation, frame)?;
            }
            data[index * 256..index * 256 + 96].copy_from_slice(bytemuck::bytes_of(&uniforms));
        }
        let uniform_buffer = buffer(
            device,
            "ProGPU original 256-byte advanced slots",
            &data,
            wgpu::BufferUsages::UNIFORM,
        );
        let mut vertices = Vec::new();
        let mut operations = Vec::new();
        vertices
            .try_reserve_exact((pending.len() + 1) * 4)
            .map_err(|_| Error::Capacity)?;
        operations
            .try_reserve_exact(pending.len())
            .map_err(|_| Error::Capacity)?;
        for (index, (draw_index, state_index, draw, scissor, bounds)) in
            pending.into_iter().enumerate()
        {
            let [x, y, w, h] = bounds;
            let mut uniforms = Frame {
                width: source_size[0],
                height: source_size[1],
                dpi_scale: frame.dpi_scale,
            }
            .uniforms(device.limits().max_texture_dimension_2d)?;
            uniforms.render_origin = [x as f32, y as f32];
            if draw.raster_operation.is_some() {
                // Original managed bounded-source frame. Keep multiplication
                // before division and original global source pixel origin.
                let sw = source_size[0] as f32;
                let sh = source_size[1] as f32;
                let projection = [
                    2.0 * frame.dpi_scale / sw,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    -2.0 * frame.dpi_scale / sh,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    1.0,
                    0.0,
                    -1.0 - (2.0 * x as f32 / sw),
                    1.0 + (2.0 * y as f32 / sh),
                    0.0,
                    1.0,
                ];
                uniforms.projection = projection;
                uniforms.mvp = projection;
                uniforms.canvas_size = [x as f32, y as f32];
                uniforms.pad0 = 2.0;
                uniforms.render_origin = [0.0; 2];
            }
            let resolve_frame = frame_binding(device, &state.copy, uniforms);
            let blend_binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable original advanced blend binding"),
                layout: &state.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&root),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&source),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(match draw.pattern_binding {
                            Some(index) => images
                                .ok_or(Error::ResourceDomain)?
                                .views
                                .get(index)
                                .ok_or(Error::ResourceDomain)?,
                            None => &source,
                        }),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                            buffer: &uniform_buffer,
                            offset: index as u64 * ALIGNMENT,
                            size: std::num::NonZeroU64::new(96),
                        }),
                    },
                ],
            });
            let first_vertex = vertices.len() as u64 * size_of::<VectorVertex>() as u64;
            vertices.extend(quad([w, h], frame.dpi_scale, draw.opacity));
            operations.push(Operation {
                draw_index,
                state_index,
                source_binding: draw.binding,
                resolve_frame,
                blend_binding,
                scissor,
                first_vertex,
                source_range: draw.source_range.clone(),
            });
        }
        let copy_vertex = vertices.len() as u64 * size_of::<VectorVertex>() as u64;
        vertices.extend(quad([frame.width, frame.height], frame.dpi_scale, 1.0));
        let vertices = buffer(
            device,
            "ProGPU original advanced resolve/copy quads",
            bytemuck::cast_slice(&vertices),
            wgpu::BufferUsages::VERTEX,
        );
        let indices = buffer(
            device,
            "ProGPU original layer quad indices",
            bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
            wgpu::BufferUsages::INDEX,
        );
        let count = operations.len() as u32;
        let metrics = AdvancedBlendMetrics {
            operations: count,
            render_passes: count * 3 + u32::from(!root_owner.is_some_and(|r| r.fixed)),
            texture_bytes,
            uniform_bytes: uniform_bytes + (u64::from(count) + 1) * 224,
            geometry_bytes: vertices.size() + indices.size(),
        };
        Ok(PreparedAdvanced {
            state: state.clone(),
            root,
            root_texture,
            source,
            output,
            root_copy,
            output_copy,
            copy_frame,
            vertices,
            indices,
            copy_vertex,
            order: (0..operations.len()).map(OrderedOperation::Blend).collect(),
            operations,
            hosts: Vec::new(),
            metrics,
        })
    }

    pub(super) fn encode_advanced(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        scene: &PreparedScene,
        advanced: &PreparedAdvanced,
        target: &wgpu::TextureView,
        mut load: wgpu::LoadOp<wgpu::Color>,
    ) -> Result<(), Error> {
        let mut first = 0;
        let mut initial_state = None;
        for step in &advanced.order {
            if let OrderedOperation::Backdrop(index) = step {
                let operation = &advanced.hosts[*index];
                self.encode_span(
                    encoder,
                    scene,
                    &advanced.root,
                    load,
                    first..operation.draw_index,
                    initial_state,
                )?;
                advanced.copy(
                    encoder,
                    &advanced.output,
                    &advanced.root_copy,
                    wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                );
                {
                    let mut pass = begin(
                        encoder,
                        &advanced.output,
                        wgpu::LoadOp::Load,
                        "ProGPU painter-position host backdrop",
                    );
                    let [x, y, w, h] = operation.scissor;
                    pass.set_scissor_rect(x, y, w, h);
                    scene
                        .backdrops
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw(&mut pass, operation.backdrop, Some(&operation.source))?;
                }
                advanced.copy(
                    encoder,
                    &advanced.root,
                    &advanced.output_copy,
                    wgpu::LoadOp::Load,
                );
                first = operation.draw_index + 1;
                initial_state = operation.state_index;
                load = wgpu::LoadOp::Load;
                continue;
            }
            let OrderedOperation::Blend(index) = step else {
                unreachable!()
            };
            let operation = &advanced.operations[*index];
            let images = scene.images.as_ref().ok_or(Error::ResourceDomain)?;
            self.encode_span(
                encoder,
                scene,
                &advanced.root,
                load,
                first..operation.draw_index,
                initial_state,
            )?;
            let mask = operation.state_index.and_then(|i| {
                scene
                    .masks
                    .as_ref()
                    .and_then(|m| m.states[i].binding.as_ref())
            });
            {
                let mut pass = begin(
                    encoder,
                    &advanced.source,
                    wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    "ProGPU advanced bounded source resolve",
                );
                let [x, y, w, h] = operation.scissor;
                pass.set_scissor_rect(x, y, w, h);
                pass.set_pipeline(if operation.source_range.is_some() {
                    advanced.state.raster[usize::from(mask.is_some())]
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                } else if mask.is_some() {
                    advanced
                        .state
                        .masked
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                } else {
                    &advanced.state.copy
                });
                pass.set_bind_group(0, &operation.resolve_frame, &[]);
                pass.set_bind_group(
                    1,
                    images
                        .bindings
                        .get(operation.source_binding)
                        .ok_or(Error::ResourceDomain)?,
                    &[],
                );
                if let Some(mask) = mask {
                    pass.set_bind_group(2, mask, &[]);
                }
                if let Some(range) = &operation.source_range {
                    pass.set_vertex_buffer(0, images.vertices.slice(..));
                    pass.set_index_buffer(images.indices.slice(..), wgpu::IndexFormat::Uint32);
                    pass.draw_indexed(range.clone(), 0, 0..1);
                } else {
                    pass.set_vertex_buffer(
                        0,
                        advanced
                            .vertices
                            .slice(operation.first_vertex..operation.first_vertex + 224),
                    );
                    pass.set_index_buffer(advanced.indices.slice(..), wgpu::IndexFormat::Uint32);
                    pass.draw_indexed(0..6, 0, 0..1);
                }
            }
            {
                let mut pass = begin(
                    encoder,
                    &advanced.output,
                    wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    "ProGPU original destination-aware blend",
                );
                pass.set_pipeline(&advanced.state.pipeline);
                pass.set_bind_group(0, &operation.blend_binding, &[]);
                pass.draw(0..3, 0..1);
            }
            advanced.copy(
                encoder,
                &advanced.root,
                &advanced.output_copy,
                wgpu::LoadOp::Load,
            );
            first = operation.draw_index + 1;
            initial_state = operation.state_index;
            load = wgpu::LoadOp::Load;
        }
        if first < scene.draws.len() {
            self.encode_span(
                encoder,
                scene,
                &advanced.root,
                load,
                first..scene.draws.len(),
                initial_state,
            )?;
        }
        if !scene.fixed_target() {
            advanced.copy(
                encoder,
                target,
                &advanced.root_copy,
                wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
            );
        }
        Ok(())
    }
}
impl PreparedAdvanced {
    fn copy(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        image: &wgpu::BindGroup,
        load: wgpu::LoadOp<wgpu::Color>,
    ) {
        let mut pass = begin(
            encoder,
            target,
            load,
            "ProGPU original advanced parent/root replace",
        );
        pass.set_pipeline(&self.state.copy);
        pass.set_bind_group(0, &self.copy_frame, &[]);
        pass.set_bind_group(1, image, &[]);
        pass.set_vertex_buffer(
            0,
            self.vertices
                .slice(self.copy_vertex..self.copy_vertex + 224),
        );
        pass.set_index_buffer(self.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..6, 0, 0..1);
    }
}
pub(super) fn advanced_texture_bytes(frame: [u32; 2], source: [u32; 2]) -> Result<u64, Error> {
    let bytes = |size: [u32; 2]| {
        u64::from(size[0])
            .checked_mul(u64::from(size[1]))
            .and_then(|v| v.checked_mul(4))
            .ok_or(Error::Capacity)
    };
    let root = bytes(frame)?;
    let source = bytes(source)?;
    if root > 256 * 1024 * 1024 || source > 256 * 1024 * 1024 {
        return Err(Error::Capacity);
    }
    let total = root
        .checked_mul(2)
        .and_then(|v| v.checked_add(source))
        .ok_or(Error::Capacity)?;
    if total > 512 * 1024 * 1024 {
        return Err(Error::Capacity);
    }
    Ok(total)
}

fn raster_bounds(
    compiled: &CompiledScene,
    range: &std::ops::Range<u32>,
    frame: RenderFrame,
) -> Result<[u32; 4], Error> {
    let indices = compiled
        .image_mesh
        .indices()
        .get(range.start as usize..range.end as usize)
        .ok_or(Error::ResourceDomain)?;
    if indices.is_empty() {
        return Ok([0; 4]);
    }
    let mut extent = [
        f32::INFINITY,
        f32::INFINITY,
        f32::NEG_INFINITY,
        f32::NEG_INFINITY,
    ];
    // Original ordered finite index inventory. Independent XY extent updates
    // retain source min/max order; physical XY projection uses independent SIMD lanes.
    for index in indices {
        let [x, y] = compiled
            .image_mesh
            .vertices()
            .get(*index as usize)
            .ok_or(Error::ResourceDomain)?
            .position;
        if !x.is_finite() || !y.is_finite() {
            return Err(Error::InvalidInput);
        }
        extent[0] = extent[0].min(x);
        extent[1] = extent[1].min(y);
        extent[2] = extent[2].max(x);
        extent[3] = extent[3].max(y);
    }
    let physical = (wide::f32x4::from(extent) * wide::f32x4::splat(frame.dpi_scale)).to_array();
    if physical.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    let [ox, oy] = frame.origin;
    let left = (physical[0].floor().clamp(0.0, frame.root.width as f32) as u32).max(ox);
    let top = (physical[1].floor().clamp(0.0, frame.root.height as f32) as u32).max(oy);
    let right =
        (physical[2].ceil().clamp(0.0, frame.root.width as f32) as u32).min(ox + frame.width);
    let bottom =
        (physical[3].ceil().clamp(0.0, frame.root.height as f32) as u32).min(oy + frame.height);
    Ok([
        left,
        top,
        right.saturating_sub(left),
        bottom.saturating_sub(top),
    ])
}
fn raster_uniforms(
    uniforms: &mut SamplingUniforms,
    operation: &crate::composition::RasterOperation,
    frame: RenderFrame,
) -> Result<(), Error> {
    use crate::composition::RasterPattern;
    operation.validate()?;
    uniforms.operation_kind = 1;
    uniforms.raster_operation_code = u32::from(operation.code);
    let origin = match &operation.pattern {
        RasterPattern::Solid(color) => {
            uniforms.pattern_color = *color;
            return Ok(());
        }
        RasterPattern::Tile {
            bits,
            foreground,
            background,
            origin,
        } => {
            uniforms.pattern_kind = 1;
            uniforms.pattern_color = *foreground;
            uniforms.pattern_background_color = *background;
            uniforms.pattern_mask_low = *bits as u32;
            uniforms.pattern_mask_high = (*bits >> 32) as u32;
            uniforms.pattern_flags = u32::from(background[3] <= 0.0);
            *origin
        }
        RasterPattern::Texture { image, origin } => {
            uniforms.pattern_kind = 2;
            uniforms.pattern_texture_extent = image.size().map(|v| v as f32);
            *origin
        }
    };
    let original = (wide::f32x4::from([origin[0], origin[1], 0.0, 0.0])
        * wide::f32x4::splat(frame.dpi_scale))
    .to_array();
    uniforms.pattern_origin = [
        original[0] - frame.origin[0] as f32,
        original[1] - frame.origin[1] as f32,
    ];
    if uniforms.pattern_origin.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
