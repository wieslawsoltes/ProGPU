# CAD allocation measurement isolation

Date: 2026-09-08. This changes test measurement, not production mesh picking.

## Evidence and limits

The original `WarmModernMeshSubobjectQueryAllocatesNothing` failed intermittently
on Linux CI with 7,312 bytes. Direct invocation outside xUnit reproduced the
same value on Ubuntu 24.04 ARM64 / .NET 10.0.5 with tiered compilation disabled:
8 of 100 ordinary runs failed; 3 of 100 runs under forced generation-0 collection
failed with 5,400–7,296 bytes. The matching macOS forced-collection run passed.

A per-query diagnostic under forced collection failed 8/100 times. An otherwise
equivalent control retained the mesh setup/warmup but executed only spin waits
inside measurement: it also reported bytes once in 100 runs. Therefore the
counter difference alone does not prove allocations originate in mesh queries.
Smaller sleep/spin controls and a standalone .NET-only control passed, so a
general GC-accounting bug is not established either. The exact runtime cause
remains unproven; do not describe this as a production rendering optimization.

The original method's Linux ARM64 disassembly is FullOpts, without tiered PGO;
assertion allocations occur after its second allocation-counter call. Thus the
[runtime maintainers' allocation-reordering discussion](https://github.com/dotnet/runtime/issues/96836)
informs measurement isolation but does not establish reordering as this failure's
cause. That discussion recommends non-inlined measurement boundaries. No external
implementation code was imported.

## Test contract

Construct the same 32-by-32 mesh, scene, index, viewport, and pick point before
starting a dedicated measured thread. Its non-inlined method retains the same
32 warmup queries, stack-allocated 16-hit buffer, 256 measured queries, all
subobject filters, and positive hit assertion. GC remains enabled; no retry,
allocation allowance, runtime change, or skip is introduced. The required
measured allocation remains exactly zero. Join is bounded to 30 seconds and
worker exceptions propagate back to the test runner.

A positive control follows the identical path while publishing a new object
through a volatile reference for every query. It must detect the escaping
allocations, preventing false success from a disabled or ineffective counter.
Setup, thread/delegate creation, and assertions remain outside measurement.
This is test-only isolation of immutable CPU selection; managed/native rendering,
shaders, ABI, resource lifetime, and production selection algorithms are unchanged.

The actual revised zero-allocation test passed 1,000/1,000 direct Ubuntu runs
under forced collection. The positive control detected allocations in
1,000/1,000 runs under the same conditions. Both focused macOS tests and the
full 1,622-test Release CAD suite pass. Final x64 CI qualification is required.
Programs, disassembly, logs, and staged VM binaries remain outside tracked source.
