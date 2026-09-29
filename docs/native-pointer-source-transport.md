# Native pointer source transport

`PortablePointerInput` carries immutable native pointer data across the neutral
WPF boundary without a backend assembly dependency: original event kind, double
coordinates, monotonic timestamp, native button/click identity, modifier flags,
scroll units and both phase fields. Coordinate copies retain all other fields.
Points use the receiving client's coordinate frame; line scroll deltas must never
be DPI-scaled or interpreted as points. Constructor validation rejects malformed
event kinds, nonfinite values, unknown modifier/unit bits and inconsistent button
or scroll metadata. Native phase bits remain exact; each provider must explicitly
admit its phase semantics before using them. The actual owned Cocoa provider tags
scroll packets with the AppKit phase protocol; hosts must carry that tag alongside
the untouched bits. An unspecified protocol does not become AppKit merely because
the process runs on macOS. Unknown protocol values and protocol tags on non-scroll
packets are rejected. Untagged phase values remain transportable for compatibility,
but that does not admit them to a source phase interpreter.

The neutral packet keeps its original constructor identity and uses a separate
overload for the explicit protocol. Coordinate copies retain that protocol. The
backend event uses a non-positional property so its original constructor and
deconstruction signatures remain unchanged.

`IPortableNativePointerInputService` is an optional source capability separate
from legacy key/wheel input. An old provider must not silently discard metadata.
The host's normalized shortcut modifiers are passed separately, so Command-to-
Control policy does not overwrite the original native modifier snapshot. Existing
legacy constructors and service implementations remain unchanged.

This contract does not itself admit a native popup. Source implementations still
need exact capture ownership/cancellation, native event timing/click behavior,
precise scrolling and lifecycle handling before selecting the owned factory.
Unsupported capability or units must fail explicitly, not use legacy wheel
notches, a different renderer or an owner-surface fallback.

Contract cases cover exact transformed packets, all button-event kinds, invalid
construction, atomic failed coordinate copies, original constructor identity and
explicit protocol preservation. Native UI and
application/package qualification remain separate gates.
