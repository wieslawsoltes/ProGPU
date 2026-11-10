//! Complete original managed name selection, including actual runtime decoding.
use progpu::font::SfntFace;
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
#[path = "support/names_fixture.rs"]
mod fixture;
#[path = "support/name_replay.rs"]
mod replay;
#[path = "support/managed_variations_fixture.rs"]
mod variations;
use fixture::Record;
use replay::{Case, emit};
use variations::font;

fn case(font: Vec<u8>) -> Case {
    Case {
        font,
        ids: vec![1],
        axes: false,
    }
}
#[test]
fn names_keep_rank_order_deduplicate_and_own_selected_bytes() {
    let records = vec![
        Record::utf16(3, 0x409, 1, &[0x3b1]),
        Record::utf16(0, 0, 1, &[65]),
        Record::utf16(3, 0x409, 1, &[32, 0, 66, 0, 32]),
        Record::new(1, 0, 0, 1, vec![65]),
        Record::utf16(3, 0x409, 1, &[67]),
    ];
    let face = SfntFace::new(fixture::sfnt(&records), 0).unwrap();
    let result = face.managed_names(1);
    drop(face);
    assert_eq!(
        result.iter().map(|s| s.as_ref()).collect::<Vec<_>>(),
        ["B", "C", "A", "α"]
    );
    let mut tables = variations::tables(2, 1);
    tables.push((*b"name", fixture::table(&records)));
    let face = SfntFace::new(font::sfnt(&tables, 0), 0).unwrap();
    let data = progpu::font::ManagedVariationData::from_named_face(face)
        .unwrap()
        .unwrap();
    assert_eq!(data.axes()[0].name.as_ref(), [66]);
    assert_eq!(data.axes()[1].name.as_ref(), [119, 100, 116, 104]);
}

fn corpus() -> (Vec<Case>, Vec<usize>) {
    let mut cases = Vec::new();
    let mut browser = Vec::new();
    // Every original UTF-16 Char is independently witnessed for decoding,
    // whitespace, and the original >024F letter-ranking boundary.
    for c in 0..=u16::MAX {
        if [
            0, 9, 13, 0x85, 0xa0, 0x24f, 0x250, 0x370, 0x600, 0x1680, 0x2000, 0x2028, 0x2060,
            0x3000, 0xd800, 0xdc00, 0xfffe,
        ]
        .contains(&c)
        {
            browser.push(cases.len());
        }
        cases.push(case(fixture::sfnt(&[
            Record::utf16(0, 0, 1, &[65, 83, 67, 73, 73]),
            Record::utf16(3, 0x409, 1, &[32, 0, c, 0, 32]),
        ])));
    }
    // Exhaust every two-byte sequence through both actual UTF-8 replacement
    // decoding and the source's Latin1 branch (not a MacRoman substitution).
    for v in 0..=u16::MAX {
        let b = v.to_be_bytes().to_vec();
        cases.push(case(fixture::sfnt(&[Record::new(2, 0, 0, 1, b.clone())])));
        cases.push(case(fixture::sfnt(&[Record::new(1, 0, 0, 1, b)])));
    }
    for b in 0..=u8::MAX {
        for (platform, encoding) in [(0, 0), (3, 10), (1, 0), (1, 1), (2, 0)] {
            cases.push(case(fixture::sfnt(&[Record::new(
                platform,
                encoding,
                0,
                1,
                vec![b],
            )])));
        }
    }
    let special: Vec<Vec<u8>> = vec![
        vec![0xd8, 0, 0],
        vec![0xd8, 0, 0xdc, 0],
        vec![0xd8, 0, 0xd8, 0],
        vec![0xdc, 0, 0xd8, 0],
        vec![0xd8, 0x01, 0xdc, 0],
        vec![0xfe, 0xff, 0, 65],
        vec![0, 65, 0xfe, 0xff],
        vec![0xef, 0xbb, 0xbf, 65],
        vec![0xf4, 0x8f, 0xbf, 0xbf],
        vec![0xf4, 0x90, 0x80, 0x80],
        vec![0xed, 0xa0, 0x80],
        vec![0xe0, 0x80, 0x80],
        vec![0xf0, 0x80, 0x80, 0x80],
        vec![0xe2, 0x82],
        vec![0xe2, 0x82, 65],
        vec![0xf0, 0x9f, 0x92],
        vec![0xf0, 0x9f, 0x92, 65],
    ];
    for b in special {
        for (platform, encoding) in [(0, 0), (3, 10), (1, 0), (1, 1), (2, 0)] {
            if platform == 0 || platform == 2 {
                browser.push(cases.len());
            }
            cases.push(case(fixture::sfnt(&[
                Record::new(platform, encoding, 0x409, 1, b.clone()),
                Record::utf16(0, 0, 1, &[65]),
            ])));
        }
    }
    let mut rng = 0x6a195dc1u32;
    let mut next = || {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        rng
    };
    for n in 0..30000 {
        let len = next() as usize % 33;
        let b = (0..len).map(|_| next() as u8).collect();
        let platform = (n % 5) as u16;
        let encoding = (n % 3) as u16;
        cases.push(case(fixture::sfnt(&[Record::new(
            platform,
            encoding,
            next() as u16,
            1,
            b,
        )])));
    }
    let original = fixture::table(&[
        Record::utf16(3, 0x409, 1, &[65, 0, 66]),
        Record::new(1, 0, 0, 1, vec![0xc0, 0x85, 66]),
        Record::utf16(3, 0x411, 1, &[0x4e00]),
        Record::utf16(0, 0, 1, &[65, 66]),
        Record::utf16(0, 0, 2, &[67]),
        Record::new(2, 3, 7, 1, vec![0xf0, 0x90, 0x90, 0x80]),
    ]);
    for n in 0..8000 {
        let mut t = original.clone();
        for _ in 0..1 + next() % 4 {
            let at = next() as usize % t.len();
            t[at] ^= next() as u8;
        }
        if n % 3 == 0 {
            t.truncate(next() as usize % (t.len() + 1));
        }
        if n < 16 {
            browser.push(cases.len());
        }
        cases.push(Case {
            font: font::sfnt(&[(*b"name", t)], 0),
            ids: vec![1, 2, 65535],
            axes: false,
        });
    }
    // Header format is not an admission gate; offsets may point into records.
    for format in [0, 1, 2, 65535] {
        for offset in [0, 1, 5, 6, 7, 20, 65535] {
            let mut t = original.clone();
            font::u16_at(&mut t, 0, format);
            font::u16_at(&mut t, 4, offset);
            cases.push(case(font::sfnt(&[(*b"name", t)], 0)));
        }
    }
    for len in 0..original.len() {
        cases.push(case(font::sfnt(&[(*b"name", original[..len].to_vec())], 0)));
    }
    cases.push(case(font::sfnt(&[], 0)));
    let many = vec![0; 6 + usize::from(u16::MAX) * 12];
    let mut many = many;
    font::u16_at(&mut many, 2, u16::MAX);
    font::u16_at(&mut many, 4, 0);
    cases.push(Case {
        font: font::sfnt(&[(*b"name", many)], 0),
        ids: vec![0, 1],
        axes: false,
    });
    for invalid in [false, true] {
        let mut f = font::sfnt(
            &[
                (*b"name", original.clone()),
                (
                    *b"name",
                    fixture::table(&[Record::utf16(3, 0x409, 1, &[90])]),
                ),
            ],
            0,
        );
        if invalid {
            font::u32_at(&mut f, 12 + 16 + 8, u32::MAX);
        }
        browser.push(cases.len());
        cases.push(case(f));
    }
    // Actual TtfFont owns the same decoded name resolver for variation axes.
    for axes in 1..=3 {
        for variant in 0..8 {
            let mut ts = variations::tables(axes, variant);
            let records = (0..axes)
                .filter(|i| variant % 4 != 0 || i % 2 != 0)
                .flat_map(|i| {
                    [
                        Record::utf16(3, 0x409, 1 + i as u16, &[0x3b1 + i as u16]),
                        Record::utf16(0, 0, 1 + i as u16, &[32, 0, 65 + i as u16, 0, 32]),
                    ]
                })
                .collect::<Vec<_>>();
            ts.push((*b"name", fixture::table(&records)));
            browser.push(cases.len());
            cases.push(Case {
                font: font::sfnt(&ts, 0),
                ids: vec![1, 2, 3],
                axes: true,
            });
        }
    }
    (cases, browser)
}
struct Temp(PathBuf);
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

#[test]
#[ignore = "requires .NET 10 and actual managed font-name source"]
fn original_managed_names_and_variation_axis_resolution() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir =
        Temp(std::env::temp_dir().join(format!("progpu-managed-names-{}", std::process::id())));
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_names.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let (cases, browser) = corpus();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ranges = Vec::new();
    for c in &cases {
        c.write(&mut input);
        let start = expected.len();
        expected.extend(c.expected().unwrap());
        ranges.push(start..expected.len());
    }
    let input_path = dir.0.join("input.bin");
    std::fs::write(&input_path, &input).unwrap();
    drop(input);
    let output = Command::new(&dotnet)
        .arg(dir.0.join("artifacts/bin/Control/release/Control.dll"))
        .stdin(std::fs::File::open(input_path).unwrap())
        .stdout(Stdio::piped())
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    if output.stdout != expected {
        let at = output
            .stdout
            .iter()
            .zip(&expected)
            .position(|(a, b)| a != b)
            .unwrap_or(output.stdout.len().min(expected.len()));
        let index = ranges
            .iter()
            .position(|r| r.contains(&at))
            .unwrap_or(cases.len());
        panic!(
            "name oracle mismatch case {index}, byte {at}: source {:?}, Rust {:?}; lengths {} vs {}",
            output.stdout.get(at..(at + 32).min(output.stdout.len())),
            expected.get(at..(at + 32).min(expected.len())),
            output.stdout.len(),
            expected.len()
        );
    }
    let mut fixture = b"PNM1".to_vec();
    emit(&mut fixture, browser.len() as u32);
    for &i in &browser {
        let c = &cases[i];
        c.write(&mut fixture);
        let captured = &output.stdout[ranges[i].clone()];
        emit(&mut fixture, captured.len() as u32);
        fixture.extend(captured);
    }
    let fixture_path = root.join("src/ProGPU.Rust/browser/managed-names.bin");
    if std::env::var_os("PROGPU_RUST_UPDATE_NAME_FIXTURE").is_some() {
        std::fs::write(&fixture_path, &fixture).unwrap();
    } else {
        assert_eq!(std::fs::read(fixture_path).unwrap(), fixture);
    }
    println!(
        "Original managed names: {} cases, {} exact bytes; {} browser cases, {} bytes",
        cases.len(),
        output.stdout.len(),
        browser.len(),
        fixture.len()
    );
}
