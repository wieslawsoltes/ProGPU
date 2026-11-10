//! Original 3D line and ACIS edge producers with exact command-route ownership.
use super::{ArchiveCommand, ArchiveCommandKind, ArchivePicture, drawing_error};
use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
    vector::{Brush, BrushKind, Matrix4, Pen},
};
use std::{ops::Range, sync::Arc};

/// Original generic-vector command versus explicit dedicated extension route.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ThreeDRoute {
    Line,
    LineExtension,
    Acis,
    AcisExtension,
}
/// Original complete source owner, separate from later uniform camera selection.
#[derive(Clone, Debug)]
pub struct ArchiveThreeDDraw {
    command: Arc<ArchiveCommand>,
    floats: Arc<[f32]>,
    edges: Arc<[[[f32; 3]; 2]]>,
    provider: bool,
    transform: Matrix4,
    opacity: f32,
    camera: Option<Matrix4>,
    pen: Option<Arc<Pen>>,
    material: Option<Arc<Brush>>,
    data_pen: bool,
    route: ThreeDRoute,
}
impl ArchiveThreeDDraw {
    /// Retain an actual picture provider and raw source command. ACIS provider
    /// selection remains independent of inline edge presence, as in the source.
    pub fn from_picture(
        command: Arc<ArchiveCommand>,
        picture: &ArchivePicture,
        transform: Matrix4,
        opacity: f32,
        camera: Option<Matrix4>,
    ) -> Result<Self, Error> {
        Self::create(
            command,
            picture.floats.clone(),
            picture.lines_3d.clone(),
            true,
            transform,
            opacity,
            camera,
            None,
        )
    }
    /// Explicit provider-free command source, retaining original inline edges.
    pub fn from_command(
        command: Arc<ArchiveCommand>,
        transform: Matrix4,
        opacity: f32,
        camera: Option<Matrix4>,
    ) -> Result<Self, Error> {
        Self::create(
            command,
            Arc::from([]),
            Arc::from([]),
            false,
            transform,
            opacity,
            camera,
            None,
        )
    }
    /// Original typed DrawLine3D source producer. Its DataParam is an owned Pen;
    /// original v1-v10 archive serialization does not accept that external slot.
    pub fn line(
        pen: Arc<Pen>,
        points: [[f32; 3]; 2],
        transform: Matrix4,
        opacity: f32,
        camera: Option<Matrix4>,
    ) -> Result<Self, Error> {
        let c = ArchiveCommand {
            kind: ArchiveCommandKind::DrawExtension,
            extension_id: 2,
            position3d1: points[0],
            position3d2: points[1],
            ..Default::default()
        };
        Self::create(
            Arc::new(c),
            Arc::from([]),
            Arc::from([]),
            false,
            transform,
            opacity,
            camera,
            Some(pen),
        )
    }
    /// Original typed ACIS edge inventory, owned without a model parser/provider.
    pub fn acis(
        pen: Arc<Pen>,
        edges: Arc<[[[f32; 3]; 2]]>,
        transform: Matrix4,
        opacity: f32,
        camera: Option<Matrix4>,
    ) -> Result<Self, Error> {
        let count = i32::try_from(edges.len()).map_err(|_| Error::Capacity)?;
        let c = ArchiveCommand {
            kind: ArchiveCommandKind::DrawExtension,
            extension_id: 1,
            line3d_buffer_count: count,
            ..Default::default()
        };
        Self::create(
            Arc::new(c),
            Arc::from([]),
            edges,
            true,
            transform,
            opacity,
            camera,
            Some(pen),
        )
    }
    fn create(
        command: Arc<ArchiveCommand>,
        floats: Arc<[f32]>,
        edges: Arc<[[[f32; 3]; 2]]>,
        provider: bool,
        transform: Matrix4,
        opacity: f32,
        camera: Option<Matrix4>,
        typed_pen: Option<Arc<Pen>>,
    ) -> Result<Self, Error> {
        let route = match (command.kind, command.extension_id) {
            (ArchiveCommandKind::DrawLine3D, _) => ThreeDRoute::Line,
            (ArchiveCommandKind::DrawAcisSolid, _) => ThreeDRoute::Acis,
            (ArchiveCommandKind::DrawExtension, 2) => ThreeDRoute::LineExtension,
            (ArchiveCommandKind::DrawExtension, 1) => ThreeDRoute::AcisExtension,
            _ => return Err(Error::UnsupportedExecution),
        };
        let data_pen = typed_pen.is_some() && route == ThreeDRoute::LineExtension;
        let pen = match typed_pen {
            Some(p) => Some(p),
            None => command
                .pen
                .as_ref()
                .map(|p| p.to_pen().map(Arc::new))
                .transpose()
                .map_err(drawing_error)?,
        };
        let material = pen.as_ref().map(|p| {
            if data_pen {
                p.brush.clone()
            } else {
                placed_brush(&p.brush, transform)
            }
        });
        let result = Self {
            command,
            floats,
            edges,
            provider,
            transform,
            opacity,
            camera,
            pen,
            material,
            data_pen,
            route,
        };
        result.validate()?;
        Ok(result)
    }
    pub fn route(&self) -> ThreeDRoute {
        self.route
    }
    pub(crate) fn is_active(&self) -> bool {
        self.pen.is_some()
            && (matches!(self.route, ThreeDRoute::Line | ThreeDRoute::LineExtension)
                || self.selected_edges().is_ok_and(|v| !v.is_empty()))
    }
    pub fn camera(&self) -> Option<Matrix4> {
        self.camera
    }
    pub fn transform(&self) -> Matrix4 {
        self.transform
    }
    pub fn opacity(&self) -> f32 {
        self.opacity
    }
    pub fn command(&self) -> &Arc<ArchiveCommand> {
        &self.command
    }
    pub(crate) fn brush(&self) -> Option<&Arc<Brush>> {
        self.material.as_ref()
    }
    pub(crate) fn placed(&self, placement: Matrix4) -> Result<Self, Error> {
        let mut result = self.clone();
        if let Some(camera) = result.camera {
            result.camera = Some(camera * placement);
        } else {
            result.transform = result.transform * placement;
        }
        if !result.data_pen {
            result.material = result
                .pen
                .as_ref()
                .map(|p| placed_brush(&p.brush, result.transform));
        }
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn with_opacity(&self, opacity: f32) -> Result<Self, Error> {
        let mut result = self.clone();
        result.opacity *= opacity;
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn validate(&self) -> Result<(), Error> {
        if !self.opacity.is_finite()
            || self.transform.0.iter().flatten().any(|v| !v.is_finite())
            || self
                .camera
                .is_some_and(|m| m.0.iter().flatten().any(|v| !v.is_finite()))
        {
            return Err(Error::InvalidInput);
        }
        if self.pen.is_some() {
            match self.route {
                ThreeDRoute::Line | ThreeDRoute::LineExtension => {
                    self.line_points()?;
                }
                ThreeDRoute::Acis | ThreeDRoute::AcisExtension => {
                    self.selected_edges()?;
                }
            }
        }
        Ok(())
    }
    fn line_points(&self) -> Result<[[f32; 3]; 2], Error> {
        // Ordinary DrawLine3D's helper passes a null provider even in a picture.
        if self.route == ThreeDRoute::LineExtension
            && self.provider
            && self.command.float_buffer_count >= 6
        {
            let values = range(&self.floats, self.command.float_buffer_offset, 6)?;
            Ok([
                [values[0], values[1], values[2]],
                [values[3], values[4], values[5]],
            ])
        } else {
            Ok([self.command.position3d1, self.command.position3d2])
        }
    }
    fn selected_edges(&self) -> Result<&[[[f32; 3]; 2]], Error> {
        if self.provider {
            range(
                &self.edges,
                self.command.line3d_buffer_offset,
                self.command.line3d_buffer_count,
            )
        } else {
            Ok(self.command.edges3d.as_deref().unwrap_or(&[]))
        }
    }
    pub(crate) fn append(
        &self,
        mesh: &mut Mesh,
        brush_index: f32,
        batch: &mut ThreeDBatch,
    ) -> Result<Range<u32>, Error> {
        self.validate()?;
        let begin = u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
        let Some(pen) = &self.pen else {
            return Ok(begin..begin);
        };
        let color = match pen.brush.kind {
            BrushKind::Solid(c) => c,
            _ => [1.0; 4],
        };
        let late = if self.camera.is_some() { 100.0 } else { 0.0 };
        match self.route {
            ThreeDRoute::Line | ThreeDRoute::LineExtension => {
                let points = self.line_points()?.map(|p| transform3(self.transform, p));
                let p0 = [points[0][0], points[0][1]];
                let p1 = [points[1][0], points[1][1]];
                let vertices = std::array::from_fn::<_, 4, _>(|i| VectorVertex {
                    position: if i < 2 { p0 } else { p1 },
                    color,
                    texture_coordinate: [points[usize::from(i >= 2)][2], 0.0],
                    brush_index,
                    shape_size: p1,
                    corner_radius: [1.0, -1.0, 2.0, -2.0][i],
                    stroke_thickness: pen.options.thickness,
                    shape_type: 8.0 + late,
                });
                mesh.push_indexed(&vertices, &[0, 1, 2, 1, 3, 2])?;
            }
            ThreeDRoute::Acis | ThreeDRoute::AcisExtension => {
                let edges = self.selected_edges()?;
                if edges.is_empty() {
                    return Ok(begin..begin);
                }
                let start = u32::try_from(batch.edges.len()).map_err(|_| Error::Capacity)?;
                let count = u32::try_from(edges.len()).map_err(|_| Error::Capacity)?;
                let record = u32::try_from(batch.records.len()).map_err(|_| Error::Capacity)?;
                // Float vertex slots preserve original integer identity only in
                // their exact representable range; do not round an edge ordinal.
                if start.checked_add(count).is_none_or(|n| n > 16_777_216) || record > 16_777_216 {
                    return Err(Error::Capacity);
                }
                batch
                    .edges
                    .try_reserve(edges.len())
                    .map_err(|_| Error::Capacity)?;
                batch.records.try_reserve(1).map_err(|_| Error::Capacity)?;
                batch.edges.extend(edges.iter().map(|e| AcisEdge {
                    p0: [e[0][0], e[0][1], e[0][2], 0.0],
                    p1: [e[1][0], e[1][1], e[1][2], 0.0],
                }));
                batch.records.push(AcisRecord {
                    transform: self.transform.0,
                    color,
                    start_edge: start,
                    edge_count: count,
                    pen_thickness: pen.options.thickness,
                    opacity: pen.brush.opacity * self.opacity,
                });
                let vertex_count = edges.len().checked_mul(4).ok_or(Error::Capacity)?;
                let index_count = edges.len().checked_mul(6).ok_or(Error::Capacity)?;
                mesh.extend_generated(
                    (0..vertex_count).map(|i| VectorVertex {
                        position: [(start + i as u32 / 4) as f32, (i % 4) as f32],
                        color,
                        texture_coordinate: [0.0; 2],
                        brush_index,
                        shape_size: [record as f32, 0.0],
                        corner_radius: 0.0,
                        stroke_thickness: 0.0,
                        shape_type: 10.0 + late,
                    }),
                    (0..edges.len()).flat_map(|i| [0, 1, 2, 1, 3, 2].map(|n| i as u32 * 4 + n)),
                    index_count,
                )?;
            }
        }
        let end = u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
        Ok(begin..end)
    }
}
fn range<T>(source: &[T], offset: i32, count: i32) -> Result<&[T], Error> {
    let start = usize::try_from(offset).map_err(|_| Error::InvalidInput)?;
    let count = usize::try_from(count).map_err(|_| Error::InvalidInput)?;
    source
        .get(start..start.checked_add(count).ok_or(Error::Capacity)?)
        .ok_or(Error::InvalidInput)
}
fn transform3(matrix: Matrix4, p: [f32; 3]) -> [f32; 3] {
    let m = matrix.0;
    std::array::from_fn(|a| p[0] * m[0][a] + p[1] * m[1][a] + p[2] * m[2][a] + m[3][a])
}
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub(crate) struct AcisEdge {
    pub p0: [f32; 4],
    pub p1: [f32; 4],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub(crate) struct AcisRecord {
    pub transform: [[f32; 4]; 4],
    pub color: [f32; 4],
    pub start_edge: u32,
    pub edge_count: u32,
    pub pen_thickness: f32,
    pub opacity: f32,
}
const _: () = assert!(size_of::<AcisEdge>() == 32);
const _: () = assert!(size_of::<AcisRecord>() == 96);
#[derive(Debug, Default)]
pub(crate) struct ThreeDBatch {
    pub records: Vec<AcisRecord>,
    pub edges: Vec<AcisEdge>,
    pub sources: Vec<Arc<ArchiveThreeDDraw>>,
}

// Original TransformCommandBrush: an uninvertible command leaves the original
// immutable brush unchanged. Typed DrawLine3D DataParam pens are outside that
// command.Pen rewrite, preserving the source factory's distinct contract.
fn placed_brush(source: &Arc<Brush>, transform: Matrix4) -> Arc<Brush> {
    if transform == Matrix4::IDENTITY
        || matches!(source.kind, BrushKind::Solid(_) | BrushKind::Tile(_))
    {
        return source.clone();
    }
    let Some(inverse) = crate::image_filter::FilterTransform::new(transform).inverse_raw() else {
        return source.clone();
    };
    let mut result = source.as_ref().clone();
    let matrix = match &mut result.kind {
        BrushKind::Linear(v) => &mut v.options.transform,
        BrushKind::Radial(v) => &mut v.options.transform,
        BrushKind::Path(v) => &mut v.options.transform,
        BrushKind::TwoPointConical(v) => &mut v.options.transform,
        BrushKind::Sweep(v) => &mut v.options.transform,
        BrushKind::PerlinNoise(v) => &mut v.transform,
        BrushKind::Hatch(v) | BrushKind::CrossHatch(v) => &mut v.transform,
        BrushKind::HatchSet(v) => &mut v.transform,
        BrushKind::Solid(_) | BrushKind::Tile(_) => return source.clone(),
    };
    *matrix = inverse * *matrix;
    Arc::new(result)
}
