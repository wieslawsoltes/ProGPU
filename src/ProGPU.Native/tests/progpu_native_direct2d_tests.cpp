#include "progpu_native_direct2d.h"

#include <d2d1_2.h>
#include <d2d1effects.h>
#include <d3d11_1.h>
#include <dwrite_3.h>
#include <dxgi1_2.h>
#include <roapi.h>
#include <windows.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <wrl/client.h>

#include <cstdlib>
#include <iostream>

using Microsoft::WRL::ComPtr;
using Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;

namespace {

constexpr GUID gaussian_blur_effect_id = {
    0x1feb6d69,
    0x2fe6,
    0x4ac9,
    {0x8c, 0x58, 0x1d, 0x7f, 0x93, 0xe7, 0xa6, 0xa5}
};

constexpr GUID shadow_effect_id = {
    0xc67ea361,
    0x1863,
    0x4e69,
    {0x89, 0xdb, 0x69, 0x5d, 0x3e, 0x9a, 0x5b, 0x6b}
};

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

bool has_same_com_identity(IUnknown* left, IUnknown* right)
{
    ComPtr<IUnknown> left_identity;
    ComPtr<IUnknown> right_identity;
    return left != nullptr && right != nullptr &&
        SUCCEEDED(left->QueryInterface(IID_PPV_ARGS(&left_identity))) &&
        SUCCEEDED(right->QueryInterface(IID_PPV_ARGS(&right_identity))) &&
        left_identity.Get() == right_identity.Get();
}

} // namespace

int main()
{
    HRESULT runtime_initialization = RoInitialize(RO_INIT_MULTITHREADED);
    require(SUCCEEDED(runtime_initialization),
        "Windows Runtime initialization failed");

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
    auto dwrite_factory = get_interface<IDWriteFactory3>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_DWRITE_FACTORY3);

    require(factory1 && factory2 && device && context && bitmap &&
            base_context && d3d_device && texture && winrt_d3d_device &&
            dwrite_factory,
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

    progpu_native_direct2d_color_f solid_color = {
        224.0F / 255.0F,
        48.0F / 255.0F,
        96.0F / 255.0F,
        1.0F
    };
    void* solid_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_solid_color_brush(
            surface,
            &solid_color,
            &solid_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            solid_brush_value != nullptr && native_hresult == S_OK,
        "provider ID2D1SolidColorBrush creation failed");
    ComPtr<ID2D1SolidColorBrush> solid_brush;
    solid_brush.Attach(
        static_cast<ID2D1SolidColorBrush*>(solid_brush_value));
    D2D1_COLOR_F created_color = solid_brush->GetColor();
    require(
        created_color.r == solid_color.red &&
        created_color.g == solid_color.green &&
        created_color.b == solid_color.blue &&
        created_color.a == solid_color.alpha,
        "provider ID2D1SolidColorBrush changed its color");

    void* invalid_brush_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_solid_color_brush(
            surface,
            nullptr,
            &invalid_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_brush_value == nullptr && native_hresult == E_INVALIDARG,
        "invalid solid-brush creation did not fail closed");

    progpu_native_direct2d_gradient_stop gradient_stops[] = {
        {
            0.0F,
            {32.0F / 255.0F, 160.0F / 255.0F, 224.0F / 255.0F, 1.0F}
        },
        {
            1.0F,
            {224.0F / 255.0F, 96.0F / 255.0F, 32.0F / 255.0F, 1.0F}
        }
    };
    void* gradient_collection_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_gradient_stop_collection(
            surface,
            gradient_stops,
            2U,
            PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SRGB,
            PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SRGB,
            PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_8BPC_UNORM,
            PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP,
            PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_PREMULTIPLIED,
            &gradient_collection_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            gradient_collection_value != nullptr && native_hresult == S_OK,
        "provider ID2D1GradientStopCollection1 creation failed");
    ComPtr<ID2D1GradientStopCollection1> gradient_collection;
    gradient_collection.Attach(
        static_cast<ID2D1GradientStopCollection1*>(
            gradient_collection_value));
    require(gradient_collection->GetGradientStopCount() == 2U &&
            gradient_collection->GetPreInterpolationSpace() ==
                D2D1_COLOR_SPACE_SRGB &&
            gradient_collection->GetPostInterpolationSpace() ==
                D2D1_COLOR_SPACE_SRGB &&
            gradient_collection->GetBufferPrecision() ==
                D2D1_BUFFER_PRECISION_8BPC_UNORM &&
            gradient_collection->GetExtendMode() == D2D1_EXTEND_MODE_CLAMP &&
            gradient_collection->GetColorInterpolationMode() ==
                D2D1_COLOR_INTERPOLATION_MODE_PREMULTIPLIED,
        "provider gradient-stop collection metadata changed");
    D2D1_GRADIENT_STOP returned_stops[2]{};
    gradient_collection->GetGradientStops1(returned_stops, 2U);
    require(returned_stops[0].position == gradient_stops[0].position &&
            returned_stops[1].position == gradient_stops[1].position &&
            returned_stops[0].color.g == gradient_stops[0].color.green &&
            returned_stops[1].color.r == gradient_stops[1].color.red,
        "provider gradient-stop collection changed its stops");

    progpu_native_direct2d_brush_properties gradient_brush_properties{};
    gradient_brush_properties.opacity = 0.75F;
    gradient_brush_properties.transform.m11 = 1.0F;
    gradient_brush_properties.transform.m22 = 1.0F;
    progpu_native_direct2d_linear_gradient_brush_properties linear_properties{
        {0.0F, 0.0F},
        {64.0F, 0.0F}
    };
    void* linear_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_linear_gradient_brush(
            surface,
            &linear_properties,
            &gradient_brush_properties,
            gradient_collection.Get(),
            &linear_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            linear_brush_value != nullptr && native_hresult == S_OK,
        "provider ID2D1LinearGradientBrush creation failed");
    ComPtr<ID2D1LinearGradientBrush> linear_brush;
    linear_brush.Attach(
        static_cast<ID2D1LinearGradientBrush*>(linear_brush_value));
    require(linear_brush->GetStartPoint().x == 0.0F &&
            linear_brush->GetEndPoint().x == 64.0F &&
            linear_brush->GetOpacity() == 0.75F,
        "provider linear-gradient brush properties changed");

    progpu_native_direct2d_radial_gradient_brush_properties radial_properties{
        {32.0F, 24.0F},
        {2.0F, 3.0F},
        20.0F,
        16.0F
    };
    void* radial_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_radial_gradient_brush(
            surface,
            &radial_properties,
            &gradient_brush_properties,
            gradient_collection.Get(),
            &radial_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            radial_brush_value != nullptr && native_hresult == S_OK,
        "provider ID2D1RadialGradientBrush creation failed");
    ComPtr<ID2D1RadialGradientBrush> radial_brush;
    radial_brush.Attach(
        static_cast<ID2D1RadialGradientBrush*>(radial_brush_value));
    require(radial_brush->GetCenter().x == 32.0F &&
            radial_brush->GetGradientOriginOffset().y == 3.0F &&
            radial_brush->GetRadiusX() == 20.0F &&
            radial_brush->GetRadiusY() == 16.0F &&
            radial_brush->GetOpacity() == 0.75F,
        "provider radial-gradient brush properties changed");

    progpu_native_direct2d_rect_f rectangle_value{
        4.0F,
        4.0F,
        16.0F,
        12.0F
    };
    void* rectangle_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_rectangle_geometry(
            surface,
            &rectangle_value,
            &rectangle_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            rectangle_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1RectangleGeometry creation failed");
    ComPtr<ID2D1RectangleGeometry> rectangle_geometry;
    rectangle_geometry.Attach(
        static_cast<ID2D1RectangleGeometry*>(rectangle_geometry_value));
    D2D1_RECT_F returned_rectangle{};
    rectangle_geometry->GetRect(&returned_rectangle);
    require(returned_rectangle.left == 4.0F &&
            returned_rectangle.top == 4.0F &&
            returned_rectangle.right == 20.0F &&
            returned_rectangle.bottom == 16.0F,
        "provider rectangle geometry changed its rectangle");

    void* rounded_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_rounded_rectangle_geometry(
            surface,
            &rectangle_value,
            3.0F,
            2.0F,
            &rounded_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            rounded_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1RoundedRectangleGeometry creation failed");
    ComPtr<ID2D1RoundedRectangleGeometry> rounded_geometry;
    rounded_geometry.Attach(
        static_cast<ID2D1RoundedRectangleGeometry*>(rounded_geometry_value));
    D2D1_ROUNDED_RECT returned_rounded{};
    rounded_geometry->GetRoundedRect(&returned_rounded);
    require(returned_rounded.radiusX == 3.0F &&
            returned_rounded.radiusY == 2.0F,
        "provider rounded-rectangle geometry changed its radii");

    progpu_native_direct2d_point_2f ellipse_center{32.0F, 24.0F};
    void* ellipse_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_ellipse_geometry(
            surface,
            &ellipse_center,
            8.0F,
            6.0F,
            &ellipse_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            ellipse_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1EllipseGeometry creation failed");
    ComPtr<ID2D1EllipseGeometry> ellipse_geometry;
    ellipse_geometry.Attach(
        static_cast<ID2D1EllipseGeometry*>(ellipse_geometry_value));
    D2D1_ELLIPSE returned_ellipse{};
    ellipse_geometry->GetEllipse(&returned_ellipse);
    require(returned_ellipse.point.x == 32.0F &&
            returned_ellipse.point.y == 24.0F &&
            returned_ellipse.radiusX == 8.0F &&
            returned_ellipse.radiusY == 6.0F,
        "provider ellipse geometry changed its values");

    progpu_native_direct2d_path_figure path_figure{};
    path_figure.start_point = {4.0F, 24.0F};
    path_figure.segment_count = 4U;
    path_figure.flags =
        PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_FILLED |
        PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_CLOSED;
    progpu_native_direct2d_path_segment path_segments[4]{};
    path_segments[0].kind = PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_LINE;
    path_segments[0].point1 = {20.0F, 24.0F};
    path_segments[1].kind =
        PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_QUADRATIC;
    path_segments[1].point1 = {24.0F, 28.0F};
    path_segments[1].point2 = {20.0F, 32.0F};
    path_segments[1].flags =
        PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_FORCE_ROUND_LINE_JOIN;
    path_segments[2].kind = PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_CUBIC;
    path_segments[2].point1 = {16.0F, 36.0F};
    path_segments[2].point2 = {8.0F, 36.0F};
    path_segments[2].point3 = {4.0F, 32.0F};
    path_segments[3].kind = PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC;
    path_segments[3].point1 = {4.0F, 24.0F};
    path_segments[3].size = {4.0F, 4.0F};
    path_segments[3].arc_flags =
        PROGPU_NATIVE_DIRECT2D_ARC_FLAG_CLOCKWISE;
    void* path_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_path_geometry(
            surface,
            PROGPU_NATIVE_DIRECT2D_FILL_MODE_WINDING,
            &path_figure,
            1U,
            path_segments,
            4U,
            &path_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            path_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1PathGeometry1 creation failed");
    ComPtr<ID2D1PathGeometry1> path_geometry;
    path_geometry.Attach(
        static_cast<ID2D1PathGeometry1*>(path_geometry_value));
    uint32_t returned_figure_count = 0U;
    uint32_t returned_segment_count = 0U;
    require(SUCCEEDED(path_geometry->GetFigureCount(
                &returned_figure_count)) &&
            SUCCEEDED(path_geometry->GetSegmentCount(
                &returned_segment_count)),
        "provider path geometry topology query failed");
    // Direct2D counts the implicit closing edge in addition to the four
    // explicitly submitted line/quadratic/cubic/arc segments.
    if (returned_figure_count != 1U || returned_segment_count != 5U) {
        std::cerr << "provider path geometry changed its topology: figures="
                  << returned_figure_count << ", segments="
                  << returned_segment_count << '\n';
        return EXIT_FAILURE;
    }

    progpu_native_direct2d_matrix_3x2_f geometry_transform{
        1.0F,
        0.0F,
        0.0F,
        1.0F,
        2.0F,
        3.0F
    };
    void* transformed_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_transformed_geometry(
            surface,
            path_geometry.Get(),
            &geometry_transform,
            &transformed_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            transformed_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1TransformedGeometry creation failed");
    ComPtr<ID2D1TransformedGeometry> transformed_geometry;
    transformed_geometry.Attach(
        static_cast<ID2D1TransformedGeometry*>(transformed_geometry_value));
    D2D1_MATRIX_3X2_F returned_transform{};
    transformed_geometry->GetTransform(&returned_transform);
    require(returned_transform._31 == 2.0F &&
            returned_transform._32 == 3.0F,
        "provider transformed geometry changed its transform");

    void* combined_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_combine_geometry(
            surface,
            rectangle_geometry.Get(),
            ellipse_geometry.Get(),
            PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_XOR,
            nullptr,
            0.25F,
            &combined_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            combined_geometry_value != nullptr && native_hresult == S_OK,
        "provider Direct2D geometry combination failed");
    ComPtr<ID2D1PathGeometry1> combined_geometry;
    combined_geometry.Attach(
        static_cast<ID2D1PathGeometry1*>(combined_geometry_value));
    uint32_t combined_figure_count = 0U;
    require(SUCCEEDED(combined_geometry->GetFigureCount(
                &combined_figure_count)) && combined_figure_count != 0U,
        "provider combined geometry was unexpectedly empty");

    progpu_native_direct2d_stroke_style_properties stroke_properties{};
    stroke_properties.start_cap = PROGPU_NATIVE_DIRECT2D_CAP_STYLE_ROUND;
    stroke_properties.end_cap = PROGPU_NATIVE_DIRECT2D_CAP_STYLE_TRIANGLE;
    stroke_properties.dash_cap = PROGPU_NATIVE_DIRECT2D_CAP_STYLE_SQUARE;
    stroke_properties.line_join = PROGPU_NATIVE_DIRECT2D_LINE_JOIN_BEVEL;
    stroke_properties.miter_limit = 6.0F;
    stroke_properties.dash_style = PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM;
    stroke_properties.dash_offset = 0.5F;
    stroke_properties.transform_type =
        PROGPU_NATIVE_DIRECT2D_STROKE_TRANSFORM_FIXED;
    float custom_dashes[] = {2.0F, 1.0F, 0.5F, 1.0F};
    void* stroke_style_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_stroke_style(
            surface,
            &stroke_properties,
            custom_dashes,
            4U,
            &stroke_style_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            stroke_style_value != nullptr && native_hresult == S_OK,
        "provider ID2D1StrokeStyle1 creation failed");
    ComPtr<ID2D1StrokeStyle1> stroke_style;
    stroke_style.Attach(
        static_cast<ID2D1StrokeStyle1*>(stroke_style_value));
    float returned_dashes[4]{};
    stroke_style->GetDashes(returned_dashes, 4U);
    require(stroke_style->GetStartCap() == D2D1_CAP_STYLE_ROUND &&
            stroke_style->GetEndCap() == D2D1_CAP_STYLE_TRIANGLE &&
            stroke_style->GetDashCap() == D2D1_CAP_STYLE_SQUARE &&
            stroke_style->GetLineJoin() == D2D1_LINE_JOIN_BEVEL &&
            stroke_style->GetMiterLimit() == 6.0F &&
            stroke_style->GetDashStyle() == D2D1_DASH_STYLE_CUSTOM &&
            stroke_style->GetDashOffset() == 0.5F &&
            stroke_style->GetStrokeTransformType() ==
                D2D1_STROKE_TRANSFORM_TYPE_FIXED &&
            stroke_style->GetDashesCount() == 4U &&
            returned_dashes[0] == 2.0F && returned_dashes[3] == 1.0F,
        "provider ID2D1StrokeStyle1 metadata changed");

    void* invalid_stroke_style_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_stroke_style(
            surface,
            &stroke_properties,
            nullptr,
            0U,
            &invalid_stroke_style_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_stroke_style_value == nullptr &&
            native_hresult == E_INVALIDARG,
        "custom stroke style without dashes did not fail closed");

    const uint8_t bitmap_pixels[] = {
        0U, 255U, 255U, 255U,
        255U, 255U, 0U, 255U,
        255U, 0U, 255U, 255U,
        255U, 255U, 255U, 255U
    };
    progpu_native_direct2d_bitmap_properties bitmap_properties{};
    bitmap_properties.width = 2U;
    bitmap_properties.height = 2U;
    bitmap_properties.stride = 8U;
    bitmap_properties.dpi_x = 120.0F;
    bitmap_properties.dpi_y = 144.0F;
    void* source_bitmap_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_bitmap(
            surface,
            &bitmap_properties,
            bitmap_pixels,
            sizeof(bitmap_pixels),
            &source_bitmap_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            source_bitmap_value != nullptr && native_hresult == S_OK,
        "provider ID2D1Bitmap1 upload failed");
    ComPtr<ID2D1Bitmap1> source_bitmap;
    source_bitmap.Attach(static_cast<ID2D1Bitmap1*>(source_bitmap_value));
    D2D1_SIZE_U source_pixel_size = source_bitmap->GetPixelSize();
    D2D1_PIXEL_FORMAT source_pixel_format = source_bitmap->GetPixelFormat();
    require(source_pixel_size.width == 2U && source_pixel_size.height == 2U &&
            source_pixel_format.format == DXGI_FORMAT_B8G8R8A8_UNORM &&
            source_pixel_format.alphaMode == D2D1_ALPHA_MODE_PREMULTIPLIED,
        "provider ID2D1Bitmap1 metadata changed");

    void* invalid_bitmap_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_bitmap(
            surface,
            &bitmap_properties,
            bitmap_pixels,
            sizeof(bitmap_pixels) - 1U,
            &invalid_bitmap_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_bitmap_value == nullptr && native_hresult == E_INVALIDARG,
        "truncated Direct2D bitmap upload did not fail closed");

    progpu_native_direct2d_bitmap_brush_properties bitmap_brush_properties{};
    bitmap_brush_properties.extend_mode_x =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP;
    bitmap_brush_properties.extend_mode_y =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_MIRROR;
    bitmap_brush_properties.interpolation_mode =
        PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_NEAREST_NEIGHBOR;
    progpu_native_direct2d_brush_properties bitmap_common_properties{};
    bitmap_common_properties.opacity = 0.625F;
    bitmap_common_properties.transform.m11 = 1.0F;
    bitmap_common_properties.transform.m22 = 1.0F;
    void* bitmap_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_bitmap_brush(
            surface,
            source_bitmap.Get(),
            &bitmap_brush_properties,
            &bitmap_common_properties,
            &bitmap_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            bitmap_brush_value != nullptr && native_hresult == S_OK,
        "provider ID2D1BitmapBrush1 creation failed");
    ComPtr<ID2D1BitmapBrush1> bitmap_brush;
    bitmap_brush.Attach(static_cast<ID2D1BitmapBrush1*>(bitmap_brush_value));
    ComPtr<ID2D1Bitmap> brush_bitmap;
    bitmap_brush->GetBitmap(brush_bitmap.GetAddressOf());
    require(bitmap_brush->GetExtendModeX() == D2D1_EXTEND_MODE_WRAP &&
            bitmap_brush->GetExtendModeY() == D2D1_EXTEND_MODE_MIRROR &&
            bitmap_brush->GetInterpolationMode1() ==
                D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR &&
            bitmap_brush->GetOpacity() == 0.625F &&
            has_same_com_identity(source_bitmap.Get(), brush_bitmap.Get()),
        "provider ID2D1BitmapBrush1 metadata changed");

    progpu_native_direct2d_image_brush_properties image_brush_properties{};
    image_brush_properties.source_rectangle = {0.25F, 0.5F, 1.5F, 1.0F};
    image_brush_properties.extend_mode_x =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_MIRROR;
    image_brush_properties.extend_mode_y =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP;
    image_brush_properties.interpolation_mode =
        PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_NEAREST_NEIGHBOR;
    progpu_native_direct2d_brush_properties image_common_properties{};
    image_common_properties.opacity = 0.75F;
    image_common_properties.transform.m11 = 1.0F;
    image_common_properties.transform.m22 = 1.0F;
    image_common_properties.transform.m31 = 2.0F;
    void* image_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_image_brush(
            surface,
            source_bitmap.Get(),
            &image_brush_properties,
            &image_common_properties,
            &image_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            image_brush_value != nullptr && native_hresult == S_OK,
        "provider ID2D1ImageBrush creation failed");
    ComPtr<ID2D1ImageBrush> image_brush;
    image_brush.Attach(static_cast<ID2D1ImageBrush*>(image_brush_value));
    ComPtr<ID2D1Image> brush_image;
    image_brush->GetImage(brush_image.GetAddressOf());
    D2D1_RECT_F image_source_rectangle{};
    image_brush->GetSourceRectangle(&image_source_rectangle);
    require(image_brush->GetExtendModeX() == D2D1_EXTEND_MODE_MIRROR &&
            image_brush->GetExtendModeY() == D2D1_EXTEND_MODE_WRAP &&
            image_brush->GetInterpolationMode() ==
                D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR &&
            image_brush->GetOpacity() == 0.75F &&
            image_source_rectangle.left == 0.25F &&
            image_source_rectangle.top == 0.5F &&
            image_source_rectangle.right == 1.75F &&
            image_source_rectangle.bottom == 1.5F &&
            has_same_com_identity(source_bitmap.Get(), brush_image.Get()),
        "provider ID2D1ImageBrush metadata changed");

    progpu_native_direct2d_image_brush_properties invalid_image_properties =
        image_brush_properties;
    invalid_image_properties.source_rectangle.width = 0.0F;
    void* invalid_image_brush_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_image_brush(
            surface,
            source_bitmap.Get(),
            &invalid_image_properties,
            &image_common_properties,
            &invalid_image_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_image_brush_value == nullptr &&
            native_hresult == E_INVALIDARG,
        "empty Direct2D image-brush source rectangle did not fail closed");

    void* command_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &command_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            command_list_value != nullptr && native_hresult == S_OK,
        "provider ID2D1CommandList creation failed");
    ComPtr<ID2D1CommandList> command_list;
    command_list.Attach(
        static_cast<ID2D1CommandList*>(command_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            command_list.Get()) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider ID2D1CommandList recording did not begin");
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            command_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE,
        "nested ID2D1CommandList recording did not fail closed");
    context->Clear(D2D1::ColorF(0.0F, 0.0F, 0.0F, 0.0F));
    context->FillRectangle(
        D2D1::RectF(0.0F, 0.0F, 16.0F, 16.0F),
        solid_brush.Get());
    uint64_t command_tag1 = 1U;
    uint64_t command_tag2 = 1U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_draw(
            surface,
            1U,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "command-list recording accepted the shared-target end operation");
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            command_tag1 == 0U && command_tag2 == 0U &&
            native_hresult == S_OK,
        "provider ID2D1CommandList recording did not close");
    ComPtr<ID2D1Image> restored_target;
    context->GetTarget(restored_target.GetAddressOf());
    require(has_same_com_identity(bitmap.Get(), restored_target.Get()),
        "command-list recording did not restore the shared bitmap target");
    progpu_native_direct2d_surface_descriptor command_descriptor{};
    command_descriptor.struct_size = sizeof(command_descriptor);
    require(
        progpu_native_direct2d_surface_get_descriptor(
            surface,
            &command_descriptor) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            command_descriptor.content_version == descriptor.content_version,
        "command-list recording changed shared-surface content version");

    progpu_native_direct2d_image_brush_properties
        command_list_brush_properties{};
    command_list_brush_properties.source_rectangle =
        {0.0F, 0.0F, 16.0F, 16.0F};
    command_list_brush_properties.extend_mode_x =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP;
    command_list_brush_properties.extend_mode_y =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP;
    command_list_brush_properties.interpolation_mode =
        PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_LINEAR;
    progpu_native_direct2d_brush_properties
        command_list_common_properties{};
    command_list_common_properties.opacity = 1.0F;
    command_list_common_properties.transform.m11 = 1.0F;
    command_list_common_properties.transform.m22 = 1.0F;
    void* command_list_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_image_brush(
            surface,
            command_list.Get(),
            &command_list_brush_properties,
            &command_list_common_properties,
            &command_list_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            command_list_brush_value != nullptr && native_hresult == S_OK,
        "provider command-list ID2D1ImageBrush creation failed");
    ComPtr<ID2D1ImageBrush> command_list_brush;
    command_list_brush.Attach(
        static_cast<ID2D1ImageBrush*>(command_list_brush_value));
    ComPtr<ID2D1Image> command_list_brush_image;
    command_list_brush->GetImage(
        command_list_brush_image.GetAddressOf());
    require(has_same_com_identity(
            command_list.Get(),
            command_list_brush_image.Get()),
        "command-list image brush changed source COM identity");

    progpu_native_direct2d_guid gaussian_blur_id =
        to_portable_guid(gaussian_blur_effect_id);
    void* gaussian_effect_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_effect(
            surface,
            &gaussian_blur_id,
            &gaussian_effect_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            gaussian_effect_value != nullptr && native_hresult == S_OK,
        "provider ID2D1Effect creation failed");
    ComPtr<ID2D1Effect> gaussian_effect;
    gaussian_effect.Attach(
        static_cast<ID2D1Effect*>(gaussian_effect_value));
    require(gaussian_effect->GetInputCount() == 1U,
        "Gaussian blur effect input count changed");

    progpu_native_direct2d_guid empty_effect_id{};
    void* invalid_effect_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_effect(
            surface,
            &empty_effect_id,
            &invalid_effect_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_effect_value == nullptr && native_hresult == E_INVALIDARG,
        "empty Direct2D effect CLSID did not fail closed");

    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_effect_set_input(
            surface,
            gaussian_effect.Get(),
            0U,
            source_bitmap.Get(),
            1U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider ID2D1Effect image input failed");
    require(
        progpu_native_direct2d_effect_set_input(
            surface,
            gaussian_effect.Get(),
            1U,
            source_bitmap.Get(),
            1U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED &&
            native_hresult == E_INVALIDARG,
        "out-of-range Direct2D effect input did not fail closed");

    const float gaussian_standard_deviation = 0.0F;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_effect_set_value(
            surface,
            gaussian_effect.Get(),
            D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION,
            PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_FLOAT,
            &gaussian_standard_deviation,
            sizeof(gaussian_standard_deviation),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider ID2D1Effect float property update failed");
    float observed_standard_deviation = -1.0F;
    require(SUCCEEDED(gaussian_effect->GetValue(
                D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION,
                &observed_standard_deviation)) &&
            observed_standard_deviation == gaussian_standard_deviation,
        "Gaussian blur property changed");
    require(
        progpu_native_direct2d_effect_set_value(
            surface,
            gaussian_effect.Get(),
            D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION,
            PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_FLOAT,
            &gaussian_standard_deviation,
            sizeof(gaussian_standard_deviation) - 1U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            native_hresult == E_INVALIDARG,
        "malformed Direct2D effect property did not fail closed");

    void* gaussian_output_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_effect_get_output(
            surface,
            gaussian_effect.Get(),
            &gaussian_output_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            gaussian_output_value != nullptr && native_hresult == S_OK,
        "provider ID2D1Effect output query failed");
    ComPtr<ID2D1Image> gaussian_output;
    gaussian_output.Attach(static_cast<ID2D1Image*>(gaussian_output_value));
    ComPtr<ID2D1Image> direct_gaussian_output;
    gaussian_effect->GetOutput(direct_gaussian_output.GetAddressOf());
    require(has_same_com_identity(
            gaussian_output.Get(),
            direct_gaussian_output.Get()),
        "provider effect output changed COM identity");

    progpu_native_direct2d_guid shadow_id =
        to_portable_guid(shadow_effect_id);
    void* shadow_effect_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_effect(
            surface,
            &shadow_id,
            &shadow_effect_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            shadow_effect_value != nullptr && native_hresult == S_OK,
        "provider shadow ID2D1Effect creation failed");
    ComPtr<ID2D1Effect> shadow_effect;
    shadow_effect.Attach(static_cast<ID2D1Effect*>(shadow_effect_value));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_effect_set_input_effect(
            surface,
            shadow_effect.Get(),
            0U,
            gaussian_effect.Get(),
            1U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider effect-to-effect input failed");

    progpu_native_direct2d_image_brush_properties effect_brush_properties{};
    effect_brush_properties.source_rectangle = {0.0F, 0.0F, 2.0F, 2.0F};
    effect_brush_properties.extend_mode_x =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP;
    effect_brush_properties.extend_mode_y =
        PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP;
    effect_brush_properties.interpolation_mode =
        PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_NEAREST_NEIGHBOR;
    progpu_native_direct2d_brush_properties effect_common_properties{};
    effect_common_properties.opacity = 1.0F;
    effect_common_properties.transform.m11 = 1.0F;
    effect_common_properties.transform.m22 = 1.0F;
    void* effect_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_image_brush(
            surface,
            gaussian_output.Get(),
            &effect_brush_properties,
            &effect_common_properties,
            &effect_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            effect_brush_value != nullptr && native_hresult == S_OK,
        "provider effect-output ID2D1ImageBrush creation failed");
    ComPtr<ID2D1ImageBrush> effect_brush;
    effect_brush.Attach(static_cast<ID2D1ImageBrush*>(effect_brush_value));
    ComPtr<ID2D1Image> effect_brush_image;
    effect_brush->GetImage(effect_brush_image.GetAddressOf());
    require(has_same_com_identity(
            gaussian_output.Get(),
            effect_brush_image.Get()),
        "effect image brush changed output COM identity");

    progpu_native_direct2d_size_f layer_size = {64.0F, 48.0F};
    void* layer_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_layer(
            surface,
            &layer_size,
            &layer_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            layer_value != nullptr && native_hresult == S_OK,
        "provider ID2D1Layer creation failed");
    ComPtr<ID2D1Layer> layer;
    layer.Attach(static_cast<ID2D1Layer*>(layer_value));
    D2D1_SIZE_F observed_layer_size = layer->GetSize();
    require(observed_layer_size.width == layer_size.width &&
            observed_layer_size.height == layer_size.height,
        "provider ID2D1Layer size changed");

    progpu_native_direct2d_size_f invalid_layer_size = {-1.0F, 48.0F};
    void* invalid_layer_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_layer(
            surface,
            &invalid_layer_size,
            &invalid_layer_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_layer_value == nullptr && native_hresult == E_INVALIDARG,
        "invalid ID2D1Layer size did not fail closed");

    void* drawing_state_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_drawing_state_block(
            surface,
            &drawing_state_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            drawing_state_value != nullptr && native_hresult == S_OK,
        "provider ID2D1DrawingStateBlock1 creation failed");
    ComPtr<ID2D1DrawingStateBlock1> drawing_state;
    drawing_state.Attach(
        static_cast<ID2D1DrawingStateBlock1*>(drawing_state_value));
    D2D1_DRAWING_STATE_DESCRIPTION1 initial_drawing_state{};
    drawing_state->GetDescription(&initial_drawing_state);
    require(initial_drawing_state.transform._11 == 1.0F &&
            initial_drawing_state.transform._22 == 1.0F,
        "provider default drawing state changed");

    constexpr uint16_t font_family[] = {
        'S', 'e', 'g', 'o', 'e', ' ', 'U', 'I'
    };
    constexpr uint16_t locale_name[] = {'e', 'n', '-', 'U', 'S'};
    progpu_native_direct2d_text_format_properties text_properties{};
    text_properties.struct_size = sizeof(text_properties);
    text_properties.font_weight = DWRITE_FONT_WEIGHT_SEMI_BOLD;
    text_properties.font_style = PROGPU_NATIVE_DIRECT2D_FONT_STYLE_NORMAL;
    text_properties.font_stretch =
        PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_NORMAL;
    text_properties.font_size = 13.0F;
    text_properties.text_alignment =
        PROGPU_NATIVE_DIRECT2D_TEXT_ALIGNMENT_LEADING;
    text_properties.paragraph_alignment =
        PROGPU_NATIVE_DIRECT2D_PARAGRAPH_ALIGNMENT_NEAR;
    text_properties.word_wrapping =
        PROGPU_NATIVE_DIRECT2D_WORD_WRAPPING_NO_WRAP;
    text_properties.reading_direction =
        PROGPU_NATIVE_DIRECT2D_READING_DIRECTION_LEFT_TO_RIGHT;
    text_properties.flow_direction =
        PROGPU_NATIVE_DIRECT2D_FLOW_DIRECTION_TOP_TO_BOTTOM;
    text_properties.incremental_tab_stop = 24.0F;
    void* text_format_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_text_format(
            surface,
            font_family,
            static_cast<uint32_t>(std::size(font_family)),
            locale_name,
            static_cast<uint32_t>(std::size(locale_name)),
            &text_properties,
            &text_format_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            text_format_value != nullptr && native_hresult == S_OK,
        "provider IDWriteTextFormat1 creation failed");
    ComPtr<IDWriteTextFormat1> text_format;
    text_format.Attach(static_cast<IDWriteTextFormat1*>(text_format_value));
    require(text_format->GetFontWeight() == DWRITE_FONT_WEIGHT_SEMI_BOLD &&
            text_format->GetFontSize() == text_properties.font_size &&
            text_format->GetWordWrapping() == DWRITE_WORD_WRAPPING_NO_WRAP &&
            text_format->GetIncrementalTabStop() ==
                text_properties.incremental_tab_stop,
        "provider IDWriteTextFormat1 properties changed");

    auto invalid_text_properties = text_properties;
    invalid_text_properties.struct_size = sizeof(text_properties) - 1U;
    void* invalid_text_format_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_text_format(
            surface,
            font_family,
            static_cast<uint32_t>(std::size(font_family)),
            locale_name,
            static_cast<uint32_t>(std::size(locale_name)),
            &invalid_text_properties,
            &invalid_text_format_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_text_format_value == nullptr &&
            native_hresult == E_INVALIDARG,
        "invalid IDWriteTextFormat1 descriptor did not fail closed");

    constexpr uint16_t text[] = {'A', 'B', 'I', ' ', '1', '6'};
    progpu_native_direct2d_rect_f text_layout = {2.0F, 27.0F, 30.0F, 17.0F};
    void* retained_text_layout_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_text_layout(
            surface,
            text,
            static_cast<uint32_t>(std::size(text)),
            text_format.Get(),
            text_layout.width,
            text_layout.height,
            &retained_text_layout_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            retained_text_layout_value != nullptr && native_hresult == S_OK,
        "provider IDWriteTextLayout4 creation failed");
    ComPtr<IDWriteTextLayout4> retained_text_layout;
    retained_text_layout.Attach(
        static_cast<IDWriteTextLayout4*>(retained_text_layout_value));
    ComPtr<IDWriteTextLayout> retained_text_layout_base;
    DWRITE_TEXT_METRICS retained_text_metrics{};
    require(SUCCEEDED(retained_text_layout.As(&retained_text_layout_base)) &&
            retained_text_layout->GetMaxWidth() == text_layout.width &&
            retained_text_layout->GetMaxHeight() == text_layout.height &&
            SUCCEEDED(retained_text_layout_base->GetMetrics(
                &retained_text_metrics)) &&
            retained_text_metrics.layoutWidth == text_layout.width &&
            retained_text_metrics.layoutHeight == text_layout.height,
        "provider IDWriteTextLayout4 properties changed");

    progpu_native_direct2d_text_range_format range_format{};
    range_format.struct_size = sizeof(range_format);
    range_format.flags =
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_SIZE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_WEIGHT |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STYLE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STRETCH |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_UNDERLINE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_STRIKETHROUGH |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_DRAWING_EFFECT;
    range_format.range_start = 1U;
    range_format.range_length = 3U;
    range_format.font_weight = DWRITE_FONT_WEIGHT_BOLD;
    range_format.font_style = PROGPU_NATIVE_DIRECT2D_FONT_STYLE_ITALIC;
    range_format.font_stretch =
        PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_SEMI_EXPANDED;
    range_format.font_size = 18.0F;
    range_format.underline = 1U;
    range_format.strikethrough = 1U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_text_layout_set_range_format(
            surface,
            retained_text_layout.Get(),
            &range_format,
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider IDWriteTextLayout4 range formatting failed");
    DWRITE_TEXT_RANGE actual_range{};
    FLOAT actual_font_size = 0.0F;
    DWRITE_FONT_WEIGHT actual_font_weight = DWRITE_FONT_WEIGHT_NORMAL;
    DWRITE_FONT_STYLE actual_font_style = DWRITE_FONT_STYLE_NORMAL;
    DWRITE_FONT_STRETCH actual_font_stretch = DWRITE_FONT_STRETCH_NORMAL;
    BOOL actual_underline = FALSE;
    BOOL actual_strikethrough = FALSE;
    ComPtr<IUnknown> actual_drawing_effect;
    require(SUCCEEDED(retained_text_layout->GetFontSize(
                2U,
                &actual_font_size,
                &actual_range)) &&
            actual_font_size == 18.0F &&
            actual_range.startPosition == 1U &&
            actual_range.length == 3U &&
            SUCCEEDED(retained_text_layout->GetFontWeight(
                2U,
                &actual_font_weight,
                nullptr)) &&
            actual_font_weight == DWRITE_FONT_WEIGHT_BOLD &&
            SUCCEEDED(retained_text_layout->GetFontStyle(
                2U,
                &actual_font_style,
                nullptr)) &&
            actual_font_style == DWRITE_FONT_STYLE_ITALIC &&
            SUCCEEDED(retained_text_layout->GetFontStretch(
                2U,
                &actual_font_stretch,
                nullptr)) &&
            actual_font_stretch == DWRITE_FONT_STRETCH_SEMI_EXPANDED &&
            SUCCEEDED(retained_text_layout->GetUnderline(
                2U,
                &actual_underline,
                nullptr)) &&
            actual_underline != FALSE &&
            SUCCEEDED(retained_text_layout->GetStrikethrough(
                2U,
                &actual_strikethrough,
                nullptr)) &&
            actual_strikethrough != FALSE &&
            SUCCEEDED(retained_text_layout->GetDrawingEffect(
                2U,
                &actual_drawing_effect,
                nullptr)) &&
            has_same_com_identity(
                actual_drawing_effect.Get(),
                solid_brush.Get()),
        "provider IDWriteTextLayout4 range state changed");

    auto invalid_range_format = range_format;
    invalid_range_format.range_length = 0U;
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_text_layout_set_range_format(
            surface,
            retained_text_layout.Get(),
            &invalid_range_format,
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            native_hresult == E_INVALIDARG,
        "invalid IDWriteTextLayout4 range did not fail closed");

    const progpu_native_direct2d_typography_feature typography_features[] = {
        {DWRITE_FONT_FEATURE_TAG_DISCRETIONARY_LIGATURES, 1U},
        {DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_1, 2U}
    };
    void* typography_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_typography(
            surface,
            typography_features,
            static_cast<uint32_t>(std::size(typography_features)),
            &typography_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            typography_value != nullptr && native_hresult == S_OK,
        "provider IDWriteTypography creation failed");
    ComPtr<IDWriteTypography> typography;
    typography.Attach(static_cast<IDWriteTypography*>(typography_value));
    DWRITE_FONT_FEATURE actual_feature{};
    require(typography->GetFontFeatureCount() ==
                static_cast<uint32_t>(std::size(typography_features)) &&
            SUCCEEDED(typography->GetFontFeature(1U, &actual_feature)) &&
            actual_feature.nameTag == DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_1 &&
            actual_feature.parameter == 2U,
        "provider IDWriteTypography feature state changed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_text_layout_set_typography(
            surface,
            retained_text_layout.Get(),
            typography.Get(),
            1U,
            3U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider IDWriteTextLayout4 typography assignment failed");
    ComPtr<IDWriteTypography> actual_typography;
    DWRITE_TEXT_RANGE actual_typography_range{};
    DWRITE_FONT_FEATURE actual_layout_feature{};
    require(SUCCEEDED(retained_text_layout->GetTypography(
                2U,
                &actual_typography,
                &actual_typography_range)) &&
            actual_typography_range.startPosition == 1U &&
            actual_typography_range.length == 3U &&
            actual_typography->GetFontFeatureCount() ==
                static_cast<uint32_t>(std::size(typography_features)) &&
            SUCCEEDED(actual_typography->GetFontFeature(
                1U,
                &actual_layout_feature)) &&
            actual_layout_feature.nameTag ==
                DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_1 &&
            actual_layout_feature.parameter == 2U,
        "provider IDWriteTextLayout4 typography state changed");

    auto invalid_typography_feature = typography_features[0];
    invalid_typography_feature.name_tag = 0U;
    void* invalid_typography_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_typography(
            surface,
            &invalid_typography_feature,
            1U,
            &invalid_typography_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_typography_value == nullptr &&
            native_hresult == E_INVALIDARG,
        "invalid IDWriteTypography feature did not fail closed");

    progpu_native_direct2d_font_face_properties font_face_properties{};
    font_face_properties.struct_size = sizeof(font_face_properties);
    font_face_properties.font_weight = DWRITE_FONT_WEIGHT_SEMI_BOLD;
    font_face_properties.font_style =
        PROGPU_NATIVE_DIRECT2D_FONT_STYLE_NORMAL;
    font_face_properties.font_stretch =
        PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_NORMAL;
    void* font_face_reference_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_system_font_face_reference(
            surface,
            font_family,
            static_cast<uint32_t>(std::size(font_family)),
            &font_face_properties,
            &font_face_reference_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            font_face_reference_value != nullptr && native_hresult == S_OK,
        "provider system IDWriteFontFaceReference resolution failed");
    ComPtr<IDWriteFontFaceReference> font_face_reference;
    font_face_reference.Attach(
        static_cast<IDWriteFontFaceReference*>(font_face_reference_value));

    void* font_face_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_font_face_reference_create_font_face(
            surface,
            font_face_reference.Get(),
            &font_face_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            font_face_value != nullptr && native_hresult == S_OK,
        "provider IDWriteFontFace5 creation failed");
    ComPtr<IDWriteFontFace5> font_face;
    font_face.Attach(static_cast<IDWriteFontFace5*>(font_face_value));
    require(font_face->GetWeight() == DWRITE_FONT_WEIGHT_SEMI_BOLD &&
            font_face->GetStretch() == DWRITE_FONT_STRETCH_NORMAL,
        "provider IDWriteFontFace5 matching state changed");

    constexpr uint32_t glyph_code_points[] = {'A', 'B'};
    uint16_t glyph_indices[std::size(glyph_code_points)]{};
    require(SUCCEEDED(font_face->GetGlyphIndices(
                glyph_code_points,
                static_cast<uint32_t>(std::size(glyph_code_points)),
                glyph_indices)) &&
            glyph_indices[0] != 0U && glyph_indices[1] != 0U,
        "provider IDWriteFontFace5 could not map validation glyphs");
    constexpr float glyph_advances[] = {12.0F, 12.0F};
    constexpr progpu_native_direct2d_glyph_offset glyph_offsets[] = {
        {0.0F, 0.0F},
        {0.5F, 0.0F}
    };

    auto invalid_font_face_properties = font_face_properties;
    invalid_font_face_properties.font_weight = 0U;
    void* invalid_font_face_reference_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_system_font_face_reference(
            surface,
            font_family,
            static_cast<uint32_t>(std::size(font_family)),
            &invalid_font_face_properties,
            &invalid_font_face_reference_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_font_face_reference_value == nullptr &&
            native_hresult == E_INVALIDARG,
        "invalid IDWriteFontFaceReference match state did not fail closed");

    void* invalid_text_layout_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_create_text_layout(
            surface,
            text,
            static_cast<uint32_t>(std::size(text)),
            text_format.Get(),
            0.0F,
            text_layout.height,
            &invalid_text_layout_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_text_layout_value == nullptr &&
            native_hresult == E_INVALIDARG,
        "invalid IDWriteTextLayout4 dimensions did not fail closed");

    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_text(
            surface,
            text,
            static_cast<uint32_t>(std::size(text)),
            text_format.Get(),
            &text_layout,
            solid_brush.Get(),
            PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_CLIP,
            PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "ID2D1RenderTarget text draw outside a draw did not fail closed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_text_layout(
            surface,
            text_layout.x,
            text_layout.y,
            retained_text_layout.Get(),
            solid_brush.Get(),
            PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_CLIP,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "ID2D1RenderTarget text-layout draw outside a draw did not fail closed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_glyph_run(
            surface,
            2.0F,
            24.0F,
            13.0F,
            font_face.Get(),
            glyph_indices,
            static_cast<uint32_t>(std::size(glyph_indices)),
            glyph_advances,
            static_cast<uint32_t>(std::size(glyph_advances)),
            glyph_offsets,
            static_cast<uint32_t>(std::size(glyph_offsets)),
            0U,
            0U,
            solid_brush.Get(),
            PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "ID2D1DeviceContext glyph-run draw outside a draw did not fail closed");

    progpu_native_direct2d_layer_parameters layer_parameters{};
    layer_parameters.content_bounds = {0.0F, 0.0F, 24.0F, 24.0F};
    layer_parameters.mask_antialias_mode =
        PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_PER_PRIMITIVE;
    layer_parameters.mask_transform.m11 = 1.0F;
    layer_parameters.mask_transform.m22 = 1.0F;
    layer_parameters.opacity = 0.5F;
    layer_parameters.options = PROGPU_NATIVE_DIRECT2D_LAYER_OPTION_NONE;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_push_layer(
            surface,
            &layer_parameters,
            rectangle_geometry.Get(),
            nullptr,
            layer.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "ID2D1Layer push outside a draw did not fail closed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_save_drawing_state(
            surface,
            drawing_state.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "drawing-state save outside a draw did not fail closed");

    void* unbalanced_command_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &unbalanced_command_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            unbalanced_command_list_value != nullptr && native_hresult == S_OK,
        "unbalanced-layer command-list creation failed");
    ComPtr<ID2D1CommandList> unbalanced_command_list;
    unbalanced_command_list.Attach(
        static_cast<ID2D1CommandList*>(unbalanced_command_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            unbalanced_command_list.Get()) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "unbalanced-layer command-list recording did not begin");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_push_layer(
            surface,
            &layer_parameters,
            rectangle_geometry.Get(),
            nullptr,
            layer.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "unbalanced-layer command-list push failed");
    uint64_t unbalanced_tag1 = 0U;
    uint64_t unbalanced_tag2 = 0U;
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &unbalanced_tag1,
            &unbalanced_tag2,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH &&
            native_hresult == D2DERR_WRONG_STATE,
        "unbalanced ID2D1Layer scope did not fail closed and unwind");

    void* win2d_canvas_device_value = nullptr;
    native_hresult = E_FAIL;
    progpu_native_direct2d_status win2d_status =
        progpu_native_direct2d_surface_try_get_win2d_canvas_device(
            surface,
            &win2d_canvas_device_value,
            &native_hresult);
    if (win2d_status == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS) {
        require(win2d_canvas_device_value != nullptr &&
                native_hresult == S_OK,
            "Win2D activation returned invalid success state");
        ComPtr<IInspectable> win2d_canvas_device;
        win2d_canvas_device.Attach(
            static_cast<IInspectable*>(win2d_canvas_device_value));
        constexpr GUID canvas_device_interface_id = {
            0xA27F0B5D,
            0xEC2C,
            0x4D4F,
            {0x94, 0x8F, 0x0A, 0xA1, 0xE9, 0x5E, 0x33, 0xE6}
        };
        ComPtr<IUnknown> canvas_device_interface;
        require(SUCCEEDED(win2d_canvas_device->QueryInterface(
                    canvas_device_interface_id,
                    &canvas_device_interface)),
            "activated Win2D object omitted ICanvasDevice");

        progpu_native_direct2d_guid device1_id =
            to_portable_guid(__uuidof(ID2D1Device1));
        void* wrapped_device_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_native_resource(
                surface,
                PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_DEVICE,
                &device1_id,
                &wrapped_device_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                wrapped_device_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasDevice native-resource query failed");
        ComPtr<ID2D1Device1> wrapped_device;
        wrapped_device.Attach(
            static_cast<ID2D1Device1*>(wrapped_device_value));
        require(has_same_com_identity(device.Get(), wrapped_device.Get()),
            "Win2D CanvasDevice did not preserve ID2D1Device1 identity");

        void* canvas_font_face_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                font_face_reference.Get(),
                0.0F,
                &canvas_font_face_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_font_face_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasFontFace wrapping failed");
        ComPtr<IInspectable> canvas_font_face;
        canvas_font_face.Attach(
            static_cast<IInspectable*>(canvas_font_face_value));
        progpu_native_direct2d_guid font_face_reference_id =
            to_portable_guid(__uuidof(IDWriteFontFaceReference));
        void* unwrapped_font_face_reference_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_font_face.Get(),
                0.0F,
                &font_face_reference_id,
                &unwrapped_font_face_reference_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_font_face_reference_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasFontFace native-resource query failed");
        ComPtr<IDWriteFontFaceReference> unwrapped_font_face_reference;
        unwrapped_font_face_reference.Attach(
            static_cast<IDWriteFontFaceReference*>(
                unwrapped_font_face_reference_value));
        require(has_same_com_identity(
                font_face_reference.Get(),
                unwrapped_font_face_reference.Get()),
            "Win2D CanvasFontFace changed native COM identity");

        void* canvas_solid_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                solid_brush.Get(),
                0.0F,
                &canvas_solid_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_solid_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasSolidColorBrush wrapping failed");
        ComPtr<IInspectable> canvas_solid_brush;
        canvas_solid_brush.Attach(
            static_cast<IInspectable*>(canvas_solid_brush_value));
        constexpr GUID canvas_solid_color_brush_interface_id = {
            0x8BC30F87,
            0xBAD5,
            0x4871,
            {0x88, 0xB8, 0x9F, 0xE3, 0xC6, 0x3D, 0x20, 0x4A}
        };
        ComPtr<IUnknown> canvas_solid_brush_interface;
        require(SUCCEEDED(canvas_solid_brush->QueryInterface(
                    canvas_solid_color_brush_interface_id,
                    &canvas_solid_brush_interface)),
            "wrapped Win2D object omitted ICanvasSolidColorBrush");

        progpu_native_direct2d_guid solid_brush_id =
            to_portable_guid(__uuidof(ID2D1SolidColorBrush));
        void* unwrapped_solid_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_solid_brush.Get(),
                0.0F,
                &solid_brush_id,
                &unwrapped_solid_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_solid_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasSolidColorBrush native-resource query failed");
        ComPtr<ID2D1SolidColorBrush> unwrapped_solid_brush;
        unwrapped_solid_brush.Attach(
            static_cast<ID2D1SolidColorBrush*>(unwrapped_solid_brush_value));
        require(has_same_com_identity(
                solid_brush.Get(),
                unwrapped_solid_brush.Get()),
            "Win2D CanvasSolidColorBrush changed native COM identity");

        void* canvas_linear_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                linear_brush.Get(),
                0.0F,
                &canvas_linear_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_linear_brush_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasLinearGradientBrush wrapping failed");
        ComPtr<IInspectable> canvas_linear_brush;
        canvas_linear_brush.Attach(
            static_cast<IInspectable*>(canvas_linear_brush_value));
        constexpr GUID canvas_linear_gradient_brush_interface_id = {
            0xA4FFBCB1,
            0xEC22,
            0x48C8,
            {0xB1, 0xAF, 0x09, 0xBC, 0xFD, 0x34, 0xEE, 0xBD}
        };
        ComPtr<IUnknown> canvas_linear_brush_interface;
        require(SUCCEEDED(canvas_linear_brush->QueryInterface(
                    canvas_linear_gradient_brush_interface_id,
                    &canvas_linear_brush_interface)),
            "wrapped Win2D object omitted ICanvasLinearGradientBrush");
        progpu_native_direct2d_guid linear_brush_id =
            to_portable_guid(__uuidof(ID2D1LinearGradientBrush));
        void* unwrapped_linear_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_linear_brush.Get(),
                0.0F,
                &linear_brush_id,
                &unwrapped_linear_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_linear_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasLinearGradientBrush native-resource query failed");
        ComPtr<ID2D1LinearGradientBrush> unwrapped_linear_brush;
        unwrapped_linear_brush.Attach(
            static_cast<ID2D1LinearGradientBrush*>(
                unwrapped_linear_brush_value));
        require(has_same_com_identity(
                linear_brush.Get(),
                unwrapped_linear_brush.Get()),
            "Win2D CanvasLinearGradientBrush changed native COM identity");

        void* canvas_radial_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                radial_brush.Get(),
                0.0F,
                &canvas_radial_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_radial_brush_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasRadialGradientBrush wrapping failed");
        ComPtr<IInspectable> canvas_radial_brush;
        canvas_radial_brush.Attach(
            static_cast<IInspectable*>(canvas_radial_brush_value));
        constexpr GUID canvas_radial_gradient_brush_interface_id = {
            0x4D27D756,
            0x14A9,
            0x4EB7,
            {0x97, 0x3F, 0xE6, 0x61, 0x4D, 0x4F, 0x89, 0xE7}
        };
        ComPtr<IUnknown> canvas_radial_brush_interface;
        require(SUCCEEDED(canvas_radial_brush->QueryInterface(
                    canvas_radial_gradient_brush_interface_id,
                    &canvas_radial_brush_interface)),
            "wrapped Win2D object omitted ICanvasRadialGradientBrush");
        progpu_native_direct2d_guid radial_brush_id =
            to_portable_guid(__uuidof(ID2D1RadialGradientBrush));
        void* unwrapped_radial_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_radial_brush.Get(),
                0.0F,
                &radial_brush_id,
                &unwrapped_radial_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_radial_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasRadialGradientBrush native-resource query failed");
        ComPtr<ID2D1RadialGradientBrush> unwrapped_radial_brush;
        unwrapped_radial_brush.Attach(
            static_cast<ID2D1RadialGradientBrush*>(
                unwrapped_radial_brush_value));
        require(has_same_com_identity(
                radial_brush.Get(),
                unwrapped_radial_brush.Get()),
            "Win2D CanvasRadialGradientBrush changed native COM identity");

        void* canvas_geometry_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                transformed_geometry.Get(),
                0.0F,
                &canvas_geometry_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_geometry_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasGeometry wrapping failed");
        ComPtr<IInspectable> canvas_geometry;
        canvas_geometry.Attach(
            static_cast<IInspectable*>(canvas_geometry_value));
        constexpr GUID canvas_geometry_interface_id = {
            0x74EA89FA,
            0xC87C,
            0x4D0D,
            {0x90, 0x57, 0x27, 0x43, 0xB8, 0xDB, 0x67, 0xEE}
        };
        ComPtr<IUnknown> canvas_geometry_interface;
        require(SUCCEEDED(canvas_geometry->QueryInterface(
                    canvas_geometry_interface_id,
                    &canvas_geometry_interface)),
            "wrapped Win2D object omitted ICanvasGeometry");
        progpu_native_direct2d_guid geometry_id =
            to_portable_guid(__uuidof(ID2D1Geometry));
        void* unwrapped_geometry_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_geometry.Get(),
                0.0F,
                &geometry_id,
                &unwrapped_geometry_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_geometry_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasGeometry native-resource query failed");
        ComPtr<ID2D1Geometry> unwrapped_geometry;
        unwrapped_geometry.Attach(
            static_cast<ID2D1Geometry*>(unwrapped_geometry_value));
        require(has_same_com_identity(
                transformed_geometry.Get(),
                unwrapped_geometry.Get()),
            "Win2D CanvasGeometry changed native COM identity");

        void* canvas_stroke_style_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                stroke_style.Get(),
                0.0F,
                &canvas_stroke_style_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_stroke_style_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasStrokeStyle wrapping failed");
        ComPtr<IInspectable> canvas_stroke_style;
        canvas_stroke_style.Attach(
            static_cast<IInspectable*>(canvas_stroke_style_value));
        progpu_native_direct2d_guid stroke_style_id =
            to_portable_guid(__uuidof(ID2D1StrokeStyle1));
        void* unwrapped_stroke_style_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_stroke_style.Get(),
                0.0F,
                &stroke_style_id,
                &unwrapped_stroke_style_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_stroke_style_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasStrokeStyle native-resource query failed");
        ComPtr<ID2D1StrokeStyle1> unwrapped_stroke_style;
        unwrapped_stroke_style.Attach(
            static_cast<ID2D1StrokeStyle1*>(
                unwrapped_stroke_style_value));
        require(has_same_com_identity(
                stroke_style.Get(),
                unwrapped_stroke_style.Get()),
            "Win2D CanvasStrokeStyle changed native COM identity");

        void* canvas_bitmap_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                source_bitmap.Get(),
                0.0F,
                &canvas_bitmap_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_bitmap_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasBitmap wrapping failed");
        ComPtr<IInspectable> canvas_bitmap;
        canvas_bitmap.Attach(static_cast<IInspectable*>(canvas_bitmap_value));
        progpu_native_direct2d_guid bitmap1_id =
            to_portable_guid(__uuidof(ID2D1Bitmap1));
        void* unwrapped_bitmap_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_bitmap.Get(),
                0.0F,
                &bitmap1_id,
                &unwrapped_bitmap_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_bitmap_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasBitmap native-resource query failed");
        ComPtr<ID2D1Bitmap1> unwrapped_bitmap;
        unwrapped_bitmap.Attach(
            static_cast<ID2D1Bitmap1*>(unwrapped_bitmap_value));
        require(has_same_com_identity(
                source_bitmap.Get(),
                unwrapped_bitmap.Get()),
            "Win2D CanvasBitmap changed native COM identity");

        void* canvas_image_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                bitmap_brush.Get(),
                0.0F,
                &canvas_image_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_image_brush_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasImageBrush wrapping failed");
        ComPtr<IInspectable> canvas_image_brush;
        canvas_image_brush.Attach(
            static_cast<IInspectable*>(canvas_image_brush_value));
        progpu_native_direct2d_guid bitmap_brush1_id =
            to_portable_guid(__uuidof(ID2D1BitmapBrush1));
        void* unwrapped_bitmap_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_image_brush.Get(),
                0.0F,
                &bitmap_brush1_id,
                &unwrapped_bitmap_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_bitmap_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasImageBrush native-resource query failed");
        ComPtr<ID2D1BitmapBrush1> unwrapped_bitmap_brush;
        unwrapped_bitmap_brush.Attach(
            static_cast<ID2D1BitmapBrush1*>(
                unwrapped_bitmap_brush_value));
        require(has_same_com_identity(
                bitmap_brush.Get(),
                unwrapped_bitmap_brush.Get()),
            "Win2D CanvasImageBrush changed native COM identity");

        void* canvas_general_image_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                image_brush.Get(),
                0.0F,
                &canvas_general_image_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_general_image_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasImageBrush ID2D1ImageBrush wrapping failed");
        ComPtr<IInspectable> canvas_general_image_brush;
        canvas_general_image_brush.Attach(
            static_cast<IInspectable*>(canvas_general_image_brush_value));
        progpu_native_direct2d_guid image_brush_id =
            to_portable_guid(__uuidof(ID2D1ImageBrush));
        void* unwrapped_image_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_general_image_brush.Get(),
                0.0F,
                &image_brush_id,
                &unwrapped_image_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_image_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasImageBrush ID2D1ImageBrush query failed");
        ComPtr<ID2D1ImageBrush> unwrapped_image_brush;
        unwrapped_image_brush.Attach(
            static_cast<ID2D1ImageBrush*>(unwrapped_image_brush_value));
        require(has_same_com_identity(
                image_brush.Get(),
                unwrapped_image_brush.Get()),
            "Win2D CanvasImageBrush changed ID2D1ImageBrush COM identity");

        void* canvas_command_list_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                command_list.Get(),
                0.0F,
                &canvas_command_list_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_command_list_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasCommandList wrapping failed");
        ComPtr<IInspectable> canvas_command_list;
        canvas_command_list.Attach(
            static_cast<IInspectable*>(canvas_command_list_value));
        progpu_native_direct2d_guid command_list_id =
            to_portable_guid(__uuidof(ID2D1CommandList));
        void* unwrapped_command_list_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_command_list.Get(),
                0.0F,
                &command_list_id,
                &unwrapped_command_list_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_command_list_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasCommandList native-resource query failed");
        ComPtr<ID2D1CommandList> unwrapped_command_list;
        unwrapped_command_list.Attach(
            static_cast<ID2D1CommandList*>(unwrapped_command_list_value));
        require(has_same_com_identity(
                command_list.Get(),
                unwrapped_command_list.Get()),
            "Win2D CanvasCommandList changed native COM identity");

        void* canvas_command_list_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                command_list_brush.Get(),
                0.0F,
                &canvas_command_list_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_command_list_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D command-list CanvasImageBrush wrapping failed");
        ComPtr<IInspectable> canvas_command_list_brush;
        canvas_command_list_brush.Attach(
            static_cast<IInspectable*>(canvas_command_list_brush_value));
        void* unwrapped_command_list_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_command_list_brush.Get(),
                0.0F,
                &image_brush_id,
                &unwrapped_command_list_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_command_list_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D command-list CanvasImageBrush native query failed");
        ComPtr<ID2D1ImageBrush> unwrapped_command_list_brush;
        unwrapped_command_list_brush.Attach(
            static_cast<ID2D1ImageBrush*>(
                unwrapped_command_list_brush_value));
        require(has_same_com_identity(
                command_list_brush.Get(),
                unwrapped_command_list_brush.Get()),
            "Win2D command-list CanvasImageBrush changed COM identity");

        void* canvas_effect_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                effect_brush.Get(),
                0.0F,
                &canvas_effect_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_effect_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D effect-output CanvasImageBrush wrapping failed");
        ComPtr<IInspectable> canvas_effect_brush;
        canvas_effect_brush.Attach(
            static_cast<IInspectable*>(canvas_effect_brush_value));
        void* unwrapped_effect_brush_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_effect_brush.Get(),
                0.0F,
                &image_brush_id,
                &unwrapped_effect_brush_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_effect_brush_value != nullptr &&
                native_hresult == S_OK,
            "Win2D effect-output CanvasImageBrush native query failed");
        ComPtr<ID2D1ImageBrush> unwrapped_effect_brush;
        unwrapped_effect_brush.Attach(
            static_cast<ID2D1ImageBrush*>(unwrapped_effect_brush_value));
        require(has_same_com_identity(
                effect_brush.Get(),
                unwrapped_effect_brush.Get()),
            "Win2D effect-output CanvasImageBrush changed COM identity");

        progpu_native_direct2d_guid no_interface_id =
            to_portable_guid(GUID_NULL);
        void* no_interface_value =
            reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
        native_hresult = S_OK;
        require(
            progpu_native_direct2d_surface_try_get_win2d_native_resource(
                surface,
                PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_DEVICE,
                &no_interface_id,
                &no_interface_value,
                &native_hresult) ==
                    PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
                no_interface_value == nullptr &&
                native_hresult == E_NOINTERFACE,
            "unsupported Win2D native-resource query did not fail closed");
    } else {
        require(
            win2d_status ==
                PROGPU_NATIVE_DIRECT2D_STATUS_WIN2D_RUNTIME_UNAVAILABLE &&
                win2d_canvas_device_value == nullptr &&
                FAILED(native_hresult),
            "optional Win2D activation did not fail closed");
    }

    void* win2d_render_target_value = nullptr;
    native_hresult = E_FAIL;
    progpu_native_direct2d_status win2d_render_target_status =
        progpu_native_direct2d_surface_try_get_win2d_canvas_render_target(
            surface,
            &win2d_render_target_value,
            &native_hresult);
    if (win2d_status == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS) {
        require(
            win2d_render_target_status ==
                PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                win2d_render_target_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasRenderTarget wrapping failed");
        ComPtr<IInspectable> win2d_render_target;
        win2d_render_target.Attach(
            static_cast<IInspectable*>(win2d_render_target_value));
        constexpr GUID canvas_render_target_interface_id = {
            0x2D4C7349,
            0x9A32,
            0x41B9,
            {0xB3, 0xCC, 0xCA, 0xF1, 0xB7, 0xE1, 0x09, 0x9B}
        };
        ComPtr<IUnknown> canvas_render_target_interface;
        require(SUCCEEDED(win2d_render_target->QueryInterface(
                    canvas_render_target_interface_id,
                    &canvas_render_target_interface)),
            "wrapped Win2D object omitted ICanvasRenderTarget");

        progpu_native_direct2d_guid bitmap1_id =
            to_portable_guid(__uuidof(ID2D1Bitmap1));
        void* wrapped_bitmap_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_native_resource(
                surface,
                PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_RENDER_TARGET,
                &bitmap1_id,
                &wrapped_bitmap_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                wrapped_bitmap_value != nullptr && native_hresult == S_OK,
            "Win2D CanvasRenderTarget native-resource query failed");
        ComPtr<ID2D1Bitmap1> wrapped_bitmap;
        wrapped_bitmap.Attach(
            static_cast<ID2D1Bitmap1*>(wrapped_bitmap_value));
        require(has_same_com_identity(bitmap.Get(), wrapped_bitmap.Get()),
            "Win2D CanvasRenderTarget did not preserve ID2D1Bitmap1 identity");
    } else {
        require(
            win2d_render_target_status ==
                PROGPU_NATIVE_DIRECT2D_STATUS_WIN2D_RUNTIME_UNAVAILABLE &&
                win2d_render_target_value == nullptr &&
                FAILED(native_hresult),
            "optional Win2D render-target wrapping did not fail closed");
    }

    progpu_native_direct2d_guid invalid_resource_id =
        to_portable_guid(__uuidof(ID2D1Device1));
    void* invalid_resource_value =
        reinterpret_cast<void*>(static_cast<uintptr_t>(1U));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_try_get_win2d_native_resource(
            surface,
            static_cast<progpu_native_direct2d_win2d_resource_kind>(999),
            &invalid_resource_id,
            &invalid_resource_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_resource_value == nullptr && native_hresult == E_INVALIDARG,
        "unknown Win2D native-resource kind did not fail closed");

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
    D2D1_MATRIX_3X2_F saved_transform =
        D2D1::Matrix3x2F::Translation(3.0F, 5.0F);
    context->SetTransform(saved_transform);
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_save_drawing_state(
            surface,
            drawing_state.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider drawing-state save failed");
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_restore_drawing_state(
            surface,
            drawing_state.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider drawing-state restore failed");
    D2D1_MATRIX_3X2_F restored_transform{};
    context->GetTransform(&restored_transform);
    require(restored_transform._31 == saved_transform._31 &&
            restored_transform._32 == saved_transform._32,
        "provider drawing-state restore changed the transform");
    context->SetTransform(D2D1::Matrix3x2F::Identity());

    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_text(
            surface,
            text,
            static_cast<uint32_t>(std::size(text)),
            text_format.Get(),
            &text_layout,
            solid_brush.Get(),
            PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_CLIP,
            PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1RenderTarget text draw failed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_text_layout(
            surface,
            34.0F,
            text_layout.y,
            retained_text_layout.Get(),
            solid_brush.Get(),
            PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_CLIP,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1RenderTarget text-layout draw failed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_glyph_run(
            surface,
            2.0F,
            24.0F,
            13.0F,
            font_face.Get(),
            glyph_indices,
            static_cast<uint32_t>(std::size(glyph_indices)),
            glyph_advances,
            static_cast<uint32_t>(std::size(glyph_advances)),
            glyph_offsets,
            static_cast<uint32_t>(std::size(glyph_offsets)),
            0U,
            0U,
            solid_brush.Get(),
            PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1DeviceContext glyph-run draw failed");
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_draw_text(
            surface,
            text,
            static_cast<uint32_t>(std::size(text)),
            text_format.Get(),
            &text_layout,
            solid_brush.Get(),
            1U << 31U,
            PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            native_hresult == E_INVALIDARG,
        "invalid Direct2D text options did not fail closed");

    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_push_layer(
            surface,
            &layer_parameters,
            rectangle_geometry.Get(),
            nullptr,
            layer.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1Layer push failed");
    context->FillRectangle(
        D2D1::RectF(0.0F, 0.0F, 24.0F, 24.0F),
        solid_brush.Get());
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_pop_layer(
            surface,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1Layer pop failed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_pop_layer(
            surface,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH &&
            native_hresult == D2DERR_WRONG_STATE,
        "unmatched ID2D1Layer pop did not fail closed");

    context->FillRectangle(
        D2D1::RectF(4.0F, 5.0F, 32.0F, 28.0F),
        solid_brush.Get());
    context->FillRectangle(
        D2D1::RectF(32.0F, 0.0F, 64.0F, 24.0F),
        linear_brush.Get());
    context->FillRectangle(
        D2D1::RectF(32.0F, 24.0F, 64.0F, 48.0F),
        radial_brush.Get());
    context->FillRectangle(
        D2D1::RectF(48.0F, 0.0F, 64.0F, 16.0F),
        bitmap_brush.Get());
    context->FillRectangle(
        D2D1::RectF(48.0F, 48.0F, 64.0F, 64.0F),
        image_brush.Get());
    context->DrawImage(
        command_list.Get(),
        D2D1::Point2F(0.0F, 32.0F));
    context->FillRectangle(
        D2D1::RectF(32.0F, 32.0F, 48.0F, 48.0F),
        command_list_brush.Get());
    context->DrawImage(
        gaussian_output.Get(),
        D2D1::Point2F(56.0F, 40.0F));
    context->FillRectangle(
        D2D1::RectF(56.0F, 32.0F, 64.0F, 40.0F),
        effect_brush.Get());
    context->FillGeometry(path_geometry.Get(), solid_brush.Get());
    context->DrawGeometry(
        combined_geometry.Get(),
        solid_brush.Get(),
        2.0F,
        stroke_style.Get());
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
    RoUninitialize();
    return EXIT_SUCCESS;
}
