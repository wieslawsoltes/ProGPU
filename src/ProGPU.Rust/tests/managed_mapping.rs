//! Full original managed mapping: base selection, legacy symbols, UVS and caching.
use progpu::font::{FontError, ManagedFont};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
};
#[path = "support/managed_mapping_fixture.rs"]
mod fixture;
#[path = "support/managed_mapping_replay.rs"]
mod replay;
use fixture::{basic, font, source};
use font::{u16_at, u32_at};
use replay::{Case, emit};
#[test]
fn original_policy_and_atomic_cache_preserve_zero_errors_and_owner() {
    let data: Arc<[u8]> = source(fixture::seeds()[4].clone(), 0, 0).into();
    let weak = Arc::downgrade(&data);
    let f = ManagedFont::new(data, 0).unwrap();
    assert_eq!(f.variation_glyph(0x42, 0xfe0f), Ok(Some(0)));
    assert_eq!(f.source().variation_glyph(0x42, 0xfe0f), Some(11));
    let mut tasks = Vec::new();
    for _ in 0..8 {
        let f = f.clone();
        tasks.push(std::thread::spawn(move || {
            for _ in 0..1000 {
                assert_eq!(f.glyph_index(0x41), Ok(3));
                assert_eq!(f.glyph_index(0x42), Ok(0));
            }
        }));
    }
    for t in tasks {
        t.join().unwrap();
    }
    let map = f.character_map().clone();
    drop(f);
    assert!(weak.upgrade().is_some());
    assert_eq!(map.glyph_index(0x41), Ok(Some(3)));
    drop(map);
    assert!(weak.upgrade().is_none());
    let mut group = font::groups(12, 0, 127, 1);
    u32_at(&mut group, 12, u32::MAX);
    let f = ManagedFont::new(source(font::cmap(&[(3, 10, group)]), 0, 0), 0).unwrap();
    for _ in 0..2 {
        assert_eq!(f.glyph_index(65), Err(FontError::InvalidFace));
    }
}
fn corpus() -> (Vec<Case>, Vec<usize>) {
    let mut cases = Vec::new();
    let mut browser = Vec::new();
    for cm in fixture::seeds() {
        browser.push(cases.len());
        cases.push(Case {
            font: source(cm, 0, 0),
            queries: basic(),
        });
    }
    for version in [0, 1, 5] {
        for page in [0, 0xb200, 0xb300, 0xb234, 0xff00] {
            let cm = font::cmap(&[
                (3, 1, font::format4(false, false)),
                (3, 0, fixture::identity_symbol()),
                (0, 5, font::format14()),
            ]);
            browser.push(cases.len());
            cases.push(Case {
                font: source(cm.clone(), version, page),
                queries: basic(),
            });
            if version == 0 {
                let mut q = Vec::new();
                for c in 0..=65535 {
                    q.extend([[0, c, 0], [1, c, 0]]);
                }
                cases.push(Case {
                    font: source(cm, version, page),
                    queries: q,
                });
            }
        }
    }
    for f in font::format4_inventory() {
        let mut q = Vec::new();
        for c in 0..=65535 {
            q.push([0, c, 0]);
        }
        for c in 0..128 {
            q.extend([[1, c, 0], [1, c, 0], [3, c, 0]]);
        }
        cases.push(Case {
            font: source(font::cmap(&[(3, 1, f)]), 0, 0),
            queries: q,
        });
    }
    for page in [0xb200, 0xb300] {
        for code in 0xf000..=0xf2ff {
            if [0xf000, 0xf100, 0xf2ee, 0xf2ff].contains(&code) {
                browser.push(cases.len());
            }
            cases.push(Case {
                font: source(font::cmap(&[(3, 0, fixture::symbol_hole(code))]), 0, page),
                queries: vec![
                    [0, u32::from(code), 0],
                    [1, u32::from(code), 0],
                    [0, u32::from(code) - 1, 0],
                    [0, u32::from(code) + 1, 0],
                ],
            });
        }
    }
    for format in [12, 13] {
        for count in [
            0,
            1,
            2,
            3,
            4,
            0x15555555,
            0x15555556,
            i32::MAX as u32,
            0x80000000,
            u32::MAX,
        ] {
            for fallback in [false, true] {
                let mut g = font::groups(format, 0, 65535, u32::MAX);
                u32_at(&mut g, 12, count);
                let mut records = vec![(3, 10, g)];
                if fallback {
                    records.extend([
                        (0, 4, font::groups(13, 0, 65535, 7)),
                        (3, 1, font::format4(false, false)),
                    ]);
                }
                if count == 2 || count == u32::MAX {
                    browser.push(cases.len());
                }
                cases.push(Case {
                    font: source(font::cmap(&records), 0, 0),
                    queries: basic(),
                });
            }
        }
    }
    for format in [12, 13] {
        for reversed in [false, true] {
            for count in [2usize, 3, 4, 7] {
                let mut t = vec![0; 16];
                u16_at(&mut t, 0, format);
                u32_at(&mut t, 12, count as u32);
                let iter: Vec<_> = if reversed {
                    (0..count).rev().collect()
                } else {
                    (0..count).collect()
                };
                for n in iter {
                    t.extend(0x41u32.to_be_bytes());
                    t.extend(0x43u32.to_be_bytes());
                    t.extend((n as u32 + 1).to_be_bytes());
                }
                let len = t.len() as u32;
                u32_at(&mut t, 4, len);
                browser.push(cases.len());
                cases.push(Case {
                    font: source(font::cmap(&[(3, 10, t)]), 0, 0),
                    queries: basic(),
                });
            }
        }
    }
    for field in [2, 6, 13, 17, 21, 29] {
        for value in [
            0,
            1,
            9,
            10,
            11,
            21,
            29,
            i32::MAX as u32 / 11,
            i32::MAX as u32 / 11 + 1,
            0x7fffffff,
            0x80000000,
            u32::MAX,
        ] {
            let mut t = font::format14();
            u32_at(&mut t, field, value);
            if field == 2 || value == 0x80000000 {
                browser.push(cases.len());
            }
            cases.push(Case {
                font: source(
                    font::cmap(&[(3, 1, font::format4(false, false)), (0, 5, t)]),
                    0,
                    0,
                ),
                queries: basic(),
            });
        }
    }
    let seeds = fixture::seeds();
    for cm in &seeds {
        for len in 0..cm.len() {
            cases.push(Case {
                font: source(cm[..len].to_vec(), 0, 0),
                queries: basic(),
            });
        }
    }
    let mut rng = 0x9375a0abu32;
    let mut next = || {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        rng
    };
    for n in 0..6000 {
        let mut cm = seeds[n % seeds.len()].clone();
        for _ in 0..1 + next() % 5 {
            let at = next() as usize % cm.len();
            cm[at] ^= next() as u8;
        }
        if n % 3 == 0 {
            cm.truncate(next() as usize % (cm.len() + 1));
        }
        let mut q = basic();
        for _ in 0..16 {
            let c = next();
            q.extend([[0, c, 0], [1, c, 0], [2, c, next()]]);
        }
        if n < 12 {
            browser.push(cases.len());
        }
        cases.push(Case {
            font: source(cm, 0, [0, 0xb200, 0xb300][n % 3]),
            queries: q,
        });
    }
    // Whole-table duplicates and partial OS/2 storage preserve selected inventory.
    for length in [0, 1, 2, 63, 64] {
        let mut ts = font::tables(font::cmap(&[(3, 0, fixture::identity_symbol())]));
        let mut os = vec![0; 64];
        u16_at(&mut os, 62, 0xb200);
        os.truncate(length);
        ts.push((*b"OS/2", os));
        browser.push(cases.len());
        cases.push(Case {
            font: font::sfnt(&ts, 0),
            queries: basic(),
        });
    }
    for count in [1024, 4096] {
        cases.push(Case {
            font: fixture::directory_source(count),
            queries: basic(),
        });
    }
    (cases, browser)
}
struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires .NET 10 and original ProGPU font source"]
fn original_managed_character_mapping() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-managed-mapping-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_mapping.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let (cases, selected) = corpus();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for c in &cases {
        c.write(&mut input);
        expected.extend(c.expected());
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
        let q = (p - start).saturating_sub(4) / 12;
        panic!(
            "mapping case {n} query {q} {:?} byte {p}, actual {:?} expected {:?}",
            cases[n].queries.get(q),
            &actual.stdout[p.saturating_sub(8)..(p + 20).min(actual.stdout.len())],
            &expected[p.saturating_sub(8)..(p + 20).min(expected.len())]
        );
    }
    let mut binary = Vec::from(*b"PMG1");
    emit(&mut binary, selected.len() as u32);
    for &n in &selected {
        let c = &cases[n];
        let start = if n == 0 { 0 } else { ends[n - 1] };
        emit(&mut binary, c.font.len() as u32);
        binary.extend(&c.font);
        emit(&mut binary, c.queries.len() as u32);
        for q in &c.queries {
            for &v in q {
                emit(&mut binary, v);
            }
        }
        let out = &actual.stdout[start..ends[n]];
        emit(&mut binary, out.len() as u32);
        binary.extend(out);
    }
    let path = root.join("src/ProGPU.Rust/browser/managed-mapping.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_MAPPING_FIXTURE").is_some() {
        std::fs::write(&path, &binary).unwrap();
    }
    let frozen = std::fs::read(path).unwrap();
    assert!(
        frozen == binary,
        "complete browser fixture must match actual source bytes"
    );
    eprintln!(
        "Original managed mapping: {} cases / {} queries / {} exact bytes; {} browser cases / {} bytes",
        cases.len(),
        cases.iter().map(|c| c.queries.len()).sum::<usize>(),
        expected.len(),
        selected.len(),
        binary.len()
    );
}
