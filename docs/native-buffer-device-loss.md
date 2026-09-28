# Buffer admission after native device loss

The original LibreWinForms DataGridView application on Windows ARM64 exposed a
terminal resource-creation error followed by a native abort. The existing WebGPU
error callback recognized the failure and marked the owning device lost, but
buffer creation still returned a non-null error handle. A mapped upload ring
accepted that handle as mapped and called `wgpuBufferGetMappedRange`, which
aborted on the invalid buffer.

`WgpuContext.CreateBuffer` now checks the existing device-loss state before and
after native creation while holding the renderer's existing synchronization
lock. If creation reports loss synchronously, it releases its returned reference
without mapping, unmapping or destroying the error handle, then throws the
existing `WgpuDeviceLostException`. Ordinary buffers, mapped upload slots and
both staging-readback allocations share this admission rule. Upload writes also
reject a known lost device before mapping and after their nonblocking poll.

Already admitted slots retain their original cleanup and callback ownership.
Native completion, polling, deadlines, compiler/adapter defaults and renderer
selection are unchanged. An ordinary null allocation is not reclassified as
device loss, and losing one device does not invalidate an independent device.
The checks add constant work and no successful-path allocations.

`GpuBufferDeviceLossTests` contains 11 CPU-only cases using opaque test handles
and a synchronous creation callback. Nine fail against parent `6d006cfc`; the
ordinary-null and independent-live-device controls pass. All 11 pass with the
fix, including first/second-slot failure, exact release ownership and readback
rejection before encoding. The test API rejects unexpected native operations.

These tests establish failed-buffer admission, not the cause of the underlying
resource failure or successful application startup. Full CI and the unchanged
Windows application deadline/pixel checks remain required. Do not replace a
failed producer Build with partial artifacts or treat a typed failure as a
working DataGridView application.
