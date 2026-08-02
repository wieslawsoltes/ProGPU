# Xcode Instruments compact summary

Generated: 2026-08-02T23:43:32.1946500+00:00

| Signal | Count | Total | Maximum | Last/live |
| --- | ---: | ---: | ---: | ---: |
| Metal current allocated size | 0 | 0 B | 0 B | 0 B |
| Drawable waits | 0 | 0.000 ms | 0.000 ms | 0.000 ms |
| Graphics compiler spills | 0 | 0 B | 0 B | 0 B |
| Potential hangs | 0 | 0.000 ms | 0.000 ms | — |
| Hang risks | 0 | — | — | — |
| Command-buffer errors | 0 | — | — | — |

Metal submissions: 0; completions: 541.

## Native heap and anonymous VM

The Allocations instrument reports allocator payload and anonymous virtual-memory reservations. Managed-object attribution remains the responsibility of the paired .NET EventPipe capture.
The 92,274,688 B `VM: Dispatch continuations` row is a per-process libdispatch virtual-address reservation, not that many resident bytes. Use the paired `vmmap` resident and dirty columns before attributing it to physical footprint.

| Aggregate | Persistent | Total allocated | Transient |
| --- | ---: | ---: | ---: |
| Heap and anonymous VM | 105,597,120 B | 131,102,432 B | 25,505,312 B |
| Heap allocations | 3,803,328 B | — | — |
| Anonymous VM | 101,793,792 B | — | — |
| All VM regions | 722,337,792 B | — | — |

### Largest persistent native/VM categories

| Category | Persistent | Count | Total allocated |
| --- | ---: | ---: | ---: |
| VM: Memory Tag 255 | 546,062,336 B | 5 | 1,603,141,632 B |
| VM: Dispatch continuations | 92,274,688 B | 1 | 92,274,688 B |
| VM: Mapped File | 66,093,056 B | 69 | 83,558,400 B |
| VM: Stack | 9,256,960 B | 7 | 18,513,920 B |
| VM: MALLOC_SMALL | 8,388,608 B | 2 | 8,388,608 B |
| Malloc 64,00 KiB | 1,835,008 B | 28 | 1,966,080 B |
| Malloc 800,00 KiB | 819,200 B | 1 | 819,200 B |
| VM: Activity Tracing | 262,144 B | 1 | 262,144 B |
| Malloc 144,00 KiB | 147,456 B | 1 | 147,456 B |
| Malloc 48,00 KiB | 147,456 B | 3 | 1,179,648 B |
| Malloc 16,00 KiB | 81,920 B | 5 | 671,744 B |
| Malloc 32 Bytes | 80,896 B | 2,528 | 244,768 B |
| Malloc 64 Bytes | 77,056 B | 1,204 | 145,536 B |
| Malloc 1,50 KiB | 76,800 B | 50 | 238,080 B |
| Malloc 24,00 KiB | 73,728 B | 3 | 221,184 B |
| Malloc 192 Bytes | 44,928 B | 234 | 316,032 B |
| Malloc 224 Bytes | 34,272 B | 153 | 120,512 B |
| Malloc 3,00 KiB | 27,648 B | 9 | 82,944 B |
| Malloc 8,00 KiB | 24,576 B | 3 | 139,264 B |
| Malloc 7,00 KiB | 21,504 B | 3 | 28,672 B |
| Malloc 5,00 KiB | 20,480 B | 4 | 128,000 B |
| Malloc 48 Bytes | 20,256 B | 422 | 152,160 B |
| Malloc 160 Bytes | 20,160 B | 126 | 123,040 B |
| Malloc 128 Bytes | 19,072 B | 149 | 60,288 B |

### Largest attributed live native/VM groups

The opt-in allocation list attributed 6,202 live rows totaling 105,597,120 B.

| Category | Caller | Library | Live count | Live bytes | First | Last |
| --- | --- | --- | ---: | ---: | --- | --- |
| VM: Dispatch continuations | specialized _NSSwiftProcessInfo.operatingSystemVersion.getter | Foundation | 1 | 92,274,688 B | 00:06.121.677 | 00:06.121.677 |
| VM: Stack | CorUnix::InternalCreateThread(CorUnix::CPalThread*, _SECURITY_ATTRIBUTES*, unsigned int, unsigned int (*)(void*), void*, unsigned int, CorUnix::PalThreadType, unsigned long*, void**) | libcoreclr.dylib | 5 | 8,110,080 B | 00:01.568.305 | 00:01.577.122 |
| Malloc 64,00 KiB | ArenaAllocator::allocateNewPage(unsigned long) | libclrjit.dylib | 28 | 1,835,008 B | 00:01.589.604 | 00:01.631.680 |
| Malloc 800,00 KiB | WKS::gc_heap::init_semi_shared() | libcoreclr.dylib | 1 | 819,200 B | 00:01.569.974 | 00:01.569.974 |
| VM: Stack | SEHInitializeMachExceptions | libcoreclr.dylib | 1 | 573,440 B | 00:01.568.008 | 00:01.568.008 |
| VM: Stack | InitializeSignalHandlingCore | libSystem.Native.dylib | 1 | 573,440 B | 00:06.119.209 | 00:06.119.209 |
| VM: Activity Tracing | specialized _NSSwiftProcessInfo.operatingSystemVersion.getter | Foundation | 1 | 262,144 B | 00:06.120.868 | 00:06.120.868 |
| Malloc 144,00 KiB | WKS::gc_heap::init_gc_heap(int) | libcoreclr.dylib | 1 | 147,456 B | 00:01.570.000 | 00:01.570.000 |
| Malloc 1,50 KiB | GetMDInternalInterface | libcoreclr.dylib | 39 | 59,904 B | 00:01.570.343 | 00:06.151.814 |
| Malloc 24,00 KiB | HashMap::Rehash() | libcoreclr.dylib | 2 | 49,152 B | 00:01.614.451 | 00:06.136.349 |
| Malloc 48,00 KiB | StringToUnicode(char const*) | libcoreclr.dylib | 1 | 49,152 B | 00:01.567.956 | 00:01.567.956 |
| Malloc 48,00 KiB | VirtualCallStubManager::InitStatic() | libcoreclr.dylib | 1 | 49,152 B | 00:01.569.547 | 00:01.569.547 |
| Malloc 48,00 KiB | DbgTransportSession::Init(DebuggerIPCControlBlock*) | libcoreclr.dylib | 1 | 49,152 B | 00:01.569.800 | 00:01.569.800 |
| Malloc 16,00 KiB | <Call stack limit reached> |  | 2 | 32,768 B | 00:00.000.000 | 00:00.000.000 |
| Malloc 64 Bytes | ep_event_alloc(_EventPipeProvider*, unsigned long long, unsigned int, unsigned int, EventPipeEventLevel, bool, unsigned char const*, unsigned int) | libcoreclr.dylib | 452 | 28,928 B | 00:01.568.571 | 00:01.568.978 |
| Malloc 24,00 KiB | EEStartup() | libcoreclr.dylib | 1 | 24,576 B | 00:01.568.505 | 00:01.568.505 |
| Malloc 224 Bytes | BINDER_SPACE::ApplicationContext::SetupBindingPaths(SString&, SString&, SString&, int) | libcoreclr.dylib | 84 | 18,816 B | 00:01.578.159 | 00:01.578.507 |
| Malloc 192 Bytes | BINDER_SPACE::ApplicationContext::SetupBindingPaths(SString&, SString&, SString&, int) | libcoreclr.dylib | 95 | 18,240 B | 00:01.578.155 | 00:01.578.501 |
| Malloc 192 Bytes | CorUnix::CListedObjectManager::AllocateObject(CorUnix::CPalThread*, CorUnix::CObjectType*, CorUnix::CObjectAttributes*, CorUnix::IPalObject**) | libcoreclr.dylib | 94 | 18,048 B | 00:01.568.238 | 00:06.151.497 |
| Malloc 16,00 KiB | Initialize(int, char const* const*, unsigned int) | libcoreclr.dylib | 1 | 16,384 B | 00:01.568.217 | 00:01.568.217 |
| Malloc 16,00 KiB | Initialize(int, char const* const*, unsigned int) | libclrjit.dylib | 1 | 16,384 B | 00:01.589.553 | 00:01.589.553 |
| Malloc 16,00 KiB | SBuffer::ReallocateBuffer(unsigned int, SBuffer::Preserve) | libcoreclr.dylib | 1 | 16,384 B | 00:06.141.526 | 00:06.141.526 |
| Malloc 32 Bytes | CallCountingManager::SetCodeEntryPoint(NativeCodeVersion, unsigned long, bool, bool*) | libcoreclr.dylib | 492 | 15,744 B | 00:01.780.134 | 00:05.014.209 |
| Malloc 32 Bytes | ep_provider_add_event(_EventPipeProvider*, unsigned int, unsigned long long, unsigned int, EventPipeEventLevel, bool, unsigned char const*, unsigned int) | libcoreclr.dylib | 452 | 14,464 B | 00:01.568.572 | 00:01.568.978 |

## Metal resource allocations

Observed 0 resources totaling 0 B across the capture. 0 resources totaling 0 B had no recorded deallocation before capture end.

| Owner | Type | Count | Bytes | Live count | Live bytes |
| --- | --- | ---: | ---: | ---: | ---: |

### Largest observed resources

| Owner | Type | Size | Live at end | Relevant frame |
| --- | --- | ---: | --- | --- |
