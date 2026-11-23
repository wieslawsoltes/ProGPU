//! Original GSUB execution baseline. Resetting storage and serialization are untimed.
use progpu::{
    font::layout::{GsubOptions, GsubState, LayoutTable},
    text::ShapingGlyph,
};
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/gsub_fixture.rs"]
mod fixture;
fn word(out: &mut Vec<u8>, n: u32) {
    out.extend(n.to_le_bytes());
}
fn glyph(out: &mut Vec<u8>, g: ShapingGlyph) {
    for n in [
        g.glyph_id,
        g.code_point,
        g.cluster as u32,
        g.flags,
        g.advance_x as u32,
        g.advance_y as u32,
        g.offset_x as u32,
        g.offset_y as u32,
    ] {
        word(out, n);
    }
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let mode = &args[1];
    let count: usize = args[2].parse().unwrap();
    use fixture::*;
    let (kind, flags, sub) = match mode.as_str() {
        "single" => (1, 0, single(100)),
        "multiple" => (2, 0, multiple(&[100, 101, 102])),
        "delete" => (2, 0, multiple(&[])),
        "ligature" => (4, 8, ligature(&[20], 88)),
        "context" => (5, 8, context(false, 3, &[(0, 1)])),
        "chain" => (6, 8, context(true, 3, &[(0, 1)])),
        "reverse" => (8, 8, reverse()),
        "random" => (3, 0, multiple(&[100, 101, 102])),
        _ => panic!("unknown mode"),
    };
    let data = table(&[(kind, flags, vec![sub]), (1, 0, vec![single(100)])]);
    let mut original: Vec<_> = (0..count)
        .map(|i| {
            let id = [8, 9, 10, 12, 20, 30, 10, 20][i % 8];
            ShapingGlyph {
                glyph_id: id,
                code_point: if id == 12 { 0x301 } else { 65 },
                cluster: i as i32,
                flags: 0,
                advance_x: 101,
                advance_y: -202,
                offset_x: -303,
                offset_y: 404,
            }
        })
        .collect();
    original.resize(
        count * 3 + 4,
        ShapingGlyph {
            glyph_id: 0x12345678,
            ..Default::default()
        },
    );
    let initial = GsubState {
        glyph_count: count as u32,
        applied: true,
        context_match_end: Some(0),
        random_state: Some(0xdeadbeef),
    };
    let options = GsubOptions {
        mark_substituted: true,
        track_fallback_mark_metadata: true,
        track_arabic_stretch_metadata: true,
        random_alternate: true,
        restrict_to_syllable: true,
        track_substitution_provenance: true,
        ..Default::default()
    };
    if args.get(3).is_some_and(|s| s == "input") {
        let mut out = vec![];
        for n in [
            data.len() as u32,
            0,
            0,
            u32::MAX,
            original.len() as u32,
            count as u32,
            255,
            1,
            0,
            0,
            0,
            0xdeadbeef,
        ] {
            word(&mut out, n);
        }
        out.extend(&data);
        for &g in &original {
            glyph(&mut out, g);
        }
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let table = LayoutTable::parse(&data).unwrap();
    let mut storage = original.clone();
    let mut state = initial;
    table
        .apply_gsub(0, None, &mut storage, options, &mut state)
        .unwrap();
    let mut out = vec![];
    for n in [
        0,
        0,
        1,
        0,
        state.glyph_count,
        u32::from(state.applied),
        state.context_match_end.unwrap(),
        state.random_state.unwrap(),
    ] {
        word(&mut out, n);
    }
    for &g in &storage {
        glyph(&mut out, g);
    }
    let mut times = vec![];
    for round in 0..45 {
        storage.copy_from_slice(&original);
        state = initial;
        let start = Instant::now();
        black_box(table.apply_gsub(0, None, black_box(&mut storage), options, &mut state)).unwrap();
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        black_box((&storage, state));
        if round >= 5 {
            times.push(elapsed);
        }
    }
    times.sort_by(f64::total_cmp);
    eprintln!(
        "execute_p50_us={:.3} execute_p95_us={:.3} execute_p99_us={:.3}",
        times[19], times[37], times[39]
    );
    io::stdout().write_all(&out).unwrap();
}
