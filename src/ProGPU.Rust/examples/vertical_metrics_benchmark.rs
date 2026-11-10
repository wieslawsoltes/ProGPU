//! Original static vertical origins and prepared neutral VVAR query baseline.
use progpu::font::{SfntFace, VerticalMetricsInstance};
use std::{error::Error, hint::black_box, time::Instant};

fn run(instance: &VerticalMetricsInstance, count: u16) -> Result<u64, Box<dyn Error>> {
    let mut checksum = 14695981039346656037u64;
    let mut hash = |word: u32| {
        checksum = (checksum ^ u64::from(word)).wrapping_mul(1099511628211);
    };
    for glyph in 0..count {
        let glyph = black_box(glyph);
        let face = instance.source();
        for value in [
            face.design_advance_height(glyph),
            face.design_vertical_origin_y(glyph),
        ] {
            hash(u32::from(value.is_some()));
            hash(value.unwrap_or(0) as u32);
        }
        let delta = instance.variation(glyph)?;
        for value in [
            delta.advance_height,
            delta.top_side_bearing.unwrap_or(0.0),
            delta.bottom_side_bearing.unwrap_or(0.0),
            delta.vertical_origin_y.unwrap_or(0.0),
        ] {
            hash(value.to_bits());
        }
        for flag in [
            delta.uses_vvar,
            delta.top_side_bearing.is_some(),
            delta.bottom_side_bearing.is_some(),
            delta.vertical_origin_y.is_some(),
        ] {
            hash(u32::from(flag));
        }
    }
    Ok(black_box(checksum))
}
fn main() -> Result<(), Box<dyn Error>> {
    let path = std::env::args()
        .nth(1)
        .ok_or("usage: vertical_metrics_benchmark FONT [FACE_INDEX] [NORMALIZED_COORDINATE]")?;
    let face_index = std::env::args()
        .nth(2)
        .map(|s| s.parse())
        .transpose()?
        .unwrap_or(0);
    let coordinate: i16 = std::env::args()
        .nth(3)
        .map(|s| s.parse())
        .transpose()?
        .unwrap_or(8192);
    let face = SfntFace::new(std::fs::read(path)?, face_index)?;
    let count = face.glyph_count().ok_or("missing glyph count")?;
    if count == 0 {
        return Err("no glyphs".into());
    }
    let coordinates = vec![coordinate; usize::from(face.variation_axis_count()?)];
    let instance = face.prepare_vertical_metrics_variation(&coordinates)?;
    // Verify the independent batch-publication route before timing single queries.
    let glyphs: Vec<_> = (0..count).collect();
    let mut results = vec![progpu::font::VerticalMetricsVariation::default(); usize::from(count)];
    instance.variations_into(&glyphs, &mut results)?;
    for (glyph, expected) in glyphs.into_iter().zip(results) {
        assert_eq!(instance.variation(glyph)?, expected);
    }
    let checksum = run(&instance, count)?;
    for _ in 0..5 {
        assert_eq!(run(&instance, count)?, checksum);
    }
    if let Some(rounds) = std::env::var_os("PROGPU_RUST_PROFILE_ROUNDS") {
        let rounds: usize = rounds
            .to_str()
            .ok_or("non-Unicode profile rounds")?
            .parse()?;
        for _ in 0..rounds {
            assert_eq!(run(&instance, count)?, checksum);
        }
        println!("profile_rounds={rounds} checksum={checksum}");
        return Ok(());
    }
    let mut times = Vec::with_capacity(40);
    for _ in 0..40 {
        let start = Instant::now();
        let actual = run(&instance, count)?;
        times.push(start.elapsed().as_secs_f64() * 1e6);
        assert_eq!(actual, checksum);
    }
    times.sort_by(f64::total_cmp);
    println!(
        "glyphs={count} axes={} regions={} vvar={} checksum={checksum} round_us_p50={:.3} p95={:.3} p99={:.3}",
        coordinates.len(),
        instance.region_scalars().len(),
        instance.uses_vvar(),
        times[19],
        times[37],
        times[39]
    );
    Ok(())
}
