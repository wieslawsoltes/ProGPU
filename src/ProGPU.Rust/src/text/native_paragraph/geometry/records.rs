//! Owned native flat resource transport. Borrowed pointers/counts become typed
//! slices; fixed record payloads retain their original fields and reserved bytes.
use super::*;
macro_rules! record {($name:ident {$($field:ident : $ty:ty),* $(,)?})=>{
    #[repr(C)] #[derive(Clone,Copy,Debug,Default,PartialEq)]
    pub struct $name {$(pub $field:$ty),*}
};}
record!(NativeParagraphFontSource {
    byte_offset: u32,
    byte_count: u32,
    face_index: u32,
    units_per_em: u32
});
record!(NativeParagraphDeviceStyle {
    font_index: u32,
    source_scale: f32,
    logical_units_per_physical_pixel: f32,
    x_pixels_per_em_26_6: u32,
    y_pixels_per_em_26_6: u32,
    interpreter: u32,
    x_phase_26_6: u32,
    y_phase_26_6: u32,
    variation_start: u32,
    variation_count: u32,
    reserved: u32
});
record!(NativeParagraphStyleRecord {
    scalar_start: u32,
    scalar_count: u32,
    font_index: u32,
    scale: f32,
    feature_start: u32,
    feature_count: u32,
    language: u32,
    digit_substitution: u32,
    percent: u32,
    group_separator: u32,
    decimal_separator: u32
});
record!(NativeParagraphRunRecord {
    scalar_start: u32,
    scalar_count: u32,
    logical_start: u32,
    logical_count: u32,
    font_index: u32,
    style_index: u32,
    bidi_level: i32,
    source_scale: f32,
    logical_units_per_physical_pixel: f32,
    source_descriptor_count: u32
});
record!(NativeParagraphRunSlice {
    source_start: u32,
    source_count: u32,
    run_start: u32,
    run_count: u32,
    outline_start: u32,
    outline_count: u32,
    segment_start: u32,
    segment_count: u32
});
record!(NativeParagraphOutlineOwner {
    run_index: u32,
    descriptor_index: u32
});
record!(NativeParagraphPositionedRecord {
    glyph_index: u32,
    glyph_id: u32,
    font_index: u32,
    cluster: i32,
    x: f32,
    y: f32,
    advance_x: f32,
    advance_y: f32
});
impl NativeParagraphPositionedRecord {
    pub fn positioned(self) -> NativePositionedTextGlyph {
        NativePositionedTextGlyph {
            glyph_index: self.glyph_index,
            glyph_id: self.glyph_id,
            cluster: self.cluster,
            position: [self.x, self.y],
            advance: [self.advance_x, self.advance_y],
        }
    }
    pub(super) fn from_positioned(g: NativePositionedTextGlyph, font_index: u32) -> Self {
        Self {
            glyph_index: g.glyph_index,
            glyph_id: g.glyph_id,
            font_index,
            cluster: g.cluster,
            x: g.position[0],
            y: g.position[1],
            advance_x: g.advance[0],
            advance_y: g.advance[1],
        }
    }
}
record!(NativeParagraphLineRecord {
    glyph_start: u32,
    glyph_count: u32,
    input_start: i32,
    input_end: i32,
    width: f32,
    baseline_y: f32,
    height: f32,
    clipped: u8,
    flags: u8,
    reserved0: u8,
    reserved1: u8
});
impl NativeParagraphLineRecord {
    pub fn positioned(self) -> NativePositionedTextLine {
        NativePositionedTextLine {
            glyph_start: self.glyph_start,
            glyph_count: self.glyph_count,
            input_start: self.input_start,
            input_end: self.input_end,
            width: self.width,
            baseline_y: self.baseline_y,
            height: self.height,
            clipped: self.clipped != 0,
            flags: self.flags,
        }
    }
    pub(super) fn from_positioned(l: NativePositionedTextLine) -> Self {
        Self {
            glyph_start: l.glyph_start,
            glyph_count: l.glyph_count,
            input_start: l.input_start,
            input_end: l.input_end,
            width: l.width,
            baseline_y: l.baseline_y,
            height: l.height,
            clipped: u8::from(l.clipped),
            flags: l.flags,
            reserved0: 0,
            reserved1: 0,
        }
    }
}
record!(NativeParagraphBoxRecord {
    input_start: i32,
    input_end: i32,
    line_index: u32,
    bidi_level: i8,
    reserved: [u8; 3],
    x: f32,
    y: f32,
    width: f32,
    height: f32
});
impl From<NativeClusterBox> for NativeParagraphBoxRecord {
    fn from(b: NativeClusterBox) -> Self {
        Self {
            input_start: b.input_start,
            input_end: b.input_end,
            line_index: b.line_index,
            bidi_level: b.bidi_level,
            reserved: [0; 3],
            x: b.bounds.x,
            y: b.bounds.y,
            width: b.bounds.width,
            height: b.bounds.height,
        }
    }
}
record!(NativeParagraphCaretRecord {
    input_position: i32,
    line_index: u32,
    x: f32,
    y: f32,
    height: f32,
    bidi_level: i8,
    trailing: u8,
    reserved: [u8; 2]
});
impl From<NativeCaretStop> for NativeParagraphCaretRecord {
    fn from(c: NativeCaretStop) -> Self {
        Self {
            input_position: c.input_position,
            line_index: c.line_index,
            x: c.position[0],
            y: c.position[1],
            height: c.height,
            bidi_level: c.bidi_level,
            trailing: u8::from(c.trailing),
            reserved: [0; 2],
        }
    }
}
/// Original positioned source occurrence; all indices survive selected views.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeParagraphOccurrence {
    pub positioned_index: u32,
    pub logical_index: u32,
    pub run_index: u32,
    pub run_glyph_index: u32,
    pub descriptor_index: u32,
    pub font_index: u32,
    pub style_index: u32,
    pub outline_index: u32,
    pub glyph_id: u32,
    pub cluster: i32,
    pub cluster_end: i32,
    pub bidi_level: i8,
    pub position: [f32; 2],
    pub advance: [f32; 2],
}
/// Optional original C++ producer counters, retained verbatim when supplied by
/// that producer. The Rust owner does not fabricate a C++ context/plan cache.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeParagraphProducerDiagnostics {
    pub shaping_run_count: u32,
    pub cached_plan_count: u32,
    pub plan_build_count: u32,
    pub scratch_bytes_used: u64,
}
/// Pointer-free original raw resource metadata. Slice lengths replace readable
/// C pointer counts and typed enums replace invalid numeric discriminator values.
/// This is an owned transport, never proof of an actual hinted producer.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeParagraphResourceMetadata {
    pub native_diagnostics: Option<NativeParagraphProducerDiagnostics>,
    pub dpi_scale: f32,
    pub projection: NativeHintedProjection,
    pub coverage: NativeHintedCoverage,
    pub source_digit_bidi: bool,
    pub paragraph_level: i8,
    pub shaping_direction: GposDirection,
    pub zero_mark_advances: bool,
    pub layout: NativeTextLayoutOptions,
    pub metrics: NativeTextLayoutMetrics,
}
/// Complete independent flat resource. Imports validate every inventory before
/// copying; no native paragraph, driver batch or provider identity is fabricated.
#[derive(Debug)]
pub struct NativeParagraphResource {
    pub metadata: NativeParagraphResourceMetadata,
    pub font_sources: Vec<NativeParagraphFontSource>,
    pub font_bytes: Vec<u8>,
    pub device_styles: Vec<NativeParagraphDeviceStyle>,
    pub variation_coordinates_16_16: Vec<i32>,
    pub normalized_coordinates: Vec<i16>,
    pub source_scalars: Vec<UnicodeScalar>,
    pub admitted_scalars: Vec<UnicodeScalar>,
    pub pre_context: Vec<UnicodeScalar>,
    pub post_context: Vec<UnicodeScalar>,
    pub scalar_levels: Vec<BidiLevel>,
    pub styles: Vec<NativeParagraphStyleRecord>,
    pub source_metrics: Vec<NativeTextItemMetrics>,
    pub runs: Vec<NativeParagraphRunRecord>,
    pub logical_glyphs: Vec<ShapingGlyph>,
    pub logical_owners: Vec<NativeParagraphGlyphOwner>,
    pub logical_cluster_ends: Vec<i32>,
    pub logical_bidi_levels: Vec<i8>,
    pub glyph_scales: Vec<f32>,
    pub positioned_glyphs: Vec<NativeParagraphPositionedRecord>,
    pub positioned_owners: Vec<NativeParagraphGlyphOwner>,
    pub positioned_cluster_ends: Vec<i32>,
    pub positioned_bidi_levels: Vec<i8>,
    pub lines: Vec<NativeParagraphLineRecord>,
    pub line_origins: Vec<f32>,
    pub boxes: Vec<NativeParagraphBoxRecord>,
    pub carets: Vec<NativeParagraphCaretRecord>,
    pub features: Vec<NativeFeature>,
    pub outlines: Vec<NativeHintedOutline>,
    pub segments: Vec<Segment>,
    pub run_slices: Vec<NativeParagraphRunSlice>,
    pub source_outline_indices: Vec<u32>,
    pub run_outline_indices: Vec<u32>,
    pub outline_owners: Vec<NativeParagraphOutlineOwner>,
    pub positioned_outline_indices: Vec<u32>,
}
/// Complete independently writable capacity inventory; safe borrows exclude all
/// source/output and output/output aliases, including unused tails.
pub struct NativeParagraphResourceBuffers<'a> {
    pub font_sources: &'a mut [NativeParagraphFontSource],
    pub font_bytes: &'a mut [u8],
    pub device_styles: &'a mut [NativeParagraphDeviceStyle],
    pub variation_coordinates_16_16: &'a mut [i32],
    pub normalized_coordinates: &'a mut [i16],
    pub source_scalars: &'a mut [UnicodeScalar],
    pub admitted_scalars: &'a mut [UnicodeScalar],
    pub pre_context: &'a mut [UnicodeScalar],
    pub post_context: &'a mut [UnicodeScalar],
    pub scalar_levels: &'a mut [BidiLevel],
    pub styles: &'a mut [NativeParagraphStyleRecord],
    pub source_metrics: &'a mut [NativeTextItemMetrics],
    pub runs: &'a mut [NativeParagraphRunRecord],
    pub logical_glyphs: &'a mut [ShapingGlyph],
    pub logical_owners: &'a mut [NativeParagraphGlyphOwner],
    pub logical_cluster_ends: &'a mut [i32],
    pub logical_bidi_levels: &'a mut [i8],
    pub glyph_scales: &'a mut [f32],
    pub positioned_glyphs: &'a mut [NativeParagraphPositionedRecord],
    pub positioned_owners: &'a mut [NativeParagraphGlyphOwner],
    pub positioned_cluster_ends: &'a mut [i32],
    pub positioned_bidi_levels: &'a mut [i8],
    pub lines: &'a mut [NativeParagraphLineRecord],
    pub line_origins: &'a mut [f32],
    pub boxes: &'a mut [NativeParagraphBoxRecord],
    pub carets: &'a mut [NativeParagraphCaretRecord],
    pub features: &'a mut [NativeFeature],
    pub outlines: &'a mut [NativeHintedOutline],
    pub segments: &'a mut [Segment],
    pub run_slices: &'a mut [NativeParagraphRunSlice],
    pub source_outline_indices: &'a mut [u32],
    pub run_outline_indices: &'a mut [u32],
    pub outline_owners: &'a mut [NativeParagraphOutlineOwner],
    pub positioned_outline_indices: &'a mut [u32],
}
impl NativeParagraphResource {
    pub(super) fn empty(metadata: NativeParagraphResourceMetadata) -> Self {
        Self {
            metadata,
            font_sources: Vec::new(),
            font_bytes: Vec::new(),
            device_styles: Vec::new(),
            variation_coordinates_16_16: Vec::new(),
            normalized_coordinates: Vec::new(),
            source_scalars: Vec::new(),
            admitted_scalars: Vec::new(),
            pre_context: Vec::new(),
            post_context: Vec::new(),
            scalar_levels: Vec::new(),
            styles: Vec::new(),
            source_metrics: Vec::new(),
            runs: Vec::new(),
            logical_glyphs: Vec::new(),
            logical_owners: Vec::new(),
            logical_cluster_ends: Vec::new(),
            logical_bidi_levels: Vec::new(),
            glyph_scales: Vec::new(),
            positioned_glyphs: Vec::new(),
            positioned_owners: Vec::new(),
            positioned_cluster_ends: Vec::new(),
            positioned_bidi_levels: Vec::new(),
            lines: Vec::new(),
            line_origins: Vec::new(),
            boxes: Vec::new(),
            carets: Vec::new(),
            features: Vec::new(),
            outlines: Vec::new(),
            segments: Vec::new(),
            run_slices: Vec::new(),
            source_outline_indices: Vec::new(),
            run_outline_indices: Vec::new(),
            outline_owners: Vec::new(),
            positioned_outline_indices: Vec::new(),
        }
    }
    /// Copy every complete original inventory and preserve all unused output tails.
    pub fn copy_into(
        &self,
        out: NativeParagraphResourceBuffers<'_>,
    ) -> Result<NativeParagraphResourceMetadata, NativeParagraphGeometryError> {
        if out.font_sources.len() < self.font_sources.len()
            || out.font_bytes.len() < self.font_bytes.len()
            || out.device_styles.len() < self.device_styles.len()
            || out.variation_coordinates_16_16.len() < self.variation_coordinates_16_16.len()
            || out.normalized_coordinates.len() < self.normalized_coordinates.len()
            || out.source_scalars.len() < self.source_scalars.len()
            || out.admitted_scalars.len() < self.admitted_scalars.len()
            || out.pre_context.len() < self.pre_context.len()
            || out.post_context.len() < self.post_context.len()
            || out.scalar_levels.len() < self.scalar_levels.len()
            || out.styles.len() < self.styles.len()
            || out.source_metrics.len() < self.source_metrics.len()
            || out.runs.len() < self.runs.len()
            || out.logical_glyphs.len() < self.logical_glyphs.len()
            || out.logical_owners.len() < self.logical_owners.len()
            || out.logical_cluster_ends.len() < self.logical_cluster_ends.len()
            || out.logical_bidi_levels.len() < self.logical_bidi_levels.len()
            || out.glyph_scales.len() < self.glyph_scales.len()
            || out.positioned_glyphs.len() < self.positioned_glyphs.len()
            || out.positioned_owners.len() < self.positioned_owners.len()
            || out.positioned_cluster_ends.len() < self.positioned_cluster_ends.len()
            || out.positioned_bidi_levels.len() < self.positioned_bidi_levels.len()
            || out.lines.len() < self.lines.len()
            || out.line_origins.len() < self.line_origins.len()
            || out.boxes.len() < self.boxes.len()
            || out.carets.len() < self.carets.len()
            || out.features.len() < self.features.len()
            || out.outlines.len() < self.outlines.len()
            || out.segments.len() < self.segments.len()
            || out.run_slices.len() < self.run_slices.len()
            || out.source_outline_indices.len() < self.source_outline_indices.len()
            || out.run_outline_indices.len() < self.run_outline_indices.len()
            || out.outline_owners.len() < self.outline_owners.len()
            || out.positioned_outline_indices.len() < self.positioned_outline_indices.len()
        {
            return Err(NativeParagraphGeometryError::InsufficientCapacity);
        }
        out.font_sources[..self.font_sources.len()].copy_from_slice(&self.font_sources);
        out.font_bytes[..self.font_bytes.len()].copy_from_slice(&self.font_bytes);
        out.device_styles[..self.device_styles.len()].copy_from_slice(&self.device_styles);
        out.variation_coordinates_16_16[..self.variation_coordinates_16_16.len()]
            .copy_from_slice(&self.variation_coordinates_16_16);
        out.normalized_coordinates[..self.normalized_coordinates.len()]
            .copy_from_slice(&self.normalized_coordinates);
        out.source_scalars[..self.source_scalars.len()].copy_from_slice(&self.source_scalars);
        out.admitted_scalars[..self.admitted_scalars.len()].copy_from_slice(&self.admitted_scalars);
        out.pre_context[..self.pre_context.len()].copy_from_slice(&self.pre_context);
        out.post_context[..self.post_context.len()].copy_from_slice(&self.post_context);
        out.scalar_levels[..self.scalar_levels.len()].copy_from_slice(&self.scalar_levels);
        out.styles[..self.styles.len()].copy_from_slice(&self.styles);
        out.source_metrics[..self.source_metrics.len()].copy_from_slice(&self.source_metrics);
        out.runs[..self.runs.len()].copy_from_slice(&self.runs);
        out.logical_glyphs[..self.logical_glyphs.len()].copy_from_slice(&self.logical_glyphs);
        out.logical_owners[..self.logical_owners.len()].copy_from_slice(&self.logical_owners);
        out.logical_cluster_ends[..self.logical_cluster_ends.len()]
            .copy_from_slice(&self.logical_cluster_ends);
        out.logical_bidi_levels[..self.logical_bidi_levels.len()]
            .copy_from_slice(&self.logical_bidi_levels);
        out.glyph_scales[..self.glyph_scales.len()].copy_from_slice(&self.glyph_scales);
        out.positioned_glyphs[..self.positioned_glyphs.len()]
            .copy_from_slice(&self.positioned_glyphs);
        out.positioned_owners[..self.positioned_owners.len()]
            .copy_from_slice(&self.positioned_owners);
        out.positioned_cluster_ends[..self.positioned_cluster_ends.len()]
            .copy_from_slice(&self.positioned_cluster_ends);
        out.positioned_bidi_levels[..self.positioned_bidi_levels.len()]
            .copy_from_slice(&self.positioned_bidi_levels);
        out.lines[..self.lines.len()].copy_from_slice(&self.lines);
        out.line_origins[..self.line_origins.len()].copy_from_slice(&self.line_origins);
        out.boxes[..self.boxes.len()].copy_from_slice(&self.boxes);
        out.carets[..self.carets.len()].copy_from_slice(&self.carets);
        out.features[..self.features.len()].copy_from_slice(&self.features);
        out.outlines[..self.outlines.len()].copy_from_slice(&self.outlines);
        out.segments[..self.segments.len()].copy_from_slice(&self.segments);
        out.run_slices[..self.run_slices.len()].copy_from_slice(&self.run_slices);
        out.source_outline_indices[..self.source_outline_indices.len()]
            .copy_from_slice(&self.source_outline_indices);
        out.run_outline_indices[..self.run_outline_indices.len()]
            .copy_from_slice(&self.run_outline_indices);
        out.outline_owners[..self.outline_owners.len()].copy_from_slice(&self.outline_owners);
        out.positioned_outline_indices[..self.positioned_outline_indices.len()]
            .copy_from_slice(&self.positioned_outline_indices);
        Ok(self.metadata)
    }
    /// Retain independent copies, including exact original font bytes and no-ink slots.
    pub fn try_copy(&self) -> Result<Self, NativeParagraphGeometryError> {
        Ok(Self {
            metadata: self.metadata,
            font_sources: copy(&self.font_sources)?,
            font_bytes: copy(&self.font_bytes)?,
            device_styles: copy(&self.device_styles)?,
            variation_coordinates_16_16: copy(&self.variation_coordinates_16_16)?,
            normalized_coordinates: copy(&self.normalized_coordinates)?,
            source_scalars: copy(&self.source_scalars)?,
            admitted_scalars: copy(&self.admitted_scalars)?,
            pre_context: copy(&self.pre_context)?,
            post_context: copy(&self.post_context)?,
            scalar_levels: copy(&self.scalar_levels)?,
            styles: copy(&self.styles)?,
            source_metrics: copy(&self.source_metrics)?,
            runs: copy(&self.runs)?,
            logical_glyphs: copy(&self.logical_glyphs)?,
            logical_owners: copy(&self.logical_owners)?,
            logical_cluster_ends: copy(&self.logical_cluster_ends)?,
            logical_bidi_levels: copy(&self.logical_bidi_levels)?,
            glyph_scales: copy(&self.glyph_scales)?,
            positioned_glyphs: copy(&self.positioned_glyphs)?,
            positioned_owners: copy(&self.positioned_owners)?,
            positioned_cluster_ends: copy(&self.positioned_cluster_ends)?,
            positioned_bidi_levels: copy(&self.positioned_bidi_levels)?,
            lines: copy(&self.lines)?,
            line_origins: copy(&self.line_origins)?,
            boxes: copy(&self.boxes)?,
            carets: copy(&self.carets)?,
            features: copy(&self.features)?,
            outlines: copy(&self.outlines)?,
            segments: copy(&self.segments)?,
            run_slices: copy(&self.run_slices)?,
            source_outline_indices: copy(&self.source_outline_indices)?,
            run_outline_indices: copy(&self.run_outline_indices)?,
            outline_owners: copy(&self.outline_owners)?,
            positioned_outline_indices: copy(&self.positioned_outline_indices)?,
        })
    }
}
