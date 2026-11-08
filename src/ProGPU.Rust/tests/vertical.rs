//! Original static vertical metrics and strict VVAR differential controls.
use progpu::font::{FontError, SfntFace, VerticalMetricsVariation};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;
#[path = "support/item_variation_fixture.rs"]
mod item_fixture;

fn vvar(store: Vec<u8>, maps: [Option<&[u8]>; 4]) -> Vec<u8> {
    let mut data = vec![0; 24];
    fixture::u32_at(&mut data, 0, 0x10000);
    fixture::u32_at(&mut data, 4, 24);
    data.extend(store);
    for (kind, map) in maps.into_iter().enumerate() {
        if let Some(map) = map {
            let offset = data.len() as u32;
            fixture::u32_at(&mut data, 8 + kind * 4, offset);
            data.extend(map);
        }
    }
    data
}
fn store(long: bool) -> Vec<u8> {
    item_fixture::store(
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
    )
}
const MAP: &[u8] = &[0, 1, 0, 3, 0, 2, 1];
fn tables(data: Option<Vec<u8>>) -> Vec<([u8; 4], Vec<u8>)> {
    let mut tables = fixture::tables(vec![]);
    let mut fvar = vec![0; 36];
    fixture::u32_at(&mut fvar, 0, 0x10000);
    for (at, value) in [(4, 16), (8, 1), (10, 20)] {
        fixture::u16_at(&mut fvar, at, value);
    }
    tables.push((*b"fvar", fvar));
    let mut vhea = vec![0; 36];
    for (at, value) in [(4, 901), (6, -302i16 as u16), (8, 27), (10, 1400), (34, 2)] {
        fixture::u16_at(&mut vhea, at, value);
    }
    tables.push((*b"vhea", vhea));
    let mut vmtx = vec![0; 20];
    for (at, value) in [(0, 1100), (2, 12), (4, 1200), (6, -21i16 as u16), (12, 81)] {
        fixture::u16_at(&mut vmtx, at, value);
    }
    tables.push((*b"vmtx", vmtx));
    if let Some(data) = data {
        tables.push((*b"VVAR", data));
    }
    tables
}
fn font(data: Option<Vec<u8>>) -> Vec<u8> {
    fixture::sfnt(&tables(data), 0)
}
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
        glyph: 4,
        count: 1,
        coordinate: 4000,
    }
}
fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    for long in [false, true] {
        let base = font(Some(vvar(store(long), [Some(MAP); 4])));
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
    let base = font(Some(vvar(store(false), [Some(MAP); 4])));
    for glyph in 0..=u16::MAX {
        let mut c = case(base.clone());
        c.glyph = glyph;
        cases.push(c);
    }
    // Every signed ascent drives original negative floor-half and empty/bounded origins.
    let mut t = tables(None);
    t.retain(|(tag, _)| *tag != *b"vhea");
    for ascent in i16::MIN..=i16::MAX {
        fixture::u16_at(&mut t[1].1, 4, ascent as u16);
        let data = fixture::sfnt(&t, 0);
        for glyph in [0, 4] {
            let mut c = case(data.clone());
            c.glyph = glyph;
            cases.push(c);
        }
    }
    // Each optional kind separately; null subtable, sentinel, narrowing overflow,
    // illegal map format/reserved bits and unselected invalid entries.
    let maps = [
        None,
        Some(vec![0, 0, 0, 0]),
        Some(MAP.to_vec()),
        Some(vec![0, 0x3f, 0, 1, 255, 255, 255, 255]),
        Some(vec![0, 0x3f, 0, 1, 0, 1, 255, 255]),
        Some(vec![0, 0x30, 0, 1, 0, 2, 0, 0]),
        Some(vec![1, 0, 0, 0, 0, 1, 0]),
        Some(vec![0, 1, 0, 4, 0, 1, 2, 3]),
        Some(vec![0, 1, 0, 1, 8]),
    ];
    for kind in 0..4 {
        for map in &maps {
            let mut selected = [Some(MAP); 4];
            selected[kind] = map.as_deref();
            for count in 0..=2 {
                let mut c = case(font(Some(vvar(store(false), selected))));
                c.count = count;
                cases.push(c);
            }
        }
    }
    for format in 0..=255 {
        let map = [0, format, 0, 1, 255, 255, 255, 255];
        cases.push(case(font(Some(vvar(store(false), [Some(&map); 4])))));
    }
    // Whole store validation includes unused regions and unselected subtables.
    for region in [
        [-16385, 0, 0],
        [0, 0, 16385],
        [100, 50, 200],
        [-100, 1, 100],
        [-16384, 0, 16384],
        [0, 0, 0],
    ] {
        for indices in [vec![0], vec![1], vec![65535]] {
            let s = item_fixture::store(
                1,
                &[region],
                &[
                    item_fixture::table(&[0], 1, &vec![vec![7]; 8]),
                    item_fixture::table(&indices, 1, &[vec![3]]),
                ],
            );
            cases.push(case(font(Some(vvar(s, [None; 4])))));
        }
    }
    for slots in [0, 1, 7, 8, 9] {
        let s = item_fixture::store(
            1,
            &[[0, 8192, 16384]],
            &[item_fixture::table(&[0], 1, &vec![vec![7]; slots])],
        );
        cases.push(case(font(Some(vvar(s, [None; 4])))));
    }
    cases.push(case(font(Some(vvar(
        item_fixture::store(1, &[], &[vec![]]),
        [None; 4],
    )))));
    for region_count in [32767, 32768] {
        let s = item_fixture::store(1, &vec![[0, 0, 0]; region_count], &[vec![]]);
        cases.push(case(font(Some(vvar(s, [None; 4])))));
    }
    // Missing/empty/truncated metadata, strict raw duplicates and corrupt extents.
    let normal = tables(Some(vvar(store(false), [Some(MAP); 4])));
    for tag in [
        *b"head", *b"maxp", *b"hhea", *b"loca", *b"glyf", *b"fvar", *b"vhea", *b"vmtx", *b"VVAR",
    ] {
        for length in [
            None,
            Some(0),
            Some(1),
            Some(4),
            Some(6),
            Some(15),
            Some(23),
            Some(35),
        ] {
            let mut t = normal.clone();
            if let Some(n) = length {
                t.iter_mut().find(|(k, _)| *k == tag).unwrap().1.truncate(n);
            } else {
                t.retain(|(k, _)| *k != tag);
            }
            for count in 0..=2 {
                let mut c = case(fixture::sfnt(&t, 0));
                c.count = count;
                cases.push(c);
            }
        }
        for invalid in [false, true] {
            let mut t = normal.clone();
            t.push((tag, vec![]));
            let mut data = fixture::sfnt(&t, 0);
            if invalid {
                fixture::u32_at(&mut data, 12 + (t.len() - 1) * 16 + 8, u32::MAX);
            }
            cases.push(case(data));
        }
    }
    // Original binary-search behavior, including unsorted/duplicate records,
    // truncated VORG fallback and arbitrary version words.
    for records in [
        vec![],
        vec![(4, 1001)],
        vec![(0, -501), (4, 902), (65535, 17)],
        vec![(4, 1), (4, 2), (4, 3)],
        vec![(7, 1), (4, 2), (0, 3)],
    ] {
        let mut v = vec![0; 8];
        fixture::u32_at(&mut v, 0, u32::MAX);
        fixture::u16_at(&mut v, 4, -701i16 as u16);
        fixture::u16_at(&mut v, 6, records.len() as u16);
        for (g, y) in records {
            v.extend_from_slice(&(g as u16).to_be_bytes());
            v.extend_from_slice(&(y as i16).to_be_bytes());
        }
        for len in 0..=v.len() {
            let mut t = tables(None);
            t.push((*b"VORG", v[..len].to_vec()));
            for glyph in [0, 4, 7, 8, 65535] {
                let mut c = case(fixture::sfnt(&t, 0));
                c.glyph = glyph;
                cases.push(c);
            }
        }
    }
    // Selected collection directories use original absolute offsets, even when
    // two face indices select the same directory generation.
    let mut collection = fixture::sfnt(&normal, 32);
    fixture::u32_at(&mut collection, 0, 0x74746366);
    fixture::u32_at(&mut collection, 4, 0x10000);
    fixture::u32_at(&mut collection, 8, 2);
    fixture::u32_at(&mut collection, 12, 32);
    fixture::u32_at(&mut collection, 16, 32);
    for face in 0..=2 {
        let mut c = case(collection.clone());
        c.face = face;
        cases.push(c);
    }
    // A bad map entry beyond the complete glyph inventory still rejects before
    // publication; a zero relative region offset is not an absent store.
    let mut t = tables(Some(vvar(
        store(false),
        [Some(&[0, 1, 0, 4, 0, 1, 2, 3]); 4],
    )));
    fixture::u16_at(&mut t[3].1, 4, 1);
    let mut c = case(fixture::sfnt(&t, 0));
    c.glyph = 0;
    cases.push(c);
    cases.push(case(font(Some(vvar(
        vec![0, 1, 0, 0, 0, 0, 0, 0],
        [None; 4],
    )))));
    for (face, data) in fixture::corpus() {
        let mut c = case(data);
        c.face = face;
        c.count = 0;
        cases.push(c);
    }
    cases
}
fn emit(out: &mut Vec<u8>, value: u32) {
    out.extend(value.to_ne_bytes());
}
fn optional_int(out: &mut Vec<u8>, value: Option<i32>) {
    emit(out, u32::from(value.is_some()));
    if let Some(v) = value {
        emit(out, v as u32);
    }
}
fn delta(out: &mut Vec<u8>, v: VerticalMetricsVariation) {
    for value in [
        v.advance_height,
        v.top_side_bearing.unwrap_or(0.0),
        v.bottom_side_bearing.unwrap_or(0.0),
        v.vertical_origin_y.unwrap_or(0.0),
    ] {
        emit(out, value.to_bits());
    }
    for flag in [
        v.uses_vvar,
        v.top_side_bearing.is_some(),
        v.bottom_side_bearing.is_some(),
        v.vertical_origin_y.is_some(),
    ] {
        emit(out, u32::from(flag));
    }
}
fn marker() -> VerticalMetricsVariation {
    VerticalMetricsVariation {
        advance_height: 19.0,
        top_side_bearing: Some(23.0),
        bottom_side_bearing: Some(29.0),
        vertical_origin_y: Some(31.0),
        uses_vvar: true,
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
    let h = f.vertical_header_metrics();
    emit(&mut out, u32::from(h.is_some()));
    if let Some(h) = h {
        for v in [
            i32::from(h.ascender),
            i32::from(h.descender),
            i32::from(h.line_gap),
            i32::from(h.advance_height_max),
            i32::from(h.number_of_vertical_metrics),
        ] {
            emit(&mut out, v as u32);
        }
    }
    let v = f.vertical_glyph_metrics(c.glyph);
    emit(&mut out, u32::from(v.is_some()));
    if let Some(v) = v {
        emit(&mut out, u32::from(v.advance_height));
        emit(&mut out, i32::from(v.top_side_bearing.unwrap_or(0)) as u32);
        emit(&mut out, u32::from(v.top_side_bearing.is_some()));
    }
    let b = f.glyph_bounds(c.glyph);
    emit(&mut out, u32::from(b.is_some()));
    if let Some(b) = b {
        for v in b {
            emit(&mut out, i32::from(v) as u32);
        }
    }
    optional_int(&mut out, f.design_advance_height(c.glyph));
    optional_int(&mut out, f.design_vertical_origin_y(c.glyph));
    let coords = vec![c.coordinate; c.count as usize];
    match f.vertical_metrics_variation_region_count(&coords) {
        Ok(v) => {
            emit(&mut out, 0);
            emit(&mut out, u32::from(v.is_some()));
            emit(&mut out, u32::from(v.unwrap_or(0)));
        }
        Err(e) => emit(&mut out, e as u32),
    }
    match f.prepare_vertical_metrics_variation(&coords) {
        Err(e) => emit(&mut out, e as u32),
        Ok(v) => {
            emit(&mut out, 0);
            emit(&mut out, u32::from(v.uses_vvar()));
            emit(&mut out, v.region_scalars().len() as u32);
            for s in v.region_scalars() {
                emit(&mut out, s.to_bits());
            }
            match v.variation(c.glyph) {
                Ok(v) => {
                    emit(&mut out, 0);
                    delta(&mut out, v);
                }
                Err(e) => emit(&mut out, e as u32),
            }
            for len in [2, 4] {
                let mut output = [marker(); 4];
                let result = v.variations_into(&[0, c.glyph, 1], &mut output[..len]);
                emit(&mut out, result.err().map_or(0, |e| e as u32));
                for v in output {
                    delta(&mut out, v);
                }
            }
        }
    }
    out
}
#[test]
fn owner_and_atomic_batch_keep_original_generation() {
    let f = SfntFace::new(font(Some(vvar(store(false), [Some(MAP); 4]))), 0).unwrap();
    let mut coords = [7153];
    let v = f.prepare_vertical_metrics_variation(&coords).unwrap();
    assert_eq!(v.source().data().as_ptr(), f.data().as_ptr());
    drop(f);
    coords[0] = 0;
    assert_eq!(v.coordinates(), [7153]);
    assert_eq!(v.region_scalars(), [1.0, 0.0, 1.0]);
    assert_eq!(v.variation(0).unwrap().advance_height, 127.0);
    let mut output = [marker(); 4];
    assert_eq!(
        v.variations_into(&[0, 8, 1], &mut output),
        Err(FontError::InvalidArgument)
    );
    assert_eq!(output, [marker(); 4]);
    assert_eq!(
        v.variations_into(&[0, 8, 1], &mut output[..2]),
        Err(FontError::Capacity)
    );
    assert_eq!(output, [marker(); 4]);
    v.variations_into(&[0, 1, 2], &mut output).unwrap();
    assert_eq!(output[3], marker());
    assert_eq!(output[1].advance_height, 4.0);
    assert_eq!(output[2].advance_height, -95.0);
}
#[test]
fn absence_null_and_sentinel_remain_distinct() {
    let f = SfntFace::new(font(None), 0).unwrap();
    let absent = f.prepare_vertical_metrics_variation(&[0]).unwrap();
    assert!(!absent.uses_vvar());
    assert_eq!(absent.variation(0), Ok(VerticalMetricsVariation::default()));
    assert_eq!(absent.variation(8), Err(FontError::InvalidArgument));
    assert_eq!(
        f.prepare_vertical_metrics_variation(&[]).unwrap_err(),
        FontError::InvalidArgument
    );
    let sentinel = [0, 0x3f, 0, 1, 255, 255, 255, 255];
    let null = [0, 0x3f, 0, 1, 0, 1, 255, 255];
    let f = SfntFace::new(
        font(Some(vvar(
            store(false),
            [Some(&sentinel), None, Some(&null), None],
        ))),
        0,
    )
    .unwrap();
    let v = f.prepare_vertical_metrics_variation(&[8192]).unwrap();
    let d = v.variation(7).unwrap();
    assert!(d.uses_vvar);
    assert_eq!(d.advance_height, 0.0);
    assert_eq!(d.top_side_bearing, None);
    assert_eq!(d.bottom_side_bearing, Some(0.0));
}
#[test]
fn static_origins_preserve_missing_bearing_and_floor_half() {
    let mut t = tables(None);
    t.retain(|(tag, _)| *tag != *b"vhea");
    fixture::u16_at(&mut t[1].1, 4, -201i16 as u16);
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    assert_eq!(f.design_advance_height(4), Some(-1));
    assert_eq!(f.design_vertical_origin_y(0), Some(-1));
    assert_eq!(f.design_vertical_origin_y(4), Some(19));
    assert_eq!(f.design_vertical_origin_y(8), Some(-201));
    let mut t = tables(None);
    t.iter_mut()
        .find(|(tag, _)| *tag == *b"vmtx")
        .unwrap()
        .1
        .truncate(6);
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    assert_eq!(f.vertical_glyph_metrics(4).unwrap().top_side_bearing, None);
    assert_eq!(f.design_advance_height(4), Some(1200));
    assert_eq!(f.design_vertical_origin_y(4), Some(520));
}

#[test]
#[ignore = "requires original C++20 sources; mandatory vertical metric source qualification"]
fn original_vertical_metric_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-vertical-oracle-{}", std::process::id()));
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
        .arg(root.join("src/ProGPU.Rust/tests/native_vertical.cpp"));
    if std::env::var_os("PROGPU_RUST_NATIVE_DEFAULT_FP").is_none() {
        command.arg("-ffp-contract=off");
    }
    for file in [
        "sfnt",
        "font_variations",
        "item_variation_store",
        "vvar",
        "Font/progpu_native_font_vertical_metrics",
    ] {
        let file = if file.starts_with("Font/") {
            format!("{file}.cpp")
        } else {
            format!("progpu_native_{file}.cpp")
        };
        command.arg(root.join("src/ProGPU.Native/src/Text").join(file));
    }
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
            "vertical case {index} face {} glyph {} coordinate {} count {} byte {}: native {native:#010x}, rust {rust:#010x}; source {:02x?}",
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
        "native_vertical: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
