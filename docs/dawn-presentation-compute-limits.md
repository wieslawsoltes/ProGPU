# Dawn presentation compute limits

`CreateNativePresentation` queried the created Dawn device but omitted the compute
snapshot when constructing `WgpuContext`. Its default all-zero snapshot therefore
rejected explicit ordered hit queries even when the actual device supported them.
The Metal-sharing, offscreen and system-WARP factories already supplied these
limits.

All four factories now use the same exact field projection after a successful
`device.GetLimits`. It preserves storage-binding size, storage-binding count,
workgroup invocations, workgroup X size and dispatch dimensions without rounding,
clamping or deriving capabilities from adapter names. Failed queries still throw;
zero and insufficient values still fail ordered-query admission. Compiler identity,
automatic dispatch policy, fallback, adapter selection and buffer limits are unchanged.

The managed renderer reads this snapshot. The native C++ renderer independently
queries its real device in `HitTesting/progpu_native_hit_testing_execution.cpp`
and already applies the corresponding storage/workgroup/dispatch checks for both
providers, so no native semantic change is needed.

Local backend compilation and all 36 linked device-free Dawn ownership/diagnostic
tests passed, including six new exact-field/default/factory-wiring cases. These
checks do not create a presentation window or run GPU queries. Full Build and
actual platform presentation/ordered-query validation remain required; this fix
does not qualify automatic Dawn compiler selection or application UI behavior.
