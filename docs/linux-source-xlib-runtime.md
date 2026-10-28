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

Production head `01471b2ea` passes both Linux runtime jobs, each with 94 native
ABI tests and the three-context/five-presentation control. The actual payloads
require GLIBC symbol versions no newer than 2.34; this is an ELF observation,
not runtime qualification on every distribution. Local real-package validation
found and repaired an extensionless-license package path, then passed eight
build/publish asset checks across portable, Linux x64, Linux arm64 and Windows
x64 consumers. Its original archive remains preserved. The initial Linux
Avalonia source job also exposed missing staging of the new runtime; that job
now depends on the same producer and downloads its payload before compilation.
The complete repaired-head CI and original applications still remain required.

The next macOS ARM64 native run passed WebScene in 44.91 seconds but stopped
while packing its managed consumer dependency: that job had downloaded Linux
runtime payloads only on Linux hosts. Native jobs also produce cross-platform
managed packages, so both Build and Release now download the original Linux
payloads on every native matrix host. Package validation remains mandatory;
the original failure is retained and the complete corrected producer must pass.


## MSAA attachment resolution and draw scissors

The same pinned GLES dependency resolves a multisampled color attachment with
`glBlitFramebuffer`. The last draw's scissor must not limit this attachment
operation: OpenGL ES 3.1 section 16.2 specifies that framebuffer blits are affected
by the scissor test ([specification](https://registry.khronos.org/OpenGL/specs/es/3.1/es_spec_3.1.pdf)).
The original `ResolveAttachment` path left that test enabled. A final caption
scissor could therefore resolve the caption while leaving the rest of the
single-sample target unchanged.

The reviewed external patch disables `SCISSOR_TEST` immediately before the
attachment resolve. It preserves the scissor box and all draw commands. The
next `SetScissor` explicitly enables its own test, while a new render pass resets
framebuffer state. Both intermediate and final attachment resolves use this
path; EGL surface presentation already disables the test independently. No
consumer draw, default sample count, source layout or WebGPU ABI changes.

The separate `native-resolve-scissor-probe.c` control uses the original owned
Xlib setup and verifies the loaded runtime path. It compares all 576 RGBA pixels
across three authored MSAA4 passes: a clipped draw over a clear with a prefilled
resolve destination, a different clipped draw after reuse, and a subsequent
full clear. It has its own 60-second process limit; the original native lifetime
probe and its limit remain unchanged. Both Linux architecture producer jobs
must run these controls against their actual staged runtime.

The motivating qualified LibreWPF source run retained complete body commands,
opaque vertices, ordered draw calls and matching live/cached contexts, yet its
ManagedPortable dialog captures showed only the final caption region. Its logs
identify OpenGL/llvmpipe and its MSAA allocation has four samples. The original
closure's runtime hash is retained; that observer did not independently report
the loaded native image path. These observations support the resolve defect but
are not fixed application pixels. The candidate still requires rebuilt
two-architecture producer controls, whole CI, package admission and the unchanged
source application visual checks before source pin advancement or release.

The original-runtime control in [run 40713803026](https://github.com/wieslawsoltes/LibreWPF/actions/runs/40713803026)
loads the exact retained ARM64 runtime
`bf19f91a7fcbe78044ea590a07fb554a963dc0609e8e9211d96d1050b22c9653`
and witnesses its actual image path with `dladdr`. All 576 pixels are checked:
the first clipped resolve leaves 128 blue destination pixels unchanged, the
second leaves 64 stale green pixels, and the final full clear has zero
mismatches. The probe exits 1 after 7.68 seconds, preserving the expected product
failure. The diagnostic workflow succeeds only in recording that negative
result; it does not qualify the original runtime. This is an authored GLES
control on llvmpipe, not the final source application or candidate result.

The repaired runtime at `1f81f86e6` passes the original surface/lifetime control
on both Linux architectures, but the new pixel control fails with counts
`0/13/0` on Mesa 25.2.8 llvmpipe. The shifted scissor has twelve half-covered
outside pixels and one quarter-covered corner. A
[paired direct-EGL diagnostic](https://github.com/wieslawsoltes/ProGPU/pull/414)
reproduces the fringe without wgpu. Disabling scissor for resolve and enabling a
full-target box produce byte-identical pixels; the latter alternative is rejected.

A separate [source-sample diagnostic](https://github.com/wieslawsoltes/ProGPU/pull/416)
captures all 2,304 samples of a four-sample texture before resolve. Its fringe is
already present in the source samples; all 576 resolved pixels match their
actual sample means. This is a texture attribution control, separate from the
original renderbuffer observation. Exact Mesa and Ubuntu source inspection
matches every captured sample to llvmpipe's lower scissor-plane arithmetic.
The upstream [scissor-plane correction](https://gitlab.freedesktop.org/mesa/mesa/-/commit/92550b106c70408cd9f5c25a50c311e68af06d6b)
addresses those edges and is present in Ubuntu's Mesa 26.0.8 source. These
observations do not change the expected pixels or qualify the failing driver.

The original LibreWPF modal captures record Ubuntu 26.04, installed Mesa 26.0.8
and actual llvmpipe rendering, but not the loaded GL version. They retain their
independent resolve defect and must still pass the original application checks.
Ubuntu 24.04 remains an existing WPF OpenGL CI environment; the new driver
finding does not silently remove that coverage or establish fixed application
pixels. No system driver, package dependency or CI acceptance gate is changed
by this diagnosis.

The [corrected-driver comparison](https://github.com/wieslawsoltes/ProGPU/pull/417)
reuses the exact x64 instrumented probe and runtime from the failed paired test.
In a digest-pinned private Ubuntu 26.04 container with verified Mesa 26.0.8
packages, all 576 original pixels pass in 0.16 seconds, including all three
frames and fourteen unchanged GL state observations. Actual loaded EGL/Gallium
images match their prehashed package files, and GL reports Mesa 26.0.8 llvmpipe.
The container and newly pulled image are removed after retaining the evidence.
This confirms the driver comparison on x64; production uninstrumented controls,
ARM64, the older-driver acceptance decision and full source/package gates remain.
