//! Original brush-alpha drawing and bounded sampled-mask materialization.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    mask::{
        Mask, MaskChannel, MaskComposer, MaskCompositionMetrics, MaskSampling, MaskUniforms,
        SampledMaskInput, TextureMask,
    },
    scene::CompiledDrawState,
};
use std::collections::{HashMap, HashSet};

/// Original shader brush evaluation frame, explicitly independent of geometry.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum MaskBrushFrame {
    /// Original native brush/geometry opacity masks use target-space materials.
    #[default]
    Target,
    /// Original ordinary geometry-mask shader retains primitive-local materials.
    Local,
}
/// Actual GPU-generated R8 alpha coverage and its preparation evidence.
pub struct CapturedMask {
    image: GpuImage,
    source: UploadMetrics,
    metrics: MaskCompositionMetrics,
}
impl CapturedMask {
    /// Actual GPU-generated R8 coverage owner.
    pub fn image(&self) -> &GpuImage {
        &self.image
    }
    /// Transfer the completed source texture into a retained sampled mask.
    pub fn into_image(self) -> GpuImage {
        self.image
    }
    /// Actual earlier geometry/material/path preparation work.
    pub fn source_metrics(&self) -> UploadMetrics {
        self.source
    }
    /// Actual mask target allocation and encoded render work.
    pub fn metrics(&self) -> MaskCompositionMetrics {
        self.metrics
    }
}
pub(super) struct MaterialMaskState {
    composer: MaskComposer,
    coverage: Option<wgpu::RenderPipeline>,
    geometry: [Option<wgpu::RenderPipeline>; 2],
}
impl MaterialMaskState {
    fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Result<Self, Error> {
        Ok(Self {
            composer: MaskComposer::new(device, queue, ExecutionPreference::Automatic)?,
            coverage: None,
            geometry: [None, None],
        })
    }
    async fn coverage(&mut self, device: &wgpu::Device) -> Result<wgpu::RenderPipeline, Error> {
        if self.coverage.is_none() {
            self.coverage = Some(
                image::pipeline_entry(
                    device,
                    wgpu::TextureFormat::R8Unorm,
                    false,
                    crate::scene::LayerBlend::Src,
                    true,
                    Some("fs_layer_coverage"),
                )
                .await?,
            );
        }
        Ok(self.coverage.as_ref().ok_or(Error::ResourceDomain)?.clone())
    }
    async fn geometry(
        &mut self,
        device: &wgpu::Device,
        frame: MaskBrushFrame,
    ) -> Result<wgpu::RenderPipeline, Error> {
        let index = usize::from(frame == MaskBrushFrame::Local);
        if let Some(p) = &self.geometry[index] {
            return Ok(p.clone());
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical brush-mask shader"),
            source: wgpu::ShaderSource::Wgsl(shaders::VECTOR.into()),
        });
        let layout = layout::pipeline(device, layout::Family::Vector, None);
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original R8 brush-mask pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: 56,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &attributes,
                })],
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some(if frame == MaskBrushFrame::Target {
                    "fs_mask_target_space"
                } else {
                    "fs_mask_unmasked"
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
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.geometry[index] = Some(pipeline.clone());
        Ok(pipeline)
    }
}
impl PreparedScene {
    /// Actual preparation work for materialized masks. Stable scene replay
    /// samples their retained outputs without recomputing these passes.
    pub fn mask_composition_metrics(&self) -> MaskCompositionMetrics {
        self.masks
            .as_ref()
            .map_or_else(Default::default, |m| m.composition)
    }
}
impl Renderer {
    pub(super) fn mask_resource_cost(
        &self,
        scene: &Scene,
        states: &[CompiledDrawState],
        changes: &[Option<usize>],
        frame: RenderFrame,
    ) -> Result<(u64, u32), Error> {
        mask_cost(states, scene.used_mask_states(changes), frame)
    }
    pub(super) fn compiled_mask_cost(
        &self,
        compiled: &CompiledScene,
        frame: RenderFrame,
    ) -> Result<(u64, u32), Error> {
        let mut current = 0;
        let used = compiled.draws.iter().filter_map(|draw| match draw {
            DrawRange::State(index) => {
                current = *index;
                None
            }
            DrawRange::Clear(_) | DrawRange::Axis(_) => None,
            DrawRange::ImageEffect(index) if compiled.image_effects[*index].mask.is_some() => None,
            _ => Some(current),
        });
        mask_cost(&compiled.states, used, frame)
    }

    /// Render original compiled vector/path/stroke material alpha to R8 with
    /// the native no-blend overwrite policy. Full source geometry is preserved;
    /// picture composition instead uses an ordinary RGBA capture's alpha.
    pub async fn capture_geometry_mask(
        &mut self,
        compiled: &CompiledScene,
        frame: Frame,
        brush_frame: MaskBrushFrame,
    ) -> Result<CapturedMask, Error> {
        if compiled.states.len() != 1
            || compiled.states[0].clip.is_some()
            || compiled
                .draws
                .iter()
                .any(|d| !matches!(d, DrawRange::Vector(_)))
        {
            return Err(Error::UnsupportedExecution);
        }
        frame.uniforms(self.device.limits().max_texture_dimension_2d)?;
        let bytes = u64::from(frame.width) * u64::from(frame.height);
        if bytes > 512 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        let prepared = self.prepare_compiled(compiled, frame).await?;
        let mut state = match self.materialized_masks.take() {
            Some(s) => s,
            None => MaterialMaskState::new(&self.device, &self.queue)?,
        };
        let pipeline = state.geometry(&self.device, brush_frame).await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let output = texture(
            &self.device,
            [frame.width, frame.height],
            "ProGPU retained geometry/brush mask",
        );
        let view = output.create_view(&Default::default());
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU original geometry-mask capture"),
            });
        {
            let mut pass = begin(&mut encoder, &view);
            pass.set_pipeline(&pipeline);
            pass.set_bind_group(0, &prepared.bindings, &[]);
            pass.set_bind_group(1, &prepared.atlas, &[]);
            pass.set_vertex_buffer(0, prepared.vertices.slice(..));
            pass.set_index_buffer(prepared.indices.slice(..), wgpu::IndexFormat::Uint32);
            for draw in &prepared.draws {
                if let DrawRange::Vector(range) = draw {
                    pass.draw_indexed(range.clone(), 0, 0..1);
                }
            }
        }
        let command = encoder.finish();
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.queue.submit([command]);
        self.materialized_masks = Some(state);
        Ok(CapturedMask {
            image: GpuImage::from_texture(&self.device, &output, ImageAlpha::Straight)?,
            source: prepared.metrics,
            metrics: MaskCompositionMetrics {
                nodes: 1,
                render_passes: 1,
                texture_bytes: bytes,
                ..Default::default()
            },
        })
    }
    pub(super) async fn resolve_mask_states(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        cache: &mut mask::MaskState,
        uploads: &mut UploadMetrics,
    ) -> Result<(Vec<CompiledDrawState>, MaskCompositionMetrics), Error> {
        let mut used = HashSet::new();
        let mut current = 0;
        for draw in &compiled.draws {
            if let DrawRange::State(index) = draw {
                current = *index;
                continue;
            }
            if !matches!(draw, DrawRange::Clear(_) | DrawRange::Axis(_))
                && frame.clip_intersects(compiled.states[current].clip)
            {
                if let DrawRange::ImageEffect(index) = draw
                    && compiled.image_effects[*index].mask.is_some()
                {
                    continue;
                }
                used.insert(current);
            }
        }
        let mut resolved = compiled.states.clone();
        let mut metrics = MaskCompositionMetrics::default();
        let mut outputs = HashMap::<usize, (TextureMask, MaskUniforms)>::new();
        let mut state = None;
        let mut textures = HashSet::new();
        // Whole active inventory before allocating any mask output.
        mask_cost(&resolved, used.iter().copied(), frame)?;
        // Stable state order preserves deterministic uploads and submission order.
        for (index, source) in resolved.iter_mut().enumerate() {
            if !source.materialized || !used.contains(&index) {
                continue;
            }
            let identity = Arc::as_ptr(&source.components) as *const () as usize;
            let (coverage, uniform) = if let Some(output) = outputs.get(&identity) {
                output.clone()
            } else {
                if state.is_none() {
                    state = Some(match self.materialized_masks.take() {
                        Some(s) => s,
                        None => MaterialMaskState::new(&self.device, &self.queue)?,
                    });
                }
                let (image, m) = self
                    .materialize_mask(
                        source,
                        frame,
                        cache,
                        state.as_mut().ok_or(Error::ResourceDomain)?,
                        uploads,
                        &mut textures,
                    )
                    .await?;
                metrics.merge(m)?;
                let coverage = TextureMask::new(
                    image,
                    [0.0, 0.0, frame.width as f32, frame.height as f32],
                    MaskChannel::Red,
                );
                let uniform = MaskUniforms {
                    coordinate0: [frame.origin[0] as f32, frame.origin[1] as f32, 0.0, 0.0],
                    coordinate1: [
                        1.0 / frame.width as f32,
                        1.0 / frame.height as f32,
                        0.0,
                        0.0,
                    ],
                    options: [1.0, 1.0, 0.0, 1.0],
                    ..Zeroable::zeroed()
                };
                outputs.insert(identity, (coverage.clone(), uniform));
                (coverage, uniform)
            };
            source.coverage = Some(coverage);
            source.masks = [
                uniform,
                MaskUniforms::zeroed(),
                MaskUniforms::zeroed(),
                MaskUniforms::zeroed(),
            ];
            source.count = 1;
            source.materialized = false;
        }
        if let Some(state) = state {
            self.materialized_masks = Some(state);
        }
        Ok((resolved, metrics))
    }
    async fn materialize_mask(
        &self,
        source: &CompiledDrawState,
        frame: RenderFrame,
        cache: &mut mask::MaskState,
        state: &mut MaterialMaskState,
        uploads: &mut UploadMetrics,
        textures: &mut HashSet<(u8, usize)>,
    ) -> Result<(GpuImage, MaskCompositionMetrics), Error> {
        let size = [frame.width, frame.height];
        let has_analytic = source
            .components
            .iter()
            .any(|c| matches!(c.mask, Mask::Analytic(_)));
        let pipeline = if has_analytic {
            Some(state.coverage(&self.device).await?)
        } else {
            None
        };
        let scratch =
            has_analytic.then(|| texture(&self.device, size, "ProGPU reusable analytic-mask node"));
        let scratch_image = scratch
            .as_ref()
            .map(|t| GpuImage::from_texture(&self.device, t, ImageAlpha::Straight))
            .transpose()?;
        let mut inputs = Vec::new();
        let mut producers = Vec::new();
        for component in source.components.iter() {
            let (image, uniforms, sampling, producer) = match &component.mask {
                Mask::Analytic(_) => {
                    let direct =
                        CompiledDrawState::direct(component.mask.clone(), frame.dpi_scale)?;
                    let binding =
                        cache.binding(&self.device, &self.queue, &direct, uploads, textures)?;
                    let uniforms = MaskUniforms {
                        coordinate1: [
                            1.0 / frame.width as f32,
                            1.0 / frame.height as f32,
                            0.0,
                            0.0,
                        ],
                        options: [1.0, 1.0, 0.0, 1.0],
                        ..Zeroable::zeroed()
                    };
                    (
                        scratch_image.as_ref().ok_or(Error::ResourceDomain)?.clone(),
                        uniforms,
                        MaskSampling::Nearest,
                        Some(binding),
                    )
                }
                _ => {
                    let sampled = match &component.mask {
                        Mask::Coverage(c) => c.clone().into(),
                        Mask::Texture(c) => c.clone(),
                        _ => unreachable!(),
                    };
                    let uniforms = sampled.uniforms_for_target(frame.dpi_scale, frame.origin)?;
                    (
                        cache.image(
                            &self.device,
                            &self.queue,
                            &sampled.source,
                            uploads,
                            textures,
                        )?,
                        uniforms,
                        sampled.sampling,
                        None,
                    )
                }
            };
            inputs.push(SampledMaskInput {
                image,
                uniforms,
                sampling,
                operation: component.operation,
            });
            producers.push(producer);
        }
        let prepared = state.composer.prepare_sampled(&inputs, size).await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let geometry = if let Some(pipeline) = &pipeline {
            let mut vertices = advanced::quad(size, frame.dpi_scale, 1.0);
            for vertex in &mut vertices {
                vertex.position[0] += frame.origin[0] as f32 / frame.dpi_scale;
                vertex.position[1] += frame.origin[1] as f32 / frame.dpi_scale;
            }
            let vertices = advanced::buffer(
                &self.device,
                "ProGPU mask coverage quad",
                bytemuck::cast_slice(&vertices),
                wgpu::BufferUsages::VERTEX,
            );
            let indices = advanced::buffer(
                &self.device,
                "ProGPU mask coverage indices",
                bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
                wgpu::BufferUsages::INDEX,
            );
            let frame = advanced::frame_binding(
                &self.device,
                pipeline,
                frame.uniforms(self.device.limits().max_texture_dimension_2d)?,
            );
            let sampler = self.device.create_sampler(&Default::default());
            let placeholder = cache.placeholder();
            let image = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU unused coverage image"),
                layout: &pipeline.get_bind_group_layout(1),
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&placeholder),
                    },
                ],
            });
            Some((
                vertices,
                indices,
                frame,
                image,
                scratch
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?
                    .create_view(&Default::default()),
            ))
        } else {
            None
        };
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU bounded retained mask composition"),
            });
        for (index, producer) in producers.iter().enumerate() {
            if let Some(binding) = producer {
                let (vertices, indices, frame, image, view) =
                    geometry.as_ref().ok_or(Error::ResourceDomain)?;
                let mut pass = begin(&mut encoder, view);
                pass.set_pipeline(pipeline.as_ref().ok_or(Error::ResourceDomain)?);
                pass.set_bind_group(0, frame, &[]);
                pass.set_bind_group(1, image, &[]);
                pass.set_bind_group(2, binding, &[]);
                pass.set_vertex_buffer(0, vertices.slice(..));
                pass.set_index_buffer(indices.slice(..), wgpu::IndexFormat::Uint32);
                pass.draw_indexed(0..6, 0, 0..1);
            }
            prepared.encode_sampled_step(&mut encoder, index)?;
        }
        let command = encoder.finish();
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.queue.submit([command]);
        let mut metrics = prepared.metrics();
        uploads.uniform_bytes += metrics.uniform_bytes;
        if let Some((vertices, indices, ..)) = &geometry {
            let count = producers.iter().filter(|p| p.is_some()).count() as u32;
            metrics.render_passes += count;
            metrics.texture_bytes += u64::from(size[0]) * u64::from(size[1]);
            metrics.uniform_bytes += 224 + u64::from(count) * 384;
            metrics.geometry_bytes += vertices.size() + indices.size();
            uploads.uniform_bytes += 224;
            uploads.geometry_bytes += metrics.geometry_bytes;
        }
        Ok((prepared.into_image(), metrics))
    }
}
fn texture(device: &wgpu::Device, size: [u32; 2], label: &str) -> wgpu::Texture {
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
        format: wgpu::TextureFormat::R8Unorm,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::RENDER_ATTACHMENT,
        view_formats: &[],
    })
}
fn begin<'a>(
    encoder: &'a mut wgpu::CommandEncoder,
    view: &wgpu::TextureView,
) -> wgpu::RenderPass<'a> {
    encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some("ProGPU original mask source"),
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

fn mask_cost(
    states: &[CompiledDrawState],
    used: impl Iterator<Item = usize>,
    frame: RenderFrame,
) -> Result<(u64, u32), Error> {
    let mut identities = HashSet::new();
    let mut bytes = 0u64;
    let mut passes = 0u32;
    for index in used {
        let source = &states[index];
        if !source.materialized || !frame.clip_intersects(source.clip) {
            continue;
        }
        if !identities.insert(Arc::as_ptr(&source.components) as *const () as usize) {
            continue;
        }
        let analytic = source
            .components
            .iter()
            .filter(|c| matches!(c.mask, Mask::Analytic(_)))
            .count();
        bytes = bytes
            .checked_add(
                u64::from(frame.width) * u64::from(frame.height) * (2 + u64::from(analytic != 0)),
            )
            .ok_or(Error::Capacity)?;
        passes = passes
            .checked_add((source.components.len() + analytic) as u32)
            .ok_or(Error::Capacity)?;
        if bytes > 512 * 1024 * 1024 || passes > 16384 {
            return Err(Error::Capacity);
        }
    }
    Ok((bytes, passes))
}
