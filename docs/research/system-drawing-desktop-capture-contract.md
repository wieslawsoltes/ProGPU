# System.Drawing typed desktop-capture contract

Date: 2026-08-27

## Scope

This checkpoint restores all four official `Graphics.CopyFromScreen` overloads
without importing an HDC or pretending that a Silk window is the operating
system desktop. A process-scoped `ProGPU.SystemDrawing.IDesktopCaptureService`
is the narrow local-OS seam. It fills caller-owned, exact-length RGBA8 storage
for one device-pixel screen rectangle and cannot retain the supplied span.

`DesktopCaptureServices.Register` gives the registration one disposable owner,
rejects ambiguous providers, and restores the explicit unsupported boundary
when that owner is disposed. The separate ProGPU namespace avoids adding a
noncanonical member to `System.Drawing.Graphics` while allowing
LibreWinForms and ordinary ProGPU hosts to install a typed adapter without
reflection, assembly scans, private-field probes, or duck typing.

## Managed behavior and ownership

The default overloads select `CopyPixelOperation.SourceCopy`. The operation
overloads accept the official SourceCopy value plus the documented
`CaptureBlt` and `NoMirrorBitmap` modifiers. The provider writes into storage
allocated by `Graphics`; ProGPU transfers that owned storage into a temporary
CPU bitmap and records an unscaled image command. The retained drawing command
owns its texture lease, so the provider's source and the temporary bitmap may
be mutated or disposed immediately after the call.

Zero-area copies are no-ops after graphics and operation validation. Negative
dimensions and invalid operation identities fail before provider access. A
missing provider throws `PlatformNotSupportedException` at the typed local-OS
boundary. Raster operations that depend on the current destination or a GDI
pattern brush throw `NotSupportedException`; silently treating them as
SourceCopy would be incorrect. A subsequent checkpoint must add an explicit
destination/pattern raster-operation contract before removing that limitation.

## Platform boundary

Silk.NET windowing does not expose a portable desktop screenshot primitive.
Rendering LibreWinForms-owned visuals into a synthetic image would omit other
applications, native decorations, compositor effects, capture permissions,
and protected-window policy. This checkpoint therefore does not install a fake
Silk capture provider. LibreWinForms will bridge the typed service to explicit
Win32, CoreGraphics, X11, or portal adapters as those capabilities are added;
unsupported environments remain honest and compile-time complete.

## Quality and performance gates

Focused tests cover all four overloads, exact source/destination coordinates,
representative captured pixels, provider-source mutation, single-owner
registration, registration disposal, missing capability, validation order,
explicit unsupported raster operations, and disposed graphics. The warmed
16-by-16 SourceCopy test includes materialization and permits at most 65,536
bytes across sixteen operations (4 KiB per operation); the 1 KiB pixel payload
is the unavoidable lower-order component.

`DesktopCaptureBenchmarks.CaptureAndMaterialize64x64` measures the complete
typed-provider, owned-pixel, retained-command, and bitmap-materialization path.
The full Debug/Release drawing suites, strict ApiCompat gate, documentation
verifier, package build, and downstream LibreWinForms source/package lanes are
required before delivery.

Removing the four exact member suppressions reduces reviewed debt from 0
missing types, 11 missing members, 13 other diagnostics, and 24 total to 0
missing types, 7 missing members, 13 other diagnostics, and 20 total. The
remaining member diagnostics are native HDC/HWND/HICON/resource/metafile
capabilities and stay explicit typed-adapter work.
