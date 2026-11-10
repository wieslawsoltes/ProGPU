//! Original native Hangul, Hebrew and Thai/Lao preprocessing.
use super::*;

fn erase(b: &mut Buffer<'_>, at: usize, n: usize) {
    b.storage.copy_within(at + n..b.count, at);
    b.count -= n;
}
fn minimum(g: &[ShapingGlyph]) -> i32 {
    g.iter().map(|g| g.cluster).min().unwrap()
}
fn set_jamo(g: &mut ShapingGlyph, feature: u32) {
    // Keep the original preprocessing bits, distinct from the native consumer.
    g.flags = (g.flags & !0x1800) | (feature << 11);
}
fn leading(cp: u32) -> bool {
    matches!(cp, 0x1100..=0x115f | 0xa960..=0xa97c)
}
fn vowel(cp: u32) -> bool {
    matches!(cp, 0x1160..=0x11a7 | 0xd7b0..=0xd7c6)
}
fn trailing(cp: u32) -> bool {
    matches!(cp, 0x11a8..=0x11ff | 0xd7cb..=0xd7fb)
}
fn map(font: &SfntFace, cp: u32) -> Result<u32, FontError> {
    font.glyph_index(cp)
        .map(u32::from)
        .ok_or(FontError::InvalidFace)
}
pub(super) fn hangul(font: &SfntFace, b: &mut Buffer<'_>) -> Result<(), FontError> {
    b.reserve(
        b.glyphs()
            .iter()
            .filter(|g| (0xac00..=0xd7a3).contains(&g.code_point))
            .count()
            * 2,
    )?;
    for g in b.glyphs_mut() {
        set_jamo(g, 0);
    }
    let mut i = 0;
    while i < b.count {
        let cp = b.storage[i].code_point;
        if leading(cp) && i + 1 < b.count && vowel(b.storage[i + 1].code_point) {
            let v = b.storage[i + 1].code_point;
            let t = b
                .storage
                .get(i + 2)
                .filter(|_| i + 2 < b.count)
                .map_or(0, |g| {
                    if trailing(g.code_point) {
                        g.code_point
                    } else {
                        0
                    }
                });
            let n = if t == 0 { 2 } else { 3 };
            if (0x1100..=0x1112).contains(&cp)
                && (0x1161..=0x1175).contains(&v)
                && (t == 0 || (0x11a8..=0x11c2).contains(&t))
            {
                let composed = 0xac00
                    + (cp - 0x1100) * 588
                    + (v - 0x1161) * 28
                    + if t == 0 { 0 } else { t - 0x11a7 };
                let id = map(font, composed)?;
                if id != 0 {
                    b.storage[i].code_point = composed;
                    b.storage[i].glyph_id = id;
                    b.storage[i].cluster = minimum(&b.storage[i..i + n]);
                    erase(b, i + 1, n - 1);
                    i += 1;
                    continue;
                }
            }
            let cluster = minimum(&b.storage[i..i + n]);
            for (offset, g) in b.storage[i..i + n].iter_mut().enumerate() {
                g.cluster = cluster;
                set_jamo(g, offset as u32 + 1);
            }
            i += n;
            continue;
        }
        if !(0xac00..=0xd7a3).contains(&cp) {
            i += 1;
            continue;
        }
        let syllable = cp - 0xac00;
        let ti = syllable % 28;
        if ti == 0 && i + 1 < b.count && (0x11a8..=0x11c2).contains(&b.storage[i + 1].code_point) {
            let combined = cp + b.storage[i + 1].code_point - 0x11a7;
            let id = map(font, combined)?;
            if id != 0 {
                b.storage[i].code_point = combined;
                b.storage[i].glyph_id = id;
                b.storage[i].cluster = minimum(&b.storage[i..i + 2]);
                erase(b, i + 1, 1);
                i += 1;
                continue;
            }
        }
        let noncombining = ti == 0
            && i + 1 < b.count
            && trailing(b.storage[i + 1].code_point)
            && !(0x11a8..=0x11c2).contains(&b.storage[i + 1].code_point);
        if b.storage[i].glyph_id != 0 && !noncombining {
            i += 1;
            continue;
        }
        let cps = [
            0x1100 + syllable / 588,
            0x1161 + (syllable % 588) / 28,
            0x11a7 + ti,
        ];
        let ids = [
            map(font, cps[0])?,
            map(font, cps[1])?,
            if ti != 0 { map(font, cps[2])? } else { 0 },
        ];
        if ids[0] == 0 || ids[1] == 0 || ti != 0 && ids[2] == 0 {
            i += 1;
            continue;
        }
        let n = if ti == 0 { 2 } else { 3 };
        b.storage.copy_within(i + 1..b.count, i + n);
        b.count += n - 1;
        let source = b.storage[i];
        for j in 0..n {
            b.storage[i + j] = source;
            b.storage[i + j].code_point = cps[j];
            b.storage[i + j].glyph_id = ids[j];
            set_jamo(&mut b.storage[i + j], j as u32 + 1);
        }
        if noncombining {
            set_jamo(&mut b.storage[i + n], 3);
        }
        i += n;
    }
    Ok(())
}
fn compose(a: u32, b: u32) -> u32 {
    if b == 0x5bc && (0x5d0..=0x5ea).contains(&a) {
        return [
            0xfb30, 0xfb31, 0xfb32, 0xfb33, 0xfb34, 0xfb35, 0xfb36, 0, 0xfb38, 0xfb39, 0xfb3a,
            0xfb3b, 0xfb3c, 0, 0xfb3e, 0, 0xfb40, 0xfb41, 0, 0xfb43, 0xfb44, 0, 0xfb46, 0xfb47,
            0xfb48, 0xfb49, 0xfb4a,
        ][(a - 0x5d0) as usize];
    }
    match (a, b) {
        (0x5d9, 0x5b4) => 0xfb1d,
        (0x5f2, 0x5b7) => 0xfb1f,
        (0x5d0, 0x5b7) => 0xfb2e,
        (0x5d0, 0x5b8) => 0xfb2f,
        (0x5d5, 0x5b9) => 0xfb4b,
        (0x5d1, 0x5bf) => 0xfb4c,
        (0x5db, 0x5bf) => 0xfb4d,
        (0x5e4, 0x5bf) => 0xfb4e,
        (0x5e9, 0x5c1) => 0xfb2a,
        (0x5e9, 0x5c2) => 0xfb2b,
        (0xfb49, 0x5c1) | (0xfb2a, 0x5bc) => 0xfb2c,
        (0xfb49, 0x5c2) | (0xfb2b, 0x5bc) => 0xfb2d,
        _ => 0,
    }
}
pub(super) fn hebrew(font: &SfntFace, b: &mut Buffer<'_>) -> Result<(), FontError> {
    let mut start = 0;
    while start < b.count {
        let mut end = start + 1;
        while end < b.count && b.storage[end].cluster == b.storage[start].cluster {
            end += 1;
        }
        let mut starter = start;
        while starter < end
            && !matches!(b.storage[starter].code_point,0x5d0..=0x5ea|0xfb1d..=0xfb4e|0x5f2)
        {
            starter += 1;
        }
        let mut i = starter + 1;
        while i < end {
            let cp = compose(b.storage[starter].code_point, b.storage[i].code_point);
            if cp == 0 {
                i += 1;
                continue;
            }
            let id = map(font, cp)?;
            if id == 0 {
                i += 1;
                continue;
            }
            b.storage[starter].code_point = cp;
            b.storage[starter].glyph_id = id;
            erase(b, i, 1);
            end -= 1;
        }
        start = end;
    }
    Ok(())
}
pub(super) fn thai_lao(
    font: &SfntFace,
    script: Tag,
    level: NativeClusterLevel,
    b: &mut Buffer<'_>,
) -> Result<(), FontError> {
    let am = match &script.bytes() {
        b"thai" => 0xe33,
        b"lao " => 0xeb3,
        _ => return Ok(()),
    };
    let nk = if am == 0xe33 { 0xe4d } else { 0xecd };
    let ids = [map(font, nk)?, map(font, am - 1)?];
    let mut i = 0;
    while i < b.count {
        if b.storage[i].code_point != am {
            i += 1;
            continue;
        }
        let cluster = b.storage[i].cluster;
        b.insert(
            i + 1,
            ShapingGlyph {
                glyph_id: ids[1],
                code_point: am - 1,
                cluster,
                ..Default::default()
            },
        )?;
        b.storage[i] = ShapingGlyph {
            glyph_id: ids[0],
            code_point: nk,
            cluster,
            ..Default::default()
        };
        let mut start = i;
        while start > 0
            && matches!(b.storage[start-1].code_point & !0x80,0xe34..=0xe37|0xe47..=0xe4e|0xe31|0xe3b)
        {
            start -= 1;
        }
        if start < i {
            b.storage[start..=i].rotate_right(1);
        }
        let merge = if level == NativeClusterLevel::MonotoneCharacters {
            start
        } else {
            start.saturating_sub(1)
        };
        let cluster = minimum(&b.storage[merge..i + 2]);
        for g in &mut b.storage[merge..i + 2] {
            g.cluster = cluster;
        }
        i += 2;
    }
    Ok(())
}
