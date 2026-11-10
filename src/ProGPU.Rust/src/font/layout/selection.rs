//! Original native script/language/feature and variation selection contracts.
use super::{FontError, LayoutTable, OwnedLayoutTable, Tag, fits, u16_at, u32_at};

/// Explicit native selection input. Coordinates are original signed F2DOT14
/// values, already normalized by the caller's selected font generation.
#[derive(Clone, Copy, Debug)]
pub struct SelectionRequest<'a> {
    /// Exact script tag, falling back only to the original uppercase DFLT tag.
    pub script: Tag,
    /// Exact language tag; zero selects the default LangSys directly.
    pub language: Tag,
    /// Explicit feature tags; this layer does not invent shaping defaults.
    pub features: &'a [Tag],
    /// Exact normalized axis coordinates, retaining their original order.
    pub coordinates: &'a [i16],
}
impl Default for SelectionRequest<'_> {
    fn default() -> Self {
        Self {
            script: Tag::new(*b"DFLT"),
            language: Tag(0),
            features: &[],
            coordinates: &[],
        }
    }
}
fn tagged(data: &[u8], list: usize, base: usize, tag: Tag) -> Result<Option<usize>, FontError> {
    if !fits(data, list, 2) {
        return Err(FontError::InvalidFace);
    }
    let count = usize::from(u16_at(data, list));
    if !fits(data, list + 2, count * 6) {
        return Err(FontError::InvalidFace);
    }
    for i in 0..count {
        let at = list + 2 + i * 6;
        if u32_at(data, at) != tag.0 {
            continue;
        }
        let relative = usize::from(u16_at(data, at + 4));
        let offset = base.checked_add(relative).ok_or(FontError::InvalidFace)?;
        if relative == 0 || !fits(data, offset, 2) {
            return Err(FontError::InvalidFace);
        }
        return Ok(Some(offset));
    }
    Ok(None)
}
fn conditions(data: &[u8], at: usize, coordinates: &[i16]) -> bool {
    if !fits(data, at, 2) {
        return false;
    }
    let count = usize::from(u16_at(data, at));
    if !fits(data, at + 2, count * 4) {
        return false;
    }
    for i in 0..count {
        let Some(p) = at.checked_add(u32_at(data, at + 2 + i * 4) as usize) else {
            return false;
        };
        if !fits(data, p, 8) || u16_at(data, p) != 1 {
            return false;
        }
        let Some(&value) = coordinates.get(usize::from(u16_at(data, p + 2))) else {
            return false;
        };
        if value < u16_at(data, p + 4) as i16 || value > u16_at(data, p + 6) as i16 {
            return false;
        }
    }
    true
}
fn substitutions(data: &[u8], at: Option<usize>, coordinates: &[i16]) -> Option<usize> {
    let at = at?;
    if !fits(data, at, 8) || u16_at(data, at) != 1 || u16_at(data, at + 2) != 0 {
        return None;
    }
    let count = u32_at(data, at + 4) as usize;
    if !fits(data, at + 8, count.checked_mul(8)?) {
        return None;
    }
    for i in 0..count {
        let record = at + 8 + i * 8;
        let relative = u32_at(data, record) as usize;
        if relative != 0
            && !at
                .checked_add(relative)
                .is_some_and(|p| conditions(data, p, coordinates))
        {
            continue;
        }
        let relative = u32_at(data, record + 4) as usize;
        if relative == 0 {
            return None;
        }
        let table = at.checked_add(relative)?;
        if !fits(data, table, 6) {
            return None;
        }
        if u16_at(data, table) != 1 || u16_at(data, table + 2) != 0 {
            continue;
        }
        return fits(data, table + 6, usize::from(u16_at(data, table + 4)) * 6).then_some(table);
    }
    None
}
// Some(Err) means the selected alternate is invalid: skip that feature, without
// falling back to its original table. None alone selects the original feature.
fn alternate(data: &[u8], table: Option<usize>, index: u16) -> Option<Result<usize, ()>> {
    let table = table?;
    for i in 0..usize::from(u16_at(data, table + 4)) {
        let record = table + 6 + i * 6;
        if u16_at(data, record) != index {
            continue;
        }
        return Some(
            table
                .checked_add(u32_at(data, record + 2) as usize)
                .filter(|&p| fits(data, p, 4))
                .ok_or(()),
        );
    }
    None
}
#[derive(Default)]
struct Selected {
    capacity: u32,
    written: usize,
    target_feature: Option<Tag>,
}
impl LayoutTable<'_> {
    fn select(
        &self,
        request: SelectionRequest<'_>,
        excluded: &[Tag],
        include_required: bool,
        mut output: Option<&mut [u16]>,
        target: Option<u16>,
    ) -> Result<Selected, FontError> {
        let data = self.bytes();
        let substitutions =
            substitutions(data, self.feature_variations_offset(), request.coordinates);
        let scripts = self.script_list_offset();
        let features = self.feature_list_offset();
        let mut script = tagged(data, scripts, scripts, request.script)?;
        if script.is_none() && request.script != Tag::new(*b"DFLT") {
            script = tagged(data, scripts, scripts, Tag::new(*b"DFLT"))?;
        }
        let mut result = Selected::default();
        let Some(script) = script else {
            return Ok(result);
        };
        if !fits(data, script, 4)
            || !fits(data, script + 4, usize::from(u16_at(data, script + 2)) * 6)
        {
            return Err(FontError::InvalidFace);
        }
        let mut language = if request.language.0 != 0 {
            tagged(data, script + 2, script, request.language)?
        } else {
            None
        };
        let default = usize::from(u16_at(data, script));
        if language.is_none() && default != 0 {
            language = Some(script.checked_add(default).ok_or(FontError::InvalidFace)?);
        }
        let Some(language) = language else {
            return Ok(result);
        };
        if !fits(data, language, 6) {
            return Err(FontError::InvalidFace);
        }
        let required = u16_at(data, language + 2);
        let indices = usize::from(u16_at(data, language + 4));
        if !fits(data, language + 6, indices * 2) || !fits(data, features, 2) {
            return Err(FontError::InvalidFace);
        }
        let count = u16_at(data, features);
        if !fits(data, features + 2, usize::from(count) * 6)
            || (required != u16::MAX && required >= count)
        {
            return Err(FontError::InvalidFace);
        }
        let mut append = |index: u16| -> Result<(), FontError> {
            let record = features + 2 + usize::from(index) * 6;
            let feature = match alternate(data, substitutions, index) {
                Some(Err(())) => return Ok(()),
                Some(Ok(p)) => p,
                None => {
                    let relative = usize::from(u16_at(data, record + 4));
                    let p = features
                        .checked_add(relative)
                        .ok_or(FontError::InvalidFace)?;
                    if relative == 0 || !fits(data, p, 4) {
                        return Err(FontError::InvalidFace);
                    }
                    p
                }
            };
            let count = usize::from(u16_at(data, feature + 2));
            if !fits(data, feature + 4, count * 2) {
                return Err(FontError::InvalidFace);
            }
            result.capacity = result
                .capacity
                .checked_add(count as u32)
                .ok_or(FontError::InvalidFace)?;
            if output.is_none() && target.is_none() {
                return Ok(());
            }
            for i in 0..count {
                let lookup = u16_at(data, feature + 4 + i * 2);
                if lookup >= self.lookup_count() {
                    return Err(FontError::InvalidFace);
                }
                if Some(lookup) == target {
                    result.target_feature = Some(Tag(u32_at(data, record)));
                }
                if let Some(ref mut out) = output
                    && !out[..result.written].contains(&lookup)
                {
                    out[result.written] = lookup;
                    result.written += 1;
                }
            }
            Ok(())
        };
        if include_required && required != u16::MAX {
            append(required)?;
        }
        for i in 0..indices {
            let index = u16_at(data, language + 6 + i * 2);
            if index >= count {
                return Err(FontError::InvalidFace);
            }
            if index == required {
                continue;
            }
            let tag = Tag(u32_at(data, features + 2 + usize::from(index) * 6));
            if request.features.contains(&tag) && !excluded.contains(&tag) {
                append(index)?;
            }
        }
        Ok(result)
    }
    /// Original safe upper bound, including required features and duplicates,
    /// capped by lookup count. Lookup IDs are validated later during selection.
    pub fn selection_capacity(&self, request: SelectionRequest<'_>) -> Result<u32, FontError> {
        Ok(self
            .select(request, &[], true, None, None)?
            .capacity
            .min(u32::from(self.lookup_count())))
    }
    /// Select required and requested features with no exclusions. Whole capacity
    /// preflight precedes writes. A later invalid lookup ID may leave the original
    /// unsorted output prefix on error; successful output is sorted and deduplicated.
    pub fn select_lookups_into(
        &self,
        request: SelectionRequest<'_>,
        output: &mut [u16],
    ) -> Result<u32, FontError> {
        self.select_lookups_excluding_into(request, &[], output)
    }
    /// Preserve required features even if excluded. Capacity admission still uses
    /// the original unexcluded upper bound. Unused output slots remain unchanged.
    pub fn select_lookups_excluding_into(
        &self,
        request: SelectionRequest<'_>,
        excluded: &[Tag],
        output: &mut [u16],
    ) -> Result<u32, FontError> {
        let capacity = self.selection_capacity(request)? as usize;
        if output.len() < capacity {
            return Err(FontError::Capacity);
        }
        let written = self
            .select(request, excluded, true, Some(output), None)?
            .written;
        output[..written].sort_unstable();
        Ok(written as u32)
    }
    /// Select one allowed non-required feature. Requires space for the complete
    /// lookup count before source validation. Retains source order (no final sort)
    /// and original prefix writes if a later source record fails.
    pub fn select_feature_into(
        &self,
        script: Tag,
        language: Tag,
        feature: Tag,
        coordinates: &[i16],
        output: &mut [u16],
    ) -> Result<u32, FontError> {
        if output.len() < usize::from(self.lookup_count()) {
            return Err(FontError::Capacity);
        }
        let request = SelectionRequest {
            script,
            language,
            features: &[feature],
            coordinates,
        };
        Ok(self
            .select(request, &[], false, Some(output), None)?
            .written as u32)
    }
    /// Query one non-required feature, rejecting an out-of-range lookup before
    /// table traversal. Required features retain their independent query.
    pub fn feature_contains_lookup(
        &self,
        script: Tag,
        language: Tag,
        feature: Tag,
        lookup: u16,
        coordinates: &[i16],
    ) -> Result<bool, FontError> {
        if lookup >= self.lookup_count() {
            return Err(FontError::InvalidArgument);
        }
        let request = SelectionRequest {
            script,
            language,
            features: &[feature],
            coordinates,
        };
        Ok(self
            .select(request, &[], false, None, Some(lookup))?
            .target_feature
            .is_some())
    }
    /// Return the exact required feature tag owning a lookup, or absence.
    pub fn required_feature_for_lookup(
        &self,
        script: Tag,
        language: Tag,
        lookup: u16,
        coordinates: &[i16],
    ) -> Result<Option<Tag>, FontError> {
        if lookup >= self.lookup_count() {
            return Err(FontError::InvalidArgument);
        }
        let request = SelectionRequest {
            script,
            language,
            features: &[],
            coordinates,
        };
        Ok(self
            .select(request, &[], true, None, Some(lookup))?
            .target_feature)
    }
}
/// Owned immutable selection retaining its exact source and input policy.
/// Candidate construction is atomic even when the caller-buffer API preserves
/// original partial writes. This is lookup selection, not a complete shaping plan.
#[derive(Clone, Debug)]
pub struct LookupSelection {
    table: OwnedLayoutTable,
    script: Tag,
    language: Tag,
    features: Vec<Tag>,
    excluded: Vec<Tag>,
    coordinates: Vec<i16>,
    lookups: Vec<u16>,
}
fn copy<T: Copy>(values: &[T]) -> Result<Vec<T>, FontError> {
    let mut out = Vec::new();
    out.try_reserve_exact(values.len())
        .map_err(|_| FontError::Capacity)?;
    out.extend_from_slice(values);
    Ok(out)
}
impl LookupSelection {
    /// Freeze exact source, coordinates and requested/excluded tag inventories.
    pub fn new(
        table: OwnedLayoutTable,
        request: SelectionRequest<'_>,
        excluded: &[Tag],
    ) -> Result<Self, FontError> {
        let view = table.view();
        let capacity = view.selection_capacity(request)? as usize;
        let mut lookups = Vec::new();
        lookups
            .try_reserve_exact(capacity)
            .map_err(|_| FontError::Capacity)?;
        lookups.resize(capacity, 0);
        let written = view.select_lookups_excluding_into(request, excluded, &mut lookups)?;
        lookups.truncate(written as usize);
        let features = copy(request.features)?;
        let coordinates = copy(request.coordinates)?;
        let excluded = copy(excluded)?;
        Ok(Self {
            table,
            script: request.script,
            language: request.language,
            features,
            excluded,
            coordinates,
            lookups,
        })
    }
    /// Original table/font generation, retained without copying font bytes.
    pub fn table(&self) -> &OwnedLayoutTable {
        &self.table
    }
    /// Exact original selection request, borrowing the frozen inventories.
    pub fn request(&self) -> SelectionRequest<'_> {
        SelectionRequest {
            script: self.script,
            language: self.language,
            features: &self.features,
            coordinates: &self.coordinates,
        }
    }
    /// Exact excluded tags; required features are never excluded by this contract.
    pub fn excluded(&self) -> &[Tag] {
        &self.excluded
    }
    /// Sorted, unique lookup IDs. Their lookup payloads still require validation.
    pub fn lookups(&self) -> &[u16] {
        &self.lookups
    }
}
