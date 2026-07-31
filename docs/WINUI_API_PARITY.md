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
