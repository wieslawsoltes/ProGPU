//! Complete original text-to-substitution-buffer preparation, before script stages.
use progpu::{
    font::{ManagedFont, layout::ManagedGlyphBuffer},
    text::{
        preparation::*,
        unicode::{grapheme::ManagedGraphemeProfile, normalization::*, *},
    },
};
use std::{
    io::{BufReader, BufWriter, Read, Write},
    path::PathBuf,
    process::{Command, Stdio},
    sync::Arc,
};
#[path = "support/managed_preparation_fixture.rs"]
mod fixture;
#[path = "support/managed_preparation_replay.rs"]
mod replay;
use replay::Case;
fn options() -> ManagedPreparationOptions {
    ManagedPreparationOptions::new(
        ManagedNormalizationProfile::Unicode17_0,
        ManagedMarkProfile::Unicode16_0,
        ManagedGraphemeProfile::Unicode16_0,
    )
}
#[test]
fn prepared_buffers_keep_original_owners_context_and_independent_records() {
    let bytes: Arc<[u8]> = fixture::fonts().remove(0).into();
    let weak_font = Arc::downgrade(&bytes);
    let font = ManagedFont::new(bytes, 0).unwrap();
    let source: Arc<[u16]> = vec![65, 0x301, 0x200d, 0xd800].into();
    let weak_text = Arc::downgrade(&source);
    let pre: Arc<[u16]> = vec![0xdc00, 0x41].into();
    let weak_pre = Arc::downgrade(&pre);
    let mut options = options();
    options.cluster_level = ManagedClusterLevel::Characters;
    options.buffer_flags = ManagedBufferFlags::from_bits(255);
    options.pre_context = pre.into();
    options.post_context = vec![0xd800].into();
    let mut a = ManagedGlyphBuffer::from_text(source, font, "deva", options).unwrap();
    let b = a.clone();
    let original = b.glyphs().to_vec();
    a.glyphs_mut()[0].glyph_index = 123;
    assert_eq!(b.glyphs(), original);
    assert_ne!(a.glyphs(), b.glyphs());
    assert_eq!(b.preparation().unwrap().text(), [65, 0x301, 0x200d, 0xd800]);
    assert_eq!(b.preparation().unwrap().options().buffer_flags.bits(), 255);
    assert_eq!(
        b.preparation().unwrap().options().pre_context.as_ref(),
        [0xdc00, 0x41]
    );
    drop(a);
    assert!(
        weak_font.upgrade().is_some()
            && weak_text.upgrade().is_some()
            && weak_pre.upgrade().is_some()
    );
    drop(b);
    assert!(
        weak_font.upgrade().is_none()
            && weak_text.upgrade().is_none()
            && weak_pre.upgrade().is_none()
    );
    assert!(ManagedClusterLevel::try_from(4).is_err());
    let font = ManagedFont::new(fixture::fonts().remove(0), 0).unwrap();
    assert!(
        ManagedGlyphBuffer::new(font.source().clone(), vec![])
            .unwrap()
            .preparation()
            .is_none()
    );
    assert_eq!(
        ManagedGlyphBuffer::from_text(vec![0xfffe], font.clone(), "latn", self::options())
            .unwrap_err(),
        PreparationError::Unicode(UnicodeError::InvalidArgument)
    );
    // Whole-input malformed admission skips normalization; a mapped noncharacter survives.
    assert_eq!(
        ManagedGlyphBuffer::from_text(vec![0xfffe, 0xd800], font, "latn", self::options())
            .unwrap()
            .glyphs()
            .len(),
        2
    );
}
fn case(font: usize, script: &str, level: u8, text: Vec<u16>) -> Case {
    Case {
        font,
        script: script.into(),
        level,
        flags: level.wrapping_mul(85),
        text,
        pre: vec![0xd800, 0x61],
        post: vec![0x301, 0xdc00],
    }
}
fn corpus(mut emit: impl FnMut(Case, bool)) {
    let texts = vec![
        vec![],
        fixture::units(&[65, 0x301, 66]),
        fixture::units(&[0x600, 65, 0x301]),
        fixture::units(&[0x915, 0x94d, 0x200c, 0x915, 0x200d, 0x9cc, 0x9cc]),
        fixture::units(&[0x1780, 0x17d2, 0x1781, 0x17c4, 0x200d, 0x17be]),
        fixture::units(&[0x1100, 0x1160, 0x11a8, 0xac00]),
        vec![65, 0xfe0f, 0xfe0e, 0xd800, 0xfe0f],
        fixture::units(&[0xa0, 0x2000, 0x2007, 0x2011, 0x202f, 0x3000]),
        fixture::units(&[0x1f469, 0xe0100, 0x200d, 0x1f469]),
        vec![0xfffe],
        vec![0xd800, 0xfffe, 0x301],
        fixture::units(&[0x931, 0x9dc, 0x9dd, 0xb94, 0x344]),
    ];
    for font in 0..8 {
        for (s, script) in ["latn", "deva", "khmr", "hang", "tibt"].iter().enumerate() {
            for level in 0..4 {
                for (n, text) in texts.iter().enumerate() {
                    emit(
                        case(font, script, level, text.clone()),
                        (font + s + n + level as usize).is_multiple_of(17),
                    );
                }
            }
        }
    }
    for font in 0..fixture::fonts().len() {
        for &script in fixture::SCRIPTS {
            for level in 0..4 {
                emit(
                    case(
                        font,
                        script,
                        level,
                        fixture::units(&[65, 0x301, 0x200c, 0x915, 0x94d, 0x915]),
                    ),
                    false,
                );
            }
        }
    }
    // Complete Unicode repertoire through real run preparation, not property queries.
    let mut points = Vec::new();
    let mut block = 0;
    for point in (0..=0x10ffff).filter(|&point| char::from_u32(point).is_some()) {
        points.push(point);
        if points.len() == 1024 || point == 0x10ffff {
            for (font, script) in [(0, "latn"), (0, "deva"), (1, "latn"), (1, "tibt")] {
                emit(
                    case(font, script, (block % 4) as u8, fixture::units(&points)),
                    false,
                );
            }
            block += 1;
            points.clear();
        }
    }
    for unit in 0..=u16::MAX {
        for (n, text) in [
            vec![65, unit, 0x301],
            vec![0x600, unit, 0x200d],
            vec![0xd800, unit, 0xdc00],
            vec![unit, 0xfffe, 0xd800],
        ]
        .into_iter()
        .enumerate()
        {
            emit(
                case(
                    n,
                    ["latn", "deva", "khmr", "tibt"][n],
                    (unit % 4) as u8,
                    text,
                ),
                false,
            );
        }
    }
    let points = [
        65, 0x301, 0x600, 0x200c, 0x200d, 0x915, 0x94d, 0x9cc, 0x17c4, 0xfe0f, 0xd800,
    ];
    for a in points {
        for b in points {
            for c in points {
                for script in ["deva", "khmr", "hang", "tibt"] {
                    for level in 0..4 {
                        emit(
                            case(
                                (a as usize + b as usize + c as usize) % 4,
                                script,
                                level,
                                fixture::units(&[a, b, c]),
                            ),
                            false,
                        );
                    }
                }
            }
        }
    }
    let values = [
        0, 65, 0x301, 0x334, 0x344, 0x600, 0x897, 0x915, 0x94d, 0x931, 0x9cc, 0x9dc, 0x17d2,
        0x17c4, 0x1100, 0x1160, 0x11a8, 0x1acf, 0x200c, 0x200d, 0x2011, 0x2388, 0x2605, 0x105c9,
        0x11a3a, 0x11b60, 0x1e6e3, 0x1f469, 0x1f1e6, 0xfe0f, 0xe0100, 0xd800, 0xdc00, 0xfffe,
        0xffff,
    ];
    let mut seed = 0x7193b7a1u32;
    let mut next = || {
        seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
        seed
    };
    for _ in 0..12000 {
        let points: Vec<_> = (0..next() % 32)
            .map(|_| values[next() as usize % values.len()])
            .collect();
        let mut c = case(
            next() as usize % 8,
            fixture::SCRIPTS[next() as usize % fixture::SCRIPTS.len()],
            (next() % 4) as u8,
            fixture::units(&points),
        );
        c.flags = next() as u8;
        emit(c, false);
    }
    emit(
        case(0, "latn", 0, vec![65; MAX_MANAGED_SHAPED_GLYPHS]),
        false,
    );
    emit(
        case(0, "latn", 0, vec![65; MAX_MANAGED_SHAPED_GLYPHS + 1]),
        false,
    );
    emit(
        case(0, "beng", 0, vec![0x9cc; MAX_MANAGED_SHAPED_GLYPHS / 2 + 1]),
        false,
    );
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
#[ignore = "requires original managed source/.NET 10; complete text preparation and all scalar inventories"]
fn complete_original_managed_preparation() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let temporary =
        Temporary(std::env::temp_dir().join(format!("progpu-preparation-{}", std::process::id())));
    std::fs::create_dir_all(&temporary.0).unwrap();
    let xml = |s: &str| {
        root.join(s)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(temporary.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_preparation.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
        write_words(&mut writer, &[case.font as u32, request.len() as u32]);
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
                    DecodedText::from_utf16(fixture::units(points)).unwrap(),
                    profile,
                    form,
                ) {
                    Err(_) => expected.push(u32::MAX),
                    Ok(text) => replay::units(
                        &mut expected,
                        &fixture::units(
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
        replay::execute(fonts[case.font].as_ref(), &case, profile, &mut expected);
        actual.resize(expected.len() * 4, 0);
        reader.read_exact(&mut actual).unwrap();
        if let Some(n) = actual
            .chunks_exact(4)
            .zip(&expected)
            .position(|(a, &e)| u32::from_le_bytes(a.try_into().unwrap()) != e)
        {
            panic!(
                "preparation case {index}, font {}, script {}, level {}, text length {}, first units {:?}, word {n}: original {}, Rust {}",
                case.font,
                case.script,
                case.level,
                case.text.len(),
                &case.text[..case.text.len().min(24)],
                u32::from_le_bytes(actual[n * 4..n * 4 + 4].try_into().unwrap()),
                expected[n]
            );
        }
        if freeze {
            let request = case.request();
            write_words(&mut browser, &[case.font as u32, request.len() as u32]);
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
    let mut fixture = b"PTP1".to_vec();
    write_words(&mut fixture, &[font_bytes.len() as u32]);
    for font in &font_bytes {
        write_words(&mut fixture, &[font.len() as u32]);
        fixture.extend(font);
    }
    write_words(&mut fixture, &[browser_count]);
    fixture.extend(browser);
    let path = root.join("src/ProGPU.Rust/browser/managed-preparation.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_PREPARATION_FIXTURE").is_some() {
        std::fs::write(&path, &fixture).unwrap();
    }
    assert_eq!(std::fs::read(path).unwrap(), fixture);
    eprintln!(
        "Original managed preparation: {count} cases / {bytes} exact bytes; {browser_count} browser cases / {} bytes",
        fixture.len()
    );
}
