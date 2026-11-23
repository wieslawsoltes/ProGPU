//! Diagnostic matched original raw GPOS and attachment workload.
use progpu::{
    font::{SfntFace, layout::*},
    text::ManagedShapingGlyph,
};
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/gpos_fixture.rs"]
mod fixture;
#[path = "../tests/support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
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
    let (kind, flags, sub, mode) = match name.as_str() {
        "single" => (1, 0, single(1, 15), 0),
        "pair" => (2, 8, pair(2, 15, 15), 0),
        "cursive" => (3, 8, cursive(), 1),
        "base" => (4, 0, mark(4), 1),
        "ligature" => (5, 0, mark(5), 1),
        "mark" => (6, 0, mark(6), 1),
        "context" => (7, 8, context(false, 3, &[(0, 1), (1, 1)]), 0),
        "chain" => (8, 8, context(true, 3, &[(0, 1), (1, 1)]), 0),
        "resolve" => (1, 0, single(1, 0), 2),
        "zero" => (1, 0, single(1, 0), 4),
        _ => panic!("unknown workload"),
    };
    let data = table(&[(kind, flags, vec![sub]), (1, 0, vec![single(1, 15)])]);
    let mut gd = gdef();
    put(&mut gd, 26, 3);
    let mut tables = font::tables(vec![]);
    tables.push((*b"GDEF", gd));
    tables.push((*b"GPOS", data.clone()));
    let bytes = font::sfnt(&tables, 0);
    let source = SfntFace::new(bytes.clone(), 0).unwrap();
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
            if mode == 2 {
                g.attachment_kind = 2;
                g.attachment_target = i as i32 + 1;
                g.offset_y = 1;
            }
            g
        })
        .collect();
    if args.get(3).is_some_and(|s| s == "input") {
        let mut out = vec![];
        word(&mut out, 1);
        word(&mut out, bytes.len() as u32);
        out.extend(bytes);
        for v in [data.len() as u32, 0, 0, 0, 1, mode, count as u32] {
            word(&mut out, v);
        }
        out.extend(data);
        for &g in &original {
            for v in records::words(g) {
                word(&mut out, v);
            }
        }
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let table = ManagedGposTable::from_face(source.clone())
        .unwrap()
        .unwrap();
    let mut buffer = ManagedPositionBuffer::new(
        source,
        original.clone(),
        ManagedPositionDirection::LeftToRight,
    )
    .unwrap();
    let mut times = vec![];
    let mut changed = false;
    for round in 0..45 {
        buffer.glyphs_mut().copy_from_slice(&original);
        changed = false;
        let start = Instant::now();
        if mode < 2 {
            for i in 0..count {
                changed |= black_box(table.apply_lookup_at(&mut buffer, 0, i)).unwrap();
            }
        }
        if mode == 1 || mode == 2 {
            black_box(buffer.resolve_attachments()).unwrap();
        }
        if mode >= 3 {
            black_box(buffer.zero_mark_advances(mode == 4)).unwrap();
        }
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        black_box(&buffer);
        if round >= 5 {
            times.push(elapsed);
        }
    }
    let mut out = vec![];
    for v in [
        0,
        u32::from(changed),
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
