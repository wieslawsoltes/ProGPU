#include "progpu_native.h"
#include "progpu_native_direct2d_scene_submission.hpp"
#include "progpu_native_mil_visual_clip_fixture.hpp"
#include "progpu_native_mil_image_brush_fixture.hpp"

#include <wgpu.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <filesystem>
#include <limits>
#include <span>
#include <thread>
#include <utility>
#include <vector>

namespace d2d = progpu::native::direct2d::compat;
namespace native_com = progpu::native::com;

namespace {

constexpr std::uint32_t width = 64U;
constexpr std::uint32_t height = 64U;
constexpr std::uint32_t row_bytes = width * 4U;
constexpr auto gpu_timeout = std::chrono::seconds(30);

[[noreturn]] void fail(const char* message)
{
    std::fprintf(stderr, "ProGPU portable Direct2D WebGPU failed: %s\n",
        message);
    std::abort();
}

void require(bool condition, const char* message)
{
    if (!condition) {
        fail(message);
    }
}

struct device_request final {
    WGPUDevice device = nullptr;
    std::atomic_bool complete{false};
};

struct map_request final {
    WGPUBufferMapAsyncStatus status = WGPUBufferMapAsyncStatus_Unknown;
    std::atomic_bool complete{false};
};

void on_device_requested(
    WGPURequestDeviceStatus status,
    WGPUDevice device,
    const char* message,
    void* userdata)
{
    auto* request = static_cast<device_request*>(userdata);
    if (status != WGPURequestDeviceStatus_Success) {
        std::fprintf(stderr, "WebGPU device request failed: %s\n",
            message == nullptr ? "unknown error" : message);
    }
    request->device = status == WGPURequestDeviceStatus_Success
        ? device
        : nullptr;
    request->complete.store(true, std::memory_order_release);
}

void on_device_lost(WGPUDeviceLostReason, const char* message, void*)
{
    std::fprintf(stderr, "WebGPU device lost: %s\n",
        message == nullptr ? "unknown error" : message);
}

void on_uncaptured_error(WGPUErrorType, const char* message, void*)
{
    std::fprintf(stderr, "WebGPU validation error: %s\n",
        message == nullptr ? "unknown error" : message);
    std::abort();
}

void on_buffer_mapped(WGPUBufferMapAsyncStatus status, void* userdata)
{
    auto* request = static_cast<map_request*>(userdata);
    request->status = status;
    request->complete.store(true, std::memory_order_release);
}

[[nodiscard]] WGPUInstanceBackendFlags platform_backends() noexcept
{
#if defined(__APPLE__)
    return WGPUInstanceBackend_Metal;
#elif defined(_WIN32)
    return WGPUInstanceBackend_DX12;
#else
    return WGPUInstanceBackend_Vulkan;
#endif
}

[[nodiscard]] WGPUBackendType platform_backend_type() noexcept
{
#if defined(__APPLE__)
    return WGPUBackendType_Metal;
#elif defined(_WIN32)
    return WGPUBackendType_D3D12;
#else
    return WGPUBackendType_Vulkan;
#endif
}

[[nodiscard]] const char* backend_name(WGPUBackendType backend) noexcept
{
    switch (backend) {
        case WGPUBackendType_D3D12:
            return "D3D12";
        case WGPUBackendType_Metal:
            return "Metal";
        case WGPUBackendType_Vulkan:
            return "Vulkan";
        default:
            return "Unexpected";
    }
}

template<typename Request>
void wait_for_gpu_callback(
    WGPUDevice device,
    const Request& request,
    const char* timeout_message)
{
    const auto deadline = std::chrono::steady_clock::now() + gpu_timeout;
    while (!request.complete.load(std::memory_order_acquire)) {
        (void)wgpuDevicePoll(device, false, nullptr);
        if (std::chrono::steady_clock::now() >= deadline) {
            fail(timeout_message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
}

struct gpu_context final {
    WGPUInstance instance = nullptr;
    WGPUAdapter adapter = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUAdapterProperties properties{};
};

[[nodiscard]] gpu_context create_gpu(bool software = false)
{
    WGPUInstanceExtras extras{};
    extras.chain.sType = static_cast<WGPUSType>(WGPUSType_InstanceExtras);
    extras.backends = platform_backends();
    extras.flags = WGPUInstanceFlag_Validation;
    WGPUInstanceDescriptor instance_descriptor{};
    instance_descriptor.nextInChain = &extras.chain;
    WGPUInstance instance = wgpuCreateInstance(&instance_descriptor);
    require(instance != nullptr, "WebGPU instance creation failed");

    WGPUInstanceEnumerateAdapterOptions adapter_options{};
    adapter_options.backends = platform_backends();
    const std::size_t adapter_count = wgpuInstanceEnumerateAdapters(
        instance, &adapter_options, nullptr);
    require(adapter_count != 0U, "requested WebGPU adapter is unavailable");
    std::vector<WGPUAdapter> adapters(adapter_count);
    const std::size_t written = wgpuInstanceEnumerateAdapters(
        instance, &adapter_options, adapters.data());
    require(written != 0U && adapters[0U] != nullptr,
        "WebGPU adapter enumeration failed");
    std::size_t selected = 0U;
    if (software) {
        selected = written;
        for (std::size_t index = 0U; index < written; ++index) {
            WGPUAdapterProperties candidate{};
            wgpuAdapterGetProperties(adapters[index], &candidate);
            if (candidate.adapterType == WGPUAdapterType_CPU) selected = index;
        }
        require(selected != written, "requested software adapter is unavailable");
    }
    WGPUAdapter adapter = adapters[selected];
    for (std::size_t index = 0U; index < written; ++index) {
        if (index != selected) wgpuAdapterRelease(adapters[index]);
    }

    WGPUAdapterProperties properties{};
    wgpuAdapterGetProperties(adapter, &properties);
    require(properties.backendType == platform_backend_type(),
        "WebGPU selected the wrong platform backend");

    WGPUDeviceDescriptor device_descriptor{};
    device_descriptor.label = "ProGPU portable Direct2D WebGPU device";
    device_descriptor.defaultQueue.label =
        "ProGPU portable Direct2D WebGPU queue";
    device_descriptor.deviceLostCallback = on_device_lost;
    device_request requested{};
    wgpuAdapterRequestDevice(
        adapter,
        &device_descriptor,
        on_device_requested,
        &requested);
    require(requested.complete.load(std::memory_order_acquire) &&
        requested.device != nullptr,
        "WebGPU device request did not complete synchronously");
    wgpuDeviceSetUncapturedErrorCallback(
        requested.device, on_uncaptured_error, nullptr);
    WGPUQueue queue = wgpuDeviceGetQueue(requested.device);
    require(queue != nullptr, "WebGPU queue creation failed");
    return {instance, adapter, requested.device, queue, properties};
}

void release_gpu(gpu_context& gpu)
{
    wgpuQueueRelease(gpu.queue);
    wgpuDeviceRelease(gpu.device);
    wgpuAdapterRelease(gpu.adapter);
    wgpuInstanceRelease(gpu.instance);
    gpu = {};
}

struct portable_scene final {
    native_com::pointer<d2d::factory> factory;
    native_com::pointer<d2d::render_target> target;
    native_com::pointer<d2d::scene_render_target_native> scene_target;
};

[[nodiscard]] portable_scene record_scene()
{
    d2d::factory* raw_factory = nullptr;
    require(d2d::create_factory(&raw_factory) == native_com::ok &&
        raw_factory != nullptr, "portable factory creation failed");
    native_com::pointer<d2d::factory> factory;
    factory.attach(raw_factory);
    native_com::pointer<d2d::scene_factory_native> scene_factory;
    require(factory.as(
            d2d::scene_factory_native_interface_id,
            scene_factory) == native_com::ok &&
        scene_factory, "portable scene factory query failed");
    const d2d::scene_render_target_properties properties{
        width, height, 96.0F, 96.0F, 9301U, 1U};
    d2d::render_target* raw_target = nullptr;
    require(scene_factory->CreateSceneRenderTarget(
            &properties, &raw_target) == native_com::ok &&
        raw_target != nullptr, "portable render target creation failed");
    native_com::pointer<d2d::render_target> target;
    target.attach(raw_target);

    constexpr std::array<d2d::color_f, 4U> colors{
        d2d::color_f{0.0F, 0.0F, 1.0F, 1.0F},
        d2d::color_f{1.0F, 0.0F, 1.0F, 1.0F},
        d2d::color_f{1.0F, 1.0F, 0.0F, 1.0F},
        d2d::color_f{1.0F, 1.0F, 1.0F, 0.5F}};
    std::array<native_com::pointer<d2d::solid_color_brush>, 4U> brushes;
    for (std::size_t index = 0U; index < brushes.size(); ++index) {
        d2d::solid_color_brush* raw_brush = nullptr;
        require(target->CreateSolidColorBrush(
                &colors[index], nullptr, &raw_brush) == native_com::ok &&
            raw_brush != nullptr, "portable brush creation failed");
        brushes[index].attach(raw_brush);
    }
    constexpr d2d::gradient_stop linear_stops[]{
        {0.0F, {1.0F, 0.0F, 0.0F, 1.0F}},
        {1.0F, {1.0F, 1.0F, 0.0F, 1.0F}}};
    d2d::gradient_stop_collection* raw_linear_stops = nullptr;
    require(target->CreateGradientStopCollection(
            linear_stops,
            2U,
            d2d::gamma::gamma_2_2,
            d2d::extend_mode::clamp,
            &raw_linear_stops) == native_com::ok &&
        raw_linear_stops != nullptr,
        "portable linear gradient stops creation failed");
    native_com::pointer<d2d::gradient_stop_collection> linear_collection;
    linear_collection.attach(raw_linear_stops);
    const d2d::linear_gradient_brush_properties linear_properties{
        {4.0F, 12.0F}, {20.0F, 12.0F}};
    d2d::linear_gradient_brush* raw_linear = nullptr;
    require(target->CreateLinearGradientBrush(
            &linear_properties,
            nullptr,
            linear_collection.get(),
            &raw_linear) == native_com::ok &&
        raw_linear != nullptr,
        "portable linear gradient brush creation failed");
    native_com::pointer<d2d::linear_gradient_brush> linear;
    linear.attach(raw_linear);

    constexpr d2d::gradient_stop radial_stops[]{
        {0.0F, {0.0F, 1.0F, 0.0F, 1.0F}},
        {1.0F, {0.0F, 1.0F, 1.0F, 1.0F}}};
    d2d::gradient_stop_collection* raw_radial_stops = nullptr;
    require(target->CreateGradientStopCollection(
            radial_stops,
            2U,
            d2d::gamma::gamma_2_2,
            d2d::extend_mode::clamp,
            &raw_radial_stops) == native_com::ok &&
        raw_radial_stops != nullptr,
        "portable radial gradient stops creation failed");
    native_com::pointer<d2d::gradient_stop_collection> radial_collection;
    radial_collection.attach(raw_radial_stops);
    const d2d::radial_gradient_brush_properties radial_properties{
        {40.0F, 14.0F}, {0.0F, 0.0F}, 8.0F, 8.0F};
    d2d::radial_gradient_brush* raw_radial = nullptr;
    require(target->CreateRadialGradientBrush(
            &radial_properties,
            nullptr,
            radial_collection.get(),
            &raw_radial) == native_com::ok &&
        raw_radial != nullptr,
        "portable radial gradient brush creation failed");
    native_com::pointer<d2d::radial_gradient_brush> radial;
    radial.attach(raw_radial);

    constexpr std::array<std::byte, 16U> bitmap_pixels{
        std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    const d2d::bitmap_properties bitmap_properties{
        {87U, d2d::alpha_mode::premultiplied}, 96.0F, 96.0F};
    d2d::bitmap* raw_bitmap = nullptr;
    require(target->CreateBitmap(
            {2U, 2U},
            bitmap_pixels.data(),
            8U,
            &bitmap_properties,
            &raw_bitmap) == native_com::ok &&
        raw_bitmap != nullptr,
        "portable BGRA bitmap creation failed");
    native_com::pointer<d2d::bitmap> bitmap;
    bitmap.attach(raw_bitmap);
    constexpr std::array<std::byte, 4U> alpha_ignore_pixels{
        std::byte{0x20}, std::byte{0x40}, std::byte{0x80}, std::byte{0x00}};
    const d2d::bitmap_properties alpha_ignore_properties{
        {87U, d2d::alpha_mode::ignore}, 96.0F, 96.0F};
    d2d::bitmap* raw_alpha_ignore_bitmap = nullptr;
    require(target->CreateBitmap(
            {1U, 1U},
            alpha_ignore_pixels.data(),
            4U,
            &alpha_ignore_properties,
            &raw_alpha_ignore_bitmap) == native_com::ok &&
        raw_alpha_ignore_bitmap != nullptr,
        "portable alpha-ignore BGRA bitmap creation failed");
    native_com::pointer<d2d::bitmap> alpha_ignore_bitmap;
    alpha_ignore_bitmap.attach(raw_alpha_ignore_bitmap);
    constexpr std::array<std::byte, 16U> opacity_mask_pixels{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    d2d::bitmap* raw_opacity_mask = nullptr;
    require(target->CreateBitmap(
            {2U, 2U},
            opacity_mask_pixels.data(),
            8U,
            &bitmap_properties,
            &raw_opacity_mask) == native_com::ok &&
        raw_opacity_mask != nullptr,
        "portable opacity-mask bitmap creation failed");
    native_com::pointer<d2d::bitmap> opacity_mask;
    opacity_mask.attach(raw_opacity_mask);
    const d2d::bitmap_brush_properties bitmap_brush_properties{
        d2d::extend_mode::wrap,
        d2d::extend_mode::wrap,
        d2d::bitmap_interpolation_mode::nearest_neighbor};
    d2d::bitmap_brush* raw_bitmap_brush = nullptr;
    require(target->CreateBitmapBrush(
            bitmap.get(),
            &bitmap_brush_properties,
            nullptr,
            &raw_bitmap_brush) == native_com::ok &&
        raw_bitmap_brush != nullptr,
        "portable bitmap brush creation failed");
    native_com::pointer<d2d::bitmap_brush> bitmap_brush;
    bitmap_brush.attach(raw_bitmap_brush);

    d2d::path_geometry* raw_bitmap_brush_path = nullptr;
    require(factory->CreatePathGeometry(&raw_bitmap_brush_path) ==
            native_com::ok &&
        raw_bitmap_brush_path != nullptr,
        "portable bitmap-brush path creation failed");
    native_com::pointer<d2d::path_geometry> bitmap_brush_path;
    bitmap_brush_path.attach(raw_bitmap_brush_path);
    d2d::geometry_sink* raw_bitmap_brush_path_sink = nullptr;
    require(bitmap_brush_path->Open(&raw_bitmap_brush_path_sink) ==
            native_com::ok &&
        raw_bitmap_brush_path_sink != nullptr,
        "portable bitmap-brush path sink creation failed");
    native_com::pointer<d2d::geometry_sink> bitmap_brush_path_sink;
    bitmap_brush_path_sink.attach(raw_bitmap_brush_path_sink);
    bitmap_brush_path_sink->SetFillMode(d2d::fill_mode::winding);
    bitmap_brush_path_sink->BeginFigure(
        {1.0F, 30.0F}, d2d::figure_begin::filled);
    const d2d::point_2f bitmap_brush_path_points[]{
        {7.0F, 30.0F}, {4.0F, 44.0F}};
    bitmap_brush_path_sink->AddLines(
        bitmap_brush_path_points,
        2U);
    bitmap_brush_path_sink->EndFigure(d2d::figure_end::closed);
    require(bitmap_brush_path_sink->Close() == native_com::ok,
        "portable bitmap-brush path close failed");
    bitmap_brush_path_sink.Reset();

    d2d::path_geometry* raw_layer_mask_path = nullptr;
    require(factory->CreatePathGeometry(&raw_layer_mask_path) ==
            native_com::ok &&
        raw_layer_mask_path != nullptr,
        "portable layer-mask path creation failed");
    native_com::pointer<d2d::path_geometry> layer_mask_path;
    layer_mask_path.attach(raw_layer_mask_path);
    d2d::geometry_sink* raw_layer_mask_sink = nullptr;
    require(layer_mask_path->Open(&raw_layer_mask_sink) == native_com::ok &&
        raw_layer_mask_sink != nullptr,
        "portable layer-mask path sink creation failed");
    native_com::pointer<d2d::geometry_sink> layer_mask_sink;
    layer_mask_sink.attach(raw_layer_mask_sink);
    layer_mask_sink->SetFillMode(d2d::fill_mode::winding);
    layer_mask_sink->BeginFigure(
        {49.0F, 62.0F}, d2d::figure_begin::filled);
    const d2d::point_2f layer_mask_points[]{
        {56.0F, 49.0F}, {63.0F, 62.0F}};
    layer_mask_sink->AddLines(layer_mask_points, 2U);
    layer_mask_sink->EndFigure(d2d::figure_end::closed);
    require(layer_mask_sink->Close() == native_com::ok,
        "portable layer-mask path close failed");
    layer_mask_sink.Reset();

    d2d::layer* raw_layer = nullptr;
    require(target->CreateLayer(nullptr, &raw_layer) == native_com::ok &&
        raw_layer != nullptr, "portable layer creation failed");
    native_com::pointer<d2d::layer> layer;
    layer.attach(raw_layer);

    const d2d::size_f compatible_size{8.0F, 8.0F};
    const d2d::size_u compatible_pixel_size{8U, 8U};
    const d2d::pixel_format compatible_format{
        65U, d2d::alpha_mode::premultiplied};
    d2d::bitmap_render_target* raw_compatible_target = nullptr;
    require(target->CreateCompatibleRenderTarget(
            &compatible_size,
            &compatible_pixel_size,
            &compatible_format,
            d2d::compatible_render_target_options::none,
            &raw_compatible_target) == native_com::ok &&
        raw_compatible_target != nullptr,
        "portable compatible render target creation failed");
    native_com::pointer<d2d::bitmap_render_target> compatible_target;
    compatible_target.attach(raw_compatible_target);
    d2d::solid_color_brush* raw_compatible_brush = nullptr;
    const d2d::color_f compatible_white{1.0F, 1.0F, 1.0F, 1.0F};
    require(compatible_target->CreateSolidColorBrush(
            &compatible_white, nullptr, &raw_compatible_brush) ==
            native_com::ok &&
        raw_compatible_brush != nullptr,
        "portable compatible render target brush creation failed");
    native_com::pointer<d2d::solid_color_brush> compatible_brush;
    compatible_brush.attach(raw_compatible_brush);
    const d2d::color_f compatible_clear{};
    const d2d::rectangle_f compatible_opaque_half{
        4.0F, 0.0F, 8.0F, 8.0F};
    compatible_target->BeginDraw();
    compatible_target->Clear(&compatible_clear);
    compatible_target->FillRectangle(
        &compatible_opaque_half,
        static_cast<d2d::brush*>(compatible_brush.get()));
    require(compatible_target->EndDraw(nullptr, nullptr) == native_com::ok,
        "portable compatible render target recording failed");
    d2d::bitmap* raw_compatible_bitmap = nullptr;
    require(compatible_target->GetBitmap(&raw_compatible_bitmap) ==
            native_com::ok &&
        raw_compatible_bitmap != nullptr,
        "portable compatible render target bitmap retrieval failed");
    native_com::pointer<d2d::bitmap> compatible_bitmap;
    compatible_bitmap.attach(raw_compatible_bitmap);

    const d2d::color_f clear{0.05F, 0.1F, 0.15F, 1.0F};
    const d2d::rectangle_f rectangle{4.0F, 4.0F, 20.0F, 20.0F};
    const d2d::ellipse ellipse_value{{40.0F, 14.0F}, 8.0F, 8.0F};
    const d2d::rectangle_f stroked{8.0F, 28.0F, 28.0F, 42.0F};
    const d2d::rounded_rectangle rounded{
        {36.0F, 28.0F, 56.0F, 44.0F}, 4.0F, 2.0F};
    const d2d::rectangle_f bitmap_destination{
        24.0F, 4.0F, 30.0F, 10.0F};
    const d2d::rectangle_f alpha_ignore_destination{
        56.0F, 20.0F, 64.0F, 28.0F};
    const d2d::rectangle_f bitmap_brush_rectangle{
        22.0F, 12.0F, 30.0F, 20.0F};
    const d2d::ellipse bitmap_brush_ellipse{{26.0F, 24.0F}, 4.0F, 4.0F};
    const d2d::rectangle_f aliased_fill{0.0F, 48.0F, 16.0F, 64.0F};
    const d2d::rectangle_f aliased_clip{3.0F, 51.0F, 13.0F, 61.0F};
    const d2d::rectangle_f antialiased_fill{16.0F, 48.0F, 32.0F, 64.0F};
    const d2d::rectangle_f antialiased_clip{
        19.5F, 51.5F, 29.5F, 61.5F};
    const d2d::rectangle_f opacity_mask_destination{
        30.0F, 48.0F, 34.0F, 52.0F};
    const d2d::rectangle_f compatible_mask_destination{
        56.0F, 0.0F, 64.0F, 16.0F};
    const d2d::rectangle_f full_opacity_layer_bounds{
        32.0F, 20.0F, 36.0F, 24.0F};
    const d2d::rectangle_f opacity_layer_bounds{
        34.0F, 50.0F, 46.0F, 62.0F};
    const d2d::rectangle_f mask_layer_bounds{
        48.0F, 48.0F, 64.0F, 64.0F};
    const d2d::layer_parameters opacity_layer_parameters{
        opacity_layer_bounds,
        nullptr,
        d2d::antialias_mode::per_primitive,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        0.5F,
        nullptr,
        d2d::layer_options::none};
    constexpr float maximum_float = std::numeric_limits<float>::max();
    const d2d::layer_parameters full_opacity_layer_parameters{
        {-maximum_float, -maximum_float, maximum_float, maximum_float},
        nullptr,
        d2d::antialias_mode::per_primitive,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        1.0F,
        static_cast<d2d::brush*>(brushes[3U].get()),
        d2d::layer_options::none};
    const d2d::layer_parameters mask_layer_parameters{
        mask_layer_bounds,
        static_cast<d2d::geometry*>(layer_mask_path.get()),
        d2d::antialias_mode::per_primitive,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        1.0F,
        static_cast<d2d::brush*>(brushes[3U].get()),
        d2d::layer_options::none};
    target->BeginDraw();
    target->Clear(&clear);
    target->FillRectangle(
        &rectangle, static_cast<d2d::brush*>(linear.get()));
    target->FillEllipse(
        &ellipse_value, static_cast<d2d::brush*>(radial.get()));
    target->DrawRectangle(
        &stroked,
        static_cast<d2d::brush*>(brushes[0U].get()),
        3.0F,
        nullptr);
    target->FillRoundedRectangle(
        &rounded, static_cast<d2d::brush*>(brushes[1U].get()));
    target->DrawBitmap(
        bitmap.get(),
        &bitmap_destination,
        1.0F,
        d2d::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    target->DrawBitmap(
        alpha_ignore_bitmap.get(),
        &alpha_ignore_destination,
        1.0F,
        d2d::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    target->FillRectangle(
        &bitmap_brush_rectangle,
        static_cast<d2d::brush*>(bitmap_brush.get()));
    target->DrawEllipse(
        &bitmap_brush_ellipse,
        static_cast<d2d::brush*>(bitmap_brush.get()),
        2.0F,
        nullptr);
    target->FillGeometry(
        static_cast<d2d::geometry*>(bitmap_brush_path.get()),
        static_cast<d2d::brush*>(bitmap_brush.get()),
        nullptr);
    target->DrawGeometry(
        static_cast<d2d::geometry*>(bitmap_brush_path.get()),
        static_cast<d2d::brush*>(brushes[1U].get()),
        2.0F,
        nullptr);
    target->PushAxisAlignedClip(
        &aliased_clip, d2d::antialias_mode::aliased);
    target->FillRectangle(
        &aliased_fill, static_cast<d2d::brush*>(brushes[2U].get()));
    target->PopAxisAlignedClip();
    target->PushAxisAlignedClip(
        &antialiased_clip, d2d::antialias_mode::per_primitive);
    target->FillRectangle(
        &antialiased_fill, static_cast<d2d::brush*>(brushes[0U].get()));
    target->PopAxisAlignedClip();
    target->SetAntialiasMode(d2d::antialias_mode::aliased);
    target->FillOpacityMask(
        opacity_mask.get(),
        static_cast<d2d::brush*>(brushes[2U].get()),
        d2d::opacity_mask_content::graphics,
        &opacity_mask_destination,
        nullptr);
    const native_com::result opacity_mask_status =
        target->Flush(nullptr, nullptr);
    if (opacity_mask_status != native_com::ok) {
        std::fprintf(
            stderr,
            "portable Direct2D opacity mask status: %d\n",
            static_cast<int>(opacity_mask_status));
    }
    require(opacity_mask_status == native_com::ok,
        "portable Direct2D opacity mask recording failed");
    target->FillOpacityMask(
        compatible_bitmap.get(),
        static_cast<d2d::brush*>(brushes[2U].get()),
        d2d::opacity_mask_content::graphics,
        &compatible_mask_destination,
        nullptr);
    require(target->Flush(nullptr, nullptr) == native_com::ok,
        "portable compatible-target opacity mask recording failed");
    target->SetAntialiasMode(d2d::antialias_mode::per_primitive);
    target->PushLayer(&full_opacity_layer_parameters, layer.get());
    target->FillRectangle(
        &full_opacity_layer_bounds,
        static_cast<d2d::brush*>(brushes[1U].get()));
    // Active layer lifetime belongs to the render target's retained COM
    // interface, not to the caller's primary layer pointer.
    layer.reset();
    target->PopLayer();
    raw_layer = nullptr;
    require(target->CreateLayer(nullptr, &raw_layer) == native_com::ok &&
        raw_layer != nullptr, "portable replacement layer creation failed");
    layer.attach(raw_layer);
    target->PushLayer(&opacity_layer_parameters, layer.get());
    target->FillRectangle(
        &opacity_layer_bounds,
        static_cast<d2d::brush*>(brushes[1U].get()));
    target->PopLayer();
    target->PushLayer(&mask_layer_parameters, layer.get());
    target->FillRectangle(
        &mask_layer_bounds,
        static_cast<d2d::brush*>(brushes[2U].get()));
    target->PopLayer();
    const native_com::result end_draw_status =
        target->EndDraw(nullptr, nullptr);
    if (end_draw_status != native_com::ok) {
        std::fprintf(
            stderr,
            "portable Direct2D EndDraw status: %d\n",
            static_cast<int>(end_draw_status));
    }
    require(end_draw_status == native_com::ok,
        "portable scene recording failed");
    native_com::pointer<d2d::scene_render_target_native> scene_target;
    require(target.as(
            d2d::scene_render_target_native_interface_id,
            scene_target) == native_com::ok &&
        scene_target, "portable scene target query failed");
    return {std::move(factory), std::move(target), std::move(scene_target)};
}

[[nodiscard]] progpu_native_engine* create_engine(const gpu_context& gpu)
{
    progpu_native_engine_options options{};
    options.struct_size = sizeof(options);
    options.abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.backend_abi = PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
    options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM;
    options.device = reinterpret_cast<std::uintptr_t>(gpu.device);
    options.queue = reinterpret_cast<std::uintptr_t>(gpu.queue);
    const bool explicit_sampling =
        gpu.properties.backendType == WGPUBackendType_D3D12 &&
        gpu.properties.name != nullptr &&
        std::strstr(gpu.properties.name, "Parallels Display Adapter") != nullptr;
    if (explicit_sampling)
        options.flags |= PROGPU_NATIVE_ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING;
    std::fprintf(stderr, "Native image base-level sampling: %s\n",
        explicit_sampling ? "explicit-shader" : "native-sampler");
    progpu_native_engine* engine = nullptr;
    require(progpu_native_engine_create(&options, &engine) ==
            PROGPU_NATIVE_STATUS_SUCCESS &&
        engine != nullptr, "ProGPU WebGPU engine creation failed");
    return engine;
}

[[nodiscard]] std::vector<std::uint8_t> render_scene(
    const gpu_context& gpu,
    progpu_native_engine* engine,
    d2d::scene_render_target_native* scene_target,
    std::uint32_t expected_draws = 17U,
    std::uint32_t expected_commands = 27U,
    std::uint64_t expected_submissions = 4U,
    std::span<const std::byte> mil_scene = {},
    std::uint64_t mil_scene_id = 9011U,
    std::uint64_t mil_generation = 1U)
{
    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = "ProGPU portable Direct2D target";
    texture_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_CopySrc;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {width, height, 1U};
    texture_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(
        gpu.device, &texture_descriptor);
    require(texture != nullptr, "WebGPU target texture creation failed");
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    require(view != nullptr, "WebGPU target view creation failed");

    std::vector<std::byte> scratch(
        scene_target == nullptr ? 0U :
        static_cast<std::size_t>(scene_target->GetRequiredSceneSize()));
    progpu_native_scene_metrics scene_metrics{};
    scene_metrics.struct_size = sizeof(scene_metrics);
    progpu_native_scene_frame_metrics frame_metrics{};
    frame_metrics.struct_size = sizeof(frame_metrics);
    d2d::scene_submission_diagnostics diagnostics{};
    const d2d::scene_render_options render_options{
        reinterpret_cast<std::uintptr_t>(view),
        PROGPU_NATIVE_SCENE_FRAME_NONE};
    progpu_native_status render_status = PROGPU_NATIVE_STATUS_SUCCESS;
    if (!mil_scene.empty()) {
        render_status = progpu_native_engine_update_scene(
            engine, mil_scene.data(), mil_scene.size(), &scene_metrics);
        if (render_status == PROGPU_NATIVE_STATUS_SUCCESS) {
            progpu_native_scene_frame frame{};
            frame.struct_size = sizeof(frame);
            frame.width = width;
            frame.height = height;
            frame.dpi_scale = 1.0F;
            frame.target_view = reinterpret_cast<std::uintptr_t>(view);
            frame.clear_color = {0.0F, 0.0F, 0.0F, 1.0F};
            frame.scene_id = mil_scene_id;
            frame.generation = mil_generation;
            render_status = progpu_native_engine_render_scene(
                engine, &frame, &frame_metrics);
        }
    } else {
        render_status = d2d::render_scene_target(
            scene_target,
            engine,
            render_options,
            scratch,
            &scene_metrics,
            &frame_metrics,
            &diagnostics);
    }
    if (render_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        std::array<char, 512U> error{};
        (void)progpu_native_engine_get_last_error(
            engine, error.data(), error.size());
        std::fprintf(
            stderr,
            "Direct2D scene failure: status=%u stage=%u validation=%u "
            "offset=%u error=%s\n",
            static_cast<unsigned>(render_status),
            static_cast<unsigned>(diagnostics.stage),
            scene_metrics.validation_error,
            scene_metrics.error_offset,
            error.data());
    }
    const bool render_matches = render_status ==
            PROGPU_NATIVE_STATUS_SUCCESS &&
        diagnostics.stage == d2d::scene_submission_stage::none &&
        scene_metrics.draw_count == expected_draws &&
        frame_metrics.command_count == expected_commands &&
        frame_metrics.submission_count == expected_submissions;
    if (!render_matches) {
        std::fprintf(
            stderr,
            "Direct2D scene metrics: draws=%u commands=%u submissions=%llu\n",
            scene_metrics.draw_count,
            frame_metrics.command_count,
            static_cast<unsigned long long>(frame_metrics.submission_count));
    }
    require(render_matches,
        "portable Direct2D scene submission failed");

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = "ProGPU portable Direct2D readback";
    buffer_descriptor.size = static_cast<std::uint64_t>(row_bytes) * height;
    buffer_descriptor.usage = WGPUBufferUsage_CopyDst |
        WGPUBufferUsage_MapRead;
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(
        gpu.device, &buffer_descriptor);
    require(buffer != nullptr, "WebGPU readback buffer creation failed");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        gpu.device, nullptr);
    require(encoder != nullptr, "WebGPU copy encoder creation failed");
    WGPUImageCopyTexture source{};
    source.texture = texture;
    source.aspect = WGPUTextureAspect_All;
    WGPUImageCopyBuffer destination{};
    destination.buffer = buffer;
    destination.layout.bytesPerRow = row_bytes;
    destination.layout.rowsPerImage = height;
    const WGPUExtent3D extent{width, height, 1U};
    wgpuCommandEncoderCopyTextureToBuffer(
        encoder, &source, &destination, &extent);
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(encoder, nullptr);
    require(command != nullptr, "WebGPU copy command creation failed");
    wgpuQueueSubmit(gpu.queue, 1U, &command);

    map_request mapped{};
    wgpuBufferMapAsync(
        buffer,
        WGPUMapMode_Read,
        0U,
        buffer_descriptor.size,
        on_buffer_mapped,
        &mapped);
    wait_for_gpu_callback(
        gpu.device, mapped, "WebGPU readback mapping timed out");
    require(mapped.status == WGPUBufferMapAsyncStatus_Success,
        "WebGPU readback mapping failed");
    const auto* bytes = static_cast<const std::uint8_t*>(
        wgpuBufferGetConstMappedRange(buffer, 0U, buffer_descriptor.size));
    require(bytes != nullptr, "WebGPU readback range unavailable");
    std::vector<std::uint8_t> result(
        static_cast<std::size_t>(buffer_descriptor.size));
    std::copy_n(bytes, result.size(), result.data());
    wgpuBufferUnmap(buffer);

    wgpuCommandBufferRelease(command);
    wgpuCommandEncoderRelease(encoder);
    wgpuBufferDestroy(buffer);
    wgpuBufferRelease(buffer);
    wgpuTextureViewRelease(view);
    wgpuTextureDestroy(texture);
    wgpuTextureRelease(texture);
    return result;
}

void verify_pixels(std::span<const std::uint8_t> pixels)
{
    require(pixels.size() == static_cast<std::size_t>(row_bytes) * height,
        "portable Direct2D image size mismatch");
    const auto pixel = [pixels](std::uint32_t x, std::uint32_t y) {
        return pixels.data() + static_cast<std::size_t>(y) * row_bytes +
            static_cast<std::size_t>(x) * 4U;
    };
    const auto near_rgba = [](const std::uint8_t* value,
                              int red,
                              int green,
                              int blue) {
        constexpr int tolerance = 48;
        return std::abs(static_cast<int>(value[0U]) - red) <= tolerance &&
            std::abs(static_cast<int>(value[1U]) - green) <= tolerance &&
            std::abs(static_cast<int>(value[2U]) - blue) <= tolerance &&
            value[3U] >= 240U;
    };
    require(near_rgba(pixel(2U, 2U), 13, 26, 38),
        "portable Direct2D clear pixel is missing");
    require(near_rgba(pixel(10U, 10U), 255, 96, 0),
        "portable Direct2D linear-gradient pixel is missing");
    require(near_rgba(pixel(40U, 14U), 0, 255, 0),
        "portable Direct2D radial-gradient center is missing");
    require(near_rgba(pixel(46U, 14U), 0, 255, 191),
        "portable Direct2D radial-gradient edge is missing");
    require(near_rgba(pixel(18U, 28U), 0, 0, 255),
        "portable Direct2D stroke pixel is missing");
    require(near_rgba(pixel(46U, 36U), 255, 0, 255),
        "portable Direct2D rounded-rectangle pixel is missing");
    require(near_rgba(pixel(60U, 24U), 128, 64, 32),
        "portable Direct2D alpha-ignore bitmap is not opaque");
    require(near_rgba(pixel(34U, 22U), 134, 13, 147),
        "portable Direct2D full-target opacity-brush layer is missing");
    require(near_rgba(pixel(22U, 12U), 255, 0, 0) &&
            near_rgba(pixel(23U, 12U), 0, 255, 0) &&
            near_rgba(pixel(22U, 13U), 0, 0, 255) &&
            near_rgba(pixel(23U, 13U), 255, 255, 255),
        "portable Direct2D bitmap-brush repeat pixels are missing");
    require(near_rgba(pixel(26U, 20U), 255, 0, 0),
        "portable Direct2D bitmap-brush ellipse stroke is missing");
    require(near_rgba(pixel(4U, 34U), 255, 0, 0),
        "portable Direct2D bitmap-brush path fill is missing");
    require(near_rgba(pixel(2U, 34U), 255, 0, 255),
        "portable Direct2D geometry stroke is missing");
    require(near_rgba(pixel(8U, 56U), 255, 255, 0),
        "portable Direct2D aliased clip is missing");
    require(near_rgba(pixel(1U, 56U), 13, 26, 38),
        "portable Direct2D aliased clip leaked outside its bounds");
    require(near_rgba(pixel(24U, 56U), 0, 0, 255),
        "portable Direct2D antialiased clip is missing");
    require(near_rgba(pixel(30U, 49U), 13, 26, 38),
        "portable Direct2D opacity mask leaked through transparent alpha");
    require(near_rgba(pixel(33U, 49U), 255, 255, 0),
        "portable Direct2D opacity mask did not preserve bitmap alpha");
    require(near_rgba(pixel(57U, 8U), 13, 26, 38),
        "portable compatible-target mask leaked transparent alpha");
    require(near_rgba(pixel(62U, 8U), 255, 255, 0),
        "portable compatible-target mask lost opaque alpha");
    require(near_rgba(pixel(38U, 56U), 134, 13, 147),
        "portable Direct2D opacity layer is missing");
    require(near_rgba(pixel(56U, 56U), 134, 140, 19),
        "portable Direct2D geometric/opacity mask layer is missing");
    require(near_rgba(pixel(63U, 50U), 13, 26, 38),
        "portable Direct2D geometric mask leaked outside its path");
}

void verify_stroke_transforms(const gpu_context& gpu,
    progpu_native_engine* engine, portable_scene& scene)
{
    const d2d::stroke_style_properties properties{
        d2d::cap_style::flat, d2d::cap_style::flat, d2d::cap_style::flat,
        d2d::line_join::miter, 4.0F, d2d::dash_style::solid, 0.0F};
    const d2d::color_f blue{0.0F, 0.0F, 1.0F, 1.0F};
    const d2d::color_f clear{0.0F, 0.0F, 0.0F, 1.0F};
    native_com::pointer<d2d::solid_color_brush> brush;
    require(scene.target->CreateSolidColorBrush(&blue, nullptr, brush.put()) == native_com::ok,
        "device stroke brush creation failed");
    for (const bool curved : {false, true}) {
      for (const float dpi_scale : {1.0F, 2.0F}) {
        scene.target->SetDpi(96.0F * dpi_scale, 96.0F * dpi_scale);
        const d2d::matrix_3x2_f transform{
            2.0F, 0.0F, 0.0F, 3.0F, 0.0F, 0.5F / dpi_scale};
        scene.target->SetTransform(&transform);
        scene.target->SetAntialiasMode(d2d::antialias_mode::aliased);
        scene.target->BeginDraw();
        scene.target->Clear(&clear);
        for (std::uint32_t index = 0U; index < 3U; ++index) {
            native_com::pointer<d2d::stroke_style1> style;
            require(d2d::create_stroke_style1(scene.factory.get(), &properties,
                    static_cast<d2d::stroke_transform_type>(index), nullptr, 0U,
                    style.put()) == native_com::ok,
                "device stroke style creation failed");
            const float y = 2.0F + 3.0F * static_cast<float>(index);
            const float stroke_width = index == 2U ? 0.0F : 2.0F;
            if (curved) {
                native_com::pointer<d2d::path_geometry> path;
                native_com::pointer<d2d::geometry_sink> sink;
                require(scene.factory->CreatePathGeometry(path.put()) == native_com::ok &&
                    path->Open(sink.put()) == native_com::ok, "stroke curve creation failed");
                sink->BeginFigure({2.0F, y}, d2d::figure_begin::hollow);
                const d2d::bezier_segment curve{{5.0F, y}, {9.0F, y}, {12.0F, y}};
                sink->AddBezier(&curve);
                sink->EndFigure(d2d::figure_end::open);
                require(sink->Close() == native_com::ok, "stroke curve close failed");
                scene.target->DrawGeometry(path.get(), brush.get(), stroke_width, style.get());
            } else {
                scene.target->DrawLine({2.0F, y}, {12.0F, y}, brush.get(),
                    stroke_width, style.get());
            }
        }
        require(scene.target->EndDraw(nullptr, nullptr) == native_com::ok,
            "device stroke recording failed");
        const auto pixels = render_scene(gpu, engine,
            scene.scene_target.get(), 3U, 3U, 1U);
        const auto is_blue = [&](std::uint32_t y) {
            const auto offset = static_cast<std::size_t>(y) * row_bytes + 20U * 4U;
            return pixels[offset] == 0U && pixels[offset + 1U] == 0U &&
                pixels[offset + 2U] == 255U;
        };
        const auto scale = static_cast<std::uint32_t>(dpi_scale);
        require(is_blue(6U * scale) && is_blue(6U * scale + 2U * scale),
            "normal stroke lost world/DPI scaling");
        require(is_blue(15U * scale) && !is_blue(15U * scale + 2U * scale),
            "fixed stroke incorrectly inherited world scaling");
        if (scale == 2U) {
            require(is_blue(15U * scale + 1U),
                "fixed stroke lost DPI scaling");
        }
        require(is_blue(24U * scale) && !is_blue(24U * scale - 1U) &&
                !is_blue(24U * scale + 1U),
            "hairline did not remain one physical pixel at changed DPI");
      }
    }
}

void write_capture(const char* path, std::span<const std::uint8_t> pixels);

std::string image_brush_capture_directory()
{
#if defined(_MSC_VER)
    char* value = nullptr;
    std::size_t length = 0U;
    if (_dupenv_s(&value, &length, "PROGPU_NATIVE_MIL_IMAGE_BRUSH_CAPTURE_DIR") != 0)
        fail("could not read ImageBrush capture directory");
    const std::string result = value == nullptr ? "" : value;
    std::free(value);
    return result;
#else
    const char* value = std::getenv("PROGPU_NATIVE_MIL_IMAGE_BRUSH_CAPTURE_DIR");
    return value == nullptr ? "" : value;
#endif
}

void verify_mil_image_brushes(const gpu_context& gpu, progpu_native_engine* engine)
{
    using progpu::native::tests::mil_image_brush_fixture_options;
    struct box { std::uint32_t left, top, right, bottom; };
    struct sample { mil_image_brush_fixture_options options; box red; box blue; };
    const std::array cases{
        sample{{}, {8, 8, 32, 56}, {32, 8, 56, 56}},
        sample{{.stretch = 2U}, {8, 20, 32, 44}, {32, 20, 56, 44}},
        sample{{.stretch = 3U}, {8, 8, 32, 56}, {32, 8, 56, 56}},
        sample{{.stretch = 0U, .dpi_x = 6.0, .dpi_y = 12.0},
            {16, 28, 32, 36}, {32, 28, 48, 36}},
        // Viewbox is mapping-only. The red source outside it must be visible.
        sample{{.stretch = 2U, .viewbox = {1.0, 0.0, 1.0, 2.0}, .viewbox_units = 0U},
            {8, 8, 20, 32}, {20, 8, 44, 32}},
        sample{{.rotate = true}, {8, 8, 56, 32}, {8, 32, 56, 56}},
        sample{{.relative_scale = true}, {20, 20, 32, 44}, {32, 20, 44, 44}},
        sample{{.opacity = 0.5}, {8, 8, 32, 56}, {32, 8, 56, 56}}};
    for (std::size_t i = 0U; i < cases.size(); ++i) {
        std::vector<std::byte> stream;
        const auto identity = 9400U + i;
        require(progpu::native::tests::build_mil_image_brush_fixture(
            stream, cases[i].options, identity), "MIL ImageBrush fixture failed");
        progpu_native_scene_header header{};
        std::memcpy(&header, stream.data(), sizeof(header));
        require(header.command_count == 5U, "MIL ImageBrush command budget changed");
        const auto pixels = render_scene(gpu, engine, nullptr, 1U,
            header.command_count, 1U, stream, identity);
        const auto directory = image_brush_capture_directory();
        if (!directory.empty()) {
            std::filesystem::create_directories(directory);
            const auto path = std::filesystem::path(directory) /
                ("image-brush-" + std::to_string(i) + ".ppm");
            write_capture(path.string().c_str(), pixels);
            auto linear_options = cases[i].options;
            linear_options.linear = true;
            std::vector<std::byte> linear_stream;
            require(progpu::native::tests::build_mil_image_brush_fixture(
                linear_stream, linear_options, identity + 100U), "linear ImageBrush fixture failed");
            bool found_linear = false;
            for (std::uint32_t command_index = 0U; command_index < header.command_count; ++command_index) {
                progpu_native_scene_command command{};
                std::memcpy(&command, linear_stream.data() + header.command_offset +
                    command_index * sizeof(command), sizeof(command));
                if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
                    progpu_native_scene_image_draw draw{};
                    std::memcpy(&draw, linear_stream.data() + command.payload_offset, sizeof(draw));
                    require(draw.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR,
                        "ImageBrush linear sampling was lost during MIL lowering");
                    found_linear = true;
                }
            }
            require(found_linear, "linear ImageBrush image record missing");
            const auto linear_pixels = render_scene(gpu, engine, nullptr, 1U,
                header.command_count, 1U, linear_stream, identity + 100U);
            if (i == 0U) {
                const auto offset = (8U * width + 20U) * 4U;
                if (std::abs(static_cast<int>(linear_pixels[offset]) - 250) > 1 ||
                    std::abs(static_cast<int>(linear_pixels[offset + 2U]) - 5) > 1)
                    fail("ImageBrush linear sampler did not interpolate the source texels");
            }
            const auto linear_path = std::filesystem::path(directory) /
                ("image-brush-linear-" + std::to_string(i) + ".ppm");
            write_capture(linear_path.string().c_str(), linear_pixels);
        }
        const auto inside = [](box area, std::uint32_t x, std::uint32_t y) {
            return x >= area.left && x < area.right && y >= area.top && y < area.bottom;
        };
        // Explicit scalar pixel oracle: independent integer rectangles, not a
        // copy of the product's viewbox/stretch/transform algorithm.
        for (std::uint32_t y = 0U; y < height; ++y) {
            for (std::uint32_t x = 0U; x < width; ++x) {
                const auto offset = (y * width + x) * 4U;
                const int intensity = cases[i].options.opacity == 1.0 ? 255 : 128;
                const int red = inside(cases[i].red, x, y) ? intensity : 0;
                const int blue = inside(cases[i].blue, x, y) ? intensity : 0;
                if (std::abs(static_cast<int>(pixels[offset]) - red) > 1 ||
                    pixels[offset + 1U] != 0U ||
                    std::abs(static_cast<int>(pixels[offset + 2U]) - blue) > 1 ||
                    pixels[offset + 3U] != 255U) {
                    std::fprintf(stderr, "ImageBrush case=%zu pixel=%u,%u rgba=%u,%u,%u,%u expected=%d,0,%d,255\n",
                        i, x, y, pixels[offset], pixels[offset+1U], pixels[offset+2U], pixels[offset+3U], red, blue);
                    fail("MIL ImageBrush pixel oracle mismatch");
                }
            }
        }
        const auto warm = render_scene(gpu, engine, nullptr, 1U,
            header.command_count, 1U, stream, identity);
        if (pixels != warm) {
            const auto mismatch = std::mismatch(pixels.begin(), pixels.end(), warm.begin());
            std::fprintf(stderr, "ImageBrush warm mismatch: case=%zu byte=%zu initial=%u warm=%u\n",
                i, static_cast<std::size_t>(mismatch.first - pixels.begin()),
                *mismatch.first, *mismatch.second);
        }
        require(pixels == warm, "MIL ImageBrush stable replay changed pixels");
    }
    std::vector<std::byte> skewed;
    require(progpu::native::tests::build_mil_image_brush_fixture(skewed,
        {.skew = true}, 9490U), "skewed ImageBrush fixture failed");
    progpu_native_scene_header header{};
    std::memcpy(&header, skewed.data(), sizeof(header));
    require(header.command_count == 5U, "skewed ImageBrush command budget changed");
    const auto pixels = render_scene(gpu, engine, nullptr, 1U,
        header.command_count, 1U, skewed, 9490U);
    const auto rgb = [&pixels](std::uint32_t x, std::uint32_t y) {
        const auto offset = (y * width + x) * 4U;
        return std::array{pixels[offset], pixels[offset + 1U], pixels[offset + 2U]};
    };
    require(rgb(10U, 50U) == std::array<std::uint8_t, 3U>{0, 0, 0},
        "skewed viewport clip was broadened to bounds");
    require(rgb(24U, 32U) == std::array<std::uint8_t, 3U>{255, 0, 0} &&
        rgb(40U, 32U) == std::array<std::uint8_t, 3U>{0, 0, 255},
        "skewed viewport lost its source mapping");
}

void write_capture(
    const char* path,
    std::span<const std::uint8_t> pixels)
{
    if (path == nullptr || path[0] == '\0') {
        return;
    }
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    require(output.good(), "could not create Direct2D capture");
    output << "P6\n" << width << ' ' << height << "\n255\n";
    require(output.good(), "could not write Direct2D capture header");
    for (std::uint32_t y = 0U; y < height; ++y) {
        for (std::uint32_t x = 0U; x < width; ++x) {
            const std::size_t offset =
                static_cast<std::size_t>(y) * row_bytes +
                static_cast<std::size_t>(x) * 4U;
            output.write(
                reinterpret_cast<const char*>(pixels.data() + offset), 3);
            require(output.good(), "could not write Direct2D capture pixel");
        }
    }
    output.close();
    require(output.good(), "could not close Direct2D capture");
}

} // namespace

int main(int argc, char** argv)
{
    require(argc == 1 || argc == 2,
        "usage: test [CAPTURE_PPM|--mil-image-brush-only|--mil-image-brush-software]");
    const auto started = std::chrono::steady_clock::now();
    const auto phase = [&started](const char* name) {
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started).count();
        std::fprintf(stderr, "Native GPU phase: %s (%lld ms)\n", name,
            static_cast<long long>(elapsed));
    };
    phase("request adapter");
    const bool software = argc == 2 && std::strcmp(argv[1], "--mil-image-brush-software") == 0;
    gpu_context gpu = create_gpu(software);
    std::fprintf(stderr, "Native GPU adapter: backend=%s name=%s\n",
        backend_name(gpu.properties.backendType),
        gpu.properties.name == nullptr ? "unknown" : gpu.properties.name);
    // A host owns one engine across scene updates. Keep its device-local
    // pipelines alive across fixtures too: recreating them per image repeats
    // expensive cold D3D12 shader compilation, not rendering validation.
    progpu_native_engine* engine = create_engine(gpu);
    if (software || (argc == 2 && std::strcmp(argv[1], "--mil-image-brush-only") == 0)) {
        verify_mil_image_brushes(gpu, engine);
        progpu_native_engine_destroy(engine);
        release_gpu(gpu);
        return EXIT_SUCCESS;
    }
    phase("record Direct2D");
    portable_scene scene = record_scene();
    const std::vector<std::uint8_t> pixels = render_scene(
        gpu, engine, scene.scene_target.get());
    write_capture(argc == 2 ? argv[1] : nullptr, pixels);
    verify_pixels(pixels);
    phase("Direct2D pixels passed; start stroke transforms");
    verify_stroke_transforms(gpu, engine, scene);
    phase("start MIL image brushes");
    verify_mil_image_brushes(gpu, engine);
    phase("stroke transforms passed; start MIL geometry");
    std::vector<std::byte> mil_scene;
    require(progpu::native::tests::build_mil_visual_clip_fixture(mil_scene),
        "MIL visual geometry clip compilation failed");
    const auto mil_pixels = render_scene(gpu, engine, nullptr, 2U, 12U, 2U, mil_scene);
    const auto mil_pixel = [&mil_pixels](std::uint32_t x, std::uint32_t y,
                                       std::uint8_t red, std::uint8_t blue) {
        const std::size_t offset = (y * width + x) * 4U;
        return mil_pixels[offset] == red && mil_pixels[offset + 1U] == 0U &&
            mil_pixels[offset + 2U] == blue && mil_pixels[offset + 3U] == 255U;
    };
    require(mil_pixel(16U, 32U, 255U, 0U), "MIL first clipped sibling missing");
    require(mil_pixel(48U, 32U, 0U, 255U), "MIL sibling inherited wrong mask");
    require(mil_pixel(5U, 17U, 0U, 0U), "MIL ellipse clip broadened to bounds");
    require(mil_pixel(32U, 32U, 0U, 0U), "MIL sibling clip leaked");
    require(mil_pixel(58U, 32U, 0U, 0U), "MIL ancestor rounded clip leaked");
    require(mil_pixel(16U, 12U, 0U, 0U), "MIL nested render-data clip leaked");
    phase("MIL geometry passed; start effects");
    using progpu::native::tests::mil_clip_effect;
    for (const auto effect : {mil_clip_effect::zero_blur,
                             mil_clip_effect::blur,
                             mil_clip_effect::cached_blur,
                             mil_clip_effect::box_blur,
                             mil_clip_effect::shadow}) {
        std::fprintf(stderr, "Native GPU effect variant: %u\n",
            static_cast<unsigned>(effect));
        // Each independently compiled MIL channel has a distinct scene
        // identity; generation one must never replace different contents of
        // another generation-one scene or reuse its retained bitmap pixels.
        const std::uint64_t scene_id = 9011U + static_cast<std::uint64_t>(effect);
        require(progpu::native::tests::build_mil_visual_clip_fixture(
            mil_scene, effect, scene_id), "MIL effect geometry clip compilation failed");
        const auto effect_pixels = render_scene(gpu, engine, nullptr, 2U,
            effect == mil_clip_effect::cached_blur ? 24U : 16U,
            4U, mil_scene, scene_id);
        const auto component = [&effect_pixels](std::uint32_t x,
                                                std::uint32_t y,
                                                std::uint32_t channel) {
            return effect_pixels[(y * width + x) * 4U + channel];
        };
        std::fprintf(stderr, "MIL clip effect=%u edge=%u center=%u spread=%u\n",
            static_cast<unsigned>(effect),
            static_cast<unsigned>(component(7U, 32U, 0U)),
            static_cast<unsigned>(component(48U, 32U, 2U)),
            static_cast<unsigned>(component(16U, 12U, 0U)));
        // x=7 is outside the radius-six influence of the source's x=0
        // edge, but inside that of the visual ellipse's x=4 edge.
        require(component(7U, 32U, 0U) == 255U,
            "MIL effect source was prematurely clipped by its visual ellipse");
        require(component(48U, 32U, 2U) == 255U,
            "MIL effect sibling lost its independent clip frame");
        require(component(58U, 32U, 2U) == 0U &&
            component(58U, 32U, 1U) == 0U,
            "MIL effect output escaped its ancestor clip");
        require(component(32U, 32U, 0U) == 0U &&
            component(32U, 32U, 2U) == 0U &&
            component(32U, 32U, 1U) == 0U,
            "MIL effect output escaped its final ellipse clip");
        if (effect == mil_clip_effect::zero_blur) {
            require(effect_pixels == mil_pixels,
                "MIL zero-radius effect changed exact clip coverage");
        } else {
            const std::uint32_t spread_channel =
                effect == mil_clip_effect::shadow ? 1U : 0U;
            require(component(16U, 12U, spread_channel) > 0U &&
                component(16U, 12U, spread_channel) < 255U,
                "MIL nested content clip was incorrectly applied after blur");
        }
    }
    phase("MIL effects passed");
    using progpu::native::tests::mil_clip_cache_options;
    const std::array cache_cases{
        mil_clip_cache_options{.enabled = true},
        mil_clip_cache_options{.enabled = true, .gradient = true},
        mil_clip_cache_options{.enabled = true, .scale = 2.0},
        mil_clip_cache_options{.enabled = true, .gradient = true,
            .offset_x = 0.25, .offset_y = 0.25,
            .snaps = true, .guidelines = true},
        mil_clip_cache_options{.enabled = true, .nested = true}};
    for (std::size_t index = 0U; index < cache_cases.size(); ++index) {
        const auto& cache = cache_cases[index];
        const std::uint64_t scene_id = 9100U + index;
        std::fprintf(stderr, "Native GPU cache variant: %zu\n", index);
        progpu::native::tests::mil_clip_channel channel_owner;
        require(progpu::native::tests::build_mil_visual_clip_fixture(
            mil_scene, mil_clip_effect::none, scene_id, cache, &channel_owner),
            "MIL cached geometry clip compilation failed");
        const auto cached_pixels = render_scene(gpu, engine, nullptr, 2U,
            cache.nested ? 24U : 20U, cache.nested ? 5U : 4U,
            mil_scene, scene_id);
        const auto channel = [&cached_pixels](std::uint32_t x,
                                              std::uint32_t y,
                                              std::uint32_t component) {
            return cached_pixels[(y * width + x) * 4U + component];
        };
        std::fprintf(stderr, "MIL cache=%zu red=%u blue=%u\n", index,
            static_cast<unsigned>(channel(16U, 32U, 0U)),
            static_cast<unsigned>(channel(48U, 32U, 2U)));
        require(channel(32U, 32U, 0U) == 0U &&
                channel(32U, 32U, 2U) == 0U &&
                channel(58U, 32U, 2U) == 0U &&
                channel(5U, 17U, 0U) == 0U &&
                channel(16U, 12U, 0U) == 0U,
            "MIL cache geometry/content clip escaped or inherited a sibling mask");
        if (cache.gradient) {
            const int expected_red = cache.guidelines ? 64 : 66;
            require(std::abs(static_cast<int>(channel(16U, 32U, 0U)) -
                        expected_red) <= 1 &&
                    std::abs(static_cast<int>(channel(48U, 32U, 2U)) - 193) <= 1 &&
                    channel(8U, 32U, 0U) < channel(24U, 32U, 0U),
                "MIL cached gradient mask lost its world/guideline coordinates");
        } else {
            require(channel(16U, 32U, 0U) == 255U &&
                    channel(48U, 32U, 2U) == 255U,
                "MIL local cache lost clipped sibling contents");
            if (index == 0U) {
                require(cached_pixels == mil_pixels,
                    "MIL identity local cache changed exact clip coverage");
            }
        }
        const auto retained_pixels = render_scene(gpu, engine, nullptr, 2U,
            cache.nested ? 24U : 20U, 1U, mil_scene, scene_id);
        progpu_native_layer_metrics retained_metrics{};
        retained_metrics.struct_size = sizeof(retained_metrics);
        require(progpu_native_engine_get_layer_metrics(engine, &retained_metrics) ==
                PROGPU_NATIVE_STATUS_SUCCESS &&
                retained_metrics.content_pass_count == 0U,
            "MIL clipped bitmap cache rerasterized unchanged content");
        require(retained_pixels == cached_pixels,
            "MIL retained cache hit changed geometry/gradient clip pixels");
        if (index <= 1U) {
            require(progpu::native::tests::update_mil_visual_clip_fixture(
                channel_owner.get(), scene_id, 2U, mil_scene, 4.0),
                "MIL cache clip-only channel mutation failed");
            const auto reclipped = render_scene(gpu, engine, nullptr,
                2U, 20U, 4U, mil_scene, scene_id, 2U);
            require(progpu_native_engine_get_layer_metrics(engine, &retained_metrics) ==
                    PROGPU_NATIVE_STATUS_SUCCESS &&
                    retained_metrics.content_pass_count == 0U,
                "MIL clip-only mutation rerasterized retained cache content");
            require(cached_pixels[(32U * width + 8U) * 4U] > 0U &&
                    reclipped[(32U * width + 8U) * 4U] == 0U &&
                    reclipped[(32U * width + 16U) * 4U] ==
                        cached_pixels[(32U * width + 16U) * 4U] &&
                    reclipped[(32U * width + 48U) * 4U + 2U] ==
                        cached_pixels[(32U * width + 48U) * 4U + 2U],
                "MIL clip-only mutation reused stale mask pixels or changed cached source");
        }
    }
    phase("MIL cache clips passed");
    for (const auto effect : {mil_clip_effect::blur, mil_clip_effect::cached_blur}) {
        const mil_clip_cache_options cache{.nested = true, .root_scale = 2.0};
        const std::uint64_t scene_id = 9200U + static_cast<std::uint64_t>(effect);
        require(progpu::native::tests::build_mil_visual_clip_fixture(
            mil_scene, effect, scene_id, cache),
            "MIL effect inside oversized cache compilation failed");
        const auto nested_pixels = render_scene(gpu, engine, nullptr, 2U,
            effect == mil_clip_effect::cached_blur ? 28U : 20U,
            5U, mil_scene, scene_id);
        require(nested_pixels[(32U * width + 16U) * 4U] == 255U &&
                nested_pixels[(32U * width + 48U) * 4U + 2U] == 255U,
            "MIL nested effect was truncated to the root presentation extent");
        require(nested_pixels[(32U * width + 58U) * 4U + 2U] == 0U &&
                nested_pixels[(32U * width + 32U) * 4U] == 0U &&
                nested_pixels[(32U * width + 32U) * 4U + 2U] == 0U,
            "MIL nested effect escaped its output geometry clips");
    }
    phase("MIL oversized cache effects passed");
    const std::array viewport_cases{
        mil_clip_cache_options{.viewport3d = true},
        mil_clip_cache_options{.enabled = true, .viewport3d = true},
        mil_clip_cache_options{.enabled = true, .gradient = true, .viewport3d = true},
        mil_clip_cache_options{.enabled = true, .scale = 2.0, .viewport3d = true},
        mil_clip_cache_options{.viewport3d = true},
        mil_clip_cache_options{.viewport3d = true},
        mil_clip_cache_options{.enabled = true, .nested = true, .viewport3d = true},
        mil_clip_cache_options{.viewport3d = true, .mixed2d = true},
        mil_clip_cache_options{.viewport3d = true, .mixed2d = true, .rectangular_clips = true},
        mil_clip_cache_options{.enabled = true, .nested = true, .viewport3d = true, .mixed2d = true}};
    std::vector<std::uint8_t> viewport_reference;
    for (std::size_t index = 0; index < viewport_cases.size(); ++index) {
        const auto& viewport_options = viewport_cases[index];
        const auto viewport_effect = index == 4U ? mil_clip_effect::blur :
            index == 5U ? mil_clip_effect::cached_blur : mil_clip_effect::none;
        const std::uint64_t viewport_id = 9300U + index;
        std::fprintf(stderr, "Native GPU Viewport3D variant: %zu\n", index);
        require(progpu::native::tests::build_mil_visual_clip_fixture(
            mil_scene, viewport_effect, viewport_id, viewport_options),
            "MIL Viewport3D output geometry clip compilation failed");
        const std::uint32_t viewport_commands = (viewport_options.enabled ?
            (viewport_options.nested ? 20U : 16U) : index == 5U ? 20U : 12U) +
            (viewport_options.mixed2d ? 4U : 0U) -
            (viewport_options.rectangular_clips ? 4U : 0U);
        const std::uint32_t viewport_submissions = viewport_options.rectangular_clips ? 1U :
            viewport_options.mixed2d && !viewport_options.nested ? 4U :
            viewport_options.nested ? 3U : 2U;
        const auto viewport_pixels = render_scene(gpu, engine, nullptr,
            viewport_options.mixed2d ? 4U : 2U,
            viewport_commands, viewport_submissions, mil_scene, viewport_id);
        const auto viewport_channel = [&viewport_pixels](std::uint32_t x,
            std::uint32_t y, std::uint32_t component) {
            return viewport_pixels[(y * width + x) * 4U + component];
        };
        std::fprintf(stderr, "MIL viewport centers red=%u green=%u blue=%u green=%u\n",
            viewport_channel(16U, 32U, 0U), viewport_channel(16U, 32U, 1U),
            viewport_channel(48U, 32U, 2U), viewport_channel(48U, 32U, 1U));
        require(std::abs(static_cast<int>(viewport_channel(16U, 32U, 0U)) -
                    (viewport_options.gradient ? 66 : 255)) <= (viewport_options.gradient ? 1 : 0) &&
                std::abs(static_cast<int>(viewport_channel(48U, 32U, 2U)) -
                    (viewport_options.gradient ? 193 : 255)) <= (viewport_options.gradient ? 1 : 0) &&
                viewport_channel(16U, 32U, 1U) == 0U &&
                viewport_channel(48U, 32U, 1U) == 0U,
            "MIL clipped Viewport3D lost sibling pixels or isolated depth");
        require(viewport_channel(5U, 17U, 0U) ==
                    (viewport_options.rectangular_clips ? 255U : 0U) &&
                viewport_channel(32U, 32U, 0U) == 0U &&
                viewport_channel(32U, 32U, 2U) == 0U &&
                viewport_channel(58U, 32U, 2U) == 0U,
            "MIL Viewport3D geometry clip broadened or leaked");
        if (index == 0U) viewport_reference = viewport_pixels;
        if (viewport_options.mixed2d) {
            for (const auto y : {2U, 60U}) {
                require(viewport_channel(16U, y, 1U) == 255U &&
                        viewport_channel(16U, y, 2U) == 255U &&
                        viewport_channel(16U, y, 0U) == 0U,
                    "MIL 2D content before/after Viewport3D lost its output");
            }
        }
        if (index == 1U) require(viewport_reference == viewport_pixels,
            "MIL Viewport3D identity cache changed exact clip coverage");
        if (viewport_options.enabled || index == 5U || viewport_options.mixed2d) {
            const auto warm_pixels = render_scene(gpu, engine, nullptr,
                viewport_options.mixed2d ? 4U : 2U,
                viewport_commands, 1U, mil_scene, viewport_id);
            require(warm_pixels == viewport_pixels,
                "MIL Viewport3D warm cache changed depth/clip pixels");
            progpu_native_layer_metrics metrics{};
            metrics.struct_size = sizeof(metrics);
            require(progpu_native_engine_get_layer_metrics(engine, &metrics) ==
                    PROGPU_NATIVE_STATUS_SUCCESS, "MIL Viewport3D metrics unavailable");
            std::fprintf(stderr, "MIL viewport warm content=%u cache=%u effect=%u\n",
                metrics.content_pass_count, metrics.cache_hit, metrics.effect_cache_hit);
            // An uncached outer blur still composes its cached mesh source in
            // two sibling effect targets; neither retained mesh page is redrawn.
            require(metrics.content_pass_count ==
                    (index == 5U || index == 7U ? 2U : 0U),
                "MIL Viewport3D unchanged cache rerasterized its content");
        }
    }
    phase("MIL Viewport3D geometry clips passed");
    const char* adapter_name = gpu.properties.name == nullptr
        ? "unknown"
        : gpu.properties.name;
    std::printf(
        "Portable Direct2D WebGPU passed: backend=%s adapter=%s "
        "draws=17 submissions=4 bytes=%zu\n",
        backend_name(gpu.properties.backendType),
        adapter_name,
        pixels.size());
    scene = {};
    progpu_native_engine_destroy(engine);
    release_gpu(gpu);
    return EXIT_SUCCESS;
}
