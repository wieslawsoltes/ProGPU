//! Full-output managed font construction, selection, eviction and palette baseline.
use progpu::font::{ManagedFont, ManagedVariationSetting, SfntFace};
use std::{
    hint::black_box,
    io::{self, Write},
    sync::Arc,
    time::Instant,
};
#[path = "../tests/support/managed_font_fixture.rs"]
mod fixture;
#[path = "../tests/support/managed_font_replay.rs"]
mod replay;
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let mode = &args[1];
    let variant: usize = args[2].parse().unwrap();
    let settings = vec![
        ManagedVariationSetting::from_str("wght", 700.0),
        ManagedVariationSetting::from_str("ital", 0.75),
        ManagedVariationSetting::from_str("slnt", -10.0),
    ];
    let case = replay::Case {
        font: fixture::font::sfnt(&fixture::tables(variant), 0),
        face_index: 0,
        ops: vec![replay::Op {
            kind: 0,
            from: 0,
            index: 0,
            settings: settings.clone(),
            colors: vec![],
        }],
        queries: vec![],
    };
    if args.get(3).is_some_and(|v| v == "input") {
        let mut out = Vec::new();
        case.write(&mut out);
        io::stdout().write_all(&out).unwrap();
        return;
    }
    let data: Arc<[u8]> = case.font.into();
    let construct = || ManagedFont::from_face(SfntFace::new(data.clone(), 0).unwrap()).unwrap();
    let root = (mode != "construction").then(construct);
    let selected = if let Some(r) = &root {
        black_box(r.variation_axes().unwrap());
        if mode == "hit" || mode == "palette" {
            Some(r.with_variations(&settings).unwrap())
        } else {
            None
        }
    } else {
        None
    };
    let rotating: Vec<_> = (0..45)
        .map(|i| {
            let mut s = settings.clone();
            s[0].value = (110 + i * 10) as f32;
            s
        })
        .collect();
    let mut result = None;
    let mut times = Vec::new();
    let mut first = 0.0;
    for (n, request) in rotating.iter().enumerate() {
        let start = Instant::now();
        result = Some(black_box(match mode.as_str() {
            "construction" => construct(),
            "hit" => root.as_ref().unwrap().with_variations(&settings).unwrap(),
            "rotating" => root.as_ref().unwrap().with_variations(request).unwrap(),
            "palette" => selected
                .as_ref()
                .unwrap()
                .with_color_palette(1, &[(1, 0x80402010)])
                .unwrap(),
            _ => panic!("unknown mode"),
        }));
        let elapsed = start.elapsed().as_secs_f64() * 1e6;
        if n == 0 {
            first = elapsed;
        }
        if n >= 5 {
            times.push(elapsed);
        }
    }
    let mut out = Vec::new();
    replay::snapshot(result.as_ref().unwrap(), &[], &[], &mut out).unwrap();
    times.sort_by(f64::total_cmp);
    eprintln!(
        "first_us={first:.3} execute_p50_us={:.3} execute_p95_us={:.3} execute_p99_us={:.3}",
        times[19], times[37], times[39]
    );
    io::stdout().write_all(&out).unwrap();
}
