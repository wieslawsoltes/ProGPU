use super::*;
pub(super) struct Entry {
    plan: Arc<NativeSceneResourcePlan>,
    external: NativeExternalImages,
    prepared: Option<Arc<PreparedNativeScenePages>>,
    identity: NativeReplayCacheIdentity,
}
impl NativeSceneCache {
    /// Whole-plan reuse is intentionally tied to the same immutable scene owner.
    /// Original cross-generation family identities are exposed separately.
    pub fn resource_plan(
        &self,
        scene: &Arc<NativeScene>,
        frame: NativeSceneFrame,
        options: NativeSceneCompileOptions,
        external: &NativeExternalImages,
    ) -> Option<Arc<NativeSceneResourcePlan>> {
        if self.lost || external.device() != &self.device || !self.external.same_bindings(external)
        {
            return None;
        }
        let entry = self.prepared.as_ref()?;
        (Arc::ptr_eq(scene, entry.plan.scene())
            && equal_frame(frame, entry.plan.frame())
            && equal_options(options, entry.plan.options())
            && entry.external.same_bindings(external))
        .then(|| entry.plan.clone())
    }
    pub fn store_resource_plan(
        &mut self,
        plan: Arc<NativeSceneResourcePlan>,
        external: &NativeExternalImages,
    ) -> Result<(), Error> {
        self.admit_plan(&plan, external)?;
        if self.prepared.as_ref().is_some_and(|entry| {
            Arc::ptr_eq(&entry.plan, &plan) && entry.external.same_bindings(external)
        }) {
            return Ok(());
        }
        self.prepared = Some(Entry {
            identity: NativeReplayCacheIdentity::from_plan(&plan),
            plan,
            external: external.clone(),
            prepared: None,
        });
        Ok(())
    }
    /// Hashing the immutable stream occurs once when installing the plan, not
    /// once for every depth-indexed output-cache lookup or warm replay.
    pub fn replay_identity(
        &self,
        plan: &Arc<NativeSceneResourcePlan>,
    ) -> Option<NativeReplayCacheIdentity> {
        let entry = self.prepared.as_ref()?;
        (!self.lost && Arc::ptr_eq(&entry.plan, plan)).then_some(entry.identity)
    }
    /// Only a generation previously published after successful submission can
    /// supply a warm page hit. Its current external views must still match.
    pub fn prepared_pages(
        &self,
        plan: &Arc<NativeSceneResourcePlan>,
        external: &NativeExternalImages,
    ) -> Option<Arc<PreparedNativeScenePages>> {
        if self.lost || !self.external.same_bindings(external) {
            return None;
        }
        let entry = self.prepared.as_ref()?;
        if !Arc::ptr_eq(&entry.plan, plan) || !entry.external.same_bindings(external) {
            return None;
        }
        entry.prepared.clone()
    }
    /// Call after the corresponding coverage commands have actually submitted.
    /// Later replay still encodes live image-source effects in original order;
    /// a view identity proves retention, never immutable external texture pixels.
    pub fn commit_prepared_submitted(
        &mut self,
        pages: Arc<PreparedNativeScenePages>,
        queue: &wgpu::Queue,
    ) -> Result<(), Error> {
        let plan = pages.source().source();
        self.admit_plan(plan, pages.external_images())?;
        if pages.material().device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        let entry = self.prepared.as_mut().ok_or(Error::ResourceDomain)?;
        if !Arc::ptr_eq(&entry.plan, plan) || !entry.external.same_bindings(pages.external_images())
        {
            return Err(Error::ResourceDomain);
        }
        pages.retain_submitted(queue);
        entry.prepared = Some(pages);
        Ok(())
    }
    fn admit_plan(
        &self,
        plan: &NativeSceneResourcePlan,
        external: &NativeExternalImages,
    ) -> Result<(), Error> {
        if self.lost
            || plan.options().resource_scope != self.resource_scope
            || external.device() != &self.device
            || !self.external.same_bindings(external)
            || plan.draws().external_images() != external.metadata()
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
}
// Whole-plan reuse additionally retains all independently consumed source fields,
// including unused supplied prefixes and signed zero. It never manufactures a
// canonicalized frame that the original caller did not provide.
fn equal_frame(a: NativeSceneFrame, b: NativeSceneFrame) -> bool {
    a.prefix == b.prefix
        && a.width == b.width
        && a.height == b.height
        && a.dpi_scale.to_bits() == b.dpi_scale.to_bits()
        && a.scene_id == b.scene_id
        && a.generation == b.generation
        && a.flags == b.flags
        && [
            a.clear_color.r,
            a.clear_color.g,
            a.clear_color.b,
            a.clear_color.a,
            a.damage.x,
            a.damage.y,
            a.damage.width,
            a.damage.height,
        ]
        .map(f32::to_bits)
            == [
                b.clear_color.r,
                b.clear_color.g,
                b.clear_color.b,
                b.clear_color.a,
                b.damage.x,
                b.damage.y,
                b.damage.width,
                b.damage.height,
            ]
            .map(f32::to_bits)
        && a.presentation.struct_size == b.presentation.struct_size
        && a.presentation.reserved == b.presentation.reserved
        && [
            a.presentation.viewport_x,
            a.presentation.viewport_y,
            a.presentation.viewport_width,
            a.presentation.viewport_height,
            a.presentation.dpi_scale_x.to_bits(),
            a.presentation.dpi_scale_y.to_bits(),
        ] == [
            b.presentation.viewport_x,
            b.presentation.viewport_y,
            b.presentation.viewport_width,
            b.presentation.viewport_height,
            b.presentation.dpi_scale_x.to_bits(),
            b.presentation.dpi_scale_y.to_bits(),
        ]
}
fn equal_options(a: NativeSceneCompileOptions, b: NativeSceneCompileOptions) -> bool {
    a.format == b.format
        && a.engine_flags == b.engine_flags
        && a.resource_scope == b.resource_scope
        && a.borrows_shared_vector_pipeline == b.borrows_shared_vector_pipeline
        && a.maximum_buffer_bytes == b.maximum_buffer_bytes
        && a.maximum_storage_bytes == b.maximum_storage_bytes
        && a.antialias_padding_pixels.to_bits() == b.antialias_padding_pixels.to_bits()
}
