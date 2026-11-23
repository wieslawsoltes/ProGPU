//! Original native joining automaton over decoded scalars and original clusters.
//! Classification shares only the existing frozen Unicode 17 property facts.
use super::*;
use crate::text::script_actions::managed_joining_type;

const STATES: [[(u8, u8, usize); 6]; 7] = [
    [
        (7, 7, 0),
        (7, 0, 2),
        (7, 0, 1),
        (7, 0, 2),
        (7, 0, 1),
        (7, 0, 6),
    ],
    [
        (7, 7, 0),
        (7, 0, 2),
        (7, 0, 1),
        (7, 0, 2),
        (7, 2, 5),
        (7, 0, 6),
    ],
    [
        (7, 7, 0),
        (7, 0, 2),
        (6, 1, 1),
        (6, 1, 3),
        (6, 1, 4),
        (6, 1, 6),
    ],
    [
        (7, 7, 0),
        (7, 0, 2),
        (4, 1, 1),
        (4, 1, 3),
        (4, 1, 4),
        (4, 1, 6),
    ],
    [
        (7, 7, 0),
        (7, 0, 2),
        (5, 0, 1),
        (5, 0, 2),
        (5, 2, 5),
        (5, 0, 6),
    ],
    [
        (7, 7, 0),
        (7, 0, 2),
        (0, 0, 1),
        (0, 0, 2),
        (0, 2, 5),
        (0, 0, 6),
    ],
    [
        (7, 7, 0),
        (7, 0, 2),
        (7, 0, 1),
        (7, 0, 2),
        (7, 3, 5),
        (7, 0, 6),
    ],
];
pub(super) const ACTION_MASK: u32 = 0xf0000000;
pub(super) fn action(g: ShapingGlyph) -> u8 {
    (g.flags >> 28) as u8
}
pub(super) fn set_action(g: &mut ShapingGlyph, v: u8) {
    g.flags = (g.flags & !ACTION_MASK) | (u32::from(v) << 28);
}
pub(super) fn clear(g: &mut [ShapingGlyph]) {
    for g in g {
        g.flags &= !ACTION_MASK;
    }
}
fn kind(cp: u32) -> usize {
    managed_joining_type(cp) as usize
}
pub(super) fn assign(
    input: &[UnicodeScalar],
    clusters: &[GraphemeCluster],
    pre: &[UnicodeScalar],
    post: &[UnicodeScalar],
    flags: NativeBufferFlags,
    actions: &mut [u8],
    output: &mut [u32],
) -> Result<(), FontError> {
    if actions.len() < input.len() || output.len() < input.len() {
        return Err(FontError::Capacity);
    }
    let mut n = 0usize;
    for c in clusters {
        if c.scalar_count == 0
            || c.scalar_index as usize != n
            || c.scalar_count as usize > input.len().saturating_sub(n)
        {
            return Err(FontError::InvalidArgument);
        }
        n += c.scalar_count as usize;
    }
    if n != input.len()
        || input
            .iter()
            .chain(pre)
            .chain(post)
            .any(|s| s.code_point > 0x10ffff || (0xd800..=0xdfff).contains(&s.code_point))
    {
        return Err(FontError::InvalidArgument);
    }
    actions[..n].fill(7);
    let mut previous = n;
    let mut initial = 0;
    for s in pre.iter().rev().take(5) {
        let k = kind(s.code_point);
        if k == 6 {
            continue;
        }
        initial = STATES[0][k].2;
        break;
    }
    let mut state = initial;
    for (i, s) in input.iter().enumerate() {
        let k = kind(s.code_point);
        if k == 6 {
            continue;
        }
        let (a, b, next) = STATES[state][k];
        if a != 7 && previous < n {
            actions[previous] = a;
        }
        actions[i] = b;
        previous = i;
        state = next;
    }
    for s in post.iter().take(5) {
        let k = kind(s.code_point);
        if k == 6 {
            continue;
        }
        let (a, _, _) = STATES[state][k];
        if a != 7 && previous < n {
            actions[previous] = a;
        }
        break;
    }
    output[..n].fill(0);
    let mut add = |start: usize, end: usize, safe: bool| {
        if start >= end || end > n {
            return;
        }
        let mut first = start;
        let value = if safe {
            let after = clusters.partition_point(|c| c.scalar_index as usize <= start);
            let c = clusters[after - 1];
            first = first.max(c.scalar_index as usize + c.scalar_count as usize);
            if flags.contains(NativeBufferFlags::PRODUCE_SAFE_TO_INSERT_TATWEEL) {
                4
            } else {
                3
            }
        } else if flags.contains(NativeBufferFlags::PRODUCE_UNSAFE_TO_CONCAT) {
            2
        } else {
            return;
        };
        for f in &mut output[first.min(end)..end] {
            *f |= value;
        }
    };
    previous = n;
    state = initial;
    for (i, s) in input.iter().enumerate() {
        let k = kind(s.code_point);
        if k == 6 {
            continue;
        }
        let (a, _, next) = STATES[state][k];
        if a != 7 && previous < n {
            add(previous, i + 1, true);
        } else if previous == n {
            if k >= 2 {
                add(0, i + 1, false);
            }
        } else if k >= 2 || (2..=5).contains(&state) {
            add(previous, i + 1, false);
        }
        previous = i;
        state = next;
    }
    for s in post.iter().take(5) {
        let k = kind(s.code_point);
        if k == 6 {
            continue;
        }
        let (a, _, _) = STATES[state][k];
        if a != 7 && previous < n {
            add(previous, n, true);
        } else if previous < n && (2..=5).contains(&state) {
            add(previous, n, false);
        }
        break;
    }
    Ok(())
}
fn modifier(cp: u32) -> bool {
    matches!(
        cp,
        0x654
            | 0x655
            | 0x658
            | 0x6dc
            | 0x6e3
            | 0x6e7
            | 0x6e8
            | 0x8ca
            | 0x8cb
            | 0x8cd
            | 0x8ce
            | 0x8cf
            | 0x8d3
            | 0x8f3
    )
}
pub(super) fn reorder_modifiers(g: &mut [ShapingGlyph], mut start: usize, end: usize) {
    for canonical in [220, 230] {
        let mut first = start;
        while first < end && input::modified_class(g[first].code_point) < canonical {
            first += 1;
        }
        if first == end || input::modified_class(g[first].code_point) != canonical {
            continue;
        }
        let mut last = first;
        while last < end
            && input::modified_class(g[last].code_point) == canonical
            && modifier(g[last].code_point)
        {
            last += 1;
        }
        if last == first {
            continue;
        }
        g[start..last].rotate_left(first - start);
        start += last - first;
    }
}
