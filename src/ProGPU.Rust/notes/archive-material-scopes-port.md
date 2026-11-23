# Original retained archive material and scope producers

Production port, implementation-first. No build, test, shader/runtime/browser run,
benchmark, or new fixture was performed. Source formatting only; no artifacts.

- `ArchiveMaterialEnvironment` freezes actual lookup-root, per-element theme and
  parent inventories, DependencyObject ancestor ordering, application resources,
  selected high-contrast state, and actual platform results. Original dictionary
  theme/default/local/reverse-merged order and recursion ownership are retained.
  `ArchiveThemeValue` distinguishes aliases, brushes, found non-brush values and
  null. Alias cycles use original **key-only** identity, retaining inherited or
  explicitly replaced lookup roots. Theme-marker opacity is not multiplied by the
  original resolver. Missing platform input rejects when needed; a supplied empty
  platform snapshot explicitly represents known missing resources. No operating
  system colors, active-window state, family aliases, or framework resources are
  inferred. `ArchiveMaterialProvider` captures once before replay publication;
  no provider callback is retained for encoding. Existing plain brushes borrow
  the original command and do not clone through the environment path.
- Replay accepts explicit material environments across nested pictures, pens and
  opacity-mask sources. The original rectangle/rounded-rectangle factory emits
  the actual `BackdropMaterialDraw` before the independent pen. Original opacity,
  source texture/host capture distinction, radii, sampling and shader sanitization
  remain in the existing canonical engine. Typed source-only extension 13 owns
  `BackdropMaterialParameters`; missing decoded DataParam is the original no-op.
  `ArchiveRecorder` exposes both rectangle factories and direct material input.
  Source capture retains the owner; serialization explicitly rejects this payload
  rather than dropping resources or inventing an archive wire field.
- Clip-kind bookkeeping, axis-scissor stack and shared geometry/opacity mask stack
  now follow the original three independent inventories. Crossed pops restore
  only the selected state, preserving current blend and the other coverage state.
  A geometry-clip pop removes the current shared mask exactly as the source does.
  Ordinary Scene public LIFO scopes remain unchanged. Immutable mask components
  freeze the original source scissor for painted mask capture, including when a
  later independent pop removes the receiving scissor. Canonical analytic mask
  routes retain their original admission and do not receive a fabricated clip.
  Source placement, retained state compilation, source capture and archive capture
  preserve the new scope operator. No clip envelope replaces mask geometry.
- Coordinated peer dispatches connect original 3D/ACIS/extension 1/2, compile-only
  spline extension 4, and root CustomGrid extension 7. Dedicated extension shaders
  retain their source mask policy; direct R8 masks still admit only the original
  four painter families. Camera/3D implementation provenance belongs to its peer.

External boundaries: selected platform/theme provider snapshots, non-string source
resource keys (not present in the archive wire), framework tree mutation tracking,
and original native/provider/package/application pixel qualification remain
independent. Unknown source extensions and unsupported runtime shader language
routes remain explicit gates. This implementation is not validation evidence.

Original owned sources (SHA-256):

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Vector/ThemeResourceBrush.cs` | `314537f9f6a8fe2c663846dab61c2ff8417966ed3f648ff698a381d1ec3766ef` |
| `src/ProGPU.Vector/BackdropMaterialBrush.cs` | `de6731e8e9fcedc6fefe86c05d42cd6be655d5b0e5e1c677a553fbf1e672ca2a` |
| `src/ProGPU.WinUI/Core/XamlResourceResolver.cs` | `23746588d750f8d1e4f3d4b0566f82e75d948e7840f39cfce730ab3a4e80f6f8` |
| `src/ProGPU.WinUI/Core/ResourceDictionary.cs` | `848ac16d092c954e58a65873d0be9243fce5de247031b41d77c06a06cedaf398` |
| `src/ProGPU.WinUI/Core/FrameworkElement.XamlResources.cs` | `21fac48b8777fbbd53ed893be17780ced1a424017b26cfef17945b0ca5a757af` |
| `src/ProGPU.WinUI/Core/ThemeManager.cs` | `60ba8caf9dece0566bd64c81cec0d530965439275f472d27c37d0a3c5e9a9c0f` |
| `src/ProGPU.Scene/RenderCommand.cs` | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |
| `src/ProGPU.Scene/BackdropMaterialParams.cs` | `67b82b4741477a5e9df263e6e602c3961e159a36b0eba2643e4436ec76f472ab` |
| `src/ProGPU.Scene/DrawingContextBackdropExtensions.cs` | `f2e495a832d493631d96510d2e50ea71ede72c3f6e9b77e7b3bccd974dccba6b` |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Scene/Extensions/BackdropMaterialExtensionPipeline.cs` | `390b80a12e7bd82e2fcca3cc41c7998c2ed79e18ebbac56d7bdaf5c6be6d510b` |
