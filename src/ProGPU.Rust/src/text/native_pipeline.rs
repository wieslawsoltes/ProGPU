//! Original native shaping orchestration, independent of the managed shaper.
//!
//! [`NativeShaper`] connects original ordinary, joining, Hangul and complex
//! script stages to owned design-unit positioned runs. [`NativeLatinShaper`]
//! retains its independent printable-ASCII specialization. Native hinting and
//! device fragment production remain separate provider boundaries.
use super::ShapingGlyph;
use crate::font::layout::{
    Gdef, GlyphClass, GposAttachment, GposDirection, GposOptions, GposState, GsubOptions,
    GsubState, LayoutTable, LookupSelection, OwnedGdef, OwnedLayoutTable, SelectionRequest,
    is_gdef_blocklisted, resolve_gpos_attachments,
};
use crate::font::{FontError, SfntFace, Tag};
pub mod complex;
mod features;
mod kern;
mod latin;
pub use complex::{
    NativeBufferFlags, NativeClusterLevel, NativeComplexKind, NativeComplexRequest,
    NativeComplexRequirements, NativeComplexRun, NativeComplexScratch, NativeComplexShaper,
    NativeDeviceError, NativeDeviceFragmentSource, NativeDeviceFrame, NativeDeviceMarkBounds,
    NativeDevicePositionedSlice, NativeDevicePositioningRecipe, NativeDeviceRun,
    NativeDeviceSource, NativeDeviceSpaceKind, NativeDeviceStretchScratch, NativeFallbackBounds,
    NativeFragmentContext, NativeHintPolicy, NativeHintedBatch, NativeHintedCaptureError,
    NativeHintedGlyph, NativeHintedMetrics, NativeHintedProjection, NativeHintedProjectionError,
    NativeHintedProvider, NativeHintedRequest, NativeOutlineLoadPolicy, NativeRun,
    NativeShapeRequest, NativeShapeRequirements, NativeShapeScratch, NativeShaper,
    NativeShapingRoute, apply_native_device_arabic_stretch, apply_native_device_fallback_marks,
    apply_native_device_space, resolve_native_language_tag, resolve_native_shaping_route,
};
pub use features::{
    NativeFeature, NativeFeaturePlan, NativeFeatureRoute, NativeFeatureSetting,
    native_default_features,
};
pub use kern::{apply_native_device_legacy_kern, apply_native_legacy_kern};
pub use latin::{NativeLatinRequest, NativeLatinShaper, NativePositionedRun};

const GSUB: Tag = Tag::new(*b"GSUB");
const GPOS: Tag = Tag::new(*b"GPOS");
const KERN: Tag = Tag::new(*b"kern");
const RAND: Tag = Tag::new(*b"rand");
const FRACTIONS: [Tag; 3] = [Tag::new(*b"frac"), Tag::new(*b"numr"), Tag::new(*b"dnom")];

fn copy_vec<T: Copy>(source: &[T]) -> Result<Vec<T>, FontError> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(source.len())
        .map_err(|_| FontError::Capacity)?;
    result.extend_from_slice(source);
    Ok(result)
}
fn push<T>(output: &mut Vec<T>, value: T) -> Result<(), FontError> {
    output.try_reserve(1).map_err(|_| FontError::Capacity)?;
    output.push(value);
    Ok(())
}
fn clamped(value: i64) -> i32 {
    value.clamp(i64::from(i16::MIN), i64::from(i16::MAX)) as i32
}

#[derive(Clone, Copy, Debug)]
struct Resolution {
    feature: Option<Tag>,
    required: bool,
}
impl Resolution {
    fn new(
        table: LayoutTable<'_>,
        request: SelectionRequest<'_>,
        lookup: u16,
    ) -> Result<Self, FontError> {
        if let Some(feature) = table.required_feature_for_lookup(
            request.script,
            request.language,
            lookup,
            request.coordinates,
        )? {
            return Ok(Self {
                feature: Some(feature),
                required: true,
            });
        }
        let global = |feature| {
            [
                RAND,
                Tag::new(*b"ltra"),
                Tag::new(*b"ltrm"),
                Tag::new(*b"rtla"),
                Tag::new(*b"rtlm"),
                Tag::new(*b"vert"),
                Tag::new(*b"vrt2"),
            ]
            .contains(&feature)
                || request.script == Tag::new(*b"hang")
                    && [Tag::new(*b"ljmo"), Tag::new(*b"vjmo"), Tag::new(*b"tjmo")]
                        .contains(&feature)
        };
        let mut feature = None;
        for &candidate in request.features {
            if table.feature_contains_lookup(
                request.script,
                request.language,
                candidate,
                lookup,
                request.coordinates,
            )? && feature.is_none_or(|previous| !global(previous) || global(candidate))
            {
                feature = Some(candidate);
            }
        }
        Ok(Self {
            feature,
            required: false,
        })
    }
}

#[derive(Clone, Debug)]
struct Stage {
    selection: LookupSelection,
    resolutions: Vec<Resolution>,
}
impl Stage {
    fn new(
        font: &SfntFace,
        tag: Tag,
        request: SelectionRequest<'_>,
        excluded: &[Tag],
    ) -> Result<Option<Self>, FontError> {
        let Some(table) = OwnedLayoutTable::from_face(font.clone(), tag)? else {
            return Ok(None);
        };
        let selection = LookupSelection::new(table, request, excluded)?;
        let mut resolutions = Vec::new();
        resolutions
            .try_reserve_exact(selection.lookups().len())
            .map_err(|_| FontError::Capacity)?;
        for &lookup in selection.lookups() {
            resolutions.push(Resolution::new(
                selection.table().view(),
                selection.request(),
                lookup,
            )?);
        }
        Ok(Some(Self {
            selection,
            resolutions,
        }))
    }
    fn substitute(
        &self,
        plan: &NativeFeaturePlan,
        storage: &mut [ShapingGlyph],
        count: &mut u32,
        gdef: Option<Gdef<'_>>,
        alternate: u32,
    ) -> Result<(), FontError> {
        let table = self.selection.table().view();
        let mut state = GsubState {
            glyph_count: *count,
            random_state: Some(1),
            ..Default::default()
        };
        let base = GsubOptions {
            gdef,
            alternate_value: alternate,
            track_fallback_mark_metadata: true,
            track_substitution_provenance: true,
            ..Default::default()
        };
        let result = (|| {
            for (&lookup, resolution) in self.selection.lookups().iter().zip(&self.resolutions) {
                let ranged = resolution
                    .feature
                    .filter(|&tag| !resolution.required && plan.has_settings(tag));
                let Some(feature) = ranged else {
                    state.context_match_end = None;
                    table.apply_gsub(lookup, None, storage, base, &mut state)?;
                    continue;
                };
                let reverse = table.lookup(lookup)?.lookup_type() == 8;
                let mut iteration = if reverse { state.glyph_count } else { 0 };
                while if reverse {
                    iteration != 0
                } else {
                    iteration < state.glyph_count
                } {
                    let position = if reverse {
                        iteration -= 1;
                        iteration
                    } else {
                        iteration
                    };
                    let value = plan.value(feature, storage[position as usize].cluster);
                    if value == 0 {
                        if !reverse {
                            iteration += 1;
                        }
                        continue;
                    }
                    let before = state.glyph_count;
                    state.context_match_end = Some(0);
                    let options = GsubOptions {
                        alternate_value: value,
                        random_alternate: feature == RAND && value == u32::from(u16::MAX),
                        ..base
                    };
                    table.apply_gsub(lookup, Some(position), storage, options, &mut state)?;
                    if !reverse {
                        iteration += state.glyph_count.saturating_sub(before);
                        iteration = (iteration + 1).max(state.context_match_end.unwrap_or(0));
                    }
                }
            }
            Ok(())
        })();
        *count = state.glyph_count;
        result
    }
    fn position(
        &self,
        plan: &NativeFeaturePlan,
        glyphs: &mut [ShapingGlyph],
        attachments: &mut [GposAttachment],
        options: GposOptions<'_>,
    ) -> Result<(), FontError> {
        self.position_with_device(plan, glyphs, attachments, options, None)
    }
    fn position_with_device(
        &self,
        plan: &NativeFeaturePlan,
        glyphs: &mut [ShapingGlyph],
        attachments: &mut [GposAttachment],
        options: GposOptions<'_>,
        device: Option<crate::font::layout::GposDeviceFrame<'_>>,
    ) -> Result<(), FontError> {
        let table = self.selection.table().view();
        let mut state = GposState::default();
        for (&lookup, resolution) in self.selection.lookups().iter().zip(&self.resolutions) {
            if let Some(feature) = resolution
                .feature
                .filter(|&tag| !resolution.required && plan.has_settings(tag))
            {
                for position in 0..glyphs.len() {
                    if plan.value(feature, glyphs[position].cluster) != 0 {
                        table.apply_gpos(
                            lookup,
                            Some(position as u32),
                            glyphs,
                            attachments,
                            options,
                            device,
                            &mut state,
                        )?;
                    }
                }
            } else {
                table.apply_gpos(
                    lookup,
                    None,
                    glyphs,
                    attachments,
                    options,
                    device,
                    &mut state,
                )?;
            }
        }
        Ok(())
    }
}
