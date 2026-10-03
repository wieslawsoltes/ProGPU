# Portable draw resource query ownership

The portable recorder distinguishes an absent optional brush/bitmap family from
a failed capability query. Only `E_NOINTERFACE` with a null output permits the
next family. A genuine failure is retained unchanged even if the query also
returns an owned pointer; local COM RAII releases that pointer. Any success
HRESULT without an interface returns `E_FAIL`. Successful nonzero HRESULTs with
an actual interface remain valid.

The private classifier connects `DrawBitmap`, bitmap-brush image sources,
rectangle/rounded/path/stroke bitmap-brush probes, layer opacity brushes, and
`FillOpacityMask`'s source and paint alternatives. Linear/radial/solid probes in
both ordinary painting and layer-mask translation use the same decision. A
terminal absent unsupported family retains its previous error (`E_NOTIMPL`, or
`E_NOINTERFACE` for the terminal opacity-mask source). The first drawing error,
current tags, source factory checks, resource snapshots and raster arithmetic
remain unchanged. A failed transaction still cannot export a scene.

This is defensive behavior for malformed portable resource providers, not a
claim about Microsoft Direct2D accepting a broken COM implementation. Existing
shared-bitmap and WIC controls are preserved without changing their public error
contracts. The correction does not broaden copy, text, mesh, stroke-style or
foreign-factory resource admission.

## Authored controls (not executed)

`progpu_native_direct2d_draw_query_fixture.hpp` drives the actual portable
recorder, returning real owned resource interfaces under deliberately altered
HRESULT/null responses. It never reproduces a private IID or vtable.

- Eight fault responses through four brush families and six public paint routes;
  later family responses would succeed if an erroneous fallback reached them.
- Six terminal all-absent brush routes; nine responses through direct bitmap,
  nested bitmap-brush and opacity-mask source paths; eight malformed first-stage
  opacity-mask scene alternatives.
- Exact original HRESULT, one-query/fallback counts, owned reference balance,
  unchanged prefix draw count, retained first error/tags, failed export with
  untouched destination bytes, and recovery to the full original scene bytes
  after normal transaction-generation advancement.
- Valid `S_OK`/`S_FALSE` owned results and genuine absence select all four brush
  families across all six routes, plus all three bitmap-source routes. Complete
  retained streams match the original genuine resource, not only summary counts.

Both native providers compile this shared portable recorder and compatibility
fixture. No provider selection, exported API/ABI, managed contract, shader,
pipeline or GPU work changed. The classifier is constant-time/allocation-free;
it adds no interface queries or resource callbacks. Hosted compilation, actual
fixture execution, GPU and application qualification remain deferred.
