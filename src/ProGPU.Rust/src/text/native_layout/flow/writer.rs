//! Original complete exclusion/floating flow loop. Every retry is bounded and
//! advances only through original height refits or actual obstacle bottoms.
use super::*;
struct Floats<'a> {
    items: &'a [NativeFloatingItem],
    collisions: &'a mut [NativeExclusionRectangle],
    placements: &'a mut [NativeFloatingPlacement],
    empty_metrics: NativeTextItemMetrics,
    initial: usize,
    count: usize,
    bottom: f64,
    right: f32,
}
enum Obstacles<'a> {
    Fixed(&'a [NativeExclusionRectangle]),
    Floating(Floats<'a>),
}
impl Obstacles<'_> {
    fn rectangles(&self) -> &[NativeExclusionRectangle] {
        match self {
            Self::Fixed(r) => r,
            Self::Floating(f) => &f.collisions[..f.initial + f.count],
        }
    }
    fn place_row(
        &mut self,
        total: usize,
        next: usize,
        row: u32,
        bottom: f64,
        width: f32,
        result: &mut NativeExclusionFlowResult,
        maximum_attempts: u32,
        scratch: &mut NativeExclusionScratch<'_>,
    ) -> Result<(), FontError> {
        let Self::Floating(f) = self else {
            return Ok(());
        };
        while f.count < f.items.len() {
            let item = f.items[f.count];
            if item.glyph_index as usize >= next && next != total {
                break;
            }
            if result.attempts >= maximum_attempts {
                return Err(FontError::VerificationFailed);
            }
            let (bounds, used) = intervals::place(
                NativeExclusionRectangle {
                    left: 0.0,
                    top: bottom as f32,
                    right: width,
                    bottom: f32::MAX,
                },
                item.width,
                item.height,
                item.alignment,
                true,
                true,
                &f.collisions[..f.initial + f.count],
                scratch.obstacles,
                scratch.intervals,
                maximum_attempts - result.attempts,
            )?;
            result.attempts += used;
            f.placements[f.count] = NativeFloatingPlacement {
                source_row: row,
                bounds,
            };
            f.collisions[f.initial + f.count] = bounds;
            f.count += 1;
            if f.bottom < f64::from(bounds.bottom) {
                f.bottom = f64::from(bounds.bottom);
            }
            f.right = max(f.right, bounds.right);
        }
        Ok(())
    }
}
fn core(
    input: NativeMeasuredInput<'_>,
    origin_y: f64,
    obstacles: &mut Obstacles<'_>,
    scratch: &mut NativeExclusionScratch<'_>,
    output: NativeFragmentOutput<'_>,
    maximum_attempts: u32,
) -> Result<NativeExclusionFlowResult, FontError> {
    let NativeMeasuredInput {
        glyphs,
        item_metrics: metrics,
        options,
        paragraph_level,
        ..
    } = input;
    if !origin_y.is_finite()
        || origin_y < 0.0
        || origin_y > f64::from(f32::MAX)
        || glyphs.len() > LIMIT
        || metrics.len() != glyphs.len()
        || maximum_attempts == 0
        || maximum_attempts > LIMIT as u32
    {
        return Err(FontError::InvalidArgument);
    }
    let maximum = super::super::writer::envelope(metrics)?;
    validate_options(options)?;
    if options.maximum_width <= 0.0 {
        return Err(FontError::InvalidArgument);
    }
    if output.glyphs.len() < glyphs.len()
        || output.lines.len() < glyphs.len()
        || output.placements.len() < glyphs.len()
    {
        return Err(FontError::Capacity);
    }
    let initial = max(options.line_height, maximum.ascent + maximum.descent);
    if !initial.is_finite() || !glyphs.is_empty() && initial <= 0.0 {
        return Err(FontError::InvalidArgument);
    }
    let mut result = NativeExclusionFlowResult::default();
    if glyphs.is_empty() {
        band::layout(
            input,
            0,
            NativeExclusionRectangle {
                left: 0.0,
                top: 0.0,
                right: options.maximum_width,
                bottom: 1.0,
            },
            obstacles.rectangles(),
            scratch,
            output.glyphs,
            output.lines,
            false,
        )?;
        result.height = origin_y;
        let empty = match obstacles {
            Obstacles::Floating(f) if !f.items.is_empty() => Some(f.empty_metrics),
            _ => None,
        };
        if let Some(empty) = empty {
            let height = max(options.line_height, empty.ascent + empty.descent);
            if !height.is_finite() || height <= 0.0 {
                return Err(FontError::InvalidArgument);
            }
            if output.lines.is_empty() || output.placements.is_empty() {
                return Err(FontError::Capacity);
            }
            let mut top = origin_y;
            loop {
                if result.attempts >= maximum_attempts {
                    return Err(FontError::VerificationFailed);
                }
                result.attempts += 1;
                let band_top = top as f32;
                let bottom = band_top + height;
                let baseline = band_top + empty.ascent;
                if !bottom.is_finite() || !baseline.is_finite() || bottom <= band_top {
                    return Err(FontError::InvalidArgument);
                }
                let (count, next) = native_resolve_text_intervals_into(
                    NativeExclusionRectangle {
                        left: 0.0,
                        top: band_top,
                        right: options.maximum_width,
                        bottom,
                    },
                    obstacles.rectangles(),
                    scratch.obstacles,
                    scratch.intervals,
                )?;
                if count == 0 {
                    if next <= band_top {
                        return Err(FontError::VerificationFailed);
                    }
                    top = f64::from(next);
                    continue;
                }
                let interval = scratch.intervals[if paragraph_level == 0 { 0 } else { count - 1 }];
                output.lines[0] = NativePositionedTextLine {
                    baseline_y: baseline,
                    height,
                    ..NativePositionedTextLine::default()
                };
                output.placements[0] = NativeFragmentPlacement {
                    row_index: 0,
                    left: interval.left,
                    top,
                    width: interval.right - interval.left,
                };
                result.fragment_count = 1;
                result.row_count = 1;
                result.height = top + f64::from(height);
                let bottom = result.height;
                obstacles.place_row(
                    0,
                    0,
                    0,
                    bottom,
                    options.maximum_width,
                    &mut result,
                    maximum_attempts,
                    scratch,
                )?;
                break;
            }
        }
        return Ok(result);
    }
    let seed = |i: usize| max(options.line_height, metrics[i].ascent + metrics[i].descent);
    let mut top = origin_y;
    let mut height = seed(0);
    let mut validated = false;
    while (result.next_glyph as usize) < glyphs.len() {
        if result.attempts >= maximum_attempts {
            return Err(FontError::VerificationFailed);
        }
        result.attempts += 1;
        let band_top = top as f32;
        let bottom = band_top + height;
        if !bottom.is_finite() || bottom <= band_top {
            return Err(FontError::InvalidArgument);
        }
        let band = band::layout(
            input,
            result.next_glyph as usize,
            NativeExclusionRectangle {
                left: 0.0,
                top: band_top,
                right: options.maximum_width,
                bottom,
            },
            obstacles.rectangles(),
            scratch,
            &mut output.glyphs[result.glyph_count as usize..],
            &mut output.lines[result.fragment_count as usize..],
            validated,
        )?;
        validated = true;
        if band.status == NativeExclusionBandStatus::RefitHeight {
            height = band.height;
            continue;
        }
        if band.status == NativeExclusionBandStatus::Blocked {
            if band.next_y <= band_top {
                return Err(FontError::VerificationFailed);
            }
            top = f64::from(band.next_y);
            height = seed(result.next_glyph as usize);
            continue;
        }
        if band.status != NativeExclusionBandStatus::Placed || band.next_glyph <= result.next_glyph
        {
            return Err(FontError::VerificationFailed);
        }
        for i in 0..band.fragment_count as usize {
            let index = result.fragment_count as usize + i;
            output.lines[index].glyph_start += result.glyph_count;
            output.placements[index] = NativeFragmentPlacement {
                row_index: result.row_count,
                left: scratch.fragments[i].left,
                top,
                width: scratch.fragments[i].width,
            };
        }
        result.glyph_count += band.glyph_count;
        result.fragment_count += band.fragment_count;
        result.next_glyph = band.next_glyph;
        result.row_count += 1;
        top += f64::from(band.height);
        result.height = top;
        obstacles.place_row(
            glyphs.len(),
            result.next_glyph as usize,
            result.row_count - 1,
            top,
            options.maximum_width,
            &mut result,
            maximum_attempts,
            scratch,
        )?;
        if options.maximum_lines != 0 && result.row_count >= options.maximum_lines {
            if (result.next_glyph as usize) < glyphs.len() {
                output.lines[result.fragment_count as usize - 1].clipped = true;
            }
            break;
        }
        if (result.next_glyph as usize) < glyphs.len() {
            height = seed(result.next_glyph as usize);
        }
    }
    let n = result.fragment_count as usize;
    result.content_width = native_measure_fragment_lines(
        &output.lines[..n],
        &output.placements[..n],
        options.maximum_width,
    )?
    .content_width;
    Ok(result)
}
/// Full original measured exclusion flow. A failed low-level writer may change
/// used scratch/output prefixes; only the returned success publishes counts.
pub fn native_layout_excluded_into(
    input: NativeMeasuredInput<'_>,
    origin_y: f64,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut NativeExclusionScratch<'_>,
    output: NativeFragmentOutput<'_>,
    maximum_attempts: u32,
) -> Result<NativeExclusionFlowResult, FontError> {
    core(
        input,
        origin_y,
        &mut Obstacles::Fixed(exclusions),
        scratch,
        output,
        maximum_attempts,
    )
}
/// Original floating events retain source order, actual cluster boundaries and
/// cumulative collisions. No event is represented by an invented font glyph.
pub fn native_layout_floating_into(
    input: NativeMeasuredInput<'_>,
    origin_y: f64,
    empty_metrics: NativeTextItemMetrics,
    floats: &[NativeFloatingItem],
    exclusions: &[NativeExclusionRectangle],
    collisions: &mut [NativeExclusionRectangle],
    scratch: &mut NativeExclusionScratch<'_>,
    output: NativeFragmentOutput<'_>,
    float_placements: &mut [NativeFloatingPlacement],
    maximum_attempts: u32,
) -> Result<NativeFloatingFlowResult, FontError> {
    if floats.len() > LIMIT
        || exclusions.len() > LIMIT - floats.len()
        || !empty_metrics.ascent.is_finite()
        || !empty_metrics.descent.is_finite()
        || empty_metrics.ascent < 0.0
        || empty_metrics.descent < 0.0
    {
        return Err(FontError::InvalidArgument);
    }
    let capacity = exclusions.len() + floats.len();
    if collisions.len() < capacity
        || scratch.obstacles.len() < capacity
        || scratch.intervals.len() <= capacity
        || scratch.fragments.len() <= capacity
        || float_placements.len() < floats.len()
    {
        return Err(FontError::Capacity);
    }
    let mut previous = 0;
    for item in floats {
        let index = item.glyph_index as usize;
        if item.glyph_index < previous
            || index > input.glyphs.len()
            || !item.width.is_finite()
            || !item.height.is_finite()
            || item.width <= 0.0
            || item.height <= 0.0
            || index > 0
                && index < input.glyphs.len()
                && input.glyphs[index - 1].cluster == input.glyphs[index].cluster
        {
            return Err(FontError::InvalidArgument);
        }
        previous = item.glyph_index;
    }
    collisions[..exclusions.len()].copy_from_slice(exclusions);
    let mut obstacles = Obstacles::Floating(Floats {
        items: floats,
        collisions,
        placements: float_placements,
        empty_metrics,
        initial: exclusions.len(),
        count: 0,
        bottom: 0.0,
        right: 0.0,
    });
    let text = core(
        input,
        origin_y,
        &mut obstacles,
        scratch,
        output,
        maximum_attempts,
    )?;
    let Obstacles::Floating(state) = obstacles else {
        unreachable!()
    };
    Ok(NativeFloatingFlowResult {
        text,
        float_count: state.count as u32,
        height: if text.height < state.bottom {
            state.bottom
        } else {
            text.height
        },
        content_width: max(text.content_width, state.right),
    })
}
