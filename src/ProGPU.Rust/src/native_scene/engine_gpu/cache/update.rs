use super::*;
#[derive(Clone, Debug)]
pub struct NativeSceneSnapshot {
    scene: Arc<NativeScene>,
    raw_hash: u64,
    hashes: NativeSceneContentHashes,
    scope: u64,
}
impl NativeSceneSnapshot {
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn raw_hash(&self) -> u64 {
        self.raw_hash
    }
    pub fn content_hashes(&self) -> NativeSceneContentHashes {
        self.hashes
    }
    pub fn resource_scope(&self) -> u64 {
        self.scope
    }
}
#[derive(Clone, Copy, Debug, PartialEq, thiserror::Error)]
#[error(
    "native scene update status {status}, validation {validation_error} at byte {error_offset}"
)]
pub struct NativeSceneUpdateError {
    pub status: u32,
    pub validation_error: u32,
    pub error_offset: u32,
    metrics: wire::Metrics,
}
impl NativeSceneUpdateError {
    pub fn metrics(&self) -> wire::Metrics {
        self.metrics
    }
    pub fn write_metrics(&self, destination: &mut wire::Metrics) {
        publish(destination, self.metrics);
    }
    fn admission(error: NativeSceneAdmissionError) -> Self {
        let metrics = error.metrics();
        Self {
            status: error.status(),
            validation_error: metrics.validation_error,
            error_offset: metrics.error_offset,
            metrics,
        }
    }
    fn generation(scene: &NativeScene, offset: u32) -> Self {
        // Original update first writes complete successful scene metrics, then
        // changes only the generation diagnostic fields on a rejected update.
        let mut metrics = scene.metrics();
        metrics.validation_error = NativeSceneValidationError::Generation as u32;
        metrics.error_offset = offset;
        Self {
            status: STATUS_INVALID_ARGUMENT,
            validation_error: metrics.validation_error,
            error_offset: offset,
            metrics,
        }
    }
}
/// Prepared before replacing the selected immutable CPU source generation.
pub struct NativeSceneUpdateCandidate {
    owner: Arc<()>,
    epoch: u64,
    snapshot: Arc<NativeSceneSnapshot>,
    reused: bool,
    metrics: wire::Metrics,
}
impl NativeSceneUpdateCandidate {
    pub fn snapshot(&self) -> &Arc<NativeSceneSnapshot> {
        &self.snapshot
    }
    pub fn snapshot_reused(&self) -> bool {
        self.reused
    }
    pub fn metrics(&self) -> wire::Metrics {
        self.metrics
    }
    pub fn write_metrics(&self, destination: &mut wire::Metrics) {
        publish(destination, self.metrics);
    }
}
/// Exact source-owned table candidate. No callback runs under cache mutation.
pub struct NativeExternalUpdateCandidate {
    owner: Arc<()>,
    epoch: u64,
    external: NativeExternalImages,
    identical: bool,
}
impl NativeExternalUpdateCandidate {
    pub fn external_images(&self) -> &NativeExternalImages {
        &self.external
    }
    pub fn identical(&self) -> bool {
        self.identical
    }
}
impl NativeSceneCache {
    /// Exact-byte equality precedes parsing and generation admission, as in the
    /// original update entry point. The caller supplies immutable owned bytes.
    pub fn prepare_scene(
        &self,
        bytes: Arc<[u8]>,
    ) -> Result<NativeSceneUpdateCandidate, NativeSceneUpdateError> {
        if let Some(prior) = &self.snapshot {
            if scene_bytes_equal(prior.scene.envelope().bytes(), &bytes) {
                return Ok(self.reused(prior.clone()));
            }
        }
        let scene = Arc::new(NativeScene::new(bytes).map_err(NativeSceneUpdateError::admission)?);
        self.prepare_admitted_scene(scene)
    }
    /// Same contract when the caller already owns full native semantic admission.
    pub fn prepare_admitted_scene(
        &self,
        scene: Arc<NativeScene>,
    ) -> Result<NativeSceneUpdateCandidate, NativeSceneUpdateError> {
        if let Some(prior) = &self.snapshot {
            if scene_bytes_equal(prior.scene.envelope().bytes(), scene.envelope().bytes()) {
                return Ok(self.reused(prior.clone()));
            }
            let incoming = scene.envelope().header();
            let previous = prior.scene.envelope().header();
            if incoming.scene_id == previous.scene_id {
                if incoming.generation <= previous.generation {
                    return Err(NativeSceneUpdateError::generation(&scene, 0));
                }
                scene
                    .admit_resource_generations_after(&prior.scene)
                    .map_err(|error| NativeSceneUpdateError::generation(&scene, error.offset))?;
            }
        }
        let metrics = scene.metrics();
        let snapshot = Arc::new(NativeSceneSnapshot {
            raw_hash: raw_fnv1a64(scene.envelope().bytes()),
            hashes: scene.content_hashes(self.resource_scope),
            scope: self.resource_scope,
            scene,
        });
        Ok(NativeSceneUpdateCandidate {
            owner: self.owner.clone(),
            epoch: self.epoch,
            snapshot,
            reused: false,
            metrics,
        })
    }
    fn reused(&self, snapshot: Arc<NativeSceneSnapshot>) -> NativeSceneUpdateCandidate {
        let mut metrics = snapshot.scene.metrics();
        metrics.flags |= SCENE_METRICS_SNAPSHOT_REUSED;
        NativeSceneUpdateCandidate {
            owner: self.owner.clone(),
            epoch: self.epoch,
            snapshot,
            reused: true,
            metrics,
        }
    }
    pub fn commit_scene(
        &mut self,
        candidate: NativeSceneUpdateCandidate,
    ) -> Result<NativeCacheInvalidation, Error> {
        self.admit_candidate(&candidate.owner, candidate.epoch)?;
        if candidate.reused {
            return Ok(NativeCacheInvalidation::default());
        }
        let epoch = self.next_epoch()?;
        self.snapshot = Some(candidate.snapshot);
        self.epoch = epoch;
        // Retain the last actual prepared generation; exact source-owner lookup
        // no longer selects it. This matches family caches surviving a CPU update.
        Ok(NativeCacheInvalidation {
            render_bundles: !self.lost,
            ..Default::default()
        })
    }
    pub fn prepare_external(
        &self,
        external: NativeExternalImages,
    ) -> Result<NativeExternalUpdateCandidate, Error> {
        if self.lost || external.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        // NativeExternalImages already validates the complete ordered role/ID,
        // dimensions and generation inventory and retains every actual view.
        let identical = self.external.same_bindings(&external);
        Ok(NativeExternalUpdateCandidate {
            owner: self.owner.clone(),
            epoch: self.epoch,
            external,
            identical,
        })
    }
    pub fn commit_external(
        &mut self,
        candidate: NativeExternalUpdateCandidate,
    ) -> Result<NativeCacheInvalidation, Error> {
        self.admit_candidate(&candidate.owner, candidate.epoch)?;
        if self.lost {
            return Err(Error::ResourceDomain);
        }
        if candidate.identical {
            return Ok(NativeCacheInvalidation::default());
        }
        let epoch = self.next_epoch()?;
        self.prepared = None;
        self.external = candidate.external;
        self.epoch = epoch;
        Ok(NativeCacheInvalidation {
            render_bundles: true,
            glyph_paints: true,
            images: true,
            three_d: true,
        })
    }
}
fn publish(destination: &mut wire::Metrics, mut metrics: wire::Metrics) {
    if destination.struct_size >= wire::Metrics::WIRE_SIZE as u32 {
        metrics.struct_size = destination.struct_size;
        *destination = metrics;
    }
}
