//! Compare current managed policy and every published metadata field with the
//! actual original method, including malformed sources and UTF-16 identities.
use progpu::font::{FontError, SfntFace, Tag, layout::*};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
};
#[path = "support/selection_fixture.rs"]
mod fixture;
#[path = "support/font_fixture.rs"]
#[allow(dead_code)]
mod font;
fn setting(tag: [u8; 4], value: i32) -> ManagedFeatureSetting {
    ManagedFeatureSetting::new(tag.map(u16::from), value).unwrap()
}
fn text(s: &str) -> Vec<u16> {
    s.encode_utf16().collect()
}
fn emit(b: &mut Vec<u8>, n: u32) {
    b.extend(n.to_le_bytes());
}
fn units(b: &mut Vec<u8>, s: &[u16]) {
    for &v in s {
        b.extend(v.to_le_bytes());
    }
}
fn snapshot(data: &[u8], request: ManagedSelectionRequest<'_>) -> Vec<u8> {
    let mut out = vec![];
    for &c in request.coordinates {
        emit(&mut out, c as i32 as u32);
    }
    let selected = match ManagedLookupSelection::new(data, request) {
        Ok(v) => v,
        Err(FontError::InvalidFace) => {
            emit(&mut out, 1);
            return out;
        }
        Err(e) => panic!("unexpected {e:?}"),
    };
    emit(&mut out, 0);
    emit(&mut out, u32::from(selected.uses_feature_variations()));
    emit(&mut out, selected.lookups().len() as u32);
    for entry in selected.lookups() {
        emit(&mut out, u32::from(entry.index));
        emit(&mut out, entry.value as u32);
        units(&mut out, &entry.tag.map(u16::from));
        emit(&mut out, u32::from(entry.required));
        emit(&mut out, u32::from(entry.digest.is_some()));
        if let Some(d) = entry.digest {
            for m in d.masks() {
                out.extend(m.to_le_bytes());
            }
        }
    }
    out
}
fn source_font(axes: usize) -> Vec<u8> {
    let mut tables = font::tables(vec![]);
    if axes != 0 {
        let mut fvar = vec![0; 16 + axes * 20];
        fixture::put32(&mut fvar, 0, 0x10000);
        fixture::put(&mut fvar, 4, 16);
        fixture::put(&mut fvar, 6, 2);
        fixture::put(&mut fvar, 8, axes as u16);
        fixture::put(&mut fvar, 10, 20);
        fixture::put(&mut fvar, 14, (4 + axes * 4) as u16);
        for i in 0..axes {
            let p = 16 + i * 20;
            fixture::put32(&mut fvar, p, Tag::new(*b"AX00").0 + i as u32);
            fixture::put32(&mut fvar, p + 4, (-65536i32) as u32);
            fixture::put32(&mut fvar, p + 12, 65536);
            fixture::put(&mut fvar, p + 18, 256 + i as u16);
        }
        tables.push((*b"fvar", fvar));
    }
    font::sfnt(&tables, 0)
}
fn policy_table(scripts: &[[u8; 4]], required: u16, allowed: &[u16]) -> Vec<u8> {
    let languages: Vec<_> = [
        *b"TRK ", *b"dflt", *b"ZHH ", *b"ZHT ", *b"ZHS ", *b"AZE ", *b"DEU ", *b"DHV ", *b"FAR ",
        *b"JAN ", *b"NLD ", *b"PLK ", *b"ROM ",
    ]
    .into_iter()
    .enumerate()
    .map(|(i, t)| {
        (
            Tag::new(t),
            fixture::lang((i % 8) as u16, &[(i % 8) as u16]),
        )
    })
    .collect();
    let scripts: Vec<_> = scripts
        .iter()
        .enumerate()
        .map(|(i, &s)| {
            (
                Tag::new(s),
                fixture::script(
                    Some(fixture::lang(
                        if i == 0 { required } else { i as u16 },
                        allowed,
                    )),
                    &languages,
                ),
            )
        })
        .collect();
    fixture::table(
        &scripts,
        &[
            (Tag::new(*b"rlig"), vec![1, 3, 1]),
            (Tag::new(*b"liga"), vec![3, 2, 6]),
            (Tag::new(*b"kern"), vec![2, 4]),
            (Tag::new(*b"rand"), vec![2, 5]),
            (Tag::new(*b"ljmo"), vec![5, 7]),
            (Tag::new(*b"rtlm"), vec![5, 8]),
            (Tag::new(*b"liga"), vec![9, 10]),
            (Tag::new([255, 65, 128, 66]), vec![65535]),
        ],
        12,
        None,
    )
}
fn seeds() -> Vec<Vec<u8>> {
    let mut seeds = fixture::seeds();
    for scripts in [
        vec![*b"latn"],
        vec![*b"dflt"],
        vec![*b"DFLT", *b"DFLT"],
        vec![*b"latn", *b"dflt", *b"DFLT", *b"hang"],
        vec![],
    ] {
        for required in [0, 65535] {
            seeds.push(policy_table(&scripts, required, &[1, 2, 6, 7]));
        }
    }
    // Actual selected lookup digest, including unsorted coverage and reversed
    // ranges admitted only by the original managed digest policy.
    let base = policy_table(&[*b"DFLT"], 0, &[1, 2, 3, 4, 5, 6, 7]);
    for extension in [false, true] {
        for coverage in [
            fixture::words(&[1, 5, 9, 2, 9, 65535, 0]),
            fixture::words(&[2, 3, 100, 99, 0, 4, 8, 900, 6, 10, 1]),
            fixture::words(&[3, 0]),
        ] {
            let mut b = base.clone();
            let list = u16::from_be_bytes([b[8], b[9]]) as usize;
            let lookup = b.len();
            for i in 0..12 {
                fixture::put(&mut b, list + 2 + i * 2, (lookup - list) as u16);
            }
            b.extend(fixture::words(&[if extension { 7 } else { 1 }, 0, 1, 8]));
            if extension {
                b.extend(fixture::words(&[1, 1, 0, 8]));
            }
            b.extend(fixture::words(&[1, 4]));
            b.extend(coverage);
            seeds.push(b);
        }
    }
    // Last duplicate alternate wins, zero substitution still sets uses-variation.
    for records in [
        vec![(None, None)],
        vec![(
            None,
            Some(fixture::variation_substitution(&[
                (1, vec![9]),
                (1, vec![10]),
            ])),
        )],
    ] {
        seeds.push(fixture::table(
            &[(
                Tag::new(*b"DFLT"),
                fixture::script(Some(fixture::lang(65535, &[0, 1])), &[]),
            )],
            &[(Tag::new(*b"rlig"), vec![1]), (Tag::new(*b"liga"), vec![2])],
            12,
            Some(fixture::variations(&records)),
        ));
    }
    seeds
}
#[test]
fn managed_precedence_owned_request_and_source_generation() {
    let data = policy_table(&[*b"DFLT"], 0, &[1, 2, 6, 7]);
    let settings = [
        setting(*b"liga", 2),
        setting(*b"kern", -7),
        setting(*b"rand", 3),
        setting(*b"kern", -7),
        setting(*b"rtlm", i32::MIN),
        setting(*b"ljmo", 11),
        setting(*b"?A?B", i32::MAX),
    ];
    let mut script = text("hang");
    let mut language = text("");
    let mut coordinates = [0, 8192];
    let mut tables = font::tables(vec![]);
    tables.push((*b"GSUB", data));
    let source: Arc<[u8]> = font::sfnt(&tables, 0).into();
    let face = SfntFace::new(source.clone(), 0).unwrap();
    let request = ManagedSelectionRequest {
        script: &script,
        language: Some(&language),
        settings: &settings,
        coordinates: &coordinates,
        ..Default::default()
    };
    let plan = ManagedLookupSelection::from_face(face, Tag::new(*b"GSUB"), request)
        .unwrap()
        .unwrap();
    let retained = plan.clone();
    let pointer = plan.bytes().as_ptr();
    drop(plan);
    drop(source);
    script.fill(0);
    language.push(1);
    coordinates.fill(-1);
    assert_eq!(retained.bytes().as_ptr(), pointer);
    assert!(retained.source().is_some());
    assert_eq!(retained.request().script, text("hang"));
    assert_eq!(retained.request().language, Some(&[][..]));
    assert_eq!(retained.request().coordinates, &[0, 8192]);
    let entries = retained.lookups();
    assert_eq!(
        entries.iter().map(|e| e.index).collect::<Vec<_>>(),
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 65535]
    );
    assert!(entries[0].required);
    assert_eq!(entries[1].tag, *b"rand");
    assert_eq!(entries[1].value, 3);
    assert_eq!(entries[2].value, 1);
    assert!(entries[2].required);
    assert_eq!(entries[4].tag, *b"ljmo");
    assert_eq!(entries[4].value, 11);
    assert_eq!(entries.last().unwrap().value, i32::MAX);
    assert_eq!(
        ManagedFeatureSetting::new([32, 9, 0xa0, 0x3000], 1),
        Err(FontError::InvalidArgument)
    );
    assert!(ManagedFeatureSetting::new([0xd800, 0, 0xffff, 0], -1).is_ok());
}
#[test]
fn managed_and_native_variation_and_fallback_policies_remain_distinct() {
    let settings = [setting(*b"liga", 1)];
    let bytes = policy_table(&[*b"latn"], 65535, &[1]);
    let managed = ManagedLookupSelection::new(
        bytes.clone(),
        ManagedSelectionRequest {
            settings: &settings,
            ..Default::default()
        },
    )
    .unwrap();
    assert_eq!(
        managed
            .lookups()
            .iter()
            .map(|e| e.index)
            .collect::<Vec<_>>(),
        [2, 3, 6]
    );
    let native = LookupSelection::new(
        OwnedLayoutTable::new(bytes).unwrap(),
        SelectionRequest {
            features: &[Tag::new(*b"liga")],
            ..Default::default()
        },
        &[],
    )
    .unwrap();
    assert!(native.lookups().is_empty());
    let bytes = seeds().into_iter().rev().nth(1).unwrap();
    let managed = ManagedLookupSelection::new(
        bytes,
        ManagedSelectionRequest {
            settings: &settings,
            ..Default::default()
        },
    )
    .unwrap();
    assert!(managed.uses_feature_variations());
    assert_eq!(managed.lookups()[0].index, 2);
}
struct Cleanup(PathBuf);
impl Drop for Cleanup {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
#[derive(Default)]
struct Cases {
    input: Vec<u8>,
    expected: Vec<u8>,
    ends: Vec<usize>,
}
impl Cases {
    fn add(&mut self, data: &[u8], request: ManagedSelectionRequest<'_>) {
        for n in [
            data.len() as u32,
            request.script.len() as u32,
            request.language.map_or(u32::MAX, |l| l.len() as u32),
            request.settings.len() as u32,
            request.coordinates.len() as u32,
            u32::from(request.extension_lookup_type),
        ] {
            emit(&mut self.input, n);
        }
        self.input.extend(data);
        units(&mut self.input, request.script);
        if let Some(l) = request.language {
            units(&mut self.input, l);
        }
        for s in request.settings {
            units(&mut self.input, &s.tag());
            emit(&mut self.input, s.value() as u32);
        }
        for c in request.coordinates {
            self.input.extend(c.to_le_bytes());
        }
        self.expected.extend(snapshot(data, request));
        self.ends.push(self.expected.len());
    }
}
#[test]
#[ignore = "requires actual original managed shaper and .NET 10; CI runs explicitly"]
fn original_managed_full_feature_policy_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = Cleanup(
        std::env::temp_dir().join(format!("progpu-managed-features-{}", std::process::id())),
    );
    std::fs::create_dir_all(&dir.0).unwrap();
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"),format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_feature_selection.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let mut cases = Cases::default();
    for axes in 0..3 {
        let font = source_font(axes);
        emit(&mut cases.input, font.len() as u32);
        cases.input.extend(font);
    }
    let seeds = seeds();
    let settings = [
        setting(*b"liga", 2),
        setting(*b"kern", -7),
        setting(*b"rand", 3),
        setting(*b"kern", -7),
        setting(*b"rtlm", i32::MIN),
        setting(*b"ljmo", 11),
        setting(*b"?A?B", i32::MAX),
        setting(*b"liga", 0),
        setting(*b"liga", 2),
    ];
    let languages = [
        None,
        Some(""),
        Some(" "),
        Some("\u{a0}"),
        Some("TR"),
        Some("Az_Latn"),
        Some("zh-Hant-HK"),
        Some("zh-hK"),
        Some("zh_Hant_MO"),
        Some("zh-HANS"),
        Some("zh-tw"),
        Some("tr-TR"),
        Some("de"),
        Some("dv"),
        Some("fa"),
        Some("ja"),
        Some("nl"),
        Some("pl"),
        Some("ro"),
        Some("unknown"),
    ];
    for data in &seeds {
        for script in [
            "DFLT",
            "dflt",
            "latn",
            "hang",
            "hang-long",
            "",
            "a",
            "arab",
            "\u{1044}FLT",
        ] {
            for language in languages {
                let s = text(script);
                let l = language.map(text);
                for coordinates in [&[][..], &[0][..], &[-1, 8192][..]] {
                    for end in [0, 3, 7, 8, 9] {
                        cases.add(
                            data,
                            ManagedSelectionRequest {
                                script: &s,
                                language: l.as_deref(),
                                settings: &settings[..end],
                                coordinates,
                                ..Default::default()
                            },
                        );
                    }
                }
            }
        }
        let request = ManagedSelectionRequest {
            settings: &settings,
            coordinates: &[0, 8192],
            ..Default::default()
        };
        for len in 0..data.len() {
            cases.add(&data[..len], request);
        }
    }
    // Exhaustive UTF-16 language folding witness: only exact original recognized
    // spellings select the ZHH language instead of the original dflt language.
    let language_data = policy_table(&[*b"DFLT"], 65535, &[1]);
    for c in 0..=65535 {
        cases.add(
            &language_data,
            ManagedSelectionRequest {
                settings: &settings,
                language: Some(&[122, 104, 45, 104, c]),
                ..Default::default()
            },
        );
    }
    let mut random = 0xabcd1234u32;
    for i in 0..25000 {
        let mut data = seeds[i % seeds.len()].clone();
        for _ in 0..1 + i % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let at = random as usize % data.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            data[at] = (random >> 24) as u8;
        }
        cases.add(
            &data,
            ManagedSelectionRequest {
                settings: &settings,
                coordinates: &[0, 8192],
                extension_lookup_type: if i % 2 == 0 { 7 } else { 9 },
                ..Default::default()
            },
        );
    }
    // Original managed ReadU16(data, 8) throws after this otherwise admitted
    // LangSys. Rust returns a typed error before publishing an owner.
    cases.add(
        &[0, 1, 0, 0, 0, 0, 0, 0],
        ManagedSelectionRequest::default(),
    );
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
        panic!(
            "original managed feature policy: case {} byte {at}: original={} Rust={}",
            cases.ends.partition_point(|&e| e <= at),
            output.stdout[at],
            cases.expected[at]
        );
    }
    assert_eq!(output.stdout.len(), cases.expected.len());
    eprintln!(
        "original managed feature policy: {} records / {} exact bytes",
        cases.ends.len(),
        cases.expected.len()
    );
}
