# Original ImageBrush shader-sampler property animation

The original MIL shader sampler now consumes retained `DoubleResource` opacity
and `RectResource` Viewport/Viewbox values through the existing owned bitmap
capture. This changes neither shader bytecode nor the sampler/capture wire.
Animated transforms and external/non-bitmap sampler sources remain unsupported.

The implementation is the original ProGPU tile replay at integration
`77b60ae78c416851858527bf355a947a8752ee25`: `capture_brush_rectangle` resolves
the current rectangles and opacity, while `append_cache_resource_revision`
includes all three animation handles. The new admission validates resource types
at shader binding and populated values before capture. Invalid opacity cannot
be hidden by an empty viewport. Failed scene construction publishes no scene;
normal channel transactions, resource deletion guards and retained picture
ownership remain unchanged. Work is O(1) additional validation per sampler with
no allocations, callbacks, shader variants or GPU crossings.

Source semantics follow the public [TileBrush.Viewport contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.tilebrush.viewport)
and [Brush.Opacity contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.brush.opacity).
Existing native finite/range admission is preserved, not widened to implement
additional source coercion. This is a connection to existing source property
resources, not a new animation clock, transform or rendering architecture.

Both native providers compile the same MIL implementation and replay the same
retained picture. The managed compositor receives current source brush values
through its existing typed brush adapter; it does not consume original MIL
`DoubleResource`/`RectResource` handles and needs no second interpreter.

Validation is deferred by user request. Authored packet/pixel controls are not
execution evidence, original Windows equivalence or application qualification.
The coverage ledger is mechanically regenerated, not verified or hand-edited.
