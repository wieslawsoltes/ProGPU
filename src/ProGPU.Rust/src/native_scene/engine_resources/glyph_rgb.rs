//! Original native explicit RGB occurrence packets. This physical box model is
//! independent of ordinary text/DirectWrite modes and needs actual opaque layers.
use super::*;
use crate::{
    native_scene::{
        execution::{NativeCommandFrame, NativeScissor, NativeStateCursor},
        semantic::{rgb_glyph, values},
    },
    text::glyph::{GlyphUniforms, GpuGlyphRecord},
};
use bytemuck::{Pod, Zeroable};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
mod pipeline;
mod plan;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativeRgbGlyphEngine, NativeRgbGlyphSubmission, NativeRgbMaskSource, NativeRgbTarget,
    PreparedNativeRgbGlyphPage,
};
/// Original CPU replay tile, retaining exact source geometry and physical samples.
#[derive(Clone, Copy, Debug)]
pub struct NativeRgbGlyphTile {
    pub outline: GpuGlyphRecord,
    pub x_start: f32,
    pub y_start: f32,
    pub scale: f32,
    pub subpixel_x: f32,
    pub width: u32,
    pub height: u32,
    pub target_x: i32,
    pub target_y: i32,
    pub foreground: wire::Color,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct NativeRgbInstance {
    pub target_origin: [f32; 2],
    pub extent: [f32; 2],
    pub atlas_origin: [u32; 2],
    pub reserved: [u32; 2],
    pub foreground: [f32; 4],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct NativeRgbCell {
    pub origin: [u32; 2],
    pub extent: [u32; 2],
    pub first: u32,
    pub count: u32,
    pub reserved: [u32; 2],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct NativeRgbCompositeFrame {
    pub extent: [f32; 2],
    pub render_origin: [f32; 2],
    pub backdrop_origin: [u32; 2],
    pub reserved: [u32; 2],
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeRgbGlyphMetrics {
    pub vertex_upload_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub texture_bytes: u64,
    pub coverage_staging_bytes: u64,
    pub draw_calls: u32,
    pub compute_passes: u32,
    pub raster_passes: u32,
    pub destination_copy_bytes: u64,
}
#[derive(Clone, Debug)]
pub struct NativeRgbGlyphPage {
    frame: NativeCommandFrame,
    policy: wire::RgbGlyphDraw,
    scissor: NativeScissor,
    tiles: Arc<[NativeRgbGlyphTile]>,
    segments: Arc<[wire::PathSegment]>,
    records: Arc<[GpuGlyphRecord]>,
    uniforms: Arc<[GlyphUniforms]>,
    instances: Arc<[NativeRgbInstance]>,
    cells: Arc<[NativeRgbCell]>,
    references: Arc<[u32]>,
    composite: NativeRgbCompositeFrame,
    atlas_size: [u32; 2],
    backdrop_size: [u32; 2],
    cell_count: u32,
    coverage_bytes: u64,
    engine_flags: u32,
    source_vertex_bytes: u64,
}
impl NativeRgbGlyphPage {
    pub(crate) fn preflight_translation(
        frame: &NativeCommandFrame,
        draw: &wire::RgbGlyphDraw,
    ) -> Result<[i32; 2], Error> {
        translation(frame, draw)
    }
    /// Uses the ordered cursor's original materialized-target opacity proof.
    /// `scissor` is the exact resolved target-local source clip, optionally
    /// intersected with actual root damage by the owning native executor.
    pub fn compile(
        frame: NativeCommandFrame,
        scissor: NativeScissor,
        max_buffer_size: u64,
        engine_flags: u32,
    ) -> Result<Self, Error> {
        let command = frame.command();
        if command.record.kind != SCENE_COMMAND_DRAW_RGB_GLYPH_RUN {
            return Err(Error::InvalidInput);
        }
        let policy = wire::RgbGlyphDraw::read(command.payload)
            .filter(rgb_glyph::descriptor)
            .ok_or(Error::InvalidInput)?;
        let cursor = NativeStateCursor::new(frame.scene.clone(), frame.source_state_dpi());
        if cursor.has_per_point_guidelines(&frame.state)?
            || frame.target.width > 4096
            || frame.target.height > 4096
            || engine_flags
                & (ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK | ENGINE_GLYPH_SCALAR_CPU_FALLBACK)
                != 0
        {
            return Err(Error::UnsupportedExecution);
        }
        let offset = translation(&frame, &policy)?;
        mask_kind(&frame)?;
        let resource = frame
            .scene
            .envelope()
            .resource(command.record.resource_index)
            .ok_or(Error::InvalidInput)?;
        if resource.record.kind != SCENE_RESOURCE_GLYPH_RUN
            || resource.record.flags & SCENE_COLOR_GLYPH_BITMAPS != 0
            || resource.payload.is_empty()
            || resource.payload.len() % 40 != 0
            || resource.auxiliary.is_empty()
            || resource.auxiliary.len() % 48 != 0
            || resource.payload.len() / 40 > 65536
            || resource.auxiliary.len() / 48 > 1048576
            || command.payload.len() != 40 + policy.glyph_count as usize * 56
        {
            return Err(Error::InvalidInput);
        }
        let mut segments = allocate(resource.auxiliary.len() / 48)?;
        for bytes in resource.auxiliary.chunks_exact(48) {
            let segment = wire::PathSegment::read(bytes)
                .filter(|s| values::segment(s, false))
                .ok_or(Error::InvalidInput)?;
            segments.push(segment);
        }
        let mut covered = 0u64;
        for bytes in resource.payload.chunks_exact(40) {
            let outline = wire::GlyphOutline::read(bytes).ok_or(Error::InvalidInput)?;
            if outline.segment_offset > covered
                || values::glyph_outline(&outline, segments.len() as u64).is_none()
            {
                return Err(Error::InvalidInput);
            }
            covered = covered.max(outline.segment_offset + outline.segment_count);
        }
        if covered != segments.len() as u64 {
            return Err(Error::InvalidInput);
        }
        let mut tiles = allocate(policy.glyph_count as usize)?;
        let mut pixels = 0u64;
        for bytes in command.payload[40..].chunks_exact(56) {
            let tile = wire::RgbGlyphTile::read(bytes).ok_or(Error::InvalidInput)?;
            if !rgb_glyph::tile(&tile, (resource.payload.len() / 40) as u32) {
                return Err(Error::InvalidInput);
            }
            pixels += u64::from(tile.width) * u64::from(tile.height);
            if pixels > 4096 * 4096 {
                return Err(Error::InvalidInput);
            }
            let outline =
                wire::GlyphOutline::read(&resource.payload[tile.outline_index as usize * 40..])
                    .ok_or(Error::InvalidInput)?;
            let target = [
                i64::from(tile.target_x) + i64::from(offset[0]),
                i64::from(tile.target_y) + i64::from(offset[1]),
            ];
            if target.into_iter().any(|n| !(-4096..=4096).contains(&n)) {
                return Err(Error::UnsupportedExecution);
            }
            let mut color = tile.foreground;
            color.a *= frame.source_state.opacity;
            tiles.push(NativeRgbGlyphTile {
                outline: GpuGlyphRecord {
                    start_segment: outline.segment_offset as u32,
                    segment_count: outline.segment_count as u32,
                    bounds: [outline.min_x, outline.min_y, outline.max_x, outline.max_y],
                    padding: [0; 2],
                },
                x_start: tile.x_start,
                y_start: tile.y_start,
                scale: tile.scale,
                subpixel_x: tile.subpixel_x,
                width: tile.width,
                height: tile.height,
                target_x: target[0] as i32,
                target_y: target[1] as i32,
                foreground: color,
            });
        }
        let source_vertex_bytes =
            resource.auxiliary.len() as u64 + u64::from(policy.glyph_count) * (80 + 32 + 256 + 48);
        plan::compile(
            frame,
            policy,
            scissor,
            tiles,
            segments,
            max_buffer_size,
            engine_flags,
            source_vertex_bytes,
        )
    }
    pub fn frame(&self) -> &NativeCommandFrame {
        &self.frame
    }
    pub fn policy(&self) -> wire::RgbGlyphDraw {
        self.policy
    }
    pub fn scissor(&self) -> NativeScissor {
        self.scissor
    }
    pub fn drawable(&self) -> bool {
        self.scissor.drawable && self.scissor.width != 0 && self.scissor.height != 0
    }
    pub fn mask_resource_index(&self) -> Option<u32> {
        (self.frame.source_state.flags & SCENE_STATE_MASK != 0)
            .then_some(self.frame.source_state.mask_resource_index)
    }
    pub fn tiles(&self) -> &[NativeRgbGlyphTile] {
        &self.tiles
    }
    pub fn segments(&self) -> &[wire::PathSegment] {
        &self.segments
    }
    pub fn records(&self) -> &[GpuGlyphRecord] {
        &self.records
    }
    pub fn uniforms(&self) -> &[GlyphUniforms] {
        &self.uniforms
    }
    pub fn instances(&self) -> &[NativeRgbInstance] {
        &self.instances
    }
    pub fn cells(&self) -> &[NativeRgbCell] {
        &self.cells
    }
    pub fn references(&self) -> &[u32] {
        &self.references
    }
    pub fn composite_frame(&self) -> NativeRgbCompositeFrame {
        self.composite
    }
    pub fn cell_count(&self) -> u32 {
        self.cell_count
    }
    pub fn atlas_size(&self) -> [u32; 2] {
        self.atlas_size
    }
    pub fn backdrop_size(&self) -> [u32; 2] {
        self.backdrop_size
    }
    pub fn coverage_bytes(&self) -> u64 {
        self.coverage_bytes
    }
    pub fn source_vertex_bytes(&self) -> u64 {
        self.source_vertex_bytes
    }
    /// Original scene compilation budget, independent of the later destination
    /// snapshot and whether the occurrence clips out at replay.
    pub fn source_texture_bytes(&self) -> u64 {
        u64::from(self.atlas_size[0]) * u64::from(self.atlas_size[1]) * 4
    }
    pub fn engine_flags(&self) -> u32 {
        self.engine_flags
    }
    pub fn metrics(&self) -> NativeRgbGlyphMetrics {
        if !self.drawable() {
            return Default::default();
        }
        let fragment = self.engine_flags & ENGINE_GLYPH_RASTER_SHADER_FALLBACK != 0;
        NativeRgbGlyphMetrics {
            vertex_upload_bytes: self.records.len() as u64 * 32
                + self.segments.len() as u64 * 48
                + self.instances.len() as u64 * 48
                + self.cells.len() as u64 * 32
                + self.references.len() as u64 * 4,
            uniform_upload_bytes: self.uniforms.len() as u64 * 256 + 16 + 32,
            texture_bytes: u64::from(self.atlas_size[0]) * u64::from(self.atlas_size[1]) * 4
                + u64::from(self.backdrop_size[0]) * u64::from(self.backdrop_size[1]) * 4,
            coverage_staging_bytes: if fragment { 0 } else { self.coverage_bytes },
            draw_calls: 3 + if fragment { self.tiles.len() as u32 } else { 0 },
            compute_passes: u32::from(!fragment),
            raster_passes: 1 + u32::from(fragment),
            destination_copy_bytes: u64::from(self.backdrop_size[0])
                * u64::from(self.backdrop_size[1])
                * 4,
        }
    }
}
fn mask_kind(frame: &NativeCommandFrame) -> Result<Option<u32>, Error> {
    if frame.source_state.flags & SCENE_STATE_MASK == 0 {
        return Ok(None);
    }
    let resource = frame
        .scene
        .envelope()
        .resource(frame.source_state.mask_resource_index)
        .ok_or(Error::InvalidInput)?;
    let kind = resource
        .payload
        .get(4..8)
        .and_then(|bytes| <[u8; 4]>::try_from(bytes).ok())
        .map(u32::from_le_bytes)
        .ok_or(Error::InvalidInput)?;
    if !matches!(
        kind,
        SCENE_LAYER_MASK_ROUNDED_RECTANGLE
            | SCENE_LAYER_MASK_COVERAGE_BITMAP
            | SCENE_LAYER_MASK_ANALYTIC_CHAIN
            | SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
            | SCENE_LAYER_MASK_BRUSH
            | SCENE_LAYER_MASK_GEOMETRY
            | SCENE_LAYER_MASK_PICTURE
            | SCENE_LAYER_MASK_COMPOSITE
    ) {
        return Err(Error::UnsupportedExecution);
    }
    Ok(Some(kind))
}
fn translation(frame: &NativeCommandFrame, draw: &wire::RgbGlyphDraw) -> Result<[i32; 2], Error> {
    let t = frame.source_state.transform;
    let p = frame.presentation;
    if !frame.ignores_alpha
        || t.m11 != 1.0
        || t.m12 != 0.0
        || t.m21 != 0.0
        || t.m22 != 1.0
        || draw.dpi_scale != p.dpi_scale_x
        || draw.dpi_scale != p.dpi_scale_y
    {
        return Err(Error::UnsupportedExecution);
    }
    let x = f64::from(t.m31 * draw.dpi_scale) + f64::from(p.viewport_x) - f64::from(frame.target.x);
    let y = f64::from(t.m32 * draw.dpi_scale) + f64::from(p.viewport_y) - f64::from(frame.target.y);
    if [x, y]
        .into_iter()
        .any(|v| !v.is_finite() || v != v.floor() || !(-8192.0..=8192.0).contains(&v))
    {
        return Err(Error::UnsupportedExecution);
    }
    Ok([x as i32, y as i32])
}
