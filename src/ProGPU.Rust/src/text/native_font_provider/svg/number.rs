//! Original bounded decimal scanner. The host arithmetic boundary is explicit:
//! Rust f64 does not assert equivalence to a platform's extended long double.
pub(super) fn parse(text: &[u8], index: &mut usize) -> Option<f32> {
    let start = *index;
    let negative = text.get(*index) == Some(&b'-');
    if matches!(text.get(*index), Some(b'+' | b'-')) {
        *index += 1;
    }
    let mut significand = 0u64;
    let mut collected = 0i64;
    let mut combined = 0i64;
    let mut first = 0i64;
    let mut significant = false;
    let mut consume = |digit: u8| {
        if !significant && digit != b'0' {
            significant = true;
            first = combined;
        }
        if significant && collected < 19 {
            significand = significand * 10 + u64::from(digit - b'0');
            collected += 1;
        }
        combined += 1;
    };
    let integer_start = *index;
    while let Some(&digit) = text.get(*index).filter(|v| v.is_ascii_digit()) {
        consume(digit);
        *index += 1;
    }
    let integer_digits = (*index - integer_start) as i64;
    if text.get(*index) == Some(&b'.') {
        *index += 1;
        while let Some(&digit) = text.get(*index).filter(|v| v.is_ascii_digit()) {
            consume(digit);
            *index += 1;
        }
    }
    if combined == 0 {
        *index = start;
        return None;
    }
    let mut exponent = 0i64;
    if matches!(text.get(*index), Some(b'e' | b'E')) {
        let exponent_start = *index;
        *index += 1;
        let exponent_negative = text.get(*index) == Some(&b'-');
        if matches!(text.get(*index), Some(b'+' | b'-')) {
            *index += 1;
        }
        let digits = *index;
        while let Some(&digit) = text.get(*index).filter(|v| v.is_ascii_digit()) {
            if exponent < 1_000_000 {
                exponent = (exponent * 10 + i64::from(digit - b'0')).min(1_000_000);
            }
            *index += 1;
        }
        if digits == *index {
            *index = exponent_start;
            exponent = 0;
        } else if exponent_negative {
            exponent = -exponent;
        }
    }
    if !significant {
        return Some(if negative { -0.0 } else { 0.0 });
    }
    let exponent = integer_digits - first - collected + exponent;
    if exponent > 10_000 {
        *index = start;
        return None;
    }
    let mut value = if exponent < -10_000 {
        0.0
    } else {
        significand as f64 * 10.0f64.powf(exponent as f64)
    };
    if negative {
        value = -value;
    }
    let converted = value as f32;
    if !value.is_finite() || !converted.is_finite() {
        *index = start;
        None
    } else {
        Some(converted)
    }
}
pub(super) fn exact(text: &[u8]) -> Option<f32> {
    let mut index = 0;
    let value = parse(text, &mut index)?;
    (index == text.len()).then_some(value)
}
