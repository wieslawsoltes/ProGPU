//! Exact original component records, nested coordinates and attachment controls.
use progpu::font::{FontError, SfntFace};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;

fn font(glyphs: &[Vec<u8>]) -> Vec<u8> {
    let mut tables = fixture::tables(vec![]);
    let mut loca = vec![0; (glyphs.len() + 1) * 4];
    let mut glyf = Vec::new();
    for (i, glyph) in glyphs.iter().enumerate() {
        glyf.extend_from_slice(glyph);
        fixture::u32_at(&mut loca, (i + 1) * 4, glyf.len() as u32);
    }
    for (tag, data) in &mut tables {
        match &*tag {
            b"head" => fixture::u16_at(data, 50, 1),
            b"maxp" => fixture::u16_at(data, 4, glyphs.len() as u16),
            b"loca" => *data = loca.clone(),
            b"glyf" => *data = glyf.clone(),
            _ => {}
        }
    }
    fixture::sfnt(&tables, 0)
}
fn simple() -> Vec<u8> {
    let mut data = vec![0; 10];
    fixture::u16_at(&mut data, 0, 1);
    data.extend_from_slice(&[0, 2, 0, 0, 0, 1, 0]); // three off/on/off points
    for delta in [3i16, 11, -7, -5, 13, -3] {
        data.extend_from_slice(&delta.to_be_bytes());
    }
    data
}
fn record(flags: u16, index: u16, args: [u16; 2], matrix: [i16; 4]) -> Vec<u8> {
    let mut data = Vec::new();
    data.extend_from_slice(&flags.to_be_bytes());
    data.extend_from_slice(&index.to_be_bytes());
    for arg in args {
        if flags & 1 != 0 {
            data.extend_from_slice(&arg.to_be_bytes());
        } else {
            data.push(arg as u8);
        }
    }
    let values: &[i16] = if flags & 8 != 0 {
        &matrix[..1]
    } else if flags & 0x40 != 0 {
        &matrix[..2]
    } else if flags & 0x80 != 0 {
        &matrix
    } else {
        &[]
    };
    for value in values {
        data.extend_from_slice(&value.to_be_bytes());
    }
    data
}
fn composite(records: &[Vec<u8>], instructions: bool) -> Vec<u8> {
    let mut data = vec![0; 10];
    fixture::u16_at(&mut data, 0, u16::MAX);
    for record in records {
        data.extend_from_slice(record);
    }
    if instructions {
        data.extend_from_slice(&[0, 3, 0xb0, 1, 0x2f]);
    }
    data
}
fn corpus() -> Vec<(u32, Vec<u8>, u16)> {
    let mut cases = Vec::new();
    // Every raw flag combination: unknown bits and transform precedence survive.
    for flags in 0..=u16::MAX {
        let mut records = vec![record(
            flags,
            0,
            [0x80ff, 0x7fff],
            [-8192, 12288, -16384, 32767],
        )];
        if flags & 0x20 != 0 {
            records.push(record(2, 0, [7, 9], [0; 4]));
        }
        let g = composite(&records, flags & 0x100 != 0 && flags & 0x20 == 0);
        cases.push((0, font(&[simple(), vec![], g]), 2));
    }
    // Valid and invalid point attachment, scaled offsets, half ties and nested frames.
    for parent in [0, 1, 2, 3, 255, 65535] {
        for child in [0, 1, 2, 3, 255, 65535] {
            for flags in [1, 5, 0x81, 0x85, 0x1081, 0x1885] {
                let records = [
                    record(0x23, 0, [3, 5], [0; 4]),
                    record(flags, 0, [parent, child], [8192, -8192, 24576, 16384]),
                ];
                let inner = composite(&records, false);
                let outer = composite(
                    &[record(0x883, 1, [13, 65531], [-16384, 8192, 4096, 16384])],
                    false,
                );
                cases.push((0, font(&[simple(), inner, outer]), 2));
            }
        }
    }
    for args in [[1, 3], [65535, 65533], [0x8000, 0x7fff]] {
        for scale in [8192, -8192, 24576, -24576] {
            cases.push((
                0,
                font(&[
                    simple(),
                    composite(&[record(0x80f, 0, args, [scale, 0, 0, 0])], false),
                ]),
                1,
            ));
        }
    }
    // Original depth boundary and cycles, including a valid sibling after omission.
    for depth in 0..=36 {
        for flags in [2, 0x83, 0x887] {
            let mut glyphs = vec![simple()];
            for i in 0..depth {
                glyphs.push(composite(
                    &[record(
                        flags,
                        i as u16,
                        [1, 65533],
                        [16383, 12345, -23456, 8191],
                    )],
                    false,
                ));
            }
            cases.push((0, font(&glyphs), depth as u16));
        }
    }
    for child in [0, 1, 2, 65535] {
        let g = composite(
            &[
                record(0x22, child, [1, 2], [0; 4]),
                record(2, 1, [3, 4], [0; 4]),
            ],
            false,
        );
        cases.push((0, font(&[g, simple()]), 0));
    }
    let valid = composite(
        &[
            record(0x1a3, 0, [7, 9], [-8192, 16384, -32768, 32767]),
            record(0x103, 0, [0xffff, 13], [0; 4]),
        ],
        true,
    );
    for len in 0..valid.len() {
        cases.push((0, font(&[simple(), valid[..len].to_vec()]), 1));
    }
    for i in 0..valid.len() {
        for value in [0, 1, 127, 255] {
            let mut g = valid.clone();
            g[i] = value;
            cases.push((0, font(&[simple(), g]), 1));
        }
    }
    // A malformed child is still measured before an invalid attachment is omitted.
    let bad = vec![0, 1, 0];
    cases.push((
        0,
        font(&[bad, composite(&[record(0, 0, [255, 255], [0; 4])], false)]),
        1,
    ));
    for (index, data) in fixture::corpus() {
        cases.push((index, data, 0));
    }
    cases
}
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
