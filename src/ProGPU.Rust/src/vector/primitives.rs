//! Original primitive construction and allocation-free recognizers.
use super::*;
const EPSILON: f32 = 0.0001;
impl PathGeometry {
    /// Recognize the original single open stroked line without cap overrides.
    pub fn open_line(&self) -> Option<[[f32; 2]; 2]> {
        let [figure] = self.figures.as_slice() else {
            return None;
        };
        let [segment] = figure.segments.as_slice() else {
            return None;
        };
        if figure.is_closed
            || figure.start_cap.is_some()
            || figure.end_cap.is_some()
            || !segment.is_stroked
        {
            return None;
        }
        let PathSegmentKind::Line(end) = segment.kind else {
            return None;
        };
        (finite(figure.start) && finite(end)).then_some([figure.start, end])
    }
    /// Original axis/area rectangle recognition, including the implicit closing
    /// edge and original zero-radius arcs. This does not classify Boolean paths.
    pub fn axis_aligned_rectangle_bounds(&self) -> Option<[f32; 4]> {
        let [figure] = self.figures.as_slice() else {
            return None;
        };
        if !figure.is_filled || !figure.is_closed || figure.segments.len() < 3 {
            return None;
        }
        let start = figure.start;
        let mut current = start;
        let mut min = start;
        let mut max = start;
        let mut twice_area = 0.0_f64;
        for segment in &figure.segments {
            let next = match segment.kind {
                PathSegmentKind::Line(end) => end,
                PathSegmentKind::Arc(arc) if arc.size[0] == 0.0 || arc.size[1] == 0.0 => arc.end,
                _ => return None,
            };
            let delta = sub(next, current);
            if delta[0].abs() > EPSILON && delta[1].abs() > EPSILON {
                return None;
            }
            twice_area += f64::from(current[0]) * f64::from(next[1])
                - f64::from(next[0]) * f64::from(current[1]);
            for axis in 0..2 {
                min[axis] = minimum(min[axis], next[axis]);
                max[axis] = maximum(max[axis], next[axis]);
            }
            current = next;
        }
        let closing = sub(start, current);
        if closing[0].abs() > EPSILON && closing[1].abs() > EPSILON {
            return None;
        }
        twice_area += f64::from(current[0]) * f64::from(start[1])
            - f64::from(start[0]) * f64::from(current[1]);
        let width = max[0] - min[0];
        let height = max[1] - min[1];
        if width <= EPSILON || height <= EPSILON {
            return None;
        }
        let rectangle = f64::from(width) * f64::from(height);
        let polygon = twice_area.abs() * 0.5;
        ((polygon - rectangle).abs() <= f64::from(EPSILON).max(rectangle * f64::from(EPSILON)))
            .then_some([min[0], min[1], max[0], max[1]])
    }
    /// Original four-edge rectangle, or empty for nonpositive/nonfinite extent.
    /// Position is retained unchanged, including nonfinite source coordinates.
    pub fn rectangle(x: f32, y: f32, width: f32, height: f32) -> Self {
        if !positive(width) || !positive(height) {
            return Self::default();
        }
        let mut figure = PathFigure::new([x, y], true);
        figure.segments = [
            [x + width, y],
            [x + width, y + height],
            [x, y + height],
            [x, y],
        ]
        .map(|p| PathSegment::new(PathSegmentKind::Line(p)))
        .into();
        Self {
            figures: vec![figure],
            ..Default::default()
        }
    }
    /// Original elliptical rounded rectangle. Only a zero radius axis becomes
    /// square; positive radii below the rectangle epsilon keep all four arcs.
    pub fn rounded_rectangle(x: f32, y: f32, width: f32, height: f32, rx: f32, ry: f32) -> Self {
        if !positive(width) || !positive(height) {
            return Self::default();
        }
        let rx = minimum(rx.abs(), width * 0.5);
        let ry = minimum(ry.abs(), height * 0.5);
        if rx == 0.0 || ry == 0.0 {
            return Self::rectangle(x, y, width, height);
        }
        let right = x + width;
        let bottom = y + height;
        let mut figure = PathFigure::new([x + rx, y], true);
        figure.segments = vec![
            line([right - rx, y]),
            arc([right, y + ry], rx, ry),
            line([right, bottom - ry]),
            arc([right - rx, bottom], rx, ry),
            line([x + rx, bottom]),
            arc([x, bottom - ry], rx, ry),
            line([x, y + ry]),
            arc([x + rx, y], rx, ry),
        ];
        Self {
            figures: vec![figure],
            ..Default::default()
        }
    }
    /// Original four-quarter-arc ellipse, or empty for an inadmissible radius.
    pub fn ellipse(center: [f32; 2], rx: f32, ry: f32) -> Self {
        if !positive(rx) || !positive(ry) {
            return Self::default();
        }
        let [x, y] = center;
        let mut figure = PathFigure::new([x + rx, y], true);
        figure.segments = vec![
            arc([x, y + ry], rx, ry),
            arc([x - rx, y], rx, ry),
            arc([x, y - ry], rx, ry),
            arc([x + rx, y], rx, ry),
        ];
        Self {
            figures: vec![figure],
            ..Default::default()
        }
    }
}
fn positive(value: f32) -> bool {
    value.is_finite() && value > EPSILON
}
fn line(end: [f32; 2]) -> PathSegment {
    PathSegment::new(PathSegmentKind::Line(end))
}
fn arc(end: [f32; 2], rx: f32, ry: f32) -> PathSegment {
    PathSegment::new(PathSegmentKind::Arc(ArcSegment {
        end,
        size: [rx, ry],
        rotation: 0.0,
        large: false,
        sweep: SweepDirection::Clockwise,
    }))
}
