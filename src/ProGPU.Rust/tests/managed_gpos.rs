//! Actual original managed GPOS and complete attachment-state comparisons.
use progpu::{
    font::{FontError, SfntFace, layout::*},
    text::ManagedShapingGlyph,
};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[path = "support/gpos_fixture.rs"]
mod fixture;
#[path = "support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
#[path = "support/managed_glyph_fixture.rs"]
mod records;
use fixture::*;
fn font_bytes(gdef: &[u8]) -> Vec<u8> {
    let mut tables = font::tables(vec![]);
    if !gdef.is_empty() {
        tables.push((*b"GDEF", gdef.into()));
    }
    font::sfnt(&tables, 0)
}
fn face() -> SfntFace {
    SfntFace::new(font_bytes(&[]), 0).unwrap()
}
fn glyph(id: u16, i: usize) -> ManagedShapingGlyph {
    let mut g = ManagedShapingGlyph::new(
        id,
        i as i32 - 4,
        if id == 12 || id == 13 { 0x301 } else { 65 },
    );
    g.advance_x = 101;
    g.advance_y = -202;
    g.offset_x = -303;
    g.offset_y = 404;
    g.grapheme_cluster = -700;
    g.character_cluster = 900;
    g
}
#[test]
fn clamped_values_and_unchanged_metadata_keep_actual_owner() {
    let data = table(&[(1, 0, vec![single(1, 15)])]);
    let mut tables = font::tables(vec![]);
    tables.push((*b"GPOS", data));
    let source = SfntFace::new(font::sfnt(&tables, 0), 0).unwrap();
    let plan = ManagedGposTable::from_face(source.clone())
        .unwrap()
        .unwrap();
    let mut g = glyph(10, 0);
    g.offset_x = i16::MIN;
    g.advance_y = i16::MIN;
    g.script_shaper = 9;
    g.ligature_component = 17;
    let mut buffer =
        ManagedPositionBuffer::new(source, vec![g], ManagedPositionDirection::LeftToRight).unwrap();
    let retained = plan.clone();
    drop(plan);
    assert!(retained.apply_lookup_at(&mut buffer, 0, 0).unwrap());
    let mut expected = g;
    expected.offset_x = i16::MIN;
    expected.offset_y = 221;
    expected.advance_x = -65;
    expected.advance_y = i16::MIN;
    expected.positioned = 1;
    assert_eq!(buffer.glyphs(), &[expected]);
    assert_eq!(buffer.mark_filtering_coverage(), -1);
    assert!(!retained.apply_lookup_at(&mut buffer, 0, 99).unwrap());
}
#[test]
fn contextual_work_bound_restores_mark_state_and_keeps_dependencies() {
    let cyclic =
        ManagedGposTable::new(table(&[(7, 16, vec![context(false, 3, &[(0, 0)])])])).unwrap();
    let mut buffer = ManagedPositionBuffer::new(
        face(),
        vec![glyph(10, 0), glyph(20, 1)],
        ManagedPositionDirection::LeftToRight,
    )
    .unwrap();
    assert_eq!(
        cyclic.apply_lookup_at_with_limits(
            &mut buffer,
            0,
            0,
            ManagedGposLimits {
                max_lookup_calls: 10_000
            }
        ),
        Err(FontError::Capacity)
    );
    assert_eq!(buffer.mark_filtering_coverage(), -1);
    assert_eq!(buffer.glyphs()[1].flags & 3, 3);
}
#[test]
fn attachment_paths_handle_long_chains_and_original_cycles_without_recursion() {
    let mut records = vec![glyph(10, 0); 100_000];
    for (i, g) in records.iter_mut().enumerate() {
        g.attachment_kind = 2;
        g.attachment_target = i as i32 + 1;
        g.offset_y = 1;
    }
    let mut buffer =
        ManagedPositionBuffer::new(face(), records, ManagedPositionDirection::LeftToRight).unwrap();
    buffer.resolve_attachments().unwrap();
    assert_eq!(buffer.glyphs()[0].offset_y, i16::MAX);
    assert_eq!(buffer.glyphs().last().unwrap().offset_y, 1);
    let mut a = glyph(10, 0);
    let mut b = glyph(20, 1);
    a.attachment_kind = 2;
    a.attachment_target = 1;
    a.offset_y = 10;
    b.attachment_kind = 2;
    b.attachment_target = 0;
    b.offset_y = 20;
    let mut buffer =
        ManagedPositionBuffer::new(face(), vec![a, b], ManagedPositionDirection::LeftToRight)
            .unwrap();
    buffer.resolve_attachments().unwrap();
    assert_eq!(buffer.glyphs()[0].offset_y, 30);
    assert_eq!(buffer.glyphs()[1].offset_y, 20);
}
#[derive(Clone)]
struct Case {
    data: Vec<u8>,
    font: usize,
    index: u16,
    position: usize,
    direction: u8,
    mode: u32,
    glyphs: Vec<ManagedShapingGlyph>,
}
impl Case {
    fn new(data: &[u8]) -> Self {
        Self {
            data: data.into(),
            font: 0,
            index: 0,
            position: 2,
            direction: 1,
            mode: 0,
            glyphs: [8, 9, 10, 12, 20, 30, 11, 12, 13]
                .into_iter()
                .enumerate()
                .map(|(i, g)| glyph(g, i))
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
    input_ends: Vec<usize>,
    prefix: usize,
    faces: Vec<SfntFace>,
    descriptions: Vec<String>,
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
            prefix: input.len(),
            input_ends: vec![],
            input,
            expected: vec![],
            ends: vec![],
            faces: fonts
                .into_iter()
                .map(|f| SfntFace::new(f, 0).unwrap())
                .collect(),
            descriptions: vec![],
        }
    }
    fn add(&mut self, c: &Case) {
        for v in [
            c.data.len() as u32,
            c.font as u32,
            u32::from(c.index),
            c.position as u32,
            u32::from(c.direction),
            c.mode,
            c.glyphs.len() as u32,
        ] {
            emit(&mut self.input, v);
        }
        self.input.extend(&c.data);
        for &g in &c.glyphs {
            for v in records::words(g) {
                emit(&mut self.input, v);
            }
        }
        let mut buffer = ManagedPositionBuffer::new(
            self.faces[c.font].clone(),
            c.glyphs.clone(),
            c.direction.try_into().unwrap(),
        )
        .unwrap();
        let table = ManagedGposTable::new(c.data.clone()).unwrap();
        let mut changed = false;
        let result = (|| {
            if c.mode < 2 {
                changed = table.apply_lookup_at(&mut buffer, c.index, c.position)?;
            }
            if c.mode == 1 || c.mode == 2 {
                buffer.resolve_attachments()?;
            }
            if c.mode >= 3 {
                buffer.zero_mark_advances(c.mode == 4)?;
            }
            Ok::<_, FontError>(())
        })();
        assert_ne!(
            result,
            Err(FontError::Capacity),
            "unexpected unbounded candidate {}",
            self.ends.len()
        );
        for v in [
            result.err().map_or(0, |e| e as u32),
            u32::from(changed),
            buffer.mark_filtering_coverage() as u32,
        ] {
            emit(&mut self.expected, v);
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
            for v in records::words(g) {
                emit(&mut self.expected, v);
            }
        }
        self.ends.push(self.expected.len());
        self.input_ends.push(self.input.len());
        self.descriptions.push(format!(
            "font={} lookup={} position={} direction={} mode={} data={:02x?}",
            c.font, c.index, c.position, c.direction, c.mode, c.data
        ));
    }
}
struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires .NET 10 and the actual original managed source"]
fn original_managed_raw_gpos_and_attachment_state() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-managed-gpos-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_gpos.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let mut gd = gdef();
    put(&mut gd, 26, 3);
    let mut classes = gdef();
    put(&mut classes, 24, 259);
    put(&mut classes, 26, 256);
    let mut cases = Cases::new(vec![font_bytes(&[]), font_bytes(&gd), font_bytes(&classes)]);
    let seeds = seeds();
    for (s, data) in seeds.iter().enumerate() {
        for font in 0..3 {
            for position in [0, 2, 3, 4, 7, 8, 99] {
                let mut c = Case::new(data);
                c.font = font;
                c.position = position;
                c.direction = (s % 5) as u8;
                c.mode = (s % 2) as u32;
                cases.add(&c);
            }
        }
    }
    for kind in 4..=6 {
        for direction in 0..=4 {
            for component in [0, 1, 2, 254, 255] {
                for old in [0, 1, 2, 3, 255] {
                    for target in [-1, 0, 1, 2, 100] {
                        let mut c = Case::new(&table(&[(kind, 0, vec![mark(kind)])]));
                        c.font = 1;
                        c.position = 1;
                        c.direction = direction;
                        c.mode = 1;
                        c.glyphs = vec![
                            glyph(
                                if kind == 4 {
                                    10
                                } else if kind == 5 {
                                    11
                                } else {
                                    12
                                },
                                0,
                            ),
                            glyph(13, 1),
                        ];
                        c.glyphs[0].ligature_component_count = 3;
                        c.glyphs[1].ligature_component = component;
                        c.glyphs[1].attachment_kind = old;
                        c.glyphs[1].attachment_target = target;
                        cases.add(&c);
                    }
                }
            }
        }
    }
    for chain in [false, true] {
        for format in [1, 2, 3] {
            for rec in [
                vec![],
                vec![(0, 1)],
                vec![(0, 1), (1, 1)],
                vec![(2, 1)],
                vec![(0, 65535)],
                vec![(0, 1), (1, 2)],
            ] {
                for flags in [0, 8, 16, 0x210] {
                    for direction in 0..=4 {
                        let mut c = Case::new(&table(&[
                            (
                                if chain { 8 } else { 7 },
                                flags,
                                vec![context(chain, format, &rec)],
                            ),
                            (1, 0, vec![single(1, 15)]),
                            (2, 0, vec![pair(1, 15, 15)]),
                        ]));
                        c.font = 1;
                        c.direction = direction;
                        cases.add(&c);
                    }
                }
            }
        }
    }
    // No accidental cyclic graphs: mutate complete leaf-only tables and preserve
    // their lookup type (including the one-level extension's original leaf type).
    let mut rng = 0x931acdefu32;
    let mut next = || {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        rng
    };
    for n in 0..30_000 {
        let seed = &seeds[next() as usize % seeds.len()];
        let mut c = Case::new(seed);
        c.font = n % 3;
        c.direction = (n % 5) as u8;
        c.mode = (n % 2) as u32;
        c.position = [0, 2, 3, 4, 7, 8][n % 6];
        let lookup = 14 + u16::from_be_bytes([c.data[16], c.data[17]]) as usize;
        for _ in 0..1 + next() % 4 {
            let at = next() as usize % c.data.len();
            if at >= lookup + 6 {
                c.data[at] ^= next() as u8;
            }
        }
        // Force original immutable lookup inventory, offsets and leaf kind after mutation.
        c.data[..lookup + 8].copy_from_slice(&seed[..lookup + 8]);
        if u16::from_be_bytes([seed[lookup], seed[lookup + 1]]) == 9 {
            let sub = lookup + u16::from_be_bytes([seed[lookup + 6], seed[lookup + 7]]) as usize;
            c.data[sub..sub + 4].copy_from_slice(&seed[sub..sub + 4]);
        }
        if n % 3 == 0 {
            c.data.truncate(next() as usize % (c.data.len() + 1));
        }
        if n % 11 == 0 {
            for g in &mut c.glyphs {
                let mut w = records::words(*g);
                for (i, v) in w.iter_mut().enumerate() {
                    if i != 0 && i != 4 {
                        *v = next();
                    }
                }
                *g = records::from_words(&w);
            }
        }
        if n % 17 == 0 {
            c.glyphs[3].code_point = [0x34f, 0x200c, 0x200d, 0xad, 0x110000, 0xd800][n % 6];
            c.glyphs[3].substituted = 1;
        }
        cases.add(&c);
    }
    // Cursive minor-axis reversal keeps original clearing/unwind and cyclic
    // topology, independent of the run direction and lookup RTL flag.
    for n in 0..5_000 {
        let mut c = Case::new(&table(&[(3, (n % 2) as u16, vec![cursive()])]));
        c.direction = (n % 5) as u8;
        c.mode = (n % 2) as u32;
        c.position = 1 + n % 8;
        for (i, g) in c.glyphs.iter_mut().enumerate() {
            g.glyph_index = [10, 20, 30][i % 3];
            g.code_point = 65;
            g.attachment_kind = (next() % 5) as u8;
            g.attachment_target = (next() % 12) as i32 - 2;
            g.offset_x = next() as i16;
            g.offset_y = next() as i16;
            g.advance_x = next() as i16;
            g.advance_y = next() as i16;
        }
        cases.add(&c);
    }
    // Exercise arbitrary attachment topology and per-add saturation independently.
    for n in 0..10_000 {
        let mut c = Case::new(&[]);
        c.direction = (n % 5) as u8;
        c.mode = 2 + (n % 3) as u32;
        c.font = n % 3;
        for g in &mut c.glyphs {
            g.attachment_kind = (next() % 6) as u8;
            g.attachment_target = (next() % 12) as i32 - 2;
            g.offset_x = next() as i16;
            g.offset_y = next() as i16;
            g.advance_x = next() as i16;
            g.advance_y = next() as i16;
        }
        cases.add(&c);
    }
    // Finite source depth exceeds GSUB's unrelated 64-call rule.
    for depth in [1, 63, 64, 65, 128, 257] {
        let mut lookups = Vec::new();
        for i in 0..depth {
            lookups.push((7, 0, vec![context(false, 3, &[(0, i + 1)])]));
        }
        lookups.push((1, 0, vec![single(1, 15)]));
        let mut c = Case::new(&table(&lookups));
        c.glyphs = vec![glyph(10, 0), glyph(20, 1)];
        c.position = 0;
        cases.add(&c);
    }
    let browser_start = cases.ends.len();
    for kind in 1..=8 {
        for direction in 0..=4 {
            for wrapped in [false, true] {
                let sub = match kind {
                    1 => single(2, 65535),
                    2 => pair(2, 15, 15),
                    3 => cursive(),
                    4..=6 => mark(kind),
                    _ => context(kind == 8, 3, &[(0, 1), (1, 1)]),
                };
                let raw = table(&[
                    (
                        if wrapped { 9 } else { kind },
                        0,
                        vec![if wrapped { extension(kind, &sub) } else { sub }],
                    ),
                    (1, 0, vec![single(1, 15)]),
                ]);
                let mut c = Case::new(&raw);
                c.font = 1;
                c.direction = direction;
                c.mode = u32::from(wrapped);
                if kind <= 6 {
                    c.glyphs = vec![
                        glyph(
                            match kind {
                                5 => 11,
                                6 => 12,
                                _ => 10,
                            },
                            0,
                        ),
                        glyph(if kind >= 4 { 13 } else { 20 }, 1),
                    ];
                    c.position = if kind >= 3 { 1 } else { 0 };
                    c.glyphs[0].ligature_component_count = 3;
                }
                cases.add(&c);
            }
        }
    }
    std::fs::write(dir.0.join("input.bin"), &cases.input).unwrap();
    let output = Command::new(&dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .stdin(Stdio::from(
            std::fs::File::open(dir.0.join("input.bin")).unwrap(),
        ))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    if output.stdout != cases.expected {
        let offset = output
            .stdout
            .iter()
            .zip(&cases.expected)
            .position(|(a, b)| a != b)
            .unwrap_or(output.stdout.len().min(cases.expected.len()));
        let i = cases.ends.partition_point(|&end| end <= offset);
        let begin = if i == 0 { 0 } else { cases.ends[i - 1] };
        let word = (offset - begin) / 4;
        panic!(
            "source mismatch case {i} word {word} byte {offset}; {}\nactual={:02x?} expected={:02x?}",
            cases.descriptions.get(i).unwrap(),
            &output.stdout[offset.saturating_sub(8)..(offset + 16).min(output.stdout.len())],
            &cases.expected[offset.saturating_sub(8)..(offset + 16).min(cases.expected.len())]
        );
    }
    let mut fixture = Vec::from(*b"PGP1");
    fixture.extend_from_slice(&cases.input[..cases.prefix]);
    emit(&mut fixture, (cases.ends.len() - browser_start) as u32);
    for i in browser_start..cases.ends.len() {
        let start = if i == 0 {
            cases.prefix
        } else {
            cases.input_ends[i - 1]
        };
        let bytes = &cases.input[start..cases.input_ends[i]];
        emit(&mut fixture, bytes.len() as u32);
        fixture.extend_from_slice(bytes);
        let start = if i == 0 { 0 } else { cases.ends[i - 1] };
        let bytes = &output.stdout[start..cases.ends[i]];
        emit(&mut fixture, bytes.len() as u32);
        fixture.extend_from_slice(bytes);
    }
    let fixture_path = root.join("src/ProGPU.Rust/browser/managed-gpos.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_GPOS_FIXTURE").is_some() {
        std::fs::write(&fixture_path, &fixture).unwrap();
    }
    assert_eq!(
        std::fs::read(&fixture_path).unwrap(),
        fixture,
        "browser fixture must match actual original C# output"
    );
    eprintln!(
        "Original managed GPOS: {} cases, {} exact full-record/state bytes; all nine types and 257 nested lookups; no bounded-work exclusions",
        cases.ends.len(),
        cases.expected.len()
    );
}
