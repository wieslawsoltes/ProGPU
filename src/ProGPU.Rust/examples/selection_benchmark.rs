//! Original native lookup selection baseline, with reused output storage.
use progpu::font::{
    Tag,
    layout::{LayoutTable, SelectionRequest},
};
use std::{hint::black_box, time::Instant};
fn main() {
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 2);
    let data = std::fs::read(&args[1]).unwrap();
    let table = LayoutTable::parse(&data).unwrap();
    let at = table.feature_list_offset();
    let count = usize::from(u16::from_be_bytes(data[at..at + 2].try_into().unwrap()));
    let features: Vec<_> = (0..count)
        .map(|i| {
            Tag(u32::from_be_bytes(
                data[at + 2 + i * 6..at + 6 + i * 6].try_into().unwrap(),
            ))
        })
        .collect();
    let request = SelectionRequest {
        features: &features,
        coordinates: &[0],
        ..Default::default()
    };
    let mut output = vec![0; table.selection_capacity(request).unwrap() as usize];
    let mut times = vec![];
    let mut identity = None;
    for round in 0..45 {
        let start = Instant::now();
        let written = table
            .select_lookups_into(black_box(request), &mut output)
            .unwrap() as usize;
        black_box(&output);
        let us = start.elapsed().as_secs_f64() * 1e6;
        let hash = output[..written].iter().fold(2166136261u32, |h, &v| {
            (h ^ u32::from(v)).wrapping_mul(16777619)
        });
        if let Some(old) = identity {
            assert_eq!(old, (written, hash));
        }
        identity = Some((written, hash));
        if round >= 5 {
            times.push(us);
        }
    }
    times.sort_by(f64::total_cmp);
    let (count, hash) = identity.unwrap();
    println!(
        "lookups={count} checksum={hash} p50_us={:.3} p95_us={:.3} p99_us={:.3}",
        times[19], times[37], times[39]
    );
}
