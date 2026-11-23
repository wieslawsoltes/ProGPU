//! Actual managed font construction, retained identity and ordered query controls.
use progpu::font::{ManagedFont, ManagedVariationSetting as Setting, SfntFace};
use std::{
    hash::{Hash, Hasher},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
};
#[path = "support/managed_font_fixture.rs"]
mod fixture;
#[path = "support/managed_font_replay.rs"]
mod replay;
use fixture::font;
use replay::{Case, Op, emit};
fn op(from: usize, value: f32) -> Op {
    Op {
        kind: 0,
        from,
        index: 0,
        settings: vec![
            Setting::from_str("wght", value),
            Setting::from_str("ital", 0.75),
            Setting::from_str("slnt", -10.0),
        ],
        colors: vec![],
    }
}
fn basic(bytes: Vec<u8>) -> Case {
    let mut ops = vec![
        op(0, 400.0),
        op(0, 700.0),
        op(0, 700.001),
        op(3, 700.002),
        op(1, 700.0),
        op(3, 400.0),
        op(0, 100.0),
        op(0, 900.0),
        op(0, f32::NAN),
        op(0, f32::INFINITY),
        op(0, f32::NEG_INFINITY),
    ];
    ops.push(Op {
        kind: 0,
        from: 3,
        index: 0,
        settings: vec![],
        colors: vec![],
    });
    for (index, colors) in [
        (0, vec![]),
        (1, vec![]),
        (-1, vec![]),
        (0, vec![(0, 0x80402010), (2, 0x10203040), (65535, 0)]),
        (1, vec![(1, 0xffffffff), (1, 0x01020304)]),
    ] {
        ops.push(Op {
            kind: 1,
            from: 3,
            index,
            settings: vec![],
            colors,
        });
    }
    ops.push(Op {
        kind: 2,
        from: 18,
        index: 0,
        settings: vec![],
        colors: vec![],
    });
    ops.push(op(18, 700.0));
    let mut queries = Vec::new();
    for glyph in [0, 1, 3, 7, 8, 65535] {
        queries.push((0, glyph, 0, 12.5f32.to_bits()));
        queries.push((1, glyph, 0, 12.5f32.to_bits()));
    }
    for a in 0..4 {
        for b in [0, 3, 4, 7, 99] {
            queries.push((2, a, b, 0));
        }
    }
    queries.extend([(3, 0, 4, 0), (4, 0, 0, 0)]);
    Case {
        font: bytes,
        face_index: 0,
        ops,
        queries,
    }
}
#[test]
fn cache_identity_eviction_concurrency_and_owner_retirement() {
    let data: Arc<[u8]> = font::sfnt(&fixture::tables(1), 0).into();
    let weak = Arc::downgrade(&data);
    let root = ManagedFont::from_face(SfntFace::new(data, 0).unwrap()).unwrap();
    let a = root.with_variations(&op(0, 700.0).settings).unwrap();
    let b = root.with_variations(&op(0, 700.001).settings).unwrap();
    assert!(a.same_instance(&b));
    assert_eq!(b.variation_settings()[0].value, 700.0);
    let mut workers = Vec::new();
    for _ in 0..8 {
        let r = root.clone();
        workers.push(std::thread::spawn(move || {
            r.with_variations(&op(0, 700.0).settings).unwrap()
        }));
    }
    for w in workers {
        assert!(a.same_instance(&w.join().unwrap()));
    }
    for n in 0..33 {
        let _ = root
            .with_variations(&op(0, 110.0 + n as f32 * 10.0).settings)
            .unwrap();
    }
    let c = root.with_variations(&op(0, 700.001).settings).unwrap();
    assert_eq!(a, c);
    assert!(!a.same_instance(&c));
    let hash = |f: &ManagedFont| {
        let mut h = std::collections::hash_map::DefaultHasher::new();
        f.hash(&mut h);
        h.finish()
    };
    assert_eq!(hash(&a), hash(&c));
    assert_eq!(c.variation_settings()[0].value, 700.001);
    drop(root);
    assert!(
        a.root()
            .with_variations(&[])
            .unwrap()
            .same_instance(&a.root())
    );
    drop(a);
    drop(b);
    drop(c);
    assert!(weak.upgrade().is_none(), "cache retained an Arc cycle");
}
#[test]
fn failed_metadata_selection_does_not_publish_or_replace_root() {
    let mut tables = fixture::tables(1);
    let at = tables.iter().position(|t| t.0 == *b"MVAR").unwrap();
    let mut m = tables.remove(at);
    font::u16_at(&mut m.1, 82, 7);
    m.1.truncate(m.1.len() - 20);
    tables.push(m);
    let root = ManagedFont::from_face(SfntFace::new(font::sfnt(&tables, 0), 0).unwrap()).unwrap();
    assert_eq!(root.variation_axes().unwrap().len(), 3);
    for _ in 0..2 {
        assert!(root.with_variations(&op(0, 700.0).settings).is_err());
    }
    assert!(root.with_variations(&[]).unwrap().same_instance(&root));
    assert_eq!(root.metadata().weight_class, 500);
    assert_eq!(root.layout_delta(0, 0).unwrap(), 0.0);
}
fn corpus() -> (Vec<Case>, Vec<usize>) {
    let mut cases = Vec::new();
    let mut browser = Vec::new();
    for variant in 0..8 {
        let mut c = basic(font::sfnt(&fixture::tables(variant), 0));
        browser.push(cases.len());
        cases.push(c.clone());
        for n in 0..40 {
            c.ops.push(op(0, 110.0 + n as f32 * 10.0));
        }
        c.ops.push(op(0, 700.001));
        c.ops.push(op(3, 700.002));
        browser.push(cases.len());
        cases.push(c);
    }
    let mut rng = 0x82acd93bu32;
    let mut next = || {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        rng
    };
    for n in 0..6000 {
        let mut tables = fixture::tables(n % 8);
        let selected = next() as usize % tables.len();
        let b = &mut tables[selected].1;
        if !b.is_empty() {
            for _ in 0..1 + next() % 3 {
                let at = next() as usize % b.len();
                b[at] ^= next() as u8;
            }
            if n % 3 == 0 {
                b.truncate(next() as usize % (b.len() + 1));
            }
        }
        let mut c = basic(font::sfnt(&tables, 0));
        c.ops.truncate(5);
        c.ops.push(op(0, f32::from_bits(next())));
        if n < 16 {
            browser.push(cases.len());
        }
        cases.push(c);
    }
    for n in 0..2000 {
        let mut c = basic(font::sfnt(&fixture::tables(n % 8), 0));
        c.ops = (0..6)
            .map(|_| {
                let mut o = op(0, f32::from_bits(next()));
                for s in &mut o.settings {
                    s.value = f32::from_bits(next());
                }
                o
            })
            .collect();
        c.queries = (0..8).map(|i| (i % 2, next() as u16, 0, next())).collect();
        cases.push(c);
    }
    for variant in 0..8 {
        for tag in [
            *b"head", *b"hhea", *b"vhea", *b"maxp", *b"OS/2", *b"post", *b"COLR", *b"CPAL",
        ] {
            let original = fixture::tables(variant);
            let index = original.iter().position(|t| t.0 == tag).unwrap();
            for len in 0..original[index].1.len() {
                let mut tables = original.clone();
                tables[index].1.truncate(len);
                cases.push(basic(font::sfnt(&tables, 0)));
            }
            // Whole-font reads preserve physically present bytes beyond a short declaration.
            let mut bytes = font::sfnt(&original, 0);
            font::u32_at(&mut bytes, 12 + index * 16 + 12, 1);
            cases.push(basic(bytes));
        }
        let mut tables = fixture::tables(variant);
        tables.retain(|t| t.0 != *b"hhea" && t.0 != *b"vhea");
        browser.push(cases.len());
        cases.push(basic(font::sfnt(&tables, 0)));
        let mut tables = fixture::tables(variant);
        tables.iter_mut().find(|t| t.0 == *b"head").unwrap().0 = *b"bhed";
        browser.push(cases.len());
        cases.push(basic(font::sfnt(&tables, 0)));
    }
    for len in [0, 1, 2, 3, 4, 12, 20, 50] {
        let mut ts = fixture::tables(1);
        let at = ts.iter().position(|t| t.0 == *b"MVAR").unwrap();
        let mut m = ts.remove(at);
        font::u16_at(&mut m.1, 82, 7);
        m.1.truncate(m.1.len() - len);
        ts.push(m);
        browser.push(cases.len());
        cases.push(basic(font::sfnt(&ts, 0)));
    }
    for first_variant in 0..4 {
        let mut bytes = font::sfnt(&fixture::tables(first_variant), 20);
        let second = bytes.len();
        let other = font::sfnt(&fixture::tables(first_variant + 4), second);
        bytes.resize(other.len(), 0);
        bytes[second..].copy_from_slice(&other[second..]);
        bytes[..4].copy_from_slice(b"ttcf");
        font::u32_at(&mut bytes, 4, 0x10000);
        font::u32_at(&mut bytes, 8, 2);
        font::u32_at(&mut bytes, 12, 20);
        font::u32_at(&mut bytes, 16, second as u32);
        for index in [0, 1, 2, u32::MAX] {
            let mut c = basic(bytes.clone());
            c.face_index = index;
            browser.push(cases.len());
            cases.push(c);
        }
        for selected in [0, 1] {
            for mode in 0..4 {
                let mut bad = bytes.clone();
                let other = if selected == 0 { second } else { 20 };
                if mode < 2 {
                    font::u16_at(&mut bad, other + 4, u16::MAX);
                } else {
                    let entry = if selected == 0 { 16 } else { 12 };
                    font::u32_at(&mut bad, entry, u32::MAX);
                }
                let mut c = basic(bad);
                c.face_index = if mode % 2 == 0 {
                    selected
                } else {
                    1 - selected
                };
                browser.push(cases.len());
                cases.push(c);
            }
        }
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
#[ignore = "requires .NET 10 and original TtfFont constructors and queries"]
fn original_managed_font_constructor_cache_and_queries() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-managed-font-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_font.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let (cases, browser) = corpus();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ranges = Vec::new();
    for (index, c) in cases.iter().enumerate() {
        c.write(&mut input);
        let at = expected.len();
        expected.extend(c.expected().unwrap_or_else(|e| panic!("case {index}: {e}")));
        ranges.push(at..expected.len());
    }
    let input_path = dir.0.join("input.bin");
    std::fs::write(&input_path, input).unwrap();
    let output = Command::new(&dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .stdin(std::fs::File::open(input_path).unwrap())
        .stdout(Stdio::piped())
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    if output.stdout != expected {
        let at = output
            .stdout
            .iter()
            .zip(&expected)
            .position(|(a, b)| a != b)
            .unwrap_or(output.stdout.len().min(expected.len()));
        let index = ranges
            .iter()
            .position(|r| r.contains(&at))
            .unwrap_or(cases.len());
        std::fs::write("/tmp/progpu-managed-font-mismatch.bin", &cases[index].font).unwrap();
        panic!(
            "case {index} byte {at}, source {:?}, Rust {:?}, lengths {} {}",
            output.stdout.get(at..(at + 32).min(output.stdout.len())),
            expected.get(at..(at + 32).min(expected.len())),
            output.stdout.len(),
            expected.len()
        );
    }
    let mut fixture = b"PMF1".to_vec();
    emit(&mut fixture, browser.len() as u32);
    for &i in &browser {
        let mut record = Vec::new();
        cases[i].write(&mut record);
        emit(&mut fixture, record.len() as u32);
        fixture.extend(record);
        let bytes = &output.stdout[ranges[i].clone()];
        emit(&mut fixture, bytes.len() as u32);
        fixture.extend(bytes);
    }
    let path = root.join("src/ProGPU.Rust/browser/managed-font.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_FONT_FIXTURE").is_some() {
        std::fs::write(path, &fixture).unwrap();
    } else {
        assert_eq!(std::fs::read(path).unwrap(), fixture);
    }
    println!(
        "Original managed font: {} cases / {} exact bytes; {} browser cases / {} bytes",
        cases.len(),
        output.stdout.len(),
        browser.len(),
        fixture.len()
    );
}
