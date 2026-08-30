#include "progpu_native_direct2d.h"

#include <d2d1_2.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <roapi.h>
#include <windows.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winstring.h>
#include <wrl/client.h>

#include <atomic>
#include <cmath>
#include <mutex>
#include <new>
#include <utility>

using Microsoft::WRL::ComPtr;

MIDL_INTERFACE("A27F0B5D-EC2C-4D4F-948F-0AA1E95E33E6")
IProGpuWin2DCanvasDevice : public IInspectable {
};

MIDL_INTERFACE("695C440D-04B3-4EDD-BFD9-63E51E9F7202")
IProGpuWin2DCanvasFactoryNative : public IInspectable {
public:
    virtual HRESULT STDMETHODCALLTYPE GetOrCreate(
        IProGpuWin2DCanvasDevice* canvas_device,
        IUnknown* resource,
        float dpi,
        IInspectable** wrapper) = 0;
};

MIDL_INTERFACE("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A")
IProGpuWin2DCanvasResourceWrapperNative : public IUnknown {
public:
    virtual HRESULT STDMETHODCALLTYPE GetNativeResource(
        IProGpuWin2DCanvasDevice* canvas_device,
        float dpi,
        REFIID interface_id,
        void** resource) = 0;
};

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
    ComPtr<IInspectable> winrt_d3d_device;
    ComPtr<IProGpuWin2DCanvasFactoryNative> win2d_factory;
    ComPtr<IInspectable> win2d_canvas_device;
    ComPtr<IInspectable> win2d_canvas_render_target;
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

GUID to_native_guid(const progpu_native_direct2d_guid& value)
{
    GUID result{};
    result.Data1 = value.data1;
    result.Data2 = value.data2;
    result.Data3 = value.data3;
    for (uint32_t index = 0U; index < 8U; ++index) {
        result.Data4[index] = value.data4[index];
    }
    return result;
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

HRESULT create_winrt_direct3d_device(
    progpu_native_direct2d_surface& surface)
{
    return CreateDirect3D11DeviceFromDXGIDevice(
        surface.dxgi_device.Get(),
        surface.winrt_d3d_device.GetAddressOf());
}

HRESULT get_win2d_factory(
    progpu_native_direct2d_surface& surface)
{
    if (surface.win2d_factory) {
        return S_OK;
    }
    constexpr wchar_t runtime_class_name[] =
        L"Microsoft.Graphics.Canvas.CanvasDevice";
    HSTRING class_name = nullptr;
    HRESULT hr = WindowsCreateString(
        runtime_class_name,
        static_cast<UINT32>(
            sizeof(runtime_class_name) / sizeof(runtime_class_name[0]) - 1U),
        &class_name);
    if (FAILED(hr)) {
        return hr;
    }

    hr = RoGetActivationFactory(
        class_name,
        __uuidof(IProGpuWin2DCanvasFactoryNative),
        reinterpret_cast<void**>(surface.win2d_factory.GetAddressOf()));
    static_cast<void>(WindowsDeleteString(class_name));
    if (FAILED(hr)) {
        surface.win2d_factory.Reset();
    }
    return hr;
}

HRESULT create_win2d_canvas_device(
    progpu_native_direct2d_surface& surface)
{
    if (surface.win2d_canvas_device) {
        return S_OK;
    }
    HRESULT hr = get_win2d_factory(surface);
    if (FAILED(hr)) {
        return hr;
    }
    return surface.win2d_factory->GetOrCreate(
        nullptr,
        surface.d2d_device.Get(),
        0.0F,
        surface.win2d_canvas_device.GetAddressOf());
}

HRESULT create_win2d_canvas_render_target(
    progpu_native_direct2d_surface& surface)
{
    HRESULT hr = create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    hr = surface.win2d_canvas_device.As(&canvas_device);
    if (FAILED(hr)) {
        return hr;
    }
    return surface.win2d_factory->GetOrCreate(
        canvas_device.Get(),
        surface.d2d_bitmap.Get(),
        surface.dpi_x,
        surface.win2d_canvas_render_target.GetAddressOf());
}

HRESULT create_win2d_wrapper(
    progpu_native_direct2d_surface& surface,
    IUnknown* native_resource,
    float dpi,
    IInspectable** wrapper)
{
    HRESULT hr = create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    hr = surface.win2d_canvas_device.As(&canvas_device);
    if (FAILED(hr)) {
        return hr;
    }
    return surface.win2d_factory->GetOrCreate(
        canvas_device.Get(),
        native_resource,
        dpi,
        wrapper);
}

HRESULT get_win2d_wrapper_native_resource(
    progpu_native_direct2d_surface& surface,
    IUnknown* wrapper,
    float dpi,
    REFIID interface_id,
    void** native_resource)
{
    HRESULT hr = create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    hr = surface.win2d_canvas_device.As(&canvas_device);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasResourceWrapperNative> resource_wrapper;
    hr = wrapper->QueryInterface(IID_PPV_ARGS(&resource_wrapper));
    if (FAILED(hr)) {
        return hr;
    }
    hr = resource_wrapper->GetNativeResource(
        canvas_device.Get(),
        dpi,
        interface_id,
        native_resource);
    if (SUCCEEDED(hr) && *native_resource == nullptr) {
        return E_UNEXPECTED;
    }
    return hr;
}

progpu_native_direct2d_status status_from_win2d_hresult(HRESULT hr)
{
    if (hr == CO_E_NOTINITIALIZED) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_WINDOWS_RUNTIME_NOT_INITIALIZED;
    }
    if (hr == REGDB_E_CLASSNOTREG ||
        hr == HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND) ||
        hr == HRESULT_FROM_WIN32(ERROR_DLL_NOT_FOUND)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_WIN2D_RUNTIME_UNAVAILABLE;
    }
    if (hr == E_NOINTERFACE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
    }
    if (hr == DXGI_ERROR_DEVICE_REMOVED ||
        hr == DXGI_ERROR_DEVICE_RESET ||
        hr == D2DERR_RECREATE_TARGET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
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
    hr = create_winrt_direct3d_device(*instance);
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
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_WINRT_DIRECT3D11_DEVICE:
            return return_interface(surface->winrt_d3d_device, value);
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

    GUID native_interface_id = to_native_guid(*interface_id);
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

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_canvas_device(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = S_OK;
    if (!surface->win2d_canvas_device) {
        hr = create_win2d_canvas_device(*surface);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        surface->win2d_canvas_device.Reset();
        return status_from_win2d_hresult(hr);
    }
    return return_interface(surface->win2d_canvas_device, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_canvas_render_target(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = S_OK;
    if (!surface->win2d_canvas_render_target) {
        hr = create_win2d_canvas_render_target(*surface);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        surface->win2d_canvas_render_target.Reset();
        return status_from_win2d_hresult(hr);
    }
    return return_interface(surface->win2d_canvas_render_target, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_native_resource(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_win2d_resource_kind resource_kind,
    const progpu_native_direct2d_guid* interface_id,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || interface_id == nullptr || value == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = S_OK;
    ComPtr<IInspectable> wrapper;
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    float dpi = 0.0F;
    switch (resource_kind) {
        case PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_DEVICE:
            hr = create_win2d_canvas_device(*surface);
            if (SUCCEEDED(hr)) {
                wrapper = surface->win2d_canvas_device;
            }
            break;
        case PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_RENDER_TARGET:
            hr = create_win2d_canvas_render_target(*surface);
            if (SUCCEEDED(hr)) {
                wrapper = surface->win2d_canvas_render_target;
                hr = surface->win2d_canvas_device.As(&canvas_device);
                dpi = surface->dpi_x;
            }
            break;
        default:
            surface->last_hresult.store(
                E_INVALIDARG,
                std::memory_order_release);
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    ComPtr<IProGpuWin2DCanvasResourceWrapperNative> resource_wrapper;
    if (SUCCEEDED(hr)) {
        hr = wrapper.As(&resource_wrapper);
    }
    if (SUCCEEDED(hr)) {
        GUID native_interface_id = to_native_guid(*interface_id);
        hr = resource_wrapper->GetNativeResource(
            canvas_device.Get(),
            dpi,
            native_interface_id,
            value);
        if (SUCCEEDED(hr) && *value == nullptr) {
            hr = E_UNEXPECTED;
        }
    }

    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        *value = nullptr;
        return status_from_win2d_hresult(hr);
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_solid_color_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_color_f* color,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || color == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(color->red) ||
        !std::isfinite(color->green) || !std::isfinite(color->blue) ||
        !std::isfinite(color->alpha)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    D2D1_COLOR_F native_color = {
        color->red,
        color->green,
        color->blue,
        color->alpha
    };
    ComPtr<ID2D1SolidColorBrush> brush;
    HRESULT hr = surface->d2d_context->CreateSolidColorBrush(
        native_color,
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
    progpu_native_direct2d_surface* surface,
    void* native_resource,
    float dpi,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_resource == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(dpi) || dpi < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IInspectable> wrapper;
    HRESULT hr = create_win2d_wrapper(
        *surface,
        reinterpret_cast<IUnknown*>(native_resource),
        dpi,
        wrapper.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(wrapper, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
    progpu_native_direct2d_surface* surface,
    void* wrapper,
    float dpi,
    const progpu_native_direct2d_guid* interface_id,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || wrapper == nullptr || interface_id == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !std::isfinite(dpi) || dpi < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    GUID native_interface_id = to_native_guid(*interface_id);
    HRESULT hr = get_win2d_wrapper_native_resource(
        *surface,
        reinterpret_cast<IUnknown*>(wrapper),
        dpi,
        native_interface_id,
        value);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        *value = nullptr;
        return status_from_win2d_hresult(hr);
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
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
