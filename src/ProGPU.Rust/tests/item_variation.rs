//! Original store/map error boundaries, encoded deltas and scalar arithmetic.
use progpu::font::{DeltaSetIndexMap, FontError, ItemVariationStore};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;

#[path = "support/item_variation_fixture.rs"]
mod item_fixture;
use item_fixture::{store, table};

const REGIONS: [[i16; 3]; 4] = [
    [0, 7153, 16384],
    [-16384, -9973, 0],
    [-10, 0, 10],
    [7, 3, 10],
];
fn standard(long: bool) -> Vec<u8> {
    store(
        1,
        &REGIONS,
        &[
            table(
                &[0, 1, 2, 3, 65535],
                if long { 0x8002 } else { 2 },
                &[
                    vec![123456789, -23456789, 32767, -32768, 103],
                    vec![-111, 222, -13, 17, 999],
                ],
            ),
            vec![],
            table(&[3, 2, 1, 0], 0, &[]),
            table(&[0, 65535], 0, &[]),
        ],
    )
}
#[test]
fn encoded_rows_missing_entries_and_selected_blend_policy_are_distinct() {
    for long in [false, true] {
        let data = standard(long);
        let s = ItemVariationStore::parse(&data, 0, 1).unwrap();
        assert_eq!(s.region_count(), 4);
        assert_eq!(s.delta(&[0], 0, 1), Ok(4.0));
        assert_eq!(s.delta(&[0], 1, 0), Ok(0.0));
        assert_eq!(s.delta(&[0], 65535, 65535), Ok(0.0));
        assert_eq!(s.delta(&[], 65535, 65535), Err(FontError::Capacity));
        assert_eq!(s.blend_region_count(0), Err(FontError::InvalidFace));
        assert_eq!(s.blend_region_count(2), Ok(4));
        assert_eq!(s.blend_region_count(3), Err(FontError::InvalidFace));
        assert_eq!(s.blend_region_scalar(&[0], 2, 0), Ok(1.0));
        assert_eq!(
            s.blend_region_scalar(&[0], 2, 4),
            Err(FontError::InvalidArgument)
        );
        assert_eq!(s.delta_with_scalars(&[0.0, 0.0, 1.0, 1.0], 0, 1), Ok(4.0));
    }
}
#[test]
fn later_subtable_truncation_rejects_whole_store_and_offsets_are_bounded() {
    let data = standard(true);
    assert!(ItemVariationStore::parse(&data[..data.len() - 1], 0, 1).is_err());
    for offset in [usize::MAX, usize::MAX - 7, data.len()] {
        assert!(ItemVariationStore::parse(&data, offset, 1).is_err());
        assert!(DeltaSetIndexMap::parse(&data, offset).is_err());
    }
    assert!(ItemVariationStore::parse(&data, 0, 0).is_err());
    assert_eq!(
        DeltaSetIndexMap::parse(&[0, 0, 0, 0], 0)
            .unwrap()
            .get(u32::MAX),
        (0, 65535)
    );
    let map = DeltaSetIndexMap::parse(&[1, 0x31, 0, 0, 0, 1, 0xab, 0xcd, 0xef, 0xf1], 0).unwrap();
    assert_eq!(map.get(0), (0x7bfc, 1));
    assert_eq!(map.get(u32::MAX), map.get(0));
}
#[test]
fn scalar_region_rules_retain_neutral_and_exact_boundary_behavior() {
    let data = standard(false);
    let s = ItemVariationStore::parse(&data, 0, 1).unwrap();
    assert_eq!(s.region_scalar(&[7153], 0), Ok(1.0));
    assert_eq!(s.region_scalar(&[16384], 0), Ok(0.0));
    assert_eq!(s.region_scalar(&[-1], 0), Ok(0.0));
    for coordinate in [i16::MIN, 0, i16::MAX] {
        assert_eq!(s.region_scalar(&[coordinate], 2), Ok(1.0));
        assert_eq!(s.region_scalar(&[coordinate], 3), Ok(1.0));
    }
}

#[derive(Clone)]
struct Case {
    mode: u32,
    data: Vec<u8>,
    args: [u32; 6],
    coords: Vec<i16>,
    scalars: Vec<f32>,
}
fn case(data: Vec<u8>) -> Case {
    Case {
        mode: 1,
        data,
        args: [0, 1, 0, 0, 0, 0],
        coords: vec![12345],
        scalars: vec![0.12345, -0.45678, 0.98765, -0.81234],
    }
}
fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    for long in [false, true] {
        let base = case(standard(long));
        for coordinate in i16::MIN..=i16::MAX {
            let mut c = base.clone();
            c.coords[0] = coordinate;
            cases.push(c);
        }
        for length in 0..base.data.len() {
            let mut c = base.clone();
            c.data.truncate(length);
            cases.push(c);
        }
        for offset in 0..base.data.len() {
            for value in [0, 1, 2, 3, 7, 127, 128, 255] {
                let mut c = base.clone();
                c.data[offset] = value;
                cases.push(c);
            }
        }
        for outer in [0, 1, 2, 3, 4, 65535] {
            for inner in [0, 1, 2, 65535] {
                for region in [0, 1, 2, 3, 4, 65535] {
                    for length in 0..=2 {
                        let mut c = base.clone();
                        c.args[2] = region;
                        c.args[3] = outer;
                        c.args[4] = inner;
                        c.args[5] = inner;
                        c.coords.resize(length, 7311);
                        c.scalars.resize(length * 2, 0.7);
                        cases.push(c);
                    }
                }
            }
        }
    }
    let values = [i16::MIN, -16384, -9973, -1, 0, 1, 7153, 16384, i16::MAX];
    for start in values {
        for peak in values {
            for end in values {
                let data = store(
                    2,
                    &[[start, peak, end], [0, 8192, 16384]],
                    &[table(&[0], 0, &[])],
                );
                for coordinate in values {
                    let mut c = case(data.clone());
                    c.args[1] = 2;
                    c.coords = vec![coordinate, 11001];
                    cases.push(c);
                }
            }
        }
    }
    for packed in [0, 1, 2, 3, 0x7fff, 0x8000, 0x8001, 0x8002, 0x8003, 0xffff] {
        let mut t = table(&[0, 1], 0, &[vec![0, 0]]);
        t.resize(64, 0xab);
        fixture::u16_at(&mut t, 2, packed);
        cases.push(case(store(1, &REGIONS, &[t])));
    }
    for offset in [0, 1, 7, 31] {
        let mut c = case(standard(true));
        let mut prefix = vec![0xaa; offset];
        prefix.extend_from_slice(&c.data);
        c.data = prefix;
        c.args[0] = offset as u32;
        cases.push(c);
    }
    cases.push(case(store(0, &[], &[table(&[], 0, &[])])));
    cases.last_mut().unwrap().args[1] = 0;
    for format in 0..=2 {
        for entry in 0..=u8::MAX {
            let mut data = vec![format, entry, 0, 3];
            if format == 1 {
                data = vec![format, entry, 0, 0, 0, 3];
            }
            data.extend_from_slice(&[
                0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0, 0x89, 0x67, 0x45, 0x23,
            ]);
            for item in [0, 1, 2, 3, 65535, u32::MAX] {
                let mut c = case(data.clone());
                c.mode = 0;
                c.args[2] = item;
                cases.push(c);
            }
            for length in 0..data.len() {
                let mut c = case(data[..length].to_vec());
                c.mode = 0;
                cases.push(c);
            }
        }
    }
    for count in [0, 1, 65535, u32::MAX] {
        let mut c = case(vec![1, 0x3f, 0, 0, 0, 0]);
        fixture::u32_at(&mut c.data, 2, count);
        c.mode = 0;
        c.args[2] = u32::MAX;
        cases.push(c);
    }
    cases
}
fn emit(out: &mut Vec<u8>, v: u32) {
    out.extend_from_slice(&v.to_ne_bytes());
}
fn result(out: &mut Vec<u8>, r: Result<f32, FontError>) {
    match r {
        Ok(v) => {
            emit(out, 0);
            emit(out, v.to_bits());
        }
        Err(e) => emit(out, e as u32),
    }
}
fn snapshot(c: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    if c.mode == 0 {
        match DeltaSetIndexMap::parse(&c.data, c.args[0] as usize) {
            Ok(m) => {
                let (outer, inner) = m.get(c.args[2]);
                for v in [0, m.len(), u32::from(outer), u32::from(inner)] {
                    emit(&mut out, v);
                }
            }
            Err(e) => emit(&mut out, e as u32),
        }
        return out;
    }
    let s = match ItemVariationStore::parse(&c.data, c.args[0] as usize, c.args[1] as u16) {
        Ok(s) => {
            for v in [
                0,
                u32::from(s.axis_count()),
                u32::from(s.region_count()),
                u32::from(s.subtable_count()),
            ] {
                emit(&mut out, v);
            }
            s
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    let outer = c.args[3] as u16;
    let inner = c.args[4] as u16;
    result(&mut out, s.region_scalar(&c.coords, c.args[2] as u16));
    result(&mut out, s.delta(&c.coords, outer, inner));
    result(&mut out, s.delta_with_scalars(&c.scalars, outer, inner));
    match s.blend_region_count(outer) {
        Ok(v) => {
            emit(&mut out, 0);
            emit(&mut out, u32::from(v));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    result(
        &mut out,
        s.blend_region_scalar(&c.coords, outer, c.args[5] as u16),
    );
    out
}
#[test]
#[ignore = "requires original C++20 sources; mandatory item-variation qualification"]
fn original_native_item_variation_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!(
        "progpu-item-variation-oracle-{}",
        std::process::id()
    ));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("item-variation-oracle");
    let mut command = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    command
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_item_variation.cpp"))
        .arg(root.join("src/ProGPU.Native/src/Text/progpu_native_item_variation_store.cpp"));
    if std::env::var_os("PROGPU_RUST_NATIVE_DEFAULT_FP").is_none() {
        command.arg("-ffp-contract=off");
    }
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let cases = corpus();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for c in &cases {
        emit(&mut input, c.mode);
        emit(&mut input, c.data.len() as u32);
        for v in c.args {
            emit(&mut input, v);
        }
        input.extend_from_slice(&c.data);
        emit(&mut input, c.coords.len() as u32);
        for v in &c.coords {
            input.extend_from_slice(&v.to_ne_bytes());
        }
        emit(&mut input, c.scalars.len() as u32);
        for v in &c.scalars {
            emit(&mut input, v.to_bits());
        }
        expected.extend_from_slice(&snapshot(c));
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
        panic!("item variation case {case} byte {byte}: native {native:#010x}, rust {rust:#010x}");
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_item_variation: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
