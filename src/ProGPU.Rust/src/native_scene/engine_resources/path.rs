//! Original native path page compilation and source physical coverage frames.
use super::*;
use crate::native_scene::{
    draw::NativePathDraw,
    engine_materials::NativeBrushPage,
    execution::{self, NativeCommandFrame, NativeScissor, NativeStateCursor},
    semantic::{source_draw, values},
};
mod compile;
#[cfg(feature = "gpu")]
pub(crate) mod gpu;
mod raster;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativePathCoverageCommands, NativePathPageEngine, NativeRasterResourceMetrics,
    PreparedNativePathPage,
};
pub use raster::{
    NativePathCombineUniforms, NativePathRaster, NativePathRasterPage, NativePathUniforms,
};
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourcePathFrame {
    pub physical: bool,
    pub offset_x: f32,
    pub offset_y: f32,
    pub dpi_x: f32,
    pub dpi_y: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePathPageMetrics {
    pub draw_count: u64,
    pub path_count: u64,
    pub segment_count: u64,
    pub boolean_node_count: u64,
    pub vertex_bytes: u64,
    pub index_bytes: u64,
    pub coverage_bytes: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativePathDrawRange {
    pub command_index: u32,
    pub draw: NativePathDraw,
}
#[derive(Clone, Debug)]
pub struct NativePathPage {
    scene: Arc<NativeScene>,
    frames: Arc<[NativeCommandFrame]>,
    materials: NativeBrushPage,
    paths: Arc<[wire::PathFill]>,
    segments: Arc<[wire::PathSegment]>,
    nodes: Arc<[wire::PathBooleanNode]>,
    source_frames: Arc<[NativeSourcePathFrame]>,
    brush_indices: Arc<[u32]>,
    draws: Arc<[NativePathDrawRange]>,
    metrics: NativePathPageMetrics,
}
impl NativePathPage {
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn frames(&self) -> &[NativeCommandFrame] {
        &self.frames
    }
    pub fn materials(&self) -> &NativeBrushPage {
        &self.materials
    }
    pub fn paths(&self) -> &[wire::PathFill] {
        &self.paths
    }
    pub fn segments(&self) -> &[wire::PathSegment] {
        &self.segments
    }
    pub fn boolean_nodes(&self) -> &[wire::PathBooleanNode] {
        &self.nodes
    }
    pub fn source_frames(&self) -> &[NativeSourcePathFrame] {
        &self.source_frames
    }
    pub fn brush_indices(&self) -> &[u32] {
        &self.brush_indices
    }
    pub fn draws(&self) -> &[NativePathDrawRange] {
        &self.draws
    }
    pub fn metrics(&self) -> NativePathPageMetrics {
        self.metrics
    }
}
/// Actual physical lattice admission shared by source paths and source meshes.
pub(crate) fn source_coverage_placement(
    captured: &wire::SourceCoverageFrame,
    state: &wire::State,
    target: NativeScissor,
    presentation: wire::Presentation,
) -> Result<[f32; 2], Error> {
    let t = state.transform;
    if !source_draw::valid_source_coverage_frame(captured)
        || captured.dpi_scale_x != presentation.dpi_scale_x
        || captured.dpi_scale_y != presentation.dpi_scale_y
        || captured.pixel_width != presentation.viewport_width
        || captured.pixel_height != presentation.viewport_height
        || t.m11 != 1.0
        || t.m12 != 0.0
        || t.m21 != 0.0
        || t.m22 != 1.0
    {
        return Err(Error::UnsupportedExecution);
    }
    let x = f64::from(t.m31 * captured.dpi_scale_x) + f64::from(presentation.viewport_x)
        - f64::from(target.x);
    let y = f64::from(t.m32 * captured.dpi_scale_y) + f64::from(presentation.viewport_y)
        - f64::from(target.y);
    if [x, y]
        .into_iter()
        .any(|v| !v.is_finite() || v != v.floor() || !(-16384.0..=16384.0).contains(&v))
    {
        return Err(Error::UnsupportedExecution);
    }
    Ok([x as f32, y as f32])
}
fn push<T>(v: &mut Vec<T>, value: T) -> Result<(), Error> {
    v.try_reserve(1).map_err(|_| Error::Capacity)?;
    v.push(value);
    Ok(())
}
fn read<T: WireRecord>(bytes: &[u8], index: usize) -> Result<T, Error> {
    index
        .checked_mul(T::WIRE_SIZE)
        .and_then(|i| bytes.get(i..))
        .and_then(T::read)
        .ok_or(Error::InvalidInput)
}
fn count_u32(n: usize) -> Result<u32, Error> {
    u32::try_from(n).map_err(|_| Error::Capacity)
}
fn identity() -> wire::Affine2d {
    wire::Affine2d {
        m11: 1.0,
        m22: 1.0,
        ..Default::default()
    }
}
fn material(path: &mut wire::PathFill, brush: &wire::Brush) {
    if brush.kind == SCENE_BRUSH_SOLID {
        path.color = brush.colors[0];
    }
}
