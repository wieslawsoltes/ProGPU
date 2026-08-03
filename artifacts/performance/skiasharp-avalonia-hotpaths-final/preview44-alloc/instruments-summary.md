# Xcode Instruments compact summary

Generated: 2026-08-03T08:46:26.2297070+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 0.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.
The 92,274,688 B `VM: Dispatch continuations` row is a per-process libdispatch virtual-address reservation, not that many resident bytes. Use the paired `vmmap` resident and dirty columns before attributing it to physical footprint.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 108,811,344 B | 2,441,702,816 B | 2,332,891,472 B |
| Heap allocations | 4,248,656 B | — | — |
| Anonymous VM | 104,562,688 B | — | — |
| All VM regions | 2,480,308,224 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 2,152,251,392 B | 25 | 14,380,679,168 B |
| VM: Mapped File | 198,328,320 B | 98 | 216,252,416 B |
| VM: Dispatch continuations | 92,274,688 B | 1 | 92,274,688 B |
| VM: MALLOC_SMALL | 25,165,824 B | 6 | 25,165,824 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| VM: IOAccelerator | 1,146,880 B | 33 | 1,146,880 B |
| Malloc 64,00 KiB | 851,968 B | 13 | 10,092,544 B |
| Malloc 48 Bytes | 483,552 B | 10,074 | 739,920 B |
| Malloc 416,00 KiB | 425,984 B | 1 | 4,259,840 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 3,096,576 B |
| Malloc 64 Bytes | 218,176 B | 3,409 | 559,232 B |
| Malloc 32 Bytes | 200,544 B | 6,267 | 490,464 B |
| Malloc 160,00 KiB | 163,840 B | 1 | 5,406,720 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 5,013,504 B |
| Malloc 128,00 KiB | 131,072 B | 1 | 4,587,520 B |
| Malloc 1,50 KiB | 121,344 B | 79 | 614,400 B |
| Malloc 2,50 KiB | 120,320 B | 47 | 652,800 B |
| Malloc 16,00 KiB | 98,304 B | 6 | 1,392,640 B |
| Malloc 5,00 KiB | 71,680 B | 14 | 358,400 B |
| Malloc 192 Bytes | 51,648 B | 269 | 748,224 B |
| Malloc 10,00 KiB | 51,200 B | 5 | 10,772,480 B |
| MTLResourceList | 49,152 B | 1 | 49,152 B |
| Malloc 8,00 KiB | 49,152 B | 6 | 303,104 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
