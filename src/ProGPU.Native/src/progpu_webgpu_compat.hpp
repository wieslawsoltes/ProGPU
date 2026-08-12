#pragma once

#include <webgpu.h>

#include <cstddef>
#include <cstdint>

namespace progpu::native::webgpu {

#if defined(PROGPU_NATIVE_DAWN_ABI)

template<std::size_t Size>
constexpr WGPUStringView string_view(
    const char (&value)[Size]) noexcept {
    return WGPUStringView{value, Size - 1U};
}

inline WGPUStringView string_view(const char* value) noexcept {
    return WGPUStringView{value, WGPU_STRLEN};
}

inline WGPUStringView string_view(
    const std::uint8_t* value,
    std::size_t size) noexcept {
    return WGPUStringView{
        reinterpret_cast<const char*>(value),
        size
    };
}

struct wgsl_source final {
    WGPUShaderSourceWGSL value{};

    wgsl_source(
        const std::uint8_t* code,
        std::size_t size) noexcept {
        value.chain.sType = WGPUSType_ShaderSourceWGSL;
        value.code = string_view(code, size);
    }

    WGPUChainedStruct* chain() noexcept {
        return &value.chain;
    }
};

using image_copy_texture = WGPUTexelCopyTextureInfo;
using texture_data_layout = WGPUTexelCopyBufferLayout;
using image_copy_buffer = WGPUTexelCopyBufferInfo;
using buffer_usage_flags = WGPUBufferUsage;

inline void device_add_ref(WGPUDevice device) noexcept {
    wgpuDeviceAddRef(device);
}

inline void queue_add_ref(WGPUQueue queue) noexcept {
    wgpuQueueAddRef(queue);
}

inline void texture_view_add_ref(WGPUTextureView view) noexcept {
    wgpuTextureViewAddRef(view);
}

inline void queue_work_done(
    WGPUQueueWorkDoneStatus,
    WGPUStringView,
    void*,
    void*) noexcept {
}

inline std::uint64_t submit(
    WGPUQueue queue,
    std::size_t command_count,
    WGPUCommandBuffer const* commands) noexcept {
    wgpuQueueSubmit(queue, command_count, commands);
    WGPUQueueWorkDoneCallbackInfo callback{};
    callback.mode = WGPUCallbackMode_WaitAnyOnly;
    callback.callback = queue_work_done;
    return wgpuQueueOnSubmittedWorkDone(queue, callback).id;
}

inline bool poll_submission(
    WGPUInstance instance,
    WGPUDevice,
    WGPUQueue,
    std::uint64_t submission_index,
    bool wait) noexcept {
    WGPUFutureWaitInfo future{};
    future.future.id = submission_index;
    const WGPUWaitStatus status = wgpuInstanceWaitAny(
        instance,
        1U,
        &future,
        wait ? UINT64_MAX : 0U);
    return status == WGPUWaitStatus_Success && future.completed != 0U;
}

#else

template<std::size_t Size>
constexpr const char* string_view(
    const char (&value)[Size]) noexcept {
    return value;
}

inline const char* string_view(const char* value) noexcept {
    return value;
}

inline const char* string_view(
    const std::uint8_t* value,
    std::size_t) noexcept {
    return reinterpret_cast<const char*>(value);
}

struct wgsl_source final {
    WGPUShaderModuleWGSLDescriptor value{};

    wgsl_source(
        const std::uint8_t* code,
        std::size_t size) noexcept {
        value.chain.sType = WGPUSType_ShaderModuleWGSLDescriptor;
        value.code = string_view(code, size);
    }

    WGPUChainedStruct* chain() noexcept {
        return &value.chain;
    }
};

using image_copy_texture = WGPUImageCopyTexture;
using texture_data_layout = WGPUTextureDataLayout;
using image_copy_buffer = WGPUImageCopyBuffer;
using buffer_usage_flags = WGPUBufferUsageFlags;

inline void device_add_ref(WGPUDevice device) noexcept {
    wgpuDeviceReference(device);
}

inline void queue_add_ref(WGPUQueue queue) noexcept {
    wgpuQueueReference(queue);
}

inline void texture_view_add_ref(WGPUTextureView view) noexcept {
    wgpuTextureViewReference(view);
}

inline std::uint64_t submit(
    WGPUQueue queue,
    std::size_t command_count,
    WGPUCommandBuffer const* commands) noexcept {
    return wgpuQueueSubmitForIndex(queue, command_count, commands);
}

inline bool poll_submission(
    WGPUInstance,
    WGPUDevice device,
    WGPUQueue queue,
    std::uint64_t submission_index,
    bool wait) noexcept {
    const WGPUWrappedSubmissionIndex wrapped{
        queue,
        submission_index
    };
    return wgpuDevicePoll(device, wait, &wrapped) != 0U;
}

#endif

inline WGPUVertexAttribute vertex_attribute(
    WGPUVertexFormat format,
    std::uint64_t offset,
    std::uint32_t shader_location) noexcept {
    WGPUVertexAttribute result{};
    result.format = format;
    result.offset = offset;
    result.shaderLocation = shader_location;
    return result;
}

} // namespace progpu::native::webgpu
