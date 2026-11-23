# Original drawable recording

Implementation only; validation is deferred until the complete port is implemented.

`CanvasDrawable` retains the actual callback producer and its atomic wrapping generation. Drawing executes the original Save/Concat/OnDraw/Restore sequence synchronously during CPU recording. Errors and unwinding restore exactly one scope; callbacks do not execute during immutable picture preparation or GPU submission. Already returned pictures retain their independent command and resource generations.

Snapshot uses the producer's explicit override or records its actual Draw callback with the original independent bounds. The cull origin does not translate source commands, and an empty recording publishes zero bounds. The existing Rust canvas finite nonnegative extent admission remains explicit; it does not establish original malformed-extent compatibility. Approximate byte counts remain the producer's own estimate.

Original SHA-256 provenance:

| Source under `src/SkiaSharp/` | SHA-256 |
| --- | --- |
| `SKDrawable.cs` | `3ecac100917f2052a0891e979750acd54825e79bbba70418843856c91f761a58` |
| `SKPicture.cs` | `10f7fe2fd1682989e8baff48afe1c911c5b8f166b9e754772a210269e1d1c26c` |
| `SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |

Source callback/error/lifetime, native/browser pixels, package and performance qualification remain open.
