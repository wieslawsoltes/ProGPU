//! Diagnostic baseline of original current managed nested calls, with full records.
use progpu::{
    font::{SfntFace, layout::*},
    text::ManagedShapingGlyph,
};
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/gsub_fixture.rs"]
mod fixture;
#[path = "../tests/support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
#[path = "../tests/support/managed_glyph_fixture.rs"]
#[allow(dead_code)]
mod records;
fn word(out: &mut Vec<u8>, n: u32) {
    out.extend(n.to_le_bytes());
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
        "alternate" => (3, 0, multiple(&[100, 101, 102])),
        _ => panic!("unknown mode"),
    };
    let data = table(&[(kind, flags, vec![sub]), (1, 0, vec![single(100)])]);
    let reverse = (kind == 8).then(|| {
        LayoutTable::parse(&data)
            .unwrap()
            .lookup(0)
            .unwrap()
            .subtable_offset(0)
            .unwrap()
    });
    let bytes = font::sfnt(&font::tables(vec![]), 0);
    let source = SfntFace::new(bytes.clone(), 0).unwrap();
    let original: Vec<_> = (0..count)
        .map(|i| {
            let id = [8, 9, 10, 12, 20, 30, 10, 20][i % 8];
            let mut g = ManagedShapingGlyph::new(id, i as i32, if id == 12 { 0x301 } else { 65 });
            g.advance_x = 101;
            g.advance_y = -202;
            g.offset_x = -303;
            g.offset_y = 404;
            g.grapheme_cluster = -700;
            g.character_cluster = 900;
            g.attachment_kind = 5;
            g.attachment_target = 10123;
            g
        })
        .collect();
    let initial = ManagedGlyphState {
        random_state: 0xdeadbeef,
        ..Default::default()
    };
    if args.get(3).is_some_and(|s| s == "input") {
        let mut out = vec![];
        word(&mut out, 1);
        word(&mut out, bytes.len() as u32);
        out.extend(bytes);
        for n in [
            data.len() as u32,
            0,
            0,
            2,
            reverse.map_or(u32::MAX, |v| v as u32),
            u32::from(flags),
            0,
            0,
            0,
            initial.random_state,
            count as u32,
        ] {
            word(&mut out, n);
        }
        out.extend(data);
        for &g in &original {
            for n in records::words(g) {
                word(&mut out, n);
            }
        }
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let table = ManagedGsubTable::new(data).unwrap();
    let positions: Vec<_> = original
        .iter()
        .enumerate()
        .filter_map(|(i, g)| (g.glyph_index == 10).then_some(i))
        .rev()
        .collect();
    let mut times = vec![];
    let mut out = vec![];
    for round in 0..45 {
        let mut glyphs = Vec::with_capacity(count * 3 + 4);
        glyphs.extend_from_slice(&original);
        let mut buffer = ManagedGlyphBuffer::new(source.clone(), glyphs).unwrap();
        buffer.set_state(initial);
        let mut changed = false;
        let start = Instant::now();
        for &position in &positions {
            changed |= black_box(if let Some(offset) = reverse {
                table.apply_reverse_at(&mut buffer, offset, position, flags)
            } else {
                table.apply_lookup_at(&mut buffer, 0, position)
            })
            .unwrap();
        }
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        black_box((&buffer, changed));
        if round >= 5 {
            times.push(elapsed);
        }
        if round == 44 {
            for n in [
                0,
                u32::from(changed),
                buffer.glyphs().len() as u32,
                buffer.state().context_match_end as u32,
                buffer.state().random_state,
                buffer.mark_filtering_coverage() as u32,
                buffer.nested_lookup_depth(),
            ] {
                word(&mut out, n);
            }
            for i in 0..buffer.glyphs().len() {
                word(&mut out, u32::from(buffer.glyph_class(i).unwrap()));
            }
            for &g in buffer.glyphs() {
                for n in records::words(g) {
                    word(&mut out, n);
                }
            }
        }
    }
    times.sort_by(f64::total_cmp);
    eprintln!(
        "execute_p50_us={:.3} execute_p95_us={:.3} execute_p99_us={:.3}",
        times[19], times[37], times[39]
    );
    io::stdout().write_all(&out).unwrap();
}
