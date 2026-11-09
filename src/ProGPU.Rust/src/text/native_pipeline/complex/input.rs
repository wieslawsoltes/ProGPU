//! Original native initial mapping, complex-script preprocessing and final
//! default-ignorable handling. All source indices and mutation order are retained.
use super::*;
#[path = "../../script_actions/vowel_data.rs"]
mod vowels;
pub(super) fn unicode_error(error: unicode::UnicodeError) -> FontError {
    if error == unicode::UnicodeError::Capacity {
        FontError::Capacity
    } else {
        FontError::InvalidArgument
    }
}
fn scalar(bytes: &[u8], index: usize) -> u32 {
    u32::from_le_bytes(bytes[index * 4..index * 4 + 4].try_into().unwrap())
}
pub(super) struct Mapping<'a> {
    prefix: u32,
    single: u32,
    decomposition: &'a [u8],
}
impl Mapping<'_> {
    pub(super) fn len(&self) -> usize {
        usize::from(self.prefix != 0)
            + if self.decomposition.is_empty() {
                1
            } else {
                self.decomposition.len() / 4
            }
    }
    pub(super) fn at(&self, mut index: usize) -> u32 {
        if self.prefix != 0 {
            if index == 0 {
                return self.prefix;
            }
            index -= 1;
        }
        if self.decomposition.is_empty() {
            self.single
        } else {
            scalar(self.decomposition, index)
        }
    }
}
pub(super) fn mapping<'a>(
    font: &SfntFace,
    cp: u32,
    kind: NativeComplexKind,
    normalization: Option<NormalizationData<'a>>,
) -> Result<Mapping<'a>, FontError> {
    let id = font.glyph_index(cp).ok_or(FontError::InvalidFace)?;
    let decomposition = normalization
        .and_then(|n| n.decomposition(cp))
        .unwrap_or(&[]);
    let prefix =
        if kind == NativeComplexKind::Khmer && matches!(cp, 0x17be..=0x17c0 | 0x17c4 | 0x17c5) {
            0x17c1
        } else {
            0
        };
    let mut result = Mapping {
        prefix,
        single: cp,
        decomposition: &[],
    };
    if kind == NativeComplexKind::Indic
        && !decomposition.is_empty()
        && unicode_mark(scalar(decomposition, 0))
    {
        result.decomposition = decomposition;
    } else if id == 0 && cp == 0x2011 {
        if font.glyph_index(0x2010).ok_or(FontError::InvalidFace)? != 0 {
            result.single = 0x2010;
        } else {
            result.decomposition = decomposition;
        }
    } else if id == 0 {
        result.decomposition = decomposition;
    }
    Ok(result)
}
pub(super) fn mapped_count(
    font: &SfntFace,
    input: &[UnicodeScalar],
    kind: NativeComplexKind,
    normalization: Option<NormalizationData<'_>>,
) -> Result<usize, FontError> {
    let mut count = 0usize;
    for s in input {
        count = count
            .checked_add(mapping(font, s.code_point, kind, normalization)?.len())
            .filter(|&n| n <= u32::MAX as usize)
            .ok_or(FontError::InvalidArgument)?;
    }
    Ok(count)
}
pub(super) fn needs_composition(input: &[UnicodeScalar], data: NormalizationData<'_>) -> bool {
    let (mut starter, mut previous, mut has_starter) = (0, 0, false);
    for s in input {
        if let Some(decomposition) = data.decomposition(s.code_point).filter(|d| !d.is_empty()) {
            let mut start = scalar(decomposition, 0);
            let mut count = 1;
            let mut prior = 0;
            for i in 1..decomposition.len() / 4 {
                let cp = scalar(decomposition, i);
                let class = unicode::canonical_combining_class(cp);
                if !(prior != 0 && prior >= class)
                    && let Some(composed) = data.compose(start, cp)
                {
                    start = composed;
                    continue;
                }
                count += 1;
                if class == 0 {
                    start = cp;
                }
                prior = class;
            }
            if count != 1 || start != s.code_point {
                return true;
            }
        }
        let class = unicode::canonical_combining_class(s.code_point);
        if class != 0 && previous > class
            || has_starter
                && !(previous != 0 && previous >= class)
                && data.compose(starter, s.code_point).is_some()
        {
            return true;
        }
        if class == 0 {
            starter = s.code_point;
            has_starter = true;
        }
        previous = class;
    }
    false
}
pub(super) fn normalize(
    input: &[UnicodeScalar],
    kind: NativeComplexKind,
    data: NormalizationData<'_>,
    clusters: &mut [GraphemeCluster],
    output: &mut [UnicodeScalar],
) -> Result<usize, FontError> {
    let count =
        grapheme::segment_into(input, policy(kind), clusters).map_err(unicode_error)? as usize;
    let mut written = 0;
    for cluster in &clusters[..count] {
        let source = &input
            [cluster.scalar_index as usize..(cluster.scalar_index + cluster.scalar_count) as usize];
        if kind == NativeComplexKind::Indic
            && source
                .iter()
                .any(|s| matches!(s.code_point, 0x931 | 0x9dc | 0x9dd | 0xb94))
        {
            if source.len() > output.len() - written {
                return Err(FontError::Capacity);
            }
            output[written..written + source.len()].copy_from_slice(source);
            written += source.len();
        } else {
            written += unicode::normalization::normalize_into(
                source,
                &data,
                NormalizationForm::CanonicalComposition,
                &mut output[written..],
            )
            .map_err(unicode_error)? as usize;
        }
    }
    Ok(written)
}
pub(super) fn policy(kind: NativeComplexKind) -> GraphemePolicy {
    if matches!(kind, NativeComplexKind::Indic | NativeComplexKind::Myanmar) {
        GraphemePolicy::ManagedCompatible
    } else {
        GraphemePolicy::Unicode17
    }
}
pub(super) fn map(
    font: &SfntFace,
    input: &[UnicodeScalar],
    kind: NativeComplexKind,
    level: NativeClusterLevel,
    normalization: Option<NormalizationData<'_>>,
    clusters: &mut [GraphemeCluster],
    b: &mut Buffer<'_>,
) -> Result<(), FontError> {
    let count =
        grapheme::segment_into(input, policy(kind), clusters).map_err(unicode_error)? as usize;
    let mut mapped = 0;
    for cluster in &clusters[..count] {
        let mut script_cluster = cluster.input_index;
        let mut previous = 0;
        for offset in 0..cluster.scalar_count as usize {
            let s = input[cluster.scalar_index as usize + offset];
            let cp = s.code_point;
            if kind == NativeComplexKind::Khmer
                && (matches!(cp, 0x200c | 0x200d)
                    || previous == 0x17d2
                        && matches!(
                            ManagedIndicProperties::for_raw_code_point(cp).category(),
                            1 | 2 | 10 | 11 | 15
                        ))
            {
                script_cluster = s.input_index;
            }
            if offset != 0
                && mapped != 0
                && matches!(cp, 0xfe00..=0xfe0f | 0xe0100..=0xe01ef)
                && let Some(id) = font.variation_glyph(b.storage[mapped - 1].code_point, cp)
            {
                b.storage[mapped - 1].glyph_id = u32::from(id);
                previous = cp;
                continue;
            }
            let mapping = mapping(font, cp, kind, normalization)?;
            for i in 0..mapping.len() {
                if mapped >= b.storage.len() {
                    return Err(FontError::Capacity);
                }
                let code = mapping.at(i);
                let id = font.glyph_index(code).ok_or(FontError::InvalidFace)?;
                let id = spaces::map(font, code, id)?;
                let source = if kind == NativeComplexKind::Khmer
                    && matches!(
                        level,
                        NativeClusterLevel::MonotoneCharacters | NativeClusterLevel::Characters
                    ) {
                    s.input_index
                } else {
                    script_cluster
                };
                b.storage[mapped] = ShapingGlyph {
                    glyph_id: u32::from(id),
                    code_point: code,
                    cluster: source as i32,
                    ..Default::default()
                };
                mapped += 1;
            }
            previous = cp;
        }
    }
    b.count = mapped;
    Ok(())
}
pub(super) fn directional(
    font: &SfntFace,
    g: &mut [ShapingGlyph],
    direction: GposDirection,
    vertical_substitution: bool,
) -> Result<(), FontError> {
    let backward = matches!(
        direction,
        GposDirection::RightToLeft | GposDirection::BottomToTop
    );
    let vertical = !vertical_substitution
        && matches!(
            direction,
            GposDirection::TopToBottom | GposDirection::BottomToTop
        );
    for glyph in g {
        let mut cp = glyph.code_point;
        if backward {
            let next = unicode::mirrored_code_point(cp);
            if next != cp && font.glyph_index(next).ok_or(FontError::InvalidFace)? != 0 {
                cp = next;
            }
        }
        if vertical {
            let next = unicode::vertical_code_point(cp);
            if next != cp && font.glyph_index(next).ok_or(FontError::InvalidFace)? != 0 {
                cp = next;
            }
        }
        if cp != glyph.code_point {
            glyph.glyph_id = u32::from(font.glyph_index(cp).ok_or(FontError::InvalidFace)?);
            glyph.code_point = cp;
        }
    }
    Ok(())
}
pub(super) fn modified_class(cp: u32) -> u8 {
    if matches!(cp, 0x1a60 | 0xfc6) {
        return 254;
    }
    if cp == 0xf39 {
        return 127;
    }
    match unicode::canonical_combining_class(cp) {
        10 => 22,
        11 => 15,
        12 => 16,
        13 => 17,
        14 => 23,
        15 => 18,
        16 => 19,
        17 => 20,
        18 => 21,
        19 => 14,
        20 => 24,
        21 => 12,
        22 => 25,
        23 => 13,
        24 => 10,
        25 => 11,
        27..=32 => unicode::canonical_combining_class(cp) + 1,
        33 => 27,
        84 => 4,
        91 => 5,
        103 => 3,
        130 => 132,
        132 => 131,
        v => v,
    }
}
fn reorder_marks(g: &mut [ShapingGlyph], level: NativeClusterLevel) {
    let mut start = 0;
    while start < g.len() {
        while start < g.len() && modified_class(g[start].code_point) == 0 {
            start += 1;
        }
        let mut end = start;
        while end < g.len() && modified_class(g[end].code_point) != 0 {
            end += 1;
        }
        for i in start + 1..end {
            let value = g[i];
            let class = modified_class(value.code_point);
            let mut destination = i;
            let mut crossed = i32::MAX;
            while destination > start && modified_class(g[destination - 1].code_point) > class {
                crossed = crossed.min(g[destination - 1].cluster);
                g[destination] = g[destination - 1];
                destination -= 1;
            }
            g[destination] = value;
            if level == NativeClusterLevel::MonotoneCharacters && destination < i {
                for g in &mut g[destination + 1..=i] {
                    g.cluster = crossed;
                }
            }
        }
        start = if end == start { end + 1 } else { end };
    }
}
fn vowel_match(script: Tag, first: u32, second: u32, third: u32) -> usize {
    for &(tag, a, b, c) in vowels::CONSTRAINTS {
        if tag == script.0 && a == first && b == second {
            if c == 0 {
                return 2;
            }
            if c == third {
                return 3;
            }
        }
    }
    0
}
fn vowel_length(script: Tag, g: &[ShapingGlyph], i: usize) -> usize {
    vowel_match(
        script,
        g[i].code_point,
        g[i + 1].code_point,
        g.get(i + 2).map_or(0, |g| g.code_point),
    )
}
fn vowel_additions(script: Tag, g: &[ShapingGlyph]) -> usize {
    let mut n = 0;
    let mut i = 0;
    while i + 1 < g.len() {
        let count = vowel_length(script, g, i);
        if count == 0 {
            i += 1;
        } else {
            n += 1;
            i += count;
        }
    }
    n
}
fn eligible<'a>(data: NormalizationData<'a>, cp: u32) -> Option<&'a [u8]> {
    data.decomposition(cp)
        .filter(|d| !d.is_empty() && unicode_mark(scalar(d, 0)))
}
pub(super) fn use_count(
    input: &[UnicodeScalar],
    data: NormalizationData<'_>,
) -> Result<usize, FontError> {
    let mut count = 0usize;
    for s in input {
        count = count
            .checked_add(eligible(data, s.code_point).map_or(1, |d| d.len() / 4))
            .filter(|&n| n <= u32::MAX as usize)
            .ok_or(FontError::InvalidArgument)?;
    }
    Ok(count)
}
fn use_additions(
    font: &SfntFace,
    data: NormalizationData<'_>,
    g: &[ShapingGlyph],
) -> Result<usize, FontError> {
    let mut count = 0usize;
    for glyph in g {
        if let Some(d) = eligible(data, glyph.code_point) {
            for i in 0..d.len() / 4 {
                font.glyph_index(scalar(d, i))
                    .ok_or(FontError::InvalidFace)?;
            }
            count = count
                .checked_add(d.len() / 4 - 1)
                .ok_or(FontError::InvalidArgument)?;
        }
    }
    Ok(count)
}
pub(super) fn preprocess(
    font: &SfntFace,
    script: Tag,
    kind: NativeComplexKind,
    level: NativeClusterLevel,
    flags: NativeBufferFlags,
    has_pre_context: bool,
    normalization: Option<NormalizationData<'_>>,
    b: &mut Buffer<'_>,
) -> Result<(), FontError> {
    let data = if kind == NativeComplexKind::Use {
        normalization
    } else {
        None
    };
    let additions = vowel_additions(script, b.glyphs());
    let use_added = data
        .map(|d| use_additions(font, d, b.glyphs()))
        .transpose()?
        .unwrap_or(0);
    let initial_mark = b.count != 0
        && (matches!(
            grapheme::break_class(b.storage[0].code_point),
            grapheme::GraphemeBreakClass::Extend | grapheme::GraphemeBreakClass::SpacingMark
        ) || unicode::canonical_combining_class(b.storage[0].code_point) != 0);
    let insert = initial_mark
        && flags.contains(NativeBufferFlags::BEGINNING_OF_TEXT)
        && !has_pre_context
        && !flags.contains(NativeBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE);
    let dotted = if insert {
        font.glyph_index(0x25cc).ok_or(FontError::InvalidFace)?
    } else {
        0
    };
    b.reserve(
        additions
            .checked_add(use_added)
            .and_then(|n| n.checked_add(usize::from(dotted != 0)))
            .ok_or(FontError::InvalidArgument)?,
    )?;
    if dotted != 0 {
        b.insert(
            0,
            ShapingGlyph {
                glyph_id: u32::from(dotted),
                code_point: 0x25cc,
                cluster: b.storage[0].cluster,
                ..Default::default()
            },
        )?;
    }
    reorder_marks(b.glyphs_mut(), level);
    let additions = vowel_additions(script, b.glyphs());
    b.reserve(additions)?;
    if additions != 0 {
        let id = font.glyph_index(0x25cc).ok_or(FontError::InvalidFace)?;
        let mut i = 0;
        while i + 1 < b.count {
            let length = vowel_length(script, b.glyphs(), i);
            if length == 0 {
                i += 1;
                continue;
            }
            let final_index = i + length - 1;
            b.insert(
                final_index,
                ShapingGlyph {
                    glyph_id: u32::from(id),
                    code_point: 0x25cc,
                    cluster: b.storage[final_index].cluster,
                    ..Default::default()
                },
            )?;
            i += length + 1;
        }
    }
    if let Some(data) = data {
        let additions = use_additions(font, data, b.glyphs())?;
        b.reserve(additions)?;
        let final_count = b.count + additions;
        let mut destination = final_count;
        for source in (0..b.count).rev() {
            let original = b.storage[source];
            if let Some(d) = eligible(data, original.code_point) {
                for i in (0..d.len() / 4).rev() {
                    destination -= 1;
                    b.storage[destination] = glyph(font, scalar(d, i), original.cluster)?;
                }
            } else {
                destination -= 1;
                b.storage[destination] = original;
            }
        }
        b.count = final_count;
    }
    Ok(())
}
pub(super) fn default_ignorable(cp: u32) -> bool {
    matches!(cp, 0xad | 0x34f | 0x61c | 0x115f | 0x1160 | 0x17b4 | 0x17b5 | 0x180b..=0x180f | 0x200b..=0x200f | 0x202a..=0x202e | 0x2060..=0x206f | 0x3164 | 0xfeff | 0xffa0 | 0xfff0..=0xfff8 | 0xfe00..=0xfe0f | 0x1bca0..=0x1bcaf | 0x1d173..=0x1d17a | 0xe0000..=0xe0fff)
}
pub(super) fn process_ignorables(
    font: &SfntFace,
    direction: GposDirection,
    flags: NativeBufferFlags,
    b: &mut Buffer<'_>,
) -> Result<(), FontError> {
    let preserve = flags.contains(NativeBufferFlags::PRESERVE_DEFAULT_IGNORABLES);
    let remove = flags.contains(NativeBufferFlags::REMOVE_DEFAULT_IGNORABLES);
    let invisible = if !preserve && !remove {
        font.glyph_index(0x20).ok_or(FontError::InvalidFace)?
    } else {
        0
    };
    let (mut forward_source, mut forward_merged, mut output) = (i32::MIN, i32::MAX, 0);
    for source in 0..b.count {
        let mut glyph = b.storage[source];
        let ignorable = default_ignorable(glyph.code_point);
        let substituted = glyph.advance_x == i32::MIN;
        if ignorable && !substituted && invisible == 0 && !preserve {
            let cluster = glyph.cluster;
            if source + 1 < b.count && cluster == b.storage[source + 1].cluster {
                continue;
            }
            if matches!(
                direction,
                GposDirection::RightToLeft | GposDirection::BottomToTop
            ) && source + 1 < b.count
            {
                forward_source = b.storage[source + 1].cluster;
                forward_merged = cluster.min(forward_source);
            } else if output != 0 {
                if cluster < b.storage[output - 1].cluster {
                    let old = b.storage[output - 1].cluster;
                    let mut i = output;
                    while i != 0 && b.storage[i - 1].cluster == old {
                        i -= 1;
                        b.storage[i].cluster = cluster;
                    }
                }
            } else if source + 1 < b.count {
                forward_source = b.storage[source + 1].cluster;
                forward_merged = cluster.min(forward_source);
            }
            continue;
        }
        if glyph.cluster == forward_source {
            glyph.cluster = forward_merged;
        } else if forward_source != i32::MIN {
            forward_source = i32::MIN;
        }
        if ignorable && !substituted && !preserve {
            glyph.glyph_id = u32::from(invisible);
        }
        b.storage[output] = glyph;
        output += 1;
    }
    b.count = output;
    Ok(())
}
