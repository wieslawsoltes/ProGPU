//! Original complete interval inventory, anchor sizing and ordered obstacle retries.
use super::*;
pub fn native_resolve_text_intervals_into(
    band: NativeExclusionRectangle,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut [NativeLineInterval],
    output: &mut [NativeLineInterval],
) -> Result<(usize, f32), FontError> {
    if exclusions.len() > LIMIT || !valid_rectangle(band) || band.top == band.bottom {
        return Err(FontError::InvalidArgument);
    }
    if scratch.len() < exclusions.len() || output.len() <= exclusions.len() {
        return Err(FontError::Capacity);
    }
    if exclusions.iter().any(|&r| !valid_rectangle(r)) {
        return Err(FontError::InvalidArgument);
    }
    let mut used = 0;
    let mut next = f32::MAX;
    for &r in exclusions {
        if r.left == r.right || r.top == r.bottom || r.top >= band.bottom || r.bottom <= band.top {
            continue;
        }
        let left = max(band.left, r.left);
        let right = min(band.right, r.right);
        if left >= right {
            continue;
        }
        scratch[used] = NativeLineInterval { left, right };
        used += 1;
        next = min(next, r.bottom);
    }
    scratch[..used].sort_unstable_by(|a, b| {
        a.left
            .partial_cmp(&b.left)
            .unwrap()
            .then_with(|| a.right.partial_cmp(&b.right).unwrap())
    });
    let mut cursor = band.left;
    let mut count = 0;
    for interval in &scratch[..used] {
        if interval.left > cursor {
            output[count] = NativeLineInterval {
                left: cursor,
                right: interval.left,
            };
            count += 1;
        }
        cursor = max(cursor, interval.right);
    }
    if cursor < band.right {
        output[count] = NativeLineInterval {
            left: cursor,
            right: band.right,
        };
        count += 1;
    }
    Ok((count, if used == 0 { band.top } else { next }))
}
pub fn native_resolve_anchor_width(
    available: f32,
    insets: f32,
    mode: NativeAnchorWidthMode,
    specified: f32,
    has_measurement: bool,
    measured: f32,
) -> Result<NativeAnchorWidth, FontError> {
    if [available, insets, specified, measured]
        .into_iter()
        .any(|v| !v.is_finite() || v < 0.0)
    {
        return Err(FontError::InvalidArgument);
    }
    let outer = if mode == NativeAnchorWidthMode::Fixed {
        min(available, specified)
    } else {
        available
    };
    let initial = max(0.0, outer - insets);
    let content = if mode == NativeAnchorWidthMode::FitContent && has_measurement {
        min(initial, measured)
    } else {
        initial
    };
    let outer = f64::from(content) + f64::from(insets);
    if outer > f64::from(f32::MAX) {
        return Err(FontError::VerificationFailed);
    }
    Ok(NativeAnchorWidth {
        content_width: content,
        outer_width: outer as f32,
        requires_remeasure: content != initial,
    })
}
pub(super) fn place(
    reference: NativeExclusionRectangle,
    width: f32,
    height: f32,
    alignment: NativeAnchorAlignment,
    allow_delay: bool,
    floating: bool,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut [NativeLineInterval],
    intervals: &mut [NativeLineInterval],
    maximum_attempts: u32,
) -> Result<(NativeExclusionRectangle, u32), FontError> {
    if !width.is_finite()
        || !height.is_finite()
        || width <= 0.0
        || height <= 0.0
        || maximum_attempts == 0
        || maximum_attempts > LIMIT as u32
    {
        return Err(FontError::InvalidArgument);
    }
    native_resolve_text_intervals_into(reference, exclusions, scratch, intervals)?;
    let available = f64::from(reference.right) - f64::from(reference.left);
    if f64::from(width) > available
        || f64::from(height) > f64::from(reference.bottom) - f64::from(reference.top)
    {
        return Err(FontError::VerificationFailed);
    }
    let factor = match alignment {
        NativeAnchorAlignment::Left => 0.0,
        NativeAnchorAlignment::Right => 1.0,
        NativeAnchorAlignment::Center => 0.5,
    };
    let spare = available - f64::from(width);
    let left = (f64::from(reference.left) + spare * factor) as f32;
    let right = (f64::from(left) + f64::from(width)) as f32;
    if right <= left || right > reference.right {
        return Err(FontError::VerificationFailed);
    }
    let mut top = reference.top;
    for attempt in 0..maximum_attempts {
        let bottom = f64::from(top) + f64::from(height);
        if bottom > f64::from(reference.bottom) {
            break;
        }
        let bottom = bottom as f32;
        if bottom <= top {
            break;
        }
        let (count, next) = native_resolve_text_intervals_into(
            NativeExclusionRectangle {
                left: reference.left,
                top,
                right: reference.right,
                bottom,
            },
            exclusions,
            scratch,
            intervals,
        )?;
        for i in 0..count {
            let interval = intervals[if floating && alignment == NativeAnchorAlignment::Right {
                count - 1 - i
            } else {
                i
            }];
            let (mut candidate_left, mut candidate_right) = (left, right);
            if floating {
                let spare = f64::from(interval.right) - f64::from(interval.left) - f64::from(width);
                if spare < 0.0 {
                    continue;
                }
                candidate_left = (f64::from(interval.left) + spare * factor) as f32;
                candidate_right = (f64::from(candidate_left) + f64::from(width)) as f32;
            }
            if candidate_right > candidate_left
                && candidate_left >= interval.left
                && candidate_right <= interval.right
            {
                return Ok((
                    NativeExclusionRectangle {
                        left: candidate_left,
                        top,
                        right: candidate_right,
                        bottom,
                    },
                    attempt + 1,
                ));
            }
        }
        if !allow_delay || next <= top {
            break;
        }
        top = next;
    }
    Err(FontError::VerificationFailed)
}
pub fn native_place_text_anchor(
    reference: NativeExclusionRectangle,
    width: f32,
    height: f32,
    alignment: NativeAnchorAlignment,
    allow_delay: bool,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut [NativeLineInterval],
    intervals: &mut [NativeLineInterval],
    maximum_attempts: u32,
) -> Result<NativeExclusionRectangle, FontError> {
    place(
        reference,
        width,
        height,
        alignment,
        allow_delay,
        false,
        exclusions,
        scratch,
        intervals,
        maximum_attempts,
    )
    .map(|r| r.0)
}
pub fn native_place_text_floater(
    reference: NativeExclusionRectangle,
    width: f32,
    height: f32,
    alignment: NativeAnchorAlignment,
    allow_delay: bool,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut [NativeLineInterval],
    intervals: &mut [NativeLineInterval],
    maximum_attempts: u32,
) -> Result<NativeExclusionRectangle, FontError> {
    place(
        reference,
        width,
        height,
        alignment,
        allow_delay,
        true,
        exclusions,
        scratch,
        intervals,
        maximum_attempts,
    )
    .map(|r| r.0)
}
