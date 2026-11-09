//! Original source AA coverage resolve, background initialization and replacement.
use super::*;
use crate::{
    composition::AxisClipPlan,
    image::{GpuImage, ImageAlpha},
    scene::LayerBlend,
};
use advanced::{AdvancedState, SamplingUniforms, begin, buffer, frame_binding, quad};

/// Actual owned resources and fixed work of one source AA target.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct AxisClipMetrics {
    /// Actual child color storage, excluding the caller's parent texture.
    pub source_texture_bytes: u64,
    /// Independent floating coverage storage; zero on ordinary sRGB clips.
    pub coverage_texture_bytes: u64,
    /// Separate parent-sized blend output; zero on ordinary sRGB clips.
    pub output_texture_bytes: u64,
    /// Immutable mask/frame/sampling payload including aligned storage.
    pub uniform_bytes: u64,
    /// Actual quad/index payload retained by the operation.
    pub geometry_bytes: u64,
    /// Per-pop draws, excluding caller-painted content.
    pub resolve_draws: u32,
    /// Actual copied parent bytes per initialization; zero without background.
    pub background_copy_bytes: u64,
}
/// Work encoded by a complete AA group, excluding caller-painted content.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct AxisClipReplayMetrics {
    /// Original child clear pass, before optional background copying.
    pub initialization_passes: u32,
    /// Original resolve/blend/replace draws, or one ordinary masked sRGB draw.
    pub resolve_draws: u32,
    /// Exact parent-to-child byte count copied at the actual painter position.
    pub background_copy_bytes: u64,
}
pub(super) struct AxisClipState {
    advanced: Option<AdvancedState>,
    coverage: Option<wgpu::RenderPipeline>,
    ordinary: Option<wgpu::RenderPipeline>,
}
struct Resources {
    parent: GpuImage,
    source: GpuImage,
    parent_view: wgpu::TextureView,
    source_view: wgpu::TextureView,
    resolve: wgpu::RenderPipeline,
    resolve_frame: wgpu::BindGroup,
    source_binding: wgpu::BindGroup,
    mask: wgpu::BindGroup,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    advanced: Option<AdvancedResources>,
}
struct AdvancedResources {
    state: AdvancedState,
    coverage: wgpu::TextureView,
    output: wgpu::TextureView,
    binding: wgpu::BindGroup,
    copy_frame: wgpu::BindGroup,
    copy_binding: wgpu::BindGroup,
}

/// Actual parent/child resources for explicit deferred AA execution. Preparation
/// never reads the parent or paints the source. Source recorders must separately
/// retain their original scopes, binary ancestors and nearest ordinary owner.
pub struct PreparedAxisClip {
    plan: AxisClipPlan,
    resources: Option<Resources>,
    metrics: AxisClipMetrics,
}
impl PreparedAxisClip {
    /// Original immutable source and physical target plan.
    pub fn plan(&self) -> &AxisClipPlan {
        &self.plan
    }
    /// Actual retained child image, useful as the parent of another AA group.
    /// Empty intersections allocate no texture.
    pub fn source(&self) -> Option<&GpuImage> {
        self.resources.as_ref().map(|r| &r.source)
    }
    /// Actual resources and per-replay work, not performance or pixel evidence.
    pub fn metrics(&self) -> AxisClipMetrics {
        self.metrics
    }
    /// Initialize the actual child at this exact painter position. Empty plans
    /// encode nothing. Keep ordinary source state outside this explicit target
    /// boundary; paint through `parts`, then consume the token with `finish`.
    pub fn begin<'a>(&'a self, encoder: &'a mut wgpu::CommandEncoder) -> Option<AxisClipPass<'a>> {
        let resources = self.resources.as_ref()?;
        drop(begin(
            encoder,
            &resources.source_view,
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
            "ProGPU original AA child initialization",
        ));
        if self.plan.initializes_background() {
            let parent = self.plan.frame().parent;
            let [x, y, w, h] = self.plan.extent();
            encoder.copy_texture_to_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: resources.parent.texture(),
                    mip_level: 0,
                    origin: wgpu::Origin3d {
                        x: x - parent[0],
                        y: y - parent[1],
                        z: 0,
                    },
                    aspect: wgpu::TextureAspect::All,
                },
                resources.source.texture().as_image_copy(),
                wgpu::Extent3d {
                    width: w,
                    height: h,
                    depth_or_array_layers: 1,
                },
            );
        }
        Some(AxisClipPass {
            prepared: self,
            resources,
            encoder,
        })
    }
}
/// Explicit in-progress AA target. Dropping an unfinished token does no GPU or
/// queue work: discard that unfinished command encoder instead of submitting it.
#[must_use = "finish the AA group before submitting its encoder"]
pub struct AxisClipPass<'a> {
    prepared: &'a PreparedAxisClip,
    resources: &'a Resources,
    encoder: &'a mut wgpu::CommandEncoder,
}
impl AxisClipPass<'_> {
    /// The current encoder and actual child texture. Load its initialized pixels
    /// when drawing; neither a new transparent frame nor a cached capture can
    /// replace the copied background. Nested AA groups use this same encoder.
    pub fn parts(&mut self) -> (&mut wgpu::CommandEncoder, &wgpu::Texture) {
        (self.encoder, self.resources.source.texture())
    }
    /// Apply coverage exactly once at group exit, preserving the actual parent
    /// bytes outside coverage. No submission, readback or queue wait is added.
    pub fn finish(self) -> AxisClipReplayMetrics {
        let r = self.resources;
        let (target, load) = r
            .advanced
            .as_ref()
            .map_or((&r.parent_view, wgpu::LoadOp::Load), |a| {
                (&a.coverage, wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT))
            });
        {
            let mut pass = begin(
                self.encoder,
                target,
                load,
                "ProGPU original AA coverage/composite",
            );
            pass.set_pipeline(&r.resolve);
            pass.set_bind_group(0, &r.resolve_frame, &[]);
            pass.set_bind_group(1, &r.source_binding, &[]);
            pass.set_bind_group(2, &r.mask, &[]);
            pass.set_vertex_buffer(0, r.vertices.slice(0..224));
            pass.set_index_buffer(r.indices.slice(..), wgpu::IndexFormat::Uint32);
            pass.draw_indexed(0..6, 0, 0..1);
        }
        if let Some(a) = &r.advanced {
            {
                let mut pass = begin(
                    self.encoder,
                    &a.output,
                    wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    "ProGPU original AA destination resolve",
                );
                pass.set_pipeline(&a.state.pipeline);
                pass.set_bind_group(0, &a.binding, &[]);
                pass.draw(0..3, 0..1);
            }
            {
                let mut pass = begin(
                    self.encoder,
                    &r.parent_view,
                    wgpu::LoadOp::Load,
                    "ProGPU original AA parent replacement",
                );
                pass.set_pipeline(&a.state.copy);
                pass.set_bind_group(0, &a.copy_frame, &[]);
                pass.set_bind_group(1, &a.copy_binding, &[]);
                pass.set_vertex_buffer(0, r.vertices.slice(224..448));
                pass.set_index_buffer(r.indices.slice(..), wgpu::IndexFormat::Uint32);
                pass.draw_indexed(0..6, 0, 0..1);
            }
        }
        AxisClipReplayMetrics {
            initialization_passes: 1,
            resolve_draws: self.prepared.metrics.resolve_draws,
            background_copy_bytes: self.prepared.metrics.background_copy_bytes,
        }
    }
}

impl Renderer {
    /// Prepare original source AA execution against an actual sampled parent.
    /// Parent is an alpha-preserving, same-device, single-level render texture.
    /// Source ownership and background initialization are explicit; no ordinary
    /// opacity/mask layer is guessed to be an axis clip. Ordinary sRGB source-over
    /// retains the fixed path; initialized sRGB and linear-byte targets use the
    /// original independent coverage and destination-aware shader branches.
    pub async fn prepare_axis_clip(
        &mut self,
        parent: &GpuImage,
        plan: &AxisClipPlan,
    ) -> Result<PreparedAxisClip, Error> {
        let frame = plan.frame();
        let texture = parent.texture();
        if parent.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        if parent.alpha() != ImageAlpha::Premultiplied
            || parent.size() != [frame.parent[2], frame.parent[3]]
            || texture.format() != self.format
            || texture.mip_level_count() != 1
            || !texture
                .usage()
                .contains(wgpu::TextureUsages::RENDER_ATTACHMENT)
            || (plan.initializes_background()
                && !texture.usage().contains(wgpu::TextureUsages::COPY_SRC))
        {
            return Err(Error::InvalidInput);
        }
        if plan.is_empty() {
            return Ok(PreparedAxisClip {
                plan: plan.clone(),
                resources: None,
                metrics: Default::default(),
            });
        }
        let linear = matches!(
            self.format,
            wgpu::TextureFormat::Rgba8Unorm | wgpu::TextureFormat::Bgra8Unorm
        );
        if !linear
            && !matches!(
                self.format,
                wgpu::TextureFormat::Rgba8UnormSrgb | wgpu::TextureFormat::Bgra8UnormSrgb
            )
        {
            return Err(Error::UnsupportedExecution);
        }
        let advanced = linear || plan.initializes_background();
        let [_, _, w, h] = plan.extent();
        let bytes = u64::from(w) * u64::from(h) * 4;
        let parent_bytes = u64::from(frame.parent[2]) * u64::from(frame.parent[3]) * 4;
        let total = if advanced {
            bytes
                .checked_mul(2)
                .and_then(|b| b.checked_add(parent_bytes))
                .ok_or(Error::Capacity)?
        } else {
            bytes
        };
        let limits = self.device.limits();
        if bytes > 256 * 1024 * 1024
            || (advanced && parent_bytes > 256 * 1024 * 1024)
            || total > 512 * 1024 * 1024
            || (advanced && limits.max_sampled_textures_per_shader_stage < 3)
            || [w, h, frame.parent[2], frame.parent[3]]
                .into_iter()
                .any(|v| v > limits.max_texture_dimension_2d)
        {
            return Err(Error::Capacity);
        }
        let mut state = self.axis_clip.take().unwrap_or(AxisClipState {
            advanced: None,
            coverage: None,
            ordinary: None,
        });
        if advanced {
            if state.advanced.is_none() {
                state.advanced = Some(match &self.advanced {
                    Some(existing) => existing.clone(),
                    None => AdvancedState::new(&self.device, self.format).await?,
                });
            }
            if state.coverage.is_none() {
                state.coverage = Some(
                    image::pipeline_entry(
                        &self.device,
                        wgpu::TextureFormat::R32Float,
                        false,
                        LayerBlend::Src,
                        true,
                        Some("fs_layer_coverage"),
                    )
                    .await?,
                );
            }
        } else if state.ordinary.is_none() {
            state.ordinary = Some(
                image::pipeline_entry(
                    &self.device,
                    self.format,
                    false,
                    LayerBlend::SrcOver,
                    true,
                    Some("fs_main"),
                )
                .await?,
            );
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = self.populate_axis_clip(parent, plan, &state, advanced, linear);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let prepared = result?;
        if self.advanced.is_none() {
            self.advanced = state.advanced.clone();
        }
        self.axis_clip = Some(state);
        Ok(prepared)
    }

    fn populate_axis_clip(
        &self,
        parent: &GpuImage,
        plan: &AxisClipPlan,
        state: &AxisClipState,
        advanced: bool,
        linear: bool,
    ) -> Result<PreparedAxisClip, Error> {
        let device = &self.device;
        let frame = plan.frame();
        let [x, y, w, h] = plan.extent();
        let parent_size = [frame.parent[2], frame.parent[3]];
        let create = |size: [u32; 2], format, label| {
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
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                    | wgpu::TextureUsages::TEXTURE_BINDING
                    | wgpu::TextureUsages::COPY_SRC
                    | wgpu::TextureUsages::COPY_DST,
                view_formats: &[],
            })
        };
        let source = GpuImage::from_texture(
            device,
            &create([w, h], self.format, "ProGPU deferred source AA target"),
            ImageAlpha::Premultiplied,
        )?;
        let source_view = source.texture().create_view(&Default::default());
        let parent_view = parent.texture().create_view(&Default::default());
        let resolve = (if advanced {
            &state.coverage
        } else {
            &state.ordinary
        })
        .as_ref()
        .ok_or(Error::ResourceDomain)?
        .clone();
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ProGPU original AA linear sampler"),
            min_filter: wgpu::FilterMode::Linear,
            mag_filter: wgpu::FilterMode::Linear,
            lod_max_clamp: 0.0,
            ..Default::default()
        });
        let image_binding = |pipeline: &wgpu::RenderPipeline, view: &wgpu::TextureView| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable AA source/copy binding"),
                layout: &pipeline.get_bind_group_layout(1),
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
        let source_binding = image_binding(&resolve, &source_view);
        let mut mask_uniforms = plan.coverage_uniforms();
        if !advanced {
            // Ordinary sRGB mask resolves in the parent frame. Subtract each
            // original projected edge in double before narrowing, as native.
            mask_uniforms.bounds = std::array::from_fn(|i| {
                (f64::from(plan.projected_edges()[i]) - f64::from(frame.parent[i % 2])) as f32
            });
        }
        let mask_buffer = buffer(
            device,
            "ProGPU original AA area mask",
            bytemuck::bytes_of(&mask_uniforms),
            wgpu::BufferUsages::UNIFORM,
        );
        let chain_buffer = buffer(
            device,
            "ProGPU unused AA mask continuation",
            &[0; 288],
            wgpu::BufferUsages::UNIFORM,
        );
        let mask = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU exact source AA area binding"),
            layout: &resolve.get_bind_group_layout(2),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&source_view),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: mask_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: chain_buffer.as_entire_binding(),
                },
            ],
        });
        let make_frame = |size: [u32; 2]| {
            Frame {
                width: size[0],
                height: size[1],
                dpi_scale: frame.semantic_dpi,
            }
            .uniforms(device.limits().max_texture_dimension_2d)
        };
        let resolve_frame = frame_binding(
            device,
            &resolve,
            make_frame(if advanced { [w, h] } else { parent_size })?,
        );
        let mut vertices = Vec::from(quad([w, h], frame.semantic_dpi, 1.0));
        if !advanced {
            let origin = [
                (x as f32 - frame.parent[0] as f32) / frame.semantic_dpi,
                (y as f32 - frame.parent[1] as f32) / frame.semantic_dpi,
            ];
            for v in &mut vertices {
                v.position[0] += origin[0];
                v.position[1] += origin[1];
            }
        }
        let advanced_resources = if advanced {
            let state = state
                .advanced
                .as_ref()
                .ok_or(Error::ResourceDomain)?
                .clone();
            let coverage = create(
                [w, h],
                wgpu::TextureFormat::R32Float,
                "ProGPU independent floating AA coverage",
            )
            .create_view(&Default::default());
            let output = create(
                parent_size,
                self.format,
                "ProGPU original AA destination output",
            )
            .create_view(&Default::default());
            let mut sampling = SamplingUniforms {
                source_origin: [
                    x as f32 - frame.parent[0] as f32,
                    y as f32 - frame.parent[1] as f32,
                ],
                source_extent: [w as f32, h as f32],
                operation_kind: if plan.initializes_background() {
                    if linear { 3 } else { 2 }
                } else {
                    4
                },
                ..SamplingUniforms::zeroed()
            };
            if let Some([parent, child]) = plan.coverage_ratio_bounds() {
                sampling.pattern_kind = 1;
                sampling.pattern_color = parent;
                sampling.pattern_background_color = child;
            }
            let mut data = [0u8; 256];
            data[..96].copy_from_slice(bytemuck::bytes_of(&sampling));
            let uniforms = buffer(
                device,
                "ProGPU original aligned AA sampling record",
                &data,
                wgpu::BufferUsages::UNIFORM,
            );
            let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable destination-aware AA binding"),
                layout: &state.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&parent_view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&source_view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(&coverage),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                            buffer: &uniforms,
                            offset: 0,
                            size: std::num::NonZeroU64::new(96),
                        }),
                    },
                ],
            });
            let copy_binding = image_binding(&state.copy, &output);
            let copy_frame = frame_binding(device, &state.copy, make_frame(parent_size)?);
            vertices.extend_from_slice(&quad(parent_size, frame.semantic_dpi, 1.0));
            Some(AdvancedResources {
                state,
                coverage,
                output,
                binding,
                copy_frame,
                copy_binding,
            })
        } else {
            None
        };
        let geometry_bytes = (std::mem::size_of_val(vertices.as_slice()) + 24) as u64;
        let vertices = buffer(
            device,
            "ProGPU original AA resolve/copy quads",
            bytemuck::cast_slice(&vertices),
            wgpu::BufferUsages::VERTEX,
        );
        let indices = buffer(
            device,
            "ProGPU original AA quad indices",
            bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
            wgpu::BufferUsages::INDEX,
        );
        let source_bytes = u64::from(w) * u64::from(h) * 4;
        let metrics = AxisClipMetrics {
            source_texture_bytes: source_bytes,
            coverage_texture_bytes: if advanced { source_bytes } else { 0 },
            output_texture_bytes: if advanced {
                u64::from(parent_size[0]) * u64::from(parent_size[1]) * 4
            } else {
                0
            },
            uniform_bytes: 96 + 288 + 224 + if advanced { 256 + 224 } else { 0 },
            geometry_bytes,
            resolve_draws: if advanced { 3 } else { 1 },
            background_copy_bytes: if plan.initializes_background() {
                source_bytes
            } else {
                0
            },
        };
        Ok(PreparedAxisClip {
            plan: plan.clone(),
            resources: Some(Resources {
                parent: parent.clone(),
                source,
                parent_view,
                source_view,
                resolve,
                resolve_frame,
                source_binding,
                mask,
                vertices,
                indices,
                advanced: advanced_resources,
            }),
            metrics,
        })
    }
}
