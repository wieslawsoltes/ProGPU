#pragma once

#include <webgpu.h>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdlib>

namespace progpu::native::webgpu {

struct buffer_map_read_state final {
    std::atomic<std::uint8_t> completion{0U};
};

inline constexpr std::uint8_t buffer_map_pending = 0U;
inline constexpr std::uint8_t buffer_map_succeeded = 1U;
inline constexpr std::uint8_t buffer_map_failed = 2U;

inline void initialize_color_attachment(
    WGPURenderPassColorAttachment& attachment) noexcept {
    // Both supported headers expose depthSlice, but only the modern Dawn ABI
    // validates its sentinel for a non-3D target. Zero names depth slice zero
    // and is therefore invalid for ordinary 2D render attachments.
    attachment.depthSlice = WGPU_DEPTH_SLICE_UNDEFINED;
}

#if defined(PROGPU_NATIVE_DAWN_ABI)

using proc_resolver = void* (*)(void* context, const char* name);

#define PROGPU_NATIVE_DAWN_PROC_LIST(X) \
    X(BindGroupLayoutRelease) \
    X(BindGroupRelease) \
    X(BufferDestroy) \
    X(BufferGetConstMappedRange) \
    X(BufferGetMapState) \
    X(BufferMapAsync) \
    X(BufferRelease) \
    X(BufferUnmap) \
    X(CommandBufferRelease) \
    X(CommandEncoderBeginComputePass) \
    X(CommandEncoderBeginRenderPass) \
    X(CommandEncoderCopyBufferToBuffer) \
    X(CommandEncoderCopyBufferToTexture) \
    X(CommandEncoderCopyTextureToTexture) \
    X(CommandEncoderFinish) \
    X(CommandEncoderRelease) \
    X(ComputePassEncoderDispatchWorkgroups) \
    X(ComputePassEncoderEnd) \
    X(ComputePassEncoderRelease) \
    X(ComputePassEncoderSetBindGroup) \
    X(ComputePassEncoderSetPipeline) \
    X(ComputePipelineRelease) \
    X(DeviceAddRef) \
    X(DeviceCreateBindGroup) \
    X(DeviceCreateBindGroupLayout) \
    X(DeviceCreateBuffer) \
    X(DeviceCreateCommandEncoder) \
    X(DeviceCreateComputePipeline) \
    X(DeviceCreatePipelineLayout) \
    X(DeviceCreateRenderBundleEncoder) \
    X(DeviceCreateRenderPipeline) \
    X(DeviceCreateSampler) \
    X(DeviceCreateShaderModule) \
    X(DeviceCreateTexture) \
    X(DeviceRelease) \
    X(InstanceAddRef) \
    X(InstanceRelease) \
    X(InstanceWaitAny) \
    X(PipelineLayoutRelease) \
    X(QueueAddRef) \
    X(QueueOnSubmittedWorkDone) \
    X(QueueRelease) \
    X(QueueSubmit) \
    X(QueueWriteBuffer) \
    X(QueueWriteTexture) \
    X(RenderPassEncoderDraw) \
    X(RenderPassEncoderDrawIndexed) \
    X(RenderPassEncoderEnd) \
    X(RenderPassEncoderExecuteBundles) \
    X(RenderPassEncoderRelease) \
    X(RenderPassEncoderSetBindGroup) \
    X(RenderPassEncoderSetIndexBuffer) \
    X(RenderPassEncoderSetPipeline) \
    X(RenderPassEncoderSetScissorRect) \
    X(RenderPassEncoderSetVertexBuffer) \
    X(RenderBundleEncoderDraw) \
    X(RenderBundleEncoderDrawIndexed) \
    X(RenderBundleEncoderFinish) \
    X(RenderBundleEncoderRelease) \
    X(RenderBundleEncoderSetBindGroup) \
    X(RenderBundleEncoderSetIndexBuffer) \
    X(RenderBundleEncoderSetPipeline) \
    X(RenderBundleEncoderSetVertexBuffer) \
    X(RenderBundleRelease) \
    X(RenderPipelineGetBindGroupLayout) \
    X(RenderPipelineRelease) \
    X(SamplerRelease) \
    X(ShaderModuleRelease) \
    X(TextureCreateView) \
    X(TextureDestroy) \
    X(TextureRelease) \
    X(TextureViewAddRef) \
    X(TextureViewRelease)

struct dispatch final {
#define PROGPU_NATIVE_DECLARE_DAWN_PROC(Name) \
    WGPUProc##Name wgpu##Name = nullptr;
    PROGPU_NATIVE_DAWN_PROC_LIST(PROGPU_NATIVE_DECLARE_DAWN_PROC)
#undef PROGPU_NATIVE_DECLARE_DAWN_PROC

    bool load(void* context, proc_resolver resolver) noexcept {
#if defined(PROGPU_NATIVE_BROWSER)
        (void)context;
        (void)resolver;
#define PROGPU_NATIVE_LOAD_BROWSER_PROC(Name) \
        wgpu##Name = &::wgpu##Name;
        PROGPU_NATIVE_DAWN_PROC_LIST(PROGPU_NATIVE_LOAD_BROWSER_PROC)
#undef PROGPU_NATIVE_LOAD_BROWSER_PROC
        return true;
#else
        if (resolver == nullptr) {
            return false;
        }
        bool complete = true;
#define PROGPU_NATIVE_LOAD_DAWN_PROC(Name) \
        wgpu##Name = reinterpret_cast<WGPUProc##Name>( \
            resolver(context, "wgpu" #Name)); \
        complete = complete && wgpu##Name != nullptr;
        PROGPU_NATIVE_DAWN_PROC_LIST(PROGPU_NATIVE_LOAD_DAWN_PROC)
#undef PROGPU_NATIVE_LOAD_DAWN_PROC
        return complete;
#endif
    }
};

inline thread_local const dispatch* current_dispatch = nullptr;

class dispatch_scope final {
public:
    explicit dispatch_scope(const dispatch* value) noexcept
        : previous_(current_dispatch) {
        current_dispatch = value;
    }

    ~dispatch_scope() {
        current_dispatch = previous_;
    }

    dispatch_scope(const dispatch_scope&) = delete;
    dispatch_scope& operator=(const dispatch_scope&) = delete;

private:
    const dispatch* previous_;
};

inline const dispatch& active_dispatch() noexcept {
    if (current_dispatch == nullptr) {
        std::abort();
    }
    return *current_dispatch;
}

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

inline void instance_add_ref(WGPUInstance instance) noexcept {
    active_dispatch().wgpuInstanceAddRef(instance);
}

inline void instance_release(WGPUInstance instance) noexcept {
    active_dispatch().wgpuInstanceRelease(instance);
}

inline void device_add_ref(WGPUDevice device) noexcept {
    active_dispatch().wgpuDeviceAddRef(device);
}

inline void queue_add_ref(WGPUQueue queue) noexcept {
    active_dispatch().wgpuQueueAddRef(queue);
}

inline void texture_view_add_ref(WGPUTextureView view) noexcept {
    active_dispatch().wgpuTextureViewAddRef(view);
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
#if defined(PROGPU_NATIVE_BROWSER)
    static thread_local std::uint64_t submission_count = 0U;
    active_dispatch().wgpuQueueSubmit(queue, command_count, commands);
    return ++submission_count;
#else
    active_dispatch().wgpuQueueSubmit(queue, command_count, commands);
    WGPUQueueWorkDoneCallbackInfo callback{};
    callback.mode = WGPUCallbackMode_WaitAnyOnly;
    callback.callback = queue_work_done;
    return active_dispatch()
        .wgpuQueueOnSubmittedWorkDone(queue, callback)
        .id;
#endif
}

inline bool poll_submission(
    WGPUInstance instance,
    WGPUDevice,
    WGPUQueue,
    std::uint64_t submission_index,
    bool wait) noexcept {
#if defined(PROGPU_NATIVE_BROWSER)
    (void)instance;
    (void)submission_index;
    (void)wait;
    return false;
#else
    WGPUFutureWaitInfo future{};
    future.future.id = submission_index;
    const WGPUWaitStatus status = active_dispatch().wgpuInstanceWaitAny(
        instance,
        1U,
        &future,
        wait ? UINT64_MAX : 0U);
    return status == WGPUWaitStatus_Success && future.completed != 0U;
#endif
}

inline WGPUBufferMapState poll_buffer_map(
    WGPUDevice,
    WGPUBuffer buffer,
    const buffer_map_read_state&) noexcept {
    return active_dispatch().wgpuBufferGetMapState(buffer);
}

inline const void* buffer_get_const_mapped_range(
    WGPUBuffer buffer,
    std::uint64_t size) noexcept {
    return active_dispatch().wgpuBufferGetConstMappedRange(
        buffer,
        0U,
        size);
}

inline void buffer_unmap(WGPUBuffer buffer) noexcept {
    active_dispatch().wgpuBufferUnmap(buffer);
}

#else

struct dispatch final {
};

class dispatch_scope final {
public:
    explicit dispatch_scope(const dispatch*) noexcept {
    }
};

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

inline void instance_add_ref(WGPUInstance instance) noexcept {
    wgpuInstanceReference(instance);
}

inline void instance_release(WGPUInstance instance) noexcept {
    wgpuInstanceRelease(instance);
}

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

inline WGPUBufferMapState poll_buffer_map(
    WGPUDevice device,
    WGPUBuffer,
    const buffer_map_read_state& state) noexcept {
    (void)wgpuDevicePoll(device, false, nullptr);
    const auto completion = state.completion.load(std::memory_order_acquire);
    if (completion == buffer_map_pending) {
        return WGPUBufferMapState_Pending;
    }
    return completion == buffer_map_succeeded
        ? WGPUBufferMapState_Mapped
        : WGPUBufferMapState_Unmapped;
}

inline const void* buffer_get_const_mapped_range(
    WGPUBuffer buffer,
    std::uint64_t size) noexcept {
    return wgpuBufferGetConstMappedRange(buffer, 0U, size);
}

inline void buffer_unmap(WGPUBuffer buffer) noexcept {
    wgpuBufferUnmap(buffer);
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

#if defined(PROGPU_NATIVE_DAWN_ABI)
#define wgpuBindGroupLayoutRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuBindGroupLayoutRelease)
#define wgpuBindGroupRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuBindGroupRelease)
#define wgpuBufferDestroy \
    (::progpu::native::webgpu::active_dispatch().wgpuBufferDestroy)
#define wgpuBufferGetConstMappedRange \
    (::progpu::native::webgpu::active_dispatch().wgpuBufferGetConstMappedRange)
#define wgpuBufferGetMapState \
    (::progpu::native::webgpu::active_dispatch().wgpuBufferGetMapState)
#define wgpuBufferMapAsync \
    (::progpu::native::webgpu::active_dispatch().wgpuBufferMapAsync)
#define wgpuBufferRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuBufferRelease)
#define wgpuBufferUnmap \
    (::progpu::native::webgpu::active_dispatch().wgpuBufferUnmap)
#define wgpuCommandBufferRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandBufferRelease)
#define wgpuCommandEncoderBeginComputePass \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandEncoderBeginComputePass)
#define wgpuCommandEncoderBeginRenderPass \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandEncoderBeginRenderPass)
#define wgpuCommandEncoderCopyBufferToBuffer \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandEncoderCopyBufferToBuffer)
#define wgpuCommandEncoderCopyBufferToTexture \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandEncoderCopyBufferToTexture)
#define wgpuCommandEncoderCopyTextureToTexture \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandEncoderCopyTextureToTexture)
#define wgpuCommandEncoderFinish \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandEncoderFinish)
#define wgpuCommandEncoderRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuCommandEncoderRelease)
#define wgpuComputePassEncoderDispatchWorkgroups \
    (::progpu::native::webgpu::active_dispatch().wgpuComputePassEncoderDispatchWorkgroups)
#define wgpuComputePassEncoderEnd \
    (::progpu::native::webgpu::active_dispatch().wgpuComputePassEncoderEnd)
#define wgpuComputePassEncoderRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuComputePassEncoderRelease)
#define wgpuComputePassEncoderSetBindGroup \
    (::progpu::native::webgpu::active_dispatch().wgpuComputePassEncoderSetBindGroup)
#define wgpuComputePassEncoderSetPipeline \
    (::progpu::native::webgpu::active_dispatch().wgpuComputePassEncoderSetPipeline)
#define wgpuComputePipelineRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuComputePipelineRelease)
#define wgpuDeviceCreateBindGroup \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateBindGroup)
#define wgpuDeviceCreateBindGroupLayout \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateBindGroupLayout)
#define wgpuDeviceCreateBuffer \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateBuffer)
#define wgpuDeviceCreateCommandEncoder \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateCommandEncoder)
#define wgpuDeviceCreateComputePipeline \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateComputePipeline)
#define wgpuDeviceCreatePipelineLayout \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreatePipelineLayout)
#define wgpuDeviceCreateRenderBundleEncoder \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateRenderBundleEncoder)
#define wgpuDeviceCreateRenderPipeline \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateRenderPipeline)
#define wgpuDeviceCreateSampler \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateSampler)
#define wgpuDeviceCreateShaderModule \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateShaderModule)
#define wgpuDeviceCreateTexture \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceCreateTexture)
#define wgpuDeviceRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuDeviceRelease)
#define wgpuPipelineLayoutRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuPipelineLayoutRelease)
#define wgpuQueueRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuQueueRelease)
#define wgpuQueueWriteBuffer \
    (::progpu::native::webgpu::active_dispatch().wgpuQueueWriteBuffer)
#define wgpuQueueWriteTexture \
    (::progpu::native::webgpu::active_dispatch().wgpuQueueWriteTexture)
#define wgpuRenderPassEncoderDraw \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderDraw)
#define wgpuRenderPassEncoderDrawIndexed \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderDrawIndexed)
#define wgpuRenderPassEncoderEnd \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderEnd)
#define wgpuRenderPassEncoderExecuteBundles \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderExecuteBundles)
#define wgpuRenderPassEncoderRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderRelease)
#define wgpuRenderPassEncoderSetBindGroup \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderSetBindGroup)
#define wgpuRenderPassEncoderSetIndexBuffer \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderSetIndexBuffer)
#define wgpuRenderPassEncoderSetPipeline \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderSetPipeline)
#define wgpuRenderPassEncoderSetScissorRect \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderSetScissorRect)
#define wgpuRenderPassEncoderSetVertexBuffer \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPassEncoderSetVertexBuffer)
#define wgpuRenderBundleEncoderDraw \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderDraw)
#define wgpuRenderBundleEncoderDrawIndexed \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderDrawIndexed)
#define wgpuRenderBundleEncoderFinish \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderFinish)
#define wgpuRenderBundleEncoderRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderRelease)
#define wgpuRenderBundleEncoderSetBindGroup \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderSetBindGroup)
#define wgpuRenderBundleEncoderSetIndexBuffer \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderSetIndexBuffer)
#define wgpuRenderBundleEncoderSetPipeline \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderSetPipeline)
#define wgpuRenderBundleEncoderSetVertexBuffer \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleEncoderSetVertexBuffer)
#define wgpuRenderBundleRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderBundleRelease)
#define wgpuRenderPipelineGetBindGroupLayout \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPipelineGetBindGroupLayout)
#define wgpuRenderPipelineRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuRenderPipelineRelease)
#define wgpuSamplerRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuSamplerRelease)
#define wgpuShaderModuleRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuShaderModuleRelease)
#define wgpuTextureCreateView \
    (::progpu::native::webgpu::active_dispatch().wgpuTextureCreateView)
#define wgpuTextureDestroy \
    (::progpu::native::webgpu::active_dispatch().wgpuTextureDestroy)
#define wgpuTextureRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuTextureRelease)
#define wgpuTextureViewRelease \
    (::progpu::native::webgpu::active_dispatch().wgpuTextureViewRelease)
#endif
