# Xcode Instruments compact summary

Generated: 2026-08-03T08:48:58.9896220+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 58 | 51,806,208 B | 1,589,248 B | 1,589,248 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 3,220.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 0 B | 0 B | 0 B |
| Heap allocations | 0 B | — | — |
| Anonymous VM | 0 B | — | — |
| All VM regions | 0 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |

## Metal resource allocations

Observed 42 resources totaling 3,227,648 B across the capture. 42 resources totaling 3,227,648 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |
| metal-driver | Buffer | 14 | 1,835,008 B | 14 | 1,835,008 B |
| wgpu-native | Buffer | 7 | 737,280 B | 7 | 737,280 B |
| other | Buffer | 9 | 262,144 B | 9 | 262,144 B |
| other | Texture | 8 | 262,144 B | 8 | 262,144 B |
| wgpu-native | Texture | 4 | 131,072 B | 4 | 131,072 B |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
| wgpu-native | Buffer | 524,288 B | yes | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_buffer::h9af284e0431d6175 |
| wgpu-native | Buffer | 131,072 B | yes | wgpu_hal::metal::adapter::_$LT$impl$u20$wgpu_hal..Adapter$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Adapter$GT$::open::hc8c0e3de05863667 |
| metal-driver | Buffer | 131,072 B | yes | -[IOGPUMetalBuffer initWithPrimaryBuffer:heapIndex:bufferIndex:bufferOffset:length:args:argsSize:gpuTag:] |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<16u, 0u, true, 0u, 268435456u, AGX::G15::TextureHeapElem, AGX::G15::TextureHeapElem, unsigned long long>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<16u, 0u, true, 0u, 268435456u, AGX::G15::TextureHeapElem, AGX::G15::TextureHeapElem, unsigned long long>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<16u, 0u, true, 0u, 0u, AGX::G15::SamplerHeapElem>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<16u, 0u, true, 0u, 0u, AGX::G15::BVHStateHeapElem>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<16u, 0u, true, 0u, 0u, unsigned long long>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<16u, 0u, true, 8u, 0u, AGX::G15::TensorStateHeapElem>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<32u, 0u, true, 0u, 0u, unsigned long long>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | __36-[AGXG15XFamilyDevice setupDeferred]_block_invoke |
| metal-driver | Buffer | 131,072 B | yes | __36-[AGXG15XFamilyDevice setupDeferred]_block_invoke |
| metal-driver | Buffer | 131,072 B | yes | AGX::Mempool<32u, 0u, true, 0u, 0u, std::__1::array<unsigned long long, 8ul>>::grow(unsigned int, bool) |
| metal-driver | Buffer | 131,072 B | yes | __36-[AGXG15XFamilyDevice setupDeferred]_block_invoke |
| metal-driver | Buffer | 131,072 B | yes | invocation function for block in AGX::Device<AGX::G15::Encoders, AGX::G15::Classes, AGX::G15::ObjClasses>::createFastIntegerDivideBufferIfNeeded(AGXG15XFamilyDevice*) |
| metal-driver | Buffer | 131,072 B | yes | __36-[AGXG15XFamilyDevice setupDeferred]_block_invoke |
| other | Buffer | 131,072 B | yes | Unavailable |
| wgpu-native | Texture | 32,768 B | yes | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_texture::h55018f0b1128144a |
| wgpu-native | Texture | 32,768 B | yes | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_texture::h55018f0b1128144a |
| wgpu-native | Texture | 32,768 B | yes | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_texture::h55018f0b1128144a |
| wgpu-native | Texture | 32,768 B | yes | wgpu_hal::metal::device::_$LT$impl$u20$wgpu_hal..Device$LT$wgpu_hal..metal..Api$GT$$u20$for$u20$wgpu_hal..metal..Device$GT$::create_texture::h55018f0b1128144a |
| other | Texture | 32,768 B | yes | 0x109fd6047 |
| other | Texture | 32,768 B | yes | 0x109fd6047 |
| other | Texture | 32,768 B | yes | 0x109fd6047 |
