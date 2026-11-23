//! Immutable original indexed vertices and one-batch canonical vector transport.
use super::{Brush, Matrix4};
use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
};
use std::sync::Arc;
use wide::f32x4 as V;
mod patch;
/// Original source/vertex-color blend inventory, identical to texture-patch color
/// blends and independent of destination framebuffer blending.
pub use crate::image::ImageColorBlend as VertexColorBlend;
pub use patch::CoonsPatch;

/// Original triangle-list/alternating-strip/fan topology.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum VertexMode {
    /// Independent consecutive triples.
    #[default]
    Triangles,
    /// Consecutive triples with alternating first/second indices.
    TriangleStrip,
    /// Every consecutive pair after the original first element.
    TriangleFan,
}
#[derive(Debug)]
struct Storage {
    mode: VertexMode,
    positions: Vec<[f32; 2]>,
    texture_coordinates: Vec<[f32; 2]>,
    colors: Vec<[f32; 4]>,
    indices: Vec<u16>,
}
/// Copied or transferred original mesh generation. Clones share immutable storage.
#[derive(Clone, Debug)]
pub struct VertexMesh(Arc<Storage>);
impl VertexMesh {
    /// Copy the complete original inventory once. Empty attribute/index slices
    /// select original absent-attribute/sequential-index behavior.
    pub fn copy(
        mode: VertexMode,
        positions: &[[f32; 2]],
        texture_coordinates: &[[f32; 2]],
        colors: &[[f32; 4]],
        indices: &[u16],
    ) -> Result<Self, Error> {
        validate_lengths(positions.len(), texture_coordinates.len(), colors.len())?;
        Self::from_owned(
            mode,
            copied(positions)?,
            copied(texture_coordinates)?,
            copied(colors)?,
            copied(indices)?,
        )
    }
    /// Transfer fresh complete arrays without another vertex/pixel copy. Colors
    /// remain straight until canonical draw compilation premultiplies them once.
    pub fn from_owned(
        mode: VertexMode,
        positions: Vec<[f32; 2]>,
        texture_coordinates: Vec<[f32; 2]>,
        colors: Vec<[f32; 4]>,
        indices: Vec<u16>,
    ) -> Result<Self, Error> {
        validate_lengths(positions.len(), texture_coordinates.len(), colors.len())?;
        Ok(Self(Arc::new(Storage {
            mode,
            positions,
            texture_coordinates,
            colors,
            indices,
        })))
    }
    /// Original SKVertices byte-color copy; present empty optional arrays retain
    /// the original exact-length admission rather than meaning absent.
    pub fn copy_rgba8(
        mode: VertexMode,
        positions: &[[f32; 2]],
        texture_coordinates: Option<&[[f32; 2]]>,
        colors: Option<&[[u8; 4]]>,
        indices: Option<&[u16]>,
    ) -> Result<Self, Error> {
        if texture_coordinates.is_some_and(|v| v.len() != positions.len())
            || colors.is_some_and(|v| v.len() != positions.len())
        {
            return Err(Error::InvalidInput);
        }
        let mut rgba = Vec::new();
        if let Some(colors) = colors {
            rgba.try_reserve_exact(colors.len())
                .map_err(|_| Error::Capacity)?;
            rgba.extend(
                colors
                    .iter()
                    .map(|v| (V::from(v.map(f32::from)) / V::splat(255.0)).to_array()),
            );
        }
        Self::from_owned(
            mode,
            copied(positions)?,
            copied(texture_coordinates.unwrap_or(&[]))?,
            rgba,
            copied(indices.unwrap_or(&[]))?,
        )
    }
    /// Original topology.
    pub fn mode(&self) -> VertexMode {
        self.0.mode
    }
    /// Immutable original local positions.
    pub fn positions(&self) -> &[[f32; 2]] {
        &self.0.positions
    }
    /// Original optional shader-space coordinates; absent uses local positions.
    pub fn texture_coordinates(&self) -> &[[f32; 2]] {
        &self.0.texture_coordinates
    }
    /// Original optional straight colors; absent uses opaque white.
    pub fn colors(&self) -> &[[f32; 4]] {
        &self.0.colors
    }
    /// Original optional 16-bit indices; invalid triangles are skipped on draw.
    pub fn indices(&self) -> &[u16] {
        &self.0.indices
    }
    /// Original topology count before invalid-index rejection.
    pub fn triangle_count(&self) -> usize {
        let count = if self.0.indices.is_empty() {
            self.0.positions.len()
        } else {
            self.0.indices.len()
        };
        if self.0.mode == VertexMode::Triangles {
            count / 3
        } else {
            count.saturating_sub(2)
        }
    }
    /// Original ordered source triple, including out-of-range source indices.
    /// Returns None only when the triangle ordinal itself is out of range.
    pub fn triangle(&self, ordinal: usize) -> Option<[usize; 3]> {
        if ordinal >= self.triangle_count() {
            return None;
        }
        let slots = match self.0.mode {
            VertexMode::Triangles => [ordinal * 3, ordinal * 3 + 1, ordinal * 3 + 2],
            VertexMode::TriangleFan => [0, ordinal + 1, ordinal + 2],
            VertexMode::TriangleStrip if ordinal & 1 == 0 => [ordinal, ordinal + 1, ordinal + 2],
            VertexMode::TriangleStrip => [ordinal + 1, ordinal, ordinal + 2],
        };
        Some(slots.map(|i| {
            if self.0.indices.is_empty() {
                i
            } else {
                usize::from(self.0.indices[i])
            }
        }))
    }
}
fn validate_lengths(count: usize, tex: usize, colors: usize) -> Result<(), Error> {
    if (tex != 0 && tex != count) || (colors != 0 && colors != count) {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
fn copied<T: Copy>(source: &[T]) -> Result<Vec<T>, Error> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(source.len())
        .map_err(|_| Error::Capacity)?;
    output.extend_from_slice(source);
    Ok(output)
}

/// One original retained mesh draw, preserving material and semantic opacity.
#[derive(Clone, Debug)]
pub struct VertexMeshDraw {
    /// Actual immutable source inventory.
    pub mesh: VertexMesh,
    /// Original shader material, registered before semantic opacity.
    pub brush: Arc<Brush>,
    /// Original row-vector source transform.
    pub transform: Matrix4,
    /// Blend of brush source and interpolated vertex destination colors.
    pub color_blend: VertexColorBlend,
    /// Original ancestor/draw opacity applied after color blending.
    pub semantic_opacity: f32,
    /// Original edge alias flag, encoded separately from topology.
    pub aliased: bool,
}
impl VertexMeshDraw {
    /// Retain one source-over destination draw with original Modulate color blend.
    pub fn new(mesh: VertexMesh, brush: Arc<Brush>) -> Self {
        Self {
            mesh,
            brush,
            transform: Matrix4::IDENTITY,
            color_blend: VertexColorBlend::Modulate,
            semantic_opacity: 1.0,
            aliased: false,
        }
    }
    pub(crate) fn validate(&self) -> Result<(), Error> {
        if !self.semantic_opacity.is_finite()
            || self.transform.0.iter().flatten().any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    pub(crate) fn append(&self, target: &mut Mesh, brush_index: f32) -> Result<(), Error> {
        self.validate()?;
        let triangle_count = self.mesh.triangle_count();
        if triangle_count == 0 {
            return Ok(());
        }
        let count = self.mesh.positions().len();
        u32::try_from(count).map_err(|_| Error::Capacity)?;
        let vertices = self.mesh.positions().iter().enumerate().map(|(i, &p)| {
            let color = self.mesh.colors().get(i).copied().unwrap_or([1.0; 4]);
            let mut premultiplied = (V::from(color) * V::splat(color[3])).to_array();
            premultiplied[3] = color[3];
            let position = point(self.transform, p);
            VectorVertex {
                position,
                color: premultiplied,
                texture_coordinate: self.mesh.texture_coordinates().get(i).copied().unwrap_or(p),
                brush_index,
                shape_size: [0.0; 2],
                corner_radius: self.color_blend as u32 as f32,
                stroke_thickness: self.semantic_opacity,
                shape_type: if self.aliased { 1018.0 } else { 18.0 },
            }
        });
        let indices = (0..triangle_count)
            .filter_map(|i| self.mesh.triangle(i))
            .filter(move |triple| triple.iter().all(|&i| i < count))
            .flat_map(|triple| triple.map(|i| i as u32));
        target.extend_generated(
            vertices,
            indices,
            triangle_count.checked_mul(3).ok_or(Error::Capacity)?,
        )
    }
}
fn point(matrix: Matrix4, p: [f32; 2]) -> [f32; 2] {
    let m = matrix.0;
    let q = (V::splat(p[0]) * V::from([m[0][0], m[0][1], 0.0, 0.0])
        + V::splat(p[1]) * V::from([m[1][0], m[1][1], 0.0, 0.0])
        + V::from([m[3][0], m[3][1], 0.0, 0.0]))
    .to_array();
    [q[0], q[1]]
}
