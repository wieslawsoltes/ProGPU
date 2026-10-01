#pragma once
#include <stdint.h>
#include <webgpu/webgpu.h>

#ifdef __cplusplus
extern "C" {
#endif

// Startup-only borrowed original Dawn handles/descriptors. No extension layout
// crosses this ABI. Outputs publish only after original-header selection succeeds.
__declspec(dllexport) int32_t progpu_dawn_system_warp_library_identity(
    void** companion_module, void** provider_module);
__declspec(dllexport) int32_t progpu_dawn_system_warp_request_adapter(
    WGPUInstance instance, const WGPURequestAdapterCallbackInfo* callback,
    uint64_t* future_id, uint32_t* luid_low, int32_t* luid_high,
    char* error, uint32_t error_capacity);
__declspec(dllexport) int32_t progpu_dawn_system_warp_verify_adapter(
    WGPUAdapter adapter, uint32_t luid_low, int32_t luid_high,
    char* error, uint32_t error_capacity);

#ifdef __cplusplus
}
#endif
