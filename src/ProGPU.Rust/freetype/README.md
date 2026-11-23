# ProGPU original FreeType binding

This opt-in companion owns the C ABI boundary for ProGPU's original FreeType
2.14.3 hinted-outline producer. The public API is safe Rust; the rendering crate
continues to forbid unsafe code. The private `src/ffi.rs` is the only unsafe
implementation and declares the documented public FreeType ABI, never a copied
interpreter, renderer or third-party wrapper implementation.

The dependency is never downloaded, configured or built by Cargo. A build requires
`PROGPU_FREETYPE_MANIFEST` naming the existing original external
`install/progpu-freetype.json`. `PROGPU_FREETYPE_PYTHON` optionally names the Python
interpreter for the original read-only verifier. The supported original targets
are win/osx/linux x64 and arm64; browser and 32-bit builds are not admitted.

The included verifier and preparation module are exact owned ProGPU source copies.
Only `validate_install` executes: signed source identity and clean checkout, static
isolated Release configuration, exact architecture of every archive member,
complete headers/notices, version and original recorded producer controls are
required. No preparation, download, font probe or test entry point is invoked.
After admission, the archive and notices are retained in Cargo's private output
and rehashed against that exact receipt. Only the private static archive name is
linked; no generic freetype or pkg-config fallback is available. The generated
build receipt contains the actual inventory hashes. Cargo metadata exposes the
retained receipt/notices for downstream distribution; original FTL notices must
accompany any redistributed dependency binaries. Publishing the companion package
is a separate prerequisite to publishing a dependent registry release.

`Library` uses actual `FT_Init_FreeType`/`FT_Done_FreeType`, version observation,
interpreter property setting and readback. Opening a face consumes the library
and retains immutable memory bytes; face destruction precedes library retirement,
which precedes the byte owner. `Face` owns MM metadata allocation/free, complete
fixed-point variation selection, exact nominal fractional size requests, actual
size metrics and null-transform phase selection. Only the original Normal,
Monochrome, HorizontalLcd and Unhinted outline-load flag combinations are exposed.
Each load checks its actual glyph index/outline format, original counts/pointers
and `FT_Outline_Check`, then immediately copies unchanged integer points, raw tags,
contour ends, flags and complete metrics. Owned snapshots can transfer their
arrays directly into ProGPU's atomic whole-batch transport.

Every native owner is non-Clone and thread-confined; no unsafe Send/Sync assertion
is used. A mutable face borrow excludes concurrent slot loads. No raw pointer,
borrowed C array or mutation callback escapes the safe API. Frozen copied glyphs
may be moved independently of the library lifetime. Runtime module lookup requires
all original seventeen FreeType functions to belong to the executing producer's
image; version and archive metadata alone do not establish that fact.

The private boundary trusts the admitted FreeType implementation to satisfy its
published allocation/layout contracts, as every C binding must. Its documented
public-prefix declarations use the target's actual `c_long`, C integers and C
pointer alignment. Windows signed-long narrowing remains separate from Unix
64-bit-long storage. This authored boundary has not yet been compiled, loaded or
qualified. Original native/provider/package/source-pixel and browser gates remain
independent; normal source policies and platform rendering modes are unchanged.
