//! Full current managed glyph-record and executor comparisons.
use progpu::{
    font::{FontError, SfntFace, layout::*},
    text::{ManagedShapingGlyph, unicode::ManagedMarkProfile},
};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[path = "support/gsub_fixture.rs"]
mod fixture;
#[path = "support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
#[path = "support/managed_glyph_fixture.rs"]
mod records;
use fixture::*;
fn face(gdef: &[u8]) -> SfntFace {
    SfntFace::new(font_bytes(gdef, 0, 0), 0).unwrap()
}
fn font_bytes(gdef: &[u8], gsub_len: usize, gpos_len: usize) -> Vec<u8> {
    let mut tables = font::tables(vec![]);
    if !gdef.is_empty() {
        tables.push((*b"GDEF", gdef.into()));
    }
    if gsub_len != 0 {
        tables.push((*b"GSUB", vec![0; gsub_len]));
    }
    if gpos_len != 0 {
        tables.push((*b"GPOS", vec![0; gpos_len]));
    }
    font::sfnt(&tables, 0)
}
fn glyph(id: u16, cluster: i32, cp: u32) -> ManagedShapingGlyph {
    let mut g = ManagedShapingGlyph::new(id, cluster, cp);
    g.advance_x = 101;
    g.advance_y = -202;
    g.offset_x = -303;
    g.offset_y = 404;
    g.grapheme_cluster = -700;
    g.character_cluster = 900;
    g.attachment_kind = 5;
    g.attachment_target = 10123;
    g
}
#[test]
fn independent_metadata_and_ligated_expansion_keep_actual_source() {
    let bytes = table(&[(2, 0, vec![multiple(&[100, 101, 102])])]);
    let source = face(&[]);
    let mut base = glyph(10, -8, 65);
    base.ligated = 7;
    base.multiplied = 19;
    base.ligature_component_count = 42;
    let mut mark = glyph(12, -8, 0x301);
    mark.ligature_component = 0;
    let tail = glyph(20, 7, 66);
    let mut buffer = ManagedGlyphBuffer::new(source.clone(), vec![base, mark, tail]).unwrap();
    drop(source);
    let table = ManagedGsubTable::new(bytes).unwrap();
    assert!(table.apply_lookup_at(&mut buffer, 0, 0).unwrap());
    assert_eq!(
        buffer
            .glyphs()
            .iter()
            .map(|g| g.glyph_index)
            .collect::<Vec<_>>(),
        [100, 12, 101, 102, 20]
    );
    assert_eq!(buffer.glyphs()[1], mark);
    assert_eq!(buffer.glyphs()[4], tail);
    for (index, component) in [(0, 0), (2, 1), (3, 2)] {
        let mut expected = base;
        expected.glyph_index = 100 + component;
        expected.substituted = 1;
        expected.multiplied = 1;
        expected.multiple_substitution_component = component as u8;
        assert_eq!(buffer.glyphs()[index], expected);
    }
    assert_eq!(buffer.state().context_match_end, 4);
    assert_eq!(buffer.nested_lookup_depth(), 0);
    assert_eq!(buffer.mark_filtering_coverage(), -1);
}
#[test]
fn managed_ligature_ignorables_and_single_component_are_independent() {
    let table = ManagedGsubTable::new(table(&[(4, 8, vec![ligature(&[20], 88)])])).unwrap();
    for (cp, expected) in [
        (0x34f, false),
        (0x200c, false),
        (0x200d, true),
        (0xad, true),
        (0x301, true),
    ] {
        let mut buffer = ManagedGlyphBuffer::new(
            face(&[]),
            vec![glyph(10, 0, 65), glyph(99, 1, cp), glyph(20, 2, 66)],
        )
        .unwrap();
        assert_eq!(table.apply_lookup_at(&mut buffer, 0, 0).unwrap(), expected);
    }
    let one = ManagedGsubTable::new(fixture::table(&[(4, 0, vec![ligature(&[], 88)])])).unwrap();
    let mut g = glyph(10, 7, 65);
    g.multiplied = 5;
    let mut buffer = ManagedGlyphBuffer::new(face(&[]), vec![g]).unwrap();
    one.apply_lookup_at(&mut buffer, 0, 0).unwrap();
    assert_eq!(buffer.glyphs()[0].ligated, 1);
    assert_eq!(buffer.glyphs()[0].multiplied, 0);
    assert_eq!(buffer.glyphs()[0].ligature_component_count, 1);
}
#[test]
fn source_read_errors_restore_private_state_and_cycles_are_bounded() {
    let mut sub = words(&[1, 6, 100, 1, 65535]);
    sub.extend(words(&[10]));
    let table = ManagedGsubTable::new(table(&[(1, 16, vec![sub])])).unwrap();
    let mut buffer = ManagedGlyphBuffer::new(face(&gdef()), vec![glyph(10, 0, 65)]).unwrap();
    assert_eq!(
        table.apply_lookup_at(&mut buffer, 0, 0),
        Err(FontError::InvalidFace)
    );
    assert_eq!(buffer.nested_lookup_depth(), 0);
    assert_eq!(buffer.mark_filtering_coverage(), -1);
    let cyclic =
        ManagedGsubTable::new(fixture::table(&[(7, 0, vec![words(&[1, 7, 0, 0])])])).unwrap();
    assert_eq!(
        cyclic.apply_lookup_at(&mut buffer, 0, 0),
        Err(FontError::InvalidFace)
    );
    assert_eq!(buffer.nested_lookup_depth(), 0);
}
#[test]
fn execution_budget_is_separate_from_depth_and_retains_dependency() {
    let table = ManagedGsubTable::new(table(&[(
        5,
        16,
        vec![context(false, 3, &[(0, 0), (0, 0)])],
    )]))
    .unwrap();
    let original = vec![glyph(10, 0, 65), glyph(20, 7, 66)];
    let mut buffer = ManagedGlyphBuffer::new(face(&gdef()), original.clone()).unwrap();
    assert_eq!(
        table.apply_lookup_at_with_limits(
            &mut buffer,
            0,
            0,
            ManagedGsubLimits {
                max_lookup_calls: 0
            }
        ),
        Err(FontError::Capacity)
    );
    assert_eq!(buffer.glyphs(), original);
    assert_eq!(
        table.apply_lookup_at_with_limits(
            &mut buffer,
            0,
            0,
            ManagedGsubLimits {
                max_lookup_calls: 128
            }
        ),
        Err(FontError::Capacity)
    );
    assert_eq!(buffer.glyphs()[1].flags, 3);
    assert_eq!(buffer.nested_lookup_depth(), 0);
    assert_eq!(buffer.mark_filtering_coverage(), -1);
    assert_eq!(buffer.state().context_match_end, 0);
    let single = ManagedGsubTable::new(fixture::table(&[(
        5,
        0,
        vec![context(false, 3, &[(0, 0)])],
    )]))
    .unwrap();
    assert_eq!(single.apply_lookup_at(&mut buffer, 0, 0), Ok(false));
}
#[test]
fn rune_profiles_and_byte_class_fallback_remain_independent() {
    for (cp, expected) in [
        (0x301, [true, true, true]),
        (0x897, [false, true, true]),
        (0x1acf, [false, false, true]),
    ] {
        for (profile, mark) in [
            ManagedMarkProfile::Unicode15_1,
            ManagedMarkProfile::Unicode16_0,
            ManagedMarkProfile::Unicode17_0,
        ]
        .into_iter()
        .zip(expected)
        {
            assert_eq!(profile.is_mark(cp), Ok(mark));
        }
    }
    for value in [0, 1, 2, 3, 4, 255, 256, 257, 258, 259, 512, 65535] {
        let mut gd = gdef();
        put(&mut gd, 24, value);
        let buffer = ManagedGlyphBuffer::new(face(&gd), vec![glyph(12, 0, 0x110000)]).unwrap();
        assert_eq!(
            buffer.glyph_class(0),
            if value as u8 == 0 {
                Err(FontError::InvalidGlyph)
            } else {
                Ok(u16::from(value as u8))
            }
        );
    }
}
#[derive(Clone)]
struct Case {
    data: Vec<u8>,
    font: usize,
    index: u16,
    position: usize,
    reverse: Option<usize>,
    flags: u16,
    state: ManagedGlyphState,
    glyphs: Vec<ManagedShapingGlyph>,
}
impl Case {
    fn new(data: &[u8]) -> Self {
        Self {
            data: data.into(),
            font: 0,
            index: 0,
            position: 2,
            reverse: None,
            flags: 0,
            state: ManagedGlyphState::default(),
            glyphs: [8, 9, 10, 12, 20, 30, 10, 20]
                .into_iter()
                .enumerate()
                .map(|(i, g)| glyph(g, i as i32 - 4, if g == 12 { 0x301 } else { 65 }))
                .collect(),
        }
    }
}
fn emit(b: &mut Vec<u8>, v: u32) {
    b.extend(v.to_le_bytes());
}
struct Cases {
    input: Vec<u8>,
    expected: Vec<u8>,
    ends: Vec<usize>,
    faces: Vec<SfntFace>,
    profile: ManagedMarkProfile,
    bounded_out: Vec<Vec<u8>>,
    font_prefix: usize,
}
impl Cases {
    fn new(fonts: Vec<Vec<u8>>) -> Self {
        let mut input = vec![];
        emit(&mut input, fonts.len() as u32);
        for f in &fonts {
            emit(&mut input, f.len() as u32);
            input.extend(f);
        }
        Self {
            font_prefix: input.len(),
            input,
            expected: vec![],
            profile: ManagedMarkProfile::default(),
            bounded_out: vec![],
            ends: vec![],
            faces: fonts
                .into_iter()
                .map(|b| SfntFace::new(b, 0).unwrap())
                .collect(),
        }
    }
    fn add(&mut self, c: &Case) {
        let input_start = self.input.len();
        for n in [
            c.data.len() as u32,
            c.font as u32,
            u32::from(c.index),
            c.position as u32,
            c.reverse.map_or(u32::MAX, |v| v as u32),
            u32::from(c.flags),
            u32::from(c.state.traversal.manual_joiners)
                | u32::from(c.state.traversal.restrict_to_syllable) << 1,
            u32::from(c.state.traversal.lookup_syllable),
            c.state.context_match_end as u32,
            c.state.random_state,
            c.glyphs.len() as u32,
        ] {
            emit(&mut self.input, n);
        }
        self.input.extend(&c.data);
        for &g in &c.glyphs {
            for n in records::words(g) {
                emit(&mut self.input, n);
            }
        }
        let mut buffer = ManagedGlyphBuffer::with_mark_profile(
            self.faces[c.font].clone(),
            c.glyphs.clone(),
            self.profile,
        )
        .unwrap();
        buffer.set_state(c.state);
        let table = ManagedGsubTable::new(c.data.clone()).unwrap();
        let result = if let Some(at) = c.reverse {
            table.apply_reverse_at(&mut buffer, at, c.position, c.flags)
        } else {
            table.apply_lookup_at(&mut buffer, c.index, c.position)
        };
        if result == Err(FontError::Capacity) {
            let hash = c.data.iter().fold(0xcbf29ce484222325u64, |h, b| {
                (h ^ u64::from(*b)).wrapping_mul(0x100000001b3)
            });
            eprintln!(
                "Work budget rejected corpus candidate {}, {} table bytes, FNV64 {hash:016x}",
                self.ends.len() + self.bounded_out.len(),
                c.data.len()
            );
            self.bounded_out.push(self.input.split_off(input_start));
            assert_eq!(buffer.nested_lookup_depth(), 0);
            assert_eq!(buffer.mark_filtering_coverage(), -1);
            return;
        }
        for n in [
            result.err().map_or(0, |e| e as u32),
            u32::from(result.unwrap_or(false)),
            buffer.glyphs().len() as u32,
            buffer.state().context_match_end as u32,
            buffer.state().random_state,
            buffer.mark_filtering_coverage() as u32,
            buffer.nested_lookup_depth(),
        ] {
            emit(&mut self.expected, n);
        }
        for i in 0..buffer.glyphs().len() {
            emit(
                &mut self.expected,
                buffer
                    .glyph_class(i)
                    .map_or_else(|e| 0x80000000 | e as u32, u32::from),
            );
        }
        for &g in buffer.glyphs() {
            for n in records::words(g) {
                emit(&mut self.expected, n);
            }
        }
        self.ends.push(self.expected.len());
    }
}
struct ChildGuard(std::process::Child);
impl Drop for ChildGuard {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}
struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires actual current original managed executor and .NET 10; CI runs explicitly"]
fn original_current_managed_executor_full_records() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-managed-executor-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_gsub_execution.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let gd = gdef();
    let mut fonts = vec![font_bytes(&[], 0, 0), font_bytes(&gd, 0, 0)];
    for n in 1..gd.len() {
        fonts.push(font_bytes(&gd[..n], 0, 0));
    }
    let mut blocked = gd.clone();
    blocked.resize(442, 0);
    fonts.push(font_bytes(&blocked, 2874, 42038));
    fonts.push(font_bytes(&blocked, 2874, 42039));
    for value in [
        0, 1, 2, 3, 4, 5, 255, 256, 257, 258, 259, 260, 511, 512, 65535,
    ] {
        let mut raw = gd.clone();
        fixture::put(&mut raw, 24, value);
        fonts.push(font_bytes(&raw, 0, 0));
    }
    let mut cases = Cases::new(fonts);
    let probe = Command::new(&dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .env("PROGPU_RUST_GSUB_MARK_PROBE", "1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    use std::io::Write;
    let mut probe = probe;
    probe.stdin.take().unwrap().write_all(&cases.input).unwrap();
    let probe = probe.wait_with_output().unwrap();
    assert!(
        probe.status.success(),
        "{}",
        String::from_utf8_lossy(&probe.stderr)
    );
    assert_eq!(probe.stdout.len(), 0x110011 * 4);
    let profiles: Vec<_> = [
        ManagedMarkProfile::Unicode15_1,
        ManagedMarkProfile::Unicode16_0,
        ManagedMarkProfile::Unicode17_0,
    ]
    .into_iter()
    .filter(|&profile| {
        (0..=0x110010)
            .zip(probe.stdout.chunks_exact(4))
            .all(|(cp, bytes)| {
                let expected = profile
                    .is_mark(cp)
                    .map_or(0x80000006, |mark| if mark { 3 } else { 1 });
                expected == u32::from_le_bytes(bytes.try_into().unwrap())
            })
    })
    .collect();
    assert_eq!(
        profiles.len(),
        1,
        "Actual source mark inventory must match exactly one explicit profile"
    );
    cases.profile = profiles[0];
    eprintln!(
        "Original Rune/GDEF class fallback: {:?}, 1114129 exact classifications; {}",
        cases.profile,
        String::from_utf8_lossy(&probe.stderr).trim()
    );
    for font in 0..cases.faces.len() {
        for cp in [65, 0x301, 0x897, 0x1acf, 0xd800, 0x110000] {
            let mut c = Case::new(&[]);
            c.font = font;
            c.glyphs = (10..16).map(|id| glyph(id, i32::from(id), cp)).collect();
            cases.add(&c);
        }
    }
    let seeds = seeds();
    for (i, data) in seeds.iter().enumerate() {
        let base = Case::new(data);
        for font in [0, 1] {
            for position in [0, 2, 6, 7] {
                for mode in 0..4 {
                    let mut c = base.clone();
                    c.font = font;
                    c.position = position;
                    c.state.context_match_end = if mode == 3 { 99 } else { 0 };
                    c.state.traversal.manual_joiners = mode & 1 != 0;
                    if mode >= 2 {
                        for (j, g) in c.glyphs.iter_mut().enumerate() {
                            g.ligated = 7;
                            g.multiplied = 9;
                            g.ligature_component = if j % 3 == 1 { 0 } else { 255 };
                            g.multiple_substitution_component = 37;
                            g.ligature_component_count = 42;
                        }
                    }
                    cases.add(&c);
                }
            }
        }
        if i % 13 == 0 {
            for length in 0..data.len() {
                let mut c = base.clone();
                c.data.truncate(length);
                cases.add(&c);
            }
        }
    }
    let mut random = 0xabcdef01u32;
    for i in 0..25000 {
        let mut c = Case::new(&seeds[i % seeds.len()]);
        c.font = i % cases.faces.len();
        for _ in 0..1 + i % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let at = random as usize % c.data.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            c.data[at] = (random >> 24) as u8;
        }
        cases.add(&c);
    }
    for i in 0..12000 {
        let mut c = Case::new(&seeds[i % seeds.len()]);
        c.font = i % cases.faces.len();
        c.position = [0, 2, 6, 7][i % 4];
        c.state.traversal = ManagedContextState {
            manual_joiners: i & 1 != 0,
            restrict_to_syllable: i & 2 != 0,
            lookup_syllable: (i % 3) as u8,
        };
        c.state.context_match_end = [-100, 0, 3, i32::MAX][i % 4];
        c.state.random_state = random;
        for (j, g) in c.glyphs.iter_mut().enumerate() {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let id = g.glyph_index;
            let mut raw = [0; 31];
            for value in &mut raw {
                random = random.wrapping_mul(1664525).wrapping_add(1013904223);
                *value = random;
            }
            *g = records::from_words(&raw);
            g.glyph_index = id;
            g.cluster = (random % 7) as i32 - 3;
            g.flags = random;
            g.code_point = [
                0x301, 0x34f, 0x200c, 0x200d, 0xad, 0x180b, 0xe0000, 0x110000,
            ][(i + j) % 8];
            g.use_syllable = ((i + j) % 3) as u8;
            g.substituted = if i % 7 == 0 { 255 } else { 0 };
            g.script_shaper = if i % 5 == 0 { 4 } else { 0 };
            g.script_feature_mask = (random >> 24) as u8;
            g.ligated = (random >> 8) as u8;
            g.multiplied = (random >> 16) as u8;
            g.ligature_component = if j % 3 == 1 { 0 } else { 255 };
        }
        cases.add(&c);
    }
    let reverse = fixture::table(&[(8, 0, vec![fixture::reverse()])]);
    let offset = LayoutTable::parse(&reverse)
        .unwrap()
        .lookup(0)
        .unwrap()
        .subtable_offset(0)
        .unwrap();
    for length in 0..=reverse.len() {
        for flags in [0, 8, 16, 0x200, 0x210] {
            let mut c = Case::new(&reverse[..length]);
            c.reverse = Some(offset);
            c.flags = flags;
            c.font = 1;
            cases.add(&c);
        }
    }
    eprintln!(
        "Separate Rust work-bound rejections: {} (not source parity cases)",
        cases.bounded_out.len()
    );
    assert_eq!(
        cases.bounded_out.len(),
        2,
        "Every work-bound rejection needs explicit review"
    );
    for (index, case) in cases.bounded_out.iter().enumerate() {
        let path = dir.0.join(format!("bounded-{index}.bin"));
        let mut input = cases.input[..cases.font_prefix].to_vec();
        input.extend(case);
        std::fs::write(&path, input).unwrap();
        let mut child = ChildGuard(
            Command::new(&dotnet)
                .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
                .env("PROGPU_RUST_GSUB_BOUND_PROBE", "1")
                .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
                .stdout(Stdio::null())
                .stderr(Stdio::piped())
                .spawn()
                .unwrap(),
        );
        use std::io::BufRead;
        let mut ready = std::io::BufReader::new(child.0.stderr.take().unwrap());
        let mut line = String::new();
        ready.read_line(&mut line).unwrap();
        line.clear();
        ready.read_line(&mut line).unwrap();
        assert_eq!(line.trim(), "Entering original bounded-work witness");
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(3);
        let mut completed = false;
        while std::time::Instant::now() < deadline {
            if child.0.try_wait().unwrap().is_some() {
                completed = true;
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        if !completed {
            child.0.kill().unwrap();
            child.0.wait().unwrap();
        }
        assert!(
            !completed,
            "Original bounded-work witness changed; review the safety difference"
        );
        eprintln!(
            "Original branching witness {index}: still executing after 3 seconds; terminated owned control; Rust returned Capacity"
        );
    }
    let input = dir.0.join("input.bin");
    std::fs::write(&input, &cases.input).unwrap();
    let result = Command::new(dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .stdin(Stdio::from(std::fs::File::open(input).unwrap()))
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
        .zip(&cases.expected)
        .position(|(a, b)| a != b)
    {
        let case = cases.ends.partition_point(|&e| e <= at);
        let begin = if case == 0 { 0 } else { cases.ends[case - 1] };
        panic!(
            "current managed GSUB case {case} relative byte {}: C#={} Rust={} first {:?} expected {:?}",
            at - begin,
            result.stdout[at],
            cases.expected[at],
            &result.stdout[begin..(begin + 28).min(result.stdout.len())],
            &cases.expected[begin..(begin + 28).min(cases.expected.len())]
        );
    }
    assert_eq!(result.stdout.len(), cases.expected.len());
    eprintln!(
        "current managed GSUB: {} records / {} exact full record and state bytes",
        cases.ends.len(),
        cases.expected.len()
    );
}
