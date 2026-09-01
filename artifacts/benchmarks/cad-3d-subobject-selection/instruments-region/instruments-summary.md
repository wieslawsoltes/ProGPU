# Xcode Instruments compact summary

Generated: 2026-09-01T03:33:11.3728370+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 8,649.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 22,079,024 B | 73,332,128 B | 51,253,104 B |
| Heap allocations | 10,937,904 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 961,413,120 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 819,347,456 B | 7 | 5,152,931,840 B |
| VM: Mapped File | 93,175,808 B | 83 | 116,736,000 B |
| VM: MALLOC_SMALL | 37,748,736 B | 9 | 37,748,736 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 8,126,464 B | 124 | 8,454,144 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 393,216 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 32 Bytes | 247,968 B | 7,749 | 560,192 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 1,622,016 B |
| Malloc 224,00 KiB | 229,376 B | 1 | 229,376 B |
| Malloc 64 Bytes | 204,096 B | 3,189 | 839,936 B |
| Malloc 192,00 KiB | 196,608 B | 1 | 196,608 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 1,50 KiB | 86,016 B | 56 | 861,696 B |
| Malloc 28,00 KiB | 86,016 B | 3 | 1,519,616 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 933,888 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 688,128 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,815,424 B |
| Malloc 224 Bytes | 67,424 B | 301 | 259,392 B |
| Malloc 24,00 KiB | 49,152 B | 2 | 393,216 B |
| Malloc 192 Bytes | 48,192 B | 251 | 909,888 B |
| Malloc 7,00 KiB | 43,008 B | 6 | 129,024 B |
| Malloc 5,00 KiB | 40,960 B | 8 | 527,360 B |
| Malloc 160 Bytes | 37,920 B | 237 | 1,469,600 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
