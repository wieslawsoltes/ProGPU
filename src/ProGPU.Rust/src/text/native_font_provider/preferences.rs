//! Original fixed64 ASCII fallback ordering, independent of managed Unicode casing.
use crate::font::FontError;
const ARABIC: &[&str] = &[
    "Geeza Pro",
    "Noto Naskh Arabic",
    "Noto Sans Arabic",
    "Segoe UI",
    "Traditional Arabic",
    "Arabic Typesetting",
    "Tahoma",
    "Arial",
    "DejaVu Sans",
];
const JAPANESE: &[&str] = &[
    "Hiragino Sans",
    "Hiragino Kaku Gothic ProN",
    "Yu Gothic",
    "Meiryo",
    "Noto Sans CJK JP",
    "Noto Sans JP",
    ".Aqua Kana",
];
const KOREAN: &[&str] = &[
    "Apple SD Gothic Neo",
    "AppleGothic",
    "Malgun Gothic",
    "Noto Sans CJK KR",
    "Noto Sans KR",
];
const CHINESE_TRADITIONAL: &[&str] = &[
    "PingFang TC",
    "Heiti TC",
    "Noto Sans CJK TC",
    "Noto Sans TC",
    "Microsoft JhengHei",
    "Songti TC",
];
const CHINESE_SIMPLIFIED: &[&str] = &[
    "PingFang SC",
    "Hiragino Sans GB",
    "Heiti SC",
    "Noto Sans CJK SC",
    "Noto Sans SC",
    "Microsoft YaHei",
    "SimHei",
    "Songti SC",
    "Noto Sans CJK JP",
    "Noto Sans JP",
];
const HEBREW: &[&str] = &[
    "Arial Hebrew",
    "Lucida Grande",
    "Noto Sans Hebrew",
    "Segoe UI",
    "Arial",
    "DejaVu Sans",
];
const LATIN: &[&str] = &[
    "Helvetica",
    "Arial",
    "Segoe UI",
    "Noto Sans",
    "DejaVu Sans",
    "Liberation Sans",
];
const SYMBOLS: &[&str] = &[
    "Apple Symbols",
    "Zapf Dingbats",
    "Segoe UI Symbol",
    "Noto Sans Symbols 2",
    "Noto Sans Symbols",
    "Apple Color Emoji",
    "Segoe UI Emoji",
    "Noto Color Emoji",
    "Symbola",
    "DejaVu Sans",
];
const EMOJI: &[&str] = &[
    "Apple Color Emoji",
    "Segoe UI Emoji",
    "Noto Color Emoji",
    "Noto Emoji",
    "Segoe UI Symbol",
];
const _: () = assert!(
    ARABIC.len()
        + JAPANESE.len()
        + KOREAN.len()
        + CHINESE_TRADITIONAL.len()
        + CHINESE_SIMPLIFIED.len()
        + HEBREW.len()
        + LATIN.len()
        + SYMBOLS.len()
        + EMOJI.len()
        <= 64
);

/// Original bounded preference buffer. All names refer to static original
/// literals; building or copying this value performs no allocation.
#[derive(Clone, Debug)]
pub struct NativeFallbackPreferences {
    values: [&'static str; 64],
    count: usize,
}
impl Default for NativeFallbackPreferences {
    fn default() -> Self {
        Self {
            values: [""; 64],
            count: 0,
        }
    }
}
impl NativeFallbackPreferences {
    pub fn as_slice(&self) -> &[&'static str] {
        &self.values[..self.count]
    }
    pub fn len(&self) -> usize {
        self.count
    }
    pub fn is_empty(&self) -> bool {
        self.count == 0
    }
    /// Complete capacity admission precedes publication; unused output slots stay
    /// unchanged. Capacity is the original insufficient-buffer error value.
    pub fn write_into(&self, output: &mut [&'static str]) -> Result<usize, FontError> {
        if output.len() < self.count {
            return Err(FontError::Capacity);
        }
        output[..self.count].copy_from_slice(self.as_slice());
        Ok(self.count)
    }
    fn add(&mut self, source: &'static [&'static str]) {
        for &family in source {
            let exists = self.as_slice().iter().any(|candidate| {
                candidate.len() == family.len()
                    && candidate
                        .bytes()
                        .zip(family.bytes())
                        .all(|(a, b)| normalized(a) == normalized(b))
            });
            if !exists {
                self.values[self.count] = family;
                self.count += 1;
            }
        }
    }
}
impl<'a> IntoIterator for &'a NativeFallbackPreferences {
    type Item = &'a &'static str;
    type IntoIter = std::slice::Iter<'a, &'static str>;
    fn into_iter(self) -> Self::IntoIter {
        self.as_slice().iter()
    }
}
fn normalized(value: u8) -> u8 {
    if value == b'_' {
        b'-'
    } else {
        value.to_ascii_lowercase()
    }
}
fn trim(mut value: &[u8]) -> &[u8] {
    let space = |b: u8| matches!(b, b' ' | b'\t' | b'\r' | b'\n' | 12 | 11);
    while value.first().is_some_and(|&v| space(v)) {
        value = &value[1..];
    }
    while value.last().is_some_and(|&v| space(v)) {
        value = &value[..value.len() - 1];
    }
    value
}
fn equals_at(value: &[u8], offset: usize, expected: &[u8]) -> bool {
    value
        .get(offset..)
        .and_then(|v| v.get(..expected.len()))
        .is_some_and(|v| v.iter().zip(expected).all(|(&a, &b)| normalized(a) == b))
}
fn language_is(value: &[u8], primary: &[u8]) -> bool {
    equals_at(value, 0, primary)
        && (value.len() == primary.len()
            || value
                .get(primary.len())
                .is_some_and(|&v| normalized(v) == b'-'))
}
fn contains(value: &[u8], part: &[u8]) -> bool {
    part.len() <= value.len() && (0..=value.len() - part.len()).any(|i| equals_at(value, i, part))
}
fn ends_with(value: &[u8], suffix: &[u8]) -> bool {
    value.len() >= suffix.len() && equals_at(value, value.len() - suffix.len(), suffix)
}
fn add_language(result: &mut NativeFallbackPreferences, tag: &[u8]) {
    let tag = trim(tag);
    if language_is(tag, b"ar") {
        result.add(ARABIC);
    } else if language_is(tag, b"ja") {
        result.add(JAPANESE);
    } else if language_is(tag, b"ko") {
        result.add(KOREAN);
    } else if language_is(tag, b"zh") {
        result.add(
            if contains(tag, b"-hant")
                || ends_with(tag, b"-tw")
                || ends_with(tag, b"-hk")
                || ends_with(tag, b"-mo")
            {
                CHINESE_TRADITIONAL
            } else {
                CHINESE_SIMPLIFIED
            },
        );
    }
}
fn build<'a>(
    tags: impl Iterator<Item = &'a [u8]>,
    code_point: u32,
) -> Result<NativeFallbackPreferences, FontError> {
    if code_point > 0x10ffff {
        return Err(FontError::InvalidArgument);
    }
    let mut result = NativeFallbackPreferences::default();
    for tag in tags {
        add_language(&mut result, tag);
    }
    match code_point {
        0x0600..=0x06ff
        | 0x0750..=0x077f
        | 0x0870..=0x089f
        | 0x08a0..=0x08ff
        | 0xfb50..=0xfdff
        | 0xfe70..=0xfeff
        | 0x1ee00..=0x1eeff => result.add(ARABIC),
        0x0590..=0x05ff | 0xfb1d..=0xfb4f => result.add(HEBREW),
        0x1f000..=0x1faff => result.add(EMOJI),
        0x2000..=0x2bff => result.add(SYMBOLS),
        0x3040..=0x30ff => result.add(JAPANESE),
        0xac00..=0xd7af | 0x1100..=0x11ff => result.add(KOREAN),
        0x3400..=0x9fff | 0xf900..=0xfaff | 0x20000..=0x323af => result.add(CHINESE_SIMPLIFIED),
        0x0020..=0x024f | 0x0370..=0x052f | 0x1e00..=0x1fff | 0x2de0..=0x2dff | 0xa640..=0xa69f => {
            result.add(LATIN)
        }
        _ => {}
    }
    Ok(result)
}
/// Original language order followed by one original scalar-range branch.
/// ASCII whitespace, case and underscore normalization never use Unicode casing.
pub fn fallback_family_preferences(
    tags: &[&str],
    code_point: u32,
) -> Result<NativeFallbackPreferences, FontError> {
    build(tags.iter().map(|v| v.as_bytes()), code_point)
}
/// Complete native string-view input, including non-UTF8 and embedded-zero bytes.
pub fn fallback_family_preferences_bytes(
    tags: &[&[u8]],
    code_point: u32,
) -> Result<NativeFallbackPreferences, FontError> {
    build(tags.iter().copied(), code_point)
}
