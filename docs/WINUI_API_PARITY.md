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
baseline recorded 7,005 ProGPU entries, 3,009 exact matches, and 13,612
missing entries. With the contract-version metadata slice, the current
baseline recorded 7,012 ProGPU entries, 3,016 exact matches, and 13,605 missing
entries. With `Microsoft.UI.System` complete, the current baseline records
7,018 ProGPU entries, 3,022 exact matches, and 13,599 missing entries. With
`Microsoft.UI.Dispatching` complete, the current baseline records 7,076 ProGPU
entries, 3,080 exact matches, and 13,541 missing entries. With the remaining
root color-display-name declaration complete, the current baseline records
7,077 ProGPU entries, 3,081 exact matches, and 13,540 missing entries, with the
same 3,996 ProGPU-only entries. These are
declaration-level entries rather than type counts: a type, base/interface edge,
member, generic constraint, constant, or semantic attribute is independently
actionable.

With `Microsoft.UI.Windowing` complete, the current baseline advances to 7,269
ProGPU entries, 3,273 exact matches, and 13,348 missing entries. Windowing
records 192/192 exact declarations with no extra entries.

With `Microsoft.UI.Text` complete, the baseline advances to 7,197 ProGPU
entries, 3,313 exact matches, and 13,308 missing entries. Text records 535/535
exact declarations with no extra entries.

The first `Microsoft.UI.Input` value-contract slice advances the baseline to
7,294 ProGPU entries, 3,410 exact matches, and 13,211 missing entries. It adds
97 exact declarations without adding ProGPU-only entries.

The `Microsoft.UI.Input` cursor slice advances the baseline to 7,322 ProGPU
entries, 3,438 exact matches, and 13,183 missing entries. It adds 28 exact
declarations without adding ProGPU-only entries.

The focus and keyboard source slice advances the baseline to 7,411 ProGPU
entries, 3,527 exact matches, and 13,094 missing entries. It adds 89 exact
declarations without adding ProGPU-only entries: 63 in `Microsoft.UI.Input`
and 26 in the minimal `Microsoft.UI.Content` island/site foundation required
by the official factories.

The activation, pre-translate source, light-dismiss, and existing-gesture
metadata slices advance the baseline to 7,446 ProGPU entries, 3,572 exact
matches, and 13,049 missing entries. They add 45 exact
`Microsoft.UI.Input` declarations and reconcile 10 existing ProGPU-only
metadata identities.

The immutable pointer-property slice advances the baseline to 7,451 ProGPU
entries, 3,598 exact matches, and 13,023 missing entries. It adds five exact
contract declarations and reconciles 21 getter-only property identities,
reducing ProGPU-only entries to 3,853.

The pointer-event snapshot slice advances the baseline to 7,458 ProGPU
entries, 3,605 exact matches, and 13,016 missing entries while keeping
ProGPU-only entries unchanged at 3,853. It adds all seven official
`PointerEventArgs` declarations without adding a candidate-only declaration.

The island pointer-source slice advances the baseline to 7,473 ProGPU
entries, 3,620 exact matches, and 13,001 missing entries while keeping
ProGPU-only entries unchanged at 3,853. It adds all 15 official
`InputPointerSource` declarations without adding a candidate-only
declaration.

The pointer-prediction slice advances the baseline to 7,480 ProGPU entries,
3,627 exact matches, and 12,994 missing entries while keeping ProGPU-only
entries unchanged at 3,853. It adds all seven official `PointerPredictor`
declarations without adding a candidate-only declaration.

The non-client pointer-source slice advances the baseline to 7,540 ProGPU
entries, 3,687 exact matches, and 12,934 missing entries while keeping
ProGPU-only entries unchanged at 3,853. It adds all 60 official declarations
across `InputNonClientPointerSource` and its eight event-argument types without
adding a candidate-only declaration.

## Clean-room implementation log

### Microsoft.UI foundation identifiers and interop surface

Primary contracts consulted:

- [Microsoft.UI namespace](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui)
- [WindowId](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowid)
- [Win32Interop](https://learn.microsoft.com/windows/apps/api-reference/cs-interop-apis/microsoft.ui/microsoft.ui.win32interop)
- [native GetWindowIdFromWindow contract](https://learn.microsoft.com/windows/windows-app-sdk/api/win32/microsoft.ui.interop/nf-microsoft-ui-interop-getwindowidfromwindow)
- [IClosableNotifier](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.iclosablenotifier)
- [ColorHelper](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.colorhelper)
- [ColorHelper.ToDisplayName](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.colorhelper.todisplayname)

Adopted: the official mutable 64-bit identifier value layout, value
equality/hash/operator behavior, the six typed handle/identifier conversions,
the parameterless close notification delegate, the `IsClosed` contract, and
the required `FrameworkClosed`-before-`Closed` notification order. The
identifier and handle conversions are fixed `O(1)` value operations with no
managed allocation. `ColorHelper.FromArgb` delegates to the shared WinRT color
value implementation and is fixed `O(1)`. `ColorHelper.ToDisplayName` delegates
to the typed, culture-aware `IColorDisplayNameProvider`; a valid localized name
is returned unchanged, while an unavailable or invalid provider result fails
explicitly with `PlatformNotSupportedException`. Provider lookup and dispatch
are fixed `O(1)`; the provider owns localization cost.

Deferred platform integration: each native host still needs to connect its
localized color-name service, and Windows hosts still need OS validation/error
translation for invalid native handles. Missing localized service behavior is
explicit rather than an invented English approximation. The declaration report
now records the root `Microsoft.UI` namespace as 193/193 exact with no missing
or extra entries. No Microsoft source or method body was inspected.

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

### WinRT contract-version metadata

Primary contract consulted:

- [ContractVersionAttribute](https://learn.microsoft.com/uwp/api/windows.foundation.metadata.contractversionattribute)

Adopted: the three official constructors, multiple-use target policy, exact
`Microsoft.Foundation.WindowsAppSDKContract` identity, and the published
version values for all foundation types implemented above. Reflection tests
verify the constructor surface and every emitted constructor argument. The
attribute is declarative, CPU-only, fixed `O(1)`, and performs no allocation
beyond normal runtime reflection when an application explicitly inspects
metadata.

### Microsoft.UI.System theme settings

Primary contracts consulted:

- [ThemeSettings](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.system.themesettings)
- [ThemeSettings.Changed](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.system.themesettings.changed)
- [ThemeSettings.CreateForWindowId](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.system.themesettings.createforwindowid)

Adopted: the complete six-entry official namespace surface, nonzero window
identity validation, live high-contrast state, optional platform-supplied
scheme identity, and change-only notifications. `ThemeSettings` reuses the
existing typed XAML platform-resource provider; hosts can add scheme data
through `ProGPU.WinUI.Platform.IHighContrastSchemeProvider` without reflection
or a dependency on native UI types. Static theme notifications retain only
weak references to settings objects, matching the documented release lifetime.
Property reads are fixed `O(1)`. A rare platform theme transition prunes and
notifies `L` live settings objects in `O(L)` time and transient storage.

Deferred behavioral gate: native hosts still need typed top-level
window/process/thread validation and window-destruction notification. Until
that provider contract is connected, zero IDs fail explicitly while a nonzero
platform ID is accepted. The declaration report now records
`Microsoft.UI.System` as 6/6 exact with no missing or extra entries, but this
does not claim that deferred native lifecycle behavior is complete.

### Microsoft.UI.Dispatching

Primary contracts consulted:

- [DispatcherQueue architecture and run-down](https://learn.microsoft.com/windows/apps/develop/dispatcherqueue)
- [DispatcherQueue](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueue)
- [DispatcherQueueController](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueuecontroller)
- [DispatcherQueueTimer](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueuetimer)
- [DispatcherRunOptions](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherrunoptions)
- [DispatcherExitDeferral](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherexitdeferral)

Adopted: the complete 58-entry official namespace surface; one thread-local
queue per owning thread; serial high/normal/low priority dispatch; current and
dedicated thread controllers; synchronous and asynchronous run-down; nested
event-loop exit deferrals; the documented application/framework shutdown event
order; shutdown deferral draining; timer tick coalescing; and dispatcher-backed
`SynchronizationContext` marshaling. Dedicated controllers keep the owned
thread alive until queue shutdown completes, and their asynchronous shutdown
action completes only after that thread unwinds.

Enqueue and dequeue are expected `O(1)` operations with `O(Q)` retained storage
for `Q` pending callbacks. A run-down is `O(Q + D)` callback work for pending
items and deferral completions. Timers retain one cached dispatcher callback
and permit at most one pending tick, so a native timer expiration performs
fixed `O(1)` queue work without allocating a new closure per tick. A focused
Release test warms queue capacity and verifies exactly zero managed allocations
across 2,000 enqueue operations using one retained callback. Behavioral tests
cover priority ordering, thread affinity, current-thread singleton lifetime,
dedicated synchronous/asynchronous shutdown, nested exit deferrals, exact
shutdown ordering, one-shot/repeating timers, exception-preserving `Send`, and
post-shutdown rejection.
Dispatcher synchronization-context marshaling reuses callback work items from
a lock-protected pool capped at 256 retained entries, avoiding one closure per
steady-state `Post` or `Send` while keeping burst retention bounded. A second
warmed Release invariant verifies exactly zero caller-thread managed
allocations across 2,000 synchronous sends.

Adapted for portability: ProGPU's queue uses a managed event-loop wake source
on every supported runtime instead of depending on USER32. Native desktop,
mobile, and browser hosts can bridge their platform pump through the typed
enqueue surface. `EnsureSystemDispatcherQueue` is intentionally a no-op in the
platform-neutral engine because ProGPU composition and input already share this
dispatch source.

Deferred behavioral gate: Windows hosts do not yet create and lifetime-manage
the separate `Windows.System.DispatcherQueue`, and the portable run options
cannot observe native `WM_QUIT` messages. Those integrations remain explicit
platform-host work; the implementation does not simulate a native queue or
silently change the cross-platform dispatch contract. The declaration report
records `Microsoft.UI.Dispatching` as 58/58 exact with no missing or extra
entries.

The macOS CI allocation gate also exposed a deferred
`ManualResetEventSlim` runtime transition on a later contended synchronous
send. A pooled synchronous work item now creates a kernel-backed
`AutoResetEvent` during its first preparation. Each wait consumes its signal,
so steady-state sends avoid both reset bookkeeping and deferred wait-state
allocation. Fifty independent Release-process runs of the 2,000-send invariant
reported zero caller-thread managed allocations.

### Microsoft.UI.Text contract shape

Primary contracts consulted:

- [Microsoft.UI.Text namespace](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.text)
- [TextGetOptions](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.text.textgetoptions)
- [FindOptions](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.text.findoptions)
- [FontWeights](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.text.fontweights)
- [ITextRange](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.text.itextrange)
- [RichEditTextDocument](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.text.richedittextdocument)

Adopted: the official unsigned storage for the bitwise find/get/set option
enums, exact published flag and undefined-effect values, sealed
`FontWeights`/`RichEditTextRange` runtime-class shapes, `TextApiContract`
identity and version metadata, contract metadata on the implemented public TOM
types, and the official `Collapse(Boolean value)` parameter name. Concrete
character-format, paragraph-format, and selection implementations are internal
typed adapters behind the official interfaces. The selection adapter and its
retained range are created once per document; property access remains fixed
`O(1)` and allocation-free. Font-weight and selection-property invariants each
verify zero managed allocations across 100,000 warmed Release iterations.

ProGPU's TOM2 table insertion is preserved as the public typed
`ProGPU.WinUI.Text.RichEditTextRangeExtensions.InsertTable` extension instead
of expanding the official `Microsoft.UI.Text` namespace. It dispatches in
fixed `O(1)` time to the retained range/selection implementation; table
construction itself remains `O(R * C)` time and storage for `R` rows and `C`
columns. Existing TOM range tracking, selection movement, RTF, paragraph,
shaping, layout, and rendering behavior is retained and covered by the
existing rich-text regression suite.

The implementation was derived from the locked official NuGet metadata and
Microsoft documentation. No Microsoft method body or foreign implementation
source was inspected. The declaration report records `Microsoft.UI.Text` as
535/535 exact with no missing or extra entries.

### Microsoft.UI.Input value contracts

Primary contracts consulted:

- [Microsoft.UI.Input namespace](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input)
- [FocusNavigationReason](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.focusnavigationreason)
- [InputPointerSourceDeviceKinds](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpointersourcedevicekinds)
- [InputActivationState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputactivationstate)
- [InputSystemCursorShape](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputsystemcursorshape)
- [PhysicalKeyStatus](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.physicalkeystatus)

Adopted: the exact focus-navigation, activation, pointer-device, system-cursor,
move/resize, non-client-region, and virtual-key state values, including
official unsigned storage for flags, plus the mutable six-field
`PhysicalKeyStatus` value layout and equality contract. These declarations are
CPU-only fixed `O(1)` operations with no platform call, heap retention, or
WebGPU initialization. A warmed 100,000-iteration Release invariant constructs,
compares, and hashes physical-key state with zero managed allocations.

This slice establishes package-compatible value contracts for the later typed
keyboard, pointer, focus, and non-client input providers. Those event sources
will reuse ProGPU's existing low-latency input queues and retained hit-testing
state; this checkpoint does not invent unavailable native behavior. It was
implemented only from the locked official NuGet metadata and Microsoft
documentation, without inspecting Microsoft implementation source.

### Microsoft.UI.Input cursor projection

Primary contracts consulted:

- [InputCursor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputcursor)
- [InputSystemCursor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputsystemcursor)
- [InputDesktopResourceCursor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputdesktopresourcecursor)
- [InputDesktopNamedResourceCursor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputdesktopnamedresourcecursor)
- [UIElement.ProtectedCursor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.protectedcursor)
- [CoreCursor](https://learn.microsoft.com/uwp/api/windows.ui.core.corecursor)

Adopted: the official factories and immutable value-state contracts, deepest
hovered-descendant precedence, and captured-element precedence. The managed
projection retains system shape or desktop module/resource identity and
exposes a typed `IInputCursorProvider` seam so native hosts can resolve custom
resources without reflection or a platform dependency in
`Microsoft.UI.Input`. Existing Silk and browser hosts receive an
allocation-free standard-cursor mapping; unsupported custom resources fall
back to the host default while remaining available to the typed provider.

The cursor slice adds 28 exact declarations without adding an extra
declaration, advancing the official comparison to 7,322 candidate
declarations, 3,438 exact matches, 13,183 missing declarations, and 3,884
extras. Repeated reads of a warmed system cursor shape allocate zero managed
bytes across 100,000 iterations.

### Microsoft.UI.Input focus and keyboard sources

Primary contracts consulted:

- [InputObject](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputobject)
- [InputFocusController](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputfocuscontroller)
- [InputFocusNavigationHost](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputfocusnavigationhost)
- [FocusNavigationRequest](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.focusnavigationrequest)
- [InputKeyboardSource](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputkeyboardsource)
- [InputKeyboardSource.GetKeyState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputkeyboardsource.getkeystate)
- [InputKeyboardSource.GetCurrentKeyState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputkeyboardsource.getcurrentkeystate)
- [ContentIsland](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentisland)
- [VirtualKey](https://learn.microsoft.com/uwp/api/windows.system.virtualkey)
- [CoreVirtualKeyStates](https://learn.microsoft.com/uwp/api/windows.ui.core.corevirtualkeystates)

Adopted: dispatcher-thread affinity; one stable focus controller and keyboard
source per content island; explicit focus acquisition; change-only got/lost
notifications; request/result propagation between an island controller and its
navigation host; normal versus Alt/system key event separation; per-message
versus live key-state snapshots; UTF-16 character notification; context-menu
key fallback; and down/locked state semantics. Navigation result handlers do
not implicitly set focus.

Adapted for portability: `ContentIslandInputRegistration`,
`IContentIslandFocusProvider`, and `IContentIslandSiteProvider` are typed host
seams outside the official `Microsoft.UI` namespace. They let desktop, mobile,
browser, Avalonia, LibreWPF, and LibreWinForms hosts associate native focus and
input state without reflection or platform types in the projection. Existing
Silk input is translated through an explicit key map rather than relying on
incompatible enum ordinals. A native host can inject complete timestamp and
physical-key status through the value-only `KeyboardInputEvent`.

Live and message key states are stored in eight fixed 64-bit bitsets. A state
read or update is fixed `O(1)` time and storage and performs no managed
allocation. Official event argument objects are created only when their event
has a subscriber. A warmed Release invariant performs 200,000 live/message
state reads with exactly zero managed allocations. Focus and key transitions
are covered for stable object identity, lifecycle cleanup, dispatcher
affinity, navigation results, handled events, system keys, context-menu keys,
characters, and lock toggles.

Deferred platform integration: each host still needs to supply native scan
codes, repeat counts, extended-key state, OS focus activation, and lock-state
resynchronization after out-of-process changes. The portable Silk fallback
uses a zero scan code and repeat count one, while preserving the public typed
injection seam for exact native data. This slice adds 89 exact declarations
without adding ProGPU-only declarations, advancing the official comparison to
7,411 candidate declarations, 3,527 exact matches, 13,094 missing
declarations, and 3,884 extras. No Microsoft source or method body was
inspected.

### Microsoft.UI.Input activation and pre-translation

Primary contracts consulted:

- [InputActivationListener](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputactivationlistener)
- [InputActivationListener.GetForIsland](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputactivationlistener.getforisland)
- [InputActivationListener.GetForWindowId](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputactivationlistener.getforwindowid)
- [InputActivationListener.InputActivationChanged](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputactivationlistener.inputactivationchanged)
- [InputActivationState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputactivationstate)
- [InputPreTranslateKeyboardSource](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpretranslatekeyboardsource)
- [InputPreTranslateKeyboardSource.GetForIsland](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpretranslatekeyboardsource.getforisland)

Adopted: one stable activation listener per valid same-thread content island or
top-level window ID, null lookup results for invalid or cross-thread objects,
change-only activation notifications, stable pre-translate source identity,
and implicit source teardown with the associated object. Island activation
reuses host-focus transitions; window activation reuses the existing XAML
Window event and retained current state. Code and pointer activation both map
to the official `Activated` input state.

Adapted for portable hosts: `InputActivationRegistration` is a typed seam
outside the official namespace that resolves an `AppWindow` ID and forwards
native activation through the existing Window notification path. Lookup and
state reads are expected fixed `O(1)` work; window lookup uses the existing
bounded registry lock. Subscriber-free state transitions do not allocate an
event argument, and a warmed Release invariant performs 100,000 state reads
with exactly zero managed allocations.

The pinned stable `InputPreTranslateKeyboardSource` metadata exposes only its
dispatcher and same-island singleton factory. ProGPU therefore does not invent
public pre-translation events; platform keyboard input continues through the
typed value source delivered in the previous slice until an official contract
adds a callable event surface. Focused tests cover island/window identity,
invalid and cross-thread lookup, change-only event delivery, object teardown,
pre-translate lifetime, contract versions, and the allocation invariant. The
slice adds 11 exact declarations with zero new extras, advancing the official
comparison to 7,422 candidate declarations, 3,538 exact matches, 13,083
missing declarations, and 3,884 extras. No Microsoft implementation source or
method body was inspected.

### Microsoft.UI.Input light dismiss

Primary contracts consulted:

- [InputLightDismissAction](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputlightdismissaction)
- [InputLightDismissAction.GetForWindowId](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputlightdismissaction.getforwindowid)
- [InputLightDismissAction.Dismissed](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputlightdismissaction.dismissed)
- [InputLightDismissEventArgs](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputlightdismisseventargs)

Adopted: one stable action per valid same-thread top-level window, null results
for invalid or cross-thread window IDs, dismissal when an activated window
loses activation, and implicit teardown with the associated window. The
official event argument remains empty. Consecutive deactivated notifications
do not duplicate a transition-based dismissal.

Adapted for portable hosts: `InputLightDismissRegistration` is a typed seam
outside the official namespace for Escape, Alt, app-command, hot-key, and
pointer-outside triggers supplied by native or embedded hosts. It resolves the
existing top-level action without reflection, boxing, or creating an action
that an application never requested. Lookup is expected fixed `O(1)` work and
bounded storage per live window. An event argument is created only when the
official event has a subscriber; a warmed Release invariant delivers 100,000
subscriber-free typed triggers with exactly zero managed allocations.

The pinned stable metadata exposes `GetForWindowId` but not the newer
`GetForIsland` factory, so ProGPU does not add that later declaration to the
official projection. Focused tests cover stable identity, invalid and
cross-thread lookup, change-only activation loss, typed triggers, lifecycle
cleanup, contract versions, and the allocation invariant. This slice adds six
exact declarations with zero new extras, advancing the official comparison to
7,428 candidate declarations, 3,544 exact matches, 13,077 missing
declarations, and 3,884 extras. No Microsoft implementation source or method
body was inspected.

### Microsoft.UI.Input existing gesture metadata

Primary contracts consulted:

- [Microsoft.UI.Input namespace](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input)
- [GestureRecognizer](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.gesturerecognizer)
- [GestureSettings](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.gesturesettings)
- [ManipulationDelta](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.manipulationdelta)
- [ManipulationVelocities](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.manipulationvelocities)
- [CrossSlideThresholds](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.crossslidethresholds)
- [MouseWheelParameters](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.mousewheelparameters)
- [Touch interactions](https://learn.microsoft.com/windows/apps/design/input/touch-interactions)

This slice reconciles the already implemented clean-room gesture engine with
the pinned public WinRT metadata; it does not replace or modify gesture
algorithms. Contract-version attributes now cover the recognizer, settings and
state enums, value structs, event arguments, and mouse-wheel settings.
Constructor and equality-operator parameter names match the official
projection, and `IsInertial` is publicly getter-only while retaining an
internal backing field for the existing state machine.

The retained recognizer remains typed and reflection-free in production.
Pointer ingestion is `O(P)` per sample for `P` active contacts with `O(P)`
contact storage; value construction, equality, and state reads remain fixed
`O(1)` work without heap allocation. Existing focused tests continue to cover
tap/double-tap, drag, cross-slide, hold, wheel, multi-contact translate/scale/
rotate, completion, and manual inertia. New metadata tests cover all 18
contract versions, the three value constructors, equality parameters, and the
getter-only inertia state.

The slice adds 18 candidate declarations, converts 10 mismatched candidate
identities to exact matches, and therefore adds 28 exact matches while
removing 10 extras. The official comparison advances to 7,446 candidate
declarations, 3,572 exact matches, 13,049 missing declarations, and 3,874
extras. No Microsoft implementation source or method body was inspected.

### Microsoft.UI.Input pointer value snapshots

Primary contracts consulted:

- [PointerPoint](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpoint)
- [PointerPointProperties](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpointproperties)
- [PointerDeviceType](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerdevicetype)
- [PointerUpdateKind](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerupdatekind)
- [IPointerPointTransform](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.ipointerpointtransform)
- [IPointerPointTransform.TryTransform](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.ipointerpointtransform.trytransform)

Adopted: `PointerPointProperties` is an immutable public snapshot whose 21
properties are getter-only, and pointer points preserve that snapshot along
with frame, pointer, timestamp, position, device, and contact state.
Transformed points create one new snapshot with transformed contact bounds
while retaining every remaining input property.

Adapted for ProGPU's typed input pipeline: hosts and routed input construct the
snapshot through one internal value-only constructor. There are no public or
nonpublic property setters in metadata, no reflection, and no mutable
post-publication state. Snapshot creation and all property reads are fixed
`O(1)` work and storage. A transformed point performs two caller-supplied
transform calls and creates the required result point and property snapshot;
it does not allocate an intermediate property map. A warmed Release invariant
performs 100,000 reads across boolean, integer, floating-point, and rectangle
properties with exactly zero managed allocations.

Focused tests cover getter-only reflection metadata, all property values,
transformed position/contact bounds and retained identity metadata, defaults,
contract versions, and the allocation invariant. The slice adds five
contract declarations and converts 21 mismatched setter-bearing candidate
identities into exact getter-only matches. The official comparison advances
to 7,451 candidate declarations, 3,598 exact matches, 13,023 missing
declarations, and 3,853 extras. No Microsoft implementation source or method
body was inspected.

### Microsoft.UI.Input pointer event snapshots

Primary contracts consulted:

- [PointerEventArgs](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointereventargs)
- [PointerEventArgs.CurrentPoint](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointereventargs.currentpoint)
- [PointerEventArgs.Handled](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointereventargs.handled)
- [PointerEventArgs.GetIntermediatePoints](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointereventargs.getintermediatepoints)
- [PointerEventArgs.GetIntermediateTransformedPoints](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointereventargs.getintermediatetransformedpoints)
- [PointerRoutedEventArgs intermediate-point ordering](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.input.pointerroutedeventargs.getintermediatepoints)
- [VirtualKeyModifiers](https://learn.microsoft.com/uwp/api/windows.system.virtualkeymodifiers)

Adopted: `PointerEventArgs` is a sealed, publicly non-constructible event
snapshot with getter-only current-point and modifier state, mutable handled
state, and an `IList<PointerPoint>` history. A connected event retains at
most 64 chronological samples, including its current point as the final
sample. Application transforms preserve that order and return an empty
collection if any position or contact-bounds transform fails, so callers
never observe a partial transformed history.

Adapted for ProGPU's typed input pipeline: an internal constructor accepts a
canonical current point and history that precedes it, keeps the newest 63
history entries, appends the current point, and publishes one read-only
collection for the event lifetime. Construction is bounded `O(min(H, 63))`
time and storage for `H` host samples. Repeated current-point, modifier, and
intermediate-point reads are `O(1)` and allocation-free. Successful
transformation is `O(P)` time and storage for at most 64 points; failure is
all-or-empty with bounded discarded work. The path is typed and
reflection-free.

Focused tests cover exact metadata, modifier values, handled state, the
64-sample tail policy, chronological/current-point identity, read-only
publication, successful position/contact transforms, failure atomicity, null
validation, and a warmed 100,000-iteration zero-allocation read invariant.
The slice adds all seven official declarations exactly. The official
comparison advances to 7,458 candidate declarations, 3,605 exact matches,
13,016 missing declarations, and 3,853 extras. No Microsoft implementation
source or method body was inspected.

### Microsoft.UI.Input island pointer source

Primary contracts consulted:

- [InputPointerSource](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpointersource)
- [InputPointerSource.GetForIsland](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpointersource.getforisland)
- [InputPointerSource.Cursor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpointersource.cursor)
- [InputPointerSource.DeviceKinds](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpointersource.devicekinds)
- [InputPointerSource event ordering](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpointersource#event-order)
- [InputPointerSourceDeviceKinds](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputpointersourcedevicekinds)

Adopted: each valid same-thread `ContentIsland` owns at most one stable
`InputPointerSource`; invalid, closed, and cross-thread island requests return
null. The source reports touch, pen, and mouse support, retains a source
cursor, and publishes the official entered, pressed, moved, released, exited,
capture-lost, routed, and wheel events. A handled source event stops delivery
into the higher-level XAML route. Capture loss and routed release are terminal
states and do not synthesize a later release or exit.

Adapted for ProGPU's existing typed input pipeline: attaching a
`WindowInputState` attaches its island-owned pointer source, and the existing
portable `PointerInputEvent` feed invokes the source before XAML routing.
Source cursor state is the fallback beneath per-element protected cursors and
flows through `IInputCursorProvider` without platform reflection. The
package-neutral `InputPointerSourceRegistration` seam lets native or embedded
hosts raise capture, boundary, and routed events that are not expressible by
the ordinary point feed. Island disposal detaches state, clears handlers, and
removes the cursor fallback.

Dispatch is expected `O(1)` time and bounded state per active pointer. When no
relevant event has subscribers, dispatch performs no event-args or point
allocation; a warmed 100,000-event invariant allocates exactly zero managed
bytes. Subscribed input creates one immutable `PointerEventArgs`/point
snapshot for the native input report and shares it across the ordered source
notifications from that report.

Focused tests cover stable identity, invalid/cross-thread/closed factories,
device flags, entered/pressed/released/exited order, capture-loss terminal
order, handled propagation, point/modifier data, typed cursor delivery,
teardown, routed host delivery, exact contract metadata, and subscriber-free
allocation behavior. The slice adds all 15 official declarations exactly.
The official comparison advances to 7,473 candidate declarations, 3,620 exact
matches, 13,001 missing declarations, and 3,853 extras. No Microsoft
implementation source or method body was inspected.

### Microsoft.UI.Input pointer prediction

Primary contracts consulted:

- [PointerPredictor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpredictor)
- [PointerPredictor.CreateForInputPointerSource](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpredictor.createforinputpointersource)
- [PointerPredictor.GetPredictedPoints](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpredictor.getpredictedpoints)
- [PointerPredictor.PredictionTime](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpredictor.predictiontime)
- [PointerPredictor.Dispose](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpredictor.dispose)

Adopted: a predictor is created for an `InputPointerSource`, defaults to a
15-millisecond horizon, emits no points until it has processed ten samples,
and derives its output count from that horizon and the observed reporting
cadence. Predicted points advance timestamp, position, pressure, X tilt, and Y
tilt; all other public point and property state is cloned from the caller's
current point. Disposal is idempotent and rejects later use.

Adapted as an original portable implementation: a fixed 16-sample ring keeps
monotonic history for one pointer and resets on pointer identity or timestamp
discontinuities. Five independent ordinary least-squares lines predict X, Y,
pressure, X tilt, and Y tilt against timestamp. Pressure is clamped to
`[0, 1]`, tilt to `[-90, 90]`, timestamps saturate on overflow, and at most 64
owned prediction points are returned for an unbounded caller horizon. This
keeps the implementation deterministic and dependency-free across desktop,
mobile, and browser runtimes.

Appending history is `O(1)` time and fixed storage. A prediction is `O(H + P)`
time and `O(P)` returned storage for at most `H = 16` retained samples and
`P = 64` output points; there are no transient lists or per-call history
copies. Duplicate prehistory samples reuse the shared empty result. A warmed
100,000-call prehistory invariant allocates exactly zero managed bytes.

Focused tests cover the ten-sample threshold, 15-millisecond default, cadence
and output count, linear position/pressure/tilt extrapolation, unchanged
property cloning, configurable/zero/invalid horizons, the 64-point cap,
pointer and timestamp resets, idempotent disposal, exact contract metadata,
and allocation behavior. The slice adds all seven official declarations
exactly. The official comparison advances to 7,480 candidate declarations,
3,627 exact matches, 12,994 missing declarations, and 3,853 extras. No
Microsoft implementation source or method body was inspected.

### Microsoft.UI.Input non-client pointer source

Primary contracts consulted:

- [InputNonClientPointerSource](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputnonclientpointersource)
- [SetRegionRects](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputnonclientpointersource.setregionrects)
- [GetRegionRects](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputnonclientpointersource.getregionrects)
- [NonClientRegionKind](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.nonclientregionkind)
- [NonClientPointerEventArgs](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.nonclientpointereventargs)
- [NonClientRegionsChangedEventArgs](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.nonclientregionschangedeventargs)
- [EnteringMoveSizeEventArgs.MoveSizeWindowId](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.enteringmovesizeeventargs.movesizewindowid)
- [WindowRectChangingEventArgs.AllowRectChange](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.windowrectchangingeventargs.allowrectchange)
- [WindowRectChangingEventArgs.ShowWindow](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.windowrectchangingeventargs.showwindow)

Adopted: a valid same-thread `AppWindow` owns one stable source associated
with its dispatcher. The source retains independently configured rectangles
for all ten official non-client region kinds, raises change notifications only
when partition boundaries actually change, and publishes the complete pointer,
caption, move-size, and window-rectangle event family. Entering a move-size
loop defaults its target to the initiating window. A proposed rectangle is
allowed by default, and its show state begins with the native host's current
window visibility.

Adapted for ProGPU's portable window hosts: a package-neutral typed
`InputNonClientPointerSourceRegistration` feed accepts native values and
returns mutable move-size and rectangle decisions through value `out`/`ref`
parameters. It never boxes event state and does not create a source merely
because a native message arrived. Source destruction follows the owning
`AppWindow`: registration lookup stops, retained regions and subscribers are
released, and later public region access fails explicitly.

Region storage is one fixed ten-slot array. Setting a region performs
`O(R)` comparison and owned-copy work for `R` rectangles; get performs the
required `O(R)` public ownership copy; clear is `O(1)` for one kind and fixed
`O(10)` for all kinds. Native hit testing is `O(R)` with half-open rectangle
bounds and no temporary collection. Subscriber-free pointer dispatch is
expected `O(1)` and creates no event args. A warmed Release test on macOS
26.4.1 arm64, Apple M3 Pro, .NET SDK 10.0.201 performs 100,000 combined
subscriber-free dispatch and region-hit-test iterations with exactly zero
managed allocations.

The matching standalone Release workload performs 100,000,000 combined
iterations in 2,905.253 milliseconds with zero managed allocations after
warmup. Xcode Instruments 16.0 records the same 100,000,000-iteration binary
to completion with the Allocations/VM Tracker template in 4.117849 seconds and
the Time Profiler template in 3.643442 seconds. The native traces and exported
tables are retained under `artifacts/input-nonclient-perf/`; this new-feature
measurement is a workload characterization, not a before/after improvement
claim.

Focused tests cover stable identity, dispatcher affinity, invalid/cross-thread
lookup, defensive input/getter ownership, no-op replacement, exact region
change batches, event ordering and state, move-size target replacement,
rectangle/visibility veto results, teardown, contract versions, and the
allocation invariant. The slice adds all 60 official declarations exactly.
The official comparison advances to 7,540 candidate declarations, 3,687 exact
matches, 12,934 missing declarations, and 3,853 extras. No Microsoft
implementation source or method body was inspected.

### Microsoft.UI.Input current pointer snapshot

Primary contracts consulted:

- [PointerPoint.GetCurrentPoint](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpoint.getcurrentpoint)
- [PointerPoint](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpoint)
- [Windows pointer app-context semantics](https://learn.microsoft.com/uwp/api/windows.ui.input.pointerpoint.getcurrentpoint)

Adopted: `PointerPoint.GetCurrentPoint(uint)` is a static query for the latest
position and state of a pointer in the current application input context.
The returned immutable snapshot preserves pointer and frame IDs, microsecond
timestamp, app-context position, device type, contact state, contact bounds,
button state, primary state, pressure, cancellation state, and wheel delta.
An unknown or terminal touch ID fails closed with no point.

Adapted for ProGPU's multi-window portable hosts: every thread-selected
`WindowInputState` owns its current-pointer table, so equal pointer IDs in
different application contexts cannot leak state across windows or islands.
Input injection records the value-type `PointerInputEvent` before either the
island pointer source or XAML route runs. A handler can therefore query the
same current snapshot even when it handles the source event. Terminal touch
reports are removed after dispatch; the table retains only the latest mouse
ID, latest pen ID, and currently active touch IDs, keeping storage bounded by
`O(T + 2)` for `T` simultaneous touches.

Tracking and lookup are expected `O(1)`. Tracking updates existing dictionary
storage without materializing a projected point; the two immutable managed
objects required by the public snapshot are created only when the caller
queries it. A warmed Release test performs 100,000 tracking updates with
exactly zero managed allocations. This CPU-only input path does not initialize
WebGPU.

Focused tests cover full snapshot fidelity, 64-bit timestamp and 32-bit frame
identity, app-context isolation, terminal touch removal, bounded mouse/pen ID
replacement, invalid lookup, and allocation behavior. The slice adds the last
non-DragDrop `Microsoft.UI.Input` declaration exactly. The official comparison
advances to 7,541 candidate declarations, 3,688 exact matches, 12,933 missing
declarations, and 3,853 extras. No Microsoft implementation source or method
body was inspected.

### Microsoft.UI.Input.DragDrop

Primary contracts consulted:

- [Microsoft.UI.Input.DragDrop namespace](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.dragdrop)
- [DragDropManager](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.dragdrop.dragdropmanager)
- [DragOperation](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.dragdrop.dragoperation)
- [DragOperation.StartAsync](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.dragdrop.dragoperation.startasync)
- [DragInfo](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.dragdrop.draginfo)
- [IDropOperationTarget](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.dragdrop.idropoperationtarget)
- [DragUIOverride](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.dragdrop.draguioverride)
- [DataPackageOperation](https://learn.microsoft.com/uwp/api/windows.applicationmodel.datatransfer.datapackageoperation)
- [BitmapPixelFormat](https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmappixelformat)
- [BitmapAlphaMode](https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapalphamode)
- The pinned `Microsoft.UI.xml` documentation and projection metadata
  extracted by the deterministic API gate.

Adopted: each `GetForIsland` call creates a new manager association; a
`TargetRequested` handler supplies the typed drop target; concurrency is
disabled by default and can be enabled explicitly. A `DragOperation` owns its
data package, allowed operations, content mode, and optional software-bitmap
visual. Starting is single-use and completes only when the native host drops
or cancels the pointer. Target callbacks are serialized in enter, over,
leave, and drop order, and every queued callback retains its call-time pointer
snapshot. Returned operations are intersected with the source's
allowed-operation mask.

Adapted for portable desktop, mobile, and browser hosts: the typed
`DragDropManagerRegistration` seam drives active pointer sessions without
reflection or XAML-owned event wrappers. A host can read an atomic
`DragDropVisualSnapshot` containing the retained bitmap identity, anchor,
caption, and visibility flags, allowing a native drag image or one WebGPU
texture upload to be reused throughout the operation. The core does not
initialize WebGPU merely to configure or negotiate a drag. The neutral
`Windows.ApplicationModel.DataTransfer` package/view and
`Windows.Graphics.Imaging.SoftwareBitmap` identity, metadata, and lifetime
contracts live in ProGPU.WinRT so non-XAML hosts can share them. Platform
adapters remain responsible for associating a native or WebGPU-backed pixel
resource with that identity.

Session lookup and each lifecycle transition are expected `O(1)`; disposing a
manager is `O(A)` for `A` active operations. The target's asynchronous work is
serialized without blocking the caller thread. Data lookup is expected
`O(1)` and enumerating format ownership is `O(F)` for `F` formats. Visual
snapshot reads are `O(1)`, atomic, and allocation-free. A warmed Release test
performs 100,000 manager property reads with exactly zero managed
allocations.

Focused tests cover exact enums, new manager identity, closed-island lookup,
missing targets, ordered asynchronous lifecycle delivery, data and position
snapshots, result masking, single and concurrent operation policy,
independent pointer sessions, bitmap anchor validation, typed visual
snapshots, cancellation, island teardown, idempotent disposal, and allocation
behavior. The slice adds all 58 official declarations exactly. The official
comparison advances to 7,598 candidate declarations, 3,746 exact matches,
12,875 missing declarations, and 3,852 extras. No Microsoft implementation
source or method body was inspected.

### Microsoft.UI.Content state value contracts

Primary contracts consulted:

- [Microsoft.UI.Content namespace](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content)
- [ContentAutomationOptions](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentautomationoptions)
- [ContentCoordinateRoundingMode](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentcoordinateroundingmode)
- [ContentSizePolicy](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsizepolicy)
- [PopupAnchor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.popupanchor)
- [ContentDeferral.Complete](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentdeferral.complete)
- [ContentIslandStateChangedEventArgs](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentislandstatechangedeventargs)
- The pinned official projection metadata and `Microsoft.UI.xml`
  documentation extracted by the deterministic API gate.

Adopted: the exact four enum layouts and contract versions; owner-dispatcher
thread affinity for content state deferrals; immutable environment, island,
and requested-size change snapshots; and mutable automation-provider response
objects with their documented null/false defaults. Adapted for deterministic
portable lifecycle behavior: completion is idempotent and invokes the retained
continuation at most once.

The environment and island change snapshots pack their flags into one byte.
Construction and every property read are fixed `O(1)` work; reads allocate no
managed memory and do not initialize WebGPU. Deferral completion is fixed
`O(1)` and clears the retained callback before invoking it. This state layer is
kept independent of rendering so the later content island/site implementation
can coalesce state changes before scheduling typed compositor work.

Focused tests cover exact enum values, every immutable and mutable event-data
property, single-completion behavior, wrong-thread rejection without consuming
the deferral, and 100,000 warmed snapshot iterations with exactly zero managed
allocations. The slice adds all 56 selected official declarations exactly. The
official comparison advances to 7,654 candidate declarations, 3,802 exact
matches, 12,819 missing declarations, and 3,852 extras. No Microsoft
implementation source or method body was inspected.

### Microsoft.UI.Content environment propagation

Primary contracts consulted:

- [ContentSiteEnvironment](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsiteenvironment)
- [ContentSiteEnvironment.NotifySettingChanged](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsiteenvironment.notifysettingchanged)
- [ContentSiteEnvironmentView](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsiteenvironmentview)
- [ContentIslandEnvironment](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentislandenvironment)
- [ContentIslandEnvironment.StateChanged](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentislandenvironment.statechanged)
- [ContentIslandEnvironment.SettingChanged](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentislandenvironment.settingchanged)
- The pinned official projection metadata and `Microsoft.UI.xml`
  documentation extracted by the deterministic API gate.

Adopted: a mutable site environment, one identity-stable live read-only site
view, one unique island environment per island, immediate state visibility,
and asynchronous island environment notifications. The bridge policy remains
explicit: setting a site property does not silently propagate it. A typed
internal attach/propagate seam lets the later site bridge choose when to copy a
consistent environment snapshot into an island.

Site and island state is published as immutable snapshots. Individual
property reads are lock-free fixed `O(1)` work with no managed allocation.
Changing one site property performs expected `O(1)` compare/exchange work and
allocates one cold-path snapshot. Propagation is fixed `O(1)`; changes made
before dispatcher delivery are combined into one state event and one cached
dispatcher callback. Setting notification preserves call order and is `O(I)`
for `I` attached islands. No environment operation initializes WebGPU or
mutates compositor resources.

Focused tests cover stable view identity, live state, display-scale
validation, immediate propagation, asynchronous delivery, change-flag
coalescing, ordered setting notifications, detach behavior, and 100,000 warmed
site-view/island reads with exactly zero managed allocations. The slice adds
all 25 selected official declarations exactly. The official comparison
advances to 7,679 candidate declarations, 3,827 exact matches, 12,794 missing
declarations, and 3,852 extras. No Microsoft implementation source or method
body was inspected.

### Microsoft.UI.Content coordinate conversion

Primary contracts consulted:

- [ContentCoordinateConverter](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentcoordinateconverter)
- [ContentCoordinateConverter.ConvertLocalToScreen](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentcoordinateconverter.convertlocaltoscreen)
- [ContentCoordinateConverter.ConvertScreenToLocal](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentcoordinateconverter.convertscreentolocal)
- [ContentCoordinateConverter.CreateForWindowId](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentcoordinateconverter.createforwindowid)
- [ContentCoordinateRoundingMode](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentcoordinateroundingmode)
- [System.Numerics.Matrix3x2](https://learn.microsoft.com/dotnet/api/system.numerics.matrix3x2)
- The pinned official projection metadata and `Microsoft.UI.xml`
  documentation extracted by the deterministic API gate.

Adopted: the exact non-sealed converter surface, a required nonzero top-level
window identity, local-to-screen conversion adjusted by the complete affine
mapping (including platform rasterization scale), inverse screen-to-local
conversion, one transform snapshot per array operation, and axis-aligned
bounds for converted rectangles. `Auto` follows the current Microsoft Learn
contract and truncates toward zero; `Floor`, `Round`, and `Ceiling` apply their
named behavior, with midpoint rounding away from zero for the explicit
`Round` mode. The older pinned XML describes `Auto` in terms of the current
FPU setting; deterministic truncation was selected because it is the current
publicly documented behavior and is stable across managed platforms.

Adapted for portable desktop, mobile, and browser hosts:
`IContentCoordinatePlatformProvider` supplies a live typed `Matrix3x2` for a
`WindowId`. The matrix includes the native client-to-screen origin and current
rasterization scale, and can also represent rotation or skew. ProGPU-owned
`AppWindow` instances retain a lock-free live-position fallback when a host
provider is absent. The later `ContentSite` and `ContentIsland` work can use an
internal typed transform source without reflection, boxed adapters, or
platform-specific types in the public WinUI contract.

Scalar conversion and rectangle conversion are fixed `O(1)` work and allocate
no managed memory. Array conversion is `O(P)` time and exactly `O(P)` result
storage for `P` points; the transform is resolved and inverted at most once.
Invalid, non-finite, singular, or overflowing mappings fail explicitly.
Coordinate conversion is a CPU-only value service and does not initialize
WebGPU.

Focused tests cover nonzero window identity, live platform transform changes,
forward/inverse mapping, all four rounding modes, one-snapshot array
conversion, rotated rectangle bounds, invalid input and transforms, and
100,000 warmed forward/inverse scalar iterations with exactly zero managed
allocations. The slice adds all 12 selected official declarations exactly.
The official comparison advances to 7,691 candidate declarations, 3,839 exact
matches, 12,782 missing declarations, and 3,852 extras. No Microsoft
implementation source or method body was inspected.

### Microsoft.UI.Content site and live view

Primary contracts consulted:

- [ContentSite](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsite)
- [ContentSiteView](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsiteview)
- [ContentSite.GetIslandStateChangeDeferral](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsite.getislandstatechangedeferral)
- [ContentDeferral](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentdeferral)
- [ContentSite.RasterizationScale](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.contentsite.rasterizationscale)
- [ContentIsland overview](https://learn.microsoft.com/windows/apps/develop/composition/content-island)
- The pinned official projection metadata and `Microsoft.UI.xml`
  documentation extracted by the deterministic API gate.

Adopted: `ContentSite` is a host-side state owner with one stable
`ContentSiteView`; the view exposes the most recent values and is explicitly
not a point-in-time snapshot. Environment, dispatcher, coordinate converter,
and view identities remain stable for the site lifetime. A disconnected site
returns null from `GetIslandStateChangeDeferral`. Connected deferrals are
owner-dispatcher-affine, nest, combine site changes, complete at most once, and
are cancelled when the 1:1 island connection ends. Requested-size changes are
visible before `RequestedStateChanged` is raised. Disposal is idempotent,
disconnects the site, and raises framework-close before close.

Adapted for ProGPU's portable bridge architecture: a shared immutable state
object carries size, transforms, input policy, automation mode, scale, layout,
visibility, and connection state. Typed internal bridge methods publish
island-requested size, connection, automation, and local-to-client transforms
without reflection or platform objects. The coordinate source composes the
site's 2D affine local-to-client matrix with the live top-level window mapping.
The public local-to-parent matrix retains the complete finite `Matrix4x4`;
the flattened local-to-client coordinate mapping rejects perspective instead
of silently producing incorrect screen coordinates. A zero override scale
selects the positive parent scale; a positive override replaces it.

Every view property read is lock-free fixed `O(1)` work. A changed site setter
performs expected `O(1)` compare/exchange work and publishes one immutable
snapshot. No-op setters reuse the current snapshot without allocation.
Deferral creation and completion are `O(1)`; combined change storage is one
packed flag value. A warmed Release test performs 100,000 complete view-read
and no-op setter iterations with exactly zero managed allocations. This
control-plane state does not initialize WebGPU; connected visual content and
effects remain on the retained WebGPU compositor path.

Focused tests cover stable identities and every property, scale/size/matrix
validation, live site-plus-window coordinate composition, requested-size
event ordering and no-op suppression, nested deferral coalescing, disconnect
cancellation, close ordering and terminal behavior, and allocation-free live
reads/no-op mutation. The slice adds all 54 selected official declarations
exactly. The official comparison advances to 7,745 candidate declarations,
3,893 exact matches, 12,728 missing declarations, and 3,852 extras. No
Microsoft implementation source or method body was inspected.

### Microsoft.UI.Content site capability interfaces

Primary contracts consulted:

- [IContentSiteInput](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.icontentsiteinput)
- [IContentSiteAutomation](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.icontentsiteautomation)
- [IContentSiteAutomation.AutomationProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.content.icontentsiteautomation.automationprovider)
- The pinned official projection metadata and `Microsoft.UI.xml`
  documentation extracted by the deterministic API gate.

Adopted: the exact version-7 public capability boundaries. Input capability
exposes independently mutable keyboard and pointer processing policy.
Automation capability exposes a mutable automation mode, a read-only resolved
provider, and four strongly typed provider-request events for fragment root
and sibling/parent navigation.

The interfaces reuse the existing typed content automation enum and event data
without reflection, boxed adapters, or a parallel provider abstraction. They
add no runtime work or allocation by themselves; the later child-link and
desktop-bridge implementations will own provider resolution and event
lifecycle while sharing these exact contracts. The current slice intentionally
does not claim those still-missing concrete bridge types.

Focused shape tests cover the complete declared property set, mutability,
event set, and exact `TypedEventHandler<IContentSiteAutomation,
ContentSiteAutomationProviderRequestedEventArgs>` identity. The slice adds all
12 selected official declarations exactly. The official comparison advances
to 7,757 candidate declarations, 3,905 exact matches, 12,716 missing
declarations, and 3,852 extras. No Microsoft implementation source or method
body was inspected.

### Microsoft.UI.Xaml automation provider contracts

Primary contracts consulted:

- [IDropTargetProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.idroptargetprovider)
- [IInvokeProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iinvokeprovider)
- [IObjectModelProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iobjectmodelprovider)
- [IScrollItemProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iscrollitemprovider)
- [ITableItemProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.itableitemprovider)
- [ITextChildProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.itextchildprovider)
- [IVirtualizedItemProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.ivirtualizeditemprovider)
- [IExpandCollapseProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iexpandcollapseprovider)
- [IRangeValueProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.irangevalueprovider)
- [IToggleProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.itoggleprovider)
- [IValueProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.ivalueprovider)
- [IGridProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.igridprovider)
- [IGridItemProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.igriditemprovider)
- [ITableProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.itableprovider)
- [IScrollProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iscrollprovider)
- [ISelectionProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iselectionprovider)
- [ISelectionItemProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iselectionitemprovider)
- [ITransformProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.itransformprovider)
- [ITransformProvider2](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.itransformprovider2)
- [IWindowProvider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.provider.iwindowprovider)
- [ExpandCollapseState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.expandcollapsestate)
- [RowOrColumnMajor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.roworcolumnmajor)
- [ScrollAmount](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.scrollamount)
- [ToggleState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.togglestate)
- [WindowInteractionState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.windowinteractionstate)
- [WindowVisualState](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.windowvisualstate)
- [ZoomUnit](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.zoomunit)
- [Grid control pattern](https://learn.microsoft.com/windows/win32/winauto/uiauto-implementinggrid)
- [Table control pattern](https://learn.microsoft.com/windows/win32/winauto/uiauto-implementingtable)
- The pinned official projection metadata and `Microsoft.UI.Xaml.xml`
  documentation extracted by the deterministic API gate.

Adopted: the exact version-1 provider boundaries for drop-target effect
reporting, stateless invocation, underlying object-model access, scroll-item
visibility, virtualized-item realization, table-item row/column header
retrieval, and text-child container/range exposure. The interfaces reuse the
existing typed raw-element and text-range provider contracts. They add no
runtime dispatch, reflection, allocation, platform accessibility bridge, or
rendering work by themselves; custom and framework automation peers can
implement them without a parallel ProGPU-only abstraction.

Focused reflection tests cover the complete declared property and method sets,
read-only shape, parameter count, exact result types, and version-1 WinUI
contract identity. Concrete provider behavior and native accessibility
transport remain separate future slices and are not claimed here.

The stateful provider slice adds the exact version-1 boundaries for controls
that expand or collapse, cycle through toggle states, expose an editable
string value, or expose a numeric value constrained by a range. It also adds
the official integral identities for collapsed, expanded, partially expanded,
leaf, off, on, and indeterminate states. These declarations remain typed,
reflection-free, allocation-free capability contracts. They do not add an
alternate state machine: each automation peer remains responsible for
reporting its current state, enforcing read-only policy and range bounds, and
forwarding state changes through the later platform accessibility transport.

Focused reflection tests reject extra methods or properties, writable
properties, incorrect method parameter or result types, incorrect enum
underlying types or values, and contract-version drift. The stateful slice
adds all 32 selected official declarations exactly.

The grid/table slice adds the exact version-1 provider boundaries for
two-dimensional containers, cells, and header-aware tables. Grid coordinates
are zero-based; cells expose their row, column, spans, and containing provider;
tables expose row/column headers plus the official row-major, column-major,
and indeterminate traversal identities. The declarations deliberately do not
invent cell storage, lookup policy, or a native accessibility transport.
Concrete peers own those behaviors and can implement these typed contracts
without reflection or boxed adapters. Contract reads and calls have no
framework-side allocation or WebGPU work.

Focused reflection tests reject extra members, writable properties, incorrect
parameter/result types, enum-value drift, and contract-version drift. The
grid/table slice adds all 23 selected official declarations exactly.

The scroll/selection slice adds the exact version-1 container and item
boundaries. Scroll providers report independent axis availability, position,
and viewport percentages and accept typed relative or absolute movement.
Selection containers report cardinality policy and selected providers;
selection items report current membership and their typed container, with
separate add, remove, and exclusive-select operations. `ScrollAmount`
preserves the official large-decrement, small-decrement, no-op,
large-increment, and small-increment identities.

These declarations contain no fallback scrolling, selection state machine,
reflection, allocation, platform accessibility transport, or WebGPU work.
Concrete automation peers remain responsible for honoring control-specific
range, cardinality, and event semantics. Focused tests reject extra members,
writable state, incorrect parameters/results, enum-value drift, and
contract-version drift. The slice adds all 30 selected declarations exactly.

The transform/window slice adds the exact version-1 movement, resize,
rotation, viewport zoom, and window-state provider boundaries.
`ITransformProvider2` inherits the base transform provider and adds only its
four zoom properties and two zoom operations. The window provider reports
interaction, visual, modal, topmost, maximize, and minimize capabilities and
exposes close, visual-state transition, and bounded idle-wait operations.
`WindowInteractionState`, `WindowVisualState`, and `ZoomUnit` preserve every
official integral identity.

These are capability contracts only. They do not move a visual, mutate a
native window, block a UI thread, allocate state, invoke WebGPU, or implement
an accessibility transport. Concrete peers must validate requested geometry,
zoom levels, process-idle policy, and platform support. Focused tests reject
inheritance drift, extra or writable members, incorrect parameters/results,
enum-value drift, and contract-version drift. This slice adds all 50 selected
official declarations exactly. Across the six automation-provider slices,
all 159 selected declarations now match. The official comparison advances to
7,916 candidate declarations, 4,064 exact matches, 12,557 missing
declarations, and 3,852 extras. No Microsoft implementation source or method
body was inspected.

### Microsoft.UI.Windowing

Primary contracts consulted:

- [AppWindowPresenter](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindowpresenter)
- [OverlappedPresenter](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.overlappedpresenter)
- [CreateForContextMenu](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.overlappedpresenter.createforcontextmenu)
- [CreateForDialog](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.overlappedpresenter.createfordialog)
- [CreateForToolWindow](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.overlappedpresenter.createfortoolwindow)
- [CompactOverlayPresenter](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.compactoverlaypresenter)
- [AppWindow](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow)
- [AppWindowTitleBar](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindowtitlebar)
- [DisplayArea](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.displayarea)
- [DisplayAreaWatcher](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.displayareawatcher)

Adopted: all eight official windowing enums with exact values and contract
versions, the presenter inheritance/factory contracts, the documented
context-menu/dialog/tool-window presets, compact-overlay initial size, bounded
dimension validation, and retained presenter configuration/state. Property
reads and mutations are fixed `O(1)` value operations. A warmed Release
invariant verifies exactly zero managed allocations across 100,000 presenter
property-read iterations.

`AppWindow` is a dispatcher-affine control plane over the existing
`Microsoft.UI.Xaml.Window`, `SilkWindowController`, and platform-native window
backends. It preserves stable typed identity, owner and dispatcher association,
registry lookup, geometry, presenter application, switcher/title state,
cancellable application destruction, non-cancellable dispatcher run-down, and
change flags. Showing a window continues through the same native lifetime that
creates the WebGPU presentation surface; creating or configuring an `AppWindow`
alone does not initialize WebGPU. Showing without activation is represented
explicitly through the native Silk path or `IWindowActivationHost`.

Display snapshots are supplied by `IWindowingDisplayAreaProvider`.
`FindAll` and point/rectangle fallback selection are `O(D)` time for `D`
displays; returned ownership is `O(D)`. A watcher retains `O(D)` identity/state
and diffs a platform transition in expected `O(D)` time. Icon and Z-order
operations use `IAppWindowPlatformProvider`; an unavailable or rejected native
operation fails explicitly rather than mutating only managed state.

Focused tests cover presenter presets and state, dispatcher affinity and
shutdown destruction, cancellable close, identity lookup, geometry/change
flags, title-bar reset/options, typed icon and Z-order dispatch, explicit
unsupported behavior, display containment/intersection/nearest fallback,
watcher add/update/remove/status ordering, contract versions, and zero managed
allocations across 100,000 warmed `AppWindow` property-read iterations.

Deferred behavioral gates: platform adapters still need to apply retained
title-bar colors and drag rectangles where the OS supports them, and native
close requests need a pre-close cancellation callback on every host. The
declaration report records 192/192 exact `Microsoft.UI.Windowing` entries with
no missing or extra entries; this does not overstate those remaining host
integration tasks.

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
