# System.Drawing LOGFONT contract

## Scope

This slice restores the .NET 10 public `System.Drawing.Interop.LOGFONT` identity and all eight `Font.FromLogFont` / `Font.ToLogFont` entry points. The implementation is original portable managed code over ProGPU font discovery; the pinned `Microsoft.WindowsDesktop.App.Ref` 10.0.11 assembly and public observable tests define its surface and behavior.

The Unicode structure is sequential and exactly 92 bytes: five 32-bit scalar fields, eight byte fields, and a fixed 32-character face-name buffer. The public `Span<char> lfFaceName` exposes that caller-owned buffer without allocation.

## Portable conversion

Typed import maps the logical face name, the `@` vertical-font prefix, charset, weight threshold, italic, underline, and strikeout flags into an owned `Font`. A completely empty structure is invalid; a structure that requests default font selection through another field can use the portable generic sans-serif fallback. Logical height is represented in world units because there is no implicit native device context.

Typed export uses the supplied `Graphics.DpiY`, or 96 DPI for the overload without a graphics context. It writes a negative character height, normal/bold GDI weight, style flags, charset, and a bounded null-terminated face name. Width, escapement, orientation, precision, quality, and pitch remain zero when no corresponding `Font` state exists.

The legacy `object` overloads accept a boxed canonical `LOGFONT`. Export mutates that box directly through its exact typed layout. Arbitrary user-defined lookalike objects are rejected: supporting them would require runtime layout probing or reflection-driven marshaling, which is not an acceptable LibreWinForms product path.

## Platform boundary

HDC-aware LOGFONT import remains an explicit Windows GDI adapter boundary. A zero handle is rejected, and a nonzero handle fails with `PlatformNotSupportedException` until a typed Windows adapter supplies device-specific LOGFONT interpretation. The later [native font/graphics interop checkpoint](system-drawing-native-interop-contract.md) routes `Font.FromHdc` through a typed selected-font service. `FromHfont`, `ToHfont`, and the LOGFONT-plus-HDC overload remain separate reviewed native-handle debt; this slice does not fabricate handles or infer native state.

## Gates and measured debt

Nine focused tests cover exact layout, face-buffer access, style/charset/vertical round trips, empty/default selection, boxed typed mutation, rejection of arbitrary layouts, graphics disposal, the HDC boundary, and exactly zero managed allocation across 10,000 warmed typed exports. The complete drawing suite and strict ApiCompat gate remain authoritative for integration.

ApiCompat removes one missing-type and eight missing-member suppressions. Measured debt falls from 12 missing types, 112 missing members, 15 other diagnostics, and 139 total to 11 missing types, 104 missing members, 15 other diagnostics, and 130 total, with no breaking changes or stale suppressions.
