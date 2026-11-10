//! Builds the ProGPU native engine for the target from the ProGPU sources and links it statically:
//! the renderer archive (`progpu_native_static`, the engine without a WebGPU library: its WebGPU
//! calls are linked to the wgpu-native crate, a dependency of this crate) with the archives it is
//! made of, and the C ABIs of `native/` over the retained scene builder and the font stack.
//!
//! The sources are `PROGPU_SOURCE_DIR`, or the ProGPU checkout this crate is part of
//! (`bindings/rust/progpu-native-sys`, as in a git submodule of ProGPU). The engine is built with
//! CMake (Ninja when present) into `OUT_DIR`, incrementally, against the pinned `webgpu.h` and
//! `wgpu.h` (SHA-256 verified). Crates that compile C or C++ against the engine read
//! `DEP_PROGPU_NATIVE_INCLUDE` and `DEP_PROGPU_NATIVE_SDK`.

use sha2::{Digest, Sha256};
use std::env;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::Command;

const VERSION: &str = "0.1.0-preview.70";

/// A pinned file of a source repository.
struct PinnedFile {
    name: &'static str,
    url: &'static str,
    sha256: &'static str,
}

// The standard WebGPU header the engine is compiled against: that of the wgpu-native the bindings
// link (webgpu-headers 673658bc, the submodule of wgpu-native v29 and of its progpu/wgpu-30 branch).
const WEBGPU_HEADER: PinnedFile = PinnedFile {
    name: "webgpu.h",
    url: "https://raw.githubusercontent.com/webgpu-native/webgpu-headers/673658bc2bd70ec39fc55ebe6bb0173cf6d0a603/webgpu.h",
    sha256: "a483031c3fed05ea5dd1c74082a71676c46c5b2b820ccca10da515c033efc997",
};

/// The static archives of the engine, in link order (an archive before those it uses).
const SDK_LIBRARIES: [&str; 9] = [
    "progpu_native_dawn",
    "progpu_native_mil",
    "progpu_native_direct2d_core",
    "progpu_native_scene_builder",
    "progpu_native_hit_testing",
    "progpu_native_text",
    "progpu_native_geometry",
    "progpu_native_image",
    "progpu_native_compression",
];

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=PROGPU_SOURCE_DIR");
    println!("cargo:rerun-if-env-changed=PROGPU_NATIVE_CACHE");
    println!("cargo:rerun-if-env-changed=PROGPU_NATIVE_CXX_COMPILER");

    let Some(rid) = runtime_identifier() else {
        // No desktop engine for this target (the browser builds the engine with Emscripten).
        return;
    };
    let Some(source) = source_checkout() else {
        panic!(
            "progpu-native-sys {VERSION} builds the engine from the ProGPU sources: build it inside a ProGPU \
             checkout (a git submodule of ProGPU) or set PROGPU_SOURCE_DIR to one"
        );
    };
    let root = build_from_source(&source, rid)
        .unwrap_or_else(|error| panic!("ProGPU native engine from the sources in {}: {error}", source.display()));
    let include = root.join("include");
    let sdk = root.join("sdk");
    println!("cargo:include={}", include.display());
    println!("cargo:sdk={}", sdk.display());
    compile_native_abis(&include, &sdk);
}

/// Compiles the C ABIs of `native/` (over the retained scene builder and over the font stack) and
/// links them with the static archives of the SDK.
fn compile_native_abis(include: &Path, sdk: &Path) {
    let mut build = cc::Build::new();
    build.cpp(true).std("c++20").include(include).include("native");
    for name in ["progpu_native_scene_builder_c", "progpu_native_font_c"] {
        println!("cargo:rerun-if-changed=native/{name}.cpp");
        println!("cargo:rerun-if-changed=native/{name}.h");
        build.file(format!("native/{name}.cpp"));
    }
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

/// Builds the engine of the checkout at `source` for `rid` (in `OUT_DIR`, incrementally) as static
/// archives: the engine of the standard `webgpu.h` (`progpu_native_dawn`, whose WebGPU calls go
/// through the procedures a host resolves) and the archives it is made of. Returns the SDK layout
/// (`include/`, `sdk/`).
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
    let cached = fetch_file(&cache_root().join("webgpu-headers"), &WEBGPU_HEADER)?;
    fs::copy(&cached, webgpu.join(WEBGPU_HEADER.name)).map_err(|error| error.to_string())?;
    let windows = rid.starts_with("win-");

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
        .arg(format!("-DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR={}", webgpu.display()))
        .args([
            "-DPROGPU_NATIVE_DAWN_LIBRARY_TYPE=STATIC",
            "-DPROGPU_NATIVE_BUILD_WGPU_TARGET=OFF",
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
    compile.arg("--build").arg(&build).args(["--config", "Release"]);
    for library in SDK_LIBRARIES {
        compile.args(["--target", library]);
    }
    if let Ok(jobs) = env::var("NUM_JOBS") {
        compile.args(["--parallel", &jobs]);
    }
    run(&mut compile)?;

    // The SDK layout: the engine's headers with the WebGPU header, and the archives.
    let root = out.join("sdk-layout");
    let _ = fs::remove_dir_all(&root);
    for dir in ["include", "sdk"] {
        fs::create_dir_all(root.join(dir)).map_err(|error| error.to_string())?;
    }
    copy_files(&native.join("include"), &root.join("include"), |_| true)?;
    copy_files(&webgpu, &root.join("include"), |name| name.ends_with(".h"))?;
    let built = if build.join(if windows { "progpu_native_dawn.lib" } else { "libprogpu_native_dawn.a" }).is_file() {
        build.clone()
    } else {
        build.join("Release")
    };
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

/// A pinned file in `dir`, downloaded once and verified on every use.
fn fetch_file(dir: &Path, file: &PinnedFile) -> Result<PathBuf, String> {
    let path = dir.join(format!("{}-{}", &file.sha256[..16], file.name));
    if !path.is_file() {
        download(&file.url.to_string(), dir, &path)?;
    }
    verify(&path, file.sha256)?;
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
