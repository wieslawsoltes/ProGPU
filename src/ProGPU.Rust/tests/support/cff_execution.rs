use super::{Case, emit, fixture, index, integer, private};
use progpu::font::{SfntFace, cff::OutlineTransform};
fn operand(v: i16) -> Vec<u8> {
    let mut b = vec![28];
    b.extend(v.to_be_bytes());
    b
}
fn font(program: &[u8], local: &[Vec<u8>], global: &[Vec<u8>]) -> Vec<u8> {
    let global = index(global, false, 4);
    let chars = index(&[program.to_vec()], false, 4);
    // Fixed-width top operands make all final offsets independent of their values.
    let char_offset = 30 + global.len();
    let private_offset = char_offset + chars.len();
    let mut top = integer(char_offset as i32);
    top.push(17);
    top.extend(private(private_offset as i32));
    let mut b = vec![1, 0, 4, 4, 0, 0];
    b.extend(index(&[top], false, 1));
    b.extend([0, 0]);
    b.extend(global);
    assert_eq!(b.len(), char_offset);
    b.extend(chars);
    b.extend([141, 19]);
    b.extend(index(local, false, 4));
    fixture::sfnt(&[(*b"CFF ", b)], 0)
}
pub(super) fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    let mut add =
        |p: &[u8], local: &[Vec<u8>], global: &[Vec<u8>], transforms: std::ops::Range<u32>| {
            let data = font(p, local, global);
            for t in transforms {
                cases.push(Case {
                    mode: 8,
                    args: [t, 0, 0, 0],
                    data: data.clone(),
                });
            }
        };
    for escaped in [false, true] {
        for op in 0..=255u8 {
            for count in 0..=17 {
                let mut p = vec![139, 139, 21];
                for i in 0..count {
                    p.extend(operand((i * 7 - 31) as i16));
                }
                if escaped {
                    p.push(12);
                }
                p.push(op);
                p.push(14);
                add(&p, &[], &[], 0..3);
                if escaped {
                    p.pop();
                    p.extend([22, 149, 159, 5, 14]);
                    add(&p, &[], &[], 0..1);
                }
            }
        }
    }
    // Observable arithmetic, transient, index, roll and deterministic random results.
    for p in [
        vec![149, 144, 12, 20, 144, 12, 21, 22, 149, 159, 5, 14],
        vec![12, 23, 22, 149, 159, 5, 14],
        vec![149, 159, 169, 141, 140, 12, 30, 12, 18, 21, 149, 159, 5, 14],
        vec![149, 159, 139, 12, 29, 12, 18, 21, 149, 159, 5, 14],
        vec![139, 139, 21, 149, 159, 5, 139, 139, 21, 14],
        vec![149, 159, 5, 14],
    ] {
        add(&p, &[], &[], 0..6);
    }
    for mode in [10, 29] {
        for depth in 0..=12 {
            let mut subs = Vec::new();
            for i in 0..depth {
                let mut p = operand(i as i16 + 1 - 107);
                p.extend([mode, 11]);
                subs.push(p);
            }
            subs.push(vec![139, 139, 21, 149, 159, 5, 11]);
            let root = [32, mode, 14];
            if mode == 10 {
                add(&root, &subs, &[], 0..3);
            } else {
                add(&root, &[], &subs, 0..3);
            }
        }
    }
    for count in [1239, 1240, 33899, 33900] {
        let bias = if count < 1240 {
            107
        } else if count < 33900 {
            1131
        } else {
            32768
        };
        let mut subs = vec![vec![11]; count];
        subs[0] = vec![149, 159, 5, 11];
        let mut p = operand(-bias as i16);
        p.extend([10, 14]);
        add(&p, &subs, &[], 0..1);
    }
    for count in [0, 1, 2, 192, 193, 512, 513, 514] {
        let mut p = vec![139; count];
        p.extend([1, 14]);
        add(&p, &[], &[], 0..1);
    }
    for stems in [0, 1, 8, 9, 96, 97] {
        let mut p = vec![139; stems * 2];
        p.push(19);
        p.resize(p.len() + stems.div_ceil(8), 0);
        p.push(14);
        add(&p, &[], &[], 0..1);
        if p.len() > 2 {
            p.truncate(p.len() - 2);
            add(&p, &[], &[], 0..1);
        }
    }
    let good = [
        139, 139, 21, 255, 0, 0, 128, 0, 255, 255, 255, 128, 0, 5, 14,
    ];
    for len in 0..=good.len() {
        add(&good[..len], &[], &[], 0..3);
    }
    let mut seed = 0x55667788u32;
    for i in 0..4096 {
        let mut p = vec![0; i % 49];
        for b in &mut p {
            seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
            *b = (seed >> 24) as u8;
        }
        add(&p, &[], &[], 0..1);
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
    let font = match face.cff1(0) {
        Ok(f) => {
            emit(&mut out, 0);
            f
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    let transform = match c.args[0] {
        1 => Some(OutlineTransform::default()),
        2 => Some(OutlineTransform([0.5, -0.25, 0.125, 2.0, 3.125, -1.5])),
        3 => Some(OutlineTransform([f64::NAN, 0.0, 0.0, 1.0, 0.0, 0.0])),
        4 => Some(OutlineTransform([f64::MAX, 0.0, 0.0, 1.0, 0.0, 0.0])),
        5 => Some(OutlineTransform([0.0; 6])),
        _ => None,
    };
    let r = if let Some(t) = transform {
        font.outline_transformed(c.args[1], t)
    } else {
        font.outline(c.args[1])
    };
    match r {
        Ok(g) => {
            emit(&mut out, 0);
            emit(&mut out, g.segments().len() as u32);
            out.extend_from_slice(bytemuck::cast_slice(g.segments()));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    out
}
#[test]
fn closed_outline_retains_owner_and_original_bounds() {
    let bytes = font(
        &[139, 139, 21, 239, 139, 139, 239, 39, 139, 5, 14],
        &[],
        &[],
    );
    let face = SfntFace::new(bytes.clone(), 0).unwrap();
    let f = face.cff1(1).unwrap();
    let g = f.outline(0).unwrap();
    assert_eq!(f.outline_segment_count(0).unwrap(), 4);
    assert_eq!(g.source().source().data().as_ptr(), face.data().as_ptr());
    drop(face);
    drop(f);
    assert_eq!(g.source().source().data(), bytes);
    assert_eq!(g.glyph_index(), 0);
    assert_eq!(g.segments()[3].points[0], [0.0, 100.0]);
    assert_eq!(g.segments()[3].points[1], [0.0, 0.0]);
    assert!(g.source().outline(1).is_err());
}
