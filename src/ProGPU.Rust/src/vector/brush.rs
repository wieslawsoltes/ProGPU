//! Original managed brush/pen payloads and construction admission. These retain
//! materials for later GPU compilation; no CPU paint or shader approximation.
use super::*;
use std::sync::Arc;

/// One original straight RGBA gradient color and unnormalized offset.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GradientStop {
    /// Original color components, including source alpha.
    pub color: [f32; 4],
    /// Original stop offset; recording does not clamp or sort.
    pub offset: f32,
}
/// Original center-to-boundary scalar blend sample.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PathGradientBlendStop {
    /// Original blend factor.
    pub factor: f32,
    /// Original offset.
    pub offset: f32,
}
/// Original managed gradient spread values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum GradientSpread {
    /// Extend the first/last stop.
    #[default]
    Pad = 0,
    /// Reflect alternate periods.
    Reflect = 1,
    /// Repeat periods.
    Repeat = 2,
    /// Transparent outside the gradient domain.
    Decal = 3,
}
/// Original independent gradient interpolation policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum GradientInterpolation {
    /// Original sRGB component interpolation.
    #[default]
    SRgbLinear = 0,
    /// Original scRGB interpolation.
    ScRgbLinear = 1,
}
/// Shared source gradient metadata; sweep constructors select Repeat explicitly.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GradientOptions {
    /// Complete source coordinate transform, default identity.
    pub transform: Matrix4,
    /// Independent spread policy, default Pad.
    pub spread: GradientSpread,
    /// Independent color interpolation, default sRGB.
    pub interpolation: GradientInterpolation,
}
/// Owned source brush generation with independent original opacity.
#[derive(Clone, Debug, PartialEq)]
pub struct Brush {
    /// Raw source brush opacity, default one; recording does not clamp.
    pub opacity: f32,
    /// Typed original material payload.
    pub kind: BrushKind,
}
impl Brush {
    /// Retain a material with the original unit opacity default.
    pub fn new(kind: BrushKind) -> Self {
        Self { opacity: 1.0, kind }
    }
    /// Original uint solid constructor uses RRGGBBAA, independent of native BGRA.
    pub fn solid_rgba(rgba: u32) -> Self {
        Self::new(BrushKind::Solid(
            [24, 16, 8, 0].map(|shift| ((rgba >> shift) & 255) as f32 / 255.0),
        ))
    }
}
impl Default for Brush {
    fn default() -> Self {
        Self::new(BrushKind::Solid([0.0; 4]))
    }
}
/// Complete non-image material families from the original managed Brush.cs.
/// Image/effect brushes and command interception have separate source contracts.
#[derive(Clone, Debug, PartialEq)]
pub enum BrushKind {
    /// Original straight RGBA solid.
    Solid([f32; 4]),
    /// Linear gradient with retained unsorted stops.
    Linear(LinearGradient),
    /// Elliptical radial gradient with independent focal origin.
    Radial(RadialGradient),
    /// Bounded polygon path gradient.
    Path(PathGradient),
    /// Two-center, two-radius conical gradient.
    TwoPointConical(TwoPointConicalGradient),
    /// Angular sweep gradient.
    Sweep(SweepGradient),
    /// Procedural noise parameters; evaluated by the GPU consumer.
    PerlinNoise(PerlinNoise),
    /// One family of parallel hatch lines.
    Hatch(HatchPattern),
    /// Two perpendicular hatch families, independent of the single-hatch kind.
    CrossHatch(HatchPattern),
    /// Immutable one-bit 8-by-8 source tile.
    Tile(TilePattern),
    /// Validated ordered multi-family procedural hatch.
    HatchSet(HatchPatternSet),
}
/// Original linear gradient payload.
#[derive(Clone, Debug, PartialEq)]
pub struct LinearGradient {
    /// Original start point.
    pub start: [f32; 2],
    /// Original end point.
    pub end: [f32; 2],
    /// Complete source stops in original order.
    pub stops: Vec<GradientStop>,
    /// Independent source frame/spread/interpolation.
    pub options: GradientOptions,
}
impl LinearGradient {
    /// Record without validating, sorting or normalizing source values.
    pub fn new(start: [f32; 2], end: [f32; 2], stops: Vec<GradientStop>) -> Self {
        Self {
            start,
            end,
            stops,
            options: GradientOptions::default(),
        }
    }
}
/// Original radial gradient, retaining independent radii and gradient origin.
#[derive(Clone, Debug, PartialEq)]
pub struct RadialGradient {
    /// Original center.
    pub center: [f32; 2],
    /// Original focal point, not implicitly clamped into the ellipse.
    pub origin: [f32; 2],
    /// Original X/Y radius pair.
    pub radii: [f32; 2],
    /// Complete source stops in original order.
    pub stops: Vec<GradientStop>,
    /// Independent source frame/spread/interpolation.
    pub options: GradientOptions,
}
impl RadialGradient {
    /// Original full constructor, retaining all source values.
    pub fn new(
        center: [f32; 2],
        origin: [f32; 2],
        radii: [f32; 2],
        stops: Vec<GradientStop>,
    ) -> Self {
        Self {
            center,
            origin,
            radii,
            stops,
            options: GradientOptions::default(),
        }
    }
    /// Original common-radius constructor with the focal point at the center.
    pub fn circular(center: [f32; 2], radius: f32, stops: Vec<GradientStop>) -> Self {
        Self::new(center, center, [radius; 2], stops)
    }
    /// Original comparison-based radius getter, including its NaN behavior.
    pub fn radius(&self) -> f32 {
        if self.radii[0] >= self.radii[1] {
            self.radii[0]
        } else {
            self.radii[1]
        }
    }
    /// Original common-radius setter; neither value is normalized.
    pub fn set_radius(&mut self, radius: f32) {
        self.radii = [radius; 2];
    }
}
/// Original path gradient with immutable owned boundary and stop inventories.
#[derive(Clone, Debug, PartialEq)]
pub struct PathGradient {
    boundary: Arc<[[f32; 2]]>,
    surround_colors: Arc<[[f32; 4]]>,
    blend_stops: Arc<[PathGradientBlendStop]>,
    preset_stops: Arc<[GradientStop]>,
    /// Original center.
    pub center: [f32; 2],
    /// Original center color.
    pub center_color: [f32; 4],
    /// Original independent focus scales, default zero.
    pub focus_scales: [f32; 2],
    /// Independent source frame/spread/interpolation.
    pub options: GradientOptions,
}
impl PathGradient {
    /// Original production shader's boundary limit.
    pub const MAX_BOUNDARY_POINTS: usize = 128;
    /// Snapshot complete input after original whole-inventory length admission.
    /// Exactly one of blend/preset stops must be nonempty. Other raw values stay
    /// unchanged; finiteness is not an additional constructor requirement.
    pub fn new(
        boundary: &[[f32; 2]],
        surround_colors: &[[f32; 4]],
        center: [f32; 2],
        center_color: [f32; 4],
        blend_stops: &[PathGradientBlendStop],
        preset_stops: &[GradientStop],
    ) -> Result<Self, Error> {
        if !(2..=Self::MAX_BOUNDARY_POINTS).contains(&boundary.len())
            || surround_colors.len() != boundary.len()
            || blend_stops.is_empty() == preset_stops.is_empty()
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            boundary: snapshot(boundary)?,
            surround_colors: snapshot(surround_colors)?,
            blend_stops: snapshot(blend_stops)?,
            preset_stops: snapshot(preset_stops)?,
            center,
            center_color,
            focus_scales: [0.0; 2],
            options: GradientOptions::default(),
        })
    }
    /// Complete immutable boundary.
    pub fn boundary(&self) -> &[[f32; 2]] {
        &self.boundary
    }
    /// One immutable original color per boundary point.
    pub fn surround_colors(&self) -> &[[f32; 4]] {
        &self.surround_colors
    }
    /// Original scalar blend samples (empty for a preset-color gradient).
    pub fn blend_stops(&self) -> &[PathGradientBlendStop] {
        &self.blend_stops
    }
    /// Original color samples (empty for a scalar-blend gradient).
    pub fn preset_stops(&self) -> &[GradientStop] {
        &self.preset_stops
    }
    /// Original explicit preset-color selection.
    pub fn uses_preset_colors(&self) -> bool {
        !self.preset_stops.is_empty()
    }
}
/// Original two-point conical gradient parameters.
#[derive(Clone, Debug, PartialEq)]
pub struct TwoPointConicalGradient {
    /// Original first center.
    pub start_center: [f32; 2],
    /// Original first radius.
    pub start_radius: f32,
    /// Original second center.
    pub end_center: [f32; 2],
    /// Original second radius.
    pub end_radius: f32,
    /// Optional original outside color; absence remains distinct.
    pub outside_color: Option<[f32; 4]>,
    /// Complete original stop inventory.
    pub stops: Vec<GradientStop>,
    /// Independent source frame/spread/interpolation.
    pub options: GradientOptions,
}
impl TwoPointConicalGradient {
    /// Record original values with no outside-color override.
    pub fn new(
        start_center: [f32; 2],
        start_radius: f32,
        end_center: [f32; 2],
        end_radius: f32,
        stops: Vec<GradientStop>,
    ) -> Self {
        Self {
            start_center,
            start_radius,
            end_center,
            end_radius,
            outside_color: None,
            stops,
            options: GradientOptions::default(),
        }
    }
}
/// Original angular gradient with independently retained start/end degrees.
#[derive(Clone, Debug, PartialEq)]
pub struct SweepGradient {
    /// Original center.
    pub center: [f32; 2],
    /// Original first angle in degrees, default zero.
    pub start_angle: f32,
    /// Original final angle in degrees, default 360.
    pub end_angle: f32,
    /// Complete original stops.
    pub stops: Vec<GradientStop>,
    /// Independent source metadata; the original default is Repeat.
    pub options: GradientOptions,
}
impl SweepGradient {
    /// Original full-circle constructor with Repeat spread.
    pub fn new(center: [f32; 2], stops: Vec<GradientStop>) -> Self {
        Self {
            center,
            start_angle: 0.0,
            end_angle: 360.0,
            stops,
            options: GradientOptions {
                spread: GradientSpread::Repeat,
                ..Default::default()
            },
        }
    }
}
/// Original procedural noise parameters, without CPU noise evaluation.
#[derive(Clone, Debug, PartialEq)]
pub struct PerlinNoise {
    /// Original turbulence selection.
    pub turbulence: bool,
    /// Original two-axis base frequency.
    pub base_frequency: [f32; 2],
    /// Original signed octave count.
    pub octaves: i32,
    /// Original floating seed.
    pub seed: f32,
    /// Original tile size.
    pub tile_size: [f32; 2],
    /// Complete original coordinate frame.
    pub transform: Matrix4,
}
impl PerlinNoise {
    /// Original constructor with identity coordinate transform.
    pub fn new(
        turbulence: bool,
        base_frequency: [f32; 2],
        octaves: i32,
        seed: f32,
        tile_size: [f32; 2],
    ) -> Self {
        Self {
            turbulence,
            base_frequency,
            octaves,
            seed,
            tile_size,
            transform: Matrix4::IDENTITY,
        }
    }
}
/// Original single/cross-hatch payload. The BrushKind preserves the distinction.
#[derive(Clone, Debug, PartialEq)]
pub struct HatchPattern {
    /// Original normal-axis angle in radians.
    pub angle: f32,
    /// Original periodic spacing.
    pub spacing: f32,
    /// Original width; zero selects derivative-based device hairline.
    pub thickness: f32,
    /// Original straight color.
    pub color: [f32; 4],
    /// Complete original coordinate frame.
    pub transform: Matrix4,
}
impl HatchPattern {
    /// Original unchecked constructor with identity coordinate transform.
    pub fn new(angle: f32, spacing: f32, thickness: f32, color: [f32; 4]) -> Self {
        Self {
            angle,
            spacing,
            thickness,
            color,
            transform: Matrix4::IDENTITY,
        }
    }
}
/// Immutable source one-bit tile payload, independent of brush opacity.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TilePattern {
    pattern: u64,
    foreground: [f32; 4],
    background: [f32; 4],
    origin: [f32; 2],
}
impl TilePattern {
    /// Retain the original complete tile and exact frame/color values.
    pub fn new(pattern: u64, foreground: [f32; 4], background: [f32; 4], origin: [f32; 2]) -> Self {
        Self {
            pattern,
            foreground,
            background,
            origin,
        }
    }
    /// Complete original 64-bit mask.
    pub fn pattern(self) -> u64 {
        self.pattern
    }
    /// Original foreground color.
    pub fn foreground(self) -> [f32; 4] {
        self.foreground
    }
    /// Original background color.
    pub fn background(self) -> [f32; 4] {
        self.background
    }
    /// Original tile origin.
    pub fn origin(self) -> [f32; 2] {
        self.origin
    }
    /// Original periodic bit lookup, preserving negative integer wrapping.
    pub fn is_foreground_pixel(self, x: i32, y: i32) -> bool {
        ((self.pattern >> ((y & 7) * 8 + (x & 7))) & 1) != 0
    }
}
/// One original ordered hatch family; signed offsets/counts remain explicit.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HatchLineFamily {
    /// Original line base point.
    pub base: [f32; 2],
    /// Original unit tangent.
    pub direction: [f32; 2],
    /// Original tangent shift between rows.
    pub tangent_shift: f32,
    /// Original positive row spacing.
    pub spacing: f32,
    /// Original first dash index.
    pub dash_offset: i32,
    /// Original dash count, between zero and six.
    pub dash_count: i32,
    /// Original source period.
    pub dash_period: f32,
}
/// Owned multi-family hatch with immutable complete family/dash inventories.
#[derive(Clone, Debug, PartialEq)]
pub struct HatchPatternSet {
    families: Arc<[HatchLineFamily]>,
    dashes: Arc<[f32]>,
    /// Original mutable width; constructor validates against every spacing.
    pub thickness: f32,
    /// Original straight color.
    pub color: [f32; 4],
    /// Complete original coordinate frame.
    pub transform: Matrix4,
}
impl HatchPatternSet {
    /// Original PAT/DXF per-family dash bound.
    pub const MAX_DASH_COUNT: i32 = 6;
    /// Validate the complete original ordered stream before publishing owned
    /// snapshots. Signed gaps and zero-length drawn dots stay distinct.
    pub fn new(
        families: &[HatchLineFamily],
        dashes: &[f32],
        thickness: f32,
        color: [f32; 4],
    ) -> Result<Self, Error> {
        if families.is_empty()
            || !thickness.is_finite()
            || thickness < 0.0
            || !color.iter().all(|c| c.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        let total = i32::try_from(dashes.len()).map_err(|_| Error::Capacity)?;
        let mut expected = 0;
        for family in families {
            let length_squared = dot(family.direction, family.direction);
            if !finite(family.base)
                || !finite(family.direction)
                || !family.tangent_shift.is_finite()
                || !family.spacing.is_finite()
                || family.spacing <= 0.0
                || thickness > family.spacing
                || !family.dash_period.is_finite()
                || family.dash_period < 0.0
                || !length_squared.is_finite()
                || (length_squared - 1.0).abs() > 0.001
                || family.dash_offset != expected
                || family.dash_count < 0
                || family.dash_count > Self::MAX_DASH_COUNT
                || family.dash_offset > total - family.dash_count
            {
                return Err(Error::InvalidInput);
            }
            if family.dash_count == 0 {
                if family.dash_period != 0.0 {
                    return Err(Error::InvalidInput);
                }
                continue;
            }
            let end = expected + family.dash_count;
            let mut period = 0.0;
            let mut draws = false;
            for &dash in &dashes[expected as usize..end as usize] {
                if !dash.is_finite() {
                    return Err(Error::InvalidInput);
                }
                period += dash.abs();
                draws |= dash >= 0.0;
            }
            let tolerance = maximum(1.0, period) * 0.00001;
            if !draws || period <= 0.0 || (period - family.dash_period).abs() > tolerance {
                return Err(Error::InvalidInput);
            }
            expected = end;
        }
        if expected != total {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            families: snapshot(families)?,
            dashes: snapshot(dashes)?,
            thickness,
            color,
            transform: Matrix4::IDENTITY,
        })
    }
    /// Complete immutable original family inventory.
    pub fn families(&self) -> &[HatchLineFamily] {
        &self.families
    }
    /// Complete immutable contiguous dash inventory.
    pub fn dashes(&self) -> &[f32] {
        &self.dashes
    }
}
/// Original join values; MiterOrBevel is independent of clipped-Miter/WPF flags.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum PenLineJoin {
    /// Generic original miter.
    #[default]
    Miter = 0,
    /// Bevel join.
    Bevel = 1,
    /// Round join.
    Round = 2,
    /// Miter within the limit, bevel beyond it.
    MiterOrBevel = 3,
}
/// Original stroke expansion coordinate space.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum PenStrokeTransform {
    /// Expand in source space, then transform.
    #[default]
    Normal = 0,
    /// Transform centerline, then expand in device space.
    Fixed = 1,
}
/// Raw original mutable pen parameters, preserving all independent source flags.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PenOptions {
    /// Original width, including the exact -1 hairline sentinel.
    pub thickness: f32,
    /// Original join selection.
    pub join: PenLineJoin,
    /// Original miter limit; normalized only by the pen constructor.
    pub miter_limit: f32,
    /// Independent centerline-relative clipped-Miter policy, default false.
    pub clip_miter_at_limit: bool,
    /// Independent source WPF clipping/reversal/smooth policy, default false.
    pub use_wpf_join_semantics: bool,
    /// Original first cap.
    pub start_cap: PenLineCap,
    /// Original last cap.
    pub end_cap: PenLineCap,
    /// Original cap for dash endpoints.
    pub dash_cap: PenLineCap,
    /// Original source/device expansion frame.
    pub transform: PenStrokeTransform,
    /// Exact double source offset, normalized only by the pen constructor.
    pub dash_offset: f64,
}
impl Default for PenOptions {
    fn default() -> Self {
        Self {
            thickness: 1.0,
            join: PenLineJoin::Miter,
            miter_limit: 10.0,
            clip_miter_at_limit: false,
            use_wpf_join_semantics: false,
            start_cap: PenLineCap::Flat,
            end_cap: PenLineCap::Flat,
            dash_cap: PenLineCap::Flat,
            transform: PenStrokeTransform::Normal,
            dash_offset: 0.0,
        }
    }
}
/// Owned pen generation. Immutable material and dash generations can be shared;
/// replacing either never changes another retained pen's source state.
#[derive(Clone, Debug, PartialEq)]
pub struct Pen {
    /// Retained material generation, independent of the stroke parameters.
    pub brush: Arc<Brush>,
    /// Raw original stroke state. Later assignments retain their exact values.
    pub options: PenOptions,
    dashes: Option<Arc<[f64]>>,
}
impl Pen {
    /// Original explicit one-device-pixel hairline sentinel.
    pub const HAIRLINE_THICKNESS: f32 = -1.0;
    /// Original default pen around an owned material generation.
    pub fn new(brush: Arc<Brush>) -> Self {
        Self {
            brush,
            options: PenOptions::default(),
            dashes: None,
        }
    }
    /// Original constructor normalization and copied optional double intervals.
    /// `Some([])` remains distinct from the span setter's empty-to-None behavior.
    pub fn with_options(
        brush: Arc<Brush>,
        mut options: PenOptions,
        dashes: Option<&[f64]>,
    ) -> Result<Self, Error> {
        if !options.miter_limit.is_finite() || options.miter_limit < 1.0 {
            options.miter_limit = 1.0;
        }
        if !options.dash_offset.is_finite() {
            options.dash_offset = 0.0;
        }
        Ok(Self {
            brush,
            options,
            dashes: dashes.map(snapshot).transpose()?,
        })
    }
    /// Original exact sentinel test; arbitrary negative widths are not hairlines.
    pub fn is_hairline(&self) -> bool {
        self.options.thickness == Self::HAIRLINE_THICKNESS
    }
    /// Original fixed-width classification excludes explicit hairlines.
    pub fn is_fixed(&self) -> bool {
        !self.is_hairline() && self.options.transform == PenStrokeTransform::Fixed
    }
    /// Original nonempty-pattern test, independent of dash values.
    pub fn has_dash_pattern(&self) -> bool {
        self.dashes.as_ref().is_some_and(|d| !d.is_empty())
    }
    /// Immutable view of the exact retained double intervals. Rust borrowing
    /// prevents callers from mutating the shared source snapshot.
    pub fn dash_array(&self) -> Option<&[f64]> {
        self.dashes.as_deref()
    }
    /// Copy optional original property input, retaining Some(empty).
    pub fn set_dash_array(&mut self, dashes: Option<&[f64]>) -> Result<(), Error> {
        self.dashes = dashes.map(snapshot).transpose()?;
        Ok(())
    }
    /// Original span setter: copy nonempty intervals once; empty clears storage.
    pub fn set_dash_pattern(&mut self, dashes: &[f64]) -> Result<(), Error> {
        self.set_dash_array((!dashes.is_empty()).then_some(dashes))
    }
    /// Snapshot every original flag/parameter while sharing immutable dash data.
    pub fn with_brush(&self, brush: Arc<Brush>) -> Self {
        Self {
            brush,
            options: self.options,
            dashes: self.dashes.clone(),
        }
    }
    /// Original independent WPF join admission. No generic policy is inferred
    /// from the join enum, and this is not source/package pixel qualification.
    pub fn validate_join_semantics(&self) -> Result<(), Error> {
        if self.options.use_wpf_join_semantics
            && (self.is_hairline()
                || self.options.transform != PenStrokeTransform::Normal
                || self.options.join == PenLineJoin::MiterOrBevel)
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
}
fn snapshot<T: Copy>(values: &[T]) -> Result<Arc<[T]>, Error> {
    let mut owned = Vec::new();
    owned
        .try_reserve_exact(values.len())
        .map_err(|_| Error::Capacity)?;
    owned.extend_from_slice(values);
    Ok(owned.into())
}
