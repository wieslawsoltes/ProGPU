//! Original SKPathBuilder common packed lane and complete legacy curve writer.
//! The first legacy operation materializes one actual packed generation; conics
//! retain both their source identity and original sixteen polynomial spans.
use super::{
    ArcSegment, EffectMatrix, FillRule, Matrix4, PackedPath, PackedPathBuilder, PathEffectSource,
    PathFigure, PathGeometry, PathIterator, PathSegment, PathSegmentKind, RawPathIterator,
    RoundRect, SweepDirection,
};
use crate::Error;
use std::{cell::RefCell, sync::Arc};
mod arcs;
mod canvas;
mod queries;
pub use queries::{PathConvexity, PathInventory, PathRectangle};
const EPS: f32 = 0.00001;

/// Original explicit contour traversal direction.
pub type PathDirection = SweepDirection;
/// Original append versus first-figure extension semantics.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum PathAddMode {
    /// Append all independent source contours.
    #[default]
    Append,
    /// Connect the first source contour, including an explicit equal-point line.
    Extend,
}
#[derive(Clone, Debug)]
enum Source {
    Packed(PackedPath),
    Geometry(Arc<PathGeometry>),
}
/// Owned immutable path generation, preserving inverse-fill metadata separately
/// from the ordinary geometry fill rule. Packed snapshots share their used prefix.
#[derive(Clone, Debug)]
pub struct RetainedPath {
    source: RefCell<Source>,
    inverse: bool,
}
impl RetainedPath {
    /// Parse the original shared SVG/WPF path grammar into an owned generation.
    /// Failure keeps the caller's existing path unchanged.
    pub fn parse_svg(source: &str) -> Result<Self, super::PathParseError> {
        Ok(Self::from_geometry(
            Arc::new(PathGeometry::parse(source)?),
            false,
        ))
    }
    /// Retain a complete ordinary/deferred source generation without reconstructing it.
    pub fn from_geometry(geometry: Arc<PathGeometry>, inverse: bool) -> Self {
        Self {
            source: RefCell::new(Source::Geometry(geometry)),
            inverse,
        }
    }
    /// Retain the actual original packed generation.
    pub fn from_packed(path: PackedPath) -> Self {
        let inverse = path.is_inverse();
        Self {
            source: RefCell::new(Source::Packed(path)),
            inverse,
        }
    }
    /// Original ordinary fill rule, independent of inverse selection.
    pub fn fill_rule(&self) -> FillRule {
        match &*self.source.borrow() {
            Source::Packed(path) => path.fill_rule(),
            Source::Geometry(path) => path.fill_rule,
        }
    }
    /// Whether source drawing reverses the ordinary fill result.
    pub fn is_inverse(&self) -> bool {
        self.inverse
    }
    /// Materialize the original packed generation once on first geometry access.
    /// Subsequent queries share that exact owner, and later copies follow the
    /// source's materialized zero-offset-copy route.
    pub fn geometry(&self) -> Result<Arc<PathGeometry>, Error> {
        let mut source = self.source.borrow_mut();
        if let Source::Packed(path) = &*source {
            let geometry = Arc::new(path.geometry()?);
            *source = Source::Geometry(geometry);
        }
        match &*source {
            Source::Geometry(path) => Ok(path.clone()),
            Source::Packed(_) => unreachable!(),
        }
    }
    /// Original control envelope, including move-only figures.
    pub fn bounds(&self) -> [f32; 4] {
        match &*self.source.borrow() {
            Source::Packed(path) => path.bounds(),
            Source::Geometry(path) => path.bounds().unwrap_or([0.0; 4]),
        }
    }
    /// Original analytic derivative/arc extent.
    pub fn tight_bounds(&self) -> Result<[f32; 4], Error> {
        match &*self.source.borrow() {
            Source::Packed(path) => path.tight_bounds(),
            Source::Geometry(path) => path.tight_bounds(),
        }
    }
    /// Original raw iterator over retained source conics and compatible arc runs.
    pub fn raw_iterator(&self) -> Result<RawPathIterator, Error> {
        let geometry = self.geometry()?;
        RawPathIterator::new(&geometry)
    }
    /// Original force-close iterator with independent synthetic close flags.
    pub fn iterator(&self, force_close: bool) -> Result<PathIterator, Error> {
        let geometry = self.geometry()?;
        PathIterator::new(&geometry, force_close)
    }
    /// Retain exact geometry and inverse metadata for the original effect stack.
    pub fn effect_source(&self) -> Result<PathEffectSource, Error> {
        let geometry = self.geometry()?;
        PathEffectSource::from_geometry(&geometry, self.inverse)
    }
    /// Create an independent writer using the original zero-offset legacy copy.
    pub fn to_builder(&self) -> Result<PathBuilder, Error> {
        PathBuilder::from_path(self)
    }
}

#[derive(Debug)]
struct Legacy {
    geometry: PathGeometry,
    current: [f32; 2],
    start: [f32; 2],
    active: Option<usize>,
}
impl Legacy {
    fn new(geometry: PathGeometry) -> Self {
        let mut output = Self {
            geometry,
            current: [0.0; 2],
            start: [0.0; 2],
            active: None,
        };
        output.restore();
        output
    }
    fn restore(&mut self) {
        if let Some(figure) = self.geometry.figures.last() {
            self.start = figure.start;
            self.current = if figure.is_closed {
                figure.start
            } else {
                figure
                    .segments
                    .last()
                    .map_or(figure.start, PathSegment::end)
            };
            self.active = (!figure.is_closed).then_some(self.geometry.figures.len() - 1);
        } else {
            self.current = [0.0; 2];
            self.start = [0.0; 2];
            self.active = None;
        }
    }
    fn move_to(&mut self, point: [f32; 2]) -> Result<(), Error> {
        if let Some(index) = self
            .active
            .filter(|&i| self.geometry.figures[i].segments.is_empty())
        {
            self.geometry.figures[index].start = point;
        } else {
            self.geometry
                .figures
                .try_reserve(1)
                .map_err(|_| Error::Capacity)?;
            self.active = Some(self.geometry.figures.len());
            self.geometry.figures.push(PathFigure::new(point, false));
        }
        self.current = point;
        self.start = point;
        Ok(())
    }
    fn ensure(&mut self) -> Result<&mut PathFigure, Error> {
        if self.active.is_none() {
            self.move_to(self.current)?;
        }
        Ok(&mut self.geometry.figures[self.active.expect("original active contour")])
    }
    fn segment(&mut self, kind: PathSegmentKind) -> Result<(), Error> {
        let segment = PathSegment::new(kind);
        let end = segment.end();
        let figure = self.ensure()?;
        figure
            .segments
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        figure.segments.push(segment);
        self.current = end;
        Ok(())
    }
    fn close(&mut self) {
        if let Some(index) = self.active.take() {
            self.geometry.figures[index].is_closed = true;
            self.current = self.start;
        }
    }
    fn push_figure(&mut self, figure: PathFigure) -> Result<(), Error> {
        self.geometry
            .figures
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        self.geometry.figures.push(figure);
        self.restore();
        Ok(())
    }
}
#[derive(Debug)]
enum Writer {
    Packed(PackedPathBuilder),
    Legacy(Legacy),
}
/// Original mutable path builder. Common verbs use contiguous copy-on-write
/// storage until an arc, conic or legacy topology operation requires figures.
#[derive(Debug)]
pub struct PathBuilder {
    writer: Writer,
    inverse: bool,
    fill: FillRule,
}
impl Default for PathBuilder {
    fn default() -> Self {
        Self::new()
    }
}
impl PathBuilder {
    /// Empty original nonzero, non-inverse writer with bounds-only packed tracking.
    pub fn new() -> Self {
        Self {
            writer: Writer::Packed(PackedPathBuilder::with_tight_bounds(false)),
            inverse: false,
            fill: FillRule::Nonzero,
        }
    }
    /// Original independent copy, preserving packed sharing or full legacy records.
    pub fn from_path(path: &RetainedPath) -> Result<Self, Error> {
        let writer = match &*path.source.borrow() {
            Source::Packed(p) => Writer::Packed(p.to_builder()),
            Source::Geometry(p) => Writer::Legacy(Legacy::new(copy_geometry(p, [0.0; 2])?)),
        };
        Ok(Self {
            writer,
            fill: path.fill_rule(),
            inverse: path.inverse,
        })
    }
    fn legacy(&mut self) -> Result<&mut Legacy, Error> {
        if let Writer::Packed(packed) = &self.writer {
            let geometry = packed.snapshot().geometry()?;
            self.writer = Writer::Legacy(Legacy::new(geometry));
        }
        match &mut self.writer {
            Writer::Legacy(path) => Ok(path),
            Writer::Packed(_) => unreachable!(),
        }
    }
    /// Original current writer position; a closed contour selects its start.
    pub fn current_point(&self) -> [f32; 2] {
        match &self.writer {
            Writer::Packed(p) => p.current_point(),
            Writer::Legacy(p) => p.current,
        }
    }
    /// Original ordinary and inverse fill metadata.
    pub fn set_fill(&mut self, fill: FillRule, inverse: bool) {
        self.fill = fill;
        self.inverse = inverse;
        match &mut self.writer {
            Writer::Packed(p) => p.set_fill(fill, inverse),
            Writer::Legacy(p) => p.geometry.fill_rule = fill,
        }
    }
    /// Ordinary source fill selection.
    pub fn fill_rule(&self) -> FillRule {
        self.fill
    }
    /// Original empty writer test includes move-only contours as nonempty.
    pub fn is_empty(&self) -> bool {
        match &self.writer {
            Writer::Packed(path) => path.is_empty(),
            Writer::Legacy(path) => path.geometry.figures.is_empty(),
        }
    }
    /// Independent inverse selection.
    pub fn is_inverse(&self) -> bool {
        self.inverse
    }
    /// Begin or replace the original empty open contour.
    pub fn move_to(&mut self, point: [f32; 2]) -> Result<(), Error> {
        match &mut self.writer {
            Writer::Packed(p) => p.move_to(point),
            Writer::Legacy(p) => p.move_to(point),
        }
    }
    /// Append a source line, inserting the original implicit move if necessary.
    pub fn line_to(&mut self, point: [f32; 2]) -> Result<(), Error> {
        match &mut self.writer {
            Writer::Packed(p) => p.line_to(point),
            Writer::Legacy(p) => p.segment(PathSegmentKind::Line(point)),
        }
    }
    /// Append a source polynomial quadratic.
    pub fn quadratic_to(&mut self, points: [[f32; 2]; 2]) -> Result<(), Error> {
        match &mut self.writer {
            Writer::Packed(p) => p.quadratic_to(points),
            Writer::Legacy(p) => p.segment(PathSegmentKind::Quadratic(points)),
        }
    }
    /// Append a source polynomial cubic.
    pub fn cubic_to(&mut self, points: [[f32; 2]; 3]) -> Result<(), Error> {
        match &mut self.writer {
            Writer::Packed(p) => p.cubic_to(points),
            Writer::Legacy(p) => p.segment(PathSegmentKind::Cubic(points)),
        }
    }
    /// Original rational conic and its separate sixteen actual render quadratics.
    pub fn conic_to(&mut self, points: [[f32; 2]; 2], weight: f32) -> Result<(), Error> {
        let path = self.legacy()?;
        let start = path.current;
        super::retained_conic::append(path.ensure()?, [start, points[0], points[1]], weight)?;
        path.current = points[1];
        Ok(())
    }
    /// Relative move using the original current point.
    pub fn relative_move_to(&mut self, p: [f32; 2]) -> Result<(), Error> {
        self.move_to(add(self.current_point(), p))
    }
    /// Relative line using the original current point.
    pub fn relative_line_to(&mut self, p: [f32; 2]) -> Result<(), Error> {
        self.line_to(add(self.current_point(), p))
    }
    /// Relative quadratic; both points use the same original origin.
    pub fn relative_quadratic_to(&mut self, p: [[f32; 2]; 2]) -> Result<(), Error> {
        let start = self.current_point();
        self.quadratic_to(p.map(|p| add(start, p)))
    }
    /// Relative cubic; all three points use the same original origin.
    pub fn relative_cubic_to(&mut self, p: [[f32; 2]; 3]) -> Result<(), Error> {
        let start = self.current_point();
        self.cubic_to(p.map(|p| add(start, p)))
    }
    /// Relative conic forces the original materialization before selecting origin.
    pub fn relative_conic_to(&mut self, p: [[f32; 2]; 2], weight: f32) -> Result<(), Error> {
        let start = self.legacy()?.current;
        self.conic_to(p.map(|p| add(start, p)), weight)
    }
    /// Close the actual active contour without manufacturing an explicit final line.
    pub fn close(&mut self) -> Result<(), Error> {
        match &mut self.writer {
            Writer::Packed(p) => p.close(),
            Writer::Legacy(p) => {
                p.close();
                Ok(())
            }
        }
    }
    /// Original bulk common-verb triangle recording.
    pub fn add_triangles(&mut self, triangles: &[[[f32; 2]; 3]]) -> Result<(), Error> {
        if let Writer::Packed(p) = &mut self.writer {
            return p.add_triangles(triangles);
        }
        for &[a, b, c] in triangles {
            self.move_to(a)?;
            self.line_to(b)?;
            self.line_to(c)?;
            self.close()?;
        }
        Ok(())
    }
    /// Original polygon writer; an empty inventory does not alter current state.
    pub fn add_polygon(&mut self, points: &[[f32; 2]], closed: bool) -> Result<(), Error> {
        self.legacy()?;
        if let Some((first, rest)) = points.split_first() {
            self.move_to(*first)?;
            for &point in rest {
                self.line_to(point)?;
            }
            if closed {
                self.close()?;
            }
        }
        Ok(())
    }
    /// Original indexed rectangle traversal, preserving signed edge coordinates.
    pub fn add_rectangle(
        &mut self,
        bounds: [f32; 4],
        direction: PathDirection,
        start: u32,
    ) -> Result<(), Error> {
        if start > 3 {
            return Err(Error::InvalidInput);
        }
        self.legacy()?;
        let [l, t, r, b] = bounds;
        let points = [[l, t], [r, t], [r, b], [l, b]];
        let mut index = start as usize;
        self.move_to(points[index])?;
        for _ in 0..3 {
            index = (index
                + if direction == PathDirection::Clockwise {
                    1
                } else {
                    3
                })
                % 4;
            self.line_to(points[index])?;
        }
        self.close()
    }
    /// Original indexed rounded-rectangle path, sharing the owned source emitter.
    pub fn add_round_rectangle(
        &mut self,
        rect: RoundRect,
        direction: PathDirection,
        start: u32,
    ) -> Result<(), Error> {
        let geometry = rect.to_geometry(direction, start)?;
        let path = self.legacy()?;
        for mut figure in geometry.figures {
            path.move_to(figure.start)?;
            let target = path.ensure()?;
            target
                .segments
                .try_reserve(figure.segments.len())
                .map_err(|_| Error::Capacity)?;
            target.segments.append(&mut figure.segments);
            path.close();
        }
        Ok(())
    }
    /// Original direction-dependent default rounded-rectangle starting index.
    pub fn add_round_rect(
        &mut self,
        rect: RoundRect,
        direction: PathDirection,
    ) -> Result<(), Error> {
        self.add_round_rectangle(
            rect,
            direction,
            if direction == PathDirection::Clockwise {
                6
            } else {
                7
            },
        )
    }
    /// Append or extend original source figures, applying even a zero offset.
    pub fn add_path(
        &mut self,
        source: &RetainedPath,
        offset: [f32; 2],
        mode: PathAddMode,
    ) -> Result<(), Error> {
        let source = source.geometry()?;
        let source = copy_geometry(&source, offset)?;
        let path = self.legacy()?;
        let extend = mode == PathAddMode::Extend && !path.geometry.figures.is_empty();
        for (index, mut figure) in source.figures.into_iter().enumerate() {
            if extend && index == 0 {
                path.segment(PathSegmentKind::Line(figure.start))?;
                let end = figure
                    .segments
                    .last()
                    .map_or(path.current, PathSegment::end);
                let target = path.ensure()?;
                target
                    .segments
                    .try_reserve(figure.segments.len())
                    .map_err(|_| Error::Capacity)?;
                target.segments.append(&mut figure.segments);
                path.current = end;
                if figure.is_closed {
                    path.close();
                }
            } else {
                path.push_figure(figure)?;
            }
        }
        Ok(())
    }
    /// Original copy/transform/zero-offset sequence for matrix path addition.
    pub fn add_transformed_path(
        &mut self,
        source: &RetainedPath,
        matrix: EffectMatrix,
        mode: PathAddMode,
    ) -> Result<(), Error> {
        let mut copy = source.to_builder()?;
        copy.transform(matrix)?;
        self.add_path(&copy.detach(), [0.0; 2], mode)
    }
    /// Reverse contour order and source segments, regenerating conic groups from
    /// their original source points and swapping actual endpoint cap metadata.
    pub fn reverse_add_path(&mut self, source: &RetainedPath) -> Result<(), Error> {
        let source = source.geometry()?;
        if source.deferred.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        let path = self.legacy()?;
        for figure in source.figures.iter().rev() {
            path.push_figure(super::retained_conic::reverse(figure)?)?;
        }
        Ok(())
    }
    /// Original packed homogeneous point mapping versus legacy Vector2 mapping.
    /// Both routes retain their independent source arithmetic and conic metadata.
    pub fn transform(&mut self, matrix: EffectMatrix) -> Result<(), Error> {
        match &mut self.writer {
            Writer::Packed(p) => p.transform(matrix),
            Writer::Legacy(p) => {
                let [a, b, x, c, d, y, u, v, w] = matrix.0;
                let transform = Matrix4([
                    [a, c, 0.0, u],
                    [b, d, 0.0, v],
                    [0.0, 0.0, 1.0, 0.0],
                    [x, y, 0.0, w],
                ]);
                p.geometry = p.geometry.transformed(transform)?;
                p.restore();
                Ok(())
            }
        }
    }
    /// Original translation follows the selected packed/legacy transform route.
    pub fn offset(&mut self, offset: [f32; 2]) -> Result<(), Error> {
        self.transform(EffectMatrix::translation(offset))
    }
    /// Original control envelope without forcing legacy materialization.
    pub fn bounds(&self) -> [f32; 4] {
        match &self.writer {
            Writer::Packed(p) => p.bounds(),
            Writer::Legacy(p) => p.geometry.bounds().unwrap_or([0.0; 4]),
        }
    }
    /// Original exact polynomial/arc bound query.
    pub fn tight_bounds(&mut self) -> Result<[f32; 4], Error> {
        match &mut self.writer {
            Writer::Packed(p) => p.tight_bounds(),
            Writer::Legacy(p) => p.geometry.tight_bounds(),
        }
    }
    /// Snapshot shares packed commands, or performs the original zero-offset
    /// legacy copy without changing current writer or any source generation.
    pub fn snapshot(&self) -> Result<RetainedPath, Error> {
        let source = match &self.writer {
            Writer::Packed(p) => Source::Packed(p.snapshot()),
            Writer::Legacy(p) => Source::Geometry(Arc::new(copy_geometry(&p.geometry, [0.0; 2])?)),
        };
        Ok(RetainedPath {
            source: RefCell::new(source),
            inverse: self.inverse,
        })
    }
    /// Transfer the actual generation and reset writer/fill to source defaults.
    pub fn detach(&mut self) -> RetainedPath {
        let old = std::mem::take(self);
        let source = match old.writer {
            Writer::Packed(mut p) => Source::Packed(p.detach()),
            Writer::Legacy(p) => Source::Geometry(Arc::new(p.geometry)),
        };
        RetainedPath {
            source: RefCell::new(source),
            inverse: old.inverse,
        }
    }
    /// Rewind retained storage, preserving the already-selected legacy route.
    pub fn reset(&mut self) {
        match &mut self.writer {
            Writer::Packed(p) => p.reset(),
            Writer::Legacy(p) => {
                p.geometry.figures.clear();
                p.geometry.deferred = None;
                p.geometry.fill_rule = FillRule::Nonzero;
                p.restore();
            }
        }
        self.inverse = false;
        self.fill = FillRule::Nonzero;
    }
}
fn copy_geometry(source: &PathGeometry, offset: [f32; 2]) -> Result<PathGeometry, Error> {
    if source.deferred.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    let mut output = PathGeometry {
        fill_rule: source.fill_rule,
        ..Default::default()
    };
    output
        .figures
        .try_reserve_exact(source.figures.len())
        .map_err(|_| Error::Capacity)?;
    for figure in &source.figures {
        output
            .figures
            .push(super::raw_path::copy_figure(figure, offset)?);
    }
    Ok(output)
}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    let p = (wide::f32x4::from([a[0], a[1], 0.0, 0.0]) + wide::f32x4::from([b[0], b[1], 0.0, 0.0]))
        .to_array();
    [p[0], p[1]]
}
fn near(a: [f32; 2], b: [f32; 2]) -> bool {
    let x = a[0] - b[0];
    let y = a[1] - b[1];
    x * x + y * y <= EPS * EPS
}
