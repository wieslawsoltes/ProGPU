//! Actual GPU image page ownership. Nested sources run during preparation only;
//! encoding replays retained Gaussian programs and never calls a source resolver.
use super::*;
use std::{
    collections::{HashMap, HashSet},
    future::Future,
    pin::Pin,
};
use wgpu::util::DeviceExt;
mod blur;
mod layout;
use blur::{BlurPipeline, PreparedBlur};
pub use layout::NativeImageLayouts;
pub(crate) use layout::image_sampler as native_image_sampler;

/// Actual nested native-picture execution supplied by the owning command engine.
/// Preflight accounts for additional child work before GPU preparation. The
/// required source-format output allocation is counted by this page and must be excluded
/// from the returned child metrics. `prepare` returns that actual rendered
/// texture with its complete retained source lease.
pub trait NativePictureImageResolver {
    fn preflight(
        &self,
        source: &NativePictureImageSource,
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
    ) -> Result<NativeImageResourceMetrics, Error>;
    fn prepare<'a>(
        &'a mut self,
        source: NativePictureImageSource,
        device: &'a wgpu::Device,
        queue: &'a wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Pin<Box<dyn Future<Output = Result<NativePictureImageCapture, Error>> + 'a>>;
}
/// Actual child work including only newly allocated capture output storage.
/// Warm retained captures return zero allocation/upload work.
pub struct NativePictureImageCapture {
    pub texture: NativeImageTexture,
    pub metrics: NativeImageResourceMetrics,
}
/// Original source shader bindings and their retained source generation.
#[derive(Clone)]
pub struct PreparedNativeImageDraw {
    pub descriptor: NativeImageDrawPage,
    pub source: NativeImageTexture,
    pub render_source: NativeImageTexture,
    pub chroma: Option<NativeImageTexture>,
    pub mask: Option<NativeImageTexture>,
    pub sampler: wgpu::Sampler,
    pub texture_binding: Option<wgpu::BindGroup>,
    pub color_matrix_binding: Option<wgpu::BindGroup>,
    pub effect_uniform_binding: Option<wgpu::BindGroup>,
    pub effect_texture_binding: Option<wgpu::BindGroup>,
    pub effect_mask_binding: Option<wgpu::BindGroup>,
    /// Original effect after only the live-blur/YUV fields are cleared.
    pub render_effect: Option<wire::ImageEffect>,
    uniforms: Vec<wgpu::Buffer>,
    blur: Option<PreparedBlur>,
}
impl PreparedNativeImageDraw {
    pub fn has_live_blur(&self) -> bool {
        self.blur.is_some()
    }
    pub fn uniform_buffers(&self) -> &[wgpu::Buffer] {
        &self.uniforms
    }
}
pub struct PreparedNativeImagePage {
    owner: Arc<()>,
    device: wgpu::Device,
    page: Arc<NativeImagePage>,
    pub vertices: wgpu::Buffer,
    pub quad_indices: Option<wgpu::Buffer>,
    draws: Vec<PreparedNativeImageDraw>,
    layouts: NativeImageLayouts,
    metrics: NativeImageResourceMetrics,
    external: NativeExternalImages,
}
impl PreparedNativeImagePage {
    /// The owning native family cache has matched the complete original image
    /// key and actual external views. Retain captured textures/programs while
    /// publishing current source command descriptors and their scene owner.
    pub(crate) fn rebind(
        &self,
        page: Arc<NativeImagePage>,
        external: NativeExternalImages,
    ) -> Result<Self, Error> {
        if page.draws().len() != self.draws.len() || !self.external.same_bindings(&external) {
            return Err(Error::ResourceDomain);
        }
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(self.draws.len())
            .map_err(|_| Error::Capacity)?;
        for (old, descriptor) in self.draws.iter().zip(page.draws()) {
            let mut draw = old.clone();
            draw.descriptor = descriptor.clone();
            draws.push(draw);
        }
        Ok(Self {
            owner: self.owner.clone(),
            device: self.device.clone(),
            page,
            vertices: self.vertices.clone(),
            quad_indices: self.quad_indices.clone(),
            draws,
            layouts: self.layouts.clone(),
            metrics: self.replay_metrics(),
            external,
        })
    }
    pub fn page(&self) -> &Arc<NativeImagePage> {
        &self.page
    }
    pub fn draws(&self) -> &[PreparedNativeImageDraw] {
        &self.draws
    }
    pub fn layouts(&self) -> &NativeImageLayouts {
        &self.layouts
    }
    /// Allocations/uploads performed when this generation was prepared.
    pub fn metrics(&self) -> NativeImageResourceMetrics {
        self.metrics
    }
    /// Reusing the immutable page uploads no resources. Its actual live blur
    /// passes still execute before the ordinary image consumers on every replay.
    pub fn replay_metrics(&self) -> NativeImageResourceMetrics {
        NativeImageResourceMetrics {
            draws: self.draws.len() as u64,
            // Retained child captures do not render again. Only this page's
            // actual two-pass Gaussian programs replay in encode_sources.
            live_blur_passes: self.draws.iter().filter(|draw| draw.blur.is_some()).count() as u64
                * 2,
            ..Default::default()
        }
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    /// Must follow the owning queue submission; producer leases survive release
    /// of this entire page until the queued consumer finishes.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        self.external.retain_submitted(queue);
        for draw in &self.draws {
            draw.source.retain_submitted(queue);
            draw.render_source.retain_submitted(queue);
            if let Some(texture) = &draw.chroma {
                texture.retain_submitted(queue);
            }
            if let Some(texture) = &draw.mask {
                texture.retain_submitted(queue);
            }
            if let Some(blur) = &draw.blur {
                blur.retain_submitted(queue);
            }
        }
    }
}
/// Lazy device-owned preparation resources. Cache identity for complete pages is
/// supplied by the separate original native identity/cursor layer, not invented here.
pub struct NativeImagePageEngine {
    owner: Arc<()>,
    target_format: wgpu::TextureFormat,
    device: wgpu::Device,
    queue: wgpu::Queue,
    layouts: Option<NativeImageLayouts>,
    identity_mask_uniform: Option<wgpu::Buffer>,
    quad_indices: Option<wgpu::Buffer>,
    samplers: HashMap<(u32, u32, u32), wgpu::Sampler>,
    blur_pipelines: [Option<BlurPipeline>; 2],
}
impl NativeImagePageEngine {
    /// Existing standalone RGBA8 domain. Native parent engines use their actual
    /// attachment format through `new_with_target_format`.
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        Self::new_with_target_format(device, queue, wgpu::TextureFormat::Rgba8Unorm)
    }
    pub fn new_with_target_format(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Self {
        Self {
            owner: Arc::new(()),
            target_format,
            device: device.clone(),
            queue: queue.clone(),
            layouts: None,
            identity_mask_uniform: None,
            quad_indices: None,
            samplers: HashMap::new(),
            blur_pipelines: [None, None],
        }
    }
    /// Whole actual page/device/source admission before any resource publication.
    pub fn preflight(
        &self,
        page: &NativeImagePage,
        external: &NativeExternalImages,
        pictures: &impl NativePictureImageResolver,
    ) -> Result<NativeImageResourceMetrics, Error> {
        self.preflight_costs(page, external, pictures, &mut HashMap::new())
    }
    fn preflight_costs(
        &self,
        page: &NativeImagePage,
        external: &NativeExternalImages,
        pictures: &impl NativePictureImageResolver,
        picture_costs: &mut HashMap<u32, NativeImageResourceMetrics>,
    ) -> Result<NativeImageResourceMetrics, Error> {
        picture_costs
            .try_reserve(picture_capacity(page))
            .map_err(|_| Error::Capacity)?;
        if !matches!(
            self.target_format,
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::R8Unorm
        ) {
            return Err(Error::UnsupportedExecution);
        }
        if external.device() != &self.device || page.external_images() != external.metadata() {
            return Err(Error::ResourceDomain);
        }
        if page.draws().is_empty() {
            return Err(Error::InvalidInput);
        }
        let limits = self.device.limits();
        if page.metrics().vertex_bytes > limits.max_buffer_size
            || limits.max_bind_groups < 4
            || limits.max_sampled_textures_per_shader_stage < 3
            || limits.max_uniform_buffer_binding_size < 288
        {
            return Err(Error::Capacity);
        }
        let mut metrics = NativeImageResourceMetrics {
            draws: page.draws().len() as u64,
            vertex_bytes: page.metrics().vertex_bytes,
            index_bytes: if self.quad_indices.is_none() { 24 } else { 0 },
            ..Default::default()
        };
        let mut seen = HashSet::new();
        seen.try_reserve(picture_capacity(page))
            .map_err(|_| Error::Capacity)?;
        for draw in page.draws() {
            if [draw.image.image_width, draw.image.image_height]
                .into_iter()
                .any(|v| v > limits.max_texture_dimension_2d)
            {
                return Err(Error::Capacity);
            }
            match draw.storage {
                NativeImageStorage::Upload {
                    format,
                    upload_bytes,
                } => {
                    page.source_pixels(draw)?;
                    metrics = metrics.checked_add(NativeImageResourceMetrics {
                        texture_upload_bytes: upload_bytes,
                        texture_allocations: 1,
                        texture_bytes: u64::from(draw.image.image_width)
                            * u64::from(draw.image.image_height)
                            * u64::from(format.bytes_per_pixel()),
                        ..Default::default()
                    })?;
                }
                NativeImageStorage::External {
                    primary,
                    chroma,
                    mask,
                } => {
                    let effect = draw.effect;
                    let unfilterable = effect
                        .is_some_and(|e| e.flags & SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR != 0);
                    let primary_image = &external
                        .bindings()
                        .get(primary)
                        .ok_or(Error::ResourceDomain)?
                        .image;
                    sampled_format(
                        &self.device,
                        primary_image,
                        !unfilterable || draw.color_matrix.is_some(),
                    )?;
                    if effect.is_some_and(|e| e.flags0[0] > 0.5) {
                        sampled_format(
                            &self.device,
                            &external
                                .bindings()
                                .get(chroma.ok_or(Error::InvalidInput)?)
                                .ok_or(Error::ResourceDomain)?
                                .image,
                            !unfilterable,
                        )?;
                    }
                    if effect.is_some_and(|e| e.flags0[1] > 0.5) {
                        sampled_format(
                            &self.device,
                            &external
                                .bindings()
                                .get(mask.ok_or(Error::InvalidInput)?)
                                .ok_or(Error::ResourceDomain)?
                                .image,
                            true,
                        )?;
                    }
                    for index in [Some(primary), chroma, mask].into_iter().flatten() {
                        let source = external
                            .bindings()
                            .get(index)
                            .ok_or(Error::ResourceDomain)?;
                        if source.image.device() != &self.device {
                            return Err(Error::ResourceDomain);
                        }
                    }
                }
                NativeImageStorage::Picture { .. } => {
                    if seen.insert(draw.resource_index) {
                        let source = page.picture_source(draw).ok_or(Error::InvalidInput)?;
                        let cost = pictures
                            .preflight(&source, &self.device, self.target_format)?
                            .checked_add(NativeImageResourceMetrics {
                                texture_allocations: 1,
                                texture_bytes: u64::from(source.descriptor.width)
                                    * u64::from(source.descriptor.height)
                                    * 4,
                                nested_pictures: 1,
                                ..Default::default()
                            })?;
                        metrics = metrics.checked_add(cost)?;
                        picture_costs.insert(draw.resource_index, cost);
                    }
                }
            }
            let mut uniforms = if draw.color_matrix.is_some() { 96 } else { 0 };
            if let Some(effect) = draw.effect {
                uniforms += 288;
                if effect.flags0[1] > 0.5 {
                    uniforms += 96;
                }
                if live_blur(&effect) {
                    if limits.max_uniform_buffer_binding_size < 912 {
                        return Err(Error::Capacity);
                    }
                    let bytes =
                        u64::from(draw.image.image_width) * u64::from(draw.image.image_height) * 8;
                    metrics = metrics.checked_add(NativeImageResourceMetrics {
                        texture_bytes: bytes,
                        texture_allocations: 2,
                        live_blur_passes: 2,
                        ..Default::default()
                    })?;
                    uniforms += 1824;
                }
            }
            metrics.uniform_upload_bytes = metrics
                .uniform_upload_bytes
                .checked_add(uniforms)
                .ok_or(Error::Capacity)?;
        }
        Ok(metrics)
    }
    pub async fn prepare(
        &mut self,
        page: Arc<NativeImagePage>,
        external: &NativeExternalImages,
        pictures: &mut impl NativePictureImageResolver,
    ) -> Result<PreparedNativeImagePage, Error> {
        let mut picture_costs = HashMap::new();
        let metrics = self.preflight_costs(&page, external, pictures, &mut picture_costs)?;
        // Candidate handles are local until every draw succeeds; replacing an
        // existing retained page is the owning engine's atomic publication step.
        let device = self.device.clone();
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = self
            .prepare_candidate(page, external, pictures, metrics, picture_costs)
            .await;
        let validation = scope.pop().await;
        if let Some(error) = validation {
            return Err(error.into());
        }
        result
    }
    async fn prepare_candidate(
        &mut self,
        page: Arc<NativeImagePage>,
        external: &NativeExternalImages,
        pictures: &mut impl NativePictureImageResolver,
        mut metrics: NativeImageResourceMetrics,
        picture_costs: HashMap<u32, NativeImageResourceMetrics>,
    ) -> Result<PreparedNativeImagePage, Error> {
        self.samplers
            .try_reserve((11usize * 17 * 9).saturating_sub(self.samplers.len()))
            .map_err(|_| Error::Capacity)?;
        let layouts = self
            .layouts
            .get_or_insert_with(|| NativeImageLayouts::new(&self.device))
            .clone();
        let device = self.device.clone();
        let queue = self.queue.clone();
        let vertices = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Native retained image vertex page"),
            contents: bytemuck::cast_slice(page.vertices()),
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
        });
        let quad_indices = Some(
            self.quad_indices
                .get_or_insert_with(|| {
                    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                        label: Some("Native retained image quad indices"),
                        contents: bytemuck::cast_slice(&page.quad_indices()),
                        usage: wgpu::BufferUsages::INDEX | wgpu::BufferUsages::COPY_DST,
                    })
                })
                .clone(),
        );
        let identity_mask_uniform = if page.draws().iter().any(|d| d.effect.is_some()) {
            Some(
                self.identity_mask_uniform
                    .get_or_insert_with(|| {
                        device.create_buffer(&wgpu::BufferDescriptor {
                            label: Some("Native zero identity mask uniform"),
                            size: 96,
                            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                            mapped_at_creation: false,
                        })
                    })
                    .clone(),
            )
        } else {
            None
        };
        let mut draws = allocate(page.draws().len())?;
        let mut rendered_pictures = HashMap::<u32, NativeImageTexture>::new();
        rendered_pictures
            .try_reserve(picture_capacity(&page))
            .map_err(|_| Error::Capacity)?;
        let linear = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Native original image-mask linear sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            lod_max_clamp: 0.0,
            ..Default::default()
        });
        for descriptor in page.draws() {
            let (source, chroma, mask) = match descriptor.storage {
                NativeImageStorage::Upload { format, .. } => {
                    let gpu_format = match format {
                        crate::image::ImageFormat::R8 => wgpu::TextureFormat::R8Unorm,
                        crate::image::ImageFormat::Bgra8 => wgpu::TextureFormat::Bgra8Unorm,
                        crate::image::ImageFormat::Rgba8 => wgpu::TextureFormat::Rgba8Unorm,
                    };
                    let texture = allocate_texture(
                        &device,
                        [descriptor.image.image_width, descriptor.image.image_height],
                        gpu_format,
                        wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                    )?;
                    queue.write_texture(
                        wgpu::TexelCopyTextureInfo {
                            texture: texture.texture(),
                            mip_level: 0,
                            origin: wgpu::Origin3d::ZERO,
                            aspect: wgpu::TextureAspect::All,
                        },
                        page.source_pixels(descriptor)?,
                        wgpu::TexelCopyBufferLayout {
                            offset: 0,
                            bytes_per_row: Some(descriptor.image.row_bytes),
                            rows_per_image: Some(descriptor.image.image_height),
                        },
                        wgpu::Extent3d {
                            width: descriptor.image.image_width,
                            height: descriptor.image.image_height,
                            depth_or_array_layers: 1,
                        },
                    );
                    (texture, None, None)
                }
                NativeImageStorage::External {
                    primary,
                    chroma,
                    mask,
                } => (
                    external.bindings()[primary].image.clone(),
                    chroma.map(|i| external.bindings()[i].image.clone()),
                    mask.map(|i| external.bindings()[i].image.clone()),
                ),
                NativeImageStorage::Picture {
                    descriptor: picture,
                    ..
                } => {
                    let texture = if let Some(existing) =
                        rendered_pictures.get(&descriptor.resource_index)
                    {
                        existing.clone()
                    } else {
                        let source = page.picture_source(descriptor).ok_or(Error::InvalidInput)?;
                        let capture = pictures
                            .prepare(source, &device, &queue, self.target_format)
                            .await?;
                        let output = capture.texture;
                        metrics = subtract_picture_cost(
                            metrics,
                            *picture_costs
                                .get(&descriptor.resource_index)
                                .ok_or(Error::ResourceDomain)?,
                        )?
                        .checked_add(capture.metrics)?;
                        if output.device() != &device
                            || output.size() != [picture.width, picture.height]
                            || output.texture().format() != self.target_format
                        {
                            return Err(Error::ResourceDomain);
                        }
                        rendered_pictures.insert(descriptor.resource_index, output.clone());
                        output
                    };
                    (texture, None, None)
                }
            };
            let requires_chroma = descriptor.effect.is_some_and(|e| e.flags0[0] > 0.5);
            let requires_mask = descriptor.effect.is_some_and(|e| e.flags0[1] > 0.5);
            let chroma = if requires_chroma {
                Some(chroma.ok_or(Error::InvalidInput)?)
            } else {
                None
            };
            let mask = if requires_mask {
                Some(mask.ok_or(Error::InvalidInput)?)
            } else {
                None
            };
            let blur = if let Some(effect) = descriptor.effect.filter(live_blur) {
                Some(self.prepare_blur(&source, chroma.as_ref(), &effect).await?)
            } else {
                None
            };
            let render_source = blur
                .as_ref()
                .map_or_else(|| source.clone(), |b| b.output.clone());
            let mut render_effect = descriptor.effect;
            if blur.is_some() {
                if let Some(effect) = &mut render_effect {
                    effect.effects1[2] = 0.0;
                    effect.flags0[0] = 0.0;
                    effect.flags = 0;
                }
            }
            let address =
                descriptor.image.flags & (SCENE_IMAGE_ADDRESS_U_MASK | SCENE_IMAGE_ADDRESS_V_MASK);
            let mode = if address == 0
                && matches!(
                    descriptor.image.sampling,
                    IMAGE_SAMPLING_CUBIC | IMAGE_SAMPLING_FANT
                ) {
                IMAGE_SAMPLING_LINEAR
            } else {
                descriptor.image.sampling
            };
            let key = (mode, descriptor.image.max_anisotropy.max(1), address);
            let sampler = if let Some(s) = self.samplers.get(&key) {
                s.clone()
            } else {
                let s = layout::image_sampler(&device, &descriptor.image)?;
                self.samplers.insert(key, s.clone());
                s
            };
            // Unfilterable planar source is consumed only by its integer-load
            // blur/decode program; final image/effect bindings use the RGB output.
            let unfilterable = descriptor
                .effect
                .is_some_and(|e| e.flags & SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR != 0);
            let texture_binding = (!unfilterable).then(|| {
                layout::binding(
                    &device,
                    &layouts.texture,
                    &[
                        layout::sampler_entry(0, &sampler),
                        layout::view_entry(1, source.view()),
                    ],
                )
            });
            let mut uniforms = allocate(4)?;
            let color_matrix_binding = if let Some(matrix) = descriptor.color_matrix {
                let record = [
                    matrix.red,
                    matrix.green,
                    matrix.blue,
                    matrix.alpha,
                    matrix.offset,
                    [
                        if matrix.flags & SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA != 0 {
                            1.0
                        } else {
                            0.0
                        },
                        0.0,
                        0.0,
                        0.0,
                    ],
                ];
                let buffer = uniform_buffer(&device, bytemuck::cast_slice(&record));
                let group = layout::binding(
                    &device,
                    &layouts.mask,
                    &[
                        layout::sampler_entry(0, &linear),
                        layout::view_entry(1, source.view()),
                        layout::buffer_entry(2, &buffer),
                    ],
                );
                uniforms.push(buffer);
                Some(group)
            } else {
                None
            };
            let (effect_uniform_binding, effect_texture_binding, effect_mask_binding) =
                if let Some(effect) = render_effect {
                    let mut bytes = [0u8; 304];
                    effect
                        .write_into(&mut bytes)
                        .map_err(|_| Error::InvalidInput)?;
                    let buffer = uniform_buffer(&device, &bytes[..288]);
                    let ub = layout::binding(
                        &device,
                        &layouts.effect_uniform,
                        &[layout::buffer_entry(0, &buffer)],
                    );
                    uniforms.push(buffer);
                    let selected_chroma = if blur.is_none() {
                        chroma.as_ref().unwrap_or(&render_source)
                    } else {
                        &render_source
                    };
                    let tb = layout::binding(
                        &device,
                        &layouts.effect_texture,
                        &[
                            layout::sampler_entry(0, &sampler),
                            layout::view_entry(1, render_source.view()),
                            layout::view_entry(2, selected_chroma.view()),
                        ],
                    );
                    let mut record = [[0.0f32; 4]; 6];
                    if let Some(mask) = &mask {
                        record[1][0] = 1.0 / mask.size()[0] as f32;
                        record[1][1] = 1.0 / mask.size()[1] as f32;
                        record[5][0] = 1.0;
                    }
                    let buffer = if mask.is_some() {
                        uniform_buffer(&device, bytemuck::cast_slice(&record))
                    } else {
                        identity_mask_uniform
                            .as_ref()
                            .ok_or(Error::InvalidInput)?
                            .clone()
                    };
                    let mb = layout::binding(
                        &device,
                        &layouts.mask,
                        &[
                            layout::sampler_entry(0, &linear),
                            layout::view_entry(1, mask.as_ref().unwrap_or(&render_source).view()),
                            layout::buffer_entry(2, &buffer),
                        ],
                    );
                    uniforms.push(buffer);
                    (Some(ub), Some(tb), Some(mb))
                } else {
                    (None, None, None)
                };
            draws.push(PreparedNativeImageDraw {
                descriptor: descriptor.clone(),
                source,
                render_source,
                chroma,
                mask,
                sampler,
                texture_binding,
                color_matrix_binding,
                effect_uniform_binding,
                effect_texture_binding,
                effect_mask_binding,
                render_effect,
                uniforms,
                blur,
            });
        }
        Ok(PreparedNativeImagePage {
            owner: self.owner.clone(),
            device,
            page,
            vertices,
            quad_indices,
            draws,
            layouts,
            metrics,
            external: external.clone(),
        })
    }
    /// Encode original live source passes before the ordered consuming draw pass.
    /// External pixels are evaluated on every replay; no source callback occurs.
    pub fn encode_sources(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        page: &PreparedNativeImagePage,
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&self.owner, &page.owner) {
            return Err(Error::ResourceDomain);
        }
        for draw in &page.draws {
            if let Some(blur) = &draw.blur {
                blur.encode(encoder);
            }
        }
        Ok(())
    }
}
fn live_blur(effect: &wire::ImageEffect) -> bool {
    effect.effects1[2] > 0.01 || effect.flags & SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR != 0
}
fn uniform_buffer(device: &wgpu::Device, bytes: &[u8]) -> wgpu::Buffer {
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some("Native retained image uniforms"),
        contents: bytes,
        usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
    })
}
fn allocate_texture(
    device: &wgpu::Device,
    size: [u32; 2],
    format: wgpu::TextureFormat,
    usage: wgpu::TextureUsages,
) -> Result<NativeImageTexture, Error> {
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("Native retained image storage"),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage,
        view_formats: &[],
    });
    let view = texture.create_view(&Default::default());
    NativeImageTexture::from_view(device, &texture, &view, size, None)
}

fn sampled_format(
    device: &wgpu::Device,
    source: &NativeImageTexture,
    filtering: bool,
) -> Result<(), Error> {
    match source
        .texture()
        .format()
        .sample_type(None, Some(device.features()))
    {
        Some(wgpu::TextureSampleType::Float { filterable }) if !filtering || filterable => Ok(()),
        _ => Err(Error::UnsupportedExecution),
    }
}

fn picture_capacity(page: &NativeImagePage) -> usize {
    page.draws()
        .iter()
        .filter(|d| matches!(d.storage, NativeImageStorage::Picture { .. }))
        .count()
        .min(page.owner().envelope().header().resource_count as usize)
}

fn subtract_picture_cost(
    a: NativeImageResourceMetrics,
    b: NativeImageResourceMetrics,
) -> Result<NativeImageResourceMetrics, Error> {
    macro_rules! sub {
        ($f:ident) => {
            a.$f.checked_sub(b.$f).ok_or(Error::ResourceDomain)?
        };
    }
    Ok(NativeImageResourceMetrics {
        draws: sub!(draws),
        vertex_bytes: sub!(vertex_bytes),
        index_bytes: sub!(index_bytes),
        texture_bytes: sub!(texture_bytes),
        texture_upload_bytes: sub!(texture_upload_bytes),
        uniform_upload_bytes: sub!(uniform_upload_bytes),
        texture_allocations: sub!(texture_allocations),
        nested_pictures: sub!(nested_pictures),
        live_blur_passes: sub!(live_blur_passes),
    })
}
