//! Native materialized target allocation and replay state. Whole-command
//! admission belongs to the execution preflight; these plans consume its budgets.
use super::*;
use crate::native_scene::execution::{
    NativeSceneFrame, NativeScissor,
    budget::{
        CACHED_LAYER_SLOT_BASE, LAYER_SLOT_COUNT, MAX_CACHED_LAYERS, NativeCacheBudget,
        NativeLayerBudget,
    },
};
mod cache;
#[cfg(feature = "gpu")]
mod composite;
#[cfg(feature = "gpu")]
pub use composite::{
    NativeLayerCompositeEngine, NativeLayerCompositeMetrics, PreparedNativeLayerComposite,
};
mod quad;
mod replay;
pub use cache::{NativeLayerOutputCache, NativeLayerOutputKey};
pub use quad::{
    NativeLayerQuadPath, native_layer_quad, native_transformed_layer_quad,
    native_transformed_layer_quad_with_preference,
};
pub use replay::{NativeDepthLoad, NativeLayerPassState, NativeTargetLoad, NativeTargetPass};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativeLayerDepthTarget, NativeLayerEffects, NativeLayerOutputUpdates, NativeLayerTarget,
    NativeLayerTargetPool, NativeLayerTexture, PreparedNativeLayerTargets,
};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLayerTargetId {
    Root,
    Slot(u32),
}
impl NativeLayerTargetId {
    pub fn from_wire(value: u32) -> Result<Self, Error> {
        if value == SCENE_NO_INDEX {
            Ok(Self::Root)
        } else if value < LAYER_SLOT_COUNT {
            Ok(Self::Slot(value))
        } else {
            Err(Error::InvalidInput)
        }
    }
    pub fn wire(self) -> u32 {
        match self {
            Self::Root => SCENE_NO_INDEX,
            Self::Slot(i) => i,
        }
    }
}
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeLayerPoolOptions {
    pub root_color: bool,
    pub depth: bool,
}
#[derive(Clone, Copy, Debug)]
pub struct NativeLayerTargetRequest {
    pub target: NativeLayerTargetId,
    pub size: [u32; 2],
    pub color: bool,
    pub effects: bool,
    pub depth: bool,
    pub cache_identity: u64,
}
/// Exact stable cache-slot assignment. Previous owners remain reserved when
/// their identity occurs later in the incoming inventory.
#[derive(Clone, Debug)]
pub struct NativeLayerTargetPlan {
    frame: NativeSceneFrame,
    requests: Arc<[NativeLayerTargetRequest]>,
    cache_budget: NativeCacheBudget,
    identities: [u64; MAX_CACHED_LAYERS],
    source_texture_bytes: u64,
    depth_bytes: u64,
}
impl NativeLayerTargetPlan {
    pub fn new(
        frame: NativeSceneFrame,
        layers: &NativeLayerBudget,
        caches: &NativeCacheBudget,
        previous_identities: [u64; MAX_CACHED_LAYERS],
        options: NativeLayerPoolOptions,
    ) -> Result<Self, Error> {
        frame.resolve_presentation().ok_or(Error::InvalidInput)?;
        if layers.peak_materialized_depth > SCENE_MAX_MATERIALIZED_LAYERS
            || caches.count as usize > MAX_CACHED_LAYERS
        {
            return Err(Error::InvalidInput);
        }
        let mut source_texture_bytes = 0u64;
        for bytes in [
            layers.pooled_bytes(),
            caches.pooled_bytes(),
            layers.pooled_effect_bytes(),
            caches.pooled_effect_bytes(),
        ] {
            source_texture_bytes = source_texture_bytes
                .checked_add(bytes)
                .ok_or(Error::Capacity)?;
        }
        if options.root_color {
            source_texture_bytes = source_texture_bytes
                .checked_add(texture_bytes([frame.width, frame.height], 1)?)
                .ok_or(Error::Capacity)?;
        }
        if source_texture_bytes > u64::from(SCENE_MAX_LAYER_BYTES) {
            return Err(Error::Capacity);
        }
        let mut requests =
            allocate(1 + layers.peak_materialized_depth as usize + caches.count as usize)?;
        let mut depth_bytes = 0;
        if options.root_color || options.depth {
            requests.push(NativeLayerTargetRequest {
                target: NativeLayerTargetId::Root,
                size: [frame.width, frame.height],
                color: options.root_color,
                effects: false,
                depth: options.depth,
                cache_identity: 0,
            });
            if options.depth {
                depth_bytes = texture_bytes([frame.width, frame.height], 1)?;
            }
        }
        for index in 0..layers.peak_materialized_depth as usize {
            let size = [layers.slot_widths[index], layers.slot_heights[index]];
            if size.contains(&0) {
                return Err(Error::InvalidInput);
            }
            requests.push(NativeLayerTargetRequest {
                target: NativeLayerTargetId::Slot(index as u32),
                size,
                color: true,
                effects: layers.slot_effected[index],
                depth: options.depth,
                cache_identity: 0,
            });
            if options.depth {
                depth_bytes = depth_bytes
                    .checked_add(texture_bytes(size, 1)?)
                    .ok_or(Error::Capacity)?;
            }
        }
        let mut cache_budget = caches.clone();
        let mut identities = previous_identities;
        let mut retained = [false; MAX_CACHED_LAYERS];
        for cache in 0..caches.count as usize {
            let identity = caches.identities[cache];
            if identity == 0 || caches.identities[..cache].contains(&identity) {
                return Err(Error::InvalidInput);
            }
            let existing = identities.iter().position(|&i| i == identity);
            let index = existing
                .or_else(|| {
                    (0..MAX_CACHED_LAYERS).find(|&i| {
                        !retained[i]
                            && !caches.identities[..caches.count as usize].contains(&identities[i])
                    })
                })
                .ok_or(Error::Capacity)?;
            identities[index] = identity;
            retained[index] = true;
            let slot = CACHED_LAYER_SLOT_BASE + index as u32;
            cache_budget.slots[cache] = slot;
            let size = [caches.widths[cache], caches.heights[cache]];
            if size.contains(&0) {
                return Err(Error::InvalidInput);
            }
            requests.push(NativeLayerTargetRequest {
                target: NativeLayerTargetId::Slot(slot),
                size,
                color: true,
                effects: caches.effected[cache],
                depth: options.depth,
                cache_identity: identity,
            });
            if options.depth {
                depth_bytes = depth_bytes
                    .checked_add(texture_bytes(size, 1)?)
                    .ok_or(Error::Capacity)?;
            }
        }
        for i in 0..MAX_CACHED_LAYERS {
            if !retained[i] {
                identities[i] = 0;
            }
        }
        Ok(Self {
            frame,
            requests: requests.into(),
            cache_budget,
            identities,
            source_texture_bytes,
            depth_bytes,
        })
    }
    pub fn frame(&self) -> NativeSceneFrame {
        self.frame
    }
    pub fn requests(&self) -> &[NativeLayerTargetRequest] {
        &self.requests
    }
    pub fn cache_budget(&self) -> &NativeCacheBudget {
        &self.cache_budget
    }
    pub fn cache_identities(&self) -> &[u64; MAX_CACHED_LAYERS] {
        &self.identities
    }
    pub fn source_texture_bytes(&self) -> u64 {
        self.source_texture_bytes
    }
    pub fn depth_budget_bytes(&self) -> u64 {
        self.depth_bytes
    }
}
fn texture_bytes(size: [u32; 2], count: u64) -> Result<u64, Error> {
    u64::from(size[0])
        .checked_mul(u64::from(size[1]))
        .and_then(|v| v.checked_mul(4))
        .and_then(|v| v.checked_mul(count))
        .ok_or(Error::Capacity)
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLayerResourceMetrics {
    pub color_allocations: u64,
    pub depth_allocations: u64,
    pub effect_allocations: u64,
    pub color_bytes: u64,
    pub depth_bytes: u64,
    pub effect_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub uniform_allocations: u64,
    pub bind_groups: u64,
}
