# System.Drawing Graphics Flush Contract

## Scope

This slice implements the official `System.Drawing.Drawing2D.FlushIntention` enum and both `System.Drawing.Graphics.Flush` overloads in ProGPU. It also adds a typed host callback used by retained UI frameworks to consume a command batch without exposing window-system objects to `System.Drawing.Common`.

The public shape is pinned by the .NET 10.0.11 Windows Desktop reference assembly and Microsoft ApiCompat. Observable behavior is cross-checked against the repository's canonical `System.Drawing.Common` tests. The ProGPU implementation is original retained-renderer code; it does not call GDI+, import an HDC, scan private fields, or manufacture a WinForms-shaped compatibility object.

## Behavioral contract

- `Flush()` is equivalent to `Flush(FlushIntention.Flush)`.
- Undefined enum values remain accepted, matching the public framework behavior; only the exact `Sync` value adds a completion wait.
- Bitmap-backed graphics submit their current balanced command batch to the bitmap and retain logical clip state for later drawing.
- A host-owned retained recorder invokes a typed `Action<FlushIntention>` synchronously. The callback must consume or clear the batch before returning.
- `FlushIntention.Sync` polls the explicit `WgpuContext` after the host callback has committed or presented the batch.
- A recorder created without a bitmap or host callback throws `InvalidOperationException` because it has no truthful submission target.
- A disposed graphics instance throws `ArgumentException`, matching the observable framework boundary.

## State and ownership

Deferred clip commands are stack state, so a flush first emits the matching pop, hands off a balanced batch, and then pushes the logical clip into the new batch. This prevents a host from receiving an unterminated clip and allows drawing to continue after any number of flushes. The completed callback remains exactly-once and independent of intermediate flushes.

The callback is deliberately narrow. Window dispatch, retained visual ownership, presentation scheduling, and local-OS integration stay in the framework host. ProGPU only defines the `System.Drawing` contract and the explicit GPU completion point.

## Gates

- ApiCompat: 49 missing types, 317 missing members, 47 other diagnostics, 413 total; no breaking changes or stale suppressions.
- Focused flush tests: 6/6.
- Complete `System.Drawing.Common.Tests`: 170/170.
- ARM64/.NET 10.0.11 ShortRun: 155.858 ns mean, 155.881 ns median, 2.573 ns standard deviation, 40 B allocated for one retained rectangle record+flush.
- The warmed allocation gate permits at most 64 B per record+flush.

## Remaining boundaries

`GetHdc`/`ReleaseHdc`, native GDI busy-state interaction, and local-OS device-context import remain separate explicit platform work. This slice does not claim those APIs or make raw recorder-only graphics submit anywhere implicitly.
