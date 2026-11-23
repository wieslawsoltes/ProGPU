//! Original complete bidi levels, caller scratch, source identity and rollback.
use progpu::{
    font::Tag,
    text::{
        read_simple_code_point,
        unicode::{bidi::*, *},
    },
};
const UNIT: BidiUnit = BidiUnit {
    code_point: 0x11111111,
    input_index: 0x22222222,
    input_length: 0x3333,
    original: BidiClass::RightToLeft,
    resolved_type: BidiClass::ArabicLetter,
    level: 7,
    reserved: 0x66,
    matching_isolate: 123,
};
const RUN: BidiLevelRun = BidiLevelRun {
    active_start: 0x11111111,
    active_count: 0x22222222,
    next: 13,
    explicit_level: 9,
    has_predecessor: true,
    reserved0: 0x55,
    reserved1: 0x66,
};
const PAIR: BidiBracketPair = BidiBracketPair {
    open_position: 0x66666666,
    close_position: 0x77777777,
};
const LEVEL: BidiLevel = BidiLevel {
    input_index: 0xf0f0f0f0,
    input_length: 0x1234,
    level: 7,
    reserved: 0xcc,
};
const CLASSES: [u32; 23] = [
    0x41, 0x5d0, 0x627, 0x31, 0x2b, 0x24, 0x661, 0x2c, 0x301, 0xad, 10, 9, 0x20, 0x21, 0x202a,
    0x202d, 0x202b, 0x202e, 0x202c, 0x2066, 0x2067, 0x2068, 0x2069,
];
#[derive(Clone)]
struct Case {
    input: Vec<UnicodeScalar>,
    requested: i8,
    caps: [usize; 5],
}
fn scalar(cp: u32, index: u32, length: u16) -> UnicodeScalar {
    UnicodeScalar {
        code_point: cp,
        input_index: index,
        input_length: length,
        script: Tag(u32::MAX),
        canonical_combining_class: 99,
        reserved: 77,
    }
}
fn from_utf16(text: &[u16], requested: i8) -> Case {
    let mut input = Vec::new();
    let mut i = 0;
    while i < text.len() {
        let (cp, n) = read_simple_code_point(text, i).unwrap();
        input.push(scalar(cp, i as u32, n as u16));
        i += n;
    }
    case(input, requested)
}
fn case(input: Vec<UnicodeScalar>, requested: i8) -> Case {
    let n = input.len();
    Case {
        input,
        requested,
        caps: [n + 2, n * 4 + 2, n + 2, n / 2 + 2, n + 2],
    }
}
fn codes(points: &[u32], requested: i8) -> Case {
    case(
        points
            .iter()
            .enumerate()
            .map(|(i, &cp)| scalar(cp, (i * 3) as u32, 2))
            .collect(),
        requested,
    )
}
fn corpus() -> Vec<Case> {
    let mut result = Vec::new();
    for (i, &cp) in CLASSES.iter().enumerate() {
        assert_eq!(bidi_class(cp) as usize, i);
    }
    for requested in [0, 1, 2] {
        result.push(codes(&[], requested));
        for &a in &CLASSES {
            for &b in &CLASSES {
                for &c in &CLASSES {
                    result.push(codes(&[a, b, c], requested));
                }
            }
        }
        for a in 0..128 {
            for b in 0..128 {
                result.push(from_utf16(&[a, b], requested));
            }
        }
        for text in [
            "abc אבג",
            "אבג \u{2066}(abc 123)\u{2069} דהו",
            "A 12,345+67 $890 אב\n\t",
            "\u{2068}\u{2067}abc\u{2069} אב\u{2069} A",
            "\u{202b}\u{2066}[א]\u{2069}\u{202c}\u{301}",
            "😀 العربية (123) A\u{301}",
        ] {
            result.push(from_utf16(
                &text.encode_utf16().collect::<Vec<_>>(),
                requested,
            ));
        }
        // Every original paired bracket, canonical equivalence and trailing NSMs.
        for cp in 0..=0x10ffff {
            if let Some(pair) = bidi_bracket(cp)
                && pair.kind == BidiBracketKind::Open
            {
                for content in [0x41, 0x5d0, 0x627, 0x31, 0x661, 0x20] {
                    result.push(codes(
                        &[
                            0x5d0,
                            cp,
                            content,
                            pair.paired_code_point,
                            0x301,
                            0x301,
                            0x41,
                        ],
                        requested,
                    ));
                }
            }
        }
        for open in [0x2329, 0x3008] {
            for close in [0x232a, 0x3009] {
                result.push(codes(&[0x5d0, open, 0x41, close, 0x301], requested));
            }
        }
        // Explicit and isolate overflow, reset, unmatched controls and bracket bounds.
        for depth in 0..=260 {
            for &(open, close) in &[
                (0x202a, 0x202c),
                (0x202b, 0x202c),
                (0x202d, 0x202c),
                (0x202e, 0x202c),
                (0x2066, 0x2069),
                (0x2067, 0x2069),
                (0x2068, 0x2069),
                (0x28, 0x29),
            ] {
                let mut v = vec![open; depth];
                v.extend([0x5d0, 0x31, 0x301]);
                v.extend(vec![close; depth]);
                v.extend([0x20, 0x202c, 0x2069, 0x41]);
                result.push(codes(&v, requested));
            }
        }
        for depth in [62, 63, 64, 65, 125, 126, 127, 128, 256] {
            let mut v = vec![0x5d0, 0x28, 0x41, 0x29];
            v.extend(vec![0x28; depth]);
            v.extend([0x5d0, 0x29, 0x301]);
            result.push(codes(&v, requested));
            let mut v = vec![0x2068; depth];
            v.extend(vec![0x202b; depth]);
            v.extend([10, 0x31, 0x2069, 0x202c, 0x5d0]);
            v.extend(vec![0x2069; depth]);
            result.push(codes(&v, requested));
        }
    }
    for requested in i8::MIN..=i8::MAX {
        for v in [
            vec![],
            vec![0x20, 0x5d0, 0x41],
            vec![0x2068, 0x5d0, 0x2069, 0x41],
        ] {
            result.push(codes(&v, requested));
        }
    }
    // All raw source lengths, invalid scalars and metadata remain original inputs.
    for length in 0..=u16::MAX {
        result.push(case(
            vec![
                scalar(0x5d0, u32::MAX, length),
                scalar(0xd800, 0, 0),
                scalar(u32::MAX, 17, length),
                scalar(0x202c, 1, 0),
            ],
            2,
        ));
    }
    for n in 0usize..=16 {
        for slot in 0..5 {
            for cap in [0, n / 2, n.saturating_sub(1), n, n * 4] {
                let mut c = codes(&CLASSES[..n], 2);
                c.caps[slot] = cap;
                result.push(c);
            }
        }
    }
    let mut state = 0x123456789abcdef0u64;
    for i in 0..8192 {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        let len = (state % 97) as usize;
        let mut v = Vec::new();
        for _ in 0..len {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            v.push(if state & 7 == 0 {
                [0x28, 0x29, 0x5b, 0x5d, 0x2329, 0x3009, 0x301, 0x1f600][(state as usize >> 8) & 7]
            } else {
                CLASSES[(state as usize >> 8) % 23]
            });
        }
        result.push(codes(&v, (i % 3) as i8));
    }
    result
}
fn emit(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_ne_bytes());
}
fn encode(c: &Case, out: &mut Vec<u8>) {
    emit(out, c.input.len() as u32);
    out.extend(bytemuck::cast_slice::<_, u8>(&c.input));
    emit(out, c.requested as u32);
    for n in c.caps {
        emit(out, n as u32);
    }
}
fn snapshot(c: &Case) -> Vec<u8> {
    let mut out = Vec::new();
    let r = requirements(c.input.len()).unwrap();
    for v in [
        0,
        r.unit_count,
        r.index_count,
        r.run_count,
        r.bracket_pair_count,
    ] {
        emit(&mut out, v);
    }
    let mut units = vec![UNIT; c.caps[0]];
    let mut indices = vec![0xa5a5a5a5; c.caps[1]];
    let mut runs = vec![RUN; c.caps[2]];
    let mut pairs = vec![PAIR; c.caps[3]];
    let mut output = vec![LEVEL; c.caps[4]];
    match resolve_into(
        &c.input,
        c.requested,
        BidiScratch {
            units: &mut units,
            indices: &mut indices,
            runs: &mut runs,
            bracket_pairs: &mut pairs,
        },
        &mut output,
    ) {
        Ok(r) => {
            emit(&mut out, 0);
            emit(&mut out, r.paragraph_level as u32);
            emit(&mut out, r.written);
        }
        Err(e) => {
            emit(&mut out, e as u32);
            emit(&mut out, u32::from(c.requested == 1));
            emit(&mut out, 0);
        }
    }
    out.extend(bytemuck::cast_slice::<_, u8>(&output));
    for u in units {
        for v in [
            u.code_point,
            u.input_index,
            u32::from(u.input_length),
            u.original as u32,
            u.resolved_type as u32,
            u.level as u32,
            u32::from(u.reserved),
            u.matching_isolate as u32,
        ] {
            emit(&mut out, v);
        }
    }
    for v in indices {
        emit(&mut out, v);
    }
    for r in runs {
        for v in [
            r.active_start,
            r.active_count,
            r.next as u32,
            r.explicit_level as u32,
            u32::from(r.has_predecessor),
            u32::from(r.reserved0),
            u32::from(r.reserved1),
        ] {
            emit(&mut out, v);
        }
    }
    for p in pairs {
        emit(&mut out, p.open_position);
        emit(&mut out, p.close_position);
    }
    out
}
#[test]
fn owned_paragraph_retains_sources_and_original_levels() {
    let text = DecodedText::from_utf16("abc אבג".encode_utf16().collect::<Vec<_>>()).unwrap();
    let p = BidiParagraph::new(text, 0).unwrap();
    assert_eq!(
        p.levels().iter().map(|v| v.level).collect::<Vec<_>>(),
        [0, 0, 0, 0, 1, 1, 1]
    );
    assert_eq!(p.paragraph_level(), 0);
    assert_eq!(p.text().scalars().len(), 7);
    assert_eq!(std::mem::size_of::<BidiLevel>(), 8);
    let p = BidiParagraph::new(p.text().clone(), 1).unwrap();
    assert_eq!(
        p.levels().iter().map(|v| v.level).collect::<Vec<_>>(),
        [2, 2, 2, 1, 1, 1, 1]
    );
    let p = BidiParagraph::new(DecodedText::from_utf8("😀 אב".as_bytes()).unwrap(), -1).unwrap();
    assert_eq!(p.paragraph_level(), 1);
    assert_eq!(p.levels()[0].input_length, 4);
}
#[test]
fn workspace_capacity_rollback_and_raw_admission() {
    let c = case(
        vec![scalar(0xd800, u32::MAX, 65535), scalar(u32::MAX, 0, 0)],
        2,
    );
    let mut small = BidiWorkspace::new(1).unwrap();
    let mut output = [LEVEL; 4];
    assert_eq!(
        small.resolve_into(&c.input, 2, &mut output),
        Err(UnicodeError::Capacity)
    );
    assert_eq!(output, [LEVEL; 4]);
    let mut workspace = BidiWorkspace::new(2).unwrap();
    assert_eq!(
        workspace
            .resolve_into(&c.input, 2, &mut output)
            .unwrap()
            .written,
        2
    );
    assert_eq!(
        output[0],
        BidiLevel {
            input_index: u32::MAX,
            input_length: 65535,
            level: 0,
            reserved: 0
        }
    );
    assert_eq!(output[2..], [LEVEL; 2]);
    assert_eq!(
        requirements((u32::MAX / 4) as usize + 1),
        Err(UnicodeError::InvalidArgument)
    );
}
#[test]
#[ignore = "requires original C++20 sources; complete bidi/scratch differential gate"]
fn original_bidi_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-bidi-oracle-{}", std::process::id()));
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
        .arg(root.join("src/ProGPU.Rust/tests/native_bidi.cpp"))
        .arg(root.join("src/ProGPU.Native/src/Text/progpu_native_unicode.cpp"))
        .arg(root.join("src/ProGPU.Native/src/Text/Bidi/progpu_native_unicode_bidi.cpp"));
    assert!(command.arg("-o").arg(&exe).status().unwrap().success());
    let cases = corpus();
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    for c in &cases {
        encode(c, &mut input);
        expected.extend(
            std::panic::catch_unwind(|| snapshot(c)).unwrap_or_else(|e| {
                eprintln!(
                    "failing bidi request={} points={:?}",
                    c.requested,
                    c.input.iter().map(|s| s.code_point).collect::<Vec<_>>()
                );
                std::panic::resume_unwind(e)
            }),
        );
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
            "bidi case {index}, byte {}: native {native:#x}, rust {rust:#x}",
            byte - ends.get(index.wrapping_sub(1)).copied().unwrap_or(0)
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_bidi: {} cases, {} exact bytes including every scratch field and tail",
        cases.len(),
        expected.len()
    );
}

#[test]
#[ignore = "requires .NET 10 and original Uax9Resolver; managed bidi source control"]
fn original_managed_bidi_results_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-managed-bidi-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let includes: String = [
        "ProGPU.Rust/tests/managed_bidi.cs",
        "ProGPU.Text/Bidi/Uax9Resolver.cs",
        "ProGPU.Text/Bidi/BidiTypes.cs",
        "ProGPU.Text/Bidi/UnicodeBidiData.Generated.cs",
    ]
    .iter()
    .map(|f| {
        let p = root
            .join("src")
            .join(f)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;");
        format!("<Compile Include=\"{p}\"/>")
    })
    .collect();
    std::fs::write(dir.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>")).unwrap();
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
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    for c in corpus() {
        // C# owns its UTF-16 decoding boundary. Recreate the actual encoded input;
        // arbitrary raw native source indices/lengths are qualified independently.
        if c.input.iter().any(|s| s.code_point > 0x10ffff) {
            continue;
        }
        let mut text = Vec::new();
        for s in &c.input {
            if s.code_point <= 0xffff {
                text.push(s.code_point as u16);
            } else {
                let mut pair = [0; 2];
                text.extend_from_slice(
                    char::from_u32(s.code_point)
                        .unwrap()
                        .encode_utf16(&mut pair),
                );
            }
        }
        let decoded = from_utf16(&text, c.requested);
        let mut workspace = BidiWorkspace::new(decoded.input.len()).unwrap();
        let mut levels = vec![BidiLevel::default(); decoded.input.len()];
        let resolved = workspace
            .resolve_into(&decoded.input, c.requested, &mut levels)
            .unwrap();
        emit(&mut input, text.len() as u32);
        input.extend(bytemuck::cast_slice::<_, u8>(&text));
        emit(&mut input, c.requested as u32);
        emit(&mut expected, resolved.paragraph_level as u32);
        emit(&mut expected, resolved.written);
        for level in levels {
            emit(&mut expected, level.input_index);
            emit(&mut expected, u32::from(level.input_length));
            emit(&mut expected, level.level as u32);
        }
        ends.push(expected.len());
    }
    let path = dir.join("input.bin");
    std::fs::write(&path, input).unwrap();
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
    if let Some(byte) = actual
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        let case = ends.partition_point(|end| *end <= byte);
        panic!("managed bidi control {case}, output byte {byte}");
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "managed_bidi: {} controls, {} exact bytes from original Uax9Resolver",
        ends.len(),
        expected.len()
    );
}
