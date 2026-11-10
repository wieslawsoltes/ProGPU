# Managed legacy symbol observations

The two `managed-symbol-*.bin` files contain sorted big-endian `(code, glyph)`
u16 pairs observed through original ProGPU `TtfFont.GetGlyphIndex`. They are data,
not transcribed implementation or source mapping arrays. The generator builds an
independent identity-PUA symbol font with OS/2 version zero and page B2/B3, queries
every BMP code, and retains nonzero nonidentity results. It then removes each of
the 768 PUA codes in turn and queries that code again, preventing direct coverage
from concealing a fallback mapping. This observes 66,304 queries per page.

Run `PROGPU_RUST_DOTNET=<dotnet-10> python3
eng/progpu-generate-rust-symbol-maps.py --check` from the repository root. Without
`--check`, the command regenerates the captures. Actual original API output is the
authority; the script does not read or parse the source mapping arrays. The source
runtime's existing generated-data dependency remains in the managed control only.
`perf/managed-mapping-symbol-capture.json` records source, output and inventory
hashes. Mandatory Linux CI checks these exact bytes against the original source.

The Rust mapping inventory contains 240 simplified and 285 traditional entries.
No font, shaping implementation or third-party source is bundled with these data.
These observations preserve the current ProGPU policy, not a general Windows
font-page or original source-application qualification.
