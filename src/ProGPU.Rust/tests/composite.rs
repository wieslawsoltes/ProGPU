//! Exact original component records, nested coordinates and attachment controls.
use progpu::font::{FontError, SfntFace};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;

#[path = "support/composite_fixture.rs"]
mod composite_fixture;
use composite_fixture::*;

#[test]
fn owned_components_and_expansion_preserve_sources_and_attachment() {
    let c = composite(
        &[
            record(0x23, 0, [1, 2], [0; 4]),
            record(0x101, 0, [1, 0], [0; 4]),
        ],
        true,
    );
    let face = SfntFace::new(font(&[simple(), c]), 0).unwrap();
    let descriptors = face.composite_glyph(1).unwrap();
    let outline = face.glyph_outline(1).unwrap();
    assert_eq!(outline.source().data().as_ptr(), face.data().as_ptr());
    assert_eq!(outline.glyph_index(), 1);
    assert_eq!(outline.points().len(), 6);
    assert_eq!(outline.segments().len(), 4);
    assert_eq!(outline.points()[1], outline.points()[3]);
    assert_eq!(outline.points()[0], [4.0, -3.0]);
    drop(face);
    assert_eq!(descriptors.instructions(), [0xb0, 1, 0x2f]);
    assert_eq!(descriptors.components()[1].arguments, [1, 0]);
    assert_eq!(
        descriptors.source().data().as_ptr(),
        outline.source().data().as_ptr()
    );
    assert_eq!(
        outline.source().glyph_outline(2).unwrap_err(),
        FontError::InvalidArgument
    );
}
#[test]
fn malformed_components_reject_and_original_empty_routes_remain() {
    let valid = composite(
        &[record(0x183, 0, [7, 9], [-8192, 16384, -32768, 32767])],
        true,
    );
    for len in 10..valid.len() {
        let face = SfntFace::new(font(&[simple(), valid[..len].to_vec()]), 0).unwrap();
        assert_eq!(
            face.composite_requirements(1).unwrap_err(),
            FontError::InvalidGlyph
        );
        assert_eq!(face.glyph_outline(1).unwrap_err(), FontError::InvalidGlyph);
    }
    let cycle = composite(&[record(2, 0, [0, 0], [0; 4])], false);
    let face = SfntFace::new(font(&[cycle, vec![]]), 0).unwrap();
    for index in [0, 1] {
        assert!(face.glyph_outline(index).unwrap().points().is_empty());
    }
}
fn snapshot(index: u32, data: Vec<u8>, glyph: u16) -> Vec<u8> {
    fn emit(out: &mut Vec<u8>, value: u32) {
        out.extend_from_slice(&value.to_ne_bytes());
    }
    let mut out = Vec::new();
    let face = match SfntFace::new(data, index) {
        Ok(f) => {
            emit(&mut out, 0);
            f
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    match face.composite_requirements(glyph) {
        Ok(r) => {
            emit(&mut out, 0);
            emit(&mut out, r.component_count);
            emit(&mut out, u32::from(r.instruction_bytes));
            let g = face.composite_glyph(glyph).unwrap();
            for c in g.components() {
                for v in [
                    u32::from(c.flags),
                    u32::from(c.glyph_index),
                    c.arguments[0] as u32,
                    c.arguments[1] as u32,
                ] {
                    emit(&mut out, v);
                }
                for v in c.transform {
                    emit(&mut out, v.to_bits());
                }
            }
        }
        Err(e) => emit(&mut out, e as u32),
    }
    match face.expanded_requirements(glyph) {
        Ok(r) => {
            for v in [
                0,
                r.point_count,
                r.path_segment_count,
                r.simple_point_scratch_count,
                u32::from(r.simple_contour_scratch_count),
            ] {
                emit(&mut out, v);
            }
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    }
    match face.glyph_outline(glyph) {
        Ok(g) => {
            emit(&mut out, 0);
            emit(&mut out, g.points().len() as u32);
            emit(&mut out, g.segments().len() as u32);
            for p in g.points() {
                for v in p {
                    emit(&mut out, v.to_bits());
                }
            }
            out.extend_from_slice(bytemuck::cast_slice(g.segments()));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    out
}
#[test]
#[ignore = "requires original C++20 sources; mandatory native outline qualification"]
fn original_native_composite_records_and_expanded_bytes_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-composite-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("composite-oracle");
    let mut command = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    command
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_composite.cpp"));
    // Match Rust's separate scalar operations, as in the existing geometry oracle.
    // Keep the original compiler-default control reproducible: ARM64 Clang contracts
    // these expressions and differs on deep fractional transforms. Passing this
    // source-arithmetic control does not qualify those native package pixels.
    if std::env::var_os("PROGPU_RUST_NATIVE_DEFAULT_FP").is_none() {
        command.arg("-ffp-contract=off");
    }
    for file in [
        "progpu_native_sfnt.cpp",
        "progpu_native_true_type_glyph.cpp",
        "progpu_native_true_type_path.cpp",
        "progpu_native_true_type_composite.cpp",
        "progpu_native_true_type_outline.cpp",
    ] {
        command.arg(root.join("src/ProGPU.Native/src/Text").join(file));
    }
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let mut cases = corpus();
    let mut fonts: Vec<_> = std::fs::read_dir(root.join("tests/Shared"))
        .unwrap()
        .map(|p| p.unwrap().path())
        .filter(|p| {
            p.extension().is_some_and(|e| e == "ttf")
                && p.file_name()
                    .unwrap()
                    .to_string_lossy()
                    .starts_with("ProGPU-Authored-")
        })
        .collect();
    fonts.sort();
    assert!(fonts.len() >= 10);
    for file in fonts {
        let bytes = std::fs::read(file).unwrap();
        let font = SfntFace::new(bytes.clone(), 0).unwrap();
        for glyph in 0..font.glyph_count().unwrap() {
            cases.push((0, bytes.clone(), glyph));
        }
    }
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for (index, bytes, glyph) in &cases {
        input.extend_from_slice(&index.to_ne_bytes());
        input.extend_from_slice(&(bytes.len() as u32).to_ne_bytes());
        input.extend_from_slice(bytes);
        input.extend_from_slice(&u32::from(*glyph).to_ne_bytes());
        expected.extend_from_slice(&snapshot(*index, bytes.clone(), *glyph));
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
        let native_word = u32::from_ne_bytes(actual.stdout[word..word + 4].try_into().unwrap());
        let rust_word = u32::from_ne_bytes(expected[word..word + 4].try_into().unwrap());
        panic!(
            "composite case {case} byte {byte}: native word {native_word:#010x}, rust word {rust_word:#010x}"
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_composite_outline: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
