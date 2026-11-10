use super::*;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeMaterialGpuMetrics {
    pub brush_upload_bytes: u64,
    pub gradient_upload_bytes: u64,
    pub style_upload_bytes: u64,
    pub allocated_bytes: u64,
}
/// Immutable source-owned material generation. Old frame bindings retain this
/// allocation when a later scene prepares replacement material pages.
pub struct PreparedNativeMaterials {
    device: wgpu::Device,
    brushes: Arc<NativeBrushPage>,
    styles: Arc<NativeTextStylePage>,
    pub brush_buffer: wgpu::Buffer,
    pub gradient_buffer: wgpu::Buffer,
    pub style_buffer: wgpu::Buffer,
    metrics: NativeMaterialGpuMetrics,
}
impl PreparedNativeMaterials {
    pub fn preflight(
        device: &wgpu::Device,
        brushes: &NativeBrushPage,
        styles: &NativeTextStylePage,
    ) -> Result<NativeMaterialGpuMetrics, Error> {
        if !Arc::ptr_eq(brushes.scene(), styles.scene()) {
            return Err(Error::ResourceDomain);
        }
        let l = device.limits();
        let maximum = l.max_buffer_size.min(l.max_storage_buffer_binding_size);
        let b = record_bytes(brushes.brushes())?;
        let g = record_bytes(brushes.gradient_stops())?;
        let s = record_bytes(styles.styles())?;
        Ok(NativeMaterialGpuMetrics {
            brush_upload_bytes: b,
            gradient_upload_bytes: g,
            style_upload_bytes: s,
            allocated_bytes: capacity(b, 64 * 256, maximum)?
                + capacity(g, 32, maximum)?
                + capacity(s, 32, maximum)?,
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn brushes(&self) -> &Arc<NativeBrushPage> {
        &self.brushes
    }
    pub fn styles(&self) -> &Arc<NativeTextStylePage> {
        &self.styles
    }
    pub fn metrics(&self) -> NativeMaterialGpuMetrics {
        self.metrics
    }
    /// An immutable generation needs no uploads when used again.
    pub fn replay_metrics(&self) -> NativeMaterialGpuMetrics {
        NativeMaterialGpuMetrics::default()
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
    pub async fn prepare(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        brushes: Arc<NativeBrushPage>,
        styles: Arc<NativeTextStylePage>,
    ) -> Result<Self, Error> {
        if !Arc::ptr_eq(brushes.scene(), styles.scene()) {
            return Err(Error::ResourceDomain);
        }
        let limits = device.limits();
        let maximum = limits
            .max_buffer_size
            .min(u64::from(limits.max_storage_buffer_binding_size));
        // Whole CPU/device preflight precedes the first GPU allocation or queue write.
        let brush_size = record_bytes(brushes.brushes())?;
        let gradient_size = record_bytes(brushes.gradient_stops())?;
        let style_size = record_bytes(styles.styles())?;
        let brush_capacity = capacity(brush_size, 64 * 256, maximum)?;
        let gradient_capacity = capacity(gradient_size, 32, maximum)?;
        let style_capacity = capacity(style_size, 32, maximum)?;
        let brush_bytes = encode(brushes.brushes(), brush_size)?;
        let gradient_bytes = encode(brushes.gradient_stops(), gradient_size)?;
        let style_bytes = encode(styles.styles(), style_size)?;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let make = |label, size| {
            device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(label),
                size,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            })
        };
        let brush_buffer = make("Native immutable brush page", brush_capacity);
        let gradient_buffer = make("Native immutable gradient page", gradient_capacity);
        let style_buffer = make("Native immutable text-style page", style_capacity);
        queue.write_buffer(&brush_buffer, 0, &brush_bytes);
        queue.write_buffer(&gradient_buffer, 0, &gradient_bytes);
        queue.write_buffer(&style_buffer, 0, &style_bytes);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            device: device.clone(),
            brushes,
            styles,
            brush_buffer,
            gradient_buffer,
            style_buffer,
            metrics: NativeMaterialGpuMetrics {
                brush_upload_bytes: brush_size,
                gradient_upload_bytes: gradient_size,
                style_upload_bytes: style_size,
                allocated_bytes: brush_capacity + gradient_capacity + style_capacity,
            },
        })
    }
}
fn record_bytes<T: WireRecord>(values: &[T]) -> Result<u64, Error> {
    if values.is_empty() {
        return Err(Error::InvalidInput);
    }
    (values.len() as u64)
        .checked_mul(T::WIRE_SIZE as u64)
        .ok_or(Error::Capacity)
}
fn capacity(required: u64, initial: u64, maximum: u64) -> Result<u64, Error> {
    let mut size = initial;
    while size < required {
        size = size.checked_mul(2).ok_or(Error::Capacity)?;
    }
    if size > maximum {
        return Err(Error::Capacity);
    }
    Ok(size)
}
fn encode<T: WireRecord>(values: &[T], size: u64) -> Result<Vec<u8>, Error> {
    let size = usize::try_from(size).map_err(|_| Error::Capacity)?;
    let mut bytes = Vec::new();
    bytes.try_reserve_exact(size).map_err(|_| Error::Capacity)?;
    bytes.resize(size, 0);
    for (record, target) in values.iter().zip(bytes.chunks_exact_mut(T::WIRE_SIZE)) {
        record.write_into(target).map_err(|_| Error::InvalidInput)?;
    }
    Ok(bytes)
}
