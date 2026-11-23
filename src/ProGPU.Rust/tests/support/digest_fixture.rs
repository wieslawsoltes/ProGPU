#![allow(dead_code)]
#[path = "layout_fixture.rs"]
mod layout;
pub use layout::{coverage1, ranges, words};
pub fn put(b: &mut [u8], at: usize, n: u16) {
    b[at..at + 2].copy_from_slice(&n.to_be_bytes());
}
pub fn table(kind: u16, flags: u16, subtables: &[Vec<u8>]) -> Vec<u8> {
    let mut b = words(&[
        1,
        0,
        10,
        12,
        14,
        0,
        0,
        1,
        4,
        kind,
        flags,
        subtables.len() as u16,
    ]);
    b.resize(24 + subtables.len() * 2, 0);
    if flags & 16 != 0 {
        b.extend(words(&[42]));
    }
    for (i, s) in subtables.iter().enumerate() {
        let relative = (b.len() - 18) as u16;
        put(&mut b, 24 + i * 2, relative);
        b.extend(s);
    }
    b
}
pub fn simple(coverage: &[u8]) -> Vec<u8> {
    let mut b = words(&[1, 6, 42]);
    b.extend(coverage);
    b
}
pub fn extension(kind: u16, subtable: &[u8]) -> Vec<u8> {
    let mut b = words(&[1, kind, 0, 8]);
    b.extend(subtable);
    b
}
pub fn context(chain: bool, counts: [u16; 3], coverages: &[Vec<u8>]) -> Vec<u8> {
    assert_eq!(
        counts.iter().map(|&x| usize::from(x)).sum::<usize>(),
        coverages.len()
    );
    let mut b = words(&[3]);
    let mut offsets = vec![];
    if chain {
        for count in counts {
            b.extend(words(&[count]));
            for _ in 0..count {
                offsets.push(b.len());
                b.extend([0, 0]);
            }
        }
        b.extend(words(&[2]));
    } else {
        assert_eq!(counts[0], 0);
        assert_eq!(counts[2], 0);
        b.extend(words(&[counts[1], 2]));
        for _ in 0..counts[1] {
            offsets.push(b.len());
            b.extend([0, 0]);
        }
    }
    // Record values are intentionally opaque to accelerator preparation.
    b.extend(words(&[65535, 65535, 0, 0]));
    for (at, coverage) in offsets.into_iter().zip(coverages) {
        let relative = b.len() as u16;
        put(&mut b, at, relative);
        b.extend(coverage);
    }
    b
}
pub fn seeds() -> Vec<(u16, Vec<u8>)> {
    let covs = [
        coverage1(&[]),
        coverage1(&[10, 10, 11, 64, 65535]),
        ranges(2, &[[0, 1, 0], [64, 66, 2]]),
        ranges(2, &[[0, 65535, 0]]),
    ];
    let mut out = vec![];
    for extension_kind in [7, 9, 0, 65535] {
        for flags in [0, 1, 16, 65535] {
            for c in &covs {
                let plain = simple(c);
                out.push((
                    extension_kind,
                    table(1, flags, std::slice::from_ref(&plain)),
                ));
                out.push((
                    extension_kind,
                    table(extension_kind, flags, &[extension(1, &plain)]),
                ));
            }
            out.push((extension_kind, table(1, flags, &[])));
            for chain in [false, true] {
                let kind = if extension_kind == 7 { 5 } else { 7 } + u16::from(chain);
                let counts = if chain { [1, 2, 1] } else { [0, 4, 0] };
                let ctx = context(chain, counts, &covs);
                for parts in [vec![ctx.clone()], vec![ctx.clone(), ctx.clone()]] {
                    out.push((extension_kind, table(kind, flags, &parts)));
                }
                out.push((
                    extension_kind,
                    table(extension_kind, flags, &[extension(kind, &ctx)]),
                ));
            }
        }
    }
    out
}
