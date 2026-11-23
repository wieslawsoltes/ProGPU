//! Original allocation-free native name selection and caller-owned UTF-8 writer.
use super::*;
/// Complete selected original name-record metadata, before any output write.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeNameRequirements {
    pub utf8_bytes: usize,
    pub score: i32,
    pub platform_id: u16,
    pub encoding_id: u16,
    pub language_id: u16,
}
fn decode(input: &[u8], platform: u16, encoding: u16, offset: usize) -> (char, usize) {
    if platform == 0 || platform == 3 {
        if input.len() - offset < 2 {
            return (char::REPLACEMENT_CHARACTER, input.len() - offset);
        }
        let first = word(input, offset);
        if (0xd800..=0xdbff).contains(&first) && input.len() - offset >= 4 {
            let second = word(input, offset + 2);
            if (0xdc00..=0xdfff).contains(&second) {
                let scalar =
                    0x10000 + (u32::from(first - 0xd800) << 10) + u32::from(second - 0xdc00);
                return (char::from_u32(scalar).unwrap(), 4);
            }
        }
        return (
            char::from_u32(u32::from(first)).unwrap_or(char::REPLACEMENT_CHARACTER),
            2,
        );
    }
    let first = input[offset];
    if platform == 1 && encoding == 0 {
        return (char::from(first), 1);
    }
    if first <= 0x7f {
        return (char::from(first), 1);
    }
    let continuation = |value: u8| value & 0xc0 == 0x80;
    let scalar = if (0xc2..=0xdf).contains(&first) && input.len() - offset >= 2 {
        let second = input[offset + 1];
        continuation(second)
            .then_some(((u32::from(first & 0x1f) << 6) | u32::from(second & 0x3f), 2))
    } else if (0xe0..=0xef).contains(&first) && input.len() - offset >= 3 {
        let (second, third) = (input[offset + 1], input[offset + 2]);
        (continuation(second)
            && continuation(third)
            && (first != 0xe0 || second >= 0xa0)
            && (first != 0xed || second <= 0x9f))
            .then_some((
                (u32::from(first & 15) << 12)
                    | (u32::from(second & 0x3f) << 6)
                    | u32::from(third & 0x3f),
                3,
            ))
    } else if (0xf0..=0xf4).contains(&first) && input.len() - offset >= 4 {
        let (second, third, fourth) = (input[offset + 1], input[offset + 2], input[offset + 3]);
        (continuation(second)
            && continuation(third)
            && continuation(fourth)
            && (first != 0xf0 || second >= 0x90)
            && (first != 0xf4 || second <= 0x8f))
            .then_some((
                (u32::from(first & 7) << 18)
                    | (u32::from(second & 0x3f) << 12)
                    | (u32::from(third & 0x3f) << 6)
                    | u32::from(fourth & 0x3f),
                4,
            ))
    } else {
        None
    };
    scalar.map_or((char::REPLACEMENT_CHARACTER, 1), |(c, n)| {
        (char::from_u32(c).unwrap(), n)
    })
}
fn whitespace(value: char) -> bool {
    matches!(value as u32, 0x9..=0xd | 0x20 | 0x85 | 0xa0 | 0x1680 | 0x2000..=0x200a | 0x2028 | 0x2029 | 0x202f | 0x205f | 0x3000)
}
fn non_latin_letter(value: char) -> bool {
    let value = value as u32;
    if value <= 0x024f || value > 0xffff {
        return false;
    }
    let ranges = &super::names_data::NAME_LETTER_RANGES;
    let mut low = 0;
    let mut high = ranges.len() / 2;
    while low < high {
        let middle = low + (high - low) / 2;
        if value < u32::from(ranges[middle * 2]) {
            high = middle;
        } else if value > u32::from(ranges[middle * 2 + 1]) {
            low = middle + 1;
        } else {
            return true;
        }
    }
    false
}
fn scalars(input: &[u8], platform: u16, encoding: u16) -> impl Iterator<Item = char> + '_ {
    let mut offset = 0;
    std::iter::from_fn(move || {
        if offset == input.len() {
            return None;
        }
        let (value, count) = decode(input, platform, encoding, offset);
        offset += count;
        Some(value)
    })
    .filter(|&c| c != '\0')
}
#[derive(Clone, Copy)]
struct Analysis {
    first: usize,
    last: usize,
    bytes: usize,
    non_latin: bool,
}
fn analyze(input: &[u8], platform: u16, encoding: u16) -> Option<Analysis> {
    let mut first = None;
    let mut last = 0;
    let mut non_latin = false;
    for (index, value) in scalars(input, platform, encoding).enumerate() {
        if !whitespace(value) {
            first.get_or_insert(index);
            last = index;
        }
        non_latin |= non_latin_letter(value);
    }
    let first = first?;
    let bytes = scalars(input, platform, encoding)
        .enumerate()
        .filter(|(i, _)| *i >= first && *i <= last)
        .map(|(_, c)| c.len_utf8())
        .sum();
    Some(Analysis {
        first,
        last,
        bytes,
        non_latin,
    })
}
struct Selected<'a> {
    bytes: &'a [u8],
    analysis: Analysis,
    requirements: NativeNameRequirements,
}
fn select(font: &SfntFace, name: u16) -> Result<Option<Selected<'_>>, FontError> {
    let Some(table) = font.table(Tag::new(*b"name")) else {
        return Ok(None);
    };
    let table = table.bytes;
    if table.len() < 6 {
        return Err(FontError::InvalidFace);
    }
    let count = usize::from(word(table, 2));
    let strings = usize::from(word(table, 4));
    if 6 + count * 12 > table.len() || strings > table.len() {
        return Err(FontError::InvalidFace);
    }
    let mut selected: Option<Selected<'_>> = None;
    for index in 0..count {
        let record = 6 + index * 12;
        if word(table, record + 6) != name {
            continue;
        }
        let platform = word(table, record);
        let encoding = word(table, record + 2);
        let language = word(table, record + 4);
        let length = usize::from(word(table, record + 8));
        let offset = strings + usize::from(word(table, record + 10));
        let Some(bytes) = table.get(offset..offset + length).filter(|b| !b.is_empty()) else {
            continue;
        };
        let Some(analysis) = analyze(bytes, platform, encoding) else {
            continue;
        };
        let score = match (platform, language) {
            (3, 0x0409) => 4,
            (3, _) => 3,
            (0, _) => 2,
            _ => 1,
        } + if analysis.non_latin { 0 } else { 10 };
        if selected
            .as_ref()
            .is_none_or(|value| score > value.requirements.score)
        {
            selected = Some(Selected {
                bytes,
                analysis,
                requirements: NativeNameRequirements {
                    utf8_bytes: analysis.bytes,
                    score,
                    platform_id: platform,
                    encoding_id: encoding,
                    language_id: language,
                },
            });
        }
    }
    Ok(selected)
}
impl SfntFace {
    /// Original highest-score first-record native selection. Malformed table
    /// inventory rejects; individually malformed/empty strings are skipped.
    pub fn native_name_requirements(
        &self,
        name: u16,
    ) -> Result<Option<NativeNameRequirements>, FontError> {
        Ok(select(self, name)?.map(|value| value.requirements))
    }
    /// Full admission and capacity check precede the first caller-byte mutation.
    /// Native invalid UTF-8 consumes exactly one byte per replacement scalar.
    pub fn native_decode_name_into(
        &self,
        name: u16,
        output: &mut [u8],
    ) -> Result<Option<NativeNameRequirements>, FontError> {
        let Some(selected) = select(self, name)? else {
            return Ok(None);
        };
        if output.len() < selected.requirements.utf8_bytes {
            return Err(FontError::Capacity);
        }
        let mut written = 0;
        for (index, value) in scalars(
            selected.bytes,
            selected.requirements.platform_id,
            selected.requirements.encoding_id,
        )
        .enumerate()
        {
            if index >= selected.analysis.first && index <= selected.analysis.last {
                let count = value.len_utf8();
                value.encode_utf8(&mut output[written..written + count]);
                written += count;
            }
        }
        Ok(Some(selected.requirements))
    }
    /// Owned native selected name; preserves the caller-owned writer's bytes.
    pub fn native_name(&self, name: u16) -> Result<Option<String>, FontError> {
        let Some(requirements) = self.native_name_requirements(name)? else {
            return Ok(None);
        };
        let mut output = Vec::new();
        output
            .try_reserve_exact(requirements.utf8_bytes)
            .map_err(|_| FontError::Capacity)?;
        output.resize(requirements.utf8_bytes, 0);
        self.native_decode_name_into(name, &mut output)?;
        String::from_utf8(output)
            .map(Some)
            .map_err(|_| FontError::InvalidFace)
    }
}
