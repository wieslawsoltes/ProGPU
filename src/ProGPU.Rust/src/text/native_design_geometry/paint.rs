//! Original explicit vector-glyph phase cache, synthetic passes, point mapping
//! and text raster policy, fed by actual native design outlines and placements.
use super::*;
use crate::{
    path::FillRule,
    text::paint::{GlyphCoverageTransfer, TextPaintContext, TextPathPolicy, TextRenderingMode},
    vector::{Brush, Matrix4},
};
#[derive(Clone, Debug)]
pub struct NativeDesignPaint {
    pub font_size: f32,
    pub brush: Option<Arc<Brush>>,
    pub bold: bool,
    pub italic: bool,
    pub font_transform: Option<[f32; 2]>,
    pub rendering_mode: TextRenderingMode,
    pub coverage_transfer: GlyphCoverageTransfer,
    pub logical_atlas_resolution: bool,
}
impl NativeDesignPaint {
    pub fn new(font_size: f32, brush: Option<Arc<Brush>>) -> Self {
        Self {
            font_size,
            brush,
            bold: false,
            italic: false,
            font_transform: None,
            rendering_mode: TextRenderingMode::Grayscale,
            coverage_transfer: GlyphCoverageTransfer::default(),
            logical_atlas_resolution: false,
        }
    }
}
/// Immutable source recording. Selection includes every original selected slot,
/// including no ink; repeated indices remain repeated source occurrences.
#[derive(Clone, Debug)]
pub struct NativeDesignVectorDraw {
    geometry: Arc<NativeDesignGeometry>,
    selected: Arc<[u32]>,
    paints: Arc<[NativeDesignPaint]>,
    position: [f32; 2],
    rotation: f32,
    context: TextPaintContext,
}
impl NativeDesignVectorDraw {
    pub fn selected(
        geometry: Arc<NativeDesignGeometry>,
        selected: &[u32],
        paints: &[NativeDesignPaint],
        position: [f32; 2],
        rotation: f32,
        context: TextPaintContext,
    ) -> Result<Self> {
        let mut indices = reserved(selected.len())?;
        indices.extend_from_slice(selected);
        let mut owned_paints = reserved(paints.len())?;
        owned_paints.extend_from_slice(paints);
        let value = Self {
            geometry,
            selected: indices.into(),
            paints: owned_paints.into(),
            position,
            rotation,
            context,
        };
        value.validate()?;
        Ok(value)
    }
    pub fn all(
        geometry: Arc<NativeDesignGeometry>,
        paints: &[NativeDesignPaint],
        position: [f32; 2],
        rotation: f32,
        context: TextPaintContext,
    ) -> Result<Self> {
        let mut indices = reserved(geometry.slots.len())?;
        indices.extend(0..geometry.slots.len() as u32);
        Self::selected(geometry, &indices, paints, position, rotation, context)
    }
    pub fn geometry(&self) -> &Arc<NativeDesignGeometry> {
        &self.geometry
    }
    pub fn selected_indices(&self) -> &[u32] {
        &self.selected
    }
    pub fn paints(&self) -> &[NativeDesignPaint] {
        &self.paints
    }
    pub fn context(&self) -> TextPaintContext {
        self.context
    }
    pub fn position(&self) -> [f32; 2] {
        self.position
    }
    pub fn rotation(&self) -> f32 {
        self.rotation
    }
    pub fn with_context(&self, context: TextPaintContext) -> Result<Self> {
        let value = Self {
            context,
            ..self.clone()
        };
        value.validate()?;
        Ok(value)
    }
    pub fn validate(&self) -> Result<()> {
        let c = self.context;
        if !self.position.into_iter().all(f32::is_finite)
            || !self.rotation.is_finite()
            || !c.semantic_opacity.is_finite()
            || self.paints.len() != self.geometry.styles.len()
            || self
                .selected
                .iter()
                .any(|&i| i as usize >= self.geometry.slots.len())
        {
            return Err(Error::InvalidInput.into());
        }
        let m = c.transform.0;
        if m.iter().flatten().any(|v| !v.is_finite())
            || m[0][2] != 0.0
            || m[0][3] != 0.0
            || m[1][2] != 0.0
            || m[1][3] != 0.0
            || m[2] != [0.0, 0.0, 1.0, 0.0]
            || m[3][2] != 0.0
            || m[3][3] != 1.0
            || c.use_mvp
            || c.gpu_transforms_active
            || c.camera_view != Matrix4::IDENTITY
            || c.static_zoom != 1.0
            || c.clip.is_some()
        {
            return Err(Error::UnsupportedExecution.into());
        }
        for (p, s) in self.paints.iter().zip(&self.geometry.styles) {
            if !p.font_size.is_finite()
                || p.font_size <= 0.0
                || (p.font_size / f32::from(s.units_per_em)).to_bits() != s.scale.to_bits()
                || p.coverage_transfer.encoding() != 0
                    && p.rendering_mode != TextRenderingMode::Grayscale
            {
                return Err(Error::InvalidInput.into());
            }
        }
        Ok(())
    }
}
#[derive(Clone, Debug)]
pub struct NativeDesignVectorPath {
    pub path: Path,
    pub transform: Matrix4,
    pub brush: Arc<Brush>,
    pub semantic_opacity: f32,
    pub policy: TextPathPolicy,
    pub source_slot: u32,
    pub pass: u8,
}
#[derive(Debug)]
pub struct NativeDesignVectorPlan {
    source: NativeDesignVectorDraw,
    paths: Vec<NativeDesignVectorPath>,
}
impl NativeDesignVectorPlan {
    pub fn source(&self) -> &NativeDesignVectorDraw {
        &self.source
    }
    pub fn paths(&self) -> &[NativeDesignVectorPath] {
        &self.paths
    }
}
#[derive(Clone, Copy, Debug, Hash, PartialEq, Eq)]
struct Key {
    outline: usize,
    values: [u32; 5],
}
#[derive(Default)]
pub struct NativeDesignVectorPainter {
    cache: HashMap<Key, (Arc<NativeDesignOutline>, Path)>,
}
fn key(v: f32) -> u32 {
    if v == 0.0 { 0 } else { v.to_bits() }
}
fn translation(p: [f32; 2]) -> Matrix4 {
    let mut result = Matrix4::IDENTITY;
    result.0[3][0] = p[0];
    result.0[3][1] = p[1];
    result
}
fn max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
fn min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
impl NativeDesignVectorPainter {
    pub fn cached_path_count(&self) -> usize {
        self.cache.len()
    }
    pub fn clear(&mut self) {
        self.cache.clear();
    }
    /// Prepare at the complete actual target boundary. Whole source/paint
    /// admission precedes cache mutation; failed plans publish no Scene commands.
    pub fn prepare(
        &mut self,
        draw: &NativeDesignVectorDraw,
    ) -> Result<Arc<NativeDesignVectorPlan>> {
        draw.validate()?;
        let c = draw.context;
        let mut transform = c.transform;
        if draw.rotation.abs() > 0.0001 {
            let cosine = draw.rotation.cos();
            let sine = draw.rotation.sin();
            let rotation = Matrix4([
                [cosine, sine, 0.0, 0.0],
                [-sine, cosine, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ]);
            transform = (translation([-draw.position[0], -draw.position[1]])
                * rotation
                * translation(draw.position))
                * transform;
        }
        let mut paths = reserved(draw.selected.len())?;
        for &index in draw.selected.iter() {
            let slot = draw.geometry.slots[index as usize];
            let (Some(style), Some(outline)) = (slot.style_index, slot.outline_index) else {
                continue;
            };
            let outline = &draw.geometry.outlines[outline as usize];
            let paint = &draw.paints[style as usize];
            let Some(brush) = &paint.brush else {
                continue;
            };
            if !outline.has_ink() {
                continue;
            }
            let font = paint.font_transform.unwrap_or([1.0, 0.0]);
            let scale_x = if font[0].is_finite() { font[0] } else { 1.0 };
            let skew = if font[1].is_finite() { font[1] } else { 0.0 };
            let italic = (if paint.italic { 0.22 } else { 0.0 }) - skew;
            let policy = crate::text::paint::vector_glyph_raster_policy(
                crate::text::paint::VectorGlyphRasterInputs {
                    font_size: paint.font_size,
                    scale_x,
                    skew_x: skew,
                    rendering_mode: paint.rendering_mode,
                    coverage_transfer: paint.coverage_transfer,
                    logical_atlas_resolution: paint.logical_atlas_resolution,
                },
                transform,
                c,
            )?;
            let origin = [
                slot.position[0] + draw.position[0],
                slot.position[1] + draw.position[1],
            ];
            let em = draw.geometry.styles[style as usize].scale;
            for pass in 0..if paint.bold { 2 } else { 1 } {
                let position = [
                    origin[0] + pass as f32 * (paint.font_size * 0.035),
                    origin[1] + 0.0,
                ];
                if !position.into_iter().all(f32::is_finite) {
                    return Err(Error::InvalidInput.into());
                }
                let phase = position.map(|v| {
                    let phase = ((v - v.floor()) * 128.0).round_ties_even() / 128.0;
                    if phase >= 1.0 { 0.0 } else { phase }
                });
                let key = Key {
                    outline: Arc::as_ptr(outline) as usize,
                    values: [em, phase[0], phase[1], italic, scale_x].map(key),
                };
                let mut path = if let Some((_, p)) = self.cache.get(&key) {
                    p.clone()
                } else {
                    let p = mapped(outline, em, phase, italic, scale_x)?;
                    if self.cache.len() >= 4096 {
                        self.cache.clear();
                    }
                    self.cache.try_reserve(1).map_err(|_| FontError::Capacity)?;
                    self.cache.insert(key, (outline.clone(), p.clone()));
                    p
                };
                path.retain_source_owner(draw.geometry.clone());
                paths.try_reserve(1).map_err(|_| FontError::Capacity)?;
                paths.push(NativeDesignVectorPath {
                    path,
                    transform: translation([position[0] - phase[0], position[1] - phase[1]])
                        * transform,
                    brush: brush.clone(),
                    semantic_opacity: c.semantic_opacity,
                    policy,
                    source_slot: index,
                    pass,
                });
            }
        }
        Ok(Arc::new(NativeDesignVectorPlan {
            source: draw.clone(),
            paths,
        }))
    }
}
fn mapped(
    outline: &NativeDesignOutline,
    em: f32,
    position: [f32; 2],
    italic: f32,
    scale_x: f32,
) -> Result<Path> {
    let mut segments = reserved(outline.segments().len())?;
    let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
    for &source in outline.segments() {
        let mut segment = source;
        for p in &mut segment.points[..source.kind as usize + 2] {
            let [x, y] = *p;
            *p = [
                position[0] + (x * scale_x + y * italic) * em + 0.0,
                position[1] + y * em * (-1.0),
            ];
            if !p.iter().copied().all(f32::is_finite) {
                return Err(Error::InvalidInput.into());
            }
            bounds[0] = min(bounds[0], p[0]);
            bounds[1] = min(bounds[1], p[1]);
            bounds[2] = max(bounds[2], p[0]);
            bounds[3] = max(bounds[3], p[1]);
        }
        segments.push(segment);
    }
    Ok(Path::new(segments, bounds, FillRule::NonZero)?)
}
