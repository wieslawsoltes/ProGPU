//! Exact original grapheme properties, state, source ranges, and atomic writes.
use progpu::{
    font::Tag,
    text::unicode::{grapheme::*, *},
};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
const SENTINEL: GraphemeCluster = GraphemeCluster {
    input_index: 0x11111111,
    input_length: 0x22222222,
    scalar_index: 0x33333333,
    scalar_count: 0x44444444,
};
const POINTS: [u32; 22] = [
    0x41, 13, 10, 0, 0x301, 0x200d, 0x1f1e6, 0x600, 0x903, 0x1100, 0x1160, 0x11a8, 0xac00, 0xac01,
    0x1f469, 0x1f3fb, 0x915, 0x94d, 0x200c, 0xd800, 0x10ffff, 0xffffffff,
];
fn scalar(cp: u32, i: u32, n: u16) -> UnicodeScalar {
    UnicodeScalar {
        code_point: cp,
        input_index: i,
        input_length: n,
        canonical_combining_class: 99,
        reserved: 77,
        script: Tag(u32::MAX),
    }
}
fn raw(points: &[u32]) -> Vec<UnicodeScalar> {
    points
        .iter()
        .enumerate()
        .map(|(i, &cp)| scalar(cp, (i * 3) as u32, 2))
        .collect()
}
fn corpus() -> Vec<Vec<UnicodeScalar>> {
    let mut cases = vec![vec![]];
    for &a in &POINTS {
        for &b in &POINTS {
            for &c in &POINTS {
                cases.push(raw(&[a, b, c]));
            }
        }
    }
    for n in 0..=u16::MAX {
        cases.push(vec![
            scalar(0x41, u32::MAX - 31, n),
            scalar(0x301, 3, 2),
            scalar(0x42, 1, n),
        ]);
    }
    for length in 0..=260 {
        for points in [
            [0x1f1e6, 0x1f1e7],
            [0x1f469, 0x301],
            [0x915, 0x94d],
            [0x600, 0x301],
        ] {
            let mut c = points.repeat(length);
            c.extend([0x200d, 0x1f469, 0x915, 13, 10, 0x301]);
            cases.push(raw(&c));
        }
    }
    let mut random = 0x93a45f01u32;
    for _ in 0..8192 {
        random = random.wrapping_mul(1664525).wrapping_add(1013904223);
        let n = (random % 97) as usize;
        let mut c = Vec::new();
        for i in 0..n {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            c.push(scalar(
                POINTS[(random as usize) % POINTS.len()],
                if i % 7 == 0 {
                    u32::MAX - random % 50
                } else {
                    random
                },
                random as u16,
            ));
        }
        cases.push(c);
    }
    cases
}
fn emit(out: &mut Vec<u8>, n: u32) {
    out.extend(n.to_le_bytes());
}
fn result(out: &mut Vec<u8>, r: Result<u32, UnicodeError>) {
    emit(out, u32::from(r.is_ok()));
    emit(out, r.err().map_or(0, |e| e as u32));
    emit(out, r.unwrap_or(0));
}
struct Cleanup(PathBuf);
impl Drop for Cleanup {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn temp(name: &str) -> Cleanup {
    let p = std::env::temp_dir().join(format!("progpu-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&p).unwrap();
    Cleanup(p)
}
fn compare(actual: &[u8], expected: &[u8], ends: &[usize], name: &str) {
    if let Some(byte) = actual.iter().zip(expected).position(|(a, b)| a != b) {
        let case = ends.partition_point(|&e| e <= byte);
        panic!(
            "{name} case {case}, byte {byte}: native={} rust={}",
            actual[byte], expected[byte]
        );
    }
    assert_eq!(actual.len(), expected.len());
    eprintln!(
        "{name}: {} controls, {} exact bytes",
        ends.len(),
        expected.len()
    );
}
#[test]
fn original_boundaries_ownership_and_policy() {
    assert_eq!(size_of::<GraphemeCluster>(), 16);
    let source: Vec<_> = "A\u{301}🇵🇱👩\u{301}\u{200d}👧\r\nक्\u{915}"
        .encode_utf16()
        .collect();
    let text = GraphemeText::new(
        DecodedText::from_utf16(source.clone()).unwrap(),
        GraphemePolicy::Unicode17,
    )
    .unwrap();
    assert_eq!(text.clusters().len(), 5);
    assert_eq!(
        text.clusters()
            .iter()
            .map(|c| c.scalar_count)
            .collect::<Vec<_>>(),
        [2, 2, 4, 2, 3]
    );
    let clone = text.clone();
    drop(text);
    assert_eq!(clone.policy(), GraphemePolicy::Unicode17);
    match clone.text().source() {
        UnicodeSource::Utf16(v) => assert_eq!(&**v, &source),
        _ => panic!(),
    }
    let conjunct = raw(&[0x915, 0x94d, 0x915]);
    assert_eq!(cluster_count(&conjunct, GraphemePolicy::Unicode17), Ok(1));
    assert_eq!(
        cluster_count(&conjunct, GraphemePolicy::ManagedCompatible),
        Ok(2)
    );
    let mut output = [SENTINEL; 2];
    assert_eq!(
        segment_into(
            &raw(&[0x41, 0x42, 0x43]),
            GraphemePolicy::Unicode17,
            &mut output
        ),
        Err(UnicodeError::Capacity)
    );
    assert_eq!(output, [SENTINEL; 2]);
    let mut starts = vec![0xa5; source.len() + 2];
    utf16_starts_into(&source, &mut starts).unwrap();
    for c in clone.clusters() {
        assert_eq!(starts[c.input_index as usize], 1);
    }
    assert_eq!(&starts[source.len()..], &[0xa5, 0xa5]);
    for units in [vec![0xd800], vec![0x41, 0xdc00], vec![0xd800, 0x41]] {
        let mut output = [0xa5; 1];
        assert_eq!(
            utf16_starts_into(&units, &mut output),
            Err(UnicodeError::InvalidEncoding)
        );
        assert_eq!(output, [0xa5]);
    }
}
#[test]
#[ignore = "requires original C++20 Unicode/grapheme source inventory"]
fn original_native_graphemes_match() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("grapheme-native");
    let exe = dir.0.join("oracle");
    let mut compiler = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    compiler
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/src/Text").display()
        ));
    for source in [
        "src/ProGPU.Rust/tests/native_grapheme.cpp",
        "src/ProGPU.Native/src/Text/progpu_native_unicode.cpp",
        "src/ProGPU.Native/src/Text/progpu_native_unicode_grapheme.cpp",
    ] {
        compiler.arg(root.join(source));
    }
    assert!(compiler.arg("-o").arg(&exe).status().unwrap().success());
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    for cp in (0..=0x110010).chain([0x7fffffff, u32::MAX]) {
        emit(&mut input, 0);
        emit(&mut input, cp);
        emit(&mut expected, break_class(cp) as u32);
        emit(&mut expected, indic_conjunct_class(cp) as u32);
        emit(&mut expected, u32::from(is_extended_pictographic(cp)));
        ends.push(expected.len());
    }
    for source in corpus() {
        for policy in [GraphemePolicy::Unicode17, GraphemePolicy::ManagedCompatible] {
            let required = cluster_count(&source, policy).unwrap() as usize;
            for cap in [required.saturating_sub(1), required, required + 2] {
                emit(&mut input, 1);
                emit(&mut input, source.len() as u32);
                input.extend(bytemuck::cast_slice::<_, u8>(&source));
                emit(&mut input, cap as u32);
                emit(
                    &mut input,
                    u32::from(policy == GraphemePolicy::ManagedCompatible),
                );
                result(
                    &mut expected,
                    cluster_count(&source, GraphemePolicy::Unicode17),
                );
                let mut output = vec![SENTINEL; cap];
                result(&mut expected, segment_into(&source, policy, &mut output));
                expected.extend(bytemuck::cast_slice::<_, u8>(&output));
                ends.push(expected.len());
            }
        }
    }
    for cp in 0..=u16::MAX {
        for units in [vec![cp], vec![0x41, cp, 0x301, 0xd83d, 0xdc69]] {
            for cap in [0, units.len() + 2] {
                emit(&mut input, 2);
                emit(&mut input, units.len() as u32);
                input.extend(bytemuck::cast_slice::<_, u8>(&units));
                emit(&mut input, cap as u32);
                let mut output = vec![0xa5; cap];
                let r = utf16_starts_into(&units, &mut output);
                emit(&mut expected, u32::from(r.is_ok()));
                emit(&mut expected, r.err().map_or(0, |e| e as u32));
                expected.extend(output);
                ends.push(expected.len());
            }
        }
    }
    let path = dir.0.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = Command::new(exe)
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(actual.status.success());
    compare(&actual.stdout, &expected, &ends, "native_grapheme");
}
#[test]
#[ignore = "requires .NET 10 and original OriginalGraphemePolicy source"]
fn original_managed_grapheme_policy_matches() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("grapheme-managed");
    let includes: String = [
        "src/ProGPU.Rust/tests/managed_grapheme.cs",
        "src/ProGPU.Text/OriginalGraphemePolicy.cs",
    ]
    .iter()
    .map(|p| {
        format!(
            "<Compile Include=\"{}\"/>",
            root.join(p)
                .display()
                .to_string()
                .replace('&', "&amp;")
                .replace('"', "&quot;")
        )
    })
    .collect();
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>")).unwrap();
    let dotnet = std::env::var_os("PROGPU_RUST_DOTNET").unwrap_or_else(|| "dotnet".into());
    assert!(
        Command::new(&dotnet)
            .current_dir(&dir.0)
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
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    let mut cases: Vec<Vec<u16>> = (0..=u16::MAX).map(|u| vec![u]).collect();
    for a in 0..128 {
        for b in 0..128 {
            cases.push(vec![a, b]);
        }
    }
    for source in corpus().into_iter().take(10649) {
        if source
            .iter()
            .all(|s| char::from_u32(s.code_point).is_some())
        {
            let mut units = Vec::new();
            for s in source {
                let mut b = [0; 2];
                units.extend_from_slice(char::from_u32(s.code_point).unwrap().encode_utf16(&mut b));
            }
            cases.push(units);
        }
    }
    for units in cases {
        emit(&mut input, units.len() as u32);
        input.extend(bytemuck::cast_slice::<_, u8>(&units));
        match DecodedText::from_utf16(units) {
            Err(_) => emit(&mut expected, u32::MAX),
            Ok(text) => {
                let end = text
                    .scalars()
                    .last()
                    .map_or(0, |s| s.input_index + u32::from(s.input_length));
                let clusters = GraphemeText::new(text, GraphemePolicy::ManagedCompatible).unwrap();
                emit(&mut expected, clusters.clusters().len() as u32 + 1);
                emit(&mut expected, 0);
                for (i, c) in clusters.clusters().iter().enumerate() {
                    emit(
                        &mut expected,
                        clusters
                            .clusters()
                            .get(i + 1)
                            .map_or(end, |n| n.input_index),
                    );
                    assert!(c.scalar_count > 0);
                }
            }
        }
        ends.push(expected.len());
    }
    let path = dir.0.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = Command::new(dotnet)
        .arg(dir.0.join("out/Control.dll"))
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(
        actual.status.success(),
        "{}",
        String::from_utf8_lossy(&actual.stderr)
    );
    compare(&actual.stdout, &expected, &ends, "managed_grapheme");
}
