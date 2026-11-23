//! Immutable original layer-quad GPU page and exact raw-FNV warm upload key.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLayerCompositeMetrics {
    pub vertex_upload_bytes: u64,
    pub index_upload_bytes: u64,
    pub allocated_bytes: u64,
    pub buffer_allocations: u64,
}
#[derive(Clone)]
pub struct PreparedNativeLayerComposite {
    device: wgpu::Device,
    vertices: Arc<[VectorVertex]>,
    buffer: wgpu::Buffer,
    indices: wgpu::Buffer,
    hash: u64,
    capacity: u64,
    metrics: NativeLayerCompositeMetrics,
}
pub struct NativeLayerCompositeEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    cache: Option<PreparedNativeLayerComposite>,
    indices: Option<wgpu::Buffer>,
    capacity: u64,
}
impl NativeLayerCompositeEngine {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        Self {
            device: device.clone(),
            queue: queue.clone(),
            cache: None,
            indices: None,
            capacity: 0,
        }
    }
    pub fn clear_cache(&mut self) {
        self.cache = None;
    }
    pub fn preflight(&self, vertices: &[VectorVertex]) -> Result<u64, Error> {
        if vertices.is_empty() || vertices.len() % 4 != 0 || vertices.len() > u32::MAX as usize {
            return Err(Error::InvalidInput);
        }
        let bytes = (vertices.len() as u64)
            .checked_mul(56)
            .ok_or(Error::Capacity)?;
        capacity(
            self.capacity,
            bytes,
            256,
            self.device.limits().max_buffer_size,
        )
    }
    pub async fn prepare(
        &mut self,
        vertices: Arc<[VectorVertex]>,
    ) -> Result<PreparedNativeLayerComposite, Error> {
        let capacity = self.preflight(&vertices)?;
        let data = bytemuck::cast_slice(&vertices);
        let hash = crate::native_scene::identity::raw_fnv1a64(data);
        if let Some(old) = &self.cache {
            if old.hash == hash && old.vertices.len() == vertices.len() {
                let mut result = old.clone();
                result.vertices = vertices;
                result.metrics = Default::default();
                return Ok(result);
            }
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let buffer = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Native immutable layer composite vertices"),
            size: capacity,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        self.queue.write_buffer(&buffer, 0, data);
        let mut metrics = NativeLayerCompositeMetrics {
            vertex_upload_bytes: data.len() as u64,
            allocated_bytes: capacity,
            buffer_allocations: 1,
            ..Default::default()
        };
        let indices = if let Some(indices) = &self.indices {
            indices.clone()
        } else {
            let indices = self.device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Native layer shared quad indices"),
                size: 24,
                usage: wgpu::BufferUsages::INDEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            self.queue
                .write_buffer(&indices, 0, bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]));
            metrics.index_upload_bytes = 24;
            metrics.allocated_bytes += 24;
            metrics.buffer_allocations += 1;
            indices
        };
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let result = PreparedNativeLayerComposite {
            device: self.device.clone(),
            vertices,
            buffer,
            indices: indices.clone(),
            hash,
            capacity,
            metrics,
        };
        self.capacity = capacity;
        self.indices = Some(indices);
        self.cache = Some(result.clone());
        Ok(result)
    }
}
impl PreparedNativeLayerComposite {
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn vertices(&self) -> &[VectorVertex] {
        &self.vertices
    }
    pub fn buffer(&self) -> &wgpu::Buffer {
        &self.buffer
    }
    pub fn indices(&self) -> &wgpu::Buffer {
        &self.indices
    }
    pub fn capacity(&self) -> u64 {
        self.capacity
    }
    pub fn metrics(&self) -> NativeLayerCompositeMetrics {
        self.metrics
    }
    pub fn encode_quad<'a>(
        &'a self,
        pass: &mut wgpu::RenderPass<'a>,
        first_vertex: u32,
    ) -> Result<(), Error> {
        if first_vertex % 4 != 0
            || first_vertex as usize + 4 > self.vertices.len()
            || first_vertex > i32::MAX as u32
        {
            return Err(Error::InvalidInput);
        }
        pass.set_vertex_buffer(0, self.buffer.slice(..));
        pass.set_index_buffer(self.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..6, first_vertex as i32, 0..1);
        Ok(())
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
fn capacity(current: u64, required: u64, initial: u64, maximum: u64) -> Result<u64, Error> {
    if maximum == 0 || required > maximum || current > maximum {
        return Err(Error::Capacity);
    }
    let mut capacity = initial.max(1).min(maximum).max(current);
    while capacity < required {
        capacity = if capacity > maximum / 2 {
            maximum
        } else {
            capacity * 2
        };
    }
    Ok(capacity)
}
