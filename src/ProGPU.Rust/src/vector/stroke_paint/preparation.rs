//! Immutable original dash geometry keys. A replaced source Arc invalidates
//! topology and all preparations; paint changes reuse geometry without stale paint.
use super::*;
use crate::path::Path;

pub(crate) enum PreparedStroke {
    Stroke(StrokeDraw),
    Fill(Path),
    Empty,
}
#[derive(Debug, Default)]
pub(super) struct Cache {
    source: Option<Arc<PathGeometry>>,
    linear: Option<bool>,
    linear_entry: Option<LinearEntry>,
    generic: Option<(Key, Arc<PathGeometry>)>,
    derived: Option<(Arc<Pen>, f32, Arc<Pen>)>,
}
#[derive(Debug)]
struct LinearEntry {
    key: Key,
    // None retains the source's failed complete preparation for this exact key.
    result: Option<LinearResult>,
}
#[derive(Debug)]
enum LinearResult {
    Stroke(Arc<PathGeometry>, Arc<Pen>),
    Fill(Option<Path>),
}
#[derive(Debug)]
struct Key {
    width: f32,
    options: PenOptions,
    intervals: Option<Arc<[f64]>>,
}
impl Key {
    fn new(pen: &Pen, width: f32) -> Self {
        Self {
            width,
            options: pen.options,
            intervals: pen.shared_dashes(),
        }
    }
    fn matches(&self, pen: &Pen, width: f32, linear: bool) -> bool {
        let a = self.options;
        let b = pen.options;
        let width_equal = if linear {
            equal(f64::from(self.width), f64::from(width))
        } else {
            self.width == width
        };
        let offset_equal = if linear {
            equal(a.dash_offset, b.dash_offset)
        } else {
            a.dash_offset == b.dash_offset
        };
        let intervals = self.intervals.as_deref().unwrap_or_default();
        let other = pen.dash_array().unwrap_or_default();
        width_equal
            && offset_equal
            && a.start_cap == b.start_cap
            && a.end_cap == b.end_cap
            && a.dash_cap == b.dash_cap
            && (!linear
                || (a.join == b.join
                    && equal(f64::from(a.miter_limit), f64::from(b.miter_limit))
                    && a.clip_miter_at_limit == b.clip_miter_at_limit
                    && a.use_wpf_join_semantics == b.use_wpf_join_semantics))
            && intervals.len() == other.len()
            && intervals
                .iter()
                .zip(other)
                .all(|(a, b)| if linear { equal(*a, *b) } else { a == b })
    }
}
fn equal(a: f64, b: f64) -> bool {
    a == b || (a.is_nan() && b.is_nan())
}
fn derived_matches(source: &Pen, pen: &Pen, old_width: f32, width: f32) -> bool {
    let a = source.options;
    let b = pen.options;
    Arc::ptr_eq(&source.brush, &pen.brush)
        && old_width == width
        && a.join == b.join
        && a.miter_limit == b.miter_limit
        && a.clip_miter_at_limit == b.clip_miter_at_limit
        && a.use_wpf_join_semantics == b.use_wpf_join_semantics
        && a.start_cap == b.start_cap
        && a.end_cap == b.end_cap
        && a.dash_cap == b.dash_cap
        && a.transform == b.transform
}
impl StrokeDraw {
    pub(crate) fn prepare(&self) -> Result<Option<PreparedStroke>, Error> {
        self.validate()?;
        let Some(state) = self.state()? else {
            return Ok(None);
        };
        if !self.pen.has_dash_pattern() {
            return Ok(None);
        }
        let mut cache = self.preparation.lock().map_err(|_| Error::InvalidInput)?;
        if !cache
            .source
            .as_ref()
            .is_some_and(|p| Arc::ptr_eq(p, &self.path))
        {
            *cache = Cache {
                source: Some(self.path.clone()),
                ..Default::default()
            };
        }
        let candidate =
            !self.pen.is_hairline() && self.pen.options.transform == PenStrokeTransform::Normal;
        let linear = candidate
            && *cache.linear.get_or_insert_with(|| {
                self.path.deferred.is_none()
                    && self.path.figures.iter().all(|f| {
                        f.segments
                            .iter()
                            .all(|s| matches!(s.kind, PathSegmentKind::Line(_)))
                    })
            });
        let (path, pen) = if linear {
            if !cache
                .linear_entry
                .as_ref()
                .is_some_and(|entry| entry.key.matches(&self.pen, state.local, true))
            {
                let mut local_pen = (*self.pen).clone();
                local_pen.options.thickness = state.local;
                // Publish the complete result once. Allocation failures leave the
                // previous generation intact; source rejection is cached explicitly.
                let result = self
                    .path
                    .prepare_linear_stroke(&local_pen)?
                    .map(|coverage| -> Result<LinearResult, Error> {
                        if let Some(fill) = coverage.fill_coverage {
                            Ok(LinearResult::Fill(fill.compile_raster_path()?))
                        } else {
                            Ok(LinearResult::Stroke(
                                Arc::new(coverage.stroke_path),
                                Arc::new(coverage.pen),
                            ))
                        }
                    })
                    .transpose()?;
                cache.linear_entry = Some(LinearEntry {
                    key: Key::new(&self.pen, state.local),
                    result,
                });
            }
            let result = cache
                .linear_entry
                .as_mut()
                .unwrap()
                .result
                .as_mut()
                .ok_or(Error::UnsupportedExecution)?;
            match result {
                LinearResult::Fill(path) => {
                    return Ok(Some(
                        path.clone()
                            .map_or(PreparedStroke::Empty, PreparedStroke::Fill),
                    ));
                }
                LinearResult::Stroke(path, pen) => {
                    if !Arc::ptr_eq(&pen.brush, &self.pen.brush) {
                        *pen = Arc::new(pen.with_brush(self.pen.brush.clone()));
                    }
                    (path.clone(), pen.clone())
                }
            }
        } else {
            if !cache
                .generic
                .as_ref()
                .is_some_and(|(key, _)| key.matches(&self.pen, state.local, false))
            {
                let dashed = self
                    .path
                    .dashed_stroke(&self.pen, state.local, false)?
                    .ok_or(Error::UnsupportedExecution)?;
                cache.generic = Some((Key::new(&self.pen, state.local), Arc::new(dashed.path)));
            }
            if !cache.derived.as_ref().is_some_and(|(source, width, _)| {
                derived_matches(source, &self.pen, *width, state.local)
            }) {
                let pen = Arc::new(self.pen.undashed(state.local)?);
                cache.derived = Some((self.pen.clone(), state.local, pen));
            }
            (
                cache.generic.as_ref().unwrap().1.clone(),
                cache.derived.as_ref().unwrap().2.clone(),
            )
        };
        let mut draw = self.clone();
        draw.path = path;
        draw.pen = pen;
        draw.frame.thickness_is_local = true;
        draw.frame.recorded_transform = None;
        Ok(Some(PreparedStroke::Stroke(draw)))
    }
}
