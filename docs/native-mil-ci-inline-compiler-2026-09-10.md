# Native inline paragraph compiler checkpoint

The ProGPU Build run [34516759144](https://github.com/wieslawsoltes/ProGPU/actions/runs/34516759144/job/103003974331)
at source `26a6030f6a72bdb2256a8fba80d79770086e5544` failed MSVC's
warnings-as-errors check. The inline-object fixture declared `positioned` inside
a scope whose containing function already used that name (C4456).

Rename only that fixture buffer to `inline_glyphs`. Preserve every assertion,
compiler warning level and production implementation. The managed implementation
is unaffected: this is a C++ test-local identifier correction, not a rendering
or text contract change.

Local verification on macOS arm64 rebuilt `progpu_native_text_interop_tests`
and ran its CTest entry successfully (1/1, 0.50 seconds). This does not prove
MSVC compatibility; the corrected source must pass the Windows CI compiler lane.

The separate [SVG representative performance job](https://github.com/wieslawsoltes/ProGPU/actions/runs/34516759230/job/103003973398)
failed its checksum assertion: expected `1eff2c6cbe8504b8`, observed
`1eff2c56a78504b8`. Reported median/p95 were 3216.380/3235.656 ms and
175592952/176681592 allocated bytes. The reported violation is output identity,
not a timing-budget violation. Do not update the expected checksum or weaken
the gate without identifying and reviewing the changed fixture output.

Latest fetched main is `cde81083` (PR #160), which introduces that performance
gate after the previously integrated `8842f828` base. Its integration and the
checksum investigation remain required. No package, application, platform or
merge qualification is implied by this checkpoint.
