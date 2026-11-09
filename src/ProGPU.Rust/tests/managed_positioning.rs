//! Whole current managed GPOS dispatch and original lazy variation-store controls.
use progpu::{
    font::{FontError, SfntFace, layout::*},
    text::ManagedShapingGlyph,
};
use std::{
    collections::HashMap,
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[path = "support/gpos_dispatch_fixture.rs"]
mod fixture;
#[path = "support/managed_glyph_fixture.rs"]
mod records;
use fixture::*;
fn feature(tag: &[u8; 4], value: i32) -> ManagedFeatureSetting {
    ManagedFeatureSetting::new(tag.map(u16::from), value).unwrap()
}
fn options(mode: u32) -> ManagedFeatureOptions {
    let f = [
        feature(
            b"kern",
            if mode == 2 {
                0
            } else if mode == 4 {
                -7
            } else {
                1
            },
        ),
        feature(b"dist", if mode == 4 { -4 } else { 1 }),
        feature(b"mark", if mode == 4 { -3 } else { 1 }),
    ];
    if mode == 0 {
        return ManagedFeatureOptions::default();
    }
    if mode == 1 {
        return ManagedFeatureOptions::with_overrides(&f).unwrap();
    }
    let ranges = if mode == 3 || mode == 5 {
        vec![
            ManagedFeatureRange::new(
                *b"kern",
                if mode == 3 { 0 } else { 1 },
                if mode == 3 { 0 } else { 2 },
                if mode == 3 { 3 } else { 5 },
            )
            .unwrap(),
            ManagedFeatureRange::new(*b"mark", 0, 0, 3).unwrap(),
        ]
    } else {
        vec![]
    };
    ManagedFeatureOptions::new(&f, if mode == 5 { Some(&[]) } else { None }, None, &ranges).unwrap()
}
fn glyph(id: u16, i: usize) -> ManagedShapingGlyph {
    let mut g =
        ManagedShapingGlyph::new(id, i as i32, if id == 12 || id == 13 { 0x301 } else { 65 });
    g.advance_x = 101;
    g.advance_y = -202;
    g.offset_x = -303;
    g.offset_y = 404;
    g.ligature_component_count = 3;
    g.grapheme_cluster = -700;
    g.character_cluster = 900;
    g
}
fn source(raw: Vec<u8>, tag: [u8; 4]) -> SfntFace {
    SfntFace::new(
        font(&selected(raw, &[(tag, vec![0])], 65535), &gdef(&store())),
        0,
    )
    .unwrap()
}
#[test]
fn active_single_variations_keep_original_range_and_ignored_glyph_policy() {
    let face = source(table(&[(1, 0, vec![single(1, 0x10, 0, 2)])]), *b"kern");
    let mut g = glyph(10, 0);
    g.code_point = 0x200d;
    let plan = ManagedPositioningPlan::from_face(
        face.clone(),
        options(3),
        &[68, 70, 76, 84],
        None,
        &[8192],
    )
    .unwrap()
    .unwrap();
    let mut buffer =
        ManagedPositionBuffer::new(face, vec![g], ManagedPositionDirection::LeftToRight).unwrap();
    assert!(plan.apply(&mut buffer).unwrap());
    assert_eq!(buffer.glyphs()[0].offset_x, -302);
    assert_eq!(buffer.glyphs()[0].positioned, 1);
    assert_eq!(buffer.glyphs()[0].advance_x, g.advance_x);
}
#[test]
fn zero_value_variations_still_position_and_keep_kerning_plan_identity() {
    let face = source(table(&[(1, 0, vec![single(1, 0, 0, 0)])]), *b"kern");
    let plan =
        ManagedPositioningPlan::from_face(face.clone(), options(3), &[68, 70, 76, 84], None, &[1])
            .unwrap()
            .unwrap();
    let mut buffer = ManagedPositionBuffer::new(
        face,
        vec![glyph(10, 0)],
        ManagedPositionDirection::LeftToRight,
    )
    .unwrap();
    assert!(plan.apply(&mut buffer).unwrap());
    assert_eq!(buffer.glyphs()[0].positioned, 1);
}
#[derive(Clone)]
struct Case {
    font: usize,
    requested: i32,
    direction: u8,
    mode: u32,
    policy: u32,
    glyphs: Vec<ManagedShapingGlyph>,
    queries: Vec<(u16, u16)>,
    browser: bool,
}
impl Case {
    fn new(font: usize) -> Self {
        Self {
            font,
            browser: false,
            requested: 8192,
            direction: 1,
            mode: 0,
            policy: 0,
            glyphs: [8, 9, 10, 12, 20, 30, 11, 12, 13]
                .into_iter()
                .enumerate()
                .map(|(i, g)| glyph(g, i))
                .collect(),
            queries: vec![
                (0, 0),
                (0, 1),
                (0, 2),
                (0, 7),
                (1, 4),
                (3, 0),
                (4, 0),
                (0, 99),
            ],
        }
    }
}
fn emit(b: &mut Vec<u8>, v: u32) {
    b.extend(v.to_le_bytes());
}
struct Corpus {
    fonts: Vec<Vec<u8>>,
    map: HashMap<Vec<u8>, usize>,
    cases: Vec<Case>,
}
impl Corpus {
    fn new() -> Self {
        Self {
            fonts: vec![],
            map: HashMap::new(),
            cases: vec![],
        }
    }
    fn font(&mut self, bytes: Vec<u8>) -> usize {
        if let Some(&i) = self.map.get(&bytes) {
            return i;
        }
        let i = self.fonts.len();
        self.map.insert(bytes.clone(), i);
        self.fonts.push(bytes);
        i
    }
    fn add(&mut self, c: Case) {
        self.cases.push(c);
    }
    fn input(&self) -> Vec<u8> {
        let mut out = vec![];
        emit(&mut out, self.fonts.len() as u32);
        for f in &self.fonts {
            emit(&mut out, f.len() as u32);
            out.extend(f);
        }
        for c in &self.cases {
            for v in [
                c.font as u32,
                c.requested as u32,
                u32::from(c.direction),
                c.mode,
                c.policy,
                c.glyphs.len() as u32,
                c.queries.len() as u32,
            ] {
                emit(&mut out, v);
            }
            for &(a, b) in &c.queries {
                emit(&mut out, u32::from(a));
                emit(&mut out, u32::from(b));
            }
            for &g in &c.glyphs {
                for v in records::words(g) {
                    emit(&mut out, v);
                }
            }
        }
        out
    }
}
struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires .NET 10 and actual original managed positioning/variation source"]
fn original_managed_plan_and_layout_variation_records() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = Temp(
        std::env::temp_dir().join(format!("progpu-managed-positioning-{}", std::process::id())),
    );
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_positioning.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let mut corpus = Corpus::new();
    let gd = gdef(&store());
    let mut seeds = Vec::new();
    for form in [1, 2] {
        for f in [0, 1, 15, 16, 32, 64, 128, 0xf0, 0xff, 0xff00, 65535] {
            for outer in [0, 1, 3] {
                seeds.push((1, single(form, f, outer, 2)));
            }
        }
    }
    for form in [1, 2] {
        for (a, b) in [(0, 0), (15, 0), (0x10, 0x40), (0xff, 0xff), (0xff00, 0xf0)] {
            seeds.push((2, pair(form, a, b, 0, 0)));
        }
    }
    seeds.extend([
        (3, cursive()),
        (4, mark(4, 0, 0)),
        (6, mark(6, 1, 3)),
        (5, gpos::mark(5)),
    ]);
    for (s, (kind, sub)) in seeds.iter().enumerate() {
        for wrapped in [false, true] {
            for flags in [0, 8, 16, 0x210] {
                let raw = table(&[(
                    if wrapped { 9 } else { *kind },
                    flags,
                    vec![if wrapped {
                        extension(*kind, sub)
                    } else {
                        sub.clone()
                    }],
                )]);
                let selected = selected(
                    raw,
                    &[(*b"kern", vec![0])],
                    if s % 7 == 0 { 0 } else { 65535 },
                );
                let f = corpus.font(font(&selected, &gd));
                for policy in 0..6 {
                    let mut c = Case::new(f);
                    c.policy = policy;
                    c.browser = !wrapped && flags == 0 && policy == (s % 6) as u32;
                    c.direction = (s % 5) as u8;
                    c.mode = (s % 2) as u32;
                    c.requested = [0, 1, 4096, 8192, 12288, 16384, -8192][s % 7];
                    if *kind >= 4 && *kind <= 6 {
                        c.glyphs = vec![
                            glyph(
                                if *kind == 4 {
                                    10
                                } else if *kind == 5 {
                                    11
                                } else {
                                    12
                                },
                                0,
                            ),
                            glyph(13, 1),
                        ];
                    }
                    corpus.add(c);
                }
            }
        }
    }
    for chain in [false, true] {
        for format in [1, 2, 3] {
            let raw = table(&[
                (
                    if chain { 8 } else { 7 },
                    8,
                    vec![context(chain, format, &[(0, 1), (1, 1)])],
                ),
                (1, 0, vec![single(1, 0xff, 0, 0)]),
            ]);
            for ids in [vec![0], vec![0, 1], vec![1, 0, 1]] {
                let f = corpus.font(font(&selected(raw.clone(), &[(*b"dist", ids)], 65535), &gd));
                for policy in 0..6 {
                    let mut c = Case::new(f);
                    c.policy = policy;
                    corpus.add(c);
                }
            }
        }
    }
    // Source-specific dispatch differences: a mixed extension lookup sends its
    // entire variation pass through pair handling; raw lookup remains ordered.
    for reverse in [false, true] {
        let mut subs = vec![
            extension(1, &single(1, 0xf0, 0, 2)),
            extension(2, &pair(1, 0x10, 0x40, 1, 0)),
        ];
        if reverse {
            subs.reverse();
        }
        for policy in 0..6 {
            let f = corpus.font(font(
                &selected(
                    table(&[(9, 16, subs.clone())]),
                    &[(*b"kern", vec![0])],
                    65535,
                ),
                &gd,
            ));
            let mut c = Case::new(f);
            c.policy = policy;
            c.browser = true;
            corpus.add(c);
        }
    }
    // Pair variation scans the original unsorted pair inventory linearly while
    // ordinary positioning retains its independent binary search.
    for ids in [[30, 12, 20], [20, 20, 12], [30, 20, 12]] {
        let mut sub = pair(1, 0x11, 0x40, 0, 2);
        for (i, id) in ids.into_iter().enumerate() {
            put(&mut sub, 14 + i * 8, id);
        }
        for policy in 0..6 {
            let f = corpus.font(font(
                &selected(
                    table(&[(2, 0, vec![sub.clone()])]),
                    &[(*b"kern", vec![0])],
                    65535,
                ),
                &gd,
            ));
            let mut c = Case::new(f);
            c.policy = policy;
            c.browser = true;
            corpus.add(c);
        }
    }
    // Variation single/mark passes retain their own original feature, ignored-
    // glyph and component rules after the lookup-level digest gate.
    for kind in [1, 4, 6] {
        for policy in 0..6 {
            let sub = if kind == 1 {
                single(2, 0xf0, 0, 1)
            } else {
                mark(kind, 0, 1)
            };
            let f = corpus.font(font(
                &selected(
                    table(&[(kind, 8, vec![sub])]),
                    &[(*b"kern", vec![0])],
                    65535,
                ),
                &gd,
            ));
            let mut c = Case::new(f);
            c.policy = policy;
            c.browser = true;
            c.glyphs = vec![
                glyph(if kind == 6 { 12 } else { 10 }, 0),
                glyph(13, 1),
                glyph(10, 2),
            ];
            c.glyphs[0].ligature_component = 1;
            c.glyphs[1].ligature_component = 2;
            c.glyphs[1].code_point = 0x200d;
            c.glyphs[2].code_point = 0x200d;
            c.requested = 4096;
            corpus.add(c);
        }
    }
    // Selected rows are read lazily from the complete font owner, including
    // bytes beyond the declared GDEF extent. Advance-Y device deltas are still
    // read for errors even though their numerical result is intentionally unused.
    for declared in [18, 72, gd.len()] {
        for row in [0, 7, 65535] {
            let mut sub = single(1, 0x80, 3, 0);
            let pointer = u16::from_be_bytes([sub[6], sub[7]]) as usize;
            put(&mut sub, pointer + 2, row);
            let mut bytes = font(
                &selected(table(&[(1, 16, vec![sub])]), &[(*b"kern", vec![0])], 65535),
                &gd,
            );
            let count = u16::from_be_bytes([bytes[4], bytes[5]]) as usize;
            for i in 0..count {
                let at = 12 + i * 16;
                if &bytes[at..at + 4] == b"GDEF" {
                    bytes[at + 12..at + 16].copy_from_slice(&(declared as u32).to_be_bytes());
                }
            }
            let f = corpus.font(bytes);
            let mut c = Case::new(f);
            c.browser = true;
            c.queries = (0..4)
                .flat_map(|a| [0, 7, 65535].map(move |b| (a, b)))
                .collect();
            corpus.add(c);
        }
    }
    // A selected missing row fails lazily. The ignored advance-Y result still
    // performs its source read, after the preceding raw positioning has written.
    for format in [0x81, 0xf1] {
        let mut broken = store();
        let last = u32::from_be_bytes(broken[20..24].try_into().unwrap()) as usize;
        put(&mut broken, last, 65535);
        let mut sub = single(1, format, 3, 0);
        for p in (8..8 + ((format as u8 & 0xf0).count_ones() as usize) * 2).step_by(2) {
            let at = u16::from_be_bytes([sub[p], sub[p + 1]]) as usize;
            put(&mut sub, at + 2, 65534);
        }
        let f = corpus.font(font(
            &selected(table(&[(1, 16, vec![sub])]), &[(*b"kern", vec![0])], 65535),
            &gdef(&broken),
        ));
        let mut c = Case::new(f);
        c.browser = true;
        c.glyphs = vec![glyph(10, 0)];
        c.queries = vec![(3, 65534)];
        corpus.add(c);
    }
    // Per-root safety limits must not become an ordinary long-run glyph limit.
    let f = corpus.font(font(
        &selected(
            table(&[(1, 0, vec![single(1, 0xf0, 0, 0)])]),
            &[(*b"kern", vec![0])],
            65535,
        ),
        &gd,
    ));
    let mut c = Case::new(f);
    c.glyphs = (0..131073).map(|i| glyph(10, i)).collect();
    corpus.add(c);
    let mut rng = 0x8383dcedu32;
    let mut next = || {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        rng
    };
    for n in 0..12_000 {
        let (kind, sub) = &seeds[next() as usize % seeds.len()];
        let mut sub = sub.clone();
        for _ in 0..1 + next() % 3 {
            let p = next() as usize % sub.len();
            sub[p] ^= next() as u8;
        }
        if n % 3 == 0 {
            sub.truncate(next() as usize % (sub.len() + 1));
        }
        let raw = selected(
            table(&[(*kind, if n % 2 == 0 { 0 } else { 8 }, vec![sub])]),
            &[(*b"kern", vec![0])],
            65535,
        );
        let f = corpus.font(font(&raw, &gd));
        let mut c = Case::new(f);
        c.requested = (next() as i16) as i32;
        c.policy = (n % 6) as u32;
        c.direction = (n % 5) as u8;
        c.mode = (n % 2) as u32;
        if n % 7 == 0 {
            c.glyphs[2].code_point = 0x200d;
            c.glyphs[2].substituted = 1;
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
        corpus.add(c);
    }
    let data = selected(
        table(&[(1, 0, vec![single(1, 0xf0, 0, 0)])]),
        &[(*b"kern", vec![0])],
        65535,
    );
    for n in 0..4_000 {
        let mut gd = gd.clone();
        for _ in 0..1 + next() % 3 {
            let p = 18 + next() as usize % (gd.len() - 18);
            gd[p] ^= next() as u8;
        }
        if n % 2 == 0 {
            gd.truncate(next() as usize % (gd.len() + 1));
        }
        let f = corpus.font(font(&data, &gd));
        let mut c = Case::new(f);
        c.requested = (next() as i16) as i32;
        c.policy = (n % 6) as u32;
        c.glyphs = vec![glyph(10, 0)];
        c.queries = (0..5).flat_map(|a| (0..10).map(move |b| (a, b))).collect();
        corpus.add(c);
    }
    let f = corpus.font(font(&[], &gd));
    for _ in 0..10_000 {
        let mut c = Case::new(f);
        c.requested = (next() as i16) as i32;
        c.glyphs.clear();
        c.queries = (0..4).flat_map(|a| (0..9).map(move |b| (a, b))).collect();
        corpus.add(c);
    }
    for axes in [2, 3] {
        let f = corpus.font(font_with_axes(&data, &gdef(&multi_store(axes)), axes));
        for n in 0..1000 {
            let mut c = Case::new(f);
            c.requested = (next() as i16) as i32;
            c.queries = (0..4).flat_map(|a| (0..9).map(move |b| (a, b))).collect();
            c.browser = n < 6;
            corpus.add(c);
        }
    }
    let input = corpus.input();
    std::fs::write(dir.0.join("input.bin"), input).unwrap();
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
    let faces: Vec<_> = corpus
        .fonts
        .iter()
        .map(|f| SfntFace::new(f.clone(), 0).unwrap())
        .collect();
    let mut cursor = 0;
    let mut bytes = 0;
    let mut queries = 0;
    let mut captures = 0;
    let mut browser = Vec::from(*b"PMP1");
    emit(
        &mut browser,
        corpus.cases.iter().filter(|c| c.browser).count() as u32,
    );
    for (number, c) in corpus.cases.iter().enumerate() {
        let mut take = || {
            let n = u32::from_le_bytes(output.stdout[cursor..cursor + 4].try_into().unwrap());
            cursor += 4;
            n
        };
        let n = take();
        let coordinates: Vec<_> = (0..n).map(|_| take() as i16).collect();
        let active = take();
        captures += 1;
        let vars =
            ManagedLayoutVariations::from_normalized(faces[c.font].clone(), &coordinates).unwrap();
        assert_eq!(u32::from(vars.is_active()), active, "capture {number}");
        for &(outer, inner) in &c.queries {
            let source_error = take();
            let source_bits = take();
            let result = vars.delta(outer, inner);
            assert_eq!(
                (
                    result.err().map_or(0, |e| e as u32),
                    result.unwrap_or(0.0).to_bits()
                ),
                (source_error, source_bits),
                "delta case {number} outer {outer} inner {inner} coords {coordinates:?}"
            );
            queries += 1;
            bytes += 8;
        }
        let browser_output_start = cursor - 4 - c.queries.len() * 8;
        let source_start = cursor;
        let length = 12 + c.glyphs.len() * 32 * 4;
        cursor += length;
        let actual = &output.stdout[source_start..cursor];
        let mut buffer = ManagedPositionBuffer::new(
            faces[c.font].clone(),
            c.glyphs.clone(),
            c.direction.try_into().unwrap(),
        )
        .unwrap();
        let mut kerning = false;
        let result = (|| {
            if let Some(plan) = ManagedPositioningPlan::from_face(
                faces[c.font].clone(),
                options(c.policy),
                &[68, 70, 76, 84],
                None,
                &coordinates,
            )? {
                kerning = plan.apply(&mut buffer)?;
            }
            if c.mode == 1 {
                buffer.resolve_attachments()?;
            }
            Ok::<_, FontError>(())
        })();
        let mut expected = vec![];
        for v in [
            result.err().map_or(0, |e| e as u32),
            u32::from(kerning),
            buffer.mark_filtering_coverage() as u32,
        ] {
            emit(&mut expected, v);
        }
        for i in 0..buffer.glyphs().len() {
            emit(
                &mut expected,
                buffer
                    .glyph_class(i)
                    .map_or_else(|e| 0x80000000 | e as u32, u32::from),
            );
        }
        for &g in buffer.glyphs() {
            for v in records::words(g) {
                emit(&mut expected, v);
            }
        }
        if actual != expected {
            let p = actual
                .iter()
                .zip(&expected)
                .position(|(a, b)| a != b)
                .unwrap();
            panic!(
                "position case {number} word {} coords {coordinates:?} policy={} mode={}\nactual={:02x?}\nexpected={:02x?}\nfont={:02x?}",
                p / 4,
                c.policy,
                c.mode,
                actual,
                expected,
                corpus.fonts[c.font]
            );
        }
        bytes += length;
        if c.browser {
            emit(&mut browser, corpus.fonts[c.font].len() as u32);
            browser.extend(&corpus.fonts[c.font]);
            emit(&mut browser, coordinates.len() as u32);
            for v in &coordinates {
                emit(&mut browser, *v as i32 as u32);
            }
            let policy = policy_words(c.policy);
            emit(&mut browser, policy.len() as u32);
            for v in policy {
                emit(&mut browser, v);
            }
            emit(&mut browser, c.direction.into());
            emit(&mut browser, c.mode);
            emit(&mut browser, c.queries.len() as u32);
            for &(a, b) in &c.queries {
                emit(&mut browser, a.into());
                emit(&mut browser, b.into());
            }
            emit(&mut browser, c.glyphs.len() as u32);
            for &g in &c.glyphs {
                for v in records::words(g) {
                    emit(&mut browser, v);
                }
            }
            let actual = &output.stdout[browser_output_start..cursor];
            emit(&mut browser, actual.len() as u32);
            browser.extend(actual);
        }
    }
    assert_eq!(cursor, output.stdout.len());
    let path = root.join("src/ProGPU.Rust/browser/managed-positioning.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_POSITIONING_FIXTURE").is_some() {
        std::fs::write(&path, &browser).unwrap();
    }
    assert_eq!(
        std::fs::read(path).unwrap(),
        browser,
        "browser fixture must match actual original C# output"
    );
    eprintln!(
        "Original managed positioning browser: {} cases, {} bytes",
        corpus.cases.iter().filter(|c| c.browser).count(),
        browser.len()
    );
    eprintln!(
        "Original managed positioning: {} cases, {queries} delta queries, {captures} actual normalized captures, {bytes} exact output bytes",
        corpus.cases.len()
    );
}

fn policy_words(mode: u32) -> Vec<u32> {
    if mode == 0 {
        return vec![1, 0, u32::MAX, u32::MAX, 0];
    }
    let ranged = mode == 3 || mode == 5;
    let mut out = vec![
        if mode == 1 { 2 } else { 0 },
        3,
        if mode == 5 { 0 } else { u32::MAX },
        u32::MAX,
        if ranged { 2 } else { 0 },
    ];
    for (tag, value) in [
        (
            b"kern",
            if mode == 2 {
                0
            } else if mode == 4 {
                -7
            } else {
                1
            },
        ),
        (b"dist", if mode == 4 { -4 } else { 1 }),
        (b"mark", if mode == 4 { -3 } else { 1 }),
    ] {
        out.extend(tag.map(u32::from));
        out.push(value as u32);
    }
    if ranged {
        out.extend([
            u32::from_be_bytes(*b"kern"),
            if mode == 3 { 0 } else { 1 },
            if mode == 3 { 0 } else { 2 },
            if mode == 3 { 3 } else { 5 },
            u32::from_be_bytes(*b"mark"),
            0,
            0,
            3,
        ]);
    }
    out
}

#[test]
fn oversized_region_inventory_rejects_without_integer_overflow() {
    let mut data = store();
    put(&mut data, 24, 65535);
    put(&mut data, 26, 65535);
    let face = SfntFace::new(font(&[], &gdef(&data)), 0).unwrap();
    assert!(matches!(
        ManagedLayoutVariations::from_normalized(face, &vec![1; 65535]),
        Err(FontError::InvalidFace | FontError::Capacity)
    ));
}
