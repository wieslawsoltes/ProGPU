//! Original static-DXF Hatch producer, independently owned from dynamic frames.
use super::{ArchiveCommand, ArchiveCommandKind, ArchivePicture, GpuHatchRecord, GpuHatchSegment};
use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
    vector::{MaterialPage, MaterialTable, Matrix4},
};
use std::{ops::Range, sync::Arc};

#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{
    PreparedStaticHatches, StaticHatchEngine, StaticHatchMetrics, StaticHatchTarget,
    UploadedStaticHatches,
};

/// The actual source route remains visible even though both static producers
/// append an extension-3 drawcall. Ordinary dynamic DrawHatch stays independent.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StaticHatchRoute {
    DrawHatch,
    Extension,
}

/// Original ordered static drawcall, including an empty boundary command.
#[derive(Clone, Debug)]
pub struct StaticHatchBatch {
    pub command_index: u32,
    pub route: StaticHatchRoute,
    pub indices: Range<u32>,
}

#[derive(Debug)]
enum Source {
    Picture(Arc<ArchivePicture>),
    Commands(Arc<[ArchiveCommand]>),
}
impl Source {
    fn commands(&self) -> &[ArchiveCommand] {
        match self {
            Self::Picture(picture) => &picture.commands,
            Self::Commands(commands) => commands,
        }
    }
}

#[derive(Debug)]
struct Data {
    source: Source,
    mesh: Mesh,
    materials: MaterialPage,
    records: Vec<GpuHatchRecord>,
    segments: Vec<GpuHatchSegment>,
    batches: Vec<StaticHatchBatch>,
    static_zoom: f32,
}

/// One complete immutable static Hatch compilation. Boundary ordinals and
/// material records are shared in source order, never restarted per draw.
///
/// This is the Hatch-only static producer. Other drawing families reject as a
/// whole before compilation; they are not dropped from a mixed DXF document.
#[derive(Clone, Debug)]
pub struct StaticHatchBuffer(Arc<Data>);
impl StaticHatchBuffer {
    /// Retain the complete original provider generation and all its side arrays.
    pub fn compile(source: Arc<ArchivePicture>, static_zoom: f32) -> Result<Self, Error> {
        Self::compile_source(Source::Picture(source), static_zoom)
    }
    /// Original command-list overload, with no fabricated provider buffers.
    pub fn compile_commands(
        commands: Arc<[ArchiveCommand]>,
        static_zoom: f32,
    ) -> Result<Self, Error> {
        Self::compile_source(Source::Commands(commands), static_zoom)
    }
    fn compile_source(source: Source, static_zoom: f32) -> Result<Self, Error> {
        // Admission precedes material registration and boundary allocation.
        u32::try_from(source.commands().len()).map_err(|_| Error::Capacity)?;
        for command in source.commands() {
            match command.kind {
                ArchiveCommandKind::DrawHatch => {}
                ArchiveCommandKind::DrawExtension if command.extension_id == 3 => {}
                // CompileStaticDxf accepts binary clips but its Hatch renderer
                // does not apply each saved drawcall's ClipRect. The actual
                // caller scissor is retained by DrawStaticDxfBuffer instead.
                ArchiveCommandKind::PushClip | ArchiveCommandKind::PopClip => {}
                // These commands have no branch in either original static
                // compiler switch. In particular they do not alter alpha1.
                ArchiveCommandKind::PushOpacity
                | ArchiveCommandKind::PopOpacity
                | ArchiveCommandKind::PushGeometryClip
                | ArchiveCommandKind::PopGeometryClip
                | ArchiveCommandKind::PushOpacityMask
                | ArchiveCommandKind::PopOpacityMask
                | ArchiveCommandKind::PushBlendMode
                | ArchiveCommandKind::PopBlendMode => {}
                _ => return Err(Error::UnsupportedExecution),
            }
        }
        let mut mesh = Mesh::default();
        let mut table = MaterialTable::default();
        let mut records = Vec::new();
        let mut segments = Vec::new();
        let mut batches = Vec::new();
        for (command_index, command) in source.commands().iter().enumerate() {
            let route = match command.kind {
                ArchiveCommandKind::DrawHatch => StaticHatchRoute::DrawHatch,
                ArchiveCommandKind::DrawExtension => StaticHatchRoute::Extension,
                _ => continue,
            };
            let start = u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
            batches.try_reserve(1).map_err(|_| Error::Capacity)?;
            // Original Hatch compile returns before registration for null Path;
            // a present brush is registered before the empty-boundary return.
            if command.path.is_some()
                && let Some(brush) = &command.brush
            {
                let brush = brush.to_vector().map_err(super::drawing_error)?;
                let brush_index = table.register(Some(&brush), 1.0)? as f32;
                let (boundary, bounds) = super::hatch::boundary(command)?;
                if !boundary.is_empty() {
                    let transform = if command.transform == Matrix4([[0.0; 4]; 4]) {
                        Matrix4::IDENTITY
                    } else {
                        command.transform
                    };
                    if transform.0.iter().flatten().any(|v| !v.is_finite()) {
                        return Err(Error::InvalidInput);
                    }
                    let record = u32::try_from(records.len()).map_err(|_| Error::Capacity)?;
                    if record >= 1 << 24 {
                        return Err(Error::Capacity);
                    }
                    let segment_start =
                        u32::try_from(segments.len()).map_err(|_| Error::Capacity)?;
                    let segment_count =
                        u32::try_from(boundary.len()).map_err(|_| Error::Capacity)?;
                    segment_start
                        .checked_add(segment_count)
                        .ok_or(Error::Capacity)?;
                    records.try_reserve(1).map_err(|_| Error::Capacity)?;
                    segments
                        .try_reserve(boundary.len())
                        .map_err(|_| Error::Capacity)?;
                    let [left, top, right, bottom] = bounds;
                    let local = [[left, top], [right, top], [right, bottom], [left, bottom]];
                    let uv = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
                    let vertices = std::array::from_fn(|i| VectorVertex {
                        position: transform.transform_point(local[i]),
                        color: [local[i][0], local[i][1], record as f32, 0.0],
                        texture_coordinate: uv[i],
                        brush_index,
                        shape_size: [right - left, bottom - top],
                        corner_radius: 0.0,
                        stroke_thickness: 0.0,
                        // Source adds200 after the whole static compilation;
                        // Hatch does not add100 for UseGpuTransforms here.
                        shape_type: 9.0 + 200.0,
                    });
                    if vertices.iter().any(|v| {
                        v.position
                            .iter()
                            .chain(&v.shape_size)
                            .any(|n| !n.is_finite())
                    }) {
                        return Err(Error::InvalidInput);
                    }
                    mesh.push_quad(vertices)?;
                    records.push(GpuHatchRecord {
                        start_segment: segment_start,
                        segment_count,
                        bounds,
                        padding: [0; 2],
                    });
                    segments.extend(boundary);
                }
            }
            batches.push(StaticHatchBatch {
                command_index: command_index as u32,
                route,
                indices: start..u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?,
            });
        }
        Ok(Self(Arc::new(Data {
            source,
            mesh,
            materials: table.snapshot()?,
            records,
            segments,
            batches,
            static_zoom: if static_zoom.is_finite() && static_zoom > 0.0 {
                static_zoom
            } else {
                1.0
            },
        })))
    }
    pub fn commands(&self) -> &[ArchiveCommand] {
        self.0.source.commands()
    }
    pub fn source_picture(&self) -> Option<&Arc<ArchivePicture>> {
        match &self.0.source {
            Source::Picture(p) => Some(p),
            Source::Commands(_) => None,
        }
    }
    pub fn static_zoom(&self) -> f32 {
        self.0.static_zoom
    }
    pub fn vertices(&self) -> &[VectorVertex] {
        self.0.mesh.vertices()
    }
    pub fn indices(&self) -> &[u32] {
        self.0.mesh.indices()
    }
    pub fn records(&self) -> &[GpuHatchRecord] {
        &self.0.records
    }
    pub fn segments(&self) -> &[GpuHatchSegment] {
        &self.0.segments
    }
    pub fn materials(&self) -> &MaterialPage {
        &self.0.materials
    }
    pub fn batches(&self) -> &[StaticHatchBatch] {
        &self.0.batches
    }
    pub fn is_empty(&self) -> bool {
        self.indices().is_empty()
    }
    pub fn same_generation(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

/// Original explicit static-DXF viewport camera. The source ignores the
/// UpdateViewport projection/center arguments; projection is supplied at draw.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct StaticHatchViewport(Matrix4);
impl StaticHatchViewport {
    pub fn new(zoom: f32, pan: [f32; 2], screen_center: [f32; 2]) -> Result<Self, Error> {
        let camera = Matrix4([
            [zoom, 0.0, 0.0, 0.0],
            [0.0, zoom, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [
                -screen_center[0] * zoom + screen_center[0] + pan[0],
                -screen_center[1] * zoom + screen_center[1] + pan[1],
                0.0,
                1.0,
            ],
        ]);
        if camera.0.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(Self(camera))
    }
    pub fn matrix(self) -> Matrix4 {
        self.0
    }
}
