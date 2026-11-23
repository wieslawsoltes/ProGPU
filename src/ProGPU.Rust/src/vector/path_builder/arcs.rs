//! Original endpoint, oval and tangent arc writer arithmetic.
use super::*;
use std::f32::consts::PI;
use wide::f32x4 as V;
impl PathBuilder {
    /// Original SVG endpoint arc. Invalid/near-zero radii select a line; rotation
    /// and endpoints otherwise retain original values until geometric admission.
    pub fn arc_to(
        &mut self,
        radii: [f32; 2],
        rotation: f32,
        large: bool,
        direction: PathDirection,
        end: [f32; 2],
    ) -> Result<(), Error> {
        let path = self.legacy()?;
        path.ensure()?;
        let kind = if radii.iter().any(|r| !r.is_finite() || r.abs() <= EPS) {
            PathSegmentKind::Line(end)
        } else {
            PathSegmentKind::Arc(ArcSegment {
                end,
                size: radii.map(f32::abs),
                rotation,
                large,
                sweep: direction,
            })
        };
        path.segment(kind)
    }
    /// Original relative endpoint arc uses the materialized current point.
    pub fn relative_arc_to(
        &mut self,
        radii: [f32; 2],
        rotation: f32,
        large: bool,
        direction: PathDirection,
        end: [f32; 2],
    ) -> Result<(), Error> {
        let current = self.legacy()?.current;
        self.arc_to(radii, rotation, large, direction, add(current, end))
    }
    /// Original tangent arc through two lines; degenerate input falls back to
    /// the actual corner. This records one analytic arc without flattening.
    pub fn tangent_arc_to(
        &mut self,
        corner: [f32; 2],
        next: [f32; 2],
        radius: f32,
    ) -> Result<(), Error> {
        let path = self.legacy()?;
        path.ensure()?;
        let mut incoming = vector(path.current) - vector(corner);
        let mut outgoing = vector(next) - vector(corner);
        let incoming_length = length(incoming);
        let outgoing_length = length(outgoing);
        let radius = radius.abs();
        if !radius.is_finite() || radius <= EPS || incoming_length <= EPS || outgoing_length <= EPS
        {
            return path.segment(PathSegmentKind::Line(corner));
        }
        incoming /= V::splat(incoming_length);
        outgoing /= V::splat(outgoing_length);
        let a = incoming.to_array();
        let b = outgoing.to_array();
        let dot = (a[0] * b[0] + a[1] * b[1]).clamp(-1.0, 1.0);
        let cross = a[0] * b[1] - a[1] * b[0];
        let tangent = (dot.acos() * 0.5).tan();
        if cross.abs() <= EPS || tangent.abs() <= EPS {
            return path.segment(PathSegmentKind::Line(corner));
        }
        let distance = radius / tangent;
        if !distance.is_finite() {
            return path.segment(PathSegmentKind::Line(corner));
        }
        let start = xy(vector(corner) + incoming * V::splat(distance));
        let end = xy(vector(corner) + outgoing * V::splat(distance));
        if !near(path.current, start) {
            path.segment(PathSegmentKind::Line(start))?;
        }
        path.segment(PathSegmentKind::Arc(ArcSegment {
            end,
            size: [radius; 2],
            rotation: 0.0,
            large: false,
            sweep: if cross < 0.0 {
                SweepDirection::Clockwise
            } else {
                SweepDirection::Counterclockwise
            },
        }))
    }
    /// Original oval ArcTo connects its start but emits no arc for a full turn.
    /// AddArc has its independent full-turn/closure policy.
    pub fn oval_arc_to(
        &mut self,
        oval: [f32; 4],
        start_degrees: f32,
        sweep_degrees: f32,
        force_move: bool,
    ) -> Result<(), Error> {
        let path = self.legacy()?;
        let Some(arc) = Oval::new(oval, start_degrees, sweep_degrees) else {
            return Ok(());
        };
        arc.connect(path, force_move)?;
        if sweep_degrees.abs() >= 360.0 {
            return Ok(());
        }
        arc.append(path)
    }
    /// Original independent arc contour, clamped to one turn and closed only
    /// when the original requested sweep spans at least a full turn.
    pub fn add_arc(
        &mut self,
        oval: [f32; 4],
        start_degrees: f32,
        sweep_degrees: f32,
    ) -> Result<(), Error> {
        let path = self.legacy()?;
        let Some(arc) = Oval::new(oval, start_degrees, sweep_degrees) else {
            return Ok(());
        };
        arc.connect(path, true)?;
        arc.append(path)?;
        if sweep_degrees.abs() >= 360.0 {
            path.close();
        }
        Ok(())
    }
    /// Original full analytic oval; Close is retained even when oval admission
    /// emitted no arc, matching the source's independent closure operation.
    pub fn add_oval(&mut self, oval: [f32; 4], direction: PathDirection) -> Result<(), Error> {
        let path = self.legacy()?;
        if let Some(arc) = Oval::new(
            oval,
            0.0,
            if direction == PathDirection::Clockwise {
                360.0
            } else {
                -360.0
            },
        ) {
            arc.connect(path, true)?;
            arc.append(path)?;
        }
        path.close();
        Ok(())
    }
    /// Original circle forwards its signed radius rectangle to oval construction.
    pub fn add_circle(
        &mut self,
        center: [f32; 2],
        radius: f32,
        direction: PathDirection,
    ) -> Result<(), Error> {
        self.add_oval(
            [
                center[0] - radius,
                center[1] - radius,
                center[0] + radius,
                center[1] + radius,
            ],
            direction,
        )
    }
}
struct Oval {
    center: [f32; 2],
    radii: [f32; 2],
    start: f32,
    sweep: f32,
    point: [f32; 2],
}
impl Oval {
    fn new([l, t, r, b]: [f32; 4], start: f32, sweep: f32) -> Option<Self> {
        let center = [l + (r - l) / 2.0, t + (b - t) / 2.0];
        let radii = [(r - l).abs() * 0.5, (b - t).abs() * 0.5];
        if !start.is_finite()
            || !sweep.is_finite()
            || center.iter().chain(&radii).any(|v| !v.is_finite())
            || radii[0] <= EPS
            || radii[1] <= EPS
        {
            return None;
        }
        let sweep = sweep.clamp(-360.0, 360.0);
        if sweep.abs() <= EPS {
            return None;
        }
        Some(Self {
            center,
            radii,
            start,
            sweep,
            point: point(center, radii, start),
        })
    }
    fn connect(&self, path: &mut Legacy, force_move: bool) -> Result<(), Error> {
        if force_move || path.geometry.figures.is_empty() {
            return path.move_to(self.point);
        }
        path.ensure()?;
        if !near(path.current, self.point) {
            path.segment(PathSegmentKind::Line(self.point))?;
        }
        Ok(())
    }
    fn append(&self, path: &mut Legacy) -> Result<(), Error> {
        let count = (self.sweep.abs() / 180.0).ceil().max(1.0) as usize;
        let sweep = self.sweep / count as f32;
        for index in 1..=count {
            let end = point(self.center, self.radii, self.start + sweep * index as f32);
            path.segment(PathSegmentKind::Arc(ArcSegment {
                end,
                size: self.radii,
                rotation: 0.0,
                large: sweep.abs() >= 180.0 - EPS,
                sweep: if sweep >= 0.0 {
                    SweepDirection::Clockwise
                } else {
                    SweepDirection::Counterclockwise
                },
            }))?;
        }
        Ok(())
    }
}
fn point(center: [f32; 2], radii: [f32; 2], degrees: f32) -> [f32; 2] {
    let angle = degrees * PI / 180.0;
    [
        center[0] + radii[0] * snap(angle.cos()),
        center[1] + radii[1] * snap(angle.sin()),
    ]
}
fn snap(v: f32) -> f32 {
    if v.abs() <= EPS {
        0.0
    } else if (v - 1.0).abs() <= EPS {
        1.0
    } else if (v + 1.0).abs() <= EPS {
        -1.0
    } else {
        v
    }
}
fn vector(p: [f32; 2]) -> V {
    V::from([p[0], p[1], 0.0, 0.0])
}
fn xy(v: V) -> [f32; 2] {
    let [x, y, _, _] = v.to_array();
    [x, y]
}
fn length(v: V) -> f32 {
    let [x, y, _, _] = v.to_array();
    (x * x + y * y).sqrt()
}
