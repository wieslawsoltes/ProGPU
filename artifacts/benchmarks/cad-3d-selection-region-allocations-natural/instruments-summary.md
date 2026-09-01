# Xcode Instruments compact summary

Generated: 2026-09-01T00:24:58.4286630+00:00

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
| Heap and anonymous VM | 20,751,776 B | 70,316,448 B | 49,564,672 B |
| Heap allocations | 9,610,656 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 416,645,120 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 278,872,064 B | 11 | 5,427,494,912 B |
| VM: Mapped File | 93,077,504 B | 83 | 116,555,776 B |
| VM: MALLOC_SMALL | 33,554,432 B | 8 | 33,554,432 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 7,143,424 B | 109 | 7,471,104 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 393,216 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 1,523,712 B |
| Malloc 32 Bytes | 228,544 B | 7,142 | 523,616 B |
| Malloc 64 Bytes | 175,360 B | 2,740 | 690,176 B |
| Malloc 160,00 KiB | 163,840 B | 1 | 163,840 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 24,00 KiB | 98,304 B | 4 | 393,216 B |
| Malloc 1,50 KiB | 86,016 B | 56 | 769,536 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 884,736 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 630,784 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,744,768 B |
| Malloc 224 Bytes | 67,200 B | 300 | 245,952 B |
| Malloc 192 Bytes | 48,192 B | 251 | 792,576 B |
| Malloc 5,00 KiB | 40,960 B | 8 | 506,880 B |
| Malloc 7,00 KiB | 35,840 B | 5 | 107,520 B |
| Malloc 48 Bytes | 32,784 B | 683 | 246,336 B |
| Malloc 16 Bytes | 29,424 B | 1,839 | 54,880 B |
| Malloc 28,00 KiB | 28,672 B | 1 | 1,433,600 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
