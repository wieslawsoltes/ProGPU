# Source Xlib display ownership

An Xlib window can coexist with a Wayland session. Choosing an EGL platform from
the process environment before receiving that window can create a Wayland EGL
context and then fail the first Xlib surface configuration. LibreWPF issue
[187](https://github.com/wieslawsoltes/LibreWPF/issues/187) retains the original
public preview69 failure and the independent headless control.

The Linux managed backend now obtains the screen from the actual live source
window and passes its borrowed display to `progpuWgpuCreateInstanceXlibV1` before
initializing GL. The standard WebGPU descriptor and chain are forwarded intact.
The current native surface wire carries screen zero; other screens fail before
native initialization. Forced Vulkan, other operating systems, headless contexts
and externally supplied devices keep their existing paths. Linux Xlib contexts
whose backend mask includes GL require the matching runtime; an older runtime
cannot silently select the wrong platform.
If explicit GL initialization fails in a multi-backend instance, the other
requested backends retain their existing eligibility; no ambient GL instance is
substituted. Forced GL fails initialization instead.

## Reviewed dependency

The implementation changes to wgpu live only as reviewed dependency patches in
[`eng/wgpu-xlib`](../eng/wgpu-xlib), applied to exact external source checkouts.
They are not copied into ordinary ProGPU implementation sources. Both upstream
projects retain their MIT/Apache licenses; the package also retains the WebGPU
header license. Standard WebGPU records, symbols and the existing native ABI
remain unchanged. The added entry point is Linux-only and versioned.

The GL backend opens a private Xlib connection to the explicitly named source
server. It never initializes or terminates the borrowed toolkit EGL display.
It rejects aliasing with that display or another live explicit backend. The
actual context leases retain the owned connection, EGL context, pbuffer and
display until the last user retires. Releasing an instance while its surface
and device remain alive must not tear down those resources. A later source
surface must carry the same original display and screen.

The architecture and primary sources are recorded in
[`research-and-design.txt`](../eng/wgpu-xlib/research-and-design.txt).
Display/context selection happens once per instance. The live-display uniqueness
guard scans the current explicit context inventory at creation and retirement;
it adds no per-frame lookup. Drawing, text, cache identity and shader execution
are unchanged. No rendering-performance improvement is claimed.

## Building and packaging

On each matching native Linux architecture, install Rust 1.99.0, Clang/libclang
18 and the normal X11/EGL build dependencies, then run:

```sh
python3 eng/build-wgpu-native-linux.py --rid linux-x64
# Run independently on arm64 with --rid linux-arm64.
```

The builder uses fresh external checkouts and a locked dependency graph, runs
generated ABI tests, verifies ELF architecture and exported symbols, and writes
an immutable payload plus hashes, licenses, compiler identity and ELF version
requirements. It refuses to overwrite existing payloads. A failed build retains
its work directory for diagnosis. Completed work directories may be removed
after preserving the payload and qualification receipts.

`ProGPU.Backend` packs both real Linux payloads from
`artifacts/progpu-xlib/package`. `ProGpuXlibRuntimeRoot` selects a different
producer location. Missing, altered, wrong-architecture or unexpected inputs
fail packaging. Native C++ builds use the same verified staged dependency.
No consumer binary or NuGet cache entry is patched in place.

Package consumers receive normal `runtimes/linux-*/native` assets. The transitive
target removes only the known older Silk Linux library, covering RID-specific
build, portable build and publish lists. Other platforms and caller libraries
remain independent. Project-reference applications can use the same payload via
`ProGpuXlibSourceRuntimeRoot`; repository applications automatically discover
the default staged directory. This does not build a missing native dependency.

## Evidence and remaining qualification

The original public-package app failed before its first presentation on owned
GNOME 50/Xwayland while its unchanged headless X11 counterpart passed. The
independent EGL connection test established distinct private displays and kept
the borrowed source uninitialized. Hosted native experiment 39086711084 passed
three context creations and five actual presentations, including release of an
instance before its device/surface, survival of another context and replacement.
It used software llvmpipe, not accelerated hardware. The original failed
Clang/bindgen experiment is retained; explicit libclang 18 corrected that build.

Managed descriptor/rejection controls and package admission controls are local
checks. The production workflow rebuilds both Linux architectures, checks ABI and
native lifetime behavior, then supplies the complete native and managed package
matrix. Original source application pixels, the mixed-session app, final package
closure and whole CI remain required before release. The authored native probe
does not by itself close the source application issue.
