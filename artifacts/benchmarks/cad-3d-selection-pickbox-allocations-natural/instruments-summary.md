# Xcode Instruments compact summary

Generated: 2026-09-01T01:10:15.7015950+00:00

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
| Heap and anonymous VM | 19,140,992 B | 71,071,568 B | 51,930,576 B |
| Heap allocations | 7,999,872 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 390,234,112 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 260,849,664 B | 10 | 1,993,883,648 B |
| VM: Mapped File | 93,077,504 B | 83 | 116,555,776 B |
| VM: MALLOC_SMALL | 25,165,824 B | 6 | 25,165,824 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 5,373,952 B | 82 | 7,471,104 B |
| Malloc 128,00 KiB | 524,288 B | 4 | 524,288 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 1,523,712 B |
| Malloc 32 Bytes | 228,672 B | 7,146 | 525,344 B |
| Malloc 176,00 KiB | 180,224 B | 1 | 180,224 B |
| Malloc 64 Bytes | 179,520 B | 2,805 | 710,272 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 1,50 KiB | 86,016 B | 56 | 775,680 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 884,736 B |
| Malloc 24,00 KiB | 73,728 B | 3 | 368,640 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 645,120 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,750,912 B |
| Malloc 224 Bytes | 67,200 B | 300 | 246,624 B |
| Malloc 28,00 KiB | 57,344 B | 2 | 1,490,944 B |
| Malloc 192 Bytes | 48,192 B | 251 | 805,248 B |
| Malloc 5,00 KiB | 40,960 B | 8 | 506,880 B |
| Malloc 7,00 KiB | 35,840 B | 5 | 107,520 B |
| Malloc 48 Bytes | 33,264 B | 693 | 248,496 B |
| Malloc 16 Bytes | 29,936 B | 1,871 | 55,392 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
