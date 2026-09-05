# ProGPU.CAD COPYBASE/PASTECLIP Research

Date: 2026-09-01

## Scope and clean-room provenance

This slice implements CAD-object COPYBASE and PASTECLIP across the shared
desktop/browser clipboard seam. It was designed from the public command
contracts below and from original ProGPU-owned document-session, edit-history,
coordinate-input, selection, and retained-scene contracts. No third-party
implementation source, private clipboard format, helper structure, or control
flow was copied or adapted.

Authoritative behavior sources:

- Autodesk's public
  [COPYBASE contract](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-59113CD3-B5EC-404B-989C-F98F4B70EDB5-htm.html)
  specifies that selected objects and a caller-supplied base point are copied
  and may be pasted in the same or another drawing relative to that base.
- Autodesk's public
  [PASTECLIP contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-F7A49705-42BC-46AC-922A-862EE6836CCF.htm)
  specifies that drawing objects are restored from the highest-fidelity
  available clipboard representation.

ProGPU-owned implementation provenance:

- `CadDocumentSession` supplies synchronized, non-retained document reads and
  monotonic edit generations.
- `CadEditCommand`, `CadDocumentHistory`, and the existing selection duplicate
  commands supply exact retained graph identity and one-generation
  Apply/Undo/Redo ownership.
- `CadCoordinateInput`, `CadSampleCanvas`, and `CadSampleView` supply the shared
  current-UCS/global-last-point grammar, object/grid acquisition precedence,
  pointer routing, and desktop/browser controls.
- The existing ACadSharp dependency is consumed only through its public
  clone, collection, and DXF reader/writer APIs.

## Envelope and bounds

`CadClipboardCodec` creates a private version-1 text envelope containing exact
binary64 base-point bits, source entity count, binary-DXF byte count, a SHA-256
digest, and Base64 binary DXF. Binary DXF is used as the dependency-complete
interchange payload so supported layers, linetypes, text styles, block
definitions, attributes, and entity-owned data cross document boundaries
without a reflection-based property serializer or a renderer-specific format.
The envelope is independent of process pointers and host ABI.

The default source bound is 65,536 unique semantic model-space roots and the
DXF bound is 64 MiB. A bounded output stream rejects the writer before it can
grow past the byte budget. Decode rejects foreign text, malformed headers,
non-finite points, oversized Base64, byte-count mismatches, checksum failures,
reader failures, and entity-count mismatches before document mutation. Source
handles are deduplicated in caller order. Construction is `O(E + B)` time and
storage for `E` selected entity graphs and `B` encoded bytes; Base64 transport
uses `O(B)` additional text storage. Clipboard work is explicit user action and
never enters render, compile, upload, replay, or frame hot paths.

## Paste ownership and conflict policy

Decode returns detached graphs. `CadPasteModelSpaceEntitiesCommand` clones and
translates the complete payload before Apply, then publishes one placement-major
model-space batch. Undo detaches that exact batch; Redo restores those exact
graphs without parsing or cloning again. The source clipboard and source drawing
remain independent. Translation uses the existing ProGPU entity-transform
contract, including OCS SOLID and persisted DIMENSION handling; unsupported
modeler transforms fail before destination mutation.

Named records use destination-wins semantics: when a destination already owns a
layer, linetype, style, material, or block with the same persisted name, pasted
entities bind to that destination record. Otherwise the detached dependency is
registered with the destination. This is deterministic and avoids silently
rewriting existing drawing-wide definitions. A later explicit import-conflict
UI may offer rename or replacement; it is not inferred during ordinary paste.

The shared shell exposes `Copy base…` and `Paste…`. Each accepts one snapped
pointer point or bounded absolute/current-UCS or global-last-relative typed
coordinate. COPYBASE changes no document
generation. PASTECLIP commits one history action and one snapshot/picture
replacement. Escape, document replacement, or selection teardown discards an
uncommitted prompt. The current scope intentionally rejects ordinary text,
spreadsheet, OLE, image, PASTESPEC, and platform-native AutoCAD private formats;
those are separate interoperability adapters, not lossy fallbacks for CAD
objects.

## Managed/native applicability and validation

This change adds no shader, C ABI, native module, generated wire declaration,
atlas, GPU cache key, upload contract, or device resource. Both renderers consume
the same immutable snapshot and retained picture rebuilt after the one atomic
paste. A matched regression compiles a cross-document pasted line and nested
block through `GpuPictureNativeSceneCompiler` and verifies equal flattened
command semantics.

Focused tests cover exact base/insertion translation, source deduplication,
cross-document layer and block dependency behavior, destination named-record
reuse, byte/entity bounds, foreign/tampered/checksum rejection, exact retained
identity and handles through Undo/Redo, shared click/typed point prompts, and
managed/native replay.
