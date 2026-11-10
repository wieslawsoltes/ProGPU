//! Original TrueType decoding, ownership and exact contour topology controls.
use progpu::font::{FontError, GlyphKind, SfntFace};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;

fn font_with_glyph(glyph: Vec<u8>) -> Vec<u8> {
    let mut tables = fixture::tables(vec![]);
    for (tag, data) in &mut tables {
        match &*tag {
            b"head" => fixture::u16_at(data, 50, 1),
            b"maxp" => fixture::u16_at(data, 4, 1),
            b"loca" => {
                *data = vec![0; 8];
                fixture::u32_at(data, 4, glyph.len() as u32);
            }
            b"glyf" => *data = glyph.clone(),
            _ => {}
        }
    }
    fixture::sfnt(&tables, 0)
}
fn glyph(flags: &[u8], ends: &[u16], repeat: bool) -> Vec<u8> {
    let mut data = vec![0; 10];
    fixture::u16_at(&mut data, 0, ends.len() as u16);
    for end in ends {
        data.extend_from_slice(&end.to_be_bytes());
    }
    data.extend_from_slice(&[0, 2, 0xb0, 0x01]); // original instruction bytes retained
    for (index, &flag) in flags.iter().enumerate() {
        if repeat && index > 0 {
            continue;
        }
        data.push(flag);
        if flag & 8 != 0 {
            data.push(if repeat { (flags.len() - 1) as u8 } else { 0 });
        }
    }
    for axis in 0..2 {
        for &flag in flags {
            if flag & (2 << axis) != 0 {
                data.push(129);
            } else if flag & (16 << axis) == 0 {
                data.extend_from_slice(&(-513i16).to_be_bytes());
            }
        }
    }
    data
}
fn corpus() -> Vec<(u32, Vec<u8>, u16)> {
    let mut cases = Vec::new();
    for count in 1..=8 {
        for pattern in 0..1 << count {
            let flags: Vec<u8> = (0..count).map(|i| ((pattern >> i) & 1) as u8).collect();
            let data = glyph(&flags, &[(count - 1) as u16], false);
            cases.push((0, font_with_glyph(data), 0));
        }
    }
    for flag in 0..=255u8 {
        for repeat in [false, true] {
            let data = glyph(&[flag; 6], &[0, 2, 5], repeat && flag & 8 != 0);
            cases.push((0, font_with_glyph(data), 0));
        }
    }
    let valid = glyph(&[0x3b; 6], &[0, 2, 5], true);
    for length in 0..valid.len() {
        cases.push((0, font_with_glyph(valid[..length].to_vec()), 0));
    }
    for index in 10..valid.len() {
        for value in [0, 1, 127, 255] {
            let mut data = valid.clone();
            data[index] = value;
            cases.push((0, font_with_glyph(data), 0));
        }
    }
    // Largest original endpoint: 65,536 points, including signed accumulator limits.
    let flags = vec![0; 65536];
    cases.push((0, font_with_glyph(glyph(&flags, &[65535], false)), 0));
    for (index, data) in fixture::corpus() {
        for glyph in [0, 3, 4, 7, u16::MAX] {
            cases.push((index, data.clone(), glyph));
        }
    }
    cases
}

#[test]
fn original_points_instructions_and_contours_keep_source_ownership() {
    let source = font_with_glyph(glyph(&[0, 1, 0], &[2], false));
    let face = SfntFace::new(source.clone(), 0).unwrap();
    let decoded = face.simple_glyph(0).unwrap();
    assert_eq!(decoded.source().data().as_ptr(), face.data().as_ptr());
    assert_eq!(decoded.glyph_index(), 0);
    assert_eq!(decoded.requirements().kind, GlyphKind::Simple);
    assert_eq!(decoded.requirements().point_count, 3);
    assert_eq!(decoded.requirements().path_segment_count, 2);
    assert_eq!(decoded.contour_end_points(), [2]);
    assert_eq!(decoded.points()[2].x, -1539);
    assert_eq!(decoded.points()[2].y, -1539);
    drop(face);
    assert_eq!(decoded.instructions(), [0xb0, 1]);
    assert_eq!(decoded.source().data(), source);
    let segments = decoded.segments().unwrap();
    assert_eq!(segments.len(), 2);
    assert_eq!(segments[0].points[0], [-1026.0, -1026.0]);
    assert_eq!(segments[0].points[1], [-513.0, -513.0]);
    assert_eq!(segments[0].points[2], [-1026.0, -1026.0]);
    assert_eq!(
        decoded.segments_with_positions(&[[0.0; 2]]),
        Err(FontError::InvalidArgument)
    );
    assert_eq!(decoded.segments_with_positions(&[]).unwrap(), segments);
    let single = SfntFace::new(font_with_glyph(glyph(&[1], &[0], false)), 0)
        .unwrap()
        .simple_glyph(0)
        .unwrap();
    assert_eq!(single.points().len(), 1);
    assert!(single.segments().unwrap().is_empty());
}

#[test]
fn all_malformed_records_reject_before_publication() {
    for (index, data, glyph) in corpus() {
        if let Ok(face) = SfntFace::new(data.clone(), index) {
            let requirements = face.glyph_requirements(glyph);
            if let Ok(decoded) = face.simple_glyph(glyph) {
                assert_eq!(requirements.unwrap(), decoded.requirements());
                assert_eq!(
                    decoded.segments().unwrap().len(),
                    decoded.requirements().path_segment_count as usize
                );
                assert_eq!(decoded.source().data(), data);
            }
        }
    }
}
fn snapshot(index: u32, data: Vec<u8>, glyph: u16) -> Vec<u8> {
    fn emit(out: &mut Vec<u8>, value: u32) {
        out.extend_from_slice(&value.to_ne_bytes());
    }
    let mut out = Vec::new();
    let face = match SfntFace::new(data, index) {
        Ok(face) => {
            emit(&mut out, 0);
            face
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    match face.glyph_requirements(glyph) {
        Ok(r) => {
            for value in [
                0,
                r.kind as u32,
                u32::from(r.contour_count),
                r.point_count,
                r.path_segment_count,
                u32::from(r.instruction_bytes),
            ] {
                emit(&mut out, value);
            }
        }
        Err(e) => emit(&mut out, e as u32),
    }
    match face.simple_glyph(glyph) {
        Ok(g) => {
            emit(&mut out, 0);
            for &end in g.contour_end_points() {
                emit(&mut out, u32::from(end));
            }
            for p in g.points() {
                for v in [p.x as u32, p.y as u32, u32::from(p.flags)] {
                    emit(&mut out, v);
                }
            }
            let varied: Vec<_> = g
                .points()
                .iter()
                .map(|p| [p.x as f32 * 0.25 + 0.125, p.y as f32 * -0.5 - 0.25])
                .collect();
            for s in [
                g.segments().unwrap(),
                g.segments_with_positions(&varied).unwrap(),
            ] {
                emit(&mut out, s.len() as u32);
                out.extend_from_slice(bytemuck::cast_slice(&s));
            }
        }
        Err(e) => emit(&mut out, e as u32),
    }
    out
}

#[test]
#[ignore = "requires original C++20 sources; mandatory native outline qualification"]
fn original_native_simple_glyphs_and_path_bytes_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-outline-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("outline-oracle");
    let mut command = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    command
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_outline.cpp"));
    for file in [
        "progpu_native_sfnt.cpp",
        "progpu_native_true_type_glyph.cpp",
        "progpu_native_true_type_path.cpp",
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
        panic!(
            "outline case {case} byte {byte}: native {} rust {}",
            actual.stdout[byte], expected[byte]
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_simple_outline: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
