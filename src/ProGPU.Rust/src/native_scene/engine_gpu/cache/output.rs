use super::*;
/// Actual slot key selected from original replay metadata. This neither records
/// output nor proves readiness; use the target pool's submitted-output journal.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeLayerCacheSelection {
    pub slot: u32,
    pub key: NativeLayerOutputKey,
    pub effected: bool,
}
impl NativeLayerCacheSelection {
    pub fn push(
        identity: &NativeReplayCacheIdentity,
        operation: &NativeReplayLayerPush,
        target: &NativeLayerTarget,
    ) -> Result<Self, Error> {
        Self::select(
            identity,
            target,
            operation.target,
            operation.cache_content,
            operation.cache_content_revision,
            operation.cache_identity,
            operation.effect_count,
            operation.operation_id,
            operation.effect_cache_operation_id,
        )
    }
    pub fn pop(
        identity: &NativeReplayCacheIdentity,
        operation: &NativeReplayLayerPop,
        target: &NativeLayerTarget,
    ) -> Result<Self, Error> {
        Self::select(
            identity,
            target,
            operation.source,
            operation.cache_content,
            operation.cache_content_revision,
            operation.cache_identity,
            operation.effect.as_ref().map_or(0, |e| e.effect_count()),
            operation.operation_id,
            0,
        )
    }
    #[allow(clippy::too_many_arguments)]
    fn select(
        replay: &NativeReplayCacheIdentity,
        target: &NativeLayerTarget,
        id: NativeLayerTargetId,
        cache_content: bool,
        revision: u64,
        identity: u64,
        count: u32,
        operation: u64,
        effect_operation: u64,
    ) -> Result<Self, Error> {
        let NativeLayerTargetId::Slot(slot) = id else {
            return Err(Error::ResourceDomain);
        };
        if target.request().target != id {
            return Err(Error::ResourceDomain);
        }
        let (scene_hash, operation_id, effected) = if cache_content {
            (revision, identity, count != 0)
        } else {
            (
                replay.presented_scene_hash,
                if effect_operation != 0 {
                    effect_operation
                } else {
                    operation
                },
                true,
            )
        };
        Ok(Self {
            slot,
            key: target.output_key(scene_hash, operation_id, effected)?,
            effected,
        })
    }
}
