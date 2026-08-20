#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_hit_testing_execution.hpp"
#include "GpuHitTestingWgsl.generated.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace progpu::native::execution {
namespace {

constexpr std::uint64_t result_buffer_size =
    static_cast<std::uint64_t>(PROGPU_NATIVE_HIT_TEST_MAX_RESULT_COUNT + 1U) *
    sizeof(progpu_native_hit_test_result);
constexpr std::uint32_t allowed_query_flags =
    PROGPU_NATIVE_HIT_TEST_RESULT_CAPACITY_MASK |
    PROGPU_NATIVE_HIT_TEST_ELLIPSE_REGION |
    PROGPU_NATIVE_HIT_TEST_BOUNDS_REGION;

#if defined(PROGPU_NATIVE_DAWN_ABI)
void hit_test_map_complete(
    WGPUMapAsyncStatus status,
    WGPUStringView,
    void* userdata,
    void*) noexcept {
#else
void hit_test_map_complete(
    WGPUBufferMapAsyncStatus status,
    void* userdata) noexcept {
#endif
#if defined(PROGPU_NATIVE_DAWN_ABI)
    (void)status;
    (void)userdata;
#else
    auto* state = static_cast<webgpu::buffer_map_read_state*>(userdata);
    state->completion.store(
        status == WGPUBufferMapAsyncStatus_Success
            ? webgpu::buffer_map_succeeded
            : webgpu::buffer_map_failed,
        std::memory_order_release);
#endif
}

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

void release_buffer(WGPUBuffer& buffer) noexcept {
    if (buffer != nullptr) {
        wgpuBufferDestroy(buffer);
        wgpuBufferRelease(buffer);
        buffer = nullptr;
    }
}

WGPUBuffer create_storage_buffer(
    progpu_native_engine& engine,
    const char* label,
    const void* data,
    std::size_t size,
    std::size_t minimum_size) noexcept {
#if defined(PROGPU_NATIVE_DAWN_ABI)
    WGPUBufferDescriptor descriptor = WGPU_BUFFER_DESCRIPTOR_INIT;
#else
    WGPUBufferDescriptor descriptor{};
#endif
    descriptor.label = webgpu::string_view(label);
    descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    descriptor.size = std::max(size, minimum_size);
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (buffer != nullptr && size != 0U) {
        wgpuQueueWriteBuffer(engine.queue, buffer, 0U, data, size);
    }
    return buffer;
}

bool ensure_hit_test_pipeline(progpu_native_engine& engine) noexcept {
    if (engine.semantic_hit_test_pipeline != nullptr &&
        engine.semantic_hit_test_layout != nullptr &&
        engine.semantic_hit_test_query_buffer != nullptr &&
        engine.semantic_hit_test_result_buffer != nullptr &&
        engine.semantic_hit_test_readback_buffer != nullptr) {
        return true;
    }
    if (engine.semantic_hit_test_shader != nullptr ||
        engine.semantic_hit_test_pipeline != nullptr ||
        engine.semantic_hit_test_layout != nullptr ||
        engine.semantic_hit_test_pipeline_layout != nullptr ||
        engine.semantic_hit_test_query_buffer != nullptr ||
        engine.semantic_hit_test_result_buffer != nullptr ||
        engine.semantic_hit_test_readback_buffer != nullptr) {
        engine.release_semantic_hit_test_resources();
    }

    webgpu::wgsl_source wgsl(
        generated::gpu_hit_testing_wgsl,
        generated::gpu_hit_testing_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = webgpu::string_view(
        "ProGPU shared GpuHitTesting.wgsl");
    engine.semantic_hit_test_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.semantic_hit_test_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 6U> entries{};
    const std::array<std::uint64_t, 6U> minimum_sizes{{
        sizeof(progpu_native_hit_test_query),
        sizeof(progpu_native_hit_test_node),
        sizeof(std::uint32_t),
        sizeof(progpu_native_hit_test_primitive),
        sizeof(progpu_native_hit_test_result),
        sizeof(progpu_native_path_segment)}};
    for (std::uint32_t index = 0U; index < entries.size(); ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Compute;
        entries[index].buffer.type = index == 4U
            ? WGPUBufferBindingType_Storage
            : WGPUBufferBindingType_ReadOnlyStorage;
        entries[index].buffer.minBindingSize = minimum_sizes[index];
    }
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test storage layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.semantic_hit_test_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.semantic_hit_test_layout == nullptr) {
        engine.release_semantic_hit_test_resources();
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts =
        &engine.semantic_hit_test_layout;
    engine.semantic_hit_test_pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (engine.semantic_hit_test_pipeline_layout == nullptr) {
        engine.release_semantic_hit_test_resources();
        return false;
    }

    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test pipeline");
    pipeline_descriptor.layout = engine.semantic_hit_test_pipeline_layout;
    pipeline_descriptor.compute.module = engine.semantic_hit_test_shader;
    pipeline_descriptor.compute.entryPoint = webgpu::string_view("cs_main");
    engine.semantic_hit_test_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.semantic_hit_test_pipeline == nullptr) {
        engine.release_semantic_hit_test_resources();
        return false;
    }

    WGPUBufferDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test query");
    descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    descriptor.size = sizeof(progpu_native_hit_test_query);
    engine.semantic_hit_test_query_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &descriptor);
    descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test results");
    descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst |
        WGPUBufferUsage_CopySrc;
    descriptor.size = result_buffer_size;
    engine.semantic_hit_test_result_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &descriptor);
    descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test asynchronous readback");
    descriptor.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
    engine.semantic_hit_test_readback_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &descriptor);
    if (engine.semantic_hit_test_query_buffer == nullptr ||
        engine.semantic_hit_test_result_buffer == nullptr ||
        engine.semantic_hit_test_readback_buffer == nullptr) {
        engine.release_semantic_hit_test_resources();
        return false;
    }
    return true;
}

bool find_hit_test_resource(
    const progpu_native_engine& engine,
    progpu_native_scene_resource& resource,
    progpu_native_scene_hit_test_index& page) noexcept {
    const auto* bytes = engine.semantic_scene_snapshot.data();
    bool found = false;
    for (std::uint32_t index = 0U;
         index < engine.semantic_scene_header.resource_count;
         ++index) {
        const std::size_t offset = engine.semantic_scene_header.resource_offset +
            static_cast<std::size_t>(index) *
                engine.semantic_scene_header.resource_stride;
        const auto candidate =
            read_record<progpu_native_scene_resource>(bytes, offset);
        if (candidate.kind != PROGPU_NATIVE_SCENE_RESOURCE_HIT_TEST_INDEX) {
            continue;
        }
        if (found) {
            return false;
        }
        found = true;
        resource = candidate;
        page = read_record<progpu_native_scene_hit_test_index>(
            bytes,
            candidate.payload_offset);
    }
    return found;
}

bool ensure_hit_test_index(progpu_native_engine& engine) noexcept {
    if (!ensure_hit_test_pipeline(engine)) {
        return false;
    }
    if (engine.semantic_hit_test_bind_group != nullptr &&
        engine.semantic_hit_test_gpu_hash == engine.semantic_hashes.hit_test) {
        return true;
    }

    progpu_native_scene_resource resource{};
    progpu_native_scene_hit_test_index page{};
    if (!find_hit_test_resource(engine, resource, page)) {
        engine.release_semantic_hit_test_index();
        return false;
    }
    const auto* auxiliary = engine.semantic_scene_snapshot.data() +
        resource.auxiliary_offset;
    const auto* primitives = auxiliary + page.primitive_offset;
    const auto* nodes = auxiliary + page.node_offset;
    const auto* primitive_indices = auxiliary + page.primitive_index_offset;
    const auto* path_segments = auxiliary + page.path_segment_offset;
    const std::size_t primitive_bytes =
        static_cast<std::size_t>(page.primitive_count) *
        sizeof(progpu_native_hit_test_primitive);
    const std::size_t node_bytes =
        static_cast<std::size_t>(page.node_count) *
        sizeof(progpu_native_hit_test_node);
    const std::size_t primitive_index_bytes =
        static_cast<std::size_t>(page.primitive_index_count) *
        sizeof(std::uint32_t);
    const std::size_t path_segment_bytes =
        static_cast<std::size_t>(page.path_segment_count) *
        sizeof(progpu_native_path_segment);

    WGPUBuffer next_nodes = create_storage_buffer(
        engine,
        "ProGPU retained GPU hit-test nodes",
        nodes,
        node_bytes,
        sizeof(progpu_native_hit_test_node));
    WGPUBuffer next_indices = create_storage_buffer(
        engine,
        "ProGPU retained GPU hit-test primitive indexes",
        primitive_indices,
        primitive_index_bytes,
        sizeof(std::uint32_t));
    WGPUBuffer next_primitives = create_storage_buffer(
        engine,
        "ProGPU retained GPU hit-test primitives",
        primitives,
        primitive_bytes,
        sizeof(progpu_native_hit_test_primitive));
    WGPUBuffer next_path_segments = create_storage_buffer(
        engine,
        "ProGPU retained GPU hit-test path segments",
        path_segments,
        path_segment_bytes,
        sizeof(progpu_native_path_segment));
    if (next_nodes == nullptr || next_indices == nullptr ||
        next_primitives == nullptr || next_path_segments == nullptr) {
        release_buffer(next_nodes);
        release_buffer(next_indices);
        release_buffer(next_primitives);
        release_buffer(next_path_segments);
        return false;
    }

    const std::array<WGPUBindGroupEntry, 6U> bind_entries{{
        {nullptr, 0U, engine.semantic_hit_test_query_buffer, 0U,
            sizeof(progpu_native_hit_test_query), nullptr, nullptr},
        {nullptr, 1U, next_nodes, 0U,
            std::max(node_bytes, sizeof(progpu_native_hit_test_node)),
            nullptr, nullptr},
        {nullptr, 2U, next_indices, 0U,
            std::max(primitive_index_bytes, sizeof(std::uint32_t)),
            nullptr, nullptr},
        {nullptr, 3U, next_primitives, 0U,
            std::max(primitive_bytes,
                sizeof(progpu_native_hit_test_primitive)), nullptr, nullptr},
        {nullptr, 4U, engine.semantic_hit_test_result_buffer, 0U,
            result_buffer_size, nullptr, nullptr},
        {nullptr, 5U, next_path_segments, 0U,
            std::max(path_segment_bytes, sizeof(progpu_native_path_segment)),
            nullptr, nullptr}}};
    WGPUBindGroupDescriptor bind_descriptor{};
    bind_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test index bindings");
    bind_descriptor.layout = engine.semantic_hit_test_layout;
    bind_descriptor.entryCount = bind_entries.size();
    bind_descriptor.entries = bind_entries.data();
    WGPUBindGroup next_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_descriptor);
    if (next_bind_group == nullptr) {
        release_buffer(next_nodes);
        release_buffer(next_indices);
        release_buffer(next_primitives);
        release_buffer(next_path_segments);
        return false;
    }

    engine.release_semantic_hit_test_index();
    engine.semantic_hit_test_node_buffer = next_nodes;
    engine.semantic_hit_test_primitive_index_buffer = next_indices;
    engine.semantic_hit_test_primitive_buffer = next_primitives;
    engine.semantic_hit_test_path_segment_buffer = next_path_segments;
    engine.semantic_hit_test_bind_group = next_bind_group;
    engine.semantic_hit_test_gpu_hash = engine.semantic_hashes.hit_test;
    engine.semantic_hit_test_primitive_count = page.primitive_count;
    engine.semantic_hit_test_node_count = page.node_count;
    engine.semantic_hit_test_primitive_index_count =
        page.primitive_index_count;
    engine.semantic_hit_test_path_segment_count = page.path_segment_count;
    return true;
}

bool valid_query(const progpu_native_hit_test_query& query) noexcept {
    const std::uint32_t capacity =
        query.flags & PROGPU_NATIVE_HIT_TEST_RESULT_CAPACITY_MASK;
    return std::isfinite(query.point.x) && std::isfinite(query.point.y) &&
        std::isfinite(query.region_max.x) &&
        std::isfinite(query.region_max.y) &&
        (query.flags & ~allowed_query_flags) == 0U &&
        capacity <= PROGPU_NATIVE_HIT_TEST_MAX_RESULT_COUNT &&
        ((query.flags & PROGPU_NATIVE_HIT_TEST_ELLIPSE_REGION) == 0U ||
            (query.flags & PROGPU_NATIVE_HIT_TEST_BOUNDS_REGION) != 0U);
}

} // namespace

progpu_native_status begin_hit_test(
    progpu_native_engine* engine,
    const progpu_native_hit_test_query* query,
    std::uint64_t* request_token) {
    if (engine == nullptr || query == nullptr || request_token == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const webgpu::dispatch_scope dispatch_scope(&engine->webgpu_dispatch);
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native GPU hit testing is owner-thread affine.");
    }
    if (engine->device_lost || engine->device == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_DEVICE_LOST,
            "A lost native device cannot execute retained GPU hit tests.");
    }
    if (!valid_query(*query) ||
        engine->semantic_hit_test_pending_token != 0U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The retained GPU hit-test request is invalid or another request is pending.");
    }
    if (engine->semantic_scene_snapshot.empty() ||
        !ensure_hit_test_index(*engine)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_UNSUPPORTED,
            "The retained scene has no executable GPU hit-test index.");
    }
    if (query->root_node_index >= engine->semantic_hit_test_node_count) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The retained GPU hit-test root node is out of range.");
    }

    progpu_native_hit_test_query native_query = *query;
    native_query.primitive_count = engine->semantic_hit_test_primitive_count;
    native_query.node_count = engine->semantic_hit_test_node_count;
    native_query.primitive_index_count =
        engine->semantic_hit_test_primitive_index_count;
    native_query.path_segment_count =
        engine->semantic_hit_test_path_segment_count;
    const std::uint32_t requested =
        native_query.flags & PROGPU_NATIVE_HIT_TEST_RESULT_CAPACITY_MASK;
    const std::uint32_t element_count = requested == 0U
        ? 1U
        : requested + 1U;
    const std::uint64_t copy_bytes =
        static_cast<std::uint64_t>(element_count) *
        sizeof(progpu_native_hit_test_result);
    std::array<progpu_native_hit_test_result,
        PROGPU_NATIVE_HIT_TEST_MAX_RESULT_COUNT + 1U> initial{};
    for (std::uint32_t index = 0U; index < element_count; ++index) {
        initial[index].id = -1;
        initial[index].primitive_index =
            std::numeric_limits<std::uint32_t>::max();
        initial[index].z_index = -std::numeric_limits<float>::max();
    }
    wgpuQueueWriteBuffer(
        engine->queue,
        engine->semantic_hit_test_query_buffer,
        0U,
        &native_query,
        sizeof(native_query));
    wgpuQueueWriteBuffer(
        engine->queue,
        engine->semantic_hit_test_result_buffer,
        0U,
        initial.data(),
        copy_bytes);

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained GPU hit-test encoder could not be created.");
    }
    WGPUComputePassDescriptor pass_descriptor{};
    pass_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test pass");
    WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained GPU hit-test pass could not be created.");
    }
    wgpuComputePassEncoderSetPipeline(
        pass,
        engine->semantic_hit_test_pipeline);
    wgpuComputePassEncoderSetBindGroup(
        pass,
        0U,
        engine->semantic_hit_test_bind_group,
        0U,
        nullptr);
    wgpuComputePassEncoderDispatchWorkgroups(pass, 1U, 1U, 1U);
    wgpuComputePassEncoderEnd(pass);
    wgpuComputePassEncoderRelease(pass);
    wgpuCommandEncoderCopyBufferToBuffer(
        encoder,
        engine->semantic_hit_test_result_buffer,
        0U,
        engine->semantic_hit_test_readback_buffer,
        0U,
        copy_bytes);
    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU hit-test submission");
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained GPU hit-test command could not be finished.");
    }
    engine->submit(command);
    wgpuCommandBufferRelease(command);
    engine->semantic_hit_test_map_state.completion.store(
        webgpu::buffer_map_pending,
        std::memory_order_relaxed);
#if defined(PROGPU_NATIVE_DAWN_ABI)
    WGPUBufferMapCallbackInfo callback = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowSpontaneous;
    callback.callback = hit_test_map_complete;
    callback.userdata1 = nullptr;
    wgpuBufferMapAsync(
        engine->semantic_hit_test_readback_buffer,
        WGPUMapMode_Read,
        0U,
        copy_bytes,
        callback);
#else
    wgpuBufferMapAsync(
        engine->semantic_hit_test_readback_buffer,
        WGPUMapMode_Read,
        0U,
        copy_bytes,
        hit_test_map_complete,
        &engine->semantic_hit_test_map_state);
#endif

    ++engine->semantic_hit_test_next_token;
    if (engine->semantic_hit_test_next_token == 0U) {
        ++engine->semantic_hit_test_next_token;
    }
    engine->semantic_hit_test_pending_token =
        engine->semantic_hit_test_next_token;
    engine->semantic_hit_test_pending_bytes = copy_bytes;
    engine->semantic_hit_test_requested_result_count = requested;
    *request_token = engine->semantic_hit_test_pending_token;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status poll_hit_test(
    progpu_native_engine* engine,
    std::uint64_t request_token,
    progpu_native_hit_test_result* results,
    std::uint32_t result_capacity,
    std::uint32_t* result_count,
    progpu_native_hit_test_result* summary,
    std::uint8_t* complete) {
    if (engine == nullptr || request_token == 0U || result_count == nullptr ||
        summary == nullptr || complete == nullptr ||
        ((results == nullptr) != (result_capacity == 0U))) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const webgpu::dispatch_scope dispatch_scope(&engine->webgpu_dispatch);
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native GPU hit-test polling is owner-thread affine.");
    }
    if (engine->device_lost || engine->device == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_DEVICE_LOST,
            "A lost native device cannot complete retained GPU hit tests.");
    }
    if (request_token != engine->semantic_hit_test_pending_token ||
        (result_capacity != 0U && result_capacity <
            engine->semantic_hit_test_requested_result_count)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The retained GPU hit-test token or result capacity is invalid.");
    }
    *complete = 0U;
    const WGPUBufferMapState state = webgpu::poll_buffer_map(
        engine->device,
        engine->semantic_hit_test_readback_buffer,
        engine->semantic_hit_test_map_state);
    if (state == WGPUBufferMapState_Pending) {
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    if (state != WGPUBufferMapState_Mapped) {
        engine->semantic_hit_test_pending_token = 0U;
        engine->semantic_hit_test_pending_bytes = 0U;
        engine->semantic_hit_test_requested_result_count = 0U;
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained GPU hit-test readback map failed.");
    }
    const auto* mapped = static_cast<const progpu_native_hit_test_result*>(
        webgpu::buffer_get_const_mapped_range(
            engine->semantic_hit_test_readback_buffer,
            engine->semantic_hit_test_pending_bytes));
    if (mapped == nullptr) {
        webgpu::buffer_unmap(engine->semantic_hit_test_readback_buffer);
        engine->semantic_hit_test_pending_token = 0U;
        engine->semantic_hit_test_pending_bytes = 0U;
        engine->semantic_hit_test_requested_result_count = 0U;
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained GPU hit-test readback range is unavailable.");
    }

    *summary = mapped[0];
    const std::uint32_t requested =
        engine->semantic_hit_test_requested_result_count;
    std::uint32_t copied = 0U;
    if (results != nullptr) {
        while (copied < requested && mapped[copied + 1U].hit != 0U) {
            results[copied] = mapped[copied + 1U];
            ++copied;
        }
    }
    *result_count = copied;
    *complete = 1U;
    webgpu::buffer_unmap(engine->semantic_hit_test_readback_buffer);
    engine->semantic_hit_test_map_state.completion.store(
        webgpu::buffer_map_pending,
        std::memory_order_relaxed);
    engine->semantic_hit_test_pending_token = 0U;
    engine->semantic_hit_test_pending_bytes = 0U;
    engine->semantic_hit_test_requested_result_count = 0U;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace progpu::native::execution

extern "C" {

progpu_native_status progpu_native_engine_begin_hit_test(
    progpu_native_engine* engine,
    const progpu_native_hit_test_query* query,
    std::uint64_t* request_token) {
    return progpu::native::execution::begin_hit_test(
        engine,
        query,
        request_token);
}

progpu_native_status progpu_native_engine_poll_hit_test(
    progpu_native_engine* engine,
    std::uint64_t request_token,
    progpu_native_hit_test_result* results,
    std::uint32_t result_capacity,
    std::uint32_t* result_count,
    progpu_native_hit_test_result* summary,
    std::uint8_t* complete) {
    return progpu::native::execution::poll_hit_test(
        engine,
        request_token,
        results,
        result_capacity,
        result_count,
        summary,
        complete);
}

} // extern "C"
