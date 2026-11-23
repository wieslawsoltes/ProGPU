//! Matched immutable property requests, indirect typed calls and reused outputs.
use progpu::text::unicode::{
    ManagedIndicProperties, managed_indic_properties, managed_use_category,
};
use std::{hint::black_box, io::Read, time::Instant};

fn main() -> std::io::Result<()> {
    let mut bytes = Vec::new();
    std::io::stdin().read_to_end(&mut bytes)?;
    assert_eq!(bytes.len() % 4, 0);
    let mut words = bytes
        .chunks_exact(4)
        .map(|v| u32::from_le_bytes(v.try_into().unwrap()));
    let count = words.next().unwrap();
    for _ in 0..count {
        let mode = words.next().unwrap();
        let repetitions = words.next().unwrap();
        let size = words.next().unwrap() as usize;
        let points: Vec<_> = words.by_ref().take(size).collect();
        assert_eq!(points.len(), size);
        assert!(size > 0 && repetitions > 0);
        let query: fn(u32) -> u16 = match mode {
            0 => |p| ManagedIndicProperties::for_raw_code_point(p).bits(),
            1 => |p| managed_indic_properties(p).bits(),
            2 => |p| u16::from(managed_use_category(p)),
            _ => panic!("unknown query"),
        };
        let query = black_box(query);
        let mut values = vec![0; size];
        let mut samples = [0.0; 40];
        for round in 0..45 {
            let start = Instant::now();
            for _ in 0..repetitions {
                for (point, slot) in black_box(&points).iter().zip(&mut values) {
                    *slot = query(*point);
                }
                black_box(&values);
            }
            let elapsed = start.elapsed();
            if round >= 5 {
                samples[round - 5] =
                    elapsed.as_secs_f64() * 1e9 / f64::from(repetitions) / size as f64;
            }
        }
        samples.sort_by(f64::total_cmp);
        println!(
            "{{\"p50_ns\":{},\"p95_ns\":{},\"p99_ns\":{},\"values\":{:?}}}",
            samples[19], samples[37], samples[39], values
        );
    }
    assert!(words.next().is_none());
    Ok(())
}
