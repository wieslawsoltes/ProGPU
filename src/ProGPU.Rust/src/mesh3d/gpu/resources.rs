//! Source generation cache admission and immutable viewport attachment owners.
use super::*;
pub(super) struct GeometryBuffer {
    pub buffer: wgpu::Buffer,
    pub count: u32,
    source: MeshGeometry3D,
}
pub(super) struct RecordBuffers {
    pub records: wgpu::Buffer,
    pub indices: wgpu::Buffer,
    _source: Arc<Mesh3DViewport>,
    generation: u64,
    opacity: u32,
    count: usize,
}
pub(super) struct EdgeBuffer {
    pub buffer: wgpu::Buffer,
    _source: Arc<Mesh3DViewport>,
    generation: u64,
    count: usize,
}
#[derive(Default)]
pub(super) struct ViewportCache {
    records: Option<Arc<RecordBuffers>>,
    edges: Option<Arc<EdgeBuffer>>,
    gradients: Arc<[MeshGradientStop]>,
}
pub(super) struct Targets {
    pub color: wgpu::Texture,
    pub color_view: wgpu::TextureView,
    _depth: wgpu::Texture,
    pub depth_view: wgpu::TextureView,
    _msaa: Option<wgpu::Texture>,
    pub msaa_view: Option<wgpu::TextureView>,
    pub bytes: u64,
}
impl Targets {
    pub fn new(device: &wgpu::Device, size: [u32; 2], samples: u32) -> Self {
        let create = |format, sample_count, usage, label| {
            device.create_texture(&wgpu::TextureDescriptor {
                label: Some(label),
                size: wgpu::Extent3d {
                    width: size[0],
                    height: size[1],
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count,
                dimension: wgpu::TextureDimension::D2,
                format,
                usage,
                view_formats: &[],
            })
        };
        let color = create(
            wgpu::TextureFormat::Rgba8Unorm,
            1,
            wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            "ProGPU owned Mesh3D RGBA8 target",
        );
        let depth = create(
            wgpu::TextureFormat::Depth24PlusStencil8,
            samples,
            wgpu::TextureUsages::RENDER_ATTACHMENT,
            "ProGPU owned Mesh3D depth stencil",
        );
        let msaa = (samples > 1).then(|| {
            create(
                wgpu::TextureFormat::Rgba8Unorm,
                samples,
                wgpu::TextureUsages::RENDER_ATTACHMENT,
                "ProGPU owned Mesh3D MSAA color",
            )
        });
        let bytes = u64::from(size[0])
            * u64::from(size[1])
            * 4
            * (1 + u64::from(samples) + if samples > 1 { u64::from(samples) } else { 0 });
        Self {
            color_view: color.create_view(&Default::default()),
            depth_view: depth.create_view(&Default::default()),
            msaa_view: msaa.as_ref().map(|v| v.create_view(&Default::default())),
            color,
            _depth: depth,
            _msaa: msaa,
            bytes,
        }
    }
}
pub(super) fn check_buffer(
    device: &wgpu::Device,
    count: usize,
    stride: usize,
) -> Result<u64, Error> {
    let size = count.checked_mul(stride).ok_or(Error::Capacity)? as u64;
    if size > device.limits().max_buffer_size {
        return Err(Error::Capacity);
    }
    Ok(size)
}
pub(super) fn check_storage(
    device: &wgpu::Device,
    count: usize,
    stride: usize,
) -> Result<u64, Error> {
    let size = check_buffer(device, count, stride)?;
    if size > u64::from(device.limits().max_storage_buffer_binding_size) {
        return Err(Error::Capacity);
    }
    Ok(size)
}
pub(super) fn initialized(
    device: &wgpu::Device,
    label: &str,
    bytes: &[u8],
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents: if bytes.is_empty() { &[0; 4] } else { bytes },
        usage,
    })
}
fn doubled(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    label: &str,
    bytes: &[u8],
    usage: wgpu::BufferUsages,
) -> Result<wgpu::Buffer, Error> {
    let size = (bytes.len() as u64)
        .checked_mul(2)
        .ok_or(Error::Capacity)?
        .max(4);
    if size > device.limits().max_buffer_size
        || (usage.contains(wgpu::BufferUsages::STORAGE)
            && size > u64::from(device.limits().max_storage_buffer_binding_size))
    {
        return Err(Error::Capacity);
    }
    let buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(label),
        size,
        usage: usage | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    if !bytes.is_empty() {
        queue.write_buffer(&buffer, 0, bytes);
    }
    Ok(buffer)
}
impl Mesh3DEngine {
    pub(super) fn geometry(
        &mut self,
        source: &MeshGeometry3D,
        metrics: &mut Mesh3DMetrics,
    ) -> Result<Arc<GeometryBuffer>, Error> {
        if let Some(cached) = self
            .geometries
            .get(&source.identity())
            .filter(|v| v.source.version() == source.version())
        {
            metrics.geometry_cache_hits = metrics
                .geometry_cache_hits
                .checked_add(1)
                .ok_or(Error::Capacity)?;
            return Ok(cached.clone());
        }
        let vertices = source.deindexed()?;
        check_buffer(&self.device, vertices.len(), 32)?;
        let buffer = initialized(
            &self.device,
            "ProGPU immutable deindexed Mesh3D geometry",
            bytemuck::cast_slice(vertices.as_ref()),
            wgpu::BufferUsages::VERTEX,
        );
        let result = Arc::new(GeometryBuffer {
            buffer,
            count: u32::try_from(vertices.len()).map_err(|_| Error::Capacity)?,
            source: source.clone(),
        });
        metrics.geometry_cache_misses = metrics
            .geometry_cache_misses
            .checked_add(1)
            .ok_or(Error::Capacity)?;
        metrics.geometry_vertex_upload_bytes = metrics
            .geometry_vertex_upload_bytes
            .checked_add((vertices.len() as u64) * 32)
            .ok_or(Error::Capacity)?;
        self.geometries
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        self.geometries.insert(source.identity(), result.clone());
        Ok(result)
    }
    pub(super) fn gradient_snapshot(
        &mut self,
        source: &[MeshGradientStop],
        reused: bool,
    ) -> Result<Arc<[MeshGradientStop]>, Error> {
        let cache = self
            .viewports
            .get_mut(self.viewport_index)
            .ok_or(Error::ResourceDomain)?;
        let count = cache
            .gradients
            .len()
            .max(if reused { 1 } else { source.len() });
        let mut values = Vec::new();
        values
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        values.extend_from_slice(&cache.gradients);
        values.resize(count, MeshGradientStop::default());
        if reused {
            values[0] = MeshGradientStop::default();
        } else {
            values[..source.len()].copy_from_slice(source);
        }
        let snapshot: Arc<[MeshGradientStop]> = values.into();
        cache.gradients = snapshot.clone();
        Ok(snapshot)
    }
    pub(super) fn prepare_record_buffers(
        &mut self,
        request: &Mesh3DRequest,
        records: &Mesh3DRecords,
        metrics: &mut Mesh3DMetrics,
    ) -> Result<(Arc<RecordBuffers>, Option<Arc<EdgeBuffer>>), Error> {
        check_storage(&self.device, records.records.len(), 560)?;
        check_buffer(&self.device, records.edges.len(), 80)?;
        check_storage(&self.device, records.lights.len(), 80)?;
        check_storage(&self.device, records.gradient_stops.len(), 32)?;
        if self.viewports.len() <= self.viewport_index {
            self.viewports.try_reserve(1).map_err(|_| Error::Capacity)?;
            self.viewports.push(ViewportCache::default());
        }
        let cache = &mut self.viewports[self.viewport_index];
        let dynamic = request
            .viewport
            .meshes
            .iter()
            .any(|m| m.texture_source.is_some());
        let reusable = cache.records.as_ref().filter(|r| {
            !dynamic
                && request.viewport.record_generation != 0
                && r.generation == request.viewport.record_generation
                && r.count == records.records.len()
                && r.opacity == request.opacity.to_bits()
                && Arc::ptr_eq(&r._source, &request.viewport)
        });
        let selected = if let Some(reused) = reusable {
            reused.clone()
        } else {
            let result = Arc::new(RecordBuffers {
                records: doubled(
                    &self.device,
                    &self.queue,
                    "ProGPU retained Mesh3D material generation",
                    bytemuck::cast_slice(&records.records),
                    wgpu::BufferUsages::STORAGE,
                )?,
                indices: doubled(
                    &self.device,
                    &self.queue,
                    "ProGPU retained Mesh3D record ordinals",
                    bytemuck::cast_slice(&records.record_indices),
                    wgpu::BufferUsages::VERTEX,
                )?,
                _source: request.viewport.clone(),
                generation: request.viewport.record_generation,
                opacity: request.opacity.to_bits(),
                count: records.records.len(),
            });
            metrics.record_upload_bytes = (records.records.len() as u64) * 560;
            metrics.record_index_upload_bytes = (records.record_indices.len() as u64) * 4;
            cache.records = Some(result.clone());
            result
        };
        let edge = if records.edges.is_empty() {
            None
        } else if let Some(existing) = cache.edges.as_ref().filter(|e| {
            request.viewport.scene_generation != 0
                && e.generation == request.viewport.scene_generation
                && e.count == records.edges.len()
                && Arc::ptr_eq(&e._source, &request.viewport)
        }) {
            Some(existing.clone())
        } else {
            let result = Arc::new(EdgeBuffer {
                buffer: doubled(
                    &self.device,
                    &self.queue,
                    "ProGPU retained Mesh3D edge generation",
                    bytemuck::cast_slice(&records.edges),
                    wgpu::BufferUsages::VERTEX,
                )?,
                _source: request.viewport.clone(),
                generation: request.viewport.scene_generation,
                count: records.edges.len(),
            });
            metrics.edge_upload_bytes = (records.edges.len() as u64) * 80;
            cache.edges = Some(result.clone());
            Some(result)
        };
        Ok((selected, edge))
    }
}
