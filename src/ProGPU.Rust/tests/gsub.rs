//! Complete mutable-buffer comparisons against the unchanged original executor.
use progpu::{
    font::{FontError, layout::*},
    text::ShapingGlyph,
};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[path = "support/gsub_fixture.rs"]
mod fixture;
use fixture::*;
fn glyph(id: u32, cluster: i32) -> ShapingGlyph {
    ShapingGlyph {
        glyph_id: id,
        code_point: if id == 12 { 0x301 } else { 65 },
        cluster,
        flags: 0,
        advance_x: 101,
        advance_y: -202,
        offset_x: -303,
        offset_y: 404,
    }
}
fn state(count: u32) -> GsubState {
    GsubState {
        glyph_count: count,
        ..Default::default()
    }
}
#[test]
fn substitutions_preserve_original_metadata_capacity_and_tail() {
    assert_eq!(std::mem::size_of::<ShapingGlyph>(), 32);
    let bytes = table(&[(2, 0, vec![multiple(&[100, 101, 102])])]);
    let table = LayoutTable::parse(&bytes).unwrap();
    let mut storage = [glyph(10, -8), glyph(20, 7), glyph(900, 20), glyph(901, 21)];
    let initial = storage;
    let options = GsubOptions {
        mark_substituted: true,
        track_arabic_stretch_metadata: true,
        track_substitution_provenance: true,
        ..Default::default()
    };
    let mut s = state(3);
    assert_eq!(
        table.apply_gsub(0, Some(0), &mut storage, options, &mut s),
        Err(FontError::Capacity)
    );
    assert_eq!(storage, initial);
    assert!(!s.applied);
    assert_eq!(s.glyph_count, 3);
    s.glyph_count = 2;
    table
        .apply_gsub(0, None, &mut storage, options, &mut s)
        .unwrap();
    assert_eq!(s.glyph_count, 4);
    assert!(s.applied);
    for (i, actual) in storage.iter().enumerate().take(3) {
        let mut expected = initial[0];
        expected.glyph_id = 100 + i as u32;
        expected.flags = 0xa0080000 | (i as u32) << 20;
        expected.advance_x = i32::MIN;
        assert_eq!(*actual, expected);
    }
    assert_eq!(storage[3], initial[1]);
    let bytes = fixture::table(&[(2, 0, vec![multiple(&[])])]);
    let table = LayoutTable::parse(&bytes).unwrap();
    storage = initial;
    s = state(2);
    table
        .apply_gsub(0, Some(0), &mut storage, GsubOptions::default(), &mut s)
        .unwrap();
    assert_eq!(s.glyph_count, 1);
    assert_eq!(storage, [initial[1], initial[1], initial[2], initial[3]]);
}
#[test]
fn ligatures_merge_complete_adjacent_clusters_and_skipped_marks() {
    let bytes = table(&[(4, 8, vec![ligature(&[20], 88)])]);
    let table = LayoutTable::parse(&bytes).unwrap();
    let mut storage = [
        glyph(9, 8),
        glyph(10, 8),
        glyph(12, -3),
        glyph(20, 7),
        glyph(30, 7),
        glyph(40, 9),
    ];
    let initial = storage;
    let mut s = state(6);
    let options = GsubOptions {
        mark_substituted: true,
        track_fallback_mark_metadata: true,
        ..Default::default()
    };
    table
        .apply_gsub(0, Some(1), &mut storage, options, &mut s)
        .unwrap();
    assert_eq!(s.glyph_count, 5);
    assert!(s.applied);
    assert_eq!(
        storage[..4].iter().map(|g| g.cluster).collect::<Vec<_>>(),
        [-3; 4]
    );
    assert_eq!(storage[1].glyph_id, 88);
    assert_eq!(storage[1].flags, 0xc0000010);
    assert_eq!(storage[2].glyph_id, 12);
    assert_eq!(storage[2].flags, 0x800);
    assert_eq!(storage[3].glyph_id, 30);
    assert_eq!(storage[4], initial[5]);
    assert_eq!(storage[5], initial[5]);
}
#[test]
fn nested_errors_retain_writes_and_withhold_top_level_publication() {
    let bytes = table(&[
        (5, 0, vec![context(false, 3, &[(0, 1), (2, 1)])]),
        (1, 0, vec![single(100)]),
    ]);
    let table = LayoutTable::parse(&bytes).unwrap();
    for position in [None, Some(0)] {
        let mut storage = [glyph(10, 0), glyph(20, 9)];
        let mut s = GsubState {
            context_match_end: Some(7),
            applied: true,
            ..state(2)
        };
        assert_eq!(
            table.apply_gsub(0, position, &mut storage, GsubOptions::default(), &mut s),
            Err(FontError::InvalidFace)
        );
        assert!(!s.applied);
        assert_eq!(s.context_match_end, Some(7));
        assert_eq!(storage[0].glyph_id, 110);
        assert_eq!(storage[1].flags, 3);
    }
    let bytes = fixture::table(&[(5, 0, vec![context(false, 3, &[(0, 0)])])]);
    let mut storage = [glyph(10, 0), glyph(20, 9)];
    let mut s = state(2);
    LayoutTable::parse(&bytes)
        .unwrap()
        .apply_gsub(0, None, &mut storage, GsubOptions::default(), &mut s)
        .unwrap();
    assert!(!s.applied);
    assert_eq!(storage[1].flags, 3);
}
#[test]
fn original_bulk_and_at_filters_are_distinct() {
    let bytes = table(&[(1, 0, vec![single(100)])]);
    let table = LayoutTable::parse(&bytes).unwrap();
    let options = GsubOptions {
        lookup_digest: Some(GlyphSetDigest::default()),
        ..Default::default()
    };
    let mut storage = [glyph(10, 0)];
    let mut s = state(1);
    table
        .apply_gsub(0, None, &mut storage, options, &mut s)
        .unwrap();
    assert!(!s.applied);
    table
        .apply_gsub(0, Some(0), &mut storage, options, &mut s)
        .unwrap();
    assert!(s.applied);
    let bytes = fixture::table(&[(9, 0, vec![single(100)])]);
    let table = LayoutTable::parse(&bytes).unwrap();
    table
        .apply_gsub(0, None, &mut storage, GsubOptions::default(), &mut s)
        .unwrap();
    assert_eq!(
        table.apply_gsub(0, Some(0), &mut storage, GsubOptions::default(), &mut s),
        Err(FontError::InvalidFace)
    );
}
#[test]
fn nested_no_match_continues_at_the_original_storage_slot() {
    let one = words(&[3, 1, 2, 16, 0, 1, 0, 1, 1, 1, 10]);
    let bytes = table(&[
        (
            7,
            0,
            vec![extension(5, &one), extension(2, &multiple(&[100, 101]))],
        ),
        (2, 0, vec![multiple(&[])]),
    ]);
    let mut storage = [glyph(10, 7), glyph(999, 8)];
    let mut s = state(1);
    LayoutTable::parse(&bytes)
        .unwrap()
        .apply_gsub(0, None, &mut storage, GsubOptions::default(), &mut s)
        .unwrap();
    assert!(s.applied);
    assert_eq!(s.glyph_count, 1);
    assert_eq!(storage[0].glyph_id, 100);
    assert_eq!(storage[1].glyph_id, 101);
}
#[derive(Clone)]
struct Case {
    table: Vec<u8>,
    gdef: Vec<u8>,
    index: u16,
    position: u32,
    storage: Vec<ShapingGlyph>,
    count: u32,
    bits: u32,
    alternate: u32,
    required: u32,
    syllable: u8,
    end: u32,
    random: u32,
}
impl Case {
    fn new(table: &[u8]) -> Self {
        let mut storage: Vec<_> = [8, 9, 10, 12, 20, 30, 10, 20]
            .into_iter()
            .enumerate()
            .map(|(i, g)| glyph(g, i as i32 - 4))
            .collect();
        storage.extend((0..8).map(|i| glyph(0x12345000 + i, 999)));
        Self {
            table: table.into(),
            gdef: vec![],
            index: 0,
            position: u32::MAX,
            storage,
            count: 8,
            bits: 0,
            alternate: 1,
            required: 0,
            syllable: 0,
            end: 0,
            random: 0xdeadbeef,
        }
    }
}
fn emit(b: &mut Vec<u8>, v: u32) {
    b.extend(v.to_le_bytes());
}
fn emit_glyph(b: &mut Vec<u8>, g: ShapingGlyph) {
    for v in [
        g.glyph_id,
        g.code_point,
        g.cluster as u32,
        g.flags,
        g.advance_x as u32,
        g.advance_y as u32,
        g.offset_x as u32,
        g.offset_y as u32,
    ] {
        emit(b, v);
    }
}
#[derive(Default)]
struct Cases {
    input: Vec<u8>,
    expected: Vec<u8>,
    ends: Vec<usize>,
}
impl Cases {
    fn add(&mut self, case: &Case) {
        for v in [
            case.table.len() as u32,
            case.gdef.len() as u32,
            u32::from(case.index),
            case.position,
            case.storage.len() as u32,
            case.count,
            case.bits,
            case.alternate,
            case.required,
            u32::from(case.syllable),
            case.end,
            case.random,
        ] {
            emit(&mut self.input, v);
        }
        self.input.extend(&case.table);
        self.input.extend(&case.gdef);
        for &g in &case.storage {
            emit_glyph(&mut self.input, g);
        }
        self.result(case);
        self.ends.push(self.expected.len());
    }
    fn result(&mut self, c: &Case) {
        let table = match LayoutTable::parse(&c.table) {
            Ok(t) => {
                emit(&mut self.expected, 0);
                t
            }
            Err(e) => {
                emit(&mut self.expected, e as u32);
                return;
            }
        };
        let gdef = if c.gdef.is_empty() {
            None
        } else {
            match Gdef::parse(&c.gdef) {
                Ok(g) => Some(g),
                Err(e) => {
                    emit(&mut self.expected, e as u32);
                    return;
                }
            }
        };
        emit(&mut self.expected, 0);
        let mut options = GsubOptions {
            gdef,
            alternate_value: c.alternate,
            required_glyph_flags: c.required,
            restricted_syllable: c.syllable,
            mark_substituted: c.bits & 1 != 0,
            track_fallback_mark_metadata: c.bits & 2 != 0,
            track_arabic_stretch_metadata: c.bits & 4 != 0,
            random_alternate: c.bits & 8 != 0,
            restrict_to_syllable: c.bits & 16 != 0,
            track_substitution_provenance: c.bits & 32 != 0,
            ..Default::default()
        };
        if c.bits & 256 != 0 {
            options.lookup_digest = table.lookup_digest(c.index, 7).ok().flatten();
        }
        if c.bits & 512 != 0 {
            options.lookup_coverage = table
                .single_subtable_coverage(c.index, 7)
                .ok()
                .and_then(|(_, c)| c);
        }
        let mut subs = vec![];
        let mut covs = vec![];
        if c.bits & 1024 != 0
            && let Ok(Some(r)) = table.context_requirements(c.index, 7)
        {
            subs.resize(r.subtables as usize, ContextSubtable::default());
            covs.resize(r.coverages as usize, ContextCoverage::default());
            if table
                .build_context_into(c.index, 7, &mut subs, &mut covs)
                .ok()
                .flatten()
                .is_some()
            {
                options.lookup_context_subtables = &subs;
                options.lookup_context_coverages = &covs;
            }
        }
        let empty = [0, 1, 0, 0];
        if c.bits & 2048 != 0 {
            options.lookup_digest = Some(GlyphSetDigest::default());
        }
        if c.bits & 4096 != 0 {
            options.lookup_coverage = Some(Coverage::parse(&empty, 0).unwrap());
        }
        let mut state = GsubState {
            glyph_count: c.count,
            applied: true,
            context_match_end: (c.bits & 64 != 0).then_some(c.end),
            random_state: (c.bits & 128 != 0).then_some(c.random),
        };
        let mut storage = c.storage.clone();
        let r = table.apply_gsub(
            c.index,
            (c.position != u32::MAX).then_some(c.position),
            &mut storage,
            options,
            &mut state,
        );
        for v in [
            u32::from(r.is_ok()),
            r.err().map_or(0, |e| e as u32),
            state.glyph_count,
            u32::from(state.applied),
            state.context_match_end.unwrap_or(c.end),
            state.random_state.unwrap_or(c.random),
        ] {
            emit(&mut self.expected, v);
        }
        for g in storage {
            emit_glyph(&mut self.expected, g);
        }
    }
}
struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires original C++ compiler; CI runs explicitly"]
fn original_native_gsub_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = Temp(std::env::temp_dir().join(format!("progpu-rust-gsub-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let exe = dir.0.join("oracle");
    let mut cc = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    cc.args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ));
    for p in [
        "src/ProGPU.Rust/tests/native_gsub.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gsub.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gdef.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_digest.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_unicode_shaping.cpp",
        "src/ProGPU.Native/src/Text/Unicode/progpu_native_unicode_category.cpp",
    ] {
        cc.arg(root.join(p));
    }
    cc.arg(format!(
        "-I{}",
        root.join("src/ProGPU.Native/src/Text").display()
    ));
    assert!(cc.arg("-o").arg(&exe).status().unwrap().success());
    let mut cases = Cases::default();
    let seeds = seeds();
    for (i, seed) in seeds.iter().enumerate() {
        let base = Case::new(seed);
        for position in [u32::MAX, 0, 2, 6, 7, 8] {
            for bits in [0, 63, 255, 1791, 2048, 4096] {
                let mut c = base.clone();
                c.position = position;
                c.bits = bits;
                cases.add(&c);
                c.gdef = gdef();
                cases.add(&c);
            }
        }
        for alternate in [0, 1, 2, 3, 4, u32::MAX] {
            let mut c = base.clone();
            c.alternate = alternate;
            c.bits = 255;
            c.position = 2;
            cases.add(&c);
        }
        for count in [0, 1, 2, 7, 16, 17, u32::MAX] {
            let mut c = base.clone();
            c.count = count;
            cases.add(&c);
        }
        if i % 13 == 0 {
            for len in 0..seed.len() {
                let mut c = base.clone();
                c.table.truncate(len);
                c.bits = 255;
                cases.add(&c);
            }
        }
        for (flags, required, syllable) in [
            (0, 1, 0),
            (1, 1, 0),
            (0x2000, 0, 1),
            (0x2000, 0, 2),
            (u32::MAX, 0x40000000, 255),
        ] {
            let mut c = base.clone();
            for g in &mut c.storage {
                g.flags = flags;
            }
            c.storage[4].flags ^= 0x2000;
            c.required = required;
            c.syllable = syllable;
            c.bits = 255;
            cases.add(&c);
            c.bits &= !16;
            cases.add(&c);
        }
    }
    let mut zero = Case::new(seeds.last().unwrap());
    zero.count = 1;
    zero.storage[0] = glyph(10, 7);
    cases.add(&zero);
    let mut random = 0x31415926u32;
    for i in 0..25000 {
        let mut c = Case::new(&seeds[i % seeds.len()]);
        c.bits = (i % 8192) as u32;
        c.position = [u32::MAX, 0, 2, 7][i % 4];
        for _ in 0..1 + i % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let at = random as usize % c.table.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            c.table[at] = (random >> 24) as u8;
        }
        cases.add(&c);
    }
    for i in 0..10000 {
        let mut c = Case::new(&seeds[i % seeds.len()]);
        c.bits = (i % 8192) as u32;
        c.random = random;
        c.end = (i % 11) as u32;
        c.position = [u32::MAX, 0, 2][i % 3];
        if i % 3 != 0 {
            c.gdef = gdef();
        }
        for (j, g) in c.storage.iter_mut().enumerate().take(c.count as usize) {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            if i % 2 == 0 {
                g.flags = random;
            }
            g.cluster = (random % 7) as i32 - 3;
            g.code_point =
                [0x301, 0x903, 0x488, 0x200d, 65, 0x110000, 0xd800, u32::MAX][(i + j) % 8];
            if i % 5 == 0 {
                g.glyph_id = [0, 65535, 65536, u32::MAX, 10, 20, 30, 12][(i + j) % 8];
            }
        }
        cases.add(&c);
    }
    // Large expansion retains bounded metadata and all 300 component positions.
    let mut c = Case::new(&table(&[(
        2,
        0,
        vec![multiple(&(0..300).collect::<Vec<_>>())],
    )]));
    c.storage.resize(320, glyph(0x12345678, -100));
    c.bits = 255;
    cases.add(&c);
    let input = dir.0.join("input.bin");
    std::fs::write(&input, &cases.input).unwrap();
    let output = Command::new(exe)
        .stdin(Stdio::from(std::fs::File::open(input).unwrap()))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "original GSUB: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    if let Some(at) = output
        .stdout
        .iter()
        .zip(&cases.expected)
        .position(|(a, b)| a != b)
    {
        let case = cases.ends.partition_point(|&end| end <= at);
        let begin = if case == 0 { 0 } else { cases.ends[case - 1] };
        panic!(
            "original GSUB case {case} at byte {}: native={} Rust={}; output {:?} expected {:?}",
            at - begin,
            output.stdout[at],
            cases.expected[at],
            &output.stdout[begin..(begin + 32).min(output.stdout.len())],
            &cases.expected[begin..(begin + 32).min(cases.expected.len())]
        );
    }
    assert_eq!(output.stdout.len(), cases.expected.len());
    eprintln!(
        "original native GSUB: {} records / {} exact result and complete storage bytes",
        cases.ends.len(),
        cases.expected.len()
    );
}

#[path = "support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
#[test]
#[ignore = "requires actual original C# GSUB and .NET 10; CI runs explicitly"]
fn original_managed_gsub_shared_contracts() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-rust-managed-gsub-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"), format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>", xml("src/ProGPU.Rust/tests/managed_gsub.cs"), xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let font = font::sfnt(&font::tables(vec![]), 0);
    let mut input = vec![];
    emit(&mut input, font.len() as u32);
    input.extend(font);
    let mut expected = vec![];
    let mut ends = vec![];
    let mut seeds = vec![];
    for (kind, sub) in [
        (1, single(100)),
        (1, single(65530)),
        (1, single2()),
        (2, multiple(&[])),
        (2, multiple(&[77])),
        (2, multiple(&[77, 78, 79])),
        (3, multiple(&[100, 200, 300])),
        (4, ligature(&[20], 88)),
        (4, ligature(&[20, 30], 89)),
        (8, reverse()),
    ] {
        for ext in [false, true] {
            seeds.push(table(&[(
                if ext { 7 } else { kind },
                0,
                vec![if ext {
                    extension(kind, &sub)
                } else {
                    sub.clone()
                }],
            )]));
        }
    }
    for chain in [false, true] {
        for format in [1, 2, 3] {
            for records in [
                vec![],
                vec![(0, 1)],
                vec![(0, 1), (1, 1)],
                vec![(0, 2)],
                vec![(0, 3)],
                vec![(0, 4)],
                vec![(0, 5)],
            ] {
                seeds.push(table(&[
                    (
                        if chain { 6 } else { 5 },
                        0,
                        vec![context(chain, format, &records)],
                    ),
                    (1, 0, vec![single(100)]),
                    (2, 0, vec![multiple(&[77, 78, 79])]),
                    (2, 0, vec![multiple(&[])]),
                    (4, 0, vec![ligature(&[20], 88)]),
                    (3, 0, vec![multiple(&[100, 200, 300])]),
                ]));
            }
        }
    }
    for bytes in seeds {
        let table = LayoutTable::parse(&bytes).unwrap();
        let lookup = table.lookup(0).unwrap();
        let mut offset = lookup.subtable_offset(0).unwrap();
        let mut kind = lookup.lookup_type();
        if kind == 7 {
            kind = u16::from_be_bytes(bytes[offset + 2..offset + 4].try_into().unwrap());
            offset += 8;
        }
        for ids in [
            vec![8, 9, 10, 20, 30],
            vec![10, 20, 30, 10, 20],
            vec![10, 30, 20],
            vec![65535, 10, 0],
        ] {
            for position in 0..ids.len() {
                for cluster_mode in 0..4 {
                    let mut storage: Vec<_> = ids
                        .iter()
                        .enumerate()
                        .map(|(i, &g)| {
                            glyph(
                                g,
                                match cluster_mode {
                                    0 => i as i32,
                                    1 => -(i as i32),
                                    2 => i as i32 / 2,
                                    _ => -7,
                                },
                            )
                        })
                        .collect();
                    for g in &mut storage {
                        g.flags = 4;
                    }
                    let count = storage.len();
                    for v in [
                        bytes.len() as u32,
                        0,
                        position as u32,
                        count as u32,
                        if kind == 8 { offset as u32 } else { u32::MAX },
                    ] {
                        emit(&mut input, v);
                    }
                    input.extend(&bytes);
                    for &g in &storage {
                        emit_glyph(&mut input, g);
                    }
                    storage.resize(count + 8, glyph(0, 0));
                    let mut state = state(count as u32);
                    table
                        .apply_gsub(
                            0,
                            Some(position as u32),
                            &mut storage,
                            GsubOptions {
                                mark_substituted: true,
                                track_fallback_mark_metadata: true,
                                track_arabic_stretch_metadata: true,
                                ..Default::default()
                            },
                            &mut state,
                        )
                        .unwrap();
                    emit(&mut expected, u32::from(state.applied));
                    emit(&mut expected, state.glyph_count);
                    for g in &storage[..state.glyph_count as usize] {
                        for v in [
                            g.glyph_id,
                            g.code_point,
                            g.cluster as u32,
                            g.flags & 7,
                            g.advance_x as u32,
                            g.advance_y as u32,
                            g.offset_x as u32,
                            g.offset_y as u32,
                            u32::from(g.flags & 0x80000000 != 0),
                            u32::from(g.flags & 0x40000000 != 0),
                            u32::from(g.flags & 0x20000000 != 0),
                            (g.flags & 0x0ff00000) >> 20,
                            (g.flags & 0x7f8) >> 3,
                            if g.flags & 0x7f800 == 0 {
                                255
                            } else {
                                ((g.flags & 0x7f800) >> 11) - 1
                            },
                        ] {
                            emit(&mut expected, v);
                        }
                    }
                    ends.push(expected.len());
                }
            }
        }
    }
    let path = dir.0.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let result = Command::new(dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    if let Some(at) = result
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        panic!(
            "original managed GSUB shared case {} byte {at}: C#={} Rust={}",
            ends.partition_point(|&e| e <= at),
            result.stdout[at],
            expected[at]
        );
    }
    assert_eq!(result.stdout.len(), expected.len());
    eprintln!(
        "original managed GSUB: {} shared-policy records / {} exact active glyph and metadata bytes",
        ends.len(),
        expected.len()
    );
}
