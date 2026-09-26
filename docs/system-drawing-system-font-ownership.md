# System font ownership and role identity

Each `SystemFonts` property now creates a caller-owned `Font`. Requests for the
same role have equal font values but independent disposal state. A request for
one role cannot dispose another role's font or corrupt later requests.

The owned font retains the requested property name in `SystemFontName` and
reports `IsSystemFont`. Ordinary constructors, prototype/style copies and clones
retain their existing untagged identity. Font value equality does not include
the system role. Unknown, empty, differently cased and null `GetFontByName`
requests return null rather than selecting an unrelated default font.

This is an original ProGPU implementation of observable API and ownership
contracts. The current portable generic-sans selection, 8.25-point size, style,
charset and font metrics are unchanged. These role names do not claim native OS
font-settings discovery, Windows font-name parity, accessibility text scaling,
or identical cross-platform form autoscaling. Those are separate contracts.

## Validation

The same 13 new ownership/identity cases fail on unchanged main
`1fca0bcad00e7359b9aeb78427d37b6794045d20`. With the implementation change,
the complete source Drawing suite passes 662/662 with no skips on macOS ARM64
(.NET 10.0.5). The original allocation assertion is unchanged.

After integrating qualified main `a79ec26d7deb05f7efdc7cd121200921efd4d1dd`,
a fresh full host run reports 661 passes and one failure: the unchanged
`WarmedPrivateMetricReadsAreAllocationFree` assertion measured 2,872 bytes instead
of zero. Its failing TRX is retained for allocation diagnosis. This integration
run is not a passing gate; the ownership cases pass, but attribution and the
complete CI result remain required before merge. No warmup, allocation threshold,
test parallelism or runtime policy was changed to hide the failure.

Paired complete Linux ARM64 corpus runs compare that unchanged main with
`1ff5d57634fdb166310cfccbd6506db1e7fa905e`, using .NET SDK 10.0.400/runtime
10.0.11. Both retain all 4,453 cases, 1,753 discovered methods, the original two
method exclusions, 82 included source files and seven excluded files. The pinned
WinForms source is `b8acee9d29af0ed4c9049cea5f05f80570ecf3b0`; runtime assets are
`13b701d371826c168b26d581351422c031089faa`.

The baseline has 3,263 passes, 1,105 failures and 85 skips. The fixed run has
3,272 passes, 1,096 failures and the same 85 skips. Exactly nine original cases
change from failure to success: six independent-instance failures and three
unknown-name failures. No failure group is added; skip inventories are
byte-identical. The unchanged verifier accepts the baseline and rejects the
fixed run solely because those three failure groups disappeared. Per-case review
also accounts for six unchanged passing cases with run-specific GUID/path labels.
Both builds report zero warnings and errors.

The ARM64 expectation files now match that measured result. The x64 files require
the same nine platform-independent contract improvements: 3,288 passes, 1,131
failures and 34 unchanged skips. This x64 result is an expectation awaiting the
independent complete x64 CI corpus, not qualification inferred from ARM64. Neither
architecture admits new failures or excludes additional tests. The complete Build
must pass before merge; host source tests alone do not qualify package consumers
or native font settings. No upstream assertion is relaxed by this change.
