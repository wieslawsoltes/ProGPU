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
        Self::prepare_retained(device, queue, brushes, styles, None, None).await
    }
    /// Original independent brush/gradient and style ownership hashes. New
    /// wrappers retain the new CPU scene even when one buffer family survives.
    pub(crate) async fn prepare_retained(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        brushes: Arc<NativeBrushPage>,
        styles: Arc<NativeTextStylePage>,
        prior_brush: Option<&Self>,
        prior_style: Option<&Self>,
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
        let retained_brush = prior_brush.filter(|p| {
            p.device == *device
                && brushes.scene_hash() != 0
                && brushes.scene_hash() == p.brushes.scene_hash()
                && brushes.brushes().len() == p.brushes.brushes().len()
                && brushes.gradient_stops().len() == p.brushes.gradient_stops().len()
        });
        let retained_style = prior_style.filter(|p| {
            p.device == *device
                && styles.scene_hash() != 0
                && styles.scene_hash() == p.styles.scene_hash()
                && styles.styles().len() == p.styles.styles().len()
        });
        let brush_bytes = if retained_brush.is_none() {
            encode(brushes.brushes(), brush_size)?
        } else {
            Vec::new()
        };
        let gradient_bytes = if retained_brush.is_none() {
            encode(brushes.gradient_stops(), gradient_size)?
        } else {
            Vec::new()
        };
        let style_bytes = if retained_style.is_none() {
            encode(styles.styles(), style_size)?
        } else {
            Vec::new()
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let make = |label, size| {
            device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(label),
                size,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            })
        };
        let (brush_buffer, gradient_buffer) = if let Some(prior) = retained_brush {
            (prior.brush_buffer.clone(), prior.gradient_buffer.clone())
        } else {
            let b = make("Native immutable brush page", brush_capacity);
            let g = make("Native immutable gradient page", gradient_capacity);
            queue.write_buffer(&b, 0, &brush_bytes);
            queue.write_buffer(&g, 0, &gradient_bytes);
            (b, g)
        };
        let style_buffer = if let Some(prior) = retained_style {
            prior.style_buffer.clone()
        } else {
            let s = make("Native immutable text-style page", style_capacity);
            queue.write_buffer(&s, 0, &style_bytes);
            s
        };
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
                brush_upload_bytes: brush_bytes.len() as u64,
                gradient_upload_bytes: gradient_bytes.len() as u64,
                style_upload_bytes: style_bytes.len() as u64,
                allocated_bytes: if retained_brush.is_none() {
                    brush_capacity + gradient_capacity
                } else {
                    0
                } + if retained_style.is_none() {
                    style_capacity
                } else {
                    0
                },
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
