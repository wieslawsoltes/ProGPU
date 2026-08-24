#include "progpu_native.h"
#include "progpu_native_sample_font.hpp"
#include "progpu_native_scene_builder.hpp"

#include <wgpu.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

namespace {

struct device_request {
    WGPUDevice device = nullptr;
    std::atomic_bool complete{false};
};

struct map_request {
    WGPUBufferMapAsyncStatus status = WGPUBufferMapAsyncStatus_Unknown;
    std::atomic_bool complete{false};
};

constexpr auto gpu_async_timeout = std::chrono::seconds(30);

bool wait_for_gpu_callback(
    WGPUDevice device,
    const std::atomic_bool& complete,
    const char* operation) {
    const auto deadline = std::chrono::steady_clock::now() + gpu_async_timeout;
    while (!complete.load(std::memory_order_acquire)) {
        (void)wgpuDevicePoll(device, false, nullptr);
        if (complete.load(std::memory_order_acquire)) {
            return true;
        }
        if (std::chrono::steady_clock::now() >= deadline) {
            std::cerr << operation << " timed out after "
                      << gpu_async_timeout.count() << " seconds.\n";
            return false;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return true;
}

void on_device_requested(
    WGPURequestDeviceStatus status,
    WGPUDevice device,
    const char* message,
    void* userdata) {
    auto& request = *static_cast<device_request*>(userdata);
    if (status == WGPURequestDeviceStatus_Success) {
        request.device = device;
    } else {
        std::cerr << "WebGPU device request failed: "
                  << (message == nullptr ? "unknown error" : message)
                  << '\n';
    }
    request.complete.store(true, std::memory_order_release);
}

void on_device_lost(
    WGPUDeviceLostReason,
    const char* message,
    void*) {
    std::cerr << "WebGPU device lost: "
              << (message == nullptr ? "unknown error" : message)
              << '\n';
}

void on_uncaptured_error(
    WGPUErrorType,
    const char* message,
    void*) {
    std::cerr << "WebGPU validation error: "
              << (message == nullptr ? "unknown error" : message)
              << '\n';
}

void on_buffer_mapped(WGPUBufferMapAsyncStatus status, void* userdata) {
    auto& request = *static_cast<map_request*>(userdata);
    request.status = status;
    request.complete.store(true, std::memory_order_release);
}

WGPUInstanceBackendFlags platform_backends() {
#if defined(__APPLE__)
    return WGPUInstanceBackend_Metal;
#elif defined(_WIN32)
    return WGPUInstanceBackend_DX12;
#else
    return WGPUInstanceBackend_Vulkan;
#endif
}

WGPUBackendType platform_backend_type() {
#if defined(__APPLE__)
    return WGPUBackendType_Metal;
#elif defined(_WIN32)
    return WGPUBackendType_D3D12;
#else
    return WGPUBackendType_Vulkan;
#endif
}

const char* backend_name(WGPUBackendType backend) {
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

bool request_device(
    WGPUInstance instance,
    WGPUAdapter& adapter,
    WGPUDevice& device,
    WGPUQueue& queue) {
    WGPUInstanceEnumerateAdapterOptions adapter_options{};
    adapter_options.backends = platform_backends();
    const std::size_t adapter_count = wgpuInstanceEnumerateAdapters(
        instance,
        &adapter_options,
        nullptr);
    if (adapter_count == 0U) {
        std::cerr << "No requested WebGPU backend adapter was found.\n";
        return false;
    }
    std::vector<WGPUAdapter> adapters(adapter_count);
    const std::size_t written = wgpuInstanceEnumerateAdapters(
        instance,
        &adapter_options,
        adapters.data());
    if (written == 0U || adapters[0] == nullptr) {
        std::cerr << "WebGPU adapter enumeration failed.\n";
        return false;
    }
    adapter = adapters[0];
    for (std::size_t index = 1U; index < written; ++index) {
        wgpuAdapterRelease(adapters[index]);
    }

    WGPUDeviceDescriptor descriptor{};
    descriptor.label = "ProGPU native sample device";
    descriptor.defaultQueue.label = "ProGPU native sample queue";
    descriptor.deviceLostCallback = on_device_lost;
    device_request request{};
    wgpuAdapterRequestDevice(
        adapter,
        &descriptor,
        on_device_requested,
        &request);
    if (!request.complete.load(std::memory_order_acquire) ||
        request.device == nullptr) {
        std::cerr << "The wgpu-native ABI did not complete the device request.\n";
        return false;
    }
    device = request.device;
    wgpuDeviceSetUncapturedErrorCallback(
        device,
        on_uncaptured_error,
        nullptr);
    queue = wgpuDeviceGetQueue(device);
    return queue != nullptr;
}

std::uint32_t align_row_bytes(std::uint32_t value) {
    return (value + 255U) & ~255U;
}

bool write_ppm(
    const std::string& path,
    const std::uint8_t* pixels,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes) {
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) {
        return false;
    }
    output << "P6\n" << width << ' ' << height << "\n255\n";
    for (std::uint32_t y = 0U; y < height; ++y) {
        const std::uint8_t* row = pixels + y * row_bytes;
        for (std::uint32_t x = 0U; x < width; ++x) {
            output.write(
                reinterpret_cast<const char*>(row + x * 4U),
                3);
        }
    }
    return output.good();
}

bool write_evidence(
    const std::string& path,
    const WGPUAdapterProperties& properties,
    const progpu_native_scene_frame_metrics& metrics,
    const char* hit_test_status,
    const std::string& capture_path) {
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) {
        return false;
    }
    output << "backend=" << backend_name(properties.backendType) << '\n'
           << "adapter="
           << (properties.name == nullptr ? "unknown" : properties.name)
           << '\n'
           << "driver="
           << (properties.driverDescription == nullptr
               ? "unknown"
               : properties.driverDescription)
           << '\n'
           << "vendor_id=" << properties.vendorID << '\n'
           << "device_id=" << properties.deviceID << '\n'
           << "draw_calls=" << metrics.draw_call_count << '\n'
           << "vertex_upload_bytes=" << metrics.vertex_upload_bytes << '\n'
           << "gpu_hit_test=" << hit_test_status << '\n'
           << "capture=" << capture_path << '\n';
    return output.good();
}

bool has_expected_colors(
    const std::uint8_t* pixels,
    std::uint32_t row_bytes,
    bool requires_decoded_glyph) {
    const auto pixel = [&](std::uint32_t x, std::uint32_t y) {
        return pixels + y * row_bytes + x * 4U;
    };
    const std::uint8_t* blue = pixel(100U, 100U);
    const std::uint8_t* amber = pixel(360U, 130U);
    const std::uint8_t* background = pixel(10U, 10U);
    if (!(blue[2] > 180U && blue[0] < 100U &&
        amber[0] > 180U && amber[1] > 90U &&
        background[0] < 30U && background[1] < 30U)) {
        return false;
    }
    if (!requires_decoded_glyph) {
        return true;
    }
    std::uint32_t yellow_coverage_pixels = 0U;
    for (std::uint32_t y = 230U; y < 300U; ++y) {
        for (std::uint32_t x = 68U; x < 116U; ++x) {
            const std::uint8_t* glyph_pixel = pixel(x, y);
            if (glyph_pixel[0] > 70U &&
                glyph_pixel[1] > 55U &&
                glyph_pixel[2] < 40U) {
                ++yellow_coverage_pixels;
            }
        }
    }
    return yellow_coverage_pixels >= 24U;
}

bool verify_retained_gpu_hit_test(
    progpu_native_engine* engine,
    WGPUDevice device) {
    progpu_native_hit_test_query query{};
    query.point = {80.0F, 80.0F};
    query.region_max = query.point;
    query.root_node_index = 0U;
    query.flags = 1U;
    std::uint64_t token = 0U;
    const auto begin_status = progpu_native_engine_begin_hit_test(
            engine,
            &query,
            &token);
    if (begin_status != PROGPU_NATIVE_STATUS_SUCCESS || token == 0U) {
        std::cerr << "GPU hit-test begin status="
                  << static_cast<int>(begin_status)
                  << " token=" << token << '\n';
        return false;
    }

    std::array<progpu_native_hit_test_result, 1U> hits{};
    progpu_native_hit_test_result summary{};
    std::uint32_t hit_count = 0U;
    std::uint8_t complete = 0U;
    const auto deadline = std::chrono::steady_clock::now() + gpu_async_timeout;
    while (complete == 0U) {
        const auto poll_status = progpu_native_engine_poll_hit_test(
                engine,
                token,
                hits.data(),
                static_cast<std::uint32_t>(hits.size()),
                &hit_count,
                &summary,
                &complete);
        if (poll_status != PROGPU_NATIVE_STATUS_SUCCESS) {
            std::cerr << "GPU hit-test poll status="
                      << static_cast<int>(poll_status) << '\n';
            return false;
        }
        if (complete == 0U) {
            (void)wgpuDevicePoll(device, false, nullptr);
            if (std::chrono::steady_clock::now() >= deadline) {
                std::cerr << "GPU hit testing timed out after "
                          << gpu_async_timeout.count() << " seconds.\n";
                return false;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }
    const bool passed = summary.hit == 1U &&
        summary.candidate_count == 1U &&
        summary.nodes_visited == 1U && summary.precise_tests == 1U &&
        hit_count == 1U && hits[0U].hit != 0U && hits[0U].id == 501 &&
        hits[0U].primitive_index == 0U;
    if (!passed) {
        std::cerr << "GPU hit-test result: summary(hit=" << summary.hit
                  << ", id=" << summary.id
                  << ", primitive=" << summary.primitive_index
                  << ", candidates=" << summary.candidate_count
                  << ", nodes=" << summary.nodes_visited
                  << ", precise=" << summary.precise_tests
                  << "), count=" << hit_count
                  << ", first(hit=" << hits[0U].hit
                  << ", id=" << hits[0U].id
                  << ", primitive=" << hits[0U].primitive_index
                  << ")\n";
    }
    return passed;
}

} // namespace

int main(int argc, char** argv) {
    const std::string output_path = argc > 1
        ? argv[1]
        : "progpu-native-sample.ppm";
    const std::string evidence_path = argc > 2 ? argv[2] : std::string{};
    const std::string font_path = argc > 3 ? argv[3] : std::string{};

    WGPUInstanceExtras extras{};
    extras.chain.sType = static_cast<WGPUSType>(WGPUSType_InstanceExtras);
    extras.backends = platform_backends();
    extras.flags = WGPUInstanceFlag_Validation;
    WGPUInstanceDescriptor instance_descriptor{};
    instance_descriptor.nextInChain = &extras.chain;
    WGPUInstance instance = wgpuCreateInstance(&instance_descriptor);
    if (instance == nullptr) {
        std::cerr << "Could not create the wgpu-native instance.\n";
        return EXIT_FAILURE;
    }

    WGPUAdapter adapter = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    if (!request_device(instance, adapter, device, queue)) {
        wgpuInstanceRelease(instance);
        return EXIT_FAILURE;
    }
    WGPUAdapterProperties adapter_properties{};
    wgpuAdapterGetProperties(adapter, &adapter_properties);
    const std::string adapter_name = adapter_properties.name == nullptr
        ? "unknown"
        : adapter_properties.name;
    const bool defer_software_d3d12_hit_test =
        adapter_properties.backendType == WGPUBackendType_D3D12 &&
        adapter_name == "Microsoft Basic Render Driver";
    const char* hit_test_status = defer_software_d3d12_hit_test
        ? "deferred-software-adapter"
        : "passed";
    if (adapter_properties.backendType != platform_backend_type()) {
        std::cerr << "Expected " << backend_name(platform_backend_type())
                  << " but selected "
                  << backend_name(adapter_properties.backendType) << ".\n";
        return EXIT_FAILURE;
    }
    std::cout << "[ProGPUNative] selected "
              << backend_name(adapter_properties.backendType)
              << " adapter '" << adapter_name << "'.\n";

    constexpr std::uint32_t width = 640U;
    constexpr std::uint32_t height = 360U;
    WGPUTextureDescriptor target_descriptor{};
    target_descriptor.label = "ProGPU native sample target";
    target_descriptor.usage =
        WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc;
    target_descriptor.dimension = WGPUTextureDimension_2D;
    target_descriptor.size = {width, height, 1U};
    target_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    target_descriptor.mipLevelCount = 1U;
    target_descriptor.sampleCount = 1U;
    WGPUTexture target = wgpuDeviceCreateTexture(device, &target_descriptor);
    WGPUTextureView target_view = target == nullptr
        ? nullptr
        : wgpuTextureCreateView(target, nullptr);
    if (target_view == nullptr) {
        std::cerr << "Could not create the native sample target.\n";
        return EXIT_FAILURE;
    }

    progpu_native_engine_options options{};
    options.struct_size = sizeof(options);
    options.abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.backend_abi =
        PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
    options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM;
    options.device = reinterpret_cast<std::uintptr_t>(device);
    options.queue = reinterpret_cast<std::uintptr_t>(queue);
    progpu_native_engine* engine = nullptr;
    const progpu_native_status create_status =
        progpu_native_engine_create(&options, &engine);
    if (create_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        std::cerr << "Could not create ProGPU native engine: "
                  << static_cast<int>(create_status) << '\n';
        return EXIT_FAILURE;
    }

    progpu::native::semantic_scene_builder scene_builder(501U, 1U);
    if (!scene_builder.reserve(9U, 11U, 9216U)) {
        std::cerr << "Could not reserve the native retained scene builder.\n";
        return EXIT_FAILURE;
    }
    std::array<std::uint32_t, 5U> brush_indices{};
    if (!scene_builder.add_solid_brush(
            {0.08F, 0.42F, 0.95F, 1.0F}, 1.0F, brush_indices[0]) ||
        !scene_builder.add_solid_brush(
            {0.98F, 0.52F, 0.08F, 1.0F}, 1.0F, brush_indices[1]) ||
        !scene_builder.add_solid_brush(
            {0.20F, 0.82F, 0.48F, 1.0F}, 0.90F, brush_indices[2]) ||
        !scene_builder.add_solid_brush(
            {0.20F, 0.82F, 0.95F, 1.0F}, 1.0F, brush_indices[3]) ||
        !scene_builder.add_solid_brush(
            {0.72F, 0.34F, 0.96F, 1.0F}, 1.0F, brush_indices[4])) {
        std::cerr << "Could not record native retained brushes.\n";
        return EXIT_FAILURE;
    }
    const auto identity =
        progpu::native::semantic_scene_builder::identity_transform();
    progpu_native_scene_layer_mask scene_mask{};
    scene_mask.bounds = {36.0F, 36.0F, 600.0F, 314.0F};
    scene_mask.transform = identity;
    for (std::size_t index = 0U; index < 4U; ++index) {
        scene_mask.corner_radii_x[index] = 18.0F;
        scene_mask.corner_radii_y[index] = 18.0F;
    }
    scene_mask.opacity = 1.0F;
    progpu_native_group_effect drop_shadow{};
    drop_shadow.kind = PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
    drop_shadow.revision = 1U;
    drop_shadow.sigma_x = 2.0F;
    drop_shadow.sigma_y = 2.0F;
    drop_shadow.offset_x = 5.0F;
    drop_shadow.offset_y = 5.0F;
    drop_shadow.color_r = 0.0F;
    drop_shadow.color_g = 0.0F;
    drop_shadow.color_b = 0.0F;
    drop_shadow.color_a = 0.65F;
    std::uint32_t scene_mask_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t scene_effect_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!scene_builder.add_rounded_rectangle_mask(
            scene_mask,
            scene_mask_index) ||
        !scene_builder.add_effect_chain(
            std::span<const progpu_native_group_effect>(&drop_shadow, 1U),
            1U,
            scene_effect_index)) {
        std::cerr << "Could not record native retained layer resources.\n";
        return EXIT_FAILURE;
    }
    progpu_native_scene_layer scene_layer{};
    scene_layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
    scene_layer.bounds = {32.0F, 32.0F, 608.0F, 324.0F};
    scene_layer.opacity = 1.0F;
    scene_layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    scene_layer.mask_resource_index = scene_mask_index;
    scene_layer.effect_resource_index = scene_effect_index;
    scene_layer.content_revision = 1U;
    scene_layer.composite_revision = 1U;
    if (!scene_builder.push_layer(scene_layer)) {
        std::cerr << "Could not begin the native retained layer.\n";
        return EXIT_FAILURE;
    }
    const std::array primitives{
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            48.0F, 48.0F, 180.0F, 120.0F, 0.0F, 0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F}, identity},
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            280.0F, 64.0F, 280.0F, 132.0F, 0.0F, 0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F}, identity},
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE, 0U,
            128.0F, 224.0F, 384.0F, 72.0F, 12.0F, 0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F}, identity}};
    if (!scene_builder.draw_analytic(
            primitives,
            std::span<const std::uint32_t>(brush_indices.data(), 3U),
            {48.0F, 48.0F, 512.0F, 248.0F})) {
        std::cerr << "Could not record native retained primitives.\n";
        return EXIT_FAILURE;
    }
    progpu_native_geometry_primitive separator{};
    separator.kind = PROGPU_NATIVE_GEOMETRY_LINE;
    separator.p0 = {48.0F, 204.0F};
    separator.p1 = {560.0F, 204.0F};
    separator.stroke_thickness = 3.0F;
    separator.color = {1.0F, 1.0F, 1.0F, 1.0F};
    separator.transform = identity;
    if (!scene_builder.draw_geometry(
            std::span<const progpu_native_geometry_primitive>(
                &separator,
                1U),
            std::span<const std::uint32_t>(&brush_indices[3], 1U),
            {46.0F, 200.0F, 516.0F, 8.0F})) {
        std::cerr << "Could not record native retained geometry.\n";
        return EXIT_FAILURE;
    }
    const std::array native_stroke_points{
        progpu_native_point{64.0F, 326.0F},
        progpu_native_point{196.0F, 310.0F},
        progpu_native_point{332.0F, 334.0F},
        progpu_native_point{548.0F, 314.0F}};
    const std::array native_stroke_dashes{8.0, 4.0};
    progpu_native_scene_stroke native_stroke{};
    native_stroke.struct_size = sizeof(native_stroke);
    native_stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
    native_stroke.point_count = native_stroke_points.size();
    native_stroke.dash_interval_count = native_stroke_dashes.size();
    native_stroke.color = {1.0F, 1.0F, 1.0F, 1.0F};
    native_stroke.transform = identity;
    native_stroke.stroke_thickness = 3.0F;
    native_stroke.miter_limit = 10.0F;
    native_stroke.start_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    native_stroke.end_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    native_stroke.line_join = PROGPU_NATIVE_STROKE_JOIN_ROUND;
    native_stroke.dash_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    if (!scene_builder.draw_strokes(
            std::span<const progpu_native_scene_stroke>(&native_stroke, 1U),
            native_stroke_points,
            native_stroke_dashes,
            std::span<const std::uint32_t>(&brush_indices[3], 1U),
            {60.0F, 304.0F, 492.0F, 34.0F})) {
        std::cerr << "Could not record native retained strokes.\n";
        return EXIT_FAILURE;
    }
    const std::array native_path_segments{
        progpu_native_path_segment{
            {566.0F, 228.0F}, {616.0F, 264.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {616.0F, 264.0F}, {566.0F, 298.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {566.0F, 298.0F}, {566.0F, 228.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_path_fill native_path{
        0U,
        native_path_segments.size(),
        0U,
        0U,
        566.0F,
        228.0F,
        616.0F,
        298.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        identity,
        PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        8U};
    if (!scene_builder.draw_paths(
            std::span<const progpu_native_scene_path_fill>(&native_path, 1U),
            native_path_segments,
            std::span<const std::uint32_t>(&brush_indices[4], 1U),
            {562.0F, 224.0F, 58.0F, 78.0F})) {
        std::cerr << "Could not record native retained paths.\n";
        return EXIT_FAILURE;
    }
    constexpr std::array<std::byte, 16U> native_image_pixels{
        std::byte{0xff}, std::byte{0x20}, std::byte{0x70}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xe8}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xe8}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x20}, std::byte{0x70}, std::byte{0xff}};
    std::uint32_t native_image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_image_draw native_image{};
    native_image.image_width = 2U;
    native_image.image_height = 2U;
    native_image.row_bytes = 8U;
    native_image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    native_image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    native_image.destination_rect = {430.0F, 92.0F, 88.0F, 64.0F};
    native_image.transform = identity;
    native_image.opacity = 1.0F;
    if (!scene_builder.add_rgba8_image(
            2U,
            2U,
            8U,
            native_image_pixels,
            native_image_index) ||
        !scene_builder.draw_image(
            native_image_index,
            native_image,
            {430.0F, 92.0F, 88.0F, 64.0F})) {
        std::cerr << "Could not record native retained image.\n";
        return EXIT_FAILURE;
    }
    const std::array fallback_glyph_segments{
        progpu_native_path_segment{
            {0.0F, 0.0F}, {30.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {30.0F, 0.0F}, {15.0F, 40.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {15.0F, 40.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    progpu::native::sample::decoded_font_glyph decoded_glyph{};
    std::string font_error;
    if (!font_path.empty() &&
        !progpu::native::sample::try_load_font_glyph(
            font_path,
            0x00e9U,
            decoded_glyph,
            font_error)) {
        std::cerr << "Could not decode U+00E9 from '" << font_path
                  << "': " << font_error << ".\n";
        return EXIT_FAILURE;
    }
    const bool uses_decoded_glyph = !decoded_glyph.segments.empty();
    const std::span<const progpu_native_path_segment> native_glyph_segments =
        uses_decoded_glyph
            ? std::span<const progpu_native_path_segment>(
                decoded_glyph.segments)
            : std::span<const progpu_native_path_segment>(
                fallback_glyph_segments);
    constexpr float font_size = 48.0F;
    constexpr float raster_font_size = 64.0F;
    const float units_per_em = uses_decoded_glyph
        ? static_cast<float>(decoded_glyph.units_per_em)
        : raster_font_size;
    const float design_to_logical = font_size / units_per_em;
    const float glyph_min_x = uses_decoded_glyph ? decoded_glyph.min_x : 0.0F;
    const float glyph_min_y = uses_decoded_glyph ? decoded_glyph.min_y : 0.0F;
    const float glyph_max_x = uses_decoded_glyph ? decoded_glyph.max_x : 30.0F;
    const float glyph_max_y = uses_decoded_glyph ? decoded_glyph.max_y : 40.0F;
    const float glyph_raster_scale = uses_decoded_glyph
        ? raster_font_size / units_per_em
        : 1.0F;
    const float atlas_to_logical_scale = uses_decoded_glyph
        ? font_size / raster_font_size
        : 1.0F;
    const progpu_native_scene_glyph_outline native_glyph_outline{
        0U,
        native_glyph_segments.size(),
        glyph_min_x,
        glyph_min_y,
        glyph_max_x,
        glyph_max_y,
        glyph_raster_scale,
        0.25F};
    constexpr progpu_native_point glyph_position{76.0F, 288.0F};
    const progpu_native_positioned_glyph native_glyph{
        0U,
        0U,
        glyph_position,
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {1.0F, 1.0F, 1.0F, 1.0F},
        atlas_to_logical_scale,
        0.0F,
        0.0F,
        0.0F};
    const progpu_native_scene_text_style native_text_style{
        {1.0F, 0.84F, 0.10F, 1.0F},
        PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE,
        0U,
        0U,
        0U};
    std::uint32_t native_glyph_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t native_text_style_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!scene_builder.add_glyph_outlines(
            std::span<const progpu_native_scene_glyph_outline>(
                &native_glyph_outline,
                1U),
            native_glyph_segments,
            native_glyph_resource) ||
        !scene_builder.add_text_style(
            native_text_style,
            native_text_style_index) ||
        !scene_builder.draw_glyph_run(
            native_glyph_resource,
            std::span<const progpu_native_positioned_glyph>(
                &native_glyph,
                1U),
            {
                glyph_position.x + glyph_min_x * design_to_logical,
                glyph_position.y - glyph_max_y * design_to_logical,
                (glyph_max_x - glyph_min_x) * design_to_logical,
                (glyph_max_y - glyph_min_y) * design_to_logical},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            native_text_style_index)) {
        std::cerr << "Could not record native retained glyph.\n";
        return EXIT_FAILURE;
    }
    if (uses_decoded_glyph) {
        std::cout << "[ProGPUNativeText] decoded U+00E9 as glyph "
                  << decoded_glyph.glyph_index << " with "
                  << native_glyph_segments.size()
                  << " retained path records.\n";
    }
    const progpu_native_scene_color_glyph_bitmap native_color_bitmap{
        0U, 2U, 2U, 8U, 0U,
        0.0F, 0.0F, 36.0F, 40.0F, 0U, 0U};
    const progpu_native_positioned_glyph native_color_glyph{
        0U,
        0U,
        {378.0F, 104.0F},
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {1.0F, 1.0F, 1.0F, 1.0F},
        1.0F,
        0.0F,
        0.0F,
        0.0F};
    std::uint32_t native_color_glyph_resource =
        PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!scene_builder.add_color_glyph_bitmaps(
            std::span<const progpu_native_scene_color_glyph_bitmap>(
                &native_color_bitmap,
                1U),
            native_image_pixels,
            native_color_glyph_resource) ||
        !scene_builder.draw_glyph_run(
            native_color_glyph_resource,
            std::span<const progpu_native_positioned_glyph>(
                &native_color_glyph,
                1U),
            {378.0F, 104.0F, 36.0F, 40.0F})) {
        std::cerr << "Could not record native retained color glyph.\n";
        return EXIT_FAILURE;
    }
    if (!scene_builder.pop_layer()) {
        std::cerr << "Could not close the native retained layer.\n";
        return EXIT_FAILURE;
    }
    progpu_native_hit_test_primitive hit_primitive{};
    hit_primitive.bounds_min = {48.0F, 48.0F};
    hit_primitive.bounds_max = {228.0F, 168.0F};
    hit_primitive.data0 = {48.0F, 48.0F, 228.0F, 168.0F};
    hit_primitive.inverse_transform0 = {1.0F, 0.0F, 0.0F, 0.0F};
    hit_primitive.inverse_transform1 = {0.0F, 1.0F, 0.0F, 0.0F};
    hit_primitive.kind = PROGPU_NATIVE_HIT_TEST_RECTANGLE_FILL;
    hit_primitive.flags = PROGPU_NATIVE_HIT_TEST_VISIBLE |
        PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT;
    hit_primitive.id = 501;
    hit_primitive.z_index = 1.0F;
    const progpu_native_hit_test_node hit_node{
        {48.0F, 48.0F},
        {228.0F, 168.0F},
        0U,
        0U,
        0U,
        1U};
    constexpr std::uint32_t hit_primitive_index = 0U;
    std::uint32_t hit_test_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!scene_builder.add_hit_test_index(
            std::span<const progpu_native_hit_test_primitive>(
                &hit_primitive,
                1U),
            std::span<const progpu_native_hit_test_node>(&hit_node, 1U),
            std::span<const std::uint32_t>(&hit_primitive_index, 1U),
            {},
            hit_test_resource)) {
        std::cerr << "Could not record the native retained hit-test index.\n";
        return EXIT_FAILURE;
    }
    std::vector<std::byte> scene_stream;
    progpu::native::scene_build_metrics build_metrics{};
    const std::size_t scene_stream_size =
        scene_builder.required_stream_size();
    scene_stream.resize(scene_stream_size);
    std::size_t scene_stream_bytes = 0U;
    if (scene_stream_size == 0U ||
        !scene_builder.build_into(
            scene_stream,
            scene_stream_bytes,
            &build_metrics) ||
        scene_stream_bytes != scene_stream.size()) {
        std::cerr << "Could not compile the native retained scene.\n";
        return EXIT_FAILURE;
    }
    progpu_native_scene_metrics update_metrics{};
    update_metrics.struct_size = sizeof(update_metrics);
    if (progpu_native_engine_update_scene(
            engine,
            scene_stream.data(),
            scene_stream.size(),
            &update_metrics) != PROGPU_NATIVE_STATUS_SUCCESS) {
        std::cerr << "Could not install the native retained scene.\n";
        return EXIT_FAILURE;
    }
    if (!defer_software_d3d12_hit_test) {
        std::cout << "[ProGPUNative] executing retained GPU hit test.\n";
    }
    if (!defer_software_d3d12_hit_test &&
        !verify_retained_gpu_hit_test(engine, device)) {
        std::array<char, 512U> error{};
        progpu_native_engine_get_last_error(
            engine,
            error.data(),
            error.size());
        std::cerr << "Native retained GPU hit testing failed: "
                  << error.data() << '\n';
        return EXIT_FAILURE;
    }
    if (defer_software_d3d12_hit_test) {
        std::cout
            << "[ProGPUNative] retained GPU hit test deferred on "
            << adapter_name
            << "; the full D3D12 renderer sample remains required.\n";
    }

    progpu_native_scene_frame frame{};
    frame.struct_size = sizeof(frame);
    frame.scene_id = scene_builder.scene_id();
    frame.generation = scene_builder.generation();
    frame.width = width;
    frame.height = height;
    frame.dpi_scale = 1.0F;
    frame.target_view = reinterpret_cast<std::uintptr_t>(target_view);
    frame.clear_color = {0.02F, 0.025F, 0.04F, 1.0F};
    progpu_native_scene_frame_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    std::cout << "[ProGPUNative] rendering retained scene.\n";
    const progpu_native_status render_status =
        progpu_native_engine_render_scene(engine, &frame, &metrics);
    if (render_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        std::array<char, 512U> error{};
        progpu_native_engine_get_last_error(
            engine,
            error.data(),
            error.size());
        std::cerr << "Native render failed: " << error.data() << '\n';
        return EXIT_FAILURE;
    }
    std::cout << "[ProGPUNative] retained scene rendered; reading pixels.\n";

    const std::uint32_t row_bytes = align_row_bytes(width * 4U);
    const std::uint64_t readback_size =
        static_cast<std::uint64_t>(row_bytes) * height;
    WGPUBufferDescriptor readback_descriptor{};
    readback_descriptor.label = "ProGPU native sample readback";
    readback_descriptor.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
    readback_descriptor.size = readback_size;
    WGPUBuffer readback = wgpuDeviceCreateBuffer(device, &readback_descriptor);

    WGPUCommandEncoder copy_encoder = wgpuDeviceCreateCommandEncoder(device, nullptr);
    WGPUImageCopyTexture source{};
    source.texture = target;
    source.aspect = WGPUTextureAspect_All;
    WGPUImageCopyBuffer destination{};
    destination.buffer = readback;
    destination.layout.bytesPerRow = row_bytes;
    destination.layout.rowsPerImage = height;
    const WGPUExtent3D extent{width, height, 1U};
    wgpuCommandEncoderCopyTextureToBuffer(
        copy_encoder,
        &source,
        &destination,
        &extent);
    WGPUCommandBuffer copy_commands = wgpuCommandEncoderFinish(copy_encoder, nullptr);
    wgpuQueueSubmit(queue, 1U, &copy_commands);
    wgpuCommandBufferRelease(copy_commands);
    wgpuCommandEncoderRelease(copy_encoder);

    map_request mapped{};
    wgpuBufferMapAsync(
        readback,
        WGPUMapMode_Read,
        0U,
        readback_size,
        on_buffer_mapped,
        &mapped);
    const bool map_completed = wait_for_gpu_callback(
        device,
        mapped.complete,
        "Native sample readback");
    const auto* pixels = map_completed &&
            mapped.status == WGPUBufferMapAsyncStatus_Success
        ? static_cast<const std::uint8_t*>(
            wgpuBufferGetConstMappedRange(readback, 0U, readback_size))
        : nullptr;
    bool passed =
        map_completed &&
        mapped.status == WGPUBufferMapAsyncStatus_Success &&
        pixels != nullptr &&
        has_expected_colors(pixels, row_bytes, uses_decoded_glyph) &&
        write_ppm(output_path, pixels, width, height, row_bytes) &&
        (evidence_path.empty() || write_evidence(
            evidence_path,
            adapter_properties,
            metrics,
            hit_test_status,
            output_path));

    if (pixels != nullptr) {
        wgpuBufferUnmap(readback);
    }
    wgpuBufferDestroy(readback);
    wgpuBufferRelease(readback);
    progpu_native_hit_test_query abandoned_query{};
    abandoned_query.point = {80.0F, 80.0F};
    abandoned_query.region_max = abandoned_query.point;
    std::uint64_t abandoned_token = 0U;
    passed = passed && (defer_software_d3d12_hit_test ||
        (progpu_native_engine_begin_hit_test(
            engine,
            &abandoned_query,
            &abandoned_token) == PROGPU_NATIVE_STATUS_SUCCESS &&
            abandoned_token != 0U));
    progpu_native_engine_destroy(engine);
    wgpuTextureViewRelease(target_view);
    wgpuTextureDestroy(target);
    wgpuTextureRelease(target);
    wgpuQueueRelease(queue);
    wgpuDeviceRelease(device);
    wgpuAdapterRelease(adapter);
    wgpuInstanceRelease(instance);

    if (!passed) {
        std::cerr << "Native sample image verification failed.\n";
        return EXIT_FAILURE;
    }

    std::cout << "[ProGPUNative] "
              << backend_name(adapter_properties.backendType)
              << " adapter '"
              << adapter_name
              << "' uploaded " << metrics.vertex_upload_bytes
              << " vertex bytes in " << metrics.draw_call_count
              << " draw call from " << build_metrics.command_count
              << " native retained command; retained GPU hit test "
              << hit_test_status << "; wrote "
              << output_path << '\n';
    return EXIT_SUCCESS;
}
