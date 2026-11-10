//! Original final aggregate layer, material, effect and partial-replay decisions.
use super::*;

/// Produced by the complete ordered typed-draw preflight. These are original
/// conservative admission costs, separate from actual GPU allocation metrics.
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeSceneBudgetInput {
    pub compilation: budget::NativeCompilationBudget,
    pub material_bytes: u64,
    pub generated_mask_bytes: u64,
    pub has_state_masks: bool,
    pub three_d_draw_count: u32,
    pub has_binary_glyphs: bool,
    /// Complete flattened source coverage, including ordinary vector glyphs.
    pub glyph_coverage_bytes: u64,
}
#[derive(Clone, Copy, Debug)]
pub struct NativeSceneExecutionBudget {
    pub compilation: budget::NativeCompilationBudget,
    pub material_bytes: u64,
    pub compiled_bytes: u64,
    pub effect_uniform_bytes: u64,
    pub all_effect_bytes: u64,
    pub retained_layer_bytes: u64,
    pub destination_frame_bytes: u64,
    pub advanced_source_bytes: u64,
    pub destination_texture_bytes: u64,
    pub destination_texture_count: u64,
    pub destination_sampling: bool,
    pub partial_replay_supported: bool,
    pub partial_damage: bool,
    pub preserve_target: bool,
    pub damage: NativeScissor,
}
impl NativeLayerExecutionPlan {
    /// Preserve the original whole-scene budget and damage fallback after every
    /// typed payload has been admitted. No GPU resources or caches are mutated.
    pub fn finish_budget(
        &self,
        input: NativeSceneBudgetInput,
    ) -> Result<NativeSceneExecutionBudget, Error> {
        // Public budget records may be supplied independently of the ordered
        // producer. Reject malformed counters before the source bounded sums.
        let b = input.compilation;
        if b.draw_passes > budget::MAX_DRAW_PASSES
            || b.vertex_bytes > budget::MAX_VERTEX_BYTES
            || b.index_bytes > budget::MAX_INDEX_BYTES
            || b.texture_bytes > budget::MAX_TEXTURE_BYTES
            || b.coverage_bytes > budget::MAX_COVERAGE_BYTES
            || b.total_bytes() > MAX_TOTAL_COMPILED_BYTES
        {
            return Err(Error::Capacity);
        }
        if self.mapped_presentation && input.three_d_draw_count != 0 {
            return Err(Error::UnsupportedExecution);
        }
        let m = self.metrics;
        let mut compilation = input.compilation;
        let effect_uniform_bytes = u64::from(m.effect_pass_count - m.shader_effect_count)
            * u64::from(budget::EFFECT_UNIFORM_ALIGNMENT);
        let all_effect_bytes = effect_uniform_bytes
            + u64::from(m.shader_effect_count) * 528
            + u64::from(m.shader_sample_count) * 64
            + m.shader_sampler_bytes;
        let destination_sampling = m.advanced_layer_count != 0 || m.backdrop_layer_count != 0;
        let partial_replay_supported = !m.has_materialized_layers
            && !input.has_state_masks
            && !destination_sampling
            && input.three_d_draw_count == 0;
        let flags = self.frame.effective_flags();
        let damage_requested = flags & SCENE_FRAME_DAMAGE_RECT != 0;
        let partial_damage = damage_requested && partial_replay_supported;
        let preserve_target =
            flags & SCENE_FRAME_PRESERVE_TARGET != 0 && (!damage_requested || partial_damage);
        let p = self.presentation;
        let damage = if partial_damage {
            resolve_damage_scissor(self.frame.damage, p)
        } else {
            NativeScissor {
                x: p.viewport_x,
                y: p.viewport_y,
                width: p.viewport_width,
                height: p.viewport_height,
                drawable: true,
            }
        };
        let destination_texture_count = if m.advanced_layer_count != 0 {
            2
        } else if destination_sampling {
            1
        } else {
            0
        };
        let destination_frame_bytes = texture_bytes(
            self.frame.width,
            self.frame.height,
            destination_texture_count,
        )?;
        let advanced_source_bytes = if m.advanced_layer_count == 0 {
            0
        } else {
            texture_bytes(
                m.advanced_source_width.max(1),
                m.advanced_source_height.max(1),
                if m.needs_layer_coverage { 2 } else { 1 },
            )?
        };
        let limit = u64::from(SCENE_MAX_LAYER_BYTES);
        if destination_frame_bytes > limit
            || advanced_source_bytes > limit - destination_frame_bytes
        {
            return Err(Error::Capacity);
        }
        let destination_texture_bytes = destination_frame_bytes + advanced_source_bytes;
        let layers = self.layer_budget.pooled_bytes();
        let effects = self.layer_budget.pooled_effect_bytes();
        let caches = self.cache_budget.pooled_bytes();
        let cache_effects = self.cache_budget.pooled_effect_bytes();
        if layers > limit
            || caches > limit - layers
            || effects > limit - layers - caches
            || cache_effects > limit - layers - caches - effects
        {
            return Err(Error::Capacity);
        }
        let retained_base = layers + caches + effects + cache_effects + destination_texture_bytes;
        let mut mask_bytes = m.layer_coverage_texture_bytes;
        add_mask_cost(&mut mask_bytes, input.generated_mask_bytes)?;
        // Match the source unsigned subtraction at this original admission
        // point; the independent combined compiled limit below still applies.
        if mask_bytes > limit.wrapping_sub(retained_base) {
            return Err(Error::Capacity);
        }
        let retained_layer_bytes = retained_base + mask_bytes;
        if input.has_binary_glyphs && !compilation.add(0, 0, 0, input.glyph_coverage_bytes) {
            return Err(Error::Capacity);
        }
        let payload = compilation.total_bytes();
        if input.material_bytes > MAX_TOTAL_COMPILED_BYTES
            || payload > MAX_TOTAL_COMPILED_BYTES - input.material_bytes
        {
            return Err(Error::Capacity);
        }
        let compiled_bytes = payload + input.material_bytes;
        if all_effect_bytes > MAX_TOTAL_COMPILED_BYTES - compiled_bytes
            || self.layer_budget.peak_bytes.max(retained_layer_bytes)
                > MAX_TOTAL_COMPILED_BYTES - compiled_bytes - all_effect_bytes
        {
            return Err(Error::Capacity);
        }
        Ok(NativeSceneExecutionBudget {
            compilation,
            material_bytes: input.material_bytes,
            compiled_bytes,
            effect_uniform_bytes,
            all_effect_bytes,
            retained_layer_bytes,
            destination_frame_bytes,
            advanced_source_bytes,
            destination_texture_bytes,
            destination_texture_count,
            destination_sampling,
            partial_replay_supported,
            partial_damage,
            preserve_target,
            damage,
        })
    }
}
fn texture_bytes(width: u32, height: u32, count: u64) -> Result<u64, Error> {
    u64::from(width)
        .checked_mul(u64::from(height))
        .and_then(|v| v.checked_mul(4))
        .and_then(|v| v.checked_mul(count))
        .ok_or(Error::Capacity)
}
