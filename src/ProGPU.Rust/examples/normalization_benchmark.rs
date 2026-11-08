//! Original canonical normalization with reused input/output storage.
use progpu::text::unicode::{DecodedText, normalization::*};
use std::{error::Error, hint::black_box, time::Instant};
fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 3 {
        return Err(
            "usage: normalization_benchmark ascii|composed|reordered nfd|nfc REPEATS".into(),
        );
    }
    let pattern = match args[0].as_str() {
        "ascii" => "ProGPU sample 123 ",
        "composed" => "Åé각𝅗𝅥 日本語 ",
        "reordered" => "A\u{315}\u{301}\u{327} क\u{93c} \u{1100}\u{1161}\u{11a8} ",
        _ => return Err("invalid kind".into()),
    };
    let form = match args[1].as_str() {
        "nfd" => NormalizationForm::CanonicalDecomposition,
        "nfc" => NormalizationForm::CanonicalComposition,
        _ => return Err("invalid form".into()),
    };
    let repeats: usize = args[2].parse()?;
    if !(1..=4096).contains(&repeats) {
        return Err("repeats must be 1..4096".into());
    }
    let units: Vec<_> = pattern.repeat(repeats).encode_utf16().collect();
    let unit_count = units.len();
    let text = DecodedText::from_utf16(units)?;
    let data = default_data()?;
    let mut output = vec![Default::default(); scalar_capacity(text.scalars(), data)? as usize];
    let mut execute = || -> Result<(u64, u32), Box<dyn Error>> {
        let count = normalize_into(black_box(text.scalars()), data, form, &mut output)?;
        let mut hash = 14695981039346656037u64;
        for s in &output[..count as usize] {
            hash = (hash ^ u64::from(s.code_point)).wrapping_mul(1099511628211);
        }
        Ok(black_box((hash, count)))
    };
    let identity = execute()?;
    for _ in 0..5 {
        assert_eq!(execute()?, identity);
    }
    let mut times = Vec::with_capacity(40);
    for _ in 0..40 {
        let start = Instant::now();
        let actual = execute()?;
        times.push(start.elapsed().as_secs_f64() * 1e6);
        assert_eq!(actual, identity);
    }
    times.sort_by(f64::total_cmp);
    println!(
        "units={unit_count} scalars={} output={} buffer_bytes={} checksum={} round_us_p50={:.3} p95={:.3} p99={:.3}",
        text.scalars().len(),
        identity.1,
        output.len() * 16,
        identity.0,
        times[19],
        times[37],
        times[39]
    );
    Ok(())
}
