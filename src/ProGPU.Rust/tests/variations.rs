//! Exact source fvar inventory, fixed normalization and strict design maps.
use progpu::font::{FontError, SfntFace, VariationAxis};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;

fn fvar(endpoints: &[[i32; 3]], stride: usize) -> Vec<u8> {
    let mut data = vec![0; 16 + endpoints.len() * stride];
    fixture::u32_at(&mut data, 0, 0x10000);
    fixture::u16_at(&mut data, 4, 16);
    fixture::u16_at(&mut data, 8, endpoints.len() as u16);
    fixture::u16_at(&mut data, 10, stride as u16);
    for (i, values) in endpoints.iter().enumerate() {
        let base = 16 + i * stride;
        fixture::u32_at(&mut data, base, 0x77676874 + i as u32);
        for (j, value) in values.iter().enumerate() {
            fixture::u32_at(&mut data, base + 4 + j * 4, *value as u32);
        }
        fixture::u16_at(&mut data, base + 16, 0xabcd);
        fixture::u16_at(&mut data, base + 18, 256 + i as u16);
    }
    data
}
fn avar(maps: &[&[[i16; 2]]]) -> Vec<u8> {
    let mut data = vec![0; 8];
    fixture::u32_at(&mut data, 0, 0x10000);
    fixture::u16_at(&mut data, 6, maps.len() as u16);
    for map in maps {
        data.extend_from_slice(&(map.len() as u16).to_be_bytes());
        for pair in *map {
            for value in pair {
                data.extend_from_slice(&value.to_be_bytes());
            }
        }
    }
    data
}
const MAP: [[i16; 2]; 5] = [
    [-16384, -16384],
    [-9973, -7311],
    [0, 0],
    [7153, 12111],
    [16384, 16384],
];
fn font(fvar: Vec<u8>, avar: Option<Vec<u8>>) -> Vec<u8> {
    let mut tables = vec![(*b"fvar", fvar)];
    if let Some(data) = avar {
        tables.push((*b"avar", data));
    }
    fixture::sfnt(&tables, 0)
}
#[test]
fn raw_inventory_and_selected_validation_are_independent() {
    let data = font(fvar(&[[-65536, 0, 65536], [1, -1, 2]], 24), None);
    let face = SfntFace::new(data, 0).unwrap();
    let mut axes = face.variation_axes().unwrap();
    assert_eq!(axes.len(), 2);
    assert_eq!(axes.next().unwrap().flags, 0xabcd);
    assert_eq!(axes.next().unwrap().default_fixed, -1);
    assert!(axes.next().is_none());
    assert!(axes.next().is_none());
    assert_eq!(face.variation_axis(1), Err(FontError::InvalidFace));
    assert_eq!(face.variation_axis(2), Err(FontError::InvalidArgument));
    let clone = face.clone();
    drop(face);
    assert_eq!(clone.variation_axis(0).unwrap().minimum(), -1.0);
}
#[test]
fn strict_maps_validate_unselected_axes_and_fixed_route_keeps_legacy_policy() {
    let fv = fvar(&[[-65536, 0, 65536]; 2], 20);
    let bad = [[-16384, -16384], [0, 1], [16384, 16384]];
    let face = SfntFace::new(font(fv.clone(), Some(avar(&[&MAP, &bad]))), 0).unwrap();
    assert_eq!(face.normalize_variation_coordinate(0, 0), Ok(0));
    assert_eq!(
        face.normalize_variation_design_coordinate(0, 0.0),
        Err(FontError::InvalidFace)
    );
    let face = SfntFace::new(font(fv, Some(avar(&[&MAP]))), 0).unwrap();
    assert_eq!(face.normalize_variation_coordinate(0, 32768), Ok(8192));
    assert_eq!(
        face.normalize_variation_design_coordinate(0, 0.5),
        Err(FontError::InvalidFace)
    );
    assert_eq!(
        face.normalize_variation_design_coordinate(0, f32::NAN),
        Err(FontError::InvalidArgument)
    );
}
#[test]
fn initial_half_ties_are_away_from_zero_and_map_ties_are_even() {
    let face = SfntFace::new(font(fvar(&[[-131072, 0, 131072]], 20), None), 0).unwrap();
    assert_eq!(face.normalize_variation_coordinate(0, 4), Ok(1));
    assert_eq!(face.normalize_variation_coordinate(0, -4), Ok(-1));
    let map = [[-16384, -16384], [-4, -2], [0, 0], [4, 2], [16384, 16384]];
    let face = SfntFace::new(
        font(fvar(&[[-65536, 0, 65536]], 20), Some(avar(&[&map]))),
        0,
    )
    .unwrap();
    for (input, expected) in [(-12, -2), (-4, 0), (4, 0), (12, 2)] {
        assert_eq!(face.normalize_variation_coordinate(0, input), Ok(expected));
    }
}

type Case = (Vec<u8>, u16, i32, f32);
fn corpus() -> Vec<Case> {
    let fv = fvar(&[[-65536, 0, 65536]], 20);
    let av = avar(&[&MAP]);
    let valid = font(fv.clone(), Some(av.clone()));
    let mut cases = Vec::new();
    for coordinate in i16::MIN..=i16::MAX {
        cases.push((
            valid.clone(),
            0,
            i32::from(coordinate) * 4,
            f32::from(coordinate) / 16384.0,
        ));
    }
    // Every selected byte is varied, not just successful endpoints; later axes
    // must still be checked before publishing the selected coordinate.
    let fv2 = fvar(&[[-65536, 0, 65536], [i32::MIN, 17, i32::MAX]], 24);
    let av2 = avar(&[&MAP, &MAP]);
    for (which, bytes) in [(0, &fv2), (1, &av2)] {
        for length in 0..bytes.len() {
            let data = if which == 0 {
                font(bytes[..length].to_vec(), Some(av2.clone()))
            } else {
                font(fv2.clone(), Some(bytes[..length].to_vec()))
            };
            cases.push((data, 0, 32767, 0.49998474));
        }
        for offset in 0..bytes.len() {
            for value in 0..=u8::MAX {
                let mut modified = bytes.clone();
                modified[offset] = value;
                let data = if which == 0 {
                    font(modified, Some(av2.clone()))
                } else {
                    font(fv2.clone(), Some(modified))
                };
                cases.push((data, 0, -27961, -0.426651));
            }
        }
    }
    let values = [i32::MIN, -131072, -65536, -1, 0, 1, 65536, 131072, i32::MAX];
    for min in values {
        for default in values {
            for max in values {
                for value in values {
                    cases.push((
                        font(fvar(&[[min, default, max]], 20), None),
                        0,
                        value,
                        value as f32 / 65536.0,
                    ));
                }
            }
        }
    }
    let mut seed = 0x12345678u32;
    for _ in 0..8192 {
        seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
        cases.push((valid.clone(), 0, seed as i32, f32::from_bits(seed)));
    }
    for index in [0, 1, 2, 65535] {
        for value in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY, 0.0, -0.0] {
            cases.push((valid.clone(), index, 0, value));
        }
    }
    for (_, data) in fixture::corpus() {
        cases.push((data, 0, 0, 0.0));
    }
    cases
}
fn emit(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_ne_bytes());
}
fn axis(out: &mut Vec<u8>, a: VariationAxis) {
    for value in [
        a.tag.0,
        a.minimum_fixed as u32,
        a.default_fixed as u32,
        a.maximum_fixed as u32,
        u32::from(a.flags),
        u32::from(a.name_id),
        a.minimum().to_bits(),
        a.default_value().to_bits(),
        a.maximum().to_bits(),
        u32::from(a.hidden()),
    ] {
        emit(out, value);
    }
}
fn snapshot(case: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    let face = match SfntFace::new(case.0.clone(), 0) {
        Ok(f) => {
            emit(&mut out, 0);
            f
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    match face.variation_axes() {
        Ok(axes) => {
            emit(&mut out, 0);
            emit(&mut out, axes.len() as u32);
            for a in axes {
                axis(&mut out, a);
            }
        }
        Err(e) => emit(&mut out, e as u32),
    }
    match face.variation_axis(case.1) {
        Ok(a) => {
            emit(&mut out, 0);
            axis(&mut out, a);
        }
        Err(e) => emit(&mut out, e as u32),
    }
    for result in [
        face.normalize_variation_coordinate(case.1, case.2),
        face.normalize_variation_design_coordinate(case.1, case.3),
    ] {
        match result {
            Ok(v) => {
                emit(&mut out, 0);
                emit(&mut out, v as u32);
            }
            Err(e) => emit(&mut out, e as u32),
        }
    }
    out
}

#[test]
#[ignore = "requires original C++20 sources; mandatory variation source qualification"]
fn original_native_variation_metadata_and_normalization_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-variations-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("variations-oracle");
    let mut command = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    command
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_variations.cpp"));
    // Same isolated source-arithmetic control as geometry/composite/CFF tests.
    // Native compiler defaults and package qualification remain independent.
    if std::env::var_os("PROGPU_RUST_NATIVE_DEFAULT_FP").is_none() {
        command.arg("-ffp-contract=off");
    }
    for file in [
        "progpu_native_sfnt.cpp",
        "progpu_native_font_variations.cpp",
    ] {
        command.arg(root.join("src/ProGPU.Native/src/Text").join(file));
    }
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let cases = corpus();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for case in &cases {
        emit(&mut input, case.0.len() as u32);
        input.extend_from_slice(&case.0);
        for value in [u32::from(case.1), case.2 as u32, case.3.to_bits()] {
            emit(&mut input, value);
        }
        expected.extend_from_slice(&snapshot(case));
        ends.push(expected.len());
    }
    let input_path = dir.join("input.bin");
    std::fs::write(&input_path, input).unwrap();
    let actual = Command::new(&exe)
        .stdin(Stdio::from(std::fs::File::open(input_path).unwrap()))
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
        panic!("variation case {case} byte {byte}: native {native:#010x}, rust {rust:#010x}");
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_variations: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
