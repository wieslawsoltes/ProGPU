//! Original retained command input cache and independent raster-bounds traversal.
//! Archive commands have no SourceHitGeometry override, hinted geometry or live
//! Visual source contract; no such metadata is inferred from these records.
use super::{
    ArchiveCommand, ArchiveCommandKind as K, ArchiveError, ArchivePicture, PictureArchive, Result,
};
use crate::{Error, path::Segment, vector::*};
use std::sync::Arc;
mod captured;
mod commands;
pub use captured::{CapturedHitCommand, SourceHitGeometry, SourceHitGeometryKind};
mod index;
mod paths;
mod primitive;
mod series;
mod strokes;
mod traversal;
pub use index::{ArchiveHitIndex, HitNode};
pub use primitive::{HitPrimitive, HitPrimitiveKind};

/// Original reusable CPU builder. An error faults publication until `clear`;
/// completed indices independently retain all source picture/command owners.
#[derive(Debug, Default)]
pub struct ArchiveHitTestCache {
    captured: Vec<Arc<CapturedHitCommand>>,
    image_depth: u32,
    point_stack: Vec<u32>,
    participation: u32,
    primitives: Vec<HitPrimitive>,
    segments: Vec<Segment>,
    clips: Vec<Clip>,
    opacities: Vec<f32>,
    active_opacity: Option<f32>,
    next_id: i32,
    bounds: Option<[f32; 4]>,
    failed: bool,
    pictures: Vec<Arc<ArchivePicture>>,
    commands: Vec<Arc<ArchiveCommand>>,
}
#[derive(Clone, Debug)]
struct Clip {
    bounds: [f32; 4],
    path: Option<Arc<PathGeometry>>,
    compiled: Option<Compiled>,
}
#[derive(Clone, Copy, Debug)]
struct Compiled {
    bounds: [f32; 4],
    start: u32,
    count: u32,
    rule: FillRule,
}
impl Clip {
    fn empty() -> Self {
        Self {
            bounds: [1.0, 1.0, 0.0, 0.0],
            path: None,
            compiled: None,
        }
    }
}
impl ArchiveHitTestCache {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn clear(&mut self) {
        self.captured.clear();
        self.image_depth = 0;
        self.point_stack.clear();
        self.participation = 0;
        self.primitives.clear();
        self.segments.clear();
        self.clips.clear();
        self.opacities.clear();
        self.active_opacity = None;
        self.next_id = 0;
        self.bounds = None;
        self.failed = false;
        self.pictures.clear();
        self.commands.clear();
    }
    pub fn primitive_count(&self) -> usize {
        self.primitives.len()
    }
    /// Original union of clipped primitive envelopes, separate from exact input.
    pub fn bounds(&self) -> Option<[f32; 4]> {
        self.bounds
    }
    /// Record one original command with its caller-resolved input frame. Path
    /// commands retain their separate internal recorded-transform composition.
    pub fn add_command(
        &mut self,
        command: Arc<ArchiveCommand>,
        transform: Matrix4,
        provider: Option<Arc<ArchivePicture>>,
        id: Option<i32>,
    ) -> Result<()> {
        if self.failed {
            return Err(ArchiveError::InvalidData);
        }
        let result = (|| {
            push(&mut self.commands, command.clone())?;
            if let Some(p) = &provider {
                push(&mut self.pictures, p.clone())?;
            }
            self.command(&command, normalize(transform), provider.as_deref(), id)
        })();
        if result.is_err() {
            self.failed = true;
        }
        result
    }
    pub fn build_index(
        &self,
        max_depth: u32,
        max_primitives_per_node: u32,
    ) -> Result<ArchiveHitIndex> {
        if self.failed || self.image_depth != 0 || !self.point_stack.is_empty() {
            return Err(ArchiveError::InvalidData);
        }
        ArchiveHitIndex::build(self, max_depth, max_primitives_per_node)
    }
    pub fn build(&self) -> Result<ArchiveHitIndex> {
        self.build_index(8, 32)
    }
    fn id(&mut self, explicit: Option<i32>, source: i32) -> i32 {
        if let Some(id) = explicit.or((source != 0).then_some(source)) {
            if id >= self.next_id {
                self.next_id = id.wrapping_add(1);
            }
            id
        } else {
            let id = self.next_id;
            self.next_id = self.next_id.wrapping_add(1);
            id
        }
    }
    fn append(&mut self, mut primitive: HitPrimitive) -> Result<()> {
        if self.participation != 0 {
            primitive = primitive.with_flags(primitive.flags | self.participation)?;
        }
        if let Some(clip) = self.clips.last() {
            let b = intersect(primitive_bounds(primitive), clip.bounds);
            if b[2] < b[0] || b[3] < b[1] {
                return Ok(());
            }
            primitive = primitive.with_world_bounds([b[0], b[1]], [b[2], b[3]]);
            if let Some(path) = clip.compiled {
                primitive = primitive.with_clip(path.start, path.count, path.rule);
            }
        }
        let b = primitive_bounds(primitive);
        if b.iter().any(|v| !v.is_finite()) || b[2] < b[0] || b[3] < b[1] {
            return Ok(());
        }
        push(&mut self.primitives, primitive)?;
        self.bounds = Some(self.bounds.map_or(b, |old| union(old, b)));
        Ok(())
    }
    pub fn push_clip(&mut self, rect: [f32; 4], transform: Matrix4) -> Result<()> {
        if self.image_depth != 0 {
            self.image_depth = self
                .image_depth
                .checked_add(1)
                .ok_or(ArchiveError::Capacity)?;
            return Ok(());
        }
        let mut b = transform_bounds(edges(rect), transform);
        let mut clip = Clip {
            bounds: b,
            path: None,
            compiled: None,
        };
        if let Some(active) = self.clips.last() {
            b = intersect(b, active.bounds);
            clip = active.clone();
            clip.bounds = b;
        }
        push(&mut self.clips, clip)
    }
    pub fn pop_clip(&mut self) {
        if self.image_depth != 0 {
            self.image_depth -= 1;
        } else {
            self.clips.pop();
        }
    }
    fn geometry_clip(&mut self, c: &ArchiveCommand, transform: Matrix4) -> Result<()> {
        let path = c.path.as_ref().map(|p| p.to_geometry()).transpose()?;
        let Some((source, bounds)) = path.and_then(|p| p.bounds().map(|b| (p, b))) else {
            let inherited = self.clips.last().cloned().unwrap_or(Clip {
                bounds: [
                    f32::NEG_INFINITY,
                    f32::NEG_INFINITY,
                    f32::INFINITY,
                    f32::INFINITY,
                ],
                path: None,
                compiled: None,
            });
            return push(&mut self.clips, inherited);
        };
        let mut bounds = transform_bounds(bounds, transform);
        if let Some(active) = self.clips.last() {
            bounds = intersect(bounds, active.bounds);
        }
        let mut path = source.transformed(transform)?;
        if let Some(inherited) = self.clips.last().and_then(|c| c.path.as_ref()) {
            path = PathGeometry::combine_deferred(
                inherited.clone(),
                Arc::new(path),
                BooleanOperation::Intersect,
            );
        }
        if let Some(compiled) = self.compile_path(&path)? {
            push(
                &mut self.clips,
                Clip {
                    bounds,
                    path: Some(Arc::new(path)),
                    compiled: Some(compiled),
                },
            )
        } else {
            let mut clip = self.clips.last().cloned().unwrap_or(Clip {
                bounds,
                path: None,
                compiled: None,
            });
            clip.bounds = bounds;
            push(&mut self.clips, clip)
        }
    }
}
fn reserve<T>(v: &mut Vec<T>, n: usize) -> Result<()> {
    v.try_reserve(n).map_err(|_| ArchiveError::Capacity)
}
fn push<T>(v: &mut Vec<T>, x: T) -> Result<()> {
    reserve(v, 1)?;
    v.push(x);
    Ok(())
}
fn copy<T: Clone>(v: &[T]) -> Result<Vec<T>> {
    let mut out = Vec::new();
    reserve(&mut out, v.len())?;
    out.extend_from_slice(v);
    Ok(out)
}
fn finite(p: [f32; 2]) -> bool {
    p.iter().all(|v| v.is_finite())
}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] + b[0], a[1] + b[1]]
}
fn sub(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] - b[0], a[1] - b[1]]
}
fn mul(a: [f32; 2], v: f32) -> [f32; 2] {
    [a[0] * v, a[1] * v]
}
fn length(a: [f32; 2]) -> f32 {
    (a[0] * a[0] + a[1] * a[1]).sqrt()
}
fn primitive_bounds(p: HitPrimitive) -> [f32; 4] {
    [
        p.bounds_min[0],
        p.bounds_min[1],
        p.bounds_max[0],
        p.bounds_max[1],
    ]
}
fn edges(r: [f32; 4]) -> [f32; 4] {
    [r[0], r[1], r[0] + r[2], r[1] + r[3]]
}
fn union(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    [
        a[0].min(b[0]),
        a[1].min(b[1]),
        a[2].max(b[2]),
        a[3].max(b[3]),
    ]
}
fn intersect(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    [
        a[0].max(b[0]),
        a[1].max(b[1]),
        a[2].min(b[2]),
        a[3].min(b[3]),
    ]
}
fn transform_bounds(b: [f32; 4], m: Matrix4) -> [f32; 4] {
    let p = [[b[0], b[1]], [b[2], b[1]], [b[2], b[3]], [b[0], b[3]]].map(|p| m.transform_point(p));
    [
        p[0][0].min(p[1][0]).min(p[2][0].min(p[3][0])),
        p[0][1].min(p[1][1]).min(p[2][1].min(p[3][1])),
        p[0][0].max(p[1][0]).max(p[2][0].max(p[3][0])),
        p[0][1].max(p[1][1]).max(p[2][1].max(p[3][1])),
    ]
}
fn normalize(m: Matrix4) -> Matrix4 {
    if m == Matrix4([[0.0; 4]; 4]) {
        Matrix4::IDENTITY
    } else {
        m
    }
}
fn invertible(matrix: Matrix4) -> bool {
    let m = matrix.0;
    let d = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    [m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]
        .iter()
        .all(|v| v.is_finite())
        && ![
            m[0][2],
            m[0][3],
            m[1][2],
            m[1][3],
            m[2][0],
            m[2][1],
            m[2][3],
            m[3][3] - 1.0,
        ]
        .iter()
        .any(|v| v.abs() > 0.0001)
        && d.is_finite()
        && d != 0.0
}
fn local_width(c: &ArchiveCommand, pen: &Pen) -> Option<f32> {
    if !pen.options.thickness.is_finite() || (!pen.is_hairline() && pen.options.thickness <= 0.0) {
        return None;
    }
    if pen.is_hairline() {
        return Some(1.0);
    }
    let mut width = pen.options.thickness;
    if !c.is_pen_thickness_local && c.transform != Matrix4([[0.0; 4]; 4]) {
        if !invertible(c.transform) {
            return None;
        }
        width /= c.transform.stroke_scales()?[0];
    }
    (width.is_finite() && width > 0.0).then_some(width)
}
fn device(p: &Pen) -> bool {
    p.is_hairline() || p.is_fixed()
}
fn device_width(p: &Pen) -> f32 {
    if p.is_hairline() {
        1.0
    } else {
        p.options.thickness
    }
}
fn polygon(points: &[[f32; 2]], closed: bool) -> Result<PathGeometry> {
    let mut path = PathGeometry::default();
    if let Some(&start) = points.first() {
        let mut f = PathFigure::new(start, closed);
        reserve(&mut f.segments, points.len() - 1)?;
        f.segments.extend(
            points[1..]
                .iter()
                .map(|p| PathSegment::new(PathSegmentKind::Line(*p))),
        );
        push(&mut path.figures, f)?;
    }
    Ok(path)
}
