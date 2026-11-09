//! Current original C# raw feature policy. This deliberately does not use the
//! stricter native LayoutTable admission or its different fallback/variation rules.
use super::managed_context::{self, ContextData, ManagedContextView};
use super::{Arc, FontError, ManagedGlyphSetDigest, SfntFace, Storage, Tag, fits, u16_at, u32_at};
use std::collections::{HashMap, HashSet};

/// An original four-code-unit managed feature tag and signed feature value.
/// UTF-16 preserves ordinal string identity, including unpaired surrogates.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash)]
pub struct ManagedFeatureSetting {
    tag: [u16; 4],
    value: i32,
}
impl ManagedFeatureSetting {
    /// Match OpenTypeFeatureSetting's non-whitespace, four-code-unit admission.
    pub fn new(tag: [u16; 4], value: i32) -> Result<Self, FontError> {
        if tag.iter().copied().all(whitespace) {
            return Err(FontError::InvalidArgument);
        }
        Ok(Self { tag, value })
    }
    /// Exact original UTF-16 tag.
    pub fn tag(self) -> [u16; 4] {
        self.tag
    }
    /// Original signed value; zero disables the feature.
    pub fn value(self) -> i32 {
        self.value
    }
}
/// Explicit current managed selection policy. Coordinates are the original
/// font instance's normalized F2Dot14 axis inventory, in original axis order.
#[derive(Clone, Copy, Debug)]
pub struct ManagedSelectionRequest<'a> {
    /// Original script string; the four-unit tag conversion and exact `hang`
    /// comparison intentionally have different semantics.
    pub script: &'a [u16],
    /// Original optional language string, before invariant normalization.
    pub language: Option<&'a [u16]>,
    /// Ordered settings, retaining duplicates and signed values.
    pub settings: &'a [ManagedFeatureSetting],
    /// Frozen original font coordinates; missing axes do not imply zero.
    pub coordinates: &'a [i16],
    /// Original GSUB extension type 7 or GPOS extension type 9; raw values remain raw.
    pub extension_lookup_type: u16,
}
impl Default for ManagedSelectionRequest<'_> {
    fn default() -> Self {
        Self {
            script: &[68, 70, 76, 84],
            language: None,
            settings: &[],
            coordinates: &[],
            extension_lookup_type: 7,
        }
    }
}
/// One selected lookup. An ID is not proof that its execution payload is valid.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ManagedEnabledLookup {
    /// Original lookup index, including out-of-range values retained by selection.
    pub index: u16,
    /// Original feature value after required/global precedence.
    pub value: i32,
    /// Original ASCII-decoded feature tag (non-ASCII bytes become `?`).
    pub tag: [u8; 4],
    /// Whether the original required feature owns this lookup.
    pub required: bool,
    /// Original optional negative-only filter, not exact lookup admission.
    pub digest: Option<ManagedGlyphSetDigest>,
}
/// Atomically published managed feature selection retaining the original source
/// generation, complete request and optional original managed context preparation.
/// Lookup execution remains separate; this is not a complete shaping plan.
#[derive(Clone, Debug)]
pub struct ManagedLookupSelection {
    storage: Storage,
    script: Vec<u16>,
    language: Option<Vec<u16>>,
    settings: Vec<ManagedFeatureSetting>,
    coordinates: Vec<i16>,
    extension: u16,
    uses_variations: bool,
    lookups: Vec<ManagedEnabledLookup>,
    contexts: Vec<(usize, ContextData)>,
}
fn copy<T: Copy>(v: &[T]) -> Result<Vec<T>, FontError> {
    let mut out = Vec::new();
    out.try_reserve_exact(v.len())
        .map_err(|_| FontError::Capacity)?;
    out.extend_from_slice(v);
    Ok(out)
}
impl ManagedLookupSelection {
    /// Select from immutable raw table bytes with the original managed admission.
    pub fn new(
        data: impl Into<Arc<[u8]>>,
        request: ManagedSelectionRequest<'_>,
    ) -> Result<Self, FontError> {
        Self::build(Storage::Bytes(data.into()), request)
    }
    /// Retain the exact selected font generation without copying its table bytes.
    pub fn from_face(
        face: SfntFace,
        tag: Tag,
        request: ManagedSelectionRequest<'_>,
    ) -> Result<Option<Self>, FontError> {
        Storage::from_face(face, tag)
            .map(|storage| Self::build(storage, request))
            .transpose()
    }
    fn build(storage: Storage, request: ManagedSelectionRequest<'_>) -> Result<Self, FontError> {
        if storage.bytes().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        let (lookups, uses_variations) = select(storage.bytes(), request)?;
        let mut contexts = Vec::new();
        for (position, lookup) in lookups.iter().enumerate() {
            if let Some(context) = managed_context::prepare(
                storage.bytes(),
                lookup.index,
                request.extension_lookup_type,
            )? {
                contexts.try_reserve(1).map_err(|_| FontError::Capacity)?;
                contexts.push((position, context));
            }
        }
        Ok(Self {
            storage,
            script: copy(request.script)?,
            language: request.language.map(copy).transpose()?,
            settings: copy(request.settings)?,
            coordinates: copy(request.coordinates)?,
            extension: request.extension_lookup_type,
            uses_variations,
            lookups,
            contexts,
        })
    }
    /// Frozen original request, including duplicate settings and null language.
    pub fn request(&self) -> ManagedSelectionRequest<'_> {
        ManagedSelectionRequest {
            script: &self.script,
            language: self.language.as_deref(),
            settings: &self.settings,
            coordinates: &self.coordinates,
            extension_lookup_type: self.extension,
        }
    }
    /// Exact retained raw table bytes.
    pub fn bytes(&self) -> &[u8] {
        self.storage.bytes()
    }
    /// Exact selected face and table identity, if constructed from a face.
    pub fn source(&self) -> Option<(&SfntFace, Tag)> {
        self.storage.source()
    }
    /// Sorted unique lookup IDs and their original winning metadata.
    pub fn lookups(&self) -> &[ManagedEnabledLookup] {
        &self.lookups
    }
    /// Original optional context for the selected entry at this slice position.
    /// The lookup ID itself remains in `lookups()[position].index`.
    pub fn context(&self, position: usize) -> Option<ManagedContextView<'_>> {
        let found = self
            .contexts
            .binary_search_by_key(&position, |(position, _)| *position)
            .ok()?;
        Some(self.contexts[found].1.view(self.storage.bytes()))
    }
    /// A matching null substitution table is still an active managed variation.
    pub fn uses_feature_variations(&self) -> bool {
        self.uses_variations
    }
}

fn whitespace(c: u16) -> bool {
    matches!(c, 9..=13 | 32 | 0x85 | 0xa0 | 0x1680 | 0x2000..=0x200a | 0x2028..=0x2029 | 0x202f | 0x205f | 0x3000)
}
fn string_tag(s: &[u16]) -> u32 {
    let mut tag = 0;
    for i in 0..4 {
        tag |= u32::from(*s.get(i).unwrap_or(&32)) << (24 - i * 8);
    }
    tag
}
fn language_tag(s: &[u16]) -> u32 {
    // All recognized original language spellings are ASCII and at most ten
    // units. Invariant lowercase may map Kelvin sign to ASCII k; UTF-16 inputs
    // outside this exact spelling inventory remain the original dflt fallback.
    let mut normalized = [0u8; 10];
    if s.len() > normalized.len() {
        return Tag::new(*b"dflt").0;
    }
    for (out, &c) in normalized.iter_mut().zip(s) {
        let Some(c) = char::from_u32(u32::from(c)) else {
            return Tag::new(*b"dflt").0;
        };
        let mut lower = c.to_lowercase();
        let first = lower.next().unwrap();
        if !first.is_ascii() || lower.next().is_some() {
            return Tag::new(*b"dflt").0;
        }
        *out = if first == '_' { b'-' } else { first as u8 };
    }
    let tag = match &normalized[..s.len()] {
        b"az" | b"az-latn" => *b"AZE ",
        b"de" => *b"DEU ",
        b"dv" => *b"DHV ",
        b"fa" => *b"FAR ",
        b"ja" => *b"JAN ",
        b"nl" => *b"NLD ",
        b"pl" => *b"PLK ",
        b"ro" => *b"ROM ",
        b"tr" => *b"TRK ",
        b"zh" | b"zh-cn" | b"zh-sg" | b"zh-hans" => *b"ZHS ",
        b"zh-tw" | b"zh-hant" => *b"ZHT ",
        b"zh-hk" | b"zh-mo" | b"zh-hant-hk" | b"zh-hant-mo" => *b"ZHH ",
        _ => *b"dflt",
    };
    Tag::new(tag).0
}
fn global(tag: &[u8; 4], hangul: bool) -> bool {
    matches!(
        tag,
        b"rand" | b"ltra" | b"ltrm" | b"rtla" | b"rtlm" | b"vert" | b"vrt2"
    ) || hangul && matches!(tag, b"ljmo" | b"vjmo" | b"tjmo")
}
fn ascii_tag(data: &[u8], at: usize) -> [u8; 4] {
    std::array::from_fn(|i| {
        if data[at + i].is_ascii() {
            data[at + i]
        } else {
            b'?'
        }
    })
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
        let relative = u32_at(data, at + 2 + i * 4) as usize;
        if relative > i32::MAX as usize {
            return false;
        }
        let p = at + relative;
        if !fits(data, p, 8) || u16_at(data, p) != 1 {
            return false;
        }
        let Some(&c) = coordinates.get(usize::from(u16_at(data, p + 2))) else {
            return false;
        };
        if c < u16_at(data, p + 4) as i16 || c > u16_at(data, p + 6) as i16 {
            return false;
        }
    }
    true
}
fn substitutions(
    data: &[u8],
    coordinates: &[i16],
) -> Result<Option<HashMap<u16, usize>>, FontError> {
    if !fits(data, 0, 14) || u16_at(data, 0) != 1 || u16_at(data, 2) < 1 {
        return Ok(None);
    }
    let at = u32_at(data, 10) as usize;
    if at == 0 || at > i32::MAX as usize || !fits(data, at, 8) || u32_at(data, at) != 0x10000 {
        return Ok(None);
    }
    let count = u32_at(data, at + 4) as usize;
    if count > i32::MAX as usize / 8 || !fits(data, at + 8, count * 8) {
        return Ok(None);
    }
    for i in 0..count {
        let condition = u32_at(data, at + 8 + i * 8) as usize;
        let substitution = u32_at(data, at + 12 + i * 8) as usize;
        if condition > i32::MAX as usize
            || substitution > i32::MAX as usize
            || condition != 0 && !conditions(data, at + condition, coordinates)
        {
            continue;
        }
        if substitution == 0 {
            return Ok(Some(HashMap::new()));
        }
        let p = at + substitution;
        if !fits(data, p, 6) {
            return Ok(None);
        }
        if u32_at(data, p) != 0x10000 {
            continue;
        }
        let count = usize::from(u16_at(data, p + 4));
        if !fits(data, p + 6, count * 6) {
            return Ok(None);
        }
        let mut result = HashMap::new();
        result.try_reserve(count).map_err(|_| FontError::Capacity)?;
        for i in 0..count {
            let q = p + 6 + i * 6;
            let relative = u32_at(data, q + 2) as usize;
            if relative <= i32::MAX as usize - p {
                result.insert(u16_at(data, q), p + relative);
            }
        }
        return Ok(Some(result));
    }
    Ok(None)
}
fn language_features(
    data: &[u8],
    script: &[u16],
    language: Option<&[u16]>,
) -> Result<(Option<HashSet<u16>>, u16), FontError> {
    let none = u16::MAX;
    if !fits(data, 4, 2) {
        return Ok((None, none));
    }
    let list = usize::from(u16_at(data, 4));
    if !fits(data, list, 2) {
        return Ok((None, none));
    }
    let requested = string_tag(script);
    let mut offsets = [0; 4];
    for i in 0..usize::from(u16_at(data, list)) {
        let p = list + 2 + i * 6;
        if !fits(data, p, 6) {
            break;
        }
        let tag = u32_at(data, p);
        let offset = list + usize::from(u16_at(data, p + 4));
        if tag == requested {
            offsets[0] = offset;
        } else if tag == Tag::new(*b"DFLT").0 {
            offsets[1] = offset;
        } else if tag == Tag::new(*b"dflt").0 {
            offsets[2] = offset;
        } else if tag == Tag::new(*b"latn").0 {
            offsets[3] = offset;
        }
    }
    let script = offsets.into_iter().find(|&p| p != 0).unwrap_or(0);
    let empty = |required| Ok((Some(HashSet::new()), required));
    if !fits(data, script, 4) {
        return empty(none);
    }
    let relative = usize::from(u16_at(data, script));
    let mut language_offset = if relative == 0 { 0 } else { script + relative };
    if let Some(language) = language.filter(|s| !s.iter().copied().all(whitespace)) {
        let requested = language_tag(language);
        for i in 0..usize::from(u16_at(data, script + 2)) {
            let p = script + 4 + i * 6;
            if !fits(data, p, 6) {
                break;
            }
            if u32_at(data, p) == requested {
                language_offset = script + usize::from(u16_at(data, p + 4));
                break;
            }
        }
    }
    let p = language_offset;
    if p == 0 || !fits(data, p, 6) {
        return empty(none);
    }
    let required = u16_at(data, p + 2);
    let count = usize::from(u16_at(data, p + 4));
    if !fits(data, p + 6, count * 2) {
        return Ok((None, required));
    }
    // The managed source throws on an otherwise admitted truncated top header.
    // Fail atomically with a typed error instead of indexing outside the input.
    if !fits(data, 6, 4) {
        return Err(FontError::InvalidFace);
    }
    for boundary in [usize::from(u16_at(data, 6)), usize::from(u16_at(data, 8))] {
        if p < boundary && p + 6 + count * 2 > boundary {
            return empty(none);
        }
    }
    let mut set = HashSet::new();
    set.try_reserve(count + 1)
        .map_err(|_| FontError::Capacity)?;
    for i in 0..count {
        set.insert(u16_at(data, p + 6 + i * 2));
    }
    if required != none {
        set.insert(required);
    }
    Ok((Some(set), required))
}
struct Selection<'a> {
    data: &'a [u8],
    list: usize,
    substitutions: Option<HashMap<u16, usize>>,
    positions: HashMap<u16, usize>,
    result: Vec<ManagedEnabledLookup>,
}
impl Selection<'_> {
    fn add(&mut self, feature: u16, value: i32, required: bool) -> Result<(), FontError> {
        let record = self.list + 2 + usize::from(feature) * 6;
        if !fits(self.data, record, 6) {
            return Ok(());
        }
        let tag = ascii_tag(self.data, record);
        let offset = self
            .substitutions
            .as_ref()
            .and_then(|s| s.get(&feature).copied())
            .unwrap_or_else(|| self.list + usize::from(u16_at(self.data, record + 4)));
        if !fits(self.data, offset, 4) {
            return Ok(());
        }
        for i in 0..usize::from(u16_at(self.data, offset + 2)) {
            let p = offset + 4 + i * 2;
            if !fits(self.data, p, 2) {
                break;
            }
            let index = u16_at(self.data, p);
            let mut entry = ManagedEnabledLookup {
                index,
                value,
                tag,
                required,
                digest: None,
            };
            if let Some(&position) = self.positions.get(&index) {
                let current = self.result[position];
                if current.required && !required {
                    continue;
                }
                if !global(&current.tag, true) || global(&tag, true) {
                    entry.required |= current.required;
                    self.result[position] = entry;
                }
            } else {
                self.positions
                    .try_reserve(1)
                    .map_err(|_| FontError::Capacity)?;
                self.result
                    .try_reserve(1)
                    .map_err(|_| FontError::Capacity)?;
                self.positions.insert(index, self.result.len());
                self.result.push(entry);
            }
        }
        Ok(())
    }
}
fn select(
    data: &[u8],
    request: ManagedSelectionRequest<'_>,
) -> Result<(Vec<ManagedEnabledLookup>, bool), FontError> {
    let substitutions = substitutions(data, request.coordinates)?;
    let uses = substitutions.is_some();
    if !fits(data, 6, 2) {
        return Ok((vec![], uses));
    }
    let list = usize::from(u16_at(data, 6));
    if !fits(data, list, 2) {
        return Ok((vec![], uses));
    }
    let mut requested = HashMap::new();
    requested
        .try_reserve(request.settings.len())
        .map_err(|_| FontError::Capacity)?;
    for setting in request.settings {
        requested.insert(setting.tag, setting.value);
    }
    let count = u16_at(data, list);
    let (allowed, required) = language_features(data, request.script, request.language)?;
    let mut selection = Selection {
        data,
        list,
        substitutions,
        positions: HashMap::new(),
        result: vec![],
    };
    if required < count {
        selection.add(required, 1, true)?;
    }
    for setting in request.settings {
        if setting.value == 0 || requested[&setting.tag] != setting.value {
            continue;
        }
        let ascii = setting.tag.map(|c| u8::try_from(c).unwrap_or(255));
        let global = global(&ascii, request.script == [104, 97, 110, 103]);
        for i in 0..count {
            if allowed.as_ref().is_some_and(|s| !s.contains(&i)) && !global {
                continue;
            }
            let p = list + 2 + usize::from(i) * 6;
            if !fits(data, p, 6) {
                break;
            }
            if ascii_tag(data, p).map(u16::from) == setting.tag {
                selection.add(i, setting.value, false)?;
            }
        }
    }
    selection.result.sort_unstable_by_key(|entry| entry.index);
    for entry in &mut selection.result {
        entry.digest = lookup_digest(data, entry.index, request.extension_lookup_type);
    }
    Ok((selection.result, uses))
}
fn lookup_digest(data: &[u8], index: u16, extension: u16) -> Option<ManagedGlyphSetDigest> {
    let (lookup, kind, count) = managed_context::raw_lookup(data, index)?;
    let count = usize::from(count);
    if count == 0 {
        return None;
    }
    let mut digest = ManagedGlyphSetDigest::default();
    for i in 0..count {
        let p = lookup + 6 + i * 2;
        if !fits(data, p, 2) {
            return None;
        }
        let mut sub = lookup + usize::from(u16_at(data, p));
        if kind == extension {
            if !fits(data, sub, 8) || u16_at(data, sub) != 1 {
                return None;
            }
            let relative = u32_at(data, sub + 4) as usize;
            if relative > i32::MAX as usize {
                return None;
            }
            sub += relative;
        }
        if !fits(data, sub, 4) {
            return None;
        }
        let coverage = sub + usize::from(u16_at(data, sub + 2));
        if !fits(data, coverage, 4) {
            return None;
        }
        let format = u16_at(data, coverage);
        let count = usize::from(u16_at(data, coverage + 2));
        let stride = match format {
            1 => 2,
            2 => 6,
            _ => return None,
        };
        if !fits(data, coverage + 4, count * stride) {
            return None;
        }
        for i in 0..count {
            let p = coverage + 4 + i * stride;
            if format == 1 {
                digest.add(u16_at(data, p));
            } else {
                digest.add_range(u16_at(data, p), u16_at(data, p + 2));
            }
        }
    }
    Some(digest)
}
