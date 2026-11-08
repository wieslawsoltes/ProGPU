//! Original cmap + horizontal metric workload; no shaping or rasterization.
//! Run in Release with a font path and optional collection face index.
use progpu::font::SfntFace;
use std::{error::Error, hint::black_box, sync::Arc, time::Instant};

fn query(face: &SfntFace) -> u64 {
    let mut checksum = 0u64;
    for index in 0..66_560u32 {
        let code = if index < 65_536 {
            index
        } else {
            0x1f600 + index - 65_536
        };
        if let Some(glyph) = face.glyph_index(black_box(code)) {
            checksum = checksum.wrapping_add(u64::from(glyph) + (1u64 << 48));
            if let Some(metrics) = face.horizontal_glyph_metrics(glyph) {
                checksum = checksum.wrapping_add(
                    (u64::from(metrics.advance_width) << 16)
                        + (u64::from(metrics.left_side_bearing as u16) << 32),
                );
            }
        }
    }
    black_box(checksum)
}

fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.is_empty() || args.len() > 2 {
        return Err("usage: font_benchmark FONT_PATH [FACE_INDEX]".into());
    }
    let index = args.get(1).map_or(Ok(0), |s| s.parse::<u32>())?;
    let data: Arc<[u8]> = std::fs::read(&args[0])?.into();
    let start = Instant::now();
    let face = SfntFace::new(data.clone(), index)?;
    let first_parse_us = start.elapsed().as_secs_f64() * 1e6;
    let expected = query(&face);
    for _ in 0..5 {
        assert_eq!(query(&face), expected);
    }
    let mut samples = Vec::new();
    for _ in 0..40 {
        let start = Instant::now();
        assert_eq!(query(&face), expected);
        samples.push(start.elapsed().as_secs_f64() * 1e6);
    }
    samples.sort_by(f64::total_cmp);
    println!(
        "bytes={} face={} tables={} first_parse_us={first_parse_us:.3} checksum={expected}",
        data.len(),
        index,
        face.table_count()
    );
    println!(
        "queries_per_round=66560 warmup=5 rounds=40 round_us p50={:.3} p95={:.3} p99={:.3}",
        samples[20], samples[38], samples[39]
    );
    Ok(())
}
