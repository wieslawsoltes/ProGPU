//! Matched original managed metadata admission and immutable instance creation.
use progpu::font::{ManagedVariationData, ManagedVariationInstance, SfntFace};
use std::{
    hint::black_box,
    io::{self, Write},
    time::Instant,
};
#[path = "../tests/support/managed_variations_fixture.rs"]
mod fixture;
#[path = "../tests/support/managed_variation_replay.rs"]
mod replay;
fn word(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_le_bytes());
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let mode = &args[1];
    assert!(mode == "admission" || mode == "instance");
    let axes: usize = args[2].parse().unwrap();
    let variant: usize = args[3].parse().unwrap();
    let bytes = fixture::font::sfnt(&fixture::tables(axes, variant), 0);
    let mut request = vec![8, 3];
    for (tag, value) in [(b"wght", 0.375f32), (b"wdth", -0.1875), (b"opsz", 0.125)] {
        request.push(4);
        request.extend(tag.map(u32::from));
        request.push(value.to_bits());
    }
    request.push(0);
    if args.get(4).is_some_and(|s| s == "input") {
        let mut out = Vec::new();
        word(&mut out, bytes.len() as u32);
        out.extend(bytes);
        for v in request {
            word(&mut out, v);
        }
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let case = replay::decode(bytes.clone(), &request).unwrap();
    let source = SfntFace::new(bytes, 0).unwrap();
    let mut data = ManagedVariationData::from_face(source.clone(), replay::name).unwrap();
    let mut instance: Option<ManagedVariationInstance> = None;
    let mut times = Vec::new();
    for round in 0..45 {
        let start = Instant::now();
        if mode == "admission" {
            data = black_box(ManagedVariationData::from_face(
                source.clone(),
                replay::name,
            ))
            .unwrap();
        } else {
            instance =
                Some(black_box(data.as_ref().unwrap().create_instance(&case.settings)).unwrap());
        }
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        black_box(&data);
        black_box(&instance);
        if round >= 5 {
            times.push(elapsed);
        }
    }
    let output = case.capture(data, instance.map(Ok)).unwrap();
    assert_eq!(output, case.expected().unwrap());
    times.sort_by(f64::total_cmp);
    eprintln!(
        "execute_p50_us={:.3} execute_p95_us={:.3} execute_p99_us={:.3}",
        times[19], times[37], times[39]
    );
    io::stdout().write_all(&output).unwrap();
}
