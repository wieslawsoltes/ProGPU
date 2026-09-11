# Native caret metadata for source-owned WPF editing

## Core dependency and contract

The LibreWPF Showcase TextBox/RichTextBox focus-and-type path reaches source
CaretElement rendering. On Windows its old Win32SetCaretPos route accepted any
presentation source, even though Win32CreateCaret could not create a caret for
the non-HWND PortablePresentationSource. That caused a source-local Win32 call
without native caret ownership. OS guards alone do not select this ownership.

ProGPU now provides two separate contracts:

- `IPortableNativeCaretHost` attaches an optional typed service to a presentation
  source. `IPortableNativeCaretService` borrows an opaque caret owner and a native
  source client-coordinate rectangle; it never exposes WPF structs or a source
  handle as a native HWND. False explicitly reports unavailable/failed OS mirror
  synchronization, not renderer fallback or successful accessibility qualification.
- `NativeWindowCaret` mirrors metadata on a caller-owned live Win32 window. It
  creates a hidden system caret without a GDI bitmap and never calls ShowCaret.
  Source WPF still draws the actual caret, including blinking, italic/bidi and
  interim state. The mirror is an insertion-bar rectangle, not a replacement for
  shaped text, visual caret geometry, IME or a complete accessibility provider.

The native window must remain live on the creating thread until the mirror is
disposed. Native admission validates a real local Win32 window/thread/process;
other native platform kinds return unavailable. Callers update only the active
source editor. The rectangle uses source client units, not desktop coordinates.
The adapter must apply its actual source-to-client transform once, preserving
independent desktop geometry. Windows mixed-DPI/awareness behavior remains a
required native qualification case, not proven by these conversions alone.

## Ownership and failure behavior

Windows has one caret per input queue. ProGPU additionally tracks the current
mirror and opaque editor identity on the host thread. Successful creation replaces
previous ownership; failed creation does not publish a new owner. Repeated
placement updates reuse the shape unless owner/size/native ownership changes.
A late release from an old editor or different window cannot destroy the new
ProGPU caret. Queries detect removal or replacement by a different native HWND;
release does not destroy that other window's caret. Reentrant mutations are
rejected across mirrors on the same thread, and callback exceptions release the
mutation guard without being swallowed.

This API cannot distinguish an external library replacing a caret with another
caret on the *same HWND*: Windows exposes no caret-generation token. Such a library
must coordinate ownership through the host instead of independently sharing that
window's caret. Do not present HWND equality as a universal native ownership proof.

Release failure retains ownership for an explicit retry and is not silently
reported as disposal. LibreWPF releases the mirror before setting its host's
disposed flag or destroying the native window. Source caret hide, inactive
selection, adorner migration/detach and failed synchronization release only that
caret identity. Missing optional service still selects the portable branch; it
must not send a fake portable handle or unowned SetCaretPos call to user32.

The bridge resolves the actual live native window through ProGPU's controller,
preserves explicitly supplied caret services, and renews its mirror when native
window identity changes. Its source service remains independent of managed/native
renderer mode and survives renderer-only device/target renewal. The C++ renderer
has no OS queue caret ownership to duplicate; visible source caret commands still
flow through the existing native or managed composition path.

## Cost and provenance

Construction allocates one bounded operations/mirror pair on first use. Updates
and releases are O(1), with no managed allocation, GDI bitmap, pixel readback or
GPU submissions. The state machine and fixed native queries are ordered ownership
operations, not independent-lane compute; SIMD/GPU-stage fallback policies do not
apply. No speed or platform-parity claim is made before final measurement.

Original ProGPU implementation extends the backend/interop contracts at
`a902578a`; source adaptation extends LibreWPF `545b9845e`. No foreign engine
implementation was copied. The design uses these primary Win32 contracts:

- [CreateCaret](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createcaret):
  queue ownership, replacement, hidden initial state and bitmap-free creation.
- [SetCaretPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcaretpos):
  caret positioning in the owning window's client coordinates.
- [GetGUIThreadInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo):
  query the calling GUI thread explicitly, not the foreground thread, with the
  actual structure size and native pointer-sized window fields.
- [DestroyCaret](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-destroycaret):
  release the queue resource on focus/lifetime completion.

This is platform caret synchronization, not a new text/shaping/rendering or
startup architecture. Existing retained paragraphs, glyph/caret source maps,
rendering quality and CPU/GPU execution policy are unchanged.

## Authored qualification

`NativeWindowCaretTests` covers shape reuse, size/owner replacement, delayed
release, native replacement/removal, failed create/query/position/release,
invalid input, wrong-thread use, reentrancy and callback exception identity.
The existing `NativeWindowInputWindowsTests` GUI lane adds an actual native
window test that queries caret owner, rectangle and hidden state, then confirms
owner-specific release. It is Windows-only, not an environment-bypassed test.

LibreWPF's source document fixtures use an actual CaretElement and portable source
to check typed placement at nonidentity source DPI, movement, rejection cleanup,
selection deactivation, detach and missing-service routing while preserving real
drawing. Bridge/source guards keep this route ahead of legacy SetCaretPos.

All fixtures are authored for the final validation phase. Compilation is not
test execution, native caret accessibility evidence, package-mode success or
permission to remove the Windows SDK admission guard. Visible text editing,
focus transitions, DPI changes and native/managed comparison remain in the final
Showcase/Toolkit/Windows gate. Cocoa/Linux native IME/accessibility mirrors and broader
COM/Direct2D/Win2D completeness are not implemented by this Windows caret contract.

Build-only checkpoint (2026-09-09): SDK 10.0.201 compiled ProGPU.Tests with
`build --no-restore -m:1 -v:quiet '-clp:ErrorsOnly;Summary'`, 0 warnings and
0 errors in 28.06 seconds. No tests, native GUI calls, verifiers, benchmarks or
CI qualification were executed.
