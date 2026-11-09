//! Original retained solid-path stroke compilation. Source curve, cap and join
//! frames remain distinct from the independent line-only widening/query APIs.
use super::*;
use crate::geometry::{Mesh, VectorVertex};
use std::sync::Arc;
mod vertices;
const EPS: f32 = 0.0001;

/// Actual source command and complete target frame for retained stroke expansion.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct StrokeFrame {
    /// Complete source-to-target transform, including the recorded transform.
    pub transform: Matrix4,
    /// Original command width policy. New source recorders use true.
    pub thickness_is_local: bool,
    /// Original command transform for legacy pre-scaled width recovery only.
    /// None is the original absent/default-command-transform sentinel.
    pub recorded_transform: Option<Matrix4>,
}
impl Default for StrokeFrame {
    fn default() -> Self {
        Self {
            transform: Matrix4::IDENTITY,
            thickness_is_local: true,
            recorded_transform: None,
        }
    }
}

/// Retained source stroke generation. Compilation uses the original analytic
/// line/Bezier/arc and affine-section shader contracts; no generic flattening
/// replaces a supported curve. Dashed owning-API preparation remains separate.
#[derive(Clone, Debug)]
pub struct StrokeDraw {
    /// Complete original figure/segment generation.
    pub path: Arc<PathGeometry>,
    /// Original pen, material and independent cap/join/frame policies.
    pub pen: Arc<Pen>,
    /// Actual complete transform and legacy width metadata.
    pub frame: StrokeFrame,
    /// Original aliased edge selection.
    pub aliased: bool,
    /// Independent retained ancestor/draw opacity used by material registration.
    pub semantic_opacity: f32,
}
impl StrokeDraw {
    /// Record a source-local pen under the identity frame, with unit opacity.
    pub fn new(path: Arc<PathGeometry>, pen: Arc<Pen>) -> Self {
        Self {
            path,
            pen,
            frame: StrokeFrame::default(),
            aliased: false,
            semantic_opacity: 1.0,
        }
    }
    /// Compile one complete original solid stroke into standalone material-zero
    /// vertices. Scene::draw_stroke supplies the actual retained material binding.
    /// Invalid source width/frame produces no geometry; unsupported dashed or
    /// deferred geometry rejects. Publication is atomic on allocation failure.
    pub fn compile(&self) -> Result<Mesh, Error> {
        let mut mesh = Mesh::default();
        self.append(&mut mesh, 0.0)?;
        Ok(mesh)
    }
    pub(crate) fn validate(&self) -> Result<(), Error> {
        if self.state()?.is_some() && (self.pen.has_dash_pattern() || self.path.deferred.is_some())
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
    pub(crate) fn is_renderable(&self) -> Result<bool, Error> {
        Ok(self.state()?.is_some())
    }
    pub(crate) fn append(&self, mesh: &mut Mesh, brush_index: f32) -> Result<(), Error> {
        self.validate()?;
        let Some(stroke) = self.state()? else {
            return Ok(());
        };
        let color = match self.pen.brush.kind {
            BrushKind::Solid(color) => color,
            _ => [1.0; 4],
        };
        let mut compiler = Compiler {
            draw: self,
            stroke,
            mesh,
            brush_index,
            color,
        };
        for figure in &self.path.figures {
            compiler.figure(figure)?;
        }
        Ok(())
    }
    fn state(&self) -> Result<Option<State>, Error> {
        self.pen.validate_join_semantics()?;
        let pen = &self.pen;
        if !pen.options.thickness.is_finite()
            || (!pen.is_hairline() && pen.options.thickness <= 0.0)
        {
            return Ok(None);
        }
        let transform = self.frame.transform;
        if !affine(transform) {
            return Ok(None);
        }
        let mut local = if pen.is_hairline() {
            1.0
        } else {
            pen.options.thickness
        };
        if !pen.is_hairline()
            && !self.frame.thickness_is_local
            && let Some(recorded) = self.frame.recorded_transform
        {
            if !affine(recorded) {
                return Ok(None);
            }
            let Some([scale, _]) = recorded.stroke_scales() else {
                return Ok(None);
            };
            local /= scale;
        }
        let Some([maximum, minimum]) = transform.stroke_scales() else {
            return Ok(None);
        };
        let (geometry, bounds, device, use_affine) = if pen.is_hairline() {
            (1.0 / maximum, 1.0 / minimum, 1.0, false)
        } else if pen.is_fixed() {
            (local / maximum, local / minimum, local, false)
        } else {
            (local, local, local * maximum, requires_affine(transform))
        };
        if [local, geometry, bounds, device]
            .iter()
            .any(|v| !v.is_finite() || *v <= 0.0)
        {
            return Ok(None);
        }
        Ok(Some(State {
            geometry,
            device,
            use_affine,
            encoded: if pen.is_hairline() {
                -1.0
            } else if pen.is_fixed() {
                encode_fixed(device)
            } else {
                device
            },
        }))
    }
}

impl Matrix4 {
    /// Original managed singular scales. The minimum uses abs(det)/maximum,
    /// preserving its source cancellation behavior independently of native
    /// geometry::Affine::scales. Only the linear XY coefficients are inspected.
    pub fn stroke_scales(self) -> Option<[f32; 2]> {
        let [a, b, c, d] = [self.0[0][0], self.0[0][1], self.0[1][0], self.0[1][1]].map(f64::from);
        if [a, b, c, d].iter().any(|v| !v.is_finite()) {
            return None;
        }
        let sum = a * a + b * b + c * c + d * d;
        let determinant = a * d - b * c;
        let discriminant = (sum * sum - 4.0 * determinant * determinant).max(0.0);
        let maximum = ((sum + discriminant.sqrt()) * 0.5).max(0.0).sqrt();
        let minimum = determinant.abs() / maximum;
        let scales = [maximum as f32, minimum as f32];
        scales
            .iter()
            .all(|v| v.is_finite() && *v > 0.0)
            .then_some(scales)
    }
}
fn affine(matrix: Matrix4) -> bool {
    let m = matrix.0;
    let determinant = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    [m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]
        .iter()
        .all(|v| v.is_finite())
        && [
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
        .all(|v| v.abs() <= EPS)
        && determinant.is_finite()
        && determinant != 0.0
}
fn requires_affine(m: Matrix4) -> bool {
    let x = [m.0[0][0], m.0[0][1]];
    let y = [m.0[1][0], m.0[1][1]];
    let (lx, ly) = (length(x), length(y));
    if !lx.is_finite() || !ly.is_finite() || lx <= EPS || ly <= EPS {
        return false;
    }
    (lx - ly).abs() > maximum(lx, ly) * EPS || dot(x, y).abs() > lx * ly * EPS
}
fn encode_fixed(width: f32) -> f32 {
    -maximum(width + 1.0, f32::from_bits(1.0f32.to_bits() + 1))
}
fn direction(m: Matrix4, v: [f32; 2]) -> [f32; 2] {
    // Original compositor subtracts transformed points; translation cancellation
    // is observable and must not become a direct linear-coefficient shortcut.
    sub(m.transform_point(v), m.transform_point([0.0; 2]))
}
fn select(candidates: &[[f32; 2]]) -> Option<[f32; 2]> {
    candidates.iter().copied().find(|v| {
        let n = length(*v);
        n.is_finite() && n > EPS
    })
}
fn tangent(segment: PathSegmentKind, start: [f32; 2], at_start: bool) -> Option<[f32; 2]> {
    match segment {
        PathSegmentKind::Line(end) => select(&[sub(end, start)]),
        PathSegmentKind::Quadratic([control, end])
        | PathSegmentKind::RationalQuadratic {
            points: [control, end],
            ..
        } => select(&[
            if at_start {
                sub(control, start)
            } else {
                sub(end, control)
            },
            sub(end, start),
        ]),
        PathSegmentKind::Cubic([c1, c2, end])
        | PathSegmentKind::RationalCubic {
            points: [c1, c2, end],
            ..
        } => select(&if at_start {
            [sub(c1, start), sub(c2, start), sub(end, start)]
        } else {
            [sub(end, c2), sub(end, c1), sub(end, start)]
        }),
        PathSegmentKind::Arc(arc) => {
            let c = arc.center(start)?;
            let phi = arc.rotation * std::f32::consts::PI / 180.0;
            let x = [c.radii[0] * phi.cos(), c.radii[0] * phi.sin()];
            let y = [-c.radii[1] * phi.sin(), c.radii[1] * phi.cos()];
            let theta = if at_start { c.theta } else { c.theta + c.delta };
            let v = mul(
                add(mul(mul(x, -1.0), theta.sin()), mul(y, theta.cos())),
                if c.delta < 0.0 { -1.0 } else { 1.0 },
            );
            select(&[v])
        }
    }
}

#[derive(Clone, Copy)]
struct State {
    geometry: f32,
    device: f32,
    encoded: f32,
    use_affine: bool,
}
struct Compiler<'a> {
    draw: &'a StrokeDraw,
    stroke: State,
    mesh: &'a mut Mesh,
    brush_index: f32,
    color: [f32; 4],
}
impl Compiler<'_> {
    fn figure(&mut self, figure: &PathFigure) -> Result<(), Error> {
        let start_cap = figure.start_cap.unwrap_or(self.draw.pen.options.start_cap);
        let end_cap = figure.end_cap.unwrap_or(self.draw.pen.options.end_cap);
        let mut current = figure.start;
        let mut first_direction = None;
        let mut previous_direction = None;
        let mut first_smooth = false;
        let mut last_cap = None;
        let mut degenerate = None;
        let mut first = true;
        for segment in &figure.segments {
            let start = current;
            let end = segment.end();
            if !segment.is_stroked {
                if !figure.is_closed {
                    if let Some((center, direction)) = last_cap {
                        self.cap(center, direction, false, end_cap)?;
                    } else if let Some(center) = degenerate {
                        self.degenerate(center)?;
                    }
                }
                current = end;
                first_direction = None;
                previous_direction = None;
                first_smooth = false;
                last_cap = None;
                degenerate = None;
                first = true;
                continue;
            }
            let incoming = tangent(segment.kind, start, true);
            let outgoing = tangent(segment.kind, start, false);
            if incoming.is_none()
                && outgoing.is_none()
                && dot(sub(start, end), sub(start, end)) <= EPS * EPS
            {
                if first {
                    degenerate = Some(start);
                }
                current = end;
                continue;
            }
            degenerate = None;
            if first && let Some(incoming) = incoming {
                first_direction = Some(incoming);
                first_smooth = segment.is_smooth_join;
                if !figure.is_closed {
                    self.cap(start, incoming, true, start_cap)?;
                }
            }
            if !first && let (Some(previous), Some(incoming)) = (previous_direction, incoming) {
                self.join(start, previous, incoming, segment.is_smooth_join)?;
            }
            let supported = match segment.kind {
                PathSegmentKind::Line(end) => {
                    self.line(start, end)?;
                    true
                }
                PathSegmentKind::Quadratic([control, end]) => {
                    self.bezier(&[start, control, end])?;
                    true
                }
                PathSegmentKind::Cubic([c1, c2, end]) => {
                    self.bezier(&[start, c1, c2, end])?;
                    true
                }
                PathSegmentKind::Arc(arc) => {
                    self.arc(start, arc)?;
                    true
                }
                // The original solid compositor advances these endpoints but
                // emits no rational body. Do not invent polynomial replacement.
                PathSegmentKind::RationalQuadratic { .. }
                | PathSegmentKind::RationalCubic { .. } => false,
            };
            current = end;
            first = false;
            if supported {
                previous_direction = outgoing;
                last_cap = outgoing.map(|d| (end, d));
            } else {
                previous_direction = None;
                last_cap = None;
            }
        }
        if !figure.is_closed {
            if let Some((center, direction)) = last_cap {
                self.cap(center, direction, false, end_cap)?;
            } else if let Some(center) = degenerate {
                self.degenerate(center)?;
            }
        } else if current != figure.start {
            let close = sub(figure.start, current);
            if let Some(previous) = previous_direction {
                self.join(current, previous, close, false)?;
            }
            self.line(current, figure.start)?;
            if let Some(first) = first_direction {
                self.join(figure.start, close, first, first_smooth)?;
            }
        } else if let (Some(previous), Some(first)) = (previous_direction, first_direction) {
            self.join(figure.start, previous, first, first_smooth)?;
        }
        Ok(())
    }
}
