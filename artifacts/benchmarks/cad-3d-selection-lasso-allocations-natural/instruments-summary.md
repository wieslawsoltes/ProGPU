# Xcode Instruments compact summary

Generated: 2026-09-01T02:03:06.3642100+00:00

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

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 20,850,448 B | 71,332,080 B | 50,481,632 B |
| Heap allocations | 9,709,328 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 410,173,440 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 280,772,608 B | 11 | 5,681,053,696 B |
| VM: Mapped File | 93,093,888 B | 83 | 116,572,160 B |
| VM: MALLOC_SMALL | 25,165,824 B | 6 | 25,165,824 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 6,946,816 B | 106 | 7,471,104 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 393,216 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 1,572,864 B |
| Malloc 32 Bytes | 233,856 B | 7,308 | 534,528 B |
| Malloc 224,00 KiB | 229,376 B | 1 | 229,376 B |
| Malloc 192,00 KiB | 196,608 B | 1 | 196,608 B |
| Malloc 64 Bytes | 186,688 B | 2,917 | 743,104 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 1,50 KiB | 86,016 B | 56 | 775,680 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 917,504 B |
| Malloc 24,00 KiB | 73,728 B | 3 | 368,640 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 630,784 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,757,056 B |
| Malloc 224 Bytes | 67,424 B | 301 | 248,640 B |
| Malloc 28,00 KiB | 57,344 B | 2 | 1,462,272 B |
| Malloc 192 Bytes | 48,192 B | 251 | 841,536 B |
| Malloc 5,00 KiB | 40,960 B | 8 | 517,120 B |
| Malloc 7,00 KiB | 35,840 B | 5 | 114,688 B |
| Malloc 48 Bytes | 34,368 B | 716 | 255,744 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
