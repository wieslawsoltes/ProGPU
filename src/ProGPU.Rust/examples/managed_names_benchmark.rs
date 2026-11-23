//! Exact-output original managed name-query baselines; construction is untimed.
use progpu::font::SfntFace;
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/names_fixture.rs"]
mod fixture;
#[path = "../tests/support/name_replay.rs"]
mod replay;
use fixture::Record;
#[allow(dead_code)]
#[path = "../tests/support/font_fixture.rs"]
mod font;
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let mode = &args[1];
    assert!(mode == "all" || mode == "first");
    let count: usize = args[2].parse().unwrap();
    let variant: usize = args[3].parse().unwrap();
    let records = (0..count)
        .map(|i| {
            let key = if variant == 2 { i % 8 } else { i };
            let name = format!("  ProGPU name {key:04} \0");
            if variant == 1 {
                let mut bytes = name.into_bytes();
                bytes.extend([0xf0, 0x90, 0x90, 0x80, 0xe2, 0x82, 0]);
                Record::new((i % 4) as u16, (i % 2) as u16, 0x409, 1, bytes)
            } else {
                Record::utf16(
                    3,
                    if i % 2 == 0 { 0x409 } else { 0x411 },
                    1,
                    &name.encode_utf16().collect::<Vec<_>>(),
                )
            }
        })
        .collect::<Vec<_>>();
    let case = replay::Case {
        font: fixture::sfnt(&records),
        ids: vec![1],
        axes: false,
    };
    if args.get(4).is_some_and(|s| s == "input") {
        let mut out = Vec::new();
        case.write(&mut out);
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let face = SfntFace::new(case.font.clone(), 0).unwrap();
    let mut names = face.managed_names(1);
    let mut first = face.managed_name(1);
    let mut times = Vec::new();
    for n in 0..45 {
        let start = Instant::now();
        if mode == "all" {
            names = black_box(face.managed_names(1));
        } else {
            first = black_box(face.managed_name(1));
        }
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        black_box((&names, &first));
        if n >= 5 {
            times.push(elapsed);
        }
    }
    let mut out = Vec::new();
    replay::emit(&mut out, names.len() as u32);
    for name in names {
        replay::text(&mut out, name.encode_utf16());
    }
    replay::emit(&mut out, u32::from(first.is_some()));
    replay::text(&mut out, first.as_deref().unwrap_or("").encode_utf16());
    assert_eq!(out, case.expected().unwrap());
    times.sort_by(f64::total_cmp);
    eprintln!(
        "execute_p50_us={:.3} execute_p95_us={:.3} execute_p99_us={:.3}",
        times[19], times[37], times[39]
    );
    io::stdout().write_all(&out).unwrap();
}
