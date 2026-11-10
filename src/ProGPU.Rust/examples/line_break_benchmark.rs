//! Matched original line-break workloads; decoding/allocation stay outside timing.
use progpu::text::unicode::{DecodedText, line_break::*};
use std::{error::Error, hint::black_box, time::Instant};
fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 3 {
        return Err("usage: line_break_benchmark ascii|mixed|emoji unicode|edit REPEATS".into());
    }
    let pattern = match args[0].as_str() {
        "ascii" => "ProGPU sample ($123.45) - end ",
        "mixed" => "A\u{301} العربية 日本語\r\n क्\u{915} ",
        "emoji" => "🇵🇱👩\u{301}\u{200d}👧👍🏽🇦 \u{1100}\u{1161}\u{11a8} ",
        _ => return Err("invalid pattern".into()),
    };
    let policy = match args[1].as_str() {
        "unicode" => LineBreakPolicy::Unicode17,
        "edit" => LineBreakPolicy::EditSelection,
        _ => return Err("invalid mode".into()),
    };
    let repeats: usize = args[2].parse()?;
    if !(1..=4096).contains(&repeats) {
        return Err("repeats must be 1..4096".into());
    }
    let source: Vec<_> = pattern.repeat(repeats).encode_utf16().collect();
    let units = source.len();
    let text = DecodedText::from_utf16(source)?;
    let n = text.scalars().len();
    let mut classes = vec![LineBreakClass::default(); n];
    let mut breaks = vec![LineBreakKind::default(); n];
    let mut execute = || -> Result<u64, Box<dyn Error>> {
        resolve_into(black_box(text.scalars()), policy, &mut classes, &mut breaks)?;
        let mut hash = 14695981039346656037u64;
        for (&c, &b) in classes.iter().zip(&breaks) {
            for v in [c as u8, b as u8] {
                hash = (hash ^ u64::from(v)).wrapping_mul(1099511628211);
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
        "units={units} scalars={n} scratch_bytes={n} checksum={checksum} round_us_p50={:.3} p95={:.3} p99={:.3}",
        times[19], times[37], times[39]
    );
    Ok(())
}
