//! Original packed runs, circular interpolation, atomic errors and raw float bits.
use progpu::font::{
    FontError,
    gvar::{PackedDeltas, PackedPoints, infer_untouched},
};
#[path = "support/gvar_apply.rs"]
mod apply;
#[path = "support/composite_fixture.rs"]
mod composite_fixture;
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;
#[allow(dead_code)]
#[path = "../examples/support/font_scene.rs"]
mod font_scene;
#[path = "support/gvar_metadata.rs"]
mod metadata;
#[path = "support/gvar_outline.rs"]
mod varied;

#[test]
fn explicit_zero_count_is_not_all_points_and_output_is_transactional() {
    let all = PackedPoints::parse(&[0, 99]).unwrap();
    assert!(all.all_points());
    assert_eq!(all.bytes_consumed(), 1);
    let empty = PackedPoints::parse(&[128, 0, 99]).unwrap();
    assert!(!empty.all_points());
    assert!(empty.is_empty());
    assert_eq!(empty.bytes_consumed(), 2);
    let p = PackedPoints::parse(&[3, 2, 1, 0, 7, 99]).unwrap();
    assert_eq!(p.iter().collect::<Vec<_>>(), [1, 1, 8]);
    let mut output = [42; 2];
    assert_eq!(p.decode_into(&mut output), Err(FontError::Capacity));
    assert_eq!(output, [42; 2]);
    let mut it = p.iter();
    assert_eq!(it.len(), 3);
    assert_eq!(it.next(), Some(1));
    assert_eq!(it.len(), 2);
    let d = PackedDeltas::parse(&[0xc2, 99], 3).unwrap();
    assert_eq!(d.iter().collect::<Vec<_>>(), [0, 0, 0]);
    assert_eq!(d.bytes_consumed(), 1);
    let mut output = [42; 4];
    assert_eq!(d.decode_into(&mut output), Ok(3));
    assert_eq!(output, [0, 0, 0, 42]);
}
#[test]
fn contour_validation_precedes_all_writes_and_untouched_contours_survive() {
    let p = [[0.0, 0.0], [10.0, 5.0], [20.0, 10.0], [0.0, 0.0]];
    let (mut x, mut y) = ([2.0, 99.0, 6.0, 17.0, 31.0], [4.0, 98.0, 8.0, 19.0, 37.0]);
    assert_eq!(
        infer_untouched(&p, &[2, 1], &mut x, &mut y, &[1, 0, 1, 0]),
        Err(FontError::InvalidGlyph)
    );
    assert_eq!(x, [2.0, 99.0, 6.0, 17.0, 31.0]);
    infer_untouched(&p, &[2, 3], &mut x, &mut y, &[1, 0, 255, 0]).unwrap();
    assert_eq!(x, [2.0, 4.0, 6.0, 17.0, 31.0]);
    assert_eq!(y, [4.0, 6.0, 8.0, 19.0, 37.0]);
}
#[derive(Clone)]
struct Case {
    a: [u32; 8],
    data: Vec<u8>,
}
fn packed(mode: u32, data: Vec<u8>, count: u32, capacity: u32) -> Case {
    Case {
        a: [mode, data.len() as u32, count, capacity, 0, 0, 0, 0],
        data,
    }
}
fn iup(points: &[[f32; 2]], ends: &[u16], x: &[f32], y: &[f32], touched: &[u8]) -> Case {
    let mut data = Vec::new();
    for p in points {
        for v in p {
            data.extend(v.to_ne_bytes());
        }
    }
    for v in ends {
        data.extend(v.to_ne_bytes());
    }
    for values in [x, y] {
        for v in values {
            data.extend(v.to_ne_bytes());
        }
    }
    data.extend(touched);
    Case {
        a: [
            2,
            data.len() as u32,
            0,
            x.len() as u32,
            y.len() as u32,
            touched.len() as u32,
            ends.len() as u32,
            points.len() as u32,
        ],
        data,
    }
}
fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    for first in 0..=u8::MAX {
        for control in 0..=u8::MAX {
            let mut data = vec![first];
            if first & 0x80 != 0 {
                data.push(3);
            }
            data.push(control);
            data.extend((0..256).map(|i| (i * 31 + 129) as u8));
            cases.push(packed(0, data, 0, 260));
        }
    }
    for control in 0..=u8::MAX {
        for count in 0..=66 {
            let mut data = vec![control];
            data.extend((0..128).map(|i| (i * 37 + 131) as u8));
            cases.push(packed(1, data, count, 67));
        }
    }
    for mode in 0..=1 {
        for capacity in 0..=5 {
            for data in [
                vec![],
                vec![0],
                vec![128],
                vec![128, 0],
                vec![3, 2, 1, 0, 7],
                vec![2, 128, 255],
                vec![0xc2],
            ] {
                for len in 0..=data.len() {
                    cases.push(packed(mode, data[..len].to_vec(), 3, capacity));
                }
            }
        }
    }
    // Multiple maximum-length word runs with duplicates and cumulative positions.
    let mut points = vec![0xff, 0xff];
    for i in 0..32767 {
        if i % 128 == 0 {
            points.push(0x80 | ((32767 - i).min(128) - 1) as u8);
        }
        points.extend(65535u16.to_be_bytes());
    }
    cases.push(packed(0, points, 0, 32768));
    let points = [
        [3.25, -7.5],
        [-11.75, 19.125],
        [3.25, 11.0],
        [0.25, -2.0],
        [17.75, 11.0],
        [-11.75, -7.5],
        [2.125, 6.75],
        [14.25, -3.5],
    ];
    let x = [1.12345, -13.76543, 25.125, -3.5, 0.0, -0.0, 7.33, 91.117];
    let y = [-7.21, 9.55, 0.0, -0.0, -11.512, 25.745, 1.55, 2.33];
    for partition in 0..128 {
        let mut ends = Vec::new();
        for i in 0..7 {
            if partition & (1 << i) != 0 {
                ends.push(i);
            }
        }
        ends.push(7);
        for mask in 0..256 {
            let touched: Vec<_> = (0..8)
                .map(|i| if mask & (1 << i) != 0 { 255 } else { 0 })
                .collect();
            cases.push(iup(&points, &ends, &x, &y, &touched));
        }
    }
    // Complete later-contour rejection, capacity precedence and preserved tails.
    for ends in [
        vec![],
        vec![0],
        vec![7],
        vec![8],
        vec![65535],
        vec![2, 1],
        vec![2, 8],
        vec![2, 7],
    ] {
        for size in 0..=9 {
            let mut xx = x.to_vec();
            xx.resize(size, 123.0);
            let mut yy = y.to_vec();
            yy.resize(size, 127.0);
            let touched = vec![1; size];
            cases.push(iup(&points, &ends, &xx, &y, &[1; 8]));
            cases.push(iup(&points, &ends, &x, &yy, &[1; 8]));
            cases.push(iup(&points, &ends, &x, &y, &touched));
        }
    }
    for ends in [vec![], vec![0]] {
        cases.push(iup(&[], &ends, &[123.0], &[127.0], &[]));
    }
    let special = [
        0.0,
        -0.0,
        f32::MIN_POSITIVE,
        f32::from_bits(1),
        f32::MAX,
        -f32::MAX,
        f32::INFINITY,
        f32::NEG_INFINITY,
        f32::NAN,
    ];
    for a in special {
        for b in special {
            for t in special {
                cases.push(iup(
                    &[[a, b], [t, t], [b, a]],
                    &[2],
                    &[a, 17.0, b],
                    &[b, 19.0, a],
                    &[1, 0, 1],
                ));
            }
        }
    }
    cases.extend(metadata::corpus());
    cases.extend(apply::corpus());
    cases.extend(varied::corpus());
    cases
}
fn emit(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_ne_bytes());
}
fn snapshot(c: &Case) -> Vec<u8> {
    if c.a[0] == 5 {
        return varied::snapshot(c);
    }
    if c.a[0] == 4 {
        return apply::snapshot(c);
    }
    if c.a[0] == 3 {
        return metadata::snapshot(c);
    }
    let mut out = Vec::new();
    match c.a[0] {
        0 => match PackedPoints::parse(&c.data) {
            Ok(p) => {
                for v in [
                    0,
                    p.len(),
                    p.bytes_consumed() as u32,
                    u32::from(p.all_points()),
                ] {
                    emit(&mut out, v);
                }
                let mut output = vec![0x12345678; c.a[3] as usize];
                emit(
                    &mut out,
                    p.decode_into(&mut output).err().map_or(0, |e| e as u32),
                );
                for v in output {
                    emit(&mut out, v);
                }
            }
            Err(e) => emit(&mut out, e as u32),
        },
        1 => match PackedDeltas::parse(&c.data, c.a[2]) {
            Ok(p) => {
                for v in [0, p.len(), p.bytes_consumed() as u32] {
                    emit(&mut out, v);
                }
                let mut output = vec![-12345; c.a[3] as usize];
                emit(
                    &mut out,
                    p.decode_into(&mut output).err().map_or(0, |e| e as u32),
                );
                for v in output {
                    emit(&mut out, v as u32);
                }
            }
            Err(e) => emit(&mut out, e as u32),
        },
        2 => {
            let mut cursor = 0;
            let mut read = |n: usize| {
                let b = &c.data[cursor..cursor + n];
                cursor += n;
                b
            };
            let points: Vec<_> = (0..c.a[7])
                .map(|_| {
                    [
                        f32::from_ne_bytes(read(4).try_into().unwrap()),
                        f32::from_ne_bytes(read(4).try_into().unwrap()),
                    ]
                })
                .collect();
            let ends: Vec<_> = (0..c.a[6])
                .map(|_| u16::from_ne_bytes(read(2).try_into().unwrap()))
                .collect();
            let mut x: Vec<_> = (0..c.a[3])
                .map(|_| f32::from_ne_bytes(read(4).try_into().unwrap()))
                .collect();
            let mut y: Vec<_> = (0..c.a[4])
                .map(|_| f32::from_ne_bytes(read(4).try_into().unwrap()))
                .collect();
            let touched = read(c.a[5] as usize);
            emit(
                &mut out,
                infer_untouched(&points, &ends, &mut x, &mut y, touched)
                    .err()
                    .map_or(0, |e| e as u32),
            );
            for v in x.into_iter().chain(y) {
                emit(&mut out, v.to_bits());
            }
        }
        _ => unreachable!(),
    }
    out
}
#[test]
#[ignore = "requires original C++20 sources; mandatory gvar source qualification"]
fn original_native_packed_variation_and_interpolation_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-gvar-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("gvar-oracle");
    let mut command = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    command
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_gvar.cpp"));
    if std::env::var_os("PROGPU_RUST_NATIVE_DEFAULT_FP").is_none() {
        command.arg("-ffp-contract=off");
    }
    for file in ["packed", "interpolate"] {
        command.arg(root.join(format!(
            "src/ProGPU.Native/src/Text/progpu_native_gvar_{file}.cpp"
        )));
    }
    for file in ["sfnt", "font_variations", "gvar", "gvar_tuples"] {
        command.arg(root.join(format!(
            "src/ProGPU.Native/src/Text/progpu_native_{file}.cpp"
        )));
    }
    for file in [
        "gvar_payload",
        "gvar_apply",
        "gvar_composite",
        "gvar_phantom",
        "hvar",
        "item_variation_store",
        "true_type_glyph",
        "true_type_path",
        "true_type_composite",
        "true_type_outline",
        "true_type_varied_requirements",
        "true_type_varied_outline",
    ] {
        command.arg(root.join(format!(
            "src/ProGPU.Native/src/Text/progpu_native_{file}.cpp"
        )));
    }
    command.arg(
        root.join("src/ProGPU.Native/src/Text/Font/progpu_native_font_horizontal_metrics.cpp"),
    );
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let mut cases = corpus();
    if std::env::var_os("PROGPU_RUST_VARIED_ONLY").is_some() {
        cases.retain(|c| c.a[0] == 5);
    }
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for c in &cases {
        for v in c.a {
            emit(&mut input, v);
        }
        input.extend(&c.data);
        expected.extend(snapshot(c));
        ends.push(expected.len());
    }
    let path = dir.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = Command::new(&exe)
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(actual.status.success());
    if let Some(byte) = actual
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        let case = ends.partition_point(|end| *end <= byte);
        let word = byte & !3;
        let native = u32::from_ne_bytes(actual.stdout[word..word + 4].try_into().unwrap());
        let rust = u32::from_ne_bytes(expected[word..word + 4].try_into().unwrap());
        let start = if case == 0 { 0 } else { ends[case - 1] };
        eprintln!("arguments: {:?}, case byte {}", cases[case].a, byte - start);
        eprintln!("source bytes: {:02x?}", cases[case].data);
        panic!(
            "gvar case {case} mode {} byte {byte}: native {native:#010x}, rust {rust:#010x}",
            cases[case].a[0]
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_gvar_primitives: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
