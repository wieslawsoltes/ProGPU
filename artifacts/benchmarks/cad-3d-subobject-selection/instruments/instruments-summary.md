# Xcode Instruments compact summary

Generated: 2026-09-01T02:54:41.1329650+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 2,507.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 20,752,800 B | 71,469,584 B | 50,716,784 B |
| Heap allocations | 9,611,680 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 412,434,432 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 282,984,448 B | 7 | 3,593,076,736 B |
| VM: Mapped File | 93,143,040 B | 83 | 116,621,312 B |
| VM: MALLOC_SMALL | 25,165,824 B | 6 | 25,165,824 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 7,077,888 B | 108 | 7,471,104 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 393,216 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 1,523,712 B |
| Malloc 32 Bytes | 241,952 B | 7,561 | 550,432 B |
| Malloc 64 Bytes | 195,840 B | 3,060 | 796,416 B |
| Malloc 176,00 KiB | 180,224 B | 1 | 180,224 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 24,00 KiB | 98,304 B | 4 | 393,216 B |
| Malloc 1,50 KiB | 86,016 B | 56 | 855,552 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 901,120 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 673,792 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,784,704 B |
| Malloc 224 Bytes | 66,976 B | 299 | 258,272 B |
| Malloc 192 Bytes | 48,192 B | 251 | 880,704 B |
| Malloc 5,00 KiB | 40,960 B | 8 | 517,120 B |
| Malloc 7,00 KiB | 35,840 B | 5 | 100,352 B |
| Malloc 48 Bytes | 35,184 B | 733 | 263,616 B |
| Malloc 160 Bytes | 32,000 B | 200 | 1,457,280 B |
| Malloc 16 Bytes | 31,472 B | 1,967 | 56,928 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
