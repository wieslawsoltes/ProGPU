# System.Drawing managed base-shape and serialization contract

Date: 2026-08-27

## Scope

This checkpoint restores managed inheritance and serialization contracts that
do not require a native GDI/GDI+ pointer:

- `Graphics` derives from `MarshalByRefObject`;
- `Icon` derives from `MarshalByRefObject`, is serializable, and implements
  `ISerializable`; and
- `Image` is serializable and implements `ISerializable` over its existing
  managed encoder pipeline.

The .NET 10 reference assembly and canonical declarations define the shape.
The canonical serialized field names are retained for compatibility:
`IconData` plus `IconSize` for `Icon`, and `Data` for `Image`.

## Managed behavior

Icon serialization writes an owned single-image ICO through `Icon.Save` and
records the exact logical size. Deserialization decodes that owned byte array
through the existing ProGPU bitmap pipeline and applies the stored size. Bitmap
serialization writes an owned encoded snapshot using `RawFormat`; metafiles
retain their already-owned validated WMF/EMF source snapshot. Private
serialization constructors reconstruct each concrete type from the `Data`
field. Neither path retains caller buffers, streams, files, native handles, or
renderer state.

The explicit `ISerializable` implementations validate `SerializationInfo` and
use no runtime field discovery or private-state scans. Binary formatter use is
obsolete in modern .NET, but preserving these public interface and data-shape
contracts remains necessary for API compatibility, component-model tooling,
and callers that invoke `ISerializable` directly.

## Deliberate native boundaries

The internal canonical `IGraphics` and `IImage` identities ultimately inherit
GDI+ pointer/HDC contracts. ProGPU does not add empty interfaces with those
names merely to silence ApiCompat. They remain suppressed until an explicit
typed native adapter can implement the real ownership and lifetime semantics.
The same rule applies to `Icon`'s internal `IIcon : IHandle<HICON>` shape and
the `IPointer<Gp*>` and `IHandle<HDC>` diagnostics.

## Quality gates

Focused tests verify exact base types and interfaces, serializable attributes,
canonical field names and types, ICO/PNG signatures, size and representative
pixels, reconstruction through the private serialization constructor, owned
data after mutation and original disposal, and null-information failures. The
complete Debug and Release drawing suites, ApiCompat verifier, documentation
verifier, and package build remain required before delivery.

Removing the two base-type suppressions reduces reviewed debt from 0 missing
types, 11 missing members, 15 other diagnostics, and 26 total to 0 missing
types, 11 missing members, 13 other diagnostics, and 24 total. The `Icon`
type-level shape suppression remains because completing `ISerializable` exposes
the separate native `IIcon : IHandle<HICON>` diagnostic.
