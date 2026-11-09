//! Deferred source AA children and actual shared target ownership.
use super::*;
use crate::{
    composition::{AxisClipFrame, AxisClipPlan},
    image::{GpuImage, ImageAlpha},
    scene::AxisClipDraw,
};

/// Actual retained AA resources, separate from eager ordinary-layer captures.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RetainedAaMetrics {
    /// Nonempty retained AA groups across this deferred subtree.
    pub groups: u32,
    /// Actual child/coverage/output and owned root texture bytes.
    pub texture_bytes: u64,
    /// Actual immutable AA/root-copy uniform bytes.
    pub uniform_bytes: u64,
    /// Actual AA/root-copy quad/index payload bytes.
    pub geometry_bytes: u64,
    /// Per-replay AA resolve draws, excluding content and root presentation.
    pub resolve_draws: u32,
    /// Per-replay exact initialized-background texture copies.
    pub background_copy_bytes: u64,
    /// Existing geometry-upload counters across retained child scenes.
    pub source_geometry_bytes: u64,
    /// Existing frame/mask uniform counters across retained child scenes.
    pub source_uniform_bytes: u64,
    /// Original retained child glyph upload bytes.
    pub source_glyph_upload_bytes: u64,
    /// Retained child image and coverage upload bytes.
    pub source_image_mask_upload_bytes: u64,
    /// Destination-aware operations in deferred child scenes, excluding captures.
    pub source_advanced_operations: u64,
    /// Actual destination-aware passes in deferred child scenes.
    pub source_advanced_passes: u64,
    /// Child advanced scratch storage; shared AA targets are counted only above.
    pub source_advanced_texture_bytes: u64,
    /// Child advanced sampling/frame uniform payloads.
    pub source_advanced_uniform_bytes: u64,
    /// Child advanced resolve/copy geometry payloads.
    pub source_advanced_geometry_bytes: u64,
    /// Fused image effects and live-blur resources in deferred children.
    pub source_image_effects: ImageEffectMetrics,
    /// Materialized mask preparation in deferred source subtrees.
    pub source_masks: crate::mask::MaskCompositionMetrics,
    /// Actual final-device programmable shader resources/draws in child targets.
    pub source_shaders: crate::shader_effect::ShaderEffectMetrics,
    /// Actual source color-filter graphs and their per-replay passes.
    pub source_color_filters: ColorFilterMetrics,
    /// Managed WGSL parameters and final-device shader draws in child targets.
    pub source_managed_shaders: crate::shader_effect::ManagedShaderMetrics,
}
#[derive(Default)]
pub(super) struct PreparedGroups {
    pub root: Option<GroupTarget>,
    pub children: Vec<PreparedGroup>,
    pub metrics: RetainedAaMetrics,
}
pub(super) struct PreparedGroup {
    pub clip: PreparedAxisClip,
    pub scene: PreparedScene,
}
pub(super) struct GroupTarget {
    pub image: GpuImage,
    pub view: wgpu::TextureView,
    pub fixed: bool,
    copy: Option<RootCopy>,
}
struct RootCopy {
    pipeline: wgpu::RenderPipeline,
    frame: wgpu::BindGroup,
    image: wgpu::BindGroup,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
}
impl GroupTarget {
    pub fn new(image: GpuImage, fixed: bool) -> Self {
        let view = image.texture().create_view(&Default::default());
        Self {
            image,
            view,
            fixed,
            copy: None,
        }
    }
    pub fn copy(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
    ) -> Result<(), Error> {
        let copy = self.copy.as_ref().ok_or(Error::ResourceDomain)?;
        let mut pass = advanced::begin(
            encoder,
            target,
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
            "ProGPU retained AA root presentation",
        );
        pass.set_pipeline(&copy.pipeline);
        pass.set_bind_group(0, &copy.frame, &[]);
        pass.set_bind_group(1, &copy.image, &[]);
        pass.set_vertex_buffer(0, copy.vertices.slice(..));
        pass.set_index_buffer(copy.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..6, 0, 0..1);
        Ok(())
    }
}
impl PreparedScene {
    /// Actual retained AA resources and per-replay resolves across this tree.
    /// Ordinary captured layers retain their separate preparation metrics.
    pub fn retained_aa_metrics(&self) -> RetainedAaMetrics {
        self.groups.metrics
    }
    /// Original resolved direct AA plans, including actual background policy.
    pub fn axis_clip_plans(&self) -> impl Iterator<Item = &AxisClipPlan> {
        self.groups.children.iter().map(|c| c.clip.plan())
    }
    pub(super) fn fixed_target(&self) -> bool {
        self.groups.root.as_ref().is_some_and(|r| r.fixed)
    }
}
impl Renderer {
    pub(super) fn resolve_axis(
        draw: &AxisClipDraw,
        frame: RenderFrame,
        ancestor: Option<&AxisClipPlan>,
        clip: Option<[u32; 4]>,
    ) -> Result<AxisClipPlan, Error> {
        let plan = frame.axis(draw, ancestor)?;
        let source = draw.scene().with_binary_clip(clip);
        let background = Self::active_storage_clear(&source, frame.at(plan.extent()), Some(&plan))?;
        Ok(plan.with_background(background))
    }
    fn active_storage_clear(
        scene: &Scene,
        frame: RenderFrame,
        ancestor: Option<&AxisClipPlan>,
    ) -> Result<bool, Error> {
        if !scene.has_storage_clear() || frame.empty() {
            return Ok(false);
        }
        let (states, changes) = scene.compile_states(frame.dpi_scale)?;
        let mut current = 0;
        let mut cursor = 0;
        for (index, axis) in scene.storage_visits() {
            for next in changes.get(cursor..index).unwrap_or(&[]).iter().flatten() {
                current = *next;
            }
            cursor = index + 1;
            let clip = states[current].clip;
            if !frame.clip_intersects(clip) {
                continue;
            }
            if let Some(axis) = axis {
                let plan = frame.axis(axis, ancestor)?;
                if Self::active_storage_clear(
                    &axis.scene().with_binary_clip(clip),
                    frame.at(plan.extent()),
                    Some(&plan),
                )? {
                    return Ok(true);
                }
            } else {
                return Ok(true);
            }
        }
        Ok(false)
    }
    pub(super) fn create_group_target(&self, frame: RenderFrame) -> Result<GpuImage, Error> {
        frame.uniforms(self.device.limits().max_texture_dimension_2d)?;
        if u64::from(frame.width) * u64::from(frame.height) * 4 > 256 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU retained AA actual root"),
            size: wgpu::Extent3d {
                width: frame.width,
                height: frame.height,
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
        GpuImage::from_texture(&self.device, &texture, ImageAlpha::Premultiplied)
    }
    pub(super) async fn finish_groups(
        &mut self,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        let Some(root) = &mut prepared.groups.root else {
            return Ok(());
        };
        if !root.fixed && prepared.advanced.is_none() {
            let pipeline = if let Some(state) = &self.advanced {
                state.copy.clone()
            } else if let Some(copy) = &self.group_copy {
                copy.clone()
            } else {
                let copy = image::pipeline(
                    &self.device,
                    self.format,
                    false,
                    crate::scene::LayerBlend::Src,
                    false,
                )
                .await?;
                self.group_copy = Some(copy.clone());
                copy
            };
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let sampler = self.device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU retained AA root sampler"),
                min_filter: wgpu::FilterMode::Linear,
                mag_filter: wgpu::FilterMode::Linear,
                lod_max_clamp: 0.0,
                ..Default::default()
            });
            let image = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU retained AA root binding"),
                layout: &pipeline.get_bind_group_layout(1),
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&root.view),
                    },
                ],
            });
            let frame = advanced::frame_binding(
                &self.device,
                &pipeline,
                prepared
                    .frame
                    .uniforms(self.device.limits().max_texture_dimension_2d)?,
            );
            let vertices = advanced::buffer(
                &self.device,
                "ProGPU AA root-copy quad",
                bytemuck::cast_slice(&advanced::quad(
                    [prepared.frame.width, prepared.frame.height],
                    prepared.frame.dpi_scale,
                    1.0,
                )),
                wgpu::BufferUsages::VERTEX,
            );
            let indices = advanced::buffer(
                &self.device,
                "ProGPU AA root-copy indices",
                bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
                wgpu::BufferUsages::INDEX,
            );
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            root.copy = Some(RootCopy {
                pipeline,
                frame,
                image,
                vertices,
                indices,
            });
            prepared.groups.metrics.uniform_bytes += 224;
            prepared.groups.metrics.geometry_bytes += 248;
        }
        Ok(())
    }
}

impl RenderFrame {
    pub fn clip_intersects(self, clip: Option<[u32; 4]>) -> bool {
        !self.empty()
            && clip.is_none_or(|[l, t, r, b]| {
                r.min(self.origin[0] + self.width) > l.max(self.origin[0])
                    && b.min(self.origin[1] + self.height) > t.max(self.origin[1])
            })
    }
    pub fn at(self, [x, y, w, h]: [u32; 4]) -> Self {
        Self {
            frame: Frame {
                width: w,
                height: h,
                dpi_scale: self.dpi_scale,
            },
            origin: [x, y],
            ..self
        }
    }
    pub fn axis(
        self,
        draw: &AxisClipDraw,
        parent: Option<&AxisClipPlan>,
    ) -> Result<AxisClipPlan, Error> {
        match parent {
            Some(p) => p.child(draw.bounds(), draw.initializes_background()),
            None => AxisClipPlan::new(
                draw.bounds(),
                AxisClipFrame {
                    root_size: [self.root.width, self.root.height],
                    parent: [self.origin[0], self.origin[1], self.width, self.height],
                    dpi: [self.dpi_scale; 2],
                    semantic_dpi: self.dpi_scale,
                    viewport: [0, 0, self.root.width, self.root.height],
                },
                draw.initializes_background(),
            ),
        }
    }
}
impl RetainedAaMetrics {
    pub(super) fn merge(&mut self, other: Self) -> Result<(), Error> {
        self.source_masks.merge(other.source_masks)?;
        shader_effect::merge_metrics(&mut self.source_shaders, other.source_shaders)?;
        self.source_color_filters
            .merge(other.source_color_filters)?;
        self.source_managed_shaders
            .merge(other.source_managed_shaders)?;
        self.source_image_effects
            .merge(other.source_image_effects)?;
        macro_rules! add {
            ($field:ident) => {
                self.$field = self
                    .$field
                    .checked_add(other.$field)
                    .ok_or(Error::Capacity)?;
            };
        }
        add!(groups);
        add!(texture_bytes);
        add!(uniform_bytes);
        add!(geometry_bytes);
        add!(resolve_draws);
        add!(background_copy_bytes);
        add!(source_geometry_bytes);
        add!(source_uniform_bytes);
        add!(source_glyph_upload_bytes);
        add!(source_image_mask_upload_bytes);
        add!(source_advanced_operations);
        add!(source_advanced_passes);
        add!(source_advanced_texture_bytes);
        add!(source_advanced_uniform_bytes);
        add!(source_advanced_geometry_bytes);
        Ok(())
    }

    pub(super) fn add(
        &mut self,
        clip: &PreparedAxisClip,
        source: &PreparedScene,
    ) -> Result<(), Error> {
        let nested = source.groups.metrics;
        self.source_masks.merge(nested.source_masks)?;
        shader_effect::merge_metrics(&mut self.source_shaders, nested.source_shaders)?;
        self.source_color_filters
            .merge(nested.source_color_filters)?;
        self.source_managed_shaders
            .merge(nested.source_managed_shaders)?;
        self.source_masks.merge(source.mask_composition_metrics())?;
        shader_effect::merge_metrics(&mut self.source_shaders, source.shader_effect_metrics())?;
        self.source_color_filters
            .merge(source.color_filter_metrics())?;
        self.source_managed_shaders
            .merge(source.managed_shader_metrics())?;
        self.source_image_effects
            .merge(nested.source_image_effects)?;
        self.source_image_effects
            .merge(source.image_effect_metrics())?;
        let m = clip.metrics();
        let uploads = source.metrics;
        macro_rules! add {
            ($field:ident,$value:expr) => {
                self.$field = self
                    .$field
                    .checked_add(nested.$field)
                    .and_then(|v| v.checked_add($value))
                    .ok_or(Error::Capacity)?;
            };
        }
        let advanced = source.advanced_blend_metrics();
        add!(source_advanced_operations, u64::from(advanced.operations));
        add!(source_advanced_passes, u64::from(advanced.render_passes));
        add!(source_advanced_texture_bytes, advanced.texture_bytes);
        add!(source_advanced_uniform_bytes, advanced.uniform_bytes);
        add!(source_advanced_geometry_bytes, advanced.geometry_bytes);
        add!(groups, 1);
        add!(
            texture_bytes,
            m.source_texture_bytes + m.coverage_texture_bytes + m.output_texture_bytes
        );
        add!(uniform_bytes, m.uniform_bytes);
        add!(geometry_bytes, m.geometry_bytes);
        add!(resolve_draws, m.resolve_draws);
        add!(background_copy_bytes, m.background_copy_bytes);
        add!(source_geometry_bytes, uploads.geometry_bytes);
        add!(source_uniform_bytes, uploads.uniform_bytes);
        add!(source_glyph_upload_bytes, uploads.glyph_upload_bytes);
        add!(
            source_image_mask_upload_bytes,
            uploads.image_upload_bytes + uploads.mask_upload_bytes
        );
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scene::LayerDraw;

    fn frame() -> RenderFrame {
        Frame {
            width: 128,
            height: 96,
            dpi_scale: 1.0,
        }
        .into()
    }
    fn clear_source() -> Scene {
        let mut source = Scene::default();
        source.clear_target([0.7, 0.2, 0.9, 0.0]).unwrap();
        source
    }
    fn plan(source: Scene) -> AxisClipPlan {
        let mut scene = Scene::default();
        scene
            .draw_axis_clip([20.25, 10.5, 50.5, 50.25], source)
            .unwrap();
        Renderer::resolve_axis(scene.axis_clips().next().unwrap(), frame(), None, None).unwrap()
    }
    #[test]
    fn clear_propagates_through_aa_but_stops_at_ordinary_owner() {
        let mut source = Scene::default();
        source
            .draw_axis_clip([24.25, 14.5, 40.0, 40.0], clear_source())
            .unwrap();
        assert!(plan(source.clone()).initializes_background());
        let mut ordinary = Scene::default();
        ordinary.draw_layer(LayerDraw::new(source)).unwrap();
        assert!(!plan(ordinary).initializes_background());
    }
    #[test]
    fn disjoint_closed_children_do_not_initialize_background() {
        let mut source = Scene::default();
        source.push_clip_rect([60.0, 40.0, 10.0, 10.0]).unwrap();
        source
            .draw_axis_clip([24.0, 14.0, 8.0, 8.0], clear_source())
            .unwrap();
        source.pop_scope().unwrap();
        assert!(!plan(source.clone()).initializes_background());
        source
            .draw_axis_clip([24.0, 14.0, 8.0, 8.0], clear_source())
            .unwrap();
        assert!(plan(source).initializes_background());
    }
    #[test]
    fn inherited_binary_clip_uses_actual_offset_target() {
        let mut source = Scene::default();
        source
            .draw_axis_clip([20.25, 10.5, 50.5, 50.25], clear_source())
            .unwrap();
        let draw = source.axis_clips().next().unwrap();
        let target = frame().at([16, 8, 80, 72]);
        let hidden = Renderer::resolve_axis(draw, target, None, Some([72, 10, 90, 60])).unwrap();
        assert!(!hidden.initializes_background());
        let visible = Renderer::resolve_axis(draw, target, None, Some([24, 16, 48, 40])).unwrap();
        assert!(visible.initializes_background());
        assert_eq!(visible.extent(), [20, 10, 51, 51]);
    }
}
