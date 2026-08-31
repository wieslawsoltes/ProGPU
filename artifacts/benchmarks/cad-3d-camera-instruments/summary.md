# ProGPU.CAD 3D camera Instruments qualification

Captured 2026-08-31 on macOS 26.6 with Instruments 16.0 against the exact
Release benchmark assembly whose SHA-256 is
`f938077bf8bd84aaa87c04331dccbafb61794e71750bdf9cc91a0436818de64f`.

The uninstrumented distribution is retained in
`../cad-3d-camera-updates.json`. Each of 48 measured batches performs 65,536
camera captures after six warmups. The one-entity p50/p95/p99 is
7.2793/9.7206/14.5672 ms; the 10,000-entity result is
5.0183/10.5257/13.8607 ms. The large/small p95 ratio is 1.082824. Both lanes
allocate zero managed bytes and report zero camera-only scene compilations,
entity visits, draw-batch visits, and upload bytes.

`time-profiler.trace` ran the same binary for 250 batches per scene and exited
zero after 5.418940 seconds. Its committed full exported table contains 3,408
samples; its exported TOC records the exact command, tool version, and target.
The raw trace remains in the local evidence directory and is intentionally not
committed. The committed XML redacts only absolute host paths and the host
device UUID; sample times, stacks, tool metadata, workload arguments, and exit
status are unchanged.

`allocations.trace` ran the same 250-batch workload and exited zero after
7.647928 seconds. The exported TOC retains both Allocations and VM Tracker
tracks, configured for all heap/VM types and freed events. Xcode did not expose
an allocation event schema through `xctrace export`, so no native-heap number
is inferred from that trace; the benchmark's exact current-thread managed
allocation counter remains the reported zero-allocation evidence. The raw
trace remains local and is intentionally not committed.

Metal System Trace is not applicable to this bounded CPU qualification. The
benchmark never initializes WebGPU and performs no resource creation, command
encoding, submission, presentation, or readback. This checkpoint therefore
makes no GPU frame-time, residency, or submission-count claim; those remain a
separate feature-matrix gate.
