//! Retained native physical target leases. Slots replace immutable owners; old
//! prepared generations and queue submissions keep their actual resources alive.
use super::*;
use crate::native_scene::{
    engine_gpu::{
        NativeFrameBindings, NativeGpuUniforms, NativePipelineCache, PreparedNativeMaterials,
    },
    execution::budget::{NativeResidencyBudget, NativeResidencyReservation},
};
#[derive(Clone, Debug)]
pub struct NativeLayerTexture(Arc<TextureOwner>);
#[derive(Debug)]
struct TextureOwner {
    device: wgpu::Device,
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    linear: wgpu::BindGroup,
    nearest: Option<wgpu::BindGroup>,
    size: [u32; 2],
    generation: u32,
    _reservation: NativeResidencyReservation,
}
impl NativeLayerTexture {
    pub fn device(&self) -> &wgpu::Device {
        &self.0.device
    }
    pub fn texture(&self) -> &wgpu::Texture {
        &self.0.texture
    }
    pub fn view(&self) -> &wgpu::TextureView {
        &self.0.view
    }
    pub fn linear_binding(&self) -> &wgpu::BindGroup {
        &self.0.linear
    }
    pub fn nearest_binding(&self) -> Option<&wgpu::BindGroup> {
        self.0.nearest.as_ref()
    }
    pub fn size(&self) -> [u32; 2] {
        self.0.size
    }
    pub fn generation(&self) -> u32 {
        self.0.generation
    }
    /// Retains the actual renderable storage lease. This does not freeze its
    /// pixels; a stable pixel snapshot requires an explicit source copy.
    pub fn as_image(&self) -> Result<NativeImageTexture, Error> {
        NativeImageTexture::from_view(
            self.device(),
            self.texture(),
            self.view(),
            self.size(),
            Some(Arc::new(self.clone())),
        )
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
#[derive(Clone)]
pub struct NativeLayerEffects {
    textures: [NativeLayerTexture; 3],
    generation: u32,
}
impl NativeLayerEffects {
    pub fn textures(&self) -> &[NativeLayerTexture; 3] {
        &self.textures
    }
    pub fn generation(&self) -> u32 {
        self.generation
    }
    pub fn size(&self) -> [u32; 2] {
        self.textures[0].size()
    }
}
#[derive(Debug)]
struct DepthOwner {
    target: super::super::three_d::NativeThreeDDepthTarget,
    _reservation: NativeResidencyReservation,
}
#[derive(Clone, Debug)]
pub struct NativeLayerDepthTarget(Arc<DepthOwner>);
impl NativeLayerDepthTarget {
    pub fn size(&self) -> [u32; 2] {
        self.0.target.size()
    }
    pub fn view(&self) -> &wgpu::TextureView {
        self.0.target.view()
    }
    pub fn texture(&self) -> &wgpu::Texture {
        self.0.target.texture()
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
/// Frame bindings use the actual allocation extent. The logical content's
/// source origin and clipping remain independently retained command metadata.
#[derive(Clone)]
pub struct NativeLayerTarget {
    request: NativeLayerTargetRequest,
    color: Option<NativeLayerTexture>,
    effects: Option<NativeLayerEffects>,
    depth: Option<NativeLayerDepthTarget>,
    frame: Option<Arc<NativeFrameBindings>>,
}
impl NativeLayerTarget {
    pub fn request(&self) -> NativeLayerTargetRequest {
        self.request
    }
    pub fn color(&self) -> Option<&NativeLayerTexture> {
        self.color.as_ref()
    }
    pub fn effects(&self) -> Option<&NativeLayerEffects> {
        self.effects.as_ref()
    }
    pub fn depth(&self) -> Option<&NativeLayerDepthTarget> {
        self.depth.as_ref()
    }
    pub fn frame(&self) -> Option<&Arc<NativeFrameBindings>> {
        self.frame.as_ref()
    }
    pub fn effect_source(&self, index: i32) -> Option<&wgpu::TextureView> {
        if index < 0 {
            self.color.as_ref().map(NativeLayerTexture::view)
        } else {
            self.effects
                .as_ref()?
                .textures
                .get(index as usize)
                .map(NativeLayerTexture::view)
        }
    }
    pub fn output_key(
        &self,
        scene_hash: u64,
        operation_id: u64,
        effected: bool,
    ) -> Result<NativeLayerOutputKey, Error> {
        let (generation, size) = if effected {
            let e = self.effects.as_ref().ok_or(Error::ResourceDomain)?;
            (e.generation(), e.size())
        } else {
            let c = self.color.as_ref().ok_or(Error::ResourceDomain)?;
            (c.generation(), c.size())
        };
        Ok(NativeLayerOutputKey {
            scene_hash,
            operation_id,
            texture_generation: generation,
            width: size[0],
            height: size[1],
        })
    }
}
#[derive(Clone)]
struct Samplers {
    linear: wgpu::Sampler,
    nearest: wgpu::Sampler,
}
impl Samplers {
    fn new(device: &wgpu::Device) -> Self {
        let make = |filter| {
            device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("Original native layer clamp level-zero sampler"),
                address_mode_u: wgpu::AddressMode::ClampToEdge,
                address_mode_v: wgpu::AddressMode::ClampToEdge,
                address_mode_w: wgpu::AddressMode::ClampToEdge,
                mag_filter: filter,
                min_filter: filter,
                mipmap_filter: wgpu::MipmapFilterMode::Nearest,
                lod_min_clamp: 0.0,
                lod_max_clamp: 0.0,
                anisotropy_clamp: 1,
                ..Default::default()
            })
        };
        Self {
            linear: make(wgpu::FilterMode::Linear),
            nearest: make(wgpu::FilterMode::Nearest),
        }
    }
}
/// Texture residency is charged to the actual allocation owner, including old
/// generations retained outside this pool. Frame/material buffers have their
/// independent native buffer budgets.
pub struct NativeLayerTargetPool {
    owner: Arc<()>,
    epoch: u64,
    device: wgpu::Device,
    format: wgpu::TextureFormat,
    residency: Arc<NativeResidencyBudget>,
    samplers: Option<Samplers>,
    root: Option<Arc<NativeLayerTarget>>,
    slots: Vec<Option<Arc<NativeLayerTarget>>>,
    identities: [u64; MAX_CACHED_LAYERS],
    caches: [NativeLayerOutputCache; LAYER_SLOT_COUNT as usize],
    composite_frame: Option<Arc<NativeFrameBindings>>,
}
#[derive(Clone)]
pub struct PreparedNativeLayerTargets {
    owner: Arc<()>,
    epoch: u64,
    device: wgpu::Device,
    format: wgpu::TextureFormat,
    plan: Arc<NativeLayerTargetPlan>,
    root: Option<Arc<NativeLayerTarget>>,
    slots: Vec<Option<Arc<NativeLayerTarget>>>,
    composite_frame: Option<Arc<NativeFrameBindings>>,
    metrics: NativeLayerResourceMetrics,
}
pub struct NativeLayerOutputUpdates {
    owner: Arc<()>,
    epoch: u64,
    caches: [NativeLayerOutputCache; LAYER_SLOT_COUNT as usize],
    updated: [bool; LAYER_SLOT_COUNT as usize],
}
impl NativeLayerTargetPool {
    pub fn new(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        residency: Arc<NativeResidencyBudget>,
    ) -> Result<Self, Error> {
        let mut slots = allocate(LAYER_SLOT_COUNT as usize)?;
        slots.resize_with(LAYER_SLOT_COUNT as usize, || None);
        Ok(Self {
            owner: Arc::new(()),
            epoch: 0,
            device: device.clone(),
            format,
            residency,
            samplers: None,
            root: None,
            slots,
            identities: [0; MAX_CACHED_LAYERS],
            caches: Default::default(),
            composite_frame: None,
        })
    }
    /// Private candidate for a connected resource transaction. Actual owners and
    /// pool identity are retained; publication remains the caller's final step.
    pub(crate) fn fork_candidate(&self) -> Self {
        Self {
            owner: self.owner.clone(), epoch: self.epoch, device: self.device.clone(),
            format: self.format, residency: self.residency.clone(), samplers: self.samplers.clone(),
            root: self.root.clone(), slots: self.slots.clone(), identities: self.identities,
            caches: self.caches, composite_frame: self.composite_frame.clone(),
        }
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn format(&self) -> wgpu::TextureFormat {
        self.format
    }
    pub fn residency(&self) -> &Arc<NativeResidencyBudget> {
        &self.residency
    }
    pub fn cache_identities(&self) -> &[u64; MAX_CACHED_LAYERS] {
        &self.identities
    }
    pub fn plan(
        &self,
        frame: NativeSceneFrame,
        layers: &NativeLayerBudget,
        caches: &NativeCacheBudget,
        options: NativeLayerPoolOptions,
    ) -> Result<NativeLayerTargetPlan, Error> {
        NativeLayerTargetPlan::new(frame, layers, caches, self.identities, options)
    }
    pub fn clear(&mut self) {
        self.root = None;
        self.slots.fill(None);
        self.identities.fill(0);
        self.caches.fill(Default::default());
        self.composite_frame = None;
        self.epoch = self.epoch.wrapping_add(1);
    }
    pub fn preflight(
        &self,
        pipelines: &NativePipelineCache,
        material: &PreparedNativeMaterials,
        plan: &NativeLayerTargetPlan,
    ) -> Result<(), Error> {
        if pipelines.device() != &self.device
            || material.device() != &self.device
            || pipelines.target_format() != self.format
        {
            return Err(Error::ResourceDomain);
        }
        let limits = self.device.limits();
        for request in plan.requests() {
            if request.size.contains(&0)
                || request
                    .size
                    .into_iter()
                    .any(|n| n > limits.max_texture_dimension_2d)
            {
                return Err(Error::Capacity);
            }
            NativeGpuUniforms::new(request.size, plan.frame.dpi_scale)?;
        }
        NativeGpuUniforms::new([plan.frame.width, plan.frame.height], plan.frame.dpi_scale)?;
        if limits.max_uniform_buffer_binding_size < 224 || limits.max_buffer_size < 224 {
            return Err(Error::Capacity);
        }
        let features = self
            .format
            .guaranteed_format_features(self.device.features());
        let usage = wgpu::TextureUsages::RENDER_ATTACHMENT
            | wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::COPY_SRC
            | wgpu::TextureUsages::COPY_DST;
        if plan.requests().iter().any(|r| r.color)
            && (!features.allowed_usages.contains(usage)
                || !matches!(
                    self.format.sample_type(None, Some(self.device.features())),
                    Some(wgpu::TextureSampleType::Float { filterable: true })
                ))
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
    pub async fn prepare(
        &mut self,
        pipelines: &NativePipelineCache,
        material: Arc<PreparedNativeMaterials>,
        frame: NativeSceneFrame,
        layers: &NativeLayerBudget,
        caches: &NativeCacheBudget,
        options: NativeLayerPoolOptions,
    ) -> Result<PreparedNativeLayerTargets, Error> {
        let plan = Arc::new(self.plan(frame, layers, caches, options)?);
        self.preflight(pipelines, &material, &plan)?;
        let mut metrics = NativeLayerResourceMetrics::default();
        let mut slots = self.slots.clone();
        let mut root = self.root.clone();
        let mut output_caches = self.caches;
        for i in 0..MAX_CACHED_LAYERS {
            if self.identities[i] != plan.identities[i] {
                output_caches[CACHED_LAYER_SLOT_BASE as usize + i].invalidate();
            }
        }
        if plan.requests().is_empty() {
            self.epoch = self.epoch.wrapping_add(1);
            self.identities = plan.identities;
            self.caches = output_caches;
            return Ok(PreparedNativeLayerTargets {
                owner: self.owner.clone(),
                epoch: self.epoch,
                device: self.device.clone(),
                format: self.format,
                plan,
                root,
                slots,
                composite_frame: None,
                metrics,
            });
        }
        let mut reservations = self.reserve_allocations(&plan)?.into_iter();
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let needs_samplers = plan.requests().iter().any(|r| r.color || r.effects);
        let samplers = self
            .samplers
            .clone()
            .or_else(|| needs_samplers.then(|| Samplers::new(&self.device)));
        // Reservations are acquired before creating each allocation, owned by
        // its candidate, and automatically retired on every rejected candidate.
        let result: Result<(), Error> = async {
            for request in plan.requests() {
                let previous = match request.target {
                    NativeLayerTargetId::Root => root.as_ref(),
                    NativeLayerTargetId::Slot(i) => slots[i as usize].as_ref(),
                };
                let candidate = self
                    .prepare_target(
                        pipelines,
                        &material,
                        samplers.as_ref(),
                        *request,
                        previous,
                        frame.dpi_scale,
                        &mut metrics,
                        &mut reservations,
                    )
                    .await?;
                if let NativeLayerTargetId::Slot(i) = request.target {
                    let old = previous
                        .and_then(|s| s.color.as_ref())
                        .map(NativeLayerTexture::generation);
                    let new = candidate.color.as_ref().map(NativeLayerTexture::generation);
                    let old_effect = previous
                        .and_then(|s| s.effects.as_ref())
                        .map(NativeLayerEffects::generation);
                    let new_effect = candidate
                        .effects
                        .as_ref()
                        .map(NativeLayerEffects::generation);
                    if old != new || old_effect != new_effect {
                        output_caches[i as usize].invalidate();
                    }
                    slots[i as usize] = Some(Arc::new(candidate));
                } else {
                    root = Some(Arc::new(candidate));
                }
            }
            Ok(())
        }
        .await;
        let gpu_error = scope.pop().await;
        result?;
        if let Some(error) = gpu_error {
            return Err(error.into());
        }
        let has_color = plan.requests().iter().any(|r| r.color);
        let composite_frame = if has_color {
            Some(
                prepare_frame(
                    pipelines,
                    material,
                    NativeGpuUniforms::new([frame.width, frame.height], frame.dpi_scale)?,
                    self.composite_frame.as_ref(),
                    &mut metrics,
                )
                .await?,
            )
        } else {
            None
        };
        let epoch = self.epoch.wrapping_add(1);
        // Publication is last: old targets/cache owners survive all failed work.
        self.root = root.clone();
        self.slots = slots.clone();
        self.identities = plan.identities;
        self.caches = output_caches;
        self.epoch = epoch;
        self.samplers = samplers;
        if has_color {
            self.composite_frame = composite_frame.clone();
        }
        Ok(PreparedNativeLayerTargets {
            owner: self.owner.clone(),
            epoch,
            device: self.device.clone(),
            format: self.format,
            plan,
            root,
            slots,
            composite_frame,
            metrics,
        })
    }
    async fn prepare_target(
        &self,
        pipelines: &NativePipelineCache,
        material: &Arc<PreparedNativeMaterials>,
        samplers: Option<&Samplers>,
        request: NativeLayerTargetRequest,
        previous: Option<&Arc<NativeLayerTarget>>,
        dpi: f32,
        metrics: &mut NativeLayerResourceMetrics,
        reservations: &mut std::vec::IntoIter<NativeResidencyReservation>,
    ) -> Result<NativeLayerTarget, Error> {
        let old_color = previous.and_then(|p| p.color.as_ref());
        let color = if request.color {
            if let Some(old) = old_color.filter(|c| c.size() == request.size) {
                Some(old.clone())
            } else {
                let generation = old_color.map_or(1, |c| c.generation().wrapping_add(1));
                let texture = self.create_texture(
                    pipelines,
                    samplers,
                    request.size,
                    generation,
                    false,
                    take_reservation(reservations, request.size)?,
                )?;
                metrics.color_allocations += 1;
                metrics.color_bytes += texture_bytes(request.size, 1)?;
                metrics.bind_groups += 2;
                Some(texture)
            }
        } else {
            previous.and_then(|p| p.color.clone())
        };
        let old_effects = previous.and_then(|p| p.effects.as_ref());
        let effects = if request.effects {
            if let Some(old) = old_effects.filter(|e| e.size() == request.size) {
                Some(old.clone())
            } else {
                let generation = old_effects.map_or(1, |e| e.generation.wrapping_add(1));
                let a = self.create_texture(
                    pipelines,
                    samplers,
                    request.size,
                    generation,
                    true,
                    take_reservation(reservations, request.size)?,
                )?;
                let b = self.create_texture(
                    pipelines,
                    samplers,
                    request.size,
                    generation,
                    true,
                    take_reservation(reservations, request.size)?,
                )?;
                let c = self.create_texture(
                    pipelines,
                    samplers,
                    request.size,
                    generation,
                    true,
                    take_reservation(reservations, request.size)?,
                )?;
                metrics.effect_allocations += 3;
                metrics.effect_bytes += texture_bytes(request.size, 3)?;
                metrics.bind_groups += 3;
                Some(NativeLayerEffects {
                    textures: [a, b, c],
                    generation,
                })
            }
        } else {
            previous.and_then(|p| p.effects.clone())
        };
        let depth = if request.depth {
            if let Some(old) = previous
                .and_then(|p| p.depth.as_ref())
                .filter(|d| d.size() == request.size)
            {
                Some(old.clone())
            } else {
                let bytes = texture_bytes(request.size, 1)?;
                let reservation = take_reservation(reservations, request.size)?;
                let target =
                    super::super::three_d::NativeThreeDDepthTarget::new(&self.device, request.size)
                        .await?;
                metrics.depth_allocations += 1;
                metrics.depth_bytes += bytes;
                Some(NativeLayerDepthTarget(Arc::new(DepthOwner {
                    target,
                    _reservation: reservation,
                })))
            }
        } else {
            previous.and_then(|p| p.depth.clone())
        };
        let target_frame = if request.color {
            Some(
                prepare_frame(
                    pipelines,
                    material.clone(),
                    NativeGpuUniforms::new(request.size, dpi)?,
                    previous.and_then(|p| p.frame.as_ref()),
                    metrics,
                )
                .await?,
            )
        } else {
            previous.and_then(|p| p.frame.clone())
        };
        Ok(NativeLayerTarget {
            request,
            color,
            effects,
            depth,
            frame: target_frame,
        })
    }
    fn create_texture(
        &self,
        pipelines: &NativePipelineCache,
        samplers: Option<&Samplers>,
        size: [u32; 2],
        generation: u32,
        effect: bool,
        reservation: NativeResidencyReservation,
    ) -> Result<NativeLayerTexture, Error> {
        let samplers = samplers.ok_or(Error::ResourceDomain)?;
        let usage = if effect {
            wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::STORAGE_BINDING
                | wgpu::TextureUsages::RENDER_ATTACHMENT
        } else {
            wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST
        };
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Native materialized physical target"),
            size: wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: if effect {
                wgpu::TextureFormat::Rgba8Unorm
            } else {
                self.format
            },
            usage,
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        let bind = |sampler| {
            self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("Native layer retained texture binding"),
                layout: &pipelines.layouts().image.texture,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&view),
                    },
                ],
            })
        };
        let linear = bind(&samplers.linear);
        let nearest = (!effect).then(|| bind(&samplers.nearest));
        Ok(NativeLayerTexture(Arc::new(TextureOwner {
            device: self.device.clone(),
            texture,
            view,
            linear,
            nearest,
            size,
            generation,
            _reservation: reservation,
        })))
    }
    fn reserve_allocations(
        &self,
        plan: &NativeLayerTargetPlan,
    ) -> Result<Vec<NativeResidencyReservation>, Error> {
        let mut reservations = allocate(plan.requests().len() * 5)?;
        for request in plan.requests() {
            let previous = match request.target {
                NativeLayerTargetId::Root => self.root.as_ref(),
                NativeLayerTargetId::Slot(i) => self.slots[i as usize].as_ref(),
            };
            let color = request.color
                && previous
                    .and_then(|p| p.color.as_ref())
                    .is_none_or(|c| c.size() != request.size);
            let effects = request.effects
                && previous
                    .and_then(|p| p.effects.as_ref())
                    .is_none_or(|e| e.size() != request.size);
            let depth = request.depth
                && previous
                    .and_then(|p| p.depth.as_ref())
                    .is_none_or(|d| d.size() != request.size);
            for _ in 0..usize::from(color) + 3 * usize::from(effects) + usize::from(depth) {
                reservations.push(
                    self.residency
                        .try_acquire(texture_bytes(request.size, 1)?)
                        .ok_or(Error::Capacity)?,
                );
            }
        }
        Ok(reservations)
    }
    /// Snapshot the current committed cache state at replay start. The immutable
    /// prepared target generation alone does not assert that its pixels are ready.
    pub fn output_updates(
        &self,
        prepared: &PreparedNativeLayerTargets,
    ) -> Result<NativeLayerOutputUpdates, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) || self.epoch != prepared.epoch {
            return Err(Error::ResourceDomain);
        }
        Ok(NativeLayerOutputUpdates {
            owner: self.owner.clone(),
            epoch: self.epoch,
            caches: self.caches,
            updated: [false; LAYER_SLOT_COUNT as usize],
        })
    }
    /// Publish only after the actual encoder flush/submission succeeds. Prepared
    /// generations from an older pool publication cannot overwrite current keys.
    pub fn commit_submitted(
        &mut self,
        prepared: &PreparedNativeLayerTargets,
        updates: NativeLayerOutputUpdates,
        queue: &wgpu::Queue,
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner)
            || !Arc::ptr_eq(&self.owner, &updates.owner)
            || self.epoch != prepared.epoch
            || self.epoch != updates.epoch
        {
            return Err(Error::ResourceDomain);
        }
        for i in 0..LAYER_SLOT_COUNT as usize {
            if updates.updated[i] {
                self.caches[i] = updates.caches[i];
            }
        }
        prepared.retain_submitted(queue);
        Ok(())
    }
}
fn take_reservation(
    reservations: &mut std::vec::IntoIter<NativeResidencyReservation>,
    size: [u32; 2],
) -> Result<NativeResidencyReservation, Error> {
    let reservation = reservations.next().ok_or(Error::ResourceDomain)?;
    if reservation.bytes() != texture_bytes(size, 1)? {
        return Err(Error::ResourceDomain);
    }
    Ok(reservation)
}
async fn prepare_frame(
    pipelines: &NativePipelineCache,
    material: Arc<PreparedNativeMaterials>,
    uniforms: NativeGpuUniforms,
    previous: Option<&Arc<NativeFrameBindings>>,
    metrics: &mut NativeLayerResourceMetrics,
) -> Result<Arc<NativeFrameBindings>, Error> {
    if let Some(previous) = previous {
        if previous.uniforms().to_bytes() == uniforms.to_bytes() {
            let old = previous.material();
            if old.brush_buffer == material.brush_buffer
                && old.gradient_buffer == material.gradient_buffer
                && old.style_buffer == material.style_buffer
            {
                return Ok(previous.clone());
            }
            let result =
                Arc::new(NativeFrameBindings::rebind(pipelines, material, previous).await?);
            metrics.bind_groups += u64::from(result.binding_allocations());
            return Ok(result);
        }
    }
    let result = Arc::new(NativeFrameBindings::prepare(pipelines, material, uniforms).await?);
    metrics.uniform_upload_bytes += 224;
    metrics.uniform_allocations += 1;
    metrics.bind_groups += 3;
    Ok(result)
}
impl PreparedNativeLayerTargets {
    pub fn plan(&self) -> &Arc<NativeLayerTargetPlan> {
        &self.plan
    }
    pub fn metrics(&self) -> NativeLayerResourceMetrics {
        self.metrics
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn format(&self) -> wgpu::TextureFormat {
        self.format
    }
    pub fn target(&self, id: NativeLayerTargetId) -> Option<&Arc<NativeLayerTarget>> {
        if !self.plan.requests().iter().any(|r| r.target == id) {
            return None;
        }
        match id {
            NativeLayerTargetId::Root => self.root.as_ref(),
            NativeLayerTargetId::Slot(i) => self.slots.get(i as usize)?.as_ref(),
        }
    }
    pub fn composite_frame(&self) -> Option<&Arc<NativeFrameBindings>> {
        self.composite_frame.as_ref()
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
impl NativeLayerOutputUpdates {
    pub fn hit(&self, slot: u32, key: NativeLayerOutputKey) -> bool {
        self.caches.get(slot as usize).is_some_and(|c| c.hit(key))
    }
    pub fn record_encoded_output(
        &mut self,
        prepared: &PreparedNativeLayerTargets,
        slot: u32,
        key: NativeLayerOutputKey,
        effected: bool,
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) || self.epoch != prepared.epoch {
            return Err(Error::ResourceDomain);
        }
        let target = prepared
            .target(NativeLayerTargetId::Slot(slot))
            .ok_or(Error::InvalidInput)?;
        let actual = target.output_key(key.scene_hash, key.operation_id, effected)?;
        if actual != key {
            return Err(Error::ResourceDomain);
        }
        self.caches[slot as usize].record_output(key);
        self.updated[slot as usize] = true;
        Ok(())
    }
}
