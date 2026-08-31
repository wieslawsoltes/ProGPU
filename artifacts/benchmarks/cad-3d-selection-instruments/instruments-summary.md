# Xcode Instruments compact summary

Generated: 2026-08-31T23:06:36.8793410+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 509.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 19,788,704 B | 60,382,960 B | 40,594,256 B |
| Heap allocations | 9,221,024 B | — | — |
| Anonymous VM | 10,567,680 B | — | — |
| All VM regions | 675,037,184 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 546,062,336 B | 5 | 1,603,239,936 B |
| VM: Mapped File | 89,047,040 B | 60 | 109,903,872 B |
| VM: MALLOC_SMALL | 29,360,128 B | 7 | 29,360,128 B |
| VM: Stack | 10,305,536 B | 7 | 20,611,072 B |
| Malloc 64,00 KiB | 7,143,424 B | 109 | 7,471,104 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 393,216 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 196,608 B | 4 | 1,327,104 B |
| Malloc 32 Bytes | 180,032 B | 5,626 | 398,848 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 64 Bytes | 131,328 B | 2,052 | 467,520 B |
| Malloc 24,00 KiB | 122,880 B | 5 | 319,488 B |
| Malloc 14,00 KiB | 71,680 B | 5 | 458,752 B |
| Malloc 16,00 KiB | 65,536 B | 4 | 704,512 B |
| Malloc 1,50 KiB | 64,512 B | 42 | 539,136 B |
| Malloc 224 Bytes | 63,840 B | 285 | 181,216 B |
| Malloc 3,00 KiB | 55,296 B | 18 | 3,640,320 B |
| Malloc 192 Bytes | 45,696 B | 238 | 471,168 B |
| Malloc 5,00 KiB | 35,840 B | 7 | 419,840 B |
| Malloc 7,00 KiB | 35,840 B | 5 | 78,848 B |
| Malloc 28,00 KiB | 28,672 B | 1 | 1,347,584 B |
| Malloc 16 Bytes | 25,648 B | 1,603 | 49,248 B |
| Malloc 128 Bytes | 25,344 B | 198 | 117,760 B |
| Malloc 48 Bytes | 24,768 B | 516 | 187,488 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
