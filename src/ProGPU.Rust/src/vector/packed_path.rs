//! Original contiguous common-path recording, copy-on-write snapshots and lazy
//! translation. Legacy arc/conic object storage remains an independent adapter.
use super::{
    EffectMatrix, FillRule, MeasureCommand, MeasuredPath, PathEffectSource, PathFigure,
    PathGeometry, PathSegment, PathSegmentKind,
};
use crate::Error;
use std::{cell::RefCell, sync::Arc};
use wide::f32x4 as V;
mod bounds;
use bounds::Bounds;

/// Original common path verb inventory, retaining every actual control point.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum PackedPathCommand {
    /// Begin or replace an empty open contour.
    Move([f32; 2]),
    /// Original line endpoint.
    Line([f32; 2]),
    /// Original quadratic control and endpoint.
    Quadratic([[f32; 2]; 2]),
    /// Original cubic controls and endpoint.
    Cubic([[f32; 2]; 3]),
    /// Original explicit contour closure.
    Close,
}
impl PackedPathCommand {
    fn map(self, mut map: impl FnMut([f32; 2]) -> [f32; 2]) -> Self {
        match self {
            Self::Move(p) => Self::Move(map(p)),
            Self::Line(p) => Self::Line(map(p)),
            Self::Quadratic(p) => Self::Quadratic(p.map(map)),
            Self::Cubic(p) => Self::Cubic(p.map(map)),
            Self::Close => Self::Close,
        }
    }
    fn measured(self) -> MeasureCommand {
        match self {
            Self::Move(p) => MeasureCommand::Move(p),
            Self::Line(p) => MeasureCommand::Line(p),
            Self::Quadratic(p) => MeasureCommand::Quadratic(p),
            Self::Cubic(p) => MeasureCommand::Cubic(p),
            Self::Close => MeasureCommand::Close,
        }
    }
}
thread_local! { static COMMAND_POOL:RefCell<Option<Vec<PackedPathCommand>>>=const{RefCell::new(None)}; }
#[derive(Debug)]
struct Buffer {
    commands: Vec<PackedPathCommand>,
}
impl Buffer {
    fn rent() -> Self {
        Self {
            commands: COMMAND_POOL
                .try_with(|v| v.borrow_mut().take())
                .ok()
                .flatten()
                .unwrap_or_default(),
        }
    }
}
impl Drop for Buffer {
    fn drop(&mut self) {
        self.commands.clear();
        if self.commands.capacity() <= 4096 {
            let _ = COMMAND_POOL.try_with(|pool| {
                if let Ok(mut pool) = pool.try_borrow_mut()
                    && pool.is_none()
                {
                    *pool = Some(std::mem::take(&mut self.commands));
                }
            });
        }
    }
}
#[derive(Clone, Debug)]
struct Data {
    buffer: Arc<Buffer>,
    count: usize,
    current: [f32; 2],
    start: [f32; 2],
    open: bool,
    has_segments: bool,
    bounds: Bounds,
    tight: Bounds,
    track_tight: bool,
    pending: [f32; 2],
}
impl Data {
    fn new(track_tight: bool) -> Self {
        Self {
            buffer: Arc::new(Buffer::rent()),
            count: 0,
            current: [0.0; 2],
            start: [0.0; 2],
            open: false,
            has_segments: false,
            bounds: Bounds::default(),
            tight: Bounds::default(),
            track_tight,
            pending: [0.0; 2],
        }
    }
    fn reset(&mut self) {
        self.count = 0;
        self.current = [0.0; 2];
        self.start = [0.0; 2];
        self.open = false;
        self.has_segments = false;
        self.bounds = Bounds::default();
        self.tight = Bounds::default();
        self.pending = [0.0; 2];
    }
    fn writable(&mut self, required: usize) -> Result<(), Error> {
        let capacity = self.buffer.commands.capacity();
        if Arc::get_mut(&mut self.buffer).is_some() && capacity >= required {
            return Ok(());
        }
        let target = if capacity == 0 {
            required.max(64)
        } else if capacity >= required {
            capacity
        } else {
            capacity
                .checked_mul(2)
                .ok_or(Error::Capacity)?
                .max(required)
        };
        if let Some(buffer) = Arc::get_mut(&mut self.buffer) {
            buffer
                .commands
                .try_reserve_exact(target.saturating_sub(buffer.commands.len()))
                .map_err(|_| Error::Capacity)?;
        } else {
            let mut commands = Vec::new();
            commands
                .try_reserve_exact(target)
                .map_err(|_| Error::Capacity)?;
            commands.extend_from_slice(&self.buffer.commands[..self.count]);
            self.buffer = Arc::new(Buffer { commands });
        }
        Ok(())
    }
    fn apply_pending(&mut self) -> Result<(), Error> {
        if self.pending == [0.0; 2] || self.count == 0 {
            self.pending = [0.0; 2];
            return Ok(());
        }
        self.writable(self.count)?;
        let offset = self.pending;
        let buffer = Arc::get_mut(&mut self.buffer).ok_or(Error::ResourceDomain)?;
        for command in &mut buffer.commands[..self.count] {
            *command = command.map(|p| add(p, offset));
        }
        self.pending = [0.0; 2];
        Ok(())
    }
    fn write(&mut self, command: PackedPathCommand) {
        let buffer = Arc::get_mut(&mut self.buffer).expect("exclusive packed command storage");
        if self.count < buffer.commands.len() {
            buffer.commands[self.count] = command;
        } else {
            buffer.commands.push(command);
        }
        self.count += 1;
    }
    fn append(&mut self, command: PackedPathCommand) -> Result<(), Error> {
        self.apply_pending()?;
        self.writable(self.count.checked_add(1).ok_or(Error::Capacity)?)?;
        self.write(command);
        Ok(())
    }
    fn include(&mut self, p: [f32; 2]) {
        self.bounds.include(p);
        if self.track_tight {
            self.tight.include(p);
        }
    }
    fn move_to(&mut self, p: [f32; 2]) -> Result<(), Error> {
        self.apply_pending()?;
        if self.open
            && !self.has_segments
            && self.count > 0
            && matches!(
                self.buffer.commands[self.count - 1],
                PackedPathCommand::Move(_)
            )
        {
            self.writable(self.count)?;
            Arc::get_mut(&mut self.buffer)
                .ok_or(Error::ResourceDomain)?
                .commands[self.count - 1] = PackedPathCommand::Move(p);
            self.recalculate();
        } else {
            self.append(PackedPathCommand::Move(p))?;
        }
        self.current = p;
        self.start = p;
        self.open = true;
        self.has_segments = false;
        self.include(p);
        Ok(())
    }
    fn ensure_figure(&mut self) -> Result<(), Error> {
        if !self.open {
            self.move_to(self.current)?;
        }
        Ok(())
    }
    fn segment(&mut self, command: PackedPathCommand) -> Result<(), Error> {
        self.ensure_figure()?;
        self.append(command)?;
        match command {
            PackedPathCommand::Line(p) => {
                self.current = p;
                self.include(p);
            }
            PackedPathCommand::Quadratic([control, p]) => {
                if self.track_tight {
                    self.tight.quadratic(self.current, control, p);
                }
                self.current = p;
                self.bounds.include(control);
                self.include(p);
            }
            PackedPathCommand::Cubic([a, b, p]) => {
                if self.track_tight {
                    self.tight.cubic(self.current, a, b, p);
                }
                self.current = p;
                self.bounds.include(a);
                self.bounds.include(b);
                self.include(p);
            }
            _ => unreachable!(),
        }
        self.has_segments = true;
        Ok(())
    }
    fn close(&mut self) -> Result<(), Error> {
        if !self.open {
            return Ok(());
        }
        self.append(PackedPathCommand::Close)?;
        self.current = self.start;
        self.open = false;
        self.has_segments = false;
        Ok(())
    }
    fn recalculate(&mut self) {
        self.bounds = Bounds::default();
        self.tight = Bounds::default();
        let mut current = [0.0; 2];
        for command in &self.buffer.commands[..self.count] {
            accumulate(
                *command,
                &mut current,
                &mut self.bounds,
                self.track_tight.then_some(&mut self.tight),
            );
        }
    }
    fn transform(&mut self, matrix: EffectMatrix) -> Result<(), Error> {
        if matrix == EffectMatrix::IDENTITY {
            return Ok(());
        }
        let m = matrix.0;
        if m[0] == 1.0
            && m[4] == 1.0
            && m[1] == 0.0
            && m[3] == 0.0
            && m[6] == 0.0
            && m[7] == 0.0
            && m[8] == 1.0
            && m[2].is_finite()
            && m[5].is_finite()
        {
            let offset = [m[2], m[5]];
            self.pending = add(self.pending, offset);
            self.bounds.translate(offset);
            self.tight.translate(offset);
            self.current = add(self.current, offset);
            self.start = add(self.start, offset);
            return Ok(());
        }
        self.apply_pending()?;
        self.writable(self.count)?;
        let buffer = Arc::get_mut(&mut self.buffer).ok_or(Error::ResourceDomain)?;
        for command in &mut buffer.commands[..self.count] {
            *command = command.map(|p| matrix.point(p));
        }
        self.recalculate();
        self.current = matrix.point(self.current);
        self.start = matrix.point(self.start);
        Ok(())
    }
    fn tight_bounds(&mut self) -> Result<[f32; 4], Error> {
        if self.track_tight {
            return Ok(self.tight.rect());
        }
        self.apply_pending()?;
        let mut bounds = Bounds::default();
        let mut ignored = Bounds::default();
        let mut current = [0.0; 2];
        for command in &self.buffer.commands[..self.count] {
            accumulate(*command, &mut current, &mut ignored, Some(&mut bounds));
        }
        Ok(bounds.rect())
    }
    fn commands(&mut self) -> Result<PackedPathCommands, Error> {
        self.apply_pending()?;
        Ok(PackedPathCommands {
            buffer: self.buffer.clone(),
            index: 0,
            count: self.count,
        })
    }
}
fn accumulate(
    command: PackedPathCommand,
    current: &mut [f32; 2],
    bounds: &mut Bounds,
    mut tight: Option<&mut Bounds>,
) {
    match command {
        PackedPathCommand::Move(p) | PackedPathCommand::Line(p) => {
            *current = p;
            bounds.include(p);
            if let Some(t) = tight {
                t.include(p);
            }
        }
        PackedPathCommand::Quadratic([control, p]) => {
            if let Some(t) = tight.as_deref_mut() {
                t.quadratic(*current, control, p);
            }
            *current = p;
            bounds.include(control);
            bounds.include(p);
            if let Some(t) = tight {
                t.include(p);
            }
        }
        PackedPathCommand::Cubic([a, b, p]) => {
            if let Some(t) = tight.as_deref_mut() {
                t.cubic(*current, a, b, p);
            }
            *current = p;
            bounds.include(a);
            bounds.include(b);
            bounds.include(p);
            if let Some(t) = tight {
                t.include(p);
            }
        }
        // Original scan does not reset current on Close. Every later segment is
        // preceded by the writer's actual Move, including a post-close extension.
        PackedPathCommand::Close => {}
    }
}
/// Immutable owned common-path generation. The private read cache materializes
/// original pending translation without changing semantic source commands.
#[derive(Debug)]
pub struct PackedPath {
    data: RefCell<Data>,
    fill_rule: FillRule,
    inverse: bool,
}
impl Clone for PackedPath {
    fn clone(&self) -> Self {
        Self {
            data: RefCell::new(self.data.borrow().clone()),
            fill_rule: self.fill_rule,
            inverse: self.inverse,
        }
    }
}
impl PackedPath {
    /// Original command count, including Move and Close slots.
    pub fn command_count(&self) -> usize {
        self.data.borrow().count
    }
    /// Whether no original command has been recorded.
    pub fn is_empty(&self) -> bool {
        self.command_count() == 0
    }
    /// Independent source winding policy.
    pub fn fill_rule(&self) -> FillRule {
        self.fill_rule
    }
    /// Independent original inverse-fill intent.
    pub fn is_inverse(&self) -> bool {
        self.inverse
    }
    /// Cached source control bounds; does not materialize pending translation.
    pub fn bounds(&self) -> [f32; 4] {
        self.data.borrow().bounds.rect()
    }
    /// Original derivative-root bounds with independent incremental/lazy policies.
    pub fn tight_bounds(&self) -> Result<[f32; 4], Error> {
        self.data.borrow_mut().tight_bounds()
    }
    /// Snapshot the actual translated original command storage once. The owned
    /// iterator retains this exact generation through later builder mutation.
    pub fn commands(&self) -> Result<PackedPathCommands, Error> {
        self.data.borrow_mut().commands()
    }
    /// Reopen original writer state without copying its shared command inventory.
    pub fn to_builder(&self) -> PackedPathBuilder {
        PackedPathBuilder {
            data: self.data.borrow().clone(),
            fill_rule: self.fill_rule,
            inverse: self.inverse,
        }
    }
    /// Transfer this snapshot's writer state into an independent mutable builder.
    pub fn into_builder(self) -> PackedPathBuilder {
        PackedPathBuilder {
            data: self.data.into_inner(),
            fill_rule: self.fill_rule,
            inverse: self.inverse,
        }
    }
    /// Publish actual analytic figures only at an explicit geometry boundary.
    pub fn geometry(&self) -> Result<PathGeometry, Error> {
        let mut result = PathGeometry {
            fill_rule: self.fill_rule,
            ..Default::default()
        };
        let mut figure = None;
        for command in self.commands()? {
            match command {
                PackedPathCommand::Move(p) => {
                    result.figures.try_reserve(1).map_err(|_| Error::Capacity)?;
                    result.figures.push(PathFigure::new(p, false));
                    figure = Some(result.figures.len() - 1);
                }
                PackedPathCommand::Close => {
                    let index = figure.take().ok_or(Error::ResourceDomain)?;
                    result.figures[index].is_closed = true;
                }
                _ => {
                    let index = figure.ok_or(Error::ResourceDomain)?;
                    let kind = match command {
                        PackedPathCommand::Line(p) => PathSegmentKind::Line(p),
                        PackedPathCommand::Quadratic(p) => PathSegmentKind::Quadratic(p),
                        PackedPathCommand::Cubic(p) => PathSegmentKind::Cubic(p),
                        _ => unreachable!(),
                    };
                    let segments = &mut result.figures[index].segments;
                    segments.try_reserve(1).map_err(|_| Error::Capacity)?;
                    segments.push(PathSegment::new(kind));
                }
            }
        }
        Ok(result)
    }
    /// Retain original raw commands for analytic measurement and slicing.
    pub fn measured_path(&self) -> Result<MeasuredPath, Error> {
        let input = self.commands()?;
        let mut output = Vec::new();
        output
            .try_reserve_exact(input.len())
            .map_err(|_| Error::Capacity)?;
        output.extend(input.map(PackedPathCommand::measured));
        Ok(MeasuredPath::new(output))
    }
    /// Bridge to owned path effects without losing inverse-fill intent.
    pub fn effect_source(&self) -> Result<PathEffectSource, Error> {
        Ok(PathEffectSource::new(
            self.measured_path()?,
            self.fill_rule,
            self.inverse,
        ))
    }
}
/// Reusable original packed common-path writer. Snapshots are O(1); the first
/// subsequent mutation copies the used prefix only when another owner retains it.
#[derive(Debug)]
pub struct PackedPathBuilder {
    data: Data,
    fill_rule: FillRule,
    inverse: bool,
}
impl Default for PackedPathBuilder {
    fn default() -> Self {
        Self::new()
    }
}
impl PackedPathBuilder {
    /// Original SKPathBuilder fast path computes control bounds while recording;
    /// tight derivative bounds are evaluated only on explicit request.
    pub fn new() -> Self {
        Self::with_tight_bounds(false)
    }
    /// Select the original generic packed-path incremental tight-bound policy.
    pub fn with_tight_bounds(track: bool) -> Self {
        Self {
            data: Data::new(track),
            fill_rule: FillRule::Nonzero,
            inverse: false,
        }
    }
    /// Original live writer point, including post-close and lazy translation state.
    pub fn current_point(&self) -> [f32; 2] {
        self.data.current
    }
    /// Original current contour start.
    pub fn contour_start(&self) -> [f32; 2] {
        self.data.start
    }
    /// Actual number of original common verbs recorded.
    pub fn command_count(&self) -> usize {
        self.data.count
    }
    /// Whether no command, including Move, is present.
    pub fn is_empty(&self) -> bool {
        self.data.count == 0
    }
    /// Independent complete winding and inverse-fill metadata.
    pub fn set_fill(&mut self, fill_rule: FillRule, inverse: bool) {
        self.fill_rule = fill_rule;
        self.inverse = inverse;
    }
    /// Begin a contour; original consecutive empty Moves replace the last Move.
    pub fn move_to(&mut self, p: [f32; 2]) -> Result<(), Error> {
        self.data.move_to(p)
    }
    /// Original relative Move from the live writer point.
    pub fn relative_move_to(&mut self, p: [f32; 2]) -> Result<(), Error> {
        self.move_to(add(self.data.current, p))
    }
    /// Append a line, implicitly beginning at the actual current point if closed.
    pub fn line_to(&mut self, p: [f32; 2]) -> Result<(), Error> {
        self.data.segment(PackedPathCommand::Line(p))
    }
    /// Original relative line from the live writer point.
    pub fn relative_line_to(&mut self, p: [f32; 2]) -> Result<(), Error> {
        self.line_to(add(self.data.current, p))
    }
    /// Retain one original quadratic control and endpoint.
    pub fn quadratic_to(&mut self, p: [[f32; 2]; 2]) -> Result<(), Error> {
        self.data.segment(PackedPathCommand::Quadratic(p))
    }
    /// Relative quadratic offsets all use the same original writer point.
    pub fn relative_quadratic_to(&mut self, p: [[f32; 2]; 2]) -> Result<(), Error> {
        let origin = self.data.current;
        self.quadratic_to(p.map(|p| add(origin, p)))
    }
    /// Retain original cubic controls and endpoint.
    pub fn cubic_to(&mut self, p: [[f32; 2]; 3]) -> Result<(), Error> {
        self.data.segment(PackedPathCommand::Cubic(p))
    }
    /// Relative cubic offsets all use the same original writer point.
    pub fn relative_cubic_to(&mut self, p: [[f32; 2]; 3]) -> Result<(), Error> {
        let origin = self.data.current;
        self.cubic_to(p.map(|p| add(origin, p)))
    }
    /// Close only a live contour and reset the writer point to its actual start.
    pub fn close(&mut self) -> Result<(), Error> {
        self.data.close()
    }
    /// Original four-command bulk triangle transport with whole-capacity preflight.
    pub fn add_triangles(&mut self, triangles: &[[[f32; 2]; 3]]) -> Result<(), Error> {
        if triangles.is_empty() {
            return Ok(());
        }
        self.data.apply_pending()?;
        let required = triangles
            .len()
            .checked_mul(4)
            .and_then(|n| self.data.count.checked_add(n))
            .ok_or(Error::Capacity)?;
        self.data.writable(required)?;
        for &p in triangles {
            for command in [
                PackedPathCommand::Move(p[0]),
                PackedPathCommand::Line(p[1]),
                PackedPathCommand::Line(p[2]),
                PackedPathCommand::Close,
            ] {
                self.data.write(command);
            }
            for p in p {
                self.data.include(p);
            }
        }
        self.data.current = triangles[triangles.len() - 1][0];
        self.data.start = self.data.current;
        self.data.open = false;
        self.data.has_segments = false;
        Ok(())
    }
    /// Original affine/projective point mapping; pure finite translation remains
    /// deferred with independent cumulative metadata arithmetic until observation.
    pub fn transform(&mut self, matrix: EffectMatrix) -> Result<(), Error> {
        self.data.transform(matrix)
    }
    /// Original cached control envelope.
    pub fn bounds(&self) -> [f32; 4] {
        self.data.bounds.rect()
    }
    /// Original cached or explicitly recomputed derivative-root bounds.
    pub fn tight_bounds(&mut self) -> Result<[f32; 4], Error> {
        self.data.tight_bounds()
    }
    /// Immutable generation sharing the exact original used command prefix/state.
    pub fn snapshot(&self) -> PackedPath {
        PackedPath {
            data: RefCell::new(self.data.clone()),
            fill_rule: self.fill_rule,
            inverse: self.inverse,
        }
    }
    /// Transfer the actual recorded generation and reset fill/writer state.
    pub fn detach(&mut self) -> PackedPath {
        let track = self.data.track_tight;
        let data = std::mem::replace(&mut self.data, Data::new(track));
        let result = PackedPath {
            data: RefCell::new(data),
            fill_rule: self.fill_rule,
            inverse: self.inverse,
        };
        self.fill_rule = FillRule::Nonzero;
        self.inverse = false;
        result
    }
    /// Rewind without mutating shared snapshot commands; retain bounded storage.
    pub fn reset(&mut self) {
        self.data.reset();
        self.fill_rule = FillRule::Nonzero;
        self.inverse = false;
    }
}
/// Exact-size owned iteration over one materialized command generation.
#[derive(Clone, Debug)]
pub struct PackedPathCommands {
    buffer: Arc<Buffer>,
    index: usize,
    count: usize,
}
impl Iterator for PackedPathCommands {
    type Item = PackedPathCommand;
    fn next(&mut self) -> Option<Self::Item> {
        if self.index == self.count {
            return None;
        }
        let command = self.buffer.commands[self.index];
        self.index += 1;
        Some(command)
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        let remaining = self.count - self.index;
        (remaining, Some(remaining))
    }
}
impl ExactSizeIterator for PackedPathCommands {}
impl std::iter::FusedIterator for PackedPathCommands {}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    let p = (V::from([a[0], a[1], 0.0, 0.0]) + V::from([b[0], b[1], 0.0, 0.0])).to_array();
    [p[0], p[1]]
}
