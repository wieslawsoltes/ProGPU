//! Original encoded CFF inventory and numeric parsing controls.
use progpu::font::{
    FontError, SfntFace,
    cff::{FdSelect, Index, TopDictionary, TopDictionary2, read_number},
};
#[path = "support/cff_execution.rs"]
mod execution;
#[allow(dead_code)]
#[path = "support/font_fixture.rs"]
mod fixture;
#[derive(Clone)]
struct Case {
    mode: u32,
    args: [u32; 4],
    data: Vec<u8>,
}
fn index(items: &[Vec<u8>], cff2: bool, width: u8) -> Vec<u8> {
    let mut b = if cff2 {
        (items.len() as u32).to_be_bytes().to_vec()
    } else {
        (items.len() as u16).to_be_bytes().to_vec()
    };
    if items.is_empty() {
        return b;
    }
    b.push(width);
    let mut offset = 1u32;
    for i in 0..=items.len() {
        b.extend_from_slice(&offset.to_be_bytes()[4 - usize::from(width)..]);
        if let Some(item) = items.get(i) {
            offset += item.len() as u32;
        }
    }
    for item in items {
        b.extend_from_slice(item);
    }
    b
}
fn integer(value: i32) -> Vec<u8> {
    let mut b = vec![29];
    b.extend_from_slice(&value.to_be_bytes());
    b
}
fn real(value: &str) -> Vec<u8> {
    let mut n = Vec::new();
    for byte in value.bytes() {
        n.push(match byte {
            b'0'..=b'9' => byte - b'0',
            b'.' => 10,
            b'E' => 11,
            b'-' => 14,
            _ => 13,
        });
    }
    n.push(15);
    if n.len() % 2 != 0 {
        n.push(15);
    }
    let mut b = vec![30];
    for p in n.chunks(2) {
        b.push(p[0] * 16 + p[1]);
    }
    b
}
fn private(offset: i32) -> Vec<u8> {
    let mut b = integer(2);
    b.extend(integer(offset));
    b.push(18);
    b
}
fn font(cid: bool) -> Vec<u8> {
    let mut table = vec![0; 432];
    table[..4].copy_from_slice(&[1, 0, 4, 4]);
    let mut top = integer(128);
    top.push(17);
    top.extend(private(200));
    if cid {
        top.extend(integer(256));
        top.extend([12, 36]);
        top.extend(integer(320));
        top.extend([12, 37]);
    }
    let mut prefix = vec![0, 0];
    prefix.extend(index(&[top], false, 1));
    prefix.extend([0, 0]);
    prefix.extend(index(&[vec![11]], false, 1));
    table[4..4 + prefix.len()].copy_from_slice(&prefix);
    let chars = index(
        &[
            vec![139, 139, 21, 239, 139, 139, 239, 39, 139, 5, 14],
            vec![],
            vec![14],
        ],
        false,
        1,
    );
    table[128..128 + chars.len()].copy_from_slice(&chars);
    table[200..202].copy_from_slice(&[141, 19]);
    let subs = index(&[vec![11]], false, 1);
    table[202..202 + subs.len()].copy_from_slice(&subs);
    if cid {
        let dictionaries = index(&[private(360), private(400)], false, 1);
        table[256..256 + dictionaries.len()].copy_from_slice(&dictionaries);
        table[320..331].copy_from_slice(&[3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 3]);
        for (offset, sub) in [(360, vec![11]), (400, vec![139, 11])] {
            table[offset..offset + 2].copy_from_slice(&[141, 19]);
            let b = index(&[sub], false, 1);
            table[offset + 2..offset + 2 + b.len()].copy_from_slice(&b);
        }
    }
    fixture::sfnt(&[(*b"CFF ", table)], 0)
}
fn variants(cases: &mut Vec<Case>, case: Case) {
    cases.push(case.clone());
    for len in 0..case.data.len() {
        let mut c = case.clone();
        c.data.truncate(len);
        cases.push(c);
    }
    for i in 0..case.data.len() {
        for value in [0, 1, 127, 255] {
            let mut c = case.clone();
            c.data[i] = value;
            cases.push(c);
        }
    }
}
fn corpus() -> Vec<Case> {
    let mut cases = Vec::new();
    for cff2 in [false, true] {
        for width in 1..=4 {
            for items in [
                vec![],
                vec![vec![]],
                vec![vec![0xaa, 0xbb], vec![], vec![0xcc]],
            ] {
                variants(
                    &mut cases,
                    Case {
                        mode: u32::from(cff2),
                        args: [0; 4],
                        data: index(&items, cff2, width),
                    },
                );
            }
        }
    }
    for first in 0..=255 {
        for second in 0..=255 {
            cases.push(Case {
                mode: 2,
                args: [first, 0, 0, 0],
                data: vec![second as u8, 0x5f, 0xff, 0x80],
            });
        }
    }
    for text in [
        "1.5",
        "-0",
        "-1.25E-2",
        "0.00048828125",
        "999999999999999999999999999999",
        "1E-22",
        "1E22",
        "1E-23",
        "1E23",
        "1E309",
        "0E309",
        "1E-400",
        "1E40960",
        "1E409600",
        "--1",
        ".1",
        "1.",
        "1..0",
        "E2",
        "1E-",
        "",
        "-",
    ] {
        let data = real(text);
        variants(
            &mut cases,
            Case {
                mode: 2,
                args: [30, 1, 0, 0],
                data,
            },
        );
    }
    for n in [47, 48, 49, 95, 96, 97, 100] {
        let data = real(&"1".repeat(n));
        cases.push(Case {
            mode: 2,
            args: [30, 1, 0, 0],
            data,
        });
    }
    let mut top = integer(300);
    top.push(17);
    top.extend(private(400));
    top.extend(integer(500));
    top.extend([12, 36]);
    top.extend(integer(600));
    top.extend([12, 37]);
    for mode in [3, 4] {
        variants(
            &mut cases,
            Case {
                mode,
                args: [0; 4],
                data: top.clone(),
            },
        );
    }
    let mut top2 = integer(50);
    top2.push(17);
    top2.extend(integer(70));
    top2.extend([12, 36]);
    let mut matrix = real("0.00048828125");
    matrix.extend([139, 139]);
    matrix.extend(real("0.00048828125"));
    matrix.extend([139, 139, 12, 7]);
    top2.extend(matrix);
    variants(
        &mut cases,
        Case {
            mode: 4,
            args: [0; 4],
            data: top2,
        },
    );
    for count in [47, 48, 49] {
        for mode in [3, 4] {
            let mut data = vec![139; count];
            data.extend([140, 17]);
            cases.push(Case {
                mode,
                args: [0; 4],
                data,
            });
        }
    }
    for data in [
        vec![0, 0, 1, 1, 0],
        vec![3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 4],
        vec![
            4, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 4,
        ],
    ] {
        variants(
            &mut cases,
            Case {
                mode: 5,
                args: [0, 4, 2, 0],
                data,
            },
        );
    }
    variants(
        &mut cases,
        Case {
            mode: 6,
            args: [0, 2, 0, 0],
            data: vec![141, 19, 0, 1, 1, 1, 2, 11],
        },
    );
    for cid in [false, true] {
        for expected in [0, 3, 4] {
            variants(
                &mut cases,
                Case {
                    mode: 7,
                    args: [expected, 0, 0, 0],
                    data: font(cid),
                },
            );
        }
    }
    // Deterministic arbitrary bytes check failure/cursor behavior in every parser.
    let mut seed = 0x12345678u32;
    for i in 0..4096u32 {
        let mut data = vec![0; (i % 97) as usize];
        for b in &mut data {
            seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
            *b = (seed >> 24) as u8;
        }
        let mode = i % 7;
        let args = match mode {
            2 => [i % 256, i % 5, 0, 0],
            5 => [i % 5, i % 17, i % 3, 0],
            6 => [i % 5, i % 23, 0, 0],
            _ => [i % 5, 0, 0, 0],
        };
        cases.push(Case { mode, args, data });
    }
    cases.extend(execution::corpus());
    cases
}
fn emit(out: &mut Vec<u8>, v: u32) {
    out.extend_from_slice(&v.to_ne_bytes());
}
fn number(out: &mut Vec<u8>, v: f64) {
    let bits = v.to_bits();
    emit(out, bits as u32);
    emit(out, (bits >> 32) as u32);
}
fn blob(out: &mut Vec<u8>, b: &[u8]) {
    emit(out, b.len() as u32);
    out.extend_from_slice(b);
}
fn idx(out: &mut Vec<u8>, v: Index<'_>) {
    emit(out, v.len());
    emit(out, u32::from(v.offset_size()));
    for i in 0..v.len().min(64) {
        match v.get(i) {
            Ok(b) => {
                emit(out, 0);
                blob(out, b);
            }
            Err(e) => emit(out, e as u32),
        }
    }
}
fn top(out: &mut Vec<u8>, v: TopDictionary) {
    for n in [
        v.char_strings_offset,
        v.private_size,
        v.private_offset,
        v.font_dictionary_offset,
        v.fd_select_offset,
    ] {
        emit(out, n);
    }
}
fn snapshot(c: &Case) -> Vec<u8> {
    if c.mode == 8 {
        return execution::snapshot(c);
    }
    let mut out = Vec::new();
    let b = &c.data;
    let a = c.args;
    match c.mode {
        0 | 1 => {
            let mut cursor = a[0] as usize;
            let r = if c.mode == 0 {
                Index::read(b, &mut cursor)
            } else {
                Index::read_cff2(b, &mut cursor)
            };
            emit(&mut out, r.as_ref().err().map_or(0, |e| *e as u32));
            emit(&mut out, cursor as u32);
            if let Ok(v) = r {
                idx(&mut out, v);
            }
        }
        2 => {
            let mut cursor = a[1] as usize;
            let r = read_number(b, &mut cursor, a[0] as u8);
            emit(&mut out, u32::from(r.is_some()));
            emit(&mut out, cursor as u32);
            if let Some(v) = r {
                number(&mut out, v);
            }
        }
        3 => match TopDictionary::read(b) {
            Ok(v) => {
                emit(&mut out, 0);
                top(&mut out, v);
            }
            Err(e) => emit(&mut out, e as u32),
        },
        4 => match TopDictionary2::read(b) {
            Ok(v) => {
                emit(&mut out, 0);
                for n in [
                    v.char_strings_offset,
                    v.font_dictionary_offset,
                    v.fd_select_offset,
                    v.variation_store_offset,
                ] {
                    emit(&mut out, n);
                }
                number(&mut out, v.font_matrix_scale);
                emit(&mut out, u32::from(v.has_font_matrix));
            }
            Err(e) => emit(&mut out, e as u32),
        },
        5 => match FdSelect::read(b, a[0], a[1], a[2]) {
            Ok(v) => {
                emit(&mut out, 0);
                emit(&mut out, u32::from(v.format()));
                emit(&mut out, v.glyph_count());
                emit(&mut out, v.range_count());
                for g in [0, 1, 2, 3, 17, a[1].wrapping_sub(1), a[1], u32::MAX] {
                    match v.dictionary(g) {
                        Ok(d) => {
                            emit(&mut out, 0);
                            emit(&mut out, d);
                        }
                        Err(e) => emit(&mut out, e as u32),
                    }
                }
            }
            Err(e) => emit(&mut out, e as u32),
        },
        6 => match Index::local_subroutines(b, a[0], a[1]) {
            Ok(v) => {
                emit(&mut out, 0);
                idx(&mut out, v);
            }
            Err(e) => emit(&mut out, e as u32),
        },
        7 => {
            let face = match SfntFace::new(b.clone(), 0) {
                Ok(f) => {
                    emit(&mut out, 0);
                    f
                }
                Err(e) => {
                    emit(&mut out, e as u32);
                    return out;
                }
            };
            let f = match face.cff1(a[0] as u16) {
                Ok(f) => {
                    emit(&mut out, 0);
                    f
                }
                Err(e) => {
                    emit(&mut out, e as u32);
                    return out;
                }
            };
            blob(&mut out, f.data());
            top(&mut out, f.top_dictionary());
            idx(&mut out, f.char_strings());
            idx(&mut out, f.global_subroutines());
            idx(&mut out, f.font_dictionaries());
            for g in [
                0,
                1,
                f.char_strings().len() - 1,
                f.char_strings().len(),
                u32::MAX,
            ] {
                match f.local_subroutines(g) {
                    Ok(v) => {
                        emit(&mut out, 0);
                        idx(&mut out, v);
                    }
                    Err(e) => emit(&mut out, e as u32),
                }
            }
        }
        _ => unreachable!(),
    }
    out
}
#[test]
fn source_retention_and_original_cid_selection() {
    for cid in [false, true] {
        let face = SfntFace::new(font(cid), 0).unwrap();
        let f = face.cff1(3).unwrap();
        assert_eq!(f.source().data().as_ptr(), face.data().as_ptr());
        drop(face);
        assert_eq!(f.char_strings().len(), 3);
        assert_eq!(f.char_strings().get(2).unwrap(), [14]);
        assert_eq!(f.global_subroutines().get(0).unwrap(), [11]);
        assert_eq!(f.local_subroutines(0).unwrap().get(0).unwrap(), [11]);
        assert_eq!(
            f.local_subroutines(2).unwrap().get(0).unwrap(),
            if cid { &[139, 11][..] } else { &[11][..] }
        );
        assert!(matches!(
            f.local_subroutines(3),
            Err(FontError::InvalidArgument)
        ));
    }
    let data = real("0.00048828125");
    assert_eq!(
        read_number(&data, &mut 1, 30).unwrap().to_bits(),
        (1.0f64 / 2048.0).to_bits()
    );
}
#[test]
fn malformed_inventories_are_bounded() {
    for c in corpus() {
        let _ = snapshot(&c);
    }
}
#[test]
#[ignore = "requires original C++20 CFF readers; mandatory source differential gate"]
fn original_cff_containers_and_dictionary_bytes_match() {
    use std::process::{Command, Stdio};
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = std::env::temp_dir().join(format!("progpu-cff-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(dir.clone());
    let exe = dir.join("cff-oracle");
    let mut cmd = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()));
    cmd.args(["-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror"])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_cff.cpp"));
    // Same separate-operation source oracle as geometry/composite. The original
    // compiler-default control remains independently selectable and reproducible.
    if std::env::var_os("PROGPU_RUST_NATIVE_DEFAULT_FP").is_none() {
        cmd.arg("-ffp-contract=off");
    }
    for name in [
        "sfnt",
        "cff_index",
        "cff_dictionary",
        "cff_fd_select",
        "cff_private",
        "cff1",
        "cff2",
        "cff_type2",
        "cff_type2_operators",
        "cff_outline",
        "item_variation_store",
        "font_variations",
    ] {
        cmd.arg(root.join(format!(
            "src/ProGPU.Native/src/Text/progpu_native_{name}.cpp"
        )));
    }
    assert!(cmd.arg("-o").arg(&exe).status().unwrap().success());
    let cases = corpus();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut ends = Vec::new();
    for c in &cases {
        emit(&mut input, c.mode);
        emit(&mut input, c.data.len() as u32);
        for a in c.args {
            emit(&mut input, a);
        }
        input.extend_from_slice(&c.data);
        expected.extend(snapshot(c));
        ends.push(expected.len());
    }
    let path = dir.join("input.bin");
    std::fs::write(&path, input).unwrap();
    let actual = Command::new(&exe)
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
        let case = ends.partition_point(|end| *end <= byte);
        panic!(
            "CFF case {case}, mode {}, byte {byte}: native {}, Rust {}; args {:?}, input {:?}",
            cases[case].mode,
            actual.stdout[byte],
            expected[byte],
            cases[case].args,
            cases[case].data
        );
    }
    assert_eq!(actual.stdout.len(), expected.len());
    eprintln!(
        "native_cff_data: {} inputs, {} exact bytes",
        cases.len(),
        expected.len()
    );
}
