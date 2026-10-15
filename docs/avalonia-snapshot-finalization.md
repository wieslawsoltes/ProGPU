# Avalonia image-brush snapshot ownership

The package integration stores snapshots of public Avalonia bitmaps in a
`ConditionalWeakTable`. Its cache entry previously finalized by calling ordinary
`ImmutableBitmap.Dispose`. That call takes the bitmap gate and then disposes the
texture through `RenderLock`. A renderer holding `RenderLock` while waiting for
finalizers therefore blocked this cleanup. The cache entry could also dispose a
snapshot still retained by its consumer once the original source key died.

An independent actual-device probe against `2fa033b1` reproduces the unchanged
five-second lock-held failure on both stock wgpu-native and Dawn. Both finish
after releasing the lock. The original encoded public `Bitmap`, resolver and
snapshot texture are used; the texture is independently retained so its own
finalizer cannot hide the cache-owner path. Original failed outputs are retained.

## Actual snapshot ownership

The cache entry is no longer finalizable. A private `SnapshotBitmap` subtype
owns finalization of the actual returned immutable snapshot. A retained snapshot
therefore stays live independently of its original public bitmap. Once the
snapshot itself is unreachable, its finalizer calls the existing atomic
`GpuTexture.DisposeFromFinalizer` transfer. It takes neither the bitmap gate nor
`RenderLock`; handle release remains in the original context retirement queues.
No finalizer order between the snapshot and its texture is assumed.

Ordinary explicit disposal still retires the texture on the caller and now
suppresses the subtype finalizer. Mutable-source replacement still explicitly
disposes the previous snapshot. Unused encoded snapshots do not create a texture
for cleanup. Other immutable bitmaps do not acquire a finalizer. The source
compositor continues to borrow the original typed platform bitmap and does not
use this snapshot subtype.

The finalizable object moves from the cache entry to its snapshot; no registry,
worker, extra native call, pixel copy or render pass is introduced. Transfer is
constant work per snapshot and retains existing queue and submission ownership.
This is a deadlock and lifetime correction, not a measured performance or
allocation improvement.

## Provenance and validation

The implementation uses original ProGPU's bitmap resolver, immutable bitmap and
atomic texture retirement at `2fa033b1`, plus the existing
[retained-image ownership research](retained-image-finalization.md). No external
implementation was copied. Both managed providers use this path. Native C++
renderers have no Avalonia CLR bitmap/cache finalizer and retain their original
submission-owned resources.

The repaired independent probe passes both original five-second checks. Eight
actual-device tests pass for each of Avalonia 11.3.20 and 12.1.1, with no skips.
The same suite against the original resolver/bitmap produces four failures and
four passes per version; both ownership failures and both lock-held failures
are reproduced, while explicit disposal and mutable replacement already pass.
They cover collection while holding the render lock, a snapshot retained after
source collection, explicit disposal exactly once, and writeable source
replacement, on both providers. CI runs both package versions on Windows, Linux
and macOS in the existing Avalonia native-Dawn job and retains their TRX files.
Whole CI and package/application qualification remain separate required gates.
This concrete reproduction does not identify the missing historical Linux
collector stack in [#371](https://github.com/wieslawsoltes/ProGPU/issues/371).
