//! Original legacy/strict HVAR boundaries, prepared generations and gvar precedence.
use progpu::font::{FontError, SfntFace};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;
#[path = "support/item_variation_fixture.rs"]
mod item_fixture;

fn hvar(long: bool, advance: Option<&[u8]>, bearing: Option<&[u8]>) -> Vec<u8> {
    let store = item_fixture::store(
        1,
        &[[0, 7153, 16384], [-16384, -9973, 0], [0, 0, 0]],
        &[
            item_fixture::table(
                &[0, 1, 2],
                if long { 0x8002 } else { 2 },
                &[
                    vec![if long { 123456789 } else { 120 }, -30, 7],
                    vec![-90, 110, -5],
                    vec![1, 2, 3],
                ],
            ),
            vec![],
        ],
    );
    let mut data = vec![0; 20];
    fixture::u32_at(&mut data, 0, 0x10000);
    fixture::u32_at(&mut data, 4, 20);
    // The original advance and LSB routes do not interpret the right-bearing map.
    fixture::u32_at(&mut data, 16, u32::MAX);
    data.extend(store);
    for (at, map) in [(8, advance), (12, bearing)] {
        if let Some(map) = map {
            let offset = data.len() as u32;
            fixture::u32_at(&mut data, at, offset);
            data.extend(map);
        }
    }
    data
}
fn tables(hvar: Option<Vec<u8>>) -> Vec<([u8; 4], Vec<u8>)> {
    let mut tables = fixture::tables(vec![]);
    let mut fvar = vec![0; 36];
    fixture::u32_at(&mut fvar, 0, 0x10000);
    fixture::u16_at(&mut fvar, 4, 16);
    fixture::u16_at(&mut fvar, 8, 1);
    fixture::u16_at(&mut fvar, 10, 20);
    tables.push((*b"fvar", fvar));
    if let Some(data) = hvar {
        tables.push((*b"HVAR", data));
    }
    tables
}
const ADVANCE: &[u8] = &[0, 1, 0, 3, 0, 2, 1];
const BEARING: &[u8] = &[0, 1, 0, 2, 1, 0];
#[derive(Clone)]
struct Case {
    data: Vec<u8>,
    face: u32,
    glyph: u16,
    count: u32,
    coordinate: i16,
}
fn case(data: Vec<u8>) -> Case {
    Case {
        data,
        face: 0,
        glyph: 0,
        count: 1,
        coordinate: 4000,
    }
}
fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    for long in [false, true] {
        let base = fixture::sfnt(&tables(Some(hvar(long, Some(ADVANCE), Some(BEARING)))), 0);
        for coordinate in i16::MIN..=i16::MAX {
            let mut c = case(base.clone());
            c.coordinate = coordinate;
            cases.push(c);
        }
        for len in 0..base.len() {
            cases.push(case(base[..len].to_vec()));
        }
        for at in 0..base.len() {
            for value in [0, 1, 127, 128, 255] {
                let mut data = base.clone();
                data[at] = value;
                cases.push(case(data));
            }
        }
    }
    let base = fixture::sfnt(&tables(Some(hvar(false, Some(ADVANCE), Some(BEARING)))), 0);
    for glyph in 0..=u16::MAX {
        let mut c = case(base.clone());
        c.glyph = glyph;
        cases.push(c);
    }
    for advance in [
        None,
        Some(vec![0, 0, 0, 0]),
        Some(vec![1, 0x31, 0, 0, 0, 1, 255, 255, 255, 255]),
        Some(ADVANCE.to_vec()),
        Some(vec![2, 0, 0, 1, 0]),
    ] {
        for bearing in [
            None,
            Some(vec![0, 0, 0, 0]),
            Some(BEARING.to_vec()),
            Some(vec![255]),
        ] {
            let h = hvar(false, advance.as_deref(), bearing.as_deref());
            for count in 0..=2 {
                for glyph in [0, 1, 2, 3, 7, 8, 65535] {
                    let mut c = case(fixture::sfnt(&tables(Some(h.clone())), 0));
                    c.count = count;
                    c.glyph = glyph;
                    cases.push(c);
                }
            }
        }
    }
    // Independent complete raw map-entry format inventory.
    for format in 0..=255 {
        let map = [0, format, 0, 1, 255, 255, 255, 255];
        cases.push(case(fixture::sfnt(
            &tables(Some(hvar(false, Some(&map), Some(&map)))),
            0,
        )));
    }
    // Short/zero-store/version headers preserve advance-only versus strict policy.
    for len in 0..=24 {
        for version in [0, 0x10000, 0x20000, u32::MAX] {
            let mut h = vec![0; len];
            if len >= 4 {
                fixture::u32_at(&mut h, 0, version);
            }
            for count in 0..=2 {
                let mut c = case(fixture::sfnt(&tables(Some(h.clone())), 0));
                c.count = count;
                cases.push(c);
            }
        }
    }
    let normal = tables(Some(hvar(false, Some(ADVANCE), Some(BEARING))));
    for tag in [
        *b"head", *b"hhea", *b"hmtx", *b"maxp", *b"glyf", *b"loca", *b"fvar", *b"HVAR",
    ] {
        for length in [None, Some(0), Some(1), Some(16), Some(35)] {
            let mut t = normal.clone();
            if let Some(length) = length {
                if let Some((_, v)) = t.iter_mut().find(|(k, _)| *k == tag) {
                    v.truncate(length)
                }
            } else {
                t.retain(|(k, _)| *k != tag)
            }
            for count in 0..=2 {
                let mut c = case(fixture::sfnt(&t, 0));
                c.count = count;
                cases.push(c);
            }
        }
    }
    // Original gvar fallback; HVAR activation bypasses even a malformed gvar.
    for active in [false, true] {
        for gvar in [phantom_font_table(), vec![0]] {
            let mut t = tables(active.then(|| hvar(false, None, None)));
            t.push((*b"gvar", gvar));
            for glyph in [0, 3, 7, 8, 65535] {
                for count in 0..=2 {
                    let mut c = case(fixture::sfnt(&t, 0));
                    c.glyph = glyph;
                    c.count = count;
                    cases.push(c);
                }
            }
        }
    }
    for (face, data) in fixture::corpus() {
        let mut c = case(data);
        c.face = face;
        cases.push(c);
    }
    for bytes in [vec![], vec![0], vec![0, 13], vec![0, 17, 0, 19]] {
        let mut t = tables(None);
        t.push((*b"hmtx", bytes));
        for mode in 0..3 {
            let mut data = fixture::sfnt(&t, 0);
            let record = 12 + (t.len() - 1) * 16;
            if mode == 1 {
                fixture::u32_at(&mut data, record + 8, u32::MAX);
            }
            if mode == 2 {
                fixture::u32_at(&mut data, record + 8, 0);
                fixture::u32_at(&mut data, record + 12, 0);
            }
            for glyph in [0, 1, 65535] {
                let mut c = case(data.clone());
                c.glyph = glyph;
                cases.push(c);
            }
        }
    }
    cases
}
fn phantom_font_table() -> Vec<u8> {
    // Eight original slots. Glyph zero is empty, so its four items are phantoms.
    let mut data = vec![0; 56];
    fixture::u32_at(&mut data, 0, 0x10000);
    fixture::u16_at(&mut data, 4, 1);
    fixture::u32_at(&mut data, 8, 56);
    fixture::u16_at(&mut data, 12, 8);
    fixture::u16_at(&mut data, 14, 1);
    fixture::u32_at(&mut data, 16, 56);
    for i in 1..=8 {
        fixture::u32_at(&mut data, 20 + i * 4, 18);
    }
    data.extend([
        0, 1, 0, 10, 0, 8, 0xa0, 0, 0x40, 0, 2, 1, 0, 1, 1, 7, 19, 0x81,
    ]);
    data
}
fn emit(out: &mut Vec<u8>, value: u32) {
    out.extend(value.to_ne_bytes());
}
fn scalar(out: &mut Vec<u8>, r: Result<f32, FontError>) {
    match r {
        Ok(v) => {
            emit(out, 0);
            emit(out, v.to_bits());
        }
        Err(e) => emit(out, e as u32),
    }
}
fn optional(out: &mut Vec<u8>, r: Result<Option<f32>, FontError>) {
    match r {
        Ok(v) => {
            emit(out, 0);
            emit(out, u32::from(v.is_some()));
            emit(out, v.unwrap_or(0.0).to_bits());
        }
        Err(e) => emit(out, e as u32),
    }
}
fn snapshot(c: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    let f = match SfntFace::new(c.data.clone(), c.face) {
        Ok(f) => {
            emit(&mut out, 0);
            f
        }
        Err(e) => {
            emit(&mut out, e as u32);
            return out;
        }
    };
    let coords = vec![c.coordinate; c.count as usize];
    let g = c.glyph;
    match f.glyph_variation_item_count(g) {
        Ok(v) => {
            emit(&mut out, 0);
            emit(&mut out, v);
        }
        Err(e) => emit(&mut out, e as u32),
    }
    optional(&mut out, f.horizontal_advance_variation(g, &coords));
    match f.horizontal_advance_variation_region_count(&coords) {
        Ok(v) => {
            emit(&mut out, 0);
            emit(&mut out, u32::from(v.is_some()));
            emit(&mut out, u32::from(v.unwrap_or(0)));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    scalar(&mut out, f.design_advance_width_hvar(g, &coords));
    scalar(&mut out, f.design_advance_width(g, &coords));
    match f.prepare_horizontal_advance_variation(&coords) {
        Ok(v) => {
            emit(&mut out, 0);
            emit(&mut out, u32::from(v.uses_hvar()));
            emit(&mut out, v.region_scalars().len() as u32);
            for s in v.region_scalars() {
                emit(&mut out, s.to_bits());
            }
            scalar(&mut out, v.design_advance_width_hvar(g));
            scalar(&mut out, v.design_advance_width(g));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    match f.prepare_horizontal_metrics_variation(&coords) {
        Ok(v) => {
            emit(&mut out, 0);
            emit(&mut out, u32::from(v.advance().uses_hvar()));
            optional(&mut out, v.left_side_bearing_variation(g));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    out
}
#[test]
fn prepared_owner_keeps_original_coordinates_maps_and_scalars() {
    let f = SfntFace::new(
        fixture::sfnt(&tables(Some(hvar(false, Some(ADVANCE), Some(BEARING)))), 0),
        0,
    )
    .unwrap();
    let mut coords = vec![7153, 123];
    let v = f.prepare_horizontal_metrics_variation(&coords).unwrap();
    assert_eq!(v.advance().source().data().as_ptr(), f.data().as_ptr());
    coords.fill(0);
    drop(f);
    assert_eq!(v.advance().coordinates(), [7153, 123]);
    assert_eq!(v.advance().region_scalars(), [1.0, 0.0, 1.0]);
    assert_eq!(v.advance().advance_variation(0), Ok(Some(127.0)));
    assert_eq!(v.advance().advance_variation(1), Ok(Some(4.0)));
    assert_eq!(v.advance().advance_variation(65535), Ok(Some(-95.0)));
    assert_eq!(v.left_side_bearing_variation(0), Ok(Some(-95.0)));
    assert_eq!(v.left_side_bearing_variation(1), Ok(Some(127.0)));
    assert_eq!(v.advance().design_advance_width(0), Ok(627.0));
}
#[test]
fn empty_request_legacy_header_and_phantom_routes_remain_distinct() {
    let t = tables(Some(vec![0; 12]));
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    assert_eq!(f.horizontal_advance_variation(0, &[]), Ok(None));
    assert_eq!(
        f.prepare_horizontal_metrics_variation(&[]).unwrap_err(),
        FontError::InvalidFace
    );
    let mut t = tables(None);
    t.push((*b"gvar", phantom_font_table()));
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    assert_eq!(f.design_advance_width_hvar(0, &[8192]), Ok(500.0));
    assert_eq!(f.design_advance_width(0, &[8192]), Ok(506.0));
    t.push((*b"HVAR", vec![0]));
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    assert_eq!(f.design_advance_width(0, &[]), Ok(500.0));
    assert_eq!(
        f.design_advance_width(0, &[8192]),
        Err(FontError::InvalidFace)
    );
}
#[test]
fn cached_base_preserves_absent_empty_and_late_glyph_errors() {
    let mut t = tables(None);
    t.retain(|(tag, _)| *tag != *b"hmtx");
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    let absent = f.prepare_horizontal_advance_variation(&[]).unwrap();
    assert_eq!(absent.design_advance_width(65535), Ok(500.0));
    t.push((*b"hmtx", vec![]));
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    let empty = f.prepare_horizontal_advance_variation(&[]).unwrap();
    assert_eq!(empty.design_advance_width(0), Err(FontError::InvalidFace));
    t.last_mut().unwrap().1 = vec![0, 13];
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    let short = f.prepare_horizontal_advance_variation(&[]).unwrap();
    assert_eq!(short.design_advance_width(0), Ok(13.0));
    assert_eq!(short.design_advance_width(1), Err(FontError::InvalidFace));
}

#[test]
#[ignore = "requires original C++20 sources; mandatory horizontal metric source qualification"]
fn original_horizontal_metric_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-horizontal-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("oracle");
    let mut command = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    command
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_horizontal.cpp"));
    if std::env::var_os("PROGPU_RUST_NATIVE_DEFAULT_FP").is_none() {
        command.arg("-ffp-contract=off");
    }
    for file in [
        "sfnt",
        "font_variations",
        "item_variation_store",
        "hvar",
        "gvar",
        "gvar_tuples",
        "gvar_packed",
        "gvar_payload",
        "gvar_phantom",
        "true_type_glyph",
        "true_type_path",
        "true_type_composite",
        "true_type_outline",
    ] {
        command.arg(root.join(format!(
            "src/ProGPU.Native/src/Text/progpu_native_{file}.cpp"
        )));
    }
    command.arg(
        root.join("src/ProGPU.Native/src/Text/Font/progpu_native_font_horizontal_metrics.cpp"),
    );
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let cases = corpus();
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    for c in &cases {
        for v in [
            c.data.len() as u32,
            c.face,
            u32::from(c.glyph),
            c.count,
            c.coordinate as u16 as u32,
        ] {
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
        let index = ends.partition_point(|end| *end <= byte);
        let word = byte & !3;
        let native = u32::from_ne_bytes(actual.stdout[word..word + 4].try_into().unwrap());
        let rust = u32::from_ne_bytes(expected[word..word + 4].try_into().unwrap());
        let c = &cases[index];
        panic!(
            "horizontal case {index} face {} glyph {} coordinate {} count {} byte {}: native {native:#010x}, rust {rust:#010x}; source {:02x?}",
            c.face,
            c.glyph,
            c.coordinate,
            c.count,
            byte - ends.get(index.wrapping_sub(1)).copied().unwrap_or(0),
            c.data
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_horizontal: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
