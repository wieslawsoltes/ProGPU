//! Original bounded single/pair hinted positioning capture and immutable slicing.
use super::*;
use crate::font::layout::{GlyphSetDigest, Lookup};

/// Immutable original pre-positioning generation and fully admitted lookup recipe.
/// Only original single/pair families, including completely checked extensions,
/// are replayable. Negative coverage digests can exclude unrelated lookups.
#[derive(Debug)]
pub struct NativeDevicePositioningRecipe {
    prepared: Vec<ShapingGlyph>,
    features: NativeFeaturePlan,
    coordinates: Vec<i16>,
    lookups: Vec<(u16, Resolution)>,
    direction: GposDirection,
    projection: NativeHintedProjection,
    legacy_kerning: bool,
}
fn single_or_pair(lookup: Lookup<'_>) -> Result<bool, FontError> {
    if lookup.lookup_type() != 9 {
        return Ok(matches!(lookup.lookup_type(), 1 | 2));
    }
    let data = lookup.bytes();
    let read = |at: usize| u16::from_be_bytes([data[at], data[at + 1]]);
    let mut supported = true;
    for i in 0..lookup.subtable_count() {
        let at = lookup.subtable_offset(i)?;
        if at > data.len() || data.len() - at < 8 || read(at) != 1 {
            return Err(FontError::InvalidFace);
        }
        let kind = read(at + 2);
        let relative = (u32::from(read(at + 4)) << 16) | u32::from(read(at + 6));
        let relative = relative as usize;
        if relative == 0 || relative > data.len() - at || data.len() - at - relative < 2 {
            return Err(FontError::InvalidFace);
        }
        supported &= matches!(kind, 1 | 2);
    }
    Ok(supported)
}
impl NativeDevicePositioningRecipe {
    pub(super) fn capture(
        shaper: &NativeShaper,
        prepared: &[ShapingGlyph],
        projection: NativeHintedProjection,
    ) -> Result<Option<Self>, FontError> {
        if prepared.len() > u32::MAX as usize
            || shaper.route.complex != NativeComplexKind::None
            || shaper.level != NativeClusterLevel::MonotoneGraphemes
            || !matches!(
                shaper.route.direction,
                GposDirection::LeftToRight | GposDirection::RightToLeft
            )
            || !prepared_identity(prepared)
        {
            return Ok(None);
        }
        let mut digest = GlyphSetDigest::default();
        for glyph in prepared {
            digest.add(glyph.glyph_id as u16);
        }
        let mut lookups = Vec::new();
        let mut has_gpos_kerning = false;
        if let Some(stage) = &shaper.gpos {
            let table = stage.selection.table().view();
            lookups
                .try_reserve_exact(stage.selection.lookups().len())
                .map_err(|_| FontError::Capacity)?;
            for (&index, &resolution) in stage.selection.lookups().iter().zip(&stage.resolutions) {
                has_gpos_kerning |= resolution
                    .feature
                    .is_some_and(|tag| tag == KERN || tag == Tag::new(*b"dist"));
                if table
                    .lookup_digest(index, 9)?
                    .is_some_and(|d| !d.may_intersect(&digest))
                {
                    continue;
                }
                if !single_or_pair(table.lookup(index)?)? {
                    return Ok(None);
                }
                lookups.push((index, resolution));
            }
        }
        Ok(Some(Self {
            prepared: copy_vec(prepared)?,
            features: shaper.features.try_copy()?,
            coordinates: copy_vec(shaper.normalized_coordinates())?,
            lookups,
            direction: shaper.route.direction,
            projection,
            legacy_kerning: !has_gpos_kerning && shaper.features.run_enabled(KERN),
        }))
    }
    /// Exact original logical-order pre-positioning records and dependency flags.
    pub fn prepared_glyphs(&self) -> &[ShapingGlyph] {
        &self.prepared
    }
    /// Original owned feature policy, including ranged values.
    pub fn feature_plan(&self) -> &NativeFeaturePlan {
        &self.features
    }
    /// Complete admitted original lookup index order.
    pub fn lookup_indices(&self) -> impl ExactSizeIterator<Item = u16> + '_ {
        self.lookups.iter().map(|&(index, _)| index)
    }
    /// Original legacy-kerning decision made before lookup digest pruning.
    pub fn uses_legacy_kerning(&self) -> bool {
        self.legacy_kerning
    }
}
/// Owned recomposed slice retaining its exact original whole run/capture. Only
/// placement can differ for a smaller safe slice; original records stay immutable.
#[derive(Debug)]
pub struct NativeDevicePositionedSlice {
    original: Arc<NativeDeviceRun>,
    prepared_start: u32,
    prepared_count: u32,
    glyphs: Vec<ShapingGlyph>,
    original_indices: Vec<u32>,
}
impl NativeDevicePositionedSlice {
    /// Actual whole-run generation retained beyond provider/context retirement.
    pub fn original(&self) -> &Arc<NativeDeviceRun> {
        &self.original
    }
    /// Exact logical prepared range used to replay this slice.
    pub fn prepared_range(&self) -> std::ops::Range<u32> {
        self.prepared_start..self.prepared_start + self.prepared_count
    }
    /// Native device positions recomposed over only this complete safe slice.
    pub fn glyphs(&self) -> &[ShapingGlyph] {
        &self.glyphs
    }
    /// Exact occurrence indices into the original final run.
    pub fn original_glyph_indices(&self) -> &[u32] {
        &self.original_indices
    }
}
fn safe_edge(glyphs: &[ShapingGlyph], edge: usize) -> bool {
    edge == 0
        || edge == glyphs.len()
        || edge < glyphs.len()
            && glyphs[edge - 1].cluster != glyphs[edge].cluster
            && glyphs[edge].flags & 1 == 0
}
impl NativeDeviceRun {
    /// Replay an original admitted single/pair recipe over complete safe cluster
    /// edges, retaining the source generation. Whole-run replay must reproduce
    /// all original fields exactly or return VerificationFailed before publication.
    pub fn recompose_positioning(
        self: &Arc<Self>,
        prepared_start: u32,
        prepared_count: u32,
    ) -> Result<Arc<NativeDevicePositionedSlice>, FontError> {
        let recipe = self
            .positioning
            .as_ref()
            .ok_or(FontError::InvalidArgument)?;
        let count = recipe.prepared.len();
        let start = prepared_start as usize;
        let length = prepared_count as usize;
        if length == 0
            || count != self.glyphs.len()
            || count != self.mapping.len()
            || count != self.source_count
            || count > u32::MAX as usize
            || start > count
            || length > count - start
            || !safe_edge(&recipe.prepared, start)
            || !safe_edge(&recipe.prepared, start + length)
            || recipe.coordinates != self.coordinates
        {
            return Err(FontError::InvalidArgument);
        }
        let font = self.batch.font();
        let failure = Cell::new(None);
        let bound = BoundBatch {
            font,
            coordinates: &recipe.coordinates,
            batch: &self.batch,
            policy: recipe.projection,
            source_count: self.source_count,
            auxiliary: self.auxiliary,
            failure: &failure,
        };
        let frame = NativeDeviceFrame {
            source: &bound,
            arithmetic: bound.bind().map_err(|_| FontError::InvalidArgument)?,
        };
        let mut glyphs = copy_vec(&recipe.prepared[start..start + length])?;
        let mut attachments = filled(length, GposAttachment::default())?;
        // Recomposition intentionally reads raw GDEF, independently of ordinary
        // shaping's compatibility blocklist, matching the original bounded recipe.
        let gdef = OwnedGdef::from_face(font.clone())?;
        let gdef = gdef.as_ref().map(OwnedGdef::view);
        if !recipe.lookups.is_empty() {
            let table =
                OwnedLayoutTable::from_face(font.clone(), GPOS)?.ok_or(FontError::InvalidFace)?;
            let table = table.view();
            let options = GposOptions {
                gdef,
                direction: recipe.direction,
                font: Some(font),
                normalized_coordinates: &recipe.coordinates,
                ..Default::default()
            };
            let mut state = GposState::default();
            for &(lookup, resolution) in &recipe.lookups {
                if let Some(feature) = resolution
                    .feature
                    .filter(|&tag| !resolution.required && recipe.features.has_settings(tag))
                {
                    for i in 0..glyphs.len() {
                        if recipe.features.value(feature, glyphs[i].cluster) != 0 {
                            table.apply_gpos(
                                lookup,
                                Some(i as u32),
                                &mut glyphs,
                                &mut attachments,
                                options,
                                Some(frame.gpos()),
                                &mut state,
                            )?;
                        }
                    }
                } else {
                    table.apply_gpos(
                        lookup,
                        None,
                        &mut glyphs,
                        &mut attachments,
                        options,
                        Some(frame.gpos()),
                        &mut state,
                    )?;
                }
            }
        }
        if recipe.legacy_kerning {
            apply_native_device_legacy_kern(font, &mut glyphs, gdef, frame.gpos())?;
        }
        let rtl = recipe.direction == GposDirection::RightToLeft;
        if rtl {
            glyphs.reverse();
        }
        let mut indices = Vec::new();
        indices
            .try_reserve_exact(length)
            .map_err(|_| FontError::Capacity)?;
        for (i, glyph) in glyphs.iter().enumerate() {
            let descriptor = if rtl {
                start + length - 1 - i
            } else {
                start + i
            };
            let original_index = if rtl {
                count - 1 - descriptor
            } else {
                descriptor
            };
            if self.mapping[original_index] != descriptor as u32
                || self.glyphs[original_index].glyph_id != glyph.glyph_id
                || self.glyphs[original_index].cluster != glyph.cluster
            {
                return Err(FontError::InvalidArgument);
            }
            indices.push(original_index as u32);
        }
        if start == 0 && length == count && glyphs != self.glyphs {
            return Err(FontError::VerificationFailed);
        }
        Ok(Arc::new(NativeDevicePositionedSlice {
            original: Arc::clone(self),
            prepared_start,
            prepared_count,
            glyphs,
            original_indices: indices,
        }))
    }
}
