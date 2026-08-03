# Xcode Instruments compact summary

Generated: 2026-08-03T16:35:41.4188170+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 16 | 1,003,749,376 B | 62,734,336 B | 62,734,336 B |
| Drawable waits | 38 | 575.242 ms | 17.236 ms | 13.533 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 40; completions: 370.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.
The 92,274,688 B `VM: Dispatch continuations` row is a per-process libdispatch virtual-address reservation, not that many resident bytes. Use the paired `vmmap` resident and dirty columns before attributing it to physical footprint.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 205,265,120 B | 768,632,304 B | 563,367,184 B |
| Heap allocations | 37,591,264 B | — | — |
| Anonymous VM | 167,673,856 B | — | — |
| All VM regions | 1,225,687,040 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 546,111,488 B | 6 | 4,488,953,856 B |
| VM: Mapped File | 440,434,688 B | 134 | 471,449,600 B |
| VM: Dispatch continuations | 92,274,688 B | 1 | 92,274,688 B |
| VM: MALLOC_SMALL | 71,303,168 B | 17 | 71,303,168 B |
| VM: IOSurface | 39,321,600 B | 3 | 39,321,600 B |
| Malloc 64,00 KiB | 13,959,168 B | 213 | 19,922,944 B |
| VM: CoreServices | 13,598,720 B | 1 | 27,197,440 B |
| VM: Stack | 11,501,568 B | 10 | 25,296,896 B |
| VM: IOAccelerator | 6,520,832 B | 64 | 6,520,832 B |
| Malloc 1,00 MiB | 4,194,304 B | 4 | 4,194,304 B |
| Malloc 96,00 KiB | 1,867,776 B | 19 | 8,159,232 B |
| VM: CoreAnimation | 1,359,872 B | 37 | 2,031,616 B |
| Malloc 320,00 KiB | 1,310,720 B | 4 | 15,728,640 B |
| VM: CoreUI image data | 1,277,952 B | 9 | 1,277,952 B |
| Malloc 48 Bytes | 1,123,056 B | 23,397 | 4,813,728 B |
| VM: ImageIO_PNG_Data | 1,064,960 B | 1 | 1,064,960 B |
| Malloc 992,00 KiB | 1,015,808 B | 1 | 1,015,808 B |
| Malloc 32 Bytes | 962,176 B | 30,068 | 3,099,328 B |
| Malloc 304,00 KiB | 933,888 B | 3 | 20,856,832 B |
| Malloc 64 Bytes | 748,352 B | 11,693 | 3,221,120 B |
| Malloc 128,00 KiB | 524,288 B | 4 | 13,500,416 B |
| Malloc 464,00 KiB | 475,136 B | 1 | 12,353,536 B |
| Malloc 448,00 KiB | 458,752 B | 1 | 15,138,816 B |
| MTLResourceList | 393,216 B | 8 | 393,216 B |

### Largest attributed live native/VM groups

The opt-in allocation list attributed 95,485 live rows totaling 205,265,120 B.

| Category | Caller | Library | Live count | Live bytes | First | Last |
| --- | --- | --- | ---: | ---: | --- | --- |
| VM: Dispatch continuations | 0x107ccb26c | libSkiaSharp.dylib | 1 | 92,274,688 B | 00:01.808.026 | 00:01.808.026 |
| VM: IOSurface | CA::SurfaceUtil::CAIOSurfaceCreate(unsigned int, unsigned int, unsigned int, unsigned int, unsigned int, unsigned int, unsigned long long, CA::SurfaceUtil::SurfaceAlignment, __CFString const*) | QuartzCore | 3 | 39,321,600 B | 00:03.456.056 | 00:03.860.062 |
| VM: CoreServices | CSStore2::VM::Allocate(unsigned int) | CoreServicesStore | 1 | 13,598,720 B | 00:02.455.004 | 00:02.455.004 |
| Malloc 64,00 KiB | ArenaAllocator::allocateNewPage(unsigned long) | libclrjit.dylib | 203 | 13,303,808 B | 00:01.623.686 | 00:02.269.900 |
| VM: Stack | CorUnix::InternalCreateThread(CorUnix::CPalThread*, _SECURITY_ATTRIBUTES*, unsigned int, unsigned int (*)(void*), void*, unsigned int, CorUnix::PalThreadType, unsigned long*, void**) | libcoreclr.dylib | 6 | 9,207,808 B | 00:01.600.163 | 00:02.006.560 |
| VM: IOAccelerator | -[IOGPUMetalResource initWithDevice:remoteStorageResource:options:args:argsSize:] | IOGPU | 54 | 6,356,992 B | 00:01.994.744 | 00:08.869.162 |
| Malloc 1,00 MiB | 0x107b2fde4 | libSkiaSharp.dylib | 4 | 4,194,304 B | 00:02.168.695 | 00:02.500.369 |
| Malloc 96,00 KiB | 0x107bafcb0 | libSkiaSharp.dylib | 18 | 1,769,472 B | 00:02.335.027 | 00:02.353.762 |
| VM: CoreUI image data | -[_CSIRenditionBlockData _allocateImageBytes] | CoreUI | 9 | 1,277,952 B | 00:03.877.769 | 00:03.983.458 |
| VM: CoreAnimation | CA::Render::Shmem::new_shmem(unsigned long) | QuartzCore | 22 | 1,114,112 B | 00:03.452.168 | 00:03.989.542 |
| VM: ImageIO_PNG_Data | _ImageIO_Malloc | ImageIO | 1 | 1,064,960 B | 00:04.036.659 | 00:04.036.659 |
| Malloc 992,00 KiB | 0x107b2fde4 | libSkiaSharp.dylib | 1 | 1,015,808 B | 00:02.503.488 | 00:02.503.488 |
| Malloc 320,00 KiB | 0x107b054d8 | libSkiaSharp.dylib | 3 | 983,040 B | 00:02.335.694 | 00:02.352.693 |
| Malloc 304,00 KiB | 0x107b054d8 | libSkiaSharp.dylib | 3 | 933,888 B | 00:02.347.556 | 00:02.353.848 |
| VM: Stack | SEHInitializeMachExceptions | libcoreclr.dylib | 1 | 573,440 B | 00:01.599.535 | 00:01.599.535 |
| VM: Stack | InitializeSignalHandlingCore | libSystem.Native.dylib | 1 | 573,440 B | 00:01.686.610 | 00:01.686.610 |
| VM: Stack | +[NSEvent(NSConcurrentEvents) _startConcurrentEventProcessing] | AppKit | 1 | 573,440 B | 00:01.930.315 | 00:01.930.315 |
| VM: Stack | CVDisplayLink::start() | CoreVideo | 1 | 573,440 B | 00:03.430.886 | 00:03.430.886 |
| Malloc 32 Bytes | CallCountingManager::SetCodeEntryPoint(NativeCodeVersion, unsigned long, bool, bool*) | libcoreclr.dylib | 16,910 | 541,120 B | 00:03.056.869 | 00:10.895.947 |
| Malloc 464,00 KiB | fscache_insert_and_retain | libCoreFSCache.dylib | 1 | 475,136 B | 00:03.764.551 | 00:03.764.551 |
| Malloc 48 Bytes | operator_new_impl[abi:nqe210106](unsigned long, std::__type_descriptor_t) | libc++abi.dylib | 9,663 | 463,824 B | 00:01.584.182 | 00:04.108.471 |
| Malloc 448,00 KiB | SLSCopyRegisteredCursorImages | SkyLight | 1 | 458,752 B | 00:01.977.989 | 00:01.977.989 |
| Malloc 48 Bytes | fscache_open_worker | libCoreFSCache.dylib | 8,194 | 393,312 B | 00:03.486.020 | 00:03.523.954 |
| MTLResourceList | MTLResourceListPoolCreateResourceList | Metal | 8 | 393,216 B | 00:02.041.146 | 00:08.854.019 |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
