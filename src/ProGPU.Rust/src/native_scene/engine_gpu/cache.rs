//! Original immutable scene updates, independent family identities and actual
//! prepared-page leases. Retained pixels are committed by the layer pool only.
use super::*;
use crate::native_scene::{
    NativeScene, NativeSceneAdmissionError, NativeSceneContentHashes, NativeSceneValidationError,
    engine_resources::{
        NativeExternalImages,
        layers::{NativeLayerOutputKey, NativeLayerTarget, NativeLayerTargetId},
    },
    execution::{
        NativeReplayLayerPop, NativeReplayLayerPush, NativeSceneCompileOptions, NativeSceneFrame,
        NativeSceneResourcePlan,
    },
    identity::{presentation_content_hash, raw_fnv1a64, scene_bytes_equal},
};
mod identity;
mod output;
mod prepared;
mod update;
pub use identity::{NativeBundleCacheIdentity, NativePageCacheIdentity, NativeReplayCacheIdentity};
pub use output::NativeLayerCacheSelection;
pub use update::{
    NativeExternalUpdateCandidate, NativeSceneSnapshot, NativeSceneUpdateCandidate,
    NativeSceneUpdateError,
};

/// Exact original release families. An update never destroys leased objects;
/// outer cache entries retire their own references at these boundaries.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeCacheInvalidation {
    pub render_bundles: bool,
    pub glyph_paints: bool,
    pub images: bool,
    pub three_d: bool,
}
/// One actual device/resource scope. CPU snapshots may still change after loss,
/// while all old GPU owners remain retained until terminal cache destruction.
pub struct NativeSceneCache {
    device: wgpu::Device,
    resource_scope: u64,
    owner: Arc<()>,
    epoch: u64,
    lost: bool,
    snapshot: Option<Arc<NativeSceneSnapshot>>,
    external: NativeExternalImages,
    prepared: Option<prepared::Entry>,
}
impl NativeSceneCache {
    pub fn new(
        device: &wgpu::Device,
        resource_scope: u64,
        external: NativeExternalImages,
    ) -> Result<Self, Error> {
        if external.device() != device {
            return Err(Error::ResourceDomain);
        }
        Ok(Self {
            device: device.clone(),
            resource_scope,
            owner: Arc::new(()),
            epoch: 0,
            lost: false,
            snapshot: None,
            external,
            prepared: None,
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn resource_scope(&self) -> u64 {
        self.resource_scope
    }
    pub fn snapshot(&self) -> Option<&Arc<NativeSceneSnapshot>> {
        self.snapshot.as_ref()
    }
    pub fn external_images(&self) -> &NativeExternalImages {
        &self.external
    }
    pub fn is_device_lost(&self) -> bool {
        self.lost
    }
    /// Terminal loss does not release GPU owners from a later CPU scene update.
    pub fn mark_device_lost(&mut self) {
        self.lost = true;
    }
    fn next_epoch(&self) -> Result<u64, Error> {
        self.epoch.checked_add(1).ok_or(Error::Capacity)
    }
    fn admit_candidate(&self, owner: &Arc<()>, epoch: u64) -> Result<(), Error> {
        if !Arc::ptr_eq(&self.owner, owner) || epoch != self.epoch {
            Err(Error::ResourceDomain)
        } else {
            Ok(())
        }
    }
}
