//! Original fixed-subdivision pixel-center region scanner. Explicit CPU region
//! APIs never initialize WebGPU or substitute a painted path's raster samples.
use super::super::{FillRule, PathEffectSource, PathVerb, RawPathIterator, maximum, minimum};
use super::*;
use wide::{CmpGt, f32x4 as V};

/// Explicit original CPU producer policy, separate from GPU path rendering.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum RegionExecution {
    /// Four original pixel centers per intrinsic comparison batch.
    #[default]
    CpuSimd,
    /// Original independent scalar reference scan.
    ScalarReference,
}
/// Actual authored region work; these counters do not assert qualification.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RegionScanMetrics {
    /// Selected explicit producer route.
    pub execution: RegionExecution,
    /// Complete retained polygon point/edge inventory.
    pub edges: u64,
    /// Visited integer rows within the original source/clip bounds.
    pub rows: u64,
    /// Original integer centers visited, including centers rejected by the clip.
    pub pixels: u64,
    /// Canonical rectangles published after original band normalization.
    pub rectangles: u64,
}
impl Region {
    /// Original path constructor: outward source bounds become the finite clip.
    pub fn from_path(
        source: &PathEffectSource,
        execution: RegionExecution,
    ) -> Result<(Self, RegionScanMetrics), Error> {
        let geometry = source.geometry()?;
        let clip = Self::from_rect(integer_bounds(geometry.bounds().unwrap_or([0.0; 4]))?)?;
        let mut result = Self::new();
        let metrics = result.set_path(source, &clip, execution)?;
        Ok((result, metrics))
    }
    /// Original SetPath(path) overload admits exactly one closed three-line
    /// rectangle with all four original corner witnesses, independently of fill.
    pub fn set_rectangular_path(&mut self, geometry: &PathGeometry) -> Result<bool, Error> {
        let Some(rect) = rectangular(geometry)? else {
            self.clear();
            return Ok(false);
        };
        self.set_rect(rect)
    }
    /// Original clipped path assignment. Flatten each raw contour once, then
    /// compute original finite pixel-center winding/even-odd coverage inside clip.
    /// Inverse fill remains bounded by the original path/clip intersection.
    pub fn set_path(
        &mut self,
        source: &PathEffectSource,
        clip: &Self,
        execution: RegionExecution,
    ) -> Result<RegionScanMetrics, Error> {
        let mut metrics = RegionScanMetrics {
            execution,
            ..Default::default()
        };
        let geometry = source.geometry()?;
        if geometry.figures.is_empty() || clip.is_empty() {
            self.clear();
            return Ok(metrics);
        }
        let bounds = clip
            .bounds()
            .intersection(integer_bounds(geometry.bounds().unwrap_or([0.0; 4]))?);
        if !bounds.is_valid() {
            self.clear();
            return Ok(metrics);
        }
        let polygons = polygons(&geometry)?;
        if polygons.is_empty() {
            self.clear();
            return Ok(metrics);
        }
        for p in &polygons {
            metrics.edges = metrics
                .edges
                .checked_add(p.len() as u64)
                .ok_or(Error::Capacity)?;
        }
        let clip_state = clip.normalized()?;
        let mut result = Vec::new();
        let mut previous: HashMap<[i32; 2], usize> = HashMap::new();
        let mut current: HashMap<[i32; 2], usize> = HashMap::new();
        let [left, top, right, bottom] = bounds.0;
        for y in top..bottom {
            metrics.rows = metrics.rows.checked_add(1).ok_or(Error::Capacity)?;
            current.clear();
            let mut open = None;
            let mut x = left;
            while x < right {
                let count = if execution == RegionExecution::CpuSimd {
                    (i64::from(right) - i64::from(x)).min(4) as usize
                } else {
                    1
                };
                let mut admitted = [false; 4];
                let mut positions = [0.0; 4];
                for lane in 0..count {
                    let pixel = x + lane as i32;
                    positions[lane] = pixel as f32 + 0.5;
                    admitted[lane] = clip_state.bounds.contains(pixel, y)
                        && clip_state.rects.iter().any(|r| r.contains(pixel, y));
                }
                let covered = if admitted.iter().any(|v| *v) {
                    if execution == RegionExecution::CpuSimd {
                        contains_four(
                            &polygons,
                            source.fill_rule(),
                            source.is_inverse(),
                            positions,
                            y as f32 + 0.5,
                        )
                    } else {
                        [
                            contains_scalar(
                                &polygons,
                                source.fill_rule(),
                                source.is_inverse(),
                                positions[0],
                                y as f32 + 0.5,
                            ),
                            false,
                            false,
                            false,
                        ]
                    }
                } else {
                    [false; 4]
                };
                for lane in 0..count {
                    let pixel = x + lane as i32;
                    if admitted[lane] && covered[lane] {
                        if open.is_none() {
                            open = Some(pixel);
                        }
                    } else if let Some(start) = open.take() {
                        append_run(&mut result, &previous, &mut current, [start, pixel], y)?;
                    }
                }
                metrics.pixels = metrics
                    .pixels
                    .checked_add(count as u64)
                    .ok_or(Error::Capacity)?;
                x += count as i32;
            }
            if let Some(start) = open {
                append_run(&mut result, &previous, &mut current, [start, right], y)?;
            }
            std::mem::swap(&mut previous, &mut current);
        }
        drop(clip_state);
        self.publish(result)?;
        metrics.rectangles = self.normalized()?.rects.len() as u64;
        Ok(metrics)
    }
    /// Original path Boolean adapter, using a complete temporary region.
    pub fn op_path(
        &mut self,
        source: &PathEffectSource,
        operation: RegionOperation,
        execution: RegionExecution,
    ) -> Result<bool, Error> {
        let (region, _) = Self::from_path(source, execution)?;
        self.op_region(&region, operation)
    }
    /// Original path containment adapter; the path's own finite bounds clip it.
    pub fn contains_path(
        &self,
        source: &PathEffectSource,
        execution: RegionExecution,
    ) -> Result<bool, Error> {
        let (region, _) = Self::from_path(source, execution)?;
        self.contains_region(&region)
    }
    /// Original complete path/region intersection adapter.
    pub fn intersects_path(
        &self,
        source: &PathEffectSource,
        execution: RegionExecution,
    ) -> Result<bool, Error> {
        let (region, _) = Self::from_path(source, execution)?;
        self.intersects_region(&region)
    }
    /// Original quick path rejection considers the outward source bounds only.
    pub fn quick_reject_path(&self, source: &PathEffectSource) -> Result<bool, Error> {
        Ok(!self.intersects_rect(integer_bounds(
            source.geometry()?.bounds().unwrap_or([0.0; 4]),
        )?)?)
    }
}
fn append_run(
    result: &mut Vec<RegionRect>,
    previous: &HashMap<[i32; 2], usize>,
    current: &mut HashMap<[i32; 2], usize>,
    run: [i32; 2],
    y: i32,
) -> Result<(), Error> {
    let old = previous.get(&run).copied().filter(|i| result[*i].0[3] == y);
    let index = if let Some(index) = old {
        result[index].0[3] = y + 1;
        index
    } else {
        let index = result.len();
        push(result, RegionRect([run[0], y, run[1], y + 1]))?;
        index
    };
    current.try_reserve(1).map_err(|_| Error::Capacity)?;
    current.insert(run, index);
    Ok(())
}
fn integer_bounds(bounds: [f32; 4]) -> Result<RegionRect, Error> {
    let v = [
        bounds[0].floor(),
        bounds[1].floor(),
        bounds[2].ceil(),
        bounds[3].ceil(),
    ];
    if v.iter().any(|v| {
        !v.is_finite() || f64::from(*v) < f64::from(i32::MIN) || f64::from(*v) > f64::from(i32::MAX)
    }) {
        return Err(Error::InvalidInput);
    }
    Ok(RegionRect(v.map(|v| v as i32)))
}
fn rectangular(geometry: &PathGeometry) -> Result<Option<RegionRect>, Error> {
    if geometry.deferred.is_some() || geometry.figures.len() != 1 {
        return Ok(None);
    }
    let f = &geometry.figures[0];
    if !f.is_closed || f.segments.len() != 3 {
        return Ok(None);
    }
    let mut p = [[0.0; 2]; 4];
    p[0] = f.start;
    for (index, s) in f.segments.iter().enumerate() {
        let PathSegmentKind::Line(end) = s.kind else {
            return Ok(None);
        };
        p[index + 1] = end;
    }
    let mut b = [p[0][0], p[0][1], p[0][0], p[0][1]];
    for p in &p[1..] {
        b = [
            minimum(b[0], p[0]),
            minimum(b[1], p[1]),
            maximum(b[2], p[0]),
            maximum(b[3], p[1]),
        ];
    }
    if b.iter().any(|v| !v.is_finite()) || b[2] <= b[0] || b[3] <= b[1] {
        return Ok(None);
    }
    let corners = [[b[0], b[1]], [b[2], b[1]], [b[2], b[3]], [b[0], b[3]]];
    let mut witnessed = [false; 4];
    for p in p {
        let Some(index) = corners
            .iter()
            .position(|c| (p[0] - c[0]).abs() <= 0.0001 && (p[1] - c[1]).abs() <= 0.0001)
        else {
            return Ok(None);
        };
        witnessed[index] = true;
    }
    if witnessed.iter().any(|v| !*v) {
        return Ok(None);
    }
    let rect = integer_bounds(b)?;
    Ok(rect.is_valid().then_some(rect))
}
fn polygons(geometry: &PathGeometry) -> Result<Vec<Vec<[f32; 2]>>, Error> {
    let mut iterator = RawPathIterator::new(geometry)?;
    let mut out = Vec::new();
    let mut current = None;
    let mut p = [[0.0; 2]; 4];
    loop {
        let verb = iterator.next_points(&mut p);
        match verb {
            PathVerb::Move => {
                flush(&mut out, &mut current)?;
                let mut points = Vec::new();
                push(&mut points, p[0])?;
                current = Some(points);
            }
            PathVerb::Line => add_point(&mut current, p[1])?,
            PathVerb::Quadratic | PathVerb::Conic | PathVerb::Cubic => {
                let steps = if verb == PathVerb::Cubic { 24 } else { 16 };
                let v = p.map(pair);
                for step in 1..=steps {
                    let t = step as f32 / steps as f32;
                    let u = 1.0 - t;
                    let point = match verb {
                        PathVerb::Quadratic => {
                            V::splat(u * u) * v[0]
                                + V::splat(2.0 * u * t) * v[1]
                                + V::splat(t * t) * v[2]
                        }
                        PathVerb::Conic => {
                            let middle = 2.0 * iterator.conic_weight() * u * t;
                            let denominator = u * u + middle + t * t;
                            (V::splat(u * u) * v[0]
                                + V::splat(middle) * v[1]
                                + V::splat(t * t) * v[2])
                                / V::splat(denominator)
                        }
                        _ => {
                            V::splat(u * u * u) * v[0]
                                + V::splat(3.0 * u * u * t) * v[1]
                                + V::splat(3.0 * u * t * t) * v[2]
                                + V::splat(t * t * t) * v[3]
                        }
                    }
                    .to_array();
                    add_point(&mut current, [point[0], point[1]])?;
                }
            }
            PathVerb::Close => flush(&mut out, &mut current)?,
            PathVerb::Done => {
                flush(&mut out, &mut current)?;
                return Ok(out);
            }
        }
    }
}
fn pair(p: [f32; 2]) -> V {
    V::from([p[0], p[1], 0.0, 0.0])
}
fn add_point(current: &mut Option<Vec<[f32; 2]>>, point: [f32; 2]) -> Result<(), Error> {
    let Some(current) = current else {
        return Ok(());
    };
    if point.iter().any(|v| !v.is_finite()) {
        return Ok(());
    }
    let distinct = current.last().is_none_or(|previous| {
        let d = (pair(*previous) - pair(point)).to_array();
        d[0] * d[0] + d[1] * d[1] > 1e-12
    });
    if distinct {
        push(current, point)?;
    }
    Ok(())
}
fn flush(out: &mut Vec<Vec<[f32; 2]>>, current: &mut Option<Vec<[f32; 2]>>) -> Result<(), Error> {
    if let Some(p) = current.take()
        && p.len() >= 3
    {
        push(out, p)?;
    }
    Ok(())
}
fn contains_scalar(
    polygons: &[Vec<[f32; 2]>],
    fill: FillRule,
    inverse: bool,
    x: f32,
    y: f32,
) -> bool {
    let mut odd = false;
    let mut winding = 0_i32;
    for polygon in polygons {
        let mut previous = polygon[polygon.len() - 1];
        for &current in polygon {
            let upward = previous[1] <= y && current[1] > y;
            let downward = previous[1] > y && current[1] <= y;
            if upward || downward {
                let cross = previous[0]
                    + (y - previous[1]) * (current[0] - previous[0]) / (current[1] - previous[1]);
                if cross > x {
                    odd = !odd;
                    winding = winding.wrapping_add(if upward { 1 } else { -1 });
                }
            }
            previous = current;
        }
    }
    (if fill == FillRule::EvenOdd {
        odd
    } else {
        winding != 0
    }) ^ inverse
}
fn contains_four(
    polygons: &[Vec<[f32; 2]>],
    fill: FillRule,
    inverse: bool,
    x: [f32; 4],
    y: f32,
) -> [bool; 4] {
    let mut odd = [false; 4];
    let mut winding = [0_i32; 4];
    let x = V::from(x);
    // Edge traversal and its exact original intersection expression are ordered.
    // Four independent X admissions share one Y expression, never recomputed roots.
    for polygon in polygons {
        let mut previous = polygon[polygon.len() - 1];
        for &current in polygon {
            let upward = previous[1] <= y && current[1] > y;
            let downward = previous[1] > y && current[1] <= y;
            if upward || downward {
                let cross = previous[0]
                    + (y - previous[1]) * (current[0] - previous[0]) / (current[1] - previous[1]);
                let mask = V::splat(cross).cmp_gt(x).to_array();
                for lane in 0..4 {
                    if mask[lane].to_bits() != 0 {
                        odd[lane] = !odd[lane];
                        winding[lane] = winding[lane].wrapping_add(if upward { 1 } else { -1 });
                    }
                }
            }
            previous = current;
        }
    }
    std::array::from_fn(|i| {
        (if fill == FillRule::EvenOdd {
            odd[i]
        } else {
            winding[i] != 0
        }) ^ inverse
    })
}
