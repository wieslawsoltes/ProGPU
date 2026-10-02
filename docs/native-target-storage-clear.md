# Native target-storage Clear

The private target-storage encoder replaces the actual current attachment inside
its binary scissor. A single fullscreen triangle writes the original straight
color after one premultiplication, with blending disabled. Transparent colors
therefore erase storage. Actual IGNORE-alpha targets select alpha one; neither
an opaque clear color nor an ancestor proves that target property.

Source transforms, geometry bounds, opacity and antialiased masks do not belong
to this storage operation. The caller retains source order, ends the previous
pass, identifies the exact live target and supplies its actual dimensions and
scissor. Enclosing source layers apply their own group coverage when they pop.
For an antialiased source clip, preserving the background before drawing and
replacing it through that clip at pop is a separate required recorder contract.

Both native providers compile the same embedded `TargetClear.wgsl` and C++
encoder. Pipeline creation is lazy. Its one 16-byte uniform and binding use the
existing submission-retired raster-resource lease; no target handle escapes,
and the encoder neither submits, waits nor reads back. Complexity is O(covered
pixels) GPU work and O(1) command/uniform storage per clear.

The builder now prepares only the source-declared AA clip chain inward of the
nearest ordinary layer. Each exact unit-opacity rectangular clip reuses existing
`INITIALIZE_FROM_BACKGROUND` composition. The ordinary owner, if elided, becomes
isolated without changing its own opacity, masks or initialization policy.
Older AA scopes outside that owner are unchanged. SAVE frames are not storage.
The atomic preflight retains exact original mask/bounds identity, rejects other
group families, and counts both historical closed child peaks and currently open
children without double-counting their live materialized depth. Publication is
allocation-free; ordinary draw-only layers remain eligible for elision. Existing
nearest-layer isolation uses this same depth accounting.

Authored builder controls compare full retained streams against independently
declared background/isolation flags, including idempotence, SAVE scopes, a source
owner inside an outer AA clip, eight non-clip rejection families, and exact open
and historical capacity limits. They have not been executed.

The additive required `CLEAR_TARGET` command (kind 5) now retains the original
16-byte straight color without a geometry resource. Native and managed writers
pair atomic byte validation. Older readers reject the unknown required command.
Actual replay splits the prior bundle, uses the current attachment's dimensions
and alpha policy, and retains source order across ordinary draws and nested
pictures. This is not a regular geometry draw family and emits no source hit
primitive. Each executed occurrence reports one draw and a 16-byte upload,
including warm replay. Per-draw masks reject before publication.

Binary SAVE clips use the original physical pixel-center boundary
`ceil(double(edge) - 0.5)`, with actual independent-axis DPI, viewport and target
localization. AA layer allocation remains outward-rounded; it is never replaced
by this binary clip calculation. No-op clips encode no draw.

Both actual Direct2D recorders now use this retained operation when an AA
`PushAxisAlignedClip` scope is active. Later transforms, even singular ones,
cannot move Clear. The nearest ordinary source layer keeps its alpha/opacity
identity, while AA descendants preserve background and resolve coverage once
at pop. A bounded AA child supplies its own current storage extent; without
that child an unbounded target-independent ordinary owner still requires its
existing explicit target metrics. Leading/full-target and all-aliased Clear
keep their original paths. Empty source intersections add no draw or isolation;
portable DPI-history and Windows original callback counts remain distinct.

Raw/managed command, source-order, nested-picture, exact fractional clip and
provider controls are authored, with independent original Windows/source AA
controls following in the stack. No validation has been executed for this
implementation stack; qualify only the final integrated tips, retaining the
existing pixel, lifetime, package and UI gates. This does not advertise managed
Canvas routing, general device-context operations or desktop UI parity.
