//! Connected original native simple-Latin design-unit run writer.
use super::*;
use crate::font::HorizontalAdvanceInstance;

/// Frozen native request for the source's explicit printable-ASCII Latin route.
#[derive(Clone, Copy, Debug)]
pub struct NativeLatinRequest<'a> {
    /// Exact OpenType language-system tag, default `dflt`.
    pub language: Tag,
    /// Public feature requests, normalized using the original native policy.
    pub features: &'a [NativeFeature],
    /// Exact normalized instance coordinates, without clamping extra axes.
    pub normalized_coordinates: &'a [i16],
    /// Original one-based default alternate value.
    pub alternate_value: u32,
    /// Original native mark-advance and fallback metadata policy.
    pub zero_mark_advances: bool,
}
impl Default for NativeLatinRequest<'_> {
    fn default() -> Self {
        Self {
            language: Tag::new(*b"dflt"),
            features: &[],
            normalized_coordinates: &[],
            alternate_value: 1,
            zero_mark_advances: true,
        }
    }
}

/// Reusable immutable native font, feature, GDEF and variation generation.
/// This owns the selected font after the caller releases its bytes. All shaping
/// runs remain CPU data; constructing or using this owner never creates a GPU.
#[derive(Clone, Debug)]
pub struct NativeLatinShaper {
    font: SfntFace,
    advance: HorizontalAdvanceInstance,
    features: NativeFeaturePlan,
    gsub: Option<Stage>,
    gpos: Option<Stage>,
    gdef: Option<OwnedGdef>,
    alternate: u32,
    zero_marks: bool,
    has_gpos_kerning: bool,
}
impl NativeLatinShaper {
    /// Capture one font instance and original native feature policy. Present
    /// malformed tables reject; the exact native GDEF length blocklist remains
    /// independent of managed compatibility policy.
    pub fn new(font: SfntFace, request: NativeLatinRequest<'_>) -> Result<Self, FontError> {
        let features = NativeFeaturePlan::new(NativeFeatureRoute::latin(), request.features)?;
        let mut excluded = [Tag(0); 3];
        let mut excluded_count = 0;
        for tag in features.excluded_fractions() {
            excluded[excluded_count] = tag;
            excluded_count += 1;
        }
        let selection = SelectionRequest {
            script: Tag::new(*b"latn"),
            language: request.language,
            features: features.requested(),
            coordinates: request.normalized_coordinates,
        };
        let gsub = Stage::new(&font, GSUB, selection, &excluded[..excluded_count])?;
        let gpos = Stage::new(&font, GPOS, selection, &excluded[..excluded_count])?;
        let length = |tag| font.table(tag).map_or(0, |t| t.bytes.len());
        let gdef = if is_gdef_blocklisted(length(Tag::new(*b"GDEF")), length(GSUB), length(GPOS)) {
            None
        } else {
            OwnedGdef::from_face(font.clone())?
        };
        let has_gpos_kerning = gpos.as_ref().is_some_and(|stage| {
            stage.resolutions.iter().any(|r| {
                r.feature
                    .is_some_and(|tag| tag == KERN || tag == Tag::new(*b"dist"))
            })
        });
        let advance = font.prepare_horizontal_advance_variation(request.normalized_coordinates)?;
        Ok(Self {
            font,
            advance,
            features,
            gsub,
            gpos,
            gdef,
            alternate: request.alternate_value,
            zero_marks: request.zero_mark_advances,
            has_gpos_kerning,
        })
    }
    /// Exact retained selected font generation.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Exact original normalized coordinate request.
    pub fn normalized_coordinates(&self) -> &[i16] {
        self.advance.coordinates()
    }
    /// Original immutable feature request and script planning result.
    pub fn feature_plan(&self) -> &NativeFeaturePlan {
        &self.features
    }
    /// Exact source-declared baseline capacity, three glyph slots per scalar.
    /// Larger GSUB expansions may require the explicit caller-capacity overload.
    pub fn glyph_capacity(input: &[u16]) -> Result<usize, FontError> {
        if input.len() > u32::MAX as usize / 3 || input.iter().any(|&c| !(0x20..=0x7e).contains(&c))
        {
            return Err(FontError::InvalidArgument);
        }
        Ok(input.len() * 3)
    }
    /// Shape one native Latin run using the original declared three-times input
    /// capacity. A larger actual GSUB expansion returns `Capacity`, without retry.
    pub fn shape(&self, input: &[u16]) -> Result<NativePositionedRun, FontError> {
        self.shape_with_capacity(input, Self::glyph_capacity(input)?)
    }
    /// Shape with an explicit glyph capacity. Publication is atomic: a failed
    /// table/metric/expansion never returns a partially positioned owned run.
    pub fn shape_with_capacity(
        &self,
        input: &[u16],
        capacity: usize,
    ) -> Result<NativePositionedRun, FontError> {
        Self::glyph_capacity(input)?;
        if capacity < input.len() || capacity > u32::MAX as usize {
            return Err(FontError::Capacity);
        }
        let mut glyphs = Vec::new();
        glyphs
            .try_reserve_exact(capacity)
            .map_err(|_| FontError::Capacity)?;
        glyphs.resize(capacity, ShapingGlyph::default());
        let mut attachments = Vec::new();
        attachments
            .try_reserve_exact(capacity)
            .map_err(|_| FontError::Capacity)?;
        attachments.resize(capacity, GposAttachment::default());
        let mut states = Vec::new();
        states
            .try_reserve_exact(capacity)
            .map_err(|_| FontError::Capacity)?;
        states.resize(capacity, 0);
        let mut count = 0;
        self.shape_into(
            input,
            &mut glyphs,
            &mut attachments,
            &mut states,
            &mut count,
        )?;
        glyphs.truncate(count as usize);
        Ok(NativePositionedRun {
            font: self.font.clone(),
            coordinates: copy_vec(self.normalized_coordinates())?,
            input: copy_vec(input)?,
            glyphs,
        })
    }
    /// Original caller-storage execution. Whole input/capacities and initial
    /// nominal metrics are admitted before mapping. Later failures preserve the
    /// original partial glyph writes and live count. Tail slots stay caller-owned.
    ///
    /// Input is exactly U+0020 through U+007E, Unicode/layout script `latn`, LTR,
    /// no complex shaper, no verification flag and default grapheme clusters.
    /// Every successful glyph retains its source UTF-16 index and native integer
    /// design-unit metrics; no managed shaping or font-size scaling is involved.
    pub fn shape_into(
        &self,
        input: &[u16],
        storage: &mut [ShapingGlyph],
        attachments: &mut [GposAttachment],
        attachment_states: &mut [u8],
        glyph_count: &mut u32,
    ) -> Result<(), FontError> {
        *glyph_count = 0;
        Self::glyph_capacity(input)?;
        if storage.len() > u32::MAX as usize
            || storage.len() < input.len()
            || attachments.len() < storage.len()
            || attachment_states.len() < storage.len()
        {
            return Err(FontError::Capacity);
        }
        // Original preflight evaluates mapping and advances before writer mutation.
        for &code in input {
            let glyph = self
                .font
                .glyph_index(u32::from(code))
                .ok_or(FontError::InvalidFace)?;
            self.advance.design_advance_width(glyph)?;
        }
        for (index, &code) in input.iter().enumerate() {
            storage[index] = ShapingGlyph {
                glyph_id: u32::from(
                    self.font
                        .glyph_index(u32::from(code))
                        .ok_or(FontError::InvalidFace)?,
                ),
                code_point: u32::from(code),
                cluster: index as i32,
                ..Default::default()
            };
        }
        *glyph_count = input.len() as u32;
        let gdef = self.gdef.as_ref().map(OwnedGdef::view);
        if let Some(stage) = &self.gsub {
            stage.substitute(&self.features, storage, glyph_count, gdef, self.alternate)?;
            self.explicit_fractions(stage, storage, glyph_count, gdef)?;
        }
        // The original default-ignorable pass performs this query even when
        // every source scalar is printable ASCII and its compaction is inert.
        self.font.glyph_index(0x20).ok_or(FontError::InvalidFace)?;
        let count = *glyph_count as usize;
        let glyphs = &mut storage[..count];
        let attachments = &mut attachments[..count];
        for (glyph, attachment) in glyphs.iter_mut().zip(attachments.iter_mut()) {
            *attachment = GposAttachment::default();
            if self.zero_marks {
                attachment.component_count = ((glyph.flags & 0x7f8) >> 3) as u8;
                let encoded = ((glyph.flags & 0x7f800) >> 11) as u8;
                attachment.component = encoded.checked_sub(1).unwrap_or(255);
                glyph.flags &= !0x7fff8;
            }
            let id = u16::try_from(glyph.glyph_id).map_err(|_| FontError::InvalidGlyph)?;
            let width = self.advance.design_advance_width(id)?;
            if !width.is_finite() {
                return Err(FontError::InvalidFace);
            }
            glyph.advance_x = clamped(f64::from(width).round() as i64);
            glyph.advance_y = 0;
            glyph.offset_x = 0;
            glyph.offset_y = 0;
            // U+0020's original fallback can only return unchanged: original
            // and fallback cmap queries address the same code point.
            if glyph.code_point == 0x20 && self.font.glyph_index(0x20).is_none() {
                return Err(FontError::InvalidFace);
            }
        }
        if let Some(stage) = &self.gpos {
            stage.position(
                &self.features,
                glyphs,
                attachments,
                GposOptions {
                    gdef,
                    direction: GposDirection::LeftToRight,
                    font: Some(&self.font),
                    normalized_coordinates: self.normalized_coordinates(),
                    ..Default::default()
                },
            )?;
        }
        if !self.has_gpos_kerning && self.features.run_enabled(KERN) {
            apply_native_legacy_kern(&self.font, glyphs, gdef);
        }
        if self.zero_marks {
            for glyph in glyphs.iter_mut() {
                if gdef.is_some_and(|d| d.glyph_class(glyph.glyph_id as u16) == GlyphClass::Mark) {
                    if self.gpos.is_none() {
                        glyph.offset_x =
                            clamped(i64::from(glyph.offset_x) - i64::from(glyph.advance_x));
                        glyph.offset_y =
                            clamped(i64::from(glyph.offset_y) - i64::from(glyph.advance_y));
                    }
                    glyph.advance_x = 0;
                    glyph.advance_y = 0;
                }
            }
        }
        if self
            .gpos
            .as_ref()
            .is_some_and(|s| s.selection.table().view().lookup_count() != 0)
        {
            resolve_gpos_attachments(
                glyphs,
                attachments,
                GposDirection::LeftToRight,
                attachment_states,
                false,
            )?;
        }
        // Native fallback mark positioning still checks head/units-per-em on
        // mark-free runs. ASCII source code points make its mark loops empty.
        if self.zero_marks
            && self
                .font
                .header_metrics()
                .is_none_or(|h| h.units_per_em == 0)
        {
            return Err(FontError::InvalidFace);
        }
        Ok(())
    }
    fn explicit_fractions(
        &self,
        stage: &Stage,
        storage: &mut [ShapingGlyph],
        count: &mut u32,
        gdef: Option<Gdef<'_>>,
    ) -> Result<(), FontError> {
        if !FRACTIONS
            .iter()
            .any(|tag| self.features.explicit().contains(tag) || self.features.has_settings(*tag))
        {
            return Ok(());
        }
        let table = stage.selection.table().view();
        let request = stage.selection.request();
        // Original fraction pass follows ordinary GSUB, even when an explicit
        // fraction lookup was already selected there. ASCII has no U+2044 auto action.
        for lookup in 0..table.lookup_count() {
            let mut feature = None;
            for tag in FRACTIONS {
                if request.features.contains(&tag)
                    && table.feature_contains_lookup(
                        request.script,
                        request.language,
                        tag,
                        lookup,
                        request.coordinates,
                    )?
                {
                    feature = Some(tag);
                }
            }
            let Some(tag) = feature else {
                continue;
            };
            let mut state = GsubState {
                glyph_count: *count,
                ..Default::default()
            };
            let mut position = 0;
            while position < state.glyph_count {
                let cluster = storage[position as usize].cluster;
                let value = self.features.value(tag, cluster);
                let explicit = self.features.settings().iter().any(|f| {
                    f.tag == tag
                        && f.start <= cluster.max(0) as u32
                        && (cluster.max(0) as u32) < f.end
                }) || !self.features.has_settings(tag)
                    && self.features.explicit().contains(&tag);
                if cluster < 0 || value == 0 || !explicit {
                    position += 1;
                    continue;
                }
                let before = state.glyph_count;
                state.context_match_end = Some(0);
                let result = table.apply_gsub(
                    lookup,
                    Some(position),
                    storage,
                    GsubOptions {
                        gdef,
                        alternate_value: value,
                        track_fallback_mark_metadata: true,
                        track_substitution_provenance: true,
                        ..Default::default()
                    },
                    &mut state,
                );
                *count = state.glyph_count;
                result?;
                position += state.glyph_count.saturating_sub(before);
                position = (position + 1).max(state.context_match_end.unwrap_or(0));
            }
        }
        Ok(())
    }
}

/// Published native positioned run retaining the exact source font and request.
/// Consumers can keep this CPU generation across renderer/device recreation.
#[derive(Clone, Debug)]
pub struct NativePositionedRun {
    font: SfntFace,
    coordinates: Vec<i16>,
    input: Vec<u16>,
    glyphs: Vec<ShapingGlyph>,
}
impl NativePositionedRun {
    /// Original font owner, independent of the shaper's lifetime.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Frozen exact normalized variation coordinates.
    pub fn normalized_coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Original UTF-16 input, whose indices are retained by glyph clusters.
    pub fn input(&self) -> &[u16] {
        &self.input
    }
    /// Fully positioned original native records in output order.
    pub fn glyphs(&self) -> &[ShapingGlyph] {
        &self.glyphs
    }
}
