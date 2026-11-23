//! Owned font lifetime, native compatibility and malformed-input controls.
use progpu::font::{FontError, SfntFace, Tag};
use std::sync::Arc;
#[path = "support/font_fixture.rs"]
mod fixture;

#[test]
fn original_metrics_mapping_and_owner_lifetime() {
    let data: Arc<[u8]> = fixture::base().into();
    let face = SfntFace::new(data.clone(), 0).unwrap();
    assert_eq!(face.data().as_ptr(), data.as_ptr());
    let retained = face.select_face(0).unwrap();
    assert_eq!(retained.data().as_ptr(), face.data().as_ptr());
    assert!(face.select_face(1).is_err());
    drop(face);
    drop(data);
    assert_eq!(
        retained.header_metrics().unwrap().bounds,
        [-20, -200, 900, 800]
    );
    assert_eq!(retained.header_metrics().unwrap().units_per_em, 1000);
    assert_eq!(
        retained.horizontal_header_metrics().unwrap().descender,
        -200
    );
    assert_eq!(
        retained.horizontal_glyph_metrics(3).unwrap().advance_width,
        600
    );
    assert_eq!(
        retained
            .horizontal_glyph_metrics(3)
            .unwrap()
            .left_side_bearing,
        30
    );
    assert_eq!(
        retained
            .horizontal_glyph_metrics(u16::MAX)
            .unwrap()
            .left_side_bearing,
        0
    );
    assert_eq!(retained.glyph_count(), Some(8));
    assert_eq!(retained.glyph_index(0x41), Some(3));
    assert_eq!(retained.glyph_index(0x42), Some(0));
    assert_eq!(retained.glyph_index(0x1f600), Some(7));
    assert_eq!(retained.variation_glyph(0x41, 0xfe0f), Some(3));
    assert_eq!(retained.variation_glyph(0x42, 0xfe0f), Some(7));
    assert_eq!(retained.variation_glyph(0x43, 0xfe0f), None);
    assert!(retained.glyph_data(3).unwrap().bytes.is_empty());
    assert_eq!(retained.glyph_data(4).unwrap().bounds, [10, 0, 30, 40]);
    assert_eq!(retained.glyph_data(4).unwrap().bytes.len(), 22);
    assert_eq!(retained.table(Tag::new(*b"cmap")).unwrap().checksum, 0x1006);
    let standalone = retained.standalone_font().unwrap();
    let independent = SfntFace::new(standalone, 0).unwrap();
    drop(retained);
    assert_eq!(independent.glyph_index(0x41), Some(3));
    assert_eq!(independent.glyph_data(4).unwrap().bytes.len(), 22);
    fn thread_safe<T: Send + Sync>() {}
    thread_safe::<SfntFace>();
}

#[test]
fn symbol_precedence_and_cmap_missing_are_independent() {
    let cm = fixture::cmap(&[
        (3, 0, fixture::format4(true, false)),
        (3, 10, fixture::groups(12, 0x41, 0x41, 99)),
        (3, 1, fixture::format4(false, true)),
    ]);
    let face = SfntFace::new(fixture::sfnt(&fixture::tables(cm), 0), 0).unwrap();
    assert!(face.uses_symbol_character_map());
    assert_eq!(face.glyph_index(0x41), Some(3));
    assert_eq!(face.glyph_index(0xf041), Some(3));
    assert_eq!(face.glyph_index(0x1f600), Some(0));
    let absent = SfntFace::new(fixture::sfnt(&fixture::tables(vec![]), 0), 0).unwrap();
    assert_eq!(absent.glyph_index(0x41), None);
}

#[test]
fn duplicate_tables_and_cmap_fallback_keep_original_order() {
    let mut records = vec![
        (3, 10, fixture::groups(12, 0x41, 0x41, 0)),
        (3, 10, fixture::groups(13, 0x41, 0x43, 9)),
    ];
    let face = SfntFace::new(
        fixture::sfnt(&fixture::tables(fixture::cmap(&records)), 0),
        0,
    )
    .unwrap();
    // Native 12/13 fallback is conditional on format 4 being present.
    assert_eq!(face.glyph_index(0x41), Some(0));
    records.push((3, 1, fixture::format4(false, false)));
    let face = SfntFace::new(
        fixture::sfnt(&fixture::tables(fixture::cmap(&records)), 0),
        0,
    )
    .unwrap();
    assert_eq!(face.glyph_index(0x41), Some(9));

    let mut tables = fixture::tables(vec![]);
    tables.push((*b"head", vec![0; 54]));
    let mut data = fixture::sfnt(&tables, 0);
    let face = SfntFace::new(data.clone(), 0).unwrap();
    assert_eq!(face.header_metrics().unwrap().units_per_em, 0);
    fixture::u32_at(&mut data, 12 + 7 * 16 + 8, u32::MAX);
    let face = SfntFace::new(data, 0).unwrap();
    assert_eq!(face.header_metrics().unwrap().units_per_em, 1000);
    let standalone = SfntFace::new(face.standalone_font().unwrap(), 0).unwrap();
    assert_eq!(standalone.table_count(), 7);
    assert_eq!(standalone.header_metrics().unwrap().units_per_em, 1000);
}

#[test]
fn complete_malformed_corpus_never_panics_or_rewrites_source() {
    for (index, data) in fixture::corpus() {
        let original = data.clone();
        if let Ok(face) = SfntFace::new(data, index) {
            for code in [0, 0x41, 0x42, 0xffff, 0x1f600, 0x10ffff, u32::MAX] {
                face.glyph_index(code);
                face.variation_glyph(code, 0xfe0f);
            }
            face.header_metrics();
            face.horizontal_header_metrics();
            face.glyph_count();
            for glyph in [0, 3, 4, 7, u16::MAX] {
                face.glyph_data(glyph);
                face.horizontal_glyph_metrics(glyph);
            }
            if let Ok(output) = face.standalone_font() {
                SfntFace::new(output, 0).unwrap();
            }
            assert_eq!(face.data(), original);
        }
    }
    assert_eq!(
        SfntFace::face_count(b"wOFF12345678"),
        Err(FontError::UnsupportedContainer)
    );
    assert_eq!(
        SfntFace::face_count(b"ttcf1234\0\0\0\0"),
        Err(FontError::InvalidCollection)
    );
    assert!(matches!(
        SfntFace::new(fixture::base(), 1),
        Err(FontError::InvalidArgument)
    ));
}

fn native_snapshot(index: u32, data: Vec<u8>) -> Vec<u8> {
    let mut output = Vec::new();
    fn emit(output: &mut Vec<u8>, value: u32) {
        output.extend_from_slice(&value.to_ne_bytes());
    }
    fn payload(output: &mut Vec<u8>, data: &[u8]) {
        emit(output, data.len() as u32);
        output.extend_from_slice(data);
    }
    match SfntFace::face_count(&data) {
        Ok(count) => {
            emit(&mut output, 0);
            emit(&mut output, count);
        }
        Err(error) => emit(&mut output, error as u32),
    }
    let face = match SfntFace::new(data, index) {
        Ok(face) => {
            emit(&mut output, 0);
            face
        }
        Err(error) => {
            emit(&mut output, error as u32);
            return output;
        }
    };
    for value in [
        face.face_index(),
        face.face_offset(),
        u32::from(face.table_count()),
        u32::from(face.uses_symbol_character_map()),
    ] {
        emit(&mut output, value);
    }
    for tag in [
        *b"head", *b"hhea", *b"hmtx", *b"maxp", *b"loca", *b"glyf", *b"cmap", *b"CFF2", *b"zzzz",
    ] {
        let table = face.table(Tag::new(tag));
        emit(&mut output, u32::from(table.is_some()));
        if let Some(table) = table {
            emit(&mut output, table.checksum);
            payload(&mut output, table.bytes);
        }
    }
    let head = face.header_metrics();
    emit(&mut output, u32::from(head.is_some()));
    if let Some(head) = head {
        emit(&mut output, u32::from(head.units_per_em));
        for value in head.bounds.into_iter().chain([head.index_to_loc_format]) {
            emit(&mut output, value as u32);
        }
    }
    let horizontal = face.horizontal_header_metrics();
    emit(&mut output, u32::from(horizontal.is_some()));
    if let Some(h) = horizontal {
        for value in [
            h.ascender as u32,
            h.descender as u32,
            h.line_gap as u32,
            u32::from(h.advance_width_max),
            u32::from(h.number_of_horizontal_metrics),
        ] {
            emit(&mut output, value);
        }
    }
    let count = face.glyph_count();
    emit(&mut output, u32::from(count.is_some()));
    if let Some(count) = count {
        emit(&mut output, u32::from(count));
    }
    for glyph in [0, 1, 2, 3, 4, 5, 6, 7, u16::MAX] {
        let metrics = face.horizontal_glyph_metrics(glyph);
        emit(&mut output, u32::from(metrics.is_some()));
        if let Some(m) = metrics {
            emit(&mut output, u32::from(m.advance_width));
            emit(&mut output, m.left_side_bearing as u32);
        }
        let data = face.glyph_data(glyph);
        emit(&mut output, u32::from(data.is_some()));
        if let Some(data) = data {
            for v in [data.contour_count].into_iter().chain(data.bounds) {
                emit(&mut output, v as u32);
            }
            payload(&mut output, data.bytes);
        }
    }
    for code in [
        0,
        32,
        0x41,
        0x42,
        0x43,
        0xff,
        0xf041,
        0xffff,
        0x1f600,
        0x10ffff,
        u32::MAX,
    ] {
        let glyph = face.glyph_index(code);
        emit(&mut output, u32::from(glyph.is_some()));
        if let Some(glyph) = glyph {
            emit(&mut output, u32::from(glyph));
        }
        for selector in [0, 0xfe0f, 0xe0100, u32::MAX] {
            let glyph = face.variation_glyph(code, selector);
            emit(&mut output, u32::from(glyph.is_some()));
            if let Some(glyph) = glyph {
                emit(&mut output, u32::from(glyph));
            }
        }
    }
    match face.standalone_font() {
        Ok(data) => {
            emit(&mut output, 0);
            payload(&mut output, &data);
        }
        Err(error) => emit(&mut output, error as u32),
    }
    output
}

#[test]
#[ignore = "requires a C++20 compiler and original sources; mandatory native parity lane"]
fn original_native_sfnt_and_standalone_bytes_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let directory = std::env::temp_dir().join(format!("progpu-font-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let executable = directory.join("font-oracle");
    let status = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()))
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_font.cpp"))
        .arg(root.join("src/ProGPU.Native/src/Text/progpu_native_sfnt.cpp"))
        .arg(root.join("src/ProGPU.Native/src/Text/Metadata/progpu_native_sfnt_standalone.cpp"))
        .arg("-o")
        .arg(&executable)
        .status()
        .unwrap();
    assert!(status.success());
    let mut corpus = fixture::corpus();
    let mut fixtures: Vec<_> = std::fs::read_dir(root.join("tests/Shared"))
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|p| {
            p.extension().is_some_and(|s| s == "ttf")
                && p.file_name()
                    .unwrap()
                    .to_string_lossy()
                    .starts_with("ProGPU-Authored-")
        })
        .collect();
    fixtures.sort();
    assert!(
        fixtures.len() >= 10,
        "original authored source inventory must remain present"
    );
    for path in fixtures {
        corpus.push((0, std::fs::read(path).unwrap()));
    }
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for (index, data) in &corpus {
        input.extend_from_slice(&index.to_ne_bytes());
        input.extend_from_slice(&(data.len() as u32).to_ne_bytes());
        input.extend_from_slice(data);
        expected.extend_from_slice(&native_snapshot(*index, data.clone()));
        ends.push(expected.len());
    }
    let input_path = directory.join("input.bin");
    std::fs::write(&input_path, input).unwrap();
    let output = Command::new(&executable)
        .stdin(Stdio::from(std::fs::File::open(input_path).unwrap()))
        .output()
        .unwrap();
    assert!(output.status.success());
    if let Some(byte) = output
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        let case = ends.partition_point(|end| *end <= byte);
        panic!(
            "native font case {case} differs at byte {byte}: native={} rust={}",
            output.stdout[byte], expected[byte]
        );
    }
    assert_eq!(output.stdout.len(), expected.len());
    eprintln!(
        "native_sfnt: {} matched inputs, {} exact bytes",
        corpus.len(),
        expected.len()
    );
    // Every BMP code point across sorted, duplicate and unsorted inventories,
    // both sides of the lookup threshold, and direct/indirect/zero glyph slots.
    let inputs = fixture::format4_inventory();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    for data in &inputs {
        input.extend_from_slice(&0u32.to_ne_bytes());
        input.extend_from_slice(&(data.len() as u32).to_ne_bytes());
        input.extend_from_slice(data);
        for value in [0u32, 1, 0] {
            expected.extend_from_slice(&value.to_ne_bytes());
        }
        let face = SfntFace::new(data.clone(), 0).unwrap();
        for code in 0..=65535 {
            let glyph = face.glyph_index(code);
            expected.extend_from_slice(&u32::from(glyph.is_some()).to_ne_bytes());
            expected.extend_from_slice(&u32::from(glyph.unwrap_or(0)).to_ne_bytes());
        }
    }
    let input_path = directory.join("cmap4-input.bin");
    std::fs::write(&input_path, input).unwrap();
    let output = Command::new(&executable)
        .arg("cmap4-exhaustive")
        .stdin(Stdio::from(std::fs::File::open(input_path).unwrap()))
        .output()
        .unwrap();
    assert!(output.status.success());
    assert_eq!(output.stdout.len(), expected.len());
    if let Some(byte) = output
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        panic!("native exhaustive cmap4 differs at byte {byte}");
    }
    eprintln!(
        "native_cmap4: {} complete BMP inventories, {} exact mappings",
        inputs.len(),
        inputs.len() * 65536
    );
    std::fs::remove_dir_all(directory).unwrap();
}
