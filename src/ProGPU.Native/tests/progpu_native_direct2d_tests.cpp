#include "progpu_native_direct2d.h"

#include <d2d1_2.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <windows.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <wrl/client.h>

#include <cstdlib>
#include <iostream>

using Microsoft::WRL::ComPtr;
using Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;

namespace {

[[noreturn]] void fail(const char* message)
{
    std::cerr << message << '\n';
    std::exit(EXIT_FAILURE);
}

void require(bool condition, const char* message)
{
    if (!condition) {
        fail(message);
    }
}

template<typename T>
ComPtr<T> get_interface(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_interface_kind kind)
{
    void* value = nullptr;
    require(
        progpu_native_direct2d_surface_get_interface(
            surface,
            kind,
            &value) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "Direct2D interface query failed");
    require(value != nullptr, "Direct2D interface query returned null");
    ComPtr<T> result;
    result.Attach(static_cast<T*>(value));
    return result;
}

progpu_native_direct2d_guid to_portable_guid(const GUID& value)
{
    progpu_native_direct2d_guid result{};
    result.data1 = value.Data1;
    result.data2 = value.Data2;
    result.data3 = value.Data3;
    for (uint32_t index = 0U; index < 8U; ++index) {
        result.data4[index] = value.Data4[index];
    }
    return result;
}

} // namespace

int main()
{
    require(
        progpu_native_direct2d_get_abi_version() ==
            PROGPU_NATIVE_DIRECT2D_ABI_VERSION,
        "unexpected Direct2D provider ABI version");

    progpu_native_direct2d_surface_options invalid_options{};
    invalid_options.struct_size = sizeof(invalid_options);
    invalid_options.dpi_x = 96.0F;
    invalid_options.dpi_y = 96.0F;
    progpu_native_direct2d_surface* invalid_surface = nullptr;
    int32_t native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create(
            &invalid_options,
            &invalid_surface,
            &native_hresult) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT,
        "zero-sized Direct2D surface did not fail closed");
    require(invalid_surface == nullptr, "invalid surface escaped creation");
    require(native_hresult == E_INVALIDARG, "invalid HRESULT was not reported");

    progpu_native_direct2d_surface_options options{};
    options.struct_size = sizeof(options);
    options.flags =
        PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ALLOW_WARP_FALLBACK;
    options.width = 64U;
    options.height = 48U;
    options.dpi_x = 120.0F;
    options.dpi_y = 144.0F;

    progpu_native_direct2d_surface* surface = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create(
            &options,
            &surface,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "Direct2D surface creation failed");
    require(surface != nullptr, "Direct2D surface was not returned");
    require(native_hresult == S_OK, "successful creation retained a failure HRESULT");

    progpu_native_direct2d_surface_descriptor descriptor{};
    descriptor.struct_size = sizeof(descriptor);
    require(
        progpu_native_direct2d_surface_get_descriptor(
            surface,
            &descriptor) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "Direct2D descriptor query failed");
    require(descriptor.width == 64U && descriptor.height == 48U,
        "Direct2D descriptor dimensions changed");
    require(descriptor.dpi_x == 120.0F && descriptor.dpi_y == 144.0F,
        "Direct2D descriptor DPI changed");
    require(descriptor.dxgi_format == DXGI_FORMAT_B8G8R8A8_UNORM,
        "Direct2D descriptor format changed");
    require(descriptor.alpha_mode == D2D1_ALPHA_MODE_PREMULTIPLIED,
        "Direct2D descriptor alpha mode changed");
    require(descriptor.shared_nt_handle != 0U,
        "Direct2D descriptor omitted its NT shared handle");
    require(
        (descriptor.flags &
         (PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_KEYED_MUTEX |
          PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_NT_HANDLE)) ==
            (PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_KEYED_MUTEX |
             PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_NT_HANDLE),
        "Direct2D descriptor omitted synchronization flags");
    require(descriptor.initial_acquire_key == 0U &&
            descriptor.initial_release_key == 0U &&
            descriptor.content_version == 0U,
        "Direct2D initial synchronization state changed");

    auto factory1 = get_interface<ID2D1Factory1>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY1);
    auto factory2 = get_interface<ID2D1Factory2>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY2);
    auto device = get_interface<ID2D1Device1>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE1);
    auto context = get_interface<ID2D1DeviceContext1>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT1);
    auto base_context = get_interface<ID2D1DeviceContext>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT);
    auto bitmap = get_interface<ID2D1Bitmap1>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_BITMAP1);
    auto d3d_device = get_interface<ID3D11Device>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_DEVICE);
    auto texture = get_interface<ID3D11Texture2D>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_TEXTURE2D);
    auto winrt_d3d_device = get_interface<IInspectable>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_WINRT_DIRECT3D11_DEVICE);

    require(factory1 && factory2 && device && context && bitmap &&
            base_context && d3d_device && texture && winrt_d3d_device,
        "one or more genuine COM interfaces were unavailable");

    ComPtr<IDirect3DDxgiInterfaceAccess> dxgi_interface_access;
    require(SUCCEEDED(winrt_d3d_device.As(&dxgi_interface_access)),
        "WinRT IDirect3DDevice omitted DXGI interface access");
    ComPtr<ID3D11Device> unwrapped_d3d_device;
    require(SUCCEEDED(dxgi_interface_access->GetInterface(
                IID_PPV_ARGS(&unwrapped_d3d_device))),
        "WinRT IDirect3DDevice did not expose its D3D11 device");
    ComPtr<IUnknown> original_device_identity;
    ComPtr<IUnknown> unwrapped_device_identity;
    require(SUCCEEDED(d3d_device.As(&original_device_identity)) &&
            SUCCEEDED(unwrapped_d3d_device.As(&unwrapped_device_identity)) &&
            original_device_identity.Get() == unwrapped_device_identity.Get(),
        "WinRT IDirect3DDevice did not preserve COM device identity");

    progpu_native_direct2d_guid context1_id =
        to_portable_guid(__uuidof(ID2D1DeviceContext1));
    void* queried_context_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_com_query_interface(
            base_context.Get(),
            &context1_id,
            &queried_context_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            queried_context_value != nullptr && native_hresult == S_OK,
        "generic COM query did not return ID2D1DeviceContext1");
    ComPtr<ID2D1DeviceContext1> queried_context;
    queried_context.Attach(
        static_cast<ID2D1DeviceContext1*>(queried_context_value));

    progpu_native_direct2d_guid unsupported_id =
        to_portable_guid(GUID_NULL);
    void* unsupported_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_com_query_interface(
            base_context.Get(),
            &unsupported_id,
            &unsupported_value,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            unsupported_value == nullptr && native_hresult == E_NOINTERFACE,
        "unsupported generic COM query did not fail closed");
    ComPtr<ID2D1Multithread> multithread;
    require(SUCCEEDED(factory2.As(&multithread)) &&
            multithread->GetMultithreadProtected(),
        "Direct2D factory is not multithread protected");

    D2D1_SIZE_U pixel_size = bitmap->GetPixelSize();
    require(pixel_size.width == 64U && pixel_size.height == 48U,
        "ID2D1Bitmap1 pixel size changed");
    float dpi_x = 0.0F;
    float dpi_y = 0.0F;
    bitmap->GetDpi(&dpi_x, &dpi_y);
    require(dpi_x == 120.0F && dpi_y == 144.0F,
        "ID2D1Bitmap1 DPI changed");
    require(
        (bitmap->GetOptions() &
         (D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW)) ==
            (D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW),
        "ID2D1Bitmap1 target options changed");

    require(
        progpu_native_direct2d_surface_release(surface, 0U) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_NOT_ACQUIRED,
        "unacquired Direct2D release did not fail closed");
    require(
        progpu_native_direct2d_surface_begin_draw(surface, 0U, 1000U) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "Direct2D producer draw acquisition failed");
    require(
        progpu_native_direct2d_surface_begin_draw(surface, 0U, 0U) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE,
        "nested Direct2D draw did not fail closed");
    require(
        progpu_native_direct2d_surface_release(surface, 1U) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE,
        "Direct2D draw scope allowed a raw mutex release");

    context->Clear(D2D1::ColorF(0.125F, 0.25F, 0.5F, 1.0F));
    ComPtr<ID2D1SolidColorBrush> brush;
    require(SUCCEEDED(context->CreateSolidColorBrush(
        D2D1::ColorF(D2D1::ColorF::Orange),
        brush.GetAddressOf())),
        "Direct2D solid brush creation failed");
    context->FillRectangle(
        D2D1::RectF(4.0F, 5.0F, 32.0F, 28.0F),
        brush.Get());
    D2D1_TAG tag1 = 0U;
    D2D1_TAG tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_draw(
            surface,
            0U,
            &tag1,
            &tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "Direct2D transactional drawing failed");
    require(
        progpu_native_direct2d_surface_end_draw(
            surface,
            1U,
            &tag1,
            &tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "unmatched Direct2D EndDraw did not fail closed");

    ComPtr<ID3D11Device1> d3d_device1;
    require(SUCCEEDED(d3d_device.As(&d3d_device1)),
        "ID3D11Device1 is unavailable");
    ComPtr<ID3D11Texture2D> imported_texture;
    require(SUCCEEDED(d3d_device1->OpenSharedResource1(
        reinterpret_cast<HANDLE>(descriptor.shared_nt_handle),
        IID_PPV_ARGS(&imported_texture))),
        "DXGI NT shared handle could not be reopened");
    ComPtr<IDXGIKeyedMutex> imported_mutex;
    require(SUCCEEDED(imported_texture.As(&imported_mutex)),
        "reopened DXGI texture omitted its keyed mutex");
    require(SUCCEEDED(imported_mutex->AcquireSync(0U, 1000U)),
        "DXGI consumer mutex acquisition failed");
    require(SUCCEEDED(imported_mutex->ReleaseSync(0U)),
        "DXGI consumer mutex release failed");
    require(
        progpu_native_direct2d_surface_acquire(surface, 0U, 1000U) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "Direct2D producer did not observe the consumer handoff");
    require(
        progpu_native_direct2d_surface_release(surface, 0U) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "Direct2D second producer release failed");

    descriptor = {};
    descriptor.struct_size = sizeof(descriptor);
    require(
        progpu_native_direct2d_surface_get_descriptor(
            surface,
            &descriptor) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            descriptor.content_version == 2U,
        "Direct2D content version did not follow producer releases");
    require(
        progpu_native_direct2d_surface_get_last_hresult(surface) == S_OK,
        "Direct2D provider retained a synchronization failure");

    void* invalid_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    require(
        progpu_native_direct2d_surface_get_interface(
            surface,
            static_cast<progpu_native_direct2d_interface_kind>(999),
            &invalid_value) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_value == nullptr,
        "unknown Direct2D interface did not fail closed");

    imported_mutex.Reset();
    imported_texture.Reset();
    texture.Reset();
    unwrapped_device_identity.Reset();
    original_device_identity.Reset();
    unwrapped_d3d_device.Reset();
    dxgi_interface_access.Reset();
    winrt_d3d_device.Reset();
    d3d_device.Reset();
    bitmap.Reset();
    queried_context.Reset();
    base_context.Reset();
    context.Reset();
    device.Reset();
    factory2.Reset();
    factory1.Reset();
    progpu_native_direct2d_surface_destroy(surface);
    return EXIT_SUCCESS;
}
