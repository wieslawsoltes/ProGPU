//! Actual source actions, full glyph/state bytes, metadata and error continuations.
use progpu::{
    font::{ManagedFont, layout::ManagedGlyphBuffer},
    text::{
        preparation::*,
        script_actions::*,
        unicode::{normalization::*, *},
    },
};
use std::{
    io::{BufReader, BufWriter, Read, Write},
    path::PathBuf,
    process::{Command, Stdio},
};
#[path = "support/managed_script_actions_fixture.rs"]
mod fixture;
#[path = "support/managed_script_actions_replay.rs"]
mod replay;
use replay::Case;
#[test]
fn script_actions_require_source_and_keep_source_owners() {
    let font = ManagedFont::new(fixture::fonts().remove(0), 0).unwrap();
    let mut mapped = ManagedGlyphBuffer::new(font.source().clone(), vec![]).unwrap();
    assert_eq!(
        mapped.apply_script_action(ManagedScriptAction::Reverse),
        Err(PreparationError::MissingPreparation)
    );
    let mut options = ManagedPreparationOptions::new(
        ManagedNormalizationProfile::Unicode17_0,
        ManagedMarkProfile::Unicode16_0,
        grapheme::ManagedGraphemeProfile::Unicode16_0,
    );
    options.buffer_flags = ManagedBufferFlags::BEGINNING_OF_TEXT;
    let mut buffer = ManagedGlyphBuffer::from_text(vec![0x301], font, "latn", options).unwrap();
    buffer
        .apply_script_action(ManagedScriptAction::BeginningDottedCircle)
        .unwrap();
    assert_eq!(buffer.preparation().unwrap().text(), [0x301]);
    let mut clone = buffer.clone();
    clone
        .apply_script_action(ManagedScriptAction::Reverse)
        .unwrap();
    assert_ne!(clone.glyphs(), buffer.glyphs());
    assert_eq!(
        clone.preparation().unwrap().text(),
        buffer.preparation().unwrap().text()
    );
}
fn source(font: usize, script: &str, level: u8, text: Vec<u16>) -> replay::preparation::Case {
    replay::preparation::Case {
        font,
        script: script.into(),
        level,
        flags: 0,
        text,
        pre: vec![],
        post: vec![],
    }
}
fn raw(font: usize, script: &str, level: u8, points: &[u32], actions: Vec<u32>) -> Case {
    let records = points
        .iter()
        .enumerate()
        .map(|(i, &cp)| {
            let mut words = std::array::from_fn::<_, 31, _>(|n| ((i + 1) * (n + 3) * 7) as u32);
            words[0] = 7;
            words[1] = (i / 2) as u32;
            words[2] = 0;
            words[3] = i as u32;
            words[4] = cp;
            replay::preparation::records::from_words(&words)
        })
        .collect();
    Case {
        source: source(font, script, level, vec![65; points.len()]),
        actions,
        raw: Some(records),
    }
}
fn corpus(mut emit: impl FnMut(Case, bool)) {
    let sets: [&[u32]; 9] = [
        &[],
        &[0x301],
        &[65, 0x654, 0x327, 0x655, 0x301],
        &[0x1100, 0x1161, 0x11a8, 0xac00, 0xd7cb],
        &[0x5e9, 0x5bc, 0x5c1],
        &[0xe01, 0xe48, 0xe33],
        &[0xe33],
        &[0x31, 0x2044, 0x32, 0x2044, 0x33],
        &[0x28, 0x29, 0x3008, 0x3009, 0x344],
    ];
    for (s, script) in [
        "latn", "hang", "hebr", "arab", "syrc", "thai", "lao ", "tibt",
    ]
    .iter()
    .enumerate()
    {
        for font in [0, 1, 2, 30, 31, 32, 33, 34, 35, 36] {
            for level in 0..4 {
                for (n, points) in sets.iter().enumerate() {
                    for action in 0..=20 {
                        let mut c = raw(font, script, level, points, vec![action]);
                        c.source.flags = 1;
                        emit(
                            c,
                            (font == 0 && level == 1 && n == (s + action as usize) % sets.len())
                                || (matches!(font, 1 | 30 | 33)
                                    && ((script == &"hang" && action == 9 && n == 3)
                                        || (script == &"hebr" && action == 11 && n == 4)
                                        || (script == &"thai" && action == 15 && n == 5))),
                        );
                    }
                }
            }
        }
    }
    // Actual text generation before the source action, including malformed UTF-16.
    for font in [0, 1, 2, 30, 33, 34] {
        for script in ["latn", "hang", "hebr", "arab", "thai", "lao ", "tibt"] {
            for level in 0..4 {
                for points in sets {
                    let mut text = fixture::preparation::units(points);
                    text.push(0xd800);
                    for flags in [0, 1, 2, 3, 17, 255] {
                        let mut c = Case {
                            source: source(font, script, level, text.clone()),
                            actions: vec![0, 7, 9, 10, 11, 14, 15, 16, 17, 18, 19, 20],
                            raw: None,
                        };
                        c.source.flags = flags;
                        emit(c, font == 0 && level == 1 && flags == 1);
                    }
                }
            }
        }
    }
    // Independent original mark/fraction malformed-record admission and context gates.
    for cp in [0, 0x301, 0xfffe, 0xd800, 0xdfff, 0x110000, u32::MAX] {
        for flags in 0..=255 {
            for pre in [vec![], vec![0xd800]] {
                for action in [0, 10, 14, 16] {
                    let points = if action == 16 {
                        vec![cp, 0x2044, 0x31]
                    } else {
                        vec![cp]
                    };
                    let mut c = raw(0, "latn", 0, &points, vec![action]);
                    c.source.flags = flags;
                    c.source.pre = pre.clone();
                    emit(c, false);
                }
            }
        }
    }
    // Every valid scalar through real actions, preserving arbitrary original metadata.
    let mut block = Vec::new();
    let mut number = 0;
    for cp in (0..=0x10ffff).filter(|&v| char::from_u32(v).is_some()) {
        block.push(cp);
        if block.len() == 512 || cp == 0x10ffff {
            for (script, action) in [("latn", 7), ("arab", 10), ("tibt", 14)] {
                emit(
                    raw(0, script, (number % 4) as u8, &block, vec![action]),
                    false,
                );
            }
            block.clear();
            number += 1;
        }
    }
    // Every modern syllable and matching L/V/T path, with font coverage and all levels.
    for syllable in 0xac00..=0xd7a3 {
        let index = syllable - 0xac00;
        let l = 0x1100 + index / 588;
        let v = 0x1161 + (index % 588) / 28;
        let t = index % 28;
        let mut jamo = vec![l, v];
        if t != 0 {
            jamo.push(0x11a7 + t);
        }
        for font in [0, 1, 30, 31, 32] {
            for level in 0..4 {
                emit(raw(font, "hang", level, &jamo, vec![9]), false);
                emit(
                    raw(font, "hang", level, &[syllable, 0xd7cb], vec![9]),
                    false,
                );
            }
        }
    }
    let hebrew: Vec<_> = (0x590..=0x5ff).chain(0xfb1d..=0xfb4e).collect();
    for &base in &hebrew {
        for mark in 0x590..=0x5ff {
            for font in [0, 1, 33] {
                emit(
                    raw(
                        font,
                        "hebr",
                        ((base + mark) % 4) as u8,
                        &[base, mark, 0x5bc, 0x5c1],
                        vec![11],
                    ),
                    false,
                );
            }
        }
    }
    let thai = [
        0xe01, 0xe31, 0xe33, 0xe34, 0xe37, 0xe38, 0xe3b, 0xe46, 0xe47, 0xe48, 0xe4e, 0xe4f,
    ];
    for &a in &thai {
        for &b in &thai {
            for &c in &thai {
                for level in 0..4 {
                    for (script, shift) in [("thai", 0), ("lao ", 0x80)] {
                        emit(
                            raw(
                                0,
                                script,
                                level,
                                &[a + shift, b + shift, c + shift],
                                vec![15],
                            ),
                            false,
                        );
                    }
                }
            }
        }
    }
    let marks = [
        0x301, 0x300, 0x327, 0x5b0, 0x5b1, 0x5bc, 0x654, 0x655, 0x658, 0x6dc, 0x8ca, 0xf39, 0xfc6,
        0x1a60, 0x1acf,
    ];
    for &a in &marks {
        for &b in &marks {
            for &c in &marks {
                for level in 0..4 {
                    for script in ["latn", "arab", "adlm"] {
                        emit(raw(0, script, level, &[65, a, b, c, 66], vec![10]), false);
                    }
                }
            }
        }
    }
    let mut rng = 0x7135ac9u64;
    for _ in 0..12000 {
        let mut next = || {
            rng ^= rng << 13;
            rng ^= rng >> 7;
            rng ^= rng << 17;
            rng as u32
        };
        let n = (next() % 32) as usize;
        let pool = [
            0x1100, 0x1161, 0x11a8, 0xac00, 0xd7cb, 0x301, 0x5e9, 0x5c1, 0xe33, 0xe48, 0xeb3,
            0x344, 0x31, 0x2044, 0xfffe, 0xd800, 0x110000, 0x28,
        ];
        let points: Vec<_> = (0..n).map(|_| pool[next() as usize % pool.len()]).collect();
        let actions = (0..1 + next() % 6).map(|_| next() % 21).collect();
        let scripts = ["hang", "hebr", "arab", "thai", "lao ", "tibt", "latn"];
        let mut c = raw(
            (next() % 3) as usize,
            scripts[next() as usize % scripts.len()],
            (next() % 4) as u8,
            &points,
            actions,
        );
        c.source.flags = next() as u8;
        if let Some(glyphs) = &mut c.raw {
            for g in glyphs {
                let cp = g.code_point;
                let mut words = std::array::from_fn::<_, 31, _>(|_| next());
                words[4] = cp;
                *g = replay::preparation::records::from_words(&words);
            }
        }
        emit(c, false);
    }
}
struct Temporary(PathBuf);
impl Drop for Temporary {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn word(reader: &mut impl Read) -> u32 {
    let mut b = [0; 4];
    reader.read_exact(&mut b).unwrap();
    u32::from_le_bytes(b)
}
fn write_words(writer: &mut impl Write, words: &[u32]) {
    for &v in words {
        writer.write_all(&v.to_le_bytes()).unwrap();
    }
}
#[test]
#[ignore = "requires original managed source/.NET 10; complete script actions and scalar inventories"]
fn complete_original_managed_script_actions() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let temporary = Temporary(
        std::env::temp_dir().join(format!("progpu-script-actions-{}", std::process::id())),
    );
    std::fs::create_dir_all(&temporary.0).unwrap();
    let xml = |s: &str| {
        root.join(s)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(temporary.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_script_actions.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
    let dotnet = std::env::var_os("PROGPU_RUST_DOTNET").unwrap_or_else(|| "dotnet".into());
    assert!(
        Command::new(&dotnet)
            .current_dir(&temporary.0)
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
                temporary.0.join("artifacts").display()
            ))
            .status()
            .unwrap()
            .success()
    );
    let facts_path = temporary.0.join("facts.bin");
    assert!(
        Command::new(&dotnet)
            .arg(
                temporary
                    .0
                    .join("artifacts/bin/Control/release/Control.dll")
            )
            .env("PROGPU_RUST_ACTION_FACTS", "1")
            .stdin(Stdio::null())
            .stdout(std::fs::File::create(&facts_path).unwrap())
            .status()
            .unwrap()
            .success()
    );
    let mut facts = BufReader::new(std::fs::File::open(&facts_path).unwrap());
    for _ in 0..12 {
        let count = word(&mut facts);
        if count != u32::MAX {
            for _ in 0..count {
                word(&mut facts);
            }
        }
    }
    for cp in 0..=0x110000 {
        assert_eq!(
            word(&mut facts),
            managed_modified_combining_class(cp) as u32,
            "modified class U+{cp:x}"
        );
        let digit = ManagedDigitProfile::Unicode16_0
            .is_decimal_digit(cp)
            .map(u32::from)
            .unwrap_or(u32::MAX);
        assert_eq!(word(&mut facts), digit, "decimal class U+{cp:x}");
    }
    assert_eq!(facts.read(&mut [0]).unwrap(), 0);
    eprintln!(
        "Original modified classes and Rune digits: 1114113 complete raw scalar queries / 8912904 exact bytes"
    );
    let font_bytes = fixture::fonts();
    let fonts: Vec<_> = font_bytes
        .iter()
        .map(|data| ManagedFont::new(data.clone(), 0).ok())
        .collect();
    let input = temporary.0.join("input.bin");
    let output = temporary.0.join("output.bin");
    let mut writer = BufWriter::new(std::fs::File::create(&input).unwrap());
    write_words(&mut writer, &[font_bytes.len() as u32]);
    for font in &font_bytes {
        write_words(&mut writer, &[font.len() as u32]);
        writer.write_all(font).unwrap();
    }
    let mut count = 0;
    corpus(|case, _| {
        let request = case.request();
        write_words(
            &mut writer,
            &[case.source.font as u32, request.len() as u32],
        );
        write_words(&mut writer, &request);
        count += 1;
    });
    writer.flush().unwrap();
    drop(writer);
    assert!(
        Command::new(&dotnet)
            .arg(
                temporary
                    .0
                    .join("artifacts/bin/Control/release/Control.dll")
            )
            .stdin(std::fs::File::open(&input).unwrap())
            .stdout(std::fs::File::create(&output).unwrap())
            .stderr(Stdio::inherit())
            .status()
            .unwrap()
            .success()
    );
    let mut reader = BufReader::new(std::fs::File::open(&output).unwrap());
    let mut witnesses = Vec::new();
    for _ in 0..12 {
        let n = word(&mut reader);
        witnesses.push(n);
        if n != u32::MAX {
            for _ in 0..n {
                witnesses.push(word(&mut reader));
            }
        }
    }
    let mut candidates = Vec::new();
    for profile in [
        ManagedNormalizationProfile::Unicode15_1,
        ManagedNormalizationProfile::Unicode16_0,
        ManagedNormalizationProfile::Unicode17_0,
    ] {
        let mut expected = Vec::new();
        for points in [
            &[0x105c9][..],
            &[0x105d2, 0x307],
            &[0x61, 0x897, 0x334],
            &[0x61, 0x1acf, 0x334],
            &[0x61, 0x301, 0x1acf],
            &[0xfffe],
        ] {
            for form in [
                NormalizationForm::CanonicalDecomposition,
                NormalizationForm::CanonicalComposition,
            ] {
                match NormalizedText::with_managed_profile(
                    DecodedText::from_utf16(fixture::preparation::units(points)).unwrap(),
                    profile,
                    form,
                ) {
                    Err(_) => expected.push(u32::MAX),
                    Ok(text) => replay::preparation::units(
                        &mut expected,
                        &fixture::preparation::units(
                            &text
                                .scalars()
                                .iter()
                                .map(|s| s.code_point)
                                .collect::<Vec<_>>(),
                        ),
                    ),
                }
            }
        }
        if expected == witnesses {
            candidates.push(profile);
        }
    }
    assert_eq!(
        candidates.len(),
        1,
        "unsupported or ambiguous original normalization facts"
    );
    let profile = candidates[0];
    eprintln!("Observed original preparation normalization candidate: {profile:?}");
    let mut index = 0;
    let mut bytes = 0;
    let mut expected = Vec::new();
    let mut actual = Vec::new();
    let mut browser = Vec::new();
    let mut browser_count = 0;
    corpus(|case, freeze| {
        replay::execute(
            fonts[case.source.font].as_ref(),
            &case,
            profile,
            &mut expected,
        );
        actual.resize(expected.len() * 4, 0);
        reader.read_exact(&mut actual).unwrap();
        if let Some(n) = actual
            .chunks_exact(4)
            .zip(&expected)
            .position(|(a, &e)| u32::from_le_bytes(a.try_into().unwrap()) != e)
        {
            panic!(
                "preparation case {index}, font {}, script {}, level {}, text length {}, first units {:?}, word {n}: original {}, Rust {}",
                case.source.font,
                case.source.script,
                case.source.level,
                case.source.text.len(),
                &case.source.text[..case.source.text.len().min(24)],
                u32::from_le_bytes(actual[n * 4..n * 4 + 4].try_into().unwrap()),
                expected[n]
            );
        }
        if freeze {
            let request = case.request();
            write_words(
                &mut browser,
                &[case.source.font as u32, request.len() as u32],
            );
            write_words(&mut browser, &request);
            write_words(&mut browser, &[actual.len() as u32]);
            browser.extend(&actual);
            browser_count += 1;
        }
        index += 1;
        bytes += actual.len();
    });
    assert_eq!(index, count);
    assert_eq!(reader.read(&mut [0]).unwrap(), 0);
    let mut fixture = b"PSA1".to_vec();
    write_words(&mut fixture, &[font_bytes.len() as u32]);
    for font in &font_bytes {
        write_words(&mut fixture, &[font.len() as u32]);
        fixture.extend(font);
    }
    write_words(&mut fixture, &[browser_count]);
    fixture.extend(browser);
    let path = root.join("src/ProGPU.Rust/browser/managed-script-actions.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_ACTIONS_FIXTURE").is_some() {
        std::fs::write(&path, &fixture).unwrap();
    }
    assert_eq!(std::fs::read(path).unwrap(), fixture);
    eprintln!(
        "Original managed script actions: {count} cases / {bytes} exact bytes; {browser_count} browser cases / {} bytes",
        fixture.len()
    );
}
