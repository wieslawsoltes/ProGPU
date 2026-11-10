//! Original immutable text-blob runs, nominal placement and contour factories.
use super::canvas_font::{CanvasFont, CanvasFontError, TextAlign, TextEncoding};
use crate::{
    Error,
    image::RotationScaleMatrix,
    vector::{MeasuredPath, PathAddMode, PathBuilder, PathMeasure, RetainedPath},
};
use std::sync::{
    Arc, Mutex, OnceLock,
    atomic::{AtomicU32, Ordering},
};
mod builder;
mod intercepts;
pub use builder::{MAX_REUSABLE_POSITIONED_GLYPHS, TextBlobBuilder, TextBlobRunBuffer};
type Result<T> = std::result::Result<T, CanvasFontError>;
static NEXT_ID: AtomicU32 = AtomicU32::new(0);

/// One immutable actual selected font/glyph/position generation. No shaping or
/// fabricated paragraph metadata is introduced by this already-positioned API.
#[derive(Clone, Debug)]
pub struct TextBlobRun {
    font: CanvasFont,
    glyphs: Arc<[u16]>,
    positions: Arc<[[f32; 2]]>,
    transforms: Option<Arc<[RotationScaleMatrix]>>,
    paths: Arc<Mutex<std::collections::HashMap<u16, Option<RetainedPath>>>>,
}
impl TextBlobRun {
    /// Adopt complete immutable arrays, retaining exact used glyph slots.
    pub fn new(
        font: CanvasFont,
        glyphs: Arc<[u16]>,
        positions: Arc<[[f32; 2]]>,
        transforms: Option<Arc<[RotationScaleMatrix]>>,
    ) -> Result<Self> {
        if glyphs.len() != positions.len()
            || transforms.as_ref().is_some_and(|t| t.len() != glyphs.len())
        {
            return Err(Error::InvalidInput.into());
        }
        Ok(Self {
            font,
            glyphs,
            positions,
            transforms,
            paths: Arc::new(Mutex::new(std::collections::HashMap::new())),
        })
    }
    /// Frozen font settings and actual selected source owner.
    pub fn font(&self) -> &CanvasFont {
        &self.font
    }
    /// Complete original glyph slots.
    pub fn glyphs(&self) -> &[u16] {
        &self.glyphs
    }
    /// Complete original source positions, with no replay conversion/allocation.
    pub fn positions(&self) -> &[[f32; 2]] {
        &self.positions
    }
    /// Optional independent per-glyph rotation/scale frames.
    pub fn transforms(&self) -> Option<&[RotationScaleMatrix]> {
        self.transforms.as_deref()
    }
    /// Actual selected glyph path with immutable font size/skew/bold policy.
    /// Cache publication happens after source work and never holds the cache
    /// lock while decoding an outline. Missing/no-ink results retain their slots.
    pub fn glyph_path(&self, index: usize) -> Result<Option<RetainedPath>> {
        let glyph = *self.glyphs.get(index).ok_or(Error::InvalidInput)?;
        if let Some(path) = self
            .paths
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .get(&glyph)
        {
            return Ok(path.clone());
        }
        let candidate = self.font.glyph_path(glyph)?;
        let mut paths = self.paths.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(path) = paths.get(&glyph) {
            return Ok(path.clone());
        }
        paths.try_reserve(1).map_err(|_| Error::Capacity)?;
        Ok(paths.entry(glyph).or_insert(candidate).clone())
    }
    /// Retain original arrays in the renderer's metadata-free positioned record.
    pub fn positioned_run(&self) -> Option<super::paint::PositionedGlyphRun> {
        if self.transforms.is_some() {
            return None;
        }
        self.font.typeface.clone().map(|font| {
            super::paint::PositionedGlyphRun::new(font, self.glyphs.clone(), self.positions.clone())
        })
    }
}
#[derive(Debug)]
struct Data {
    runs: Arc<[TextBlobRun]>,
    glyphs: Arc<[u16]>,
    positions: Arc<[[f32; 2]]>,
    bounds: OnceLock<[f32; 4]>,
    id: u32,
    bold: bool,
    // Last field: actual run/aggregate arrays retire before a scratch lease is
    // returned. Independent renderer/run arrays still forbid mutable reuse.
    _lease: Option<builder::BlobLease>,
}
/// Immutable multi-run source generation. Cloning retains exact font/array owners
/// in O(1); bounds are computed lazily from actual outlines and cached once.
#[derive(Clone, Debug)]
pub struct TextBlob(Arc<Data>);
impl TextBlob {
    /// Adopt one or more complete original runs; empty run arrays reject.
    pub fn from_runs(runs: Vec<TextBlobRun>) -> Result<Self> {
        if runs.is_empty() {
            return Err(Error::InvalidInput.into());
        }
        let (glyphs, positions) = if runs.len() == 1 {
            (runs[0].glyphs.clone(), runs[0].positions.clone())
        } else {
            let count = runs.iter().try_fold(0_usize, |n, r| {
                n.checked_add(r.glyphs.len()).ok_or(Error::Capacity)
            })?;
            let mut glyphs = Vec::new();
            let mut positions = Vec::new();
            glyphs
                .try_reserve_exact(count)
                .map_err(|_| Error::Capacity)?;
            positions
                .try_reserve_exact(count)
                .map_err(|_| Error::Capacity)?;
            for run in &runs {
                glyphs.extend_from_slice(&run.glyphs);
                positions.extend_from_slice(&run.positions);
            }
            (Arc::from(glyphs), Arc::from(positions))
        };
        let bold = runs.iter().any(|r| r.font.embolden);
        Ok(Self(Arc::new(Data {
            runs: Arc::from(runs),
            glyphs,
            positions,
            bounds: OnceLock::new(),
            id: NEXT_ID.fetch_add(1, Ordering::Relaxed).wrapping_add(1),
            bold,
            _lease: None,
        })))
    }
    /// Original monotonically wrapping identity, shared by cloned references.
    pub fn unique_id(&self) -> u32 {
        self.0.id
    }
    /// Actual original run inventory in source order.
    pub fn runs(&self) -> &[TextBlobRun] {
        &self.0.runs
    }
    /// Original concatenated complete glyph-slot inventory.
    pub fn glyphs(&self) -> &[u16] {
        &self.0.glyphs
    }
    /// Original concatenated complete source position inventory.
    pub fn positions(&self) -> &[[f32; 2]] {
        &self.0.positions
    }
    /// Original captured synthetic-bold summary.
    pub fn has_emboldened_runs(&self) -> bool {
        self.0.bold
    }
    /// Nominal complete encoded text, retaining the strict empty/malformed policy.
    pub fn from_text(
        font: &CanvasFont,
        bytes: &[u8],
        encoding: TextEncoding,
        origin: [f32; 2],
    ) -> Result<Option<Self>> {
        let glyphs = font.encoded_glyphs(bytes, encoding)?;
        Self::from_glyphs(font, &glyphs, origin)
    }
    /// Original independent nominal glyph advances and actual selected font.
    pub fn from_glyphs(
        font: &CanvasFont,
        glyphs: &[u16],
        origin: [f32; 2],
    ) -> Result<Option<Self>> {
        if glyphs.is_empty() {
            return Ok(None);
        }
        let positions = font.glyph_positions(glyphs, origin)?;
        Self::positioned(font, glyphs, &positions)
    }
    /// Original horizontal factory permits a short inventory and zero-fills its
    /// unprovided X slots; an overlong inventory rejects before publication.
    pub fn horizontal(
        font: &CanvasFont,
        glyphs: &[u16],
        x: &[f32],
        y: f32,
    ) -> Result<Option<Self>> {
        if glyphs.is_empty() {
            return Ok(None);
        }
        if x.len() > glyphs.len() {
            return Err(Error::InvalidInput.into());
        }
        let mut positions = allocate(glyphs.len(), [0.0, y])?;
        for (position, &x) in positions.iter_mut().zip(x) {
            position[0] = x;
        }
        Self::positioned(font, glyphs, &positions)
    }
    /// Original positioned factory with zero-filled missing positions.
    pub fn positioned(
        font: &CanvasFont,
        glyphs: &[u16],
        positions: &[[f32; 2]],
    ) -> Result<Option<Self>> {
        if glyphs.is_empty() {
            return Ok(None);
        }
        if positions.len() > glyphs.len() {
            return Err(Error::InvalidInput.into());
        }
        let mut owned = allocate(glyphs.len(), [0.0; 2])?;
        owned[..positions.len()].copy_from_slice(positions);
        let run = TextBlobRun::new(font.clone(), copy(glyphs)?, Arc::from(owned), None)?;
        Ok(Some(Self::from_runs(vec![run])?))
    }
    /// Original per-glyph matrices, preserving zero default frames and explicit
    /// translation positions rather than assuming missing matrices are identity.
    pub fn rotation_scale(
        font: &CanvasFont,
        glyphs: &[u16],
        matrices: &[RotationScaleMatrix],
    ) -> Result<Option<Self>> {
        if glyphs.is_empty() {
            return Ok(None);
        }
        if matrices.len() > glyphs.len() {
            return Err(Error::InvalidInput.into());
        }
        let mut owned = allocate(glyphs.len(), RotationScaleMatrix::EMPTY)?;
        owned[..matrices.len()].copy_from_slice(matrices);
        let mut positions = allocate(glyphs.len(), [0.0; 2])?;
        for (position, matrix) in positions.iter_mut().zip(&owned) {
            *position = matrix.translation;
        }
        let run = TextBlobRun::new(
            font.clone(),
            copy(glyphs)?,
            Arc::from(positions),
            Some(Arc::from(owned)),
        )?;
        Ok(Some(Self::from_runs(vec![run])?))
    }
    /// Original midpoint/tangent placement on the first contour, including the
    /// source's contiguous selected-glyph slicing after skipped midpoint visits.
    pub fn path_positioned(
        font: &CanvasFont,
        glyphs: &[u16],
        path: &RetainedPath,
        align: TextAlign,
        origin: [f32; 2],
    ) -> Result<Option<Self>> {
        if glyphs.is_empty() {
            return Ok(None);
        }
        let widths = font.glyph_widths(glyphs)?;
        let offsets = font.glyph_positions(glyphs, origin)?;
        let geometry = path.geometry()?;
        let measured = MeasuredPath::from_geometry(&geometry)?;
        let measure = PathMeasure::new(Some(&measured), false, 1.0)?;
        let length = measure.length();
        let last = glyphs.len() - 1;
        let text_width = offsets[last][0] + widths[last];
        let aligned = offsets[0][0] + (length - text_width) * (align as u8 as f32 * 0.5);
        let mut matrices = Vec::new();
        matrices
            .try_reserve_exact(glyphs.len())
            .map_err(|_| Error::Capacity)?;
        let mut start = 0;
        for (index, offset) in offsets.iter().enumerate() {
            let half = widths[index] * 0.5;
            let midpoint = aligned + offset[0] + half;
            if midpoint < 0.0 || midpoint >= length {
                continue;
            }
            let Some((point, tangent)) = measure.position_and_tangent(midpoint) else {
                continue;
            };
            if matrices.is_empty() {
                start = index;
            }
            let x = point[0] - tangent[0] * half - offset[1] * tangent[1];
            let y = point[1] - tangent[1] * half + offset[1] * tangent[0];
            matrices.push(RotationScaleMatrix::new(tangent[0], tangent[1], [x, y]));
        }
        if matrices.is_empty() {
            return Ok(None);
        }
        Self::rotation_scale(font, &glyphs[start..start + matrices.len()], &matrices)
    }
    /// Original complete actual outline bounds, including transformed and bold
    /// runs. Missing/no-ink slots remain retained even when they add no bounds.
    pub fn bounds(&self) -> Result<[f32; 4]> {
        if let Some(bounds) = self.0.bounds.get() {
            return Ok(*bounds);
        }
        let mut bounds = None;
        for run in self.runs() {
            for index in 0..run.glyphs.len() {
                let Some(path) = run.glyph_path(index)? else {
                    continue;
                };
                if path.is_empty()? {
                    continue;
                }
                let b = if let Some(matrices) = &run.transforms {
                    let mut transformed = path.to_builder()?;
                    transformed.transform(matrices[index].matrix())?;
                    transformed.bounds()
                } else {
                    let [l, t, r, b] = path.bounds();
                    let [x, y] = run.positions[index];
                    [l + x, t + y, r + x, b + y]
                };
                union(&mut bounds, b);
            }
        }
        let result = bounds.unwrap_or([0.0; 4]);
        let _ = self.0.bounds.set(result);
        Ok(result)
    }
    /// Original combined source path for shader/stroke/bold fallback; actual
    /// glyph placements remain separate from font Size/ScaleX/SkewX transforms.
    pub fn path(&self, origin: [f32; 2]) -> Result<RetainedPath> {
        let mut output = PathBuilder::new();
        for run in self.runs() {
            for index in 0..run.glyphs.len() {
                let Some(path) = run.glyph_path(index)? else {
                    continue;
                };
                if let Some(matrices) = &run.transforms {
                    let mut matrix = matrices[index].matrix();
                    matrix.0[2] += origin[0];
                    matrix.0[5] += origin[1];
                    output.add_transformed_path(&path, matrix, PathAddMode::Append)?;
                } else {
                    let position = run.positions[index];
                    output.add_path(
                        &path,
                        [origin[0] + position[0], origin[1] + position[1]],
                        PathAddMode::Append,
                    )?;
                }
            }
        }
        Ok(output.detach())
    }
}
fn allocate<T: Clone>(count: usize, value: T) -> Result<Vec<T>> {
    let mut out = Vec::new();
    out.try_reserve_exact(count).map_err(|_| Error::Capacity)?;
    out.resize(count, value);
    Ok(out)
}
fn copy<T: Clone>(source: &[T]) -> Result<Arc<[T]>> {
    let mut out = Vec::new();
    out.try_reserve_exact(source.len())
        .map_err(|_| Error::Capacity)?;
    out.extend_from_slice(source);
    Ok(Arc::from(out))
}
fn union(out: &mut Option<[f32; 4]>, b: [f32; 4]) {
    *out = Some(out.map_or(b, |a| {
        [
            minimum(a[0], b[0]),
            minimum(a[1], b[1]),
            maximum(a[2], b[2]),
            maximum(a[3], b[3]),
        ]
    }));
}
fn minimum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() | b.to_bits())
    } else {
        a.min(b)
    }
}
fn maximum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() & b.to_bits())
    } else {
        a.max(b)
    }
}
