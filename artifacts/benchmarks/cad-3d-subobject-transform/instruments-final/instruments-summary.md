# Xcode Instruments compact summary

Generated: 2026-09-01T04:35:11.1238700+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 13,806.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 14,034,240 B | 1,617,079,344 B | 1,603,045,104 B |
| Heap allocations | 2,893,120 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 739,033,088 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 597,000,192 B | 20 | 8,944,959,488 B |
| VM: Mapped File | 97,337,344 B | 93 | 120,881,152 B |
| VM: MALLOC_SMALL | 33,554,432 B | 8 | 33,554,432 B |
| VM: Stack | 10,878,976 B | 8 | 25,001,984 B |
| Malloc 64,00 KiB | 458,752 B | 7 | 23,658,496 B |
| Malloc 32 Bytes | 287,872 B | 8,996 | 670,496 B |
| Malloc 64 Bytes | 277,824 B | 4,341 | 1,360,256 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 1,572,864 B |
| Malloc 160,00 KiB | 163,840 B | 1 | 163,840 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 128,00 KiB | 131,072 B | 1 | 786,432 B |
| Malloc 1,50 KiB | 98,304 B | 64 | 1,832,448 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 901,120 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 673,792 B |
| Malloc 224 Bytes | 70,784 B | 316 | 336,224 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,769,344 B |
| Malloc 28,00 KiB | 57,344 B | 2 | 1,433,600 B |
| Malloc 192 Bytes | 51,456 B | 268 | 1,167,936 B |
| Malloc 24,00 KiB | 49,152 B | 2 | 368,640 B |
| Malloc 48 Bytes | 42,816 B | 892 | 316,080 B |
| Malloc 8,00 KiB | 40,960 B | 5 | 237,568 B |
| Malloc 320 Bytes | 36,800 B | 115 | 679,040 B |
| Malloc 5,00 KiB | 35,840 B | 7 | 552,960 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
