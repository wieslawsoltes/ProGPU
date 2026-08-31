#include "progpu_native_direct2d.h"
#include "progpu_native.h"

#include <d2d1_3.h>
#include <d2d1effects.h>
#include <d3d11_1.h>
#include <dwrite_3.h>
#include <dxgi1_2.h>
#include <roapi.h>
#include <windows.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <wrl/client.h>

#include <array>
#include <cmath>
#include <cstring>
#include <cstdlib>
#include <iostream>
#include <vector>

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
T read_value(const std::vector<uint8_t>& bytes, size_t offset)
{
    require(
        offset <= bytes.size() && sizeof(T) <= bytes.size() - offset,
        "typed Direct2D scene read exceeded the stream");
    T value{};
    std::memcpy(&value, bytes.data() + offset, sizeof(value));
    return value;
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

void record_rectangle_path(ID2D1GeometrySink* sink)
{
    const std::array<D2D1_POINT_2F, 3U> points = {{
        D2D1::Point2F(10.0F, 0.0F),
        D2D1::Point2F(10.0F, 8.0F),
        D2D1::Point2F(0.0F, 8.0F)}};
    sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    sink->BeginFigure(
        D2D1::Point2F(0.0F, 0.0F),
        D2D1_FIGURE_BEGIN_FILLED);
    sink->AddLines(points.data(), static_cast<UINT32>(points.size()));
    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
}

void record_path_vocabulary(ID2D1GeometrySink* sink)
{
    sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    sink->BeginFigure(
        D2D1::Point2F(0.0F, 0.0F),
        D2D1_FIGURE_BEGIN_FILLED);
    sink->AddLine(D2D1::Point2F(10.0F, 0.0F));
    sink->SetSegmentFlags(D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN);
    const D2D1_QUADRATIC_BEZIER_SEGMENT quadratic = {
        D2D1::Point2F(15.0F, 5.0F),
        D2D1::Point2F(10.0F, 10.0F)};
    sink->AddQuadraticBezier(&quadratic);
    const D2D1_BEZIER_SEGMENT cubic = {
        D2D1::Point2F(8.0F, 12.0F),
        D2D1::Point2F(2.0F, 12.0F),
        D2D1::Point2F(0.0F, 10.0F)};
    sink->AddBezier(&cubic);
    const D2D1_ARC_SEGMENT arc = {
        D2D1::Point2F(0.0F, 0.0F),
        D2D1::SizeF(5.0F, 5.0F),
        0.0F,
        D2D1_SWEEP_DIRECTION_CLOCKWISE,
        D2D1_ARC_SIZE_SMALL};
    sink->SetSegmentFlags(D2D1_PATH_SEGMENT_NONE);
    sink->AddArc(&arc);
    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
}

bool approximately_equal(float left, float right, float tolerance) noexcept
{
    return std::abs(left - right) <= tolerance;
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

    void* compat_factory_value = nullptr;
    int32_t native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_compat_factory_create(
            &compat_factory_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            compat_factory_value != nullptr && native_hresult == S_OK,
        "ProGPU ID2D1Factory1 compatibility facade creation failed");
    ComPtr<ID2D1Factory1> compat_factory;
    compat_factory.Attach(static_cast<ID2D1Factory1*>(compat_factory_value));
    ComPtr<ID2D1Factory> compat_base_factory;
    ComPtr<ID2D1Multithread> compat_multithread;
    require(
        SUCCEEDED(compat_factory.As(&compat_base_factory)) &&
            SUCCEEDED(compat_factory.As(&compat_multithread)) &&
            has_same_com_identity(
                compat_factory.Get(), compat_base_factory.Get()) &&
            has_same_com_identity(
                compat_factory.Get(), compat_multithread.Get()) &&
            compat_multithread->GetMultithreadProtected() == TRUE,
        "ProGPU compatibility factory COM identity changed");
    compat_multithread->Enter();
    compat_multithread->Leave();

    const D2D1_RECT_F compat_rectangle_value = {2.0F, 3.0F, 12.0F, 11.0F};
    ComPtr<ID2D1RectangleGeometry> compat_rectangle;
    require(
        compat_factory->CreateRectangleGeometry(
            &compat_rectangle_value,
            &compat_rectangle) == S_OK &&
            compat_rectangle != nullptr,
        "ProGPU ID2D1RectangleGeometry creation failed");
    D2D1_RECT_F compat_returned_rectangle{};
    compat_rectangle->GetRect(&compat_returned_rectangle);
    require(
        compat_returned_rectangle.left == 2.0F &&
            compat_returned_rectangle.top == 3.0F &&
            compat_returned_rectangle.right == 12.0F &&
            compat_returned_rectangle.bottom == 11.0F,
        "ProGPU rectangle geometry changed its immutable rectangle");
    ComPtr<ID2D1Factory> rectangle_factory;
    compat_rectangle->GetFactory(&rectangle_factory);
    require(
        rectangle_factory != nullptr &&
            has_same_com_identity(
                rectangle_factory.Get(), compat_factory.Get()),
        "ProGPU rectangle geometry lost its factory identity");
    D2D1_RECT_F compat_bounds{};
    require(
        compat_rectangle->GetBounds(nullptr, &compat_bounds) == S_OK &&
            compat_bounds.left == 2.0F && compat_bounds.top == 3.0F &&
            compat_bounds.right == 12.0F && compat_bounds.bottom == 11.0F,
        "ProGPU rectangle geometry identity bounds changed");
    const D2D1_MATRIX_3X2_F compat_transform = {
        2.0F, 0.0F, 0.0F, 3.0F, 5.0F, -2.0F};
    require(
        compat_rectangle->GetBounds(
            &compat_transform, &compat_bounds) == S_OK &&
            compat_bounds.left == 9.0F && compat_bounds.top == 7.0F &&
            compat_bounds.right == 29.0F && compat_bounds.bottom == 31.0F,
        "ProGPU rectangle geometry transformed bounds changed");
    BOOL compat_contains = FALSE;
    require(
        compat_rectangle->FillContainsPoint(
            D2D1::Point2F(7.0F, 7.0F),
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &compat_contains) == S_OK &&
            compat_contains == TRUE,
        "ProGPU rectangle geometry rejected an interior point");
    require(
        compat_rectangle->FillContainsPoint(
            D2D1::Point2F(20.0F, 20.0F),
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &compat_contains) == S_OK &&
            compat_contains == FALSE,
        "ProGPU rectangle geometry accepted an exterior point");
    FLOAT compat_area = 0.0F;
    FLOAT compat_length = 0.0F;
    require(
        compat_rectangle->ComputeArea(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &compat_area) == S_OK &&
            compat_area == 80.0F &&
            compat_rectangle->ComputeLength(
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                &compat_length) == S_OK &&
            compat_length == 36.0F,
        "ProGPU rectangle geometry metrics changed");
    D2D1_POINT_2F compat_point{};
    D2D1_POINT_2F compat_tangent{};
    require(
        compat_rectangle->ComputePointAtLength(
            5.0F,
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &compat_point,
            &compat_tangent) == S_OK &&
            compat_point.x == 7.0F && compat_point.y == 3.0F &&
            compat_tangent.x == 1.0F && compat_tangent.y == 0.0F,
        "ProGPU rectangle geometry point-at-length changed");

    ComPtr<ID2D1PathGeometry1> compat_path;
    require(
        compat_factory->CreatePathGeometry(&compat_path) == S_OK &&
            compat_path != nullptr,
        "ProGPU ID2D1PathGeometry1 creation failed");
    ComPtr<ID2D1PathGeometry> compat_base_path;
    require(
        SUCCEEDED(compat_path.As(&compat_base_path)) &&
            has_same_com_identity(
                compat_path.Get(), compat_base_path.Get()),
        "ProGPU path geometry COM identity changed");
    ComPtr<ID2D1Factory> compat_path_factory;
    compat_path->GetFactory(&compat_path_factory);
    require(
        compat_path_factory != nullptr &&
            has_same_com_identity(
                compat_path_factory.Get(), compat_factory.Get()),
        "ProGPU path geometry lost its factory identity");
    ComPtr<ID2D1GeometrySink> compat_path_sink;
    require(
        compat_path->Open(&compat_path_sink) == S_OK &&
            compat_path_sink != nullptr,
        "ProGPU ID2D1GeometrySink creation failed");
    ComPtr<ID2D1SimplifiedGeometrySink> compat_simplified_sink;
    require(
        SUCCEEDED(compat_path_sink.As(&compat_simplified_sink)) &&
            has_same_com_identity(
                compat_path_sink.Get(), compat_simplified_sink.Get()),
        "ProGPU geometry sink COM identity changed");
    UINT32 compat_segment_count = 0U;
    require(
        compat_path->GetSegmentCount(&compat_segment_count) ==
            D2DERR_WRONG_STATE,
        "open ProGPU path geometry did not fail closed");
    record_path_vocabulary(compat_path_sink.Get());
    require(
        compat_path_sink->Close() == S_OK &&
            compat_path->GetSegmentCount(&compat_segment_count) == S_OK &&
            compat_segment_count == 5U,
        "ProGPU path geometry segment vocabulary changed");
    UINT32 compat_figure_count = 0U;
    require(
        compat_path->GetFigureCount(&compat_figure_count) == S_OK &&
            compat_figure_count == 1U,
        "ProGPU path geometry figure count changed");
    D2D1_RECT_F compat_path_bounds{};
    require(
        compat_path->GetBounds(nullptr, &compat_path_bounds) == S_OK &&
            compat_path_bounds.left <= 0.0F &&
            compat_path_bounds.top == 0.0F &&
            compat_path_bounds.right >= 12.0F &&
            compat_path_bounds.bottom >= 11.0F,
        "ProGPU path geometry bounds changed");

    ComPtr<ID2D1PathGeometry1> streamed_compat_path;
    ComPtr<ID2D1GeometrySink> streamed_compat_sink;
    require(
        compat_factory->CreatePathGeometry(&streamed_compat_path) == S_OK &&
            streamed_compat_path->Open(&streamed_compat_sink) == S_OK &&
            compat_path->Stream(streamed_compat_sink.Get()) == S_OK &&
            streamed_compat_sink->Close() == S_OK &&
            streamed_compat_path->GetSegmentCount(
                &compat_segment_count) == S_OK &&
            compat_segment_count == 5U,
        "ProGPU path geometry streaming changed its segment vocabulary");

    ComPtr<ID2D1PathGeometry1> compat_rectangle_path;
    ComPtr<ID2D1GeometrySink> compat_rectangle_path_sink;
    require(
        compat_factory->CreatePathGeometry(&compat_rectangle_path) == S_OK &&
            compat_rectangle_path->Open(&compat_rectangle_path_sink) == S_OK,
        "ProGPU rectangle path creation failed");
    record_rectangle_path(compat_rectangle_path_sink.Get());
    require(
        compat_rectangle_path_sink->Close() == S_OK &&
            compat_rectangle_path->ComputeArea(
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                &compat_area) == S_OK &&
            compat_area == 80.0F &&
            compat_rectangle_path->ComputeLength(
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                &compat_length) == S_OK &&
            compat_length == 36.0F,
        "ProGPU rectangle path metrics changed");
    require(
        compat_rectangle_path->FillContainsPoint(
            D2D1::Point2F(5.0F, 4.0F),
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &compat_contains) == S_OK &&
            compat_contains == TRUE &&
        compat_rectangle_path->FillContainsPoint(
            D2D1::Point2F(15.0F, 4.0F),
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &compat_contains) == S_OK &&
            compat_contains == FALSE,
        "ProGPU rectangle path containment changed");
    D2D1_POINT_DESCRIPTION compat_point_description{};
    require(
        compat_rectangle_path->ComputePointAndSegmentAtLength(
            5.0F,
            0U,
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &compat_point_description) == S_OK &&
            compat_point_description.point.x == 5.0F &&
            compat_point_description.point.y == 0.0F &&
            compat_point_description.endSegment == 0U &&
            compat_point_description.endFigure == 0U &&
            compat_point_description.lengthToEndSegment == 10.0F,
        "ProGPU path point-and-segment query changed");
    ComPtr<ID2D1EllipseGeometry> unsupported_ellipse;
    const D2D1_ELLIPSE ellipse = {D2D1::Point2F(0.0F, 0.0F), 1.0F, 1.0F};
    require(
        compat_factory->CreateEllipseGeometry(
            &ellipse, &unsupported_ellipse) == E_NOTIMPL &&
            unsupported_ellipse == nullptr,
        "unsupported ProGPU compatibility geometry did not fail closed");

    const progpu_native_direct2d_color_f compat_brush_color = {
        0.25F, 0.5F, 0.75F, 1.0F};
    const progpu_native_direct2d_brush_properties compat_brush_properties = {
        0.75F,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}};
    void* compat_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_compat_factory_create_solid_color_brush(
            compat_factory.Get(),
            &compat_brush_color,
            &compat_brush_properties,
            &compat_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            compat_brush_value != nullptr && native_hresult == S_OK,
        "ProGPU ID2D1SolidColorBrush compatibility creation failed");
    ComPtr<ID2D1SolidColorBrush> compat_solid_brush;
    compat_solid_brush.Attach(
        static_cast<ID2D1SolidColorBrush*>(compat_brush_value));
    ComPtr<ID2D1Brush> compat_base_brush;
    require(
        SUCCEEDED(compat_solid_brush.As(&compat_base_brush)) &&
            has_same_com_identity(
                compat_solid_brush.Get(), compat_base_brush.Get()),
        "ProGPU solid brush COM identity changed");
    ComPtr<ID2D1Factory> compat_brush_factory;
    compat_solid_brush->GetFactory(&compat_brush_factory);
    const D2D1_COLOR_F initial_compat_color =
        compat_solid_brush->GetColor();
    require(
        compat_brush_factory != nullptr &&
            has_same_com_identity(
                compat_brush_factory.Get(), compat_factory.Get()) &&
            initial_compat_color.r == 0.25F &&
            initial_compat_color.g == 0.5F &&
            initial_compat_color.b == 0.75F &&
            initial_compat_color.a == 1.0F &&
            compat_solid_brush->GetOpacity() == 0.75F,
        "ProGPU solid brush initial state or factory identity changed");
    const D2D1_COLOR_F final_compat_color =
        D2D1::ColorF(0.875F, 0.375F, 0.125F, 1.0F);
    const D2D1_MATRIX_3X2_F compat_brush_transform = {
        1.0F, 0.0F, 0.0F, 1.0F, 3.0F, 4.0F};
    compat_solid_brush->SetColor(&final_compat_color);
    compat_solid_brush->SetOpacity(0.625F);
    compat_solid_brush->SetTransform(&compat_brush_transform);
    D2D1_MATRIX_3X2_F returned_compat_brush_transform{};
    compat_solid_brush->GetTransform(&returned_compat_brush_transform);
    const D2D1_COLOR_F returned_compat_color =
        compat_solid_brush->GetColor();
    require(
        returned_compat_color.r == 0.875F &&
            returned_compat_color.g == 0.375F &&
            returned_compat_color.b == 0.125F &&
            returned_compat_color.a == 1.0F &&
            compat_solid_brush->GetOpacity() == 0.625F &&
            returned_compat_brush_transform._31 == 3.0F &&
            returned_compat_brush_transform._32 == 4.0F,
        "ProGPU solid brush mutable state changed");
    const D2D1_COLOR_F invalid_compat_mutation_color = {
        std::numeric_limits<float>::quiet_NaN(), 0.0F, 0.0F, 1.0F};
    compat_solid_brush->SetColor(&invalid_compat_mutation_color);
    compat_solid_brush->SetOpacity(-1.0F);
    require(
        compat_solid_brush->GetColor().r == 0.875F &&
            compat_solid_brush->GetOpacity() == 0.625F,
        "invalid ProGPU solid brush mutation escaped validation");
    void* invalid_compat_brush = reinterpret_cast<void*>(1);
    const progpu_native_direct2d_color_f invalid_compat_creation_color = {
        std::numeric_limits<float>::quiet_NaN(), 0.0F, 0.0F, 1.0F};
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_compat_factory_create_solid_color_brush(
            compat_factory.Get(),
            &invalid_compat_creation_color,
            nullptr,
            &invalid_compat_brush,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            invalid_compat_brush == nullptr && native_hresult == E_INVALIDARG,
        "invalid ProGPU compatibility brush did not fail closed");

    progpu_native_direct2d_surface_options invalid_options{};
    invalid_options.struct_size = sizeof(invalid_options);
    invalid_options.dpi_x = 96.0F;
    invalid_options.dpi_y = 96.0F;
    progpu_native_direct2d_surface* invalid_surface = nullptr;
    native_hresult = S_OK;
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

    progpu_native_direct2d_device_loss_state invalid_loss_state{};
    require(
        progpu_native_direct2d_surface_get_device_loss_state(
            surface,
            &invalid_loss_state) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT,
        "undersized device-loss state did not fail closed");
    progpu_native_direct2d_device_loss_state loss_state{};
    loss_state.struct_size = sizeof(loss_state);
    require(
        progpu_native_direct2d_surface_get_device_loss_state(
            surface,
            &loss_state) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "Direct2D device-loss state query failed");
    require(
        loss_state.resource_generation != 0U,
        "Direct2D resource generation was zero");
    require(
        loss_state.reason_hresult == S_OK &&
        (loss_state.flags &
         (PROGPU_NATIVE_DIRECT2D_DEVICE_LOSS_FLAG_DEVICE_LOST |
          PROGPU_NATIVE_DIRECT2D_DEVICE_LOSS_FLAG_REMOVAL_EVENT_SIGNALED)) ==
            0U,
        "new Direct2D device domain reported device loss");
    require(
        (loss_state.flags &
         PROGPU_NATIVE_DIRECT2D_DEVICE_LOSS_FLAG_REMOVAL_EVENT_REGISTERED) !=
            0U,
        "ID3D11Device4 removal event was not registered");

    progpu_native_direct2d_surface* replacement_surface = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create(
            &options,
            &replacement_surface,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            replacement_surface != nullptr && native_hresult == S_OK,
        "replacement Direct2D surface creation failed");
    progpu_native_direct2d_device_loss_state replacement_loss_state{};
    replacement_loss_state.struct_size = sizeof(replacement_loss_state);
    require(
        progpu_native_direct2d_surface_get_device_loss_state(
            replacement_surface,
            &replacement_loss_state) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        replacement_loss_state.resource_generation != 0U &&
        replacement_loss_state.resource_generation !=
            loss_state.resource_generation,
        "replacement Direct2D surface reused a resource generation");
    progpu_native_direct2d_surface_destroy(replacement_surface);

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
    auto context5 = get_interface<ID2D1DeviceContext5>(
        surface,
        PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT5);
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

    require(factory1 && factory2 && device && context && context5 && bitmap &&
            base_context && d3d_device && texture && winrt_d3d_device &&
            dwrite_factory,
        "one or more genuine COM interfaces were unavailable");

    ComPtr<ID2D1PathGeometry1> system_path;
    ComPtr<ID2D1GeometrySink> system_path_sink;
    require(
        factory1->CreatePathGeometry(&system_path) == S_OK &&
            system_path->Open(&system_path_sink) == S_OK,
        "system Direct2D path geometry creation failed");
    record_path_vocabulary(system_path_sink.Get());
    require(
        system_path_sink->Close() == S_OK,
        "system Direct2D path vocabulary recording failed");
    UINT32 system_segment_count = 0U;
    UINT32 system_figure_count = 0U;
    D2D1_RECT_F system_path_bounds{};
    FLOAT compat_path_length = 0.0F;
    FLOAT system_path_length = 0.0F;
    require(
        system_path->GetSegmentCount(&system_segment_count) == S_OK &&
            system_path->GetFigureCount(&system_figure_count) == S_OK &&
            system_path->GetBounds(nullptr, &system_path_bounds) == S_OK &&
            system_path->ComputeLength(
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                &system_path_length) == S_OK &&
            compat_path->ComputeLength(
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                &compat_path_length) == S_OK,
        "Direct2D path differential queries failed");
    require(
        system_segment_count == compat_segment_count &&
            system_figure_count == compat_figure_count,
        "ProGPU path counts diverged from system Direct2D");
    require(
        approximately_equal(
            system_path_bounds.left, compat_path_bounds.left, 0.05F) &&
            approximately_equal(
                system_path_bounds.top, compat_path_bounds.top, 0.05F) &&
            approximately_equal(
                system_path_bounds.right, compat_path_bounds.right, 0.05F) &&
            approximately_equal(
                system_path_bounds.bottom, compat_path_bounds.bottom, 0.05F),
        "ProGPU path bounds diverged from system Direct2D");
    require(
        approximately_equal(
            system_path_length, compat_path_length, 0.05F),
        "ProGPU path length diverged from system Direct2D");

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
    gradient_brush_properties.transform.m31 = 4.0F;
    gradient_brush_properties.transform.m32 = -2.0F;
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

    progpu_native_direct2d_brush_properties mutable_brush_properties{};
    mutable_brush_properties.opacity = 0.5F;
    mutable_brush_properties.transform = {1.0F, 0.0F, 0.0F, 1.0F, 3.0F, 4.0F};
    require(
        progpu_native_direct2d_brush_set_properties(
            surface, solid_brush.Get(), &mutable_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider common brush property update failed");
    progpu_native_direct2d_brush_properties returned_brush_properties{};
    require(
        progpu_native_direct2d_brush_get_properties(
            surface, solid_brush.Get(), &returned_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_brush_properties.opacity == 0.5F &&
            returned_brush_properties.transform.m31 == 3.0F &&
            returned_brush_properties.transform.m32 == 4.0F,
        "provider common brush property query failed");
    const progpu_native_direct2d_color_f mutable_solid_color = {
        0.25F, 0.5F, 0.75F, 1.0F
    };
    require(
        progpu_native_direct2d_solid_color_brush_set_color(
            surface, solid_brush.Get(), &mutable_solid_color,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider solid-brush color update failed");
    progpu_native_direct2d_color_f returned_solid_color{};
    require(
        progpu_native_direct2d_solid_color_brush_get_color(
            surface, solid_brush.Get(), &returned_solid_color,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_solid_color.red == mutable_solid_color.red &&
            returned_solid_color.green == mutable_solid_color.green &&
            returned_solid_color.blue == mutable_solid_color.blue,
        "provider solid-brush color query failed");
    const progpu_native_direct2d_linear_gradient_brush_properties
        mutable_linear_properties{{1.0F, 2.0F}, {31.0F, 42.0F}};
    require(
        progpu_native_direct2d_linear_gradient_brush_set_properties(
            surface, linear_brush.Get(), &mutable_linear_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider linear-gradient property update failed");
    progpu_native_direct2d_linear_gradient_brush_properties
        returned_linear_properties{};
    require(
        progpu_native_direct2d_linear_gradient_brush_get_properties(
            surface, linear_brush.Get(), &returned_linear_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_linear_properties.start_point.x == 1.0F &&
            returned_linear_properties.end_point.y == 42.0F,
        "provider linear-gradient property query failed");
    const progpu_native_direct2d_radial_gradient_brush_properties
        mutable_radial_properties{{12.0F, 14.0F}, {1.0F, 2.0F}, 9.0F, 7.0F};
    require(
        progpu_native_direct2d_radial_gradient_brush_set_properties(
            surface, radial_brush.Get(), &mutable_radial_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider radial-gradient property update failed");
    progpu_native_direct2d_radial_gradient_brush_properties
        returned_radial_properties{};
    require(
        progpu_native_direct2d_radial_gradient_brush_get_properties(
            surface, radial_brush.Get(), &returned_radial_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_radial_properties.center.x == 12.0F &&
            returned_radial_properties.gradient_origin_offset.y == 2.0F &&
            returned_radial_properties.radius_x == 9.0F &&
            returned_radial_properties.radius_y == 7.0F,
        "provider radial-gradient property query failed");
    const progpu_native_direct2d_brush_properties restored_brush_properties = {
        1.0F, {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}
    };
    require(
        progpu_native_direct2d_brush_set_properties(
            surface, solid_brush.Get(), &restored_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_solid_color_brush_set_color(
            surface, solid_brush.Get(), &solid_color,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider solid brush restore failed");

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

    progpu_native_direct2d_rect_f geometry_bounds{};
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_get_bounds(
            surface,
            rectangle_geometry.Get(),
            nullptr,
            &geometry_bounds,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && geometry_bounds.x == 4.0F &&
            geometry_bounds.y == 4.0F && geometry_bounds.width == 16.0F &&
            geometry_bounds.height == 12.0F,
        "provider ID2D1Geometry bounds changed");

    progpu_native_direct2d_rect_f widened_bounds{};
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_get_widened_bounds(
            surface,
            rectangle_geometry.Get(),
            2.0F,
            nullptr,
            nullptr,
            0.25F,
            &widened_bounds,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && widened_bounds.x == 3.0F &&
            widened_bounds.y == 3.0F && widened_bounds.width == 18.0F &&
            widened_bounds.height == 14.0F,
        "provider ID2D1Geometry widened bounds changed");

    const progpu_native_direct2d_point_2f inside_point{8.0F, 8.0F};
    uint32_t contains = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_fill_contains_point(
            surface,
            rectangle_geometry.Get(),
            &inside_point,
            nullptr,
            0.25F,
            &contains,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && contains == 1U,
        "provider ID2D1Geometry fill hit testing changed");

    const progpu_native_direct2d_point_2f stroke_point{4.0F, 8.0F};
    contains = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_stroke_contains_point(
            surface,
            rectangle_geometry.Get(),
            &stroke_point,
            2.0F,
            nullptr,
            nullptr,
            0.25F,
            &contains,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && contains == 1U,
        "provider ID2D1Geometry stroke hit testing changed");

    progpu_native_direct2d_geometry_relation geometry_relation =
        PROGPU_NATIVE_DIRECT2D_GEOMETRY_RELATION_UNKNOWN;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_compare(
            surface,
            rectangle_geometry.Get(),
            ellipse_geometry.Get(),
            nullptr,
            0.25F,
            &geometry_relation,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            geometry_relation ==
                PROGPU_NATIVE_DIRECT2D_GEOMETRY_RELATION_DISJOINT,
        "provider ID2D1Geometry comparison changed");

    float geometry_area = 0.0F;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_compute_area(
            surface,
            rectangle_geometry.Get(),
            nullptr,
            0.25F,
            &geometry_area,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && geometry_area == 192.0F,
        "provider ID2D1Geometry area changed");

    float geometry_length = 0.0F;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_compute_length(
            surface,
            rectangle_geometry.Get(),
            nullptr,
            0.25F,
            &geometry_length,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && geometry_length == 56.0F,
        "provider ID2D1Geometry length changed");

    progpu_native_direct2d_point_2f sampled_point{};
    progpu_native_direct2d_point_2f sampled_tangent{};
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_compute_point_at_length(
            surface,
            rectangle_geometry.Get(),
            4.0F,
            nullptr,
            0.25F,
            &sampled_point,
            &sampled_tangent,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && std::isfinite(sampled_point.x) &&
            std::isfinite(sampled_point.y) &&
            std::isfinite(sampled_tangent.x) &&
            std::isfinite(sampled_tangent.y) &&
            std::abs(
                sampled_tangent.x * sampled_tangent.x +
                sampled_tangent.y * sampled_tangent.y - 1.0F) < 0.001F,
        "provider ID2D1Geometry point-at-length changed");

    widened_bounds = {1.0F, 1.0F, 1.0F, 1.0F};
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_geometry_get_widened_bounds(
            surface,
            rectangle_geometry.Get(),
            2.0F,
            nullptr,
            nullptr,
            0.0F,
            &widened_bounds,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            native_hresult == E_INVALIDARG && widened_bounds.x == 0.0F &&
            widened_bounds.y == 0.0F && widened_bounds.width == 0.0F &&
            widened_bounds.height == 0.0F,
        "invalid geometry-analysis tolerance did not fail closed");

    void* simplified_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_simplify(
            surface,
            rectangle_geometry.Get(),
            PROGPU_NATIVE_DIRECT2D_GEOMETRY_SIMPLIFICATION_LINES,
            nullptr,
            0.25F,
            &simplified_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            simplified_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1Geometry simplification failed");
    ComPtr<ID2D1PathGeometry1> simplified_geometry;
    simplified_geometry.Attach(
        static_cast<ID2D1PathGeometry1*>(simplified_geometry_value));
    uint32_t simplified_segment_count = 0U;
    require(SUCCEEDED(simplified_geometry->GetSegmentCount(
                &simplified_segment_count)) && simplified_segment_count != 0U,
        "provider simplified geometry was empty");

    void* outlined_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_outline(
            surface,
            rectangle_geometry.Get(),
            nullptr,
            0.25F,
            &outlined_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            outlined_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1Geometry outline failed");
    ComPtr<ID2D1PathGeometry1> outlined_geometry;
    outlined_geometry.Attach(
        static_cast<ID2D1PathGeometry1*>(outlined_geometry_value));
    uint32_t outlined_figure_count = 0U;
    require(SUCCEEDED(outlined_geometry->GetFigureCount(
                &outlined_figure_count)) && outlined_figure_count != 0U,
        "provider outlined geometry was empty");

    void* widened_geometry_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_widen(
            surface,
            rectangle_geometry.Get(),
            2.0F,
            nullptr,
            nullptr,
            0.25F,
            &widened_geometry_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            widened_geometry_value != nullptr && native_hresult == S_OK,
        "provider ID2D1Geometry widening failed");
    ComPtr<ID2D1PathGeometry1> widened_geometry;
    widened_geometry.Attach(
        static_cast<ID2D1PathGeometry1*>(widened_geometry_value));
    progpu_native_direct2d_rect_f materialized_widened_bounds{};
    require(
        progpu_native_direct2d_geometry_get_bounds(
            surface,
            widened_geometry.Get(),
            nullptr,
            &materialized_widened_bounds,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            materialized_widened_bounds.x == 3.0F &&
            materialized_widened_bounds.y == 3.0F &&
            materialized_widened_bounds.width == 18.0F &&
            materialized_widened_bounds.height == 14.0F,
        "provider materialized widened geometry bounds changed");

    uint32_t required_triangle_count = 0U;
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_geometry_tessellate(
            surface,
            rectangle_geometry.Get(),
            nullptr,
            0.25F,
            nullptr,
            0U,
            &required_triangle_count,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            required_triangle_count == 2U &&
            native_hresult == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER),
        "provider tessellation size query changed");
    progpu_native_direct2d_triangle tessellation[2]{};
    required_triangle_count = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_geometry_tessellate(
            surface,
            rectangle_geometry.Get(),
            nullptr,
            0.25F,
            tessellation,
            static_cast<uint32_t>(std::size(tessellation)),
            &required_triangle_count,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            required_triangle_count == 2U && native_hresult == S_OK,
        "provider caller-span tessellation failed");
    float tessellated_area = 0.0F;
    for (const progpu_native_direct2d_triangle& triangle : tessellation) {
        tessellated_area += 0.5F * std::abs(
            triangle.point1.x * (triangle.point2.y - triangle.point3.y) +
            triangle.point2.x * (triangle.point3.y - triangle.point1.y) +
            triangle.point3.x * (triangle.point1.y - triangle.point2.y));
    }
    require(std::abs(tessellated_area - 192.0F) < 0.001F,
        "provider tessellation coverage changed");

    void* filled_realization_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_filled_geometry_realization(
            surface,
            rectangle_geometry.Get(),
            0.25F,
            &filled_realization_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            filled_realization_value != nullptr && native_hresult == S_OK,
        "provider filled ID2D1GeometryRealization creation failed");
    ComPtr<ID2D1GeometryRealization> filled_realization;
    filled_realization.Attach(
        static_cast<ID2D1GeometryRealization*>(filled_realization_value));

    void* stroked_realization_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_stroked_geometry_realization(
            surface,
            rectangle_geometry.Get(),
            0.25F,
            2.0F,
            nullptr,
            &stroked_realization_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            stroked_realization_value != nullptr && native_hresult == S_OK,
        "provider stroked ID2D1GeometryRealization creation failed");
    ComPtr<ID2D1GeometryRealization> stroked_realization;
    stroked_realization.Attach(
        static_cast<ID2D1GeometryRealization*>(stroked_realization_value));

    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_draw_geometry_realization(
            surface,
            filled_realization.Get(),
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "geometry realization draw outside a producer did not fail closed");
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_surface_clear(
            surface,
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "Direct2D clear outside a producer did not fail closed");

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
    progpu_native_direct2d_bitmap_descriptor bitmap_descriptor{};
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_bitmap_get_descriptor(
            surface,
            source_bitmap.Get(),
            &bitmap_descriptor,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            bitmap_descriptor.struct_size == static_cast<uint32_t>(sizeof(bitmap_descriptor)) &&
            bitmap_descriptor.pixel_width == 2U &&
            bitmap_descriptor.pixel_height == 2U &&
            std::abs(bitmap_descriptor.width - (96.0F / 120.0F * 2.0F)) < 0.0001F &&
            std::abs(bitmap_descriptor.height - (96.0F / 144.0F * 2.0F)) < 0.0001F &&
            bitmap_descriptor.dpi_x == 120.0F &&
            bitmap_descriptor.dpi_y == 144.0F &&
            bitmap_descriptor.dxgi_format == DXGI_FORMAT_B8G8R8A8_UNORM &&
            bitmap_descriptor.alpha_mode == D2D1_ALPHA_MODE_PREMULTIPLIED &&
            bitmap_descriptor.options == static_cast<uint32_t>(D2D1_BITMAP_OPTIONS_NONE),
        "provider typed ID2D1Bitmap1 descriptor changed");

    const uint8_t zero_bitmap_pixels[16]{};
    void* mutable_bitmap_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_bitmap(
            surface,
            &bitmap_properties,
            zero_bitmap_pixels,
            sizeof(zero_bitmap_pixels),
            &mutable_bitmap_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            mutable_bitmap_value != nullptr && native_hresult == S_OK,
        "provider mutable ID2D1Bitmap1 creation failed");
    ComPtr<ID2D1Bitmap1> mutable_bitmap;
    mutable_bitmap.Attach(static_cast<ID2D1Bitmap1*>(mutable_bitmap_value));
    const uint8_t memory_update_pixel[] = {17U, 34U, 51U, 255U};
    const progpu_native_direct2d_rect_u memory_update_rectangle = {
        0U, 0U, 1U, 1U
    };
    require(
        progpu_native_direct2d_bitmap_copy_from_memory(
            surface,
            mutable_bitmap.Get(),
            &memory_update_rectangle,
            memory_update_pixel,
            sizeof(memory_update_pixel),
            4U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed ID2D1Bitmap1 memory update failed");
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_bitmap_copy_from_memory(
            surface,
            mutable_bitmap.Get(),
            &memory_update_rectangle,
            memory_update_pixel,
            sizeof(memory_update_pixel) - 1U,
            4U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            native_hresult == E_INVALIDARG,
        "truncated ID2D1Bitmap1 memory update did not fail closed");
    const progpu_native_direct2d_point_2u bitmap_copy_destination = {1U, 0U};
    const progpu_native_direct2d_rect_u bitmap_copy_source = {
        0U, 1U, 1U, 1U
    };
    require(
        progpu_native_direct2d_bitmap_copy_from_bitmap(
            surface,
            mutable_bitmap.Get(),
            &bitmap_copy_destination,
            source_bitmap.Get(),
            &bitmap_copy_source,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider same-device ID2D1Bitmap1 GPU copy failed");
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_bitmap_copy_from_bitmap(
            surface,
            mutable_bitmap.Get(),
            nullptr,
            mutable_bitmap.Get(),
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            native_hresult == E_INVALIDARG,
        "self ID2D1Bitmap1 copy did not fail closed");

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

    const progpu_native_direct2d_bitmap_brush_properties
        updated_bitmap_brush_properties = {
            PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP,
            PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP,
            PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_LINEAR
        };
    require(
        progpu_native_direct2d_bitmap_brush_set_properties(
            surface,
            bitmap_brush.Get(),
            &updated_bitmap_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider bitmap-brush property update failed");
    progpu_native_direct2d_bitmap_brush_properties
        returned_bitmap_brush_properties{};
    require(
        progpu_native_direct2d_bitmap_brush_get_properties(
            surface,
            bitmap_brush.Get(),
            &returned_bitmap_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_bitmap_brush_properties.extend_mode_x ==
                PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP &&
            returned_bitmap_brush_properties.extend_mode_y ==
                PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_WRAP &&
            returned_bitmap_brush_properties.interpolation_mode ==
                PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_LINEAR,
        "provider bitmap-brush property query failed");
    require(
        progpu_native_direct2d_bitmap_brush_set_bitmap(
            surface,
            bitmap_brush.Get(),
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider bitmap-brush null bitmap update failed");
    void* returned_bitmap_value = reinterpret_cast<void*>(
        static_cast<uintptr_t>(1U));
    require(
        progpu_native_direct2d_bitmap_brush_get_bitmap(
            surface,
            bitmap_brush.Get(),
            &returned_bitmap_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_bitmap_value == nullptr,
        "provider bitmap-brush null bitmap query failed");
    require(
        progpu_native_direct2d_bitmap_brush_set_bitmap(
            surface,
            bitmap_brush.Get(),
            source_bitmap.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider bitmap-brush bitmap restore failed");
    require(
        progpu_native_direct2d_bitmap_brush_get_bitmap(
            surface,
            bitmap_brush.Get(),
            &returned_bitmap_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_bitmap_value != nullptr,
        "provider bitmap-brush bitmap query failed");
    ComPtr<ID2D1Bitmap1> returned_bitmap;
    returned_bitmap.Attach(static_cast<ID2D1Bitmap1*>(returned_bitmap_value));
    require(has_same_com_identity(source_bitmap.Get(), returned_bitmap.Get()),
        "provider bitmap-brush bitmap query changed COM identity");
    require(
        progpu_native_direct2d_bitmap_brush_set_properties(
            surface,
            bitmap_brush.Get(),
            &bitmap_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider bitmap-brush property restore failed");

    const progpu_native_direct2d_image_brush_properties
        updated_image_brush_properties = {
            {0.0F, 0.0F, 2.0F, 2.0F},
            PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP,
            PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_MIRROR,
            PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_LINEAR
        };
    require(
        progpu_native_direct2d_image_brush_set_properties(
            surface,
            image_brush.Get(),
            &updated_image_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider image-brush property update failed");
    progpu_native_direct2d_image_brush_properties
        returned_image_brush_properties{};
    require(
        progpu_native_direct2d_image_brush_get_properties(
            surface,
            image_brush.Get(),
            &returned_image_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_image_brush_properties.source_rectangle.width == 2.0F &&
            returned_image_brush_properties.source_rectangle.height == 2.0F &&
            returned_image_brush_properties.extend_mode_x ==
                PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP &&
            returned_image_brush_properties.extend_mode_y ==
                PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_MIRROR &&
            returned_image_brush_properties.interpolation_mode ==
                PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_LINEAR,
        "provider image-brush property query failed");
    require(
        progpu_native_direct2d_image_brush_set_image(
            surface,
            image_brush.Get(),
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider image-brush null image update failed");
    void* returned_image_value = reinterpret_cast<void*>(
        static_cast<uintptr_t>(1U));
    require(
        progpu_native_direct2d_image_brush_get_image(
            surface,
            image_brush.Get(),
            &returned_image_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_image_value == nullptr,
        "provider image-brush null image query failed");
    require(
        progpu_native_direct2d_image_brush_set_image(
            surface,
            image_brush.Get(),
            source_bitmap.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider image-brush image restore failed");
    require(
        progpu_native_direct2d_image_brush_get_image(
            surface,
            image_brush.Get(),
            &returned_image_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_image_value != nullptr,
        "provider image-brush image query failed");
    ComPtr<ID2D1Image> returned_image;
    returned_image.Attach(static_cast<ID2D1Image*>(returned_image_value));
    require(has_same_com_identity(source_bitmap.Get(), returned_image.Get()),
        "provider image-brush image query changed COM identity");
    require(
        progpu_native_direct2d_image_brush_set_properties(
            surface,
            image_brush.Get(),
            &image_brush_properties,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider image-brush property restore failed");

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
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_clear(
            surface,
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1DeviceContext clear failed");
    const progpu_native_direct2d_matrix_3x2_f command_transform = {
        1.0F, 0.0F, 0.0F, 1.0F, 1.0F, 2.0F
    };
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_set_transform(
            surface,
            &command_transform,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1DeviceContext transform set failed");
    progpu_native_direct2d_matrix_3x2_f command_returned_transform{};
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_get_transform(
            surface,
            &command_returned_transform,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            command_returned_transform.m11 == 1.0F &&
            command_returned_transform.m22 == 1.0F &&
            command_returned_transform.m31 == 1.0F &&
            command_returned_transform.m32 == 2.0F,
        "provider typed ID2D1DeviceContext transform get failed");
    const progpu_native_direct2d_matrix_3x2_f identity_transform = {
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F
    };
    require(
        progpu_native_direct2d_surface_set_transform(
            surface,
            &identity_transform,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D transform restore failed");
    require(
        progpu_native_direct2d_surface_set_antialias_mode(
            surface,
            PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_ALIASED,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D antialias state set failed");
    progpu_native_direct2d_antialias_mode returned_antialias_mode =
        PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_PER_PRIMITIVE;
    require(
        progpu_native_direct2d_surface_get_antialias_mode(
            surface,
            &returned_antialias_mode,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_antialias_mode ==
                PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_ALIASED,
        "provider Direct2D antialias state get failed");
    require(
        progpu_native_direct2d_surface_set_text_antialias_mode(
            surface,
            PROGPU_NATIVE_DIRECT2D_TEXT_ANTIALIAS_MODE_GRAYSCALE,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D text-antialias state set failed");
    progpu_native_direct2d_text_antialias_mode returned_text_antialias_mode =
        PROGPU_NATIVE_DIRECT2D_TEXT_ANTIALIAS_MODE_DEFAULT;
    require(
        progpu_native_direct2d_surface_get_text_antialias_mode(
            surface,
            &returned_text_antialias_mode,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_text_antialias_mode ==
                PROGPU_NATIVE_DIRECT2D_TEXT_ANTIALIAS_MODE_GRAYSCALE,
        "provider Direct2D text-antialias state get failed");
    require(
        progpu_native_direct2d_surface_set_primitive_blend(
            surface,
            PROGPU_NATIVE_DIRECT2D_PRIMITIVE_BLEND_ADD,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D primitive-blend state set failed");
    progpu_native_direct2d_primitive_blend returned_primitive_blend =
        PROGPU_NATIVE_DIRECT2D_PRIMITIVE_BLEND_SOURCE_OVER;
    require(
        progpu_native_direct2d_surface_get_primitive_blend(
            surface,
            &returned_primitive_blend,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_primitive_blend ==
                PROGPU_NATIVE_DIRECT2D_PRIMITIVE_BLEND_ADD,
        "provider Direct2D primitive-blend state get failed");
    require(
        progpu_native_direct2d_surface_set_unit_mode(
            surface,
            PROGPU_NATIVE_DIRECT2D_UNIT_MODE_PIXELS,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D unit-mode state set failed");
    progpu_native_direct2d_unit_mode returned_unit_mode =
        PROGPU_NATIVE_DIRECT2D_UNIT_MODE_DIPS;
    require(
        progpu_native_direct2d_surface_get_unit_mode(
            surface,
            &returned_unit_mode,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_unit_mode == PROGPU_NATIVE_DIRECT2D_UNIT_MODE_PIXELS,
        "provider Direct2D unit-mode state get failed");
    constexpr uint64_t expected_tag1 = UINT64_C(0x1122334455667788);
    constexpr uint64_t expected_tag2 = UINT64_C(0x8877665544332211);
    require(
        progpu_native_direct2d_surface_set_tags(
            surface,
            expected_tag1,
            expected_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D tags set failed");
    uint64_t returned_tag1 = 0U;
    uint64_t returned_tag2 = 0U;
    require(
        progpu_native_direct2d_surface_get_tags(
            surface,
            &returned_tag1,
            &returned_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_tag1 == expected_tag1 && returned_tag2 == expected_tag2,
        "provider Direct2D tags get failed");
    require(
        progpu_native_direct2d_surface_set_dpi(
            surface,
            144.0F,
            120.0F,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D DPI set failed");
    float returned_dpi_x = 0.0F;
    float returned_dpi_y = 0.0F;
    require(
        progpu_native_direct2d_surface_get_dpi(
            surface,
            &returned_dpi_x,
            &returned_dpi_y,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            returned_dpi_x == 144.0F && returned_dpi_y == 120.0F,
        "provider Direct2D DPI get failed");
    require(
        progpu_native_direct2d_surface_set_antialias_mode(
            surface,
            static_cast<progpu_native_direct2d_antialias_mode>(2U),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT,
        "unknown Direct2D antialias state did not fail closed");
    require(
        progpu_native_direct2d_surface_set_antialias_mode(
            surface,
            PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_PER_PRIMITIVE,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_set_text_antialias_mode(
            surface,
            PROGPU_NATIVE_DIRECT2D_TEXT_ANTIALIAS_MODE_DEFAULT,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_set_primitive_blend(
            surface,
            PROGPU_NATIVE_DIRECT2D_PRIMITIVE_BLEND_SOURCE_OVER,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_set_unit_mode(
            surface,
            PROGPU_NATIVE_DIRECT2D_UNIT_MODE_DIPS,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_set_tags(
            surface,
            0U,
            0U,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_set_dpi(
            surface,
            96.0F,
            96.0F,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider Direct2D drawing state restore failed");
    const progpu_native_direct2d_rect_f vector_rectangle = {
        0.0F, 0.0F, 16.0F, 16.0F
    };
    require(
        progpu_native_direct2d_surface_push_axis_aligned_clip(
            surface,
            &vector_rectangle,
            PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_ALIASED,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D axis-aligned clip push failed");
    require(
        progpu_native_direct2d_surface_fill_rectangle(
            surface,
            &vector_rectangle,
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D rectangle fill failed");
    require(
        progpu_native_direct2d_surface_draw_rectangle(
            surface,
            &vector_rectangle,
            solid_brush.Get(),
            1.0F,
            stroke_style.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D rectangle draw failed");
    const progpu_native_direct2d_rect_f rounded_rectangle = {
        2.0F, 2.0F, 12.0F, 12.0F
    };
    require(
        progpu_native_direct2d_surface_fill_rounded_rectangle(
            surface,
            &rounded_rectangle,
            2.0F,
            2.0F,
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_draw_rounded_rectangle(
            surface,
            &rounded_rectangle,
            2.0F,
            2.0F,
            solid_brush.Get(),
            1.0F,
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D rounded-rectangle operations failed");
    const progpu_native_direct2d_point_2f vector_ellipse_center = {
        8.0F, 8.0F
    };
    require(
        progpu_native_direct2d_surface_fill_ellipse(
            surface,
            vector_ellipse_center,
            4.0F,
            3.0F,
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_draw_ellipse(
            surface,
            vector_ellipse_center,
            5.0F,
            4.0F,
            solid_brush.Get(),
            1.0F,
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D ellipse operations failed");
    require(
        progpu_native_direct2d_surface_draw_line(
            surface,
            {1.0F, 1.0F},
            {15.0F, 15.0F},
            solid_brush.Get(),
            1.0F,
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D line draw failed");
    require(
        progpu_native_direct2d_surface_fill_geometry(
            surface,
            rectangle_geometry.Get(),
            solid_brush.Get(),
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
        progpu_native_direct2d_surface_draw_geometry(
            surface,
            path_geometry.Get(),
            solid_brush.Get(),
            1.0F,
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D geometry operations failed");
    const progpu_native_direct2d_rect_f bitmap_destination = {
        0.0F, 0.0F, 2.0F, 2.0F
    };
    const progpu_native_direct2d_rect_f bitmap_source = {
        0.0F, 0.0F, 2.0F, 2.0F
    };
    const progpu_native_direct2d_matrix_4x4_f bitmap_perspective = {
        1.0F, 0.0F, 0.0F, 0.0F,
        0.0F, 1.0F, 0.0F, 0.0F,
        0.0F, 0.0F, 1.0F, 0.0F,
        0.0F, 0.0F, 0.0F, 1.0F
    };
    require(
        progpu_native_direct2d_surface_draw_bitmap(
            surface,
            source_bitmap.Get(),
            &bitmap_destination,
            1.0F,
            PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
            &bitmap_source,
            &bitmap_perspective,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D bitmap draw failed");
    const progpu_native_direct2d_point_2f image_offset = {12.0F, 0.0F};
    require(
        progpu_native_direct2d_surface_draw_image(
            surface,
            source_bitmap.Get(),
            &image_offset,
            &bitmap_source,
            PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_LINEAR,
            PROGPU_NATIVE_DIRECT2D_COMPOSITE_MODE_SOURCE_OVER,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D image draw failed");
    require(
        progpu_native_direct2d_surface_pop_axis_aligned_clip(
            surface,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider typed Direct2D axis-aligned clip pop failed");
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

    progpu_native_direct2d_command_stream_summary command_summary{};
    command_summary.struct_size = static_cast<uint32_t>(sizeof(command_summary));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_command_list_get_stream_summary(
            surface,
            command_list.Get(),
            PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_OPTION_NONE,
            &command_summary,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            (command_summary.flags &
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_BALANCED_SCOPES) !=
                0U &&
            (command_summary.flags &
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_UNSUPPORTED_OPERATIONS) ==
                0U &&
            command_summary.state_change_count != 0U &&
            command_summary.clear_count == 1U &&
            command_summary.draw_count >= 7U &&
            command_summary.fill_count >= 4U &&
            command_summary.text_draw_count == 0U &&
            command_summary.image_draw_count == 2U &&
            command_summary.clip_push_count == 1U &&
            command_summary.clip_pop_count == 1U &&
            command_summary.layer_push_count == 0U &&
            command_summary.layer_pop_count == 0U &&
            command_summary.unsupported_operation_count == 0U &&
            command_summary.max_scope_depth == 1U &&
            command_summary.total_command_count ==
                command_summary.state_change_count +
                command_summary.clear_count +
                command_summary.draw_count +
                command_summary.fill_count +
                command_summary.clip_push_count +
                command_summary.clip_pop_count +
                command_summary.layer_push_count +
                command_summary.layer_pop_count,
        "provider ID2D1CommandSink1 supported-stream summary changed");
    command_summary.struct_size = static_cast<uint32_t>(sizeof(command_summary));
    require(
        progpu_native_direct2d_command_list_get_stream_summary(
            surface,
            command_list.Get(),
            PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_OPTION_REQUIRE_SUPPORTED_OPERATIONS,
            &command_summary,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "supported Direct2D command stream did not pass strict preflight");
    command_summary.struct_size = static_cast<uint32_t>(sizeof(command_summary));
    require(
        progpu_native_direct2d_command_list_get_stream_summary(
            surface,
            command_list.Get(),
            2U,
            &command_summary,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT &&
            native_hresult == E_INVALIDARG,
        "unknown Direct2D command-stream option did not fail closed");

    void* scene_command_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &scene_command_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            scene_command_list_value != nullptr && native_hresult == S_OK,
        "semantic scene command-list creation failed");
    ComPtr<ID2D1CommandList> scene_command_list;
    scene_command_list.Attach(
        static_cast<ID2D1CommandList*>(scene_command_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            scene_command_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "semantic scene command-list recording did not begin");
    context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
    context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
    context->SetTextRenderingParams(nullptr);
    context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_ALIASED);
    const D2D1_MATRIX_3X2_F scene_transform = D2D1::Matrix3x2F(
        1.25F, 0.0F, 0.0F, 0.75F, 3.0F, 5.0F);
    context->SetTransform(scene_transform);
    const D2D1_COLOR_F scene_clear =
        D2D1::ColorF(0.125F, 0.25F, 0.5F, 1.0F);
    context->Clear(&scene_clear);
    const D2D1_RECT_F scene_outer_clip = {0.0F, 0.0F, 30.0F, 30.0F};
    const D2D1_RECT_F scene_inner_clip = {10.0F, 10.0F, 50.0F, 50.0F};
    const D2D1_RECT_F scene_fill = {2.0F, 4.0F, 18.0F, 20.0F};
    const D2D1_RECT_F scene_linear_fill = {3.0F, 7.0F, 16.0F, 12.0F};
    const D2D1_RECT_F scene_linear_fill_identity =
        {18.0F, 7.0F, 26.0F, 12.0F};
    const D2D1_RECT_F scene_radial_fill = {12.0F, 10.0F, 28.0F, 22.0F};
    const D2D1_RECT_F scene_stroke = {22.0F, 5.0F, 40.0F, 24.0F};
    context->PushAxisAlignedClip(
        &scene_outer_clip,
        D2D1_ANTIALIAS_MODE_ALIASED);
    context->FillRectangle(&scene_fill, solid_brush.Get());
    context->FillRectangle(&scene_linear_fill, linear_brush.Get());
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    context->FillRectangle(
        &scene_linear_fill_identity,
        linear_brush.Get());
    context->SetTransform(scene_transform);
    context->PushAxisAlignedClip(
        &scene_inner_clip,
        D2D1_ANTIALIAS_MODE_ALIASED);
    context->DrawRectangle(&scene_stroke, solid_brush.Get(), 2.0F);
    context->FillRectangle(&scene_radial_fill, radial_brush.Get());
    context->PopAxisAlignedClip();
    context->DrawLine(
        D2D1::Point2F(4.0F, 28.0F),
        D2D1::Point2F(42.0F, 30.0F),
        solid_brush.Get(),
        3.0F);
    context->PopAxisAlignedClip();
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "semantic scene command-list recording did not close");

    progpu_native_direct2d_scene_stream_result scene_measure{};
    scene_measure.struct_size = static_cast<uint32_t>(sizeof(scene_measure));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            scene_command_list.Get(),
            7001U,
            9U,
            nullptr,
            0U,
            &scene_measure,
            &native_hresult) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            native_hresult == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) &&
            scene_measure.required_bytes >= sizeof(progpu_native_scene_header) &&
            scene_measure.written_bytes == 0U &&
            scene_measure.translated_draw_count == 6U &&
            scene_measure.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE &&
            (scene_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_LEADING_CLEAR) !=
                0U &&
            (scene_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_ALIASED_PRIMITIVES) !=
                0U &&
            (scene_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_AXIS_ALIGNED_CLIPS) !=
                0U &&
            (scene_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_GRADIENT_BRUSHES) !=
                0U &&
            scene_measure.clear_color.red == 0.125F &&
            scene_measure.clear_color.green == 0.25F &&
            scene_measure.clear_color.blue == 0.5F &&
            scene_measure.clear_color.alpha == 1.0F,
        "Direct2D command-list semantic scene size pass changed");
    std::vector<uint8_t> scene_stream(
        static_cast<size_t>(scene_measure.required_bytes));
    progpu_native_direct2d_scene_stream_result scene_write{};
    scene_write.struct_size = static_cast<uint32_t>(sizeof(scene_write));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            scene_command_list.Get(),
            7001U,
            9U,
            scene_stream.data(),
            scene_stream.size(),
            &scene_write,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            scene_write.required_bytes == scene_stream.size() &&
            scene_write.written_bytes == scene_stream.size() &&
            scene_write.command_count == 10U &&
            scene_write.resource_count == 9U &&
            scene_write.brush_count == 4U &&
            scene_write.translated_draw_count == 6U,
        "Direct2D command-list semantic scene write pass changed");

    progpu_native_direct2d_command_stream_summary recorder_hint{};
    recorder_hint.struct_size = static_cast<uint32_t>(sizeof(recorder_hint));
    recorder_hint.clear_count = 1U;
    recorder_hint.fill_count = 2U;
    recorder_hint.total_command_count = 3U;
    progpu_native_direct2d_scene_recorder* direct_recorder = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_scene_recorder_create(
            7002U,
            10U,
            &recorder_hint,
            &direct_recorder,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            direct_recorder != nullptr && native_hresult == S_OK,
        "ProGPU Direct2D COM scene recorder creation failed");
    void* direct_sink_value = nullptr;
    require(
        progpu_native_direct2d_scene_recorder_get_command_sink(
            direct_recorder,
            &direct_sink_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            direct_sink_value != nullptr && native_hresult == S_OK,
        "ProGPU ID2D1CommandSink1 acquisition failed");
    ComPtr<ID2D1CommandSink1> direct_sink;
    direct_sink.Attach(static_cast<ID2D1CommandSink1*>(direct_sink_value));
    ComPtr<ID2D1CommandSink> direct_base_sink;
    require(
        SUCCEEDED(direct_sink.As(&direct_base_sink)) &&
            has_same_com_identity(direct_sink.Get(), direct_base_sink.Get()),
        "ProGPU command sink did not expose genuine Direct2D COM identity");
    progpu_native_direct2d_scene_stream_result incomplete_recorder{};
    incomplete_recorder.struct_size =
        static_cast<uint32_t>(sizeof(incomplete_recorder));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_scene_recorder_build_stream(
            direct_recorder,
            nullptr,
            0U,
            &incomplete_recorder,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH &&
            native_hresult == D2DERR_WRONG_STATE &&
            incomplete_recorder.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_DRAWING_STATE,
        "incomplete ProGPU Direct2D COM recording did not fail closed");
    const D2D1_COLOR_F direct_clear =
        D2D1::ColorF(0.0625F, 0.125F, 0.25F, 1.0F);
    const D2D1_RECT_F direct_fill = {4.0F, 6.0F, 20.0F, 18.0F};
    const D2D1_MATRIX_3X2_F direct_transform =
        D2D1::Matrix3x2F::Identity();
    require(
        direct_sink->BeginDraw() == S_OK &&
            direct_sink->SetPrimitiveBlend(
                D2D1_PRIMITIVE_BLEND_SOURCE_OVER) == S_OK &&
            direct_sink->SetUnitMode(D2D1_UNIT_MODE_DIPS) == S_OK &&
            direct_sink->SetTransform(
                &direct_transform) == S_OK,
        "ProGPU Direct2D COM recording state initialization failed");
    require(
        direct_sink->Clear(&direct_clear) == S_OK,
        "ProGPU Direct2D COM Clear callback failed");
    require(
        direct_sink->FillRectangle(
            &direct_fill,
            compat_solid_brush.Get()) == S_OK,
        "ProGPU Direct2D COM FillRectangle callback failed");
    require(
        direct_sink->FillGeometry(
            compat_path.Get(),
            compat_solid_brush.Get(),
            nullptr) == S_OK,
        "ProGPU Direct2D COM FillGeometry callback failed");
    require(
        direct_sink->EndDraw() == S_OK,
        "ProGPU Direct2D COM EndDraw callback failed");
    progpu_native_direct2d_scene_stream_result direct_measure{};
    direct_measure.struct_size = static_cast<uint32_t>(sizeof(direct_measure));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_scene_recorder_build_stream(
            direct_recorder,
            nullptr,
            0U,
            &direct_measure,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            native_hresult == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) &&
            direct_measure.scene_id == 7002U &&
            direct_measure.generation == 10U &&
            direct_measure.translated_draw_count == 2U &&
            direct_measure.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE &&
            (direct_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_LEADING_CLEAR) !=
                0U &&
            (direct_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_PATH_GEOMETRY) !=
                0U &&
            direct_measure.required_bytes >= sizeof(progpu_native_scene_header),
        "ProGPU Direct2D COM recorder size pass changed");
    std::vector<uint8_t> direct_scene_stream(
        static_cast<size_t>(direct_measure.required_bytes));
    progpu_native_direct2d_scene_stream_result direct_write{};
    direct_write.struct_size = static_cast<uint32_t>(sizeof(direct_write));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_scene_recorder_build_stream(
            direct_recorder,
            direct_scene_stream.data(),
            direct_scene_stream.size(),
            &direct_write,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            direct_write.written_bytes == direct_scene_stream.size() &&
            direct_write.command_count == 2U &&
            direct_write.brush_count == 1U,
        "ProGPU Direct2D COM recorder write pass changed");
    const progpu_native_scene_header direct_scene_header =
        read_value<progpu_native_scene_header>(direct_scene_stream, 0U);
    require(
        direct_scene_header.scene_id == 7002U &&
            direct_scene_header.generation == 10U &&
            direct_scene_header.command_count == 2U,
        "ProGPU Direct2D COM recorder scene identity changed");
    direct_base_sink.Reset();
    direct_sink.Reset();
    progpu_native_direct2d_scene_recorder_destroy(direct_recorder);
    direct_recorder = nullptr;

    progpu_native_scene_header scene_header{};
    std::memcpy(&scene_header, scene_stream.data(), sizeof(scene_header));
    require(
        scene_header.struct_size == sizeof(scene_header) &&
            scene_header.total_size == scene_stream.size() &&
            scene_header.scene_id == 7001U && scene_header.generation == 9U &&
            scene_header.command_count == scene_write.command_count &&
            scene_header.resource_count == scene_write.resource_count,
        "translated Direct2D semantic scene header changed");
    std::array<progpu_native_scene_state, 2U> translated_clip_states{};
    uint32_t translated_clip_state_count = 0U;
    std::array<progpu_native_scene_brush, 4U> translated_brushes{};
    std::array<progpu_native_scene_gradient_stop, 6U>
        translated_gradient_stops{};
    bool translated_brush_table_found = false;
    for (uint32_t index = 0U; index < scene_header.resource_count; ++index) {
        progpu_native_scene_resource resource{};
        std::memcpy(
            &resource,
            scene_stream.data() + scene_header.resource_offset +
                static_cast<size_t>(index) * scene_header.resource_stride,
            sizeof(resource));
        if (resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            require(
                !translated_brush_table_found &&
                    resource.payload_size == sizeof(translated_brushes) &&
                    resource.auxiliary_size ==
                        sizeof(translated_gradient_stops) &&
                    static_cast<uint64_t>(resource.payload_offset) +
                        resource.payload_size <= scene_stream.size() &&
                    static_cast<uint64_t>(resource.auxiliary_offset) +
                        resource.auxiliary_size <= scene_stream.size(),
                "translated Direct2D brush-table resource layout changed");
            std::memcpy(
                translated_brushes.data(),
                scene_stream.data() + resource.payload_offset,
                sizeof(translated_brushes));
            std::memcpy(
                translated_gradient_stops.data(),
                scene_stream.data() + resource.auxiliary_offset,
                sizeof(translated_gradient_stops));
            translated_brush_table_found = true;
            continue;
        }
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            continue;
        }
        require(
            translated_clip_state_count < translated_clip_states.size() &&
                resource.payload_size == sizeof(progpu_native_scene_state) &&
                static_cast<uint64_t>(resource.payload_offset) +
                    resource.payload_size <= scene_stream.size(),
            "translated Direct2D clip-state resource layout changed");
        std::memcpy(
            &translated_clip_states[translated_clip_state_count],
            scene_stream.data() + resource.payload_offset,
            sizeof(progpu_native_scene_state));
        ++translated_clip_state_count;
    }
    require(
        translated_clip_state_count == translated_clip_states.size() &&
            translated_clip_states[0].flags ==
                PROGPU_NATIVE_SCENE_STATE_CLIP_RECT &&
            translated_clip_states[0].clip_rect.x == 3.0F &&
            translated_clip_states[0].clip_rect.y == 5.0F &&
            translated_clip_states[0].clip_rect.width == 37.5F &&
            translated_clip_states[0].clip_rect.height == 22.5F &&
            translated_clip_states[1].flags ==
                PROGPU_NATIVE_SCENE_STATE_CLIP_RECT &&
            translated_clip_states[1].clip_rect.x == 15.5F &&
            translated_clip_states[1].clip_rect.y == 12.5F &&
            translated_clip_states[1].clip_rect.width == 25.0F &&
            translated_clip_states[1].clip_rect.height == 15.0F,
        "Direct2D transformed nested clip intersection changed");
    require(
        translated_brush_table_found &&
            translated_brushes[0].type ==
                PROGPU_NATIVE_SCENE_BRUSH_SOLID &&
            translated_brushes[1].type ==
                PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
            translated_brushes[1].opacity == 0.75F &&
            translated_brushes[1].start_point.x == 1.0F &&
            translated_brushes[1].start_point.y == 2.0F &&
            translated_brushes[1].end_point.x == 31.0F &&
            translated_brushes[1].end_point.y == 42.0F &&
            translated_brushes[1].stop_count == 2U &&
            translated_brushes[1].stop_offset == 0U &&
            translated_brushes[1].spread_method ==
                PROGPU_NATIVE_SCENE_GRADIENT_PAD &&
            translated_brushes[1].color_interpolation_mode ==
                PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB &&
            std::abs(
                translated_brushes[1].coordinate_transform0[0] - 0.8F) <
                0.0001F &&
            std::abs(
                translated_brushes[1].coordinate_transform0[2] + 6.4F) <
                0.0001F &&
            std::abs(
                translated_brushes[1].coordinate_transform1[1] -
                    (4.0F / 3.0F)) < 0.0001F &&
            std::abs(
                translated_brushes[1].coordinate_transform1[2] +
                    (14.0F / 3.0F)) < 0.0001F &&
            translated_brushes[2].type ==
                PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
            translated_brushes[2].stop_count == 2U &&
            translated_brushes[2].stop_offset == 2U &&
            translated_brushes[2].coordinate_transform0[0] == 1.0F &&
            translated_brushes[2].coordinate_transform0[2] == -4.0F &&
            translated_brushes[2].coordinate_transform1[1] == 1.0F &&
            translated_brushes[2].coordinate_transform1[2] == 2.0F &&
            translated_brushes[3].type ==
                PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT &&
            translated_brushes[3].center.x == 12.0F &&
            translated_brushes[3].center.y == 14.0F &&
            translated_brushes[3].start_point.x == 13.0F &&
            translated_brushes[3].start_point.y == 16.0F &&
            translated_brushes[3].radius == 9.0F &&
            translated_brushes[3].radius_y == 7.0F &&
            translated_brushes[3].stop_count == 2U &&
            translated_brushes[3].stop_offset == 4U &&
            translated_gradient_stops[0].offset == 0.0F &&
            translated_gradient_stops[1].offset == 1.0F &&
            translated_gradient_stops[2].offset == 0.0F &&
            translated_gradient_stops[3].offset == 1.0F &&
            translated_gradient_stops[4].offset == 0.0F &&
            translated_gradient_stops[5].offset == 1.0F &&
            translated_gradient_stops[0].color.g ==
                gradient_stops[0].color.green &&
            translated_gradient_stops[5].color.r ==
                gradient_stops[1].color.red,
        "Direct2D linear/radial gradient scene translation changed");

    ComPtr<ID2D1PathGeometry> scene_path_geometry;
    require(SUCCEEDED(factory1->CreatePathGeometry(
                scene_path_geometry.GetAddressOf())),
        "Direct2D scene path geometry creation failed");
    ComPtr<ID2D1GeometrySink> scene_path_geometry_sink;
    require(SUCCEEDED(scene_path_geometry->Open(
                scene_path_geometry_sink.GetAddressOf())),
        "Direct2D scene path geometry did not open");
    scene_path_geometry_sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    scene_path_geometry_sink->BeginFigure(
        D2D1::Point2F(5.0F, 6.0F),
        D2D1_FIGURE_BEGIN_FILLED);
    scene_path_geometry_sink->AddLine(D2D1::Point2F(20.0F, 6.0F));
    scene_path_geometry_sink->AddBezier({
        D2D1::Point2F(25.0F, 10.0F),
        D2D1::Point2F(18.0F, 24.0F),
        D2D1::Point2F(5.0F, 18.0F)});
    scene_path_geometry_sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    scene_path_geometry_sink->BeginFigure(
        D2D1::Point2F(30.0F, 30.0F),
        D2D1_FIGURE_BEGIN_HOLLOW);
    scene_path_geometry_sink->AddLine(D2D1::Point2F(34.0F, 30.0F));
    scene_path_geometry_sink->AddLine(D2D1::Point2F(34.0F, 34.0F));
    scene_path_geometry_sink->EndFigure(D2D1_FIGURE_END_OPEN);
    require(SUCCEEDED(scene_path_geometry_sink->Close()),
        "Direct2D scene path geometry did not close");

    void* path_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &path_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            path_list_value != nullptr && native_hresult == S_OK,
        "path command-list creation failed");
    ComPtr<ID2D1CommandList> path_list;
    path_list.Attach(static_cast<ID2D1CommandList*>(path_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            path_list.Get()) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "path command-list recording did not begin");
    context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
    context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
    context->SetTextRenderingParams(nullptr);
    context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    const D2D1_MATRIX_3X2_F path_transform = D2D1::Matrix3x2F(
        1.5F, 0.0F, 0.0F, 0.5F, 7.0F, 9.0F);
    context->SetTransform(path_transform);
    context->FillGeometry(
        scene_path_geometry.Get(),
        solid_brush.Get());
    constexpr float scene_path_stroke_width = 3.0F;
    context->DrawGeometry(
        scene_path_geometry.Get(),
        solid_brush.Get(),
        scene_path_stroke_width,
        stroke_style.Get());
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "path command-list recording did not close");
    ComPtr<ID2D1PathGeometry> reference_stroked_path;
    require(SUCCEEDED(factory1->CreatePathGeometry(
                reference_stroked_path.GetAddressOf())),
        "reference stroked path creation failed");
    ComPtr<ID2D1GeometrySink> reference_stroked_path_sink;
    require(SUCCEEDED(reference_stroked_path->Open(
                reference_stroked_path_sink.GetAddressOf())),
        "reference stroked path did not open");
    HRESULT reference_widen_hr = scene_path_geometry->Widen(
        scene_path_stroke_width,
        stroke_style.Get(),
        &path_transform,
        D2D1_DEFAULT_FLATTENING_TOLERANCE,
        reference_stroked_path_sink.Get());
    const HRESULT reference_widen_close_hr =
        reference_stroked_path_sink->Close();
    require(SUCCEEDED(reference_widen_hr) &&
            SUCCEEDED(reference_widen_close_hr),
        "reference Direct2D stroke widening failed");
    uint32_t reference_stroked_segment_count = 0U;
    require(SUCCEEDED(reference_stroked_path->GetSegmentCount(
                &reference_stroked_segment_count)),
        "reference Direct2D stroke segment count query failed");
    require(reference_stroked_segment_count != 0U,
        "reference Direct2D stroke widening produced no segments");
    progpu_native_direct2d_scene_stream_result path_measure{};
    path_measure.struct_size = static_cast<uint32_t>(sizeof(path_measure));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            path_list.Get(),
            7005U,
            1U,
            nullptr,
            0U,
            &path_measure,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            native_hresult == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) &&
            path_measure.translated_draw_count == 2U &&
            path_measure.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE &&
            (path_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_PATH_GEOMETRY) !=
                0U &&
            (path_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_STROKED_PATH_GEOMETRY) !=
                0U,
        "Direct2D filled-path scene size pass changed");
    std::vector<uint8_t> path_stream(
        static_cast<size_t>(path_measure.required_bytes));
    progpu_native_direct2d_scene_stream_result path_write{};
    path_write.struct_size = static_cast<uint32_t>(sizeof(path_write));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            path_list.Get(),
            7005U,
            1U,
            path_stream.data(),
            path_stream.size(),
            &path_write,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK && path_write.command_count == 2U &&
            path_write.resource_count == 3U && path_write.brush_count == 1U &&
            path_write.translated_draw_count == 2U,
        "Direct2D filled-path scene write pass changed");
    progpu_native_scene_header path_header{};
    std::memcpy(&path_header, path_stream.data(), sizeof(path_header));
    uint32_t path_resource_count = 0U;
    for (uint32_t index = 0U; index < path_header.resource_count; ++index) {
        progpu_native_scene_resource resource{};
        std::memcpy(
            &resource,
            path_stream.data() + path_header.resource_offset +
                static_cast<size_t>(index) * path_header.resource_stride,
            sizeof(resource));
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            continue;
        }
        require(
            path_resource_count < 2U &&
                resource.payload_size == sizeof(progpu_native_scene_path_fill) &&
                static_cast<uint64_t>(resource.payload_offset) +
                    resource.payload_size <= path_stream.size() &&
                static_cast<uint64_t>(resource.auxiliary_offset) +
                    resource.auxiliary_size <= path_stream.size(),
            "translated Direct2D path resource layout changed");
        progpu_native_scene_path_fill translated_path{};
        std::memcpy(
            &translated_path,
            path_stream.data() + resource.payload_offset,
            sizeof(translated_path));
        if (path_resource_count == 0U) {
            require(
                resource.auxiliary_size ==
                    3U * sizeof(progpu_native_path_segment),
                "translated Direct2D fill-path segment layout changed");
            std::array<progpu_native_path_segment, 3U>
                translated_segments{};
            std::memcpy(
                translated_segments.data(),
                path_stream.data() + resource.auxiliary_offset,
                sizeof(translated_segments));
            require(
                translated_path.segment_offset == 0U &&
                    translated_path.segment_count ==
                        translated_segments.size() &&
                    translated_path.fill_rule ==
                        PROGPU_NATIVE_FILL_RULE_NON_ZERO &&
                    translated_path.sample_grid == 8U &&
                    translated_path.transform.m11 == path_transform._11 &&
                    translated_path.transform.m22 == path_transform._22 &&
                    translated_path.transform.m31 == path_transform._31 &&
                    translated_path.transform.m32 == path_transform._32 &&
                    translated_segments[0].kind ==
                        PROGPU_NATIVE_PATH_SEGMENT_LINE &&
                    translated_segments[0].p0.x == 5.0F &&
                    translated_segments[0].p0.y == 6.0F &&
                    translated_segments[0].p1.x == 20.0F &&
                    translated_segments[0].p1.y == 6.0F &&
                    translated_segments[1].kind ==
                        PROGPU_NATIVE_PATH_SEGMENT_CUBIC &&
                    translated_segments[1].p3.x == 5.0F &&
                    translated_segments[1].p3.y == 18.0F &&
                    translated_segments[2].kind ==
                        PROGPU_NATIVE_PATH_SEGMENT_LINE &&
                    translated_segments[2].p0.x == 5.0F &&
                    translated_segments[2].p0.y == 18.0F &&
                    translated_segments[2].p1.x == 5.0F &&
                    translated_segments[2].p1.y == 6.0F,
                "Direct2D filled-path contour translation changed");
        } else {
            require(
                translated_path.segment_offset == 0U &&
                    translated_path.segment_count ==
                        reference_stroked_segment_count &&
                    resource.auxiliary_size ==
                        static_cast<uint64_t>(reference_stroked_segment_count) *
                            sizeof(progpu_native_path_segment) &&
                    translated_path.sample_grid == 8U &&
                    translated_path.transform.m11 == 1.0F &&
                    translated_path.transform.m12 == 0.0F &&
                    translated_path.transform.m21 == 0.0F &&
                    translated_path.transform.m22 == 1.0F &&
                    translated_path.transform.m31 == 0.0F &&
                    translated_path.transform.m32 == 0.0F,
                "Direct2D transformed custom stroke widening changed");
        }
        ++path_resource_count;
    }
    require(path_resource_count == 2U,
        "translated Direct2D scene omitted a fill/stroke path resource");

    void* aliased_path_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &aliased_path_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            aliased_path_list_value != nullptr && native_hresult == S_OK,
        "aliased-path command-list creation failed");
    ComPtr<ID2D1CommandList> aliased_path_list;
    aliased_path_list.Attach(
        static_cast<ID2D1CommandList*>(aliased_path_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            aliased_path_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "aliased-path command-list recording did not begin");
    context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
    context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
    context->SetTextRenderingParams(nullptr);
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_ALIASED);
    context->FillGeometry(scene_path_geometry.Get(), solid_brush.Get());
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "aliased-path command-list recording did not close");
    progpu_native_direct2d_scene_stream_result aliased_path_scene{};
    aliased_path_scene.struct_size =
        static_cast<uint32_t>(sizeof(aliased_path_scene));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            aliased_path_list.Get(),
            7006U,
            1U,
            nullptr,
            0U,
            &aliased_path_scene,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            native_hresult == E_NOTIMPL &&
            aliased_path_scene.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE &&
            aliased_path_scene.failure_callback_index != 0U &&
            aliased_path_scene.written_bytes == 0U,
        "aliased Direct2D filled path did not fail closed");

    void* opacity_layer_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &opacity_layer_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            opacity_layer_list_value != nullptr && native_hresult == S_OK,
        "opacity-layer command-list creation failed");
    ComPtr<ID2D1CommandList> opacity_layer_list;
    opacity_layer_list.Attach(
        static_cast<ID2D1CommandList*>(opacity_layer_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            opacity_layer_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "opacity-layer command-list recording did not begin");
    context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
    context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
    context->SetTextRenderingParams(nullptr);
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    D2D1_LAYER_PARAMETERS1 opacity_layer_parameters{};
    opacity_layer_parameters.contentBounds =
        D2D1::RectF(1.0F, 2.0F, 21.0F, 22.0F);
    opacity_layer_parameters.maskAntialiasMode =
        D2D1_ANTIALIAS_MODE_PER_PRIMITIVE;
    const D2D1_MATRIX_3X2_F opacity_layer_mask_transform =
        D2D1::Matrix3x2F::Translation(2.0F, 3.0F);
    opacity_layer_parameters.geometricMask = scene_path_geometry.Get();
    opacity_layer_parameters.maskTransform = opacity_layer_mask_transform;
    opacity_layer_parameters.opacity = 0.375F;
    opacity_layer_parameters.layerOptions = D2D1_LAYER_OPTIONS1_NONE;
    const D2D1_RECT_F opacity_layer_clip =
        D2D1::RectF(2.0F, 3.0F, 36.0F, 37.0F);
    context->PushAxisAlignedClip(
        &opacity_layer_clip,
        D2D1_ANTIALIAS_MODE_ALIASED);
    const D2D1_MATRIX_3X2_F opacity_layer_transform =
        D2D1::Matrix3x2F(2.0F, 0.0F, 0.0F, 0.5F, 7.0F, 9.0F);
    D2D1_MATRIX_3X2_F opacity_layer_mask_target_transform{};
    opacity_layer_mask_target_transform._11 = 2.0F;
    opacity_layer_mask_target_transform._12 = 0.0F;
    opacity_layer_mask_target_transform._21 = 0.0F;
    opacity_layer_mask_target_transform._22 = 0.5F;
    opacity_layer_mask_target_transform._31 = 11.0F;
    opacity_layer_mask_target_transform._32 = 10.5F;
    D2D1_RECT_F expected_opacity_mask_bounds{};
    require(SUCCEEDED(scene_path_geometry->GetBounds(
                &opacity_layer_mask_target_transform,
                &expected_opacity_mask_bounds)),
        "Direct2D opacity-layer mask bounds query failed");
    const float expected_opacity_layer_left = std::max(
        9.0F,
        expected_opacity_mask_bounds.left);
    const float expected_opacity_layer_top = std::max(
        10.0F,
        expected_opacity_mask_bounds.top);
    const float expected_opacity_layer_right = std::min(
        49.0F,
        expected_opacity_mask_bounds.right);
    const float expected_opacity_layer_bottom = std::min(
        20.0F,
        expected_opacity_mask_bounds.bottom);
    context->SetTransform(opacity_layer_transform);
    context->PushLayer(&opacity_layer_parameters, nullptr);
    const D2D1_RECT_F opacity_layer_fill0 =
        D2D1::RectF(4.0F, 5.0F, 24.0F, 25.0F);
    const D2D1_RECT_F opacity_layer_fill1 =
        D2D1::RectF(12.0F, 13.0F, 32.0F, 33.0F);
    context->FillRectangle(&opacity_layer_fill0, solid_brush.Get());
    context->FillRectangle(&opacity_layer_fill1, solid_brush.Get());
    context->PopLayer();
    context->PopAxisAlignedClip();
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "opacity-layer command-list recording did not close");
    progpu_native_direct2d_scene_stream_result opacity_layer_measure{};
    opacity_layer_measure.struct_size =
        static_cast<uint32_t>(sizeof(opacity_layer_measure));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            opacity_layer_list.Get(),
            7007U,
            1U,
            nullptr,
            0U,
            &opacity_layer_measure,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            native_hresult == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) &&
            opacity_layer_measure.translated_draw_count == 2U &&
            opacity_layer_measure.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE &&
            (opacity_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_OPACITY_LAYERS) !=
                0U &&
            (opacity_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_AXIS_ALIGNED_CLIPS) !=
                0U &&
            (opacity_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_GEOMETRIC_LAYER_MASKS) !=
                0U,
        "Direct2D opacity-layer scene size pass changed");
    std::vector<uint8_t> opacity_layer_stream(
        static_cast<size_t>(opacity_layer_measure.required_bytes));
    progpu_native_direct2d_scene_stream_result opacity_layer_write{};
    opacity_layer_write.struct_size =
        static_cast<uint32_t>(sizeof(opacity_layer_write));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            opacity_layer_list.Get(),
            7007U,
            1U,
            opacity_layer_stream.data(),
            opacity_layer_stream.size(),
            &opacity_layer_write,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            opacity_layer_write.translated_draw_count == 2U,
        "Direct2D opacity-layer scene write pass changed");
    progpu_native_scene_header opacity_layer_header{};
    std::memcpy(
        &opacity_layer_header,
        opacity_layer_stream.data(),
        sizeof(opacity_layer_header));
    uint32_t push_layer_count = 0U;
    uint32_t pop_layer_count = 0U;
    uint32_t save_index = std::numeric_limits<uint32_t>::max();
    uint32_t push_layer_index = std::numeric_limits<uint32_t>::max();
    uint32_t pop_layer_index = std::numeric_limits<uint32_t>::max();
    uint32_t restore_index = std::numeric_limits<uint32_t>::max();
    uint32_t translated_mask_resource_index =
        PROGPU_NATIVE_SCENE_NO_INDEX;
    for (uint32_t index = 0U;
         index < opacity_layer_header.command_count;
         ++index) {
        progpu_native_scene_command command{};
        std::memcpy(
            &command,
            opacity_layer_stream.data() + opacity_layer_header.command_offset +
                static_cast<size_t>(index) *
                    opacity_layer_header.command_stride,
            sizeof(command));
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            require(
                push_layer_count == 0U &&
                    command.payload_size ==
                        sizeof(progpu_native_scene_layer) &&
                    static_cast<uint64_t>(command.payload_offset) +
                        command.payload_size <= opacity_layer_stream.size(),
                "translated Direct2D opacity-layer payload changed");
            progpu_native_scene_layer translated_layer{};
            std::memcpy(
                &translated_layer,
                opacity_layer_stream.data() + command.payload_offset,
                sizeof(translated_layer));
            require(
                translated_layer.flags == PROGPU_NATIVE_SCENE_LAYER_BOUNDS &&
                    translated_layer.bounds.x ==
                        expected_opacity_layer_left &&
                    translated_layer.bounds.y ==
                        expected_opacity_layer_top &&
                    translated_layer.bounds.width ==
                        expected_opacity_layer_right -
                            expected_opacity_layer_left &&
                    translated_layer.bounds.height ==
                        expected_opacity_layer_bottom -
                            expected_opacity_layer_top &&
                    translated_layer.opacity == 0.375F &&
                    translated_layer.blend_mode ==
                        PROGPU_NATIVE_BLEND_SRC_OVER &&
                    translated_layer.mask_resource_index !=
                        PROGPU_NATIVE_SCENE_NO_INDEX &&
                    translated_layer.effect_resource_index ==
                        PROGPU_NATIVE_SCENE_NO_INDEX,
                "Direct2D grouped opacity translation changed");
            translated_mask_resource_index =
                translated_layer.mask_resource_index;
            push_layer_index = index;
            ++push_layer_count;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
            pop_layer_index = index;
            ++pop_layer_count;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE) {
            save_index = index;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE) {
            restore_index = index;
        }
    }
    require(
        push_layer_count == 1U && pop_layer_count == 1U &&
            save_index < push_layer_index &&
            push_layer_index < pop_layer_index &&
            pop_layer_index < restore_index,
        "translated Direct2D clip/layer scopes were not balanced and nested");
    require(
        translated_mask_resource_index < opacity_layer_header.resource_count,
        "translated Direct2D layer omitted its geometric mask resource");
    progpu_native_scene_resource translated_mask_resource{};
    std::memcpy(
        &translated_mask_resource,
        opacity_layer_stream.data() + opacity_layer_header.resource_offset +
            static_cast<size_t>(translated_mask_resource_index) *
                opacity_layer_header.resource_stride,
        sizeof(translated_mask_resource));
    require(
        translated_mask_resource.kind ==
                PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
            translated_mask_resource.payload_size ==
                sizeof(progpu_native_scene_layer_vector_mask) &&
            translated_mask_resource.auxiliary_size ==
                sizeof(progpu_native_scene_clip_path) +
                    3U * sizeof(progpu_native_path_segment) &&
            static_cast<uint64_t>(translated_mask_resource.payload_offset) +
                translated_mask_resource.payload_size <=
                    opacity_layer_stream.size() &&
            static_cast<uint64_t>(translated_mask_resource.auxiliary_offset) +
                translated_mask_resource.auxiliary_size <=
                    opacity_layer_stream.size(),
        "translated Direct2D geometric layer-mask layout changed");
    progpu_native_scene_layer_vector_mask translated_vector_mask{};
    progpu_native_scene_clip_path translated_mask_path{};
    std::array<progpu_native_path_segment, 3U> translated_mask_segments{};
    std::memcpy(
        &translated_vector_mask,
        opacity_layer_stream.data() + translated_mask_resource.payload_offset,
        sizeof(translated_vector_mask));
    std::memcpy(
        &translated_mask_path,
        opacity_layer_stream.data() + translated_mask_resource.auxiliary_offset,
        sizeof(translated_mask_path));
    std::memcpy(
        translated_mask_segments.data(),
        opacity_layer_stream.data() + translated_mask_resource.auxiliary_offset +
            sizeof(translated_mask_path),
        sizeof(translated_mask_segments));
    require(
        translated_vector_mask.kind ==
                PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN &&
            translated_vector_mask.path_count == 1U &&
            translated_vector_mask.segment_count == 3U &&
            translated_vector_mask.opacity == 1.0F &&
            translated_mask_path.segment_offset == 0U &&
            translated_mask_path.segment_count == 3U &&
            translated_mask_path.fill_rule ==
                PROGPU_NATIVE_FILL_RULE_NON_ZERO &&
            translated_mask_path.sample_grid == 8U &&
            translated_mask_path.operation == PROGPU_NATIVE_CLIP_INTERSECT &&
            translated_mask_path.transform.m11 ==
                opacity_layer_mask_target_transform._11 &&
            translated_mask_path.transform.m22 ==
                opacity_layer_mask_target_transform._22 &&
            translated_mask_path.transform.m31 ==
                opacity_layer_mask_target_transform._31 &&
            translated_mask_path.transform.m32 ==
                opacity_layer_mask_target_transform._32 &&
            translated_mask_segments[0].kind ==
                PROGPU_NATIVE_PATH_SEGMENT_LINE &&
            translated_mask_segments[1].kind ==
                PROGPU_NATIVE_PATH_SEGMENT_CUBIC &&
            translated_mask_segments[2].kind ==
                PROGPU_NATIVE_PATH_SEGMENT_LINE,
        "Direct2D geometric layer-mask topology/transform changed");

    void* opacity_brush_layer_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &opacity_brush_layer_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            opacity_brush_layer_list_value != nullptr && native_hresult == S_OK,
        "opacity-brush layer command-list creation failed");
    ComPtr<ID2D1CommandList> opacity_brush_layer_list;
    opacity_brush_layer_list.Attach(
        static_cast<ID2D1CommandList*>(opacity_brush_layer_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            opacity_brush_layer_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "opacity-brush layer command-list recording did not begin");
    context->SetTransform(opacity_layer_transform);
    D2D1_LAYER_PARAMETERS1 opacity_brush_layer_parameters =
        opacity_layer_parameters;
    opacity_brush_layer_parameters.geometricMask = nullptr;
    opacity_brush_layer_parameters.opacityBrush = linear_brush.Get();
    context->PushLayer(&opacity_brush_layer_parameters, nullptr);
    context->FillRectangle(&opacity_layer_fill0, solid_brush.Get());
    context->PopLayer();
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "opacity-brush layer command-list recording did not close");
    progpu_native_direct2d_scene_stream_result opacity_brush_layer_measure{};
    opacity_brush_layer_measure.struct_size =
        static_cast<uint32_t>(sizeof(opacity_brush_layer_measure));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            opacity_brush_layer_list.Get(),
            7010U,
            1U,
            nullptr,
            0U,
            &opacity_brush_layer_measure,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            native_hresult == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) &&
            (opacity_brush_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_OPACITY_BRUSH_LAYER_MASKS) !=
                0U &&
            (opacity_brush_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_GRADIENT_BRUSHES) !=
                0U,
        "Direct2D opacity-brush layer size pass changed");
    std::vector<uint8_t> opacity_brush_layer_stream(
        static_cast<size_t>(opacity_brush_layer_measure.required_bytes));
    progpu_native_direct2d_scene_stream_result opacity_brush_layer_write{};
    opacity_brush_layer_write.struct_size =
        static_cast<uint32_t>(sizeof(opacity_brush_layer_write));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            opacity_brush_layer_list.Get(),
            7010U,
            1U,
            opacity_brush_layer_stream.data(),
            opacity_brush_layer_stream.size(),
            &opacity_brush_layer_write,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "Direct2D opacity-brush layer write pass changed");
    progpu_native_scene_header opacity_brush_layer_header{};
    std::memcpy(
        &opacity_brush_layer_header,
        opacity_brush_layer_stream.data(),
        sizeof(opacity_brush_layer_header));
    uint32_t opacity_brush_mask_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    for (uint32_t index = 0U;
         index < opacity_brush_layer_header.command_count;
         ++index) {
        progpu_native_scene_command command{};
        std::memcpy(
            &command,
            opacity_brush_layer_stream.data() +
                opacity_brush_layer_header.command_offset +
                static_cast<size_t>(index) *
                    opacity_brush_layer_header.command_stride,
            sizeof(command));
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            progpu_native_scene_layer translated_layer{};
            std::memcpy(
                &translated_layer,
                opacity_brush_layer_stream.data() + command.payload_offset,
                sizeof(translated_layer));
            require(
                translated_layer.bounds.x == 9.0F &&
                    translated_layer.bounds.y == 10.0F &&
                    translated_layer.bounds.width == 40.0F &&
                    translated_layer.bounds.height == 10.0F,
                "Direct2D opacity-brush layer bounds changed");
            opacity_brush_mask_index = translated_layer.mask_resource_index;
        }
    }
    require(
        opacity_brush_mask_index < opacity_brush_layer_header.resource_count,
        "Direct2D opacity-brush layer omitted its mask resource");
    progpu_native_scene_resource opacity_brush_mask_resource{};
    std::memcpy(
        &opacity_brush_mask_resource,
        opacity_brush_layer_stream.data() +
            opacity_brush_layer_header.resource_offset +
            static_cast<size_t>(opacity_brush_mask_index) *
                opacity_brush_layer_header.resource_stride,
        sizeof(opacity_brush_mask_resource));
    progpu_native_scene_layer_brush_mask translated_brush_mask{};
    std::memcpy(
        &translated_brush_mask,
        opacity_brush_layer_stream.data() +
            opacity_brush_mask_resource.payload_offset,
        sizeof(translated_brush_mask));
    require(
        opacity_brush_mask_resource.kind ==
                PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
            opacity_brush_mask_resource.payload_size ==
                sizeof(translated_brush_mask) &&
            opacity_brush_mask_resource.auxiliary_size ==
                2U * sizeof(progpu_native_scene_gradient_stop) &&
            translated_brush_mask.kind ==
                PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH &&
            translated_brush_mask.gradient_stop_count == 2U &&
            translated_brush_mask.bounds.x == 1.0F &&
            translated_brush_mask.bounds.y == 2.0F &&
            translated_brush_mask.bounds.width == 20.0F &&
            translated_brush_mask.bounds.height == 20.0F &&
            translated_brush_mask.transform.m11 == 2.0F &&
            translated_brush_mask.transform.m22 == 0.5F &&
            translated_brush_mask.transform.m31 == 7.0F &&
            translated_brush_mask.transform.m32 == 9.0F &&
            translated_brush_mask.brush.type ==
                PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
            translated_brush_mask.brush.opacity == 0.75F &&
            translated_brush_mask.brush.coordinate_transform0[0] == 0.5F &&
            translated_brush_mask.brush.coordinate_transform0[2] == -7.5F &&
            translated_brush_mask.brush.coordinate_transform1[1] == 2.0F &&
            translated_brush_mask.brush.coordinate_transform1[2] == -16.0F,
        "Direct2D opacity-brush layer mask mapping changed");

    void* composite_layer_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &composite_layer_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            composite_layer_list_value != nullptr && native_hresult == S_OK,
        "composite-mask layer command-list creation failed");
    ComPtr<ID2D1CommandList> composite_layer_list;
    composite_layer_list.Attach(
        static_cast<ID2D1CommandList*>(composite_layer_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            composite_layer_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "composite-mask layer command-list recording did not begin");
    context->SetTransform(opacity_layer_transform);
    D2D1_LAYER_PARAMETERS1 composite_layer_parameters =
        opacity_layer_parameters;
    composite_layer_parameters.opacityBrush = linear_brush.Get();
    context->PushLayer(&composite_layer_parameters, nullptr);
    context->FillRectangle(&opacity_layer_fill0, solid_brush.Get());
    context->PopLayer();
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "composite-mask layer command-list recording did not close");
    progpu_native_direct2d_scene_stream_result composite_layer_measure{};
    composite_layer_measure.struct_size =
        static_cast<uint32_t>(sizeof(composite_layer_measure));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            composite_layer_list.Get(),
            7011U,
            1U,
            nullptr,
            0U,
            &composite_layer_measure,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            native_hresult == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) &&
            (composite_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_GEOMETRIC_LAYER_MASKS) !=
                0U &&
            (composite_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_OPACITY_BRUSH_LAYER_MASKS) !=
                0U &&
            (composite_layer_measure.flags &
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_COMPOSITE_LAYER_MASKS) !=
                0U,
        "Direct2D composite-mask layer size pass changed");
    std::vector<uint8_t> composite_layer_stream(
        static_cast<size_t>(composite_layer_measure.required_bytes));
    progpu_native_direct2d_scene_stream_result composite_layer_write{};
    composite_layer_write.struct_size =
        static_cast<uint32_t>(sizeof(composite_layer_write));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            composite_layer_list.Get(),
            7011U,
            1U,
            composite_layer_stream.data(),
            composite_layer_stream.size(),
            &composite_layer_write,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "Direct2D composite-mask layer write pass changed");
    progpu_native_scene_header composite_layer_header{};
    std::memcpy(
        &composite_layer_header,
        composite_layer_stream.data(),
        sizeof(composite_layer_header));
    const progpu_native_scene_command composite_push =
        read_value<progpu_native_scene_command>(
            composite_layer_stream,
            composite_layer_header.command_offset);
    const progpu_native_scene_layer composite_layer =
        read_value<progpu_native_scene_layer>(
            composite_layer_stream,
            composite_push.payload_offset);
    require(
        composite_push.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER &&
            composite_layer.bounds.x == expected_opacity_layer_left &&
            composite_layer.bounds.y == expected_opacity_layer_top &&
            composite_layer.bounds.width == expected_opacity_layer_right -
                expected_opacity_layer_left &&
            composite_layer.bounds.height == expected_opacity_layer_bottom -
                expected_opacity_layer_top &&
            composite_layer.mask_resource_index <
                composite_layer_header.resource_count,
        "Direct2D composite-mask layer bounds/reference changed");
    const progpu_native_scene_resource composite_mask_resource =
        read_value<progpu_native_scene_resource>(
            composite_layer_stream,
            composite_layer_header.resource_offset +
                static_cast<size_t>(composite_layer.mask_resource_index) *
                    composite_layer_header.resource_stride);
    const progpu_native_scene_layer_composite_mask composite_mask =
        read_value<progpu_native_scene_layer_composite_mask>(
            composite_layer_stream,
            composite_mask_resource.payload_offset);
    const size_t expected_composite_auxiliary_size =
        sizeof(progpu_native_scene_layer_brush_mask) +
        sizeof(progpu_native_scene_clip_path) +
        3U * sizeof(progpu_native_path_segment) +
        2U * sizeof(progpu_native_scene_gradient_stop);
    require(
        composite_mask_resource.kind ==
                PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK &&
            composite_mask_resource.payload_size == sizeof(composite_mask) &&
            composite_mask_resource.auxiliary_size ==
                expected_composite_auxiliary_size &&
            composite_mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE &&
            composite_mask.component_count == 2U &&
            composite_mask.brush_mask_count == 1U &&
            composite_mask.path_count == 1U &&
            composite_mask.segment_count == 3U &&
            composite_mask.gradient_stop_count == 2U &&
            composite_mask.geometry_mask_count == 0U &&
            composite_mask.picture_mask_count == 0U,
        "Direct2D composite geometric/brush mask layout changed");

    void* background_layer_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &background_layer_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            background_layer_list_value != nullptr && native_hresult == S_OK,
        "background-layer command-list creation failed");
    ComPtr<ID2D1CommandList> background_layer_list;
    background_layer_list.Attach(
        static_cast<ID2D1CommandList*>(background_layer_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            background_layer_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "background-layer command-list recording did not begin");
    context->SetTransform(opacity_layer_transform);
    D2D1_LAYER_PARAMETERS1 background_layer_parameters =
        opacity_layer_parameters;
    background_layer_parameters.layerOptions =
        D2D1_LAYER_OPTIONS1_INITIALIZE_FROM_BACKGROUND;
    context->PushLayer(&background_layer_parameters, nullptr);
    context->FillRectangle(&opacity_layer_fill0, solid_brush.Get());
    context->PopLayer();
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "background-layer command-list recording did not close");
    progpu_native_direct2d_scene_stream_result background_layer_scene{};
    background_layer_scene.struct_size =
        static_cast<uint32_t>(sizeof(background_layer_scene));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            background_layer_list.Get(),
            7008U,
            1U,
            nullptr,
            0U,
            &background_layer_scene,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            native_hresult == E_NOTIMPL &&
            background_layer_scene.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE &&
            background_layer_scene.failure_callback_index != 0U &&
            background_layer_scene.written_bytes == 0U,
        "Direct2D background-initialized layer did not fail closed");

    void* aliased_mask_layer_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &aliased_mask_layer_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            aliased_mask_layer_list_value != nullptr && native_hresult == S_OK,
        "aliased-mask layer command-list creation failed");
    ComPtr<ID2D1CommandList> aliased_mask_layer_list;
    aliased_mask_layer_list.Attach(
        static_cast<ID2D1CommandList*>(aliased_mask_layer_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            aliased_mask_layer_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "aliased-mask layer command-list recording did not begin");
    context->SetTransform(opacity_layer_transform);
    D2D1_LAYER_PARAMETERS1 aliased_mask_layer_parameters =
        opacity_layer_parameters;
    aliased_mask_layer_parameters.maskAntialiasMode =
        D2D1_ANTIALIAS_MODE_ALIASED;
    context->PushLayer(&aliased_mask_layer_parameters, nullptr);
    context->FillRectangle(&opacity_layer_fill0, solid_brush.Get());
    context->PopLayer();
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "aliased-mask layer command-list recording did not close");
    progpu_native_direct2d_scene_stream_result aliased_mask_layer_scene{};
    aliased_mask_layer_scene.struct_size =
        static_cast<uint32_t>(sizeof(aliased_mask_layer_scene));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            aliased_mask_layer_list.Get(),
            7009U,
            1U,
            nullptr,
            0U,
            &aliased_mask_layer_scene,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            native_hresult == E_NOTIMPL &&
            aliased_mask_layer_scene.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE &&
            aliased_mask_layer_scene.failure_callback_index != 0U &&
            aliased_mask_layer_scene.written_bytes == 0U,
        "Direct2D aliased geometric layer mask did not fail closed");

    progpu_native_direct2d_scene_stream_result scene_short{};
    scene_short.struct_size = static_cast<uint32_t>(sizeof(scene_short));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            scene_command_list.Get(),
            7001U,
            9U,
            scene_stream.data(),
            scene_stream.size() - 1U,
            &scene_short,
            &native_hresult) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER &&
            scene_short.required_bytes == scene_stream.size() &&
            scene_short.written_bytes == 0U,
        "short Direct2D semantic scene destination did not fail closed");

    progpu_native_direct2d_gradient_stop varying_alpha_stops[] = {
        {0.0F, {1.0F, 0.0F, 0.0F, 0.25F}},
        {1.0F, {0.0F, 0.0F, 1.0F, 1.0F}}
    };
    void* varying_alpha_collection_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_gradient_stop_collection(
            surface,
            varying_alpha_stops,
            2U,
            PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SRGB,
            PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SRGB,
            PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_8BPC_UNORM,
            PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP,
            PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_PREMULTIPLIED,
            &varying_alpha_collection_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            varying_alpha_collection_value != nullptr &&
            native_hresult == S_OK,
        "varying-alpha gradient-stop collection creation failed");
    ComPtr<ID2D1GradientStopCollection1> varying_alpha_collection;
    varying_alpha_collection.Attach(
        static_cast<ID2D1GradientStopCollection1*>(
            varying_alpha_collection_value));
    progpu_native_direct2d_brush_properties varying_alpha_brush_properties{};
    varying_alpha_brush_properties.opacity = 1.0F;
    varying_alpha_brush_properties.transform.m11 = 1.0F;
    varying_alpha_brush_properties.transform.m22 = 1.0F;
    void* varying_alpha_brush_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_linear_gradient_brush(
            surface,
            &linear_properties,
            &varying_alpha_brush_properties,
            varying_alpha_collection.Get(),
            &varying_alpha_brush_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            varying_alpha_brush_value != nullptr && native_hresult == S_OK,
        "varying-alpha linear-gradient brush creation failed");
    ComPtr<ID2D1LinearGradientBrush> varying_alpha_brush;
    varying_alpha_brush.Attach(
        static_cast<ID2D1LinearGradientBrush*>(varying_alpha_brush_value));
    void* varying_alpha_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &varying_alpha_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            varying_alpha_list_value != nullptr && native_hresult == S_OK,
        "varying-alpha command-list creation failed");
    ComPtr<ID2D1CommandList> varying_alpha_list;
    varying_alpha_list.Attach(
        static_cast<ID2D1CommandList*>(varying_alpha_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            varying_alpha_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "varying-alpha command-list recording did not begin");
    context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
    context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
    context->SetTextRenderingParams(nullptr);
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    context->FillRectangle(&scene_fill, varying_alpha_brush.Get());
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "varying-alpha command-list recording did not close");
    progpu_native_direct2d_scene_stream_result varying_alpha_scene{};
    varying_alpha_scene.struct_size =
        static_cast<uint32_t>(sizeof(varying_alpha_scene));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            varying_alpha_list.Get(),
            7004U,
            1U,
            nullptr,
            0U,
            &varying_alpha_scene,
            &native_hresult) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            native_hresult == E_NOTIMPL &&
            varying_alpha_scene.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE &&
            varying_alpha_scene.failure_callback_index != 0U &&
            varying_alpha_scene.written_bytes == 0U,
        "premultiplied varying-alpha gradient did not fail closed");

    void* antialiased_clip_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &antialiased_clip_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            antialiased_clip_list_value != nullptr && native_hresult == S_OK,
        "antialiased-clip command-list creation failed");
    ComPtr<ID2D1CommandList> antialiased_clip_list;
    antialiased_clip_list.Attach(
        static_cast<ID2D1CommandList*>(antialiased_clip_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            antialiased_clip_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "antialiased-clip command-list recording did not begin");
    context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
    context->SetUnitMode(D2D1_UNIT_MODE_DIPS);
    context->SetTextRenderingParams(nullptr);
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    context->PushAxisAlignedClip(
        &scene_outer_clip,
        D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    context->FillRectangle(&scene_fill, solid_brush.Get());
    context->PopAxisAlignedClip();
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "antialiased-clip command-list recording did not close");
    progpu_native_direct2d_scene_stream_result antialiased_clip_scene{};
    antialiased_clip_scene.struct_size =
        static_cast<uint32_t>(sizeof(antialiased_clip_scene));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            antialiased_clip_list.Get(),
            7003U,
            1U,
            nullptr,
            0U,
            &antialiased_clip_scene,
            &native_hresult) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            native_hresult == E_NOTIMPL &&
            antialiased_clip_scene.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE &&
            antialiased_clip_scene.failure_callback_index != 0U &&
            antialiased_clip_scene.written_bytes == 0U,
        "per-primitive Direct2D clip antialiasing did not fail closed");

    ComPtr<IDWriteRenderingParams> text_rendering_params;
    require(SUCCEEDED(dwrite_factory->CreateRenderingParams(
                text_rendering_params.GetAddressOf())),
        "DirectWrite rendering-parameter creation failed");
    ComPtr<IDWriteTextFormat> preflight_text_format;
    require(SUCCEEDED(dwrite_factory->CreateTextFormat(
                L"Segoe UI",
                nullptr,
                DWRITE_FONT_WEIGHT_NORMAL,
                DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL,
                12.0F,
                L"en-us",
                preflight_text_format.GetAddressOf())),
        "DirectWrite preflight text-format creation failed");
    void* unsupported_command_list_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_command_list(
            surface,
            &unsupported_command_list_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            unsupported_command_list_value != nullptr && native_hresult == S_OK,
        "unsupported-operation command-list creation failed");
    ComPtr<ID2D1CommandList> unsupported_command_list;
    unsupported_command_list.Attach(
        static_cast<ID2D1CommandList*>(unsupported_command_list_value));
    require(
        progpu_native_direct2d_surface_begin_command_list_draw(
            surface,
            unsupported_command_list.Get()) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "unsupported-operation command-list recording did not begin");
    context->SetTextRenderingParams(text_rendering_params.Get());
    const D2D1_RECT_F preflight_text_bounds = {0.0F, 0.0F, 16.0F, 16.0F};
    context->DrawText(
        L"x",
        1U,
        preflight_text_format.Get(),
        &preflight_text_bounds,
        solid_brush.Get());
    command_tag1 = 0U;
    command_tag2 = 0U;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_end_command_list_draw(
            surface,
            &command_tag1,
            &command_tag2,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "unsupported-operation command-list recording did not close");
    context->SetTextRenderingParams(nullptr);
    progpu_native_direct2d_command_stream_summary unsupported_summary{};
    unsupported_summary.struct_size =
        static_cast<uint32_t>(sizeof(unsupported_summary));
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_command_list_get_stream_summary(
            surface,
            unsupported_command_list.Get(),
            PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_OPTION_NONE,
            &unsupported_summary,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            (unsupported_summary.flags &
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_BALANCED_SCOPES) !=
                0U &&
            (unsupported_summary.flags &
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_UNSUPPORTED_OPERATIONS) !=
                0U &&
            (unsupported_summary.flags &
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_TEXT_RENDERING_PARAMETERS) !=
                0U &&
            unsupported_summary.unsupported_operation_count == 1U,
        "provider ID2D1CommandSink1 unsupported-stream audit changed");
    unsupported_summary.struct_size =
        static_cast<uint32_t>(sizeof(unsupported_summary));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_get_stream_summary(
            surface,
            unsupported_command_list.Get(),
            PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_OPTION_REQUIRE_SUPPORTED_OPERATIONS,
            &unsupported_summary,
            &native_hresult) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            native_hresult == E_NOTIMPL &&
            unsupported_summary.unsupported_operation_count == 1U,
        "unsupported Direct2D command stream did not fail strict preflight");
    progpu_native_direct2d_scene_stream_result unsupported_scene{};
    unsupported_scene.struct_size =
        static_cast<uint32_t>(sizeof(unsupported_scene));
    native_hresult = S_OK;
    require(
        progpu_native_direct2d_command_list_build_scene_stream(
            surface,
            unsupported_command_list.Get(),
            7002U,
            1U,
            nullptr,
            0U,
            &unsupported_scene,
            &native_hresult) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED &&
            native_hresult == E_NOTIMPL &&
            unsupported_scene.failure_reason ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE &&
            unsupported_scene.failure_callback_index != 0U &&
            unsupported_scene.written_bytes == 0U,
        "unsupported Direct2D state did not fail semantic translation");

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

    constexpr char svg_xml[] =
        "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'>"
        "<rect width='16' height='16' fill='#20a0e0'/></svg>";
    void* svg_document_value = nullptr;
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_create_svg_document(
            surface,
            reinterpret_cast<const uint8_t*>(svg_xml),
            static_cast<uint32_t>(std::size(svg_xml) - 1U),
            16.0F,
            16.0F,
            &svg_document_value,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            svg_document_value != nullptr && native_hresult == S_OK,
        "provider ID2D1SvgDocument creation failed");
    ComPtr<ID2D1SvgDocument> svg_document;
    svg_document.Attach(static_cast<ID2D1SvgDocument*>(svg_document_value));
    const D2D1_SIZE_F initial_svg_viewport =
        svg_document->GetViewportSize();
    require(initial_svg_viewport.width == 16.0F &&
            initial_svg_viewport.height == 16.0F,
        "provider ID2D1SvgDocument viewport changed");

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
    auto color_glyph_path =
        static_cast<progpu_native_direct2d_color_glyph_path>(99);
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_color_glyph_run(
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
            0U,
            PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL,
            &color_glyph_path,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE &&
            static_cast<uint32_t>(color_glyph_path) == 0U,
        "Direct2D color-glyph draw outside a draw did not fail closed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_svg_document(
            surface,
            svg_document.Get(),
            20.0F,
            12.0F,
            2.0F,
            3.0F,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE,
        "ID2D1DeviceContext5 SVG draw outside a draw did not fail closed");

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

        void* canvas_svg_document_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
                surface,
                svg_document.Get(),
                0.0F,
                &canvas_svg_document_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                canvas_svg_document_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasSvgDocument wrapping failed");
        ComPtr<IInspectable> canvas_svg_document;
        canvas_svg_document.Attach(
            static_cast<IInspectable*>(canvas_svg_document_value));
        progpu_native_direct2d_guid svg_document_id =
            to_portable_guid(__uuidof(ID2D1SvgDocument));
        void* unwrapped_svg_document_value = nullptr;
        native_hresult = E_FAIL;
        require(
            progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
                surface,
                canvas_svg_document.Get(),
                0.0F,
                &svg_document_id,
                &unwrapped_svg_document_value,
                &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
                unwrapped_svg_document_value != nullptr &&
                native_hresult == S_OK,
            "Win2D CanvasSvgDocument native-resource query failed");
        ComPtr<ID2D1SvgDocument> unwrapped_svg_document;
        unwrapped_svg_document.Attach(
            static_cast<ID2D1SvgDocument*>(unwrapped_svg_document_value));
        require(has_same_com_identity(
                svg_document.Get(),
                unwrapped_svg_document.Get()),
            "Win2D CanvasSvgDocument changed native COM identity");

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
    color_glyph_path =
        static_cast<progpu_native_direct2d_color_glyph_path>(0);
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_color_glyph_run(
            surface,
            28.0F,
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
            0U,
            PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL,
            &color_glyph_path,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK &&
            color_glyph_path >=
                PROGPU_NATIVE_DIRECT2D_COLOR_GLYPH_PATH_DEVICE_CONTEXT7 &&
            color_glyph_path <=
                PROGPU_NATIVE_DIRECT2D_COLOR_GLYPH_PATH_MONOCHROME_NO_COLOR,
        "provider typed Direct2D color-glyph draw failed");
    D2D1_MATRIX_3X2_F transform_before_svg{};
    context->GetTransform(&transform_before_svg);
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_svg_document(
            surface,
            svg_document.Get(),
            14.0F,
            10.0F,
            46.0F,
            2.0F,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider typed ID2D1DeviceContext5 SVG draw failed");
    D2D1_MATRIX_3X2_F transform_after_svg{};
    context->GetTransform(&transform_after_svg);
    const D2D1_SIZE_F viewport_after_draw =
        svg_document->GetViewportSize();
    require(transform_after_svg._11 == transform_before_svg._11 &&
            transform_after_svg._12 == transform_before_svg._12 &&
            transform_after_svg._21 == transform_before_svg._21 &&
            transform_after_svg._22 == transform_before_svg._22 &&
            transform_after_svg._31 == transform_before_svg._31 &&
            transform_after_svg._32 == transform_before_svg._32 &&
            viewport_after_draw.width == initial_svg_viewport.width &&
            viewport_after_draw.height == initial_svg_viewport.height,
        "provider SVG draw did not restore viewport/transform state");
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
    const progpu_native_direct2d_rect_f nested_clip = {
        0.0F, 0.0F, 24.0F, 24.0F
    };
    require(
        progpu_native_direct2d_surface_push_axis_aligned_clip(
            surface,
            &nested_clip,
            PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_PER_PRIMITIVE,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider nested Direct2D clip push failed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_pop_layer(
            surface,
            &native_hresult) ==
                PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH &&
            native_hresult == D2DERR_WRONG_STATE,
        "cross-kind Direct2D scope pop consumed the wrong state");
    require(
        progpu_native_direct2d_surface_pop_axis_aligned_clip(
            surface,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider nested Direct2D clip pop failed");
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
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_geometry_realization(
            surface,
            filled_realization.Get(),
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider filled geometry-realization draw failed");
    native_hresult = E_FAIL;
    require(
        progpu_native_direct2d_surface_draw_geometry_realization(
            surface,
            stroked_realization.Get(),
            solid_brush.Get(),
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS &&
            native_hresult == S_OK,
        "provider stroked geometry-realization draw failed");
    const progpu_native_direct2d_rect_f mutable_bitmap_destination = {
        0.0F, 44.0F, 2.0F, 2.0F
    };
    const progpu_native_direct2d_rect_f mutable_bitmap_source = {
        0.0F, 0.0F, 2.0F, 2.0F
    };
    require(
        progpu_native_direct2d_surface_draw_bitmap(
            surface,
            mutable_bitmap.Get(),
            &mutable_bitmap_destination,
            1.0F,
            PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
            &mutable_bitmap_source,
            nullptr,
            &native_hresult) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "provider mutated Direct2D bitmap draw failed");
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
    D3D11_TEXTURE2D_DESC staging_descriptor{};
    imported_texture->GetDesc(&staging_descriptor);
    staging_descriptor.Usage = D3D11_USAGE_STAGING;
    staging_descriptor.BindFlags = 0U;
    staging_descriptor.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    staging_descriptor.MiscFlags = 0U;
    ComPtr<ID3D11Texture2D> staging_texture;
    require(SUCCEEDED(d3d_device->CreateTexture2D(
                &staging_descriptor,
                nullptr,
                &staging_texture)),
        "Direct2D vector-draw staging texture creation failed");
    ComPtr<ID3D11DeviceContext> immediate_context;
    d3d_device->GetImmediateContext(&immediate_context);
    require(immediate_context != nullptr,
        "Direct2D vector-draw readback context was unavailable");
    immediate_context->CopyResource(
        staging_texture.Get(),
        imported_texture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped_texture{};
    require(SUCCEEDED(immediate_context->Map(
                staging_texture.Get(),
                0U,
                D3D11_MAP_READ,
                0U,
                &mapped_texture)),
        "Direct2D vector-draw staging map failed");
    const auto* pixel_bytes =
        static_cast<const uint8_t*>(mapped_texture.pData);
    const uint8_t* vector_pixel =
        pixel_bytes + mapped_texture.RowPitch * 40U + 8U * 4U;
    require(vector_pixel[0] == 96U && vector_pixel[1] == 48U &&
            vector_pixel[2] == 224U && vector_pixel[3] == 255U,
        "typed Direct2D command-list vector pixel changed");
    const uint8_t* memory_update_output =
        pixel_bytes + mapped_texture.RowPitch * 44U;
    require(memory_update_output[0] == 17U &&
            memory_update_output[1] == 34U &&
            memory_update_output[2] == 51U &&
            memory_update_output[3] == 255U,
        "typed Direct2D bitmap memory-update pixel changed");
    const uint8_t* bitmap_copy_output = memory_update_output + 4U;
    require(bitmap_copy_output[0] == 255U &&
            bitmap_copy_output[1] == 0U &&
            bitmap_copy_output[2] == 255U &&
            bitmap_copy_output[3] == 255U,
        "typed Direct2D GPU bitmap-copy pixel changed");
    immediate_context->Unmap(staging_texture.Get(), 0U);
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
