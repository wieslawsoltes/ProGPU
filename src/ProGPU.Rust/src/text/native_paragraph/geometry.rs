//! Original combined native paragraph geometry and independent flat imports.
use super::*;
use crate::{
    font::OutlinePoint,
    path::Segment,
    text::{
        font_policy::{FontPolicyError, FontReplayGeometry},
        hinting::*,
    },
};
mod admission;
mod import;
mod paint;
mod records;
pub use paint::{NativeParagraphPaint, prepare_native_paragraph_draws};
pub use records::*;
const MAX_SLOTS: usize = 1 << 24;
const MAX_OUTLINES: usize = 1 << 20;
const MAX_FONT_BYTES: usize = 256 * 1024 * 1024;
/// Original geometry/import error classes. A failed candidate is never published.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
pub enum NativeParagraphGeometryError {
    #[error("invalid native paragraph geometry argument")]
    InvalidArgument,
    #[error("invalid retained paragraph layout or resource records")]
    InvalidLayout,
    #[error("unsupported exact physical paragraph mapping")]
    UnsupportedMapping,
    #[error("native outline conversion failed: {0:?}")]
    OutlineConversion(NativeHintedOutlineError),
    #[error("native paragraph resource capacity exceeded")]
    InsufficientCapacity,
    #[error("native paragraph resource allocation failed")]
    ResourceExhausted,
}
fn copy<T: Clone>(v: &[T]) -> Result<Vec<T>, NativeParagraphGeometryError> {
    copied(v).map_err(|_| NativeParagraphGeometryError::ResourceExhausted)
}
fn alloc<T: Clone>(n: usize, v: T) -> Result<Vec<T>, NativeParagraphGeometryError> {
    filled(n, v).map_err(|_| NativeParagraphGeometryError::ResourceExhausted)
}
fn reserve<T>(n: usize) -> Result<Vec<T>, NativeParagraphGeometryError> {
    reserved(n).map_err(|_| NativeParagraphGeometryError::ResourceExhausted)
}
fn checked(v: bool) -> Result<(), NativeParagraphGeometryError> {
    if v {
        Ok(())
    } else {
        Err(NativeParagraphGeometryError::InvalidLayout)
    }
}
fn policy(
    dpi: f32,
    projection: NativeHintedProjection,
    coverage: NativeHintedCoverage,
) -> Result<f32, NativeParagraphGeometryError> {
    use NativeParagraphGeometryError as E;
    if !dpi.is_finite() || dpi <= 0.0 {
        return Err(E::InvalidArgument);
    }
    if coverage == NativeHintedCoverage::Monochrome
        || !matches!(
            projection,
            NativeHintedProjection::Automatic
                | NativeHintedProjection::IntrinsicSimd
                | NativeHintedProjection::ScalarReference
        )
    {
        return Err(E::OutlineConversion(
            NativeHintedOutlineError::UnsupportedPolicy,
        ));
    }
    let units = 1.0 / dpi;
    if !units.is_finite() || units <= 0.0 {
        return Err(E::UnsupportedMapping);
    }
    Ok(units)
}
/// One combined retained raster generation. Imports own complete source records
/// and have no paragraph/provider identity. Producer resources retain the actual
/// full paragraph, run batches, source fonts and immutable copied wire records.
/// Source-double generations additionally retain their original v2 witnesses.
#[derive(Debug)]
pub struct NativeParagraphGeometry {
    paragraph: Option<Arc<NativeHintedParagraph>>,
    records: NativeParagraphResource,
    source_paragraph: Option<Arc<NativeSourceParagraph>>,
    source_records: Option<NativeSourceResource>,
    occurrences: Vec<NativeParagraphOccurrence>,
}
impl NativeParagraphGeometry {
    /// Convert every actual original run once, excluding only auxiliary capture
    /// descriptors. Maximum-lines/reflow retains all original unpositioned runs.
    pub fn new(
        paragraph: Arc<NativeHintedParagraph>,
        dpi: f32,
        projection: NativeHintedProjection,
        coverage: NativeHintedCoverage,
    ) -> Result<Self, NativeParagraphGeometryError> {
        checked(paragraph.source.source_policies.is_none())?;
        Self::new_inner(paragraph, None, dpi, projection, coverage)
    }
    /// Retain the actual source-double generation and its full version-two
    /// witnesses alongside the common captured outline/descriptor geometry.
    pub fn new_source(
        paragraph: Arc<NativeSourceParagraph>,
        dpi: f32,
        projection: NativeHintedProjection,
        coverage: NativeHintedCoverage,
    ) -> Result<Self, NativeSourceError> {
        if paragraph.options().pixels_per_dip as f32 != dpi {
            return Err(NativeSourceError::InvalidArgument);
        }
        Self::new_inner(
            paragraph.raster().clone(),
            Some(paragraph),
            dpi,
            projection,
            coverage,
        )
        .map_err(NativeSourceError::from)
    }
    fn new_inner(
        paragraph: Arc<NativeHintedParagraph>,
        source_paragraph: Option<Arc<NativeSourceParagraph>>,
        dpi: f32,
        projection: NativeHintedProjection,
        coverage: NativeHintedCoverage,
    ) -> Result<Self, NativeParagraphGeometryError> {
        use NativeParagraphGeometryError as E;
        let source_records = source_paragraph
            .as_deref()
            .map(NativeSourceResource::from_paragraph)
            .transpose()
            .map_err(|e| match e {
                NativeSourceError::Font(FontError::Capacity) => E::ResourceExhausted,
                _ => E::InvalidLayout,
            })?;
        let units = policy(dpi, projection, coverage)?;
        let p = &paragraph;
        let s = &p.source;
        if [
            s.runs.len(),
            s.logical.len(),
            p.glyphs.len(),
            s.source.len(),
        ]
        .into_iter()
        .any(|n| n > MAX_SLOTS)
        {
            return Err(E::InsufficientCapacity);
        }
        admission::paragraph(p)?;
        let mut required = reserve(s.runs.len())?;
        let mut totals = [0usize; 4];
        let mut scratch = 0;
        for run in &s.runs {
            let r = native_hinted_run_outline_requirements(&run.generation, projection, coverage)
                .map_err(E::OutlineConversion)?;
            for (i, n) in [r.source_slots, r.positioned_slots, r.outlines, r.segments]
                .into_iter()
                .enumerate()
            {
                let limit = if i == 2 { MAX_OUTLINES } else { MAX_SLOTS };
                if n > limit - totals[i] {
                    return Err(E::InsufficientCapacity);
                }
                totals[i] += n;
            }
            scratch = scratch.max(r.scratch_points);
            required.push(r);
        }
        let metadata = NativeParagraphResourceMetadata {
            native_diagnostics: None,
            dpi_scale: dpi,
            projection,
            coverage,
            source_digit_bidi: s.source_digit_bidi,
            paragraph_level: s.paragraph_level,
            shaping_direction: s.shaping.direction,
            zero_mark_advances: s.shaping.zero_mark_advances,
            layout: p.options,
            metrics: p.metrics,
        };
        let mut v = NativeParagraphResource::empty(metadata);
        for font in &s.fonts {
            let h = font.header_metrics().ok_or(E::InvalidLayout)?;
            checked(
                h.units_per_em != 0 && font.data().len() <= u32::MAX as usize - v.font_bytes.len(),
            )?;
            v.font_sources
                .try_reserve(1)
                .map_err(|_| E::ResourceExhausted)?;
            v.font_bytes
                .try_reserve(font.data().len())
                .map_err(|_| E::ResourceExhausted)?;
            v.font_sources.push(NativeParagraphFontSource {
                byte_offset: v.font_bytes.len() as u32,
                byte_count: font.data().len() as u32,
                face_index: font.face_index(),
                units_per_em: u32::from(h.units_per_em),
            });
            v.font_bytes.extend_from_slice(font.data());
        }
        v.features = copy(&s.shaping.features)?;
        for style in &s.styles {
            let hint = &style.hinting;
            let feature_start = if style.features.is_empty() {
                0
            } else if let Some(start) = v
                .features
                .windows(style.features.len())
                .position(|v| v == style.features.as_slice())
            {
                start
            } else {
                let start = v.features.len();
                v.features
                    .try_reserve(style.features.len())
                    .map_err(|_| E::ResourceExhausted)?;
                v.features.extend_from_slice(&style.features);
                start
            };
            if v.features.len() > u32::MAX as usize
                || v.variation_coordinates_16_16
                    .len()
                    .checked_add(hint.variation_coordinates_16_16.len())
                    .is_none_or(|n| n > u32::MAX as usize)
            {
                return Err(E::InsufficientCapacity);
            }
            v.styles.try_reserve(1).map_err(|_| E::ResourceExhausted)?;
            v.device_styles
                .try_reserve(1)
                .map_err(|_| E::ResourceExhausted)?;
            v.source_metrics
                .try_reserve(1)
                .map_err(|_| E::ResourceExhausted)?;
            v.styles.push(NativeParagraphStyleRecord {
                scalar_start: style.scalar_start,
                scalar_count: style.scalar_count,
                font_index: style.font_index,
                scale: style.source_scale,
                feature_start: feature_start as u32,
                feature_count: style.features.len() as u32,
                language: style.language.0,
                digit_substitution: style.number_substitution.digits,
                percent: style.number_substitution.percent,
                group_separator: style.number_substitution.group_separator,
                decimal_separator: style.number_substitution.decimal_separator,
            });
            v.device_styles.push(NativeParagraphDeviceStyle {
                font_index: style.font_index,
                source_scale: style.source_scale,
                logical_units_per_physical_pixel: style.logical_units_per_physical_pixel,
                x_pixels_per_em_26_6: hint.pixels_per_em_26_6[0],
                y_pixels_per_em_26_6: hint.pixels_per_em_26_6[1],
                interpreter: hint.hint_policy as u32,
                x_phase_26_6: hint.phase_26_6[0],
                y_phase_26_6: hint.phase_26_6[1],
                variation_start: v.variation_coordinates_16_16.len() as u32,
                variation_count: hint.variation_coordinates_16_16.len() as u32,
                reserved: 0,
            });
            v.variation_coordinates_16_16
                .try_reserve(hint.variation_coordinates_16_16.len())
                .map_err(|_| E::ResourceExhausted)?;
            v.variation_coordinates_16_16
                .extend_from_slice(&hint.variation_coordinates_16_16);
            v.source_metrics.push(style.metrics);
        }
        v.normalized_coordinates = copy(&s.shaping.normalized_coordinates)?;
        v.source_scalars = copy(&s.source)?;
        v.admitted_scalars = copy(&s.input)?;
        v.pre_context = copy(&s.shaping.pre_context)?;
        v.post_context = copy(&s.shaping.post_context)?;
        v.scalar_levels = copy(&s.scalar_levels)?;
        v.logical_glyphs = copy(&s.logical)?;
        v.logical_owners = copy(&s.owners)?;
        v.logical_cluster_ends = copy(&s.ends)?;
        v.logical_bidi_levels = copy(&s.levels)?;
        v.glyph_scales = copy(&s.scales)?;
        v.positioned_owners = copy(&p.owners)?;
        v.positioned_cluster_ends = copy(&p.ends)?;
        v.positioned_bidi_levels = copy(&p.levels)?;
        v.line_origins = copy(&p.origins)?;
        v.runs = reserve(s.runs.len())?;
        for r in &s.runs {
            v.runs.push(NativeParagraphRunRecord {
                scalar_start: r.scalar_range.start,
                scalar_count: r.scalar_range.end - r.scalar_range.start,
                logical_start: r.logical_range.start,
                logical_count: r.logical_range.end - r.logical_range.start,
                font_index: r.font_index,
                style_index: r.style_index,
                bidi_level: i32::from(r.bidi_level),
                source_scale: r.source_scale,
                logical_units_per_physical_pixel: r.logical_units_per_physical_pixel,
                source_descriptor_count: r.generation.source_descriptor_count() as u32,
            });
        }
        v.positioned_glyphs = reserve(p.glyphs.len())?;
        for &g in &p.glyphs {
            v.positioned_glyphs
                .push(NativeParagraphPositionedRecord::from_positioned(
                    g,
                    s.font_indices[g.glyph_index as usize],
                ));
        }
        v.lines = reserve(p.lines.len())?;
        for &l in &p.lines {
            v.lines.push(NativeParagraphLineRecord::from_positioned(l));
        }
        if source_paragraph.is_none() {
            let interaction =
                NativeParagraphInteraction::new(paragraph.clone()).map_err(|_| E::InvalidLayout)?;
            v.boxes = reserve(interaction.boxes().len())?;
            v.carets = reserve(interaction.carets().len())?;
            v.boxes
                .extend(interaction.boxes().iter().copied().map(Into::into));
            v.carets
                .extend(interaction.carets().iter().copied().map(Into::into));
        }
        v.outlines = alloc(totals[2], NativeHintedOutline::default())?;
        v.segments = alloc(totals[3], Segment::line([0.0; 2], [0.0; 2]))?;
        v.source_outline_indices = alloc(totals[0], 0)?;
        v.run_outline_indices = alloc(totals[1], 0)?;
        v.outline_owners = alloc(totals[2], NativeParagraphOutlineOwner::default())?;
        v.run_slices = reserve(s.runs.len())?;
        let mut topology = alloc(scratch, OutlinePoint::default())?;
        let mut physical = alloc(scratch, [0.0; 2])?;
        let mut at = [0usize; 4];
        for (i, r) in required.into_iter().enumerate() {
            let [a, b, c, d] = at;
            let written = write_native_hinted_run_outlines(
                &s.runs[i].generation,
                NativeHintedOutlineScratch {
                    topology: &mut topology,
                    physical_points: &mut physical,
                },
                &mut v.outlines[c..c + r.outlines],
                &mut v.segments[d..d + r.segments],
                &mut v.source_outline_indices[a..a + r.source_slots],
                &mut v.run_outline_indices[b..b + r.positioned_slots],
                projection,
                coverage,
            )
            .map_err(E::OutlineConversion)?;
            checked(written == r)?;
            for o in &mut v.outlines[c..c + r.outlines] {
                o.segment_offset += d;
            }
            for j in 0..r.source_slots {
                let o = &mut v.source_outline_indices[a + j];
                if *o == NATIVE_HINTED_NO_OUTLINE {
                    continue;
                }
                checked((*o as usize) < r.outlines)?;
                *o += c as u32;
                v.outline_owners[*o as usize] = NativeParagraphOutlineOwner {
                    run_index: i as u32,
                    descriptor_index: j as u32,
                };
            }
            for o in &mut v.run_outline_indices[b..b + r.positioned_slots] {
                if *o != NATIVE_HINTED_NO_OUTLINE {
                    checked((*o as usize) < r.outlines)?;
                    *o += c as u32;
                }
            }
            v.run_slices.push(NativeParagraphRunSlice {
                source_start: a as u32,
                source_count: r.source_slots as u32,
                run_start: b as u32,
                run_count: r.positioned_slots as u32,
                outline_start: c as u32,
                outline_count: r.outlines as u32,
                segment_start: d as u32,
                segment_count: r.segments as u32,
            });
            at = [
                a + r.source_slots,
                b + r.positioned_slots,
                c + r.outlines,
                d + r.segments,
            ];
        }
        v.positioned_outline_indices = reserve(p.glyphs.len())?;
        for owner in &p.owners {
            let run = &s.runs[owner.run_index as usize];
            let slice = v.run_slices[owner.run_index as usize];
            let o = v.source_outline_indices
                [slice.source_start as usize + owner.descriptor_index as usize];
            if run.logical_units_per_physical_pixel != units
                || run.logical_units_per_physical_pixel * dpi != 1.0
            {
                return Err(E::UnsupportedMapping);
            }
            checked(
                v.run_outline_indices[slice.run_start as usize + owner.run_glyph_index as usize]
                    == o,
            )?;
            v.positioned_outline_indices.push(o);
        }
        let occurrences = occurrences(&v)?;
        Ok(Self {
            paragraph: Some(paragraph),
            records: v,
            source_paragraph,
            source_records,
            occurrences,
        })
    }
    /// Validate complete original flat metadata and copy every record before
    /// publication. This never creates a paragraph, shaped run or hinted batch.
    pub fn import(records: &NativeParagraphResource) -> Result<Self, NativeParagraphGeometryError> {
        import::validate(records, false)?;
        let owned = records.try_copy()?;
        let occurrences = occurrences(&owned)?;
        Ok(Self {
            paragraph: None,
            records: owned,
            source_paragraph: None,
            source_records: None,
            occurrences,
        })
    }
    /// Complete paired v2 import: admit all base records, then original source
    /// policies, placement witnesses, double writer output and interaction bits.
    pub fn import_source(
        records: &NativeParagraphResource,
        source: &NativeSourceResource,
    ) -> Result<Self, NativeSourceError> {
        import::validate(records, true)?;
        source.admit(records)?;
        let owned = records.try_copy()?;
        let source = source.try_copy()?;
        let occurrences = occurrences(&owned)?;
        Ok(Self {
            paragraph: None,
            records: owned,
            source_paragraph: None,
            source_records: Some(source),
            occurrences,
        })
    }
    /// Copy the complete paired resource only after both full capacities pass.
    /// No font/provider work or placement reconstruction occurs during copies.
    pub fn copy_source_into(
        &self,
        base: NativeParagraphResourceBuffers<'_>,
        source: NativeSourceResourceBuffers<'_>,
    ) -> Result<
        (
            NativeParagraphResourceMetadata,
            NativeSourceResourceMetadata,
        ),
        NativeSourceError,
    > {
        let records = self
            .source_records
            .as_ref()
            .ok_or(NativeSourceError::Unsupported)?;
        self.records.preflight_copy(&base)?;
        records.preflight_copy(&source)?;
        let base = self.records.copy_into(base)?;
        let source = records.copy_into(source)?;
        Ok((base, source))
    }
    pub fn source_paragraph(&self) -> Option<&Arc<NativeSourceParagraph>> {
        self.source_paragraph.as_ref()
    }
    pub fn source_interaction(&self) -> Option<NativeSourceInteraction<'_>> {
        self.source_records
            .as_ref()
            .map(NativeSourceResource::interaction)
    }
    pub fn source_records(&self) -> Option<&NativeSourceResource> {
        self.source_records.as_ref()
    }
    pub fn paragraph(&self) -> Option<&Arc<NativeHintedParagraph>> {
        self.paragraph.as_ref()
    }
    pub fn records(&self) -> &NativeParagraphResource {
        &self.records
    }
    pub fn is_imported(&self) -> bool {
        self.paragraph.is_none()
    }
    pub fn dpi_scale(&self) -> f32 {
        self.records.metadata.dpi_scale
    }
    pub fn projection(&self) -> NativeHintedProjection {
        self.records.metadata.projection
    }
    pub fn coverage(&self) -> NativeHintedCoverage {
        self.records.metadata.coverage
    }
    pub fn outlines(&self) -> &[NativeHintedOutline] {
        &self.records.outlines
    }
    pub fn segments(&self) -> &[Segment] {
        &self.records.segments
    }
    pub fn occurrences(&self) -> &[NativeParagraphOccurrence] {
        &self.occurrences
    }
    pub fn into_replay(self: Arc<Self>) -> Result<FontReplayGeometry, FontPolicyError> {
        FontReplayGeometry::from_paragraph_source(self)
    }
}
fn occurrences(
    v: &NativeParagraphResource,
) -> Result<Vec<NativeParagraphOccurrence>, NativeParagraphGeometryError> {
    let mut result = reserve(v.positioned_glyphs.len())?;
    for (i, g) in v.positioned_glyphs.iter().enumerate() {
        let o = v.positioned_owners[i];
        let r = v.runs[o.run_index as usize];
        result.push(NativeParagraphOccurrence {
            positioned_index: i as u32,
            logical_index: g.glyph_index,
            run_index: o.run_index,
            run_glyph_index: o.run_glyph_index,
            descriptor_index: o.descriptor_index,
            font_index: r.font_index,
            style_index: r.style_index,
            outline_index: v.positioned_outline_indices[i],
            glyph_id: g.glyph_id,
            cluster: g.cluster,
            cluster_end: v.positioned_cluster_ends[i],
            bidi_level: v.positioned_bidi_levels[i],
            position: [g.x, g.y],
            advance: [g.advance_x, g.advance_y],
        });
    }
    Ok(result)
}
