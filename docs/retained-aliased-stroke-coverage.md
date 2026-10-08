# Retained aliased stroke coverage and selected input joins

Integrated execution exposed three independent managed coverage faults. Aliased
line bodies stopped their raster quad at the original flat endpoint, so a
four-sample target could resolve only three covered samples even when the
fragment selected full aliased coverage. The shared vector vertex shader now
pads both ends of the raster quad and transports the original longitudinal
interval to the fragment, which applies a half-open, center-sampled flat end.
The original centerline, width, brush coordinates and retained vertex ABI remain
unchanged. Antialiased line expansion is unchanged.

An aliased triangle fan also applied two different decisions to an internal
edge: its existing single-owner partition, followed by an additional zero-distance
test. Near an internal diagonal the designated owner could fail the second test
while its neighbor deliberately excluded that edge. Aliased and antialiased fans
now share the existing internal ownership decision; only exterior edges select
hard versus filtered coverage. No tolerance was added or increased.

Finally, managed `PathCacheKey` normalized a requested single sample to the
ordinary four-by-four grid. Aliased fill compilation now requests and retains
one sample, matching the existing native scene compiler. The sample count remains
part of exact retained tile identity. Ordinary four/eight-sample fills, transform
phase, atlas placement and the exact physical-pixel sampling gate are unchanged.

The shader source is canonical for managed rendering and both native providers.
Native compilation already selects single-sample aliased paths; no native wire
or provider-specific shader substitution is needed. These fixes preserve all
original full-frame cold/warm comparisons and add explicit single-sample target
controls alongside the existing four-sample target cases. Literal interior fan
and exterior polygon witnesses distinguish missing shared coverage from an
incorrectly rounded fill sample.

## Retained input

The newer explicit MiterOrBevel and clipped-Miter policies were present in paint
and the standalone stroke-query helper, but the managed retained GPU input
builder still encoded a multi-segment PathStroke whose internal joins are round.
These selected policies now reuse the source-join builder: flat internal bodies,
the actual typed join triangles, original caps, gaps, closed seams and rollback.
Normal widths retain the painter's conformal or affine arithmetic. Fixed and
hairline widths transform original centerlines/tangents before expansion and
reuse the existing physical body/arc compiler; they do not inverse-scale a width.
WPF's separate device-width rejection and legacy generic-join behavior remain.

Native semantic input already consumes the emitted stroke/join geometry and is
not changed by this managed input repair. The native provider, package and source
application gates remain necessary; passing the managed queries does not replace
them.

## Integration evidence, 2026-10-08

The macOS arm64 managed run passed 271 selected stroke, cap, transform, atlas,
raster-pipeline and shader-resource tests, with no failures or skips. The original
six clipped-miter and six miter-or-bevel full-frame/input cases and eight source
WPF reversal cases retain their assertions. Both native providers, SDK archives,
native tests and samples compiled with the explicit `--build-only` profile;
that compilation executed no native tests or renderer/package qualification.

The miter-or-bevel red-coverage witness now clears to black explicitly: a partial
hairline over the compositor's colored default clear cannot have zero green/blue.
Its stroke input, expected miter/bevel route, full-image comparison and hit points
are unchanged. Exact-head hosted CI, both provider/package comparisons and final
consumer/application qualification remain pending.
