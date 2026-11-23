use super::super::{FontError, bytes};

/// Original CFF1 top-dictionary offsets. Unknown operators keep native admission.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct TopDictionary {
    /// Required CharStrings INDEX offset.
    pub char_strings_offset: u32,
    /// Private dictionary length.
    pub private_size: u32,
    /// Private dictionary offset.
    pub private_offset: u32,
    /// CID font-dictionary INDEX offset, or zero.
    pub font_dictionary_offset: u32,
    /// CID FDSelect offset, or zero.
    pub fd_select_offset: u32,
}
/// Original strict CFF2 top-dictionary metadata, before container admission.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TopDictionary2 {
    /// Required CharStrings INDEX offset.
    pub char_strings_offset: u32,
    /// Required font-dictionary INDEX offset.
    pub font_dictionary_offset: u32,
    /// Optional FDSelect offset.
    pub fd_select_offset: u32,
    /// Optional variation-store offset.
    pub variation_store_offset: u32,
    /// Original uniform positive font-matrix scale, default 0.001.
    pub font_matrix_scale: f64,
    /// Whether an explicit admissible matrix was present.
    pub has_font_matrix: bool,
}
impl Default for TopDictionary2 {
    fn default() -> Self {
        Self {
            char_strings_offset: 0,
            font_dictionary_offset: 0,
            fd_select_offset: 0,
            variation_store_offset: 0,
            font_matrix_scale: 0.001,
            has_font_matrix: false,
        }
    }
}
const POWERS: [f64; 23] = {
    let mut a = [1.0; 23];
    let mut i = 1;
    while i < 23 {
        a[i] = a[i - 1] * 10.0;
        i += 1;
    }
    a
};
fn real(data: &[u8]) -> Option<f64> {
    let mut cursor = 0;
    let negative = data.first() == Some(&b'-');
    if negative {
        cursor += 1;
    }
    let (mut significand, mut digits, mut saw_digit, mut decimal) = (0.0, 0i32, false, false);
    while cursor < data.len() && data[cursor] != b'E' {
        let byte = data[cursor];
        cursor += 1;
        if byte == b'.' {
            if decimal {
                return None;
            }
            decimal = true;
            continue;
        }
        if !byte.is_ascii_digit() {
            return None;
        }
        saw_digit = true;
        significand = significand * 10.0 + f64::from(byte - b'0');
        if decimal {
            digits += 1;
        }
    }
    if !saw_digit {
        return None;
    }
    let mut exponent = 0i32;
    if cursor < data.len() {
        cursor += 1;
        let negative = data.get(cursor) == Some(&b'-');
        if negative {
            cursor += 1;
        }
        let first = cursor;
        while cursor < data.len() {
            let byte = data[cursor];
            cursor += 1;
            if !byte.is_ascii_digit() || exponent > 4096 {
                return None;
            }
            exponent = exponent * 10 + i32::from(byte - b'0');
        }
        if cursor == first {
            return None;
        }
        if negative {
            exponent = -exponent;
        }
    }
    let scale = exponent - digits;
    let mut value = if (-22..0).contains(&scale) {
        significand / POWERS[(-scale) as usize]
    } else if (0..=22).contains(&scale) {
        significand * POWERS[scale as usize]
    } else {
        significand * 10.0f64.powf(f64::from(scale))
    };
    if negative {
        value = -value;
    }
    value.is_finite().then_some(value)
}
/// Decode an original DICT number after its first byte has already been consumed.
/// Failure retains any consumed payload bytes, including malformed real nibbles.
/// This preserves the native top/private reader's independent operator handling.
pub fn read_number(data: &[u8], cursor: &mut usize, first: u8) -> Option<f64> {
    match first {
        32..=246 => Some(f64::from(first) - 139.0),
        247..=254 => {
            let last = *data.get(*cursor)?;
            *cursor += 1;
            if first <= 250 {
                Some(f64::from(
                    u32::from(first - 247) * 256 + u32::from(last) + 108,
                ))
            } else {
                Some(-f64::from(
                    u32::from(first - 251) * 256 + u32::from(last) + 108,
                ))
            }
        }
        28 => {
            let v = bytes::i16(data, *cursor)?;
            *cursor += 2;
            Some(f64::from(v))
        }
        29 => {
            let v = bytes::u32(data, *cursor)? as i32;
            *cursor += 4;
            Some(f64::from(v))
        }
        30 => {
            let mut encoded = [0u8; 96];
            let mut len = 0;
            while let Some(&pair) = data.get(*cursor) {
                *cursor += 1;
                for n in [pair >> 4, pair & 15] {
                    if n == 15 {
                        return real(&encoded[..len]);
                    }
                    let chars: &[u8] = match n {
                        0..=9 => &[b'0' + n],
                        10 => b".",
                        11 => b"E",
                        12 => b"E-",
                        14 => b"-",
                        _ => return None,
                    };
                    if chars.len() > encoded.len() - len {
                        return None;
                    }
                    encoded[len..len + chars.len()].copy_from_slice(chars);
                    len += chars.len();
                }
            }
            None
        }
        _ => None,
    }
}
pub(super) fn to_offset(value: f64) -> u32 {
    if value.is_finite() && value >= 0.0 && value <= f64::from(i32::MAX) {
        value as u32
    } else {
        u32::MAX
    }
}
// False from the visitor terminates successfully (private Subrs uses first match).
fn visit(
    data: &[u8],
    mut action: impl FnMut(u16, &[f64]) -> Result<bool, FontError>,
) -> Result<usize, FontError> {
    let mut operands = [0.0; 48];
    let (mut cursor, mut count) = (0, 0);
    while cursor < data.len() {
        let first = data[cursor];
        cursor += 1;
        if let Some(number) = read_number(data, &mut cursor, first) {
            if count == operands.len() {
                return Err(FontError::InvalidFace);
            }
            operands[count] = number;
            count += 1;
            continue;
        }
        let op = if first == 12 {
            let second = *data.get(cursor).ok_or(FontError::InvalidFace)?;
            cursor += 1;
            0xc00 | u16::from(second)
        } else {
            u16::from(first)
        };
        if !action(op, &operands[..count])? {
            return Ok(0);
        }
        count = 0;
    }
    Ok(count)
}
impl TopDictionary {
    /// Parse with the original CFF1 last-applicable-offset and operand policy.
    pub fn read(data: &[u8]) -> Result<Self, FontError> {
        let mut r = Self::default();
        visit(data, |op, a| {
            match op {
                17 if !a.is_empty() => r.char_strings_offset = to_offset(a[a.len() - 1]),
                18 if a.len() >= 2 => {
                    r.private_size = to_offset(a[a.len() - 2]);
                    r.private_offset = to_offset(a[a.len() - 1]);
                }
                0xc24 if !a.is_empty() => r.font_dictionary_offset = to_offset(a[a.len() - 1]),
                0xc25 if !a.is_empty() => r.fd_select_offset = to_offset(a[a.len() - 1]),
                _ => {}
            }
            Ok(true)
        })?;
        if r.char_strings_offset == 0
            || [
                r.char_strings_offset,
                r.private_size,
                r.private_offset,
                r.font_dictionary_offset,
                r.fd_select_offset,
            ]
            .contains(&u32::MAX)
        {
            return Err(FontError::InvalidFace);
        }
        Ok(r)
    }
}
impl TopDictionary2 {
    /// Parse the original strict CFF2 operator/arity/duplicate/matrix contract.
    pub fn read(data: &[u8]) -> Result<Self, FontError> {
        let mut r = Self::default();
        let mut saw = [false; 5];
        let trailing = visit(data, |op, a| {
            let i = match op {
                17 => 0,
                0xc24 => 1,
                0xc25 => 2,
                24 => 3,
                0xc07 => 4,
                _ => return Err(FontError::InvalidFace),
            };
            if saw[i] {
                return Err(FontError::InvalidFace);
            }
            if i == 4 {
                if a.len() != 6
                    || a[0] != a[3]
                    || a[1] != 0.0
                    || a[2] != 0.0
                    || a[4] != 0.0
                    || a[5] != 0.0
                    || a[0] <= 0.0
                    || !a[0].is_finite()
                {
                    return Err(FontError::InvalidFace);
                }
                r.font_matrix_scale = a[0];
                r.has_font_matrix = true;
            } else {
                if a.len() != 1 {
                    return Err(FontError::InvalidFace);
                }
                let value = to_offset(a[0]);
                match i {
                    0 => r.char_strings_offset = value,
                    1 => r.font_dictionary_offset = value,
                    2 => r.fd_select_offset = value,
                    _ => r.variation_store_offset = value,
                }
            }
            saw[i] = true;
            Ok(true)
        })?;
        if trailing != 0
            || !saw[0]
            || !saw[1]
            || r.char_strings_offset == 0
            || r.font_dictionary_offset == 0
            || [
                r.char_strings_offset,
                r.font_dictionary_offset,
                r.fd_select_offset,
                r.variation_store_offset,
            ]
            .contains(&u32::MAX)
        {
            return Err(FontError::InvalidFace);
        }
        Ok(r)
    }
}
pub(super) fn private_range(data: &[u8]) -> Option<(u32, u32)> {
    let (mut size, mut offset) = (0, 0);
    visit(data, |op, a| {
        if op == 18 && a.len() >= 2 {
            size = to_offset(a[a.len() - 2]);
            offset = to_offset(a[a.len() - 1]);
        }
        Ok(true)
    })
    .ok()?;
    (size != u32::MAX && offset != u32::MAX).then_some((offset, size))
}
pub(super) fn subroutine_offset(data: &[u8]) -> Option<u32> {
    let mut result = None;
    visit(data, |op, a| {
        if op == 19 && !a.is_empty() {
            let offset = to_offset(a[a.len() - 1]);
            result = (offset != u32::MAX).then_some(offset);
            return Ok(false);
        }
        Ok(true)
    })
    .ok()?;
    result
}
