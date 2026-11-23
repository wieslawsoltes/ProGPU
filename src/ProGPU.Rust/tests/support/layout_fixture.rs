// Authored binary controls for the original OpenType data boundaries.
pub fn words(values: &[u16]) -> Vec<u8> {
    values.iter().flat_map(|v| v.to_be_bytes()).collect()
}
pub fn coverage1(glyphs: &[u16]) -> Vec<u8> {
    let mut b = words(&[1, glyphs.len() as u16]);
    b.extend(words(glyphs));
    b
}
pub fn ranges(format: u16, records: &[[u16; 3]]) -> Vec<u8> {
    let mut b = words(&[format, records.len() as u16]);
    for r in records {
        b.extend(words(r));
    }
    b
}
pub fn class1(start: u16, classes: &[u16]) -> Vec<u8> {
    let mut b = words(&[1, start, classes.len() as u16]);
    b.extend(words(classes));
    b
}
pub fn layout(flags: u16) -> Vec<u8> {
    // Empty script/feature arrays, two lookups, two raw subtables each.
    let mut b = words(&[1, 1, 14, 16, 18, 0, 0, 0, 0, 2, 6, 18]);
    for kind in [1, 65535] {
        b.extend(words(&[kind, flags, 2, 12, 14, 42]));
    }
    // Lookup 0 can point into lookup 1; raw subtable type is deliberately opaque.
    b.extend(words(&[0xbeef, 0x1234]));
    b
}
pub fn gdef(minor: u16) -> Vec<u8> {
    let mut b = words(&[1, minor, 18, 0xffff, 0xffff, 36, 52, 0, 0]);
    b.extend(class1(10, &[0, 1, 2, 3, 4, 5])); // 18..36
    b.extend(ranges(2, &[[10, 14, 65535], [65535, 65535, 7]])); // 36..52
    b.extend(words(&[1, 2, 0, 12, 0, 24])); // two uint32 relative offsets
    b.extend(coverage1(&[10, 10, 10, 65535])); // 64..76
    b.extend(ranges(2, &[[0, 65535, 0]]));
    b
}
pub fn seeds() -> Vec<(u32, Vec<u8>)> {
    let mut out = vec![];
    for count in [0, 1, 2, 3, 4, 5, 16, 255] {
        for duplicate in [false, true] {
            let glyphs: Vec<_> = (0..count)
                .map(|i| if duplicate { i / 2 } else { i * 257 })
                .collect();
            out.push((0, coverage1(&glyphs)));
            out.push((1, class1(0, &glyphs)));
        }
    }
    for r in [
        vec![],
        vec![[0, 65535, 0]],
        vec![[0, 0, 0], [1, 1, 1], [65535, 65535, 2]],
        vec![[10, 19, 0], [100, 199, 10]],
    ] {
        out.push((0, ranges(2, &r)));
        out.push((1, ranges(2, &r)));
    }
    out.push((1, class1(65534, &[4, 65535])));
    for flags in [0, 0x10, 0xffff] {
        out.push((2, layout(flags)));
    }
    for minor in [0, 2, 3] {
        out.push((3, gdef(minor)));
    }
    out
}
