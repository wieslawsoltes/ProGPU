//! Original binary lookups over immutable packed property inventories.
use super::{
    BidiBracket, BidiBracketKind, BidiClass, DEFAULT_SCRIPT, GeneralCategory, data, is_scalar,
};
use crate::font::Tag;
/// Original Unicode script to OpenType tag, including hira->kana and laoo->lao.
/// Common/Inherited/Unknown, invalid scalars and missing mappings return DFLT.
pub fn script(code_point: u32) -> Tag {
    if !is_scalar(code_point) {
        return DEFAULT_SCRIPT;
    }
    let index = range_value(code_point, &data::SCRIPT_RANGES, 0) as usize;
    let value = data::SCRIPT_TAGS.get(index).copied().unwrap_or(0);
    match value {
        0 => DEFAULT_SCRIPT,
        v if v == Tag::new(*b"hira").0 => Tag::new(*b"kana"),
        v if v == Tag::new(*b"laoo").0 => Tag::new(*b"lao "),
        v => Tag(v),
    }
}
/// Original first non-DFLT script inference, skipping invalid scalar values.
pub fn infer_script(input: &[u32]) -> Tag {
    input
        .iter()
        .map(|v| script(*v))
        .find(|s| *s != DEFAULT_SCRIPT)
        .unwrap_or(DEFAULT_SCRIPT)
}
/// Original canonical combining class, or zero for invalid/unlisted scalars.
pub fn canonical_combining_class(code_point: u32) -> u8 {
    if is_scalar(code_point) {
        range_value(code_point, &data::COMBINING_RANGES, 0) as u8
    } else {
        0
    }
}
/// Original .NET category inventory frozen in ProGPU's native generated data.
/// Invalid scalars, including surrogate code points, return OtherNotAssigned.
pub fn general_category(code_point: u32) -> GeneralCategory {
    if is_scalar(code_point) {
        GeneralCategory::from_data(range_value(code_point, &data::CATEGORY_RANGES, 29))
    } else {
        GeneralCategory::OtherNotAssigned
    }
}
/// Exact original decimal digit 0..9; absence is distinct from the digit zero.
pub fn decimal_digit_value(code_point: u32) -> Option<u8> {
    let (mut low, mut high) = (0, data::DIGIT_ZEROS.len());
    while low < high {
        let mid = low + (high - low) / 2;
        let zero = data::DIGIT_ZEROS[mid];
        if code_point < zero {
            high = mid;
        } else if code_point > zero + 9 {
            low = mid + 1;
        } else {
            return Some((code_point - zero) as u8);
        }
    }
    None
}
// Compile-time projection of the exact original inventory. This is 128 bytes,
// no lazy allocation/cache state or separate Unicode policy. The remaining
// scalar domain retains the original binary search below.
const ASCII_BIDI: [BidiClass; 128] = {
    let mut classes = [BidiClass::LeftToRight; 128];
    let mut point = 0;
    while point < classes.len() {
        let mut index = 0;
        while index < data::BIDI_RANGES.len() {
            let range = data::BIDI_RANGES[index];
            if point as u64 >= range & MASK && point as u64 <= (range >> 21) & MASK {
                classes[point] = BidiClass::from_data((range >> 42) as u32);
                break;
            }
            index += 1;
        }
        point += 1;
    }
    classes
};
/// Original bidi property, independent of paragraph direction/level resolution.
pub fn bidi_class(code_point: u32) -> BidiClass {
    if code_point < 128 {
        return ASCII_BIDI[code_point as usize];
    }
    if !is_scalar(code_point) {
        return BidiClass::LeftToRight;
    }
    let (mut low, mut high) = (0, data::BIDI_RANGES.len());
    while low < high {
        let mid = low + (high - low) / 2;
        let v = data::BIDI_RANGES[mid];
        let start = (v & MASK) as u32;
        let end = ((v >> 21) & MASK) as u32;
        if code_point < start {
            high = mid;
        } else if code_point > end {
            low = mid + 1;
        } else {
            return BidiClass::from_data((v >> 42) as u32);
        }
    }
    BidiClass::LeftToRight
}
/// Original paired-bracket mapping and kind; invalid/unlisted scalars stay absent.
pub fn bidi_bracket(code_point: u32) -> Option<BidiBracket> {
    if !is_scalar(code_point) {
        return None;
    }
    let (mut low, mut high) = (0, data::BIDI_BRACKETS.len());
    while low < high {
        let mid = low + (high - low) / 2;
        let v = data::BIDI_BRACKETS[mid];
        let key = (v & MASK) as u32;
        if code_point < key {
            high = mid;
        } else if code_point > key {
            low = mid + 1;
        } else {
            return Some(BidiBracket {
                paired_code_point: ((v >> 21) & MASK) as u32,
                kind: BidiBracketKind::from_data((v >> 42) as u32),
            });
        }
    }
    None
}
/// Original directional mirror mapping. Unlisted raw values return unchanged.
pub fn mirrored_code_point(code_point: u32) -> u32 {
    directional(&data::MIRROR_PAIRS, code_point)
}
/// Original vertical presentation mapping. This does not place a vertical run.
pub fn vertical_code_point(code_point: u32) -> u32 {
    directional(&data::VERTICAL_PAIRS, code_point)
}
const MASK: u64 = (1 << 21) - 1;
fn range_value(code_point: u32, data: &[u32], fallback: u32) -> u32 {
    let (mut low, mut high) = (0, data.len() / 3);
    while low < high {
        let mid = low + (high - low) / 2;
        let at = mid * 3;
        if code_point < data[at] {
            high = mid;
        } else if code_point > data[at + 1] {
            low = mid + 1;
        } else {
            return data[at + 2];
        }
    }
    fallback
}
fn directional(data: &[u32], code_point: u32) -> u32 {
    let (mut low, mut high) = (0, data.len() / 2);
    while low < high {
        let mid = low + (high - low) / 2;
        let at = mid * 2;
        if code_point < data[at] {
            high = mid;
        } else if code_point > data[at] {
            low = mid + 1;
        } else {
            return data[at + 1];
        }
    }
    code_point
}
