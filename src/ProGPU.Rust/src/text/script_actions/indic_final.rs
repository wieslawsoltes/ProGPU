//! Original font- and substitution-sensitive final Indic record reordering.
use super::*;

pub(in crate::text::script_actions) fn final_reorder(
    g: &mut [ManagedShapingGlyph],
    font: &ManagedFont,
    script: &str,
    profile: ManagedIndicContinuationProfile,
) -> Result<(), PreparationError> {
    let mut start = 0;
    while start < g.len() {
        let end = syllable_end(g, start);
        final_syllable(g, start, end, font, script, profile)?;
        start = end;
    }
    Ok(())
}
fn final_syllable(
    g: &mut [ManagedShapingGlyph],
    start: usize,
    end: usize,
    font: &ManagedFont,
    script: &str,
    profile: ManagedIndicContinuationProfile,
) -> Result<(), PreparationError> {
    let mut reordered = false;
    let virama = font.glyph_index(virama(script))?;
    if virama != 0 {
        for glyph in &mut g[start..end] {
            if glyph.glyph_index == virama && glyph.ligated != 0 && glyph.multiplied != 0 {
                glyph.indic_category = 4;
                glyph.ligated = 0;
                glyph.multiplied = 0;
            }
        }
    }
    let mut prebase = g[start..end]
        .iter()
        .any(|glyph| glyph.script_feature_mask & PREF != 0);
    let mut base = start;
    while base < end && g[base].indic_position < BASE {
        base += 1;
    }
    if prebase && base + 1 < end {
        for i in base + 1..end {
            if g[i].script_feature_mask & PREF == 0 {
                continue;
            }
            if !(g[i].substituted != 0 && g[i].ligated != 0 && g[i].multiplied == 0) {
                base = i;
                while base < end && halant(g[base]) {
                    base += 1;
                }
                if base < end {
                    g[base].indic_position = BASE;
                }
                prebase = false;
            }
            break;
        }
    }
    if script == "mlym" && base < end {
        let mut i = base + 1;
        while i < end {
            while i < end && joiner(g[i]) {
                i += 1;
            }
            if i == end || !halant(g[i]) {
                break;
            }
            i += 1;
            while i < end && joiner(g[i]) {
                i += 1;
            }
            if i < end && consonant(g[i]) && g[i].indic_position == BELOW {
                base = i;
                g[base].indic_position = BASE;
            }
            i += 1;
        }
    }
    if base < end && base > start && g[base].indic_position > BASE {
        base -= 1;
    }
    if base == end && g[end - 1].indic_category == 6 {
        base -= 1;
    }
    while base > start
        && base < end
        && (g[base].ligated == 0 && g[base].indic_category == 3 || halant(g[base]))
    {
        base -= 1;
    }
    if start + 1 < end && start < base {
        let mut dest = if base == end { base - 2 } else { base - 1 };
        if !matches!(script, "mlym" | "taml") {
            loop {
                while dest > start && !matches!(g[dest].indic_category, 7 | 13 | 4) {
                    dest -= 1;
                }
                if !halant(g[dest]) || g[dest].indic_position == PRE_MATRA {
                    dest = start;
                    break;
                }
                if dest + 1 < end && g[dest + 1].indic_category == 6 && dest > start {
                    dest -= 1;
                    continue;
                }
                break;
            }
        }
        if dest > start && g[dest].indic_position != PRE_MATRA {
            for i in (start + 1..=dest).rev() {
                if g[i - 1].indic_position != PRE_MATRA {
                    continue;
                }
                g[i - 1..=dest].rotate_left(1);
                merge(g, dest as i32, end.min(base + 1))?;
                reordered = true;
                dest -= 1;
            }
        } else if let Some(i) = (start..base).find(|&i| g[i].indic_position == PRE_MATRA) {
            merge(g, i as i32, end.min(base + 1))?;
        }
    }
    if start + 1 < end
        && g[start].indic_position == REPH
        && ((g[start].indic_category == 14) ^ (g[start].ligated != 0 && g[start].multiplied == 0))
    {
        let dest = reph_destination(g, start, end, base, script);
        merge(g, start as i32, dest + 1)?;
        g[start..=dest].rotate_left(1);
        reordered = true;
        if start < base && base <= dest {
            base -= 1;
        }
    }
    if prebase && base + 1 < end {
        for i in base + 1..end {
            if g[i].script_feature_mask & PREF == 0 {
                continue;
            }
            if g[i].ligated != 0 && g[i].multiplied == 0 {
                let mut dest = base;
                if !matches!(script, "mlym" | "taml") {
                    while dest > start && !matches!(g[dest - 1].indic_category, 7 | 13 | 4) {
                        dest -= 1;
                    }
                }
                if dest > start && halant(g[dest - 1]) && dest < end && joiner(g[dest]) {
                    dest += 1;
                }
                merge(g, dest as i32, i + 1)?;
                g[dest..=i].rotate_right(1);
                reordered = true;
                // Original base update has no subsequent base consumer.
            }
            break;
        }
    }
    if reordered || g[start].indic_position == PRE_MATRA {
        merge(g, start as i32, end)?;
    }
    if g[start].indic_position == PRE_MATRA {
        if start == 0 || !profile.is_continuation(g[start - 1].code_point)? {
            g[start].script_feature_mask |= INIT;
        } else {
            let minimum = g[start - 1].cluster.min(g[start].cluster);
            for glyph in &mut g[start - 1..=start] {
                if glyph.cluster != minimum {
                    glyph.flags |= 3;
                }
            }
        }
    }
    Ok(())
}
fn reph_destination(
    g: &[ManagedShapingGlyph],
    start: usize,
    end: usize,
    base: usize,
    script: &str,
) -> usize {
    let desired = match script {
        "beng" => AFTER_SUB,
        "guru" => 7,
        "orya" | "mlym" => AFTER_MAIN,
        "taml" | "telu" | "knda" => AFTER_POST,
        _ => 10,
    };
    if desired != AFTER_POST {
        let mut explicit = start + 1;
        while explicit < base && !halant(g[explicit]) {
            explicit += 1;
        }
        if explicit < base {
            if explicit + 1 < base && joiner(g[explicit + 1]) {
                explicit += 1;
            }
            return explicit;
        }
    }
    if desired == AFTER_MAIN {
        let mut dest = base;
        while dest + 1 < end && g[dest + 1].indic_position <= AFTER_MAIN {
            dest += 1;
        }
        if dest < end {
            return dest;
        }
    }
    if desired == AFTER_SUB {
        let mut dest = base;
        while dest + 1 < end && !matches!(g[dest + 1].indic_position, POST | AFTER_POST | MODIFIER)
        {
            dest += 1;
        }
        if dest < end {
            return dest;
        }
    }
    let mut dest = end - 1;
    while dest > start && g[dest].indic_position == MODIFIER {
        dest -= 1;
    }
    if halant(g[dest]) && (base + 1..dest).any(|i| matches!(g[i].indic_category, 7 | 13)) {
        dest -= 1;
    }
    dest
}
