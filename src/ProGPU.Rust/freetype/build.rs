//! Original read-only dependency admission; never downloads or builds FreeType.
use serde_json::Value;
use std::{
    env, fs,
    path::{Path, PathBuf},
    process::Command,
};

fn field<'a>(value: &'a Value, name: &str) -> &'a str {
    value
        .get(name)
        .and_then(Value::as_str)
        .unwrap_or_else(|| panic!("missing verified FreeType receipt field {name}"))
}
fn digest(value: &str) -> String {
    assert!(
        value.len() == 64
            && value
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
        "invalid verified FreeType digest"
    );
    let bytes: Vec<_> = value
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            u8::from_str_radix(std::str::from_utf8(pair).expect("ASCII digest"), 16)
                .expect("hex digest")
        })
        .collect();
    format!("{:?}", bytes)
}
fn main() {
    for key in ["PROGPU_FREETYPE_MANIFEST", "PROGPU_FREETYPE_PYTHON"] {
        println!("cargo:rerun-if-env-changed={key}");
    }
    println!("cargo:rerun-if-changed=build-support");
    let os = env::var("CARGO_CFG_TARGET_OS").expect("target OS");
    let arch = env::var("CARGO_CFG_TARGET_ARCH").expect("target architecture");
    assert_eq!(
        env::var("CARGO_CFG_TARGET_POINTER_WIDTH").as_deref(),
        Ok("64"),
        "the original FreeType producer requires a 64-bit native target"
    );
    let os = match os.as_str() {
        "windows" => "win",
        "macos" => "osx",
        "linux" => "linux",
        _ => panic!("unsupported original native FreeType target"),
    };
    let arch = match arch.as_str() {
        "x86_64" => "x64",
        "aarch64" => "arm64",
        _ => panic!("unsupported original native FreeType architecture"),
    };
    let rid = format!("{os}-{arch}");
    let manifest = env::var_os("PROGPU_FREETYPE_MANIFEST").expect("native-freetype requires PROGPU_FREETYPE_MANIFEST naming the original prepared, verified installation; no ambient fallback is permitted");
    let root = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("manifest directory"));
    let python = env::var_os("PROGPU_FREETYPE_PYTHON").unwrap_or_else(|| {
        if cfg!(windows) {
            "python".into()
        } else {
            "python3".into()
        }
    });
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("output directory"));
    let output = Command::new(python)
        .arg(root.join("build-support/receipt.py"))
        .arg(&manifest)
        .arg(&rid)
        .arg(&out)
        .env("PYTHONDONTWRITEBYTECODE", "1")
        .output()
        .expect("could not execute the original read-only FreeType receipt verifier");
    assert!(
        output.status.success(),
        "original FreeType dependency admission failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let receipt: Value = serde_json::from_slice(&output.stdout).expect("invalid verifier receipt");
    assert_eq!(
        field(&receipt, "rid"),
        rid,
        "verified RID differs from compilation target"
    );
    assert_eq!(
        receipt["headerVersion"],
        serde_json::json!([2, 14, 3]),
        "verified headers differ from the original ABI"
    );
    let include = Path::new(field(&receipt, "include"));
    let notice_output = Path::new(field(&receipt, "retainedNotices"));
    // The verifier retained and rehashed this exact prebuilt static archive under
    // a unique private link name. No generic freetype search name is emitted.
    assert_eq!(
        Path::new(field(&receipt, "privateLibrary")).parent(),
        Some(out.as_path())
    );
    fs::write(
        out.join("progpu-freetype-build-receipt.json"),
        &output.stdout,
    )
    .expect("could not retain build receipt");
    let generated = format!(
        "pub const BUILD_RECEIPT: DependencyReceipt = DependencyReceipt {{ rid: {rid:?}, header_version: [2,14,3], manifest_sha256: {}, archive_sha256: {}, headers_sha256: {}, notices_sha256: {} }};\n",
        digest(field(&receipt, "manifestSha256")),
        digest(field(&receipt, "sha256")),
        digest(field(&receipt, "headersSha256")),
        digest(field(&receipt, "noticesSha256")),
    );
    fs::write(out.join("receipt.rs"), generated).expect("could not write immutable receipt");
    // Changes in the exact prepared tree require its complete original admission
    // again, including dirty source, keyring, build configuration and inventories.
    let install = Path::new(field(&receipt, "manifest"))
        .parent()
        .expect("install directory");
    println!(
        "cargo:rerun-if-changed={}",
        install.parent().expect("prepared workspace").display()
    );
    println!("cargo:rustc-link-search=native={}", out.display());
    println!("cargo:rustc-link-lib=static=progpu_verified_freetype");
    if os != "win" {
        println!("cargo:rustc-link-lib=m");
    }
    println!("cargo::metadata=include={}", include.display());
    println!("cargo::metadata=notices={}", notice_output.display());
    println!(
        "cargo::metadata=receipt={}",
        out.join("progpu-freetype-build-receipt.json").display()
    );
}
