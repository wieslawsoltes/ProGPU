//! Locates the ProGPU native SDK of this crate's version for the target: the headers, the runtime
//! libraries (`progpu_native` and the wgpu-native library it is linked against) and the static
//! SDK archives (the C++ scene builder and text libraries).
//!
//! `PROGPU_NATIVE_SDK_DIR` names a prepared directory with `include/`, `runtime/` and `sdk/`.
//! Otherwise the pinned NuGet packages are downloaded once into a cache (`PROGPU_NATIVE_CACHE`,
//! else the user cache directory), verified by their SHA-256 and extracted for the target.
//!
//! The runtime libraries are loaded at run time (`Runtime::load`), so nothing is linked here: the
//! directory is passed on as `PROGPU_NATIVE_RUNTIME_DIR` (a compile-time default of the loader)
//! and, through `links`, as `DEP_PROGPU_NATIVE_INCLUDE`, `DEP_PROGPU_NATIVE_SDK` and
//! `DEP_PROGPU_NATIVE_RUNTIME` for crates that compile C++ against the SDK.

use sha2::{Digest, Sha256};
use std::env;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;

const VERSION: &str = "0.1.0-preview.70";

struct Package {
    id: &'static str,
    version: &'static str,
    sha256: &'static str,
}

const BACKEND: Package = Package {
    id: "progpu.backend.native",
    version: VERSION,
    sha256: "9626f461feaf8870a9935452dc650d03183f11b1adb277dbb32e206d9cd5b24d",
};
const SDK: Package = Package {
    id: "progpu.native.sdk",
    version: VERSION,
    sha256: "d91825ddc733623ee321250bbd7669d845112332ac672bafd07ba46466c6df64",
};
// The wgpu-native runtime the engine's runtime library is linked against (ProGPU's
// eng/build-progpu-native.sh takes the same package).
const WGPU: Package = Package {
    id: "silk.net.webgpu.native.wgpu",
    version: "2.23.0",
    sha256: "83c73f1a30a82b53f0eedd5e1ec9e7a75a5570644f1e8a9a0c9953c20e87e0e5",
};

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=PROGPU_NATIVE_SDK_DIR");
    println!("cargo:rerun-if-env-changed=PROGPU_NATIVE_CACHE");
    println!("cargo::rustc-check-cfg=cfg(progpu_native_sdk)");

    let Some(rid) = runtime_identifier() else {
        // No desktop SDK for this target (the browser, mobile): the loader reports it at run time.
        return;
    };
    let root = match env::var_os("PROGPU_NATIVE_SDK_DIR") {
        Some(dir) => PathBuf::from(dir),
        None => prepare_from_packages(rid).unwrap_or_else(|error| panic!("ProGPU native SDK {VERSION} for {rid}: {error}")),
    };
    let include = root.join("include");
    let runtime = root.join("runtime");
    let sdk = root.join("sdk");
    for dir in [&include, &runtime, &sdk] {
        assert!(dir.is_dir(), "the ProGPU native SDK has no {}", dir.display());
    }
    println!("cargo:rustc-cfg=progpu_native_sdk");
    println!("cargo:rustc-env=PROGPU_NATIVE_RUNTIME_DIR={}", runtime.display());
    println!("cargo:include={}", include.display());
    println!("cargo:sdk={}", sdk.display());
    println!("cargo:runtime={}", runtime.display());
}

/// The NuGet runtime identifier of the target.
fn runtime_identifier() -> Option<&'static str> {
    let os = env::var("CARGO_CFG_TARGET_OS").ok()?;
    let arch = env::var("CARGO_CFG_TARGET_ARCH").ok()?;
    Some(match (os.as_str(), arch.as_str()) {
        ("macos", "aarch64") => "osx-arm64",
        ("macos", "x86_64") => "osx-x64",
        ("linux", "x86_64") => "linux-x64",
        ("linux", "aarch64") => "linux-arm64",
        ("windows", "x86_64") => "win-x64",
        ("windows", "aarch64") => "win-arm64",
        _ => return None,
    })
}

fn cache_root() -> PathBuf {
    if let Some(dir) = env::var_os("PROGPU_NATIVE_CACHE") {
        return PathBuf::from(dir);
    }
    let base = if cfg!(windows) {
        env::var_os("LOCALAPPDATA").map(PathBuf::from)
    } else {
        env::var_os("XDG_CACHE_HOME").map(PathBuf::from).or_else(|| env::var_os("HOME").map(|home| Path::new(&home).join(".cache")))
    };
    base.unwrap_or_else(|| PathBuf::from(env::var("OUT_DIR").unwrap())).join("progpu-native")
}

/// Downloads, verifies and extracts the packages for `rid`; returns the SDK directory.
fn prepare_from_packages(rid: &str) -> Result<PathBuf, String> {
    let cache = cache_root();
    let root = cache.join(VERSION).join(rid);
    let marker = root.join(".complete");
    if marker.is_file() {
        return Ok(root);
    }
    let backend = fetch(&cache, &BACKEND)?;
    let sdk = fetch(&cache, &SDK)?;
    let wgpu = fetch(&cache, &WGPU)?;
    let _ = fs::remove_dir_all(&root);
    let runtime_prefix = format!("runtimes/{rid}/native/");
    extract(&backend, &root.join("include"), |name| name.strip_prefix("build/native/include/"))?;
    extract(&backend, &root.join("runtime"), |name| {
        name.strip_prefix(runtime_prefix.as_str()).filter(|rest| !rest.contains('/'))
    })?;
    extract(&backend, &root.join("licenses"), |name| name.strip_prefix(format!("{runtime_prefix}licenses/").as_str()))?;
    extract(&sdk, &root.join("sdk"), |name| name.strip_prefix(format!("{runtime_prefix}sdk/").as_str()))?;
    extract(&wgpu, &root.join("runtime"), |name| {
        name.strip_prefix(runtime_prefix.as_str()).filter(|rest| !rest.contains('/'))
    })?;
    fs::write(&marker, VERSION).map_err(|error| error.to_string())?;
    Ok(root)
}

/// The package file in the cache, downloaded once and verified on every use.
fn fetch(cache: &Path, package: &Package) -> Result<PathBuf, String> {
    let path = cache.join(format!("{}.{}.nupkg", package.id, package.version));
    if !path.is_file() {
        fs::create_dir_all(cache).map_err(|error| error.to_string())?;
        let partial = path.with_extension("partial");
        let url = format!(
            "https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg",
            id = package.id,
            version = package.version
        );
        let status = Command::new("curl")
            .args(["--fail", "--silent", "--show-error", "--location", "--retry", "3", "--output"])
            .arg(&partial)
            .arg(&url)
            .status()
            .map_err(|error| format!("curl could not be run to download {url}: {error}"))?;
        if !status.success() {
            return Err(format!("downloading {url} failed ({status})"));
        }
        fs::rename(&partial, &path).map_err(|error| error.to_string())?;
    }
    let mut hasher = Sha256::new();
    let mut file = fs::File::open(&path).map_err(|error| error.to_string())?;
    let mut buffer = vec![0u8; 1 << 20];
    loop {
        let read = file.read(&mut buffer).map_err(|error| error.to_string())?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    let digest: String = hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect();
    if digest != package.sha256 {
        return Err(format!("{} has SHA-256 {digest}, expected {}", path.display(), package.sha256));
    }
    Ok(path)
}

/// Extracts the entries whose names `select` maps to a relative path.
fn extract(package: &Path, into: &Path, select: impl Fn(&str) -> Option<&str>) -> Result<(), String> {
    let file = fs::File::open(package).map_err(|error| error.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|error| error.to_string())?;
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|error| error.to_string())?;
        if entry.is_dir() {
            continue;
        }
        let name = entry.name().to_string();
        let Some(relative) = select(&name).filter(|relative| !relative.is_empty()) else { continue };
        if relative.split('/').any(|part| part == "..") {
            return Err(format!("unexpected entry {name}"));
        }
        let target = into.join(relative);
        fs::create_dir_all(target.parent().unwrap()).map_err(|error| error.to_string())?;
        let mut bytes = Vec::with_capacity(entry.size() as usize);
        entry.read_to_end(&mut bytes).map_err(|error| error.to_string())?;
        let mut output = fs::File::create(&target).map_err(|error| error.to_string())?;
        output.write_all(&bytes).map_err(|error| error.to_string())?;
    }
    Ok(())
}
