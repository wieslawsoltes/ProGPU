//! Matched allocation-free original simple mapping/advance workload.
use progpu::{
    font::SfntFace,
    text::{
        SimpleAdvanceScale, SimpleGlyphMetrics, build_simple_glyph_run_into,
        fill_simple_glyph_advances, simple_glyph_run_requirements,
    },
};
use std::{error::Error, hint::black_box, time::Instant};
struct Work {
    text: Vec<u16>,
    clusters: Vec<u16>,
    glyphs: Vec<u16>,
    metrics: Vec<SimpleGlyphMetrics>,
    state: Vec<u8>,
    advances: Vec<i32>,
    scale: SimpleAdvanceScale,
    blank: u16,
    hyphen: u16,
}
fn run(face: &SfntFace, work: &mut Work) -> Result<u64, Box<dyn Error>> {
    let count = build_simple_glyph_run_into(
        face,
        black_box(&work.text),
        work.blank,
        work.hyphen,
        &mut work.clusters,
        &mut work.glyphs,
    )?;
    if count as usize != work.glyphs.len() {
        return Err("unstable glyph inventory".into());
    }
    fill_simple_glyph_advances(
        &work.text,
        &work.clusters,
        &work.glyphs,
        &work.metrics,
        work.scale,
        &mut work.state,
        &mut work.advances,
    )?;
    let mut hash = 14695981039346656037u64;
    let mut add = |v: u32| {
        hash = (hash ^ u64::from(v)).wrapping_mul(1099511628211);
    };
    for &v in &work.clusters {
        add(u32::from(v));
    }
    for &v in &work.glyphs {
        add(u32::from(v));
    }
    for &v in &work.advances {
        add(v as u32);
    }
    for &v in &work.state {
        add(u32::from(v));
    }
    Ok(black_box(hash))
}
fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.is_empty() || args.len() > 3 {
        return Err("usage: simple_text_benchmark FONT [FACE] [REPEATS]".into());
    }
    let face = SfntFace::new(
        std::fs::read(&args[0])?,
        args.get(1).map_or(Ok(0), |s| s.parse::<u32>())?,
    )?;
    let repeats = args.get(2).map_or(Ok(256), |s| s.parse::<usize>())?;
    if repeats == 0 || repeats > 4096 {
        return Err("repeats must be 1..4096".into());
    }
    let text = [
        0x41, 0x56, 0x57, 0x20, 0x69, 0x6a, 0xad, 10, 0x85, 0xd83d, 0xde00, 0xd800, 0x200d, 0x5d0,
    ]
    .repeat(repeats);
    let r = simple_glyph_run_requirements(&text)?;
    let count = r.glyph_count as usize;
    let mut work = Work {
        text,
        clusters: vec![0; r.cluster_map_count as usize],
        glyphs: vec![0; count],
        metrics: Vec::with_capacity(count),
        state: vec![0; count],
        advances: vec![0; count],
        scale: SimpleAdvanceScale {
            units_per_em: face.header_metrics().ok_or("missing head")?.units_per_em,
            font_em_size: 13.25,
            scaling_factor: 1.25,
            is_sideways: false,
        },
        blank: face.glyph_index(0x20).ok_or("missing cmap")?,
        hyphen: face.glyph_index(0x2d).ok_or("missing cmap")?,
    };
    build_simple_glyph_run_into(
        &face,
        &work.text,
        work.blank,
        work.hyphen,
        &mut work.clusters,
        &mut work.glyphs,
    )?;
    // Immutable per-occurrence metric gathering is initialization, outside timing.
    for &glyph in &work.glyphs {
        work.metrics.push(SimpleGlyphMetrics {
            advance_width: u32::from(
                face.horizontal_glyph_metrics(glyph)
                    .ok_or("missing horizontal metrics")?
                    .advance_width,
            ),
            advance_height: u32::try_from(
                face.design_advance_height(glyph)
                    .ok_or("missing vertical metrics")?,
            )?,
        });
    }
    let checksum = run(&face, &mut work)?;
    for _ in 0..5 {
        assert_eq!(run(&face, &mut work)?, checksum);
    }
    if let Some(rounds) = std::env::var_os("PROGPU_RUST_PROFILE_ROUNDS") {
        let rounds: usize = rounds.to_str().ok_or("non-Unicode rounds")?.parse()?;
        for _ in 0..rounds {
            assert_eq!(run(&face, &mut work)?, checksum);
        }
        println!("profile_rounds={rounds} checksum={checksum}");
        return Ok(());
    }
    let mut times = Vec::with_capacity(40);
    for _ in 0..40 {
        let start = Instant::now();
        let actual = run(&face, &mut work)?;
        times.push(start.elapsed().as_secs_f64() * 1e6);
        assert_eq!(actual, checksum);
    }
    times.sort_by(f64::total_cmp);
    println!(
        "units={} glyphs={count} scratch_bytes={} checksum={checksum} round_us_p50={:.3} p95={:.3} p99={:.3}",
        work.text.len(),
        work.state.len(),
        times[19],
        times[37],
        times[39]
    );
    Ok(())
}
