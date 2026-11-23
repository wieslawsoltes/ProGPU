# Native source-double paragraphs and version-two resources

Implementation-only increment. No compiler, lint, tests, fixtures, benchmarks,
browser or pixel work ran. Only touched source files were formatted; no build or
trace artifacts were produced. Runtime import admission below is production code,
not a validation run or qualification claim.

## Original provenance

All algorithms come from this repository's original native implementation. Paths
in this table are relative to `src/ProGPU.Native/src/Text/`.

| Original | SHA-256 | Port |
| --- | --- | --- |
| `Font/progpu_native_hinted_source_policy.cpp` | `8794caea7fdde7c8cca1d3d2d5bf2f670488d140741cf108c0145b4897458c5f` | Four device-em policies; physical ties-to-even advances; independent ideal-unit advances/offsets; integer line accumulator and floored constraints |
| `Interop/progpu_native_hinted_source_fitting.cpp` | `5a4b4b572a302b0c66f5bc43da8b802fe774d888055bf818d57725daf08e9aba` | Complete bounded candidate fitting, actual placement slices, safe boundaries, intrinsic widths and actual-generation witness admission |
| `progpu_native_text_layout.cpp` | `49a7106575011056c3ca3c4362138d210cefddf53492f3e5a61558ddbc664ef2` | Double requirements/scanner and double `layout_measured_core`, including source soft-wrap spaces and independent draw advances |
| `progpu_native_text_layout_source_internal.hpp` | `2d64bb724aeb7ad73e133dcef8c2854d8d1de5239a8da87120234655146a92b1` | Separate source-double metrics, positions, line frames and width policy |
| `progpu_native_text_interaction_impl.hpp` | `65266bdae4fe8939bc485a83abb2c6a5a7034b108fcdfbc8659cfde13355eeb0` | Shared double advance-box/caret emitter, exact admission, hit/caret/move/selection arithmetic |
| `Interop/progpu_native_hinted_paragraph_interaction.cpp` | `dab97e377a7893111c720063088708669b1f6efe63d71378d566f9c97c448232` | Source interaction generation and per-line views |
| `Interop/progpu_native_hinted_source_resource_cache.cpp` | `da6f7ea19254692030cd7d1c29eb91b6ac8942cb85c76bcaa43bf15928b90032` | Actual raw/effective/prepared occurrence witnesses and full v2 cache |
| `Interop/progpu_native_hinted_source_import_validation.inl` | `ffbea13efbfd4177d22e39dff4547b84c0e260e74d9e2de0be92ac5a835c93ed` | Complete paired v2 admission, budget, prepared/slice proof, exact double writer and interaction records |
| `Interop/progpu_native_hinted_source_transport.inl` | `26578ad8830f7c879fe045ac16988dce9d5a4331987716cb0ff8d42805c883c6` | Source options/style/view/intrinsic and retained reflow transport |
| `Interop/progpu_native_text_shaping_interop.cpp` | `501d9e190bd966ce2885ff71960ef1b1b24dbd9ce7f786d86817e6261e5b05dd` | Source paragraph append/projection, fitted measured writing and source `reflow_hinted_paragraph_core` |

## Connected API and ownership

`text::native_paragraph::NativeSourceParagraph::shape` takes the original font and
style inventory, explicit `NativeSourceStyle` and `NativeSourceOptions`, original
native shaping/layout requests and an actual `NativeHintedProvider`. It admits
all source em/device/source-scale/line-metric shadows before provider work, then
uses the existing full native paragraph shaper. The raw paragraph route continues
to keep its separate policy. Source advances are projected before logical append
publication; source offsets and ideal-unit geometry remain independent doubles.

`reflow(input_start, maximum_width: f64)` retains the full immutable source/run
owner, native prepared glyphs and original capture. It replays only actual
retained positioning recipes at the original unsafe line/run intersections.
Whole-run policy keeps original placement but still requires prepared-boundary
proof. Candidate visits retain the original `1 << 24` cap. Unsupported indivisible
emergency boundaries and unavailable native placement recipes reject explicitly.
Intrinsic widths share those exact policies and original source whitespace;
`intrinsic_widths()` distinguishes unrequested measurement with `None`.

The source writer preserves the original sequential double pen and line-top
prefix, independent paired metric lanes, exact L1/L2 group order and full levels.
Only complete U+0020 tail clusters receive the explicit source soft-wrap policy.
An opposed suffix keeps its own measured space extent; content origins remain
independent. The first separator overflow and partial-space emergency-boundary
rules are ported. Integer ideal-unit line measure never replaces glyph advances
or repairs a completed floating width. Each raster shadow is narrowed from its
original double separately. Double interaction uses the same original advance
emitter for construction and import comparison, including the float literal
caret-dedup threshold promoted to double, first distance ties and rectangle merge.

`NativeParagraphGeometry::new_source` retains the actual source paragraph and
`NativeSourceResource` v2 alongside the existing combined captured geometry.
`into_replay` and existing `Scene::draw_native_paragraph` carry the complete source
owner through style groups, selected views, no-ink slots and actual-DPI admission.
`source_interaction()` exposes only admitted original double records. No new
Scene or renderer path was introduced.

`NativeParagraphGeometry::import_source` / `NativeSourceResource::import_geometry`
admit all base records first, then the complete source-v2 inventory. Raw original,
effective selected-slice and pre-positioning records remain distinct. Admission
checks font/style/device identity, complete owner maps, prepared flags/direction,
source safe edges, exact line/run slice intersections, independent integer widths,
all double/float writer fields and original interaction bits before copying and
publishing. Import never executes fonts, creates a fake hinted provider or invents
a paragraph generation. The 256 MiB additive budget covers transported records
and the retained writer scratch inventory using safe Rust record sizes; raw base
font and outline limits remain separate. Interaction comparison streams through
the original emitter without allocating duplicate boxes/carets.

`copy_source_into` preflights both complete base and extension buffer inventories
before either write. Copies preserve all unused output tails and immutable source
bytes. Safe exclusive borrows replace native pointer/full-capacity overlap checks.
Rust enums/bools/slices replace invalid raw enum/boolean/pointer representations;
resource version, flags, reserved words and all represented record fields still
have explicit admission. This is a safe Rust transport, not a C ABI claim.
Large vectors and candidate copies reserve fallibly. Arc ownership and the
existing nested font/style/request clone behavior retain standard Rust allocation
semantics; this is not a recoverable-all-allocation guarantee.

## Independent remaining scope

- Actual safe pinned FreeType binding/execution, producer receipts and original
  native/browser/RID/package/pixel/cold/warm/performance qualification remain open.
- Original nominal GlyphRun offset/frame APIs are implemented in the subsequent
  `native-nominal-source-replay-port.md` increment. Source-host pixel qualification,
  RTL GlyphRun equivalence and ordinary Display/GDI/GDI+ admission remain separate.
- Generic unhinted paragraph inline objects, tabs, trimming, fragment/exclusion/
  floating layout, vertical paragraphs, source platform discovery and native
  context plan-cache/diagnostic counters remain separate layers.
- Imported geometry deliberately has no actual paragraph/provider to reflow.
  Source reflow requires its retained `NativeSourceParagraph`; no new source
  font execution or reconstructed ownership is inferred from flat records.
