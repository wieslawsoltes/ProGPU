//! Matched original grapheme workloads using reused input and output storage.
use progpu::text::unicode::{DecodedText, grapheme::*};
use std::{error::Error, hint::black_box, time::Instant};
fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 3 {
        return Err(
            "usage: grapheme_benchmark ascii|mixed|emoji clusters|managed|starts REPEATS".into(),
        );
    }
    let pattern = match args[0].as_str() {
        "ascii" => "ProGPU sample 123 ",
        "mixed" => "A\u{301} العربية 日本語\r\n क्\u{915} ",
        "emoji" => "🇵🇱👩\u{301}\u{200d}👧👍🏽🇦 \u{1100}\u{1161}\u{11a8} ",
        _ => return Err("invalid pattern".into()),
    };
    let mode = args[1].as_str();
    if !["clusters", "managed", "starts"].contains(&mode) {
        return Err("invalid mode".into());
    }
    let repeats: usize = args[2].parse()?;
    if !(1..=4096).contains(&repeats) {
        return Err("repeats must be 1..4096".into());
    }
    let source: Vec<_> = pattern.repeat(repeats).encode_utf16().collect();
    let text = DecodedText::from_utf16(source.clone())?;
    let policy = if mode == "managed" {
        GraphemePolicy::ManagedCompatible
    } else {
        GraphemePolicy::Unicode17
    };
    let count = cluster_count(text.scalars(), policy)?;
    let mut output = vec![GraphemeCluster::default(); count as usize];
    let mut starts = vec![0; source.len()];
    let mut execute = || -> Result<u64, Box<dyn Error>> {
        let mut hash = 14695981039346656037u64;
        let mut add = |v: u32| hash = (hash ^ u64::from(v)).wrapping_mul(1099511628211);
        if mode == "starts" {
            utf16_starts_into(black_box(&source), &mut starts)?;
            for &v in &starts {
                add(u32::from(v));
            }
        } else {
            let written = segment_into(black_box(text.scalars()), policy, &mut output)?;
            assert_eq!(written, count);
            for c in &output {
                for v in [
                    c.input_index,
                    c.input_length,
                    c.scalar_index,
                    c.scalar_count,
                ] {
                    add(v);
                }
            }
        }
        Ok(black_box(hash))
    };
    let checksum = execute()?;
    for _ in 0..5 {
        assert_eq!(execute()?, checksum);
    }
    let mut times = Vec::with_capacity(40);
    for _ in 0..40 {
        let start = Instant::now();
        let actual = execute()?;
        times.push(start.elapsed().as_secs_f64() * 1e6);
        assert_eq!(actual, checksum);
    }
    times.sort_by(f64::total_cmp);
    println!(
        "units={} scalars={} clusters={count} scratch_bytes=0 checksum={checksum} round_us_p50={:.3} p95={:.3} p99={:.3}",
        source.len(),
        text.scalars().len(),
        times[19],
        times[37],
        times[39]
    );
    Ok(())
}
