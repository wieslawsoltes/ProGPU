//! Owned connection between original native lookup selection and positioning.
use super::*;
use crate::font::layout::{LookupSelection, OwnedGdef, OwnedLayoutTable, SelectionRequest};

/// Immutable native GPOS selection and actual GDEF/font generations. This is the
/// positioning stage over already-shaped glyphs, not nominal metric generation,
/// script orchestration, fallback marks, kerning fallback or a managed shape plan.
#[derive(Clone, Debug)]
pub struct NativePositioningPlan {
    selection: LookupSelection,
    gdef: Option<OwnedGdef>,
}
impl NativePositioningPlan {
    /// Retain explicit native lookup selection and caller-selected GDEF policy.
    /// Font-backed GDEF and GPOS must retain the same underlying selected face.
    /// Standalone table owners remain supported without fabricated font identity.
    pub fn new(selection: LookupSelection, gdef: Option<OwnedGdef>) -> Result<Self, FontError> {
        if let Some((font, tag)) = selection.table().source() {
            if tag != Tag::new(*b"GPOS") {
                return Err(FontError::InvalidArgument);
            }
            if let Some(source) = gdef.as_ref().and_then(OwnedGdef::source) {
                if !std::sync::Arc::ptr_eq(&font.data, &source.data)
                    || font.face_index() != source.face_index()
                {
                    return Err(FontError::InvalidArgument);
                }
            }
        }
        Ok(Self { selection, gdef })
    }
    /// Freeze original script/language/features/coordinates against a selected
    /// font. Missing GPOS is None; present malformed metadata rejects. GDEF is
    /// used as provided by the face; compatibility blocklists remain explicit
    /// policy through `new` rather than silently changing source classification.
    pub fn from_face(
        font: SfntFace,
        request: SelectionRequest<'_>,
        excluded: &[Tag],
    ) -> Result<Option<Self>, FontError> {
        let Some(table) = OwnedLayoutTable::from_face(font.clone(), Tag::new(*b"GPOS"))? else {
            return Ok(None);
        };
        let gdef = OwnedGdef::from_face(font)?;
        Self::new(LookupSelection::new(table, request, excluded)?, gdef).map(Some)
    }
    /// Exact immutable source selection and sorted original native lookup order.
    pub fn selection(&self) -> &LookupSelection {
        &self.selection
    }
    /// Exact retained font object for a synchronous native device frame.
    pub fn font(&self) -> Option<&SfntFace> {
        self.selection.table().source().map(|(f, _)| f)
    }
    /// Already validated GDEF view, retaining this plan's actual source owner.
    pub fn gdef(&self) -> Option<Gdef<'_>> {
        self.gdef.as_ref().map(OwnedGdef::view)
    }
    /// Apply every selected native lookup without allocation, keeping original
    /// partial mutation on failure. `frame` supplies direction and ppem only;
    /// source/GDEF/coordinates come from the frozen selection. Per-lookup filters
    /// remain available through LayoutTable::apply_gpos. Resolve attachments once
    /// after all selected positioning stages using `resolve_gpos_attachments`.
    pub fn apply(
        &self,
        glyphs: &mut [ShapingGlyph],
        attachments: &mut [GposAttachment],
        frame: GposOptions<'_>,
        device: Option<GposDeviceFrame<'_>>,
        state: &mut GposState,
    ) -> Result<(), FontError> {
        state.applied = false;
        let options = GposOptions {
            direction: frame.direction,
            pixels_per_em: frame.pixels_per_em,
            gdef: self.gdef(),
            font: self.font(),
            normalized_coordinates: self.selection.request().coordinates,
            ..Default::default()
        };
        if let Some(device) = device {
            device.validate(options)?;
        }
        let table = self.selection.table().view();
        for &lookup in self.selection.lookups() {
            let mut current = GposState::default();
            let result = table.apply_gpos(
                lookup,
                None,
                glyphs,
                attachments,
                options,
                device,
                &mut current,
            );
            state.applied |= current.applied;
            result?;
        }
        Ok(())
    }
}
