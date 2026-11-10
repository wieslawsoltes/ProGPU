//! Original outer scene-frame and complete layer/effect preflight.
use super::*;
use budget::{
    MAX_DRAW_PASSES, MAX_EFFECT_PASSES, MAX_TOTAL_COMPILED_BYTES, NativeCacheBudget,
    NativeLayerBudget,
};
mod aggregate;
mod shader;
pub use aggregate::{NativeSceneBudgetInput, NativeSceneExecutionBudget};

/// Original color attachment formats; R8 is reserved for owned mask-child engines.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum NativeTargetFormat {
    Rgba8Unorm,
    Bgra8Unorm,
    Rgba8UnormSrgb,
    Bgra8UnormSrgb,
    R8Unorm,
}
impl NativeTargetFormat {
    pub fn is_linear_byte_color(self) -> bool {
        matches!(self, Self::Rgba8Unorm | Self::Bgra8Unorm)
    }
    #[cfg(feature = "gpu")]
    pub fn texture_format(self) -> wgpu::TextureFormat {
        match self {
            Self::Rgba8Unorm => wgpu::TextureFormat::Rgba8Unorm,
            Self::Bgra8Unorm => wgpu::TextureFormat::Bgra8Unorm,
            Self::Rgba8UnormSrgb => wgpu::TextureFormat::Rgba8UnormSrgb,
            Self::Bgra8UnormSrgb => wgpu::TextureFormat::Bgra8UnormSrgb,
            Self::R8Unorm => wgpu::TextureFormat::R8Unorm,
        }
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLayerPlanMetrics {
    pub has_materialized_layers: bool,
    pub has_layer_masks: bool,
    pub layer_mask_kind: u32,
    pub has_layer_effects: bool,
    pub has_builtin_effects: bool,
    pub has_drop_shadows: bool,
    pub materialized_layer_count: u32,
    pub backdrop_layer_count: u32,
    pub effected_backdrop_layer_count: u32,
    pub advanced_layer_count: u32,
    pub needs_layer_coverage: bool,
    pub advanced_source_width: u32,
    pub advanced_source_height: u32,
    pub effect_node_count: u32,
    pub effect_pass_count: u32,
    pub shader_effect_count: u32,
    pub shader_sample_count: u32,
    pub shader_sampler_bytes: u64,
    pub effect_chain_revision: u32,
    pub layer_coverage_texture_bytes: u64,
}
#[derive(Clone, Copy, Debug)]
pub struct NativeLayerOccurrence {
    pub push_command: u32,
    pub pop_command: u32,
    pub layer: wire::Layer,
    pub parent_target: NativeScissor,
    pub target: NativeScissor,
    pub presentation: wire::Presentation,
    pub materialized: bool,
    pub cached: bool,
    pub final_sample: bool,
    pub replaces_axis_clip_background: bool,
    pub byte_axis_clip: bool,
}
/// An immutable complete layer scan. Actual device attachment ownership and
/// typed draw-family admission remain independent steps before GPU publication.
pub struct NativeLayerExecutionPlan {
    scene: Arc<NativeScene>,
    frame: NativeSceneFrame,
    presentation: wire::Presentation,
    mapped_presentation: bool,
    layers: Arc<[NativeLayerOccurrence]>,
    layer_budget: NativeLayerBudget,
    cache_budget: NativeCacheBudget,
    metrics: NativeLayerPlanMetrics,
}
impl NativeLayerExecutionPlan {
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn frame(&self) -> NativeSceneFrame {
        self.frame
    }
    pub fn presentation(&self) -> wire::Presentation {
        self.presentation
    }
    pub fn mapped_presentation(&self) -> bool {
        self.mapped_presentation
    }
    pub fn layers(&self) -> &[NativeLayerOccurrence] {
        &self.layers
    }
    pub fn layer_budget(&self) -> &NativeLayerBudget {
        &self.layer_budget
    }
    pub fn cache_budget(&self) -> &NativeCacheBudget {
        &self.cache_budget
    }
    pub fn metrics(&self) -> NativeLayerPlanMetrics {
        self.metrics
    }
    /// `borrows_shared_vector_pipeline` describes the actual child engine's
    /// provider relationship; a picture child cannot use the root-only axis clip.
    pub fn compile(
        scene: Arc<NativeScene>,
        frame: NativeSceneFrame,
        format: NativeTargetFormat,
        borrows_shared_vector_pipeline: bool,
    ) -> Result<Self, Error> {
        let presentation = admit_frame(&scene, frame)?;
        let mapped_presentation = presentation.viewport_x != 0
            || presentation.viewport_y != 0
            || presentation.viewport_width != frame.width
            || presentation.viewport_height != frame.height
            || presentation.dpi_scale_x != frame.dpi_scale
            || presentation.dpi_scale_y != frame.dpi_scale;
        let mut cursor =
            NativeLayerTargetCursor::new(scene.clone(), [frame.width, frame.height], presentation);
        let mut layer_budget = NativeLayerBudget::default();
        let mut cache_budget = NativeCacheBudget::default();
        let mut active_cache_owners = [0; SCENE_MAX_STACK_DEPTH as usize];
        let mut active_occurrences = [0usize; SCENE_MAX_STACK_DEPTH as usize];
        let mut depth = 0usize;
        let mut metrics = NativeLayerPlanMetrics::default();
        let mut layers: Vec<NativeLayerOccurrence> = Vec::new();
        for (index, command) in scene.envelope().commands().enumerate() {
            let command = command.record;
            let parent_target = cursor.current();
            let target = cursor.advance(command)?;
            if command.kind == SCENE_COMMAND_POP_LAYER {
                if depth == 0 || !layer_budget.pop() {
                    return Err(Error::InvalidInput);
                }
                depth -= 1;
                layers[active_occurrences[depth]].pop_command = index as u32;
                continue;
            }
            if command.kind != SCENE_COMMAND_PUSH_LAYER {
                continue;
            }
            let layer = read_layer(&scene, command)?;
            let materialized = layer_requires_materialization(&layer);
            let final_sample = layer_output_frame(&scene, &layer).is_some();
            let axis_clip = is_axis_clip(&scene, &layer);
            let byte_axis_clip = format.is_linear_byte_color() && axis_clip;
            let source_axis_clip = !borrows_shared_vector_pipeline
                && presentation.viewport_x == 0
                && presentation.viewport_y == 0
                && presentation.viewport_width == frame.width
                && presentation.viewport_height == frame.height
                && byte_axis_clip;
            if mapped_presentation
                && materialized
                && !final_sample
                && !source_axis_clip
                && !supports_mapped_layer(&layer)
            {
                return Err(Error::UnsupportedExecution);
            }
            let cached = layer.flags & SCENE_LAYER_CACHE_CONTENT != 0;
            if depth == active_cache_owners.len()
                || cached && active_cache_owners[..depth].contains(&layer.composite_revision)
            {
                return Err(Error::InvalidInput);
            }
            active_cache_owners[depth] = if cached { layer.composite_revision } else { 0 };
            active_occurrences[depth] = layers.len();
            depth += 1;
            metrics.has_materialized_layers |= materialized;
            if layer.mask_resource_index != SCENE_NO_INDEX {
                metrics.has_layer_masks = true;
                let resource = scene
                    .envelope()
                    .resource(layer.mask_resource_index)
                    .ok_or(Error::InvalidInput)?;
                let kind = mask_kind(resource)?;
                if kind == SCENE_LAYER_MASK_ANALYTIC_CHAIN {
                    return Err(Error::UnsupportedExecution);
                }
                let texture = is_texture_mask(kind);
                metrics.layer_mask_kind = if texture {
                    GROUP_MASK_TEXTURE
                } else {
                    GROUP_MASK_ROUNDED_RECTANGLE
                };
                if texture {
                    let cost = if kind == SCENE_LAYER_MASK_COVERAGE_BITMAP {
                        u64::from(resource.record.auxiliary_size)
                    } else {
                        u64::from(parent_target.width)
                            * u64::from(parent_target.height)
                            * mask_texture_multiplier(resource, kind)?
                    };
                    add_mask_cost(&mut metrics.layer_coverage_texture_bytes, cost)?;
                }
            }
            let effected = layer.effect_resource_index != SCENE_NO_INDEX;
            if cached
                && !cache_budget.add(
                    layer.composite_revision,
                    target,
                    effected,
                    layer.flags & SCENE_LAYER_CACHE_SHARED != 0,
                    layer.content_revision,
                )
            {
                return Err(Error::InvalidInput);
            }
            metrics.has_layer_effects |= effected;
            if effected {
                let resource = scene
                    .envelope()
                    .resource(layer.effect_resource_index)
                    .ok_or(Error::InvalidInput)?;
                if resource.record.kind == SCENE_RESOURCE_WPF_SHADER_EFFECT {
                    shader::admit(
                        &scene,
                        index as u32,
                        &layer,
                        target,
                        cursor.current_presentation(),
                        &active_cache_owners[..depth],
                        resource,
                        &mut metrics,
                    )?;
                } else {
                    metrics.has_builtin_effects = true;
                    let chain =
                        wire::EffectChain::read(resource.payload).ok_or(Error::InvalidInput)?;
                    if chain.effect_count > MAX_GROUP_EFFECTS
                        || metrics.effect_node_count > MAX_EFFECT_PASSES - chain.effect_count
                    {
                        return Err(Error::Capacity);
                    }
                    metrics.effect_node_count += chain.effect_count;
                    metrics.effect_chain_revision = chain.revision;
                    for i in 0..chain.effect_count as usize {
                        let effect = read_record::<wire::GroupEffect>(resource.auxiliary, i)?;
                        let shadow = effect.kind == GROUP_EFFECT_DROP_SHADOW;
                        let passes = if shadow { 3 } else { 2 };
                        if metrics.effect_pass_count > MAX_EFFECT_PASSES - passes {
                            return Err(Error::Capacity);
                        }
                        metrics.effect_pass_count += passes;
                        metrics.has_drop_shadows |= shadow;
                        // Original root presentation, not the current layer's presentation.
                        resolve_effect(effect, presentation).ok_or(Error::InvalidInput)?;
                    }
                }
            }
            if materialized && metrics.materialized_layer_count == MAX_DRAW_PASSES {
                return Err(Error::Capacity);
            }
            metrics.materialized_layer_count += u32::from(materialized);
            let replaces_axis_clip_background =
                axis_clip && layer.flags & SCENE_LAYER_INITIALIZE_FROM_BACKGROUND != 0;
            metrics.needs_layer_coverage |= replaces_axis_clip_background || byte_axis_clip;
            let backdrop = materialized
                && layer.flags & (SCENE_LAYER_BACKDROP | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND)
                    != 0;
            metrics.backdrop_layer_count += u32::from(backdrop);
            metrics.effected_backdrop_layer_count +=
                u32::from(backdrop && (effected || layer.flags & SCENE_LAYER_IGNORE_ALPHA != 0));
            if materialized
                && (advanced_blend(layer.blend_mode)
                    || replaces_axis_clip_background
                    || byte_axis_clip)
            {
                metrics.advanced_layer_count += 1;
                metrics.advanced_source_width = metrics.advanced_source_width.max(target.width);
                metrics.advanced_source_height = metrics.advanced_source_height.max(target.height);
            }
            let budget_target = if final_sample {
                NativeScissor::full([1, 1])
            } else {
                target
            };
            if !layer_budget.push(
                budget_target,
                materialized && !cached,
                effected && !cached && !final_sample,
            ) {
                return Err(Error::Capacity);
            }
            layers.try_reserve(1).map_err(|_| Error::Capacity)?;
            layers.push(NativeLayerOccurrence {
                push_command: index as u32,
                pop_command: SCENE_NO_INDEX,
                layer,
                parent_target,
                target,
                presentation: cursor.current_presentation(),
                materialized,
                cached,
                final_sample,
                replaces_axis_clip_background,
                byte_axis_clip,
            });
        }
        if depth != 0 {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            scene,
            frame,
            presentation,
            mapped_presentation,
            layers: layers.into(),
            layer_budget,
            cache_budget,
            metrics,
        })
    }
}
pub(super) fn admit_frame(
    scene: &NativeScene,
    frame: NativeSceneFrame,
) -> Result<wire::Presentation, Error> {
    let p = frame.resolve_presentation().ok_or(Error::InvalidInput)?;
    if [
        frame.clear_color.r,
        frame.clear_color.g,
        frame.clear_color.b,
        frame.clear_color.a,
    ]
    .into_iter()
    .any(|v| !v.is_finite())
        || frame.scene_id == 0
        || frame.generation == 0
        || frame.scene_id != scene.envelope().header().scene_id
        || frame.generation != scene.envelope().header().generation
    {
        return Err(Error::InvalidInput);
    }
    let flags = frame.effective_flags();
    if flags & SCENE_FRAME_DAMAGE_RECT != 0 {
        let d = frame.damage;
        let width = (f64::from(p.viewport_width) / f64::from(p.dpi_scale_x)) as f32;
        let height = (f64::from(p.viewport_height) / f64::from(p.dpi_scale_y)) as f32;
        if flags & SCENE_FRAME_PRESERVE_TARGET == 0
            || [d.x, d.y, d.width, d.height]
                .into_iter()
                .any(|v| !v.is_finite())
            || d.width <= 0.0
            || d.height <= 0.0
            || d.x >= width
            || d.y >= height
            || d.x + d.width <= 0.0
            || d.y + d.height <= 0.0
        {
            return Err(Error::InvalidInput);
        }
    }
    // Rust returns a full typed metrics value; there is no truncated foreign
    // output prefix that could silently omit requested CPU stage timing.
    Ok(p)
}
fn is_axis_clip(scene: &NativeScene, layer: &wire::Layer) -> bool {
    if layer.flags & SCENE_LAYER_BOUNDS == 0
        || layer.opacity != 1.0
        || layer.blend_mode != BLEND_SRC_OVER
        || layer.flags & !(SCENE_LAYER_BOUNDS | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND) != 0
        || layer.effect_resource_index != SCENE_NO_INDEX
        || layer.mask_resource_index == SCENE_NO_INDEX
    {
        return false;
    }
    let Some(resource) = scene.envelope().resource(layer.mask_resource_index) else {
        return false;
    };
    if resource.record.kind != SCENE_RESOURCE_LAYER_MASK
        || resource.payload.len() != wire::LayerMask::WIRE_SIZE
    {
        return false;
    }
    wire::LayerMask::read(resource.payload).is_some_and(|m| {
        m.kind == SCENE_LAYER_MASK_ROUNDED_RECTANGLE && m.flags == SCENE_LAYER_MASK_AXIS_CLIP_AREA
    })
}
fn advanced_blend(mode: u32) -> bool {
    (BLEND_MULTIPLY..=BLEND_EXCLUSION).contains(&mode)
        || (BLEND_OVERLAY..=BLEND_LUMINOSITY).contains(&mode)
}
fn mask_kind(resource: crate::native_scene::ResourceView<'_>) -> Result<u32, Error> {
    resource
        .payload
        .get(4..8)
        .and_then(|b| b.try_into().ok())
        .map(u32::from_le_bytes)
        .ok_or(Error::InvalidInput)
}
fn is_texture_mask(kind: u32) -> bool {
    matches!(
        kind,
        SCENE_LAYER_MASK_COVERAGE_BITMAP
            | SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
            | SCENE_LAYER_MASK_BRUSH
            | SCENE_LAYER_MASK_GEOMETRY
            | SCENE_LAYER_MASK_PICTURE
            | SCENE_LAYER_MASK_COMPOSITE
    )
}
fn mask_texture_multiplier(
    resource: crate::native_scene::ResourceView<'_>,
    kind: u32,
) -> Result<u64, Error> {
    if kind == SCENE_LAYER_MASK_COMPOSITE {
        // Both original 48-byte legacy and complete 64-byte descriptors retain
        // the source memcpy-min default zeros for absent picture counts.
        let mut bytes = [0u8; 64];
        let size = resource.payload.len().min(bytes.len());
        bytes[..size].copy_from_slice(&resource.payload[..size]);
        let c = wire::LayerCompositeMask::read(&bytes).ok_or(Error::InvalidInput)?;
        Ok(u64::from(c.component_count) + 2 + u64::from(c.picture_mask_count) * 4)
    } else {
        Ok(if kind == SCENE_LAYER_MASK_PICTURE {
            5
        } else {
            1
        })
    }
}
fn add_mask_cost(total: &mut u64, cost: u64) -> Result<(), Error> {
    let limit = u64::from(SCENE_MAX_LAYER_BYTES);
    if cost > limit || *total > limit - cost {
        return Err(Error::Capacity);
    }
    *total += cost;
    Ok(())
}
fn read_record<T: WireRecord>(bytes: &[u8], index: usize) -> Result<T, Error> {
    index
        .checked_mul(T::WIRE_SIZE)
        .and_then(|o| bytes.get(o..))
        .and_then(T::read)
        .ok_or(Error::InvalidInput)
}
