//! Locates the ProGPU native SDK of this crate's version for the target: the headers, the runtime
//! libraries (`progpu_native` and the wgpu-native library it is linked against) and the static
//! SDK archives (the C++ scene builder and text libraries).
//!
//! The SDK comes from, in this order:
//! - `PROGPU_NATIVE_SDK_DIR`, a prepared directory with `include/`, `runtime/` and `sdk/`;
//! - the ProGPU sources: `PROGPU_SOURCE_DIR`, or the ProGPU checkout this crate is part of
//!   (`bindings/rust/progpu-native-sys`, as in a git submodule of ProGPU). The engine is built
//!   from them with CMake (`src/ProGPU.Native`) against the pinned wgpu-native runtime and
//!   headers, so the bindings always run the engine of the sources they were built with;
//! - else (the published crate) the pinned NuGet packages of this version, downloaded once into a
//!   cache (`PROGPU_NATIVE_CACHE`, else the user cache directory), verified by their SHA-256 and
//!   extracted for the target.
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

/// A pinned file of a source repository.
struct PinnedFile {
    name: &'static str,
    url: &'static str,
    sha256: &'static str,
}

// The WebGPU headers the engine is compiled against: those of the wgpu-native build of the runtime
// package (wgpu-native 33133da, webgpu-headers aef5e42; eng/build-progpu-native.sh).
const WEBGPU_HEADERS: [PinnedFile; 2] = [
    PinnedFile {
        name: "webgpu.h",
        url: "https://raw.githubusercontent.com/webgpu-native/webgpu-headers/aef5e428a1fdab2ea770581ae7c95d8779984e0a/webgpu.h",
        sha256: "defb965756966d04186f80fb193994cfa70b375247da7a34d20608662216a50f",
    },
    PinnedFile {
        name: "wgpu.h",
        url: "https://raw.githubusercontent.com/gfx-rs/wgpu-native/33133da4ec5a0174cb21539ef2d3346f75200411/ffi/wgpu.h",
        sha256: "a8dd002ecfa4d3ca6b8ca2a74bf7733544b68deec279ae1cde3c33acfbeef8bc",
    },
];

/// The static SDK libraries the C ABI over the scene builder links.
const SDK_LIBRARIES: [&str; 4] =
    ["progpu_native_scene_builder", "progpu_native_text", "progpu_native_image", "progpu_native_compression"];

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=PROGPU_NATIVE_SDK_DIR");
    println!("cargo:rerun-if-env-changed=PROGPU_NATIVE_CACHE");
    println!("cargo:rerun-if-env-changed=PROGPU_SOURCE_DIR");
    println!("cargo::rustc-check-cfg=cfg(progpu_native_sdk)");

    let Some(rid) = runtime_identifier() else {
        // No desktop SDK for this target (the browser, mobile): the loader reports it at run time.
        return;
    };
    let root = if let Some(dir) = env::var_os("PROGPU_NATIVE_SDK_DIR") {
        PathBuf::from(dir)
    } else if let Some(source) = source_checkout() {
        build_from_source(&source, rid).unwrap_or_else(|error| {
            panic!("ProGPU native engine from the sources in {}: {error}", source.display())
        })
    } else {
        prepare_from_packages(rid).unwrap_or_else(|error| panic!("ProGPU native SDK {VERSION} for {rid}: {error}"))
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
    compile_scene_builder(&include, &sdk);
}

/// Compiles the C ABI over the retained scene builder (`native/`) and links it with the static
/// builder of the SDK.
fn compile_scene_builder(include: &Path, sdk: &Path) {
    println!("cargo:rerun-if-changed=native/progpu_native_scene_builder_c.cpp");
    println!("cargo:rerun-if-changed=native/progpu_native_scene_builder_c.h");
    let mut build = cc::Build::new();
    build.cpp(true).std("c++20").include(include).include("native").file("native/progpu_native_scene_builder_c.cpp");
    if env::var("CARGO_CFG_TARGET_ENV").as_deref() != Ok("msvc") {
        build.flag("-fvisibility=hidden");
    }
    build.compile("progpu_native_scene_builder_c");
    println!("cargo:rustc-link-search=native={}", sdk.display());
    for library in SDK_LIBRARIES {
        println!("cargo:rustc-link-lib=static={library}");
    }
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

/// The ProGPU checkout to build the engine from: `PROGPU_SOURCE_DIR`, else the checkout this crate
/// is part of. None for the published crate.
fn source_checkout() -> Option<PathBuf> {
    let candidate = match env::var_os("PROGPU_SOURCE_DIR") {
        Some(dir) => PathBuf::from(dir),
        None => Path::new(&env::var_os("CARGO_MANIFEST_DIR")?).join("../../.."),
    };
    let candidate = candidate.canonicalize().ok()?;
    candidate.join("src/ProGPU.Native/CMakeLists.txt").is_file().then_some(candidate)
}

/// Builds the engine of the checkout at `source` for `rid` (in `OUT_DIR`, incrementally) and lays it
/// out as an SDK directory; returns that directory.
fn build_from_source(source: &Path, rid: &str) -> Result<PathBuf, String> {
    let native = source.join("src/ProGPU.Native");
    // The engine's sources and the shared shaders and data it embeds.
    for watched in [
        "src/ProGPU.Native/CMakeLists.txt",
        "src/ProGPU.Native/cmake",
        "src/ProGPU.Native/include",
        "src/ProGPU.Native/src",
        "src/ProGPU.Native/Shaders",
        "src/ProGPU.Backend/Shaders",
        "src/ProGPU.Scene/Shaders",
        "src/ProGPU.Compute/Shaders",
        "src/ProGPU.Vector/Shaders",
        "src/ProGPU.Text/UnicodeNormalizationData.bin",
    ] {
        println!("cargo:rerun-if-changed={}", source.join(watched).display());
    }
    let out = PathBuf::from(env::var("OUT_DIR").unwrap()).join("progpu-native");
    let webgpu = out.join("webgpu");
    fs::create_dir_all(&webgpu).map_err(|error| error.to_string())?;
    let cache = cache_root();
    for header in &WEBGPU_HEADERS {
        let cached = fetch_file(&cache.join("webgpu-headers"), header)?;
        fs::copy(&cached, webgpu.join(header.name)).map_err(|error| error.to_string())?;
    }
    // The wgpu-native runtime of the pinned package, which the engine links and loads beside it.
    let wgpu_package = fetch(&cache, &WGPU)?;
    let runtime_prefix = format!("runtimes/{rid}/native/");
    extract(&wgpu_package, &webgpu, |name| name.strip_prefix(runtime_prefix.as_str()).filter(|rest| !rest.contains('/')))?;
    let windows = rid.starts_with("win-");
    let (wgpu_runtime, engine_runtime) = if windows {
        ("wgpu_native.dll", "progpu_native.dll")
    } else if rid.starts_with("osx-") {
        ("libwgpu_native.dylib", "libprogpu_native.dylib")
    } else {
        ("libwgpu_native.so", "libprogpu_native.so")
    };
    let wgpu_library = if windows {
        windows_import_library(&webgpu, rid)?
    } else {
        let library = webgpu.join(wgpu_runtime);
        if rid.starts_with("osx-") {
            // The package's library names its build directory; the engine has to find it beside
            // itself.
            run(Command::new("install_name_tool").args(["-id", "@rpath/libwgpu_native.dylib"]).arg(&library))?;
        }
        library
    };

    let build = out.join("build");
    let mut configure = Command::new("cmake");
    configure.arg("-S").arg(&native).arg("-B").arg(&build);
    if !build.join("CMakeCache.txt").is_file() {
        if Command::new("ninja").arg("--version").output().is_ok() {
            configure.args(["-G", "Ninja"]);
        }
        let compiler = env::var("PROGPU_NATIVE_CXX_COMPILER")
            .unwrap_or_else(|_| if windows { "clang-cl".to_string() } else { "clang++".to_string() });
        configure.arg(format!("-DCMAKE_CXX_COMPILER={compiler}"));
    }
    configure
        .arg("-DCMAKE_BUILD_TYPE=Release")
        .arg(format!("-DPROGPU_NATIVE_WEBGPU_INCLUDE_DIR={}", webgpu.display()))
        .arg(format!("-DPROGPU_NATIVE_WEBGPU_LIBRARY={}", wgpu_library.display()))
        .args([
            "-DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR=",
            "-DPROGPU_NATIVE_WEBSCENE_PROVIDER_INCLUDE_DIR=",
            "-DPROGPU_NATIVE_WEBSCENE_PROVIDER_LIBRARY=",
            "-DPROGPU_NATIVE_BUILD_SAMPLE=OFF",
            "-DPROGPU_NATIVE_ENABLE_CPP_MODULES=OFF",
            "-DBUILD_TESTING=OFF",
        ]);
    if rid.starts_with("osx-") {
        configure.arg(format!("-DCMAKE_OSX_ARCHITECTURES={}", if rid == "osx-arm64" { "arm64" } else { "x86_64" }));
    }
    run(&mut configure)?;
    let mut compile = Command::new("cmake");
    compile.arg("--build").arg(&build).args(["--config", "Release", "--target", "progpu_native"]);
    for library in SDK_LIBRARIES {
        compile.args(["--target", library]);
    }
    if let Ok(jobs) = env::var("NUM_JOBS") {
        compile.args(["--parallel", &jobs]);
    }
    run(&mut compile)?;

    // The SDK layout of the packages: headers, the runtime libraries, the static libraries.
    let root = out.join("sdk-layout");
    let _ = fs::remove_dir_all(&root);
    for dir in ["include", "runtime", "sdk"] {
        fs::create_dir_all(root.join(dir)).map_err(|error| error.to_string())?;
    }
    copy_files(&native.join("include"), &root.join("include"), |_| true)?;
    copy_files(&webgpu, &root.join("include"), |name| name.ends_with(".h"))?;
    fs::copy(webgpu.join(wgpu_runtime), root.join("runtime").join(wgpu_runtime)).map_err(|error| error.to_string())?;
    let built = if build.join(engine_runtime).is_file() { build.clone() } else { build.join("Release") };
    fs::copy(built.join(engine_runtime), root.join("runtime").join(engine_runtime)).map_err(|error| error.to_string())?;
    for library in SDK_LIBRARIES {
        let name = if windows { format!("{library}.lib") } else { format!("lib{library}.a") };
        fs::copy(built.join(&name), root.join("sdk").join(&name)).map_err(|error| format!("{name}: {error}"))?;
    }
    Ok(root)
}

/// Runs `command` to completion; an error names it.
fn run(command: &mut Command) -> Result<(), String> {
    let status = command.status().map_err(|error| format!("{command:?} could not be run: {error}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("{command:?} failed ({status})"))
    }
}

/// Copies the files of `from` (not its subdirectories) whose names `select` accepts into `into`.
fn copy_files(from: &Path, into: &Path, select: impl Fn(&str) -> bool) -> Result<(), String> {
    for entry in fs::read_dir(from).map_err(|error| format!("{}: {error}", from.display()))? {
        let entry = entry.map_err(|error| error.to_string())?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if entry.file_type().map_err(|error| error.to_string())?.is_file() && select(&name) {
            fs::copy(entry.path(), into.join(&name)).map_err(|error| error.to_string())?;
        }
    }
    Ok(())
}

/// The import library of the wgpu-native DLL in `dir`, made from the DLL's export table with the
/// librarian of the target's toolchain (as eng/build-progpu-native-windows.ps1 does).
fn windows_import_library(dir: &Path, rid: &str) -> Result<PathBuf, String> {
    let dll = fs::read(dir.join("wgpu_native.dll")).map_err(|error| error.to_string())?;
    let exports = pe_export_names(&dll).ok_or("the wgpu-native DLL has no readable export table")?;
    if exports.len() < 100 {
        return Err("the wgpu-native export table is incomplete".to_string());
    }
    let def = dir.join("wgpu_native.def");
    let mut text = String::from("LIBRARY wgpu_native\nEXPORTS\n");
    for name in exports {
        text.push_str(&name);
        text.push('\n');
    }
    fs::write(&def, text).map_err(|error| error.to_string())?;
    let library = dir.join("wgpu_native.lib");
    let target = env::var("TARGET").unwrap();
    let mut lib = cc::windows_registry::find(&target, "lib.exe").ok_or("lib.exe of the MSVC toolchain was not found")?;
    let machine = if rid == "win-arm64" { "ARM64" } else { "X64" };
    lib.arg("/nologo").arg(format!("/def:{}", def.display())).arg(format!("/machine:{machine}"));
    lib.arg(format!("/out:{}", library.display()));
    run(&mut lib)?;
    Ok(library)
}

/// The exported names of a PE image (its export directory), sorted.
fn pe_export_names(image: &[u8]) -> Option<Vec<String>> {
    let u16_at = |at: usize| Some(u16::from_le_bytes(image.get(at..at + 2)?.try_into().ok()?));
    let u32_at = |at: usize| Some(u32::from_le_bytes(image.get(at..at + 4)?.try_into().ok()?));
    let pe = u32_at(0x3c)? as usize;
    if image.get(pe..pe + 4)? != b"PE\0\0" {
        return None;
    }
    let sections = u16_at(pe + 6)? as usize;
    let optional = pe + 24;
    let optional_size = u16_at(pe + 20)? as usize;
    let directories = optional + if u16_at(optional)? == 0x20b { 112 } else { 96 };
    let export_rva = u32_at(directories)? as usize;
    let table = optional + optional_size;
    let offset = |rva: usize| -> Option<usize> {
        (0..sections).find_map(|index| {
            let header = table + index * 40;
            let address = u32_at(header + 12)? as usize;
            let size = u32_at(header + 8)?.max(u32_at(header + 16)?) as usize;
            let raw = u32_at(header + 20)? as usize;
            (rva >= address && rva < address + size).then(|| rva - address + raw)
        })
    };
    let directory = offset(export_rva)?;
    let count = u32_at(directory + 24)? as usize;
    let names = offset(u32_at(directory + 32)? as usize)?;
    let mut exports = Vec::with_capacity(count);
    for index in 0..count {
        let start = offset(u32_at(names + index * 4)? as usize)?;
        let end = start + image.get(start..)?.iter().position(|&byte| byte == 0)?;
        exports.push(String::from_utf8(image[start..end].to_vec()).ok()?);
    }
    exports.sort();
    exports.dedup();
    Some(exports)
}

/// A pinned file in `dir`, downloaded once and verified on every use.
fn fetch_file(dir: &Path, file: &PinnedFile) -> Result<PathBuf, String> {
    let path = dir.join(format!("{}-{}", &file.sha256[..16], file.name));
    if !path.is_file() {
        download(&file.url.to_string(), dir, &path)?;
    }
    verify(&path, file.sha256)?;
    Ok(path)
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
        let url = format!(
            "https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg",
            id = package.id,
            version = package.version
        );
        download(&url, cache, &path)?;
    }
    verify(&path, package.sha256)?;
    Ok(path)
}

/// Downloads `url` to `path` (in `dir`) through a partial file.
fn download(url: &str, dir: &Path, path: &Path) -> Result<(), String> {
    fs::create_dir_all(dir).map_err(|error| error.to_string())?;
    let partial = path.with_extension("partial");
    let status = Command::new("curl")
        .args(["--fail", "--silent", "--show-error", "--location", "--retry", "3", "--output"])
        .arg(&partial)
        .arg(url)
        .status()
        .map_err(|error| format!("curl could not be run to download {url}: {error}"))?;
    if !status.success() {
        return Err(format!("downloading {url} failed ({status})"));
    }
    fs::rename(&partial, path).map_err(|error| error.to_string())
}

/// Checks the SHA-256 of the file at `path`.
fn verify(path: &Path, sha256: &str) -> Result<(), String> {
    let mut hasher = Sha256::new();
    let mut file = fs::File::open(path).map_err(|error| error.to_string())?;
    let mut buffer = vec![0u8; 1 << 20];
    loop {
        let read = file.read(&mut buffer).map_err(|error| error.to_string())?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    let digest: String = hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect();
    if digest != sha256 {
        return Err(format!("{} has SHA-256 {digest}, expected {sha256}", path.display()));
    }
    Ok(())
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
