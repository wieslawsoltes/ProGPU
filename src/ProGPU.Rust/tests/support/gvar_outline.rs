use super::{Case, composite_fixture as composite, emit, fixture};
use progpu::font::{FontError, SfntFace, Tag};

fn glyph_variation() -> Vec<u8> {
    // Two ordered private sparse tuples, including duplicate and phantom indices.
    let mut data = vec![
        0, 2, 0, 16, 0, 16, 0xa0, 0, 0x1b, 0xf1, 0, 13, 0xa0, 0, 0x1b, 0xf1,
    ];
    data.extend([4, 3, 0, 0, 2, 3, 3, 1, 7, 19, 31, 3, 3, 11, 23, 37]);
    data.extend([3, 2, 0, 2, 1, 2, 13, 29, 41, 2, 17, 31, 43]);
    data
}
fn attach(data: Vec<u8>, variations: &[Vec<u8>]) -> Vec<u8> {
    let face = SfntFace::new(data, 0).unwrap();
    let mut tables: Vec<_> = [
        *b"head", *b"hhea", *b"hmtx", *b"maxp", *b"loca", *b"glyf", *b"cmap",
    ]
    .into_iter()
    .filter_map(|t| face.table(Tag::new(t)).map(|v| (t, v.bytes.to_vec())))
    .collect();
    let mut gvar = vec![0; 20 + (variations.len() + 1) * 4];
    let start = gvar.len();
    fixture::u32_at(&mut gvar, 0, 0x10000);
    fixture::u16_at(&mut gvar, 4, 1);
    fixture::u32_at(&mut gvar, 8, start as u32);
    fixture::u16_at(&mut gvar, 12, variations.len() as u16);
    fixture::u16_at(&mut gvar, 14, 1);
    fixture::u32_at(&mut gvar, 16, start as u32);
    for (i, v) in variations.iter().enumerate() {
        gvar.extend(v);
        let end = (gvar.len() - start) as u32;
        fixture::u32_at(&mut gvar, 24 + i * 4, end);
    }
    let mut fvar = vec![0; 36];
    fixture::u16_at(&mut fvar, 4, 16);
    fixture::u16_at(&mut fvar, 8, 1);
    fixture::u16_at(&mut fvar, 10, 20);
    tables.extend([(*b"gvar", gvar), (*b"fvar", fvar)]);
    fixture::sfnt(&tables, 0)
}
fn case(data: Vec<u8>, glyph: u16, coordinate: i16) -> Case {
    Case {
        a: [
            5,
            data.len() as u32,
            glyph as u32,
            1,
            coordinate as u16 as u32,
            0,
            0,
            0,
        ],
        data,
    }
}
fn connected() -> Vec<u8> {
    let inner = composite::composite(
        &[
            composite::record(0x23, 0, [3, 5], [0; 4]),
            composite::record(0x81, 0, [1, 0], [8192, -8192, 24576, 16384]),
        ],
        false,
    );
    let outer = composite::composite(
        &[composite::record(
            0x887,
            1,
            [13, 65531],
            [-16384, 8192, 4096, 16384],
        )],
        false,
    );
    attach(
        composite::font(&[composite::simple(), inner, outer]),
        &vec![glyph_variation(); 3],
    )
}
pub(super) fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    for coordinate in [0, 8192, 16384] {
        for glyph in [0, 1, 2] {
            cases.push(case(
                super::font_scene::varied_true_type_font(),
                glyph,
                coordinate,
            ));
        }
    }
    for (index, bytes, glyph) in composite::corpus() {
        let mut c = case(bytes.clone(), glyph, 4000);
        c.a[5] = index;
        cases.push(c);
        if index == 0
            && let Ok(face) = SfntFace::new(bytes.clone(), 0)
            && let Some(count) = face.glyph_count()
        {
            cases.push(case(
                attach(bytes, &vec![glyph_variation(); count as usize]),
                glyph,
                4000,
            ));
        }
    }
    let original = connected();
    for coordinate in i16::MIN..=i16::MAX {
        cases.push(case(original.clone(), 2, coordinate));
    }
    for length in 0..original.len() {
        cases.push(case(original[..length].to_vec(), 2, 4000));
    }
    for i in 0..original.len() {
        for value in [0, 1, 127, 255] {
            let mut data = original.clone();
            data[i] = value;
            cases.push(case(data, 2, 4000));
        }
    }
    for glyph in [0, 1, 2, 3, 65535] {
        for count in 0..=3 {
            let mut c = case(original.clone(), glyph, 4000);
            c.a[3] = count;
            cases.push(c);
        }
    }
    // Shared points on a zero-tuple simple child borrow the whole-tree simple
    // capacity, including a sibling omitted by invalid point attachment.
    for omitted in [false, true] {
        let root = composite::composite(
            &[
                composite::record(0x23, 0, [0, 0], [0; 4]),
                composite::record(if omitted { 1 } else { 3 }, 1, [255, 255], [0; 4]),
            ],
            false,
        );
        let base = composite::font(&[composite::simple(), composite::simple(), root]);
        for shared in [vec![0x80, 0, 0, 4, 2, 1, 0, 2], vec![0x80, 0, 0, 4, 128, 0]] {
            let varied = attach(base.clone(), &[shared, glyph_variation(), vec![]]);
            cases.push(case(varied, 2, 4000));
        }
    }
    // A skipped child's malformed header is still measured; a malformed payload
    // is decoded only if that child is actually admitted for output.
    let root = composite::composite(&[composite::record(1, 0, [255, 255], [0; 4])], false);
    for data in [
        vec![0, 1, 0, 4],
        vec![0, 1, 0, 10, 0, 2, 0xa0, 0, 0x1b, 0xf1, 3, 0],
    ] {
        let f = attach(
            composite::font(&[composite::simple(), root.clone()]),
            &[data, vec![]],
        );
        cases.push(case(f, 1, 4000));
    }
    cases
}
pub(super) fn snapshot(c: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    let face = match SfntFace::new(c.data.clone(), c.a[5]) {
        Ok(f) => {
            emit(&mut out, 0);
            f
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    let coordinates = vec![c.a[4] as i16; c.a[3] as usize];
    match face.varied_glyph_outline(c.a[2] as u16, &coordinates) {
        Ok(g) => {
            emit(&mut out, 0);
            emit(&mut out, g.points().len() as u32);
            emit(&mut out, g.segments().len() as u32);
            for p in g.points() {
                for v in p {
                    emit(&mut out, v.to_bits());
                }
            }
            out.extend_from_slice(bytemuck::cast_slice(g.segments()));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    out
}
#[test]
fn varied_outline_owns_original_source_and_exact_request() {
    let face = SfntFace::new(connected(), 0).unwrap();
    let mut coords = vec![7153, 123];
    let g = face.varied_glyph_outline(2, &coords).unwrap();
    assert_eq!(g.source().data().as_ptr(), face.data().as_ptr());
    assert_eq!(g.coordinates(), coords);
    assert_eq!(g.glyph_index(), 2);
    coords.fill(0);
    drop(face);
    assert_eq!(g.points().len(), 6);
    assert_eq!(g.segments().len(), 4);
    assert_eq!(
        g.source().varied_glyph_outline(2, &[]).unwrap_err(),
        FontError::Capacity
    );
    let plain = g.source().glyph_outline(2).unwrap();
    assert_ne!(g.points(), plain.points());
    let zero = g.source().varied_glyph_outline(2, &[0]).unwrap();
    assert_eq!(zero.points(), plain.points());
    assert_eq!(zero.segments(), plain.segments());
}
#[test]
fn shared_capacity_includes_omitted_siblings_without_decoding_their_payloads() {
    let root = composite::composite(
        &[
            composite::record(0x23, 0, [0, 0], [0; 4]),
            composite::record(1, 1, [255, 255], [0; 4]),
        ],
        false,
    );
    let malformed_payload = vec![0, 1, 0, 10, 0, 2, 0xa0, 0, 0x1b, 0xf1, 3, 0];
    let data = attach(
        composite::font(&[composite::simple(), composite::simple(), root]),
        &[vec![0x80, 0, 0, 4, 2, 1, 0, 2], malformed_payload, vec![]],
    );
    let face = SfntFace::new(data, 0).unwrap();
    assert_eq!(
        face.simple_glyph(0)
            .unwrap()
            .varied_points(&[4000])
            .unwrap_err(),
        FontError::Capacity
    );
    let varied = face.varied_glyph_outline(2, &[4000]).unwrap();
    assert_eq!(varied.points(), face.glyph_outline(2).unwrap().points());
    assert_eq!(
        face.varied_glyph_outline(1, &[4000]).unwrap_err(),
        FontError::InvalidGlyph
    );
}
