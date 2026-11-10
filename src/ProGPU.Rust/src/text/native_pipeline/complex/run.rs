//! Owned native unified run planning, explicit caller-scratch execution and
//! atomic publication of immutable design-unit positioned glyph generations.
use super::*;
use crate::font::HorizontalAdvanceInstance;
#[path = "verify.rs"]
mod verify;
pub use verify::{NativeDeviceFragmentSource, NativeFragmentContext};

/// Original native request for one font/script/direction run.
/// Indic and USE retain the supplied original normalization-plan owner.
#[derive(Clone, Copy, Debug)]
pub struct NativeShapeRequest<'a> {
    /// Source Unicode script; font-dependent native layout generations are resolved.
    pub unicode_script: Tag,
    /// Exact OpenType language-system tag.
    pub language: Tag,
    /// Original direction, including automatic script direction when unspecified.
    pub direction: GposDirection,
    /// Original public feature requests and source-input ranges.
    pub features: &'a [NativeFeature],
    /// Exact original normalized variable-font request.
    pub normalized_coordinates: &'a [i16],
    /// One-based default alternate value.
    pub alternate_value: u32,
    /// Original mark advance zeroing policy.
    pub zero_mark_advances: bool,
    /// Explicit original native design-unit fallback extent source.
    pub fallback_bounds: NativeFallbackBounds,
    /// Original cluster assignment policy.
    pub cluster_level: NativeClusterLevel,
    /// Original flags, including bounded safe-boundary fragment verification.
    pub flags: NativeBufferFlags,
    /// Actual original normalization-plan generation, required for Indic/USE.
    pub normalization: Option<&'a NormalizationPlan>,
    /// Original decoded pre-context; its presence controls initial dotted circles.
    pub pre_context: &'a [UnicodeScalar],
    /// Original decoded post-context for bounded joining decisions.
    pub post_context: &'a [UnicodeScalar],
}
impl Default for NativeShapeRequest<'_> {
    fn default() -> Self {
        Self {
            unicode_script: Tag::new(*b"latn"),
            language: Tag::new(*b"dflt"),
            direction: GposDirection::Unspecified,
            features: &[],
            normalized_coordinates: &[],
            alternate_value: 1,
            zero_mark_advances: true,
            fallback_bounds: NativeFallbackBounds::Outline,
            cluster_level: NativeClusterLevel::MonotoneGraphemes,
            flags: NativeBufferFlags::default(),
            normalization: None,
            pre_context: &[],
            post_context: &[],
        }
    }
}
/// Complete native run capacities before glyph mutation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeShapeRequirements {
    /// Source-declared glyph capacity including preprocessing expansion.
    pub glyph_capacity: usize,
    /// Exact initial source mapping count before composition/selector folding.
    pub initial_glyph_count: usize,
    /// Original source grapheme capacity, with Indic/Myanmar scalar upper bound.
    pub grapheme_capacity: usize,
    /// Full canonical decomposition capacity when normalization is required.
    pub normalization_capacity: usize,
    /// Complete native GSUB lookup-selection scratch inventory.
    pub lookup_capacity: usize,
    /// Original input/normalization joining action inventory.
    pub script_action_capacity: usize,
    /// Source glyph slots available to the per-run outline-bound cache.
    pub bounds_capacity: usize,
    /// Independent original fragment glyph inventory for monotone Verify runs.
    pub verification_glyph_capacity: usize,
}
/// Reusable caller-owned scratch. Error returns retain the native live glyph
/// prefix and partial writes; a caller can inspect them without publishing a run.
#[derive(Clone, Debug, Default)]
pub struct NativeShapeScratch {
    glyphs: Vec<ShapingGlyph>,
    glyph_count: usize,
    verification_glyphs: Vec<ShapingGlyph>,
    actions: Vec<u8>,
    joining_flags: Vec<u32>,
    stretch_runs: Vec<stretch::Run>,
    bounds: Vec<fallback_marks::Cached>,
    bounds_generation: u64,
    categories: Vec<u8>,
    syllables: Vec<u8>,
    indices: Vec<u32>,
    lookups: Vec<u16>,
    graphemes: Vec<GraphemeCluster>,
    normalized: Vec<UnicodeScalar>,
    attachments: Vec<GposAttachment>,
    attachment_states: Vec<u8>,
}
fn filled<T: Clone>(n: usize, value: T) -> Result<Vec<T>, FontError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(n)
        .map_err(|_| FontError::Capacity)?;
    output.resize(n, value);
    Ok(output)
}
impl NativeShapeScratch {
    /// Allocate exactly the source-required capacities.
    pub fn new(requirements: NativeShapeRequirements) -> Result<Self, FontError> {
        Self::with_glyph_capacity(requirements, requirements.glyph_capacity)
    }
    /// Retain a larger explicit glyph capacity for font-authored GSUB expansions.
    pub fn with_glyph_capacity(
        r: NativeShapeRequirements,
        capacity: usize,
    ) -> Result<Self, FontError> {
        if capacity < r.glyph_capacity || capacity >= u32::MAX as usize {
            return Err(FontError::Capacity);
        }
        Ok(Self {
            glyphs: filled(capacity, ShapingGlyph::default())?,
            glyph_count: 0,
            verification_glyphs: filled(r.verification_glyph_capacity, ShapingGlyph::default())?,
            actions: filled(r.script_action_capacity, 7)?,
            joining_flags: filled(r.script_action_capacity, 0)?,
            stretch_runs: filled(capacity, stretch::Run::default())?,
            bounds: filled(r.bounds_capacity, fallback_marks::Cached::default())?,
            bounds_generation: 0,
            categories: filled(capacity, 0)?,
            syllables: filled(capacity, 0)?,
            indices: filled(capacity + 1, 0)?,
            lookups: filled(r.lookup_capacity, 0)?,
            graphemes: filled(r.grapheme_capacity, GraphemeCluster::default())?,
            normalized: filled(r.normalization_capacity, UnicodeScalar::default())?,
            attachments: filled(capacity, GposAttachment::default())?,
            attachment_states: filled(capacity, 0)?,
        })
    }
    /// Actual live native prefix, including original partial output on failure.
    pub fn glyphs(&self) -> &[ShapingGlyph] {
        &self.glyphs[..self.glyph_count]
    }
    /// Original writable glyph inventory available to the native writer.
    pub fn glyph_capacity(&self) -> usize {
        self.glyphs.len()
    }
}
/// Immutable original native font/layout/normalization owner for ordinary,
/// joining, Hangul and all four complex stage families. Verification reshapes
/// original safe fragments with independent bounded output storage.
#[derive(Clone, Debug)]
pub struct NativeShaper {
    font: SfntFace,
    route: NativeShapingRoute,
    features: NativeFeaturePlan,
    advance: HorizontalAdvanceInstance,
    gsub: Option<Stage>,
    gpos: Option<Stage>,
    gdef: Option<OwnedGdef>,
    normalization: Option<NormalizationPlan>,
    language: Tag,
    alternate: u32,
    zero_marks: bool,
    fallback_bounds: NativeFallbackBounds,
    level: NativeClusterLevel,
    flags: NativeBufferFlags,
    pre_context: Vec<UnicodeScalar>,
    post_context: Vec<UnicodeScalar>,
    gpos_kerning: bool,
}
impl NativeShaper {
    /// Capture the exact native route, original feature policies and font instance.
    /// No managed shaper, platform shaping callback or GPU object is used.
    pub fn new(font: SfntFace, request: NativeShapeRequest<'_>) -> Result<Self, FontError> {
        let route = resolve_native_shaping_route(&font, request.unicode_script, request.direction);
        if request
            .flags
            .contains(NativeBufferFlags::PRESERVE_DEFAULT_IGNORABLES)
            && request
                .flags
                .contains(NativeBufferFlags::REMOVE_DEFAULT_IGNORABLES)
            || matches!(
                route.complex,
                NativeComplexKind::Indic | NativeComplexKind::Use
            ) && request.normalization.is_none()
        {
            return Err(FontError::InvalidArgument);
        }
        let features = NativeFeaturePlan::new(route.feature_route(), request.features)?;
        let mut excluded = [Tag(0); 3];
        let mut n = 0;
        for tag in features.excluded_fractions() {
            excluded[n] = tag;
            n += 1;
        }
        let selection = SelectionRequest {
            script: route.layout_script,
            language: request.language,
            features: features.requested(),
            coordinates: request.normalized_coordinates,
        };
        let gsub = Stage::new(&font, GSUB, selection, &excluded[..n])?;
        let gpos = Stage::new(&font, GPOS, selection, &excluded[..n])?;
        let len = |tag| font.table(tag).map_or(0, |t| t.bytes.len());
        let gdef = if is_gdef_blocklisted(len(Tag::new(*b"GDEF")), len(GSUB), len(GPOS)) {
            None
        } else {
            OwnedGdef::from_face(font.clone())?
        };
        let gpos_kerning = gpos.as_ref().is_some_and(|s| {
            s.resolutions.iter().any(|r| {
                r.feature
                    .is_some_and(|t| t == KERN || t == Tag::new(*b"dist"))
            })
        });
        let advance = font.prepare_horizontal_advance_variation(request.normalized_coordinates)?;
        Ok(Self {
            font,
            route,
            features,
            advance,
            gsub,
            gpos,
            gdef,
            normalization: request.normalization.cloned(),
            language: request.language,
            alternate: request.alternate_value,
            zero_marks: request.zero_mark_advances,
            fallback_bounds: request.fallback_bounds,
            level: request.cluster_level,
            flags: request.flags,
            pre_context: copy_vec(request.pre_context)?,
            post_context: copy_vec(request.post_context)?,
            gpos_kerning,
        })
    }
    /// Exact source font generation, independent of caller byte ownership.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Resolved original native script/direction policy.
    pub fn route(&self) -> NativeShapingRoute {
        self.route
    }
    /// Frozen original OpenType language-system tag.
    pub fn language(&self) -> Tag {
        self.language
    }
    /// Frozen original native feature request and range policy.
    pub fn feature_plan(&self) -> &NativeFeaturePlan {
        &self.features
    }
    /// Exact normalized variation coordinates.
    pub fn normalized_coordinates(&self) -> &[i16] {
        self.advance.coordinates()
    }
    fn normalization(&self) -> Option<NormalizationData<'_>> {
        self.normalization.as_ref().map(NormalizationPlan::data)
    }
    /// Original run requirements, including font-dependent initial decomposition
    /// and original composed-input/USE expansion maxima. No glyph writes occur.
    pub fn requirements(
        &self,
        source: &[UnicodeScalar],
    ) -> Result<NativeShapeRequirements, FontError> {
        if source.len() > u32::MAX as usize / 3 {
            return Err(FontError::InvalidArgument);
        }
        let kind = self.route.complex;
        let data = self.normalization();
        let normalization_capacity = if self.route.unicode_script != Tag::new(*b"hang")
            && kind != NativeComplexKind::Use
            && !source.iter().all(|s| (0x20..=0x7e).contains(&s.code_point))
            && data.is_some_and(|d| input::needs_composition(source, d))
        {
            unicode::normalization::scalar_capacity(source, &data.unwrap())
                .map_err(input::unicode_error)? as usize
        } else {
            0
        };
        let initial = input::mapped_count(&self.font, source, kind, data)?;
        let expanded = initial
            .checked_add(source.len())
            .filter(|&n| n < u32::MAX as usize)
            .ok_or(FontError::InvalidArgument)?;
        let mut capacity = (source.len() * 3).max(expanded);
        if normalization_capacity != 0 {
            capacity = capacity.max(
                normalization_capacity
                    .checked_add(source.len())
                    .filter(|&n| n <= u32::MAX as usize)
                    .ok_or(FontError::InvalidArgument)?,
            );
        }
        if kind == NativeComplexKind::Use {
            capacity = capacity.max(
                input::use_count(source, data.ok_or(FontError::InvalidArgument)?)?
                    .checked_add(source.len())
                    .filter(|&n| n < u32::MAX as usize)
                    .ok_or(FontError::InvalidArgument)?,
            );
        }
        let grapheme_capacity =
            if matches!(kind, NativeComplexKind::Indic | NativeComplexKind::Myanmar) {
                source.len()
            } else {
                grapheme::cluster_count(source, GraphemePolicy::Unicode17)
                    .map_err(input::unicode_error)? as usize
            };
        Ok(NativeShapeRequirements {
            glyph_capacity: capacity,
            verification_glyph_capacity: if self.flags.contains(NativeBufferFlags::VERIFY)
                && matches!(
                    self.level,
                    NativeClusterLevel::MonotoneGraphemes | NativeClusterLevel::MonotoneCharacters
                ) {
                capacity
            } else {
                0
            },
            initial_glyph_count: initial,
            grapheme_capacity,
            normalization_capacity,
            script_action_capacity: source.len().max(normalization_capacity),
            bounds_capacity: if self.fallback_mark_positioning()
                && self.fallback_bounds == NativeFallbackBounds::Outline
            {
                usize::from(self.font.glyph_count().ok_or(FontError::InvalidFace)?)
            } else {
                0
            },
            lookup_capacity: self.gsub.as_ref().map_or(0, |s| {
                usize::from(s.selection.table().view().lookup_count())
            }),
        })
    }
    /// Shape and atomically publish one complete native design-unit generation.
    pub fn shape(&self, source: &[UnicodeScalar]) -> Result<NativeRun, FontError> {
        let mut scratch = NativeShapeScratch::new(self.requirements(source)?)?;
        self.shape_into(source, &mut scratch)?;
        scratch.glyphs.truncate(scratch.glyph_count);
        Ok(NativeRun {
            font: self.font.clone(),
            route: self.route,
            coordinates: copy_vec(self.normalized_coordinates())?,
            source: copy_vec(source)?,
            glyphs: scratch.glyphs,
        })
    }
    /// Execute against reusable caller scratch. Native errors retain their exact
    /// earlier mutations; private complex metadata is cleared on the same exit
    /// boundary as the original guard. Scratch is not an implicitly published run.
    pub fn shape_into<'a>(
        &self,
        source: &[UnicodeScalar],
        scratch: &'a mut NativeShapeScratch,
    ) -> Result<&'a [ShapingGlyph], FontError> {
        scratch.glyph_count = 0;
        let required = self.requirements(source)?;
        if scratch.verification_glyphs.len() < required.verification_glyph_capacity {
            return Err(FontError::Capacity);
        }
        let glyph_limit = scratch.glyphs.len();
        self.shape_unverified(
            source,
            scratch,
            self.flags,
            &self.pre_context,
            &self.post_context,
            glyph_limit,
        )?;
        if required.verification_glyph_capacity != 0 {
            let expected_count = scratch.glyph_count;
            let expected = std::mem::replace(
                &mut scratch.glyphs,
                std::mem::take(&mut scratch.verification_glyphs),
            );
            let result = self.verify(source, &expected[..expected_count], scratch);
            scratch.verification_glyphs = std::mem::replace(&mut scratch.glyphs, expected);
            scratch.glyph_count = if result.is_ok() { expected_count } else { 0 };
            result?;
        }
        Ok(scratch.glyphs())
    }
    fn shape_unverified(
        &self,
        source: &[UnicodeScalar],
        scratch: &mut NativeShapeScratch,
        flags: NativeBufferFlags,
        pre_context: &[UnicodeScalar],
        post_context: &[UnicodeScalar],
        glyph_limit: usize,
    ) -> Result<(), FontError> {
        scratch.glyph_count = 0;
        let r = self.requirements(source)?;
        if glyph_limit > scratch.glyphs.len()
            || glyph_limit < r.glyph_capacity
            || self.route.arabic_joining
                && (scratch.actions.len() < r.script_action_capacity
                    || scratch.joining_flags.len() < r.script_action_capacity)
            || scratch.bounds.len() < r.bounds_capacity
            || scratch.stretch_runs.len() < scratch.glyphs.len()
            || scratch.categories.len() < r.glyph_capacity
            || scratch.syllables.len() < r.glyph_capacity
            || scratch.indices.len() <= r.glyph_capacity
            || scratch.lookups.len() < r.lookup_capacity
            || scratch.graphemes.len() < r.grapheme_capacity
            || scratch.normalized.len() < r.normalization_capacity
            || scratch.attachments.len() < scratch.glyphs.len()
            || scratch.attachment_states.len() < scratch.glyphs.len()
        {
            return Err(FontError::Capacity);
        }
        scratch.bounds_generation = scratch.bounds_generation.wrapping_add(1);
        if scratch.bounds_generation == 0 {
            scratch.bounds.fill(fallback_marks::Cached::default());
            scratch.bounds_generation = 1;
        }
        let NativeShapeScratch {
            glyphs,
            glyph_count,
            verification_glyphs: _,
            actions,
            joining_flags,
            stretch_runs,
            bounds,
            bounds_generation,
            categories,
            syllables,
            indices,
            lookups,
            graphemes,
            normalized,
            attachments,
            attachment_states,
        } = scratch;
        let glyphs = &mut glyphs[..glyph_limit];
        let shaping_input = if r.normalization_capacity != 0 {
            let n = input::normalize(
                source,
                self.route.complex,
                self.normalization().ok_or(FontError::InvalidArgument)?,
                graphemes,
                normalized,
            )?;
            &normalized[..n]
        } else {
            source
        };
        let mut count = 0usize;
        for scalar in shaping_input {
            if scalar.input_index > i32::MAX as u32 {
                return Err(FontError::InvalidArgument);
            }
            let mapping = input::mapping(
                &self.font,
                scalar.code_point,
                self.route.complex,
                self.normalization(),
            )?;
            count = count
                .checked_add(mapping.len())
                .ok_or(FontError::InvalidArgument)?;
            for i in 0..mapping.len() {
                let cp = mapping.at(i);
                let id = self.font.glyph_index(cp).ok_or(FontError::InvalidFace)?;
                self.advance
                    .design_advance_width(spaces::map(&self.font, cp, id)?)?;
            }
        }
        if count > glyphs.len() {
            return Err(FontError::Capacity);
        }
        let mut b = Buffer {
            storage: glyphs,
            count: 0,
        };
        if self.route.arabic_joining {
            let count =
                grapheme::segment_into(shaping_input, input::policy(self.route.complex), graphemes)
                    .map_err(input::unicode_error)? as usize;
            joining::assign(
                shaping_input,
                &graphemes[..count],
                pre_context,
                post_context,
                flags,
                actions,
                joining_flags,
            )?;
        }
        input::map(
            &self.font,
            shaping_input,
            self.route.complex,
            self.level,
            self.normalization(),
            graphemes,
            self.route
                .arabic_joining
                .then_some((&actions[..], &joining_flags[..])),
            &mut b,
        )?;
        let mut metadata = false;
        let result = (|| {
            let preparation = (|| {
                let mut vertical_substitution = false;
                if matches!(
                    self.route.direction,
                    GposDirection::TopToBottom | GposDirection::BottomToTop
                ) && let Some(stage) = &self.gsub
                {
                    for tag in [Tag::new(*b"vert"), Tag::new(*b"vrt2")] {
                        if self.features.run_enabled(tag) {
                            vertical_substitution |=
                                stage.selection.table().view().select_feature_into(
                                    self.route.layout_script,
                                    self.language,
                                    tag,
                                    self.normalized_coordinates(),
                                    lookups,
                                )? != 0;
                        }
                    }
                }
                input::directional(
                    &self.font,
                    b.glyphs_mut(),
                    self.route.direction,
                    vertical_substitution,
                )?;
                input::preprocess(
                    &self.font,
                    self.route.unicode_script,
                    self.route.complex,
                    self.level,
                    flags,
                    !pre_context.is_empty(),
                    self.route.arabic_joining,
                    self.route.compose_hebrew,
                    self.normalization(),
                    &mut b,
                )?;
                match self.route.complex {
                    NativeComplexKind::Indic => indic::prepare(&mut b, categories, syllables)?,
                    NativeComplexKind::Use => {
                        use_shaper::prepare(&mut b, categories, syllables, indices)?
                    }
                    NativeComplexKind::Myanmar => myanmar::prepare(&mut b, categories, syllables)?,
                    NativeComplexKind::Khmer => {
                        khmer::prepare(&self.font, flags, &mut b, categories, syllables)?
                    }
                    NativeComplexKind::None => (),
                }
                Ok(())
            })();
            if let Err(error) = preparation {
                b.count = 0;
                return Err(error);
            }
            metadata = self.route.complex != NativeComplexKind::None;
            let gdef = self.gdef.as_ref().map(OwnedGdef::view);
            let context = stages::Context {
                font: &self.font,
                table: self.gsub.as_ref().map(|s| s.selection.table().view()),
                selection: SelectionRequest {
                    script: self.route.layout_script,
                    language: self.language,
                    features: self.features.requested(),
                    coordinates: self.normalized_coordinates(),
                },
                plan: &self.features,
                route: self.route,
                alternate: self.alternate,
                flags: flags,
                gdef,
                random: std::cell::Cell::new(1),
                zero_mark_advances: self.zero_marks,
            };
            context.apply_run(shaping_input, &mut b, lookups, indices)?;
            input::process_ignorables(&self.font, self.route.direction, flags, &mut b)?;
            let count = b.count;
            self.position(
                b.glyphs_mut(),
                &mut attachments[..count],
                attachment_states,
                gdef,
                bounds,
                *bounds_generation,
                flags,
            )?;
            if self.route.arabic_joining {
                let stretched = stretch::apply(
                    &self.advance,
                    &mut b,
                    self.route.direction == GposDirection::RightToLeft,
                    stretch_runs,
                );
                joining::clear(b.glyphs_mut());
                stretched?;
            }
            Ok(())
        })();
        if metadata {
            clear_metadata(b.glyphs_mut());
        }
        *glyph_count = b.count;
        result?;
        Ok(())
    }
    fn fallback_mark_positioning(&self) -> bool {
        self.zero_marks
            && self.route.complex == NativeComplexKind::None
            && ![*b"thai", *b"lao ", *b"mymr", *b"qaag"]
                .contains(&self.route.unicode_script.bytes())
    }
    fn position(
        &self,
        glyphs: &mut [ShapingGlyph],
        attachments: &mut [GposAttachment],
        states: &mut [u8],
        gdef: Option<Gdef<'_>>,
        bounds: &mut [fallback_marks::Cached],
        generation: u64,
        flags: NativeBufferFlags,
    ) -> Result<(), FontError> {
        let direction = self.route.direction;
        let vertical = matches!(
            direction,
            GposDirection::TopToBottom | GposDirection::BottomToTop
        );
        let early_zero = self.zero_marks
            && matches!(
                self.route.complex,
                NativeComplexKind::Use | NativeComplexKind::Myanmar
            );
        for (g, a) in glyphs.iter_mut().zip(attachments.iter_mut()) {
            let zero_ignorable = input::default_ignorable(g.code_point)
                && g.advance_x != i32::MIN
                && !flags.contains(NativeBufferFlags::PRESERVE_DEFAULT_IGNORABLES);
            *a = GposAttachment::default();
            if self.fallback_mark_positioning() {
                a.component_count = ((g.flags & 0x7f8) >> 3) as u8;
                let encoded = ((g.flags & 0x7f800) >> 11) as u8;
                a.component = if encoded == 0 { 255 } else { encoded - 1 };
                g.flags &= !0x7fff8;
            }
            let id = u16::try_from(g.glyph_id).map_err(|_| FontError::InvalidGlyph)?;
            let width = self.advance.design_advance_width(id)?;
            if !width.is_finite() {
                return Err(FontError::InvalidFace);
            }
            if vertical {
                g.advance_x = 0;
                g.advance_y = clamped(-i64::from(
                    self.font
                        .design_advance_height(id)
                        .ok_or(FontError::InvalidFace)?,
                ));
                g.offset_x = clamped(-(f64::from(width).round_ties_even() as i64 / 2));
                g.offset_y = clamped(-i64::from(
                    self.font
                        .design_vertical_origin_y(id)
                        .ok_or(FontError::InvalidFace)?,
                ));
            } else {
                g.advance_x = clamped(f64::from(width).round() as i64);
                g.advance_y = 0;
                g.offset_x = 0;
                g.offset_y = 0;
            }
            spaces::apply(&self.font, direction, self.normalized_coordinates(), g)?;
            let class = gdef.map_or(GlyphClass::Unclassified, |d| d.glyph_class(id));
            let mark = if class != GlyphClass::Unclassified {
                class == GlyphClass::Mark
            } else {
                !input::default_ignorable(g.code_point) && unicode_mark(g.code_point)
            };
            if early_zero && mark {
                if self.gpos.is_none()
                    && matches!(
                        direction,
                        GposDirection::LeftToRight | GposDirection::TopToBottom
                    )
                {
                    g.offset_x = clamped(i64::from(g.offset_x) - i64::from(g.advance_x));
                    g.offset_y = clamped(i64::from(g.offset_y) - i64::from(g.advance_y));
                }
                g.advance_x = 0;
                g.advance_y = 0;
            }
            if zero_ignorable {
                g.advance_x = 0;
                g.advance_y = 0;
            }
        }
        if let Some(stage) = &self.gpos {
            stage.position(
                &self.features,
                glyphs,
                attachments,
                GposOptions {
                    gdef,
                    direction,
                    font: Some(&self.font),
                    normalized_coordinates: self.normalized_coordinates(),
                    ..Default::default()
                },
            )?;
        }
        if !self.gpos_kerning
            && self.features.run_enabled(KERN)
            && self.route.complex != NativeComplexKind::Indic
            && matches!(
                direction,
                GposDirection::LeftToRight | GposDirection::RightToLeft
            )
        {
            apply_native_legacy_kern(&self.font, glyphs, gdef);
        }
        let late_zero = self.zero_marks
            && (self.fallback_mark_positioning()
                || [*b"thai", *b"lao "].contains(&self.route.unicode_script.bytes()));
        if late_zero {
            let adjust = self.gpos.is_none()
                && matches!(
                    direction,
                    GposDirection::LeftToRight | GposDirection::TopToBottom
                );
            for g in glyphs.iter_mut() {
                let class = gdef.map_or(GlyphClass::Unclassified, |d| {
                    d.glyph_class(g.glyph_id as u16)
                });
                let mark = if class != GlyphClass::Unclassified {
                    class == GlyphClass::Mark
                } else {
                    !input::default_ignorable(g.code_point) && unicode_mark(g.code_point)
                };
                if !mark {
                    continue;
                }
                if adjust {
                    g.offset_x = clamped(i64::from(g.offset_x) - i64::from(g.advance_x));
                    g.offset_y = clamped(i64::from(g.offset_y) - i64::from(g.advance_y));
                }
                g.advance_x = 0;
                g.advance_y = 0;
            }
        }
        if self
            .gpos
            .as_ref()
            .is_some_and(|s| s.selection.table().view().lookup_count() != 0)
        {
            resolve_gpos_attachments(glyphs, attachments, direction, states, false)?;
        }
        if self.fallback_mark_positioning() {
            fallback_marks::apply(
                &self.font,
                &self.advance,
                glyphs,
                direction,
                attachments,
                self.fallback_bounds,
                bounds,
                generation,
            )?;
        }
        if matches!(
            direction,
            GposDirection::RightToLeft | GposDirection::BottomToTop
        ) {
            glyphs.reverse();
            if self.level == NativeClusterLevel::MonotoneCharacters {
                let mut start = 0;
                while start < glyphs.len() {
                    while start < glyphs.len()
                        && input::modified_class(glyphs[start].code_point) == 0
                    {
                        start += 1;
                    }
                    let mut end = start;
                    while end < glyphs.len() && input::modified_class(glyphs[end].code_point) != 0 {
                        end += 1;
                    }
                    let mut left = start;
                    let mut right = end.saturating_sub(1);
                    while left < right {
                        let cluster = glyphs[left].cluster;
                        glyphs[left].cluster = glyphs[right].cluster;
                        glyphs[right].cluster = cluster;
                        left += 1;
                        right -= 1;
                    }
                    start = end;
                }
            }
        }
        Ok(())
    }
}
/// Published native positioned result retaining the exact font and original
/// scalar-source coordinates independently of shaping scratch or GPU lifetime.
#[derive(Clone, Debug)]
pub struct NativeRun {
    font: SfntFace,
    route: NativeShapingRoute,
    coordinates: Vec<i16>,
    source: Vec<UnicodeScalar>,
    glyphs: Vec<ShapingGlyph>,
}
impl NativeRun {
    /// Exact selected font owner.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Original resolved script/direction metadata.
    pub fn route(&self) -> NativeShapingRoute {
        self.route
    }
    /// Frozen normalized variation instance.
    pub fn normalized_coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Original decoded source inventory before normalization and shaping.
    pub fn source(&self) -> &[UnicodeScalar] {
        &self.source
    }
    /// Native positioned output with private complex metadata retired.
    pub fn glyphs(&self) -> &[ShapingGlyph] {
        &self.glyphs
    }
}
