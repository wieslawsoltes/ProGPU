//! All native consumers share the same actual parent-format capture cache.
use super::*;
use crate::native_scene::engine_resources::shader_effects::{
    NativeShaderPictureCapture, NativeShaderPictureResolver,
};
use std::{future::Future, pin::Pin};
impl NativePictureWorkMetrics {
    pub fn image_metrics(&self) -> NativeImageResourceMetrics {
        NativeImageResourceMetrics {
            draws: u64::from(self.frame.draw_call_count),
            vertex_bytes: self.frame.vertex_upload_bytes,
            index_bytes: self.frame.index_upload_bytes,
            texture_bytes: self.texture_bytes,
            texture_upload_bytes: self.frame.texture_upload_bytes,
            uniform_upload_bytes: self.frame.uniform_upload_bytes,
            texture_allocations: self.texture_allocations,
            nested_pictures: u64::from(self.nested_captures),
            live_blur_passes: self.live_blur_passes,
        }
    }
    pub fn mask_metrics(&self) -> Result<NativeMaskMetrics, Error> {
        Ok(NativeMaskMetrics {
            texture_bytes: self.texture_bytes,
            texture_allocation_bytes: self.texture_bytes,
            texture_allocations: self.texture_allocations,
            texture_upload_bytes: self.frame.texture_upload_bytes,
            uniform_upload_bytes: self.frame.uniform_upload_bytes,
            geometry_upload_bytes: self
                .frame
                .vertex_upload_bytes
                .checked_add(self.frame.index_upload_bytes)
                .ok_or(Error::Capacity)?,
            render_passes: self.render_passes,
            path_rasters: self.path_rasters,
            picture_captures: self.nested_captures,
            picture_cache_hits: self.cache_hits,
            compute_passes: self.compute_passes,
            coverage_staging_bytes: self.frame.coverage_staging_bytes,
            ..Default::default()
        })
    }
}
impl NativePictureCapture {
    pub fn image_metrics(&self) -> Result<NativeImageResourceMetrics, Error> {
        self.metrics
            .image_metrics()
            .checked_add(NativeImageResourceMetrics {
                texture_bytes: self.output_allocation_bytes,
                texture_allocations: self.output_allocations,
                nested_pictures: u64::from(!self.cache_hit),
                ..Default::default()
            })
    }
}
impl NativePictureImageResolver for NativePictureRuntime {
    fn preflight(
        &self,
        source: &NativePictureImageSource,
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
    ) -> Result<NativeImageResourceMetrics, Error> {
        self.admit_context(device, None, target_format)?;
        Ok(self.preflight_image(source)?.image_metrics())
    }
    fn prepare<'a>(
        &'a mut self,
        source: NativePictureImageSource,
        device: &'a wgpu::Device,
        queue: &'a wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Pin<Box<dyn Future<Output = Result<NativePictureImageCapture, Error>> + 'a>> {
        Box::pin(async move {
            self.admit_context(device, Some(queue), target_format)?;
            let capture = self.capture_image(source).await?;
            let metrics = capture.image_metrics()?;
            Ok(NativePictureImageCapture {
                texture: capture.texture,
                metrics,
            })
        })
    }
}
impl NativeShaderPictureResolver for NativePictureRuntime {
    fn preflight(
        &self,
        source: &NativePictureImageSource,
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
    ) -> Result<NativeImageResourceMetrics, Error> {
        self.admit_context(device, None, target_format)?;
        Ok(self.preflight_image(source)?.image_metrics())
    }
    fn prepare<'a>(
        &'a mut self,
        source: NativePictureImageSource,
        device: &'a wgpu::Device,
        queue: &'a wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Pin<Box<dyn Future<Output = Result<NativeShaderPictureCapture, Error>> + 'a>> {
        Box::pin(async move {
            self.admit_context(device, Some(queue), target_format)?;
            let capture = self.capture_image(source).await?;
            let metrics = capture.image_metrics()?;
            Ok(NativeShaderPictureCapture {
                texture: capture.texture,
                metrics,
            })
        })
    }
}
impl NativePictureMaskResolver for NativePictureRuntime {
    fn owns_raster_cache(&self) -> bool {
        true
    }
    fn preflight(
        &self,
        source: &NativePictureMaskSource,
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
    ) -> Result<NativeMaskMetrics, Error> {
        self.admit_context(device, None, target_format)?;
        self.preflight_mask(source)?.mask_metrics()
    }
    fn prepare<'a>(
        &'a mut self,
        source: NativePictureMaskSource,
        device: &'a wgpu::Device,
        queue: &'a wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Pin<Box<dyn Future<Output = Result<NativePictureMaskCapture, Error>> + 'a>> {
        Box::pin(async move {
            self.admit_context(device, Some(queue), target_format)?;
            let capture = self.capture_mask(source).await?;
            let mut metrics = capture.metrics.mask_metrics()?;
            metrics.picture_cache_hits = metrics
                .picture_cache_hits
                .checked_add(u32::from(capture.cache_hit))
                .ok_or(Error::Capacity)?;
            metrics.picture_captures = metrics
                .picture_captures
                .checked_add(u32::from(!capture.cache_hit))
                .ok_or(Error::Capacity)?;
            Ok(NativePictureMaskCapture {
                texture: capture.texture,
                metrics,
                output_texture_bytes: capture.output_allocation_bytes,
            })
        })
    }
}
