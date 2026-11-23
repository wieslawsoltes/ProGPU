//! Matched original strict decoding and script itemization, with reused outputs.
use progpu::text::unicode::*;
use std::{error::Error, hint::black_box, time::Instant};
fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 3 {
        return Err("usage: unicode_benchmark ascii|mixed 8|16 REPEATS".into());
    }
    let pattern = match args[0].as_str() {
        "ascii" => "ProGPU sample 123 ",
        "mixed" => " A\u{301} العربية 日本語 😀 \u{10ffff}",
        _ => return Err("expected ascii or mixed".into()),
    };
    let wide = match args[1].as_str() {
        "8" => false,
        "16" => true,
        _ => return Err("expected 8 or 16".into()),
    };
    let repeats: usize = args[2].parse()?;
    if repeats == 0 || repeats > 4096 {
        return Err("repeats must be 1..4096".into());
    }
    let text = pattern.repeat(repeats);
    let utf16: Vec<_> = text.encode_utf16().collect();
    let count = if wide {
        utf16_scalar_count(&utf16)?
    } else {
        utf8_scalar_count(text.as_bytes())?
    };
    let mut scalars = vec![UnicodeScalar::default(); count as usize];
    let mut runs = vec![ScriptRun::default(); count as usize];
    let mut execute = || -> Result<(u64, u32), UnicodeError> {
        let written = if wide {
            decode_utf16_into(black_box(&utf16), &mut scalars)?
        } else {
            decode_utf8_into(black_box(text.as_bytes()), &mut scalars)?
        };
        assert_eq!(written, count);
        let run_count = itemize_scripts_into(&scalars, &mut runs)?;
        let mut hash = 14695981039346656037u64;
        let mut add = |v: u32| {
            hash = (hash ^ u64::from(v)).wrapping_mul(1099511628211);
        };
        for s in &scalars {
            for v in [
                s.code_point,
                s.input_index,
                u32::from(s.input_length),
                u32::from(s.canonical_combining_class),
                u32::from(s.reserved),
                s.script.0,
            ] {
                add(v);
            }
        }
        for r in &runs[..run_count as usize] {
            for v in [
                r.scalar_start,
                r.scalar_count,
                r.input_start,
                r.input_length,
                r.script.0,
            ] {
                add(v);
            }
        }
        Ok((black_box(hash), run_count))
    };
    let (checksum, run_count) = execute()?;
    for _ in 0..5 {
        assert_eq!(execute()?, (checksum, run_count));
    }
    if let Some(rounds) = std::env::var_os("PROGPU_RUST_PROFILE_ROUNDS") {
        let rounds: usize = rounds.to_str().ok_or("invalid rounds")?.parse()?;
        for _ in 0..rounds {
            assert_eq!(execute()?, (checksum, run_count));
        }
        println!("profile_rounds={rounds} checksum={checksum}");
        return Ok(());
    }
    let mut times = Vec::with_capacity(40);
    for _ in 0..40 {
        let start = Instant::now();
        let actual = execute()?;
        times.push(start.elapsed().as_secs_f64() * 1e6);
        assert_eq!(actual, (checksum, run_count));
    }
    times.sort_by(f64::total_cmp);
    println!(
        "units={} scalars={count} runs={run_count} checksum={checksum} round_us_p50={:.3} p95={:.3} p99={:.3}",
        if wide { utf16.len() } else { text.len() },
        times[19],
        times[37],
        times[39]
    );
    Ok(())
}
