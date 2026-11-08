use super::{Case, emit};
use progpu::font::{FontError, SfntFace, gvar::tuple_scalar};
#[allow(dead_code)]
#[path = "font_fixture.rs"]
mod fixture;
fn font(flags: u16, long: bool) -> Vec<u8> {
    let mut glyph = vec![0, 1, 0, 0, 0, 2];
    glyph.extend(flags.to_be_bytes());
    if flags & 0x8000 != 0 {
        glyph.extend(7153i16.to_be_bytes());
        glyph.extend((-9973i16).to_be_bytes());
    }
    if flags & 0x4000 != 0 {
        for v in [0i16, -16384, 16384, 0] {
            glyph.extend(v.to_be_bytes());
        }
    }
    let payload = glyph.len() as u16;
    fixture::u16_at(&mut glyph, 2, payload);
    glyph.extend([0x80, 0x80]);
    let offset = if long { 36 } else { 30 };
    let mut gvar = vec![0; offset];
    fixture::u32_at(&mut gvar, 0, 0x10000);
    fixture::u16_at(&mut gvar, 4, 2);
    fixture::u16_at(&mut gvar, 6, 1);
    fixture::u32_at(&mut gvar, 8, (offset - 4) as u32);
    fixture::u16_at(&mut gvar, 12, 2);
    fixture::u16_at(&mut gvar, 14, u16::from(long));
    fixture::u32_at(&mut gvar, 16, offset as u32);
    if long {
        fixture::u32_at(&mut gvar, 24, glyph.len() as u32);
        fixture::u32_at(&mut gvar, 28, glyph.len() as u32);
    } else {
        fixture::u16_at(&mut gvar, 22, (glyph.len() / 2) as u16);
        fixture::u16_at(&mut gvar, 24, (glyph.len() / 2) as u16);
    }
    fixture::u16_at(&mut gvar, offset - 4, 7153);
    fixture::u16_at(&mut gvar, offset - 2, (-9973i16) as u16);
    gvar.extend(glyph);
    let mut maxp = vec![0; 6];
    fixture::u16_at(&mut maxp, 4, 2);
    let mut fvar = vec![0; 56];
    fixture::u16_at(&mut fvar, 4, 16);
    fixture::u16_at(&mut fvar, 8, 2);
    fixture::u16_at(&mut fvar, 10, 20);
    fixture::sfnt(&[(*b"gvar", gvar), (*b"maxp", maxp), (*b"fvar", fvar)], 0)
}
pub(super) fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    for flags in 0..=u16::MAX {
        cases.push(Case {
            a: [3, 0, 0, 0, 3, 4000, (-7311i32) as u32, 2],
            data: font(flags, flags & 1 != 0),
        });
    }
    for long in [false, true] {
        let data = font(0xe000, long);
        for length in 0..data.len() {
            cases.push(Case {
                a: [3, 0, 0, 0, 2, 7153, (-9973i32) as u32, 2],
                data: data[..length].to_vec(),
            });
        }
        for offset in 0..data.len() {
            for value in [0, 1, 127, 255] {
                let mut b = data.clone();
                b[offset] = value;
                cases.push(Case {
                    a: [3, 0, 0, 0, 2, 7153, 0, 2],
                    data: b,
                });
            }
        }
        for glyph in [0, 1, 2, 65535] {
            for shared in [0, 1, 65535] {
                for capacity in 0..=3 {
                    for coordinates in 0..=2 {
                        cases.push(Case {
                            a: [
                                3,
                                0,
                                glyph,
                                shared,
                                capacity,
                                7153,
                                (-9973i32) as u32,
                                coordinates,
                            ],
                            data: data.clone(),
                        });
                    }
                }
            }
        }
    }
    for (_, data) in fixture::corpus() {
        cases.push(Case {
            a: [3, 0, 0, 0, 2, 0, 0, 2],
            data,
        });
    }
    for c in &mut cases {
        c.a[1] = c.data.len() as u32;
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
    let f = match face.gvar() {
        Ok(Some(f)) => {
            emit(&mut out, 0);
            emit(&mut out, 1);
            f
        }
        Ok(None) => {
            emit(&mut out, 0);
            emit(&mut out, 0);
            return out;
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    let h = f.header();
    for v in [
        u32::from(h.axis_count),
        u32::from(h.shared_tuple_count),
        u32::from(h.glyph_count),
        u32::from(h.uses_long_offsets),
    ] {
        emit(&mut out, v);
    }
    let mut shared = vec![-12345; c.a[4] as usize];
    emit(
        &mut out,
        f.shared_tuple_into(c.a[3] as u16, &mut shared)
            .err()
            .map_or(0, |e| e as u32),
    );
    for v in shared {
        emit(&mut out, v as u32);
    }
    match f.glyph_data(c.a[2] as u16) {
        Ok(v) => {
            for n in [
                0,
                u32::from(v.tuple_count),
                u32::from(v.serialized_data_offset),
                u32::from(v.has_shared_point_numbers),
                v.bytes.len() as u32,
            ] {
                emit(&mut out, n);
            }
            out.extend(v.bytes);
        }
        Err(e) => emit(&mut out, e as u32),
    }
    match f.tuple_headers(c.a[2] as u16) {
        Ok(t) => {
            emit(&mut out, 0);
            emit(&mut out, t.headers().len() as u32);
            emit(&mut out, t.coordinates().len() as u32);
            for header in t.headers() {
                for v in [
                    header.region_coordinate_offset,
                    u32::from(header.serialized_data_size),
                    u32::from(header.flags),
                ] {
                    emit(&mut out, v);
                }
            }
            for v in t.coordinates() {
                emit(&mut out, *v as u32);
            }
            let coords = [c.a[5] as i16, c.a[6] as i16];
            for header in t.headers() {
                let start = header.region_coordinate_offset as usize;
                emit(
                    &mut out,
                    tuple_scalar(
                        &coords[..c.a[7] as usize],
                        &t.coordinates()[start..start + usize::from(h.axis_count) * 3],
                    )
                    .to_bits(),
                );
            }
        }
        Err(e) => emit(&mut out, e as u32),
    }
    out
}
#[test]
fn tuple_generation_keeps_source_raw_flags_and_empty_glyph_slots() {
    let face = SfntFace::new(font(0xe000, true), 0).unwrap();
    let f = face.gvar().unwrap().unwrap();
    let t = f.tuple_headers(0).unwrap();
    assert_eq!(t.headers()[0].flags, 0xe000);
    assert_eq!(t.coordinates(), [0, -16384, 7153, -9973, 16384, 0]);
    assert_eq!(t.source().source().data().as_ptr(), face.data().as_ptr());
    drop(face);
    drop(f);
    assert!(t.source().tuple_headers(1).unwrap().headers().is_empty());
    assert_eq!(
        t.source().glyph_data(2).unwrap_err(),
        FontError::InvalidArgument
    );
    assert_eq!(tuple_scalar(&[], t.coordinates()), 1.0);
    assert_eq!(tuple_scalar(&[0], &[0, 1]), 0.0);
}
