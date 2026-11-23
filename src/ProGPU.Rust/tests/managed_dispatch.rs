//! Original C# feature options, stage filters and full raw substitution dispatch.
use progpu::{
    font::{FontError, SfntFace, layout::*},
    text::ManagedShapingGlyph,
};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[path = "support/managed_dispatch_fixture.rs"]
mod fixture;
#[path = "support/managed_glyph_fixture.rs"]
#[allow(dead_code)]
mod records;
fn tag(s: &[u8; 4]) -> [u16; 4] {
    s.map(u16::from)
}
fn setting(s: &[u8; 4], value: i32) -> ManagedFeatureSetting {
    ManagedFeatureSetting::new(tag(s), value).unwrap()
}
fn glyph(id: u16, cluster: i32) -> ManagedShapingGlyph {
    let mut g = ManagedShapingGlyph::new(id, cluster, if id == 12 { 0x301 } else { 65 });
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
fn plan(
    data: Vec<u8>,
    feature: &[u8; 4],
    options: ManagedFeatureOptions,
) -> ManagedSubstitutionPlan {
    ManagedSubstitutionPlan::new(
        fixture::table(data, &[(*feature, vec![0])], u16::MAX),
        options,
        &tag(b"DFLT"),
        None,
        &[],
    )
    .unwrap()
}
#[test]
fn ranges_preserve_baseline_explicitness_and_signed_values() {
    let options = ManagedFeatureOptions::new(
        &[setting(b"liga", 9), setting(b"rand", 65535)],
        Some(&[]),
        Some(&[tag(b"liga")]),
        &[
            ManagedFeatureRange::new(*b"liga", 1, 3, 8).unwrap(),
            ManagedFeatureRange::new(*b"liga", u32::MAX, 5, 7).unwrap(),
        ],
    )
    .unwrap();
    assert_eq!(options.value_at(tag(b"liga"), -7), 0);
    assert_eq!(options.value_at(tag(b"rand"), 1), 65535);
    assert_eq!(options.value_at(tag(b"liga"), 5), i32::MAX);
    assert!(options.explicit_at(tag(b"liga"), 7));
    assert!(!options.explicit_at(tag(b"liga"), 8));
    assert_eq!(
        ManagedFeatureRange::new(*b"liga", 1, 8, 7),
        Err(FontError::InvalidArgument)
    );
    let options = ManagedFeatureOptions::with_overrides(&[setting(b"liga", -9)]).unwrap();
    assert!(options.enabled(glyph(10, 0), tag(b"liga")));
    assert!(options.resolves_explicit(tag(b"liga")));
    assert!(!options.resolves_explicit(tag(b"calt")));
    let custom = ManagedFeatureOptions::new(options.features(), None, None, &[]).unwrap();
    assert!(custom.resolves_explicit(tag(b"calt")));
    assert!(!custom.explicit_at(tag(b"calt"), 0));
}
#[test]
fn original_forward_progress_and_random_failed_output_are_retained() {
    use fixture::gsub::*;
    let p = plan(
        table(&[(2, 0, vec![multiple(&[10, 10, 10])])]),
        b"liga",
        ManagedFeatureOptions::default(),
    );
    let face = SfntFace::new(fixture::font(&[], &[]), 0).unwrap();
    let mut b = ManagedGlyphBuffer::new(face.clone(), vec![glyph(10, 0), glyph(20, 1)]).unwrap();
    p.apply_stage(&mut b, ManagedSubstitutionStage::All)
        .unwrap();
    assert_eq!(b.glyphs().len(), 4);
    assert_eq!(
        b.glyphs().iter().map(|g| g.glyph_index).collect::<Vec<_>>(),
        [10, 10, 10, 20]
    );
    let mut sub = multiple(&[100, 101, 102]);
    put(&mut sub, 8, 65535);
    let p = plan(
        table(&[(3, 0, vec![sub])]),
        b"rand",
        ManagedFeatureOptions::default(),
    );
    let mut b = ManagedGlyphBuffer::new(face, vec![glyph(10, 0), glyph(20, 1)]).unwrap();
    p.apply_stage(&mut b, ManagedSubstitutionStage::All)
        .unwrap();
    assert_eq!(b.state().random_state, 48271);
    assert_eq!(b.glyphs()[0].glyph_index, 10);
    assert_eq!(b.glyphs()[1].flags, 3);
}
#[test]
fn top_level_reverse_error_retains_actual_source_private_state() {
    use fixture::gsub::*;
    let mut sub = reverse();
    put(&mut sub, 20, 65535);
    let data = fixture::table(
        table(&[(8, 16, vec![sub])]),
        &[(*b"pref", vec![0])],
        u16::MAX,
    );
    let face = SfntFace::new(fixture::font(&data, &gdef()), 0).unwrap();
    let options = ManagedFeatureOptions::with_overrides(&[setting(b"pref", 1)]).unwrap();
    let plan = ManagedSubstitutionPlan::from_face(face.clone(), options, &tag(b"DFLT"), None, &[])
        .unwrap()
        .unwrap();
    let mut g = glyph(10, 0);
    g.use_syllable = 42;
    let mut b = ManagedGlyphBuffer::new(face, vec![g]).unwrap();
    assert_eq!(
        plan.apply_stage(&mut b, ManagedSubstitutionStage::Prebase),
        Err(FontError::InvalidFace)
    );
    assert!(b.state().traversal.manual_joiners);
    assert!(b.state().traversal.restrict_to_syllable);
    assert_eq!(b.state().traversal.lookup_syllable, 42);
    assert_eq!(b.mark_filtering_coverage(), 54);
    assert_eq!(b.nested_lookup_depth(), 0);
}
fn emit(b: &mut Vec<u8>, n: u32) {
    b.extend(n.to_le_bytes());
}
fn units(b: &mut Vec<u8>, tag: [u16; 4]) {
    for c in tag {
        b.extend(c.to_le_bytes());
    }
}
struct Policy {
    options: ManagedFeatureOptions,
    mode: u32,
    settings: Vec<ManagedFeatureSetting>,
}
fn policy(options: ManagedFeatureOptions) -> Policy {
    Policy {
        settings: options.features().to_vec(),
        options,
        mode: 0,
    }
}
fn settings(out: &mut Vec<u8>, s: &[ManagedFeatureSetting]) {
    emit(out, s.len() as u32);
    for f in s {
        units(out, f.tag());
        emit(out, f.value() as u32);
    }
}
fn options(out: &mut Vec<u8>, p: &Policy) {
    emit(out, p.mode);
    settings(out, &p.settings);
    emit(out, u32::from(p.options.baseline().is_some()));
    if let Some(b) = p.options.baseline() {
        settings(out, b);
    }
    emit(
        out,
        p.options
            .explicit_tags()
            .map_or(u32::MAX, |v| v.len() as u32),
    );
    if let Some(tags) = p.options.explicit_tags() {
        for &t in tags {
            units(out, t);
        }
    }
    emit(out, p.options.ranges().len() as u32);
    for r in p.options.ranges() {
        for n in [
            u32::from_be_bytes(r.tag()),
            r.value(),
            r.range().start,
            r.range().end,
        ] {
            emit(out, n);
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
#[ignore = "requires actual original C# feature policy and stage executor; CI runs explicitly"]
fn original_managed_policy_and_complete_stage_dispatch() {
    use fixture::gsub::*;
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-managed-dispatch-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_dispatch.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let tags = [
        *b"rvrn",
        *b"frac",
        *b"numr",
        *b"dnom",
        *b"ccmp",
        *b"locl",
        *b"isol",
        *b"fina",
        *b"fin2",
        *b"fin3",
        *b"medi",
        *b"med2",
        *b"init",
        *b"rlig",
        *b"calt",
        *b"clig",
        *b"liga",
        *b"rclt",
        *b"rand",
        *b"ltra",
        *b"ltrm",
        *b"rtla",
        *b"rtlm",
        *b"nukt",
        *b"akhn",
        *b"rphf",
        *b"pref",
        *b"rkrf",
        *b"blwf",
        *b"abvf",
        *b"half",
        *b"pstf",
        *b"vatu",
        *b"cjct",
        *b"ljmo",
        *b"vjmo",
        *b"tjmo",
        *b"pres",
        *b"abvs",
        *b"blws",
        *b"psts",
        *b"haln",
        *b"cfar",
        *b"stch",
        *b"mset",
        *b"TEST",
        [255, 254, 128, 0],
    ];
    let all: Vec<_> = tags
        .iter()
        .map(|t| setting(t, if t == b"rand" { 65535 } else { 1 }))
        .collect();
    let explicit: Vec<_> = tags.iter().map(tag).collect();
    let defaults = ManagedFeatureOptions::default();
    let overrides = vec![
        setting(b"calt", 1),
        setting(b"liga", -1),
        setting(b"rand", 65535),
        setting(b"liga", 3),
    ];
    let policies = vec![
        Policy {
            settings: defaults.features().to_vec(),
            options: defaults,
            mode: 1,
        },
        policy(ManagedFeatureOptions::new(&all, None, Some(&explicit), &[]).unwrap()),
        policy(ManagedFeatureOptions::new(&all, None, None, &[]).unwrap()),
        policy(
            ManagedFeatureOptions::new(
                &all,
                Some(&[]),
                Some(&explicit),
                &[
                    ManagedFeatureRange::new(*b"liga", 2, 0, 3).unwrap(),
                    ManagedFeatureRange::new(*b"liga", u32::MAX, 2, 7).unwrap(),
                    ManagedFeatureRange::new(*b"rand", 65535, 2, 4).unwrap(),
                    ManagedFeatureRange::new(*b"rand", 0, 4, u32::MAX).unwrap(),
                ],
            )
            .unwrap(),
        ),
        policy(
            ManagedFeatureOptions::new(
                &tags.iter().map(|t| setting(t, -1)).collect::<Vec<_>>(),
                None,
                Some(&[]),
                &[],
            )
            .unwrap(),
        ),
        policy(
            ManagedFeatureOptions::new(
                &tags.iter().map(|t| setting(t, 0)).collect::<Vec<_>>(),
                None,
                Some(&explicit),
                &[],
            )
            .unwrap(),
        ),
        Policy {
            options: ManagedFeatureOptions::with_overrides(&overrides).unwrap(),
            mode: 2,
            settings: overrides,
        },
        policy(
            ManagedFeatureOptions::new(
                &all,
                Some(&[setting(b"liga", 0)]),
                Some(&explicit),
                &[
                    ManagedFeatureRange::new(*b"liga", u32::MAX, 0, 0).unwrap(),
                    ManagedFeatureRange::new([255, 254, 128, 0], 17, 0, u32::MAX).unwrap(),
                ],
            )
            .unwrap(),
        ),
    ];
    let mut input = vec![];
    let base = fixture::font(&[], &[]);
    emit(&mut input, base.len() as u32);
    input.extend(base);
    emit(&mut input, policies.len() as u32);
    for p in &policies {
        options(&mut input, p);
    }
    let mut expected = vec![];
    let mut ends = vec![];
    let mut query_count = 0;
    for i in 0..262144usize {
        let p = &policies[i % policies.len()];
        let t = tag(&tags[(i / 64) % tags.len()]);
        let mut g = glyph(10, [-10, 0, 2, 3, 4, 6, 7, i32::MAX][(i / 8) % 8]);
        g.script_shaper = (i / 1024) as u8;
        g.script_feature_mask = (i / 4) as u8;
        g.arabic_action = (i % 11) as u8;
        g.fraction_action = (i % 7) as u8;
        g.use_rphf_eligible = (i % 3) as u8;
        let fractions = i & 1 != 0;
        emit(&mut input, 0);
        emit(&mut input, (i % policies.len()) as u32);
        units(&mut input, t);
        emit(&mut input, u32::from(fractions));
        for n in records::words(g) {
            emit(&mut input, n);
        }
        emit(&mut expected, p.options.value_at(t, g.cluster) as u32);
        let may = ![tag(b"frac"), tag(b"numr"), tag(b"dnom")].contains(&t)
            || fractions
            || p.options.explicit_tags().is_some_and(|v| v.contains(&t));
        emit(
            &mut expected,
            u32::from(p.options.explicit_at(t, g.cluster))
                | u32::from(p.options.enabled(g, t)) << 1
                | u32::from(may) << 2
                | u32::from(p.options.resolves_explicit(t)) << 3,
        );
        ends.push(expected.len());
        query_count += 1;
    }
    let mut stage_count = 0;
    for id in 0..42u8 {
        let stage = ManagedSubstitutionStage::try_from(id).unwrap();
        for raw in tags {
            let t = tag(&raw);
            emit(&mut input, 1);
            units(&mut input, t);
            emit(&mut input, u32::from(id));
            emit(
                &mut expected,
                u32::from(stage.enables(t))
                    | u32::from(stage.manual_joiners()) << 1
                    | u32::from(stage.restricts_syllable(t)) << 2,
            );
            ends.push(expected.len());
            stage_count += 1;
        }
    }
    let mut count = 0;
    let mut bounded = 0;
    let mut add = |data: Vec<u8>,
                   t: [u8; 4],
                   id: u8,
                   policy: usize,
                   required: bool,
                   gdef: &[u8],
                   mut glyphs: Vec<ManagedShapingGlyph>,
                   variant: usize| {
        let ids = if variant & 16 != 0 {
            (0..8).collect()
        } else {
            vec![0]
        };
        let mut features = vec![(t, ids)];
        if variant & 32 != 0 {
            features.push((*b"ccmp", vec![0, 1, 2, 1]));
        }
        let data = fixture::table(data, &features, if required { 0 } else { u16::MAX });
        let font = fixture::font(&data, gdef);
        let start = input.len();
        let state = ManagedGlyphState {
            traversal: ManagedContextState {
                manual_joiners: variant & 1 != 0,
                restrict_to_syllable: variant & 2 != 0,
                lookup_syllable: (variant % 3) as u8,
            },
            context_match_end: if variant & 8 != 0 { 99 } else { -7 },
            random_state: 0xdeadbeef,
        };
        if variant & 4 != 0 {
            for (i, g) in glyphs.iter_mut().enumerate() {
                g.script_shaper = (variant % 6) as u8;
                g.script_feature_mask = (variant % 256) as u8;
                g.arabic_action = (variant % 9) as u8;
                g.fraction_action = (i % 4) as u8;
                g.use_rphf_eligible = (i % 2) as u8;
                g.use_syllable = (i % 3) as u8;
                g.ligated = (i % 3) as u8;
                g.ligature_component = if i % 4 == 3 { 0 } else { 255 };
                g.code_point =
                    [65, 0x301, 0x34f, 0x200c, 0x200d, 0xad, 0x180b, 0xe0000][(i + variant) % 8];
            }
        }
        emit(&mut input, 2);
        emit(&mut input, policy as u32);
        emit(&mut input, u32::from(id));
        emit(&mut input, font.len() as u32);
        input.extend(&font);
        units(&mut input, tag(b"DFLT"));
        for n in [
            state.context_match_end as u32,
            state.random_state,
            u32::from(state.traversal.manual_joiners),
            u32::from(state.traversal.restrict_to_syllable),
            u32::from(state.traversal.lookup_syllable),
            u32::from(variant & 1 != 0),
            glyphs.len() as u32,
        ] {
            emit(&mut input, n);
        }
        for &g in &glyphs {
            for n in records::words(g) {
                emit(&mut input, n);
            }
        }
        let face = SfntFace::new(font, 0).unwrap();
        let mut buffer = ManagedGlyphBuffer::new(face.clone(), glyphs).unwrap();
        buffer.set_state(state);
        buffer.set_fraction_actions_present(variant & 1 != 0);
        let result = ManagedSubstitutionPlan::from_face(
            face,
            policies[policy].options.clone(),
            &tag(b"DFLT"),
            None,
            &[],
        )
        .and_then(|p| {
            p.unwrap()
                .apply_stage(&mut buffer, ManagedSubstitutionStage::try_from(id).unwrap())
        });
        if result == Err(FontError::Capacity) {
            input.truncate(start);
            bounded += 1;
            return;
        }
        let s = buffer.state();
        for n in [
            result.err().map_or(0, |e| e as u32),
            buffer.glyphs().len() as u32,
            s.context_match_end as u32,
            s.random_state,
            buffer.mark_filtering_coverage() as u32,
            buffer.nested_lookup_depth(),
            u32::from(s.traversal.manual_joiners),
            u32::from(s.traversal.restrict_to_syllable),
            u32::from(s.traversal.lookup_syllable),
            u32::from(buffer.has_fraction_actions()),
        ] {
            emit(&mut expected, n);
        }
        for &g in buffer.glyphs() {
            for n in records::words(g) {
                emit(&mut expected, n);
            }
        }
        ends.push(expected.len());
        count += 1;
    };
    let original = || {
        [8, 9, 10, 12, 20, 30, 10, 20]
            .into_iter()
            .enumerate()
            .map(|(i, id)| glyph(id, i as i32 - 3))
            .collect::<Vec<_>>()
    };
    let seeds = seeds();
    let gd = gdef();
    for (i, data) in seeds.iter().enumerate() {
        for (j, stage) in [0, (i % 42) as u8, 4, 18].into_iter().enumerate() {
            add(
                data.clone(),
                tags[(i / 2) % tags.len()],
                stage,
                (i + j) % policies.len(),
                i % 7 == 0,
                if i & 1 == 0 { &[] } else { &gd },
                original(),
                i + j,
            );
        }
    }
    for (kind, sub) in [
        (1, single(0)),
        (2, multiple(&[])),
        (2, multiple(&[10, 10, 10])),
        (3, multiple(&[100, 101, 102])),
        (4, ligature(&[20], 88)),
        (8, reverse()),
    ] {
        for t in tags {
            for id in 0..42 {
                add(
                    table(&[(kind, 16, vec![sub.clone()])]),
                    t,
                    id,
                    1,
                    false,
                    &gd,
                    original(),
                    usize::from(id),
                );
            }
        }
    }
    let mut random = 0x19485213u32;
    for i in 0..12000 {
        let mut data = seeds[i % seeds.len()].clone();
        for _ in 0..1 + i % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let p = random as usize % data.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            data[p] = (random >> 24) as u8;
        }
        add(
            data,
            tags[i % tags.len()],
            0,
            i % policies.len(),
            i % 9 == 0,
            &gd,
            original(),
            i,
        );
    }
    add(
        table(&[(1, 0, vec![single(100)])]),
        *b"liga",
        0,
        0,
        false,
        &[],
        (0..131073).map(|i| glyph(10, i)).collect(),
        0,
    );
    eprintln!(
        "Prepared {query_count} feature queries, {stage_count} stage filters, {count} complete stages; {bounded} separate Rust work-bound rejections"
    );
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
        let case = ends.partition_point(|&e| e <= at);
        let begin = if case == 0 { 0 } else { ends[case - 1] };
        panic!(
            "managed dispatch case {case}, relative byte {} C#={} Rust={}; source {:?}, expected {:?}",
            at - begin,
            result.stdout[at],
            expected[at],
            &result.stdout[begin..(begin + 40).min(result.stdout.len())],
            &expected[begin..(begin + 40).min(expected.len())]
        );
    }
    assert_eq!(result.stdout.len(), expected.len());
    eprintln!(
        "Original managed dispatch: {query_count} feature queries, {stage_count} stage filters, {count} full stages / {} exact bytes",
        expected.len()
    );
}
