//! Matched original managed first-cluster traversal, including malformed UTF-16.
use progpu::text::unicode::grapheme::{ManagedGraphemeProfile, managed_next_grapheme_length};
use std::{
    hint::black_box,
    io::{Read, Write},
    time::Instant,
};
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut input = Vec::new();
    let profile = std::env::args().nth(1).filter(|value| value == "--profile");
    if profile.is_some() {
        input = std::fs::read(std::env::args().nth(2).ok_or("profile input path")?)?;
    } else {
        std::io::stdin().read_to_end(&mut input)?;
    }
    let mut cursor = 0;
    let mut writer = std::io::BufWriter::new(std::io::stdout().lock());
    while cursor < input.len() {
        let count = u32::from_le_bytes(input[cursor..cursor + 4].try_into()?) as usize;
        cursor += 4;
        let units: Vec<_> = input[cursor..cursor + count * 2]
            .chunks_exact(2)
            .map(|v| u16::from_le_bytes(v.try_into().unwrap()))
            .collect();
        cursor += count * 2;
        let mut result = Vec::with_capacity(count + 1);
        let rounds = if profile.is_some() { 100_000 } else { 45 };
        let mut times = Vec::with_capacity(rounds - 5);
        let mut first = 0.0;
        for round in 0..rounds {
            let started = Instant::now();
            result.clear();
            result.push(0u32);
            let mut index = 0;
            while index < units.len() {
                index += managed_next_grapheme_length(
                    black_box(&units[index..]),
                    ManagedGraphemeProfile::Unicode16_0,
                );
                result.push(index as u32);
            }
            black_box(&result);
            let elapsed = started.elapsed().as_secs_f64() * 1e6;
            if round == 0 {
                first = elapsed;
            }
            if round >= 5 {
                times.push(elapsed);
            }
        }
        times.sort_by(f64::total_cmp);
        eprintln!(
            "first_us={first:.3} p50_us={:.3} p95_us={:.3} p99_us={:.3}",
            times[19], times[37], times[39]
        );
        writer.write_all(&(result.len() as u32).to_le_bytes())?;
        for boundary in result {
            writer.write_all(&boundary.to_le_bytes())?;
        }
    }
    writer.flush()?;
    Ok(())
}
