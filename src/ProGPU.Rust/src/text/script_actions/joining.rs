//! Original contextual joining over full managed glyph records.
//! O(N log R) for N glyphs and R immutable property ranges; O(1) scratch.
//! Interior flag visits cover disjoint gaps apart from their shared endpoints.
use crate::text::{ManagedShapingGlyph, preparation::ManagedBufferFlags};
#[path = "joining_data.rs"]
mod data;

/// Original managed joining columns, including the two distinct Syriac groups.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum ManagedJoiningType {
    /// No cursive join.
    NonJoining = 0,
    /// Joins to the following glyph in logical order.
    LeftJoining = 1,
    /// Joins to the preceding glyph in logical order.
    RightJoining = 2,
    /// Both sides; includes the original join-causing classification.
    DualJoining = 3,
    /// Syriac Alaph, with independent final and medial forms.
    Alaph = 4,
    /// Syriac Dalath/Rish, selecting the independent following-Alaph form.
    DalathRish = 5,
    /// Does not advance the joining state.
    Transparent = 6,
}

/// Frozen original Unicode 17 joining classification. Raw invalid values retain
/// NonJoining, independently of Rune/category and normalization profiles.
pub fn managed_joining_type(point: u32) -> ManagedJoiningType {
    let index = data::JOINING.partition_point(|&(_, end, _)| end < point);
    let value = data::JOINING
        .get(index)
        .filter(|&&(start, _, _)| start <= point)
        .map_or(0, |&(_, _, value)| value);
    use ManagedJoiningType::*;
    match value {
        1 => LeftJoining,
        2 => RightJoining,
        3 => DualJoining,
        4 => Alaph,
        5 => DalathRish,
        6 => Transparent,
        _ => NonJoining,
    }
}

// Original ProGPU state entries: previous action, current action, next state.
// Actions are isolated=0, final=1, final2=2, final3=3, medial=4,
// medial2=5, initial=6, none=7. Keep Syriac columns and all seven states.
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

fn preceding_scalar(units: &[u16], offset: &mut usize) -> u32 {
    *offset -= 1;
    let last = units[*offset];
    if (0xdc00..=0xdfff).contains(&last) && *offset > 0 {
        let first = units[*offset - 1];
        if (0xd800..=0xdbff).contains(&first) {
            *offset -= 1;
            return 0x10000 + ((first as u32 - 0xd800) << 10) + last as u32 - 0xdc00;
        }
    }
    if (0xd800..=0xdfff).contains(&last) {
        0xfffd
    } else {
        last as u32
    }
}

fn unsafe_concat(glyphs: &mut [ManagedShapingGlyph], flags: ManagedBufferFlags) {
    if flags.contains(ManagedBufferFlags::PRODUCE_UNSAFE_TO_CONCAT) {
        for glyph in glyphs {
            glyph.flags |= 2;
        }
    }
}

fn safe_tatweel(glyphs: &mut [ManagedShapingGlyph], flags: ManagedBufferFlags) {
    if glyphs.len() < 2 {
        return;
    }
    let minimum = glyphs.iter().map(|g| g.cluster).min().unwrap();
    let value = if flags.contains(ManagedBufferFlags::PRODUCE_SAFE_TO_INSERT_TATWEEL) {
        4
    } else {
        3
    };
    for glyph in glyphs {
        if glyph.cluster != minimum {
            glyph.flags |= value;
        }
    }
}

pub(super) fn assign(
    glyphs: &mut [ManagedShapingGlyph],
    script: &str,
    flags: ManagedBufferFlags,
    pre: &[u16],
    post: &[u16],
) {
    if !matches!(
        script,
        "adlm"
            | "arab"
            | "chrs"
            | "rohg"
            | "mand"
            | "mani"
            | "mong"
            | "nkoo"
            | "ougr"
            | "phag"
            | "phlp"
            | "sogd"
            | "syrc"
    ) {
        return;
    }
    let mut previous: Option<usize> = None;
    let mut state = 0;
    let mut offset = pre.len();
    for _ in 0..5 {
        if offset == 0 {
            break;
        }
        let kind = managed_joining_type(preceding_scalar(pre, &mut offset));
        if kind == ManagedJoiningType::Transparent {
            continue;
        }
        state = STATES[state][kind as usize].2;
        break;
    }
    for index in 0..glyphs.len() {
        let kind = managed_joining_type(glyphs[index].code_point);
        if kind == ManagedJoiningType::Transparent {
            glyphs[index].arabic_action = 7;
            continue;
        }
        let (prior_action, action, next) = STATES[state][kind as usize];
        match previous {
            Some(prior) if prior_action != 7 => {
                glyphs[prior].arabic_action = prior_action;
                safe_tatweel(&mut glyphs[prior..=index], flags);
            }
            None if kind as u8 >= ManagedJoiningType::RightJoining as u8 => {
                unsafe_concat(&mut glyphs[..=index], flags);
            }
            Some(prior)
                if kind as u8 >= ManagedJoiningType::RightJoining as u8
                    || (2..=5).contains(&state) =>
            {
                unsafe_concat(&mut glyphs[prior..=index], flags);
            }
            _ => {}
        }
        glyphs[index].arabic_action = action;
        previous = Some(index);
        state = next;
    }
    for value in char::decode_utf16(post.iter().copied()).take(5) {
        let kind = managed_joining_type(value.unwrap_or(char::REPLACEMENT_CHARACTER) as u32);
        if kind == ManagedJoiningType::Transparent {
            continue;
        }
        let (prior_action, _, _) = STATES[state][kind as usize];
        if let Some(prior) = previous {
            if prior_action != 7 {
                glyphs[prior].arabic_action = prior_action;
                safe_tatweel(&mut glyphs[prior..], flags);
            } else if (2..=5).contains(&state) {
                unsafe_concat(&mut glyphs[prior..], flags);
            }
        }
        break;
    }
}
