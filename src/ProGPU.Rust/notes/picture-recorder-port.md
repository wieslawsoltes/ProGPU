# Original picture recorder ownership

Implementation only; validation remains deferred until the complete port is implemented.

`PictureRecorder` owns one actual CPU Canvas. Nested begin and inactive end reject; a mutable recording borrow cannot outlive or race its recorder. Completion closes original scopes and layers before publishing an immutable `RecordedPicture`. A completion error retains the actual active recorder owner. Reuse starts an independent generation. Cancellation/drop release actual CPU command/resource owners without executing callbacks, flushing, polling or constructing a GPU context.

`RecordedPicture` retains original independent cull bounds and a wrapping nonzero identity. Empty command recordings retain the original zero cull rectangle; the cull origin never translates recorded commands. Playback records the actual shared Canvas picture. Its drawable adapter retains that same generation and snapshots immutable commands directly, without rerunning source callbacks. Existing finite/nonnegative Canvas extent admission remains explicit; malformed original recorder extents are not qualified.

Operation diagnostics preserve the original nested-picture replacement and i32 saturation policies. Rust command and inline array storage use actual Rust sizes; no C# struct layout or allocator-overhead equivalence is claimed. Shared nested occurrences are counted independently, and geometry/font/GPU caches remain outside this approximate diagnostic. Iterative traversal avoids recursive stack growth. These diagnostics are neither GPU draw counts nor live allocation instrumentation.

Original source: `src/SkiaSharp/SKPicture.cs`, SHA-256 `10f7fe2fd1682989e8baff48afe1c911c5b8f166b9e754772a210269e1d1c26c`. The source's unused R-tree hint introduces no alternate execution policy. The distinct versioned picture archive, complete picture-shader source metadata, exact source/runtime lifetime behavior, package, browser, pixels and performance qualification remain separate work.
