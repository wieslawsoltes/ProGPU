use super::*;
use crate::native_scene::engine_resources::{
    NativeGlyphPaintBindings, NativeVectorPage, PreparedNativeGlyphPage, PreparedNativePathPage,
};
use wgpu::util::DeviceExt;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeVectorGpuMetrics {
    pub vertex_upload_bytes: u64,
    pub index_upload_bytes: u64,
    pub allocated_bytes: u64,
}
/// Actual packed original vector geometry; indices remain page-global, so replay
/// binds the entire page and uses zero base vertex just as the native executor.
pub struct NativeVectorRenderPage {
    pub(super) device: wgpu::Device,
    pub(super) source: Arc<NativeVectorPage>,
    pub(super) vertices: wgpu::Buffer,
    pub(super) indices: wgpu::Buffer,
    pub(super) atlas: wgpu::BindGroup,
    sentinel: wgpu::Texture,
    metrics: NativeVectorGpuMetrics,
}
impl NativeVectorRenderPage {
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn page(&self) -> &Arc<NativeVectorPage> {
        &self.source
    }
    pub fn metrics(&self) -> NativeVectorGpuMetrics {
        self.metrics
    }
    pub fn replay_metrics(&self) -> NativeVectorGpuMetrics {
        NativeVectorGpuMetrics::default()
    }
    pub fn sentinel_texture(&self) -> &wgpu::Texture {
        &self.sentinel
    }
    pub async fn prepare(
        pipelines: &NativePipelineCache,
        source: Arc<NativeVectorPage>,
    ) -> Result<Self, Error> {
        let device = pipelines.device();
        let vertex_bytes = (source.vertices().len() as u64)
            .checked_mul(56)
            .ok_or(Error::Capacity)?;
        let index_bytes = (source.indices().len() as u64)
            .checked_mul(4)
            .ok_or(Error::Capacity)?;
        let maximum = device.limits().max_buffer_size;
        if vertex_bytes == 0 || index_bytes == 0 || vertex_bytes > maximum || index_bytes > maximum
        {
            return Err(Error::Capacity);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let make = |label, contents: &[u8], usage| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents,
                usage,
            })
        };
        let vertices = make(
            "Native immutable analytic vertices",
            bytemuck::cast_slice(source.vertices()),
            wgpu::BufferUsages::VERTEX,
        );
        let indices = make(
            "Native immutable analytic indices",
            bytemuck::cast_slice(source.indices()),
            wgpu::BufferUsages::INDEX,
        );
        // Analytic primitives bind the source's actual zero RGBA sentinel; path
        // draws bind their independent coverage atlas instead.
        let sentinel = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Native analytic sentinel"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let view = sentinel.create_view(&Default::default());
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Native analytic sentinel sampler"),
            lod_min_clamp: 0.0,
            lod_max_clamp: 0.0,
            ..Default::default()
        });
        let atlas = atlas_binding(
            device,
            &pipelines.layouts().image.texture,
            &sampler,
            &[&view],
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            device: device.clone(),
            source,
            vertices,
            indices,
            atlas,
            sentinel,
            metrics: NativeVectorGpuMetrics {
                vertex_upload_bytes: vertex_bytes,
                index_upload_bytes: index_bytes,
                allocated_bytes: vertex_bytes + index_bytes + 4,
            },
        })
    }
}
/// The raster engine owns the exact source atlas and coverage phases. This page
/// adds only the native render binding, without copying or resampling coverage.
pub struct NativePathRenderPage {
    pub(super) source: Arc<PreparedNativePathPage>,
    pub(super) atlas: wgpu::BindGroup,
}
impl NativePathRenderPage {
    pub fn page(&self) -> &Arc<PreparedNativePathPage> {
        &self.source
    }
    pub async fn prepare(
        pipelines: &NativePipelineCache,
        source: Arc<PreparedNativePathPage>,
    ) -> Result<Self, Error> {
        let device = pipelines.device();
        if device != source.device() {
            return Err(Error::ResourceDomain);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let atlas = atlas_binding(
            device,
            &pipelines.layouts().image.texture,
            source.sampler(),
            &[source.view()],
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self { source, atlas })
    }
}
pub(super) fn atlas_binding(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
    views: &[&wgpu::TextureView],
) -> wgpu::BindGroup {
    let mut entries = Vec::with_capacity(1 + views.len());
    entries.push(wgpu::BindGroupEntry {
        binding: 0,
        resource: wgpu::BindingResource::Sampler(sampler),
    });
    for (i, view) in views.iter().enumerate() {
        entries.push(wgpu::BindGroupEntry {
            binding: i as u32 + 1,
            resource: wgpu::BindingResource::TextureView(view),
        });
    }
    layout::bind(device, layout, &entries)
}

pub struct NativeGlyphRenderPage {
    pub(super) source: Arc<PreparedNativeGlyphPage>,
    pub(super) atlas: wgpu::BindGroup,
    pub(super) empty_mask: wgpu::BindGroup,
    pub(super) paints: Option<Arc<NativeGlyphPaintBindings>>,
}
impl NativeGlyphRenderPage {
    pub fn page(&self) -> &Arc<PreparedNativeGlyphPage> {
        &self.source
    }
    pub fn paint_bindings(&self) -> Option<&Arc<NativeGlyphPaintBindings>> {
        self.paints.as_ref()
    }
    pub async fn prepare(
        pipelines: &NativePipelineCache,
        source: Arc<PreparedNativeGlyphPage>,
        paints: Option<Arc<NativeGlyphPaintBindings>>,
    ) -> Result<Self, Error> {
        let device = pipelines.device();
        if device != source.device()
            || paints
                .as_ref()
                .is_some_and(|p| p.device() != device || !Arc::ptr_eq(p.page(), &source))
        {
            return Err(Error::ResourceDomain);
        }
        let count = source.page().source().paints().len();
        if count != paints.as_ref().map_or(0, |p| p.bindings().len()) {
            return Err(Error::InvalidInput);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let atlas = atlas_binding(
            device,
            &pipelines.layouts().text_atlas,
            source.sampler(),
            &[source.gray_view(), source.color_view()],
        );
        let empty_mask = layout::bind(device, &pipelines.layouts().empty, &[]);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            source,
            atlas,
            empty_mask,
            paints,
        })
    }
}
/// Paint group zero is shared for this exact frame/material/paint-buffer tuple,
/// rather than allocating an identical binding for each glyph draw.
pub struct NativeGlyphFrameBindings {
    pub(super) page: Arc<NativeGlyphRenderPage>,
    pub(super) frame: Arc<NativeFrameBindings>,
    pub(super) paint: Option<wgpu::BindGroup>,
}
impl NativeGlyphFrameBindings {
    pub fn page(&self) -> &Arc<NativeGlyphRenderPage> {
        &self.page
    }
    pub fn frame(&self) -> &Arc<NativeFrameBindings> {
        &self.frame
    }
    pub async fn prepare(
        pipelines: &NativePipelineCache,
        page: Arc<NativeGlyphRenderPage>,
        frame: Arc<NativeFrameBindings>,
    ) -> Result<Self, Error> {
        let device = pipelines.device();
        if device != page.source.device()
            || device != frame.material().device()
            || !Arc::ptr_eq(
                page.source.page().source().scene(),
                frame.material().brushes().scene(),
            )
        {
            return Err(Error::ResourceDomain);
        }
        let paint = if page.source.page().source().paints().is_empty() {
            None
        } else {
            let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
            let material = frame.material();
            let binding = layout::bind(
                device,
                &pipelines.layouts().glyph_paint,
                &[
                    layout::buffer_entry(0, &frame.buffer),
                    layout::buffer_entry(1, &material.brush_buffer),
                    layout::buffer_entry(2, &material.gradient_buffer),
                    layout::buffer_entry(3, page.source.paints()),
                ],
            );
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            Some(binding)
        };
        Ok(Self { page, frame, paint })
    }
}
