# Xcode Instruments compact summary

Generated: 2026-08-02T23:45:44.7501450+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 522.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.
The 92,274,688 B `VM: Dispatch continuations` row is a per-process libdispatch virtual-address reservation, not that many resident bytes. Use the paired `vmmap` resident and dirty columns before attributing it to physical footprint.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 105,536,944 B | 131,180,832 B | 25,643,888 B |
| Heap allocations | 3,743,152 B | — | — |
| Anonymous VM | 101,793,792 B | — | — |
| All VM regions | 722,337,792 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 546,062,336 B | 5 | 1,603,239,936 B |
| VM: Dispatch continuations | 92,274,688 B | 1 | 92,274,688 B |
| VM: Mapped File | 66,093,056 B | 69 | 83,558,400 B |
| VM: Stack | 9,256,960 B | 7 | 18,513,920 B |
| VM: MALLOC_SMALL | 8,388,608 B | 2 | 8,388,608 B |
| Malloc 64,00 KiB | 1,769,472 B | 27 | 1,900,544 B |
| Malloc 800,00 KiB | 819,200 B | 1 | 819,200 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 48,00 KiB | 147,456 B | 3 | 1,179,648 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 671,744 B |
| Malloc 32 Bytes | 81,632 B | 2,551 | 246,528 B |
| Malloc 64 Bytes | 77,248 B | 1,207 | 145,856 B |
| Malloc 1,50 KiB | 75,264 B | 49 | 236,544 B |
| Malloc 24,00 KiB | 49,152 B | 2 | 196,608 B |
| Malloc 192 Bytes | 44,736 B | 233 | 324,480 B |
| Malloc 224 Bytes | 30,464 B | 136 | 114,240 B |
| Malloc 28,00 KiB | 28,672 B | 1 | 888,832 B |
| Malloc 3,00 KiB | 27,648 B | 9 | 82,944 B |
| Malloc 8,00 KiB | 24,576 B | 3 | 139,264 B |
| Malloc 7,00 KiB | 21,504 B | 3 | 28,672 B |
| Malloc 5,00 KiB | 20,480 B | 4 | 128,000 B |
| Malloc 48 Bytes | 20,256 B | 422 | 151,920 B |
| Malloc 128 Bytes | 20,224 B | 158 | 66,432 B |

### Largest attributed live native/VM groups

The opt-in allocation list attributed 6,227 live rows totaling 105,536,944 B.

| Category | Caller | Library | Live count | Live bytes | First | Last |
| --- | --- | --- | ---: | ---: | --- | --- |
| VM: Dispatch continuations | specialized _NSSwiftProcessInfo.operatingSystemVersion.getter | Foundation | 1 | 92,274,688 B | 00:10.037.706 | 00:10.037.706 |
| VM: Stack | CorUnix::InternalCreateThread(CorUnix::CPalThread*, _SECURITY_ATTRIBUTES*, unsigned int, unsigned int (*)(void*), void*, unsigned int, CorUnix::PalThreadType, unsigned long*, void**) | libcoreclr.dylib | 5 | 8,110,080 B | 00:01.099.376 | 00:01.107.740 |
| Malloc 64,00 KiB | ArenaAllocator::allocateNewPage(unsigned long) | libclrjit.dylib | 27 | 1,769,472 B | 00:01.119.633 | 00:01.127.038 |
| Malloc 800,00 KiB | WKS::gc_heap::init_semi_shared() | libcoreclr.dylib | 1 | 819,200 B | 00:01.100.912 | 00:01.100.912 |
| VM: Stack | SEHInitializeMachExceptions | libcoreclr.dylib | 1 | 573,440 B | 00:01.099.090 | 00:01.099.090 |
| VM: Stack | InitializeSignalHandlingCore | libSystem.Native.dylib | 1 | 573,440 B | 00:10.035.134 | 00:10.035.134 |
| VM: Activity Tracing | specialized _NSSwiftProcessInfo.operatingSystemVersion.getter | Foundation | 1 | 262,144 B | 00:10.036.777 | 00:10.036.777 |
| Malloc 144,00 KiB | WKS::gc_heap::init_gc_heap(int) | libcoreclr.dylib | 1 | 147,456 B | 00:01.100.934 | 00:01.100.934 |
| Malloc 1,50 KiB | GetMDInternalInterface | libcoreclr.dylib | 39 | 59,904 B | 00:01.101.086 | 00:10.066.157 |
| Malloc 48,00 KiB | StringToUnicode(char const*) | libcoreclr.dylib | 1 | 49,152 B | 00:01.099.037 | 00:01.099.037 |
| Malloc 48,00 KiB | VirtualCallStubManager::InitStatic() | libcoreclr.dylib | 1 | 49,152 B | 00:01.100.534 | 00:01.100.534 |
| Malloc 48,00 KiB | DbgTransportSession::Init(DebuggerIPCControlBlock*) | libcoreclr.dylib | 1 | 49,152 B | 00:01.100.766 | 00:01.100.766 |
| Malloc 16,00 KiB | <Call stack limit reached> |  | 2 | 32,768 B | 00:00.000.000 | 00:00.000.000 |
| Malloc 64 Bytes | ep_event_alloc(_EventPipeProvider*, unsigned long long, unsigned int, unsigned int, EventPipeEventLevel, bool, unsigned char const*, unsigned int) | libcoreclr.dylib | 452 | 28,928 B | 00:01.099.620 | 00:01.100.041 |
| Malloc 28,00 KiB | HashMap::Rehash() | libcoreclr.dylib | 1 | 28,672 B | 00:01.141.295 | 00:01.141.295 |
| Malloc 24,00 KiB | EEStartup() | libcoreclr.dylib | 1 | 24,576 B | 00:01.099.558 | 00:01.099.558 |
| Malloc 24,00 KiB | HashMap::Rehash() | libcoreclr.dylib | 1 | 24,576 B | 00:10.052.113 | 00:10.052.113 |
| Malloc 192 Bytes | BINDER_SPACE::ApplicationContext::SetupBindingPaths(SString&, SString&, SString&, int) | libcoreclr.dylib | 95 | 18,240 B | 00:01.108.720 | 00:01.109.059 |
| Malloc 192 Bytes | CorUnix::CListedObjectManager::AllocateObject(CorUnix::CPalThread*, CorUnix::CObjectType*, CorUnix::CObjectAttributes*, CorUnix::IPalObject**) | libcoreclr.dylib | 94 | 18,048 B | 00:01.099.305 | 00:10.066.038 |
| Malloc 32 Bytes | CallCountingManager::SetCodeEntryPoint(NativeCodeVersion, unsigned long, bool, bool*) | libcoreclr.dylib | 520 | 16,640 B | 00:01.322.738 | 00:07.807.894 |
| Malloc 16,00 KiB | Initialize(int, char const* const*, unsigned int) | libcoreclr.dylib | 1 | 16,384 B | 00:01.099.285 | 00:01.099.285 |
| Malloc 16,00 KiB | Initialize(int, char const* const*, unsigned int) | libclrjit.dylib | 1 | 16,384 B | 00:01.119.587 | 00:01.119.587 |
| Malloc 16,00 KiB | SBuffer::ReallocateBuffer(unsigned int, SBuffer::Preserve) | libcoreclr.dylib | 1 | 16,384 B | 00:10.056.999 | 00:10.056.999 |
| Malloc 224 Bytes | BINDER_SPACE::ApplicationContext::SetupBindingPaths(SString&, SString&, SString&, int) | libcoreclr.dylib | 71 | 15,904 B | 00:01.108.726 | 00:01.109.063 |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
