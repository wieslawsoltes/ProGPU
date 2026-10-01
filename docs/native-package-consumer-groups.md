# Native package consumer qualification groups

## Acceptance and blocking evidence — 2026-09-14

The acceptance application is **ProGPU.Wpf.ShowcaseApp**. Its native package
startup and presented input depend on qualified ProGPU packages. The bounded
change here is CI scheduling, not application or renderer behavior.

At `0ac6a5ff79d79e7a6e5a2e5b0488955cfb2256d7`,
[Build 3047](https://github.com/wieslawsoltes/ProGPU/actions/runs/34808350830)
completed all other checks, but its general Windows package consumers were
canceled. Both check annotations explicitly report the 15-minute job limit:

- [Windows x64](https://github.com/wieslawsoltes/ProGPU/actions/runs/34808350830/job/103870004049):
  05:37:11–05:52:22 UTC. Ordered core, default JIT core and all nine focused JIT
  cases passed; NativeAOT core and three focused cases passed before cancellation.
- [Windows ARM64](https://github.com/wieslawsoltes/ProGPU/actions/runs/34808350830/job/103870004050):
  05:36:38–05:51:45 UTC. Ordered core, default JIT core and eight focused JIT
  cases passed before cancellation; NativeAOT was not reached.

No consumer assertion failure preceded these cancellations. Repeated cold native
initialization made the serial workload exceed the job budget. Passing individual
cases does not qualify this canceled Build, and consumers must still require an
entire successful Build for the exact dependency commit.

## Scheduling contract

Both Windows RIDs now have four independent jobs, each retaining the original
15-minute limit. Linux and macOS retain the full original sequence in one job
per RID. Every focused case still runs in a fresh process under both JIT and
NativeAOT; a JIT binary is built once per job instead of rebuilt per invocation.

| Suite | Original cases retained |
| --- | --- |
| `core` | Full ordered-query consumer, default broad JIT consumer, default broad NativeAOT consumer |
| `drawings` | DrawingGroup, GlyphRunDrawing, text render options |
| `visuals` | Visual clip, opacity mask, effect |
| `guidelines` | Visual guideline, DrawingImage, drawing guideline |
| `all` | Core sequence plus all nine focused cases, for each non-Windows RID |

The existing x64 default broad `--mil-only` selection is unchanged; its ordered
core still renders the full scene on the explicitly selected software adapter.
Independent DX12 NativeAOT and ordered-query differential jobs are unchanged.
Failures propagate normally; there is no `continue-on-error`, shortened fixture,
relaxed oracle, renderer fallback, longer timeout or new runtime policy.

`eng/progpu-native-package-scenarios.sh` is the shared JIT/NativeAOT selector.
`eng/progpu-verify-native-package-scenarios.sh` compares both its full list and
the concatenated groups against an independent inventory of the original nine
flags, rejects duplicates/missing cases, checks empty core and rejects bad input.
The workflow runs this verifier before default JIT consumers. Shell syntax and
GitHub Actions expression validation remain required. macOS Bash 3 must retain
empty argument-array compatibility; do not enable nounset around those arrays.

## Applicability and provenance

This is an original refactoring of this repository's existing package workflow
at `0ac6a5ff`. No third-party implementation was introduced. Managed renderer,
C++ renderer, shared shaders, native ABI, test assertions, package versions and
adapter/compiler/query selection are unchanged. No paired product-code change
is applicable: only the scheduling of the same native-package consumers changes.
This change makes no product performance or completed qualification claim.

## Additional owned hinted-text processes

The original nine MIL selectors and core sequence remain unchanged. The shared
selector additionally emits three independent processes under both JIT and
NativeAOT: existing CPU device/hinted-paragraph controls, loaded stock paragraph
rendering, and loaded typed-Dawn paragraph rendering. `text` groups the CPU and
stock cases; `text-dawn` contains the Dawn case. Both Windows RIDs have those two
additional independent jobs under the original 15-minute bound; non-Windows
`all` retains the original nine cases and adds all three text processes.

The verifier keeps a separate immutable inventory of the original nine cases
and a separate inventory of the three text cases. All/group membership, duplicate
or missing entries, exact group sizes, empty core and invalid arguments remain
fail-closed. The separate DX12 job retains its original full JIT and NativeAOT
consumers and additionally runs the stock hinted-render selector under both
modes, retaining its original 20-minute bound and explicit system-WARP selection.
No assertion, renderer policy, artifact qualification or deadline is waived.

Original scheduling validation included selector coverage/invalid-input checks,
Bash syntax checks, ShellCheck, Actionlint, release documentation/package
verification and diff whitespace checks. The additional text routing received
source review, Bash syntax and whitespace checks only; its selectors and GPU
consumers have not been executed locally. A fresh all-green exact-head Build,
downstream dependency alignment and final LibreWPF package/application/platform
gates remain required before ordered merges.
