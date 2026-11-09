//! Original native Indic preparation, font substitution probes, stable initial
//! ordering and final reph/prebase movement. Glyph records stay native throughout.
use super::*;
const RPHF: Tag = Tag::new(*b"rphf");
const PREF: Tag = Tag::new(*b"pref");
const BLWF: Tag = Tag::new(*b"blwf");
const PSTF: Tag = Tag::new(*b"pstf");
const VATU: Tag = Tag::new(*b"vatu");
type Probe<'a> = dyn FnMut(Tag, &[u16]) -> bool + 'a;
fn joiner(g: ShapingGlyph) -> bool {
    !ligated(g) && matches!(category(g), 5 | 6)
}
fn consonant(g: ShapingGlyph) -> bool {
    !ligated(g) && matches!(category(g), 1 | 18 | 15 | 16 | 2 | 10 | 11)
}
fn halant(g: ShapingGlyph) -> bool {
    !ligated(g) && category(g) == 4
}
fn has(g: ShapingGlyph, mask: u8) -> bool {
    feature(g) & mask != 0
}
fn legacy(script: Tag) -> Tag {
    const PAIRS: [(Tag, Tag); 9] = [
        (Tag::new(*b"dev2"), Tag::new(*b"deva")),
        (Tag::new(*b"bng2"), Tag::new(*b"beng")),
        (Tag::new(*b"gur2"), Tag::new(*b"guru")),
        (Tag::new(*b"gjr2"), Tag::new(*b"gujr")),
        (Tag::new(*b"ory2"), Tag::new(*b"orya")),
        (Tag::new(*b"tml2"), Tag::new(*b"taml")),
        (Tag::new(*b"tel2"), Tag::new(*b"telu")),
        (Tag::new(*b"knd2"), Tag::new(*b"knda")),
        (Tag::new(*b"mlm2"), Tag::new(*b"mlym")),
    ];
    PAIRS.iter().find(|p| p.0 == script).map_or(script, |p| p.1)
}
fn virama(script: Tag) -> u32 {
    match &legacy(script).bytes() {
        b"deva" => 0x94d,
        b"beng" => 0x9cd,
        b"guru" => 0xa4d,
        b"gujr" => 0xacd,
        b"orya" => 0xb4d,
        b"taml" => 0xbcd,
        b"telu" => 0xc4d,
        b"knda" => 0xccd,
        b"mlym" => 0xd4d,
        _ => 0,
    }
}
fn reph_position(script: Tag) -> u8 {
    match &legacy(script).bytes() {
        b"beng" => 9,
        b"guru" => 7,
        b"orya" | b"mlym" => 5,
        b"taml" | b"telu" | b"knda" => 12,
        _ => 10,
    }
}
fn reph_mode(script: Tag) -> u8 {
    match &legacy(script).bytes() {
        b"telu" => 1,
        b"mlym" => 2,
        _ => 0,
    }
}
fn below_pre_post(script: Tag) -> bool {
    !matches!(&legacy(script).bytes(), b"telu" | b"knda")
}
fn continuation(cp: u32) -> bool {
    matches!(
        unicode::general_category(cp),
        GeneralCategory::UppercaseLetter
            | GeneralCategory::LowercaseLetter
            | GeneralCategory::TitlecaseLetter
            | GeneralCategory::ModifierLetter
            | GeneralCategory::OtherLetter
            | GeneralCategory::NonspacingMark
            | GeneralCategory::SpacingCombiningMark
            | GeneralCategory::EnclosingMark
            | GeneralCategory::Format
            | GeneralCategory::Surrogate
            | GeneralCategory::PrivateUse
            | GeneralCategory::OtherNotAssigned
    )
}
fn move_glyph(glyphs: &mut [ShapingGlyph], source: usize, destination: usize) {
    if source == destination || source >= glyphs.len() || destination >= glyphs.len() {
        return;
    }
    if source < destination {
        glyphs[source..=destination].rotate_left(1);
    } else {
        glyphs[destination..=source].rotate_right(1);
    }
}
fn attach_marks(g: &mut [ShapingGlyph], start: usize, end: usize, base: usize) {
    let mut last_position = 0;
    for i in start..end {
        let value = category(g[i]);
        if joiner(g[i]) || matches!(value, 3 | 12 | 16 | 4) {
            set_position(&mut g[i], last_position);
            if value == 4 && position(g[i]) == 2 {
                for prior in (start..i).rev() {
                    let p = position(g[prior]);
                    if p != 2 {
                        set_position(&mut g[i], p);
                        break;
                    }
                }
            }
        } else if position(g[i]) != 13 {
            if value == 13 && i > start && category(g[i - 1]) == 8 {
                let p = position(g[i]);
                set_position(&mut g[i - 1], p);
            }
            last_position = position(g[i]);
        }
    }
    let mut last = base.min(end - 1);
    for i in last + 1..end {
        if consonant(g[i]) {
            for mark in last + 1..i {
                if position(g[mark]) < 13 {
                    let p = position(g[i]);
                    set_position(&mut g[mark], p);
                }
            }
            last = i;
        } else if matches!(category(g[i]), 7 | 13) {
            last = i;
        }
    }
}
fn sort(g: &mut [ShapingGlyph], order: &mut [u32], start: usize, end: usize) {
    for i in start + 1..end {
        let value = g[i];
        let value_order = order[i];
        let mut insertion = i;
        while insertion > start && position(g[insertion - 1]) > position(value) {
            g[insertion] = g[insertion - 1];
            order[insertion] = order[insertion - 1];
            insertion -= 1;
        }
        g[insertion] = value;
        order[insertion] = value_order;
    }
}
fn reverse_matras(g: &mut [ShapingGlyph], order: &mut [u32], start: usize, end: usize) {
    let (mut first, mut last) = (end, end);
    for i in start..end {
        if position(g[i]) == 4 {
            break;
        }
        if position(g[i]) == 2 {
            if first == end {
                first = i;
            }
            last = i;
        }
    }
    if first >= last {
        return;
    }
    g[first..=last].reverse();
    order[first..=last].reverse();
    let mut group = first;
    for i in first..=last {
        if matches!(category(g[i]), 7 | 13) {
            g[group..=i].reverse();
            order[group..=i].reverse();
            group = i + 1;
        }
    }
}
fn merge_sort(
    g: &mut [ShapingGlyph],
    order: &mut [u32],
    start: usize,
    end: usize,
    base: usize,
    old: bool,
) {
    if base >= end {
        return;
    }
    if old || end - start > 127 {
        merge_cluster(g, base, end);
        return;
    }
    for i in base..end {
        if order[i] == u32::MAX {
            continue;
        }
        let (mut minimum, mut maximum) = (i, i);
        let mut cursor = start.saturating_add(order[i] as usize);
        while cursor != i && cursor >= start && cursor < end {
            minimum = minimum.min(cursor);
            maximum = maximum.max(cursor);
            let next = start.saturating_add(order[cursor] as usize);
            order[cursor] = u32::MAX;
            cursor = next;
        }
        merge_cluster(g, base.max(minimum), maximum + 1);
    }
}
fn update_consonants(font: &SfntFace, script: Tag, g: &mut [ShapingGlyph], probe: &mut Probe<'_>) {
    let cp = virama(script);
    if cp == 0 {
        return;
    }
    let id = font.glyph_index(cp).unwrap_or(0);
    if id == 0 {
        return;
    }
    for glyph in g {
        if position(*glyph) != 4 || glyph.glyph_id > 65535 {
            continue;
        }
        let first = [id, glyph.glyph_id as u16];
        let second = [glyph.glyph_id as u16, id];
        if probe(BLWF, &first)
            || probe(BLWF, &second)
            || probe(VATU, &first)
            || probe(VATU, &second)
        {
            set_position(glyph, 8);
        } else if probe(PSTF, &first)
            || probe(PSTF, &second)
            || probe(PREF, &first)
            || probe(PREF, &second)
        {
            set_position(glyph, 11);
        }
    }
}
fn initial_syllable(
    g: &mut [ShapingGlyph],
    order: &mut [u32],
    start: usize,
    end: usize,
    script: Tag,
    old: bool,
    probe: &mut Probe<'_>,
) {
    let normalized = legacy(script);
    if normalized == Tag::new(*b"knda")
        && end - start >= 3
        && category(g[start]) == 15
        && category(g[start + 1]) == 4
        && category(g[start + 2]) == 6
    {
        merge_cluster(g, start + 1, start + 3);
        g.swap(start + 1, start + 2);
    }
    let (mut limit, mut base, mut reph) = (start, end, false);
    let mode = reph_mode(normalized);
    if end - start >= 3
        && category(g[start]) == 15
        && category(g[start + 1]) == 4
        && (mode == 0 && !joiner(g[start + 2]) || mode == 1 && category(g[start + 2]) == 6)
    {
        let ids = [
            g[start].glyph_id as u16,
            g[start + 1].glyph_id as u16,
            g[start + 2].glyph_id as u16,
        ];
        if probe(RPHF, &ids[..2]) || mode == 1 && probe(RPHF, &ids) {
            limit += 2;
            while limit < end && joiner(g[limit]) {
                limit += 1;
            }
            base = start;
            reph = true;
        }
    } else if mode == 2 && category(g[start]) == 14 {
        limit += 1;
        while limit < end && joiner(g[limit]) {
            limit += 1;
        }
        base = start;
        reph = true;
    }
    let mut seen_below = false;
    for i in (limit..end).rev() {
        if consonant(g[i]) {
            if position(g[i]) != 8 && (position(g[i]) != 11 || seen_below) {
                base = i;
                break;
            }
            if position(g[i]) == 8 {
                seen_below = true;
            }
            base = i;
        } else if i > start && category(g[i]) == 6 && category(g[i - 1]) == 4 {
            break;
        }
    }
    if reph && base == start && limit - base <= 2 {
        reph = false;
    }
    for glyph in &mut g[start..base] {
        let p = 3.min(position(*glyph));
        set_position(glyph, p);
    }
    if base < end {
        set_position(&mut g[base], 4);
    }
    if reph {
        set_position(&mut g[start], 1);
    }
    if old && base < end {
        let disallow = normalized == Tag::new(*b"knda");
        for i in base + 1..end {
            if category(g[i]) != 4 {
                continue;
            }
            let mut destination = end - 1;
            while destination > i
                && !consonant(g[destination])
                && !(disallow && category(g[destination]) == 4)
            {
                destination -= 1;
            }
            if category(g[destination]) != 4 && destination > i {
                g[i..=destination].rotate_left(1);
            }
            break;
        }
    }
    attach_marks(g, start, end, base);
    for i in start..end {
        order[i] = (i - start) as u32;
    }
    sort(g, order, start, end);
    reverse_matras(g, order, start, end);
    base = (start..end).find(|&i| position(g[i]) == 4).unwrap_or(end);
    merge_sort(g, order, start, end, base, old);
    for glyph in &mut g[start..end] {
        if position(*glyph) != 1 {
            break;
        }
        add_feature(glyph, 1);
    }
    let pre_mask = 16
        | if !old && below_pre_post(normalized) {
            4
        } else {
            0
        };
    for glyph in &mut g[start..base] {
        add_feature(glyph, pre_mask);
    }
    if base < end {
        for glyph in &mut g[base + 1..end] {
            add_feature(glyph, 4 | 8 | 32);
        }
    }
    if base + 2 < end {
        for i in base + 1..end - 1 {
            if g[i].glyph_id > 65535 || g[i + 1].glyph_id > 65535 {
                continue;
            }
            if probe(PREF, &[g[i].glyph_id as u16, g[i + 1].glyph_id as u16]) {
                add_feature(&mut g[i], 2);
                add_feature(&mut g[i + 1], 2);
                break;
            }
        }
    }
    for i in start + 1..end {
        if category(g[i]) == 5 {
            for prior in (start..i).rev() {
                g[prior].flags &= !(16 << 21);
                if consonant(g[prior]) {
                    break;
                }
            }
        }
    }
}
pub(super) fn prepare(
    b: &mut Buffer<'_>,
    categories: &mut [u8],
    syllables: &mut [u8],
) -> Result<(), FontError> {
    if categories.len() < b.count || syllables.len() < b.count {
        return Err(FontError::Capacity);
    }
    for i in 0..b.count {
        let p = ManagedIndicProperties::for_raw_code_point(b.storage[i].code_point);
        categories[i] = p.category();
        set_category(&mut b.storage[i], p.category());
        set_position(&mut b.storage[i], p.position());
    }
    syllables::assign(
        NativeComplexKind::Indic,
        &categories[..b.count],
        &[],
        syllables,
    )?;
    for i in 0..b.count {
        set_syllable(&mut b.storage[i], syllables[i]);
    }
    mark_syllables_unsafe(b.glyphs_mut());
    Ok(())
}
pub(super) fn initial(
    font: &SfntFace,
    script: Tag,
    layout_script: Tag,
    flags: NativeBufferFlags,
    b: &mut Buffer<'_>,
    order: &mut [u32],
    probe: &mut Probe<'_>,
) -> Result<(), FontError> {
    update_consonants(font, script, b.glyphs_mut(), probe);
    let dotted = if flags.contains(NativeBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE) {
        0
    } else {
        font.glyph_index(0x25cc).unwrap_or(0)
    };
    let additions = if dotted == 0 {
        0
    } else {
        broken_count(b.glyphs(), 4)
    };
    b.reserve(additions)?;
    if order.len() < b.count + additions {
        return Err(FontError::Capacity);
    }
    if additions != 0 {
        let mut previous = 0;
        let mut i = 0;
        while i < b.count {
            let current = syllable(b.storage[i]);
            if current != previous && current & 15 == 4 {
                previous = current;
                while i < b.count
                    && syllable(b.storage[i]) == previous
                    && category(b.storage[i]) == 14
                {
                    i += 1;
                }
                let cluster = if i < b.count {
                    b.storage[i].cluster
                } else {
                    b.storage[i - 1].cluster
                };
                let mut g = ShapingGlyph {
                    glyph_id: u32::from(dotted),
                    code_point: 0x25cc,
                    cluster,
                    ..Default::default()
                };
                set_category(&mut g, 11);
                set_position(&mut g, 14);
                set_syllable(&mut g, previous);
                b.insert(i, g)?;
            } else {
                previous = current;
            }
            i += 1;
        }
    }
    let g = b.glyphs_mut();
    let mut start = 0;
    while start < g.len() {
        let end = end_of_syllable(g, start);
        if matches!(syllable(g[start]) & 15, 0 | 1 | 2 | 4) {
            initial_syllable(
                g,
                order,
                start,
                end,
                script,
                layout_script.bytes()[3] != b'2',
                probe,
            );
        }
        start = end;
    }
    Ok(())
}
fn reph_destination(
    g: &[ShapingGlyph],
    start: usize,
    end: usize,
    base: usize,
    script: Tag,
) -> usize {
    let desired = reph_position(script);
    if desired != 12 {
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
    if desired == 5 {
        let mut destination = base;
        while destination + 1 < end && position(g[destination + 1]) <= 5 {
            destination += 1;
        }
        if destination < end {
            return destination;
        }
    }
    if desired == 9 {
        let mut destination = base;
        while destination + 1 < end && !matches!(position(g[destination + 1]), 11 | 12 | 13) {
            destination += 1;
        }
        if destination < end {
            return destination;
        }
    }
    let mut destination = end - 1;
    while destination > start && position(g[destination]) == 13 {
        destination -= 1;
    }
    if halant(g[destination]) {
        for i in base + 1..destination {
            if matches!(category(g[i]), 7 | 13) {
                destination -= 1;
                break;
            }
        }
    }
    destination
}
pub(super) fn final_reorder(font: &SfntFace, script: Tag, g: &mut [ShapingGlyph]) {
    let script = legacy(script);
    let cp = virama(script);
    let virama = if cp == 0 {
        0
    } else {
        font.glyph_index(cp).unwrap_or(0)
    };
    let special = matches!(&script.bytes(), b"mlym" | b"taml");
    let mut start = 0;
    while start < g.len() {
        let end = end_of_syllable(g, start);
        let mut reordered = false;
        if virama != 0 {
            for glyph in &mut g[start..end] {
                if glyph.glyph_id == u32::from(virama) && ligated(*glyph) && multiplied(*glyph) {
                    set_category(glyph, 4);
                    glyph.flags &= !(LIGATED | MULTIPLIED);
                }
            }
        }
        let mut prebase = g[start..end].iter().any(|&g| has(g, 2));
        let mut base = start;
        while base < end && position(g[base]) < 4 {
            base += 1;
        }
        if prebase && base + 1 < end {
            for i in base + 1..end {
                if !has(g[i], 2) {
                    continue;
                }
                if !(substituted(g[i]) && ligated(g[i]) && !multiplied(g[i])) {
                    base = i;
                    while base < end && halant(g[base]) {
                        base += 1;
                    }
                    if base < end {
                        set_position(&mut g[base], 4);
                    }
                    prebase = false;
                }
                break;
            }
        }
        if script == Tag::new(*b"mlym") && base < end {
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
                if i < end && consonant(g[i]) && position(g[i]) == 8 {
                    base = i;
                    set_position(&mut g[base], 4);
                }
                i += 1;
            }
        }
        if base < end && base > start && position(g[base]) > 4 {
            base -= 1;
        }
        if base == end && end > start && category(g[end - 1]) == 6 {
            base -= 1;
        }
        while base > start
            && base < end
            && (!ligated(g[base]) && category(g[base]) == 3 || halant(g[base]))
        {
            base -= 1;
        }
        if start + 1 < end && start < base {
            let mut destination = if base == end { base - 2 } else { base - 1 };
            if !special {
                loop {
                    while destination > start && !matches!(category(g[destination]), 7 | 13 | 4) {
                        destination -= 1;
                    }
                    if !halant(g[destination]) || position(g[destination]) == 2 {
                        destination = start;
                        break;
                    }
                    if destination + 1 < end
                        && category(g[destination + 1]) == 6
                        && destination > start
                    {
                        destination -= 1;
                        continue;
                    }
                    break;
                }
            }
            if destination > start && position(g[destination]) != 2 {
                for i in (start + 1..=destination).rev() {
                    if position(g[i - 1]) != 2 {
                        continue;
                    }
                    move_glyph(g, i - 1, destination);
                    merge_cluster(g, destination, end.min(base + 1));
                    reordered = true;
                    destination -= 1;
                }
            } else {
                for i in start..base {
                    if position(g[i]) == 2 {
                        merge_cluster(g, i, end.min(base + 1));
                        break;
                    }
                }
            }
        }
        if start + 1 < end
            && position(g[start]) == 1
            && ((category(g[start]) == 14) != (ligated(g[start]) && !multiplied(g[start])))
        {
            let destination = reph_destination(g, start, end, base, script);
            merge_cluster(g, start, destination + 1);
            move_glyph(g, start, destination);
            reordered = true;
            if start < base && base <= destination {
                base -= 1;
            }
        }
        if prebase && base + 1 < end {
            for i in base + 1..end {
                if !has(g[i], 2) {
                    continue;
                }
                if ligated(g[i]) && !multiplied(g[i]) {
                    let mut destination = base;
                    if !special {
                        while destination > start
                            && !matches!(category(g[destination - 1]), 7 | 13 | 4)
                        {
                            destination -= 1;
                        }
                    }
                    if destination > start
                        && halant(g[destination - 1])
                        && destination < end
                        && joiner(g[destination])
                    {
                        destination += 1;
                    }
                    merge_cluster(g, destination, i + 1);
                    move_glyph(g, i, destination);
                    reordered = true;
                    if destination <= base && base < i {
                        base += 1;
                    }
                }
                break;
            }
        }
        if reordered || position(g[start]) == 2 {
            merge_cluster(g, start, end);
        }
        if position(g[start]) == 2 {
            if start == 0 || !continuation(g[start - 1].code_point) {
                add_feature(&mut g[start], 64);
            } else {
                super::mark_dependency(g, start - 1, start + 1);
            }
        }
        start = end;
    }
}
