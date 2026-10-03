# Explicit null VisualBrush source snapshots

`PortableTileBrush.Visual` is a Visual-only factory retaining the original source
object or an explicitly unassigned, null Visual. Its full-arity mapping arguments
match the existing constructor without the brush-kind argument. It retains the
same numeric snapshot and transform-flag policy, without reading source state,
inventing a visual, or treating unavailable bounds as empty content.

The original public constructor still rejects null content for every kind,
including Visual. Image/Drawing constructors, enum values, immutable properties,
source identity, opacity normalization and disabled-transform identity remain
unchanged. `Content` now has its honest nullable annotation; consumers may admit
null only through the explicit Visual contract. The factory does not validate
source ownership, render policy, finite mapping, package compatibility or a
consumer's ability to capture that source.

Paired WPF source/native/managed consumers must distinguish actual null content,
an existing empty visual, unavailable source metadata and capture failure. An
empty sampler still needs the real receiving frame and owned cleared texture;
null is not a successful missing texture or an implicit-input fallback. The
adapter must be rebuilt against the qualified producer before staging. The
unchanged old producer pin does not provide this newly added API.

`PortableTileBrushTests` authors legacy null rejection, all retained snapshot
fields, exact source identity, absent source callbacks, nullable Visual state,
both transform flags, signed-zero bits and legacy opacity/unknown-value policy.
This changes only a neutral reference snapshot; no native wire, GPU algorithm or
external dependency is added. No tests, builds, verifiers or runtime validation
were executed for this change. Full consumer/package/source gates remain open.
