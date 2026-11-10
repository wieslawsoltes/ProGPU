//! Original bounded target mapping and bottom-up isolated scene compilation.
use super::*;
use crate::{
    image::{ImageDraw, ImageSampling},
    scene::LayerDraw,
};

/// Actual work preparing isolated subtrees. These are preparation costs, not
/// uploads or submissions repeated by replaying the final prepared scene.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LayerMetrics {
    /// Nonempty isolated captures, including nested captures.
    pub captures: u32,
    /// Source target allocations, including targets consumed by effects.
    pub capture_bytes: u64,
    /// Actual effect intermediate allocations, including final effect outputs.
    pub effect_texture_bytes: u64,
    /// Padded original effect uniform storage.
    pub effect_uniform_bytes: u64,
    /// Original ordered effect compute passes.
    pub effect_passes: u64,
    /// Actual subtree vertex/index/glyph payload uploaded during preparation.
    pub geometry_bytes: u64,
    /// Subtree frame and mask uniform payload, excluding effect uniforms.
    pub uniform_bytes: u64,
    /// Subtree original glyph outline/coverage/bitmap uploads.
    pub glyph_upload_bytes: u64,
    /// Subtree original image and coverage pixel uploads.
    pub image_mask_upload_bytes: u64,
    /// Ordered source draws across captured subtrees.
    pub source_draws: u64,
    /// Destination-aware operations inside captured subtrees.
    pub advanced_operations: u64,
    /// Actual three-pass operations and final subtree root copies.
    pub advanced_passes: u64,
    /// Actual destination-aware subtree scratch allocations.
    pub advanced_texture_bytes: u64,
    /// Original padded sampling slots and resolve/copy frames.
    pub advanced_uniform_bytes: u64,
    /// Original advanced resolve/copy geometry payloads.
    pub advanced_geometry_bytes: u64,
    /// Actual target-storage Clear operations while capturing subtrees.
    pub source_target_clears: u64,
    /// Original per-occurrence source Clear uploads during capture.
    pub source_replay_uniform_bytes: u64,
    /// Actual source Clear uniform allocations during capture.
    pub source_replay_uniform_allocations: u64,
    /// Exact background copies executed while capturing source subtrees.
    pub source_replay_texture_copy_bytes: u64,
    /// AA resources/work prepared inside ordinary captures, separate from live replay.
    /// Its source upload/advanced fields are also included in the totals above.
    pub source_aa: RetainedAaMetrics,
    /// Fused image effects and live-blur preparation in captured subtrees.
    pub source_image_effects: ImageEffectMetrics,
    /// Materialized masks prepared inside captured subtrees.
    pub source_masks: crate::mask::MaskCompositionMetrics,
    /// Actual final-device programmable shader resources/draws in child targets.
    pub source_shaders: crate::shader_effect::ShaderEffectMetrics,
    /// Actual source color-filter graphs and their per-replay passes.
    pub source_color_filters: ColorFilterMetrics,
    /// Actual full image-filter DAG work in these captured source subtrees.
    pub source_image_filters: ImageFilterMetrics,
    /// Actual nested owned shader capture/cache preparation work.
    pub source_owned_shaders: OwnedShaderMetrics,
    /// Actual cached-picture generations prepared in captured child targets.
    pub source_cached_pictures: CachedPictureMetrics,
    /// Managed WGSL parameters and final-device shader draws in child targets.
    pub source_managed_shaders: crate::shader_effect::ManagedShaderMetrics,
}

#[derive(Clone, Copy)]
pub(super) struct RenderFrame {
    pub frame: Frame,
    pub(super) root: Frame,
    pub origin: [u32; 2],
    pub source: Option<crate::composition::SourceRenderFrame>,
    pub mask_depth: u16,
    pub camera: Option<crate::vector::Matrix4>,
}
impl std::ops::Deref for RenderFrame {
    type Target = Frame;
    fn deref(&self) -> &Frame {
        &self.frame
    }
}
impl From<Frame> for RenderFrame {
    fn from(frame: Frame) -> Self {
        Self {
            frame,
            root: frame,
            origin: [0; 2],
            source: None,
            mask_depth: 0,
            camera: None,
        }
    }
}
impl RenderFrame {
    pub fn uniforms(self, maximum: u32) -> Result<Uniforms, Error> {
        self.frame.uniforms(maximum)?;
        let mut uniforms = self.root.uniforms(maximum)?;
        if let Some(source) = self.source {
            uniforms.projection = source.projection();
            uniforms.mvp = source.projection();
            uniforms.canvas_size = source.logical_size().map(|v| v as f32);
            uniforms.dpi_scale = source.dpi_scale();
        }
        if let Some(camera) = self.camera {
            if camera.0.iter().flatten().any(|v| !v.is_finite()) {
                return Err(Error::InvalidInput);
            }
            uniforms.view = bytemuck::cast(camera.0);
            uniforms.mvp = bytemuck::cast(crate::vector::Matrix4::IDENTITY.0);
        }
        if self.origin == [0; 2] && self.frame == self.root {
            return Ok(uniforms);
        }
        // Original managed CreateMaskRenderUniforms: retain world geometry and
        // brush coordinates, mapping only the actual physical attachment.
        let sx = self.root.width as f32 / self.width as f32;
        let sy = self.root.height as f32 / self.height as f32;
        uniforms.projection[0] *= sx;
        uniforms.projection[5] *= sy;
        uniforms.projection[12] = (uniforms.projection[12] + 1.0) * sx
            - 1.0
            - 2.0 * self.origin[0] as f32 / self.width as f32;
        uniforms.projection[13] = uniforms.projection[13] * sy + 1.0 - sy
            + 2.0 * self.origin[1] as f32 / self.height as f32;
        uniforms.canvas_size = [self.width as f32, self.height as f32];
        uniforms.render_origin = self.origin.map(|v| v as f32);
        uniforms.pad1 = [-(self.width as f32), -(self.height as f32)];
        if !bytemuck::cast_slice::<_, f32>(std::slice::from_ref(&uniforms))
            .iter()
            .all(|v| v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        Ok(uniforms)
    }
    pub(super) fn empty(self) -> bool {
        self.width == 0 || self.height == 0
    }
    pub(super) fn child(self, layer: &LayerDraw) -> Result<Self, Error> {
        layer.validate()?;
        let mask_depth = self.mask_depth.checked_add(1).ok_or(Error::Capacity)?;
        let Some(logical_edges) = layer.bounds_edges() else {
            return Ok(Self { mask_depth, ..self });
        };
        // Original native four independent presentation lanes, including +0.
        let edges: [f32; 4] = (wide::f32x4::from(logical_edges)
            * wide::f32x4::splat(self.dpi_scale)
            + wide::f32x4::splat(0.0))
        .into();
        if !edges.iter().all(|v| v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let extents = [
            self.root.width,
            self.root.height,
            self.root.width,
            self.root.height,
        ];
        let edges = std::array::from_fn::<_, 4, _>(|i| {
            let value = edges[i].clamp(0.0, extents[i] as f32);
            let rounded = value.round();
            if (value - rounded).abs() < 0.0001 {
                rounded
            } else {
                value
            }
        });
        let [left, top] = [edges[0].floor() as u32, edges[1].floor() as u32];
        let [right, bottom] = [edges[2].ceil() as u32, edges[3].ceil() as u32];
        let left = left.max(self.origin[0]).min(self.origin[0] + self.width);
        let top = top.max(self.origin[1]).min(self.origin[1] + self.height);
        let right = right.min(self.origin[0] + self.width);
        let bottom = bottom.min(self.origin[1] + self.height);
        let empty = edges[2] <= edges[0] || edges[3] <= edges[1];
        Ok(Self {
            root: self.root,
            source: self.source,
            camera: self.camera,
            mask_depth,
            origin: [left, top],
            frame: Frame {
                width: if empty { 0 } else { right.saturating_sub(left) },
                height: if empty { 0 } else { bottom.saturating_sub(top) },
                dpi_scale: self.dpi_scale,
            },
        })
    }
}

impl Renderer {
    pub(super) fn preflight_layers(&self, scene: &Scene, frame: RenderFrame) -> Result<(), Error> {
        let mut count = 0;
        let mut bytes = 0;
        let mut passes = 0;
        self.preflight_layer_tree(scene, frame, None, &mut count, &mut bytes, &mut passes)
    }
    fn preflight_layer_tree(
        &self,
        scene: &Scene,
        frame: RenderFrame,
        ancestor: Option<&crate::composition::AxisClipPlan>,
        count: &mut u32,
        bytes: &mut u64,
        passes: &mut u32,
    ) -> Result<(), Error> {
        self.preflight_backdrops(scene, frame)?;
        use crate::scene::GroupRef;
        self.preflight_owned_shaders(scene, frame)?;
        self.preflight_shaders(scene, frame)?;
        self.preflight_managed_shaders(scene, frame)?;
        let (states, changes) = scene.compile_states(frame.dpi_scale)?;
        self.validate_mask_states(&states)?;
        let (mesh_bytes, mesh_passes) = self.preflight_mesh3d(scene)?;
        let (effect_bytes, effect_passes) = self.preflight_image_effects(scene)?;
        let (filter_bytes, filter_passes) = self.preflight_color_filters(scene)?;
        let (image_filter_bytes, image_filter_passes) = self.preflight_image_filters(scene)?;
        let (cached_bytes, cached_passes) = self.preflight_cached_pictures(scene, frame)?;
        let (mask_bytes, mask_passes) = self.mask_resource_cost(scene, &states, &changes, frame)?;
        *bytes = bytes
            .checked_add(
                mesh_bytes
                    + effect_bytes
                    + mask_bytes
                    + filter_bytes
                    + image_filter_bytes
                    + cached_bytes,
            )
            .ok_or(Error::Capacity)?;
        *passes = passes
            .checked_add(
                mesh_passes
                    + effect_passes
                    + mask_passes
                    + filter_passes
                    + image_filter_passes
                    + cached_passes,
            )
            .ok_or(Error::Capacity)?;
        if *bytes > 512 * 1024 * 1024 || *passes > 16 * 1024 {
            return Err(Error::Capacity);
        }
        if scene.layer_depth() > 16 {
            return Err(Error::Capacity);
        }
        let mut current = 0;
        let mut cursor = 0;
        let mut advanced_source = [0u32; 2];
        let mut has_axis = false;
        for (index, group) in scene.groups() {
            for next in changes.get(cursor..index).unwrap_or(&[]).iter().flatten() {
                current = *next;
            }
            cursor = index + 1;
            *count = count.checked_add(1).ok_or(Error::Capacity)?;
            if *count > 16 * 1024 {
                return Err(Error::Capacity);
            }
            if self.image_sampling.is_none() {
                return Err(Error::UnsupportedExecution);
            }
            let clip = states[current].clip;
            match group {
                GroupRef::Layer(layer) => {
                    let target = frame.child(layer)?;
                    let effect = layer
                        .effects
                        .as_ref()
                        .map(|c| c.plan([frame.dpi_scale; 2]))
                        .transpose()?;
                    if let Some(effect) = &effect {
                        *passes = passes
                            .checked_add(effect.pass_count())
                            .ok_or(Error::Capacity)?;
                    }
                    if !target.empty() {
                        if !layer.blend.is_fixed() {
                            advanced_source[0] = advanced_source[0].max(target.width);
                            advanced_source[1] = advanced_source[1].max(target.height);
                        }
                        target.uniforms(self.device.limits().max_texture_dimension_2d)?;
                        let allocation =
                            target_texture_bytes(self.format, [target.width, target.height])?;
                        if allocation > 256 * 1024 * 1024 {
                            return Err(Error::Capacity);
                        }
                        let effects = effect
                            .as_ref()
                            .map(|p| p.required_texture_bytes([target.width, target.height]))
                            .transpose()?
                            .unwrap_or(0);
                        *bytes = bytes
                            .checked_add(allocation)
                            .and_then(|b| b.checked_add(effects))
                            .ok_or(Error::Capacity)?;
                    }
                    let source = if ancestor.is_some() || layer.scene.has_axis_clips() {
                        std::borrow::Cow::Owned(layer.scene.with_binary_clip(clip))
                    } else {
                        std::borrow::Cow::Borrowed(layer.scene.as_ref())
                    };
                    self.preflight_layer_tree(&source, target, None, count, bytes, passes)?;
                }
                GroupRef::Axis(axis) => {
                    if states[current].count != 0 {
                        return Err(Error::UnsupportedExecution);
                    }
                    let plan = Self::resolve_axis(axis, frame, ancestor, clip)?;
                    let target = frame.at(plan.extent());
                    let source = axis.scene().with_binary_clip(clip);
                    if target.clip_intersects(clip) {
                        has_axis = true;
                        target.uniforms(self.device.limits().max_texture_dimension_2d)?;
                        let source_bytes =
                            target_texture_bytes(self.format, [target.width, target.height])?;
                        let parent_bytes =
                            target_texture_bytes(self.format, [frame.width, frame.height])?;
                        if source_bytes > 256 * 1024 * 1024 || parent_bytes > 256 * 1024 * 1024 {
                            return Err(Error::Capacity);
                        }
                        // Conservative linear/background coverage/output plus actual child.
                        *bytes = bytes
                            .checked_add(source_bytes * 2)
                            .and_then(|b| b.checked_add(parent_bytes))
                            .ok_or(Error::Capacity)?;
                        *passes = passes.checked_add(3).ok_or(Error::Capacity)?;
                    }
                    self.preflight_layer_tree(&source, target, Some(&plan), count, bytes, passes)?;
                }
            }
            if *bytes > 512 * 1024 * 1024 || *passes > 16 * 1024 {
                return Err(Error::Capacity);
            }
        }
        if advanced_source != [0; 2] {
            *bytes = bytes
                .checked_add(advanced::advanced_texture_bytes(
                    self.format,
                    [frame.width, frame.height],
                    advanced_source,
                )?)
                .ok_or(Error::Capacity)?;
        } else if has_axis {
            *bytes = bytes
                .checked_add(target_texture_bytes(
                    self.format,
                    [frame.width, frame.height],
                )?)
                .ok_or(Error::Capacity)?;
        }
        if *bytes > 512 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        Ok(())
    }
    pub(super) async fn prepare_layer_tree(
        &mut self,
        scene: &Scene,
        frame: RenderFrame,
        ancestor: Option<&crate::composition::AxisClipPlan>,
        fixed_root: Option<crate::image::GpuImage>,
    ) -> Result<PreparedScene, Error> {
        use crate::scene::{GroupOutput, GroupRef};
        let _target_depth = self.enter_target_depth(frame);
        self.preflight_owned_shaders(scene, frame)?;
        let (resolved_cached, cached) = self.prepare_cached_picture_scene(scene, frame).await?;
        let (resolved_owned, owned) = self
            .prepare_owned_shader_scene(&resolved_cached, frame)
            .await?;
        let scene = &resolved_owned;
        let mut groups = PreparedGroups::default();
        if let Some(root) = fixed_root {
            groups.root = Some(GroupTarget::new(root, true));
        }
        if scene.layer_depth() == 0 {
            let mut prepared = self.prepare_flat_scene(scene, frame, groups).await?;
            prepared.owned_shaders = owned;
            prepared.cached_pictures = cached;
            return Ok(prepared);
        }
        let (states, changes) = scene.compile_states(frame.dpi_scale)?;
        let mut current = 0;
        let mut cursor = 0;
        let mut outputs = Vec::new();
        let mut metrics = LayerMetrics::default();
        for (index, group) in scene.groups() {
            for next in changes.get(cursor..index).unwrap_or(&[]).iter().flatten() {
                current = *next;
            }
            cursor = index + 1;
            let clip = states[current].clip;
            outputs.try_reserve(1).map_err(|_| Error::Capacity)?;
            match group {
                GroupRef::Layer(layer) => {
                    let target = frame.child(layer)?;
                    if target.empty() {
                        outputs.push(GroupOutput::Layer(None));
                        continue;
                    }
                    let scoped = if ancestor.is_some() || layer.scene.has_axis_clips() {
                        std::borrow::Cow::Owned(layer.scene.with_binary_clip(clip))
                    } else {
                        std::borrow::Cow::Borrowed(layer.scene.as_ref())
                    };
                    let source =
                        Box::pin(self.prepare_layer_tree(&scoped, target, None, None)).await?;
                    let capture = self.capture(&source, layer.effects.as_ref()).await?;
                    metrics.add_source(&source, &capture)?;
                    let [x, y] = target.origin.map(|v| v as f32 / frame.dpi_scale);
                    let mut draw = ImageDraw::new(
                        capture.into_image(),
                        [
                            x,
                            y,
                            target.width as f32 / frame.dpi_scale,
                            target.height as f32 / frame.dpi_scale,
                        ],
                    );
                    draw.opacity = layer.opacity;
                    draw.sampling = if layer.linear_sampling || !layer.blend.is_fixed() {
                        ImageSampling::Linear
                    } else {
                        ImageSampling::Nearest
                    };
                    draw.validate()?;
                    outputs.push(GroupOutput::Layer(Some((
                        Box::new(draw),
                        [
                            target.origin[0],
                            target.origin[1],
                            target.width,
                            target.height,
                        ],
                    ))));
                }
                GroupRef::Axis(axis) => {
                    let plan = Self::resolve_axis(axis, frame, ancestor, clip)?;
                    if plan.is_empty() || !frame.at(plan.extent()).clip_intersects(clip) {
                        outputs.push(GroupOutput::Axis(None));
                        continue;
                    }
                    if groups.root.is_none() {
                        groups.root =
                            Some(GroupTarget::new(self.create_group_target(frame)?, false));
                        groups.metrics.texture_bytes =
                            target_texture_bytes(self.format, [frame.width, frame.height])?;
                    }
                    let parent = &groups.root.as_ref().ok_or(Error::ResourceDomain)?.image;
                    let prepared = self.prepare_axis_clip(parent, &plan).await?;
                    let scoped = axis.scene().with_binary_clip(clip);
                    let child = prepared.source().ok_or(Error::ResourceDomain)?.clone();
                    let source = Box::pin(self.prepare_layer_tree(
                        &scoped,
                        frame.at(plan.extent()),
                        Some(&plan),
                        Some(child),
                    ))
                    .await?;
                    groups.metrics.add(&prepared, &source)?;
                    metrics.add_nested(source.layers)?;
                    groups
                        .children
                        .try_reserve(1)
                        .map_err(|_| Error::Capacity)?;
                    outputs.push(GroupOutput::Axis(Some(groups.children.len())));
                    groups.children.push(PreparedGroup {
                        clip: prepared,
                        scene: source,
                    });
                }
            }
        }
        let resolved = scene.replace_groups(outputs)?;
        let mut prepared = self.prepare_flat_scene(&resolved, frame, groups).await?;
        prepared.layers = metrics;
        prepared.owned_shaders = owned;
        prepared.cached_pictures = cached;
        self.refresh_private_text_tree(&mut prepared)?;
        Ok(prepared)
    }
}
impl LayerMetrics {
    pub(super) fn add_nested(&mut self, other: Self) -> Result<(), Error> {
        self.source_masks.merge(other.source_masks)?;
        self.source_image_filters
            .merge(other.source_image_filters)?;
        self.source_owned_shaders
            .merge(other.source_owned_shaders)?;
        self.source_cached_pictures
            .merge(other.source_cached_pictures)?;
        shader_effect::merge_metrics(&mut self.source_shaders, other.source_shaders)?;
        self.source_color_filters
            .merge(other.source_color_filters)?;
        self.source_managed_shaders
            .merge(other.source_managed_shaders)?;
        self.source_aa.merge(other.source_aa)?;
        self.source_image_effects
            .merge(other.source_image_effects)?;
        self.source_replay_texture_copy_bytes = self
            .source_replay_texture_copy_bytes
            .checked_add(other.source_replay_texture_copy_bytes)
            .ok_or(Error::Capacity)?;
        self.captures = self
            .captures
            .checked_add(other.captures)
            .ok_or(Error::Capacity)?;
        self.capture_bytes = self
            .capture_bytes
            .checked_add(other.capture_bytes)
            .ok_or(Error::Capacity)?;
        self.effect_texture_bytes = self
            .effect_texture_bytes
            .checked_add(other.effect_texture_bytes)
            .ok_or(Error::Capacity)?;
        self.effect_uniform_bytes = self
            .effect_uniform_bytes
            .checked_add(other.effect_uniform_bytes)
            .ok_or(Error::Capacity)?;
        self.effect_passes = self
            .effect_passes
            .checked_add(other.effect_passes)
            .ok_or(Error::Capacity)?;
        self.geometry_bytes = self
            .geometry_bytes
            .checked_add(other.geometry_bytes)
            .ok_or(Error::Capacity)?;
        self.uniform_bytes = self
            .uniform_bytes
            .checked_add(other.uniform_bytes)
            .ok_or(Error::Capacity)?;
        self.glyph_upload_bytes = self
            .glyph_upload_bytes
            .checked_add(other.glyph_upload_bytes)
            .ok_or(Error::Capacity)?;
        self.image_mask_upload_bytes = self
            .image_mask_upload_bytes
            .checked_add(other.image_mask_upload_bytes)
            .ok_or(Error::Capacity)?;
        self.source_draws = self
            .source_draws
            .checked_add(other.source_draws)
            .ok_or(Error::Capacity)?;
        self.advanced_operations = self
            .advanced_operations
            .checked_add(other.advanced_operations)
            .ok_or(Error::Capacity)?;
        self.advanced_passes = self
            .advanced_passes
            .checked_add(other.advanced_passes)
            .ok_or(Error::Capacity)?;
        self.advanced_texture_bytes = self
            .advanced_texture_bytes
            .checked_add(other.advanced_texture_bytes)
            .ok_or(Error::Capacity)?;
        self.advanced_uniform_bytes = self
            .advanced_uniform_bytes
            .checked_add(other.advanced_uniform_bytes)
            .ok_or(Error::Capacity)?;
        self.advanced_geometry_bytes = self
            .advanced_geometry_bytes
            .checked_add(other.advanced_geometry_bytes)
            .ok_or(Error::Capacity)?;
        self.source_target_clears = self
            .source_target_clears
            .checked_add(other.source_target_clears)
            .ok_or(Error::Capacity)?;
        self.source_replay_uniform_bytes = self
            .source_replay_uniform_bytes
            .checked_add(other.source_replay_uniform_bytes)
            .ok_or(Error::Capacity)?;
        self.source_replay_uniform_allocations = self
            .source_replay_uniform_allocations
            .checked_add(other.source_replay_uniform_allocations)
            .ok_or(Error::Capacity)?;
        Ok(())
    }
    fn add_source(&mut self, scene: &PreparedScene, capture: &CapturedScene) -> Result<(), Error> {
        let child = scene.layers;
        let source = scene.metrics;
        let aa = scene.retained_aa_metrics();
        self.source_masks.merge(child.source_masks)?;
        self.source_image_filters
            .merge(child.source_image_filters)?;
        self.source_owned_shaders
            .merge(child.source_owned_shaders)?;
        self.source_cached_pictures
            .merge(child.source_cached_pictures)?;
        self.source_cached_pictures
            .merge(scene.cached_picture_metrics())?;
        self.source_masks.merge(scene.mask_composition_metrics())?;
        self.source_image_filters
            .merge(scene.image_filter_metrics())?;
        self.source_owned_shaders
            .merge(scene.owned_shader_metrics())?;
        self.source_masks.merge(aa.source_masks)?;
        self.source_image_filters.merge(aa.source_image_filters)?;
        self.source_owned_shaders.merge(aa.source_owned_shaders)?;
        self.source_cached_pictures
            .merge(aa.source_cached_pictures)?;
        shader_effect::merge_metrics(&mut self.source_shaders, child.source_shaders)?;
        self.source_color_filters
            .merge(child.source_color_filters)?;
        self.source_managed_shaders
            .merge(child.source_managed_shaders)?;
        shader_effect::merge_metrics(&mut self.source_shaders, scene.shader_effect_metrics())?;
        self.source_color_filters
            .merge(scene.color_filter_metrics())?;
        self.source_managed_shaders
            .merge(scene.managed_shader_metrics())?;
        shader_effect::merge_metrics(&mut self.source_shaders, aa.source_shaders)?;
        self.source_color_filters.merge(aa.source_color_filters)?;
        self.source_managed_shaders
            .merge(aa.source_managed_shaders)?;
        self.source_image_effects
            .merge(child.source_image_effects)?;
        self.source_image_effects
            .merge(scene.image_effect_metrics())?;
        self.source_image_effects.merge(aa.source_image_effects)?;
        self.source_aa.merge(child.source_aa)?;
        self.source_aa.merge(aa)?;
        self.captures = self
            .captures
            .checked_add(child.captures)
            .and_then(|v| v.checked_add(1))
            .ok_or(Error::Capacity)?;
        macro_rules! add {
            ($field:ident, $value:expr) => {
                self.$field = self
                    .$field
                    .checked_add(child.$field)
                    .and_then(|v| v.checked_add($value))
                    .ok_or(Error::Capacity)?;
            };
        }
        add!(capture_bytes, capture.capture_bytes());
        add!(
            geometry_bytes,
            source.geometry_bytes + aa.source_geometry_bytes
        );
        add!(
            uniform_bytes,
            source.uniform_bytes + aa.source_uniform_bytes
        );
        add!(
            glyph_upload_bytes,
            source.glyph_upload_bytes + aa.source_glyph_upload_bytes
        );
        let image_mask_bytes = source
            .image_upload_bytes
            .checked_add(source.mask_upload_bytes)
            .ok_or(Error::Capacity)?;
        add!(
            image_mask_upload_bytes,
            image_mask_bytes + aa.source_image_mask_upload_bytes
        );
        add!(source_draws, u64::from(source.draws));
        let advanced = scene.advanced_blend_metrics();
        add!(
            advanced_operations,
            u64::from(advanced.operations) + aa.source_advanced_operations
        );
        add!(
            advanced_passes,
            u64::from(advanced.render_passes) + aa.source_advanced_passes
        );
        add!(
            advanced_texture_bytes,
            advanced.texture_bytes + aa.source_advanced_texture_bytes
        );
        add!(
            advanced_uniform_bytes,
            advanced.uniform_bytes + aa.source_advanced_uniform_bytes
        );
        add!(
            advanced_geometry_bytes,
            advanced.geometry_bytes + aa.source_advanced_geometry_bytes
        );
        let replay = capture.source_replay_metrics();
        add!(source_replay_texture_copy_bytes, replay.texture_copy_bytes);
        add!(source_target_clears, u64::from(replay.target_clears));
        add!(source_replay_uniform_bytes, replay.uniform_upload_bytes);
        add!(
            source_replay_uniform_allocations,
            u64::from(replay.uniform_buffer_allocations)
        );
        let effects = capture.effect_metrics().unwrap_or_default();
        add!(effect_texture_bytes, effects.texture_bytes);
        add!(effect_uniform_bytes, effects.uniform_bytes);
        add!(effect_passes, u64::from(effects.compute_passes));
        Ok(())
    }
}
