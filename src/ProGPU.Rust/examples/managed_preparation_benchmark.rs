//! Actual owned preparation creation/retirement; complete output checked separately.
use progpu::{
    font::{ManagedFont, layout::ManagedGlyphBuffer},
    text::{
        preparation::*,
        unicode::{
            ManagedMarkProfile, grapheme::ManagedGraphemeProfile,
            normalization::ManagedNormalizationProfile,
        },
    },
};
use std::{
    hint::black_box,
    io::{Read, Write},
    sync::Arc,
    time::Instant,
};
#[path = "../tests/support/managed_preparation_replay.rs"]
#[allow(dead_code)]
mod replay;
fn word(input: &mut &[u8]) -> Result<u32, Box<dyn std::error::Error>> {
    let (head, tail) = input.split_at_checked(4).ok_or("missing input word")?;
    *input = tail;
    Ok(u32::from_le_bytes(head.try_into()?))
}
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    let profile = ManagedNormalizationProfile::try_from(
        args.last()
            .ok_or("explicit normalization profile required")?
            .parse::<u16>()?,
    )?;
    let mut writer = std::io::BufWriter::new(std::io::stdout().lock());
    for value in replay::normalization_witness(profile) {
        writer.write_all(&value.to_le_bytes())?;
    }
    if args.first().is_some_and(|s| s == "--witness") {
        writer.flush()?;
        return Ok(());
    }
    let profiling = args.first().is_some_and(|s| s == "--profile");
    let mut input = Vec::new();
    if profiling {
        input = std::fs::read(args.get(1).ok_or("profile input path")?)?;
    } else {
        std::io::stdin().read_to_end(&mut input)?;
    }
    let mut input = input.as_slice();
    let mut fonts = Vec::new();
    for _ in 0..word(&mut input)? {
        let len = word(&mut input)? as usize;
        let (font, tail) = input.split_at_checked(len).ok_or("truncated font")?;
        input = tail;
        fonts.push(ManagedFont::new(font.to_vec(), 0)?);
    }
    let mut response = Vec::new();
    while !input.is_empty() {
        let font = word(&mut input)? as usize;
        let count = word(&mut input)?;
        let request: Vec<_> = (0..count)
            .map(|_| word(&mut input))
            .collect::<Result<_, _>>()?;
        let case = replay::Case::from_request(font, &request).ok_or("invalid request")?;
        let source: Arc<[u16]> = case.text.clone().into();
        let script: Arc<str> = case.script.clone().into();
        let mut options = ManagedPreparationOptions::new(
            profile,
            ManagedMarkProfile::Unicode16_0,
            ManagedGraphemeProfile::Unicode16_0,
        );
        options.cluster_level = ManagedClusterLevel::try_from(case.level)?;
        options.buffer_flags = ManagedBufferFlags::from_bits(case.flags);
        options.pre_context = case.pre.clone().into();
        options.post_context = case.post.clone().into();
        let rounds = if profiling { 100_000 } else { 45 };
        let mut times = Vec::with_capacity(rounds - 5);
        let mut retire = Vec::with_capacity(rounds - 5);
        let mut first = 0.0;
        for round in 0..rounds {
            let started = Instant::now();
            let buffer = ManagedGlyphBuffer::from_text(
                black_box(source.clone()),
                fonts[font].clone(),
                script.clone(),
                options.clone(),
            )?;
            let elapsed = started.elapsed().as_secs_f64() * 1e6;
            black_box(buffer.glyphs());
            let started = Instant::now();
            drop(buffer);
            let disposal = started.elapsed().as_secs_f64() * 1e6;
            if round == 0 {
                first = elapsed;
            }
            if round >= 5 {
                times.push(elapsed);
                retire.push(disposal);
            }
        }
        times.sort_by(f64::total_cmp);
        retire.sort_by(f64::total_cmp);
        let n = times.len();
        eprintln!(
            "first_us={first:.3} p50_us={:.3} p95_us={:.3} p99_us={:.3} retire_p50_us={:.3}",
            times[(n - 1) / 2],
            times[(n - 1) * 95 / 100],
            times[(n * 99).div_ceil(100) - 1],
            retire[(n - 1) / 2]
        );
        replay::execute(Some(&fonts[font]), &case, profile, &mut response);
        for value in &response {
            writer.write_all(&value.to_le_bytes())?;
        }
    }
    writer.flush()?;
    Ok(())
}
