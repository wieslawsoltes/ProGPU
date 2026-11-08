//! Matched original bidi-level workload with reused source, output and scratch.
use progpu::text::unicode::{
    DecodedText,
    bidi::{BidiLevel, BidiWorkspace, requirements},
};
use std::{error::Error, hint::black_box, time::Instant};
fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 2 {
        return Err("usage: bidi_benchmark ascii|mixed|isolates REPEATS".into());
    }
    let pattern = match args[0].as_str() {
        "ascii" => "ProGPU sample 123 ",
        "mixed" => "abc אבג (123) العربية A\u{301} 日本語 😀 ",
        "isolates" => "A\u{2068}אב \u{2066}abc (123)\u{2069} ד\u{2069} z ",
        _ => return Err("invalid pattern".into()),
    };
    let repeats: usize = args[1].parse()?;
    if repeats == 0 || repeats > 4096 {
        return Err("repeats must be 1..4096".into());
    }
    let source: Vec<_> = pattern.repeat(repeats).encode_utf16().collect();
    let units = source.len();
    let text = DecodedText::from_utf16(source)?;
    let count = text.scalars().len();
    let mut workspace = BidiWorkspace::new(count)?;
    let mut output = vec![BidiLevel::default(); count];
    let mut execute = || -> Result<u64, Box<dyn Error>> {
        let result = workspace.resolve_into(black_box(text.scalars()), 2, &mut output)?;
        assert_eq!(result.written as usize, count);
        let mut hash = 14695981039346656037u64;
        let mut add = |v: u32| {
            hash = (hash ^ u64::from(v)).wrapping_mul(1099511628211);
        };
        add(result.paragraph_level as u32);
        for level in &output {
            for v in [
                level.input_index,
                u32::from(level.input_length),
                level.level as u32,
                u32::from(level.reserved),
            ] {
                add(v);
            }
        }
        Ok(black_box(hash))
    };
    let checksum = execute()?;
    for _ in 0..5 {
        assert_eq!(execute()?, checksum);
    }
    if let Some(rounds) = std::env::var_os("PROGPU_RUST_PROFILE_ROUNDS") {
        let rounds: usize = rounds.to_str().ok_or("invalid rounds")?.parse()?;
        for _ in 0..rounds {
            assert_eq!(execute()?, checksum);
        }
        println!("profile_rounds={rounds} checksum={checksum}");
        return Ok(());
    }
    let mut times = Vec::with_capacity(40);
    for _ in 0..40 {
        let start = Instant::now();
        let actual = execute()?;
        times.push(start.elapsed().as_secs_f64() * 1e6);
        assert_eq!(actual, checksum);
    }
    times.sort_by(f64::total_cmp);
    let r = requirements(count)?;
    let scratch_bytes = r.unit_count as usize
        * std::mem::size_of::<progpu::text::unicode::bidi::BidiUnit>()
        + r.index_count as usize * 4
        + r.run_count as usize * std::mem::size_of::<progpu::text::unicode::bidi::BidiLevelRun>()
        + r.bracket_pair_count as usize * 8;
    println!(
        "units={units} scalars={count} scratch_bytes={scratch_bytes} checksum={checksum} round_us_p50={:.3} p95={:.3} p99={:.3}",
        times[19], times[37], times[39]
    );
    Ok(())
}
