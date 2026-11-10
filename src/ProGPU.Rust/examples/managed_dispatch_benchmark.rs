//! Diagnostic full raw managed stage baseline with all original record fields.
use progpu::{
    font::{SfntFace, layout::*},
    text::ManagedShapingGlyph,
};
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/managed_dispatch_fixture.rs"]
mod fixture;
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
    use fixture::gsub::*;
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
    let data = fixture::table(
        table(&[(kind, flags, vec![sub]), (1, 0, vec![single(100)])]),
        &[(
            if mode == "alternate" {
                *b"rand"
            } else {
                *b"liga"
            },
            vec![0],
        )],
        u16::MAX,
    );
    let bytes = fixture::font(&data, &[]);
    let source = SfntFace::new(bytes.clone(), 0).unwrap();
    let options = ManagedFeatureOptions::default();
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
        word(&mut out, bytes.len() as u32);
        out.extend(&bytes);
        word(&mut out, 1);
        word(&mut out, 1);
        word(&mut out, 0);
        word(&mut out, 0);
        word(&mut out, u32::MAX);
        word(&mut out, 0);
        for n in [3, 0, 0, bytes.len() as u32] {
            word(&mut out, n);
        }
        out.extend(bytes);
        for c in [68u16, 70, 76, 84] {
            out.extend(c.to_le_bytes());
        }
        for n in [0, initial.random_state, 0, 0, 0, 0, count as u32] {
            word(&mut out, n);
        }
        for &g in &original {
            for n in records::words(g) {
                word(&mut out, n);
            }
        }
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let plan =
        ManagedSubstitutionPlan::from_face(source.clone(), options, &[68, 70, 76, 84], None, &[])
            .unwrap()
            .unwrap();
    let mut times = vec![];
    let mut out = vec![];
    for round in 0..45 {
        let mut glyphs = Vec::with_capacity(count * 3 + 4);
        glyphs.extend_from_slice(&original);
        let mut buffer = ManagedGlyphBuffer::new(source.clone(), glyphs).unwrap();
        buffer.set_state(initial);
        let start = Instant::now();
        black_box(plan.apply_stage(&mut buffer, ManagedSubstitutionStage::All)).unwrap();
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        black_box(&buffer);
        if round >= 5 {
            times.push(elapsed);
        }
        if round == 44 {
            let state = buffer.state();
            for n in [
                0,
                buffer.glyphs().len() as u32,
                state.context_match_end as u32,
                state.random_state,
                buffer.mark_filtering_coverage() as u32,
                buffer.nested_lookup_depth(),
                u32::from(state.traversal.manual_joiners),
                u32::from(state.traversal.restrict_to_syllable),
                u32::from(state.traversal.lookup_syllable),
                u32::from(buffer.has_fraction_actions()),
            ] {
                word(&mut out, n);
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
