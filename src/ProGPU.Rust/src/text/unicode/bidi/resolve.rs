//! Direct port of ProGPU's original native resolver, preserving rule order.
//! O(N) scratch, fixed 127-entry explicit and 63-entry bracket stacks. Nested FSI
//! scans and bracket context searches retain the original O(N²) worst case.
use super::super::{BidiBracketKind, BidiClass as B, UnicodeScalar, bidi_bracket, bidi_class};
use super::{BidiBracketPair, BidiLevel, BidiLevelRun, BidiScratch, BidiUnit};
use B::*;

fn isolate(t: B) -> bool {
    matches!(
        t,
        LeftToRightIsolate | RightToLeftIsolate | FirstStrongIsolate
    )
}
fn removed(t: B) -> bool {
    matches!(
        t,
        RightToLeftEmbedding
            | LeftToRightEmbedding
            | RightToLeftOverride
            | LeftToRightOverride
            | PopDirectionalFormat
            | BoundaryNeutral
    )
}
fn neutral(t: B) -> bool {
    matches!(
        t,
        ParagraphSeparator
            | SegmentSeparator
            | Whitespace
            | OtherNeutral
            | LeftToRightIsolate
            | RightToLeftIsolate
            | FirstStrongIsolate
            | PopDirectionalIsolate
    )
}
fn trailing(t: B) -> bool {
    t == Whitespace || isolate(t) || t == PopDirectionalIsolate || removed(t)
}
fn direction(level: i8) -> B {
    if level & 1 == 0 {
        LeftToRight
    } else {
        RightToLeft
    }
}
fn strong(t: B) -> B {
    match t {
        LeftToRight => LeftToRight,
        RightToLeft | EuropeanNumber | ArabicNumber => RightToLeft,
        _ => t,
    }
}
fn odd_greater(level: i8) -> i8 {
    (level + 1) | 1
}
fn even_greater(level: i8) -> i8 {
    (level + 2) & !1
}
fn normalized_bracket(cp: u32) -> u32 {
    match cp {
        0x2329 => 0x3008,
        0x232a => 0x3009,
        _ => cp,
    }
}
fn paragraph(units: &[BidiUnit]) -> i8 {
    let mut depth = 0u32;
    for unit in units {
        let t = unit.original;
        if isolate(t) {
            depth += 1;
            continue;
        }
        if t == PopDirectionalIsolate {
            depth = depth.saturating_sub(1);
            continue;
        }
        if depth != 0 {
            continue;
        }
        if t == LeftToRight {
            return 0;
        }
        if matches!(t, RightToLeft | ArabicLetter) {
            return 1;
        }
    }
    0
}
fn match_isolates(units: &mut [BidiUnit], stack: &mut [u32]) {
    let mut count = 0;
    for i in 0..units.len() {
        if isolate(units[i].original) {
            stack[count] = i as u32;
            count += 1;
        } else if units[i].original == PopDirectionalIsolate && count != 0 {
            count -= 1;
            let initiator = stack[count] as usize;
            units[initiator].matching_isolate = i as i32;
            units[i].matching_isolate = initiator as i32;
        }
    }
}
#[derive(Clone, Copy, Default)]
struct Status {
    level: i8,
    direction: Option<B>,
    isolate: bool,
}
fn explicit(units: &mut [BidiUnit], paragraph_level: i8) {
    let mut stack = [Status::default(); 127];
    let mut count = 1;
    stack[0].level = paragraph_level;
    let (mut overflow_isolates, mut overflow_embeddings, mut valid_isolates) = (0u32, 0u32, 0u32);
    for i in 0..units.len() {
        let mut current = stack[count - 1];
        let mut unit = units[i];
        match unit.original {
            RightToLeftEmbedding | LeftToRightEmbedding | RightToLeftOverride
            | LeftToRightOverride => {
                let level = if matches!(unit.original, RightToLeftEmbedding | RightToLeftOverride) {
                    odd_greater(current.level)
                } else {
                    even_greater(current.level)
                };
                let override_direction = match unit.original {
                    RightToLeftOverride => Some(RightToLeft),
                    LeftToRightOverride => Some(LeftToRight),
                    _ => None,
                };
                if level <= 125 && overflow_isolates == 0 && overflow_embeddings == 0 {
                    stack[count] = Status {
                        level,
                        direction: override_direction,
                        isolate: false,
                    };
                    count += 1;
                } else if overflow_isolates == 0 {
                    overflow_embeddings += 1;
                }
            }
            RightToLeftIsolate | LeftToRightIsolate | FirstStrongIsolate => {
                let end = if unit.matching_isolate >= 0 {
                    unit.matching_isolate as usize
                } else {
                    units.len()
                };
                let rtl = unit.original == RightToLeftIsolate
                    || (unit.original == FirstStrongIsolate && paragraph(&units[i + 1..end]) == 1);
                unit.level = current.level;
                if let Some(d) = current.direction {
                    unit.resolved_type = d;
                } else if unit.original == FirstStrongIsolate {
                    unit.resolved_type = if rtl {
                        RightToLeftIsolate
                    } else {
                        LeftToRightIsolate
                    };
                }
                let level = if rtl {
                    odd_greater(current.level)
                } else {
                    even_greater(current.level)
                };
                if level <= 125 && overflow_isolates == 0 && overflow_embeddings == 0 {
                    valid_isolates += 1;
                    stack[count] = Status {
                        level,
                        direction: None,
                        isolate: true,
                    };
                    count += 1;
                } else {
                    overflow_isolates += 1;
                }
            }
            PopDirectionalIsolate => {
                if overflow_isolates != 0 {
                    overflow_isolates -= 1;
                } else if valid_isolates != 0 {
                    overflow_embeddings = 0;
                    while count > 1 && !stack[count - 1].isolate {
                        count -= 1;
                    }
                    if count > 1 {
                        count -= 1;
                    }
                    valid_isolates -= 1;
                }
                current = stack[count - 1];
                unit.level = current.level;
                if let Some(d) = current.direction {
                    unit.resolved_type = d;
                }
            }
            PopDirectionalFormat => {
                if overflow_isolates == 0 {
                    if overflow_embeddings != 0 {
                        overflow_embeddings -= 1;
                    } else if count > 1 && !stack[count - 1].isolate {
                        count -= 1;
                    }
                }
            }
            ParagraphSeparator => {
                unit.level = paragraph_level;
                count = 1;
                overflow_isolates = 0;
                overflow_embeddings = 0;
                valid_isolates = 0;
            }
            BoundaryNeutral => {}
            _ => {
                unit.level = current.level;
                if let Some(d) = current.direction {
                    unit.resolved_type = d;
                }
            }
        }
        units[i] = unit;
    }
}
fn paired_brackets(
    units: &mut [BidiUnit],
    sequence: &[u32],
    sor: B,
    pairs: &mut [BidiBracketPair],
) {
    let mut openings = [(0u32, 0u32); 63];
    let (mut opening_count, mut pair_count) = (0, 0);
    for (position, &index) in sequence.iter().enumerate() {
        let unit = units[index as usize];
        if unit.resolved_type != OtherNeutral {
            continue;
        }
        let Some(bracket) = bidi_bracket(unit.code_point) else {
            continue;
        };
        let normalized = normalized_bracket(unit.code_point);
        if bracket.kind == BidiBracketKind::Open {
            // Original overflow aborts this entire N0 resolution, retaining any
            // scratch pairs already collected; no partial bracket fixup is made.
            if opening_count == openings.len() {
                return;
            }
            openings[opening_count] = (
                position as u32,
                normalized_bracket(bracket.paired_code_point),
            );
            opening_count += 1;
            continue;
        }
        for opening in (0..opening_count).rev() {
            if openings[opening].1 != normalized {
                continue;
            }
            pairs[pair_count] = BidiBracketPair {
                open_position: openings[opening].0,
                close_position: position as u32,
            };
            pair_count += 1;
            opening_count = opening;
            break;
        }
    }
    pairs[..pair_count].sort_unstable_by_key(|p| p.open_position);
    for pair in &pairs[..pair_count] {
        let opening = pair.open_position as usize;
        let closing = pair.close_position as usize;
        let opening_unit = sequence[opening] as usize;
        let embedding = direction(units[opening_unit].level);
        let opposite = if embedding == LeftToRight {
            RightToLeft
        } else {
            LeftToRight
        };
        let (mut contains_embedding, mut contains_opposite) = (false, false);
        for &index in &sequence[opening + 1..closing] {
            let d = strong(units[index as usize].resolved_type);
            if d == embedding {
                contains_embedding = true;
                break;
            }
            contains_opposite |= d == opposite;
        }
        let resolved = if contains_embedding {
            embedding
        } else if contains_opposite {
            let mut preceding = sor;
            for &index in sequence[..opening].iter().rev() {
                let candidate = strong(units[index as usize].resolved_type);
                if matches!(candidate, LeftToRight | RightToLeft) {
                    preceding = candidate;
                    break;
                }
            }
            if preceding == opposite {
                opposite
            } else {
                embedding
            }
        } else {
            continue;
        };
        units[opening_unit].resolved_type = resolved;
        units[sequence[closing] as usize].resolved_type = resolved;
        for &index in &sequence[closing + 1..] {
            let unit = &mut units[index as usize];
            if unit.original != NonspacingMark {
                break;
            }
            unit.resolved_type = resolved;
        }
    }
}
fn resolve_sequence(
    units: &mut [BidiUnit],
    sequence: &[u32],
    sor: B,
    eor: B,
    pairs: &mut [BidiBracketPair],
) {
    // W1, W2, W3: preserve the original ordered scans and preceding-type state.
    let mut previous = sor;
    for &index in sequence {
        let unit = &mut units[index as usize];
        if unit.resolved_type == NonspacingMark {
            unit.resolved_type = if isolate(previous) || previous == PopDirectionalIsolate {
                OtherNeutral
            } else {
                previous
            };
        }
        previous = unit.resolved_type;
    }
    let mut last_strong = sor;
    for &index in sequence {
        let unit = &mut units[index as usize];
        if unit.resolved_type == EuropeanNumber && last_strong == ArabicLetter {
            unit.resolved_type = ArabicNumber;
        }
        if matches!(unit.resolved_type, LeftToRight | RightToLeft | ArabicLetter) {
            last_strong = unit.resolved_type;
        }
    }
    for &index in sequence {
        if units[index as usize].resolved_type == ArabicLetter {
            units[index as usize].resolved_type = RightToLeft;
        }
    }
    // W4: neighbours observe any earlier original in-place updates.
    for position in 1..sequence.len().saturating_sub(1) {
        let index = sequence[position] as usize;
        let t = units[index].resolved_type;
        let before = units[sequence[position - 1] as usize].resolved_type;
        let after = units[sequence[position + 1] as usize].resolved_type;
        if t == EuropeanSeparator && before == EuropeanNumber && after == EuropeanNumber {
            units[index].resolved_type = EuropeanNumber;
        } else if t == CommonSeparator
            && before == after
            && matches!(before, EuropeanNumber | ArabicNumber)
        {
            units[index].resolved_type = before;
        }
    }
    // W5 and W6.
    let mut cursor = 0;
    while cursor < sequence.len() {
        if units[sequence[cursor] as usize].resolved_type != EuropeanTerminator {
            cursor += 1;
            continue;
        }
        let start = cursor;
        while cursor < sequence.len()
            && units[sequence[cursor] as usize].resolved_type == EuropeanTerminator
        {
            cursor += 1;
        }
        let adjacent = (start != 0
            && units[sequence[start - 1] as usize].resolved_type == EuropeanNumber)
            || (cursor < sequence.len()
                && units[sequence[cursor] as usize].resolved_type == EuropeanNumber);
        if adjacent {
            for &index in &sequence[start..cursor] {
                units[index as usize].resolved_type = EuropeanNumber;
            }
        }
    }
    for &index in sequence {
        let unit = &mut units[index as usize];
        if matches!(
            unit.resolved_type,
            EuropeanSeparator | EuropeanTerminator | CommonSeparator
        ) {
            unit.resolved_type = OtherNeutral;
        }
    }
    // W7, N0.
    last_strong = sor;
    for &index in sequence {
        let unit = &mut units[index as usize];
        if unit.resolved_type == EuropeanNumber && last_strong == LeftToRight {
            unit.resolved_type = LeftToRight;
        }
        if matches!(unit.resolved_type, LeftToRight | RightToLeft) {
            last_strong = unit.resolved_type;
        }
    }
    paired_brackets(units, sequence, sor, pairs);
    // N1/N2.
    let mut position = 0;
    while position < sequence.len() {
        if !neutral(units[sequence[position] as usize].resolved_type) {
            position += 1;
            continue;
        }
        let start = position;
        while position < sequence.len() && neutral(units[sequence[position] as usize].resolved_type)
        {
            position += 1;
        }
        let before = strong(if start == 0 {
            sor
        } else {
            units[sequence[start - 1] as usize].resolved_type
        });
        let after = strong(if position == sequence.len() {
            eor
        } else {
            units[sequence[position] as usize].resolved_type
        });
        for &index in &sequence[start..position] {
            let unit = &mut units[index as usize];
            unit.resolved_type = if before == after {
                before
            } else {
                direction(unit.level)
            };
        }
    }
    // I1/I2. Preserve original int8 narrowing, including raw admitted inputs
    // whose isolate links cross paragraph separators and revisit a sequence.
    for &index in sequence {
        let unit = &mut units[index as usize];
        if unit.level & 1 == 0 {
            if unit.resolved_type == RightToLeft {
                unit.level = unit.level.wrapping_add(1);
            } else if matches!(unit.resolved_type, EuropeanNumber | ArabicNumber) {
                unit.level = unit.level.wrapping_add(2);
            }
        } else if matches!(
            unit.resolved_type,
            LeftToRight | EuropeanNumber | ArabicNumber
        ) {
            unit.level = unit.level.wrapping_add(1);
        }
    }
}
fn sequences(
    units: &mut [BidiUnit],
    paragraph_level: i8,
    indices: &mut [u32],
    runs: &mut [BidiLevelRun],
    pairs: &mut [BidiBracketPair],
) {
    let count = units.len();
    let (active, rest) = indices.split_at_mut(count);
    let (run_by_unit, rest) = rest.split_at_mut(count);
    let (active_position, sequence) = rest.split_at_mut(count);
    run_by_unit.fill(u32::MAX);
    active_position.fill(u32::MAX);
    let mut active_count = 0;
    for (index, unit) in units.iter().enumerate() {
        if !removed(unit.original) {
            active[active_count] = index as u32;
            active_position[index] = active_count as u32;
            active_count += 1;
        }
    }
    if active_count == 0 {
        return;
    }
    let (mut run_count, mut start) = (0, 0);
    while start < active_count {
        let level = units[active[start] as usize].level;
        let mut end = start + 1;
        while end < active_count && units[active[end] as usize].level == level {
            end += 1;
        }
        runs[run_count] = BidiLevelRun {
            active_start: start as u32,
            active_count: (end - start) as u32,
            explicit_level: level,
            ..Default::default()
        };
        for &index in &active[start..end] {
            run_by_unit[index as usize] = run_count as u32;
        }
        run_count += 1;
        start = end;
    }
    for run_index in 0..run_count {
        let run = runs[run_index];
        let last = active[(run.active_start + run.active_count - 1) as usize] as usize;
        if !isolate(units[last].original) || units[last].matching_isolate < 0 {
            continue;
        }
        let next = run_by_unit[units[last].matching_isolate as usize];
        if next == u32::MAX || next as usize == run_index {
            continue;
        }
        runs[run_index].next = next as i32;
        runs[next as usize].has_predecessor = true;
    }
    for run_index in 0..run_count {
        if runs[run_index].has_predecessor {
            continue;
        }
        let (mut sequence_count, mut current, mut last_run) = (0, run_index, run_index);
        while current < run_count {
            let run = runs[current];
            let n = run.active_count as usize;
            let start = run.active_start as usize;
            sequence[sequence_count..sequence_count + n].copy_from_slice(&active[start..start + n]);
            sequence_count += n;
            last_run = current;
            current = if run.next >= 0 {
                run.next as usize
            } else {
                run_count
            };
        }
        let first_position = active_position[sequence[0] as usize] as usize;
        let last_position = active_position[sequence[sequence_count - 1] as usize] as usize;
        let last_unit = units[sequence[sequence_count - 1] as usize];
        let preceding = if first_position != 0 {
            runs[run_by_unit[active[first_position - 1] as usize] as usize].explicit_level
        } else {
            paragraph_level
        };
        let unmatched = isolate(last_unit.original) && last_unit.matching_isolate < 0;
        let following = if !unmatched && last_position + 1 < active_count {
            runs[run_by_unit[active[last_position + 1] as usize] as usize].explicit_level
        } else {
            paragraph_level
        };
        let sor = direction(runs[run_index].explicit_level.max(preceding));
        let eor = direction(runs[last_run].explicit_level.max(following));
        resolve_sequence(units, &sequence[..sequence_count], sor, eor, pairs);
    }
}
fn line_l1(units: &mut [BidiUnit], paragraph_level: i8) {
    for i in 0..units.len() {
        if !matches!(units[i].original, ParagraphSeparator | SegmentSeparator) {
            continue;
        }
        units[i].level = paragraph_level;
        for unit in units[..i].iter_mut().rev() {
            if !trailing(unit.original) {
                break;
            }
            unit.level = paragraph_level;
        }
    }
    for unit in units.iter_mut().rev() {
        if !trailing(unit.original) {
            break;
        }
        unit.level = paragraph_level;
    }
}
pub(super) fn run(
    input: &[UnicodeScalar],
    requested: i8,
    scratch: BidiScratch<'_>,
    output: &mut [BidiLevel],
) -> i8 {
    let BidiScratch {
        units,
        indices,
        runs,
        bracket_pairs,
    } = scratch;
    for (scalar, unit) in input.iter().zip(units.iter_mut()) {
        let t = bidi_class(scalar.code_point);
        *unit = BidiUnit {
            code_point: scalar.code_point,
            input_index: scalar.input_index,
            input_length: scalar.input_length,
            original: t,
            resolved_type: t,
            ..Default::default()
        };
    }
    match_isolates(units, &mut indices[..input.len()]);
    let level = if matches!(requested, 0 | 1) {
        requested
    } else {
        paragraph(units)
    };
    explicit(units, level);
    sequences(units, level, indices, runs, bracket_pairs);
    line_l1(units, level);
    // Retain original X9 slots using the last nonremoved resolved level.
    let mut previous = level;
    for unit in units.iter_mut() {
        if removed(unit.original) {
            unit.level = previous;
        } else {
            previous = unit.level;
        }
    }
    for (unit, slot) in units.iter().zip(output) {
        *slot = BidiLevel {
            input_index: unit.input_index,
            input_length: unit.input_length,
            level: unit.level,
            reserved: 0,
        };
    }
    level
}
