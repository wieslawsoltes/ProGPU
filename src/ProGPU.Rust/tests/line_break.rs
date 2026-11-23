//! Independent original C++/C# controls for line-break classes and rule ordering.
use progpu::{
    font::Tag,
    text::unicode::{line_break::*, *},
};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
fn raw(points: &[u32]) -> Vec<UnicodeScalar> {
    points
        .iter()
        .enumerate()
        .map(|(i, &cp)| UnicodeScalar {
            code_point: cp,
            input_index: u32::MAX.wrapping_sub(i as u32 * 37),
            input_length: if i % 2 == 0 { u16::MAX } else { 0 },
            canonical_combining_class: 77,
            reserved: 99,
            script: Tag(0xdeadbeef),
        })
        .collect()
}
fn emit(out: &mut Vec<u8>, value: u32) {
    out.extend(value.to_le_bytes());
}
struct Cleanup(PathBuf);
impl Drop for Cleanup {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn temp(name: &str) -> Cleanup {
    let p = std::env::temp_dir().join(format!("progpu-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&p).unwrap();
    Cleanup(p)
}
fn compare(actual: &[u8], expected: &[u8], ends: &[usize], name: &str) {
    if let Some(byte) = actual.iter().zip(expected).position(|(a, b)| a != b) {
        let case = ends.partition_point(|&e| e <= byte);
        panic!(
            "{name} case {case}, byte {byte}: native={} rust={}",
            actual[byte], expected[byte]
        );
    }
    assert_eq!(actual.len(), expected.len());
    eprintln!(
        "{name}: {} controls, {} exact bytes",
        ends.len(),
        expected.len()
    );
}

#[test]
fn source_ownership_and_failure_contracts() {
    use LineBreakClass as C;
    use LineBreakKind as B;
    let units: Vec<_> = "A B\r\nC".encode_utf16().collect();
    let text = LineBreakText::new(
        DecodedText::from_utf16(units.clone()).unwrap(),
        LineBreakPolicy::Unicode17,
    )
    .unwrap();
    assert_eq!(
        text.breaks_after(),
        &[
            B::Prohibited,
            B::Opportunity,
            B::Prohibited,
            B::Prohibited,
            B::Mandatory,
            B::Mandatory
        ]
    );
    assert_eq!(
        text.classes(),
        &[
            C::Alphabetic,
            C::Space,
            C::Alphabetic,
            C::CarriageReturn,
            C::LineFeed,
            C::Alphabetic
        ]
    );
    let owned = text.clone();
    drop(text);
    assert_eq!(owned.policy(), LineBreakPolicy::Unicode17);
    match owned.text().source() {
        UnicodeSource::Utf16(v) => assert_eq!(&**v, &units),
        _ => panic!(),
    }
    let input = raw(&[0x41, 0x301, 0xd800]);
    let mut classes = [C::Surrogate; 5];
    let mut breaks = [B::Opportunity; 5];
    assert_eq!(
        resolve_into(
            &input,
            LineBreakPolicy::Unicode17,
            &mut classes[..2],
            &mut breaks
        ),
        Err(UnicodeError::Capacity)
    );
    assert_eq!(classes, [C::Surrogate; 5]);
    assert_eq!(breaks, [B::Opportunity; 5]);
    assert_eq!(
        resolve_into(
            &input,
            LineBreakPolicy::Unicode17,
            &mut classes,
            &mut breaks
        ),
        Err(UnicodeError::InvalidArgument)
    );
    assert_eq!(
        classes,
        [
            C::Alphabetic,
            C::Alphabetic,
            C::Surrogate,
            C::Surrogate,
            C::Surrogate
        ]
    );
    assert_eq!(breaks, [B::Opportunity; 5]);
    let valid = raw(&[0x4e00, 0x200d, 0x4e01]);
    resolve_into(
        &valid,
        LineBreakPolicy::Unicode17,
        &mut classes,
        &mut breaks,
    )
    .unwrap();
    assert_eq!(&breaks[..3], &[B::Prohibited, B::Prohibited, B::Mandatory]);
    resolve_into(
        &valid,
        LineBreakPolicy::EditSelection,
        &mut classes,
        &mut breaks,
    )
    .unwrap();
    assert_eq!(&breaks[..3], &[B::Prohibited, B::Opportunity, B::Mandatory]);
    assert_eq!(&breaks[3..], &[B::Opportunity; 2]);
}
fn run_case(
    input: &mut Vec<u8>,
    expected: &mut Vec<u8>,
    ends: &mut Vec<usize>,
    source: &[UnicodeScalar],
    c: usize,
    b: usize,
    policy: LineBreakPolicy,
) {
    emit(input, 1);
    emit(input, source.len() as u32);
    input.extend(bytemuck::cast_slice::<_, u8>(source));
    emit(input, c as u32);
    emit(input, b as u32);
    emit(input, u32::from(policy == LineBreakPolicy::EditSelection));
    let mut classes = vec![LineBreakClass::Surrogate; c];
    let mut breaks = vec![LineBreakKind::Opportunity; b];
    let result = resolve_into(source, policy, &mut classes, &mut breaks);
    emit(expected, u32::from(result.is_ok()));
    emit(expected, result.err().map_or(0, |e| e as u32));
    expected.extend(classes.iter().map(|c| *c as u8));
    expected.extend(breaks.iter().map(|b| *b as u8));
    expected.extend(bytemuck::cast_slice::<_, u8>(source));
    ends.push(expected.len());
}
fn oracle(managed: bool) {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp(if managed {
        "line-managed"
    } else {
        "line-native"
    });
    let mut command;
    if managed {
        let includes: String = [
            "src/ProGPU.Rust/tests/managed_line_break.cs",
            "src/ProGPU.Text/UnicodeLineBreakResolver.cs",
            "src/ProGPU.Text/UnicodeLineBreakData.Generated.cs",
            "src/ProGPU.Text/UnicodeGraphemeData.Generated.cs",
        ]
        .iter()
        .map(|p| {
            format!(
                "<Compile Include=\"{}\"/>",
                root.join(p)
                    .display()
                    .to_string()
                    .replace('&', "&amp;")
                    .replace('"', "&quot;")
            )
        })
        .collect();
        std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>")).unwrap();
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
        command = Command::new(dotnet);
        command.arg(dir.0.join("out/Control.dll"));
    } else {
        let exe = dir.0.join("oracle");
        let mut cc = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
        cc.args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"]);
        for p in ["src/ProGPU.Native/include", "src/ProGPU.Native/src/Text"] {
            cc.arg(format!("-I{}", root.join(p).display()));
        }
        for p in [
            "src/ProGPU.Rust/tests/native_line_break.cpp",
            "src/ProGPU.Native/src/Text/progpu_native_unicode.cpp",
            "src/ProGPU.Native/src/Text/progpu_native_unicode_grapheme.cpp",
            "src/ProGPU.Native/src/Text/progpu_native_unicode_line_break.cpp",
        ] {
            cc.arg(root.join(p));
        }
        assert!(cc.arg("-o").arg(&exe).status().unwrap().success());
        command = Command::new(exe);
    }
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    let mut representatives = [None; 49];
    for cp in (0..=0x110010).chain([0x7fffffff, u32::MAX]) {
        let class = break_class(cp);
        emit(&mut input, 0);
        emit(&mut input, cp);
        emit(&mut expected, class as u32);
        ends.push(expected.len());
        if char::from_u32(cp).is_some() && representatives[class as usize].is_none() {
            representatives[class as usize] = Some(cp);
        }
    }
    // Every available class plus category/EA/source-base distinctions.
    let mut points: Vec<_> = representatives.into_iter().flatten().collect();
    points.extend([
        0xd800, 0x110000, 0x301, 0xe31, 0x2018, 0x2019, 0x201c, 0x201d, 0x4e00, 0x25cc, 0x1f02c,
    ]);
    let policies = if managed {
        &[LineBreakPolicy::Unicode17][..]
    } else {
        &[LineBreakPolicy::Unicode17, LineBreakPolicy::EditSelection][..]
    };
    let mut case = |source: &[UnicodeScalar], caps: bool| {
        for &policy in policies {
            run_case(
                &mut input,
                &mut expected,
                &mut ends,
                source,
                source.len() + 2,
                source.len() + 3,
                policy,
            );
            if caps {
                for (c, b) in [
                    (0, 0),
                    (source.len().saturating_sub(1), source.len()),
                    (source.len(), source.len().saturating_sub(1)),
                    (source.len(), source.len()),
                ] {
                    run_case(&mut input, &mut expected, &mut ends, source, c, b, policy);
                }
            }
        }
    };
    case(&[], true);
    for &a in &points {
        for &b in &points {
            for &c in &points {
                case(&raw(&[a, b, c]), false);
            }
        }
    }
    for cp in 0..=u16::MAX {
        case(&raw(&[0x41, u32::from(cp), 0x301, 0x4e00]), true);
    }
    for n in [1, 2, 3, 7, 31, 128, 511] {
        for prefix in [
            [0x1f1e6, 0x301],
            [0x20, 0x20],
            [0x2018, 0x301],
            [0x30, 0x2c],
            [0x11003, 0x11046],
            [0xe31, 0x200d],
        ] {
            let mut v = prefix.repeat(n);
            v.extend([0x2019, 0x4e00, 0x1f3fb, 13, 10, 0x25cc]);
            case(&raw(&v), true);
        }
    }
    let mut random = 0x37aa93afu32;
    for _ in 0..8192 {
        random = random.wrapping_mul(1664525).wrapping_add(1013904223);
        let n = (random % 97) as usize;
        let mut v = Vec::new();
        for _ in 0..n {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            v.push(points[random as usize % points.len()]);
        }
        case(&raw(&v), true);
    }
    let path = dir.0.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = command
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(
        actual.status.success(),
        "{}",
        String::from_utf8_lossy(&actual.stderr)
    );
    compare(
        &actual.stdout,
        &expected,
        &ends,
        if managed {
            "managed_line_break"
        } else {
            "native_line_break"
        },
    );
}
#[test]
#[ignore = "requires original C++20 line/grapheme source"]
fn original_native_line_breaks_match() {
    oracle(false);
}
#[test]
#[ignore = "requires .NET 10 and original managed line-break source"]
fn original_managed_line_breaks_match() {
    oracle(true);
}
