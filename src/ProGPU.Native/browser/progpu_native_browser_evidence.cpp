#include "progpu_native_browser_evidence.hpp"

#include <emscripten.h>

#include <cstddef>
#include <cstdint>

namespace progpu::native::browser {
namespace {

struct evidence_state final {
    WGPUBuffer buffer = nullptr;
    std::uint64_t size = 0U;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::uint32_t row_bytes = 0U;
    evidence_completion completion = nullptr;
};

evidence_state state{};

void release_readback() {
    if (state.buffer != nullptr) {
        wgpuBufferDestroy(state.buffer);
        wgpuBufferRelease(state.buffer);
    }
    state = {};
}

void on_readback_mapped(
    WGPUMapAsyncStatus status,
    WGPUStringView,
    void* userdata,
    void*) {
    auto& mapped = *static_cast<evidence_state*>(userdata);
    const auto* pixels = status == WGPUMapAsyncStatus_Success
        ? static_cast<const std::uint8_t*>(wgpuBufferGetConstMappedRange(
            mapped.buffer,
            0U,
            mapped.size))
        : nullptr;
    bool success = pixels != nullptr;
    if (success) {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdollar-in-identifier-extension"
        EM_ASM({
            const source = HEAPU8.subarray($0, $0 + $4);
            const width = $1;
            const height = $2;
            const rowBytes = $3;
            const rgba = new Uint8ClampedArray(width * height * 4);
            for (let y = 0; y < height; y++) {
                const sourceRow = y * rowBytes;
                const targetRow = y * width * 4;
                for (let x = 0; x < width; x++) {
                    const sourcePixel = sourceRow + x * 4;
                    const targetPixel = targetRow + x * 4;
                    rgba[targetPixel] = source[sourcePixel + 2];
                    rgba[targetPixel + 1] = source[sourcePixel + 1];
                    rgba[targetPixel + 2] = source[sourcePixel];
                    rgba[targetPixel + 3] = source[sourcePixel + 3];
                }
            }
            const canvas = document.getElementById("progpu-native-evidence");
            const context = canvas.getContext("2d", {
                alpha: false,
                willReadFrequently: true
            });
            canvas.width = width;
            canvas.height = height;
            context.putImageData(new ImageData(rgba, width, height), 0, 0);
            document.getElementById("progpu-native-canvas").style.display =
                "none";
            canvas.style.display = "block";
            document.body.dataset.progpuNativeEvidence = "ready";
        }, pixels, mapped.width, mapped.height, mapped.row_bytes,
            mapped.row_bytes * mapped.height);
#pragma clang diagnostic pop
        wgpuBufferUnmap(mapped.buffer);
    }
    const evidence_completion completion = mapped.completion;
    release_readback();
    if (completion != nullptr) {
        completion(success);
    }
}

} // namespace

bool create_evidence_target(
    WGPUDevice device,
    WGPUTextureFormat format,
    std::uint32_t width,
    std::uint32_t height,
    WGPUTexture* texture,
    WGPUTextureView* view) {
    if (device == nullptr || format == WGPUTextureFormat_Undefined ||
        width == 0U || height == 0U || texture == nullptr || view == nullptr) {
        return false;
    }
    *texture = nullptr;
    *view = nullptr;
    WGPUTextureDescriptor descriptor = WGPU_TEXTURE_DESCRIPTOR_INIT;
    descriptor.usage =
        WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = format;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    descriptor.viewFormatCount = 0U;
    WGPUTexture created_texture = wgpuDeviceCreateTexture(device, &descriptor);
    if (created_texture == nullptr) {
        return false;
    }
    WGPUTextureView created_view = wgpuTextureCreateView(
        created_texture,
        nullptr);
    if (created_view == nullptr) {
        wgpuTextureDestroy(created_texture);
        wgpuTextureRelease(created_texture);
        return false;
    }
    *texture = created_texture;
    *view = created_view;
    return true;
}

bool begin_evidence_readback(
    WGPUDevice device,
    WGPUQueue queue,
    WGPUTexture source_texture,
    std::uint32_t width,
    std::uint32_t height,
    evidence_completion completion) {
    if (device == nullptr || queue == nullptr || source_texture == nullptr ||
        width == 0U || height == 0U || completion == nullptr ||
        state.buffer != nullptr) {
        return false;
    }
    const std::uint32_t row_bytes = (width * 4U + 255U) & ~255U;
    const std::uint64_t readback_size =
        static_cast<std::uint64_t>(row_bytes) * height;
    WGPUBufferDescriptor buffer_descriptor = WGPU_BUFFER_DESCRIPTOR_INIT;
    buffer_descriptor.usage =
        WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
    buffer_descriptor.size = readback_size;
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(device, &buffer_descriptor);
    if (buffer == nullptr) {
        return false;
    }

    WGPUCommandEncoderDescriptor encoder_descriptor =
        WGPU_COMMAND_ENCODER_DESCRIPTOR_INIT;
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        wgpuBufferDestroy(buffer);
        wgpuBufferRelease(buffer);
        return false;
    }
    WGPUTexelCopyTextureInfo source = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    source.texture = source_texture;
    source.aspect = WGPUTextureAspect_All;
    WGPUTexelCopyBufferInfo destination = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
    destination.buffer = buffer;
    destination.layout.bytesPerRow = row_bytes;
    destination.layout.rowsPerImage = height;
    const WGPUExtent3D extent{width, height, 1U};
    wgpuCommandEncoderCopyTextureToBuffer(
        encoder,
        &source,
        &destination,
        &extent);
    WGPUCommandBufferDescriptor command_descriptor =
        WGPU_COMMAND_BUFFER_DESCRIPTOR_INIT;
    WGPUCommandBuffer commands = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (commands == nullptr) {
        wgpuBufferDestroy(buffer);
        wgpuBufferRelease(buffer);
        return false;
    }
    wgpuQueueSubmit(queue, 1U, &commands);
    wgpuCommandBufferRelease(commands);

    state = {buffer, readback_size, width, height, row_bytes, completion};
    WGPUBufferMapCallbackInfo callback = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowSpontaneous;
    callback.callback = on_readback_mapped;
    callback.userdata1 = &state;
    wgpuBufferMapAsync(
        buffer,
        WGPUMapMode_Read,
        0U,
        readback_size,
        callback);
    return true;
}

} // namespace progpu::native::browser
