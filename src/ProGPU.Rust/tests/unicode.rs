//! Exhaustive original Unicode properties, strict encodings and script records.
use progpu::{font::Tag, text::unicode::*};
const MARKER: UnicodeScalar = UnicodeScalar {
    code_point: 0x41414141,
    input_index: 0x42424242,
    input_length: 0x4343,
    canonical_combining_class: 0x44,
    reserved: 0x45,
    script: Tag(0x46464646),
};
const RUN_MARKER: ScriptRun = ScriptRun {
    scalar_start: 0x41414141,
    scalar_count: 0x42424242,
    input_start: 0x43434343,
    input_length: 0x44444444,
    script: Tag(0x45454545),
};
enum Case {
    Properties(Vec<u32>),
    Utf8(Vec<u8>, usize),
    Utf16(Vec<u16>, usize),
    Scripts(Vec<UnicodeScalar>, usize),
}
fn scalar(cp: u32, index: u32, len: u16, tag: Tag) -> UnicodeScalar {
    UnicodeScalar {
        code_point: cp,
        input_index: index,
        input_length: len,
        script: tag,
        canonical_combining_class: 99,
        reserved: 77,
    }
}
fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    let mut codes: Vec<_> = (0..=0x110010).collect();
    codes.extend([0x7fffffff, 0x80000000, u32::MAX]);
    cases.push(Case::Properties(codes));
    for cp in [
        0,
        0x20,
        0x301,
        0x3042,
        0xe81,
        0x41,
        0x627,
        0xd800,
        0x10ffff,
        0x110000,
        u32::MAX,
    ] {
        cases.push(Case::Properties(vec![0, 0x20, cp, 0x41]));
    }
    cases.push(Case::Properties(vec![]));
    let mut utf8 = Vec::new();
    let mut utf16 = Vec::new();
    let mut count = 0;
    for cp in 0..=0x10ffff {
        if let Some(c) = char::from_u32(cp) {
            let mut b = [0; 4];
            utf8.extend_from_slice(c.encode_utf8(&mut b).as_bytes());
            let mut w = [0; 2];
            utf16.extend_from_slice(c.encode_utf16(&mut w));
            count += 1;
        }
    }
    cases.push(Case::Utf8(utf8, count + 2));
    cases.push(Case::Utf16(utf16, count + 2));
    for first in 0..=255 {
        cases.push(Case::Utf8(vec![first], 3));
        for second in 0..=255 {
            cases.push(Case::Utf8(vec![first, second], 4));
        }
    }
    for first in 0..=255 {
        for a in [0, 0x7f, 0x80, 0x8f, 0x90, 0x9f, 0xa0, 0xbf, 0xc0, 0xff] {
            for b in [0, 0x7f, 0x80, 0x8f, 0x90, 0x9f, 0xa0, 0xbf, 0xc0, 0xff] {
                for c in [0, 0x7f, 0x80, 0x8f, 0x90, 0x9f, 0xa0, 0xbf, 0xc0, 0xff] {
                    cases.push(Case::Utf8(vec![0x41, first, a, b, c], 7));
                }
            }
        }
    }
    for value in 0..=u16::MAX {
        cases.push(Case::Utf16(vec![value], 3));
    }
    for surrogate in 0xd800..=0xdfff {
        for next in [
            0, 0x41, 0xd7ff, 0xd800, 0xdbff, 0xdc00, 0xdfff, 0xe000, 0xffff,
        ] {
            cases.push(Case::Utf16(vec![0x41, surrogate, next, 0x42], 6));
        }
    }
    for text in [
        "",
        "ASCII",
        "Latin العربية 日本語\u{301} 😀",
        "\0\u{10ffff}",
    ] {
        let units: Vec<_> = text.encode_utf16().collect();
        for cap in 0..=text.chars().count() + 2 {
            cases.push(Case::Utf8(text.as_bytes().to_vec(), cap));
            cases.push(Case::Utf16(units.clone(), cap));
        }
    }
    for invalid in [
        vec![0xc0, 0x80],
        vec![0xed, 0xa0, 0x80],
        vec![0xf4, 0x90, 0x80, 0x80],
        vec![0xe2, 0x82],
        vec![0xff],
    ] {
        for cap in 0..=4 {
            let mut text = vec![0x41, 0x42];
            text.extend(&invalid);
            cases.push(Case::Utf8(text, cap));
        }
    }
    for invalid in [
        vec![0xd800],
        vec![0xdc00],
        vec![0xd800, 0x41],
        vec![0xdbff, 0xd800],
    ] {
        for cap in 0..=4 {
            let mut text = vec![0x41, 0x42];
            text.extend(&invalid);
            cases.push(Case::Utf16(text, cap));
        }
    }
    // Every original source length, including reversed indices and saturation.
    for length in 0..=u16::MAX {
        cases.push(Case::Scripts(
            vec![
                scalar(0x41, u32::MAX - 31, length, Tag::new(*b"latn")),
                scalar(0x301, 3, 1, Tag::new(*b"DFLT")),
            ],
            4,
        ));
        cases.push(Case::Scripts(
            vec![scalar(0x627, u32::MAX - 31, length, Tag::new(*b"arab"))],
            3,
        ));
    }
    let tags = [
        Tag::new(*b"DFLT"),
        Tag::new(*b"latn"),
        Tag::new(*b"arab"),
        Tag(0),
        Tag(u32::MAX),
    ];
    for mask in 0usize..15625 {
        let mut n = mask;
        let mut input = Vec::new();
        for i in 0..6 {
            input.push(scalar(
                if i % 2 == 0 { 0x20 } else { 0x41 },
                i * 3,
                if i == 5 { 0 } else { 2 },
                tags[n % 5],
            ));
            n /= 5;
        }
        cases.push(Case::Scripts(input, 8));
    }
    for cp in [
        0,
        0x41,
        0x301,
        0xd7ff,
        0xd800,
        0xdfff,
        0xe000,
        0x10ffff,
        0x110000,
        u32::MAX,
    ] {
        for position in 0..3 {
            for cap in 0..=5 {
                let mut v = vec![
                    scalar(0x41, 17, 1, Tag::new(*b"latn")),
                    scalar(0x627, 0, 2, Tag::new(*b"arab")),
                    scalar(0x301, u32::MAX, 65535, Tag::new(*b"DFLT")),
                ];
                v[position].code_point = cp;
                cases.push(Case::Scripts(v, cap));
            }
        }
    }
    for cap in 0..=2 {
        cases.push(Case::Scripts(vec![], cap));
    }
    cases
}
fn emit(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_ne_bytes());
}
fn array<T: bytemuck::Pod>(out: &mut Vec<u8>, v: &[T]) {
    emit(out, v.len() as u32);
    out.extend(bytemuck::cast_slice::<T, u8>(v));
}
fn encode(c: &Case, out: &mut Vec<u8>) {
    match c {
        Case::Properties(v) => {
            emit(out, 0);
            array(out, v);
        }
        Case::Utf8(v, n) => {
            emit(out, 1);
            array(out, v);
            emit(out, *n as u32);
        }
        Case::Utf16(v, n) => {
            emit(out, 2);
            array(out, v);
            emit(out, *n as u32);
        }
        Case::Scripts(v, n) => {
            emit(out, 3);
            array(out, v);
            emit(out, *n as u32);
        }
    }
}
fn result(out: &mut Vec<u8>, r: Result<u32, UnicodeError>, always_count: bool) {
    match r {
        Ok(v) => {
            emit(out, 0);
            emit(out, v);
        }
        Err(e) => {
            emit(out, e as u32);
            if always_count {
                emit(out, 0);
            }
        }
    }
}
fn snapshot(c: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    match c {
        Case::Properties(v) => {
            for &cp in v {
                for n in [
                    script(cp).0,
                    u32::from(canonical_combining_class(cp)),
                    bidi_class(cp) as u32,
                    general_category(cp) as u32,
                    decimal_digit_value(cp).map_or(u32::MAX, u32::from),
                    mirrored_code_point(cp),
                    vertical_code_point(cp),
                ] {
                    emit(&mut out, n);
                }
                let b = bidi_bracket(cp);
                emit(&mut out, u32::from(b.is_some()));
                emit(&mut out, b.map_or(0, |b| b.paired_code_point));
                emit(&mut out, b.map_or(0, |b| b.kind as u32));
            }
            emit(&mut out, infer_script(v).0);
        }
        Case::Utf8(v, n) => {
            result(&mut out, utf8_scalar_count(v), false);
            let mut output = vec![MARKER; *n];
            result(&mut out, decode_utf8_into(v, &mut output), true);
            out.extend(bytemuck::cast_slice::<_, u8>(&output));
        }
        Case::Utf16(v, n) => {
            result(&mut out, utf16_scalar_count(v), false);
            let mut output = vec![MARKER; *n];
            result(&mut out, decode_utf16_into(v, &mut output), true);
            out.extend(bytemuck::cast_slice::<_, u8>(&output));
        }
        Case::Scripts(v, n) => {
            result(&mut out, script_run_count(v), false);
            let mut output = vec![RUN_MARKER; *n];
            result(&mut out, itemize_scripts_into(v, &mut output), true);
            out.extend(bytemuck::cast_slice::<_, u8>(&output));
        }
    }
    out
}
#[test]
fn strict_decoders_preserve_source_ranges_and_transactional_output() {
    assert_eq!(std::mem::size_of::<UnicodeScalar>(), 16);
    assert_eq!(std::mem::offset_of!(UnicodeScalar, script), 12);
    assert_eq!(std::mem::size_of::<ScriptRun>(), 20);
    let input = "Aé\u{301}😀";
    let mut a = [MARKER; 6];
    assert_eq!(decode_utf8_into(input.as_bytes(), &mut a), Ok(4));
    assert_eq!(
        a[..4]
            .iter()
            .map(|v| (v.code_point, v.input_index, v.input_length))
            .collect::<Vec<_>>(),
        [(0x41, 0, 1), (0xe9, 1, 2), (0x301, 3, 2), (0x1f600, 5, 4)]
    );
    assert_eq!(a[2].canonical_combining_class, 230);
    assert_eq!(a[4..], [MARKER; 2]);
    let before = a;
    assert_eq!(
        decode_utf8_into(&[0x41, 0xf4, 0x90, 0x80, 0x80], &mut a[..1]),
        Err(UnicodeError::InvalidEncoding)
    );
    assert_eq!(a, before);
    assert_eq!(
        decode_utf16_into(&[0x41, 0xd800], &mut a[..0]),
        Err(UnicodeError::InvalidEncoding)
    );
    assert_eq!(a, before);
    assert_eq!(
        decode_utf16_into(&[0x41, 0xd83d, 0xde00], &mut a[..1]),
        Err(UnicodeError::Capacity)
    );
    assert_eq!(a, before);
}
#[test]
fn owned_generations_retain_encoding_and_initial_script_attachment() {
    let bytes: std::sync::Arc<[u8]> = b"  A\xcc\x81 \xd8\xa7".as_slice().into();
    let text = DecodedText::from_utf8(bytes.clone()).unwrap();
    match text.source() {
        UnicodeSource::Utf8(v) => assert!(std::sync::Arc::ptr_eq(&bytes, v)),
        _ => panic!("encoding changed"),
    };
    drop(bytes);
    let runs = text.script_runs().unwrap();
    assert_eq!(runs.len(), 2);
    assert_eq!(runs[0].script, Tag::new(*b"latn"));
    assert_eq!(
        (
            runs[0].scalar_count,
            runs[0].input_start,
            runs[0].input_length
        ),
        (5, 0, 6)
    );
    assert_eq!(runs[1].script, Tag::new(*b"arab"));
    let units: Vec<_> = "😀".encode_utf16().collect();
    let text = DecodedText::from_utf16(units).unwrap();
    assert_eq!(text.scalars()[0].input_length, 2);
    assert!(matches!(text.source(),UnicodeSource::Utf16(v) if v.as_ref()==[0xd83d,0xde00]));
    assert_eq!(
        DecodedText::from_utf16(vec![0xd800]).unwrap_err(),
        UnicodeError::InvalidEncoding
    );
}
#[test]
fn raw_script_metadata_and_saturated_reversed_ranges_remain_original() {
    let input = [
        scalar(0x20, u32::MAX - 2, 10, Tag::new(*b"DFLT")),
        scalar(0x41, 4, 0, Tag(0)),
    ];
    let mut runs = [RUN_MARKER; 3];
    assert_eq!(itemize_scripts_into(&input, &mut runs), Ok(1));
    assert_eq!(
        runs[0],
        ScriptRun {
            scalar_start: 0,
            scalar_count: 2,
            input_start: u32::MAX - 2,
            input_length: 0,
            script: Tag(0)
        }
    );
    assert_eq!(runs[1..], [RUN_MARKER; 2]);
    let mut bad = input;
    bad[1].code_point = 0xd800;
    let before = runs;
    assert_eq!(
        itemize_scripts_into(&bad, &mut runs[..0]),
        Err(UnicodeError::InvalidArgument)
    );
    assert_eq!(runs, before);
    assert_eq!(script(0x3042), Tag::new(*b"kana"));
    assert_eq!(script(0xe81), Tag::new(*b"lao "));
    assert_eq!(general_category(0xd800), GeneralCategory::OtherNotAssigned);
    assert_eq!(decimal_digit_value(0x1d7d0), Some(2));
    assert_eq!(bidi_bracket(0x28).unwrap().kind, BidiBracketKind::Open);
}
#[test]
#[ignore = "requires original C++20 sources; mandatory Unicode source qualification"]
fn original_unicode_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-unicode-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("oracle");
    let mut command = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    command
        .args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/src/Text").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_unicode.cpp"));
    for file in [
        "progpu_native_unicode.cpp",
        "Unicode/progpu_native_unicode_category.cpp",
        "Unicode/progpu_native_unicode_directional.cpp",
    ] {
        command.arg(root.join("src/ProGPU.Native/src/Text").join(file));
    }
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let cases = corpus();
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    for c in &cases {
        encode(c, &mut input);
        expected.extend(snapshot(c));
        ends.push(expected.len());
    }
    let path = dir.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = Command::new(exe)
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(actual.status.success());
    if let Some(byte) = actual
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        let index = ends.partition_point(|end| *end <= byte);
        let word = byte & !3;
        let native = u32::from_ne_bytes(actual.stdout[word..word + 4].try_into().unwrap());
        let rust = u32::from_ne_bytes(expected[word..word + 4].try_into().unwrap());
        panic!(
            "Unicode case {index} byte {}: native {native:#010x}, rust {rust:#010x}",
            byte - ends.get(index.wrapping_sub(1)).copied().unwrap_or(0)
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_unicode: {} inputs, {} exact bytes; every Unicode value/property and valid UTF-8/16 scalar",
        cases.len(),
        expected.len()
    );
}

#[test]
#[ignore = "requires .NET 10 SDK and original managed sources; mandatory property control"]
fn original_managed_unicode_properties_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-managed-unicode-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let mut files = vec![
        "ProGPU.Rust/tests/managed_unicode.cs".to_string(),
        "ProGPU.Text.Shaping/ShapingContracts.cs".to_string(),
        "ProGPU.Text.Shaping/ShapingBuffer.cs".to_string(),
        "ProGPU.Text/OpenTypeScriptResolver.cs".to_string(),
        "ProGPU.Text/UnicodeShapingProperties.cs".to_string(),
        "ProGPU.Text/Bidi/BidiTypes.cs".to_string(),
        "ProGPU.Text/Bidi/UnicodeBidiData.Generated.cs".to_string(),
    ];
    // These original dependencies compile the full helper unchanged. The adapter
    // calls only properties above, never foreign-derived syllable machines.
    for name in [
        "UnicodeScript",
        "UnicodeCombiningClass",
        "UnicodeDirectional",
        "UnicodeJoiningFallback",
        "ArabicJoining",
        "IndicShaping",
        "UseShaping",
        "VowelConstraint",
        "IndicSyllableMachine",
        "UseSyllableMachine",
        "MyanmarSyllableMachine",
        "KhmerSyllableMachine",
    ] {
        files.push(format!("ProGPU.Text/{name}Data.Generated.cs"));
    }
    let includes: String = files
        .iter()
        .map(|p| {
            let path = root
                .join("src")
                .join(p)
                .display()
                .to_string()
                .replace('&', "&amp;")
                .replace('"', "&quot;");
            format!("<Compile Include=\"{path}\"/>")
        })
        .collect();
    std::fs::write(dir.join("Control.csproj"), format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>")).unwrap();
    let dotnet = std::env::var_os("PROGPU_RUST_DOTNET").unwrap_or_else(|| "dotnet".into());
    assert!(
        Command::new(&dotnet)
            .current_dir(&dir)
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
    let mut points: Vec<_> = (0..=0x110010u32).collect();
    points.extend([0x7fffffff, 0x80000000, u32::MAX]);
    let path = dir.join("input.bin");
    std::fs::write(&path, bytemuck::cast_slice::<_, u8>(&points)).unwrap();
    let actual = Command::new(dotnet)
        .arg(dir.join("out/Control.dll"))
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(
        actual.status.success(),
        "{}",
        String::from_utf8_lossy(&actual.stderr)
    );
    assert_eq!(actual.stdout.len(), points.len() * 36);
    for (&cp, bytes) in points.iter().zip(actual.stdout.chunks_exact(36)) {
        let bracket = bidi_bracket(cp);
        let expected = [
            script(cp).0,
            u32::from(canonical_combining_class(cp)),
            bidi_class(cp) as u32,
            general_category(cp) as u32,
            mirrored_code_point(cp),
            vertical_code_point(cp),
            u32::from(bracket.is_some()),
            bracket.map_or(0, |b| b.paired_code_point),
            bracket.map_or(0, |b| b.kind as u32),
        ];
        let observed: Vec<_> = bytes
            .chunks_exact(4)
            .map(|v| u32::from_ne_bytes(v.try_into().unwrap()))
            .collect();
        assert_eq!(
            observed, expected,
            "original managed properties for U+{cp:04X}"
        );
    }
    eprintln!(
        "managed_unicode: {} raw values, {} exact bytes from original managed properties",
        points.len(),
        actual.stdout.len()
    );
}
