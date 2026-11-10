//! Original native presentation replacement and ordered required-ligature fallback.
use super::*;
#[path = "../../script_actions/arabic_fallback_data.rs"]
mod data;
fn map(font: &SfntFace, cp: u32) -> Result<u32, FontError> {
    font.glyph_index(cp)
        .map(u32::from)
        .ok_or(FontError::InvalidFace)
}
fn mark(g: ShapingGlyph, gdef: Option<Gdef<'_>>) -> bool {
    if let Some(d) = gdef
        && let Ok(id) = u16::try_from(g.glyph_id)
    {
        let c = d.glyph_class(id);
        if c != GlyphClass::Unclassified {
            return c == GlyphClass::Mark;
        }
    }
    unicode_mark(g.code_point)
}
fn next(b: &Buffer<'_>, mut i: usize, ignore: bool, gdef: Option<Gdef<'_>>) -> usize {
    while i < b.count {
        let g = b.storage[i];
        let boundary = matches!(g.code_point,0x34f|0x200c|0x200d|0x180b..=0x180e|0xe0000..=0xe007f);
        if input::default_ignorable(g.code_point) && !boundary || ignore && mark(g, gdef) {
            i += 1;
        } else {
            break;
        }
    }
    i
}
fn ligatures<const N: usize>(
    font: &SfntFace,
    b: &mut Buffer<'_>,
    rows: &[([u16; N], u16)],
    ignore: bool,
    gdef: Option<Gdef<'_>>,
    track: bool,
) -> Result<(), FontError> {
    let mut position = 0;
    while position < b.count {
        let first = b.storage[position].glyph_id;
        for &(row, output) in rows {
            let expected = map(font, u32::from(row[0]))?;
            if expected == 0 || first != expected {
                continue;
            }
            let mut components = [0usize; 3];
            components[0] = position;
            let mut candidate = position;
            let mut matched = true;
            for c in 1..N {
                let expected = map(font, u32::from(row[c]))?;
                if expected == 0 {
                    matched = false;
                    break;
                }
                candidate = next(b, candidate + 1, ignore, gdef);
                if candidate >= b.count || b.storage[candidate].glyph_id != expected {
                    matched = false;
                    break;
                }
                components[c] = candidate;
            }
            let id = map(font, u32::from(output))?;
            if !matched || id == 0 {
                continue;
            }
            let end = components[N - 1] + 1;
            let cluster = b.storage[position..end]
                .iter()
                .map(|g| g.cluster)
                .min()
                .unwrap();
            for g in &mut b.storage[position..end] {
                g.cluster = cluster;
            }
            b.storage[position].glyph_id = id;
            if track {
                b.storage[position].flags =
                    (b.storage[position].flags & !0x7f8) | ((N.min(255) as u32) << 3);
                for c in 0..N - 1 {
                    for g in &mut b.storage[components[c] + 1..components[c + 1]] {
                        g.flags = (g.flags & !0x7f800) | (((c.min(254) + 1) as u32) << 11);
                    }
                }
            }
            let mut write = position + 1;
            let mut component = 1;
            for read in position + 1..b.count {
                if component < N && read == components[component] {
                    component += 1;
                    continue;
                }
                b.storage[write] = b.storage[read];
                write += 1;
            }
            b.count = write;
            break;
        }
        position += 1;
    }
    Ok(())
}
pub(super) fn apply(
    font: &SfntFace,
    b: &mut Buffer<'_>,
    gdef: Option<Gdef<'_>>,
    plan: &NativeFeaturePlan,
    track: bool,
) -> Result<(), FontError> {
    for g in b.glyphs_mut() {
        let (tag, form) = match joining::action(*g) {
            6 => (Tag::new(*b"init"), 0),
            4 => (Tag::new(*b"medi"), 1),
            1 => (Tag::new(*b"fina"), 2),
            0 => (Tag::new(*b"isol"), 3),
            _ => continue,
        };
        if !plan.run_enabled(tag) || !(0x621..=0x6d3).contains(&g.code_point) {
            continue;
        }
        let index = data::FORMS.partition_point(|&(cp, _)| cp < g.code_point);
        let presentation = data::FORMS
            .get(index)
            .filter(|&&(cp, _)| cp == g.code_point)
            .map_or(0, |(_, forms)| forms[form]);
        if presentation == 0 {
            continue;
        }
        let original = map(font, g.code_point)?;
        let replacement = map(font, u32::from(presentation))?;
        if g.glyph_id == original && replacement != 0 && replacement != original {
            g.glyph_id = replacement;
        }
    }
    if plan.run_enabled(Tag::new(*b"rlig")) {
        ligatures(font, b, data::TRIPLES, true, gdef, track)?;
        ligatures(font, b, data::PAIRS, true, gdef, track)?;
        ligatures(font, b, data::MARKS, false, gdef, track)?;
    }
    Ok(())
}
