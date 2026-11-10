//! Canonical managed BackdropMaterial extension with immutable source bindings.
use super::*;
use crate::{
    backdrop::{BackdropMaterialDraw, BackdropMaterialSource},
    image::{GpuImage, ImageAlpha, ImageSampling},
    mask::MaskUniforms,
};
use std::collections::HashMap;

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct MaterialUniforms {
    tint_color: [f32; 4],
    luminosity_color: [f32; 4],
    fallback_color: [f32; 4],
    noise_color: [f32; 4],
    material0: [f32; 4],
    material1: [f32; 4],
    geometry0: [f32; 4],
    radii_x: [f32; 4],
    radii_y: [f32; 4],
    flags0: [f32; 4],
    source_uv_rect: [f32; 4],
}
const _: () = assert!(size_of::<MaterialUniforms>() == 176);
/// Actual immutable extension resources, independent from final replay work.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BackdropMetrics {
    /// Nonempty original material commands.
    pub draws: u32,
    /// Nonempty commands requiring actual painter-position destination capture.
    pub host_draws: u32,
    /// Actual immutable material, mask and frame records uploaded.
    pub uniform_bytes: u64,
    /// Actual original quad/index storage, including empty draw placeholders.
    pub geometry_bytes: u64,
    /// Base-level source bytes referenced per explicit draw; no upload is implied.
    pub source_texture_bytes: u64,
}
#[derive(Default)]
pub(super) struct BackdropState {
    pipelines: HashMap<crate::scene::LayerBlend, wgpu::RenderPipeline>,
}
struct Draw {
    pipeline: wgpu::RenderPipeline,
    frame: wgpu::BindGroup,
    material: wgpu::BindGroup,
    source: wgpu::BindGroup,
    mask: wgpu::BindGroup,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    image: GpuImage,
    sampler: wgpu::Sampler,
    host: bool,
    visible: bool,
}
pub(super) struct PreparedBackdrops {
    draws: Vec<Draw>,
    metrics: BackdropMetrics,
}
impl PreparedBackdrops {
    pub(super) fn is_host(&self, index: usize) -> bool {
        self.draws.get(index).is_some_and(|v| v.host && v.visible)
    }
    pub(super) fn draw_count(&self, index: usize) -> Result<u32, Error> {
        Ok(u32::from(
            self.draws.get(index).ok_or(Error::ResourceDomain)?.visible,
        ))
    }
    pub(super) fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        index: usize,
        host: Option<&wgpu::BindGroup>,
    ) -> Result<(), Error> {
        let draw = self.draws.get(index).ok_or(Error::ResourceDomain)?;
        if !draw.visible {
            return Ok(());
        }
        if draw.host != host.is_some() {
            return Err(Error::ResourceDomain);
        }
        pass.set_pipeline(&draw.pipeline);
        pass.set_bind_group(0, &draw.frame, &[]);
        pass.set_bind_group(1, &draw.material, &[]);
        pass.set_bind_group(2, host.unwrap_or(&draw.source), &[]);
        pass.set_bind_group(3, &draw.mask, &[]);
        pass.set_vertex_buffer(0, draw.vertices.slice(..));
        pass.set_index_buffer(draw.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..6, 0, 0..1);
        Ok(())
    }
    pub(super) fn host_binding(
        &self,
        device: &wgpu::Device,
        index: usize,
        source: &wgpu::TextureView,
    ) -> Result<wgpu::BindGroup, Error> {
        let draw = self.draws.get(index).ok_or(Error::ResourceDomain)?;
        if !draw.host {
            return Err(Error::ResourceDomain);
        }
        Ok(source_binding(
            device,
            &draw.pipeline,
            &draw.sampler,
            source,
        ))
    }
    pub(super) fn retain_submitted(&self, queue: &wgpu::Queue) {
        for draw in &self.draws {
            draw.image.retain_submitted(queue);
        }
    }
}
impl PreparedScene {
    /// Actual extension preparation; host copies are reported by advanced metrics.
    pub fn backdrop_metrics(&self) -> BackdropMetrics {
        self.backdrops
            .as_ref()
            .map_or_else(Default::default, |v| v.metrics)
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
fn sampler_entry() -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding: 0,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count: None,
    }
}
fn texture_entry() -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding: 1,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    }
}
impl BackdropState {
    async fn pipeline(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        blend: crate::scene::LayerBlend,
    ) -> Result<wgpu::RenderPipeline, Error> {
        if let Some(pipeline) = self.pipelines.get(&blend) {
            return Ok(pipeline.clone());
        }
        let blend_state = blend.fixed()?;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = |label, entries: &[wgpu::BindGroupLayoutEntry]| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some(label),
                entries,
            })
        };
        let frame = layout(
            "ProGPU backdrop original vertex frame",
            &[uniform_entry(0, 192, wgpu::ShaderStages::VERTEX)],
        );
        let material = layout(
            "ProGPU backdrop original material",
            &[uniform_entry(0, 176, wgpu::ShaderStages::FRAGMENT)],
        );
        let source = layout(
            "ProGPU backdrop original source",
            &[sampler_entry(), texture_entry()],
        );
        let mask = layout(
            "ProGPU backdrop original mask",
            &[
                sampler_entry(),
                texture_entry(),
                uniform_entry(2, 96, wgpu::ShaderStages::FRAGMENT),
            ],
        );
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU canonical backdrop layouts"),
            bind_group_layouts: &[Some(&frame), Some(&material), Some(&source), Some(&mask)],
            immediate_size: 0,
        });
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical BackdropMaterial.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::BACKDROP_MATERIAL.into()),
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original backdrop material"),
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
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(blend_state),
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
        self.pipelines.insert(blend, pipeline.clone());
        Ok(pipeline)
    }
}
fn binding(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    group: u32,
    bytes: &[u8],
) -> wgpu::BindGroup {
    let buffer = advanced::buffer(
        device,
        "ProGPU immutable backdrop uniform",
        bytes,
        wgpu::BufferUsages::UNIFORM,
    );
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU backdrop uniform binding"),
        layout: &pipeline.get_bind_group_layout(group),
        entries: &[wgpu::BindGroupEntry {
            binding: 0,
            resource: buffer.as_entire_binding(),
        }],
    })
}
fn source_binding(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    sampler: &wgpu::Sampler,
    view: &wgpu::TextureView,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU immutable backdrop source"),
        layout: &pipeline.get_bind_group_layout(2),
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(view),
            },
        ],
    })
}
fn flag(value: bool) -> f32 {
    u32::from(value) as f32
}
fn clamp(value: f32, low: f32, high: f32) -> f32 {
    if value.is_finite() {
        value.clamp(low, high)
    } else {
        low
    }
}
fn color(v: [f32; 4]) -> [f32; 4] {
    v.map(|v| clamp(v, 0.0, 1.0))
}
fn uniforms(
    draw: &BackdropMaterialDraw,
    frame: RenderFrame,
    image: &GpuImage,
    has_mask: bool,
) -> MaterialUniforms {
    let p = &draw.parameters;
    let b = &p.brush;
    let host = p.captures_host_backdrop();
    MaterialUniforms {
        tint_color: color(b.tint_color),
        luminosity_color: color(b.luminosity_color),
        fallback_color: color(b.fallback_color),
        noise_color: color(b.noise_color),
        material0: [
            b.tint_opacity,
            b.luminosity_opacity,
            b.material_opacity,
            b.noise_opacity,
        ]
        .map(|v| clamp(v, 0.0, 1.0)),
        material1: [
            clamp(b.blur_radius, 0.0, 96.0),
            clamp(b.saturation, 0.0, 4.0),
            b.kind as u32 as f32,
            flag(
                b.use_fallback
                    || (b.source == BackdropMaterialSource::Texture && p.source_texture.is_none()),
            ),
        ],
        geometry0: [
            p.rect[2].abs().max(0.0001),
            p.rect[3].abs().max(0.0001),
            (frame.width as f32).max(1.0),
            (frame.height as f32).max(1.0),
        ],
        radii_x: p
            .corner_radii_x
            .map(|v| if v.is_finite() { v.max(0.0) } else { 0.0 }),
        radii_y: p
            .corner_radii_y
            .map(|v| if v.is_finite() { v.max(0.0) } else { 0.0 }),
        flags0: [
            flag((p.source_texture.is_some() || host) && b.source != BackdropMaterialSource::None),
            flag(has_mask),
            flag(host || image.alpha() == ImageAlpha::Premultiplied),
            flag(host),
        ],
        source_uv_rect: draw.source_uv_rect(),
    }
}
impl Renderer {
    pub(super) fn preflight_backdrops(
        &self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<(), Error> {
        let (states, changes) = scene.compile_states(frame.dpi_scale)?;
        let mut current = 0;
        let mut cursor = 0;
        let mut count = 0;
        for (index, draw) in scene.backdrop_materials() {
            for next in changes.get(cursor..=index).unwrap_or(&[]).iter().flatten() {
                current = *next;
            }
            cursor = index + 1;
            draw.preflight_device(&self.device)?;
            draw.vertices(states[current].logical_clip)?;
            if frame.origin != [0; 2] && states[current].count != 0 && !states[current].materialized
            {
                return Err(Error::UnsupportedExecution);
            }
            count += 1;
            if count > 16384 {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
    pub(super) fn validate_backdrops(
        &self,
        compiled: &CompiledScene,
        frame: RenderFrame,
    ) -> Result<(), Error> {
        if compiled.backdrop_materials.len() > 16384 || self.device.limits().max_bind_groups < 4 {
            return Err(Error::Capacity);
        }
        let mut current = 0;
        for command in &compiled.draws {
            match command {
                DrawRange::State(index) => current = *index,
                DrawRange::BackdropMaterial(index) => {
                    let draw = compiled
                        .backdrop_materials
                        .get(*index)
                        .ok_or(Error::ResourceDomain)?;
                    draw.preflight_device(&self.device)?;
                    draw.vertices(compiled.states[current].logical_clip)?;
                    if frame.origin != [0; 2]
                        && compiled.states[current].count != 0
                        && !compiled.states[current].materialized
                    {
                        return Err(Error::UnsupportedExecution);
                    }
                }
                _ => {}
            }
        }
        Ok(())
    }
    pub(super) async fn prepare_backdrops(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.backdrop_materials.is_empty() {
            return Ok(());
        }
        self.validate_backdrops(compiled, frame)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let placeholder = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU original transparent backdrop"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let placeholder =
            GpuImage::from_texture(&self.device, &placeholder, ImageAlpha::Premultiplied)?;
        let mut engine = std::mem::take(&mut self.backdrop_state);
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(compiled.backdrop_materials.len())
            .map_err(|_| Error::Capacity)?;
        let mut metrics = BackdropMetrics::default();
        let mut current = 0;
        for command in &compiled.draws {
            if let DrawRange::State(index) = command {
                current = *index;
                continue;
            }
            let DrawRange::BackdropMaterial(index) = command else {
                continue;
            };
            if *index != draws.len() {
                return Err(Error::ResourceDomain);
            }
            let draw = &compiled.backdrop_materials[*index];
            let source_state = &compiled.states[current];
            let resolved = prepared
                .masks
                .as_ref()
                .and_then(|m| m.resolved.get(current))
                .unwrap_or(source_state);
            let vertices = draw.vertices(resolved.logical_clip)?;
            let host = draw.parameters.captures_host_backdrop();
            let pipeline = engine
                .pipeline(
                    &self.device,
                    self.format,
                    if host {
                        crate::scene::LayerBlend::Src
                    } else {
                        draw.blend
                    },
                )
                .await?;
            let image = draw
                .parameters
                .source_texture
                .as_ref()
                .unwrap_or(&placeholder)
                .clone();
            let [mag, min, mip] = draw.parameters.sampling.filters();
            let filter = |v| {
                if v {
                    wgpu::FilterMode::Linear
                } else {
                    wgpu::FilterMode::Nearest
                }
            };
            let sampler = self.device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU backdrop original sampler"),
                mag_filter: filter(mag),
                min_filter: filter(min),
                mipmap_filter: if mip {
                    wgpu::MipmapFilterMode::Linear
                } else {
                    wgpu::MipmapFilterMode::Nearest
                },
                lod_max_clamp: if draw.parameters.sampling.uses_mipmaps() {
                    32.0
                } else {
                    0.0
                },
                ..Default::default()
            });
            let source = source_binding(
                &self.device,
                &pipeline,
                &sampler,
                &image.texture().create_view(&Default::default()),
            );
            let mut mask_uniform = MaskUniforms::zeroed();
            let mut mask_image = placeholder.clone();
            let mut mask_sampling = ImageSampling::Linear;
            if resolved.count != 0 {
                if resolved.count != 1 {
                    return Err(Error::UnsupportedExecution);
                }
                mask_uniform = resolved.masks[0];
                if resolved.local_coverage {
                    mask_uniform.coordinate0[0] -= frame.origin[0] as f32;
                    mask_uniform.coordinate0[1] -= frame.origin[1] as f32;
                } else if frame.origin != [0; 2] {
                    return Err(Error::UnsupportedExecution);
                }
                if let Some(coverage) = &resolved.coverage {
                    mask_image = self.masks.as_mut().ok_or(Error::ResourceDomain)?.image(
                        &self.device,
                        &self.queue,
                        &coverage.source,
                        &mut UploadMetrics::default(),
                        &mut Default::default(),
                    )?;
                    mask_sampling = if coverage.sampling == crate::mask::MaskSampling::Nearest {
                        ImageSampling::Nearest
                    } else {
                        ImageSampling::Linear
                    };
                }
                if !matches!(mask_uniform.options[0], 0.0 | 1.0 | 2.0 | 3.0 | 4.0) {
                    return Err(Error::UnsupportedExecution);
                }
            }
            let mask_sampler = self.device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU backdrop original mask sampler"),
                mag_filter: if mask_sampling == ImageSampling::Nearest {
                    wgpu::FilterMode::Nearest
                } else {
                    wgpu::FilterMode::Linear
                },
                min_filter: if mask_sampling == ImageSampling::Nearest {
                    wgpu::FilterMode::Nearest
                } else {
                    wgpu::FilterMode::Linear
                },
                ..Default::default()
            });
            let mask_buffer = advanced::buffer(
                &self.device,
                "ProGPU backdrop immutable mask",
                bytemuck::bytes_of(&mask_uniform),
                wgpu::BufferUsages::UNIFORM,
            );
            let mask_view = mask_image.texture().create_view(&Default::default());
            let mask = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU backdrop exact mask"),
                layout: &pipeline.get_bind_group_layout(3),
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&mask_sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&mask_view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: mask_buffer.as_entire_binding(),
                    },
                ],
            });
            let material = binding(
                &self.device,
                &pipeline,
                1,
                bytemuck::bytes_of(&uniforms(draw, frame, &image, resolved.count != 0)),
            );
            let frame_binding = binding(
                &self.device,
                &pipeline,
                0,
                bytemuck::bytes_of(&frame.uniforms(self.device.limits().max_texture_dimension_2d)?),
            );
            let vertex_buffer = advanced::buffer(
                &self.device,
                "ProGPU original backdrop quad",
                bytemuck::cast_slice(&vertices.unwrap_or([VectorVertex::zeroed(); 4])),
                wgpu::BufferUsages::VERTEX,
            );
            let indices = advanced::buffer(
                &self.device,
                "ProGPU original backdrop indices",
                bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
                wgpu::BufferUsages::INDEX,
            );
            metrics.draws += u32::from(vertices.is_some());
            metrics.host_draws += u32::from(host && vertices.is_some());
            metrics.uniform_bytes += 176 + 96 + size_of::<Uniforms>() as u64;
            metrics.geometry_bytes += 224 + 24;
            if draw.parameters.source_texture.is_some() {
                metrics.source_texture_bytes += u64::from(image.size()[0])
                    * u64::from(image.size()[1])
                    * u64::from(
                        image
                            .texture()
                            .format()
                            .block_copy_size(None)
                            .ok_or(Error::UnsupportedExecution)?,
                    );
            }
            draws.push(Draw {
                pipeline,
                frame: frame_binding,
                material,
                source,
                mask,
                vertices: vertex_buffer,
                indices,
                image,
                sampler,
                host,
                visible: vertices.is_some(),
            });
        }
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.backdrop_state = engine;
        prepared.backdrops = Some(PreparedBackdrops { draws, metrics });
        Ok(())
    }
}
