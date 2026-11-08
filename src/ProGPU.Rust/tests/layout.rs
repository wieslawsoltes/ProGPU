//! Original common OpenType boundaries, ownership and differential controls.
use progpu::font::{FontError, SfntFace, Tag, layout::*};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
};
#[path = "support/layout_fixture.rs"]
mod fixture;
#[path = "support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
fn emit(b: &mut Vec<u8>, n: u32) {
    b.extend(n.to_le_bytes());
}
fn status<T>(b: &mut Vec<u8>, r: Result<T, FontError>) -> Option<T> {
    match r {
        Ok(v) => {
            emit(b, 0);
            Some(v)
        }
        Err(e) => {
            emit(b, e as u32);
            None
        }
    }
}
fn snapshot(kind: u32, b: &[u8], offset: usize, queries: &[u32]) -> Vec<u8> {
    let mut out = vec![];
    match kind {
        0 => {
            if let Some(v) = status(&mut out, Coverage::parse(b, offset)) {
                for &q in queries {
                    emit(&mut out, v.find(q as u16).map_or(u32::MAX, u32::from));
                }
            }
        }
        1 => {
            if let Some(v) = status(&mut out, ClassDefinition::parse(b, offset)) {
                for &q in queries {
                    emit(&mut out, u32::from(v.get(q as u16)));
                }
            }
        }
        2 => {
            if let Some(v) = status(&mut out, LayoutTable::parse(b)) {
                emit(&mut out, u32::from(v.lookup_count()));
                for &q in queries {
                    if let Some(l) = status(&mut out, v.lookup(q as u16)) {
                        for n in [
                            l.offset() as u32,
                            u32::from(l.lookup_type()),
                            u32::from(l.flags()),
                            u32::from(l.subtable_count()),
                            u32::from(l.mark_filtering_set()),
                        ] {
                            emit(&mut out, n);
                        }
                        for i in 0..=l.subtable_count() {
                            let r = l.subtable_offset(i);
                            emit(&mut out, r.err().map_or(0, |e| e as u32));
                            emit(&mut out, r.unwrap_or(0) as u32);
                        }
                    }
                }
            }
        }
        3 => {
            if let Some(v) = status(&mut out, Gdef::parse(b)) {
                emit(&mut out, u32::from(v.mark_set_count()));
                for &q in queries {
                    emit(&mut out, v.glyph_class(q as u16) as u32);
                    emit(&mut out, u32::from(v.mark_attachment_class(q as u16)));
                    emit(
                        &mut out,
                        u32::from(v.is_in_mark_set((q >> 16) as u16, q as u16)),
                    );
                }
            }
        }
        _ => unreachable!(),
    }
    out
}
#[test]
fn exact_duplicate_indices_and_owned_font_generation() {
    let bytes = fixture::coverage1(&[10, 10, 10, 10]);
    let c = Coverage::parse(&bytes, 0).unwrap();
    assert_eq!(c.find(10), Some(1));
    assert_eq!(c.find(9), None);
    let bytes = fixture::ranges(2, &[[0, 65535, 0]]);
    assert_eq!(Coverage::parse(&bytes, 0).unwrap().find(65535), Some(65535));
    let layout = fixture::layout(0xffff);
    let gdef = fixture::gdef(3);
    let mut tables = font::tables(vec![]);
    tables.extend([(*b"GSUB", layout.clone()), (*b"GDEF", gdef.clone())]);
    let data: Arc<[u8]> = font::sfnt(&tables, 0).into();
    let face = SfntFace::new(data.clone(), 0).unwrap();
    let l = OwnedLayoutTable::from_face(face.clone(), Tag::new(*b"GSUB"))
        .unwrap()
        .unwrap();
    let g = OwnedGdef::from_face(face.clone()).unwrap().unwrap();
    assert_eq!(
        l.view().bytes().as_ptr(),
        face.table(Tag::new(*b"GSUB")).unwrap().bytes.as_ptr()
    );
    assert_eq!(
        g.view().bytes().as_ptr(),
        face.table(Tag::new(*b"GDEF")).unwrap().bytes.as_ptr()
    );
    assert!(
        OwnedLayoutTable::from_face(face.clone(), Tag::new(*b"GPOS"))
            .unwrap()
            .is_none()
    );
    let g2 = g.clone();
    drop(g);
    drop(face);
    drop(data);
    assert_eq!(l.source().unwrap().0.glyph_count(), Some(8));
    assert_eq!(g2.source().unwrap().glyph_count(), Some(8));
    assert_eq!(l.view().lookup(0).unwrap().mark_filtering_set(), 42);
    assert_eq!(g2.view().glyph_class(14), GlyphClass::Component);
    assert_eq!(g2.view().glyph_class(15), GlyphClass::Unclassified);
    assert_eq!(g2.view().mark_attachment_class(14), 65535);
    assert!(g2.view().is_in_mark_set(1, 65535));
    assert!(!g2.view().is_in_mark_set(2, 10));
    let independent = OwnedGdef::new(gdef).unwrap();
    assert!(independent.source().is_none());
    assert_eq!(independent.view().glyph_class(13), GlyphClass::Mark);
    let independent = OwnedLayoutTable::new(layout).unwrap();
    assert_eq!(independent.view().lookup_count(), 2);
    fn threads<T: Send + Sync>() {}
    threads::<OwnedLayoutTable>();
    threads::<OwnedGdef>();
}
#[test]
fn malformed_inventories_and_lazy_lookup_validation() {
    assert!(Coverage::parse(&fixture::coverage1(&[9, 8]), 0).is_err());
    assert!(Coverage::parse(&fixture::ranges(2, &[[0, 1, 0], [1, 2, 2]]), 0).is_err());
    assert!(Coverage::parse(&fixture::ranges(2, &[[0, 1, 0], [2, 3, 1]]), 0).is_err());
    assert!(ClassDefinition::parse(&fixture::class1(65535, &[1, 2]), 0).is_err());
    for offset in [usize::MAX, usize::MAX - 3, 0xffff_ffff] {
        assert!(Coverage::parse(&[0; 16], offset).is_err());
        assert!(ClassDefinition::parse(&[0; 16], offset).is_err());
    }
    let mut b = fixture::layout(0);
    font::u16_at(&mut b, 24 + 8, 0);
    let l = LayoutTable::parse(&b).unwrap();
    assert!(l.lookup(0).is_err());
    assert!(l.lookup(1).is_ok());
    assert_eq!(l.lookup(2).unwrap_err(), FontError::InvalidArgument);
    let mut b = fixture::gdef(3);
    font::u32_at(&mut b, 52 + 8, u32::MAX);
    assert!(Gdef::parse(&b).is_err());
    for minor in [1, 4, 65535] {
        assert!(Gdef::parse(&fixture::gdef(minor)).is_err());
    }
    for (kind, b) in fixture::seeds() {
        for length in 0..b.len() {
            snapshot(kind, &b[..length], 0, &[0, 10, 65535, u32::MAX]);
        }
    }
}
struct Cleanup(PathBuf);
impl Drop for Cleanup {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn temp(name: &str) -> Cleanup {
    let p = std::env::temp_dir().join(format!("progpu-layout-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&p).unwrap();
    Cleanup(p)
}
#[derive(Default)]
struct Cases {
    input: Vec<u8>,
    expected: Vec<u8>,
    ends: Vec<usize>,
    queries: usize,
}
impl Cases {
    fn add(&mut self, kind: u32, b: &[u8], offset: usize, q: &[u32]) {
        emit(&mut self.input, kind);
        self.input.extend((offset as u64).to_le_bytes());
        emit(&mut self.input, b.len() as u32);
        self.input.extend(b);
        emit(&mut self.input, q.len() as u32);
        for &n in q {
            emit(&mut self.input, n);
        }
        self.expected.extend(snapshot(kind, b, offset, q));
        self.ends.push(self.expected.len());
        self.queries += q.len();
    }
    fn policy(&mut self, root: &Path) {
        let source =
            std::fs::read_to_string(root.join("src/ProGPU.Text.Shaping/OpenTypeGdefPolicy.cs"))
                .unwrap();
        let mut tuples = 0;
        for line in source.lines() {
            let line = line.trim();
            if !line.starts_with('(') || !line.chars().nth(1).unwrap().is_ascii_digit() {
                continue;
            }
            let n: Vec<usize> = line
                .split(|c: char| !c.is_ascii_digit())
                .filter(|s| !s.is_empty())
                .map(|s| s.parse().unwrap())
                .collect();
            assert_eq!(n.len(), 3);
            tuples += 1;
            for column in 0..3 {
                for delta in [-1, 0, 1] {
                    let mut t = [n[0], n[1], n[2]];
                    t[column] = t[column].checked_add_signed(delta).unwrap();
                    emit(&mut self.input, 4);
                    for x in t {
                        emit(&mut self.input, x as u32);
                    }
                    emit(
                        &mut self.expected,
                        u32::from(is_gdef_blocklisted(t[0], t[1], t[2])),
                    );
                    self.ends.push(self.expected.len());
                    self.queries += 1;
                }
            }
        }
        assert_eq!(tuples, 40);
    }
    fn run(&self, dir: &Path, command: &mut Command, name: &str) {
        let input = dir.join("input.bin");
        std::fs::write(&input, &self.input).unwrap();
        let result = command
            .stdin(std::fs::File::open(input).unwrap())
            .stderr(Stdio::inherit())
            .output()
            .unwrap();
        assert!(result.status.success());
        if let Some(byte) = result
            .stdout
            .iter()
            .zip(&self.expected)
            .position(|(a, b)| a != b)
        {
            let case = self.ends.partition_point(|&e| e <= byte);
            panic!(
                "{name}: case {case} byte {byte}: original={} Rust={}",
                result.stdout[byte], self.expected[byte]
            );
        }
        assert_eq!(result.stdout.len(), self.expected.len());
        eprintln!(
            "{name}: {} records, {} queries, {} exact bytes",
            self.ends.len(),
            self.queries,
            self.expected.len()
        );
    }
}
fn valid_cases(managed: bool) -> Cases {
    let mut cases = Cases::default();
    let all: Vec<_> = (0..=65535).collect();
    for (kind, b) in fixture::seeds() {
        if managed && kind > 1 {
            continue;
        }
        let q = if kind == 2 {
            vec![0, 1, 2, 65535]
        } else {
            all.clone()
        };
        cases.add(kind, &b, 0, &q);
        if kind < 2 {
            let mut shifted = vec![0x55; 7];
            shifted.extend(&b);
            cases.add(kind, &shifted, 7, &q);
        }
        if kind == 3 {
            for set in [1, 2, 65535] {
                let q: Vec<_> = all.iter().map(|&g| (set << 16) | g).collect();
                cases.add(kind, &b, 0, &q);
            }
        }
    }
    cases
}
#[test]
#[ignore = "requires original C++ compiler; CI runs explicitly"]
fn original_native_layout_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("native");
    let exe = dir.0.join("oracle");
    let mut cc = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    cc.args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ));
    for p in [
        "src/ProGPU.Rust/tests/native_layout.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gdef.cpp",
    ] {
        cc.arg(root.join(p));
    }
    assert!(cc.arg("-o").arg(&exe).status().unwrap().success());
    let mut cases = valid_cases(false);
    let q = [0, 1, 9, 10, 11, 13, 14, 15, 65535, 0x1000a, u32::MAX];
    let seeds = fixture::seeds();
    for (kind, b) in &seeds {
        for length in 0..b.len() {
            cases.add(*kind, &b[..length], 0, &q);
        }
        if *kind < 2 {
            for offset in [1, b.len(), usize::MAX, usize::MAX - 3] {
                cases.add(*kind, b, offset, &q);
            }
        }
    }
    let mut random = 0x987fda12u32;
    for i in 0..20000 {
        let (kind, mut b) = seeds[i % seeds.len()].clone();
        for _ in 0..1 + i % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let at = random as usize % b.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            b[at] = (random >> 24) as u8;
        }
        cases.add(kind, &b, 0, &q);
    }
    cases.policy(root);
    cases.run(&dir.0, &mut Command::new(exe), "original native layout");
}
#[test]
#[ignore = "requires original managed project and .NET 10; CI runs explicitly"]
fn original_managed_layout_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("managed");
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_layout.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
                "-v:q"
            ])
            .arg("-p:UseArtifactsOutput=true")
            .arg(format!(
                "-p:ArtifactsPath={}",
                dir.0.join("artifacts").display()
            ))
            .status()
            .unwrap()
            .success()
    );
    let mut cases = valid_cases(true);
    cases.policy(root);
    cases.run(
        &dir.0,
        Command::new(dotnet).arg(dir.0.join("artifacts/bin/Control/release/Control.dll")),
        "original managed layout",
    );
}
