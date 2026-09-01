# Xcode Instruments compact summary

Generated: 2026-09-01T04:07:09.8990940+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 5,683.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 20,251,744 B | 628,644,544 B | 608,392,800 B |
| Heap allocations | 9,110,624 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 721,141,760 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 583,319,552 B | 15 | 4,489,396,224 B |
| VM: Mapped File | 97,320,960 B | 93 | 120,864,768 B |
| VM: MALLOC_SMALL | 29,360,128 B | 7 | 29,360,128 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 6,553,600 B | 100 | 12,517,376 B |
| Malloc 128,00 KiB | 524,288 B | 4 | 655,360 B |
| Malloc 32 Bytes | 271,584 B | 8,487 | 617,792 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 1,572,864 B |
| Malloc 64 Bytes | 234,944 B | 3,671 | 980,160 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 1,50 KiB | 98,304 B | 64 | 1,339,392 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 884,736 B |
| Malloc 24,00 KiB | 73,728 B | 3 | 393,216 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 616,448 B |
| Malloc 224 Bytes | 70,336 B | 314 | 310,688 B |
| Malloc 3,00 KiB | 64,512 B | 21 | 3,735,552 B |
| Malloc 192 Bytes | 51,456 B | 268 | 1,008,576 B |
| Malloc 5,00 KiB | 40,960 B | 8 | 512,000 B |
| Malloc 8,00 KiB | 40,960 B | 5 | 229,376 B |
| Malloc 48 Bytes | 36,240 B | 755 | 280,512 B |
| Malloc 160 Bytes | 30,240 B | 189 | 1,483,680 B |
| Malloc 16 Bytes | 28,720 B | 1,795 | 54,640 B |
| Malloc 28,00 KiB | 28,672 B | 1 | 1,404,928 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
