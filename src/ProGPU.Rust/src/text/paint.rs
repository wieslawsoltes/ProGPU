//! Original text compositor preparation. Shaping/layout generations, physical
//! atlas requests and ordered vector fallback remain separate owned contracts.
use super::layout::ManagedTextLayout;
use crate::{
    Error,
    font::{FontColorLayer, FontError, ManagedFont, ManagedGlyphOutline},
    vector::*,
};
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;
mod outlines;
mod placement;
mod vector_policy;
pub(crate) use vector_policy::{VectorGlyphRasterInputs, vector_glyph_raster_policy};
#[cfg(feature = "gpu")]
mod resolve;
#[cfg(feature = "gpu")]
pub use resolve::{ResolvedTextCommand, ResolvedTextPaint};

/// Original text rendering policy; ClearType is the existing shader model,
/// independent of native source raster-policy or modern ClearType qualification.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum TextRenderingMode {
    #[default]
    /// Original scalar antialiased coverage.
    Grayscale = 0,
    /// Original binary presentation.
    Aliased = 1,
    /// Original shader RGB coverage model.
    ClearType = 2,
}
/// Original placement policy, independent of execution of font instructions.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum TextHintingMode {
    #[default]
    /// Original size-dependent automatic placement.
    Auto,
    /// Original fixed policy, sharing ordinary placement arithmetic.
    Fixed,
    /// Preserve continuous source positions and zero phase.
    Animated,
    /// Explicit whole physical origins with half ties toward positive infinity.
    IntegerPixel,
}
/// Immutable source grayscale transfer; legacy behavior is the default.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GlyphCoverageTransfer(u8);
impl GlyphCoverageTransfer {
    /// Retain exact source contrast 0..12; only grayscale rendering admits it.
    pub fn grayscale_contrast(contrast: u8) -> Result<Self, Error> {
        if contrast > 12 {
            return Err(Error::InvalidInput);
        }
        Ok(Self(contrast + 1))
    }
    /// Original reserved-word encoding: zero legacy, one through thirteen explicit.
    pub fn encoding(self) -> u32 {
        u32::from(self.0)
    }
    fn validate(self, mode: TextRenderingMode) -> Result<(), Error> {
        if self.0 != 0 && mode != TextRenderingMode::Grayscale {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    fn vector(self, gamma: f32) -> f32 {
        if self.0 == 0 {
            gamma
        } else {
            -f32::from(self.0)
        }
    }
}
/// Exact original 96-byte instanced Text.wgsl record, with physical atlas texels.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct GlyphInstance {
    /// Original resolved logical baseline after physical placement policy.
    pub snapped_logical_position: [f32; 2],
    /// Original active transform X basis.
    pub basis_x: [f32; 2],
    /// Original active transform Y basis.
    pub basis_y: [f32; 2],
    /// Physical bearing X/Y and selected render width/height.
    pub bearing_size: [f32; 4],
    /// Physical atlas near/far texels; never normalized UVs.
    pub texture_coordinates: [f32; 4],
    /// Original straight RGBA color, with opacity applied at its owning stage.
    pub color: [f32; 4],
    /// Original atlas scale, bold offset, italic skew and encoded presentation flags.
    pub scale_bold_italic_flags: [f32; 4],
    /// Shared text-style index, or negative for legacy per-instance presentation.
    pub brush_index: f32,
    /// Original reserved zero words.
    pub padding: f32,
}
/// Original ordered shared text-style record; styles are not brush materials.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct GpuTextStyle {
    /// Original straight RGBA color, with opacity applied at its owning stage.
    pub color: [f32; 4],
    /// Original independent grayscale, aliased or ClearType presentation.
    pub rendering_mode: u32,
    /// Original reserved zero words.
    pub padding: [u32; 3],
}
const _: () = {
    assert!(size_of::<GlyphInstance>() == 96);
    assert!(std::mem::offset_of!(GlyphInstance, bearing_size) == 24);
    assert!(std::mem::offset_of!(GlyphInstance, texture_coordinates) == 40);
    assert!(std::mem::offset_of!(GlyphInstance, color) == 56);
    assert!(std::mem::offset_of!(GlyphInstance, scale_bold_italic_flags) == 72);
    assert!(std::mem::offset_of!(GlyphInstance, brush_index) == 88);
    assert!(size_of::<GpuTextStyle>() == 32);
};
/// Immutable positioned transport. Zero/nonpositive count retains the original
/// whole-array/minimum-length selection; this is not fabricated shaping metadata.
#[derive(Clone, Debug)]
pub struct PositionedGlyphRun {
    /// Actual immutable selected font owner.
    pub font: ManagedFont,
    /// Complete original glyph-slot inventory.
    pub indices: Arc<[u16]>,
    /// Complete original source baseline inventory; recording never changes it.
    pub positions: Arc<[[f32; 2]]>,
    /// Original signed selected-range start, used only for a positive count.
    pub range_start: i32,
    /// Original selected count; nonpositive selects the minimum complete-array length.
    pub range_count: i32,
}
impl PositionedGlyphRun {
    /// Retain the whole original arrays without cloning their payloads.
    pub fn new(font: ManagedFont, indices: Arc<[u16]>, positions: Arc<[[f32; 2]]>) -> Self {
        Self {
            font,
            indices,
            positions,
            range_start: 0,
            range_count: 0,
        }
    }
    fn range(&self) -> Option<std::ops::Range<usize>> {
        let (start, count) = if self.range_count > 0 {
            (self.range_start, self.range_count as usize)
        } else {
            (0, self.indices.len().min(self.positions.len()))
        };
        let start = usize::try_from(start).ok()?;
        let end = start.checked_add(count)?;
        (count != 0 && end <= self.indices.len() && end <= self.positions.len())
            .then_some(start..end)
    }
}
/// Actual source text generation. Paragraphs retain their selected fallback fonts.
#[derive(Clone, Debug)]
pub enum TextSource {
    /// Complete already-shaped paragraph generation.
    Layout(Arc<ManagedTextLayout>),
    /// Original explicit glyph slots and positions, without fabricated shaping.
    Positioned(PositionedGlyphRun),
}
/// Original text paint inputs. Recording does not reshape or move source arrays.
#[derive(Clone, Debug)]
pub struct TextDraw {
    /// Actual immutable layout or metadata-free positioned generation.
    pub source: TextSource,
    /// Original logical font em size.
    pub font_size: f32,
    /// Original command-local origin.
    pub position: [f32; 2],
    /// Original command rotation in radians about its position.
    pub rotation: f32,
    /// Original optional material; None preserves the source command policy.
    pub brush: Option<Arc<Brush>>,
    /// Original two-pass synthetic bold selection.
    pub bold: bool,
    /// Original synthetic italic selection.
    pub italic: bool,
    /// Original optional horizontal scale and skew; each nonfinite component defaults independently.
    pub font_transform: Option<[f32; 2]>,
    /// Original independent grayscale, aliased or ClearType presentation.
    pub rendering_mode: TextRenderingMode,
    /// Original physical-origin policy; it does not execute font instructions.
    pub hinting_mode: TextHintingMode,
    /// Original explicit grayscale contrast or legacy default.
    pub coverage_transfer: GlyphCoverageTransfer,
    /// Original atlas preference and capacity-reuse intent.
    pub prefer_atlas: bool,
    /// Explicit original vector-glyph rendering selection.
    pub vector_rendering: bool,
    /// Original explicit logical-resolution atlas policy.
    pub logical_atlas_resolution: bool,
}
impl TextDraw {
    /// Record positioned glyphs with original default presentation.
    pub fn positioned(run: PositionedGlyphRun, font_size: f32) -> Self {
        Self::new(TextSource::Positioned(run), font_size)
    }
    /// Retain the actual completed paragraph and its original font size.
    pub fn layout(layout: Arc<ManagedTextLayout>) -> Self {
        let font_size = layout.font_size();
        Self::new(TextSource::Layout(layout), font_size)
    }
    fn new(source: TextSource, font_size: f32) -> Self {
        Self {
            source,
            font_size,
            position: [0.0; 2],
            rotation: 0.0,
            brush: None,
            bold: false,
            italic: false,
            font_transform: None,
            rendering_mode: TextRenderingMode::Grayscale,
            hinting_mode: TextHintingMode::Auto,
            coverage_transfer: GlyphCoverageTransfer::default(),
            prefer_atlas: false,
            vector_rendering: false,
            logical_atlas_resolution: false,
        }
    }
    /// O(1) descriptor admission. Integer origins validate every selected
    /// occurrence later, before any style, path-cache or atlas mutation.
    pub fn validate(&self, frame: Matrix4, dpi: f32) -> Result<(), Error> {
        self.coverage_transfer.validate(self.rendering_mode)?;
        if self.hinting_mode == TextHintingMode::IntegerPixel
            && !placement::integer_descriptor(self, frame, dpi)
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
}
/// Actual target and retained compilation context. The host must bind the same
/// MVP/camera/physical target represented here when consuming the resulting plan.
#[derive(Clone, Copy, Debug)]
pub struct TextPaintContext {
    /// Complete original target transform; no primitive history is inferred.
    pub transform: Matrix4,
    /// Actual target DPI; ordinary rasterization normalizes invalid values locally.
    pub dpi_scale: f32,
    /// Original retained compilation zoom, separate from target DPI.
    pub static_zoom: f32,
    /// Original active semantic opacity, independent of brush opacity.
    pub semantic_opacity: f32,
    /// Original retained compilation context: encode late MVP and use legacy
    /// per-instance presentation instead of shared solid text styles.
    pub use_mvp: bool,
    /// Original cached-source ClearType-to-grayscale selection.
    pub suppress_cached_cleartype: bool,
    /// Original late camera path; bypasses CPU clip culling.
    pub gpu_transforms_active: bool,
    /// Actual original camera view used for coverage-scale policy.
    pub camera_view: Matrix4,
    /// Original x/y/width/height active clip; absence does not imply target bounds.
    pub clip: Option<[f32; 4]>,
}
impl Default for TextPaintContext {
    fn default() -> Self {
        Self {
            transform: Matrix4::IDENTITY,
            dpi_scale: 1.0,
            static_zoom: 1.0,
            semantic_opacity: 1.0,
            use_mvp: false,
            suppress_cached_cleartype: false,
            gpu_transforms_active: false,
            camera_view: Matrix4::IDENTITY,
            clip: None,
        }
    }
}
/// Text-specific source path raster contract. Preserve these inputs through
/// PathAtlas compilation; ordinary native PathDraw defaults are not equivalent.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TextPathPolicy {
    /// Original binary edge selection, independent of stored sample-grid intent.
    pub aliased: bool,
    /// Original requested grid: four for aliased, eight for grayscale paths.
    pub sample_grid: u32,
    /// Original legacy exponent or explicit negative source-transfer encoding.
    pub coverage_gamma: f32,
    /// Original device-space phase grid, four for vector glyphs.
    pub device_phase_grid: u32,
    /// Original text path scale-quantization selection.
    pub quantize_scale: bool,
    /// Original independent physical/logical raster scale.
    pub raster_scale: f32,
    /// Original explicit grayscale contrast or legacy default.
    pub coverage_transfer: GlyphCoverageTransfer,
}
/// Original vector glyph/color-layer draw with its positioned source geometry.
#[derive(Clone, Debug)]
pub struct VectorGlyphPaint {
    /// Complete positioned source outline, owned beyond cache eviction.
    pub path: Arc<PathGeometry>,
    /// Complete original target transform; no primitive history is inferred.
    pub transform: Matrix4,
    /// Original optional material; None preserves the source command policy.
    pub brush: Option<Arc<Brush>>,
    /// Original active semantic opacity, independent of brush opacity.
    pub semantic_opacity: f32,
    /// Complete original text-specific path raster policy.
    pub policy: TextPathPolicy,
}
/// Original pending atlas request; the matching fallback retains the exact font
/// and target state, including integer-origin placement after capacity failure.
#[derive(Clone, Debug)]
pub struct AtlasGlyphPaint {
    /// Actual immutable selected font owner.
    pub font: ManagedFont,
    /// Original selected font glyph slot.
    pub glyph: u16,
    /// Original target atlas em size after 4..128 clamp.
    pub raster_size: f32,
    /// Original horizontal quarter-pixel atlas phase.
    pub phase: u8,
    /// Original atlas preference and capacity-reuse intent.
    pub prefer_atlas: bool,
    /// Original resolved logical baseline for the atlas draw.
    pub snapped_position: [f32; 2],
    state: Arc<State>,
    fallback_position: [f32; 2],
    style_index: f32,
}
/// Neutral source atlas metrics for CPU instance emission. Atlas origins/extents
/// are physical texels; render extent and raster scale retain bitmap semantics.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GlyphImageMetrics {
    /// Actual physical atlas texel origin.
    pub origin: [u32; 2],
    /// Actual physical coverage width and height.
    pub size: [u32; 2],
    /// Original physical baseline-to-ink offset.
    pub bearing: [f32; 2],
    /// Original optional bitmap render width/height, zero for design-font coverage.
    pub render_size: [f32; 2],
    /// Original independent physical/logical raster scale.
    pub raster_scale: f32,
    /// True only for decoded color bitmap coverage.
    pub is_color_bitmap: bool,
}
impl AtlasGlyphPaint {
    /// Original unmodified baseline used when atlas allocation produces no tile.
    pub fn fallback_position(&self) -> [f32; 2] {
        self.fallback_position
    }
    /// Write one/two original instances after whole destination-capacity admission.
    /// A zero-sized entry belongs to fallback and emits no atlas instance.
    pub fn write_instances(
        &self,
        info: GlyphImageMetrics,
        output: &mut [GlyphInstance],
    ) -> Result<usize, Error> {
        if info.size.contains(&0) {
            return Ok(0);
        }
        let state = &self.state;
        let count = state.pass_count();
        if output.len() < count {
            return Err(Error::Capacity);
        }
        let scale = state.atlas_scale
            * if info.raster_scale > 0.0 {
                info.raster_scale
            } else {
                1.0
            };
        let width = if info.render_size[0] > 0.0 {
            info.render_size[0]
        } else {
            info.size[0] as f32
        };
        let height = if info.render_size[1] > 0.0 {
            info.render_size[1]
        } else {
            info.size[1] as f32
        };
        for (pass, output) in output.iter_mut().take(count).enumerate() {
            *output = GlyphInstance {
                snapped_logical_position: self.snapped_position,
                basis_x: [state.transform.0[0][0], state.transform.0[0][1]],
                basis_y: [state.transform.0[1][0], state.transform.0[1][1]],
                bearing_size: [
                    info.bearing[0] * state.scale_x,
                    info.bearing[1],
                    width * state.scale_x,
                    height,
                ],
                texture_coordinates: [
                    info.origin[0] as f32,
                    info.origin[1] as f32,
                    info.origin[0].wrapping_add(info.size[0]) as f32,
                    info.origin[1].wrapping_add(info.size[1]) as f32,
                ],
                color: if state.shared_style {
                    state.color
                } else {
                    state.effective_color
                },
                scale_bold_italic_flags: [
                    scale,
                    pass as f32 * state.bold_offset(),
                    state.italic,
                    placement::flags(
                        state.context.use_mvp,
                        if state.shared_style {
                            TextRenderingMode::Grayscale
                        } else {
                            state.draw.rendering_mode
                        },
                        info.is_color_bitmap,
                        state.draw.coverage_transfer,
                    ),
                ],
                brush_index: self.style_index,
                padding: 0.0,
            };
        }
        Ok(count)
    }
}
/// Painter order is explicit across atlas and vector commands; do not partition
/// this sequence by resource type when submitting it.
#[derive(Clone, Debug)]
pub enum TextPaintCommand {
    /// Pending actual atlas acquisition and capacity fallback.
    Atlas(AtlasGlyphPaint),
    /// Positioned source vector/color-layer draw.
    Vector(VectorGlyphPaint),
}
/// Atomic CPU plan; all integer source positions preflight before publication.
#[derive(Clone, Debug, Default)]
pub struct TextPaintPlan {
    commands: Vec<TextPaintCommand>,
    styles: Vec<GpuTextStyle>,
    context: TextPaintContext,
}
impl TextPaintPlan {
    /// Complete painter-order preparation commands.
    pub fn commands(&self) -> &[TextPaintCommand] {
        &self.commands
    }
    /// Original ordered shared style page.
    pub fn styles(&self) -> &[GpuTextStyle] {
        &self.styles
    }
    /// Exact target/MVP/camera/clip context required by the downstream consumer.
    pub fn context(&self) -> TextPaintContext {
        self.context
    }
}
/// Owned original source preparation/cache, independent of device and atlas.
#[derive(Default)]
pub struct TextPainter {
    paths: outlines::Cache,
}
#[derive(Debug, thiserror::Error)]
/// Original source parsing, preparation or GPU realization failure.
pub enum TextPaintError {
    #[error(transparent)]
    /// Source font lookup or outline failure.
    Font(#[from] FontError),
    #[error(transparent)]
    /// Capacity, unsupported policy or target admission failure.
    Render(#[from] Error),
    #[cfg(feature = "gpu")]
    #[error(transparent)]
    /// Same-device atlas/raster realization failure.
    Glyph(#[from] super::glyph::GlyphGpuError),
}
type Result<T, E = TextPaintError> = std::result::Result<T, E>;
#[derive(Clone, Debug)]
struct State {
    draw: TextDraw,
    context: TextPaintContext,
    transform: Matrix4,
    dpi: f32,
    raster_size: f32,
    atlas_scale: f32,
    scale_x: f32,
    italic: f32,
    rotated: bool,
    atlas_upscale: f32,
    transform_scale: f32,
    color: [f32; 4],
    effective_color: [f32; 4],
    shared_style: bool,
    policy: TextPathPolicy,
}
impl TextPainter {
    /// Original ordinary layout/positioned command compilation, through atlas
    /// acquisition requests and complete color/vector fallback recipes.
    pub fn plan(&mut self, draw: &TextDraw, context: TextPaintContext) -> Result<TextPaintPlan> {
        draw.validate(context.transform, context.dpi_scale * context.static_zoom)?;
        if draw.hinting_mode == TextHintingMode::IntegerPixel {
            let TextSource::Positioned(run) = &draw.source else {
                unreachable!()
            };
            for index in run.range().ok_or(Error::UnsupportedExecution)? {
                placement::integer_origin(
                    context
                        .transform
                        .transform_point(add(run.positions[index], draw.position)),
                    context.dpi_scale * context.static_zoom,
                )
                .ok_or(Error::UnsupportedExecution)?;
            }
        }
        let range = if let TextSource::Positioned(run) = &draw.source {
            let Some(range) = run.range() else {
                return Ok(TextPaintPlan::default());
            };
            Some(range)
        } else {
            None
        };
        let state = Arc::new(placement::state(draw, context));
        let mut result = TextPaintPlan {
            context,
            ..Default::default()
        };
        let style_index = if state.shared_style {
            push(
                &mut result.styles,
                GpuTextStyle {
                    color: state.effective_color,
                    rendering_mode: state.draw.rendering_mode as u32,
                    padding: [0; 3],
                },
            )?;
            0.0
        } else {
            -1.0
        };
        match &draw.source {
            TextSource::Layout(layout) => {
                for glyph in layout.glyphs() {
                    self.glyph(
                        &mut result,
                        state.clone(),
                        &glyph.font,
                        glyph.glyph_index,
                        glyph.position,
                        style_index,
                        true,
                    )?;
                }
            }
            TextSource::Positioned(run) => {
                for index in range.unwrap() {
                    self.glyph(
                        &mut result,
                        state.clone(),
                        &run.font,
                        run.indices[index],
                        run.positions[index],
                        style_index,
                        false,
                    )?;
                }
            }
        }
        Ok(result)
    }
    #[allow(clippy::too_many_arguments)] // Original source and ordered target metadata.
    fn glyph(
        &mut self,
        output: &mut TextPaintPlan,
        state: Arc<State>,
        font: &ManagedFont,
        glyph: u16,
        source_position: [f32; 2],
        style_index: f32,
        layout: bool,
    ) -> Result<()> {
        let position = add(source_position, state.draw.position);
        if font.metadata().has_color_glyphs
            && let Some(layers) = font.color_layers(glyph)?
            && !layers.is_empty()
        {
            if state.vector_clipped(position) {
                return Ok(());
            }
            for layer in layers.iter() {
                let outline = if let Some(path) = &layer.geometry {
                    Some(outlines::Outline::Vector(path.clone()))
                } else {
                    font.glyph_outline(layer.glyph)?
                        .map(outlines::Outline::Font)
                };
                let Some(outline) = outline else { continue };
                let em = state.draw.font_size / f32::from(font.metadata().units_per_em);
                let (path, transform, phase) = self.paths.position(
                    &outline,
                    em,
                    position,
                    state.italic,
                    state.scale_x,
                    layer.uses_svg_coordinates,
                    state.transform,
                )?;
                push(
                    &mut output.commands,
                    TextPaintCommand::Vector(VectorGlyphPaint {
                        path,
                        transform,
                        brush: Some(outlines::layer_brush(layer, em, phase)?),
                        semantic_opacity: state.context.semantic_opacity,
                        policy: state.policy,
                    }),
                )?;
            }
            return Ok(());
        }
        let bitmap = font.metadata().has_bitmap_glyphs
            && font.try_bitmap_glyph(glyph, state.raster_size)?.is_some();
        if !bitmap
            && (state.draw.vector_rendering
                || ((layout || !state.draw.prefer_atlas)
                    && (font.metadata().has_cff_outlines || state.atlas_upscale > 1.0001)))
        {
            return self.fallback(
                &mut output.commands,
                font,
                glyph,
                &state,
                position,
                state.transform,
            );
        }
        let transformed = state.transform.transform_point(position);
        let (phase, snapped) = placement::placement(
            transformed,
            state.dpi,
            state.raster_size,
            state.rotated,
            state.draw.hinting_mode,
        )?;
        if state.clipped(snapped, state.draw.font_size) {
            return Ok(());
        }
        push(
            &mut output.commands,
            TextPaintCommand::Atlas(AtlasGlyphPaint {
                font: font.clone(),
                glyph,
                raster_size: state.raster_size,
                phase,
                prefer_atlas: state.draw.prefer_atlas,
                snapped_position: snapped,
                state,
                fallback_position: position,
                style_index,
            }),
        )
    }
    fn fallback(
        &mut self,
        output: &mut Vec<TextPaintCommand>,
        font: &ManagedFont,
        glyph: u16,
        state: &State,
        position: [f32; 2],
        transform: Matrix4,
    ) -> Result<()> {
        // Capacity fallback can replace the target translation after integer
        // rounding; use that exact frame for both culling and path placement.
        let mut clip_state = state.clone();
        clip_state.transform = transform;
        clip_state.transform_scale = transform.stroke_scales().map_or(1.0, |s| s[0]);
        if clip_state.vector_clipped(position) {
            return Ok(());
        }
        let Some(outline) = font.glyph_outline(glyph)? else {
            return Ok(());
        };
        let outline = outlines::Outline::Font(outline);
        let em = state.draw.font_size / f32::from(font.metadata().units_per_em);
        for pass in 0..state.pass_count() {
            let positioned = add(position, [pass as f32 * state.bold_offset(), 0.0]);
            let (path, transform, _) = self.paths.position(
                &outline,
                em,
                positioned,
                state.italic,
                state.scale_x,
                false,
                transform,
            )?;
            push(
                output,
                TextPaintCommand::Vector(VectorGlyphPaint {
                    path,
                    transform,
                    brush: state.draw.brush.clone(),
                    semantic_opacity: state.context.semantic_opacity,
                    policy: state.policy,
                }),
            )?;
        }
        Ok(())
    }
    /// Current bounded positioned-outline inventory, excluding source font caches.
    pub fn cached_path_count(&self) -> usize {
        self.paths.len()
    }
    /// Drop only this painter's positioned paths; retained plans keep their owners.
    pub fn clear_path_cache(&mut self) {
        self.paths.clear();
    }
}
impl State {
    fn pass_count(&self) -> usize {
        if self.draw.bold { 2 } else { 1 }
    }
    fn bold_offset(&self) -> f32 {
        self.draw.font_size * 0.035
    }
    fn clipped(&self, position: [f32; 2], size: f32) -> bool {
        let Some([x, y, width, height]) = self.context.clip else {
            return false;
        };
        if self.context.gpu_transforms_active {
            return false;
        }
        let half = size * 2.0;
        position[0] + half <= x
            || position[0] - half >= x + width
            || position[1] + half <= y
            || position[1] - half >= y + height
    }
    fn vector_clipped(&self, position: [f32; 2]) -> bool {
        self.clipped(
            self.transform.transform_point(position),
            self.draw.font_size * self.transform_scale * maximum(1.0, self.scale_x.abs()),
        )
    }
}
fn push<T>(values: &mut Vec<T>, value: T) -> Result<()> {
    values.try_reserve(1).map_err(|_| Error::Capacity)?;
    values.push(value);
    Ok(())
}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] + b[0], a[1] + b[1]]
}
fn finite(p: [f32; 2]) -> bool {
    p.iter().all(|v| v.is_finite())
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
