use super::{Case, emit, fixture};
use progpu::font::{
    FontError, OutlinePoint, SfntFace,
    gvar::{apply_simple, component_offsets, horizontal_phantom_deltas},
};
fn deltas(values: &[i16]) -> Vec<u8> {
    let mut data = Vec::new();
    for chunk in values.chunks(64) {
        data.push(0x40 | (chunk.len() - 1) as u8);
        for value in chunk {
            data.extend(value.to_be_bytes());
        }
    }
    data
}
fn numbers(values: &[u16]) -> Vec<u8> {
    if values.is_empty() {
        return vec![128, 0];
    }
    let mut data = Vec::new();
    let count = values.len() as u16;
    if count < 128 {
        data.push(count as u8);
    } else {
        data.extend((count | 0x8000).to_be_bytes());
    }
    let mut previous = 0;
    for chunk in values.chunks(128) {
        data.push(0x80 | (chunk.len() - 1) as u8);
        for &value in chunk {
            data.extend((value - previous).to_be_bytes());
            previous = value;
        }
    }
    data
}
fn payload(points: Option<&[u16]>, x: &[i16], y: &[i16]) -> Vec<u8> {
    let mut data = points.map(numbers).unwrap_or_default();
    data.extend(deltas(x));
    data.extend(deltas(y));
    data
}
fn font(shared: Option<&[u8]>, tuples: &[(u16, i16, Vec<u8>)]) -> Vec<u8> {
    let mut glyph = Vec::new();
    glyph.extend(((tuples.len() as u16) | if shared.is_some() { 0x8000 } else { 0 }).to_be_bytes());
    glyph.extend([0; 2]);
    for (flags, peak, data) in tuples {
        glyph.extend((data.len() as u16).to_be_bytes());
        glyph.extend(flags.to_be_bytes());
        if flags & 0x8000 != 0 {
            glyph.extend(peak.to_be_bytes());
        }
        if flags & 0x4000 != 0 {
            glyph.extend(0i16.to_be_bytes());
            glyph.extend(16384i16.to_be_bytes());
        }
    }
    let start = glyph.len() as u16;
    fixture::u16_at(&mut glyph, 2, start);
    if let Some(shared) = shared {
        glyph.extend(shared);
    }
    for (_, _, data) in tuples {
        glyph.extend(data);
    }
    let mut gvar = vec![0; 30];
    fixture::u32_at(&mut gvar, 0, 0x10000);
    fixture::u16_at(&mut gvar, 4, 1);
    fixture::u16_at(&mut gvar, 6, 1);
    fixture::u32_at(&mut gvar, 8, 28);
    fixture::u16_at(&mut gvar, 12, 1);
    fixture::u16_at(&mut gvar, 14, 1);
    fixture::u32_at(&mut gvar, 16, 30);
    fixture::u32_at(&mut gvar, 24, glyph.len() as u32);
    fixture::u16_at(&mut gvar, 28, 7153);
    gvar.extend(glyph);
    let mut maxp = vec![0; 6];
    fixture::u16_at(&mut maxp, 4, 1);
    let mut fvar = vec![0; 36];
    fixture::u16_at(&mut fvar, 4, 16);
    fixture::u16_at(&mut fvar, 8, 1);
    fixture::u16_at(&mut fvar, 10, 20);
    fixture::sfnt(&[(*b"gvar", gvar), (*b"maxp", maxp), (*b"fvar", fvar)], 0)
}
fn case(data: Vec<u8>, coordinate: i16) -> Case {
    Case {
        a: [
            4,
            data.len() as u32,
            0,
            1,
            coordinate as u16 as u32,
            3,
            3,
            7,
        ],
        data,
    }
}
fn ordered() -> Vec<u8> {
    font(
        None,
        &[
            (
                0x8000,
                7153,
                payload(
                    None,
                    &[7, 13, -5, 11, 23, 37, 41],
                    &[5, -7, 19, 29, 31, 43, 47],
                ),
            ),
            (0xa000, 7153, payload(Some(&[0, 2]), &[-13, 27], &[31, -7])),
            (
                0xa000,
                7153,
                payload(
                    Some(&[0, 0, 2, 3, 4]),
                    &[11, 17, -23, 53, -61],
                    &[-7, 19, 37, 71, 83],
                ),
            ),
        ],
    )
}
pub(super) fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    let original = ordered();
    for coordinate in i16::MIN..=i16::MAX {
        cases.push(case(original.clone(), coordinate));
    }
    for indices in [
        vec![],
        vec![0],
        vec![0, 0],
        vec![0, 2],
        vec![3, 4],
        vec![0, 1, 2, 3, 4, 5, 6],
        vec![0, 1, 2, 3, 4, 5, 6, 7],
        vec![65535],
    ] {
        let x: Vec<_> = (0..indices.len()).map(|i| i as i16 * 13 - 19).collect();
        let y: Vec<_> = x.iter().map(|v| -v).collect();
        for shared in [false, true] {
            let shared_bytes = numbers(&indices);
            let data = if shared {
                font(
                    Some(&shared_bytes),
                    &[(0x8000, 7153, payload(None, &x, &y))],
                )
            } else {
                font(None, &[(0xa000, 7153, payload(Some(&indices), &x, &y))])
            };
            for count in 0..=8 {
                for coordinate in [0, 4000, 7153, -9973] {
                    let mut c = case(data.clone(), coordinate);
                    c.a[5] = count;
                    c.a[6] = count;
                    c.a[7] = count + 4;
                    cases.push(c);
                }
            }
        }
    }
    for shared in [
        None,
        Some(vec![0]),
        Some(vec![128, 0]),
        Some(numbers(&[0, 2])),
        Some(vec![255]),
    ] {
        for private in [
            vec![0],
            vec![128, 0],
            numbers(&[0, 0, 2]),
            numbers(&[0, 1, 2, 3, 4, 5, 6, 7]),
        ] {
            let mut p = private;
            p.extend([0x86, 0x86]);
            let data = font(shared.as_deref(), &[(0xa000, 7153, p)]);
            cases.push(case(data, 4000));
        }
        cases.push(case(font(shared.as_deref(), &[]), 4000));
    }
    // Every payload byte mutation, including inactive tuples and trailing tuple data.
    for length in 0..original.len() {
        cases.push(case(original[..length].to_vec(), 4000));
    }
    for offset in 0..original.len() {
        for value in [0, 1, 127, 128, 255] {
            let mut data = original.clone();
            data[offset] = value;
            cases.push(case(data, 4000));
        }
    }
    for coordinate in [0, 7153] {
        for first in [true, false] {
            let good = (0xa000, 7153, payload(Some(&[0, 2]), &[11, 23], &[37, 41]));
            let bad = (0xa000, 7153, vec![3, 0]);
            let tuples = if first {
                vec![bad, good]
            } else {
                vec![good, bad]
            };
            cases.push(case(font(None, &tuples), coordinate));
        }
    }
    for glyph in [0, 1, 65535] {
        for count in 0..=2 {
            for items in 0..=8 {
                let mut c = case(original.clone(), 4000);
                c.a[2] = glyph;
                c.a[3] = count;
                c.a[7] = items;
                cases.push(c);
            }
        }
    }
    for (_, data) in fixture::corpus() {
        cases.push(case(data, 4000));
    }
    cases
}
pub(super) fn snapshot(c: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    let face = match SfntFace::new(c.data.clone(), 0) {
        Ok(f) => {
            emit(&mut out, 0);
            f
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    let coords = vec![c.a[4] as i16; c.a[3] as usize];
    let original: Vec<_> = (0..c.a[6])
        .map(|i| OutlinePoint {
            x: (i * 11) as i32 - 7,
            y: ((i * i * 7) % 43) as i32 - 20,
            flags: (i & 1) as u8,
        })
        .collect();
    let ends = if original.is_empty() {
        vec![]
    } else {
        vec![(original.len() - 1) as u16]
    };
    for result in [
        apply_simple(&face, c.a[2] as u16, &coords, &ends, &original),
        component_offsets(&face, c.a[2] as u16, &coords, c.a[5]),
    ] {
        match result {
            Ok(points) => {
                emit(&mut out, 0);
                for p in points {
                    for v in p {
                        emit(&mut out, v.to_bits());
                    }
                }
            }
            Err(e) => emit(&mut out, e as u32),
        }
    }
    match horizontal_phantom_deltas(&face, c.a[2] as u16, &coords, c.a[7]) {
        Ok(pair) => {
            emit(&mut out, 0);
            for v in pair {
                emit(&mut out, v.to_bits());
            }
        }
        Err(e) => emit(&mut out, e as u32),
    }
    out
}
#[test]
fn full_payload_preflight_includes_inactive_and_later_tuples() {
    let data = font(
        None,
        &[
            (0xa000, 7153, payload(Some(&[0]), &[1], &[2])),
            (0xa000, 7153, vec![3, 0]),
        ],
    );
    let face = SfntFace::new(data, 0).unwrap();
    for coordinate in [0, 7153] {
        assert_eq!(
            component_offsets(&face, 0, &[coordinate], 3),
            Err(FontError::InvalidGlyph)
        );
    }
    assert_eq!(
        horizontal_phantom_deltas(&face, 65535, &[], 3),
        Ok([0.0; 2])
    );
}
#[test]
fn zero_tuple_shared_inventory_preserves_zero_scratch_admission() {
    let data = font(Some(&numbers(&[0, 2])), &[]);
    let face = SfntFace::new(data, 0).unwrap();
    assert_eq!(
        component_offsets(&face, 0, &[7153], 3),
        Err(FontError::Capacity)
    );
    assert_eq!(
        horizontal_phantom_deltas(&face, 0, &[7153], 7),
        Err(FontError::Capacity)
    );
}
#[test]
fn sparse_duplicates_component_offsets_and_phantoms_preserve_distinct_routes() {
    let face = SfntFace::new(ordered(), 0).unwrap();
    assert_eq!(
        component_offsets(&face, 0, &[7153], 3).unwrap(),
        [[22.0, 48.0], [13.0, -7.0], [-1.0, 49.0]]
    );
    assert_eq!(
        horizontal_phantom_deltas(&face, 0, &[7153], 7).unwrap(),
        [64.0, -38.0]
    );
}
