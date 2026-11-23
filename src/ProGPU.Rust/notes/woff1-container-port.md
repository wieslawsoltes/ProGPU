# Original WOFF1 font normalization

Implementation only; no builds, tests, fixtures, browser or performance runs.

The native `sfnt_container_requirements` and caller-buffer `normalize_sfnt_container_into` port the complete original WOFF1 header/directory/extent/search-parameter admission. Every compressed table is decoded into bounded caller scratch before output mutation. The second deterministic decode writes the admitted output prefix; padding is zero, tails remain unchanged, and source table order/checksum words are copied verbatim. Plain SFNT/TTC or unknown signatures retain their original passthrough policy. Native normalization reuses the exact owned zlib/DEFLATE implementation already used by PNG, including Adler checksum and exact compressed-byte consumption. Safe disjoint borrows replace raw overlap checks.

Owned native normalization preserves the same Arc for passthrough and publishes a complete private output for WOFF1. The independent managed normalization retains the original checked signed-int output bounds and ZLibStream-style exact expanded length plus one-byte overflow probe. It uses the existing pure Rust flate2 backend; it does not impose native compressed-byte-consumption admission on the managed path. ManagedFont construction and the owned typeface/manager factories select this managed policy. Both original implementations reject WOFF2.

No metadata/private block validation, tag sorting, checksum rewriting, duplicate rejection or font semantic validation is invented at the container boundary. Font parsing remains a separate operation on the completed normalized generation. Rust FontError adds distinct container/compression variants without renumbering existing public variants; this enum is not a new native ABI declaration.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Text/SfntFontContainer.cs` | `e436e5f92ba710c6c332a6f640a87a1e34c1081120c8bddc0175100215d5c082` |
| `src/ProGPU.Native/src/Text/progpu_native_woff1.cpp` | `0d996060a2691a26edbad08873bb9a51fb8c77d0ef4f9c94d98248ec6179e552` |
| `src/ProGPU.Text/TtfFont.cs` | `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b` |

Original source byte/error/admission and allocation controls, managed zlib malformed-stream behavior, native/browser corpus, performance and package qualification remain open. No build or trace artifacts were created.
