# Unified original native design-unit shaping

`NativeShaper` now connects the original ordinary, joining, Hangul and complex
script routes from one retained font/request to an owned positioned run. Native
32-byte glyph records and the original GSUB/GPOS executors remain the execution
format. No managed glyph buffer or managed executor substitutes for native code.

| Family | Connected implementation |
| --- | --- |
| Ordinary scripts | Original selected lookup order, global/ranged feature resolution, fractions, random-alternate state, directional fallback, metrics, kern/GPOS/attachments and output order |
| Arabic joining | Native seven-state/six-column automaton, five-scalar pre/post context, exact grapheme-relative dependency flags and original packed actions |
| Arabic stages | Directional pass, stretch feature, preprocessing/fractions, form features, required ligatures, presentation fallback, rclt/calt branch ordering and temporary LTR context reversal |
| Joining USE overlap | Original Arabic pre/post stages wrap the existing complete native USE flow; prior joining admission rejection removed |
| Arabic fallback | Original source cmap guards, form replacements, ordered three-component/two-component/mark ligatures, skipped components and fallback attachment metadata |
| Arabic stretch | Original run discovery, source direction indexing, width arithmetic, repeat/overlap/copy bounds, dependency flags and backward expansion after positioned output reversal |
| Hangul | Original full decomposition/composition, extended Jamo admission, preflight capacities, transient flag writer, Jamo feature consumer and cleanup expressions |
| Hebrew/Thai/Lao | Original Hebrew presentation combinations, font admission, cluster-bounded removal; Thai/Lao Sara-Am expansion, above-mark movement and cluster policy |
| Fallback marks | Complete original design-unit class recategorization, source extent query, base/component/class walk, prior-positioned skip, two-stage int16 clamps, dependency flags and advance zeroing |
| Fallback extents | Explicit exact glyf-header policy versus original static/varied TrueType/CFF1/CFF2 selection and path-control reduction |

## Public API and ownership

The public types remain under `text::native_pipeline`:

* `NativeShaper::{new, requirements, shape, shape_into}`;
* `NativeShapeRequest`, `NativeShapeRequirements`, `NativeShapeScratch`, `NativeRun`;
* `NativeFallbackBounds::{Header, Outline}`;
* the existing `NativeComplex*` names alias these unified request/scratch/run
  types, and `NativeLatinShaper` retains its independent ASCII specialization.

The unified request defaults to Latin and now freezes decoded `post_context`
alongside `pre_context`. Its explicit fallback-bounds policy defaults to the full
outline route; `Header` preserves the original null-scratch native query. Existing
complex callers supply their script and normalization plan as before. Indic/USE
retain their original normalization-owner requirement. Features and coordinates
are copied into the immutable font/selection generation before execution.

The caller owns bounded glyph, joining action/flag, stretch run, grapheme,
normalization, attachment and lookup storage. `with_glyph_capacity` permits an
explicit larger inventory for font-authored substitutions or stretch expansion;
execution never grows the glyph buffer or retries partial work implicitly.
`shape_into` exposes native live-prefix/partial-error behavior; only successful
`shape` results publish an owned font/scalar/coordinate/glyph generation.
Arabic action retirement follows the source stage boundaries, including the
separate complex guard, pre-positioning failures and stretch completion.

The outline extent cache is caller-owned scratch, indexed by source glyph slot
and stamped once per shaping invocation. It cannot reuse bounds from another
font or variation request. Full outline misses use the already ported original
native TrueType/CFF decoders, whose Rust APIs publish temporary owned paths;
those temporary owners retire immediately after bounds reduction. This is an
explicit allocation difference from C++'s supplied point/path scratch, not a
claim that the Rust route is allocation-free. The glyph run itself remains
bounded caller storage. Cache entries retain exact rounded source bounds, not
substituted host font extents or curve-extremum bounds.

## Preserved original discrepancy

The checked-in native Hangul preprocessing source writes `0x00001800` at shift
11, whereas its native shaper consumes and clears `0x30000000` at shift 28.
Both original expressions are preserved in this port. They are not silently
unified with managed transient metadata. Consequently, implementation presence
does not establish native/managed Hangul output equivalence; the original-source
discrepancy must remain visible in the later differential validation.

## Exact algorithm provenance

Paths are relative to `src/ProGPU.Native/src/Text/`.

| Original source | SHA-256 |
| --- | --- |
| `Shaping/progpu_native_unicode_arabic.cpp` | `b82e7326a329b07c06ac9014d1917871e9fe4db3120266f420a03df6658e512a` |
| `Shaping/progpu_native_arabic_actions_internal.hpp` | `500b5d70512b5d0252dd602bfc1d6e9c97136face40c04c2d5b0baf9673711ed` |
| `Shaping/progpu_native_arabic_fallback.cpp` | `5f7c4c43c95f0a2479129855294c58aba418e5ce7eb682904e9743a95f858721` |
| `Shaping/progpu_native_arabic_stretch.cpp` | `0c35e61d9f6314dc2bdcc5b6a03f1c4a7c0d4b89616e848bf5cea3f252a58f2c` |
| `Shaping/progpu_native_open_type_hangul.cpp` | `b3d68037d59bb3553dd1bf202ece76d9060bfe66b8581639361591103151efc3` |
| `Shaping/progpu_native_fallback_marks.cpp` | `dba3622518f53e434f8ea5dd69790041b67787a3d9e8d48f284e446e4bacb466` |
| `Font/progpu_native_font_bounds.cpp` | `5b548ac58ffcb322683f703e551eaccb6ebbfb5ab3ca39b8df47a53eed15d584` |
| `Shaping/progpu_native_open_type_preprocess.cpp` | `eac1e7379742865c85fc160511b5ab0b0bcca5bff8a655efe16ab73d8d3d3794` |
| `Shaping/progpu_native_open_type_shaper.cpp` | `1590eacb54721eb1c9ae5bd71b824c91ccc0893c8e5a2969625d5bc40131ddce` |
| `Shaping/progpu_native_open_type_feature_values.cpp` | `8553eb85882daadedefaa08ad9dfc058a987d83547848072d38a4f5a05d2b132` |

The joining classifier reuses the existing independently generated Unicode 17
property inventory; presentation forms and ligature facts reuse the prior
independent original-ProGPU observations. Only immutable classification/data is
shared. Native scalar validation, bounded context visits, state transitions,
flags, cmap probes, mutations and ordering are ported from the native sources
above. No foreign implementation or managed shaping executor is imported.

## Independent remaining source families

The subsequent [native-verify-device-policy-port.md](native-verify-device-policy-port.md)
connects automatic/native fragment Verify, actual-device fragment callbacks, and
the complete captured-device space/fallback-mark/stretch policy algorithms.

* Actual hinted-device font/outline/metric producer, captured descriptor mapping,
  callback-owned positioning/boundary capture and source providers remain separate.
  The subsequent [device run port](native-device-run-port.md) connects the complete
  prepare/capture/device-shaper lifecycle to an explicit actual-provider trait;
  it does not implement the native hinted interpreter/capture producer.
* Native paragraph font fallback and bidi/run itemization/provider orchestration
  remain independent of the complete selected-font single-run stage families.
* Original source application/package/browser execution and native/managed
  differential output/performance qualification remain required.

## Architecture and deferred evidence

The prior `DESIGN.md` research remains applicable: Skia/HarfBuzz describe CPU
stage boundaries; DirectWrite reusable positioned output; Direct2D/Win2D separate
resource domains; WebRender/Vello/Parley immutable retained generations and
demand-driven realization. These are architecture references only. Original
ProGPU source is the implementation authority.

Per the mandatory port-first instruction, no compiler, tests, lint, browser,
benchmarks or new test fixtures were run or authored. Only touched-source
formatting was performed. No build, trace or benchmark artifacts were generated.
The source discrepancy, error-order behavior, malformed-font inventory, browser
execution and cold/warm allocation/performance evidence remain deferred.
