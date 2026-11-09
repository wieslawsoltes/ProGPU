//! Owned native complex run planning, complete caller-scratch execution and
//! atomic publication of immutable design-unit positioned glyph generations.
use super::*;
use crate::font::HorizontalAdvanceInstance;

/// Original native request for a single explicitly selected complex-script run.
/// Indic and USE retain the supplied original normalization-plan owner.
#[derive(Clone, Copy, Debug)]
pub struct NativeComplexRequest<'a> {
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
    /// Original cluster assignment policy.
    pub cluster_level: NativeClusterLevel,
    /// Original flags; fragment verification belongs to the separate replay API.
    pub flags: NativeBufferFlags,
    /// Actual original normalization-plan generation, required for Indic/USE.
    pub normalization: Option<&'a NormalizationPlan>,
    /// Original decoded pre-context; its presence controls initial dotted circles.
    pub pre_context: &'a [UnicodeScalar],
}
impl Default for NativeComplexRequest<'_> {
    fn default() -> Self {
        Self {
            unicode_script: Tag::new(*b"deva"),
            language: Tag::new(*b"dflt"),
            direction: GposDirection::Unspecified,
            features: &[],
            normalized_coordinates: &[],
            alternate_value: 1,
            zero_mark_advances: true,
            cluster_level: NativeClusterLevel::MonotoneGraphemes,
            flags: NativeBufferFlags::default(),
            normalization: None,
            pre_context: &[],
        }
    }
}
/// Complete native run capacities before glyph mutation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeComplexRequirements {
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
}
/// Reusable caller-owned scratch. Error returns retain the native live glyph
/// prefix and partial writes; a caller can inspect them without publishing a run.
#[derive(Clone, Debug, Default)]
pub struct NativeComplexScratch {
    glyphs: Vec<ShapingGlyph>,
    glyph_count: usize,
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
impl NativeComplexScratch {
    /// Allocate exactly the source-required capacities.
    pub fn new(requirements: NativeComplexRequirements) -> Result<Self, FontError> {
        Self::with_glyph_capacity(requirements, requirements.glyph_capacity)
    }
    /// Retain a larger explicit glyph capacity for font-authored GSUB expansions.
    pub fn with_glyph_capacity(
        r: NativeComplexRequirements,
        capacity: usize,
    ) -> Result<Self, FontError> {
        if capacity < r.glyph_capacity || capacity >= u32::MAX as usize {
            return Err(FontError::Capacity);
        }
        Ok(Self {
            glyphs: filled(capacity, ShapingGlyph::default())?,
            glyph_count: 0,
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
/// Immutable native font/layout/normalization owner for all four complex stage
/// families. Routes requiring independent Arabic joining or fragment replay are
/// rejected explicitly until their separate production families are connected.
#[derive(Clone, Debug)]
pub struct NativeComplexShaper {
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
    level: NativeClusterLevel,
    flags: NativeBufferFlags,
    pre_context: Vec<UnicodeScalar>,
    gpos_kerning: bool,
}
impl NativeComplexShaper {
    /// Capture the exact native route, original feature policies and font instance.
    /// No managed shaper, platform shaping callback or GPU object is used.
    pub fn new(font: SfntFace, request: NativeComplexRequest<'_>) -> Result<Self, FontError> {
        let route = resolve_native_shaping_route(&font, request.unicode_script, request.direction);
        if route.complex == NativeComplexKind::None
            || route.arabic_joining
            || request.flags.contains(NativeBufferFlags::VERIFY)
            || request
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
            level: request.cluster_level,
            flags: request.flags,
            pre_context: copy_vec(request.pre_context)?,
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
    ) -> Result<NativeComplexRequirements, FontError> {
        if source.len() > u32::MAX as usize / 3 {
            return Err(FontError::InvalidArgument);
        }
        let kind = self.route.complex;
        let data = self.normalization();
        let normalization_capacity = if kind != NativeComplexKind::Use
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
        Ok(NativeComplexRequirements {
            glyph_capacity: capacity,
            initial_glyph_count: initial,
            grapheme_capacity,
            normalization_capacity,
            lookup_capacity: self.gsub.as_ref().map_or(0, |s| {
                usize::from(s.selection.table().view().lookup_count())
            }),
        })
    }
    /// Shape and atomically publish one complete native design-unit generation.
    pub fn shape(&self, source: &[UnicodeScalar]) -> Result<NativeComplexRun, FontError> {
        let mut scratch = NativeComplexScratch::new(self.requirements(source)?)?;
        self.shape_into(source, &mut scratch)?;
        scratch.glyphs.truncate(scratch.glyph_count);
        Ok(NativeComplexRun {
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
        scratch: &'a mut NativeComplexScratch,
    ) -> Result<&'a [ShapingGlyph], FontError> {
        scratch.glyph_count = 0;
        let r = self.requirements(source)?;
        if scratch.glyphs.len() < r.glyph_capacity
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
        let NativeComplexScratch {
            glyphs,
            glyph_count,
            categories,
            syllables,
            indices,
            lookups,
            graphemes,
            normalized,
            attachments,
            attachment_states,
        } = scratch;
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
        input::map(
            &self.font,
            shaping_input,
            self.route.complex,
            self.level,
            self.normalization(),
            graphemes,
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
                    self.flags,
                    !self.pre_context.is_empty(),
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
                        khmer::prepare(&self.font, self.flags, &mut b, categories, syllables)?
                    }
                    NativeComplexKind::None => return Err(FontError::InvalidArgument),
                }
                Ok(())
            })();
            if let Err(error) = preparation {
                b.count = 0;
                return Err(error);
            }
            metadata = true;
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
                flags: self.flags,
                gdef,
            };
            context.apply(shaping_input, &mut b, lookups, indices)?;
            input::process_ignorables(&self.font, self.route.direction, self.flags, &mut b)?;
            let count = b.count;
            self.position(
                b.glyphs_mut(),
                &mut attachments[..count],
                attachment_states,
                gdef,
            )?;
            Ok(())
        })();
        if metadata {
            clear_metadata(b.glyphs_mut());
        }
        *glyph_count = b.count;
        result?;
        Ok(&b.storage[..b.count])
    }
    fn position(
        &self,
        glyphs: &mut [ShapingGlyph],
        attachments: &mut [GposAttachment],
        states: &mut [u8],
        gdef: Option<Gdef<'_>>,
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
                && !self
                    .flags
                    .contains(NativeBufferFlags::PRESERVE_DEFAULT_IGNORABLES);
            *a = GposAttachment::default();
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
        if self
            .gpos
            .as_ref()
            .is_some_and(|s| s.selection.table().view().lookup_count() != 0)
        {
            resolve_gpos_attachments(glyphs, attachments, direction, states, false)?;
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
/// Published native complex-script result retaining the exact font and original
/// scalar-source coordinates independently of shaping scratch or GPU lifetime.
#[derive(Clone, Debug)]
pub struct NativeComplexRun {
    font: SfntFace,
    route: NativeShapingRoute,
    coordinates: Vec<i16>,
    source: Vec<UnicodeScalar>,
    glyphs: Vec<ShapingGlyph>,
}
impl NativeComplexRun {
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
