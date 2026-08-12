#include "progpu_native.h"

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
        std::cerr << "No hardware WebGPU adapter was found.\n";
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

    const std::array<progpu_native_rect, 3U> rectangles{{
        {48.0F, 48.0F, 180.0F, 120.0F, {0.08F, 0.42F, 0.95F, 1.0F}},
        {280.0F, 64.0F, 280.0F, 132.0F, {0.98F, 0.52F, 0.08F, 1.0F}},
        {128.0F, 224.0F, 384.0F, 72.0F, {0.20F, 0.82F, 0.48F, 0.90F}}
    }};
    progpu_native_frame frame{};
    frame.struct_size = sizeof(frame);
    frame.width = width;
    frame.height = height;
    frame.dpi_scale = 1.0F;
    frame.target_view = reinterpret_cast<std::uintptr_t>(target_view);
    frame.clear_color = {0.02F, 0.025F, 0.04F, 1.0F};
    frame.rects = rectangles.data();
    frame.rect_count = rectangles.size();
    progpu_native_frame_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    const progpu_native_status render_status =
        progpu_native_engine_render(engine, &frame, &metrics);
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
        write_ppm(output_path, pixels, width, height, row_bytes);

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

    std::cout << "[ProGPUNative] rendered " << metrics.vertex_count
              << " vertices in " << metrics.draw_call_count
              << " draw call; wrote " << output_path << '\n';
    return EXIT_SUCCESS;
}
