//! Original explicit cold CPU arena uploads into a privately owned atlas.
use super::*;
impl NativeGlyphPageEngine {
    pub(super) fn preflight_cpu(
        &self,
        page: &NativeGlyphRasterPage,
    ) -> Result<NativeRasterResourceMetrics, Error> {
        if page.has_binary_glyphs() {
            return Err(Error::UnsupportedExecution);
        }
        let limits = self.device.limits();
        let instances = page.instances().len() as u64 * 96;
        let paints = page.source().paints().len() as u64 * 96;
        if page.atlas_size() > limits.max_texture_dimension_2d
            || page.color_atlas_size() > limits.max_texture_dimension_2d
            || instances > limits.max_buffer_size
            || paints
                > limits
                    .max_buffer_size
                    .min(limits.max_storage_buffer_binding_size)
            || usize::try_from(page.coverage_staging_bytes()).is_err()
        {
            return Err(Error::Capacity);
        }
        let mut color_bytes = 0u64;
        for bitmap in page.source().color_bitmaps() {
            color_bytes = color_bytes
                .checked_add(
                    u64::from(bitmap.row_bytes) * u64::from(bitmap.height - 1)
                        + u64::from(bitmap.width) * 4,
                )
                .ok_or(Error::Capacity)?;
        }
        Ok(NativeRasterResourceMetrics {
            instance_upload_bytes: instances,
            // Original CPU route reports the complete arena as outline upload.
            outline_upload_bytes: page.coverage_staging_bytes(),
            uniform_upload_bytes: paints,
            texture_upload_bytes: color_bytes,
            texture_bytes: u64::from(page.atlas_size()).pow(2)
                + u64::from(page.color_atlas_size().max(1)).pow(2) * 4,
            coverage_staging_bytes: page.coverage_staging_bytes(),
            raster_count: page.work_indices().len() as u64,
            compute_passes: 0,
            raster_passes: 0,
            ..Default::default()
        })
    }
    pub(super) async fn prepare_cpu(
        &mut self,
        page: Arc<NativeGlyphRasterPage>,
        metrics: NativeRasterResourceMetrics,
    ) -> Result<Arc<PreparedNativeGlyphPage>, Error> {
        // Complete all original crossings, row/record padding and owned source
        // ranges before any atlas mutation. No binary or RGB scalar substitution.
        let coverage = NativeGlyphCpuCoverage::compile(page.clone())?;
        let mut paint_bytes = allocate(page.source().paints().len() * 96)?;
        paint_bytes.resize(page.source().paints().len() * 96, 0);
        for (paint, bytes) in page
            .source()
            .paints()
            .iter()
            .zip(paint_bytes.chunks_exact_mut(96))
        {
            paint.write_into(bytes).map_err(|_| Error::InvalidInput)?;
        }
        let mut colors = allocate(page.source().color_bitmaps().len())?;
        for bitmap in page.source().color_bitmaps() {
            let count = u64::from(bitmap.row_bytes) * u64::from(bitmap.height - 1)
                + u64::from(bitmap.width) * 4;
            let start = usize::try_from(bitmap.pixel_offset).map_err(|_| Error::Capacity)?;
            let count = usize::try_from(count).map_err(|_| Error::Capacity)?;
            let end = start.checked_add(count).ok_or(Error::Capacity)?;
            if end > page.source().color_pixels().len() {
                return Err(Error::InvalidInput);
            }
            colors.push(start..end);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let gray = common::texture(
            &self.device,
            page.atlas_size(),
            wgpu::TextureFormat::R8Unorm,
        );
        let gray_view = gray.create_view(&Default::default());
        let color = common::texture(
            &self.device,
            page.color_atlas_size().max(1),
            wgpu::TextureFormat::Rgba8Unorm,
        );
        let color_view = color.create_view(&Default::default());
        let sampler = common::sampler(&self.device);
        let instances = common::buffer(
            &self.device,
            "ProGPU native CPU glyph instances",
            bytemuck::cast_slice(page.instances()),
            wgpu::BufferUsages::VERTEX,
            4,
        );
        let paints = common::buffer(
            &self.device,
            "ProGPU native CPU glyph paints",
            &paint_bytes,
            wgpu::BufferUsages::STORAGE,
            96,
        );
        for upload in coverage.uploads() {
            let start = upload.offset as usize;
            let end = start + upload.source_bytes as usize;
            self.queue.write_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: &gray,
                    mip_level: 0,
                    origin: wgpu::Origin3d {
                        x: upload.atlas_origin[0],
                        y: upload.atlas_origin[1],
                        z: 0,
                    },
                    aspect: wgpu::TextureAspect::All,
                },
                &coverage.bytes()[start..end],
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(upload.bytes_per_row),
                    rows_per_image: Some(upload.size[1]),
                },
                wgpu::Extent3d {
                    width: upload.size[0],
                    height: upload.size[1],
                    depth_or_array_layers: 1,
                },
            );
        }
        for ((bitmap, raster), range) in page
            .source()
            .color_bitmaps()
            .iter()
            .zip(page.color_rasters())
            .zip(colors)
        {
            self.queue.write_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: &color,
                    mip_level: 0,
                    origin: wgpu::Origin3d {
                        x: raster.atlas_x,
                        y: raster.atlas_y,
                        z: 0,
                    },
                    aspect: wgpu::TextureAspect::All,
                },
                &page.source().color_pixels()[range],
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(bitmap.row_bytes),
                    rows_per_image: Some(bitmap.height),
                },
                wgpu::Extent3d {
                    width: bitmap.width,
                    height: bitmap.height,
                    depth_or_array_layers: 1,
                },
            );
        }
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Arc::new(PreparedNativeGlyphPage {
            device: self.device.clone(),
            page,
            gray,
            gray_view,
            color,
            color_view,
            sampler,
            instances,
            paints,
            coverage: None,
            _buffers: Vec::new(),
            compute: None,
            binary: None,
            fragment: None,
            cpu_implementation: Some(coverage.implementation()),
            cpu_metrics: Some(coverage.metrics()),
            metrics,
            coverage_ready: false,
        }))
    }
}
