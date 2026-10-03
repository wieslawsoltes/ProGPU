# Owned BitmapCacheBrush sampler raster

This corrects the unqualified PR343/344 sampler realization. Ordinary
BitmapCacheBrush painting and the shader sampler are different consumers: the
sampler receives the selected raw cache texture, independently of receiving
dimensions and brush opacity/absolute/relative transforms. Original source
research identifies the boundary in [ShaderEffect.cpp](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/ShaderEffect.cpp#L419-L479).
No foreign implementation was copied. The producer uses ProGPU's existing owned
picture, cache-layer, source traversal and shader executors.

Every shader cache brush requires `SetBitmapCacheBrushRasterPolicy`, including
null, explicit empty and zero-scale sources. The versioned C record preserves
two original primary-display float scales, actual device limits and nonzero
source revision. It has no receiving-DPI or 96-DPI fallback. The owned native
engine query reads actual live-device texture limits on its creating thread;
borrowed/unknown/lost ownership never produces inferred limits. The source must
retain policy/device identity and refresh its sideband coherently.

## Frame arithmetic

The independent O(1) frame implementation follows this researched mathematical
contract, not a copied helper or a claim that `ceil` is equivalent:

- Narrow original left/top/right/bottom to binary32, then subtract each edge
  pair in binary32. Original double bounds remain in resource identity.
- For each axis, `q = (double(extent) * selectedScale) * double(primaryScale)`.
  Let `n` be its nonnegative truncated integer, `a = float(n)`, `b = float(q)`.
  Increment `n` unless the binary32 relative difference has magnitude strictly
  below `10 * 2^-23` (the denominator is `b`, or one for zero).
- If the rounded extent exceeds the actual axis limit, multiply primary scale
  by `limit / roundedExtent` in double. The source raster scale is then the
  float-narrowed product of selected scale and adjusted primary scale.
- Translation is binary32 `-sourceOrigin * rasterScale`. This owned frame is
  recorded once. The cache page composites at identity into the actual physical
  picture extent; no inverse/forward cancellation or receiving-frame fitting.

The behavioral evidence is [VisualCache dimensions/frame](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/uce/VisualCache.cpp#L270-L341)
and its [documented binary32 closeness rule](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/common/shared/real.h#L1410-L1458).
Numerical original-SDK observations remain deferred; source research is not
executed pixel/rounding qualification. Nonfinite, negative-bound, integer
overflow and unsupported renderer-budget results reject before publication.
Finite collapsed float bounds and zero realized extent produce an absent
texture, not invalid ownership. Negative selected scale retains the existing
source clamp-to-zero policy.

## Ownership and sampling

Complete cache-specific ownership preflight precedes empty realization. Null,
known empty, scale zero and float collapse produce an owned transparent 1x1
picture; none becomes a fabricated source handle or positive source bound.
Missing/declared/uninitialized/deleted sources remain failures. Only the exact
selected shader cache brush ignores brush paint properties; nested ordinary
brush consumers retain their prior contracts and generic empty rejection.

The selected explicit/target/default cache, source graph and raster policy
revision define retained content. Root content is captured directly; its outer
properties, including ScrollableAreaClip, are not applied. Descendants retain
ordinary scopes. Generic cache/ordinary brush behavior is unchanged.

The shader's implicit input still has its exact source capture dimensions.
The independently validated owned sampler picture carries its own dimensions,
which define normalized-UV clamp sampling. A separate internal uniform keeps
sampler extent from changing output UVs, derivative constants or source frames.
No public shader wire layout, renderer default, extra readback or CPU raster is
introduced. Both providers consume the same shader and native execution path.

All controls are authored, not executed. Original captures, native/managed
device and package gates, both providers and real source hosts remain required.
