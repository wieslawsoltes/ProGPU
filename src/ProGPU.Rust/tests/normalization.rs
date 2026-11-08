//! Compare original plan admission, lookup and every normalized/source/tail byte.
use progpu::{
    font::Tag,
    text::unicode::{normalization::*, *},
};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
const SENTINEL: UnicodeScalar = UnicodeScalar {
    code_point: 0x33333333,
    input_index: 0x44444444,
    input_length: 0x5555,
    canonical_combining_class: 0x66,
    reserved: 0x77,
    script: Tag(0x88888888),
};

#[test]
fn source_profiles_preserve_original_metadata_and_atomic_failures() {
    use ManagedNormalizationProfile::*;
    use NormalizationForm::*;
    let run = |points: &[u32], profile, form| {
        let text: String = points
            .iter()
            .map(|&cp| char::from_u32(cp).unwrap())
            .collect();
        NormalizedText::with_managed_profile(
            DecodedText::from_utf16(text.encode_utf16().collect::<Vec<_>>()).unwrap(),
            profile,
            form,
        )
        .unwrap()
    };
    assert_eq!(
        run(&[0x105c9], Unicode15_1, CanonicalDecomposition).scalars()[0].code_point,
        0x105c9
    );
    for p in [Unicode16_0, Unicode17_0] {
        assert_eq!(
            run(&[0x105c9], p, CanonicalDecomposition)
                .scalars()
                .iter()
                .map(|s| s.code_point)
                .collect::<Vec<_>>(),
            [0x105d2, 0x307]
        );
    }
    let old = run(&[0x61, 0x1acf, 0x334], Unicode16_0, CanonicalDecomposition);
    assert_eq!(
        old.scalars()
            .iter()
            .map(|s| s.code_point)
            .collect::<Vec<_>>(),
        [0x61, 0x1acf, 0x334]
    );
    assert_eq!(old.scalars()[1].canonical_combining_class, 230);
    assert_eq!(Unicode16_0.canonical_class(0x1acf), 0);
    let new = run(&[0x61, 0x1acf, 0x334], Unicode17_0, CanonicalDecomposition);
    assert_eq!(
        new.scalars()
            .iter()
            .map(|s| s.code_point)
            .collect::<Vec<_>>(),
        [0x61, 0x334, 0x1acf]
    );
    assert_eq!(new.scalars()[1].input_index, 2);
    assert_eq!(new.scalars()[2].input_index, 1);
    let retained = new.clone();
    drop(new);
    assert_eq!(retained.managed_profile(), Some(Unicode17_0));
    for p in [Unicode15_1, Unicode16_0, Unicode17_0] {
        let mut output = [SENTINEL; 5];
        assert_eq!(
            normalize_managed_profile_into(
                &raw(&[0x41, 0xfffe]),
                p,
                CanonicalComposition,
                &mut output[..0]
            ),
            Err(UnicodeError::InvalidArgument)
        );
        assert_eq!(output, [SENTINEL; 5]);
        assert_eq!(
            normalize_managed_profile_into(
                &raw(&[0xc5]),
                p,
                CanonicalDecomposition,
                &mut output[..1]
            ),
            Err(UnicodeError::Capacity)
        );
        assert_eq!(output, [SENTINEL; 5]);
        assert_eq!(
            normalize_managed_profile_into(
                &raw(&[0x41, 0x301]),
                p,
                CanonicalComposition,
                &mut output
            ),
            Ok(1)
        );
        assert_eq!(output[0].code_point, 0xc1);
        assert_eq!(output[0].input_length, u16::MAX);
        assert_eq!(output[1].code_point, 0x301);
        assert_eq!(output[2..], [SENTINEL; 3]);
    }
    assert_eq!(
        ManagedNormalizationProfile::try_from(0),
        Err(UnicodeError::InvalidArgument)
    );
    assert_eq!(
        NormalizedText::new_managed(
            DecodedText::from_utf16(vec![0xd801, 0xddc9]).unwrap(),
            CanonicalDecomposition
        )
        .unwrap()
        .scalars()[0]
            .code_point,
        0x105d2
    );
}
fn raw(points: &[u32]) -> Vec<UnicodeScalar> {
    points
        .iter()
        .enumerate()
        .map(|(i, &cp)| UnicodeScalar {
            code_point: cp,
            input_index: if i % 2 == 0 {
                u32::MAX - i as u32
            } else {
                i as u32
            },
            input_length: (cp.wrapping_mul(31)) as u16,
            canonical_combining_class: 99,
            reserved: 77,
            script: Tag(u32::MAX),
        })
        .collect()
}
fn emit(out: &mut Vec<u8>, n: u32) {
    out.extend(n.to_le_bytes());
}
fn plan(records: &[[u32; 3]], scalars: &[u32], pairs: &[[u32; 3]]) -> Vec<u8> {
    let mut b = Vec::new();
    for n in [
        0x4e554750,
        1,
        records.len() as u32,
        scalars.len() as u32,
        pairs.len() as u32,
    ] {
        emit(&mut b, n);
    }
    for r in records {
        for &n in r {
            emit(&mut b, n);
        }
    }
    for &n in scalars {
        emit(&mut b, n);
    }
    for r in pairs {
        for &n in r {
            emit(&mut b, n);
        }
    }
    b
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
fn original_owned_source_and_intermediate_tail() {
    let data = default_data().unwrap();
    assert_eq!(data.bytes().len(), 450920);
    assert_eq!(data.compose(0x41, 0x301), Some(0xc1));
    let source = raw(&[0x41, 0x301]);
    let mut output = [SENTINEL; 4];
    assert_eq!(
        normalize_into(
            &source,
            data,
            NormalizationForm::CanonicalComposition,
            &mut output
        ),
        Ok(1)
    );
    assert_eq!(output[0].code_point, 0xc1);
    assert_eq!(output[0].input_index, 1);
    assert_eq!(output[0].input_length, u16::MAX);
    assert_eq!(output[1].code_point, 0x301);
    assert_eq!(&output[2..], &[SENTINEL; 2]);
    let text = NormalizedText::new(
        DecodedText::from_utf16(vec![0x41, 0x301]).unwrap(),
        NormalizationForm::CanonicalComposition,
    )
    .unwrap();
    let cloned = text.clone();
    drop(text);
    assert_eq!(cloned.scalars()[0].input_length, 2);
    assert_eq!(cloned.form(), NormalizationForm::CanonicalComposition);
    match cloned.text().source() {
        UnicodeSource::Utf16(v) => assert_eq!(&**v, &[0x41, 0x301]),
        _ => panic!(),
    }
    let bytes = plan(&[[0x41, 0, 0], [0x42, 0, 1]], &[0x41, 0xd800], &[]);
    let owned = NormalizationPlan::new(bytes).unwrap();
    let cloned = owned.clone();
    drop(owned);
    assert_eq!(cloned.data().decomposition(0x41), Some(&[][..]));
    assert_eq!(cloned.data().decomposition(0x43), None);
    let result = NormalizedText::with_data(
        DecodedText::from_utf8(b"AB".to_vec()).unwrap(),
        &cloned.data(),
        NormalizationForm::CanonicalDecomposition,
    )
    .unwrap();
    drop(cloned);
    assert_eq!(result.scalars().len(), 1);
    assert_eq!(result.scalars()[0].code_point, 0x41);
    assert_eq!(result.scalars()[0].input_index, 1);
    let mut output = [SENTINEL; 2];
    assert_eq!(
        normalize_into(
            &raw(&[0xc5, 0xd800]),
            data,
            NormalizationForm::CanonicalDecomposition,
            &mut output[..0]
        ),
        Err(UnicodeError::InvalidArgument)
    );
    assert_eq!(output, [SENTINEL; 2]);
    assert_eq!(
        normalize_into(
            &raw(&[0xc5]),
            data,
            NormalizationForm::CanonicalComposition,
            &mut output[..1]
        ),
        Err(UnicodeError::Capacity)
    );
    assert_eq!(output, [SENTINEL; 2]);
    assert_eq!(
        NormalizationForm::try_from(255),
        Err(UnicodeError::InvalidArgument)
    );
}
fn normalized(
    input: &mut Vec<u8>,
    expected: &mut Vec<u8>,
    ends: &mut Vec<usize>,
    source: &[UnicodeScalar],
    data: &NormalizationData<'_>,
    cap: usize,
    form: u8,
) {
    emit(input, 2);
    emit(input, source.len() as u32);
    input.extend(bytemuck::cast_slice::<_, u8>(source));
    emit(input, cap as u32);
    emit(input, u32::from(form));
    let req = scalar_capacity(source, data);
    emit(expected, u32::from(req.is_ok()));
    emit(expected, req.err().map_or(0, |e| e as u32));
    emit(expected, req.unwrap_or(0));
    let mut output = vec![SENTINEL; cap];
    let result = NormalizationForm::try_from(form)
        .and_then(|f| normalize_into(source, data, f, &mut output));
    emit(expected, u32::from(result.is_ok()));
    emit(expected, result.err().map_or(0, |e| e as u32));
    emit(expected, result.unwrap_or(0));
    expected.extend(bytemuck::cast_slice::<_, u8>(&output));
    expected.extend(bytemuck::cast_slice::<_, u8>(source));
    ends.push(expected.len());
}
fn lookup(
    input: &mut Vec<u8>,
    expected: &mut Vec<u8>,
    ends: &mut Vec<usize>,
    data: &NormalizationData<'_>,
    cp: u32,
    a: u32,
    b: u32,
) {
    emit(input, 1);
    for n in [cp, a, b] {
        emit(input, n);
    }
    let d = data.decomposition(cp);
    emit(expected, u32::from(d.is_some()));
    emit(expected, d.map_or(0, |s| s.len() as u32));
    expected.extend(d.unwrap_or(&[]));
    let c = data.compose(a, b);
    emit(expected, u32::from(c.is_some()));
    emit(expected, c.unwrap_or(0));
    ends.push(expected.len());
}
fn load(input: &mut Vec<u8>, expected: &mut Vec<u8>, ends: &mut Vec<usize>, bytes: &[u8]) {
    emit(input, 0);
    emit(input, bytes.len() as u32);
    input.extend(bytes);
    let r = NormalizationData::parse(bytes);
    emit(expected, u32::from(r.is_ok()));
    emit(expected, r.err().map_or(0, |e| e as u32));
    ends.push(expected.len());
}
#[test]
#[ignore = "requires original C++20 normalization and Unicode source"]
fn original_native_normalization_matches() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("normalization-native");
    let exe = dir.0.join("oracle");
    let mut cc = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    cc.args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"]);
    for p in ["src/ProGPU.Native/include", "src/ProGPU.Native/src/Text"] {
        cc.arg(format!("-I{}", root.join(p).display()));
    }
    for p in [
        "src/ProGPU.Rust/tests/native_normalization.cpp",
        "src/ProGPU.Native/src/Text/progpu_native_unicode.cpp",
        "src/ProGPU.Native/src/Text/progpu_native_unicode_normalization.cpp",
    ] {
        cc.arg(root.join(p));
    }
    assert!(cc.arg("-o").arg(&exe).status().unwrap().success());
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    let data = default_data().unwrap();
    load(&mut input, &mut expected, &mut ends, data.bytes());
    for cp in (0..=0x110010).chain([0x7fffffff, u32::MAX]) {
        lookup(&mut input, &mut expected, &mut ends, data, cp, 0x41, cp);
        let source = raw(&[cp]);
        let cap = scalar_capacity(&source, data).unwrap_or(0) as usize + 2;
        for form in [0, 1] {
            normalized(
                &mut input,
                &mut expected,
                &mut ends,
                &source,
                data,
                cap,
                form,
            );
        }
    }
    // Every original composition pair is queried, normalized and reversed.
    let bytes = data.bytes();
    let u = |i| u32::from_le_bytes(bytes[i..i + 4].try_into().unwrap()) as usize;
    let start = 20 + u(8) * 12 + u(12) * 4;
    for i in 0..u(16) {
        let offset = start + i * 12;
        let a = u(offset) as u32;
        let b = u(offset + 4) as u32;
        let c = u(offset + 8) as u32;
        for (a, b) in [(a, b), (b, a)] {
            lookup(&mut input, &mut expected, &mut ends, data, c, a, b);
            let source = raw(&[a, b]);
            let cap = scalar_capacity(&source, data).unwrap() as usize;
            for form in [0, 1, 2, 255] {
                for n in [cap.saturating_sub(1), cap, cap + 2] {
                    normalized(&mut input, &mut expected, &mut ends, &source, data, n, form);
                }
            }
        }
    }
    let points = [
        0x41, 0xc5, 0x212b, 0x301, 0x315, 0x300, 0x327, 0x1100, 0x1161, 0x11a8, 0xac01, 0x1d15e,
        0xd800, 0x110000,
    ];
    for &a in &points {
        for &b in &points {
            for &c in &points {
                let source = raw(&[a, b, c]);
                let cap = scalar_capacity(&source, data).unwrap_or(0) as usize + 1;
                for form in [0, 1] {
                    normalized(
                        &mut input,
                        &mut expected,
                        &mut ends,
                        &source,
                        data,
                        cap,
                        form,
                    );
                }
            }
        }
    }
    for n in [0, 1, 2, 17, 127, 511] {
        let source = raw(&[0x41, 0x315, 0x301, 0x327].repeat(n));
        let cap = scalar_capacity(&source, data).unwrap() as usize + 2;
        for form in [0, 1] {
            normalized(
                &mut input,
                &mut expected,
                &mut ends,
                &source,
                data,
                cap,
                form,
            );
        }
    }
    let seed = plan(
        &[[0x41, 0, 0], [0x42, 0, 2], [0x43, 1, 1]],
        &[0x301, 0x41, 0xd800],
        &[[0x41, 0x301, 0xc1], [0x301, 0x41, 0x42]],
    );
    let mut candidates = vec![seed.clone(), plan(&[], &[0xffffffff], &[])];
    for n in 0..seed.len() {
        candidates.push(seed[..n].to_vec());
    }
    for i in 0..seed.len() {
        for value in [0, 0xff, 0x80] {
            let mut b = seed.clone();
            b[i] = value;
            candidates.push(b);
        }
    }
    let mut random = 0x638ada21u32;
    for _ in 0..4096 {
        let mut b = seed.clone();
        for _ in 0..3 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let i = random as usize % b.len();
            b[i] = (random >> 16) as u8;
        }
        candidates.push(b);
    }
    for bytes in candidates {
        load(&mut input, &mut expected, &mut ends, &bytes);
        if let Ok(data) = NormalizationData::parse(&bytes) {
            for cp in [0x41, 0x42, 0x43, 0x301, 0xffffffff] {
                lookup(&mut input, &mut expected, &mut ends, &data, cp, cp, 0x41);
            }
            let source = raw(&[0x42, 0x41, 0x43, 0x301]);
            let cap = scalar_capacity(&source, &data).unwrap() as usize + 2;
            for form in [0, 1] {
                normalized(
                    &mut input,
                    &mut expected,
                    &mut ends,
                    &source,
                    &data,
                    cap,
                    form,
                );
            }
        }
    }
    // Whole-capacity overflow must reject before touching even a zero output.
    let bytes = plan(&[[0x41, 0, 65536]], &vec![0x41; 65536], &[]);
    let data = NormalizationData::parse(&bytes).unwrap();
    load(&mut input, &mut expected, &mut ends, &bytes);
    let source = raw(&vec![0x41; 65536]);
    normalized(&mut input, &mut expected, &mut ends, &source, &data, 0, 1);
    let path = dir.0.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = Command::new(exe)
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(
        actual.status.success(),
        "{}",
        String::from_utf8_lossy(&actual.stderr)
    );
    compare(&actual.stdout, &expected, &ends, "native_normalization");
}
#[test]
#[ignore = "requires .NET 10, original normalization plan reader and canonical form runtime"]
fn original_managed_normalization_matches() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = temp("normalization-managed");
    let includes: String = [
        "src/ProGPU.Rust/tests/managed_normalization.cs",
        "src/ProGPU.Text/UnicodeNormalizationPlan.cs",
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
    let resource = root
        .join("src/ProGPU.Text/UnicodeNormalizationData.bin")
        .display()
        .to_string()
        .replace('&', "&amp;")
        .replace('"', "&quot;");
    std::fs::write(dir.0.join("Control.csproj"), format!("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{includes}<EmbeddedResource Include=\"{resource}\" LogicalName=\"ProGPU.Text.UnicodeNormalizationData.bin\"/></ItemGroup></Project>")).unwrap();
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
    // Select explicit source facts from observed canonical results, never an OS
    // label. This identifies a candidate only: the complete original corpus and
    // added ordering/composition inventory below must still match that runtime.
    let witnesses: &[&[u32]] = &[
        &[0x105c9],
        &[0x105d2, 0x307],
        &[0x61, 0x897, 0x334],
        &[0x61, 0x1acf, 0x334],
        &[0x61, 0x301, 0x1acf],
        &[0xfffe],
    ];
    let mut witness_input = vec![];
    for points in witnesses {
        let units: Vec<_> = points
            .iter()
            .flat_map(|&cp| {
                char::from_u32(cp)
                    .unwrap()
                    .encode_utf16(&mut [0; 2])
                    .to_vec()
            })
            .collect();
        for form in [0, 1] {
            emit(&mut witness_input, units.len() as u32);
            witness_input.extend(bytemuck::cast_slice::<_, u8>(&units));
            witness_input.push(form);
        }
    }
    let witness_path = dir.0.join("witness.bin");
    std::fs::write(&witness_path, &witness_input).unwrap();
    let observed = Command::new(&dotnet)
        .arg(dir.0.join("out/Control.dll"))
        .arg("--witness")
        .stdin(std::fs::File::open(witness_path).unwrap())
        .output()
        .unwrap();
    assert!(observed.status.success());
    let mut candidates = vec![];
    for profile in [
        ManagedNormalizationProfile::Unicode15_1,
        ManagedNormalizationProfile::Unicode16_0,
        ManagedNormalizationProfile::Unicode17_0,
    ] {
        let mut expected = vec![];
        for points in witnesses {
            let text: String = points
                .iter()
                .map(|&cp| char::from_u32(cp).unwrap())
                .collect();
            for form in [0, 1] {
                match NormalizedText::with_managed_profile(
                    DecodedText::from_utf8(text.as_bytes().to_vec()).unwrap(),
                    profile,
                    NormalizationForm::try_from(form).unwrap(),
                ) {
                    Ok(text) => {
                        let units: Vec<_> = text
                            .scalars()
                            .iter()
                            .flat_map(|s| {
                                char::from_u32(s.code_point)
                                    .unwrap()
                                    .encode_utf16(&mut [0; 2])
                                    .to_vec()
                            })
                            .collect();
                        emit(&mut expected, units.len() as u32);
                        expected.extend(bytemuck::cast_slice::<_, u8>(&units));
                    }
                    Err(_) => emit(&mut expected, u32::MAX),
                }
            }
        }
        if observed.stdout == expected {
            candidates.push(profile);
        }
    }
    assert_eq!(
        candidates.len(),
        1,
        "Original runtime has an unsupported/ambiguous canonical profile"
    );
    let profile = candidates[0];
    eprintln!(
        "Original managed runtime candidate: {profile:?}; complete comparison still mandatory"
    );
    let (mut input, mut expected, mut ends) = (Vec::new(), Vec::new(), Vec::new());
    let bytes = default_data().unwrap().bytes();
    let u = |i| u32::from_le_bytes(bytes[i..i + 4].try_into().unwrap()) as usize;
    let mut offset = 20;
    for count in [u(8) * 3, u(12), u(16) * 3] {
        emit(&mut expected, count as u32);
        expected.extend(&bytes[offset..offset + count * 4]);
        offset += count * 4;
        ends.push(expected.len());
    }
    let mut case = |units: &[u16]| {
        for form in [0, 1] {
            emit(&mut input, units.len() as u32);
            input.extend(bytemuck::cast_slice::<_, u8>(units));
            input.push(form);
            match DecodedText::from_utf16(units.to_vec()) {
                Err(_) => emit(&mut expected, u32::MAX),
                Ok(text) => {
                    let Ok(normalized) = NormalizedText::with_managed_profile(
                        text,
                        profile,
                        NormalizationForm::try_from(form).unwrap(),
                    ) else {
                        emit(&mut expected, u32::MAX);
                        ends.push(expected.len());
                        continue;
                    };
                    let mut output = Vec::new();
                    for s in normalized.scalars() {
                        let mut pair = [0; 2];
                        output.extend_from_slice(
                            char::from_u32(s.code_point)
                                .unwrap()
                                .encode_utf16(&mut pair),
                        );
                    }
                    emit(&mut expected, output.len() as u32);
                    expected.extend(bytemuck::cast_slice::<_, u8>(&output));
                }
            }
            ends.push(expected.len());
        }
    };
    for cp in 0..=0x10ffff {
        if let Some(c) = char::from_u32(cp) {
            let mut units = [0; 2];
            case(c.encode_utf16(&mut units));
            // Every scalar also participates in ordering across known marks,
            // including source-unassigned scalars and every recent CCC change.
            let mut mixed = vec![0x61, 0x301];
            mixed.extend_from_slice(c.encode_utf16(&mut units));
            mixed.extend([0x334, 0x315]);
            case(&mixed);
        }
    }
    // Every original pair, including those absent from the selected older source.
    let pairs_offset = 20 + u(8) * 12 + u(12) * 4;
    for index in 0..u(16) {
        let at = pairs_offset + index * 12;
        let mut units = vec![];
        for cp in [u(at) as u32, u(at + 4) as u32] {
            units.extend_from_slice(char::from_u32(cp).unwrap().encode_utf16(&mut [0; 2]));
        }
        case(&units);
    }
    for units in [
        vec![],
        vec![0xd800],
        vec![0x41, 0xdc00],
        vec![0xffff],
        vec![0xfffe],
    ] {
        case(&units);
    }
    for units in [
        vec![0x41, 0xfffe],
        vec![0xfffe, 0x301],
        vec![0x41, 0xfffe, 0x301],
        vec![0x41, 0xffff, 0x301],
        vec![0x41, 0xd83f, 0xdffe, 0x301],
    ] {
        case(&units);
    }
    let points = [
        0x41, 0xc5, 0x212b, 0x301, 0x315, 0x300, 0x327, 0x1100, 0x1161, 0x11a8, 0xac01, 0x1d15e,
    ];
    for a in points {
        for b in points {
            for c in points {
                let mut units = Vec::new();
                for cp in [a, b, c] {
                    let mut pair = [0; 2];
                    units.extend_from_slice(char::from_u32(cp).unwrap().encode_utf16(&mut pair));
                }
                case(&units);
            }
        }
    }
    let path = dir.0.join("input.bin");
    std::fs::write(&path, &input).unwrap();
    let actual = Command::new(dotnet)
        .arg(dir.0.join("out/Control.dll"))
        .stdin(Stdio::from(std::fs::File::open(path).unwrap()))
        .output()
        .unwrap();
    assert!(
        actual.status.success(),
        "{}",
        String::from_utf8_lossy(&actual.stderr)
    );
    compare(&actual.stdout, &expected, &ends, "managed_normalization");
}
#[test]
fn managed_admission_is_explicit_and_atomic() {
    let form = NormalizationForm::CanonicalComposition;
    for cp in [0xfffe, 0xd800, 0x110000] {
        let mut output = [SENTINEL; 4];
        assert_eq!(
            normalize_managed_into(&raw(&[0x41, cp, 0x301]), form, &mut output[..0]),
            Err(UnicodeError::InvalidArgument)
        );
        assert_eq!(output, [SENTINEL; 4]);
    }
    assert!(NormalizedText::new(DecodedText::from_utf16(vec![0xfffe]).unwrap(), form).is_ok());
    assert!(matches!(
        NormalizedText::new_managed(DecodedText::from_utf16(vec![0xfffe]).unwrap(), form),
        Err(UnicodeError::InvalidArgument)
    ));
    for cp in [0xffff, 0x1fffe, 0x10ffff] {
        let mut output = [SENTINEL; 1];
        assert_eq!(
            normalize_managed_into(&raw(&[cp]), form, &mut output),
            Ok(1)
        );
        assert_eq!(output[0].code_point, cp);
    }
}
