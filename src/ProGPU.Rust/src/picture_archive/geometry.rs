//! Original picture path/mesh records and texture-patch constructor semantics.
use super::{
    ArchiveError, MAX_ARRAY_ELEMENTS, Result, depth,
    io::{Reader, Writer},
};
use crate::{
    geometry::Affine,
    image::{ImageColorBlend, ImagePatch, ImagePatchKind},
    vector::*,
};
use std::sync::Arc;

/// Raw accepted archive state is distinct from a valid deferred render expression.
/// Noncombined Op and combined null operands survive deserialization unchanged.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ArchivePath {
    pub fill_rule: FillRule,
    pub is_combined: bool,
    pub op: i32,
    pub a: Option<Box<ArchivePath>>,
    pub b: Option<Box<ArchivePath>>,
    pub figures: Vec<PathFigure>,
}
impl ArchivePath {
    /// Convert only on explicit request; malformed combined records remain valid
    /// archive records but cannot manufacture renderable Boolean operands.
    pub fn to_geometry(&self) -> Result<PathGeometry> {
        self.to_geometry_at(0)
    }
    fn to_geometry_at(&self, level: u32) -> Result<PathGeometry> {
        depth(level)?;
        let mut geometry = if self.is_combined {
            let operation =
                BooleanOperation::try_from(self.op as u32).map_err(ArchiveError::Drawing)?;
            let a = self
                .a
                .as_ref()
                .ok_or(ArchiveError::Unsupported)?
                .to_geometry_at(level + 1)?;
            let b = self
                .b
                .as_ref()
                .ok_or(ArchiveError::Unsupported)?
                .to_geometry_at(level + 1)?;
            PathGeometry::combine_deferred(Arc::new(a), Arc::new(b), operation)
        } else {
            PathGeometry::default()
        };
        geometry.fill_rule = self.fill_rule;
        geometry.figures = copy(&self.figures)?;
        Ok(geometry)
    }
    /// Capture an actual owned geometry without executing Boolean reconstruction.
    /// Only original four serializable segment families are admitted by writing.
    pub fn from_geometry(geometry: &PathGeometry) -> Result<Self> {
        Self::from_geometry_at(geometry, 0)
    }
    fn from_geometry_at(geometry: &PathGeometry, level: u32) -> Result<Self> {
        depth(level)?;
        let (is_combined, op, a, b) = if let Some(d) = &geometry.deferred {
            let (a, b) = d.operands();
            (
                true,
                d.operation() as i32,
                Some(Box::new(Self::from_geometry_at(a, level + 1)?)),
                Some(Box::new(Self::from_geometry_at(b, level + 1)?)),
            )
        } else {
            (false, 0, None, None)
        };
        Ok(Self {
            fill_rule: geometry.fill_rule,
            is_combined,
            op,
            a,
            b,
            figures: copy(&geometry.figures)?,
        })
    }
}
pub(super) fn write_path(
    writer: &mut Writer,
    path: Option<&ArchivePath>,
    level: u32,
) -> Result<()> {
    writer.write(path.is_some())?;
    let Some(path) = path else {
        return Ok(());
    };
    depth(level)?;
    writer.write(path.fill_rule as i32)?;
    writer.write(path.is_combined)?;
    writer.write(path.op)?;
    if path.is_combined {
        write_path(writer, path.a.as_deref(), level + 1)?;
        write_path(writer, path.b.as_deref(), level + 1)?;
    }
    writer.count(path.figures.len(), MAX_ARRAY_ELEMENTS)?;
    for figure in &path.figures {
        writer.write(figure.start)?;
        writer.write(figure.is_closed)?;
        writer.write(figure.is_filled)?;
        if writer.version >= 2 {
            write_cap(writer, figure.start_cap)?;
            write_cap(writer, figure.end_cap)?;
        }
        writer.count(figure.segments.len(), MAX_ARRAY_ELEMENTS)?;
        for segment in &figure.segments {
            let kind = match segment.kind {
                PathSegmentKind::Line(_) => 0u8,
                PathSegmentKind::Quadratic(_) => 1,
                PathSegmentKind::Cubic(_) => 2,
                PathSegmentKind::Arc(_) => 3,
                _ => return Err(ArchiveError::Unsupported),
            };
            writer.write(kind)?;
            writer.write(segment.is_smooth_join)?;
            writer.write(segment.is_stroked)?;
            match segment.kind {
                PathSegmentKind::Line(v) => writer.write(v)?,
                PathSegmentKind::Quadratic(v) => writer.write(v)?,
                PathSegmentKind::Cubic(v) => writer.write(v)?,
                PathSegmentKind::Arc(a) => {
                    writer.write(a.end)?;
                    writer.write(a.size)?;
                    writer.write(a.rotation)?;
                    writer.write(a.large)?;
                    writer.write(a.sweep as i32)?;
                }
                _ => return Err(ArchiveError::Unsupported),
            }
        }
    }
    Ok(())
}
pub(super) fn read_path(reader: &mut Reader<'_>, level: u32) -> Result<Option<ArchivePath>> {
    if !reader.read::<bool>()? {
        return Ok(None);
    }
    depth(level)?;
    let fill_rule = match reader.enumeration(1)? {
        0 => FillRule::EvenOdd,
        _ => FillRule::Nonzero,
    };
    let is_combined = reader.read()?;
    let op = reader.read()?;
    let (a, b) = if is_combined {
        (
            read_path(reader, level + 1)?.map(Box::new),
            read_path(reader, level + 1)?.map(Box::new),
        )
    } else {
        (None, None)
    };
    let count = reader.count(MAX_ARRAY_ELEMENTS)?;
    let mut figures = reserve(count)?;
    for _ in 0..count {
        let mut figure = PathFigure::new(reader.read()?, reader.read()?);
        figure.is_filled = reader.read()?;
        if reader.version >= 2 {
            figure.start_cap = read_cap(reader)?;
            figure.end_cap = read_cap(reader)?;
        }
        let count = reader.count(MAX_ARRAY_ELEMENTS)?;
        figure.segments = reserve(count)?;
        for _ in 0..count {
            let tag = reader.read::<u8>()?;
            let smooth = reader.read()?;
            let stroked = reader.read()?;
            let kind = match tag {
                0 => PathSegmentKind::Line(reader.read()?),
                1 => PathSegmentKind::Quadratic(reader.read()?),
                2 => PathSegmentKind::Cubic(reader.read()?),
                3 => PathSegmentKind::Arc(ArcSegment {
                    end: reader.read()?,
                    size: reader.read()?,
                    rotation: reader.read()?,
                    large: reader.read()?,
                    sweep: match reader.enumeration(1)? {
                        0 => SweepDirection::Counterclockwise,
                        _ => SweepDirection::Clockwise,
                    },
                }),
                _ => return Err(ArchiveError::InvalidData),
            };
            let mut segment = PathSegment::new(kind);
            segment.is_smooth_join = smooth;
            segment.is_stroked = stroked;
            figure.segments.push(segment);
        }
        figures.push(figure);
    }
    Ok(Some(ArchivePath {
        fill_rule,
        is_combined,
        op,
        a,
        b,
        figures,
    }))
}
pub(super) fn cap(reader: &mut Reader<'_>) -> Result<PenLineCap> {
    Ok(match reader.enumeration(3)? {
        0 => PenLineCap::Flat,
        1 => PenLineCap::Square,
        2 => PenLineCap::Round,
        _ => PenLineCap::Triangle,
    })
}
fn read_cap(reader: &mut Reader<'_>) -> Result<Option<PenLineCap>> {
    if reader.read::<bool>()? {
        Ok(Some(cap(reader)?))
    } else {
        Ok(None)
    }
}
fn write_cap(writer: &mut Writer, cap: Option<PenLineCap>) -> Result<()> {
    writer.write(cap.is_some())?;
    if let Some(cap) = cap {
        writer.write(cap as i32)?;
    }
    Ok(())
}

pub type ArchiveVertexMesh = VertexMesh;
pub(super) fn write_vertex_mesh(
    writer: &mut Writer,
    mesh: Option<&ArchiveVertexMesh>,
) -> Result<()> {
    writer.write(mesh.is_some())?;
    if let Some(mesh) = mesh {
        writer.write(match mesh.mode() {
            VertexMode::Triangles => 0i32,
            VertexMode::TriangleStrip => 1,
            VertexMode::TriangleFan => 2,
        })?;
        writer.array(mesh.positions())?;
        writer.array(mesh.texture_coordinates())?;
        writer.array(mesh.colors())?;
        writer.array(mesh.indices())?;
    }
    Ok(())
}
pub(super) fn read_vertex_mesh(reader: &mut Reader<'_>) -> Result<Option<ArchiveVertexMesh>> {
    if !reader.read::<bool>()? {
        return Ok(None);
    }
    let mode = match reader.enumeration(2)? {
        0 => VertexMode::Triangles,
        1 => VertexMode::TriangleStrip,
        _ => VertexMode::TriangleFan,
    };
    let mesh = VertexMesh::from_owned(
        mode,
        reader.array()?,
        reader.array()?,
        reader.array()?,
        reader.array()?,
    )
    .map_err(ArchiveError::Drawing)?;
    Ok(Some(mesh))
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum ArchiveTexturePatchKind {
    Texture = 0,
    FixedColor = 1,
    AtlasColor = 2,
}
/// Original readonly TexturePatch state after its selected constructor. Default
/// matrix/color fields are zeros, distinct from the no-transform render identity.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ArchiveTexturePatch {
    source: [f32; 4],
    destination: [f32; 4],
    color: [f32; 4],
    kind: ArchiveTexturePatchKind,
    transform: [f32; 6],
    has_transform: bool,
    blend: ImageColorBlend,
}
impl ArchiveTexturePatch {
    pub fn texture(source: [f32; 4], destination: [f32; 4]) -> Self {
        Self {
            source,
            destination,
            color: [0.0; 4],
            kind: ArchiveTexturePatchKind::Texture,
            transform: [0.0; 6],
            has_transform: false,
            blend: ImageColorBlend::Clear,
        }
    }
    pub fn fixed(destination: [f32; 4], color: [f32; 4]) -> Self {
        Self {
            color,
            kind: ArchiveTexturePatchKind::FixedColor,
            ..Self::texture([0.0; 4], destination)
        }
    }
    pub fn transformed(source: [f32; 4], destination: [f32; 4], transform: [f32; 6]) -> Self {
        Self {
            transform,
            has_transform: true,
            blend: ImageColorBlend::Dst,
            ..Self::texture(source, destination)
        }
    }
    pub fn atlas(
        source: [f32; 4],
        destination: [f32; 4],
        transform: [f32; 6],
        color: [f32; 4],
        blend: ImageColorBlend,
    ) -> Self {
        Self {
            color,
            kind: ArchiveTexturePatchKind::AtlasColor,
            blend,
            ..Self::transformed(source, destination, transform)
        }
    }
    pub fn source(self) -> [f32; 4] {
        self.source
    }
    pub fn destination(self) -> [f32; 4] {
        self.destination
    }
    pub fn color(self) -> [f32; 4] {
        self.color
    }
    pub fn kind(self) -> ArchiveTexturePatchKind {
        self.kind
    }
    pub fn destination_transform(self) -> [f32; 6] {
        self.transform
    }
    pub fn has_destination_transform(self) -> bool {
        self.has_transform
    }
    pub fn color_blend(self) -> ImageColorBlend {
        self.blend
    }
    pub fn to_image_patch(self) -> ImagePatch {
        ImagePatch {
            source: self.source,
            destination: self.destination,
            transform: if self.has_transform {
                Affine(self.transform)
            } else {
                Affine::IDENTITY
            },
            kind: match self.kind {
                ArchiveTexturePatchKind::Texture => ImagePatchKind::Texture,
                ArchiveTexturePatchKind::FixedColor => ImagePatchKind::FixedColor(self.color),
                ArchiveTexturePatchKind::AtlasColor => ImagePatchKind::AtlasColor {
                    color: self.color,
                    blend: self.blend,
                },
            },
        }
    }
}
pub(super) fn write_texture_patches(
    writer: &mut Writer,
    patches: Option<&[ArchiveTexturePatch]>,
) -> Result<()> {
    writer.nullable_count(patches.map(<[ArchiveTexturePatch]>::len))?;
    if let Some(patches) = patches {
        for p in patches {
            writer.write(p.source)?;
            writer.write(p.destination)?;
            writer.write(p.color)?;
            writer.write(p.kind as u8)?;
            writer.write(p.transform)?;
            writer.write(p.has_transform)?;
            writer.write(p.blend as i32)?;
        }
    }
    Ok(())
}
pub(super) fn read_texture_patches(
    reader: &mut Reader<'_>,
) -> Result<Option<Vec<ArchiveTexturePatch>>> {
    let Some(count) = reader.nullable_count()? else {
        return Ok(None);
    };
    let mut output = reserve(count)?;
    for _ in 0..count {
        let source = reader.read()?;
        let destination = reader.read()?;
        let color = reader.read()?;
        let kind = reader.read::<u8>()?;
        let transform = reader.read()?;
        let has_transform = reader.read()?;
        let blend = color_blend(reader)?;
        // Original switch deliberately treats unknown tags as Texture and
        // discards fields not retained by the selected original constructor.
        output.push(match kind {
            1 => ArchiveTexturePatch::fixed(destination, color),
            2 => ArchiveTexturePatch::atlas(source, destination, transform, color, blend),
            _ if has_transform => ArchiveTexturePatch::transformed(source, destination, transform),
            _ => ArchiveTexturePatch::texture(source, destination),
        });
    }
    Ok(Some(output))
}
pub(super) fn color_blend(reader: &mut Reader<'_>) -> Result<ImageColorBlend> {
    use ImageColorBlend::*;
    Ok([
        Clear, Src, Dst, SrcOver, DstOver, SrcIn, DstIn, SrcOut, DstOut, SrcATop, DstATop, Xor,
        Plus, Modulate, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight,
        SoftLight, Difference, Exclusion, Multiply, Hue, Saturation, Color, Luminosity,
    ][reader.enumeration(28)? as usize])
}
fn reserve<T>(count: usize) -> Result<Vec<T>> {
    let mut v = Vec::new();
    v.try_reserve_exact(count)
        .map_err(|_| ArchiveError::Capacity)?;
    Ok(v)
}
fn copy<T: Clone>(values: &[T]) -> Result<Vec<T>> {
    let mut v = reserve(values.len())?;
    v.extend_from_slice(values);
    Ok(v)
}
