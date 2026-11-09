//! Actual original managed variation-data admission, instances and cached queries.
use progpu::font::{ManagedVariationData, ManagedVariationSetting, SfntFace};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[path = "support/managed_variations_fixture.rs"]
mod fixture;
use fixture::*;
fn emit(out: &mut Vec<u8>, n: u32) {
    out.extend(n.to_le_bytes());
}
fn text(out: &mut Vec<u8>, v: &[u16]) {
    emit(out, v.len() as u32);
    for &c in v {
        emit(out, c.into());
    }
}
#[path = "support/managed_variation_replay.rs"]
mod replay;
use replay::{Case, Query, name};
impl Case {
    fn new(font: Vec<u8>, value: f32) -> Self {
        let mut queries = Vec::new();
        for a in 0..5 {
            for b in 0..9 {
                queries.push(Query {
                    kind: 0,
                    a,
                    b,
                    count: 0,
                    points: vec![],
                    ends: vec![],
                });
            }
        }
        for kind in [1, 2] {
            for a in 0..9 {
                queries.push(Query {
                    kind,
                    a,
                    b: 0,
                    count: 0,
                    points: vec![],
                    ends: vec![],
                });
            }
        }
        // Same underlying data owns all these ordered queries, including failed first
        // parses and subsequent differing item counts.
        for (kind, count) in [(3, 4), (5, 8), (4, 4), (5, 9), (4, 5), (3, 4)] {
            queries.push(Query {
                kind,
                a: 0,
                b: 0,
                count,
                points: if kind == 3 {
                    vec![[0.0, 3.0], [17.0, 32.0], [33.0, -15.0], [55.0, 5.0]]
                } else {
                    vec![]
                },
                ends: if kind == 3 { vec![3] } else { vec![] },
            });
        }
        Self {
            font,
            settings: vec![
                ManagedVariationSetting::from_str("wght", value),
                ManagedVariationSetting::from_str("wdth", -value / 2.0),
                ManagedVariationSetting::from_str("opsz", value / 3.0),
            ],
            queries,
        }
    }
    fn write(&self, out: &mut Vec<u8>) {
        emit(out, self.font.len() as u32);
        out.extend(&self.font);
        emit(out, replay::source_glyph_count(&self.font).unwrap().into());
        emit(out, self.settings.len() as u32);
        for s in &self.settings {
            text(out, &s.tag);
            emit(out, s.value.to_bits());
        }
        emit(out, self.queries.len() as u32);
        for q in &self.queries {
            for v in [
                q.kind,
                q.a.into(),
                q.b.into(),
                q.count as u32,
                q.points.len() as u32,
            ] {
                emit(out, v);
            }
            for p in &q.points {
                emit(out, p[0].to_bits());
                emit(out, p[1].to_bits());
            }
            emit(out, q.ends.len() as u32);
            for &v in &q.ends {
                emit(out, v.into());
            }
        }
    }
}
#[test]
fn instances_retain_original_owner_and_duplicate_settings() {
    let bytes = font::sfnt(&tables(2, 1), 0);
    let face = SfntFace::new(bytes, 0).unwrap();
    let data = ManagedVariationData::from_face(face, name)
        .unwrap()
        .unwrap();
    let i = data
        .create_instance(&[
            ManagedVariationSetting::from_str("wght", 0.0),
            ManagedVariationSetting::from_str("wght", 0.5),
        ])
        .unwrap();
    assert_eq!(i.settings()[0].value, 0.5);
    let clone = i.clone();
    drop(i);
    drop(data);
    assert_eq!(clone.settings().len(), 2);
    assert!(clone.layout_delta(0, 0).is_ok());
}
struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires .NET 10 and actual managed variation source"]
fn original_managed_variation_instances_and_queries() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-managed-variation-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_variations.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
    let dotnet = std::env::var_os("PROGPU_RUST_DOTNET").unwrap_or_else(|| "dotnet".into());
    assert!(
        Command::new(&dotnet)
            .current_dir(&dir.0)
            .args([
                "build",
                "Control.csproj",
                "-c",
                "Release",
                "--nologo",
                "-v:q",
                "-p:UseArtifactsOutput=true"
            ])
            .arg(format!(
                "-p:ArtifactsPath={}",
                dir.0.join("artifacts").display()
            ))
            .status()
            .unwrap()
            .success()
    );
    let mut cases = Vec::new();
    for axes in 1..=3 {
        for variant in 0..8 {
            let bytes = font::sfnt(&tables(axes, variant), 0);
            for value in [
                f32::NEG_INFINITY,
                -32768.0,
                -1.0,
                -0.50003,
                -0.5,
                -0.00001,
                -0.0,
                0.0,
                0.00001,
                0.33,
                0.5,
                0.50003,
                1.0,
                32768.0,
                f32::INFINITY,
                f32::NAN,
            ] {
                cases.push(Case::new(bytes.clone(), value));
            }
        }
    }
    let mut rng = 0x993874abu32;
    let mut next = || {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        rng
    };
    for n in 0..6000 {
        let mut ts = tables(1 + next() as usize % 3, next() as usize % 8);
        let k = ts.len() - 1 - next() as usize % ((ts.len() - 7).max(1));
        let table = &mut ts[k].1;
        for _ in 0..1 + next() % 3 {
            let at = next() as usize % table.len();
            table[at] ^= next() as u8;
        }
        if n % 3 == 0 {
            table.truncate(next() as usize % (table.len() + 1));
        }
        let mut c = Case::new(font::sfnt(&ts, 0), f32::from_bits(next()));
        if n % 11 == 0 {
            c.queries.swap(63, 64);
        }
        cases.push(c);
    }
    // Original unchecked fixed differences, float endpoints and nonfinite values.
    for bounds in [
        (i32::MIN, 0, i32::MAX),
        (-65536, 65536, 65536),
        (0, -65536, 65536),
        (65536, 0, -65536),
        (i32::MIN, i32::MAX, i32::MAX),
        (i32::MIN, i32::MIN, 0),
        (-1, 0, 1),
    ] {
        for map in 0..5 {
            for value in [
                f32::NEG_INFINITY,
                -32768.0,
                -1.0,
                -0.5,
                -0.0,
                0.0,
                0.5,
                1.0,
                32768.0,
                f32::INFINITY,
                f32::NAN,
            ] {
                let mut ts = tables(1, map);
                let axis = ts.iter_mut().find(|(tag, _)| tag == b"fvar").unwrap();
                axis.1 = fvar(&[(
                    [255, 128, 129, b'x'],
                    bounds.0,
                    bounds.1,
                    bounds.2,
                    65535,
                    65535,
                )]);
                let mut c = Case::new(font::sfnt(&ts, 0), value);
                c.settings
                    .push(ManagedVariationSetting::from_str("???x", value));
                cases.push(c);
            }
        }
    }
    // A malformed first tuple is cached as empty after its first source failure.
    let mut ts = tables(1, 0);
    let g = &mut ts.iter_mut().find(|(tag, _)| tag == b"gvar").unwrap().1;
    let start = u32::from_be_bytes(g[16..20].try_into().unwrap()) as usize;
    let data = u16::from_be_bytes(g[start + 2..start + 4].try_into().unwrap()) as usize;
    g[start + data] = 0x7f;
    cases.push(Case::new(font::sfnt(&ts, 0), 0.5));
    for n in 0..2000 {
        let mut c = Case::new(
            font::sfnt(&tables(1 + n % 3, n % 8), 0),
            (next() as i16) as f32 / 16384.0,
        );
        for q in &mut c.queries {
            if q.kind == 3 {
                for p in &mut q.points {
                    p[0] = f32::from_bits(next());
                    p[1] = f32::from_bits(next());
                }
                if n % 9 == 0 {
                    q.ends = vec![1, 0, 3];
                }
                if n % 13 == 0 {
                    q.ends = vec![7];
                }
            }
        }
        cases.push(c);
    }
    for missing in [false, true] {
        let mut ts = tables(1, 0);
        if missing {
            ts.retain(|(tag, _)| tag != b"maxp");
            let g = &mut ts.iter_mut().find(|(tag, _)| tag == b"gvar").unwrap().1;
            put(g, 12, 0);
        }
        let mut bytes = font::sfnt(&ts, 0);
        if !missing {
            let count = u16::from_be_bytes([bytes[4], bytes[5]]) as usize;
            for i in 0..count {
                let at = 12 + i * 16;
                if &bytes[at..at + 4] == b"maxp" {
                    font::u32_at(&mut bytes, at + 12, 1);
                }
            }
        }
        cases.push(Case::new(bytes, 0.5));
    }
    // Browser requests are authored bit patterns, not host-dependent results of
    // NaN arithmetic. ARM64 and x64 can choose different signs for -NaN / 2.
    // Keep every original native-generated case as an additional source control;
    // freeze only these 24 browser requests to their already-published input bits.
    for n in (15..384).step_by(16) {
        cases.push(cases[n].clone());
        cases[n].settings[1].value = f32::from_bits(0xffc0_0000);
    }
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for c in &cases {
        c.write(&mut input);
        expected.extend(c.expected().unwrap());
        ends.push(expected.len());
    }
    std::fs::write(dir.0.join("input.bin"), input).unwrap();
    let actual = Command::new(&dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .stdin(Stdio::from(
            std::fs::File::open(dir.0.join("input.bin")).unwrap(),
        ))
        .output()
        .unwrap();
    assert!(
        actual.status.success(),
        "{}",
        String::from_utf8_lossy(&actual.stderr)
    );
    if actual.stdout != expected {
        let p = actual
            .stdout
            .iter()
            .zip(&expected)
            .position(|(a, b)| a != b)
            .unwrap_or(actual.stdout.len().min(expected.len()));
        let n = ends.partition_point(|&e| e <= p);
        let start = if n == 0 { 0 } else { ends[n - 1] };
        panic!(
            "instance case {n}, word {}, byte {p}; actual={:02x?} expected={:02x?}; font={:02x?}",
            (p - start) / 4,
            &actual.stdout[p.saturating_sub(8)..(p + 32).min(actual.stdout.len())],
            &expected[p.saturating_sub(8)..(p + 32).min(expected.len())],
            cases[n].font
        );
    }
    let mut binary = Vec::from(*b"PVI1");
    let mut selected = Vec::new();
    let mut mutations = 0;
    for (n, c) in cases.iter().enumerate() {
        let start = if n == 0 { 0 } else { ends[n - 1] };
        let bytes = &actual.stdout[start..ends[n]];
        if (n < 384 && [3, 10, 15].contains(&(n % 16)))
            || (n >= 384 && mutations < 24 && bytes[0] == 1)
        {
            selected.push((c, bytes));
            if n >= 384 {
                mutations += 1;
            }
        }
    }
    emit(&mut binary, selected.len() as u32);
    for (c, output) in &selected {
        let mut input = Vec::new();
        c.write(&mut input);
        let request = &input[4 + c.font.len()..];
        let words: Vec<_> = request
            .chunks_exact(4)
            .map(|b| u32::from_le_bytes(b.try_into().unwrap()))
            .collect();
        assert_eq!(
            replay::decode(c.font.clone(), &words)
                .unwrap()
                .expected()
                .unwrap(),
            *output
        );
        emit(&mut binary, c.font.len() as u32);
        binary.extend(&c.font);
        emit(&mut binary, request.len() as u32);
        binary.extend(request);
        emit(&mut binary, output.len() as u32);
        binary.extend(*output);
    }
    let path = root.join("src/ProGPU.Rust/browser/managed-variations.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_VARIATION_FIXTURE").is_some() {
        std::fs::write(&path, &binary).unwrap();
    }
    let frozen = std::fs::read(path).unwrap();
    if frozen != binary {
        let at = frozen
            .iter()
            .zip(&binary)
            .position(|(a, b)| a != b)
            .unwrap_or(frozen.len().min(binary.len()));
        panic!(
            "browser bytes must match actual C# output: first differing byte {at}, frozen length {}, captured length {}",
            frozen.len(),
            binary.len()
        );
    }
    eprintln!(
        "Original managed variation browser: {} cases / {} bytes",
        selected.len(),
        binary.len()
    );
    eprintln!(
        "Original managed variation: {} cases / {} exact bytes",
        cases.len(),
        expected.len()
    );
}
