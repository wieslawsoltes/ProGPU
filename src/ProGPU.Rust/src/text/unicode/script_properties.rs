//! Frozen original script-stage property observations, with independent storage.
//! These classifications do not perform syllable analysis or glyph reordering.
use super::{is_scalar, script_property_data as data};

/// Original Indic category and position bits, including caller-authored values.
/// Category occupies the low byte and position the high byte. This is independent
/// of Unicode's Indic_Syllabic_Category and Indic_Positional_Category enums.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(transparent)]
pub struct ManagedIndicProperties(u16);

impl ManagedIndicProperties {
    /// Retain every original bit without normalizing unknown categories/positions.
    pub const fn from_bits(bits: u16) -> Self {
        Self(bits)
    }
    /// Original packed category/position word.
    pub const fn bits(self) -> u16 {
        self.0
    }
    /// Original category identifier, also retained by Khmer and Myanmar stages.
    pub const fn category(self) -> u8 {
        self.0 as u8
    }
    /// Original position identifier.
    pub const fn position(self) -> u8 {
        (self.0 >> 8) as u8
    }
    /// Original raw script-stage query. Invalid code points retain the original
    /// default category zero, position fourteen. The public Unicode query below
    /// instead returns the zero word for invalid scalars.
    pub fn for_raw_code_point(code_point: u32) -> Self {
        Self(lookup(code_point, &data::INDIC, 0x0e00))
    }
}

/// Original public Unicode Indic query, returning zero for invalid scalars.
pub fn managed_indic_properties(code_point: u32) -> ManagedIndicProperties {
    if is_scalar(code_point) {
        ManagedIndicProperties::for_raw_code_point(code_point)
    } else {
        ManagedIndicProperties::from_bits(0)
    }
}

/// Original USE category identifier. Both the public Unicode query and raw
/// script-stage query return zero for invalid or unclassified code points.
pub fn managed_use_category(code_point: u32) -> u8 {
    lookup(code_point, &data::USE, 0) as u8
}

// Independent sorted range encoding: start/end use 21 bits each; the remaining
// bits carry the exact observed value. O(log R), O(1) scratch, no allocation,
// synchronization or runtime parsing. Compare the full u32 before narrowing.
fn lookup(point: u32, ranges: &[u64], default: u16) -> u16 {
    const MASK: u64 = (1 << 21) - 1;
    if point > 0x10ffff {
        return default;
    }
    let (mut low, mut high) = (0, ranges.len());
    while low < high {
        let mid = low + (high - low) / 2;
        let value = ranges[mid];
        if u64::from(point) < value & MASK {
            high = mid;
        } else if u64::from(point) > (value >> 21) & MASK {
            low = mid + 1;
        } else {
            return (value >> 42) as u16;
        }
    }
    default
}
