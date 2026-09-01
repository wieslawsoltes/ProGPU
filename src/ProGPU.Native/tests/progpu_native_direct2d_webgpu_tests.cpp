#include "progpu_native.h"
#include "progpu_native_direct2d_scene_submission.hpp"

#include <wgpu.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <span>
#include <thread>
#include <utility>
#include <vector>

namespace d2d = progpu::native::direct2d::compat;
namespace native_com = progpu::native::com;

namespace {

constexpr std::uint32_t width = 64U;
constexpr std::uint32_t height = 48U;
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

[[nodiscard]] gpu_context create_gpu()
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
    WGPUAdapter adapter = adapters[0U];
    for (std::size_t index = 1U; index < written; ++index) {
        wgpuAdapterRelease(adapters[index]);
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

    constexpr std::array<d2d::color_f, 2U> colors{
        d2d::color_f{0.0F, 0.0F, 1.0F, 1.0F},
        d2d::color_f{1.0F, 0.0F, 1.0F, 1.0F}};
    std::array<native_com::pointer<d2d::solid_color_brush>, 2U> brushes;
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

    const d2d::color_f clear{0.05F, 0.1F, 0.15F, 1.0F};
    const d2d::rectangle_f rectangle{4.0F, 4.0F, 20.0F, 20.0F};
    const d2d::ellipse ellipse_value{{40.0F, 14.0F}, 8.0F, 8.0F};
    const d2d::rectangle_f stroked{8.0F, 28.0F, 28.0F, 42.0F};
    const d2d::rounded_rectangle rounded{
        {36.0F, 28.0F, 56.0F, 44.0F}, 4.0F, 4.0F};
    const d2d::rectangle_f bitmap_destination{
        24.0F, 4.0F, 30.0F, 10.0F};
    const d2d::rectangle_f bitmap_brush_rectangle{
        22.0F, 12.0F, 30.0F, 20.0F};
    const d2d::ellipse bitmap_brush_ellipse{{26.0F, 24.0F}, 4.0F, 4.0F};
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
    require(target->EndDraw(nullptr, nullptr) == native_com::ok,
        "portable scene recording failed");
    native_com::pointer<d2d::scene_render_target_native> scene_target;
    require(target.as(
            d2d::scene_render_target_native_interface_id,
            scene_target) == native_com::ok &&
        scene_target, "portable scene target query failed");
    return {std::move(factory), std::move(target), std::move(scene_target)};
}

[[nodiscard]] std::vector<std::uint8_t> render_scene(
    const gpu_context& gpu,
    d2d::scene_render_target_native* scene_target)
{
    progpu_native_engine_options options{};
    options.struct_size = sizeof(options);
    options.abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.backend_abi = PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
    options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM;
    options.device = reinterpret_cast<std::uintptr_t>(gpu.device);
    options.queue = reinterpret_cast<std::uintptr_t>(gpu.queue);
    progpu_native_engine* engine = nullptr;
    require(progpu_native_engine_create(&options, &engine) ==
            PROGPU_NATIVE_STATUS_SUCCESS &&
        engine != nullptr, "ProGPU WebGPU engine creation failed");

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
        static_cast<std::size_t>(scene_target->GetRequiredSceneSize()));
    progpu_native_scene_metrics scene_metrics{};
    scene_metrics.struct_size = sizeof(scene_metrics);
    progpu_native_scene_frame_metrics frame_metrics{};
    frame_metrics.struct_size = sizeof(frame_metrics);
    d2d::scene_submission_diagnostics diagnostics{};
    const d2d::scene_render_options render_options{
        reinterpret_cast<std::uintptr_t>(view),
        PROGPU_NATIVE_SCENE_FRAME_NONE};
    const progpu_native_status render_status = d2d::render_scene_target(
            scene_target,
            engine,
            render_options,
            scratch,
            &scene_metrics,
            &frame_metrics,
            &diagnostics);
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
        scene_metrics.draw_count == 9U &&
        frame_metrics.command_count == 9U &&
        frame_metrics.submission_count == 1U;
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
    progpu_native_engine_destroy(engine);
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
    require(argc == 1 || argc == 2, "usage: test [CAPTURE_PPM]");
    gpu_context gpu = create_gpu();
    portable_scene scene = record_scene();
    const std::vector<std::uint8_t> pixels = render_scene(
        gpu, scene.scene_target.get());
    verify_pixels(pixels);
    write_capture(argc == 2 ? argv[1] : nullptr, pixels);
    const char* adapter_name = gpu.properties.name == nullptr
        ? "unknown"
        : gpu.properties.name;
    std::printf(
        "Portable Direct2D WebGPU passed: backend=%s adapter=%s "
        "draws=9 submissions=1 bytes=%zu\n",
        backend_name(gpu.properties.backendType),
        adapter_name,
        pixels.size());
    scene = {};
    release_gpu(gpu);
    return EXIT_SUCCESS;
}
