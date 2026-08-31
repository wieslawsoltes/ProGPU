# Xcode Instruments compact summary

Generated: 2026-08-31T22:08:28.2546790+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 149 | 10,399,580,160 B | 69,795,840 B | 69,795,840 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 417; completions: 548.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 88,533,920 B | 1,229,502,160 B | 1,140,878,928 B |
| Heap allocations | 23,260,064 B | — | — |
| Anonymous VM | 65,273,856 B | — | — |
| All VM regions | 885,555,200 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 546,062,336 B | 5 | 1,603,239,936 B |
| VM: Mapped File | 232,275,968 B | 90 | 249,741,312 B |
| VM: IOAccelerator | 56,328,192 B | 672 | 56,688,640 B |
| VM: MALLOC_SMALL | 41,943,040 B | 10 | 41,943,040 B |
| Malloc 64,00 KiB | 12,124,160 B | 185 | 16,777,216 B |
| VM: Stack | 8,683,520 B | 6 | 17,367,040 B |
| MTLResourceList | 3,096,576 B | 63 | 3,096,576 B |
| Malloc 800,00 KiB | 819,200 B | 1 | 819,200 B |
| Malloc 48 Bytes | 659,376 B | 13,737 | 5,966,688 B |
| Malloc 2,50 KiB | 401,920 B | 157 | 7,434,240 B |
| Malloc 64 Bytes | 354,304 B | 5,536 | 2,121,152 B |
| Malloc 16,00 KiB | 327,680 B | 20 | 120,143,872 B |
| Malloc 320,00 KiB | 327,680 B | 1 | 1,966,080 B |
| Malloc 48,00 KiB | 294,912 B | 6 | 353,943,552 B |
| Malloc 272,00 KiB | 278,528 B | 1 | 10,584,064 B |
| Malloc 32 Bytes | 271,584 B | 8,487 | 4,031,392 B |
| Malloc 5,00 KiB | 271,360 B | 53 | 13,020,160 B |
| Malloc 128,00 KiB | 262,144 B | 2 | 8,650,752 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 8,00 KiB | 212,992 B | 26 | 60,055,552 B |
| IOGPUMetalPooledResource | 206,208 B | 537 | 206,208 B |
| Malloc 96,00 KiB | 196,608 B | 2 | 6,586,368 B |
| Malloc 3,50 KiB | 182,784 B | 51 | 530,432 B |
| Malloc 20,00 KiB | 163,840 B | 8 | 49,418,240 B |

## Metal resource allocations

Observed 74 resources totaling 9,699,328 B across the capture. 2 resources totaling 262,144 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |
| wgpu-native | Buffer | 53 | 6,946,816 B | 2 | 262,144 B |
| other | Buffer | 21 | 2,752,512 B | 0 | 0 B |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| other | Buffer | 131,072 B | no | 0x10d483beb |
| wgpu-native | Buffer | 131,072 B | no | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
