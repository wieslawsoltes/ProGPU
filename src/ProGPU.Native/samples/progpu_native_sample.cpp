#include "progpu_native.h"
#include "progpu_native_scene_builder.hpp"

#include <wgpu.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace {

struct device_request {
    WGPUDevice device = nullptr;
    bool complete = false;
};

struct map_request {
    WGPUBufferMapAsyncStatus status = WGPUBufferMapAsyncStatus_Unknown;
    bool complete = false;
};

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
    request.complete = true;
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
    request.complete = true;
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
    if (!request.complete || request.device == nullptr) {
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
           << "capture=" << capture_path << '\n';
    return output.good();
}

bool has_expected_colors(
    const std::uint8_t* pixels,
    std::uint32_t row_bytes) {
    const auto pixel = [&](std::uint32_t x, std::uint32_t y) {
        return pixels + y * row_bytes + x * 4U;
    };
    const std::uint8_t* blue = pixel(100U, 100U);
    const std::uint8_t* amber = pixel(360U, 130U);
    const std::uint8_t* background = pixel(10U, 10U);
    return blue[2] > 180U && blue[0] < 100U &&
        amber[0] > 180U && amber[1] > 90U &&
        background[0] < 30U && background[1] < 30U;
}

} // namespace

int main(int argc, char** argv) {
    const std::string output_path = argc > 1
        ? argv[1]
        : "progpu-native-sample.ppm";
    const std::string evidence_path = argc > 2 ? argv[2] : std::string{};

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
    if (adapter_properties.backendType != platform_backend_type()) {
        std::cerr << "Expected " << backend_name(platform_backend_type())
                  << " but selected "
                  << backend_name(adapter_properties.backendType) << ".\n";
        return EXIT_FAILURE;
    }

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
    if (!scene_builder.reserve(5U, 6U, 6144U)) {
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
    std::vector<std::byte> scene_stream;
    progpu::native::scene_build_metrics build_metrics{};
    if (!scene_builder.build(scene_stream, &build_metrics)) {
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
    while (!mapped.complete) {
        wgpuDevicePoll(device, true, nullptr);
    }
    const auto* pixels = static_cast<const std::uint8_t*>(
        wgpuBufferGetConstMappedRange(readback, 0U, readback_size));
    const bool passed =
        mapped.status == WGPUBufferMapAsyncStatus_Success &&
        pixels != nullptr &&
        has_expected_colors(pixels, row_bytes) &&
        write_ppm(output_path, pixels, width, height, row_bytes) &&
        (evidence_path.empty() || write_evidence(
            evidence_path,
            adapter_properties,
            metrics,
            output_path));

    if (pixels != nullptr) {
        wgpuBufferUnmap(readback);
    }
    wgpuBufferDestroy(readback);
    wgpuBufferRelease(readback);
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
              << " native retained command; wrote " << output_path << '\n';
    return EXIT_SUCCESS;
}
