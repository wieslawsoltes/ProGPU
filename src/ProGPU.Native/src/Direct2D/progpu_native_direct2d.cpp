#include "progpu_native_direct2d.h"

#include <d2d1_2.h>
#include <d3d11.h>
#include <dwrite_3.h>
#include <dxgi1_2.h>
#include <roapi.h>
#include <windows.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winstring.h>
#include <wrl/client.h>

#include <atomic>
#include <cmath>
#include <iterator>
#include <mutex>
#include <new>
#include <string>
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
static_assert(
    sizeof(progpu_native_direct2d_color_f) == sizeof(D2D1_COLOR_F),
    "Direct2D portable color layout changed");
static_assert(
    sizeof(progpu_native_direct2d_gradient_stop) ==
        sizeof(D2D1_GRADIENT_STOP),
    "Direct2D portable gradient-stop layout changed");
static_assert(
    sizeof(progpu_native_direct2d_stroke_style_properties) ==
        sizeof(D2D1_STROKE_STYLE_PROPERTIES1),
    "Direct2D portable stroke-style layout changed");
static_assert(
    sizeof(progpu_native_direct2d_bitmap_brush_properties) ==
        sizeof(D2D1_BITMAP_BRUSH_PROPERTIES1),
    "Direct2D portable bitmap-brush layout changed");
static_assert(
    sizeof(progpu_native_direct2d_image_brush_properties) ==
        sizeof(D2D1_IMAGE_BRUSH_PROPERTIES),
    "Direct2D portable image-brush layout changed");
static_assert(
    sizeof(progpu_native_direct2d_size_f) == sizeof(D2D1_SIZE_F),
    "Direct2D portable size layout changed");
static_assert(
    sizeof(uint16_t) == sizeof(wchar_t),
    "The Windows DirectWrite ABI requires 16-bit wchar_t");

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
    ComPtr<IDWriteFactory3> dwrite_factory;
    HANDLE shared_handle = nullptr;
    DXGI_ADAPTER_DESC1 adapter_descriptor{};
    uint32_t width = 0U;
    uint32_t height = 0U;
    float dpi_x = 96.0F;
    float dpi_y = 96.0F;
    bool software_adapter = false;
    bool access_acquired = false;
    bool draw_active = false;
    bool command_list_draw_active = false;
    uint32_t layer_depth = 0U;
    ComPtr<ID2D1CommandList> active_command_list;
    std::mutex access_mutex;
    std::atomic<uint64_t> content_version{0U};
    std::atomic<int32_t> last_hresult{S_OK};

    ~progpu_native_direct2d_surface()
    {
        if (draw_active && d2d_context) {
            while (layer_depth != 0U) {
                d2d_context->PopLayer();
                --layer_depth;
            }
            D2D1_TAG tag1 = 0U;
            D2D1_TAG tag2 = 0U;
            HRESULT draw_hr = d2d_context->EndDraw(&tag1, &tag2);
            if (command_list_draw_active) {
                d2d_context->SetTarget(d2d_bitmap.Get());
                if (SUCCEEDED(draw_hr) && active_command_list) {
                    static_cast<void>(active_command_list->Close());
                }
            }
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

bool is_finite(const progpu_native_direct2d_color_f& color)
{
    return std::isfinite(color.red) && std::isfinite(color.green) &&
        std::isfinite(color.blue) && std::isfinite(color.alpha);
}

bool is_finite(const progpu_native_direct2d_point_2f& point)
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

bool is_finite(const progpu_native_direct2d_matrix_3x2_f& matrix)
{
    return std::isfinite(matrix.m11) && std::isfinite(matrix.m12) &&
        std::isfinite(matrix.m21) && std::isfinite(matrix.m22) &&
        std::isfinite(matrix.m31) && std::isfinite(matrix.m32);
}

D2D1_MATRIX_3X2_F to_native_matrix(
    const progpu_native_direct2d_matrix_3x2_f& matrix)
{
    D2D1_MATRIX_3X2_F result{};
    result._11 = matrix.m11;
    result._12 = matrix.m12;
    result._21 = matrix.m21;
    result._22 = matrix.m22;
    result._31 = matrix.m31;
    result._32 = matrix.m32;
    return result;
}

bool is_valid(progpu_native_direct2d_color_space value)
{
    return value == PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_CUSTOM ||
        value == PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SRGB ||
        value == PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SCRGB;
}

bool is_valid(progpu_native_direct2d_buffer_precision value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_UNKNOWN &&
        value <= PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_32BPC_FLOAT;
}

bool is_valid(progpu_native_direct2d_extend_mode value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP &&
        value <= PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_MIRROR;
}

bool is_valid_interpolation_mode(uint32_t value)
{
    return value <=
        PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC;
}

bool is_valid(progpu_native_direct2d_antialias_mode value)
{
    return value == PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_PER_PRIMITIVE ||
        value == PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_ALIASED;
}

bool is_valid_layer_options(uint32_t value)
{
    constexpr uint32_t allowed =
        PROGPU_NATIVE_DIRECT2D_LAYER_OPTION_INITIALIZE_FROM_BACKGROUND |
        PROGPU_NATIVE_DIRECT2D_LAYER_OPTION_IGNORE_ALPHA;
    return (value & ~allowed) == 0U;
}

bool is_valid_text_format(
    const progpu_native_direct2d_text_format_properties& properties)
{
    return properties.struct_size == sizeof(properties) &&
        properties.font_weight >= 1U && properties.font_weight <= 999U &&
        properties.font_style <= PROGPU_NATIVE_DIRECT2D_FONT_STYLE_ITALIC &&
        properties.font_stretch >=
            PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_CONDENSED &&
        properties.font_stretch <=
            PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_EXPANDED &&
        std::isfinite(properties.font_size) && properties.font_size > 0.0F &&
        properties.text_alignment <=
            PROGPU_NATIVE_DIRECT2D_TEXT_ALIGNMENT_JUSTIFIED &&
        properties.paragraph_alignment <=
            PROGPU_NATIVE_DIRECT2D_PARAGRAPH_ALIGNMENT_CENTER &&
        properties.word_wrapping <=
            PROGPU_NATIVE_DIRECT2D_WORD_WRAPPING_CHARACTER &&
        properties.reading_direction <=
            PROGPU_NATIVE_DIRECT2D_READING_DIRECTION_BOTTOM_TO_TOP &&
        properties.flow_direction <=
            PROGPU_NATIVE_DIRECT2D_FLOW_DIRECTION_RIGHT_TO_LEFT &&
        std::isfinite(properties.incremental_tab_stop) &&
        properties.incremental_tab_stop >= 0.0F;
}

bool is_valid_text_range_format(
    const progpu_native_direct2d_text_range_format& formatting,
    const void* drawing_effect_brush)
{
    constexpr uint32_t allowed =
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_SIZE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_WEIGHT |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STYLE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STRETCH |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_UNDERLINE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_STRIKETHROUGH |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_DRAWING_EFFECT;
    if (formatting.struct_size != sizeof(formatting) ||
        formatting.flags == PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_NONE ||
        (formatting.flags & ~allowed) != 0U ||
        formatting.range_length == 0U ||
        formatting.range_start > UINT32_MAX - formatting.range_length ||
        (drawing_effect_brush != nullptr &&
            (formatting.flags &
                PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_DRAWING_EFFECT) ==
                0U)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_SIZE) != 0U &&
        (!std::isfinite(formatting.font_size) ||
            formatting.font_size <= 0.0F)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_WEIGHT) != 0U &&
        (formatting.font_weight < 1U || formatting.font_weight > 999U)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STYLE) != 0U &&
        formatting.font_style > PROGPU_NATIVE_DIRECT2D_FONT_STYLE_ITALIC) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STRETCH) != 0U &&
        (formatting.font_stretch <
                PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_CONDENSED ||
            formatting.font_stretch >
                PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_EXPANDED)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_UNDERLINE) != 0U &&
        formatting.underline > 1U) {
        return false;
    }
    return (formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_STRIKETHROUGH) == 0U ||
        formatting.strikethrough <= 1U;
}

bool contains_null(const uint16_t* text, uint32_t length)
{
    for (uint32_t index = 0U; index < length; ++index) {
        if (text[index] == 0U) {
            return true;
        }
    }
    return false;
}

bool is_valid_draw_text_options(uint32_t value)
{
    constexpr uint32_t allowed =
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_NO_SNAP |
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_CLIP |
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_ENABLE_COLOR_FONT |
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_DISABLE_COLOR_BITMAP_SNAPPING;
    return (value & ~allowed) == 0U;
}

bool is_valid(progpu_native_direct2d_measuring_mode value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL &&
        value <= PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_GDI_NATURAL;
}

bool is_valid(const progpu_native_direct2d_rect_f& rectangle)
{
    return std::isfinite(rectangle.x) && std::isfinite(rectangle.y) &&
        std::isfinite(rectangle.width) && std::isfinite(rectangle.height) &&
        rectangle.width >= 0.0F && rectangle.height >= 0.0F &&
        std::isfinite(rectangle.x + rectangle.width) &&
        std::isfinite(rectangle.y + rectangle.height);
}

bool is_valid_effect_property(
    progpu_native_direct2d_effect_property_type type,
    uint32_t data_size)
{
    switch (type) {
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_BOOL:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_UINT32:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_INT32:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_FLOAT:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_ENUM:
            return data_size == sizeof(uint32_t);
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_VECTOR2:
            return data_size == sizeof(float) * 2U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_VECTOR3:
            return data_size == sizeof(float) * 3U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_VECTOR4:
            return data_size == sizeof(float) * 4U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_CLSID:
            return data_size == sizeof(progpu_native_direct2d_guid);
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_BLOB:
            return data_size != 0U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_3X2:
            return data_size == sizeof(float) * 6U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_4X3:
            return data_size == sizeof(float) * 12U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_4X4:
            return data_size == sizeof(float) * 16U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_5X4:
            return data_size == sizeof(float) * 20U;
        default:
            return false;
    }
}

bool is_empty(const progpu_native_direct2d_guid& value)
{
    if (value.data1 != 0U || value.data2 != 0U || value.data3 != 0U) {
        return false;
    }
    for (uint32_t index = 0U; index < 8U; ++index) {
        if (value.data4[index] != 0U) {
            return false;
        }
    }
    return true;
}

bool is_valid(progpu_native_direct2d_color_interpolation_mode value)
{
    return value ==
            PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_STRAIGHT ||
        value ==
            PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_PREMULTIPLIED;
}

bool is_valid_cap_style(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_CAP_STYLE_TRIANGLE;
}

bool is_valid_line_join(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_LINE_JOIN_MITER_OR_BEVEL;
}

bool is_valid_dash_style(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM;
}

bool is_valid_stroke_transform_type(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_STROKE_TRANSFORM_HAIRLINE;
}

bool is_geometry_path_segment_valid(
    const progpu_native_direct2d_path_segment& segment)
{
    if (segment.kind > static_cast<uint32_t>(
            PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC) ||
        (segment.flags &
         ~(PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_FORCE_UNSTROKED |
           PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_FORCE_ROUND_LINE_JOIN)) !=
            0U ||
        (segment.arc_flags &
         ~(PROGPU_NATIVE_DIRECT2D_ARC_FLAG_CLOCKWISE |
           PROGPU_NATIVE_DIRECT2D_ARC_FLAG_LARGE)) != 0U ||
        !is_finite(segment.point1)) {
        return false;
    }
    switch (segment.kind) {
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_LINE:
            return segment.arc_flags == 0U;
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_QUADRATIC:
            return segment.arc_flags == 0U && is_finite(segment.point2);
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_CUBIC:
            return segment.arc_flags == 0U && is_finite(segment.point2) &&
                is_finite(segment.point3);
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC:
            return is_finite(segment.size) &&
                segment.size.x >= 0.0F && segment.size.y >= 0.0F &&
                std::isfinite(segment.rotation_angle);
        default:
            return false;
    }
}

HRESULT create_path_geometry(
    progpu_native_direct2d_surface& surface,
    ComPtr<ID2D1PathGeometry1>& path)
{
    ComPtr<ID2D1PathGeometry> base_path;
    HRESULT hr = surface.d2d_factory->CreatePathGeometry(&base_path);
    if (FAILED(hr)) {
        return hr;
    }
    return base_path.As(&path);
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
    hr = DWriteCreateFactory(
        DWRITE_FACTORY_TYPE_SHARED,
        __uuidof(IDWriteFactory3),
        reinterpret_cast<IUnknown**>(
            surface.dwrite_factory.GetAddressOf()));
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
    ComPtr<IDWriteTextLayout> text_layout;
    ComPtr<IDWriteTextFormat> text_format;
    bool is_text_layout = SUCCEEDED(
        native_resource->QueryInterface(IID_PPV_ARGS(&text_layout)));
    bool device_independent = !is_text_layout && SUCCEEDED(
        native_resource->QueryInterface(IID_PPV_ARGS(&text_format)));
    HRESULT hr = device_independent
        ? get_win2d_factory(surface)
        : create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    if (!device_independent) {
        hr = surface.win2d_canvas_device.As(&canvas_device);
        if (FAILED(hr)) {
            return hr;
        }
    }
    return surface.win2d_factory->GetOrCreate(
        device_independent ? nullptr : canvas_device.Get(),
        native_resource,
        device_independent ? 0.0F : dpi,
        wrapper);
}

HRESULT get_win2d_wrapper_native_resource(
    progpu_native_direct2d_surface& surface,
    IUnknown* wrapper,
    float dpi,
    REFIID interface_id,
    void** native_resource)
{
    bool device_independent =
        IsEqualIID(interface_id, __uuidof(IDWriteTextFormat)) ||
        IsEqualIID(interface_id, __uuidof(IDWriteTextFormat1));
    HRESULT hr = device_independent
        ? get_win2d_factory(surface)
        : create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    if (!device_independent) {
        hr = surface.win2d_canvas_device.As(&canvas_device);
        if (FAILED(hr)) {
            return hr;
        }
    }
    ComPtr<IProGpuWin2DCanvasResourceWrapperNative> resource_wrapper;
    hr = wrapper->QueryInterface(IID_PPV_ARGS(&resource_wrapper));
    if (FAILED(hr)) {
        return hr;
    }
    hr = resource_wrapper->GetNativeResource(
        device_independent ? nullptr : canvas_device.Get(),
        device_independent ? 0.0F : dpi,
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
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DWRITE_FACTORY3:
            return return_interface(surface->dwrite_factory, value);
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
        native_hresult == nullptr || !is_finite(*color)) {
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
progpu_native_direct2d_surface_create_gradient_stop_collection(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_gradient_stop* stops,
    uint32_t stop_count,
    progpu_native_direct2d_color_space pre_interpolation_space,
    progpu_native_direct2d_color_space post_interpolation_space,
    progpu_native_direct2d_buffer_precision buffer_precision,
    progpu_native_direct2d_extend_mode extend_mode,
    progpu_native_direct2d_color_interpolation_mode interpolation_mode,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || stops == nullptr || stop_count == 0U ||
        value == nullptr || native_hresult == nullptr ||
        !is_valid(pre_interpolation_space) ||
        !is_valid(post_interpolation_space) ||
        !is_valid(buffer_precision) || !is_valid(extend_mode) ||
        !is_valid(interpolation_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0U; index < stop_count; ++index) {
        if (!std::isfinite(stops[index].position) ||
            !is_finite(stops[index].color)) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1GradientStopCollection1> collection;
    HRESULT hr = surface->d2d_context->CreateGradientStopCollection(
        reinterpret_cast<const D2D1_GRADIENT_STOP*>(stops),
        stop_count,
        static_cast<D2D1_COLOR_SPACE>(pre_interpolation_space),
        static_cast<D2D1_COLOR_SPACE>(post_interpolation_space),
        static_cast<D2D1_BUFFER_PRECISION>(buffer_precision),
        static_cast<D2D1_EXTEND_MODE>(extend_mode),
        static_cast<D2D1_COLOR_INTERPOLATION_MODE>(interpolation_mode),
        collection.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(collection, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_linear_gradient_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_linear_gradient_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void* gradient_stop_collection,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || properties == nullptr ||
        brush_properties == nullptr || gradient_stop_collection == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_finite(properties->start_point) ||
        !is_finite(properties->end_point) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES native_properties = {
        {properties->start_point.x, properties->start_point.y},
        {properties->end_point.x, properties->end_point.y}
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1LinearGradientBrush> brush;
    HRESULT hr = surface->d2d_context->CreateLinearGradientBrush(
        native_properties,
        native_brush_properties,
        reinterpret_cast<ID2D1GradientStopCollection*>(
            gradient_stop_collection),
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_radial_gradient_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_radial_gradient_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void* gradient_stop_collection,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || properties == nullptr ||
        brush_properties == nullptr || gradient_stop_collection == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_finite(properties->center) ||
        !is_finite(properties->gradient_origin_offset) ||
        !std::isfinite(properties->radius_x) ||
        !std::isfinite(properties->radius_y) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES native_properties = {
        {properties->center.x, properties->center.y},
        {
            properties->gradient_origin_offset.x,
            properties->gradient_origin_offset.y
        },
        properties->radius_x,
        properties->radius_y
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RadialGradientBrush> brush;
    HRESULT hr = surface->d2d_context->CreateRadialGradientBrush(
        native_properties,
        native_brush_properties,
        reinterpret_cast<ID2D1GradientStopCollection*>(
            gradient_stop_collection),
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_bitmap(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_bitmap_properties* properties,
    const uint8_t* pixels,
    uint64_t pixel_byte_count,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }

    uint64_t row_byte_count = properties == nullptr
        ? 0U
        : static_cast<uint64_t>(properties->width) * 4U;
    uint64_t required_byte_count = properties == nullptr ||
            properties->height == 0U
        ? 0U
        : static_cast<uint64_t>(properties->stride) *
                (properties->height - 1U) +
            row_byte_count;
    if (surface == nullptr || properties == nullptr || pixels == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        properties->width == 0U || properties->height == 0U ||
        properties->reserved != 0U ||
        row_byte_count > UINT32_MAX ||
        properties->stride < row_byte_count ||
        pixel_byte_count < required_byte_count ||
        !std::isfinite(properties->dpi_x) ||
        !std::isfinite(properties->dpi_y) ||
        properties->dpi_x <= 0.0F || properties->dpi_y <= 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_BITMAP_PROPERTIES1 native_properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_NONE,
        D2D1::PixelFormat(
            DXGI_FORMAT_B8G8R8A8_UNORM,
            D2D1_ALPHA_MODE_PREMULTIPLIED),
        properties->dpi_x,
        properties->dpi_y);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Bitmap1> bitmap;
    HRESULT hr = surface->d2d_context->CreateBitmap(
        D2D1::SizeU(properties->width, properties->height),
        pixels,
        properties->stride,
        &native_properties,
        bitmap.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(bitmap, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_bitmap_brush(
    progpu_native_direct2d_surface* surface,
    void* bitmap,
    const progpu_native_direct2d_bitmap_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || bitmap == nullptr || properties == nullptr ||
        brush_properties == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_x)) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_y)) ||
        !is_valid_interpolation_mode(properties->interpolation_mode) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_BITMAP_BRUSH_PROPERTIES1 native_properties = {
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_x),
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_y),
        static_cast<D2D1_INTERPOLATION_MODE>(
            properties->interpolation_mode)
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1BitmapBrush1> brush;
    HRESULT hr = surface->d2d_context->CreateBitmapBrush(
        reinterpret_cast<ID2D1Bitmap*>(bitmap),
        &native_properties,
        &native_brush_properties,
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_image_brush(
    progpu_native_direct2d_surface* surface,
    void* image,
    const progpu_native_direct2d_image_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    const progpu_native_direct2d_rect_f* source_rectangle =
        properties == nullptr ? nullptr : &properties->source_rectangle;
    if (surface == nullptr || image == nullptr || properties == nullptr ||
        brush_properties == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !std::isfinite(source_rectangle->x) ||
        !std::isfinite(source_rectangle->y) ||
        !std::isfinite(source_rectangle->width) ||
        !std::isfinite(source_rectangle->height) ||
        source_rectangle->width <= 0.0F ||
        source_rectangle->height <= 0.0F ||
        !std::isfinite(source_rectangle->x + source_rectangle->width) ||
        !std::isfinite(source_rectangle->y + source_rectangle->height) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_x)) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_y)) ||
        !is_valid_interpolation_mode(properties->interpolation_mode) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_IMAGE_BRUSH_PROPERTIES native_properties = {
        D2D1::RectF(
            source_rectangle->x,
            source_rectangle->y,
            source_rectangle->x + source_rectangle->width,
            source_rectangle->y + source_rectangle->height),
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_x),
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_y),
        static_cast<D2D1_INTERPOLATION_MODE>(
            properties->interpolation_mode)
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1ImageBrush> brush;
    HRESULT hr = surface->d2d_context->CreateImageBrush(
        reinterpret_cast<ID2D1Image*>(image),
        &native_properties,
        &native_brush_properties,
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_command_list(
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
    ComPtr<ID2D1CommandList> command_list;
    HRESULT hr = surface->d2d_context->CreateCommandList(
        command_list.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(command_list, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_effect(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_guid* effect_id,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect_id == nullptr || is_empty(*effect_id) ||
        value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    GUID native_effect_id = to_native_guid(*effect_id);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> effect;
    HRESULT hr = surface->d2d_context->CreateEffect(
        native_effect_id,
        effect.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(effect, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_set_input(
    progpu_native_direct2d_surface* surface,
    void* effect,
    uint32_t input_index,
    void* image,
    uint32_t invalidate,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || invalidate > 1U ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    if (SUCCEEDED(hr) && input_index >= native_effect->GetInputCount()) {
        hr = E_INVALIDARG;
    }
    ComPtr<ID2D1Image> native_image;
    if (SUCCEEDED(hr) && image != nullptr) {
        hr = reinterpret_cast<IUnknown*>(image)->QueryInterface(
            IID_PPV_ARGS(&native_image));
    }
    if (SUCCEEDED(hr)) {
        native_effect->SetInput(
            input_index,
            native_image.Get(),
            invalidate != 0U ? TRUE : FALSE);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_set_input_effect(
    progpu_native_direct2d_surface* surface,
    void* effect,
    uint32_t input_index,
    void* input_effect,
    uint32_t invalidate,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || input_effect == nullptr ||
        invalidate > 1U || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    if (SUCCEEDED(hr) && input_index >= native_effect->GetInputCount()) {
        hr = E_INVALIDARG;
    }
    ComPtr<ID2D1Effect> native_input_effect;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(input_effect)->QueryInterface(
            IID_PPV_ARGS(&native_input_effect));
    }
    if (SUCCEEDED(hr)) {
        native_effect->SetInputEffect(
            input_index,
            native_input_effect.Get(),
            invalidate != 0U ? TRUE : FALSE);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_set_value(
    progpu_native_direct2d_surface* surface,
    void* effect,
    uint32_t property_index,
    progpu_native_direct2d_effect_property_type property_type,
    const void* data,
    uint32_t data_size,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || data == nullptr ||
        native_hresult == nullptr ||
        !is_valid_effect_property(property_type, data_size)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    if (SUCCEEDED(hr) && property_index >= native_effect->GetPropertyCount()) {
        hr = E_INVALIDARG;
    }
    if (SUCCEEDED(hr)) {
        hr = native_effect->SetValue(
            property_index,
            static_cast<D2D1_PROPERTY_TYPE>(property_type),
            static_cast<const BYTE*>(data),
            data_size);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_get_output(
    progpu_native_direct2d_surface* surface,
    void* effect,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || value == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    ComPtr<ID2D1Image> output;
    if (SUCCEEDED(hr)) {
        native_effect->GetOutput(output.GetAddressOf());
        if (!output) {
            hr = E_UNEXPECTED;
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(output, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_layer(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_size_f* size,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr ||
        (size != nullptr &&
         (!std::isfinite(size->width) || !std::isfinite(size->height) ||
          size->width <= 0.0F || size->height <= 0.0F))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_SIZE_F native_size{};
    const D2D1_SIZE_F* native_size_pointer = nullptr;
    if (size != nullptr) {
        native_size = {size->width, size->height};
        native_size_pointer = &native_size;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Layer> layer;
    HRESULT hr = surface->d2d_context->CreateLayer(
        native_size_pointer,
        layer.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(layer, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_drawing_state_block(
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
    ComPtr<ID2D1Factory1> factory;
    HRESULT hr = surface->d2d_factory.As(&factory);
    ComPtr<ID2D1DrawingStateBlock1> drawing_state_block;
    if (SUCCEEDED(hr)) {
        hr = factory->CreateDrawingStateBlock(
            drawing_state_block.GetAddressOf());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(drawing_state_block, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_save_drawing_state(
    progpu_native_direct2d_surface* surface,
    void* drawing_state_block,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || drawing_state_block == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1DrawingStateBlock1> native_state;
    HRESULT hr = reinterpret_cast<IUnknown*>(drawing_state_block)->QueryInterface(
        IID_PPV_ARGS(&native_state));
    if (SUCCEEDED(hr)) {
        surface->d2d_context->SaveDrawingState(native_state.Get());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_restore_drawing_state(
    progpu_native_direct2d_surface* surface,
    void* drawing_state_block,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || drawing_state_block == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1DrawingStateBlock1> native_state;
    HRESULT hr = reinterpret_cast<IUnknown*>(drawing_state_block)->QueryInterface(
        IID_PPV_ARGS(&native_state));
    if (SUCCEEDED(hr)) {
        surface->d2d_context->RestoreDrawingState(native_state.Get());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_push_layer(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_layer_parameters* parameters,
    void* geometric_mask,
    void* opacity_brush,
    void* layer,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || parameters == nullptr || layer == nullptr ||
        native_hresult == nullptr || !is_valid(parameters->content_bounds) ||
        !is_valid(static_cast<progpu_native_direct2d_antialias_mode>(
            parameters->mask_antialias_mode)) ||
        !is_finite(parameters->mask_transform) ||
        !std::isfinite(parameters->opacity) || parameters->opacity < 0.0F ||
        parameters->opacity > 1.0F ||
        !is_valid_layer_options(parameters->options)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }

    ComPtr<ID2D1Layer> native_layer;
    HRESULT hr = reinterpret_cast<IUnknown*>(layer)->QueryInterface(
        IID_PPV_ARGS(&native_layer));
    ComPtr<ID2D1Geometry> native_mask;
    if (SUCCEEDED(hr) && geometric_mask != nullptr) {
        hr = reinterpret_cast<IUnknown*>(geometric_mask)->QueryInterface(
            IID_PPV_ARGS(&native_mask));
    }
    ComPtr<ID2D1Brush> native_opacity_brush;
    if (SUCCEEDED(hr) && opacity_brush != nullptr) {
        hr = reinterpret_cast<IUnknown*>(opacity_brush)->QueryInterface(
            IID_PPV_ARGS(&native_opacity_brush));
    }
    if (SUCCEEDED(hr)) {
        D2D1_LAYER_PARAMETERS1 native_parameters{};
        native_parameters.contentBounds = D2D1::RectF(
            parameters->content_bounds.x,
            parameters->content_bounds.y,
            parameters->content_bounds.x +
                parameters->content_bounds.width,
            parameters->content_bounds.y +
                parameters->content_bounds.height);
        native_parameters.geometricMask = native_mask.Get();
        native_parameters.maskAntialiasMode =
            static_cast<D2D1_ANTIALIAS_MODE>(
                parameters->mask_antialias_mode);
        native_parameters.maskTransform =
            to_native_matrix(parameters->mask_transform);
        native_parameters.opacity = parameters->opacity;
        native_parameters.opacityBrush = native_opacity_brush.Get();
        native_parameters.layerOptions =
            static_cast<D2D1_LAYER_OPTIONS1>(parameters->options);
        surface->d2d_context->PushLayer(
            native_parameters,
            native_layer.Get());
        ++surface->layer_depth;
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_pop_layer(
    progpu_native_direct2d_surface* surface,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    if (surface->layer_depth == 0U) {
        surface->last_hresult.store(
            D2DERR_WRONG_STATE,
            std::memory_order_release);
        *native_hresult = D2DERR_WRONG_STATE;
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    surface->d2d_context->PopLayer();
    --surface->layer_depth;
    surface->last_hresult.store(S_OK, std::memory_order_release);
    *native_hresult = S_OK;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_text_format(
    progpu_native_direct2d_surface* surface,
    const uint16_t* font_family,
    uint32_t font_family_length,
    const uint16_t* locale_name,
    uint32_t locale_name_length,
    const progpu_native_direct2d_text_format_properties* properties,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || font_family == nullptr ||
        font_family_length == 0U || locale_name == nullptr ||
        locale_name_length == 0U || properties == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_valid_text_format(*properties) ||
        contains_null(font_family, font_family_length) ||
        contains_null(locale_name, locale_name_length)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    try {
        std::wstring family(
            reinterpret_cast<const wchar_t*>(font_family),
            font_family_length);
        std::wstring locale(
            reinterpret_cast<const wchar_t*>(locale_name),
            locale_name_length);
        std::scoped_lock lock(surface->access_mutex);
        ComPtr<IDWriteTextFormat> base_format;
        HRESULT hr = surface->dwrite_factory->CreateTextFormat(
            family.c_str(),
            nullptr,
            static_cast<DWRITE_FONT_WEIGHT>(properties->font_weight),
            static_cast<DWRITE_FONT_STYLE>(properties->font_style),
            static_cast<DWRITE_FONT_STRETCH>(properties->font_stretch),
            properties->font_size,
            locale.c_str(),
            &base_format);
        if (SUCCEEDED(hr)) {
            hr = base_format->SetTextAlignment(
                static_cast<DWRITE_TEXT_ALIGNMENT>(
                    properties->text_alignment));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetParagraphAlignment(
                static_cast<DWRITE_PARAGRAPH_ALIGNMENT>(
                    properties->paragraph_alignment));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetWordWrapping(
                static_cast<DWRITE_WORD_WRAPPING>(
                    properties->word_wrapping));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetReadingDirection(
                static_cast<DWRITE_READING_DIRECTION>(
                    properties->reading_direction));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetFlowDirection(
                static_cast<DWRITE_FLOW_DIRECTION>(
                    properties->flow_direction));
        }
        if (SUCCEEDED(hr) && properties->incremental_tab_stop > 0.0F) {
            hr = base_format->SetIncrementalTabStop(
                properties->incremental_tab_stop);
        }
        ComPtr<IDWriteTextFormat1> format;
        if (SUCCEEDED(hr)) {
            hr = base_format.As(&format);
        }
        surface->last_hresult.store(hr, std::memory_order_release);
        *native_hresult = hr;
        if (FAILED(hr)) {
            return status_from_win2d_hresult(hr);
        }
        return return_interface(format, value);
    } catch (const std::bad_alloc&) {
        std::scoped_lock lock(surface->access_mutex);
        surface->last_hresult.store(E_OUTOFMEMORY, std::memory_order_release);
        *native_hresult = E_OUTOFMEMORY;
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_text(
    progpu_native_direct2d_surface* surface,
    const uint16_t* text,
    uint32_t text_length,
    void* text_format,
    const progpu_native_direct2d_rect_f* layout_rectangle,
    void* default_fill_brush,
    uint32_t options,
    progpu_native_direct2d_measuring_mode measuring_mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || (text == nullptr && text_length != 0U) ||
        text_format == nullptr || layout_rectangle == nullptr ||
        default_fill_brush == nullptr || native_hresult == nullptr ||
        !is_valid(*layout_rectangle) ||
        !is_valid_draw_text_options(options) || !is_valid(measuring_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<IDWriteTextFormat> format;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_format)->QueryInterface(
        IID_PPV_ARGS(&format));
    ComPtr<ID2D1Brush> brush;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(default_fill_brush)->QueryInterface(
            IID_PPV_ARGS(&brush));
    }
    if (SUCCEEDED(hr) && text_length != 0U) {
        D2D1_RECT_F native_rectangle = D2D1::RectF(
            layout_rectangle->x,
            layout_rectangle->y,
            layout_rectangle->x + layout_rectangle->width,
            layout_rectangle->y + layout_rectangle->height);
        surface->d2d_context->DrawText(
            reinterpret_cast<const wchar_t*>(text),
            text_length,
            format.Get(),
            native_rectangle,
            brush.Get(),
            static_cast<D2D1_DRAW_TEXT_OPTIONS>(options),
            static_cast<DWRITE_MEASURING_MODE>(measuring_mode));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_text_layout(
    progpu_native_direct2d_surface* surface,
    const uint16_t* text,
    uint32_t text_length,
    void* text_format,
    float maximum_width,
    float maximum_height,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || (text == nullptr && text_length != 0U) ||
        text_format == nullptr || value == nullptr || native_hresult == nullptr ||
        !std::isfinite(maximum_width) || maximum_width <= 0.0F ||
        !std::isfinite(maximum_height) || maximum_height <= 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    static constexpr wchar_t empty_text[] = L"";
    const wchar_t* native_text = text_length == 0U
        ? empty_text
        : reinterpret_cast<const wchar_t*>(text);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IDWriteTextFormat> format;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_format)->QueryInterface(
        IID_PPV_ARGS(&format));
    ComPtr<IDWriteTextLayout> base_layout;
    if (SUCCEEDED(hr)) {
        hr = surface->dwrite_factory->CreateTextLayout(
            native_text,
            text_length,
            format.Get(),
            maximum_width,
            maximum_height,
            &base_layout);
    }
    ComPtr<IDWriteTextLayout4> layout;
    if (SUCCEEDED(hr)) {
        hr = base_layout.As(&layout);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(layout, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_text_layout_set_range_format(
    progpu_native_direct2d_surface* surface,
    void* text_layout,
    const progpu_native_direct2d_text_range_format* formatting,
    void* drawing_effect_brush,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || text_layout == nullptr || formatting == nullptr ||
        native_hresult == nullptr ||
        !is_valid_text_range_format(*formatting, drawing_effect_brush)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IDWriteTextLayout> layout;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_layout)->QueryInterface(
        IID_PPV_ARGS(&layout));
    ComPtr<ID2D1Brush> brush;
    if (SUCCEEDED(hr) && drawing_effect_brush != nullptr) {
        hr = reinterpret_cast<IUnknown*>(drawing_effect_brush)->QueryInterface(
            IID_PPV_ARGS(&brush));
    }
    const DWRITE_TEXT_RANGE range = {
        formatting->range_start,
        formatting->range_length};
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_SIZE) != 0U) {
        hr = layout->SetFontSize(formatting->font_size, range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_WEIGHT) != 0U) {
        hr = layout->SetFontWeight(
            static_cast<DWRITE_FONT_WEIGHT>(formatting->font_weight),
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STYLE) != 0U) {
        hr = layout->SetFontStyle(
            static_cast<DWRITE_FONT_STYLE>(formatting->font_style),
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STRETCH) != 0U) {
        hr = layout->SetFontStretch(
            static_cast<DWRITE_FONT_STRETCH>(formatting->font_stretch),
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_UNDERLINE) != 0U) {
        hr = layout->SetUnderline(formatting->underline != 0U, range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_STRIKETHROUGH) != 0U) {
        hr = layout->SetStrikethrough(
            formatting->strikethrough != 0U,
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_DRAWING_EFFECT) != 0U) {
        hr = layout->SetDrawingEffect(brush.Get(), range);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_text_layout(
    progpu_native_direct2d_surface* surface,
    float origin_x,
    float origin_y,
    void* text_layout,
    void* default_fill_brush,
    uint32_t options,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || !std::isfinite(origin_x) ||
        !std::isfinite(origin_y) || text_layout == nullptr ||
        default_fill_brush == nullptr || native_hresult == nullptr ||
        !is_valid_draw_text_options(options)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<IDWriteTextLayout> layout;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_layout)->QueryInterface(
        IID_PPV_ARGS(&layout));
    ComPtr<ID2D1Brush> brush;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(default_fill_brush)->QueryInterface(
            IID_PPV_ARGS(&brush));
    }
    if (SUCCEEDED(hr)) {
        surface->d2d_context->DrawTextLayout(
            D2D1::Point2F(origin_x, origin_y),
            layout.Get(),
            brush.Get(),
            static_cast<D2D1_DRAW_TEXT_OPTIONS>(options));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_rectangle_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(rectangle->x) ||
        !std::isfinite(rectangle->y) || !std::isfinite(rectangle->width) ||
        !std::isfinite(rectangle->height) || rectangle->width < 0.0F ||
        rectangle->height < 0.0F ||
        !std::isfinite(rectangle->x + rectangle->width) ||
        !std::isfinite(rectangle->y + rectangle->height)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_RECT_F native_rectangle = {
        rectangle->x,
        rectangle->y,
        rectangle->x + rectangle->width,
        rectangle->y + rectangle->height
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RectangleGeometry> geometry;
    HRESULT hr = surface->d2d_factory->CreateRectangleGeometry(
        native_rectangle,
        geometry.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(geometry, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_rounded_rectangle_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    float radius_x,
    float radius_y,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(rectangle->x) ||
        !std::isfinite(rectangle->y) || !std::isfinite(rectangle->width) ||
        !std::isfinite(rectangle->height) || rectangle->width < 0.0F ||
        rectangle->height < 0.0F || !std::isfinite(radius_x) ||
        !std::isfinite(radius_y) || radius_x < 0.0F || radius_y < 0.0F ||
        !std::isfinite(rectangle->x + rectangle->width) ||
        !std::isfinite(rectangle->y + rectangle->height)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_ROUNDED_RECT native_rectangle = {
        {
            rectangle->x,
            rectangle->y,
            rectangle->x + rectangle->width,
            rectangle->y + rectangle->height
        },
        radius_x,
        radius_y
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RoundedRectangleGeometry> geometry;
    HRESULT hr = surface->d2d_factory->CreateRoundedRectangleGeometry(
        native_rectangle,
        geometry.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(geometry, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_ellipse_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_point_2f* center,
    float radius_x,
    float radius_y,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || center == nullptr || value == nullptr ||
        native_hresult == nullptr || !is_finite(*center) ||
        !std::isfinite(radius_x) || !std::isfinite(radius_y) ||
        radius_x < 0.0F || radius_y < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_ELLIPSE native_ellipse = {
        {center->x, center->y},
        radius_x,
        radius_y
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1EllipseGeometry> geometry;
    HRESULT hr = surface->d2d_factory->CreateEllipseGeometry(
        native_ellipse,
        geometry.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(geometry, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_path_geometry(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_fill_mode fill_mode,
    const progpu_native_direct2d_path_figure* figures,
    uint32_t figure_count,
    const progpu_native_direct2d_path_segment* segments,
    uint32_t segment_count,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr ||
        (figure_count != 0U && figures == nullptr) ||
        (segment_count != 0U && segments == nullptr) ||
        (fill_mode != PROGPU_NATIVE_DIRECT2D_FILL_MODE_ALTERNATE &&
         fill_mode != PROGPU_NATIVE_DIRECT2D_FILL_MODE_WINDING)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    uint32_t expected_segment = 0U;
    for (uint32_t index = 0U; index < figure_count; ++index) {
        const auto& figure = figures[index];
        if (!is_finite(figure.start_point) || figure.reserved != 0U ||
            (figure.flags &
             ~(PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_FILLED |
               PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_CLOSED)) != 0U ||
            figure.first_segment != expected_segment ||
            figure.segment_count > segment_count - expected_segment) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
        expected_segment += figure.segment_count;
    }
    if (expected_segment != segment_count) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0U; index < segment_count; ++index) {
        if (!is_geometry_path_segment_valid(segments[index])) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1PathGeometry1> path;
    HRESULT hr = create_path_geometry(*surface, path);
    ComPtr<ID2D1GeometrySink> sink;
    if (SUCCEEDED(hr)) {
        hr = path->Open(&sink);
    }
    if (SUCCEEDED(hr)) {
        sink->SetFillMode(static_cast<D2D1_FILL_MODE>(fill_mode));
        for (uint32_t figure_index = 0U;
             figure_index < figure_count;
             ++figure_index) {
            const auto& figure = figures[figure_index];
            sink->BeginFigure(
                {figure.start_point.x, figure.start_point.y},
                (figure.flags &
                 PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_FILLED) != 0U
                    ? D2D1_FIGURE_BEGIN_FILLED
                    : D2D1_FIGURE_BEGIN_HOLLOW);
            for (uint32_t offset = 0U;
                 offset < figure.segment_count;
                 ++offset) {
                const auto& segment =
                    segments[figure.first_segment + offset];
                sink->SetSegmentFlags(
                    static_cast<D2D1_PATH_SEGMENT>(segment.flags));
                switch (segment.kind) {
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_LINE:
                        sink->AddLine({segment.point1.x, segment.point1.y});
                        break;
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_QUADRATIC:
                        sink->AddQuadraticBezier({
                            {segment.point1.x, segment.point1.y},
                            {segment.point2.x, segment.point2.y}
                        });
                        break;
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_CUBIC:
                        sink->AddBezier({
                            {segment.point1.x, segment.point1.y},
                            {segment.point2.x, segment.point2.y},
                            {segment.point3.x, segment.point3.y}
                        });
                        break;
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC:
                        sink->AddArc({
                            {segment.point1.x, segment.point1.y},
                            {segment.size.x, segment.size.y},
                            segment.rotation_angle,
                            (segment.arc_flags &
                             PROGPU_NATIVE_DIRECT2D_ARC_FLAG_CLOCKWISE) != 0U
                                ? D2D1_SWEEP_DIRECTION_CLOCKWISE
                                : D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE,
                            (segment.arc_flags &
                             PROGPU_NATIVE_DIRECT2D_ARC_FLAG_LARGE) != 0U
                                ? D2D1_ARC_SIZE_LARGE
                                : D2D1_ARC_SIZE_SMALL
                        });
                        break;
                    default:
                        hr = E_INVALIDARG;
                        break;
                }
                if (FAILED(hr)) {
                    break;
                }
            }
            sink->EndFigure(
                (figure.flags &
                 PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_CLOSED) != 0U
                    ? D2D1_FIGURE_END_CLOSED
                    : D2D1_FIGURE_END_OPEN);
            if (FAILED(hr)) {
                break;
            }
        }
        HRESULT close_hr = sink->Close();
        if (SUCCEEDED(hr)) {
            hr = close_hr;
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(path, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_transformed_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || transform == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_finite(*transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform = to_native_matrix(*transform);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1TransformedGeometry> result;
    HRESULT hr = surface->d2d_factory->CreateTransformedGeometry(
        reinterpret_cast<ID2D1Geometry*>(geometry),
        native_transform,
        result.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(result, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_combine_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry_a,
    void* geometry_b,
    progpu_native_direct2d_combine_mode combine_mode,
    const progpu_native_direct2d_matrix_3x2_f* geometry_b_transform,
    float flattening_tolerance,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry_a == nullptr || geometry_b == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        combine_mode < PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_UNION ||
        combine_mode > PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_EXCLUDE ||
        !std::isfinite(flattening_tolerance) || flattening_tolerance <= 0.0F ||
        (geometry_b_transform != nullptr &&
         !is_finite(*geometry_b_transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* native_transform_pointer = nullptr;
    if (geometry_b_transform != nullptr) {
        native_transform = to_native_matrix(*geometry_b_transform);
        native_transform_pointer = &native_transform;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1PathGeometry1> path;
    HRESULT hr = create_path_geometry(*surface, path);
    ComPtr<ID2D1GeometrySink> sink;
    if (SUCCEEDED(hr)) {
        hr = path->Open(&sink);
    }
    if (SUCCEEDED(hr)) {
        HRESULT combine_hr =
            reinterpret_cast<ID2D1Geometry*>(geometry_a)->CombineWithGeometry(
                reinterpret_cast<ID2D1Geometry*>(geometry_b),
                static_cast<D2D1_COMBINE_MODE>(combine_mode),
                native_transform_pointer,
                flattening_tolerance,
                sink.Get());
        HRESULT close_hr = sink->Close();
        hr = FAILED(combine_hr) ? combine_hr : close_hr;
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(path, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_stroke_style(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_stroke_style_properties* properties,
    const float* dashes,
    uint32_t dash_count,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || properties == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !is_valid_cap_style(properties->start_cap) ||
        !is_valid_cap_style(properties->end_cap) ||
        !is_valid_cap_style(properties->dash_cap) ||
        !is_valid_line_join(properties->line_join) ||
        !std::isfinite(properties->miter_limit) ||
        properties->miter_limit <= 0.0F ||
        !is_valid_dash_style(properties->dash_style) ||
        !std::isfinite(properties->dash_offset) ||
        !is_valid_stroke_transform_type(properties->transform_type) ||
        ((properties->dash_style ==
              PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM) !=
            (dashes != nullptr && dash_count != 0U)) ||
        (properties->dash_style !=
             PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM &&
         (dashes != nullptr || dash_count != 0U))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    bool has_positive_dash = false;
    for (uint32_t index = 0U; index < dash_count; ++index) {
        if (!std::isfinite(dashes[index]) || dashes[index] < 0.0F) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
        has_positive_dash = has_positive_dash || dashes[index] > 0.0F;
    }
    if (dash_count != 0U && !has_positive_dash) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_STROKE_STYLE_PROPERTIES1 native_properties{};
    native_properties.startCap =
        static_cast<D2D1_CAP_STYLE>(properties->start_cap);
    native_properties.endCap =
        static_cast<D2D1_CAP_STYLE>(properties->end_cap);
    native_properties.dashCap =
        static_cast<D2D1_CAP_STYLE>(properties->dash_cap);
    native_properties.lineJoin =
        static_cast<D2D1_LINE_JOIN>(properties->line_join);
    native_properties.miterLimit = properties->miter_limit;
    native_properties.dashStyle =
        static_cast<D2D1_DASH_STYLE>(properties->dash_style);
    native_properties.dashOffset = properties->dash_offset;
    native_properties.transformType =
        static_cast<D2D1_STROKE_TRANSFORM_TYPE>(properties->transform_type);

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Factory1> factory;
    HRESULT hr = surface->d2d_factory.As(&factory);
    ComPtr<ID2D1StrokeStyle1> stroke_style;
    if (SUCCEEDED(hr)) {
        hr = factory->CreateStrokeStyle(
            native_properties,
            dashes,
            dash_count,
            stroke_style.GetAddressOf());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(stroke_style, value);
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
    surface->command_list_draw_active = false;
    surface->layer_depth = 0U;
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
    if (!surface->draw_active || surface->command_list_draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    bool unbalanced_layers = surface->layer_depth != 0U;
    while (surface->layer_depth != 0U) {
        surface->d2d_context->PopLayer();
        --surface->layer_depth;
    }
    D2D1_TAG native_tag1 = 0U;
    D2D1_TAG native_tag2 = 0U;
    HRESULT draw_hr = surface->d2d_context->EndDraw(
        &native_tag1,
        &native_tag2);
    if (unbalanced_layers && SUCCEEDED(draw_hr)) {
        draw_hr = D2DERR_WRONG_STATE;
    }
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
    if (unbalanced_layers) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    if (draw_hr == D2DERR_RECREATE_TARGET ||
        draw_hr == DXGI_ERROR_DEVICE_REMOVED ||
        draw_hr == DXGI_ERROR_DEVICE_RESET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_FAILED;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_begin_command_list_draw(
    progpu_native_direct2d_surface* surface,
    void* command_list)
{
    if (surface == nullptr || command_list == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }
    if (surface->access_acquired) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_ALREADY_ACQUIRED;
    }

    ID2D1CommandList* native_command_list =
        reinterpret_cast<ID2D1CommandList*>(command_list);
    native_command_list->AddRef();
    surface->active_command_list.Attach(native_command_list);
    surface->d2d_context->SetTarget(surface->active_command_list.Get());
    surface->d2d_context->BeginDraw();
    surface->draw_active = true;
    surface->command_list_draw_active = true;
    surface->layer_depth = 0U;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_end_command_list_draw(
    progpu_native_direct2d_surface* surface,
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
    if (!surface->draw_active || !surface->command_list_draw_active ||
        !surface->active_command_list) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }

    bool unbalanced_layers = surface->layer_depth != 0U;
    while (surface->layer_depth != 0U) {
        surface->d2d_context->PopLayer();
        --surface->layer_depth;
    }
    D2D1_TAG native_tag1 = 0U;
    D2D1_TAG native_tag2 = 0U;
    HRESULT draw_hr = surface->d2d_context->EndDraw(
        &native_tag1,
        &native_tag2);
    surface->d2d_context->SetTarget(surface->d2d_bitmap.Get());
    HRESULT close_hr = SUCCEEDED(draw_hr)
        ? surface->active_command_list->Close()
        : draw_hr;
    HRESULT result_hr = unbalanced_layers && SUCCEEDED(close_hr)
        ? D2DERR_WRONG_STATE
        : close_hr;
    surface->active_command_list.Reset();
    surface->draw_active = false;
    surface->command_list_draw_active = false;
    *tag1 = native_tag1;
    *tag2 = native_tag2;
    *native_hresult = result_hr;
    surface->last_hresult.store(result_hr, std::memory_order_release);

    if (SUCCEEDED(result_hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    if (unbalanced_layers) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    if (result_hr == D2DERR_RECREATE_TARGET ||
        result_hr == DXGI_ERROR_DEVICE_REMOVED ||
        result_hr == DXGI_ERROR_DEVICE_RESET) {
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
