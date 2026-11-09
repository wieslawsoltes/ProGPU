//! Full original script action timings, independently complete-output validated.
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
#[path = "../tests/support/managed_script_actions_replay.rs"]
#[allow(dead_code)]
mod replay;
fn word(input: &mut &[u8]) -> Result<u32, Box<dyn std::error::Error>> {
    let (head, tail) = input.split_at_checked(4).ok_or("missing word")?;
    *input = tail;
    Ok(u32::from_le_bytes(head.try_into()?))
}
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    let profile = ManagedNormalizationProfile::try_from(
        args.last().ok_or("explicit profile")?.parse::<u16>()?,
    )?;
    let mut writer = std::io::BufWriter::new(std::io::stdout().lock());
    for v in replay::preparation::normalization_witness(profile) {
        writer.write_all(&v.to_le_bytes())?;
    }
    if args.first().is_some_and(|s| s == "--witness") {
        writer.flush()?;
        return Ok(());
    }
    let profiling = args.first().is_some_and(|s| s == "--profile");
    let mut data = Vec::new();
    if profiling {
        data = std::fs::read(args.get(1).ok_or("input path")?)?;
    } else {
        std::io::stdin().read_to_end(&mut data)?;
    }
    let mut input = data.as_slice();
    let mut fonts = Vec::new();
    for _ in 0..word(&mut input)? {
        let len = word(&mut input)? as usize;
        let (bytes, tail) = input.split_at_checked(len).ok_or("truncated font")?;
        input = tail;
        fonts.push(ManagedFont::new(bytes.to_vec(), 0)?);
    }
    let mut response = Vec::new();
    while !input.is_empty() {
        let font = word(&mut input)? as usize;
        let count = word(&mut input)?;
        let request: Vec<_> = (0..count)
            .map(|_| word(&mut input))
            .collect::<Result<_, _>>()?;
        let case = replay::Case::from_request(font, &request).ok_or("invalid request")?;
        let source: Arc<[u16]> = case.source.text.clone().into();
        let script: Arc<str> = case.source.script.clone().into();
        let mut options = ManagedPreparationOptions::new(
            profile,
            ManagedMarkProfile::Unicode16_0,
            ManagedGraphemeProfile::Unicode16_0,
        );
        options.cluster_level = ManagedClusterLevel::try_from(case.source.level)?;
        options.buffer_flags = ManagedBufferFlags::from_bits(case.source.flags);
        options.pre_context = case.source.pre.clone().into();
        options.post_context = case.source.post.clone().into();
        let actions: Vec<_> = case
            .actions
            .iter()
            .map(|&code| replay::action(code).ok_or("invalid action"))
            .collect::<Result<_, _>>()?;
        let rounds = if profiling { 100000 } else { 45 };
        let mut times = Vec::with_capacity(rounds - 5);
        let mut first = 0.0;
        for round in 0..rounds {
            let mut buffer = ManagedGlyphBuffer::from_text(
                source.clone(),
                fonts[font].clone(),
                script.clone(),
                options.clone(),
            )?;
            if let Some(raw) = &case.raw {
                buffer.glyphs_mut().copy_from_slice(raw);
            }
            let started = Instant::now();
            for &action in &actions {
                buffer.apply_script_action(black_box(action))?;
            }
            let elapsed = started.elapsed().as_secs_f64() * 1e6;
            black_box(buffer.glyphs());
            if round == 0 {
                first = elapsed;
            }
            if round >= 5 {
                times.push(elapsed);
            }
            drop(buffer);
        }
        times.sort_by(f64::total_cmp);
        let n = times.len();
        eprintln!(
            "first_us={first:.3} p50_us={:.3} p95_us={:.3} p99_us={:.3}",
            times[(n - 1) / 2],
            times[(n - 1) * 95 / 100],
            times[(n * 99).div_ceil(100) - 1]
        );
        replay::execute(Some(&fonts[font]), &case, profile, &mut response);
        for value in &response {
            writer.write_all(&value.to_le_bytes())?;
        }
    }
    writer.flush()?;
    Ok(())
}
