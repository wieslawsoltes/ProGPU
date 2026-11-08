//! Matched CPU-only original simple-glyph decoding and path publication workload.
use progpu::font::{GlyphKind, SfntFace};
use std::{error::Error, hint::black_box, time::Instant};
fn run(face: &SfntFace, glyphs: &[u16], expanded: bool) -> Result<u64, Box<dyn Error>> {
    let mut checksum = 14695981039346656037u64;
    let mut hash = |value: u32| {
        checksum = (checksum ^ u64::from(value)).wrapping_mul(1099511628211);
    };
    for &id in glyphs {
        if expanded {
            let glyph = face.glyph_outline(black_box(id))?;
            for point in glyph.points() {
                for value in point {
                    hash(value.to_bits());
                }
            }
            for &word in bytemuck::cast_slice::<_, u32>(glyph.segments()) {
                hash(word);
            }
            continue;
        }
        let glyph = face.simple_glyph(black_box(id))?;
        for point in glyph.points() {
            for value in [point.x as u32, point.y as u32, u32::from(point.flags)] {
                hash(value);
            }
        }
        let segments = glyph.segments()?;
        for &word in bytemuck::cast_slice::<_, u32>(&segments) {
            hash(word);
        }
    }
    Ok(black_box(checksum))
}
fn main() -> Result<(), Box<dyn Error>> {
    let path = std::env::args()
        .nth(1)
        .ok_or("usage: outline_benchmark FONT_PATH [FACE_INDEX] [--expanded]")?;
    let index = std::env::args()
        .nth(2)
        .map(|s| s.parse())
        .transpose()?
        .unwrap_or(0);
    let face = SfntFace::new(std::fs::read(path)?, index)?;
    let expanded = match std::env::args().nth(3).as_deref() {
        None => false,
        Some("--expanded") => true,
        Some(_) => return Err("unknown benchmark mode".into()),
    };
    let mut glyphs = Vec::new();
    let (mut points, mut segments) = (0u64, 0u64);
    for id in 0..face.glyph_count().ok_or("missing glyph count")? {
        if expanded {
            let r = face.expanded_requirements(id)?;
            glyphs.push(id);
            points += u64::from(r.point_count);
            segments += u64::from(r.path_segment_count);
            continue;
        }
        let r = face.glyph_requirements(id)?;
        if r.kind == GlyphKind::Simple {
            glyphs.push(id);
            points += u64::from(r.point_count);
            segments += u64::from(r.path_segment_count);
        }
    }
    if glyphs.is_empty() {
        return Err("no simple glyphs".into());
    }
    let checksum = run(&face, &glyphs, expanded)?;
    for _ in 0..5 {
        assert_eq!(run(&face, &glyphs, expanded)?, checksum);
    }
    let mut times = Vec::new();
    for _ in 0..40 {
        let start = Instant::now();
        let actual = run(&face, &glyphs, expanded)?;
        times.push(start.elapsed().as_secs_f64() * 1e6);
        assert_eq!(actual, checksum);
    }
    times.sort_by(f64::total_cmp);
    println!(
        "mode={} glyphs={} points={} segments={} checksum={} round_us_p50={:.3} p95={:.3} p99={:.3}",
        if expanded { "expanded" } else { "simple" },
        glyphs.len(),
        points,
        segments,
        checksum,
        times[19],
        times[37],
        times[39]
    );
    Ok(())
}
