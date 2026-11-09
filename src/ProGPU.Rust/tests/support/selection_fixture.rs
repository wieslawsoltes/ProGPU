#![allow(dead_code)]
use progpu::font::Tag;
pub fn words(values: &[u16]) -> Vec<u8> {
    values.iter().flat_map(|v| v.to_be_bytes()).collect()
}
pub fn put(b: &mut [u8], at: usize, v: u16) {
    b[at..at + 2].copy_from_slice(&v.to_be_bytes());
}
pub fn put32(b: &mut [u8], at: usize, v: u32) {
    b[at..at + 4].copy_from_slice(&v.to_be_bytes());
}
fn tagged(records: &[(Tag, Vec<u8>)], prefix: usize) -> Vec<u8> {
    let mut b = vec![0; prefix + 2 + records.len() * 6];
    put(&mut b, prefix, records.len() as u16);
    for (i, (tag, data)) in records.iter().enumerate() {
        let p = prefix + 2 + i * 6;
        put32(&mut b, p, tag.0);
        let offset = b.len() as u16;
        put(&mut b, p + 4, offset);
        b.extend(data);
    }
    b
}
pub fn lang(required: u16, features: &[u16]) -> Vec<u8> {
    let mut b = words(&[0xbeef, required, features.len() as u16]);
    b.extend(words(features));
    b
}
pub fn script(default: Option<Vec<u8>>, languages: &[(Tag, Vec<u8>)]) -> Vec<u8> {
    let mut b = tagged(languages, 2);
    if let Some(default) = default {
        let offset = b.len() as u16;
        put(&mut b, 0, offset);
        b.extend(default);
    }
    b
}
pub fn table(
    scripts: &[(Tag, Vec<u8>)],
    features: &[(Tag, Vec<u16>)],
    lookup_count: u16,
    variations: Option<Vec<u8>>,
) -> Vec<u8> {
    let mut b = words(&[1, u16::from(variations.is_some()), 0, 0, 0]);
    if variations.is_some() {
        b.extend([0; 4]);
    }
    let offset = b.len() as u16;
    put(&mut b, 4, offset);
    b.extend(tagged(scripts, 0));
    let features: Vec<_> = features
        .iter()
        .map(|(tag, indices)| {
            let mut f = words(&[0xbeef, indices.len() as u16]);
            f.extend(words(indices));
            (*tag, f)
        })
        .collect();
    let offset = b.len() as u16;
    put(&mut b, 6, offset);
    b.extend(tagged(&features, 0));
    let offset = b.len() as u16;
    put(&mut b, 8, offset);
    b.extend(words(&[lookup_count]));
    let start = b.len();
    b.resize(start + usize::from(lookup_count) * 2, 0);
    let relative = (2 + usize::from(lookup_count) * 2) as u16;
    for i in 0..usize::from(lookup_count) {
        put(&mut b, start + i * 2, relative);
    }
    b.extend(words(&[1, 0, 0]));
    if let Some(v) = variations {
        let offset = b.len() as u32;
        put32(&mut b, 10, offset);
        b.extend(v);
    }
    b
}
pub fn variation_substitution(entries: &[(u16, Vec<u16>)]) -> Vec<u8> {
    let mut b = words(&[1, 0, entries.len() as u16]);
    b.resize(6 + entries.len() * 6, 0);
    for (i, (index, lookups)) in entries.iter().enumerate() {
        let p = 6 + i * 6;
        put(&mut b, p, *index);
        let offset = b.len() as u32;
        put32(&mut b, p + 2, offset);
        b.extend(words(&[0, lookups.len() as u16]));
        b.extend(words(lookups));
    }
    b
}
pub fn conditions(items: &[[i16; 3]]) -> Vec<u8> {
    let mut b = words(&[items.len() as u16]);
    b.resize(2 + items.len() * 4, 0);
    for (i, &[axis, min, max]) in items.iter().enumerate() {
        let offset = b.len() as u32;
        put32(&mut b, 2 + i * 4, offset);
        b.extend(words(&[1, axis as u16, min as u16, max as u16]));
    }
    b
}
pub type VariationRecord = (Option<Vec<u8>>, Option<Vec<u8>>);
pub fn variations(records: &[VariationRecord]) -> Vec<u8> {
    let mut b = words(&[1, 0]);
    b.extend((records.len() as u32).to_be_bytes());
    b.resize(8 + records.len() * 8, 0);
    for (i, (conditions, substitutions)) in records.iter().enumerate() {
        for (j, part) in [conditions, substitutions].iter().enumerate() {
            if let Some(data) = part {
                let at = b.len() as u32;
                put32(&mut b, 8 + i * 8 + j * 4, at);
                b.extend(data);
            }
        }
    }
    b
}
pub fn ordinary(variation: bool) -> Vec<u8> {
    let default = lang(0, &[3, 1, 2, 1, 0]);
    let turkish = lang(2, &[1, 3]);
    let script = script(Some(default), &[(Tag::new(*b"TRK "), turkish)]);
    let features = [
        (Tag::new(*b"rlig"), vec![3, 1, 3]),
        (Tag::new(*b"liga"), vec![6, 2, 6]),
        (Tag::new(*b"kern"), vec![4, 2]),
        (Tag::new(*b"liga"), vec![7, 6, 1]),
    ];
    let v = variation.then(|| {
        variations(&[
            (
                Some(conditions(&[[0, 0, 8192]])),
                Some(variation_substitution(&[(1, vec![9, 8]), (0, vec![10])])),
            ),
            (None, Some(variation_substitution(&[(1, vec![11, 5])]))),
        ])
    });
    table(
        &[
            (Tag::new(*b"DFLT"), script.clone()),
            (Tag::new(*b"latn"), script),
        ],
        &features,
        12,
        v,
    )
}
pub fn seeds() -> Vec<Vec<u8>> {
    let mut seeds = vec![ordinary(false), ordinary(true)];
    for required in [0, 1, 65535] {
        for indices in [vec![], vec![0, 1], vec![1, 0, 1, 0]] {
            let s = script(Some(lang(required, &indices)), &[]);
            for tag in [*b"DFLT", *b"latn", *b"dflt"] {
                seeds.push(table(
                    &[(Tag::new(tag), s.clone())],
                    &[
                        (Tag::new(*b"liga"), vec![3, 1]),
                        (Tag::new(*b"kern"), vec![2, 3]),
                    ],
                    4,
                    None,
                ));
            }
        }
    }
    seeds.push(table(&[], &[], 0, None));
    seeds.push(table(
        &[(Tag::new(*b"DFLT"), script(None, &[]))],
        &[],
        0,
        None,
    ));
    // First duplicate script and substitution entries remain authoritative.
    let a = script(Some(lang(65535, &[0])), &[]);
    let b = script(Some(lang(65535, &[1])), &[]);
    seeds.push(table(
        &[(Tag::new(*b"DFLT"), a), (Tag::new(*b"DFLT"), b)],
        &[(Tag::new(*b"liga"), vec![0]), (Tag::new(*b"liga"), vec![1])],
        2,
        None,
    ));
    let s = script(Some(lang(65535, &[0])), &[]);
    let f = [(Tag::new(*b"liga"), vec![0])];
    for minimum in [-32768, -16384, -1, 0, 1, 8192, 16384, 32767] {
        for axis in [0, 1, 2, 32767] {
            let v = variations(&[
                (
                    Some(conditions(&[[axis, minimum, minimum]])),
                    Some(variation_substitution(&[(0, vec![1]), (0, vec![2])])),
                ),
                (None, Some(variation_substitution(&[(0, vec![3])]))),
            ]);
            seeds.push(table(&[(Tag::new(*b"DFLT"), s.clone())], &f, 4, Some(v)));
        }
    }
    seeds
}
