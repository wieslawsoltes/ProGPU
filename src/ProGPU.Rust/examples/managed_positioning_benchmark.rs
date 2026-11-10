//! Diagnostic matched current managed positioning plan with active variations.
use progpu::{
    font::{SfntFace, layout::*},
    text::ManagedShapingGlyph,
};
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/gpos_dispatch_fixture.rs"]
mod fixture;
#[path = "../tests/support/managed_glyph_fixture.rs"]
#[allow(dead_code)]
mod records;
fn word(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_le_bytes());
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let count: usize = args[2].parse().unwrap();
    let name = &args[1];
    use fixture::*;
    let (kind, flags, sub, mode, active) = match name.as_str() {
        "single" => (1, 0, single(1, 0xff, 0, 0), 0, true),
        "single-static" => (1, 0, single(1, 0xff, 0, 0), 0, false),
        "single-long" => (1, 0, single(2, 0xf0, 1, 4), 0, true),
        "pair" => (2, 8, pair(1, 0xff, 0xff, 0, 0), 0, true),
        "pair-class" => (2, 8, pair(2, 0xff, 0xff, 0, 0), 0, true),
        "cursive" => (3, 8, cursive(), 1, true),
        "base" => (4, 0, mark(4, 0, 0), 1, true),
        "mark" => (6, 0, mark(6, 0, 0), 1, true),
        "context" => (7, 8, context(false, 3, &[(0, 1), (1, 1)]), 0, true),
        "chain" => (8, 8, context(true, 3, &[(0, 1), (1, 1)]), 0, true),
        _ => panic!("unknown workload"),
    };
    let raw = table(&[
        (kind, flags, vec![sub]),
        (1, 0, vec![single(1, 0xff, 0, 0)]),
    ]);
    let data = selected(raw, &[(*b"kern", vec![0, 1])], 65535);
    let bytes = font(&data, &gdef(&store()));
    let source = SfntFace::new(bytes.clone(), 0).unwrap();
    let coordinate = if active { 8192 } else { 0 };
    let original: Vec<_> = (0..count)
        .map(|i| {
            let id = [8, 9, 10, 12, 20, 30, 11, 12, 13, 10, 20, 30][i % 12];
            let mut g = ManagedShapingGlyph::new(
                id,
                i as i32,
                if id == 12 || id == 13 { 0x301 } else { 65 },
            );
            g.advance_x = 101;
            g.advance_y = -202;
            g.offset_x = -303;
            g.offset_y = 404;
            g.grapheme_cluster = -700;
            g.character_cluster = 900;
            g.ligature_component_count = 3;
            g
        })
        .collect();
    if args.get(3).is_some_and(|s| s == "input") {
        let mut out = vec![];
        word(&mut out, 1);
        word(&mut out, bytes.len() as u32);
        out.extend(bytes);
        for v in [0, coordinate as u32, 1, mode, 0, count as u32, 0] {
            word(&mut out, v);
        }
        for &g in &original {
            for v in records::words(g) {
                word(&mut out, v);
            }
        }
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let plan = ManagedPositioningPlan::from_face(
        source.clone(),
        ManagedFeatureOptions::default(),
        &[68, 70, 76, 84],
        None,
        &[coordinate],
    )
    .unwrap()
    .unwrap();
    let mut buffer = ManagedPositionBuffer::new(
        source,
        original.clone(),
        ManagedPositionDirection::LeftToRight,
    )
    .unwrap();
    let mut times = vec![];
    let mut kerning = false;
    for round in 0..45 {
        buffer.glyphs_mut().copy_from_slice(&original);
        let start = Instant::now();
        kerning = black_box(plan.apply(&mut buffer)).unwrap();
        if mode == 1 {
            black_box(buffer.resolve_attachments()).unwrap();
        }
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        black_box(&buffer);
        if round >= 5 {
            times.push(elapsed);
        }
    }
    let mut out = vec![];
    for v in [1, coordinate as i32 as u32, u32::from(active)] {
        word(&mut out, v);
    }
    for v in [
        0,
        u32::from(kerning),
        buffer.mark_filtering_coverage() as u32,
    ] {
        word(&mut out, v);
    }
    for i in 0..count {
        word(&mut out, buffer.glyph_class(i).unwrap().into());
    }
    for &g in buffer.glyphs() {
        for v in records::words(g) {
            word(&mut out, v);
        }
    }
    times.sort_by(f64::total_cmp);
    eprintln!(
        "execute_p50_us={:.3} execute_p95_us={:.3} execute_p99_us={:.3}",
        times[19], times[37], times[39]
    );
    io::stdout().write_all(&out).unwrap();
}
