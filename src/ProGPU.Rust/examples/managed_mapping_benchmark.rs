//! Actual raw/instance/selector mapping outputs with matching prepared source calls.
use progpu::font::ManagedFont;
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/managed_mapping_fixture.rs"]
mod fixture;
#[path = "../tests/support/managed_mapping_replay.rs"]
mod replay;
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let variant: usize = args[1].parse().unwrap();
    let kind: u32 = args[2].parse().unwrap();
    let count: usize = args[3].parse().unwrap();
    let queries = (0..count)
        .map(|n| {
            let code = if count == 128 {
                n as u32
            } else {
                [
                    0x20,
                    0x41,
                    0x42,
                    0x621,
                    0x64b,
                    0x200d,
                    0xfe81,
                    0xf041,
                    0xffff,
                    0x1f600 + (n as u32 % 64),
                ][n % 10]
            };
            [
                kind,
                code,
                if variant == 5 {
                    0xfe00 + n as u32 % 64
                } else {
                    0xfe0f
                },
            ]
        })
        .collect();
    let case = replay::Case {
        font: fixture::benchmark_source(variant),
        queries,
    };
    if args.get(4).is_some_and(|x| x == "input") {
        let mut out = Vec::new();
        case.write(&mut out);
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let font = ManagedFont::new(case.font.clone(), 0).unwrap();
    let mut results = vec![[0; 3]; case.queries.len()];
    let mut times = Vec::new();
    let mut first = 0.;
    for n in 0..45 {
        let start = Instant::now();
        replay::execute(
            black_box(&font),
            black_box(&case.queries),
            black_box(&mut results),
        );
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        if n == 0 {
            first = elapsed;
        }
        if n >= 5 {
            times.push(elapsed);
        }
    }
    times.sort_by(f64::total_cmp);
    eprintln!(
        "first_us={first:.3} execute_p50_us={:.3} execute_p95_us={:.3} execute_p99_us={:.3}",
        times[19], times[37], times[39]
    );
    let mut out = Vec::new();
    replay::emit(&mut out, 0);
    for r in results {
        for v in r {
            replay::emit(&mut out, v);
        }
    }
    io::stdout().write_all(&out).unwrap();
}
