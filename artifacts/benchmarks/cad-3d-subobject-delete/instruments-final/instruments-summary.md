# Xcode Instruments compact summary

Generated: 2026-09-01T05:07:24.4024490+00:00

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
| Heap and anonymous VM | 18,267,072 B | 2,046,108,880 B | 2,027,841,808 B |
| Heap allocations | 7,125,952 B | — | — |
| Anonymous VM | 11,141,120 B | — | — |
| All VM regions | 1,025,409,024 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 883,343,360 B | 32 | 9,150,873,600 B |
| VM: Mapped File | 97,370,112 B | 93 | 120,913,920 B |
| VM: MALLOC_SMALL | 33,554,432 B | 8 | 33,554,432 B |
| VM: Stack | 10,878,976 B | 8 | 25,001,984 B |
| Malloc 64,00 KiB | 4,587,520 B | 70 | 33,095,680 B |
| Malloc 32 Bytes | 296,896 B | 9,278 | 695,040 B |
| Malloc 64 Bytes | 288,448 B | 4,507 | 1,449,344 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 196,608 B | 4 | 1,671,168 B |
| Malloc 176,00 KiB | 180,224 B | 1 | 180,224 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 128,00 KiB | 131,072 B | 1 | 786,432 B |
| Malloc 1,50 KiB | 96,768 B | 63 | 1,930,752 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 933,888 B |
| Malloc 80,00 KiB | 81,920 B | 1 | 655,360 B |
| Malloc 24,00 KiB | 73,728 B | 3 | 368,640 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 659,456 B |
| Malloc 224 Bytes | 70,336 B | 314 | 345,184 B |
| Malloc 3,00 KiB | 67,584 B | 22 | 3,809,280 B |
| Malloc 28,00 KiB | 57,344 B | 2 | 1,433,600 B |
| Malloc 192 Bytes | 51,456 B | 268 | 1,223,232 B |
| Malloc 8,00 KiB | 49,152 B | 6 | 262,144 B |
| Malloc 48 Bytes | 44,736 B | 932 | 327,792 B |
| Malloc 160 Bytes | 37,600 B | 235 | 1,565,120 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
