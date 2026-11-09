//! Original managed context preparation and ordered glyph traversal controls.
use progpu::font::{SfntFace, Tag, layout::*};
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
fn glyph(index: u16, cp: u32) -> ManagedContextGlyph {
    ManagedContextGlyph {
        glyph_index: index,
        code_point: cp,
        ..Default::default()
    }
}
fn context(chain: bool, flags: u16, counts: [u16; 3], coverages: &[Vec<u8>]) -> Vec<u8> {
    fixture::table(
        if chain { 6 } else { 5 },
        flags,
        &[fixture::context(chain, counts, coverages)],
    )
}
fn input_table(flags: u16) -> Vec<u8> {
    context(
        false,
        flags,
        [0, 2, 0],
        &[fixture::coverage1(&[10]), fixture::coverage1(&[20])],
    )
}
#[test]
fn original_ordered_skip_rules_first_position_and_flags() {
    let prepared = PreparedManagedContext::new(input_table(0), 0, 7)
        .unwrap()
        .unwrap();
    let view = prepared.view();
    let mut records = [glyph(10, 65), glyph(99, 0x200d), glyph(20, 66)];
    let mut state = ManagedContextState::default();
    assert!(view.may_match(ManagedContextInput::new(&records, state)));
    state.manual_joiners = true;
    assert!(!view.may_match(ManagedContextInput::new(&records, state)));
    records[1].code_point = 0x200c;
    state.manual_joiners = false;
    assert!(!view.may_match(ManagedContextInput::new(&records, state)));
    records[1].code_point = 0x34f;
    assert!(view.may_match(ManagedContextInput::new(&records, state)));
    records[1].substituted = 1;
    assert!(!view.may_match(ManagedContextInput::new(&records, state)));
    records[1].substituted = 0;
    records[1].script_shaper = 4;
    records[1].script_feature_mask = 128;
    assert!(!view.may_match(ManagedContextInput::new(&records, state)));
    records[1].script_feature_mask = 0;
    state.restrict_to_syllable = true;
    records[1].use_syllable = 1;
    assert!(!view.may_match(ManagedContextInput::new(&records, state)));
    // The first position uses the original indexer, whose out-of-syllable
    // result is 0xffff; it does not run the traversal skip loop.
    records[1].use_syllable = 0;
    records[0].use_syllable = 255;
    records[0].code_point = 0xad;
    assert!(!view.may_match(ManagedContextInput::new(&records, state)));
    let reversed = [glyph(20, 65), glyph(10, 66)];
    let sentinel = PreparedManagedContext::new(
        context(false, 0, [0, 1, 0], &[fixture::coverage1(&[65535])]),
        0,
        7,
    )
    .unwrap()
    .unwrap();
    let mut overapproximation = ManagedGlyphSetDigest::default();
    overapproximation.add_range(0, 65535);
    assert!(sentinel.view().may_match(ManagedContextInput::with_digest(
        &records[..1],
        state,
        overapproximation
    )));
    assert!(!view.may_match(ManagedContextInput::new(&reversed, Default::default())));
    let flagged = PreparedManagedContext::new(input_table(16), 0, 7)
        .unwrap()
        .unwrap();
    assert!(
        flagged
            .view()
            .may_match(ManagedContextInput::new(&reversed, state))
    );
    assert!(
        !flagged
            .view()
            .may_match(ManagedContextInput::new(&reversed[..1], state))
    );
    let chain = context(
        true,
        0,
        [1, 1, 1],
        &[
            fixture::coverage1(&[10]),
            fixture::coverage1(&[20]),
            fixture::coverage1(&[30]),
        ],
    );
    let prepared = PreparedManagedContext::new(chain, 0, 7).unwrap().unwrap();
    let records = [
        glyph(10, 65),
        glyph(99, 0x200c),
        glyph(20, 66),
        glyph(99, 0x200d),
        glyph(30, 67),
    ];
    assert!(
        prepared
            .view()
            .may_match(ManagedContextInput::new(&records, Default::default()))
    );
    assert!(!prepared.view().may_match(ManagedContextInput::new(
        &records,
        ManagedContextState {
            manual_joiners: true,
            ..Default::default()
        }
    )));
}
#[test]
fn weak_coverage_admission_and_owned_selection_context() {
    let cov = fixture::coverage1(&[10, 3, 8, 1]);
    let data = context(false, 0, [0, 1, 0], &[cov]);
    let native = OwnedLayoutTable::new(data.clone()).unwrap();
    assert!(PreparedContext::new(native, 0, 7).unwrap().is_none());
    let prepared = PreparedManagedContext::new(data, 0, 7).unwrap().unwrap();
    for g in [10, 3, 8, 1] {
        assert!(prepared.view().coverage(0).unwrap().contains(g));
    }
    let cov = fixture::coverage1(&[10, 3, 8, 1, 4]);
    let mut data = context(false, 0, [0, 1, 0], &[cov]);
    let base = data.len();
    fixture::put(&mut data, 6, base as u16);
    data.extend(fixture::words(&[1, 0x7261, 0x6e64, 8, 0, 1, 0]));
    let mut tables = font::tables(vec![]);
    tables.push((*b"GSUB", data));
    let source: Arc<[u8]> = font::sfnt(&tables, 0).into();
    let face = SfntFace::new(source.clone(), 0).unwrap();
    let prepared = PreparedManagedContext::from_face(face.clone(), Tag::new(*b"GSUB"), 0, 7)
        .unwrap()
        .unwrap();
    let settings = [ManagedFeatureSetting::new([114, 97, 110, 100], 1).unwrap()];
    let selection = ManagedLookupSelection::from_face(
        face,
        Tag::new(*b"GSUB"),
        ManagedSelectionRequest {
            settings: &settings,
            ..Default::default()
        },
    )
    .unwrap()
    .unwrap();
    let retained = selection.clone();
    drop(selection);
    drop(source);
    assert!(prepared.source().is_some());
    assert_eq!(prepared.bytes().as_ptr(), retained.bytes().as_ptr());
    assert_eq!(retained.lookups()[0].index, 0);
    let view = retained.context(0).unwrap();
    assert_eq!(view.subtables(), prepared.view().subtables());
    assert!(!view.coverage(0).unwrap().contains(10));
    assert!(view.coverage(0).unwrap().contains(8));
    assert!(view.coverage(1).is_none());
    assert!(retained.context(1).is_none());
}
#[derive(Clone)]
struct Sample {
    glyphs: Vec<ManagedContextGlyph>,
    state: ManagedContextState,
    all_digest: bool,
}
impl Sample {
    fn input(&self) -> ManagedContextInput<'_> {
        let mut i = ManagedContextInput::new(&self.glyphs, self.state);
        if self.all_digest {
            let mut d = i.digest();
            d.add_range(0, 65535);
            i = ManagedContextInput::with_digest(&self.glyphs, self.state, d);
        }
        i
    }
}
fn samples() -> Vec<Sample> {
    let mut all = vec![];
    for state in 0..4 {
        for active in [0, 1, 255] {
            for all_digest in [false, true] {
                for ids in [
                    vec![],
                    vec![10],
                    vec![20, 10],
                    vec![10, 20],
                    vec![10, 99, 20],
                    vec![10, 20, 30],
                    vec![30, 20, 10],
                    vec![10, 64, 65535, 0],
                ] {
                    let mut glyphs: Vec<_> = ids.into_iter().map(|g| glyph(g, 65)).collect();
                    for (i, g) in glyphs.iter_mut().enumerate() {
                        g.use_syllable = (i % 2) as u8;
                    }
                    all.push(Sample {
                        glyphs,
                        state: ManagedContextState {
                            manual_joiners: state & 1 != 0,
                            restrict_to_syllable: state & 2 != 0,
                            lookup_syllable: active,
                        },
                        all_digest,
                    });
                }
            }
        }
    }
    all
}
fn seeds() -> Vec<(u16, Vec<u8>)> {
    let mut seeds = fixture::seeds();
    let covers = [
        fixture::coverage1(&[10, 3, 8, 1]),
        fixture::coverage1(&[10, 3, 8, 1, 4]),
        fixture::ranges(2, &[[10, 20, 99], [1, 5, 0], [15, 30, 5]]),
        fixture::ranges(
            2,
            &[
                [10, 20, 0],
                [1, 5, 0],
                [15, 30, 0],
                [0, 0, 0],
                [64, 65535, 0],
            ],
        ),
        fixture::ranges(2, &[[20, 10, 0]]),
    ];
    for c in covers {
        for chain in [false, true] {
            let counts = if chain { [1, 1, 1] } else { [0, 3, 0] };
            for flags in [0, 1, 16, 0x20, 0xe1, 0x100, 0xffff] {
                let c = fixture::context(chain, counts, &[c.clone(), c.clone(), c.clone()]);
                seeds.push((
                    7,
                    fixture::table(if chain { 6 } else { 5 }, flags, &[c.clone(), c]),
                ));
            }
        }
    }
    // No lookup-record payload exists. Managed preparation validates only the
    // coverage arrays, retaining its own original weaker optional admission.
    for chain in [false, true] {
        let sub = if chain {
            fixture::words(&[3, 0, 1, 10, 0, 1, 0])
        } else {
            fixture::words(&[3, 1, 65535, 8, 1, 1, 10])
        };
        seeds.push((7, fixture::table(if chain { 6 } else { 5 }, 0, &[sub])));
    }
    for offset in [0u32, 1, 0x7fffffff, 0x80000000, 0xffffffff] {
        let mut sub = fixture::extension(
            5,
            &fixture::context(false, [0, 1, 0], &[fixture::coverage1(&[10])]),
        );
        sub[4..8].copy_from_slice(&offset.to_be_bytes());
        seeds.push((7, fixture::table(7, 0, &[sub])));
    }
    seeds
}
fn emit(b: &mut Vec<u8>, n: u32) {
    b.extend(n.to_le_bytes());
}
#[derive(Default)]
struct Cases {
    input: Vec<u8>,
    expected: Vec<u8>,
    ends: Vec<usize>,
    matches: usize,
    queries: usize,
    starts: Vec<usize>,
    visited: usize,
    only: Option<usize>,
}
impl Cases {
    fn add(
        &mut self,
        data: &[u8],
        index: u16,
        extension: u16,
        queries: &[u16],
        samples: &[Sample],
    ) {
        let case = self.visited;
        self.visited += 1;
        if self.only.is_some_and(|wanted| wanted != case) {
            return;
        }
        self.starts.push(self.input.len());
        for n in [
            data.len() as u32,
            u32::from(index),
            u32::from(extension),
            queries.len() as u32,
            samples.len() as u32,
        ] {
            emit(&mut self.input, n);
        }
        self.input.extend(data);
        for &q in queries {
            emit(&mut self.input, u32::from(q));
        }
        let prepared = PreparedManagedContext::new(data, index, extension).unwrap();
        emit(&mut self.expected, u32::from(prepared.is_some()));
        if let Some(p) = &prepared {
            let v = p.view();
            for n in [
                u32::from(v.flags()),
                v.subtables().len() as u32,
                v.coverage_count() as u32,
            ] {
                emit(&mut self.expected, n);
            }
            for sub in v.subtables() {
                for n in [
                    sub.coverage_offset,
                    sub.coverage_count,
                    u32::from(sub.backtrack_count),
                    u32::from(sub.input_count),
                ] {
                    emit(&mut self.expected, n);
                }
            }
            for i in 0..v.coverage_count() {
                let c = v.coverage(i).unwrap();
                for m in c.digest().masks() {
                    self.expected.extend(m.to_le_bytes());
                }
                for &q in queries {
                    emit(&mut self.expected, u32::from(c.contains(q)));
                    self.queries += 1;
                }
            }
        }
        for s in samples {
            for n in [
                u32::from(s.state.manual_joiners) | u32::from(s.state.restrict_to_syllable) << 1,
                u32::from(s.state.lookup_syllable),
                s.glyphs.len() as u32,
                u32::from(s.all_digest),
            ] {
                emit(&mut self.input, n);
            }
            for g in &s.glyphs {
                for n in [
                    u32::from(g.glyph_index),
                    g.code_point,
                    u32::from(g.substituted),
                    u32::from(g.script_shaper),
                    u32::from(g.script_feature_mask),
                    u32::from(g.use_syllable),
                ] {
                    emit(&mut self.input, n);
                }
            }
            if let Some(p) = &prepared {
                emit(&mut self.expected, u32::from(p.view().may_match(s.input())));
                self.matches += 1;
            }
        }
        self.ends.push(self.expected.len());
    }
}
struct Cleanup(PathBuf);
impl Drop for Cleanup {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires actual original managed context/glyph buffer and .NET 10; CI runs explicitly"]
fn original_managed_context_and_ordered_buffer_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = Cleanup(
        std::env::temp_dir().join(format!("progpu-managed-context-{}", std::process::id())),
    );
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_context.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let mut cases = Cases {
        only: std::env::var("PROGPU_RUST_CONTEXT_CASE")
            .ok()
            .map(|v| v.parse().unwrap()),
        ..Default::default()
    };
    let font = font::sfnt(&font::tables(vec![]), 0);
    emit(&mut cases.input, font.len() as u32);
    cases.input.extend(font);
    let queries = [
        0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 15, 16, 20, 30, 32, 63, 64, 65, 255, 256, 4096, 65535,
    ];
    let samples = samples();
    let seeds = seeds();
    for &(extension, ref data) in &seeds {
        cases.add(data, 0, extension, &queries, &samples);
        for length in 0..data.len() {
            cases.add(&data[..length], 0, extension, &queries, &samples[..3]);
        }
    }
    let all: Vec<_> = (0..=65535).collect();
    for &(ext, ref data) in seeds.iter().rev().take(77) {
        cases.add(data, 0, ext, &all, &[]);
    }
    let mut random = 0x73456abdu32;
    for i in 0..25000 {
        let (ext, mut data) = seeds[i % seeds.len()].clone();
        for _ in 0..1 + i % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let at = random as usize % data.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            data[at] = (random >> 24) as u8;
        }
        cases.add(
            &data,
            if i % 13 == 0 { 65535 } else { 0 },
            ext,
            &queries,
            &samples[..3],
        );
    }
    // Exhaust every scalar and nearby invalid values through the actual ordered
    // buffer, rather than substituting another Unicode/default-ignorable table.
    let data = input_table(0);
    let mut batch = vec![];
    for cp in 0..=0x110010 {
        batch.push(Sample {
            glyphs: vec![glyph(10, 65), glyph(99, cp), glyph(20, 66)],
            state: Default::default(),
            all_digest: false,
        });
        if batch.len() == 4096 {
            cases.add(&data, 0, 7, &[], &batch);
            batch.clear();
        }
    }
    cases.add(&data, 0, 7, &[], &batch);
    // Source visibility and syllable metadata remain raw byte inventories.
    let mut samples = vec![];
    for cp in [
        0xad, 0x34f, 0x115f, 0x180b, 0x180f, 0x200c, 0x200d, 0xe0001, 0xe0080, 65,
    ] {
        for value in 0..=255 {
            for shaper in [0, 1, 4, 5, 255] {
                for field in 0..3 {
                    let mut g = glyph(99, cp);
                    g.script_shaper = shaper;
                    match field {
                        0 => g.substituted = value,
                        1 => g.script_feature_mask = value,
                        _ => g.use_syllable = value,
                    };
                    samples.push(Sample {
                        glyphs: vec![glyph(10, 65), g, glyph(20, 66)],
                        state: ManagedContextState {
                            manual_joiners: value & 1 != 0,
                            restrict_to_syllable: field == 2,
                            lookup_syllable: 0,
                        },
                        all_digest: value & 2 != 0,
                    });
                }
            }
        }
    }
    cases.add(&data, 0, 7, &[], &samples);
    let input = dir.0.join("input.bin");
    std::fs::write(&input, &cases.input).unwrap();
    let output = Command::new(dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .stdin(std::fs::File::open(input).unwrap())
        .stderr(Stdio::inherit())
        .output()
        .unwrap();
    assert!(output.status.success());
    if let Some(at) = output
        .stdout
        .iter()
        .zip(&cases.expected)
        .position(|(a, b)| a != b)
    {
        let case = cases.ends.partition_point(|&e| e <= at);
        let input = cases.starts[case];
        let start = if case == 0 { 0 } else { cases.ends[case - 1] };
        eprintln!(
            "relative output offset {}, input {:?}, original {:?}, Rust {:?}",
            at - start,
            &cases.input[input..(input + 120).min(cases.input.len())],
            &output.stdout[start..(start + 120).min(output.stdout.len())],
            &cases.expected[start..(start + 120).min(cases.expected.len())]
        );
        panic!(
            "original managed context case {} byte {at}: original={} Rust={}",
            cases.ends.partition_point(|&e| e <= at),
            output.stdout[at],
            cases.expected[at]
        );
    }
    assert_eq!(output.stdout.len(), cases.expected.len());
    eprintln!(
        "original managed context: {} records / {} coverage queries / {} ordered matches / {} exact bytes",
        cases.ends.len(),
        cases.queries,
        cases.matches,
        cases.expected.len()
    );
}
