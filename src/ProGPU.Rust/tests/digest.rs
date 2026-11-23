//! Differential controls execute original sources, never a translated oracle.
use progpu::font::{FontError, SfntFace, Tag, layout::*};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
};
#[path = "support/digest_fixture.rs"]
mod fixture;
#[path = "support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
fn emit(b: &mut Vec<u8>, n: u32) {
    b.extend(n.to_le_bytes());
}
fn masks(b: &mut Vec<u8>, m: &[u64]) {
    for &n in m {
        b.extend(n.to_le_bytes());
    }
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
fn snapshot(
    data: &[u8],
    index: u16,
    extension: u16,
    caps: [usize; 2],
    q: &[u16],
    managed: bool,
) -> Vec<u8> {
    let mut b = vec![];
    let parsed = LayoutTable::parse(data);
    let table = if managed {
        parsed.unwrap()
    } else {
        let Some(t) = status(&mut b, parsed) else {
            return b;
        };
        t
    };
    let result = table.lookup_digest(index, extension);
    let digest = if managed {
        result.unwrap()
    } else {
        let Some(d) = status(&mut b, result) else {
            return b;
        };
        d
    };
    emit(&mut b, u32::from(digest.is_some()));
    if let Some(d) = digest {
        if managed {
            masks(&mut b, d.managed().masks());
        } else {
            masks(&mut b, d.masks());
        }
        for &g in q {
            emit(
                &mut b,
                u32::from(if managed {
                    d.managed().may_have(g)
                } else {
                    d.may_have(g)
                }),
            );
            let mut other = GlyphSetDigest::default();
            other.add_range(g, g.saturating_add(3));
            other.add(g.wrapping_add(4096));
            emit(
                &mut b,
                u32::from(if managed {
                    d.managed().may_intersect(&other.managed())
                } else {
                    d.may_intersect(&other)
                }),
            );
        }
    }
    if !managed {
        let (lookup, coverage) =
            status(&mut b, table.single_subtable_coverage(index, extension)).unwrap();
        for n in [
            lookup.flags(),
            lookup.lookup_type(),
            lookup.subtable_count(),
        ] {
            emit(&mut b, u32::from(n));
        }
        emit(&mut b, u32::from(coverage.is_some()));
        if let Some(c) = coverage {
            for &g in q {
                emit(&mut b, c.find(g).map_or(u32::MAX, u32::from));
            }
        }
    }
    let result = table.context_requirements(index, extension);
    let requirements = if managed {
        result.unwrap()
    } else {
        status(&mut b, result).unwrap()
    };
    emit(&mut b, u32::from(requirements.is_some()));
    if let Some(r) = requirements {
        emit(&mut b, r.subtables);
        emit(&mut b, r.coverages);
        if managed {
            let p = PreparedContext::new(OwnedLayoutTable::new(data).unwrap(), index, extension)
                .unwrap()
                .unwrap();
            emit(&mut b, u32::from(p.lookup_flags()));
            for s in p.subtables() {
                for n in [
                    s.coverage_offset,
                    s.coverage_count,
                    u32::from(s.backtrack_count),
                    u32::from(s.input_count),
                ] {
                    emit(&mut b, n);
                }
            }
            for i in 0..p.coverage_count() {
                let c = p.coverage(i).unwrap();
                masks(&mut b, c.digest().managed().masks());
                for &g in q {
                    emit(&mut b, u32::from(c.contains(g)));
                }
            }
            return b;
        }
    }
    if managed {
        return b;
    }
    let sentinel = ContextSubtable {
        coverage_offset: 0x12345678,
        coverage_count: 0x23456789,
        backtrack_count: 0x3456,
        input_count: 0x4567,
    };
    let mut s = vec![sentinel; caps[0]];
    let mut c = vec![ContextCoverage::default(); caps[1]];
    let built = table.build_context_into(index, extension, &mut s, &mut c);
    emit(&mut b, built.as_ref().err().map_or(0, |e| *e as u32));
    let flags = built.ok().flatten();
    emit(&mut b, u32::from(flags.is_some()));
    emit(&mut b, u32::from(flags.unwrap_or(0)));
    for s in s {
        for n in [
            s.coverage_offset,
            s.coverage_count,
            u32::from(s.backtrack_count),
            u32::from(s.input_count),
        ] {
            emit(&mut b, n);
        }
    }
    for c in c {
        masks(&mut b, c.digest().masks());
        for &g in q {
            emit(&mut b, c.coverage().find(g).map_or(u32::MAX, u32::from));
        }
    }
    b
}
#[test]
fn range_masks_match_explicit_sets_and_keep_distinct_policies() {
    for first in 0..=65535u16 {
        let mut a = GlyphSetDigest::default();
        a.add(first);
        let mut b = GlyphSetDigest::default();
        b.add_range(first, first);
        assert_eq!(a, b);
        assert!(a.may_have(first));
        let mut c = ManagedGlyphSetDigest::default();
        c.add(first);
        assert_eq!(a.managed(), c);
    }
    for first in [0, 1, 3, 15, 63, 64, 255, 256, 1023, 4095, 65534, 65535] {
        for last in [0, 1, 2, 63, 64, 127, 256, 1024, 4096, 65535] {
            let mut a = GlyphSetDigest::default();
            let mut m = ManagedGlyphSetDigest::default();
            if last >= first {
                for g in first..=last {
                    a.add(g);
                    m.add(g);
                }
            }
            let mut b = GlyphSetDigest::default();
            b.add_range(first, last);
            let mut n = ManagedGlyphSetDigest::default();
            n.add_range(first, last);
            assert_eq!(a, b);
            assert_eq!(m, n);
            assert_eq!(b.managed(), n);
        }
    }
    let mut d = GlyphSetDigest::default();
    d.add(0);
    assert!(!d.may_have(4096));
    assert!(d.managed().may_have(4096));
    let mut other = GlyphSetDigest::default();
    other.add(4096);
    assert!(!d.may_intersect(&other));
    assert!(d.managed().may_intersect(&other.managed()));
}
#[test]
fn complete_preflight_ownership_and_optional_payloads() {
    let covers = [
        fixture::coverage1(&[10, 11]),
        fixture::coverage1(&[64]),
        fixture::coverage1(&[65]),
    ];
    let table = fixture::table(6, 0xffff, &[fixture::context(true, [1, 1, 1], &covers)]);
    let view = LayoutTable::parse(&table).unwrap();
    assert_eq!(
        view.context_requirements(0, 7).unwrap(),
        Some(ContextRequirements {
            subtables: 1,
            coverages: 3
        })
    );
    let mut s = [ContextSubtable::default(); 2];
    let mut c = [ContextCoverage::default(); 4];
    assert_eq!(
        view.build_context_into(0, 7, &mut s, &mut c[..2]),
        Err(FontError::Capacity)
    );
    assert_eq!(s, [ContextSubtable::default(); 2]);
    assert_eq!(
        view.build_context_into(0, 7, &mut s, &mut c),
        Ok(Some(65535))
    );
    assert_eq!(s[0].backtrack_count, 1);
    assert_eq!(s[0].input_count, 1);
    assert_eq!(s[1], ContextSubtable::default());
    assert!(c[0].contains(11));
    assert!(c[1].contains(64));
    assert!(c[2].contains(65));
    assert!(!c[3].contains(0));
    let mut tables = font::tables(vec![]);
    tables.push((*b"GSUB", table.clone()));
    let bytes: Arc<[u8]> = font::sfnt(&tables, 0).into();
    let face = SfntFace::new(bytes.clone(), 0).unwrap();
    let owned = OwnedLayoutTable::from_face(face.clone(), Tag::new(*b"GSUB"))
        .unwrap()
        .unwrap();
    let ptr = owned.view().bytes().as_ptr();
    let p = PreparedContext::new(owned, 0, 7).unwrap().unwrap();
    let retained = p.clone();
    drop(p);
    drop(face);
    drop(bytes);
    assert_eq!(retained.table().view().bytes().as_ptr(), ptr);
    assert!(retained.table().source().is_some());
    assert!(retained.coverage(2).unwrap().contains(65));
    assert!(retained.coverage(3).is_none());
    // Digest admission must not accidentally become exact-coverage admission.
    let bytes = fixture::table(1, 0, &[fixture::simple(&fixture::coverage1(&[11, 10]))]);
    let v = LayoutTable::parse(&bytes).unwrap();
    assert!(v.lookup_digest(0, 7).unwrap().unwrap().may_have(10));
    assert!(v.single_subtable_coverage(0, 7).unwrap().1.is_none());
    let bytes = fixture::table(
        5,
        0,
        &[fixture::context(
            false,
            [0, 1, 0],
            &[fixture::coverage1(&[11, 10])],
        )],
    );
    let v = LayoutTable::parse(&bytes).unwrap();
    let before = s;
    assert_eq!(v.build_context_into(0, 7, &mut s, &mut c), Ok(None));
    assert_eq!(s, before);
    fn threads<T: Send + Sync>() {}
    threads::<PreparedContext>();
}
struct Cleanup(PathBuf);
impl Drop for Cleanup {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn temp(name: &str) -> Cleanup {
    let dir = std::env::temp_dir().join(format!("progpu-digest-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    Cleanup(dir)
}
#[derive(Default)]
struct Cases {
    input: Vec<u8>,
    expected: Vec<u8>,
    ends: Vec<usize>,
    queries: usize,
}
impl Cases {
    fn add(
        &mut self,
        data: &[u8],
        index: u16,
        extension: u16,
        caps: [usize; 2],
        q: &[u16],
        managed: bool,
    ) {
        for n in [
            data.len() as u32,
            u32::from(index),
            u32::from(extension),
            caps[0] as u32,
            caps[1] as u32,
            q.len() as u32,
        ] {
            emit(&mut self.input, n);
        }
        self.input.extend(data);
        for &g in q {
            emit(&mut self.input, u32::from(g));
        }
        self.expected
            .extend(snapshot(data, index, extension, caps, q, managed));
        self.ends.push(self.expected.len());
        self.queries += q.len();
    }
    fn run(&self, dir: &Path, command: &mut Command, name: &str) {
        let input = dir.join("input.bin");
        std::fs::write(&input, &self.input).unwrap();
        let r = command
            .stdin(std::fs::File::open(input).unwrap())
            .stderr(Stdio::inherit())
            .output()
            .unwrap();
        assert!(r.status.success());
        if let Some(at) = r
            .stdout
            .iter()
            .zip(&self.expected)
            .position(|(a, b)| a != b)
        {
            panic!(
                "{name}: case {} byte {at}: original={} Rust={}",
                self.ends.partition_point(|&e| e <= at),
                r.stdout[at],
                self.expected[at]
            );
        }
        assert_eq!(r.stdout.len(), self.expected.len());
        eprintln!(
            "{name}: {} records / {} glyph queries / {} exact bytes",
            self.ends.len(),
            self.queries,
            self.expected.len()
        );
    }
}
#[test]
#[ignore = "requires original C++ compiler; CI runs explicitly"]
fn original_native_digest_differential() {
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
        "src/ProGPU.Rust/tests/native_digest.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_digest.cpp",
    ] {
        cc.arg(root.join(p));
    }
    assert!(cc.arg("-o").arg(&exe).status().unwrap().success());
    let mut cases = Cases::default();
    let seeds = fixture::seeds();
    let q = [0, 1, 9, 10, 11, 63, 64, 65, 66, 4096, 65535];
    for (extension, b) in &seeds {
        for caps in [[0, 0], [1, 3], [1, 4], [2, 8], [3, 9]] {
            cases.add(b, 0, *extension, caps, &q, false);
        }
        for length in 0..b.len() {
            cases.add(&b[..length], 0, *extension, [1, 2], &q, false);
        }
        cases.add(b, 1, *extension, [1, 2], &q, false);
        cases.add(b, 65535, *extension, [1, 2], &q, false);
    }
    let mut random = 0xdeadbeefu32;
    for i in 0..20000 {
        let (extension, mut b) = seeds[i % seeds.len()].clone();
        for _ in 0..1 + i % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let at = random as usize % b.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            b[at] = (random >> 24) as u8;
        }
        cases.add(&b, 0, extension, [2, 4], &q, false);
    }
    let all: Vec<_> = (0..=65535).collect();
    for (extension, b) in seeds.iter().step_by(13) {
        cases.add(b, 0, *extension, [3, 9], &all, false);
    }
    cases.run(&dir.0, &mut Command::new(exe), "original native digest");
}
#[test]
#[ignore = "requires actual original managed project and .NET 10; CI runs explicitly"]
fn original_managed_digest_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("managed");
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"), format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>", xml("src/ProGPU.Rust/tests/managed_digest.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let mut cases = Cases::default();
    let all: Vec<_> = (0..=65535).collect();
    for (extension, b) in fixture::seeds() {
        cases.add(&b, 0, extension, [0, 0], &all, true);
    }
    cases.run(
        &dir.0,
        Command::new(dotnet).arg(dir.0.join("artifacts/bin/Control/release/Control.dll")),
        "original managed digest",
    );
}
