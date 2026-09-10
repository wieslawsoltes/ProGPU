# System.Drawing Graphics Image-Overload Contract

Date: 2026-08-26

## Scope and sources

This slice completes the point, unscaled/clipped, point-anchored source rectangle, and float-source destination rectangle members of the official `Graphics.DrawImage` family. The public contract is defined by the pinned .NET 10.0.11 reference assembly and [Graphics.DrawImage](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawimage?view=windowsdesktop-10.0) / [Graphics.DrawImageUnscaled](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawimageunscaled?view=windowsdesktop-10.0) documentation.

The destination-point-array overloads were intentionally outside this slice because three points require affine source-to-parallelogram mapping and four points require perspective mapping. That follow-up is now implemented through a typed retained texture mapping rather than an axis-aligned bounding rectangle; see [`system-drawing-destination-point-image-contract.md`](system-drawing-destination-point-image-contract.md).

## Typed behavior

Point and integer-coordinate overloads retain the image's pixel dimensions. The four-integer `DrawImageUnscaled` overload follows the unscaled contract and ignores its width/height compatibility parameters. `DrawImageUnscaledAndClipped` intersects source dimensions with the requested destination extent and draws the matching source rectangle at the same size, avoiding accidental stretch.

Point-anchored source-rectangle overloads convert the declared source unit to pixels and use the converted size as the destination extent. Float-source destination-rectangle overloads share the existing typed texture recording path, including sampling mode, retained texture lifetime, remap tables, color matrices, and current graphics transform. Abort callbacks run before resource retention or command recording; a `true` result records nothing.

Only `Bitmap` has a portable retained texture implementation today. Null images fail at the public boundary. This slice does not introduce an HDC, GDI+, screen-capture dependency, runtime reflection, or a platform bitmap wrapper.

## Gates and evidence

Five focused production tests cover:

- `Point` and integer-coordinate placement;
- unscaled size preservation even when compatibility width/height arguments differ;
- clipped source/destination pixels without scaling;
- floating-point and integer point-anchored source cropping;
- remap attributes plus false/true callback paths; and
- null validation before recording.

The complete drawing suite passes 183/183. ApiCompat removes eleven exact member suppressions, reducing measured debt from 48 missing types, 303 missing members, 47 other diagnostics, and 398 total to 48 missing types, 292 missing members, 47 other diagnostics, and 387 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 lifecycle tests.

## Overlapping bitmap self-draw follow-up

Drawing a bitmap through a `Graphics` recorder owned by that same bitmap now uses an explicit retained snapshot instead of throwing at the deferred-GPU boundary or sampling the active render target. The recorder temporarily balances its clip and compositing stacks, clones the bitmap after flushing all earlier commands, restores the current state, transfers the clone's texture lease into the retained command context, and then records the requested draw. This makes overlapping copies sequential and deterministic while keeping the temporary bitmap alive only through the typed texture lease.

The focused regression performs two overlapping copies in opposite directions under `CompositingMode.SourceCopy` and nearest-neighbor sampling. Its final pixels prove that the second snapshot observes the first draw and that source-copy state survives both intermediate flushes. The complete drawing suite passes 393/393, and ApiCompat remains at 0 missing types, 0 missing members, and 13 reviewed platform-annotation differences. `ImageConvenienceBenchmarks.DrawImageOverlappingSelfSnapshot` exposes the unavoidable flush/readback/snapshot cost as a dedicated performance workload rather than hiding it in an ordinary zero-copy texture-recording benchmark. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 703.071 microsecond mean, 851.876 microsecond median, 323.506 microsecond standard deviation, and 17.28 KB allocated for a 64x64 overlapping snapshot. One launch and three measured iterations make this a coarse, high-variance subsystem checkpoint rather than a universal latency claim.
