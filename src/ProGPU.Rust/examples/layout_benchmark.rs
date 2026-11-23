//! Original prevalidated common-table queries; run with the baseline driver.
use progpu::font::layout::{ClassDefinition, Coverage};
use std::{hint::black_box, time::Instant};
fn main() {
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 3);
    let kind: u8 = args[1].parse().unwrap();
    let bytes = std::fs::read(&args[2]).unwrap();
    let coverage = if kind == 0 {
        Some(Coverage::parse(&bytes, 0).unwrap())
    } else {
        None
    };
    let classes = if kind == 1 {
        Some(ClassDefinition::parse(&bytes, 0).unwrap())
    } else {
        None
    };
    let glyphs: Vec<_> = (0..65536u32)
        .map(|i| i.wrapping_mul(25173).wrapping_add(13849) as u16)
        .collect();
    let mut output = vec![0u32; glyphs.len()];
    let mut times = vec![];
    let mut checksum = 0;
    for round in 0..45 {
        let start = Instant::now();
        if let Some(v) = coverage {
            for (&g, o) in glyphs.iter().zip(&mut output) {
                *o = v.find(black_box(g)).map_or(u32::MAX, u32::from);
            }
        } else {
            let v = classes.unwrap();
            for (&g, o) in glyphs.iter().zip(&mut output) {
                *o = u32::from(v.get(black_box(g)));
            }
        }
        black_box(&output);
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        let hash = output.iter().fold(1469598103934665603u64, |h, &v| {
            (h ^ u64::from(v)).wrapping_mul(1099511628211)
        });
        if round != 0 {
            assert_eq!(checksum, hash);
        }
        checksum = hash;
        if round >= 5 {
            times.push(elapsed);
        }
    }
    times.sort_by(f64::total_cmp);
    println!(
        "queries=65536 checksum={checksum} min_us={:.3} p50_us={:.3} p95_us={:.3} max_us={:.3}",
        times[0], times[20], times[37], times[39]
    );
}
