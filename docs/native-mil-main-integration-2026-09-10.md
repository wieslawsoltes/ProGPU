# Native MIL integration with System.Drawing main

The additional release hour starts at 11:58 UTC on 2026-09-10. It does not waive
required checks or authorize a red merge. ProGPU main advanced from `73cda9a5`
to `8842f828` when #140 merged; this integration retains both histories, including
the already completed #155 rebase and native MIL fixes through `97eef74f`.

## Conflict decisions

- Keep both Win2D/WindowsAppSDK and System.Drawing test package declarations.
- Classify the complete combined package graph: 80 projects, not either parent's
  standalone count. The existing per-project and dependency audits remain.
- Keep native pad/conical spread flags and hatch sets alongside the new tile and
  path-gradient kinds and their boundary limits. Retain both native brush tests.
- Apply projective texture coordinates before address-mode mapping and the shared
  sampler, retaining cubic filtering, ignore-alpha and premultiplied image paths.
- Keep texture address modes, raster operations and explicit opacity together.
  Native destination mapping uses #140's quad transform and the retained opacity.
- Retain the compact texture flags in RenderCommand; add the new texture union
  views without reintroducing duplicate boolean fields or changing their meaning.
- Preserve checked Win32 enabled-state handling and the modal input gate. Keep
  #140's opacity and Z-order operations, not its duplicate raw EnableWindow path.

Conflict-marker removal is not runtime proof. Build and test the combined head
using the existing prepared dependency worktree; the isolated merge worktree has
no initialized submodules. Exact-head CI and complete package/application gates
remain mandatory, including the outstanding rendering failures in
`native-mil-release-validation-2026-09-10.md` and the later collapsed-group fixture
recorded in `native-mil-stroke-spine-bounds.md`.
