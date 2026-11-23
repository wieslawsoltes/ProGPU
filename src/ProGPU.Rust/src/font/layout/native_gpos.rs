//! Original native OpenType positioning and synchronous device-frame contracts.
//! Lookup execution is allocation-free and preserves partial writes on a later
//! malformed nested table. Managed positioning remains an independent policy.
use super::{
    ClassDefinition, ContextCoverage, ContextSubtable, Coverage, FontError, Gdef, GlyphClass,
    GlyphSetDigest, LayoutTable, Lookup, SfntFace, Tag, fits, u16_at, u32_at,
};
use crate::text::ShapingGlyph;
mod attachments;
mod context;
mod plan;
mod primitive;
pub use attachments::resolve_gpos_attachments;
pub use plan::NativePositioningPlan;
type Applied = Result<bool, FontError>;

/// Original native run direction, including the unspecified wire value.
#[repr(u8)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GposDirection {
    /// Preserve unspecified input; device attachment resolution rejects it.
    Unspecified = 0,
    /// Forward horizontal placement.
    #[default]
    LeftToRight = 1,
    /// Reverse horizontal placement.
    RightToLeft = 2,
    /// Forward vertical placement.
    TopToBottom = 3,
    /// Reverse vertical placement.
    BottomToTop = 4,
}
/// Original native attachment relationship.
#[repr(u8)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GposAttachmentKind {
    /// No attachment; a nonnegative target still participates in dependency resolution.
    #[default]
    None = 0,
    /// Mark anchor resolved against parent origin and intervening advances.
    Mark = 1,
    /// Cursive cross-axis Y dependency.
    CursiveHorizontal = 2,
    /// Cursive cross-axis X dependency.
    CursiveVertical = 3,
}
/// Original eight-byte transient native attachment, separate from glyph wire data.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GposAttachment {
    /// Target glyph slot, or -1 for none.
    pub target: i32,
    /// Actual dependency kind.
    pub kind: GposAttachmentKind,
    /// Original fallback ligature component count.
    pub component_count: u8,
    /// Original selected component; 255 means unspecified.
    pub component: u8,
    /// Original prior-positioned marker, preserved through unrelated updates.
    pub positioned: u8,
}
impl Default for GposAttachment {
    fn default() -> Self {
        Self {
            target: -1,
            kind: GposAttachmentKind::None,
            component_count: 0,
            component: 255,
            positioned: 0,
        }
    }
}
const _: () = assert!(size_of::<GposAttachment>() == 8);

/// Original native positioning options, independent of feature/script selection.
#[derive(Clone, Copy)]
pub struct GposOptions<'a> {
    /// Optional exact GDEF generation.
    pub gdef: Option<Gdef<'a>>,
    /// Original run direction.
    pub direction: GposDirection,
    /// Original selected immutable font for Device/VariationIndex interpretation.
    pub font: Option<&'a SfntFace>,
    /// Original normalized F2DOT14 coordinates; empty selects the default instance.
    pub normalized_coordinates: &'a [i16],
    /// Independent X/Y target ppem for device records in design-unit execution.
    pub pixels_per_em: [u16; 2],
    /// Optional negative-only filter for whole-lookup execution.
    pub lookup_digest: Option<GlyphSetDigest>,
    /// Exact top-level coverage filter, also applied for a single-position call.
    pub lookup_coverage: Option<Coverage<'a>>,
    /// Original retained context descriptors; mismatched lookup count is ignored.
    pub lookup_context_subtables: &'a [ContextSubtable],
    /// Original coverage owners selected by the context descriptors.
    pub lookup_context_coverages: &'a [ContextCoverage<'a>],
}
impl Default for GposOptions<'_> {
    fn default() -> Self {
        Self {
            gdef: None,
            direction: Default::default(),
            font: None,
            normalized_coordinates: &[],
            pixels_per_em: [0; 2],
            lookup_digest: None,
            lookup_coverage: None,
            lookup_context_subtables: &[],
            lookup_context_coverages: &[],
        }
    }
}
/// Original independent four-lane metric addition policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GposArithmetic {
    /// Hardware SIMD on original supported native architectures or WASM SIMD128.
    #[default]
    IntrinsicSimd,
    /// Explicit scalar oracle with the same checked publication.
    ScalarReference,
}
/// Actual synchronously borrowed native outline/metric generation. Implementors
/// must retain their source owner and return existing points/projections without
/// font execution, GPU work, or callbacks into managed/application code.
pub trait GposDeviceSource {
    /// Exact font object used by this generation, not an equivalent reconstruction.
    fn font(&self) -> &SfntFace;
    /// Exact normalized instance coordinates of retained outlines.
    fn normalized_coordinates(&self) -> &[i16];
    /// Actual independent horizontal and vertical ppem.
    fn pixels_per_em(&self) -> [u16; 2];
    /// Project two original design vectors to the native 26.6 target frame.
    fn project_design(&self, vectors: [[i32; 2]; 2]) -> Result<[[i32; 2]; 2], FontError>;
    /// Read an actual retained outline point for this original descriptor slot.
    fn contour_point(
        &self,
        descriptor: usize,
        glyph: u32,
        point: u16,
    ) -> Result<[i32; 2], FontError>;
}
/// Borrowed exact native device generation and arithmetic policy.
#[derive(Clone, Copy)]
pub struct GposDeviceFrame<'a> {
    /// Generation retained for the complete synchronous positioning call.
    pub source: &'a dyn GposDeviceSource,
    /// Explicit checked metric arithmetic route.
    pub arithmetic: GposArithmetic,
}
impl GposDeviceFrame<'_> {
    /// Validate exact font-object and instance identity before glyph mutation.
    pub fn validate(&self, options: GposOptions<'_>) -> Result<(), FontError> {
        if options
            .font
            .is_none_or(|f| !std::ptr::eq(f, self.source.font()))
            || options.normalized_coordinates != self.source.normalized_coordinates()
            || self.arithmetic == GposArithmetic::IntrinsicSimd
                && !cfg!(any(
                    target_arch = "aarch64",
                    target_arch = "x86_64",
                    all(target_arch = "x86", target_feature = "sse2"),
                    all(target_arch = "wasm32", target_feature = "simd128")
                ))
        {
            return Err(FontError::InvalidArgument);
        }
        Ok(())
    }
}
/// Caller-owned top-level application result, retaining earlier successes on error.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GposState {
    /// Reset on entry; set only by successfully applied top-level lookups.
    pub applied: bool,
}

impl LayoutTable<'_> {
    /// Apply one original native GPOS lookup over the exact caller-owned glyphs.
    /// `None` visits all slots in forward lookup order; `Some` applies at one slot.
    /// Attachment lookups require exactly one supplied attachment per glyph.
    /// Nested failures preserve the original partial writes and dependency flags.
    #[allow(clippy::too_many_arguments)] // Original glyph, attachment and result owners remain independent.
    pub fn apply_gpos(
        &self,
        lookup: u16,
        position: Option<u32>,
        glyphs: &mut [ShapingGlyph],
        attachments: &mut [GposAttachment],
        options: GposOptions<'_>,
        device: Option<GposDeviceFrame<'_>>,
        state: &mut GposState,
    ) -> Result<(), FontError> {
        state.applied = false;
        let mut options = options;
        if let Some(frame) = device {
            frame.validate(options)?;
            options.pixels_per_em = frame.source.pixels_per_em();
        }
        if position.is_some_and(|p| p as usize >= glyphs.len()) {
            return Err(FontError::InvalidArgument);
        }
        let mut executor = Executor {
            table: *self,
            glyphs,
            attachments,
            options,
            device,
        };
        if let Some(position) = position {
            let p = position as usize;
            if !executor.covered(p) {
                return Ok(());
            }
            state.applied = executor.lookup(lookup, p, 0)?;
            return Ok(());
        }
        let lookup = self.lookup(lookup)?;
        if !(1..=9).contains(&lookup.lookup_type()) {
            return Ok(());
        }
        executor.attachment_admission(lookup.lookup_type())?;
        for p in 0..executor.glyphs.len() {
            let glyph = executor.glyphs[p].glyph_id;
            if glyph <= 65535
                && executor
                    .options
                    .lookup_digest
                    .is_some_and(|d| !d.may_have(glyph as u16))
            {
                continue;
            }
            let frame = Frame::new(lookup, p, 0);
            if !executor.covered(p) || !executor.eligible(p, frame) {
                continue;
            }
            for i in 0..lookup.subtable_count() {
                if !executor.may_start(lookup, i, p) {
                    continue;
                }
                if executor.subtable(lookup.lookup_type(), lookup.subtable_offset(i)?, frame)? {
                    state.applied = true;
                    break;
                }
            }
        }
        Ok(())
    }
}
#[derive(Clone, Copy)]
struct Frame {
    position: usize,
    flags: u16,
    mark_set: u16,
    depth: u32,
}
impl Frame {
    fn new(lookup: Lookup<'_>, position: usize, depth: u32) -> Self {
        Self {
            position,
            flags: lookup.flags(),
            mark_set: lookup.mark_filtering_set(),
            depth,
        }
    }
}
struct Executor<'t, 'o, 's, 'd> {
    table: LayoutTable<'t>,
    glyphs: &'s mut [ShapingGlyph],
    attachments: &'s mut [GposAttachment],
    options: GposOptions<'o>,
    device: Option<GposDeviceFrame<'d>>,
}
fn require(value: bool) -> Result<(), FontError> {
    if value {
        Ok(())
    } else {
        Err(FontError::InvalidFace)
    }
}
fn relative(parent: usize, value: u32) -> Result<usize, FontError> {
    require(value != 0)?;
    parent
        .checked_add(value as usize)
        .ok_or(FontError::InvalidFace)
}
fn checked(value: i64) -> Result<i32, FontError> {
    i32::try_from(value).map_err(|_| FontError::InvalidFace)
}
fn add_metric(
    left: [i32; 4],
    right: [i32; 4],
    path: GposArithmetic,
) -> Result<[i32; 4], FontError> {
    if path == GposArithmetic::ScalarReference {
        let mut result = [0; 4];
        for i in 0..4 {
            result[i] = checked(i64::from(left[i]) + i64::from(right[i]))?;
        }
        return Ok(result);
    }
    let a = wide::i32x4::from(left);
    let b = wide::i32x4::from(right);
    let sum = a + b;
    require(((sum ^ a) & !(a ^ b)).to_array().iter().all(|v| *v >= 0))?;
    Ok(sum.to_array())
}
impl Executor<'_, '_, '_, '_> {
    fn data(&self) -> &[u8] {
        self.table.bytes()
    }
    fn check(&self, p: usize, n: usize) -> Result<(), FontError> {
        require(fits(self.data(), p, n))
    }
    fn word(&self, p: usize) -> u16 {
        u16_at(self.data(), p)
    }
    fn covered(&self, p: usize) -> bool {
        self.glyphs[p].glyph_id > 65535
            || self
                .options
                .lookup_coverage
                .is_none_or(|c| c.find(self.glyphs[p].glyph_id as u16).is_some())
    }
    fn may_start(&self, lookup: Lookup<'_>, index: u16, p: usize) -> bool {
        if self.options.lookup_context_subtables.len() != usize::from(lookup.subtable_count()) {
            return true;
        }
        let context = &self.options.lookup_context_subtables[usize::from(index)];
        let first = u64::from(context.coverage_offset) + u64::from(context.backtrack_count);
        if context.input_count == 0
            || first >= self.options.lookup_context_coverages.len() as u64
            || self.glyphs[p].glyph_id > 65535
        {
            return true;
        }
        let c = &self.options.lookup_context_coverages[first as usize];
        let glyph = self.glyphs[p].glyph_id as u16;
        c.contains(glyph)
    }
    fn eligible(&self, p: usize, frame: Frame) -> bool {
        let glyph = self.glyphs[p].glyph_id;
        if glyph > 65535 {
            return false;
        }
        let Some(gdef) = self.options.gdef else {
            return true;
        };
        if frame.flags & 0xff1e == 0 {
            return true;
        }
        match gdef.glyph_class(glyph as u16) {
            GlyphClass::Base if frame.flags & 2 != 0 => false,
            GlyphClass::Ligature if frame.flags & 4 != 0 => false,
            GlyphClass::Mark => {
                if frame.flags & 8 != 0 {
                    false
                } else if frame.flags & 16 != 0 {
                    frame.mark_set != 65535 && gdef.is_in_mark_set(frame.mark_set, glyph as u16)
                } else {
                    frame.flags >> 8 == 0
                        || gdef.mark_attachment_class(glyph as u16) == frame.flags >> 8
                }
            }
            _ => true,
        }
    }
    fn next(&self, start: usize, frame: Frame) -> usize {
        (start..self.glyphs.len())
            .find(|&p| self.eligible(p, frame))
            .unwrap_or(self.glyphs.len())
    }
    fn previous(&self, before: usize, frame: Frame, skip_marks: bool) -> usize {
        (0..before)
            .rev()
            .find(|&p| {
                self.eligible(p, frame)
                    && !(skip_marks
                        && self.options.gdef.is_some_and(|g| {
                            g.glyph_class(self.glyphs[p].glyph_id as u16) == GlyphClass::Mark
                        }))
            })
            .unwrap_or(self.glyphs.len())
    }
    fn positioned(&mut self, p: usize) {
        if let Some(a) = self.attachments.get_mut(p) {
            a.positioned = 1;
        }
    }
    fn dependency(&mut self, first: usize, last: usize) {
        if first >= last || last >= self.glyphs.len() {
            return;
        }
        let minimum = self.glyphs[first..=last]
            .iter()
            .map(|g| g.cluster)
            .min()
            .unwrap();
        for glyph in &mut self.glyphs[first..=last] {
            if glyph.cluster != minimum {
                glyph.flags |= 3;
            }
        }
    }
    fn attachment_admission(&self, kind: u16) -> Result<(), FontError> {
        if (3..=6).contains(&kind) && self.attachments.len() != self.glyphs.len() {
            Err(FontError::InvalidArgument)
        } else {
            Ok(())
        }
    }
    fn lookup(&mut self, index: u16, p: usize, depth: u32) -> Applied {
        if depth >= 64 || p >= self.glyphs.len() {
            return Ok(false);
        }
        let lookup = self
            .table
            .lookup(index)
            .map_err(|_| FontError::InvalidFace)?;
        let frame = Frame::new(lookup, p, depth);
        if !self.eligible(p, frame) {
            return Ok(false);
        }
        self.attachment_admission(lookup.lookup_type())?;
        for index in 0..lookup.subtable_count() {
            if self.subtable(lookup.lookup_type(), lookup.subtable_offset(index)?, frame)? {
                return Ok(true);
            }
        }
        Ok(false)
    }
    fn subtable(&mut self, kind: u16, s: usize, frame: Frame) -> Applied {
        self.attachment_admission(kind)?;
        match kind {
            1 => self.single(s, frame.position),
            2 => self.pair(s, frame),
            3 => self.cursive(s, frame),
            4 | 5 | 6 => self.mark(s, kind, frame),
            7 | 8 => self.context(s, kind == 8, frame),
            9 => {
                self.check(s, 8)?;
                require(self.word(s) == 1)?;
                let kind = self.word(s + 2);
                require(kind != 9)?;
                let at = relative(s, u32_at(self.data(), s + 4))?;
                self.check(at, 2)?;
                self.subtable(kind, at, frame)
            }
            _ => Ok(false),
        }
    }
}
