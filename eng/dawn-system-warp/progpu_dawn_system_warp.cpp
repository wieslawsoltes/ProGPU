// Original ProGPU startup adapter: use public Microsoft/Dawn types and their
// original constructors, never a locally recreated extension or COM vtable.
// O(1) scoped DXGI work, one original asynchronous request, no rendering work.
#include "progpu_dawn_system_warp.h"
#include <dawn/native/D3DBackend.h>
#include <cstdio>
#include <type_traits>

namespace {
using Microsoft::WRL::ComPtr;
static_assert(std::is_base_of_v<wgpu::ChainedStruct,
              dawn::native::d3d::RequestAdapterOptionsLUID>);
static_assert(sizeof(wgpu::ChainedStruct) == sizeof(WGPUChainedStruct));
static_assert(alignof(wgpu::ChainedStruct) == alignof(WGPUChainedStruct));
static_assert(offsetof(wgpu::ChainedStruct, nextInChain) ==
              offsetof(WGPUChainedStruct, next));
static_assert(offsetof(wgpu::ChainedStruct, sType) == offsetof(WGPUChainedStruct, sType));

HRESULT fail(HRESULT status, const char* operation, char* error, uint32_t capacity) noexcept {
    if (error != nullptr && capacity != 0) {
        std::snprintf(error, capacity, "%s (HRESULT 0x%08X)", operation,
                      static_cast<unsigned>(status));
        error[capacity - 1] = '\0';
    }
    return status;
}

HRESULT query_system_warp(DXGI_ADAPTER_DESC1& descriptor) noexcept {
    ComPtr<IDXGIFactory4> factory;
    HRESULT status = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (FAILED(status)) return status;
    ComPtr<IDXGIAdapter1> adapter;
    status = factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter));
    if (FAILED(status)) return status;
    status = adapter->GetDesc1(&descriptor);
    if (FAILED(status)) return status;
    return (descriptor.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 ? S_OK : E_FAIL;
}
}

int32_t progpu_dawn_system_warp_library_identity(void** companion, void** provider) {
    if (companion == nullptr || provider == nullptr) return E_INVALIDARG;
    HMODULE companion_module = nullptr, provider_module = nullptr;
    constexpr DWORD flags = GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
    if (!GetModuleHandleExW(flags, reinterpret_cast<LPCWSTR>(
            &progpu_dawn_system_warp_library_identity), &companion_module) ||
        !GetModuleHandleExW(flags, reinterpret_cast<LPCWSTR>(
            &wgpuInstanceRequestAdapter), &provider_module)) return E_FAIL;
    *companion = companion_module;
    *provider = provider_module;
    return S_OK;
}

int32_t progpu_dawn_system_warp_request_adapter(
    WGPUInstance instance, const WGPURequestAdapterCallbackInfo* callback,
    uint64_t* future_id, uint32_t* luid_low, int32_t* luid_high,
    char* error, uint32_t capacity) {
    if (instance == nullptr || callback == nullptr || callback->callback == nullptr ||
        callback->mode != WGPUCallbackMode_WaitAnyOnly || future_id == nullptr ||
        luid_low == nullptr || luid_high == nullptr)
        return fail(E_INVALIDARG, "Invalid original adapter request", error, capacity);
    try {
        DXGI_ADAPTER_DESC1 descriptor{};
        HRESULT status = query_system_warp(descriptor);
        if (FAILED(status)) return fail(status, "EnumWarpAdapter/software identity", error, capacity);
        dawn::native::d3d::RequestAdapterOptionsLUID selection;
        selection.adapterLUID = descriptor.AdapterLuid;
        WGPURequestAdapterOptions options = WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
        options.nextInChain = reinterpret_cast<WGPUChainedStruct*>(
            static_cast<wgpu::ChainedStruct*>(&selection));
        options.backendType = WGPUBackendType_D3D12;
        options.featureLevel = WGPUFeatureLevel_Core;
        options.powerPreference = WGPUPowerPreference_HighPerformance;
        // This is a DIFFERENT explicit LUID policy. Generic fallback is unchanged:
        // this original Dawn revision rejects it before inspecting a LUID.
        options.forceFallbackAdapter = WGPU_FALSE;
        WGPUFuture future = wgpuInstanceRequestAdapter(instance, &options, *callback);
        *future_id = future.id;
        *luid_low = descriptor.AdapterLuid.LowPart;
        *luid_high = descriptor.AdapterLuid.HighPart;
        return S_OK;
    } catch (...) {
        return fail(E_FAIL, "Original typed adapter request setup", error, capacity);
    }
}

int32_t progpu_dawn_system_warp_verify_adapter(
    WGPUAdapter adapter, uint32_t low, int32_t high, char* error, uint32_t capacity) {
    if (adapter == nullptr) return fail(E_INVALIDARG, "Missing selected adapter", error, capacity);
    try {
        WGPUAdapterInfo info = WGPU_ADAPTER_INFO_INIT;
        WGPUStatus info_status = wgpuAdapterGetInfo(adapter, &info);
        bool expected_dawn_identity = info_status == WGPUStatus_Success &&
            info.backendType == WGPUBackendType_D3D12 && info.adapterType == WGPUAdapterType_CPU;
        wgpuAdapterInfoFreeMembers(info);
        if (!expected_dawn_identity)
            return fail(E_FAIL, "Selected Dawn adapter is not D3D12/CPU", error, capacity);
        ComPtr<IDXGIAdapter> selected = dawn::native::d3d::GetDXGIAdapter(adapter);
        if (!selected) return fail(E_FAIL, "Missing selected DXGI adapter", error, capacity);
        ComPtr<IDXGIAdapter1> selected1;
        HRESULT status = selected.As(&selected1);
        if (FAILED(status)) return fail(status, "Selected DXGI adapter query", error, capacity);
        DXGI_ADAPTER_DESC1 actual{}, current_warp{};
        status = selected1->GetDesc1(&actual);
        if (FAILED(status)) return fail(status, "Selected DXGI description", error, capacity);
        status = query_system_warp(current_warp);
        if (FAILED(status)) return fail(status, "Current system WARP identity", error, capacity);
        if ((actual.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 ||
            actual.AdapterLuid.LowPart != low || actual.AdapterLuid.HighPart != high ||
            current_warp.AdapterLuid.LowPart != low || current_warp.AdapterLuid.HighPart != high)
            return fail(E_FAIL, "Selected software adapter is not the original WARP LUID", error, capacity);
        return S_OK;
    } catch (...) {
        return fail(E_FAIL, "Original selected-adapter verification", error, capacity);
    }
}
