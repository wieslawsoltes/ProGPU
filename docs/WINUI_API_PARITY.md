# WinUI API parity

ProGPU tracks the stable WinUI public contract from the official Windows App
SDK NuGet packages. The parity lane is a clean-room contract comparison: it
reads public ECMA-335/WinRT metadata and documentation only. It does not
decompile, disassemble, inspect method bodies, or copy Microsoft implementation
source.

## Locked official baseline

The current stable umbrella package is `Microsoft.WindowsAppSDK` `2.3.1`. Its
NuGet dependency graph selects `Microsoft.WindowsAppSDK.WinUI` `2.3.0` and
`Microsoft.WindowsAppSDK.InteractiveExperiences` `2.1.3`. Together they contain
the managed XAML/text and composition/content/dispatching/input/windowing
projections plus their authoritative WinMD files.

`eng/winui-api-baseline.json` locks:

- the exact component package IDs and versions;
- each official `api.nuget.org` package URI;
- each NuGet catalog SHA-512 package hash;
- the exact managed projections, WinMD, and XML documentation assets;
- the `Microsoft.UI` namespace scope; and
- monotonic missing/matching API regression budgets.

The acquisition tool verifies the complete package SHA-512 before extracting
any metadata asset. It then reads the locked umbrella nuspec and verifies that
its official dependency graph selects the two locked component versions.
Extracted binaries live only under `artifacts/`; official binaries are not
committed or shipped as ProGPU source.

Authoritative sources:

- [Windows App SDK stable release notes](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0)
- [Microsoft.WindowsAppSDK 2.3.1 on NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.3.1)
- [Microsoft.WindowsAppSDK.WinUI 2.3.0 on NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.0)
- [Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.3 on NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.InteractiveExperiences/2.1.3)
- [Microsoft API compatibility tooling](https://learn.microsoft.com/dotnet/fundamentals/apicompat/global-tool)
- [ECMA-335 Common Language Infrastructure](https://ecma-international.org/publications-and-standards/standards/ecma-335/)

## Deterministic comparison

Run:

```bash
./eng/progpu-winui-api-check.sh
```

The script builds a standalone, dependency-free metadata tool and
`ProGPU.WinUI`, verifies and extracts the official package, and writes JSON and
Markdown reports under `artifacts/winui-api/report/`.

The canonical surface includes externally visible types, inheritance,
interfaces, generic constraints, public/protected fields and constants,
constructors, methods, properties, events, accessor visibility, parameter
direction/default metadata, and public custom-attribute type identities. Every
semantic custom attribute is compared by type and raw ECMA-335 value blob.
C#/WinRT projection plumbing, ABI helper attributes, and compiler-only
diagnostic attributes are excluded because they describe the producing
toolchain rather than the consumer contract. This also excludes generated
runtime-class query/equality helpers, COM cast interfaces, and delegate
`BeginInvoke`/`EndInvoke` methods that are absent from the official XML API
documentation. Every entry is ordinally sorted.
For `M` metadata declarations, extraction is
`O(M log M)` time and `O(M)` storage; comparison is `O(M + P)` expected time and
`O(M + P)` storage for official entries `M` and ProGPU entries `P`.

The CI budget is monotonic: missing entries may decrease and exact matches may
increase. A change that increases missing entries or reduces exact matches
fails until the contract change is explicitly investigated and the locked
baseline policy is reviewed.

The preview.31 starting baseline recorded 16,621 official entries, 6,820 ProGPU
entries, 2,824 exact matches, 13,797 missing entries, and 3,996 ProGPU-only
entries. After the foundation-identifier and predefined-color slices, the
current baseline records 7,005 ProGPU entries, 3,009 exact matches, and 13,612
missing entries, with the same 3,996 ProGPU-only entries. These are
declaration-level entries rather than type counts: a type, base/interface edge,
member, generic constraint, constant, or semantic attribute is independently
actionable.

## Clean-room implementation log

### Microsoft.UI foundation identifiers and interop surface

Primary contracts consulted:

- [Microsoft.UI namespace](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui)
- [WindowId](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowid)
- [Win32Interop](https://learn.microsoft.com/windows/apps/api-reference/cs-interop-apis/microsoft.ui/microsoft.ui.win32interop)
- [native GetWindowIdFromWindow contract](https://learn.microsoft.com/windows/windows-app-sdk/api/win32/microsoft.ui.interop/nf-microsoft-ui-interop-getwindowidfromwindow)
- [IClosableNotifier](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.iclosablenotifier)
- [ColorHelper](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.colorhelper)

Adopted: the official mutable 64-bit identifier value layout, value
equality/hash/operator behavior, the six typed handle/identifier conversions,
the parameterless close notification delegate, the `IsClosed` contract, and
the required `FrameworkClosed`-before-`Closed` notification order. The
identifier and handle conversions are fixed `O(1)` value operations with no
managed allocation. `ColorHelper.FromArgb` delegates to the shared WinRT color
value implementation and is fixed `O(1)`.

Deferred rather than stubbed: localized `ColorHelper.ToDisplayName`, WinRT
contract-version attributes, and OS validation/error translation for invalid
native handles. Their declaration entries remain visibly missing in the
machine report until their documented behavior can be implemented and tested.
No Microsoft source or method body was inspected.

### Microsoft.UI predefined colors

Primary contracts consulted:

- [Microsoft.UI.Colors](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.colors)
- [CSS Color Module Level 4 named colors](https://www.w3.org/TR/css-color-4/#named-colors)

Adopted: all 141 official static color properties and their published packed
ARGB values, including the `Aqua`/`Cyan` and `Fuchsia`/`Magenta` aliases and
WinUI's `#00FFFFFF` transparent value. Each getter decodes one compile-time
packed integer into the shared `Windows.UI.Color` value. Access is fixed
`O(1)`, allocation-free, culture-independent, and performs no runtime text
parsing or lookup. Focused tests verify the complete public static property
shape and packed-value fingerprint, representative values across the table,
aliases, transparency, and zero managed allocations across 100,000 warmed
accesses.

Deferred rather than synthesized: the WinRT contract-version attribute remains
visible as the one missing declaration entry for `Colors`.

## Implementation policy

API presence is only the first gate. Each parity implementation must be
original ProGPU code derived from public contracts, specifications, documented
behavior, and independent tests. Rendering APIs must compile to retained,
typed, reflection-free WebGPU work with bounded resource ownership and
allocation-free steady-state paths where practical. Platform services remain
behind typed providers; a platform limitation must fail explicitly instead of
silently changing WinUI behavior.

Behavior, rendering quality, accessibility, input, layout, threading, device
loss, and performance validation are tracked separately from declaration
parity. The metadata report therefore measures contract coverage and is not by
itself a claim of behavioral completion.
