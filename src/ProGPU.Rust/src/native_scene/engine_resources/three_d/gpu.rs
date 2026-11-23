//! Exact native storage realization, demanded pipelines and immutable replay.
//! Parent execution owns painter order, binary scissor and depth clear/load.
use super::*;
use std::collections::BTreeMap;
mod pipeline;
use pipeline::{Resources, Variant};
#[derive(Clone)]
struct Buffers {
    buffers: Vec<wgpu::Buffer>,
    binding: wgpu::BindGroup,
    materials: Vec<wgpu::BindGroup>,
}
#[derive(Clone, Copy, PartialEq, Eq)]
struct Key {
    hash: u64,
    dpi: u32,
    size: [u32; 2],
    draws: usize,
}
#[derive(Clone)]
struct Cached {
    key: Key,
    buffers: Arc<Buffers>,
    external: NativeExternalImages,
}
/// Device-owned native resources. Warm preparation retains complete immutable
/// buffers; a failed candidate never replaces the previously published page.
pub struct NativeThreeDPageEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: wgpu::TextureFormat,
    resources: Option<Arc<Resources>>,
    pipelines: BTreeMap<Variant, wgpu::RenderPipeline>,
    cache: Option<Cached>,
}
/// Fully owned replay generation. No source callback or upload occurs in encode.
#[derive(Clone)]
pub struct PreparedNativeThreeDPage {
    device: wgpu::Device,
    format: wgpu::TextureFormat,
    page: Arc<NativeThreeDPage>,
    resources: Option<Arc<Resources>>,
    buffers: Option<Arc<Buffers>>,
    pipelines: Arc<BTreeMap<Variant, wgpu::RenderPipeline>>,
    external: NativeExternalImages,
    metrics: NativeThreeDMetrics,
}
impl NativeThreeDPageEngine {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Self {
        Self {
            device: device.clone(),
            queue: queue.clone(),
            format: target_format,
            resources: None,
            pipelines: BTreeMap::new(),
            cache: None,
        }
    }
    pub fn new_with_preference(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_format: wgpu::TextureFormat,
        preference: crate::path::ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            crate::path::ExecutionPreference::Automatic
                | crate::path::ExecutionPreference::GpuShader
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self::new(device, queue, target_format))
    }
    pub fn execution_path(&self) -> crate::path::ExecutionPath {
        crate::path::ExecutionPath::GpuShader
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn target_format(&self) -> wgpu::TextureFormat {
        self.format
    }
    pub fn initialized_pipeline_count(&self) -> usize {
        self.pipelines.len()
    }
    pub fn clear_page_cache(&mut self) {
        self.cache = None;
    }
    /// Actual limits and complete external-view inventory before GPU mutation.
    pub fn preflight(
        &self,
        page: &NativeThreeDPage,
        external: &NativeExternalImages,
    ) -> Result<NativeThreeDMetrics, Error> {
        if external.device() != &self.device || external.metadata() != page.external_images() {
            return Err(Error::ResourceDomain);
        }
        if page.draws.is_empty() {
            return Ok(page.metrics);
        }
        let l = self.device.limits();
        if l.max_bind_groups < 2
            || l.max_bindings_per_bind_group < 9
            || l.max_storage_buffers_per_shader_stage < 6
            || l.max_sampled_textures_per_shader_stage < 1
            || l.max_samplers_per_shader_stage < 1
        {
            return Err(Error::Capacity);
        }
        for size in page.storage_sizes() {
            if size > l.max_buffer_size || size > u64::from(l.max_storage_buffer_binding_size) {
                return Err(Error::Capacity);
            }
        }
        for m in page.mesh_draws.iter() {
            if let Some(index) = m.material_image {
                let binding = external
                    .bindings()
                    .get(index)
                    .ok_or(Error::ResourceDomain)?;
                if binding.info.role != NativeImageRole::Primary
                    || binding.image.device() != &self.device
                {
                    return Err(Error::ResourceDomain);
                }
                if !matches!(
                    binding
                        .image
                        .texture()
                        .format()
                        .sample_type(None, Some(self.device.features())),
                    Some(wgpu::TextureSampleType::Float { filterable: true })
                ) {
                    return Err(Error::UnsupportedExecution);
                }
            }
        }
        for draw in page.draws.iter() {
            let target = draw.frame.target();
            if target.width > l.max_texture_dimension_2d
                || target.height > l.max_texture_dimension_2d
            {
                return Err(Error::Capacity);
            }
        }
        let features = self
            .format
            .guaranteed_format_features(self.device.features());
        if !features
            .allowed_usages
            .contains(wgpu::TextureUsages::RENDER_ATTACHMENT)
            || !features
                .flags
                .contains(wgpu::TextureFormatFeatureFlags::BLENDABLE)
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(page.metrics)
    }
    /// `resource_scope` is the owning native engine's actual capture scope;
    /// hashing is delegated to the shared original native identity implementation.
    pub async fn prepare(
        &mut self,
        page: Arc<NativeThreeDPage>,
        external: &NativeExternalImages,
        resource_scope: u64,
    ) -> Result<PreparedNativeThreeDPage, Error> {
        let mut metrics = self.preflight(&page, external)?;
        if page.draws.is_empty() {
            return Ok(PreparedNativeThreeDPage {
                device: self.device.clone(),
                format: self.format,
                page,
                resources: None,
                buffers: None,
                pipelines: Arc::new(BTreeMap::new()),
                external: external.clone(),
                metrics,
            });
        }
        let key = Key {
            hash: page.content_hash(resource_scope),
            dpi: page.frame.dpi_scale.to_bits(),
            size: [page.frame.width, page.frame.height],
            draws: page.draws.len(),
        };
        if let Some(cache) = &self.cache {
            if cache.key == key && cache.external.same_bindings(external) {
                metrics.upload_bytes = 0;
                metrics.buffer_allocations = 0;
                metrics.bind_groups = 0;
                return Ok(PreparedNativeThreeDPage {
                    device: self.device.clone(),
                    format: self.format,
                    page,
                    resources: self.resources.clone(),
                    buffers: Some(cache.buffers.clone()),
                    pipelines: Arc::new(self.pipelines.clone()),
                    external: external.clone(),
                    metrics,
                });
            }
        }
        // Complete safe byte production and fallible host allocations precede
        // any buffer creation or queue upload.
        let payloads = payloads(&page)?;
        let mut gpu_buffers = allocate(9)?;
        let mut bindings = allocate(page.mesh_draws.len())?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let created_resources = self.resources.is_none();
        let resources = self
            .resources
            .clone()
            .unwrap_or_else(|| Arc::new(Resources::new(&self.device)));
        let mut pipelines = self.pipelines.clone();
        for (variant, required) in pipeline::VARIANTS
            .into_iter()
            .zip(pipeline::required(&page))
        {
            if required && !pipelines.contains_key(&variant) {
                pipelines.insert(
                    variant,
                    resources.pipeline(&self.device, self.format, variant),
                );
                metrics.pipelines_created += 1;
            }
        }
        let sizes = page.storage_sizes();
        for (index, payload) in payloads.iter().enumerate() {
            let buffer = self.device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Native3D immutable original storage"),
                size: sizes[index],
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            if !payload.is_empty() {
                self.queue.write_buffer(&buffer, 0, payload);
            }
            gpu_buffers.push(buffer);
        }
        let entries: [wgpu::BindGroupEntry<'_>; 9] =
            std::array::from_fn(|i| wgpu::BindGroupEntry {
                binding: i as u32,
                resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer: &gpu_buffers[i],
                    offset: 0,
                    size: std::num::NonZeroU64::new(sizes[i]),
                }),
            });
        let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Native3D storage generation"),
            layout: &resources.storage,
            entries: &entries,
        });
        for mesh in page.mesh_draws.iter() {
            let view = mesh
                .material_image
                .map(|i| external.bindings()[i].image.view())
                .unwrap_or(&resources.sentinel_view);
            bindings.push(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("Native3D selected original material view"),
                layout: &resources.material,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&resources.sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(view),
                    },
                ],
            }));
        }
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        if created_resources {
            metrics.texture_bytes = 4;
            metrics.texture_allocations = 1;
        }
        let buffers = Arc::new(Buffers {
            buffers: gpu_buffers,
            binding,
            materials: bindings,
        });
        self.resources = Some(resources.clone());
        self.pipelines = pipelines.clone();
        self.cache = Some(Cached {
            key,
            buffers: buffers.clone(),
            external: external.clone(),
        });
        Ok(PreparedNativeThreeDPage {
            device: self.device.clone(),
            format: self.format,
            page,
            resources: Some(resources),
            buffers: Some(buffers),
            pipelines: Arc::new(pipelines),
            external: external.clone(),
            metrics,
        })
    }
}
fn raw<T: WireRecord>(values: &[T]) -> Result<Vec<u8>, Error> {
    let length = bytes(values.len(), T::WIRE_SIZE)?;
    let mut result = allocate(length)?;
    result.resize(length, 0);
    for (value, out) in values.iter().zip(result.chunks_exact_mut(T::WIRE_SIZE)) {
        value.write_into(out).map_err(|_| Error::InvalidInput)?;
    }
    Ok(result)
}
fn pod<T: bytemuck::Pod>(values: &[T]) -> Result<Vec<u8>, Error> {
    let source = bytemuck::cast_slice(values);
    let mut result = allocate(source.len())?;
    result.extend_from_slice(source);
    Ok(result)
}
fn payloads(page: &NativeThreeDPage) -> Result<[Vec<u8>; 9], Error> {
    Ok([
        pod(&page.cameras)?,
        pod(&page.lines)?,
        pod(&page.meshes)?,
        raw(&page.vertices)?,
        raw(&page.indices)?,
        raw(&page.lights)?,
        raw(&page.materials)?,
        raw(&page.stops)?,
        pod(&page.edges)?,
    ])
}
impl PreparedNativeThreeDPage {
    pub fn page(&self) -> &Arc<NativeThreeDPage> {
        &self.page
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn target_format(&self) -> wgpu::TextureFormat {
        self.format
    }
    pub fn metrics(&self) -> NativeThreeDMetrics {
        self.metrics
    }
    pub fn replay_metrics(&self) -> NativeThreeDMetrics {
        NativeThreeDMetrics {
            command_draws: self.metrics.command_draws,
            gpu_draws: self.metrics.gpu_draws,
            ..Default::default()
        }
    }
    pub fn storage_buffers(&self) -> &[wgpu::Buffer] {
        self.buffers.as_ref().map_or(&[], |b| &b.buffers)
    }
    /// Call after submission. Retains every actual source lease, storage buffer,
    /// selected view, pipeline and admitted stream even if callers drop the page.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let generation = self.clone();
        queue.on_submitted_work_done(move || drop(generation));
    }
    /// The pass must have this target format, sample count one and Depth24Plus.
    /// Parent owns the original command's binary scissor and depth load/store.
    pub fn encode_draw<'a>(
        &'a self,
        pass: &mut wgpu::RenderPass<'a>,
        draw_index: usize,
    ) -> Result<(), Error> {
        let draw = self.page.draws.get(draw_index).ok_or(Error::InvalidInput)?;
        let buffers = self.buffers.as_ref().ok_or(Error::ResourceDomain)?;
        pass.set_bind_group(0, &buffers.binding, &[]);
        if draw.frame.command().record.kind == SCENE_COMMAND_DRAW_LINE_3D_BATCH {
            pass.set_pipeline(
                self.pipelines
                    .get(&Variant::Line)
                    .ok_or(Error::ResourceDomain)?,
            );
            pass.draw(
                0..6,
                draw.first_record..draw.first_record + draw.record_count,
            );
            return Ok(());
        }
        for record in draw.first_record..draw.first_record + draw.record_count {
            let mesh = &self.page.mesh_draws[record as usize];
            pass.set_bind_group(1, &buffers.materials[record as usize], &[]);
            let variant = Variant::mesh(mesh);
            pass.set_pipeline(self.pipelines.get(&variant).ok_or(Error::ResourceDomain)?);
            if mesh.topology == MESH_3D_EDGE_LIST {
                pass.draw(
                    0..mesh.edge_vertex_count,
                    mesh.edge_offset..mesh.edge_offset + mesh.edge_count,
                );
                if mesh.flags & MESH_3D_EDGE_DISPLAY_OCCLUDED != 0 {
                    pass.set_pipeline(
                        self.pipelines
                            .get(&Variant::OccludedEdge)
                            .ok_or(Error::ResourceDomain)?,
                    );
                    pass.draw(
                        0..mesh.edge_vertex_count,
                        mesh.edge_offset..mesh.edge_offset + mesh.edge_count,
                    );
                }
            } else {
                pass.draw(0..mesh.index_count, record..record + 1);
            }
        }
        Ok(())
    }
    pub fn encode_bundle_draw<'a>(
        &'a self,
        encoder: &mut wgpu::RenderBundleEncoder<'a>,
        draw_index: usize,
    ) -> Result<(), Error> {
        let draw = self.page.draws.get(draw_index).ok_or(Error::InvalidInput)?;
        let buffers = self.buffers.as_ref().ok_or(Error::ResourceDomain)?;
        encoder.set_bind_group(0, &buffers.binding, &[]);
        if draw.frame.command().record.kind == SCENE_COMMAND_DRAW_LINE_3D_BATCH {
            encoder.set_pipeline(
                self.pipelines
                    .get(&Variant::Line)
                    .ok_or(Error::ResourceDomain)?,
            );
            encoder.draw(
                0..6,
                draw.first_record..draw.first_record + draw.record_count,
            );
            return Ok(());
        }
        for record in draw.first_record..draw.first_record + draw.record_count {
            let mesh = &self.page.mesh_draws[record as usize];
            encoder.set_bind_group(1, &buffers.materials[record as usize], &[]);
            encoder.set_pipeline(
                self.pipelines
                    .get(&Variant::mesh(mesh))
                    .ok_or(Error::ResourceDomain)?,
            );
            if mesh.topology == MESH_3D_EDGE_LIST {
                encoder.draw(
                    0..mesh.edge_vertex_count,
                    mesh.edge_offset..mesh.edge_offset + mesh.edge_count,
                );
                if mesh.flags & MESH_3D_EDGE_DISPLAY_OCCLUDED != 0 {
                    encoder.set_pipeline(
                        self.pipelines
                            .get(&Variant::OccludedEdge)
                            .ok_or(Error::ResourceDomain)?,
                    );
                    encoder.draw(
                        0..mesh.edge_vertex_count,
                        mesh.edge_offset..mesh.edge_offset + mesh.edge_count,
                    );
                }
            } else {
                encoder.draw(0..mesh.index_count, record..record + 1);
            }
        }
        Ok(())
    }
}
/// One actual native depth slot. Reuse by exact size belongs to the parent target
/// pool; there is no implicit clear, submit or substitution of color alpha.
#[derive(Clone, Debug)]
pub struct NativeThreeDDepthTarget {
    device: wgpu::Device,
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    size: [u32; 2],
}
impl NativeThreeDDepthTarget {
    pub async fn new(device: &wgpu::Device, size: [u32; 2]) -> Result<Self, Error> {
        if size.contains(&0) {
            return Err(Error::InvalidInput);
        }
        if size
            .into_iter()
            .any(|v| v > device.limits().max_texture_dimension_2d)
        {
            return Err(Error::Capacity);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Native retained target Depth24Plus"),
            size: wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Depth24Plus,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            device: device.clone(),
            texture,
            view,
            size,
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }
    pub fn view(&self) -> &wgpu::TextureView {
        &self.view
    }
    pub fn size(&self) -> [u32; 2] {
        self.size
    }
    /// Source engine accounting uses four bytes per depth pixel; actual backend
    /// physical allocation for Depth24Plus is implementation-defined.
    pub fn native_budget_bytes(&self) -> u64 {
        u64::from(self.size[0]) * u64::from(self.size[1]) * 4
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
