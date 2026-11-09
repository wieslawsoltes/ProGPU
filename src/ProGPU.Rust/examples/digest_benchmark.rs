//! Matched original negative-only filter queries; no shaping-performance claim.
use progpu::font::layout::LayoutTable;
use std::{hint::black_box, time::Instant};
fn measure(query: impl Fn(u16) -> bool, output: &mut [u8]) {
    let mut times = vec![];
    for round in 0..45 {
        let query = black_box(&query);
        let start = Instant::now();
        for (i, slot) in output.iter_mut().enumerate() {
            *slot = u8::from(query((i as u16).wrapping_mul(257)));
        }
        let us = start.elapsed().as_secs_f64() * 1e6;
        black_box(&output);
        if round >= 5 {
            times.push(us);
        }
    }
    let hash = output.iter().fold(2166136261u32, |h, &v| {
        (h ^ u32::from(v)).wrapping_mul(16777619)
    });
    times.sort_by(f64::total_cmp);
    println!(
        "queries={} checksum={} p50_us={:.3} p95_us={:.3} p99_us={:.3}",
        output.len(),
        hash,
        times[19],
        times[37],
        times[39]
    );
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 3);
    let bytes = std::fs::read(&args[1]).unwrap();
    let mode: u8 = args[2].parse().unwrap();
    let start = Instant::now();
    let table = LayoutTable::parse(&bytes).unwrap();
    let digest = table.lookup_digest(0, 7).unwrap().unwrap();
    let preparation = start.elapsed().as_secs_f64() * 1e6;
    let mut output = vec![0; 65536];
    print!("prepare_us={preparation:.3} ");
    match mode {
        0 => measure(|g| digest.may_have(g), &mut output),
        1 => {
            let d = digest.managed();
            measure(|g| d.may_have(g), &mut output);
        }
        _ => panic!("unknown digest policy"),
    }
}
