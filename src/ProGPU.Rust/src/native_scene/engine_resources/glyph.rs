//! Native immutable glyph pages, retaining original resources and draw ordering.
use super::*;
use crate::native_scene::{
    engine_materials::{NativeBrushPage, NativeTextStylePage, native_glyph_payload},
    execution::{self, NativeCommandFrame},
    semantic::{glyph_scan, text, values},
};
mod compile;
mod cpu;
pub use cpu::{
    NativeGlyphCpuCoverage, NativeGlyphCpuImplementation, NativeGlyphCpuMetrics,
    NativeGlyphCpuPolicy, NativeGlyphCpuUpload,
};
#[cfg(feature = "gpu")]
mod gpu;
mod raster;
#[cfg(feature = "gpu")]
pub use gpu::{NativeGlyphPageEngine, PreparedNativeGlyphPage};
#[cfg(feature = "gpu")]
mod paint;
#[cfg(feature = "gpu")]
pub use paint::{NativeGlyphPaintBindings, NativeGlyphPaintTextureBinding};
pub use raster::{NativeColorGlyphRaster, NativeGlyphRaster, NativeGlyphRasterPage};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeGlyphPageMetrics {
    pub draw_count: u64,
    pub glyph_count: u64,
    pub source_outline_count: u64,
    pub source_segment_count: u64,
    pub shared_outline_count: u64,
    pub shared_segment_count: u64,
    pub coverage_bytes: u64,
    pub instance_bytes: u64,
    pub color_bitmap_count: u64,
    pub color_pixel_bytes: u64,
    pub paint_bytes: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeGlyphDrawRange {
    pub command_index: u32,
    pub draw: crate::native_scene::draw::NativeGlyphDraw,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeGlyphPaintResource {
    pub resource_id: u64,
    pub generation: u64,
}
#[derive(Clone, Debug)]
pub struct NativeGlyphPage {
    scene: Arc<NativeScene>,
    frames: Arc<[NativeCommandFrame]>,
    styles: NativeTextStylePage,
    materials: NativeBrushPage,
    outlines: Arc<[wire::GlyphOutline]>,
    segments: Arc<[wire::PathSegment]>,
    glyphs: Arc<[wire::PositionedGlyph]>,
    style_indices: Arc<[u32]>,
    paint_indices: Arc<[u32]>,
    rendering_modes: Arc<[u32]>,
    paints: Arc<[wire::GlyphPaint]>,
    paint_resources: Arc<[NativeGlyphPaintResource]>,
    external_images: Arc<[NativeExternalImageInfo]>,
    color_bitmaps: Arc<[wire::ColorGlyphBitmap]>,
    color_pixels: Arc<[u8]>,
    color_bitmap_indices: Arc<[u32]>,
    draws: Arc<[NativeGlyphDrawRange]>,
    metrics: NativeGlyphPageMetrics,
    engine_flags: u32,
}
impl NativeGlyphPage {
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn frames(&self) -> &[NativeCommandFrame] {
        &self.frames
    }
    pub fn outlines(&self) -> &[wire::GlyphOutline] {
        &self.outlines
    }
    pub fn segments(&self) -> &[wire::PathSegment] {
        &self.segments
    }
    pub fn glyphs(&self) -> &[wire::PositionedGlyph] {
        &self.glyphs
    }
    pub fn style_indices(&self) -> &[u32] {
        &self.style_indices
    }
    pub fn paint_indices(&self) -> &[u32] {
        &self.paint_indices
    }
    pub fn rendering_modes(&self) -> &[u32] {
        &self.rendering_modes
    }
    pub fn paints(&self) -> &[wire::GlyphPaint] {
        &self.paints
    }
    pub fn paint_resources(&self) -> &[NativeGlyphPaintResource] {
        &self.paint_resources
    }
    pub fn external_images(&self) -> &[NativeExternalImageInfo] {
        &self.external_images
    }
    pub fn color_bitmaps(&self) -> &[wire::ColorGlyphBitmap] {
        &self.color_bitmaps
    }
    pub fn color_pixels(&self) -> &[u8] {
        &self.color_pixels
    }
    pub fn color_bitmap_indices(&self) -> &[u32] {
        &self.color_bitmap_indices
    }
    pub fn draws(&self) -> &[NativeGlyphDrawRange] {
        &self.draws
    }
    pub fn text_styles(&self) -> &NativeTextStylePage {
        &self.styles
    }
    pub fn materials(&self) -> &NativeBrushPage {
        &self.materials
    }
    pub fn metrics(&self) -> NativeGlyphPageMetrics {
        self.metrics
    }
    pub fn engine_flags(&self) -> u32 {
        self.engine_flags
    }
}
fn push<T>(v: &mut Vec<T>, value: T) -> Result<(), Error> {
    v.try_reserve(1).map_err(|_| Error::Capacity)?;
    v.push(value);
    Ok(())
}
fn extend<T: Copy>(v: &mut Vec<T>, values: &[T]) -> Result<(), Error> {
    v.try_reserve(values.len()).map_err(|_| Error::Capacity)?;
    v.extend_from_slice(values);
    Ok(())
}
fn read<T: WireRecord>(bytes: &[u8], index: usize) -> Result<T, Error> {
    index
        .checked_mul(T::WIRE_SIZE)
        .and_then(|i| bytes.get(i..))
        .and_then(T::read)
        .ok_or(Error::InvalidInput)
}
fn read_all<T: WireRecord>(bytes: &[u8]) -> Result<Vec<T>, Error> {
    if bytes.len() % T::WIRE_SIZE != 0 {
        return Err(Error::InvalidInput);
    }
    let mut result = allocate(bytes.len() / T::WIRE_SIZE)?;
    for b in bytes.chunks_exact(T::WIRE_SIZE) {
        result.push(T::read(b).ok_or(Error::InvalidInput)?);
    }
    Ok(result)
}
fn count_u32(n: usize) -> Result<u32, Error> {
    u32::try_from(n).map_err(|_| Error::Capacity)
}
fn add(total: &mut u64, value: u64) -> Result<(), Error> {
    *total = total.checked_add(value).ok_or(Error::Capacity)?;
    Ok(())
}
