use super::{Case, emit, fixture, idx, index, integer, real};
use progpu::font::{FontError, SfntFace};

fn variation(regions: usize) -> Vec<u8> {
    let mut b = vec![0; 16];
    fixture::u16_at(&mut b, 0, 1);
    fixture::u32_at(&mut b, 2, 16);
    fixture::u16_at(&mut b, 6, 2);
    b.extend(1u16.to_be_bytes());
    b.extend((regions as u16).to_be_bytes());
    for i in 0..regions {
        for v in if i % 2 == 0 {
            [0i16, 7153, 16384]
        } else {
            [-16384, -9973, 0]
        } {
            b.extend(v.to_be_bytes());
        }
    }
    for t in 0..2 {
        let pos = b.len() as u32;
        fixture::u32_at(&mut b, 8 + t * 4, pos);
        b.extend([0, 0, 0, 0]);
        b.extend((regions as u16).to_be_bytes());
        for i in 0..regions {
            b.extend((if t == 0 { i } else { regions - i - 1 } as u16).to_be_bytes());
        }
    }
    b
}
#[derive(Default)]
struct Config {
    program: Vec<u8>,
    local: Vec<Vec<u8>>,
    global: Vec<Vec<u8>>,
    private: Option<Vec<u8>>,
    store: Option<Vec<u8>>,
    multiple: bool,
    matrix: Option<&'static str>,
}
fn font(c: &Config) -> Vec<u8> {
    let mut top = vec![0; 26];
    if let Some(scale) = c.matrix {
        top.extend(real(scale));
        top.extend([139, 139]);
        top.extend(real(scale));
        top.extend([139, 139, 12, 7]);
    }
    let mut b = vec![2, 0, 5];
    b.extend((top.len() as u16).to_be_bytes());
    b.extend(&top);
    b.extend(index(&c.global, true, 4));
    let chars = b.len();
    b.extend(index(&[c.program.clone(), vec![]], true, 4));
    let fd = b.len();
    let count = if c.multiple { 2 } else { 1 };
    let dummy = vec![vec![0; 11]; count];
    let fd_len = index(&dummy, true, 4).len();
    b.resize(b.len() + fd_len, 0);
    let select = if c.multiple {
        let o = b.len();
        b.extend([0, 0, 1]);
        o
    } else {
        0
    };
    let private = c.private.as_deref().unwrap_or(&[141, 19]);
    let private_offset = b.len();
    b.extend(private);
    b.extend(index(&c.local, true, 4));
    let mut dict = integer(private.len() as i32);
    dict.extend(integer(if private.is_empty() {
        0
    } else {
        private_offset as i32
    }));
    dict.push(18);
    b[fd..fd + fd_len].copy_from_slice(&index(&vec![dict; count], true, 4));
    let var = if let Some(store) = &c.store {
        let o = b.len();
        b.extend((store.len() as u16).to_be_bytes());
        b.extend(store);
        o
    } else {
        0
    };
    let mut offsets = integer(chars as i32);
    offsets.push(17);
    offsets.extend(integer(fd as i32));
    offsets.extend([12, 36]);
    offsets.extend(integer(select as i32));
    offsets.extend([12, 37]);
    offsets.extend(integer(var as i32));
    offsets.push(24);
    assert_eq!(offsets.len(), 26);
    top[..26].copy_from_slice(&offsets);
    b[5..5 + top.len()].copy_from_slice(&top);
    let mut head = vec![0; 54];
    fixture::u16_at(
        &mut head,
        18,
        if c.matrix == Some("0.0005") {
            2000
        } else {
            1000
        },
    );
    let mut fvar = vec![0; 36];
    fixture::u16_at(&mut fvar, 4, 16);
    fixture::u16_at(&mut fvar, 8, 1);
    fixture::u16_at(&mut fvar, 10, 20);
    fixture::u32_at(&mut fvar, 20, (-65536i32) as u32);
    fixture::u32_at(&mut fvar, 28, 65536);
    fixture::sfnt(&[(*b"CFF2", b), (*b"head", head), (*b"fvar", fvar)], 0)
}
fn operand(v: i16) -> Vec<u8> {
    let mut b = vec![28];
    b.extend(v.to_be_bytes());
    b
}
fn blend() -> Vec<u8> {
    vec![139, 139, 21, 239, 159, 140, 16, 139, 5]
}
pub(super) fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    let mut add = |c: &Config, coords: u32, packed: u32| {
        cases.push(Case {
            mode: 9,
            args: [0, 0, coords, packed],
            data: font(c),
        })
    };
    for escaped in [false, true] {
        for op in 0..=u8::MAX {
            for count in 0..=17 {
                let mut p = vec![139, 139, 21];
                for i in 0..count {
                    p.extend(operand(i * 7 - 31));
                }
                if escaped {
                    p.push(12);
                }
                p.push(op);
                add(
                    &Config {
                        program: p,
                        ..Default::default()
                    },
                    0,
                    0,
                );
            }
        }
    }
    let variable = Config {
        program: blend(),
        store: Some(variation(1)),
        ..Default::default()
    };
    for coordinate in i16::MIN..=i16::MAX {
        add(&variable, 1, coordinate as u16 as u32);
    }
    for regions in [0, 1, 2, 255, 256, 511, 512, 513] {
        for n in [0, 1, 2, 255, 256, 512, 513] {
            let mut program = vec![139; ((regions + 1) * n).min(520)];
            program.extend(operand(n as i16));
            program.extend([16, 139, 5]);
            add(
                &Config {
                    program,
                    store: Some(variation(regions)),
                    ..Default::default()
                },
                1,
                4000,
            );
        }
    }
    for prefix in [
        vec![139, 15],
        vec![140, 15],
        vec![141, 15],
        vec![139, 15, 140, 15],
        vec![255, 0, 0, 128, 0, 15],
        vec![138, 15],
    ] {
        let mut program = prefix;
        program.extend(blend());
        for store in [None, Some(variation(1))] {
            add(
                &Config {
                    program: program.clone(),
                    store,
                    ..Default::default()
                },
                1,
                8192,
            );
        }
    }
    let mut after = blend();
    after.extend([139, 15]);
    add(
        &Config {
            program: after,
            store: Some(variation(1)),
            ..Default::default()
        },
        1,
        8192,
    );
    // Multiple values, preserved prefix and per-value ordered region deltas.
    for coords in 0..=2 {
        for coordinate in [-16384i16, -7311, 0, 7153, 16384] {
            add(
                &Config {
                    program: vec![149, 159, 169, 179, 189, 199, 141, 16, 21, 149, 159, 5],
                    store: Some(variation(2)),
                    ..Default::default()
                },
                coords,
                coordinate as u16 as u32,
            );
        }
    }
    for private in [
        vec![],
        vec![139, 22],
        vec![140, 22],
        vec![141, 22],
        vec![139, 22, 139, 22],
        vec![139, 6, 139, 22],
        vec![139],
        vec![139, 12],
        vec![139, 19, 139, 19],
    ] {
        for store in [None, Some(variation(1))] {
            add(
                &Config {
                    program: blend(),
                    private: Some(private.clone()),
                    store,
                    multiple: true,
                    ..Default::default()
                },
                1,
                7153,
            );
        }
    }
    for mode in [10, 29] {
        for depth in 0..=12 {
            let mut subs = Vec::new();
            for i in 0..depth {
                let mut p = operand(i as i16 + 1 - 107);
                p.push(mode);
                subs.push(p);
            }
            subs.push(vec![149, 159, 5]);
            let mut c = Config {
                program: vec![32, mode],
                ..Default::default()
            };
            if mode == 10 {
                c.local = subs;
            } else {
                c.global = subs;
            }
            add(&c, 0, 0);
        }
    }
    for count in [1239, 1240, 33899, 33900, 65536, 65537] {
        let bias = if count < 1240 {
            107
        } else if count < 33900 {
            1131
        } else {
            32768
        };
        let mut local = vec![vec![]; count];
        local[0] = vec![149, 159, 5];
        let mut program = operand(-bias as i16);
        program.push(10);
        add(
            &Config {
                program,
                local,
                ..Default::default()
            },
            0,
            0,
        );
    }
    for size in [65534, 65535, 65536] {
        add(
            &Config {
                program: vec![139; size],
                ..Default::default()
            },
            0,
            0,
        );
    }
    for matrix in [None, Some("0.001"), Some("0.0005"), Some("0.002")] {
        add(
            &Config {
                matrix,
                program: vec![149, 159, 5],
                ..Default::default()
            },
            0,
            0,
        );
    }
    let data = font(&variable);
    for length in 0..data.len() {
        cases.push(Case {
            mode: 9,
            args: [0, 0, 1, 4000],
            data: data[..length].to_vec(),
        });
    }
    for offset in 0..data.len() {
        for value in [0, 1, 127, 255] {
            let mut b = data.clone();
            b[offset] = value;
            cases.push(Case {
                mode: 9,
                args: [0, 0, 1, 4000],
                data: b,
            });
        }
    }
    for count in [0, 1, 2, 65535] {
        for glyph in [0, 1, 2, u32::MAX] {
            cases.push(Case {
                mode: 9,
                args: [count, glyph, 1, 4000],
                data: data.clone(),
            });
        }
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
    let f = match face.cff2(c.args[0] as u16) {
        Ok(f) => {
            emit(&mut out, 0);
            f
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    for v in [
        u32::from(f.axis_count()),
        f.char_strings().len(),
        f.global_subroutines().len(),
        f.font_dictionaries().len(),
    ] {
        emit(&mut out, v);
    }
    match f.glyph_private(c.args[1]) {
        Ok(p) => {
            emit(&mut out, 0);
            emit(&mut out, u32::from(p.initial_vsindex));
            idx(&mut out, p.local_subroutines);
        }
        Err(e) => emit(&mut out, e as u32),
    }
    let mut coords = vec![0; c.args[2] as usize];
    if !coords.is_empty() {
        coords[0] = c.args[3] as i16;
    }
    if coords.len() > 1 {
        coords[1] = (c.args[3] >> 16) as i16;
    }
    match f.outline(c.args[1], &coords) {
        Ok(g) => {
            emit(&mut out, 1);
            emit(&mut out, 0);
            emit(&mut out, g.segments().len() as u32);
            out.extend_from_slice(bytemuck::cast_slice(g.segments()));
        }
        Err(e) => {
            emit(&mut out, 0);
            emit(&mut out, e as u32);
        }
    }
    out
}
#[test]
fn cff2_blends_retain_owner_and_original_coordinate_request() {
    let face = SfntFace::new(
        font(&Config {
            program: blend(),
            store: Some(variation(1)),
            ..Default::default()
        }),
        0,
    )
    .unwrap();
    let f = face.cff2(2).unwrap();
    let g = f.outline(0, &[7153, 123]).unwrap();
    assert_eq!(g.segments().len(), 2);
    assert_eq!(
        g.segments()[0],
        progpu::path::Segment::line([0.0, 0.0], [120.0, 0.0])
    );
    assert_eq!(g.coordinates(), [7153, 123]);
    drop(face);
    drop(f);
    assert_eq!(g.source().outline_segment_count(0, &[0]), Ok(2));
    assert_eq!(g.source().outline(0, &[]).unwrap_err(), FontError::Capacity);
}
#[test]
fn cff2_rejects_cff1_termination_and_retains_selected_private_boundary() {
    for p in [vec![14], vec![11], vec![12, 23]] {
        let f = SfntFace::new(
            font(&Config {
                program: p,
                ..Default::default()
            }),
            0,
        )
        .unwrap()
        .cff2(0)
        .unwrap();
        assert_eq!(f.outline(0, &[]).unwrap_err(), FontError::InvalidGlyph);
    }
    let f = SfntFace::new(
        font(&Config {
            private: Some(vec![139]),
            ..Default::default()
        }),
        0,
    )
    .unwrap()
    .cff2(0)
    .unwrap();
    assert_eq!(f.glyph_private(0).unwrap_err(), FontError::InvalidFace);
}
