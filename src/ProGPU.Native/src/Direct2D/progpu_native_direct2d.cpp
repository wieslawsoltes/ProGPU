#include "progpu_native_direct2d.h"

#include <d2d1_2.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.h>
#include <wrl/client.h>

#include <atomic>
#include <mutex>
#include <new>
#include <utility>

using Microsoft::WRL::ComPtr;

static_assert(
    sizeof(progpu_native_direct2d_guid) == sizeof(GUID),
    "Direct2D portable GUID layout changed");

struct progpu_native_direct2d_surface {
    ComPtr<ID3D11Device> d3d_device;
    ComPtr<ID3D11DeviceContext> d3d_context;
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIDevice> dxgi_device;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<IDXGISurface> dxgi_surface;
    ComPtr<IDXGIKeyedMutex> keyed_mutex;
    ComPtr<ID2D1Factory2> d2d_factory;
    ComPtr<ID2D1Device1> d2d_device;
    ComPtr<ID2D1DeviceContext1> d2d_context;
    ComPtr<ID2D1Bitmap1> d2d_bitmap;
    HANDLE shared_handle = nullptr;
    DXGI_ADAPTER_DESC1 adapter_descriptor{};
    uint32_t width = 0U;
    uint32_t height = 0U;
    float dpi_x = 96.0F;
    float dpi_y = 96.0F;
    bool software_adapter = false;
    bool access_acquired = false;
    bool draw_active = false;
    std::mutex access_mutex;
    std::atomic<uint64_t> content_version{0U};
    std::atomic<int32_t> last_hresult{S_OK};

    ~progpu_native_direct2d_surface()
    {
        if (draw_active && d2d_context) {
            D2D1_TAG tag1 = 0U;
            D2D1_TAG tag2 = 0U;
            static_cast<void>(d2d_context->EndDraw(&tag1, &tag2));
        }
        if (access_acquired && keyed_mutex) {
            static_cast<void>(keyed_mutex->ReleaseSync(0U));
        }
        if (d2d_context) {
            d2d_context->SetTarget(nullptr);
        }
        if (shared_handle != nullptr) {
            static_cast<void>(CloseHandle(shared_handle));
        }
    }
};

namespace {

constexpr uint32_t dxgi_format_b8g8r8a8_unorm = 87U;
constexpr uint32_t d2d_alpha_mode_premultiplied = 1U;

bool luid_is_zero(const progpu_native_direct2d_surface_options& options)
{
    return options.adapter_luid_low == 0U &&
        options.adapter_luid_high == 0;
}

bool luid_equals(
    const LUID& value,
    const progpu_native_direct2d_surface_options& options)
{
    return value.LowPart == options.adapter_luid_low &&
        value.HighPart == options.adapter_luid_high;
}

progpu_native_direct2d_status status_from_synchronization_hresult(HRESULT hr)
{
    if (hr == DXGI_ERROR_DEVICE_REMOVED ||
        hr == DXGI_ERROR_DEVICE_RESET ||
        hr == D2DERR_RECREATE_TARGET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SYNCHRONIZATION_FAILED;
}

progpu_native_direct2d_status acquire_locked(
    progpu_native_direct2d_surface& surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds)
{
    if (surface.draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }
    if (surface.access_acquired) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_ALREADY_ACQUIRED;
    }
    HRESULT hr = surface.keyed_mutex->AcquireSync(
        acquire_key,
        timeout_milliseconds);
    surface.last_hresult.store(hr, std::memory_order_release);
    if (FAILED(hr)) {
        return status_from_synchronization_hresult(hr);
    }
    surface.access_acquired = true;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status release_locked(
    progpu_native_direct2d_surface& surface,
    uint64_t release_key,
    bool advance_content_version)
{
    if (surface.draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }
    if (!surface.access_acquired) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_NOT_ACQUIRED;
    }
    HRESULT hr = surface.keyed_mutex->ReleaseSync(release_key);
    surface.last_hresult.store(hr, std::memory_order_release);
    if (FAILED(hr)) {
        return status_from_synchronization_hresult(hr);
    }
    surface.access_acquired = false;
    if (advance_content_version) {
        static_cast<void>(surface.content_version.fetch_add(
            1U,
            std::memory_order_acq_rel));
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

HRESULT select_adapter(
    const progpu_native_direct2d_surface_options& options,
    ComPtr<IDXGIAdapter1>& adapter)
{
    if (luid_is_zero(options)) {
        return S_OK;
    }

    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) {
        return hr;
    }

    for (UINT index = 0U;; ++index) {
        ComPtr<IDXGIAdapter1> candidate;
        hr = factory->EnumAdapters1(index, &candidate);
        if (hr == DXGI_ERROR_NOT_FOUND) {
            return DXGI_ERROR_NOT_FOUND;
        }
        if (FAILED(hr)) {
            return hr;
        }

        DXGI_ADAPTER_DESC1 descriptor{};
        hr = candidate->GetDesc1(&descriptor);
        if (FAILED(hr)) {
            return hr;
        }
        if (luid_equals(descriptor.AdapterLuid, options)) {
            adapter = std::move(candidate);
            return S_OK;
        }
    }
}

HRESULT create_d3d_device(
    const progpu_native_direct2d_surface_options& options,
    progpu_native_direct2d_surface& surface)
{
    HRESULT hr = select_adapter(options, surface.adapter);
    if (FAILED(hr)) {
        return hr;
    }

    UINT creation_flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if ((options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ENABLE_DEBUG) != 0U) {
        creation_flags |= D3D11_CREATE_DEVICE_DEBUG;
    }

    constexpr D3D_FEATURE_LEVEL feature_levels[] = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };
    D3D_FEATURE_LEVEL selected_feature_level{};
    const bool force_warp =
        (options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_FORCE_WARP) != 0U;
    D3D_DRIVER_TYPE driver_type = force_warp
        ? D3D_DRIVER_TYPE_WARP
        : (surface.adapter ? D3D_DRIVER_TYPE_UNKNOWN
                           : D3D_DRIVER_TYPE_HARDWARE);
    IDXGIAdapter* selected_adapter = surface.adapter.Get();
    hr = D3D11CreateDevice(
        selected_adapter,
        driver_type,
        nullptr,
        creation_flags,
        feature_levels,
        static_cast<UINT>(sizeof(feature_levels) / sizeof(feature_levels[0])),
        D3D11_SDK_VERSION,
        surface.d3d_device.GetAddressOf(),
        &selected_feature_level,
        surface.d3d_context.GetAddressOf());

    if (FAILED(hr) && !force_warp && !surface.adapter &&
        (options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ALLOW_WARP_FALLBACK) != 0U) {
        surface.d3d_device.Reset();
        surface.d3d_context.Reset();
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            creation_flags & ~D3D11_CREATE_DEVICE_DEBUG,
            feature_levels,
            static_cast<UINT>(sizeof(feature_levels) / sizeof(feature_levels[0])),
            D3D11_SDK_VERSION,
            surface.d3d_device.GetAddressOf(),
            &selected_feature_level,
            surface.d3d_context.GetAddressOf());
    }
    if (FAILED(hr)) {
        return hr;
    }

    hr = surface.d3d_device.As(&surface.dxgi_device);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IDXGIAdapter> actual_adapter;
    hr = surface.dxgi_device->GetAdapter(&actual_adapter);
    if (FAILED(hr)) {
        return hr;
    }
    hr = actual_adapter.As(&surface.adapter);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.adapter->GetDesc1(&surface.adapter_descriptor);
    if (FAILED(hr)) {
        return hr;
    }
    surface.software_adapter =
        (surface.adapter_descriptor.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0U;
    return S_OK;
}

HRESULT create_direct2d_resources(
    const progpu_native_direct2d_surface_options& options,
    progpu_native_direct2d_surface& surface)
{
    D2D1_FACTORY_OPTIONS factory_options{};
    if ((options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ENABLE_DEBUG) != 0U) {
        factory_options.debugLevel = D2D1_DEBUG_LEVEL_INFORMATION;
    }
    HRESULT hr = D2D1CreateFactory(
        D2D1_FACTORY_TYPE_MULTI_THREADED,
        __uuidof(ID2D1Factory2),
        &factory_options,
        reinterpret_cast<void**>(surface.d2d_factory.GetAddressOf()));
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.d2d_factory->CreateDevice(
        surface.dxgi_device.Get(),
        &surface.d2d_device);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.d2d_device->CreateDeviceContext(
        D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
        &surface.d2d_context);
    if (FAILED(hr)) {
        return hr;
    }

    D3D11_TEXTURE2D_DESC texture_descriptor{};
    texture_descriptor.Width = options.width;
    texture_descriptor.Height = options.height;
    texture_descriptor.MipLevels = 1U;
    texture_descriptor.ArraySize = 1U;
    texture_descriptor.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    texture_descriptor.SampleDesc.Count = 1U;
    texture_descriptor.Usage = D3D11_USAGE_DEFAULT;
    texture_descriptor.BindFlags =
        D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    texture_descriptor.MiscFlags =
        D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX |
        D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    hr = surface.d3d_device->CreateTexture2D(
        &texture_descriptor,
        nullptr,
        &surface.texture);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.texture.As(&surface.dxgi_surface);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.texture.As(&surface.keyed_mutex);
    if (FAILED(hr)) {
        return hr;
    }

    ComPtr<IDXGIResource1> resource;
    hr = surface.texture.As(&resource);
    if (FAILED(hr)) {
        return hr;
    }
    hr = resource->CreateSharedHandle(
        nullptr,
        DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
        nullptr,
        &surface.shared_handle);
    if (FAILED(hr)) {
        return hr;
    }

    D2D1_BITMAP_PROPERTIES1 bitmap_properties{};
    bitmap_properties.pixelFormat = {
        DXGI_FORMAT_B8G8R8A8_UNORM,
        D2D1_ALPHA_MODE_PREMULTIPLIED
    };
    bitmap_properties.dpiX = surface.dpi_x;
    bitmap_properties.dpiY = surface.dpi_y;
    bitmap_properties.bitmapOptions =
        D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
    hr = surface.d2d_context->CreateBitmapFromDxgiSurface(
        surface.dxgi_surface.Get(),
        &bitmap_properties,
        &surface.d2d_bitmap);
    if (FAILED(hr)) {
        return hr;
    }
    surface.d2d_context->SetTarget(surface.d2d_bitmap.Get());
    surface.d2d_context->SetDpi(surface.dpi_x, surface.dpi_y);
    return S_OK;
}

template<typename T>
progpu_native_direct2d_status return_interface(
    const ComPtr<T>& source,
    void** value)
{
    if (!source) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
    }
    source->AddRef();
    *value = source.Get();
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

} // namespace

extern "C" {

uint32_t progpu_native_direct2d_get_abi_version(void)
{
    return PROGPU_NATIVE_DIRECT2D_ABI_VERSION;
}

progpu_native_direct2d_status progpu_native_direct2d_surface_create(
    const progpu_native_direct2d_surface_options* options,
    progpu_native_direct2d_surface** surface,
    int32_t* native_hresult)
{
    if (surface != nullptr) {
        *surface = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (options == nullptr || surface == nullptr ||
        options->struct_size != sizeof(*options) ||
        options->width == 0U || options->height == 0U ||
        options->width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
        options->height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
        !(options->dpi_x > 0.0F) || !(options->dpi_y > 0.0F) ||
        (options->flags &
         ~(PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ENABLE_DEBUG |
           PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ALLOW_WARP_FALLBACK |
           PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_FORCE_WARP)) != 0U ||
        ((options->flags & PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_FORCE_WARP) != 0U &&
         !luid_is_zero(*options))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    auto instance =
        new (std::nothrow) progpu_native_direct2d_surface();
    if (instance == nullptr) {
        if (native_hresult != nullptr) {
            *native_hresult = E_OUTOFMEMORY;
        }
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
    instance->width = options->width;
    instance->height = options->height;
    instance->dpi_x = options->dpi_x;
    instance->dpi_y = options->dpi_y;

    HRESULT hr = create_d3d_device(*options, *instance);
    if (FAILED(hr)) {
        if (native_hresult != nullptr) {
            *native_hresult = hr;
        }
        delete instance;
        return hr == DXGI_ERROR_NOT_FOUND
            ? PROGPU_NATIVE_DIRECT2D_STATUS_ADAPTER_NOT_FOUND
            : PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_CREATION_FAILED;
    }
    hr = create_direct2d_resources(*options, *instance);
    if (FAILED(hr)) {
        if (native_hresult != nullptr) {
            *native_hresult = hr;
        }
        delete instance;
        return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
    }

    if (native_hresult != nullptr) {
        *native_hresult = S_OK;
    }
    *surface = instance;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

void progpu_native_direct2d_surface_destroy(
    progpu_native_direct2d_surface* surface)
{
    delete surface;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_descriptor(
    const progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_surface_descriptor* descriptor)
{
    if (surface == nullptr || descriptor == nullptr ||
        descriptor->struct_size != sizeof(*descriptor)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    descriptor->flags =
        PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_KEYED_MUTEX |
        PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_NT_HANDLE |
        (surface->software_adapter
             ? PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_SOFTWARE_ADAPTER
             : 0U);
    descriptor->width = surface->width;
    descriptor->height = surface->height;
    descriptor->dpi_x = surface->dpi_x;
    descriptor->dpi_y = surface->dpi_y;
    descriptor->dxgi_format = dxgi_format_b8g8r8a8_unorm;
    descriptor->alpha_mode = d2d_alpha_mode_premultiplied;
    descriptor->adapter_luid_low =
        surface->adapter_descriptor.AdapterLuid.LowPart;
    descriptor->adapter_luid_high =
        surface->adapter_descriptor.AdapterLuid.HighPart;
    descriptor->shared_nt_handle =
        reinterpret_cast<uintptr_t>(surface->shared_handle);
    descriptor->initial_acquire_key = 0U;
    descriptor->initial_release_key = 0U;
    descriptor->content_version =
        surface->content_version.load(std::memory_order_acquire);
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_surface_get_interface(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_interface_kind kind,
    void** value)
{
    if (surface == nullptr || value == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    *value = nullptr;
    switch (kind) {
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_DEVICE:
            return return_interface(surface->d3d_device, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_DEVICE_CONTEXT:
            return return_interface(surface->d3d_context, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_ADAPTER1:
            return return_interface(surface->adapter, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_DEVICE:
            return return_interface(surface->dxgi_device, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_SURFACE:
            return return_interface(surface->dxgi_surface, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_KEYED_MUTEX:
            return return_interface(surface->keyed_mutex, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_TEXTURE2D:
            return return_interface(surface->texture, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY1: {
            ComPtr<ID2D1Factory1> result;
            if (FAILED(surface->d2d_factory.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY2:
            return return_interface(surface->d2d_factory, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE: {
            ComPtr<ID2D1Device> result;
            if (FAILED(surface->d2d_device.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE1:
            return return_interface(surface->d2d_device, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT: {
            ComPtr<ID2D1DeviceContext> result;
            if (FAILED(surface->d2d_context.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT1:
            return return_interface(surface->d2d_context, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_BITMAP: {
            ComPtr<ID2D1Bitmap> result;
            if (FAILED(surface->d2d_bitmap.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_BITMAP1:
            return return_interface(surface->d2d_bitmap, value);
        default:
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
}

uint32_t progpu_native_direct2d_com_release(void* value)
{
    return value == nullptr
        ? 0U
        : reinterpret_cast<IUnknown*>(value)->Release();
}

progpu_native_direct2d_status progpu_native_direct2d_com_query_interface(
    void* value,
    const progpu_native_direct2d_guid* interface_id,
    void** result,
    int32_t* native_hresult)
{
    if (result != nullptr) {
        *result = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (value == nullptr || interface_id == nullptr || result == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    GUID native_interface_id{};
    native_interface_id.Data1 = interface_id->data1;
    native_interface_id.Data2 = interface_id->data2;
    native_interface_id.Data3 = interface_id->data3;
    for (uint32_t index = 0U; index < 8U; ++index) {
        native_interface_id.Data4[index] = interface_id->data4[index];
    }
    HRESULT hr = reinterpret_cast<IUnknown*>(value)->QueryInterface(
        native_interface_id,
        result);
    *native_hresult = hr;
    if (SUCCEEDED(hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    *result = nullptr;
    return hr == E_NOINTERFACE
        ? PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED
        : PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
}

progpu_native_direct2d_status progpu_native_direct2d_surface_acquire(
    progpu_native_direct2d_surface* surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds)
{
    if (surface == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    return acquire_locked(
        *surface,
        acquire_key,
        timeout_milliseconds);
}

progpu_native_direct2d_status progpu_native_direct2d_surface_release(
    progpu_native_direct2d_surface* surface,
    uint64_t release_key)
{
    if (surface == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    return release_locked(*surface, release_key, true);
}

progpu_native_direct2d_status progpu_native_direct2d_surface_begin_draw(
    progpu_native_direct2d_surface* surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds)
{
    if (surface == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    progpu_native_direct2d_status status = acquire_locked(
        *surface,
        acquire_key,
        timeout_milliseconds);
    if (status != PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS) {
        return status;
    }
    surface->d2d_context->BeginDraw();
    surface->draw_active = true;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_surface_end_draw(
    progpu_native_direct2d_surface* surface,
    uint64_t release_key,
    uint64_t* tag1,
    uint64_t* tag2,
    int32_t* native_hresult)
{
    if (tag1 != nullptr) {
        *tag1 = 0U;
    }
    if (tag2 != nullptr) {
        *tag2 = 0U;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || tag1 == nullptr || tag2 == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    D2D1_TAG native_tag1 = 0U;
    D2D1_TAG native_tag2 = 0U;
    HRESULT draw_hr = surface->d2d_context->EndDraw(
        &native_tag1,
        &native_tag2);
    surface->draw_active = false;
    *tag1 = native_tag1;
    *tag2 = native_tag2;
    *native_hresult = draw_hr;

    progpu_native_direct2d_status release_status =
        release_locked(*surface, release_key, SUCCEEDED(draw_hr));
    if (release_status != PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS) {
        return release_status;
    }
    surface->last_hresult.store(draw_hr, std::memory_order_release);
    if (SUCCEEDED(draw_hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    if (draw_hr == D2DERR_RECREATE_TARGET ||
        draw_hr == DXGI_ERROR_DEVICE_REMOVED ||
        draw_hr == DXGI_ERROR_DEVICE_RESET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_FAILED;
}

int32_t progpu_native_direct2d_surface_get_last_hresult(
    const progpu_native_direct2d_surface* surface)
{
    return surface == nullptr
        ? E_INVALIDARG
        : surface->last_hresult.load(std::memory_order_acquire);
}

} // extern "C"
