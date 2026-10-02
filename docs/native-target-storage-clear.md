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

This first private encoder does not yet connect a scene command or either
Direct2D source host. Those connections and independent original Windows/full
provider controls are required before antialiased Clear admission. No validation
has been executed for this implementation stack; qualify only the final
integrated tips, retaining the existing pixel, lifetime, package and UI gates.
