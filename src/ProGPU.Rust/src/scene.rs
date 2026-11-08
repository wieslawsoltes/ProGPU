//! CPU-only retained scene recording and native-compatible path placement.
//! Recording owns immutable path generations. Compilation preserves painter
//! order in one vector mesh and deduplicates raster identities within a frame.
//!
//! ```
//! use progpu::{geometry::AnalyticPrimitive, scene::Scene};
//! let mut scene = Scene::default();
//! scene.draw_analytic(AnalyticPrimitive::rectangle(
//!     [10.0, 20.0, 80.0, 40.0], [0.2, 0.6, 1.0, 1.0],
//! ))?;
//! let compiled = scene.compile(2.0)?; // Actual target DPI; no GPU is needed.
//! assert_eq!(compiled.mesh().indices().len(), 6);
//! # Ok::<(), progpu::Error>(())
//! ```

use crate::{
    Error,
    geometry::{Affine, AnalyticPrimitive, Mesh, VectorVertex},
    path::{Path, RasterRequest},
};
use std::collections::HashMap;

/// Native path sampling and optional explicit source grayscale contrast.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct PathSampling(u32);

impl PathSampling {
    /// Create ordinary sampling with exactly 1, 4 or 8 samples per axis.
    pub fn new(sample_grid: u32) -> Result<Self, Error> {
        if !matches!(sample_grid, 1 | 4 | 8) {
            return Err(Error::InvalidInput);
        }
        Ok(Self(sample_grid))
    }
    /// Select original explicit source grayscale contrast 0..=12. Binary
    /// sampling rejects this metadata; ordinary sampling remains unchanged.
    pub fn with_grayscale_contrast(self, contrast: u8) -> Result<Self, Error> {
        if contrast > 12 || self.sample_grid() == 1 {
            return Err(Error::InvalidInput);
        }
        Ok(Self(self.sample_grid() | ((u32::from(contrast) + 1) << 8)))
    }
    /// Original low-byte sample grid.
    pub fn sample_grid(self) -> u32 {
        self.0 & 0xff
    }
    /// Explicit original source contrast, absent for ordinary rendering.
    pub fn grayscale_contrast(self) -> Option<u8> {
        (self.0 >> 8 != 0).then(|| ((self.0 >> 8) - 1) as u8)
    }
    fn transfer(self) -> f32 {
        if self.0 >> 8 == 0 {
            1.0
        } else {
            -((self.0 >> 8) as f32)
        }
    }
}

impl Default for PathSampling {
    fn default() -> Self {
        Self(8)
    }
}

/// A filled original path with independent paint and placement.
#[derive(Clone, Debug)]
pub struct PathDraw {
    /// Immutable path source and fill rule.
    pub path: Path,
    /// Source-to-target logical transform.
    pub transform: Affine,
    /// Straight RGBA color; finite HDR values are preserved.
    pub color: [f32; 4],
    /// Sampling and explicit source coverage metadata.
    pub sampling: PathSampling,
}

#[derive(Clone, Debug)]
enum Command {
    Analytic(AnalyticPrimitive),
    Path(PathDraw),
}

/// Ordered retained commands; no GPU work occurs while recording or compiling.
#[derive(Clone, Debug, Default)]
pub struct Scene {
    commands: Vec<Command>,
}

impl Scene {
    /// Append an analytic shape, rejecting invalid or singular input atomically.
    pub fn draw_analytic(&mut self, primitive: AnalyticPrimitive) -> Result<(), Error> {
        let [_, minimum] = primitive.transform.scales()?;
        primitive.vertices(1.5 / minimum)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Analytic(primitive));
        Ok(())
    }
    /// Retain the selected path generation without copying segments.
    pub fn draw_path(&mut self, draw: PathDraw) -> Result<(), Error> {
        validate_path_draw(&draw)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Path(draw));
        Ok(())
    }
    /// Number of retained source commands.
    pub fn len(&self) -> usize {
        self.commands.len()
    }
    /// Whether this recorder has no commands.
    pub fn is_empty(&self) -> bool {
        self.commands.is_empty()
    }
    /// Release recorded generations while retaining command capacity.
    pub fn clear(&mut self) {
        self.commands.clear();
    }

    /// Compile one immutable generation at the actual target DPI. O(C + P)
    /// expected work and storage for C commands and P distinct raster requests;
    /// source segment arrays remain shared. Native atlas limits remain in force.
    pub fn compile(&self, dpi_scale: f32) -> Result<CompiledScene, Error> {
        if !dpi_scale.is_finite() || dpi_scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let mut rasters = Vec::new();
        let mut retained = HashMap::new();
        let mut placements = Vec::with_capacity(self.commands.len());
        for command in &self.commands {
            match command {
                Command::Analytic(_) => placements.push(None),
                Command::Path(draw) => {
                    let placement = Placement::new(draw, dpi_scale)?;
                    let key = RasterKey {
                        source: draw.path.identity(),
                        bounds: draw.path.bounds().map(f32::to_bits),
                        fill_rule: draw.path.fill_rule() as u32,
                        scale: placement.request.scale[0].to_bits(),
                        phase: placement.phase.map(f32::to_bits),
                        sampling: draw.sampling.0,
                    };
                    let next = rasters.len();
                    let index = *retained.entry(key).or_insert_with(|| {
                        rasters.push((draw.path.clone(), placement.request));
                        next
                    });
                    placements.push(Some((index, placement)));
                }
            }
        }
        let layout = AtlasLayout::new(rasters.iter().map(|(_, request)| request.size))?;
        let mut mesh = Mesh::default();
        let mut exact_loads = 0;
        for (command, placement) in self.commands.iter().zip(placements) {
            match command {
                Command::Analytic(primitive) => {
                    let [_, minimum] = primitive.transform.scales()?;
                    // Original native analytic execution expands in the local
                    // affine frame; DPI belongs to the final projection.
                    let padding = 1.5 / minimum;
                    mesh.push(primitive, padding)?;
                }
                Command::Path(draw) => {
                    let (index, placement) = placement.ok_or(Error::InvalidInput)?;
                    let vertices = placement.vertices(draw, layout.origins[index], dpi_scale)?;
                    exact_loads += u32::from(vertices[0].stroke_thickness < 0.0);
                    mesh.push_quad(vertices)?;
                }
            }
        }
        // Reject finite-input overflow before publishing the CPU generation.
        if mesh.vertices().iter().any(|v| {
            bytemuck::cast_slice::<_, f32>(std::slice::from_ref(v))
                .iter()
                .any(|v| !v.is_finite())
        }) {
            return Err(Error::InvalidInput);
        }
        Ok(CompiledScene {
            mesh,
            rasters,
            layout,
            dpi_scale,
            exact_loads,
        })
    }
}

/// Frozen CPU compilation that can be prepared on a worker without a GPU.
#[derive(Debug)]
pub struct CompiledScene {
    pub(crate) mesh: Mesh,
    pub(crate) rasters: Vec<(Path, RasterRequest)>,
    pub(crate) layout: AtlasLayout,
    dpi_scale: f32,
    exact_loads: u32,
}

impl CompiledScene {
    /// Original painter-ordered vector records.
    pub fn mesh(&self) -> &Mesh {
        &self.mesh
    }
    /// Exact target DPI used for path placement and projection admission.
    pub fn dpi_scale(&self) -> f32 {
        self.dpi_scale
    }
    /// Number of unique source/scale/phase/sampling raster identities.
    pub fn path_raster_count(&self) -> usize {
        self.rasters.len()
    }
    /// Square retained atlas extent, or zero for an analytic-only scene.
    pub fn atlas_size(&self) -> u32 {
        self.layout.size
    }
    /// Number of path draws admitted to exact integer texel loads.
    pub fn exact_path_draws(&self) -> u32 {
        self.exact_loads
    }
}

#[derive(Hash, PartialEq, Eq)]
struct RasterKey {
    source: usize,
    bounds: [u32; 4],
    fill_rule: u32,
    scale: u32,
    phase: [u32; 2],
    sampling: u32,
}

fn validate_path_draw(draw: &PathDraw) -> Result<(), Error> {
    draw.transform.scales()?;
    let [min_x, min_y, max_x, max_y] = draw.path.bounds();
    if min_x >= max_x || min_y >= max_y || draw.color.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    Ok(())
}

struct Placement {
    request: RasterRequest,
    minimum: [f32; 2],
    maximum: [f32; 2],
    phase: [f32; 2],
}

impl Placement {
    fn new(draw: &PathDraw, dpi_scale: f32) -> Result<Self, Error> {
        validate_path_draw(draw)?;
        let scale = draw.transform.scales()?[0] * dpi_scale;
        if !scale.is_finite() || scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let phase = [
            quantize_phase(draw.transform.0[4] * dpi_scale),
            quantize_phase(draw.transform.0[5] * dpi_scale),
        ];
        if phase.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let bounds = draw.path.bounds();
        let minimum = [
            (bounds[0] * scale).floor() - 4.0,
            (bounds[1] * scale).floor() - 4.0,
        ];
        let maximum = [
            (bounds[2] * scale).ceil() + 4.0,
            (bounds[3] * scale).ceil() + 4.0,
        ];
        let extent = [maximum[0] - minimum[0], maximum[1] - minimum[1]];
        if extent
            .iter()
            .any(|v| !v.is_finite() || *v <= 0.0 || *v > 4092.0)
        {
            return Err(Error::Capacity);
        }
        Ok(Self {
            minimum,
            maximum,
            phase,
            request: RasterRequest {
                origin: [minimum[0] - phase[0], minimum[1] - phase[1]],
                scale: [scale; 2],
                size: extent.map(|v| v as u32),
                sample_grid: draw.sampling.sample_grid(),
            },
        })
    }

    fn vertices(
        &self,
        draw: &PathDraw,
        origin: [u32; 2],
        dpi: f32,
    ) -> Result<[VectorVertex; 4], Error> {
        let scale = self.request.scale[0];
        let [left, top] = self.minimum.map(|v| v / scale);
        let [right, bottom] = self.maximum.map(|v| v / scale);
        let local = [[left, top], [right, top], [right, bottom], [left, bottom]];
        let [x, y] = origin.map(|v| v as f32);
        let [width, height] = self.request.size.map(|v| v as f32);
        let [sx, sy] = self.phase;
        let atlas = [
            [x + sx, y + sy],
            [x + width + sx, y + sy],
            [x + width + sx, y + height + sy],
            [x + sx, y + height + sy],
        ];
        let positions = local.map(|p| draw.transform.transform_point(p));
        let physical = positions.map(|p| [p[0] * dpi, p[1] * dpi]);
        let exact = exact_projection(dpi) && exact_mapping(physical, atlas);
        Ok(std::array::from_fn(|i| VectorVertex {
            position: positions[i],
            color: draw.color,
            texture_coordinate: atlas[i],
            brush_index: 0.0,
            shape_size: local[i],
            corner_radius: draw.sampling.transfer(),
            stroke_thickness: if exact {
                if dpi == 1.0 { -1.0 } else { -2.0 }
            } else {
                0.0
            },
            shape_type: 4.0,
        }))
    }
}

fn quantize_phase(mut value: f32) -> f32 {
    value -= value.floor();
    let quantized = (value * 64.0).round() / 64.0;
    if quantized >= 1.0 { 0.0 } else { quantized }
}

fn exact_projection(dpi: f32) -> bool {
    dpi.is_finite() && dpi >= 1.0 && dpi.to_bits() & 0x007f_ffff == 0
}

fn exact_mapping(positions: [[f32; 2]; 4], atlas: [[f32; 2]; 4]) -> bool {
    let offset = [atlas[0][0] - positions[0][0], atlas[0][1] - positions[0][1]];
    positions.iter().zip(atlas).all(|(p, a)| {
        (0..2).all(|axis| {
            p[axis].abs() <= 8_388_608.0
                && a[axis].abs() <= 8_388_608.0
                && p[axis].trunc() == p[axis]
                && a[axis].trunc() == a[axis]
                && a[axis] - p[axis] == offset[axis]
        })
    })
}

#[derive(Debug)]
pub(crate) struct AtlasLayout {
    pub size: u32,
    pub origins: Vec<[u32; 2]>,
}

impl AtlasLayout {
    pub(crate) fn new(sizes: impl IntoIterator<Item = [u32; 2]>) -> Result<Self, Error> {
        let mut size = 1024;
        let mut x = 2u32;
        let mut y = 2u32;
        let mut row_height = 0;
        let mut origins = Vec::new();
        for [width, height] in sizes {
            if width == 0 || height == 0 || width > 4092 || height > 4092 {
                return Err(Error::Capacity);
            }
            while width + 4 > size && size < 8192 {
                size *= 2;
            }
            if x + width + 2 > size {
                x = 2;
                y = y.checked_add(row_height + 2).ok_or(Error::Capacity)?;
                row_height = 0;
            }
            while y + height + 2 > size && size < 8192 {
                size *= 2;
            }
            if y + height + 2 > size {
                return Err(Error::Capacity);
            }
            origins.push([x, y]);
            x += width + 2;
            row_height = row_height.max(height);
        }
        if origins.is_empty() {
            size = 0;
        }
        Ok(Self { size, origins })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::path::{FillRule, Segment};

    fn draw() -> PathDraw {
        let points = [[0.0, 0.0], [16.0, 0.0], [16.0, 16.0], [0.0, 16.0]];
        let segments: Vec<_> = (0..4)
            .map(|i| Segment::line(points[i], points[(i + 1) % 4]))
            .collect();
        PathDraw {
            path: Path::new(segments, [0.0, 0.0, 16.0, 16.0], FillRule::NonZero).unwrap(),
            transform: Affine::IDENTITY,
            color: [1.0; 4],
            sampling: PathSampling::default(),
        }
    }

    #[test]
    fn raster_identity_keeps_phase_scale_and_sampling_but_shares_integer_moves() {
        let mut scene = Scene::default();
        let mut draw = draw();
        scene.draw_path(draw.clone()).unwrap();
        draw.transform.0[4] = 20.0;
        draw.color = [0.0, 0.0, 1.0, 0.5];
        scene.draw_path(draw.clone()).unwrap();
        draw.transform.0[4] = 20.25;
        scene.draw_path(draw.clone()).unwrap();
        draw.sampling = draw.sampling.with_grayscale_contrast(0).unwrap();
        scene.draw_path(draw.clone()).unwrap();
        draw.transform.0[0] = 2.0;
        scene.draw_path(draw).unwrap();
        let compiled = scene.compile(1.0).unwrap();
        assert_eq!(compiled.path_raster_count(), 4);
        assert_eq!(compiled.mesh().indices().len(), 30);
        assert_eq!(compiled.exact_path_draws(), 2);
        assert_eq!(compiled.mesh().vertices()[12].corner_radius, -1.0);
        scene.clear();
        assert_eq!(compiled.path_raster_count(), 4);
        assert_eq!(compiled.rasters[0].0.segments().len(), 4);
    }

    #[test]
    fn exact_mapping_checks_all_corners_and_the_actual_dpi_projection() {
        let mut scene = Scene::default();
        scene.draw_path(draw()).unwrap();
        for dpi in [1.0, 2.0, 4.0] {
            assert_eq!(scene.compile(dpi).unwrap().exact_path_draws(), 1);
        }
        for dpi in [0.5, 1.25, 3.0] {
            assert_eq!(scene.compile(dpi).unwrap().exact_path_draws(), 0);
        }
        let positions = [[0.0, 0.0], [4.0, 0.0], [4.0, 4.0], [0.0, 4.0]];
        let mut atlas = positions.map(|p| [p[0] + 8.0, p[1] + 4.0]);
        assert!(exact_mapping(positions, atlas));
        atlas[3][0] += 1.0;
        assert!(!exact_mapping(positions, atlas));
        assert_eq!(quantize_phase(-0.0078125), 0.0);
        assert_eq!(quantize_phase(-0.5), 0.5);
    }

    #[test]
    fn native_shelf_grows_without_repacking_old_tiles() {
        let layout = AtlasLayout::new([[1000, 1000], [1000, 1000], [2000, 1000]]).unwrap();
        assert_eq!(layout.origins, [[2, 2], [2, 1004], [2, 2006]]);
        assert_eq!(layout.size, 4096);
        assert!(AtlasLayout::new([[4093, 1]]).is_err());
        assert!(AtlasLayout::new([[4092, 4092]; 5]).is_err());
        assert_eq!(AtlasLayout::new([]).unwrap().size, 0);
    }

    #[test]
    fn invalid_commands_and_source_contrast_reject_atomically() {
        let mut scene = Scene::default();
        let mut draw = draw();
        scene.draw_path(draw.clone()).unwrap();
        draw.transform = Affine([0.0; 6]);
        assert!(scene.draw_path(draw).is_err());
        assert_eq!(scene.len(), 1);
        assert!(PathSampling::new(2).is_err());
        assert!(
            PathSampling::new(1)
                .unwrap()
                .with_grayscale_contrast(0)
                .is_err()
        );
        assert!(PathSampling::default().with_grayscale_contrast(13).is_err());
        assert_eq!(PathSampling::default().grayscale_contrast(), None);
        assert_eq!(
            PathSampling::default()
                .with_grayscale_contrast(12)
                .unwrap()
                .grayscale_contrast(),
            Some(12)
        );
    }
}
