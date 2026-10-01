# Native MIL channel retirement

`NativeMilChannel.Dispose` closes admission before calling the same provider's
original consuming destroy import. A retained handle after failure is teardown
ownership only: `IsDisposed` remains true and every operation rejects it.
Reentrant disposal cannot dispatch twice; concurrent disposals serialize behind
the current destroy attempt. Callers still serialize complete channel operations
against disposal. This primitive does not add operation leases or a finalizer.

The blittable `void Destroy(nint)` imports perform no fallible managed work after
dispatch, and the native export deletes the original channel. A normal return
clears the handle. `DllNotFoundException`, `EntryPointNotFoundException` and
`BadImageFormatException` establish a pre-entry import/binding failure; they retain
the exact handle and a later disposal retries only the same original teardown.
No channel operation is reopened and no provider is selected again.

The void ABI has no native completion receipt. An unclassified failure retains
closed ownership and the original exception, but later disposal rethrows without
another native dispatch. It is not evidence that the native channel still exists,
and arbitrary post-dispatch faults, runtime corruption or native completion cannot
be treated as safely retryable. Nothing here changes native error reporting or
qualifies source retirement queues, GPU rendering, package behavior or UI parity.

`NativeMilChannelRetirementTests` exercise the actual internal primitive with
device-free controlled pre-entry failures, reentrancy, concurrent disposal,
exact-handle retry and unknown-completion no-redispatch. They introduce no public
fake channel and do not load native libraries or execute a GPU.
