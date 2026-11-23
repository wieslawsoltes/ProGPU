//! Current managed feature-policy construction baseline, not full shaping.
use progpu::font::layout::{
    ManagedFeatureSetting, ManagedLookupSelection, ManagedSelectionRequest,
};
use std::{hint::black_box, sync::Arc, time::Instant};
fn main() {
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 2);
    let data: Arc<[u8]> = std::fs::read(&args[1]).unwrap().into();
    let at = usize::from(u16::from_be_bytes([data[6], data[7]]));
    let count = usize::from(u16::from_be_bytes([data[at], data[at + 1]]));
    let mut settings: Vec<_> = (0..count)
        .map(|i| {
            ManagedFeatureSetting::new(
                std::array::from_fn(|j| u16::from(data[at + 2 + i * 6 + j])),
                (i % 7) as i32 - 3,
            )
            .unwrap()
        })
        .collect();
    settings.push(ManagedFeatureSetting::new(settings[0].tag(), -7).unwrap());
    let request = ManagedSelectionRequest {
        settings: &settings,
        ..Default::default()
    };
    let mut times = vec![];
    let mut identity = None;
    for round in 0..45 {
        let start = Instant::now();
        let selected = ManagedLookupSelection::new(data.clone(), black_box(request)).unwrap();
        black_box(&selected);
        let us = start.elapsed().as_secs_f64() * 1e6;
        let mut hash = 2166136261u32;
        let mut mix = |n: u32| hash = (hash ^ n).wrapping_mul(16777619);
        mix(u32::from(selected.uses_feature_variations()));
        for entry in selected.lookups() {
            mix(u32::from(entry.index));
            mix(entry.value as u32);
            mix(u32::from_be_bytes(entry.tag));
            mix(u32::from(entry.required));
            mix(u32::from(entry.digest.is_some()));
        }
        let current = (selected.lookups().len(), hash);
        if let Some(old) = identity {
            assert_eq!(old, current);
        }
        identity = Some(current);
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
