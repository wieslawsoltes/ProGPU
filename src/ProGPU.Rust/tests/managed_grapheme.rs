//! Original managed runtime boundaries, independent of native compatibility.
use progpu::text::unicode::{grapheme::*, *};
use std::{
    io::{BufReader, BufWriter, Read, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
};
#[path = "support/managed_grapheme_replay.rs"]
mod replay;
use replay::PROFILE;
fn units(points: &[u32]) -> Vec<u16> {
    let mut result = Vec::new();
    for &point in points {
        if (0xd800..=0xdfff).contains(&point) {
            result.push(point as u16);
        } else {
            result.extend_from_slice(char::from_u32(point).unwrap().encode_utf16(&mut [0; 2]));
        }
    }
    result
}
#[test]
fn explicit_source_profile_and_replacement_keep_separate_contracts() {
    assert!(ManagedGraphemeProfile::try_from(170).is_err());
    for (source, managed, native) in [
        (units(&[0x11a3a, 65]), 1, 2),
        (units(&[65, 0x11b60]), 2, 1),
        (units(&[0x2388, 0x200d, 0x2605]), 1, 2),
    ] {
        let source: Arc<[u16]> = source.into();
        let weak = Arc::downgrade(&source);
        let text = DecodedText::from_utf16(source).unwrap();
        let a = GraphemeText::new(text.clone(), GraphemePolicy::ManagedRuntime(PROFILE)).unwrap();
        assert_eq!(a.clusters().len(), managed);
        assert_eq!(a.policy(), GraphemePolicy::ManagedRuntime(PROFILE));
        assert_eq!(
            cluster_count(text.scalars(), GraphemePolicy::ManagedCompatible),
            Ok(native)
        );
        let sentinel = GraphemeCluster {
            input_index: 9,
            input_length: 8,
            scalar_index: 7,
            scalar_count: 6,
        };
        let mut out = vec![sentinel; managed - 1];
        assert_eq!(
            segment_into(text.scalars(), a.policy(), &mut out),
            Err(UnicodeError::Capacity)
        );
        assert!(out.iter().all(|record| *record == sentinel));
        drop(text);
        assert!(weak.upgrade().is_some());
        drop(a);
        assert!(weak.upgrade().is_none());
    }
    for source in [
        vec![],
        vec![0xd800],
        vec![0xdc00, 0x301],
        vec![0xd800, 0xd800, 0xdc00],
        vec![0x600, 0xd800, 0x301],
    ] {
        let original = source.clone();
        let mut offset = 0;
        while offset < source.len() {
            let count = managed_next_grapheme_length(&source[offset..], PROFILE);
            assert!(count > 0 && count <= source.len() - offset);
            offset += count;
        }
        assert_eq!(source, original);
    }
    assert_eq!(managed_next_grapheme_length(&[], PROFILE), 0);
    assert_eq!(
        managed_next_grapheme_length(&[0xdc00, 0x301, 65], PROFILE),
        2
    );
    assert_eq!(
        managed_next_grapheme_length(&[0xd800, 0xd800, 0xdc00], PROFILE),
        1
    );
    // Source mode omits GB9c, while the original full Unicode policy retains it.
    let text = DecodedText::from_utf16(units(&[0x915, 0x94d, 0x915])).unwrap();
    assert_eq!(
        cluster_count(text.scalars(), GraphemePolicy::ManagedRuntime(PROFILE)),
        Ok(2)
    );
    assert_eq!(
        cluster_count(text.scalars(), GraphemePolicy::Unicode17),
        Ok(1)
    );
}
fn cases(mut emit: impl FnMut(&[u16], bool)) {
    emit(&[], true);
    for cp in [
        0x1acf, 0x11a3a, 0x11b60, 0x1e6e3, 0x113b8, 0x2388, 0x2605, 0x1fa00, 0x915, 0x94d, 0xd800,
        0xdc00,
    ] {
        for p in [
            vec![65, cp, 66],
            vec![cp, 65],
            vec![0x1f469, 0x301, 0x200d, cp],
            vec![cp, 0x301, 0x200d, 0x1f469],
        ] {
            emit(&units(&p), true);
        }
    }
    let points = [
        65, 13, 10, 0, 0x301, 0x200d, 0x1f1e6, 0x600, 0x903, 0x1100, 0x1160, 0x11a8, 0xac00,
        0xac01, 0x1f469, 0x915, 0x94d, 0xd800, 0xdc00,
    ];
    for a in points {
        for b in points {
            for c in points {
                emit(&units(&[a, b, c]), false);
            }
        }
    }
    for cp in (0..=0x10ffff).filter(|&cp| char::from_u32(cp).is_some()) {
        for p in [
            vec![cp],
            vec![65, cp, 66],
            vec![cp, 65],
            vec![13, cp, 10],
            vec![0x1100, cp, 0x1160, 0x11a8],
            vec![0x1160, cp, 0x11a8],
            vec![0x11a8, cp, 0x1100],
            vec![0xac00, cp, 0x1160],
            vec![0xac01, cp, 0x11a8],
            vec![0x1f1e6, cp, 0x1f1e7],
            vec![0x1f1e6, 0x1f1e7, cp, 0x1f1e8],
            vec![0x1f469, 0x301, 0x200d, cp],
            vec![cp, 0x301, 0x200d, 0x1f469],
            vec![0x600, cp, 0x903, 66],
            vec![cp, 0x301, 0x200d, 10],
            vec![0x915, 0x94d, cp, 0x94d, 0x915],
        ] {
            emit(&units(&p), false);
        }
    }
    for a in 0xd800..=0xdfff {
        for b in 0xd800..=0xdfff {
            emit(&[a, b], false);
        }
    }
    for unit in 0..=u16::MAX {
        for p in [[unit, 0x301], [0x600, unit], [0xd800, unit], [unit, 0xdc00]] {
            emit(&p, false);
        }
    }
    for length in [31, 128, 1024] {
        for cp in [0x301, 0x600, 0x1f1e6, 0x200d] {
            emit(&units(&vec![cp; length]), false);
        }
    }
}
struct Temporary(PathBuf);
impl Drop for Temporary {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[test]
#[ignore = "requires .NET 10 and original OriginalGraphemePolicy source; exhaustive scalar/UTF-16 contexts"]
fn complete_original_managed_grapheme_repertoire() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = Temporary(
        std::env::temp_dir().join(format!("progpu-managed-grapheme-{}", std::process::id())),
    );
    std::fs::create_dir(&dir.0).unwrap();
    let includes = [
        "src/ProGPU.Rust/tests/managed_grapheme_profile.cs",
        "src/ProGPU.Text/OriginalGraphemePolicy.cs",
    ]
    .map(|s| {
        format!(
            "<Compile Include=\"{}\"/>",
            root.join(s)
                .display()
                .to_string()
                .replace('&', "&amp;")
                .replace('"', "&quot;")
        )
    })
    .join("");
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>")).unwrap();
    let dotnet = std::env::var_os("PROGPU_RUST_DOTNET").unwrap_or_else(|| "dotnet".into());
    assert!(
        Command::new(&dotnet)
            .current_dir(&dir.0)
            .args([
                "build",
                "Control.csproj",
                "-c",
                "Release",
                "-o",
                "out",
                "--nologo",
                "-v:q"
            ])
            .status()
            .unwrap()
            .success()
    );
    let input = dir.0.join("input.bin");
    let actual = dir.0.join("actual.bin");
    let mut count = 0u64;
    {
        let mut writer = BufWriter::new(std::fs::File::create(&input).unwrap());
        cases(|source, _| {
            writer
                .write_all(&(source.len() as u32).to_le_bytes())
                .unwrap();
            for &unit in source {
                writer.write_all(&unit.to_le_bytes()).unwrap();
            }
            count += 1;
        });
        writer.flush().unwrap();
    }
    assert!(
        Command::new(&dotnet)
            .arg(dir.0.join("out/Control.dll"))
            .stdin(std::fs::File::open(input).unwrap())
            .stdout(std::fs::File::create(&actual).unwrap())
            .stderr(Stdio::inherit())
            .status()
            .unwrap()
            .success()
    );
    let mut reader = BufReader::new(std::fs::File::open(actual).unwrap());
    let mut replay = replay::Replay::default();
    let mut expected = Vec::new();
    let mut bytes = Vec::new();
    let mut index = 0u64;
    let mut compared = 0u64;
    let mut browser = Vec::new();
    cases(|source, retain| {
        replay.execute(source, &mut expected);
        bytes.resize(expected.len(), 0);
        reader.read_exact(&mut bytes).unwrap();
        if let Some(offset) = bytes.iter().zip(&expected).position(|(a, b)| a != b) {
            panic!(
                "case {index}, UTF-16 {source:x?}, byte {offset}: source {} Rust {}",
                bytes[offset], expected[offset]
            );
        }
        compared += bytes.len() as u64;
        index += 1;
        if retain {
            browser.push((source.to_vec(), bytes.clone()));
        }
    });
    assert_eq!(index, count);
    assert_eq!(reader.read(&mut [0]).unwrap(), 0);
    let mut capture = b"PMG2".to_vec();
    replay::emit(&mut capture, browser.len() as u32);
    for (source, output) in &browser {
        replay::emit(&mut capture, source.len() as u32);
        for unit in source {
            capture.extend(unit.to_le_bytes());
        }
        replay::emit(&mut capture, output.len() as u32);
        capture.extend(output);
    }
    let fixture = root.join("src/ProGPU.Rust/browser/managed-grapheme.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_GRAPHEME_FIXTURE").is_some() {
        std::fs::write(&fixture, &capture).unwrap();
    }
    assert_eq!(
        std::fs::read(fixture).unwrap(),
        capture,
        "frozen source browser fixture"
    );
    println!(
        "Original managed graphemes: {count} cases / {compared} exact bytes; {} browser cases / {} bytes",
        browser.len(),
        capture.len()
    );
}
