# Xcode Instruments compact summary

Generated: 2026-08-03T16:33:26.9750140+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 31 | 399.205 ms | 18.913 ms | 11.410 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 102; completions: 371.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.
The 92,274,688 B `VM: Dispatch continuations` row is a per-process libdispatch virtual-address reservation, not that many resident bytes. Use the paired `vmmap` resident and dirty columns before attributing it to physical footprint.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 198,790,144 B | 817,552,976 B | 618,743,312 B |
| Heap allocations | 42,011,648 B | — | — |
| Anonymous VM | 156,778,496 B | — | — |
| All VM regions | 1,227,554,816 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 546,144,256 B | 7 | 1,603,403,776 B |
| VM: Mapped File | 448,446,464 B | 161 | 489,095,168 B |
| VM: Dispatch continuations | 92,274,688 B | 1 | 92,274,688 B |
| VM: MALLOC_SMALL | 62,914,560 B | 15 | 62,914,560 B |
| VM: IOSurface | 26,214,400 B | 2 | 26,214,400 B |
| Malloc 64,00 KiB | 13,828,096 B | 211 | 18,939,904 B |
| VM: CoreServices | 13,598,720 B | 1 | 27,197,440 B |
| Malloc 12,50 MiB | 13,107,200 B | 1 | 13,107,200 B |
| VM: MALLOC_LARGE | 13,107,200 B | 1 | 13,107,200 B |
| VM: Stack | 11,452,416 B | 9 | 24,051,712 B |
| VM: IOAccelerator | 9,486,336 B | 155 | 9,797,632 B |
| VM: CoreAnimation | 2,097,152 B | 28 | 2,555,904 B |
| Malloc 32 Bytes | 1,178,912 B | 36,841 | 4,512,288 B |
| VM: CoreUI image data | 1,081,344 B | 8 | 1,081,344 B |
| Malloc 48 Bytes | 1,077,024 B | 22,438 | 4,345,008 B |
| Malloc 64 Bytes | 833,536 B | 13,024 | 4,183,808 B |
| MTLResourceList | 737,280 B | 15 | 737,280 B |
| Malloc 48,00 KiB | 589,824 B | 12 | 35,635,200 B |
| Malloc 464,00 KiB | 475,136 B | 1 | 11,403,264 B |
| Malloc 16,00 KiB | 425,984 B | 26 | 3,063,808 B |
| Malloc 128,00 KiB | 393,216 B | 3 | 12,845,056 B |
| Malloc 384,00 KiB | 393,216 B | 1 | 12,976,128 B |
| Malloc 3,50 KiB | 387,072 B | 108 | 1,609,216 B |
| Malloc 352,00 KiB | 360,448 B | 1 | 11,894,784 B |

### Largest attributed live native/VM groups

The opt-in allocation list attributed 99,614 live rows totaling 198,790,144 B.

| Category | Caller | Library | Live count | Live bytes | First | Last |
| --- | --- | --- | ---: | ---: | --- | --- |
| VM: Dispatch continuations | one-time initialization function for cache | Foundation | 1 | 92,274,688 B | 00:01.427.875 | 00:01.427.875 |
| VM: IOSurface | CA::SurfaceUtil::CAIOSurfaceCreate(unsigned int, unsigned int, unsigned int, unsigned int, unsigned int, unsigned int, unsigned long long, CA::SurfaceUtil::SurfaceAlignment, __CFString const*) | QuartzCore | 2 | 26,214,400 B | 00:03.592.795 | 00:04.369.973 |
| VM: CoreServices | CSStore2::VM::Allocate(unsigned int) | CoreServicesStore | 1 | 13,598,720 B | 00:01.618.133 | 00:01.618.133 |
| Malloc 64,00 KiB | ArenaAllocator::allocateNewPage(unsigned long) | libclrjit.dylib | 203 | 13,303,808 B | 00:01.264.295 | 00:02.027.472 |
| Malloc 12,50 MiB | CallDescrWorkerInternal | libcoreclr.dylib | 1 | 13,107,200 B | 00:03.555.986 | 00:03.555.986 |
| VM: Stack | CorUnix::InternalCreateThread(CorUnix::CPalThread*, _SECURITY_ATTRIBUTES*, unsigned int, unsigned int (*)(void*), void*, unsigned int, CorUnix::PalThreadType, unsigned long*, void**) | libcoreclr.dylib | 6 | 9,732,096 B | 00:01.229.308 | 00:03.176.412 |
| VM: IOAccelerator | -[IOGPUMetalResource initWithDevice:remoteStorageResource:options:args:argsSize:] | IOGPU | 129 | 9,060,352 B | 00:03.086.187 | 00:04.439.571 |
| VM: CoreAnimation | CA::Render::Shmem::new_shmem(unsigned long) | QuartzCore | 15 | 1,884,160 B | 00:03.218.411 | 00:04.391.562 |
| VM: CoreUI image data | -[_CSIRenditionBlockData _allocateImageBytes] | CoreUI | 8 | 1,081,344 B | 00:03.150.267 | 00:03.439.221 |
| MTLResourceList | MTLResourceListPoolCreateResourceList | Metal | 15 | 737,280 B | 00:03.224.489 | 00:06.837.804 |
| Malloc 32 Bytes | CallCountingManager::SetCodeEntryPoint(NativeCodeVersion, unsigned long, bool, bool*) | libcoreclr.dylib | 21,985 | 703,520 B | 00:01.657.182 | 00:11.433.619 |
| VM: Stack | SEHInitializeMachExceptions | libcoreclr.dylib | 1 | 573,440 B | 00:01.228.742 | 00:01.228.742 |
| VM: Stack | InitializeSignalHandlingCore | libSystem.Native.dylib | 1 | 573,440 B | 00:01.361.802 | 00:01.361.802 |
| VM: Stack | +[NSEvent(NSConcurrentEvents) _startConcurrentEventProcessing] | AppKit | 1 | 573,440 B | 00:01.576.650 | 00:01.576.650 |
| Malloc 464,00 KiB | fscache_insert_and_retain | libCoreFSCache.dylib | 1 | 475,136 B | 00:04.318.071 | 00:04.318.071 |
| Malloc 48 Bytes | operator_new_impl[abi:nqe210106](unsigned long, std::__type_descriptor_t) | libc++abi.dylib | 9,488 | 455,424 B | 00:01.213.233 | 00:04.439.050 |
| Malloc 64 Bytes | CodeVersionManager::AddNativeCodeVersion(ILCodeVersion, MethodDesc*, NativeCodeVersion::OptimizationTier, NativeCodeVersion*, PatchpointInfo*, unsigned int) | libcoreclr.dylib | 6,922 | 443,008 B | 00:02.332.607 | 00:11.433.400 |
| VM: IOAccelerator | -[IOGPUMetalDeviceShmem initWithDevice:shmemSize:shmemType:] | IOGPU | 26 | 425,984 B | 00:03.224.504 | 00:04.439.897 |
| Malloc 384,00 KiB | operator_new_impl[abi:nqe210106](unsigned long, std::__type_descriptor_t) | libc++abi.dylib | 1 | 393,216 B | 00:03.386.521 | 00:03.386.521 |
| Malloc 48 Bytes | fscache_open_worker | libCoreFSCache.dylib | 8,189 | 393,072 B | 00:03.226.719 | 00:03.656.126 |
| Malloc 352,00 KiB | SHash<CallCountingManager::CallCountingInfo::CodeVersionHashTraits>::Grow() | libcoreclr.dylib | 1 | 360,448 B | 00:04.943.204 | 00:04.943.204 |
| Malloc 320,00 KiB | fscache_open_worker | libCoreFSCache.dylib | 1 | 327,680 B | 00:03.226.692 | 00:03.226.692 |
| Malloc 128,00 KiB | ArenaAllocator::allocateNewPage(unsigned long) | libclrjit.dylib | 2 | 262,144 B | 00:02.018.815 | 00:02.019.030 |
| VM: Activity Tracing | LocaleCache.preferences() | Foundation | 1 | 262,144 B | 00:01.428.695 | 00:01.428.695 |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
