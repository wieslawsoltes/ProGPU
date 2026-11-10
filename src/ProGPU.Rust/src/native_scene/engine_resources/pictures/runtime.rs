use super::*;
use crate::native_scene::{
    engine_gpu::NativeRenderAttachment,
    execution::{
        NativeSceneCompileOptions, NativeTargetFormat,
        budget::{NativeResidencyBudget, NativeResidencyReservation},
    },
    identity::scene_bytes_equal,
};
use std::{future::Future, pin::Pin, sync::Mutex};

/// Creates the same-device child renderer and supplies actual device/resource
/// preflight. Children retain the actual immutable parent base pipeline/layout
/// owners, while scene state remains independent.
pub trait NativePictureExecutorFactory {
    fn preflight(
        &self,
        request: &Arc<NativePictureExecutionRequest>,
        external: &NativeExternalImages,
        pictures: &NativePictureRuntime,
    ) -> Result<NativePictureWorkMetrics, Error>;
    fn create(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: NativeTargetFormat,
        options: NativePictureRuntimeOptions,
    ) -> Result<Box<dyn NativePictureExecutor>, Error>;
}
/// Required actual native renderer. Success means the supplied attachment has
/// been rendered and submitted; all source/prepared owners must survive completion.
pub trait NativePictureExecutor {
    fn execute<'a>(
        &'a mut self,
        request: Arc<NativePictureExecutionRequest>,
        attachment: NativeRenderAttachment,
        external: NativeExternalImages,
        pictures: &'a mut NativePictureRuntime,
    ) -> Pin<Box<dyn Future<Output = Result<NativePictureWorkMetrics, Error>> + 'a>>;
}
#[derive(Clone)]
struct Recipe {
    scene: Arc<NativeScene>,
    descriptor: wire::PictureImage,
    presentation: wire::Presentation,
    frame: NativeSceneFrame,
    image: bool,
}
struct Entry {
    recipe: Recipe,
    texture: NativeImageTexture,
    flags: u32,
    external: NativeExternalImages,
}
impl Entry {
    fn cost(&self) -> u64 {
        u64::from(self.recipe.descriptor.width) * u64::from(self.recipe.descriptor.height) * 4
            + self.recipe.scene.envelope().bytes().len() as u64
    }
}
#[derive(Clone)]
struct Snapshot {
    plan: Option<Arc<NativeSceneResourcePlan>>,
    installed: Option<Arc<NativeScene>>,
    scope: u64,
    external: NativeExternalImages,
}
struct Child {
    plan: Option<Arc<NativeSceneResourcePlan>>,
    executor: Box<dyn NativePictureExecutor>,
    pictures: Box<NativePictureRuntime>,
    installed: Option<Arc<NativeScene>>,
    scope: u64,
    external: NativeExternalImages,
}
#[derive(Default)]
struct Active {
    entries: Vec<(Arc<()>, Arc<NativeScene>)>,
}
struct Visit {
    active: Arc<Mutex<Active>>,
    token: Arc<()>,
}
impl Drop for Visit {
    fn drop(&mut self) {
        if let Ok(mut active) = self.active.lock() {
            if let Some(i) = active
                .entries
                .iter()
                .position(|(t, _)| Arc::ptr_eq(t, &self.token))
            {
                active.entries.remove(i);
            }
        }
    }
}
#[derive(Debug)]
struct OutputOwner {
    _scene: Arc<NativeScene>,
    _external: NativeExternalImages,
    _reservation: NativeResidencyReservation,
}
/// A completed capture and the work actually performed to obtain it. A cache
/// hit retains the old exact texture and reports zero allocations/submissions.
pub struct NativePictureCapture {
    pub texture: NativeImageTexture,
    pub metrics: NativePictureWorkMetrics,
    pub output_allocation_bytes: u64,
    pub output_budget_bytes: u64,
    pub output_allocations: u64,
    pub cache_hit: bool,
}
/// One original parent engine's bounded picture history and independent child
/// context. The child owns its own nested runtime; all levels share only actual
/// device/external owners, factory, residency and a short-lived recursion guard.
pub struct NativePictureRuntime {
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: NativeTargetFormat,
    options: NativePictureRuntimeOptions,
    external: NativeExternalImages,
    residency: Arc<NativeResidencyBudget>,
    factory: Arc<dyn NativePictureExecutorFactory>,
    images: Vec<Arc<Entry>>,
    masks: Vec<Arc<Entry>>,
    child: Option<Child>,
    preflight_state: Option<Snapshot>,
    preflight_nested: Option<Box<NativePictureRuntime>>,
    active: Arc<Mutex<Active>>,
    work: NativePictureWorkMetrics,
    mask_work: NativePictureWorkMetrics,
    outputs: NativePictureOutputMetrics,
}
impl NativePictureRuntime {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: NativeTargetFormat,
        options: NativePictureRuntimeOptions,
        external: NativeExternalImages,
        residency: Arc<NativeResidencyBudget>,
        factory: Arc<dyn NativePictureExecutorFactory>,
    ) -> Result<Self, Error> {
        if external.device() != device
            || !options.antialias_padding_pixels.is_finite()
            || options.antialias_padding_pixels <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            format,
            options,
            external,
            residency,
            factory,
            images: Vec::new(),
            masks: Vec::new(),
            child: None,
            preflight_state: None,
            preflight_nested: None,
            active: Arc::new(Mutex::new(Active::default())),
            work: NativePictureWorkMetrics::default(),
            mask_work: NativePictureWorkMetrics::default(),
            outputs: NativePictureOutputMetrics::default(),
        })
    }
    /// Reset this parent invocation's diagnostic totals. Descendants are
    /// aggregated by their actual executor result exactly once.
    pub fn begin_work(&mut self) {
        self.work = Default::default();
        self.mask_work = Default::default();
        self.outputs = Default::default();
    }
    /// Actual child subtree work, excluding this runtime's supplied capture
    /// attachments. This does not redefine the native parent frame counters.
    pub fn work_metrics(&self) -> NativePictureWorkMetrics {
        self.work
    }
    /// Direct mask-consumer capture work and its complete subtree. The outer
    /// NativeMaskMetrics already carries these pass/coverage counters; image and
    /// shader captures are excluded even when their children contain masks.
    pub fn mask_work_metrics(&self) -> NativePictureWorkMetrics {
        self.mask_work
    }
    pub fn output_metrics(&self) -> NativePictureOutputMetrics {
        self.outputs
    }
    fn record_work(
        &mut self,
        mut work: NativePictureWorkMetrics,
        bytes: u64,
        budget: u64,
        hit: bool,
        mask: bool,
    ) -> Result<(), Error> {
        work.nested_captures = work
            .nested_captures
            .checked_add(u32::from(!hit))
            .ok_or(Error::Capacity)?;
        work.cache_hits = work
            .cache_hits
            .checked_add(u32::from(hit))
            .ok_or(Error::Capacity)?;
        let next = self.work.checked_add(work)?;
        let mask_work = if mask {
            self.mask_work.checked_add(work)?
        } else {
            self.mask_work
        };
        let outputs = NativePictureOutputMetrics {
            allocations: self
                .outputs
                .allocations
                .checked_add(u64::from(!hit))
                .ok_or(Error::Capacity)?,
            bytes: self
                .outputs
                .bytes
                .checked_add(bytes)
                .ok_or(Error::Capacity)?,
            descriptor_budget_bytes: self
                .outputs
                .descriptor_budget_bytes
                .checked_add(if hit { 0 } else { budget })
                .ok_or(Error::Capacity)?,
        };
        self.work = next;
        self.mask_work = mask_work;
        self.outputs = outputs;
        Ok(())
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn format(&self) -> NativeTargetFormat {
        self.format
    }
    pub fn external_images(&self) -> &NativeExternalImages {
        &self.external
    }
    pub fn resource_scope(&self) -> u64 {
        self.snapshot().map_or(0, |c| c.scope)
    }
    pub fn cached_image_count(&self) -> usize {
        self.images.len()
    }
    pub fn cached_mask_count(&self) -> usize {
        self.masks.len()
    }
    pub fn retained_bytes(&self) -> u64 {
        self.images
            .iter()
            .chain(&self.masks)
            .map(|e| e.cost())
            .sum()
    }
    pub fn clear_cache(&mut self) {
        self.images.clear();
        self.masks.clear();
    }
    /// The complete ordered sideband table retains selected view identity.
    /// Cache entries keep their own old table; changing this one never mutates it.
    pub fn bind_external_images(&mut self, external: NativeExternalImages) -> Result<bool, Error> {
        if external.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        if self.external.same_bindings(&external) {
            return Ok(false);
        }
        self.external = external;
        Ok(true)
    }
    pub(super) fn admit_context(
        &self,
        device: &wgpu::Device,
        queue: Option<&wgpu::Queue>,
        format: wgpu::TextureFormat,
    ) -> Result<(), Error> {
        if device != &self.device
            || queue.is_some_and(|q| q != &self.queue)
            || format != self.format.texture_format()
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
    pub fn preflight_image(
        &self,
        source: &NativePictureImageSource,
    ) -> Result<NativePictureWorkMetrics, Error> {
        self.preflight_recipe(self.image_recipe(source)?)
    }
    pub fn preflight_mask(
        &self,
        source: &NativePictureMaskSource,
    ) -> Result<NativePictureWorkMetrics, Error> {
        self.preflight_recipe(self.mask_recipe(source)?)
    }
    pub async fn capture_image(
        &mut self,
        source: NativePictureImageSource,
    ) -> Result<NativePictureCapture, Error> {
        let recipe = self.image_recipe(&source)?;
        self.capture(recipe).await
    }
    pub async fn capture_mask(
        &mut self,
        source: NativePictureMaskSource,
    ) -> Result<NativePictureCapture, Error> {
        let recipe = self.mask_recipe(&source)?;
        self.capture(recipe).await
    }
    fn image_recipe(&self, source: &NativePictureImageSource) -> Result<Recipe, Error> {
        let original =
            NativePictureImageSource::from_resource(source.owner().clone(), source.resource_index)?;
        if !identity::same(original.descriptor, source.descriptor)
            || !identity::same(original.presentation, source.presentation)
        {
            return Err(Error::InvalidInput);
        }
        // Fast exact snapshot search avoids parsing/copying a known immutable
        // warm child. Public IDs alone never establish this identity.
        let scene = match self
            .images
            .iter()
            .find(|e| scene_bytes_equal(e.recipe.scene.envelope().bytes(), source.bytes()))
        {
            Some(entry) => entry.recipe.scene.clone(),
            None => match self
                .snapshot()
                .and_then(|s| s.installed)
                .filter(|scene| scene_bytes_equal(scene.envelope().bytes(), source.bytes()))
            {
                Some(scene) => scene,
                None => Arc::new(admit_copy(source.bytes())?),
            },
        };
        let d = source.descriptor;
        let mask = wire::LayerPictureMask {
            struct_size: wire::LayerPictureMask::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_PICTURE,
            stream_size: u32::try_from(source.bytes().len()).map_err(|_| Error::Capacity)?,
            bounds: wire::ImageRect {
                x: 0.0,
                y: 0.0,
                width: d.width as f32,
                height: d.height as f32,
            },
            transform: wire::Affine2d {
                m11: 1.0,
                m22: 1.0,
                ..Default::default()
            },
            opacity: 1.0,
            ..Default::default()
        };
        let target = crate::native_scene::execution::NativeScissor {
            x: 0,
            y: 0,
            width: d.width,
            height: d.height,
            drawable: true,
        };
        let mut frame = crate::native_scene::execution::resolve_picture_frame(
            &mask,
            target,
            d.dpi_scale,
            Some(source.presentation),
        )
        .ok_or(Error::InvalidInput)?;
        frame.clear_color = wire::Color {
            r: d.clear_color.r * d.clear_color.a,
            g: d.clear_color.g * d.clear_color.a,
            b: d.clear_color.b * d.clear_color.a,
            a: d.clear_color.a,
        };
        Ok(Recipe {
            scene,
            descriptor: d,
            presentation: source.presentation,
            frame,
            image: true,
        })
    }
    fn mask_recipe(&self, source: &NativePictureMaskSource) -> Result<Recipe, Error> {
        source
            .frame
            .resolve_presentation()
            .ok_or(Error::InvalidInput)?;
        Ok(Recipe {
            scene: source.scene.clone(),
            descriptor: identity::descriptor(source),
            presentation: source.frame.presentation,
            frame: source.frame,
            image: false,
        })
    }
    fn eligible(recipe: &Recipe) -> bool {
        recipe.scene.append_only_suffix(&recipe.scene)
            == Some(recipe.scene.envelope().header().command_count)
            && !identity::external(recipe.scene.envelope().bytes(), 0)
    }
    fn find(&self, r: &Recipe) -> Option<(Arc<Entry>, u32)> {
        if !Self::eligible(r) {
            return None;
        }
        if r.image {
            self.images.iter().find_map(|entry| {
                if entry.flags != self.options.engine_flags
                    || !entry.external.same_bindings(&self.external)
                    || !identity::same(entry.recipe.descriptor, r.descriptor)
                    || !identity::same(entry.recipe.presentation, r.presentation)
                {
                    return None;
                }
                r.scene
                    .append_only_suffix(&entry.recipe.scene)
                    .map(|first| (entry.clone(), first))
            })
        } else {
            self.masks
                .iter()
                .find(|entry| {
                    entry.flags == self.options.engine_flags
                        && identity::same(entry.recipe.descriptor, r.descriptor)
                        && identity::same(entry.recipe.presentation, r.presentation)
                        && identity::generation_independent(&entry.recipe.scene, &r.scene)
                })
                .map(|e| (e.clone(), r.scene.envelope().header().command_count))
        }
    }
    fn enter(&self, scene: Arc<NativeScene>) -> Result<Visit, Error> {
        let mut active = self.active.lock().map_err(|_| Error::ResourceDomain)?;
        if active.entries.len() >= 64
            || active
                .entries
                .iter()
                .any(|(_, s)| scene_bytes_equal(s.envelope().bytes(), scene.envelope().bytes()))
        {
            return Err(Error::InvalidInput);
        }
        active.entries.try_reserve(1).map_err(|_| Error::Capacity)?;
        let token = Arc::new(());
        active.entries.push((token.clone(), scene));
        Ok(Visit {
            active: self.active.clone(),
            token,
        })
    }
    fn snapshot(&self) -> Option<Snapshot> {
        self.child
            .as_ref()
            .map(|c| Snapshot {
                plan: c.plan.clone(),
                installed: c.installed.clone(),
                scope: c.scope,
                external: c.external.clone(),
            })
            .or_else(|| self.preflight_state.clone())
    }
    fn shadow(&self, external: &NativeExternalImages) -> Result<Self, Error> {
        let mut shadow = Self::new(
            &self.device,
            &self.queue,
            self.format,
            self.options,
            external.clone(),
            self.residency.clone(),
            self.factory.clone(),
        )?;
        shadow.images = copy_entries(&self.images)?;
        shadow.masks = copy_entries(&self.masks)?;
        shadow.active = self.active.clone();
        shadow.preflight_state = self.snapshot();
        shadow.preflight_nested = if let Some(c) = &self.child {
            Some(Box::new(c.pictures.shadow(external)?))
        } else {
            self.preflight_nested
                .as_ref()
                .map(|n| n.shadow(external).map(Box::new))
                .transpose()?
        };
        Ok(shadow)
    }
    fn nested_preflight(&self) -> Result<Self, Error> {
        if let Some(c) = &self.child {
            return c.pictures.shadow(&self.external);
        }
        if let Some(c) = &self.preflight_nested {
            return c.shadow(&self.external);
        }
        let mut nested = Self::new(
            &self.device,
            &self.queue,
            self.format,
            self.options,
            self.external.clone(),
            self.residency.clone(),
            self.factory.clone(),
        )?;
        nested.active = self.active.clone();
        Ok(nested)
    }
    fn request(
        &self,
        recipe: &Recipe,
        first_command: u32,
        seeded: bool,
    ) -> Result<Arc<NativePictureExecutionRequest>, Error> {
        let scene = if seeded {
            suffix(&recipe.scene, first_command)?
        } else {
            recipe.scene.clone()
        };
        let snapshot = self.snapshot();
        let prior = snapshot.as_ref().and_then(|c| c.installed.as_ref());
        let exact = prior
            .is_some_and(|s| scene_bytes_equal(s.envelope().bytes(), scene.envelope().bytes()));
        let mut scope = self.resource_scope();
        if !exact && scene.picture_capture_changes_resource_scope(prior.map(Arc::as_ref)) {
            scope = scope.checked_add(1).ok_or(Error::Capacity)?;
        }
        let mut frame = recipe.frame;
        let h = scene.envelope().header();
        frame.scene_id = h.scene_id;
        frame.generation = h.generation;
        if seeded {
            frame.flags |= SCENE_FRAME_PRESERVE_TARGET;
        }
        let limits = self.device.limits();
        let options = NativeSceneCompileOptions {
            format: self.format,
            engine_flags: self.options.engine_flags,
            resource_scope: scope,
            borrows_shared_vector_pipeline: true,
            maximum_buffer_bytes: limits.max_buffer_size,
            maximum_storage_bytes: u64::from(limits.max_storage_buffer_binding_size),
            antialias_padding_pixels: self.options.antialias_padding_pixels,
        };
        let retained_plan = snapshot
            .as_ref()
            .filter(|prior| exact && prior.external.metadata() == self.external.metadata())
            .and_then(|prior| prior.plan.as_ref())
            .filter(|plan| same_frame(plan.layers().frame(), frame));
        let plan = if let Some(plan) = retained_plan {
            plan.clone()
        } else {
            Arc::new(NativeSceneResourcePlan::compile(
                scene.clone(),
                frame,
                self.external.metadata(),
                options,
            )?)
        };
        Ok(Arc::new(NativePictureExecutionRequest {
            source_scene: recipe.scene.clone(),
            scene,
            plan,
            frame,
            resource_scope: scope,
            first_command,
            exact_snapshot: exact,
            external_changed: snapshot
                .as_ref()
                .is_none_or(|c| !c.external.same_bindings(&self.external)),
        }))
    }
    fn preflight_recipe(&self, recipe: Recipe) -> Result<NativePictureWorkMetrics, Error> {
        self.admit_output(&recipe)?;
        let prior = self.find(&recipe);
        if prior
            .as_ref()
            .is_some_and(|(_, first)| *first == recipe.scene.envelope().header().command_count)
        {
            return Ok(Default::default());
        }
        let _visit = self.enter(recipe.scene.clone())?;
        let request = self.request(&recipe, prior.as_ref().map_or(0, |p| p.1), prior.is_some())?;
        // Provider receives the next child's independent nested context when it
        // exists; a cold preflight shares only policy/owners, never mutable pages.
        let pictures = self.nested_preflight()?;
        self.factory.preflight(&request, &self.external, &pictures)
    }
    fn admit_output(&self, recipe: &Recipe) -> Result<(), Error> {
        let d = recipe.descriptor;
        let limits = self.device.limits();
        if d.width == 0
            || d.height == 0
            || d.width > 16384
            || d.height > 16384
            || d.width > limits.max_texture_dimension_2d
            || d.height > limits.max_texture_dimension_2d
            || u64::from(d.width) * u64::from(d.height) * 4 > u64::from(SCENE_MAX_LAYER_BYTES)
        {
            return Err(Error::Capacity);
        }
        let usage = output_usage(recipe.image);
        if !self
            .format
            .texture_format()
            .guaranteed_format_features(self.device.features())
            .allowed_usages
            .contains(usage)
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
    async fn capture(&mut self, recipe: Recipe) -> Result<NativePictureCapture, Error> {
        self.admit_output(&recipe)?;
        let prior = self.find(&recipe);
        let output_budget_bytes =
            u64::from(recipe.descriptor.width) * u64::from(recipe.descriptor.height) * 4;
        if let Some((entry, first)) = &prior {
            if *first == recipe.scene.envelope().header().command_count {
                self.record_work(
                    Default::default(),
                    0,
                    output_budget_bytes,
                    true,
                    !recipe.image,
                )?;
                return Ok(NativePictureCapture {
                    texture: entry.texture.clone(),
                    metrics: Default::default(),
                    output_allocation_bytes: 0,
                    output_budget_bytes,
                    output_allocations: 0,
                    cache_hit: true,
                });
            }
        }
        let _visit = self.enter(recipe.scene.clone())?;
        let request = self.request(&recipe, prior.as_ref().map_or(0, |p| p.1), prior.is_some())?;
        let nested = self.nested_preflight()?;
        self.factory.preflight(&request, &self.external, &nested)?;
        let bytes = output_budget_bytes / 4
            * u64::from(
                self.format
                    .texture_format()
                    .block_copy_size(None)
                    .ok_or(Error::UnsupportedExecution)?,
            );
        let reservation = self.residency.try_acquire(bytes).ok_or(Error::Capacity)?;
        let mut child = if let Some(child) = self.child.take() {
            child
        } else {
            let mut nested = Self::new(
                &self.device,
                &self.queue,
                self.format,
                self.options,
                self.external.clone(),
                self.residency.clone(),
                self.factory.clone(),
            )?;
            nested.active = self.active.clone();
            Child {
                plan: None,
                executor: self.factory.create(
                    &self.device,
                    &self.queue,
                    self.format,
                    self.options,
                )?,
                pictures: Box::new(nested),
                installed: None,
                scope: 0,
                external: self.external.clone(),
            }
        };
        if let Err(error) = child.pictures.bind_external_images(self.external.clone()) {
            self.child = Some(child);
            return Err(error);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Native actual retained picture source"),
            size: wgpu::Extent3d {
                width: recipe.descriptor.width,
                height: recipe.descriptor.height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: self.format.texture_format(),
            usage: output_usage(recipe.image),
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        if let Some(error) = scope.pop().await {
            self.child = Some(child);
            return Err(error.into());
        }
        let owner = Arc::new(OutputOwner {
            _scene: recipe.scene.clone(),
            _external: self.external.clone(),
            _reservation: reservation,
        });
        let output = match NativeImageTexture::from_view(
            &self.device,
            &texture,
            &view,
            [recipe.descriptor.width, recipe.descriptor.height],
            Some(owner.clone()),
        ) {
            Ok(output) => output,
            Err(error) => {
                self.child = Some(child);
                return Err(error);
            }
        };
        let attachment =
            match NativeRenderAttachment::from_texture(&self.device, &texture, Some(owner)) {
                Ok(attachment) => attachment,
                Err(error) => {
                    self.child = Some(child);
                    return Err(error);
                }
            };
        let mut copy_bytes = 0;
        if let Some((entry, _)) = &prior {
            let mut encoder = self
                .device
                .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                    label: Some("Native immutable picture append seed"),
                });
            encoder.copy_texture_to_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: entry.texture.texture(),
                    mip_level: 0,
                    origin: wgpu::Origin3d::ZERO,
                    aspect: wgpu::TextureAspect::All,
                },
                wgpu::TexelCopyTextureInfo {
                    texture: &texture,
                    mip_level: 0,
                    origin: wgpu::Origin3d::ZERO,
                    aspect: wgpu::TextureAspect::All,
                },
                texture.size(),
            );
            self.queue.submit([encoder.finish()]);
            entry.texture.retain_submitted(&self.queue);
            output.retain_submitted(&self.queue);
            copy_bytes = bytes;
        }
        child.pictures.begin_work();
        let result = child
            .executor
            .execute(
                request.clone(),
                attachment,
                self.external.clone(),
                &mut child.pictures,
            )
            .await;
        // Even a failing renderer may already have submitted bounded child
        // work. Keep the actual output/source lease through that queue point.
        output.retain_submitted(&self.queue);
        let mut metrics = match result {
            Ok(m) if m.frame.submission_count != 0 => m,
            Ok(_) => {
                self.child = Some(child);
                return Err(Error::ResourceDomain);
            }
            Err(e) => {
                self.child = Some(child);
                return Err(e);
            }
        };
        if copy_bytes != 0 {
            let Some(submissions) = metrics.frame.submission_count.checked_add(1) else {
                self.child = Some(child);
                return Err(Error::Capacity);
            };
            let Some(copies) = metrics.seed_copy_bytes.checked_add(copy_bytes) else {
                self.child = Some(child);
                return Err(Error::Capacity);
            };
            metrics.frame.submission_count = submissions;
            metrics.seed_copy_bytes = copies;
        }
        if let Err(error) =
            self.record_work(metrics, bytes, output_budget_bytes, false, !recipe.image)
        {
            self.child = Some(child);
            return Err(error);
        }
        child.plan = Some(request.plan.clone());
        child.installed = Some(request.scene.clone());
        child.scope = request.resource_scope;
        child.external = self.external.clone();
        self.child = Some(child);
        self.retain(Arc::new(Entry {
            recipe: recipe.clone(),
            texture: output.clone(),
            flags: self.options.engine_flags,
            external: self.external.clone(),
        }));
        Ok(NativePictureCapture {
            texture: output,
            metrics,
            output_allocation_bytes: bytes,
            output_budget_bytes,
            output_allocations: 1,
            cache_hit: false,
        })
    }
    fn retain(&mut self, candidate: Arc<Entry>) {
        const LIMIT: u64 = 64 * 1024 * 1024;
        if !Self::eligible(&candidate.recipe) || candidate.cost() > LIMIT {
            return;
        }
        let id = candidate.recipe.scene.envelope().header().scene_id;
        let image = candidate.recipe.image;
        let cache = if image {
            &mut self.images
        } else {
            &mut self.masks
        };
        cache.retain(|e| {
            if e.recipe.scene.envelope().header().scene_id != id {
                return true;
            }
            !image
                && !(identity::same(e.recipe.descriptor, candidate.recipe.descriptor)
                    && identity::generation_independent(&e.recipe.scene, &candidate.recipe.scene))
        });
        let mut bytes = cache.iter().map(|e| e.cost()).sum::<u64>();
        while !cache.is_empty()
            && (cache.len() >= if image { 8 } else { 64 } || bytes > LIMIT - candidate.cost())
        {
            bytes -= cache.remove(0).cost();
        }
        if cache.try_reserve(1).is_ok() {
            cache.push(candidate);
        }
    }
}
fn admit_copy(bytes: &[u8]) -> Result<NativeScene, Error> {
    let mut owned = allocate(bytes.len())?;
    owned.extend_from_slice(bytes);
    NativeScene::new(owned).map_err(|e| {
        if e.error.out_of_memory {
            Error::Capacity
        } else {
            Error::InvalidInput
        }
    })
}
fn suffix(scene: &Arc<NativeScene>, first: u32) -> Result<Arc<NativeScene>, Error> {
    let mut header = scene.envelope().header();
    if first > header.command_count {
        return Err(Error::InvalidInput);
    }
    header.command_offset = header
        .command_offset
        .checked_add(
            first
                .checked_mul(header.command_stride)
                .ok_or(Error::Capacity)?,
        )
        .ok_or(Error::Capacity)?;
    header.command_count -= first;
    let mut bytes = allocate(scene.envelope().bytes().len())?;
    bytes.extend_from_slice(scene.envelope().bytes());
    header
        .write_into(&mut bytes)
        .map_err(|_| Error::InvalidInput)?;
    Ok(Arc::new(NativeScene::new(bytes).map_err(|e| {
        if e.error.out_of_memory {
            Error::Capacity
        } else {
            Error::InvalidInput
        }
    })?))
}
fn output_usage(image: bool) -> wgpu::TextureUsages {
    let base = wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING;
    if image {
        base | wgpu::TextureUsages::COPY_SRC | wgpu::TextureUsages::COPY_DST
    } else {
        base
    }
}

fn copy_entries(entries: &[Arc<Entry>]) -> Result<Vec<Arc<Entry>>, Error> {
    let mut copy = allocate(entries.len())?;
    copy.extend(entries.iter().cloned());
    Ok(copy)
}

fn same_frame(a: NativeSceneFrame, b: NativeSceneFrame) -> bool {
    a.prefix == b.prefix
        && a.width == b.width
        && a.height == b.height
        && a.dpi_scale.to_bits() == b.dpi_scale.to_bits()
        && a.scene_id == b.scene_id
        && a.generation == b.generation
        && a.flags == b.flags
        && identity::same(a.clear_color, b.clear_color)
        && identity::same(a.damage, b.damage)
        && identity::same(a.presentation, b.presentation)
}
