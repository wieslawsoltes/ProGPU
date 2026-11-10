//! CPU forecast of the original ordered cache branches, used only to avoid
//! preparing one-shot RGB work in a body which the same replay will skip.
use super::super::cache::{NativeLayerCacheSelection, NativeReplayCacheIdentity};
use super::*;
use crate::native_scene::engine_resources::layers::PreparedNativeLayerTargets;

/// Consumes a private snapshot. Forecast keys never escape this function and
/// cannot publish pixel readiness; encoding recomputes from its actual journal.
pub(crate) fn native_replay_skipped_operations(
    source: &NativeReplayPlan,
    targets: &PreparedNativeLayerTargets,
    updates: NativeLayerOutputUpdates,
) -> Result<Vec<bool>, Error> {
    let identity = NativeReplayCacheIdentity::from_plan(source.source());
    skipped_with_identity(source, targets, updates, &identity)
}
pub(super) fn skipped_with_identity(
    source: &NativeReplayPlan,
    targets: &PreparedNativeLayerTargets,
    mut updates: NativeLayerOutputUpdates,
    identity: &NativeReplayCacheIdentity,
) -> Result<Vec<bool>, Error> {
    if !Arc::ptr_eq(source.targets(), targets.plan()) {
        return Err(Error::ResourceDomain);
    }
    let mut skipped = Vec::new();
    skipped
        .try_reserve_exact(source.operations().len())
        .map_err(|_| Error::Capacity)?;
    skipped.resize(source.operations().len(), false);
    let mut depth = 0u32;
    for (index, operation) in source.operations().iter().enumerate() {
        if depth != 0 {
            match operation {
                NativeReplayOperation::PushLayer(_) => {
                    depth = depth.checked_add(1).ok_or(Error::Capacity)?;
                    skipped[index] = true;
                    continue;
                }
                NativeReplayOperation::PopLayer(_) => {
                    depth -= 1;
                    if depth != 0 {
                        skipped[index] = true;
                        continue;
                    }
                }
                _ => {
                    skipped[index] = true;
                    continue;
                }
            }
        }
        match operation {
            NativeReplayOperation::PushLayer(push)
                if !push.final_sample_shader && push.can_skip_content_on_effect_cache =>
            {
                let target = targets.target(push.target).ok_or(Error::ResourceDomain)?;
                let selected = NativeLayerCacheSelection::push(identity, push, target)?;
                if updates.hit(selected.slot, selected.key) {
                    depth = 1;
                    skipped[index] = true;
                }
            }
            NativeReplayOperation::PopLayer(pop)
                if !pop
                    .effect
                    .as_ref()
                    .is_some_and(NativeReplayEffect::final_sample)
                    && ((!pop.backdrop && pop.effect.is_some()) || pop.cache_content) =>
            {
                let target = targets.target(pop.source).ok_or(Error::ResourceDomain)?;
                let selected = NativeLayerCacheSelection::pop(identity, pop, target)?;
                // A later scope can reuse an earlier successful output in the
                // same encoder. Failure discards the entire encoding candidate.
                updates.record_encoded_output(
                    targets,
                    selected.slot,
                    selected.key,
                    selected.effected,
                )?;
            }
            _ => {}
        }
    }
    if depth != 0 {
        return Err(Error::InvalidInput);
    }
    Ok(skipped)
}
