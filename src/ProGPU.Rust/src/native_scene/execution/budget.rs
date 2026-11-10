//! Original retained-allocation ownership and bounded compilation/cache budgets.
use super::*;
use std::sync::atomic::{AtomicU64, Ordering};
pub const MAX_DRAW_PASSES: u32 = 16 * 1024;
pub const MAX_EFFECT_PASSES: u32 = 16 * 1024;
pub const MAX_CACHED_LAYERS: usize = SCENE_MAX_MATERIALIZED_LAYERS as usize;
pub const CACHED_LAYER_SLOT_BASE: u32 = SCENE_MAX_MATERIALIZED_LAYERS;
pub const LAYER_SLOT_COUNT: u32 = SCENE_MAX_MATERIALIZED_LAYERS * 2;
pub const EFFECT_UNIFORM_ALIGNMENT: u32 = 256;
pub const MAX_VERTEX_BYTES: u64 = 256 * 1024 * 1024;
pub const MAX_INDEX_BYTES: u64 = 64 * 1024 * 1024;
pub const MAX_TEXTURE_BYTES: u64 = 256 * 1024 * 1024;
pub const MAX_COVERAGE_BYTES: u64 = 256 * 1024 * 1024;
pub const MAX_TOTAL_COMPILED_BYTES: u64 = 512 * 1024 * 1024;

/// Charges belong to actual live allocation owners; cache eviction alone never
/// releases bytes still retained by a prepared draw or a queue submission.
#[derive(Debug)]
pub struct NativeResidencyBudget {
    limit: u64,
    bytes: AtomicU64,
}
impl NativeResidencyBudget {
    pub fn new(limit: u64) -> Self {
        Self {
            limit,
            bytes: AtomicU64::new(0),
        }
    }
    pub fn bytes(&self) -> u64 {
        self.bytes.load(Ordering::Relaxed)
    }
    pub fn limit(&self) -> u64 {
        self.limit
    }
    pub fn try_acquire(self: &Arc<Self>, count: u64) -> Option<NativeResidencyReservation> {
        let mut current = self.bytes.load(Ordering::Relaxed);
        loop {
            if count > self.limit || current > self.limit - count {
                return None;
            }
            match self.bytes.compare_exchange_weak(
                current,
                current + count,
                Ordering::Relaxed,
                Ordering::Relaxed,
            ) {
                Ok(_) => {
                    return Some(NativeResidencyReservation {
                        owner: Some(self.clone()),
                        bytes: count,
                    });
                }
                Err(actual) => current = actual,
            }
        }
    }
}
#[derive(Debug, Default)]
pub struct NativeResidencyReservation {
    owner: Option<Arc<NativeResidencyBudget>>,
    bytes: u64,
}
impl NativeResidencyReservation {
    pub fn is_acquired(&self) -> bool {
        self.owner.is_some()
    }
    pub fn bytes(&self) -> u64 {
        self.bytes
    }
    pub fn reset(&mut self) {
        if let Some(owner) = self.owner.take() {
            owner.bytes.fetch_sub(self.bytes, Ordering::Relaxed);
        }
        self.bytes = 0;
    }
}
impl Drop for NativeResidencyReservation {
    fn drop(&mut self) {
        self.reset();
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeCompilationBudget {
    pub draw_passes: u32,
    pub vertex_bytes: u64,
    pub index_bytes: u64,
    pub texture_bytes: u64,
    pub coverage_bytes: u64,
}
impl NativeCompilationBudget {
    pub fn add(&mut self, vertices: u64, indices: u64, textures: u64, coverage: u64) -> bool {
        let add = |current: u64, value: u64, limit: u64| {
            if value > limit || current > limit - value {
                None
            } else {
                Some(current + value)
            }
        };
        if self.draw_passes == MAX_DRAW_PASSES {
            return false;
        }
        let Some(v) = add(self.vertex_bytes, vertices, MAX_VERTEX_BYTES) else {
            return false;
        };
        let Some(i) = add(self.index_bytes, indices, MAX_INDEX_BYTES) else {
            return false;
        };
        let Some(t) = add(self.texture_bytes, textures, MAX_TEXTURE_BYTES) else {
            return false;
        };
        let Some(c) = add(self.coverage_bytes, coverage, MAX_COVERAGE_BYTES) else {
            return false;
        };
        if v + i + t + c > MAX_TOTAL_COMPILED_BYTES {
            return false;
        }
        self.draw_passes = self.draw_passes.wrapping_add(1);
        self.vertex_bytes = v;
        self.index_bytes = i;
        self.texture_bytes = t;
        self.coverage_bytes = c;
        true
    }
    pub fn total_bytes(&self) -> u64 {
        self.vertex_bytes
            .wrapping_add(self.index_bytes)
            .wrapping_add(self.texture_bytes)
            .wrapping_add(self.coverage_bytes)
    }
}
#[derive(Clone, Debug)]
pub struct NativeLayerBudget {
    pub live_bytes: [u64; SCENE_MAX_STACK_DEPTH as usize],
    pub live_materialized: [bool; SCENE_MAX_STACK_DEPTH as usize],
    pub scope_depth: u32,
    pub materialized_depth: u32,
    pub peak_materialized_depth: u32,
    pub current_bytes: u64,
    pub peak_bytes: u64,
    pub slot_widths: [u32; SCENE_MAX_MATERIALIZED_LAYERS as usize],
    pub slot_heights: [u32; SCENE_MAX_MATERIALIZED_LAYERS as usize],
    pub slot_effected: [bool; SCENE_MAX_MATERIALIZED_LAYERS as usize],
}
impl Default for NativeLayerBudget {
    fn default() -> Self {
        Self {
            live_bytes: [0; SCENE_MAX_STACK_DEPTH as usize],
            live_materialized: [false; SCENE_MAX_STACK_DEPTH as usize],
            scope_depth: 0,
            materialized_depth: 0,
            peak_materialized_depth: 0,
            current_bytes: 0,
            peak_bytes: 0,
            slot_widths: [0; SCENE_MAX_MATERIALIZED_LAYERS as usize],
            slot_heights: [0; SCENE_MAX_MATERIALIZED_LAYERS as usize],
            slot_effected: [false; SCENE_MAX_MATERIALIZED_LAYERS as usize],
        }
    }
}
impl NativeLayerBudget {
    /// Like the source budget, a pooled-limit failure retains the candidate's
    /// accounting; the caller discards the complete failed preparation budget.
    pub fn push(&mut self, extent: NativeScissor, materialized: bool, effected: bool) -> bool {
        let width = if materialized {
            u64::from(extent.width.max(1))
        } else {
            0
        };
        let height = if materialized {
            u64::from(extent.height.max(1))
        } else {
            0
        };
        let Some(bytes) = width.checked_mul(height).and_then(|v| v.checked_mul(4)) else {
            return false;
        };
        if self.scope_depth >= SCENE_MAX_STACK_DEPTH
            || materialized && self.materialized_depth >= SCENE_MAX_MATERIALIZED_LAYERS
            || materialized
                && (bytes > u64::from(SCENE_MAX_LAYER_BYTES)
                    || self.current_bytes > u64::from(SCENE_MAX_LAYER_BYTES) - bytes)
        {
            return false;
        }
        if materialized {
            let slot = self.materialized_depth as usize;
            self.slot_widths[slot] = self.slot_widths[slot].max(width as u32);
            self.slot_heights[slot] = self.slot_heights[slot].max(height as u32);
            self.slot_effected[slot] |= effected;
        }
        let scope = self.scope_depth as usize;
        self.live_bytes[scope] = if materialized { bytes } else { 0 };
        self.live_materialized[scope] = materialized;
        self.scope_depth += 1;
        self.materialized_depth += u32::from(materialized);
        self.peak_materialized_depth = self.peak_materialized_depth.max(self.materialized_depth);
        self.current_bytes += if materialized { bytes } else { 0 };
        self.peak_bytes = self.peak_bytes.max(self.current_bytes);
        self.pooled_bytes() <= u64::from(SCENE_MAX_LAYER_BYTES)
    }
    pub fn pop(&mut self) -> bool {
        if self.scope_depth == 0 || self.scope_depth > SCENE_MAX_STACK_DEPTH {
            return false;
        }
        let scope = self.scope_depth as usize - 1;
        let materialized = u32::from(self.live_materialized[scope]);
        if self.current_bytes < self.live_bytes[scope] || self.materialized_depth < materialized {
            return false;
        }
        self.scope_depth -= 1;
        self.current_bytes -= self.live_bytes[scope];
        self.materialized_depth -= materialized;
        true
    }
    pub fn pooled_bytes(&self) -> u64 {
        pooled(
            &self.slot_widths,
            &self.slot_heights,
            &self.slot_effected,
            self.peak_materialized_depth,
            4,
            false,
        )
    }
    pub fn pooled_effect_bytes(&self) -> u64 {
        pooled(
            &self.slot_widths,
            &self.slot_heights,
            &self.slot_effected,
            self.peak_materialized_depth,
            12,
            true,
        )
    }
    pub fn maximum_width(&self) -> u32 {
        self.slot_widths.into_iter().max().unwrap_or(0)
    }
    pub fn maximum_height(&self) -> u32 {
        self.slot_heights.into_iter().max().unwrap_or(0)
    }
}
#[derive(Clone, Debug, Default)]
pub struct NativeCacheBudget {
    pub identities: [u64; MAX_CACHED_LAYERS],
    pub revisions: [u64; MAX_CACHED_LAYERS],
    pub shared: [bool; MAX_CACHED_LAYERS],
    pub widths: [u32; MAX_CACHED_LAYERS],
    pub heights: [u32; MAX_CACHED_LAYERS],
    pub effected: [bool; MAX_CACHED_LAYERS],
    pub slots: [u32; MAX_CACHED_LAYERS],
    pub count: u32,
}
impl NativeCacheBudget {
    pub fn add(
        &mut self,
        identity: u64,
        extent: NativeScissor,
        has_effect: bool,
        allow_shared: bool,
        revision: u64,
    ) -> bool {
        if allow_shared && (revision == 0 || has_effect || extent.x != 0 || extent.y != 0) {
            return false;
        }
        if self.count as usize > MAX_CACHED_LAYERS {
            return false;
        }
        for i in 0..self.count as usize {
            if self.identities[i] == identity {
                return allow_shared
                    && self.shared[i]
                    && self.revisions[i] == revision
                    && self.widths[i] == extent.width.max(1)
                    && self.heights[i] == extent.height.max(1)
                    && !self.effected[i];
            }
        }
        if identity == 0 || self.count as usize == MAX_CACHED_LAYERS {
            return false;
        }
        let i = self.count as usize;
        self.identities[i] = identity;
        self.revisions[i] = revision;
        self.shared[i] = allow_shared;
        self.widths[i] = extent.width.max(1);
        self.heights[i] = extent.height.max(1);
        self.effected[i] = has_effect;
        self.count += 1;
        self.pooled_bytes() <= u64::from(SCENE_MAX_LAYER_BYTES)
    }
    pub fn pooled_bytes(&self) -> u64 {
        pooled(
            &self.widths,
            &self.heights,
            &self.effected,
            self.count,
            4,
            false,
        )
    }
    pub fn pooled_effect_bytes(&self) -> u64 {
        pooled(
            &self.widths,
            &self.heights,
            &self.effected,
            self.count,
            12,
            true,
        )
    }
    pub fn maximum_width(&self) -> u32 {
        self.widths.into_iter().max().unwrap_or(0)
    }
    pub fn maximum_height(&self) -> u32 {
        self.heights.into_iter().max().unwrap_or(0)
    }
}
fn pooled(
    widths: &[u32],
    heights: &[u32],
    effected: &[bool],
    count: u32,
    bpp: u64,
    only_effected: bool,
) -> u64 {
    let count = count as usize;
    if count > widths.len() || count > heights.len() || count > effected.len() {
        return u64::MAX;
    }
    let mut result = 0u64;
    for i in 0..count {
        if only_effected && !effected[i] {
            continue;
        }
        let Some(bytes) = u64::from(widths[i])
            .checked_mul(u64::from(heights[i]))
            .and_then(|v| v.checked_mul(bpp))
        else {
            return u64::MAX;
        };
        let Some(total) = result.checked_add(bytes) else {
            return u64::MAX;
        };
        result = total;
    }
    result
}
