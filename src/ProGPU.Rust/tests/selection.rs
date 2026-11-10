//! Original feature selection, including source-specific failure prefixes.
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
fn emit(b: &mut Vec<u8>, v: u32) {
    b.extend(v.to_le_bytes());
}
fn result<T>(b: &mut Vec<u8>, r: Result<T, FontError>) -> Option<T> {
    match r {
        Ok(v) => {
            emit(b, 0);
            Some(v)
        }
        Err(e) => {
            emit(b, e as u32);
            None
        }
    }
}
fn snapshot(
    data: &[u8],
    request: SelectionRequest<'_>,
    excluded: &[Tag],
    feature: Tag,
    target: u16,
    capacity: usize,
) -> Vec<u8> {
    let mut b = vec![];
    let Some(t) = result(&mut b, LayoutTable::parse(data)) else {
        return b;
    };
    let capacity_result = result(&mut b, t.selection_capacity(request));
    emit(&mut b, capacity_result.unwrap_or(0));
    for mode in 0..3 {
        let mut output = vec![0xbeef; capacity];
        let selected = match mode {
            0 => t.select_lookups_into(request, &mut output),
            1 => t.select_lookups_excluding_into(request, excluded, &mut output),
            _ => t.select_feature_into(
                request.script,
                request.language,
                feature,
                request.coordinates,
                &mut output,
            ),
        };
        let written = result(&mut b, selected);
        emit(&mut b, written.unwrap_or(0));
        for v in output {
            emit(&mut b, u32::from(v));
        }
    }
    let value = result(
        &mut b,
        t.feature_contains_lookup(
            request.script,
            request.language,
            feature,
            target,
            request.coordinates,
        ),
    );
    emit(&mut b, u32::from(value.unwrap_or(false)));
    let tag = result(
        &mut b,
        t.required_feature_for_lookup(
            request.script,
            request.language,
            target,
            request.coordinates,
        ),
    )
    .flatten();
    emit(&mut b, u32::from(tag.is_some()));
    emit(&mut b, tag.unwrap_or_default().0);
    b
}
#[test]
fn native_order_required_exclusions_failure_prefix_and_ownership() {
    let bytes = fixture::ordinary(false);
    let t = LayoutTable::parse(&bytes).unwrap();
    let features = [Tag::new(*b"liga"), Tag::new(*b"kern")];
    let request = SelectionRequest {
        features: &features,
        ..Default::default()
    };
    assert_eq!(t.selection_capacity(request).unwrap(), 12);
    let mut out = [0xbeef; 14];
    assert_eq!(t.select_lookups_into(request, &mut out).unwrap(), 6);
    assert_eq!(&out[..6], &[1, 2, 3, 4, 6, 7]);
    assert_eq!(out[6], 0xbeef);
    assert_eq!(
        t.select_feature_into(request.script, request.language, features[0], &[], &mut out)
            .unwrap(),
        4
    );
    assert_eq!(&out[..4], &[7, 6, 1, 2]);
    assert_eq!(
        t.select_lookups_excluding_into(request, &[Tag::new(*b"rlig"), features[0]], &mut out)
            .unwrap(),
        4
    );
    assert_eq!(&out[..4], &[1, 2, 3, 4]);
    assert_eq!(
        t.required_feature_for_lookup(request.script, request.language, 3, &[])
            .unwrap(),
        Some(Tag::new(*b"rlig"))
    );
    assert!(
        !t.feature_contains_lookup(request.script, request.language, Tag::new(*b"rlig"), 3, &[])
            .unwrap()
    );
    assert_eq!(
        t.select_lookups_excluding_into(request, &features, &mut out[..4]),
        Err(FontError::Capacity)
    );
    let mut bad = bytes.clone();
    let feature_list = u16::from_be_bytes([bad[6], bad[7]]) as usize;
    let required =
        feature_list + u16::from_be_bytes([bad[feature_list + 6], bad[feature_list + 7]]) as usize;
    fixture::put(&mut bad, required + 6, 65535);
    let invalid = LayoutTable::parse(&bad).unwrap();
    assert_eq!(invalid.selection_capacity(request).unwrap(), 12);
    out.fill(0xbeef);
    assert_eq!(
        invalid.select_lookups_into(request, &mut out),
        Err(FontError::InvalidFace)
    );
    assert_eq!(out[0], 3);
    assert_eq!(out[1], 0xbeef);
    assert!(LookupSelection::new(OwnedLayoutTable::new(bad).unwrap(), request, &[]).is_err());
    let mut tables = font::tables(vec![]);
    tables.push((*b"GSUB", bytes));
    let source: Arc<[u8]> = font::sfnt(&tables, 0).into();
    let face = SfntFace::new(source.clone(), 0).unwrap();
    let owned = OwnedLayoutTable::from_face(face.clone(), Tag::new(*b"GSUB"))
        .unwrap()
        .unwrap();
    let pointer = owned.view().bytes().as_ptr();
    let plan = LookupSelection::new(owned, request, &features[..1]).unwrap();
    let retained = plan.clone();
    drop(plan);
    drop(face);
    drop(source);
    assert_eq!(retained.table().view().bytes().as_ptr(), pointer);
    assert!(retained.table().source().is_some());
    assert_eq!(retained.lookups(), &[1, 2, 3, 4]);
    assert_eq!(retained.request().features, &features);
    assert_eq!(retained.excluded(), &features[..1]);
    fn threads<T: Send + Sync>() {}
    threads::<LookupSelection>();
}
struct Cleanup(PathBuf);
#[test]
fn owned_coordinates_keep_distinct_selected_variation_generations() {
    let table = OwnedLayoutTable::new(fixture::ordinary(true)).unwrap();
    let mut features = vec![Tag::new(*b"liga"), Tag::new(*b"kern")];
    let mut coordinates = vec![0];
    let first = LookupSelection::new(
        table.clone(),
        SelectionRequest {
            features: &features,
            coordinates: &coordinates,
            ..Default::default()
        },
        &[],
    )
    .unwrap();
    coordinates[0] = -1;
    let second = LookupSelection::new(
        table,
        SelectionRequest {
            features: &features,
            coordinates: &coordinates,
            ..Default::default()
        },
        &[],
    )
    .unwrap();
    features.clear();
    coordinates.clear();
    assert_eq!(first.lookups(), &[1, 2, 4, 6, 7, 8, 9, 10]);
    assert_eq!(second.lookups(), &[1, 2, 3, 4, 5, 6, 7, 11]);
    assert_eq!(first.request().coordinates, &[0]);
    assert_eq!(second.request().coordinates, &[-1]);
    assert_eq!(
        first.request().features,
        &[Tag::new(*b"liga"), Tag::new(*b"kern")]
    );
}
impl Drop for Cleanup {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn temp(name: &str) -> Cleanup {
    let p = std::env::temp_dir().join(format!("progpu-selection-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&p).unwrap();
    Cleanup(p)
}
#[derive(Default)]
struct Cases {
    input: Vec<u8>,
    expected: Vec<u8>,
    ends: Vec<usize>,
}
impl Cases {
    fn add(
        &mut self,
        data: &[u8],
        request: SelectionRequest<'_>,
        excluded: &[Tag],
        feature: Tag,
        target: u16,
        capacity: usize,
    ) {
        for n in [
            data.len() as u32,
            request.script.0,
            request.language.0,
            feature.0,
            u32::from(target),
            capacity as u32,
            request.features.len() as u32,
            excluded.len() as u32,
            request.coordinates.len() as u32,
        ] {
            emit(&mut self.input, n);
        }
        self.input.extend(data);
        for tag in request.features.iter().chain(excluded) {
            emit(&mut self.input, tag.0);
        }
        for &c in request.coordinates {
            emit(&mut self.input, c as i32 as u32);
        }
        self.expected
            .extend(snapshot(data, request, excluded, feature, target, capacity));
        self.ends.push(self.expected.len());
    }
    fn run(&self, dir: &Path, command: &mut Command, name: &str) {
        let input = dir.join("input.bin");
        std::fs::write(&input, &self.input).unwrap();
        let r = command
            .stdin(std::fs::File::open(input).unwrap())
            .stderr(Stdio::inherit())
            .output()
            .unwrap();
        assert!(r.status.success());
        if let Some(at) = r
            .stdout
            .iter()
            .zip(&self.expected)
            .position(|(a, b)| a != b)
        {
            panic!(
                "{name}: case {} byte {at}: original={} Rust={}",
                self.ends.partition_point(|&e| e <= at),
                r.stdout[at],
                self.expected[at]
            );
        }
        assert_eq!(r.stdout.len(), self.expected.len());
        eprintln!(
            "{name}: {} records / {} exact bytes",
            self.ends.len(),
            self.expected.len()
        );
    }
}
#[test]
#[ignore = "requires original C++ compiler; CI runs explicitly"]
fn original_native_selection_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("native");
    let exe = dir.0.join("oracle");
    let mut cc = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    cc.args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ));
    for p in [
        "src/ProGPU.Rust/tests/native_selection.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp",
        "src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_features.cpp",
    ] {
        cc.arg(root.join(p));
    }
    assert!(cc.arg("-o").arg(&exe).status().unwrap().success());
    let mut cases = Cases::default();
    let seeds = fixture::seeds();
    let features = [
        Tag::new(*b"liga"),
        Tag::new(*b"kern"),
        Tag::new(*b"rlig"),
        Tag(0xffffffff),
    ];
    for bytes in &seeds {
        for script in [*b"DFLT", *b"latn", *b"dflt", *b"arab"] {
            for language in [Tag(0), Tag::new(*b"TRK "), Tag::new(*b"NOPE")] {
                for coordinates in [&[][..], &[0][..], &[-16384, 8192][..], &[1, -1, 32767][..]] {
                    let request = SelectionRequest {
                        script: Tag::new(script),
                        language,
                        features: &features,
                        coordinates,
                    };
                    for (target, capacity) in
                        [(0, 0), (1, 1), (3, 4), (11, 12), (12, 14), (65535, 16)]
                    {
                        cases.add(
                            bytes,
                            request,
                            &features[..1],
                            features[0],
                            target,
                            capacity,
                        );
                    }
                }
            }
        }
        let request = SelectionRequest {
            features: &features,
            coordinates: &[0, 8192],
            ..Default::default()
        };
        for len in 0..bytes.len() {
            cases.add(&bytes[..len], request, &features, features[0], 0, 14);
        }
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
            SelectionRequest {
                features: &features,
                coordinates: &[0, 8192],
                ..Default::default()
            },
            &features[..1],
            features[i % features.len()],
            (i % 16) as u16,
            14,
        );
    }
    cases.run(&dir.0, &mut Command::new(exe), "original native selection");
}

fn managed_font(data: &[u8], axes: usize) -> Vec<u8> {
    let mut tables = font::tables(vec![]);
    tables.push((*b"GSUB", data.to_vec()));
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
#[test]
#[ignore = "requires actual original managed shaper and .NET 10; CI runs explicitly"]
fn original_managed_common_selection_differential() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("managed");
    let xml = |p: &str| {
        root.join(p)
            .display()
            .to_string()
            .replace('&', "&amp;")
            .replace('"', "&quot;")
    };
    std::fs::write(dir.0.join("Control.csproj"), format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{}\"/><ProjectReference Include=\"{}\"/></ItemGroup></Project>",xml("src/ProGPU.Rust/tests/managed_selection.cs"),xml("src/ProGPU.Text/ProGPU.Text.csproj"))).unwrap();
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
    let tags = [
        Tag::new(*b"liga"),
        Tag::new(*b"kern"),
        Tag::new(*b"rlig"),
        Tag::new(*b"NOPE"),
    ];
    // Compare the full original managed enabled-lookup method on the shared
    // admitted policy. Its extra fallback/global/value policies are independent.
    for varied in [false, true] {
        let data = fixture::ordinary(varied);
        for script in [*b"DFLT", *b"latn", *b"arab"] {
            for language in [Tag(0), Tag::new(*b"TRK ")] {
                for coordinates in [
                    vec![],
                    vec![-16384],
                    vec![-1],
                    vec![0],
                    vec![1],
                    vec![8192],
                    vec![8193],
                    vec![16384],
                    vec![0, 8192],
                ] {
                    let font = managed_font(&data, coordinates.len());
                    for bits in 0..16 {
                        let features: Vec<_> = tags
                            .iter()
                            .enumerate()
                            .filter_map(|(i, &t)| (bits & (1 << i) != 0).then_some(t))
                            .collect();
                        for n in [
                            font.len() as u32,
                            data.len() as u32,
                            Tag::new(script).0,
                            language.0,
                            features.len() as u32,
                            coordinates.len() as u32,
                        ] {
                            emit(&mut cases.input, n);
                        }
                        cases.input.extend(&font);
                        cases.input.extend(&data);
                        for tag in &features {
                            emit(&mut cases.input, tag.0);
                        }
                        for &c in &coordinates {
                            emit(&mut cases.input, c as i32 as u32);
                            emit(&mut cases.expected, c as i32 as u32);
                        }
                        let t = LayoutTable::parse(&data).unwrap();
                        let request = SelectionRequest {
                            script: Tag::new(script),
                            language,
                            features: &features,
                            coordinates: &coordinates,
                        };
                        let mut out = vec![0; t.selection_capacity(request).unwrap() as usize];
                        let written = t.select_lookups_into(request, &mut out).unwrap();
                        emit(&mut cases.expected, written);
                        for &v in &out[..written as usize] {
                            emit(&mut cases.expected, u32::from(v));
                        }
                        cases.ends.push(cases.expected.len());
                    }
                }
            }
        }
    }
    cases.run(
        &dir.0,
        Command::new(dotnet).arg(dir.0.join("artifacts/bin/Control/release/Control.dll")),
        "original managed common selection",
    );
}
