//! Original native monotone safe-boundary verification and bounded context replay.
use super::*;
/// Original borrowed fragment options. Other font, feature, language, direction
/// and variation state remains frozen in the supplied native shaper generation.
#[derive(Clone, Copy, Debug)]
pub struct NativeFragmentContext<'a> {
    /// Original fragment flags with Verify removed and interior BOT/EOT cleared.
    pub flags: NativeBufferFlags,
    /// At most five original decoded scalars preceding this fragment.
    pub pre_context: &'a [UnicodeScalar],
    /// At most five original decoded scalars following this fragment.
    pub post_context: &'a [UnicodeScalar],
}
/// Actual native device fragment producer. Each call must retain an independent
/// captured generation and must not overwrite the original expected run's source
/// descriptors, metric owners or output. This is a contract, not a hinted producer.
pub trait NativeDeviceFragmentSource {
    /// Shape the exact original source fragment in its actual device frame into
    /// bounded output. Keep original source indices and complete positioned fields.
    /// The producer owns its internal scratch and retires only its fragment owners.
    fn shape_fragment(
        &mut self,
        shaper: &NativeShaper,
        input: &[UnicodeScalar],
        context: NativeFragmentContext<'_>,
        output: &mut [ShapingGlyph],
    ) -> Result<usize, FontError>;
}
fn pre_context(
    outer: &[UnicodeScalar],
    prefix: &[UnicodeScalar],
    storage: &mut [UnicodeScalar; 5],
) -> usize {
    let prefix_count = prefix.len().min(5);
    let outer_count = outer.len().min(5 - prefix_count);
    storage[..outer_count].copy_from_slice(&outer[outer.len() - outer_count..]);
    storage[outer_count..outer_count + prefix_count]
        .copy_from_slice(&prefix[prefix.len() - prefix_count..]);
    outer_count + prefix_count
}
fn post_context(
    suffix: &[UnicodeScalar],
    outer: &[UnicodeScalar],
    storage: &mut [UnicodeScalar; 5],
) -> usize {
    let suffix_count = suffix.len().min(5);
    let outer_count = outer.len().min(5 - suffix_count);
    storage[..suffix_count].copy_from_slice(&suffix[..suffix_count]);
    storage[suffix_count..suffix_count + outer_count].copy_from_slice(&outer[..outer_count]);
    suffix_count + outer_count
}
fn same(left: ShapingGlyph, right: ShapingGlyph) -> bool {
    left.glyph_id == right.glyph_id
        && left.cluster == right.cluster
        && left.code_point == right.code_point
        && left.advance_x == right.advance_x
        && left.advance_y == right.advance_y
        && left.offset_x == right.offset_x
        && left.offset_y == right.offset_y
}
impl NativeShaper {
    /// Replay each original safe fragment and compare all positioned fields except
    /// flags. Scratch is independent of `expected`; capacity errors remain distinct
    /// from original verification failure. No provider or pixel parity is inferred.
    pub fn verify(
        &self,
        input: &[UnicodeScalar],
        expected: &[ShapingGlyph],
        scratch: &mut NativeShapeScratch,
    ) -> Result<(), FontError> {
        self.verify_with(input, expected, scratch, None)
    }
    /// Verify original captured-device output using an independent actual device
    /// fragment producer. Retain the complete caller output capacity as in native;
    /// no design-unit replay or synthetic captured metrics replace the producer.
    pub fn verify_device(
        &self,
        input: &[UnicodeScalar],
        expected: &[ShapingGlyph],
        scratch: &mut NativeShapeScratch,
        source: &mut dyn NativeDeviceFragmentSource,
    ) -> Result<(), FontError> {
        self.verify_with(input, expected, scratch, Some(source))
    }
    fn verify_with(
        &self,
        input: &[UnicodeScalar],
        expected: &[ShapingGlyph],
        scratch: &mut NativeShapeScratch,
        mut device: Option<&mut dyn NativeDeviceFragmentSource>,
    ) -> Result<(), FontError> {
        let required = self.requirements(input)?;
        if scratch.glyphs.len() < required.glyph_capacity {
            return Err(FontError::Capacity);
        }
        if expected.is_empty() {
            return Ok(());
        }
        if input.is_empty() {
            return Err(FontError::VerificationFailed);
        }
        if !matches!(
            self.level,
            NativeClusterLevel::MonotoneGraphemes | NativeClusterLevel::MonotoneCharacters
        ) {
            return Ok(());
        }
        let forward = matches!(
            self.route.direction,
            GposDirection::LeftToRight | GposDirection::TopToBottom
        );
        for pair in expected.windows(2) {
            let a = pair[0].cluster;
            let b = pair[1].cluster;
            if a != b && ((a < b) != forward) {
                return Err(FontError::VerificationFailed);
            }
        }
        let run_start = input[0].input_index;
        let last = input[input.len() - 1];
        let run_end = last
            .input_index
            .checked_add(last.input_length)
            .ok_or(FontError::VerificationFailed)?;
        let mut output_start = 0;
        let mut logical_edge = if forward { run_start } else { run_end };
        // Limit the fragment writer to the original whole-run declared capacity.
        let result = (|| {
            for output_end in 1..=expected.len() {
                let at_end = output_end == expected.len();
                if !at_end {
                    let left = expected[output_end - 1];
                    let right = expected[output_end];
                    if left.cluster == right.cluster {
                        continue;
                    }
                    let boundary = if forward { right } else { left };
                    if boundary.flags & 1 != 0 {
                        continue;
                    }
                }
                let cluster = |index: usize| {
                    u32::try_from(expected[index].cluster)
                        .map_err(|_| FontError::VerificationFailed)
                };
                let (start, end) = if forward {
                    let end = if at_end {
                        run_end
                    } else {
                        cluster(output_end)?
                    };
                    let start = logical_edge;
                    logical_edge = end;
                    (start, end)
                } else {
                    let start = if at_end {
                        run_start
                    } else {
                        cluster(output_end - 1)?
                    };
                    let end = logical_edge;
                    logical_edge = start;
                    (start, end)
                };
                if start >= end {
                    return Err(FontError::VerificationFailed);
                }
                let first = input.partition_point(|s| s.input_index < start);
                let last = input.partition_point(|s| s.input_index < end);
                if first == last
                    || first == input.len()
                    || input[first].input_index != start
                    || last != input.len() && input[last].input_index != end
                {
                    return Err(FontError::VerificationFailed);
                }
                let mut flags = self.flags.0 & !NativeBufferFlags::VERIFY.0;
                if start != run_start {
                    flags &= !NativeBufferFlags::BEGINNING_OF_TEXT.0;
                }
                if end != run_end {
                    flags &= !NativeBufferFlags::END_OF_TEXT.0;
                }
                let mut pre = [UnicodeScalar::default(); 5];
                let mut post = [UnicodeScalar::default(); 5];
                let pre_count = pre_context(&self.pre_context, &input[..first], &mut pre);
                let post_count = post_context(&input[last..], &self.post_context, &mut post);
                let shaped = if let Some(source) = device.as_deref_mut() {
                    scratch.glyph_count = 0;
                    source
                        .shape_fragment(
                            self,
                            &input[first..last],
                            NativeFragmentContext {
                                flags: NativeBufferFlags(flags),
                                pre_context: &pre[..pre_count],
                                post_context: &post[..post_count],
                            },
                            &mut scratch.glyphs,
                        )
                        .and_then(|count| {
                            if count > scratch.glyphs.len() {
                                return Err(FontError::VerificationFailed);
                            }
                            scratch.glyph_count = count;
                            Ok(())
                        })
                } else {
                    self.shape_unverified(
                        &input[first..last],
                        scratch,
                        NativeBufferFlags(flags),
                        &pre[..pre_count],
                        &post[..post_count],
                        required.glyph_capacity,
                    )
                };
                shaped.map_err(|error| {
                    if error == FontError::Capacity {
                        error
                    } else {
                        FontError::VerificationFailed
                    }
                })?;
                let count = output_end - output_start;
                if scratch.glyph_count != count
                    || !expected[output_start..output_end]
                        .iter()
                        .zip(scratch.glyphs())
                        .all(|(&left, &right)| same(left, right))
                {
                    return Err(FontError::VerificationFailed);
                }
                output_start = output_end;
            }
            Ok(())
        })();
        result
    }
}
