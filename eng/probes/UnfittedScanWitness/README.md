# Original unfitted scan metadata replay

This independent diagnostic retains the previously qualified producer `6208f44c`
and the same original 64 descriptors, source font hashes and 384×64 RGBA frames.
It changes only scan metadata in copied diagnostic arrays. It does not change a
product API, font-loading default, shader, source geometry or expected pixel.

The original unmodified experiment failed seven descriptors (14 cold/warm frames,
42 pixels). Current owned-font Windows captures show that unfitted GDI+ output
still distinguishes SCANTYPE modes. Current native capture preserves authored
geometry but drops those instructions under explicit Unhinted loading.

The new program captures a separate Monochrome batch from the same owned source,
size, phase, interpreter and descriptor order. It rejects incomplete or changed
curve, contour and segment topology. Only scan-mode/dropout bits are transferred;
original coordinates, precision flags, positions and target remain intact. Both
raw captures are retained. The witness owners retire before the original source
retirement assertion, so they cannot mask a lifetime failure.
An independent reader checks the captured segment bytes before and after replay
preparation: every coordinate, kind, precision flag and contour-range bit outside
the declared scan mask must remain exact, including signed zero.

The fixed Windows 2025 x64 host executes both existing providers once. The
original 45-second/16-MiB per-provider limits, complete cold/warm frames, native
identity, submission, cache and private-font cleanup checks remain. No desktop
window or input action is created. All differing pixels remain failures.

The result is a source-policy experiment, not a fix for the original 72-case
application gate or qualification of the current producer. A successful result
would justify a separately reviewed explicit source contract; a failed result
must preserve the original and changed evidence without selecting another mode.

The first hosted replay at `490367550` completed and failed on both providers.
All original geometry and non-scan segment bytes remain exact. Metadata alone
resolves the three Microsoft Sans Serif descriptor failures, changing exactly
six pixels across their cold/warm frames per provider. The four Segoe UI
descriptors still fail on eight frames and 36 pixels per provider. All provider
and cold/warm pairs agree; both processes finish within the original bounds and
retire their private fonts. This partial attribution is not a product fix or
permission to change the remaining pixels or select another scan mode.
See [the retained result](../../../docs/binary-font-scan-metadata.md).

The existing Build workflow runs this within its Windows system-font lane. No
new workflow is introduced. The exact historical package artifact remains
hash-pinned so the experiment varies metadata alone.
This historical-artifact dependency must be retired or replaced with durable
current-package regression coverage after attribution, before merge and release.
