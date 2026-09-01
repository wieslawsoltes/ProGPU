# Xcode Instruments compact summary

Generated: 2026-09-01T05:42:20.9876150+00:00

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
| Heap and anonymous VM | 20,651,072 B | 631,442,656 B | 610,791,584 B |
| Heap allocations | 9,509,952 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 538,083,328 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 404,324,352 B | 27 | 3,451,420,672 B |
| VM: Mapped File | 97,452,032 B | 94 | 120,995,840 B |
| VM: MALLOC_SMALL | 25,165,824 B | 6 | 25,165,824 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 6,815,744 B | 104 | 7,143,424 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 393,216 B |
| Malloc 32 Bytes | 296,384 B | 9,262 | 673,088 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 64 Bytes | 253,568 B | 3,962 | 1,193,728 B |
| Malloc 48,00 KiB | 196,608 B | 4 | 1,671,168 B |
| Malloc 160,00 KiB | 163,840 B | 1 | 163,840 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 1,50 KiB | 98,304 B | 64 | 1,635,840 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 1,048,576 B |
| Malloc 80,00 KiB | 81,920 B | 1 | 655,360 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 716,800 B |
| Malloc 224 Bytes | 70,336 B | 314 | 335,776 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,883,008 B |
| Malloc 28,00 KiB | 57,344 B | 2 | 1,605,632 B |
| Malloc 192 Bytes | 52,032 B | 271 | 1,128,384 B |
| Malloc 24,00 KiB | 49,152 B | 2 | 368,640 B |
| Malloc 5,00 KiB | 40,960 B | 8 | 527,360 B |
| Malloc 8,00 KiB | 40,960 B | 5 | 262,144 B |
| Malloc 48 Bytes | 39,168 B | 816 | 297,456 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
