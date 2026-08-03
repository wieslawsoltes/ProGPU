# Xcode Instruments compact summary

Generated: 2026-08-03T08:46:44.3355330+00:00

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
The 92,274,688 B `VM: Dispatch continuations` row is a per-process libdispatch virtual-address reservation, not that many resident bytes. Use the paired `vmmap` resident and dirty columns before attributing it to physical footprint.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 116,181,792 B | 327,050,480 B | 210,868,688 B |
| Heap allocations | 11,619,104 B | — | — |
| Anonymous VM | 104,562,688 B | — | — |
| All VM regions | 1,612,988,416 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 1,284,882,432 B | 9 | 2,346,827,776 B |
| VM: Mapped File | 198,377,472 B | 99 | 216,301,568 B |
| VM: Dispatch continuations | 92,274,688 B | 1 | 92,274,688 B |
| VM: MALLOC_SMALL | 25,165,824 B | 6 | 25,165,824 B |
| VM: Stack | 10,878,976 B | 8 | 21,757,952 B |
| Malloc 64,00 KiB | 7,733,248 B | 118 | 9,961,472 B |
| VM: IOAccelerator | 1,146,880 B | 33 | 1,146,880 B |
| Malloc 48 Bytes | 476,544 B | 9,928 | 715,056 B |
| Malloc 416,00 KiB | 425,984 B | 1 | 4,259,840 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 4,587,520 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 48,00 KiB | 245,760 B | 5 | 2,998,272 B |
| Malloc 224,00 KiB | 229,376 B | 1 | 7,569,408 B |
| Malloc 192,00 KiB | 196,608 B | 1 | 6,488,064 B |
| Malloc 64 Bytes | 196,288 B | 3,067 | 405,696 B |
| Malloc 32 Bytes | 182,496 B | 5,703 | 436,864 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 5,013,504 B |
| Malloc 1,50 KiB | 122,880 B | 80 | 529,920 B |
| Malloc 2,50 KiB | 120,320 B | 47 | 519,680 B |
| Malloc 16,00 KiB | 98,304 B | 6 | 851,968 B |
| Malloc 5,00 KiB | 66,560 B | 13 | 348,160 B |
| Malloc 192 Bytes | 52,608 B | 274 | 675,072 B |
| Malloc 10,00 KiB | 51,200 B | 5 | 10,137,600 B |
| Malloc 7,00 KiB | 50,176 B | 7 | 100,352 B |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
