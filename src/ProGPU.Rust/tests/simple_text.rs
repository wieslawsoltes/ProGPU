//! Original simple UTF-16 glyph mapping, atomic output and exact advance controls.
use progpu::{
    font::{FontError, SfntFace},
    text::*,
};
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;

#[derive(Clone)]
struct Build {
    data: Vec<u8>,
    face: u32,
    text: Vec<u16>,
    blank: u16,
    hyphen: u16,
    clusters: usize,
    glyphs: usize,
}
#[derive(Clone)]
struct Advances {
    text: Vec<u16>,
    clusters: Vec<u16>,
    glyphs: Vec<u16>,
    metrics: Vec<SimpleGlyphMetrics>,
    scale: SimpleAdvanceScale,
    state: usize,
    output: usize,
}
enum Case {
    Decode(Vec<u16>),
    Build(Build),
    Advances(Advances),
}
fn scale() -> SimpleAdvanceScale {
    SimpleAdvanceScale {
        units_per_em: 1000,
        font_em_size: 10.0,
        scaling_factor: 1.0,
        is_sideways: false,
    }
}
fn metrics(width: u32, height: u32) -> SimpleGlyphMetrics {
    SimpleGlyphMetrics {
        advance_width: width,
        advance_height: height,
    }
}
fn sample() -> Vec<u16> {
    vec![0x41, 0xd83d, 0xde00, 0xad, 10, 0x85, 0xd800]
}
fn advances() -> Advances {
    Advances {
        text: sample(),
        clusters: vec![0, 1, 1, 2, 3, 4, 5],
        glyphs: vec![3, 7, 6, 2, 2, 0],
        metrics: vec![
            metrics(250, 300),
            metrics(350, 400),
            metrics(450, 500),
            metrics(999, 999),
            metrics(999, 999),
            metrics(550, 600),
        ],
        scale: scale(),
        state: 8,
        output: 8,
    }
}
fn build(data: Vec<u8>, text: Vec<u16>) -> Build {
    let len = text.len();
    Build {
        data,
        face: 0,
        text,
        blank: 2,
        hyphen: 6,
        clusters: len + 2,
        glyphs: len + 2,
    }
}
fn corpus() -> Vec<Case> {
    let mut cases = vec![
        Case::Decode(vec![]),
        Case::Decode(sample()),
        Case::Decode((0..=u16::MAX).collect()),
    ];
    // Every supplementary scalar, with each low half also independently queried.
    let mut pairs = Vec::with_capacity(2 * 1024 * 1024);
    for hi in 0xd800..=0xdbff {
        for lo in 0xdc00..=0xdfff {
            pairs.extend([hi, lo]);
        }
    }
    cases.push(Case::Decode(pairs));
    for first in [
        0, 0x41, 0xd7ff, 0xd800, 0xdbff, 0xdc00, 0xdfff, 0xe000, 0xffff,
    ] {
        for second in [
            0, 0x41, 0xd7ff, 0xd800, 0xdbff, 0xdc00, 0xdfff, 0xe000, 0xffff,
        ] {
            cases.push(Case::Decode(vec![first, second]));
        }
    }
    let data = fixture::base();
    for blank in [0, 2, 65535] {
        for hyphen in [0, 6, 65535] {
            for text in [
                vec![],
                sample(),
                vec![0, 0x1f, 0x20, 0x7e, 0x7f, 0x9f, 0xa0, 0xad],
                vec![0xfe0f, 0x200d, 0x2028, 0x2060],
            ] {
                let mut b = build(data.clone(), text);
                b.blank = blank;
                b.hyphen = hyphen;
                cases.push(Case::Build(b));
            }
        }
    }
    for n in [65535, 65536, 65537] {
        for pair in [false, true] {
            let text = if pair {
                [0xd83d, 0xde00].repeat(n)
            } else {
                vec![0x41; n]
            };
            cases.push(Case::Decode(text.clone()));
            let b = build(data.clone(), text);
            cases.push(Case::Build(b));
        }
    }
    for cc in 0..=9 {
        for gc in 0..=9 {
            let mut b = build(data.clone(), sample());
            b.clusters = cc;
            b.glyphs = gc;
            cases.push(Case::Build(b));
        }
    }
    for (face, data) in fixture::corpus() {
        for text in [sample(), vec![0, 0xad, 0x85], vec![]] {
            let mut b = build(data.clone(), text);
            b.face = face;
            cases.push(Case::Build(b));
        }
    }
    // Every BMP unit is mapped through Unicode and symbol cmap selections.
    for symbol in [false, true] {
        let mut t = fixture::tables(fixture::cmap(&[(
            3,
            if symbol { 0 } else { 1 },
            fixture::format4(symbol, false),
        )]));
        for empty in [false, true] {
            if empty {
                t.retain(|(tag, _)| *tag != *b"cmap");
            }
            cases.push(Case::Build(build(
                fixture::sfnt(&t, 0),
                (0..=u16::MAX).collect(),
            )));
        }
    }
    let base = advances();
    for sideways in [false, true] {
        let mut a = base.clone();
        a.scale.is_sideways = sideways;
        cases.push(Case::Advances(a));
    }
    for text in [
        vec![0x41, 10],
        vec![10, 0x41],
        vec![0xd83d, 0xde00],
        vec![0xad, 0],
        vec![0, 0xad],
        vec![0x200d, 0x7f],
    ] {
        for clusters in [
            vec![0, 0],
            vec![0, 1],
            vec![1, 0],
            vec![65535, 0],
            vec![0, 65535],
            vec![65535, 65535],
        ] {
            let mut a = base.clone();
            a.text = text.clone();
            a.clusters = clusters;
            cases.push(Case::Advances(a));
        }
    }
    // Capacity precedence over all other request failures; scratch and output tails.
    for cl in 0..=9 {
        for ml in 0..=8 {
            for sl in [0, 5, 6, 8] {
                for ol in [0, 5, 6, 8] {
                    let mut a = base.clone();
                    a.clusters.resize(cl, 65535);
                    a.metrics.resize(ml, metrics(1, 2));
                    a.state = sl;
                    a.output = ol;
                    if (cl + ml + sl + ol) % 3 == 0 {
                        a.scale.font_em_size = f64::NAN;
                    }
                    cases.push(Case::Advances(a));
                }
            }
        }
    }
    let values = [
        0.0,
        -0.0,
        0.5,
        -0.5,
        1.5,
        -1.5,
        2.5,
        -2.5,
        10.0,
        1.0 / 3.0,
        f64::MIN_POSITIVE,
        f64::from_bits(1),
        f64::MAX,
        -f64::MAX,
        f64::INFINITY,
        f64::NEG_INFINITY,
        f64::NAN,
        2147483647.0,
        2147483647.5,
        -2147483648.0,
        -2147483648.5,
    ];
    for upem in [0, 1, 7, 1000, 65535] {
        for em in values {
            for factor in values {
                for sideways in [false, true] {
                    let mut a = base.clone();
                    a.scale = SimpleAdvanceScale {
                        units_per_em: upem,
                        font_em_size: em,
                        scaling_factor: factor,
                        is_sideways: sideways,
                    };
                    cases.push(Case::Advances(a));
                }
            }
        }
    }
    for width in [0, 1, 2, 3, 65535, 2147483647, 2147483648, u32::MAX] {
        for slot in 0..6 {
            let mut a = base.clone();
            a.metrics[slot] = metrics(width, u32::MAX - width);
            a.scale.units_per_em = 1;
            cases.push(Case::Advances(a));
        }
    }
    // Full width inventory and adjacent representable half ties; unmapped metrics
    // retain real advances, even beyond the simple-run creation capacity boundary.
    for factor in [
        0.5,
        -0.5,
        f64::from_bits(0x3fe0000000000001),
        f64::from_bits(0x3fdfffffffffffff),
        1.0 / 3.0,
    ] {
        let mut a = base.clone();
        a.text.clear();
        a.clusters.clear();
        a.glyphs = vec![65535; 65537];
        a.metrics = (0..=65536).map(|i| metrics(i, 65536 - i)).collect();
        a.state = 65539;
        a.output = 65539;
        a.scale = SimpleAdvanceScale {
            units_per_em: 1,
            font_em_size: factor,
            scaling_factor: 1.0,
            is_sideways: false,
        };
        cases.push(Case::Advances(a));
    }
    // Deterministic raw IEEE requests exercise intermediate overflow/underflow.
    let mut seed = 0x9e3779b97f4a7c15u64;
    for _ in 0..16384 {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        let mut a = base.clone();
        a.scale.font_em_size = f64::from_bits(seed);
        a.scale.scaling_factor = f64::from_bits(seed.rotate_left(23));
        a.scale.units_per_em = seed as u16;
        a.scale.is_sideways = seed & 1 != 0;
        a.metrics[5] = metrics(seed as u32, (seed >> 32) as u32);
        cases.push(Case::Advances(a));
    }
    cases
}
fn emit(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_ne_bytes());
}
fn u16s(out: &mut Vec<u8>, v: &[u16]) {
    emit(out, v.len() as u32);
    for n in v {
        out.extend(n.to_ne_bytes());
    }
}
fn encode(c: &Case, out: &mut Vec<u8>) {
    match c {
        Case::Decode(t) => {
            emit(out, 0);
            u16s(out, t);
        }
        Case::Build(b) => {
            emit(out, 1);
            emit(out, b.data.len() as u32);
            out.extend(&b.data);
            emit(out, b.face);
            u16s(out, &b.text);
            for v in [
                u32::from(b.blank),
                u32::from(b.hyphen),
                b.clusters as u32,
                b.glyphs as u32,
            ] {
                emit(out, v);
            }
        }
        Case::Advances(a) => {
            emit(out, 2);
            u16s(out, &a.text);
            u16s(out, &a.clusters);
            u16s(out, &a.glyphs);
            emit(out, a.metrics.len() as u32);
            for m in &a.metrics {
                emit(out, m.advance_width);
                emit(out, m.advance_height);
            }
            emit(out, u32::from(a.scale.units_per_em));
            out.extend(a.scale.font_em_size.to_ne_bytes());
            out.extend(a.scale.scaling_factor.to_ne_bytes());
            emit(out, u32::from(a.scale.is_sideways));
            emit(out, a.state as u32);
            emit(out, a.output as u32);
        }
    }
}
fn snapshot(c: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    match c {
        Case::Decode(t) => {
            match simple_glyph_run_requirements(t) {
                Ok(r) => {
                    emit(&mut out, 0);
                    emit(&mut out, r.cluster_map_count);
                    emit(&mut out, r.glyph_count);
                }
                Err(e) => emit(&mut out, e as u32),
            };
            for i in 0..=t.len() {
                match read_simple_code_point(t, i) {
                    Ok((cp, n)) => {
                        emit(&mut out, 0);
                        emit(&mut out, cp);
                        emit(&mut out, n as u32);
                        emit(&mut out, u32::from(is_simple_formatting_control(cp)));
                    }
                    Err(e) => emit(&mut out, e as u32),
                }
            }
        }
        Case::Build(b) => {
            let f = match SfntFace::new(b.data.clone(), b.face) {
                Ok(f) => {
                    emit(&mut out, 0);
                    f
                }
                Err(e) => {
                    emit(&mut out, e as u32);
                    return out;
                }
            };
            let mut clusters = vec![0xbeef; b.clusters];
            let mut glyphs = vec![0xcafe; b.glyphs];
            match build_simple_glyph_run_into(
                &f,
                &b.text,
                b.blank,
                b.hyphen,
                &mut clusters,
                &mut glyphs,
            ) {
                Ok(n) => {
                    emit(&mut out, 0);
                    emit(&mut out, n);
                }
                Err(e) => {
                    emit(&mut out, e as u32);
                    emit(&mut out, 0);
                }
            }
            for v in clusters.into_iter().chain(glyphs) {
                emit(&mut out, u32::from(v));
            }
        }
        Case::Advances(a) => {
            let mut state = vec![0xa5; a.state];
            let mut output = vec![-1234567; a.output];
            let result = fill_simple_glyph_advances(
                &a.text,
                &a.clusters,
                &a.glyphs,
                &a.metrics,
                a.scale,
                &mut state,
                &mut output,
            );
            emit(&mut out, result.err().map_or(0, |e| e as u32));
            for v in state {
                emit(&mut out, u32::from(v));
            }
            for v in output {
                emit(&mut out, v as u32);
            }
        }
    }
    out
}
#[test]
fn owned_run_preserves_utf16_source_and_native_control_rules() {
    let f = SfntFace::new(fixture::base(), 0).unwrap();
    let mut text = sample();
    let run = SimpleGlyphRun::new(&f, &text, 2, 6).unwrap();
    assert_eq!(run.source().data().as_ptr(), f.data().as_ptr());
    drop(f);
    text.fill(0);
    assert_eq!(run.text(), sample());
    assert_eq!(run.cluster_map(), [0, 1, 1, 2, 3, 4, 5]);
    assert_eq!(run.glyph_indices(), [3, 7, 6, 2, 2, 0]);
    let mut state = [99; 8];
    let mut output = [99; 8];
    run.fill_advances_into(&advances().metrics, scale(), &mut state, &mut output)
        .unwrap();
    assert_eq!(output, [2, 4, 4, 0, 0, 6, 99, 99]);
    assert_eq!(state, [2, 2, 2, 1, 1, 2, 99, 99]);
    let mut side = scale();
    side.is_sideways = true;
    run.fill_advances_into(&advances().metrics, side, &mut state, &mut output)
        .unwrap();
    assert_eq!(output, [3, 4, 5, 0, 0, 6, 99, 99]);
}
#[test]
fn numeric_preflight_keeps_output_and_classifies_scratch() {
    let mut a = advances();
    a.metrics[5] = metrics(u32::MAX, u32::MAX);
    a.scale.units_per_em = 1;
    let mut state = [99; 8];
    let mut output = [99; 8];
    assert_eq!(
        fill_simple_glyph_advances(
            &a.text,
            &a.clusters,
            &a.glyphs,
            &a.metrics,
            a.scale,
            &mut state,
            &mut output
        ),
        Err(FontError::InvalidArgument)
    );
    assert_eq!(output, [99; 8]);
    assert_eq!(state, [2, 2, 2, 1, 1, 2, 99, 99]);
    a.scale.font_em_size = f64::NAN;
    state.fill(99);
    assert_eq!(
        fill_simple_glyph_advances(
            &a.text,
            &a.clusters,
            &a.glyphs,
            &a.metrics,
            a.scale,
            &mut state[..5],
            &mut output
        ),
        Err(FontError::Capacity)
    );
    assert_eq!(state, [99; 8]);
}
#[test]
fn missing_cmap_allows_only_original_control_mapping_and_atomic_empty_runs() {
    let mut t = fixture::tables(vec![]);
    t.retain(|(tag, _)| *tag != *b"cmap");
    let f = SfntFace::new(fixture::sfnt(&t, 0), 0).unwrap();
    assert_eq!(
        SimpleGlyphRun::new(&f, &[0, 0xad], 9, 0)
            .unwrap()
            .glyph_indices(),
        [9, 9]
    );
    assert_eq!(
        SimpleGlyphRun::new(&f, &[], 9, 0).unwrap().glyph_indices(),
        []
    );
    let mut c = [77; 3];
    let mut g = [88; 3];
    assert_eq!(
        build_simple_glyph_run_into(&f, &[0, 0x41], 9, 0, &mut c, &mut g),
        Err(FontError::InvalidFace)
    );
    assert_eq!(c, [77; 3]);
    assert_eq!(g, [88; 3]);
    assert_eq!(
        read_simple_code_point(&[0xd83d, 0xde00], 1),
        Ok((0xde00, 1))
    );
}
#[test]
fn simple_run_limit_retains_the_last_representable_cluster() {
    let f = SfntFace::new(fixture::base(), 0).unwrap();
    let text = vec![0x41; 65536];
    let run = SimpleGlyphRun::new(&f, &text, 0, 0).unwrap();
    assert_eq!(run.glyph_indices().len(), 65536);
    assert_eq!(run.cluster_map()[65535], 65535);
    assert_eq!(
        simple_glyph_run_requirements(&vec![0x41; 65537]),
        Err(FontError::InvalidArgument)
    );
}
#[test]
#[ignore = "requires original C++20 sources; mandatory simple text source qualification"]
fn original_simple_text_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        std::env::temp_dir().join(format!("progpu-simple-text-oracle-{}", std::process::id()));
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
        .arg(root.join("src/ProGPU.Rust/tests/native_simple_text.cpp"));
    // Original compiler defaults, including floating-point contraction, stay intact.
    for file in ["sfnt", "sfnt_simple_glyph_shaper"] {
        command.arg(root.join(format!(
            "src/ProGPU.Native/src/Text/progpu_native_{file}.cpp"
        )));
    }
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let cases = corpus();
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    for c in &cases {
        encode(c, &mut input);
        expected.extend(snapshot(c));
        ends.push(expected.len());
    }
    let input_path = dir.join("input.bin");
    std::fs::write(&input_path, input).unwrap();
    let actual = Command::new(exe)
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
        panic!(
            "simple text case {case} byte {}: native {native:#010x}, rust {rust:#010x}",
            byte - ends.get(case.wrapping_sub(1)).copied().unwrap_or(0)
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_simple_text: {} inputs, {} exact bytes; original compiler defaults",
        cases.len(),
        expected.len()
    );
}

#[test]
#[ignore = "requires .NET 10 SDK and original managed source; mandatory managed simple text control"]
fn original_managed_simple_text_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        std::env::temp_dir().join(format!("progpu-managed-simple-text-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let escape = |p: &std::path::Path| {
        p.display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.join("Control.csproj"), format!(
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><Compile Include=\"{}\"/></ItemGroup></Project>",
        escape(&root.join("src/ProGPU.Text/SfntSimpleGlyphShaper.cs")), escape(&root.join("src/ProGPU.Rust/tests/managed_simple_text.cs")))).unwrap();
    let dotnet = std::env::var_os("PROGPU_RUST_DOTNET").unwrap_or_else(|| "dotnet".into());
    assert!(
        Command::new(&dotnet)
            .current_dir(&dir)
            .args([
                "build",
                "Control.csproj",
                "-c",
                "Release",
                "-o",
                "out",
                "--nologo",
                "-v:q"
            ])
            .status()
            .unwrap()
            .success()
    );
    let (mut input, mut expected, mut checks) = (Vec::new(), Vec::new(), 0usize);
    let base = fixture::base();
    for c in corpus() {
        match c {
            Case::Decode(text) => {
                emit(&mut input, 0);
                u16s(&mut input, &text);
                for i in 0..text.len() {
                    let (point, units) = read_simple_code_point(&text, i).unwrap();
                    emit(&mut expected, point);
                    emit(&mut expected, units as u32);
                    emit(
                        &mut expected,
                        u32::from(is_simple_formatting_control(point)),
                    );
                }
            }
            Case::Build(b) if b.data == base && b.face == 0 => {
                emit(&mut input, 1);
                u16s(&mut input, &b.text);
                input.extend(b.blank.to_ne_bytes());
                input.extend(b.hyphen.to_ne_bytes());
                match SimpleGlyphRun::new(
                    &SfntFace::new(base.clone(), 0).unwrap(),
                    &b.text,
                    b.blank,
                    b.hyphen,
                ) {
                    Ok(run) => {
                        emit(&mut expected, 0);
                        emit(&mut expected, run.cluster_map().len() as u32);
                        emit(&mut expected, run.glyph_indices().len() as u32);
                        for &v in run.cluster_map().iter().chain(run.glyph_indices()) {
                            emit(&mut expected, u32::from(v));
                        }
                    }
                    Err(e) => emit(&mut expected, e as u32),
                }
            }
            Case::Advances(mut a) if a.glyphs.len() <= 65536 => {
                // The managed callback is glyph-ID indexed. Give each occurrence
                // a unique original slot so it selects exactly the supplied metric.
                a.glyphs = (0..a.glyphs.len()).map(|i| i as u16).collect();
                let mut state = vec![0; a.glyphs.len()];
                let mut output = vec![-1234567; a.glyphs.len() + 2];
                if fill_simple_glyph_advances(
                    &a.text,
                    &a.clusters,
                    &a.glyphs,
                    &a.metrics,
                    a.scale,
                    &mut state,
                    &mut output,
                )
                .is_err()
                {
                    continue;
                }
                emit(&mut input, 2);
                u16s(&mut input, &a.text);
                u16s(&mut input, &a.clusters);
                u16s(&mut input, &a.glyphs);
                emit(&mut input, a.metrics.len() as u32);
                for m in &a.metrics {
                    emit(&mut input, m.advance_width);
                    emit(&mut input, m.advance_height);
                }
                input.extend(a.scale.units_per_em.to_ne_bytes());
                input.extend(a.scale.font_em_size.to_ne_bytes());
                input.extend(a.scale.scaling_factor.to_ne_bytes());
                input.push(u8::from(a.scale.is_sideways));
                for v in output {
                    emit(&mut expected, v as u32);
                }
            }
            _ => continue,
        }
        checks += 1;
    }
    let path = dir.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = Command::new(dotnet)
        .current_dir(&dir)
        .arg("out/Control.dll")
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(
        actual.status.success(),
        "{}",
        String::from_utf8_lossy(&actual.stderr)
    );
    assert_eq!(actual.stdout.len(), expected.len());
    assert_eq!(
        actual
            .stdout
            .iter()
            .zip(&expected)
            .position(|(a, b)| a != b),
        None
    );
    eprintln!(
        "managed_simple_text: {checks} controls, {} exact bytes; original managed source",
        expected.len()
    );
}
