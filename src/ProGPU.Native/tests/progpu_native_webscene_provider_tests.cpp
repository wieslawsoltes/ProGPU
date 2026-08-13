#include "progpu_native_dawn.h"
#include "webscene_gpu_provider.h"

#include <webgpu.h>

#include <IOSurface/IOSurface.h>
#include <dlfcn.h>

#include <chrono>
#include <array>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <mutex>
#include <type_traits>

namespace {

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr, "ProGPU WebScene provider integration failed: %s\n",
        message);
    std::abort();
}

void require(bool condition, const char* message) {
    if (!condition) {
        fail(message);
    }
}

template<typename T>
T load_symbol(void* module, const char* name) {
    static_assert(std::is_pointer_v<T>);
    void* symbol = dlsym(module, name);
    require(symbol != nullptr, name);
    T result{};
    static_assert(sizeof(result) == sizeof(symbol));
    std::memcpy(&result, &symbol, sizeof(result));
    return result;
}

struct provider_api final {
    decltype(&webscene_gpu_provider_get_abi_version) get_abi_version{};
    decltype(&webscene_gpu_provider_get_info) get_info{};
    decltype(&webscene_gpu_provider_create) create{};
    decltype(&webscene_gpu_provider_destroy) destroy{};
    decltype(&webscene_gpu_provider_get_wgpu_instance) get_instance{};
    decltype(&webscene_gpu_provider_get_wgpu_proc_address) get_proc{};
    decltype(&webscene_gpu_provider_create_canvas) create_canvas{};
    decltype(&webscene_gpu_provider_acquire_canvas_texture) acquire{};
    decltype(&webscene_gpu_provider_present_canvas) present{};
    decltype(&webscene_gpu_provider_destroy_canvas) destroy_canvas{};
    decltype(&webscene_gpu_provider_retain_external_texture) retain_external{};
    decltype(&webscene_gpu_provider_release_external_texture) release_external{};

    explicit provider_api(void* module)
        : get_abi_version(load_symbol<decltype(get_abi_version)>(
            module, "webscene_gpu_provider_get_abi_version")),
          get_info(load_symbol<decltype(get_info)>(
            module, "webscene_gpu_provider_get_info")),
          create(load_symbol<decltype(create)>(
            module, "webscene_gpu_provider_create")),
          destroy(load_symbol<decltype(destroy)>(
            module, "webscene_gpu_provider_destroy")),
          get_instance(load_symbol<decltype(get_instance)>(
            module, "webscene_gpu_provider_get_wgpu_instance")),
          get_proc(load_symbol<decltype(get_proc)>(
            module, "webscene_gpu_provider_get_wgpu_proc_address")),
          create_canvas(load_symbol<decltype(create_canvas)>(
            module, "webscene_gpu_provider_create_canvas")),
          acquire(load_symbol<decltype(acquire)>(
            module, "webscene_gpu_provider_acquire_canvas_texture")),
          present(load_symbol<decltype(present)>(
            module, "webscene_gpu_provider_present_canvas")),
          destroy_canvas(load_symbol<decltype(destroy_canvas)>(
            module, "webscene_gpu_provider_destroy_canvas")),
          retain_external(load_symbol<decltype(retain_external)>(
            module, "webscene_gpu_provider_retain_external_texture")),
          release_external(load_symbol<decltype(release_external)>(
            module, "webscene_gpu_provider_release_external_texture")) {}
};

template<typename T>
T resolve(const provider_api& api, webscene_gpu_provider* provider,
    const char* name) {
    static_assert(std::is_pointer_v<T>);
    void* symbol = api.get_proc(provider, name);
    require(symbol != nullptr, name);
    T result{};
    static_assert(sizeof(result) == sizeof(symbol));
    std::memcpy(&result, &symbol, sizeof(result));
    return result;
}

struct adapter_request final {
    std::mutex mutex;
    std::condition_variable changed;
    WGPUAdapter adapter{};
    bool complete{};
};

struct device_request final {
    std::mutex mutex;
    std::condition_variable changed;
    WGPUDevice device{};
    bool complete{};
};

template<typename T>
void wait_for_request(T& request, const char* message) {
    std::unique_lock lock(request.mutex);
    require(request.changed.wait_for(lock, std::chrono::seconds(15),
        [&request] { return request.complete; }), message);
}

WGPUDevice create_device(const provider_api& api,
    webscene_gpu_provider* provider) {
    adapter_request adapter_state;
    WGPURequestAdapterOptions adapter_options =
        WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
    adapter_options.backendType = WGPUBackendType_Metal;
    adapter_options.featureLevel = WGPUFeatureLevel_Core;
    WGPURequestAdapterCallbackInfo adapter_callback =
        WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
    adapter_callback.mode = WGPUCallbackMode_AllowSpontaneous;
    adapter_callback.userdata1 = &adapter_state;
    adapter_callback.callback = [](
        WGPURequestAdapterStatus status,
        WGPUAdapter adapter,
        WGPUStringView,
        void* userdata1,
        void*) {
        auto* state = static_cast<adapter_request*>(userdata1);
        {
            std::lock_guard lock(state->mutex);
            state->adapter = status == WGPURequestAdapterStatus_Success
                ? adapter
                : nullptr;
            state->complete = true;
        }
        state->changed.notify_one();
    };
    resolve<WGPUProcInstanceRequestAdapter>(
        api, provider, "wgpuInstanceRequestAdapter")(
        static_cast<WGPUInstance>(api.get_instance(provider)),
        &adapter_options,
        adapter_callback);
    wait_for_request(adapter_state, "adapter request timed out");
    require(adapter_state.adapter != nullptr, "Metal adapter unavailable");

    const WGPUFeatureName features[] = {
        WGPUFeatureName_SharedTextureMemoryIOSurface,
        WGPUFeatureName_SharedFenceMTLSharedEvent
    };
    device_request device_state;
    WGPUDeviceDescriptor descriptor = WGPU_DEVICE_DESCRIPTOR_INIT;
    descriptor.requiredFeatureCount = std::size(features);
    descriptor.requiredFeatures = features;
    descriptor.uncapturedErrorCallbackInfo.callback = [](
        WGPUDevice const*,
        WGPUErrorType type,
        WGPUStringView message,
        void*,
        void*) {
        std::fprintf(stderr, "Dawn validation error %u: %.*s\n",
            static_cast<unsigned>(type),
            static_cast<int>(message.length),
            message.data == nullptr ? "" : message.data);
        std::abort();
    };
    descriptor.deviceLostCallbackInfo.mode =
        WGPUCallbackMode_AllowSpontaneous;
    descriptor.deviceLostCallbackInfo.callback = [](
        WGPUDevice const*,
        WGPUDeviceLostReason reason,
        WGPUStringView message,
        void*,
        void*) {
        if (reason == WGPUDeviceLostReason_Destroyed) {
            return;
        }
        std::fprintf(stderr, "Dawn device lost %u: %.*s\n",
            static_cast<unsigned>(reason),
            static_cast<int>(message.length),
            message.data == nullptr ? "" : message.data);
        std::abort();
    };
    WGPURequestDeviceCallbackInfo device_callback =
        WGPU_REQUEST_DEVICE_CALLBACK_INFO_INIT;
    device_callback.mode = WGPUCallbackMode_AllowSpontaneous;
    device_callback.userdata1 = &device_state;
    device_callback.callback = [](
        WGPURequestDeviceStatus status,
        WGPUDevice device,
        WGPUStringView,
        void* userdata1,
        void*) {
        auto* state = static_cast<device_request*>(userdata1);
        {
            std::lock_guard lock(state->mutex);
            state->device = status == WGPURequestDeviceStatus_Success
                ? device
                : nullptr;
            state->complete = true;
        }
        state->changed.notify_one();
    };
    resolve<WGPUProcAdapterRequestDevice>(
        api, provider, "wgpuAdapterRequestDevice")(
        adapter_state.adapter,
        &descriptor,
        device_callback);
    wait_for_request(device_state, "device request timed out");
    resolve<WGPUProcAdapterRelease>(api, provider, "wgpuAdapterRelease")(
        adapter_state.adapter);
    require(device_state.device != nullptr, "Dawn device unavailable");
    return device_state.device;
}

struct resolver_context final {
    const provider_api* api{};
    webscene_gpu_provider* provider{};
};

void* resolve_for_progpu(void* context, const char* name) {
    auto* state = static_cast<resolver_context*>(context);
    return state->api->get_proc(state->provider, name);
}

void verify_and_capture(IOSurfaceRef surface, const char* output_path) {
    require(surface != nullptr, "provider did not expose an IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not lock IOSurface");

    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U, "unexpected IOSurface storage");

    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const std::uint8_t* outside = pixel(2U, 2U);
    const std::uint8_t* clipped = pixel(12U, 20U);
    const std::uint8_t* inside = pixel(20U, 20U);
    std::fprintf(stderr,
        "IOSurface outside=%u,%u,%u,%u clipped=%u,%u,%u,%u "
        "inside=%u,%u,%u,%u row=%zu\n",
        outside[0], outside[1], outside[2], outside[3],
        clipped[0], clipped[1], clipped[2], clipped[3],
        inside[0], inside[1], inside[2], inside[3], row_bytes);

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr, "could not create capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0; y < height; ++y) {
            for (std::size_t x = 0; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[] = {
                    source[2], source[1], source[0]
                };
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "capture write failed");
            }
        }
        require(std::fclose(output) == 0, "capture close failed");
    }

    require(outside[2] < 80U && outside[0] > outside[2],
        "clear-color pixel did not reach the IOSurface");
    require(std::memcmp(outside, clipped, 4U) == 0,
        "physical draw-state scissor did not preserve the clear color");
    require(inside[2] > 100U && inside[2] < 160U &&
        inside[2] > inside[1] * 2U &&
        inside[2] > inside[0] * 2U,
        "native primitive opacity did not reach the IOSurface");
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess, "could not unlock IOSurface");
}

} // namespace

int main(int argc, char** argv) {
    require(argc == 2 || argc == 3,
        "usage: test PROVIDER_DYLIB [CAPTURE_PPM]");
    void* module = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    require(module != nullptr, dlerror());
    provider_api api(module);
    require(api.get_abi_version() == WEBSCENE_GPU_PROVIDER_ABI_VERSION,
        "provider ABI mismatch");

    webscene_gpu_provider_info provider_info{};
    provider_info.struct_size = sizeof(provider_info);
    require(api.get_info(&provider_info) != 0U &&
        provider_info.abi_version == WEBSCENE_GPU_PROVIDER_ABI_VERSION &&
        (provider_info.capabilities & WEBSCENE_GPU_CAPABILITY_WEBGPU) != 0U,
        "provider does not report WebGPU support");

    webscene_gpu_provider_options provider_options{};
    provider_options.struct_size = sizeof(provider_options);
    provider_options.required_capabilities =
        WEBSCENE_GPU_CAPABILITY_WEBGPU;
    webscene_gpu_provider* provider = api.create(&provider_options);
    require(provider != nullptr, "provider creation failed");
    WGPUDevice device = create_device(api, provider);
    WGPUQueue queue = resolve<WGPUProcDeviceGetQueue>(
        api, provider, "wgpuDeviceGetQueue")(device);
    require(queue != nullptr, "device queue unavailable");

    resolver_context resolver{&api, provider};
    progpu_native_dawn_engine_options engine_options{};
    engine_options.struct_size = sizeof(engine_options);
    engine_options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    engine_options.adapter_abi_version =
        PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
    engine_options.provider_abi_version =
        WEBSCENE_GPU_PROVIDER_ABI_VERSION;
    engine_options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    engine_options.resolver_context = &resolver;
    engine_options.resolve_proc = resolve_for_progpu;
    engine_options.instance = reinterpret_cast<std::uintptr_t>(
        api.get_instance(provider));
    engine_options.device = reinterpret_cast<std::uintptr_t>(device);
    engine_options.queue = reinterpret_cast<std::uintptr_t>(queue);
    progpu_native_engine* engine{};
    require(progpu_native_dawn_engine_create(&engine_options, &engine) ==
        PROGPU_NATIVE_STATUS_SUCCESS && engine != nullptr,
        "ProGPU Dawn engine creation failed");

    webscene_gpu_canvas_configuration canvas_configuration{};
    canvas_configuration.struct_size = sizeof(canvas_configuration);
    canvas_configuration.device = reinterpret_cast<std::uintptr_t>(device);
    canvas_configuration.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_CopySrc;
    canvas_configuration.pixel_format =
        WEBSCENE_GPU_PIXEL_FORMAT_BGRA8_UNORM;
    canvas_configuration.alpha_mode =
        WEBSCENE_GPU_ALPHA_MODE_PREMULTIPLIED;
    canvas_configuration.buffer_count = 3U;
    webscene_gpu_canvas* canvas = api.create_canvas(
        provider, &canvas_configuration, 64U, 48U);
    require(canvas != nullptr, "canvas creation failed");

    std::uintptr_t texture_handle{};
    require(api.acquire(provider, canvas, &texture_handle) ==
        WEBSCENE_GPU_STATUS_SUCCESS && texture_handle != 0U,
        "canvas texture acquisition failed");
    auto texture = reinterpret_cast<WGPUTexture>(texture_handle);
    WGPUTextureViewDescriptor view_descriptor =
        WGPU_TEXTURE_VIEW_DESCRIPTOR_INIT;
    WGPUTextureView view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        texture, &view_descriptor);
    require(view != nullptr, "target view creation failed");

    WGPUTextureDescriptor mask_texture_descriptor =
        WGPU_TEXTURE_DESCRIPTOR_INIT;
    mask_texture_descriptor.label = {
        "ProGPU common mask provider test",
        WGPU_STRLEN};
    mask_texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    mask_texture_descriptor.dimension = WGPUTextureDimension_2D;
    mask_texture_descriptor.size = {1U, 1U, 1U};
    mask_texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    mask_texture_descriptor.mipLevelCount = 1U;
    mask_texture_descriptor.sampleCount = 1U;
    WGPUTexture mask_texture = resolve<WGPUProcDeviceCreateTexture>(
        api, provider, "wgpuDeviceCreateTexture")(
        device, &mask_texture_descriptor);
    require(mask_texture != nullptr, "mask texture creation failed");
    WGPUTextureView mask_view = resolve<WGPUProcTextureCreateView>(
        api, provider, "wgpuTextureCreateView")(
        mask_texture, &view_descriptor);
    require(mask_view != nullptr, "mask view creation failed");
    const std::uint8_t opaque_mask = 255U;
    WGPUTexelCopyTextureInfo mask_destination =
        WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    mask_destination.texture = mask_texture;
    mask_destination.aspect = WGPUTextureAspect_All;
    WGPUTexelCopyBufferLayout mask_layout =
        WGPU_TEXEL_COPY_BUFFER_LAYOUT_INIT;
    mask_layout.bytesPerRow = 1U;
    mask_layout.rowsPerImage = 1U;
    const WGPUExtent3D mask_extent{1U, 1U, 1U};
    resolve<WGPUProcQueueWriteTexture>(
        api, provider, "wgpuQueueWriteTexture")(
        queue,
        &mask_destination,
        &opaque_mask,
        sizeof(opaque_mask),
        &mask_layout,
        &mask_extent);

    const progpu_native_rect rectangles[]{
        {4.0F, 4.0F, 32.0F, 24.0F,
            {0.92F, 0.18F, 0.08F, 1.0F}},
        {4.0F, 4.0F, 32.0F, 24.0F,
            {0.92F, 0.18F, 0.08F, 1.0F}}
    };
    progpu_native_draw_state draw_state{};
    draw_state.struct_size = sizeof(draw_state);
    draw_state.flags = PROGPU_NATIVE_DRAW_STATE_CLIP_RECT;
    draw_state.opacity = 0.5F;
    draw_state.clip_rect = {10.25F, 8.25F, 10.5F, 10.5F};
    draw_state.group_opacity = 1.0F;
    progpu_native_frame frame{
        sizeof(progpu_native_frame),
        64U,
        48U,
        1.5F,
        reinterpret_cast<std::uintptr_t>(view),
        {0.05F, 0.10F, 0.22F, 1.0F},
        rectangles,
        1U,
        &draw_state
    };
    progpu_native_frame_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    draw_state.flags = 1U << 31U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "unknown draw-state feature did not fail closed");
    draw_state.flags = PROGPU_NATIVE_DRAW_STATE_CLIP_RECT;
    draw_state.struct_size = offsetof(
        progpu_native_draw_state,
        group_opacity);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "legacy ABI-v3 draw-state prefix failed");
    draw_state.struct_size = offsetof(
        progpu_native_draw_state,
        group_mask);
    draw_state.group_opacity = 0.75F;
    draw_state.group_revision = 3U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "40-byte ABI-v3 group draw-state prefix failed");
    draw_state.struct_size = offsetof(
        progpu_native_draw_state,
        group_effect);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "48-byte ABI-v3 mask draw-state prefix failed");
    draw_state.struct_size = sizeof(draw_state);
    draw_state.group_revision = 0U;
    draw_state.group_opacity = 1.1F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "out-of-range group opacity did not fail closed");
    draw_state.group_opacity = 1.0F;
    draw_state.clip_rect = {10.0F, 8.0F, 0.0F, 10.0F};
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "empty draw-state clip did not skip the draw");
    draw_state.clip_rect = {10.25F, 8.25F, 10.5F, 10.5F};
    frame.struct_size = offsetof(progpu_native_frame, draw_state);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "legacy ABI v3 frame prefix failed");
    frame.struct_size = sizeof(frame);
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "ProGPU clipped-opacity render failed");
    draw_state.opacity = 1.0F;
    draw_state.group_opacity = 0.25F;
    draw_state.group_revision = 7U;
    frame.rect_count = 2U;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 1U,
        "ProGPU group-opacity content render failed");
    progpu_native_layer_metrics layer_metrics{};
    layer_metrics.struct_size = sizeof(layer_metrics);
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 1U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 0U &&
        layer_metrics.allocation_count == 1U,
        "group layer content metrics are invalid");
    alignas(progpu_native_layer_metrics)
        std::array<std::byte, 56U> legacy_layer_metrics_bytes{};
    auto* legacy_layer_metrics =
        reinterpret_cast<progpu_native_layer_metrics*>(
            legacy_layer_metrics_bytes.data());
    legacy_layer_metrics->struct_size = legacy_layer_metrics_bytes.size();
    require(progpu_native_engine_get_layer_metrics(
        engine, legacy_layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        legacy_layer_metrics->struct_size ==
            sizeof(progpu_native_layer_metrics) &&
        legacy_layer_metrics->content_pass_count == 1U,
        "legacy layer-metrics prefix failed");
    draw_state.group_opacity = 0.5F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U &&
        metrics.vertex_upload_bytes == 0U,
        "retained group replay did not skip family compilation and upload");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.allocation_count == 1U &&
        layer_metrics.vertex_upload_bytes == 224U,
        "retained group replay metrics are invalid");

    progpu_native_group_mask group_mask{};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = 0xFFFFFFFFU;
    draw_state.group_mask = &group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "unknown group-mask kind did not fail closed");
    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_TEXTURE;
    group_mask.external_view = frame.target_view;
    group_mask.width = 1U;
    group_mask.height = 1U;
    group_mask.texture_format = PROGPU_NATIVE_MASK_TEXTURE_R8_UNORM;
    group_mask.revision = 1U;
    group_mask.destination_rect = {10.0F, 8.0F, 11.0F, 11.0F};
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "target/group-mask alias did not fail closed");
    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_TEXTURE;
    group_mask.external_view = reinterpret_cast<std::uintptr_t>(mask_view);
    group_mask.width = 1U;
    group_mask.height = 1U;
    group_mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    group_mask.texture_format = PROGPU_NATIVE_MASK_TEXTURE_R8_UNORM;
    group_mask.revision = 1U;
    group_mask.destination_rect = {10.0F, 8.0F, 11.0F, 11.0F};
    draw_state.group_mask = &group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained texture group-mask replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.uniform_upload_bytes == 96U &&
        layer_metrics.mask_kind == PROGPU_NATIVE_GROUP_MASK_TEXTURE &&
        layer_metrics.mask_revision == 1U &&
        layer_metrics.mask_bind_group_cache_hit == 0U &&
        layer_metrics.mask_uniform_upload_bytes == 96U,
        "texture group-mask metrics are invalid");

    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE;
    group_mask.bounds = {10.0F, 8.0F, 11.0F, 11.0F};
    group_mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    group_mask.corner_radii_x[0] = 2.0F;
    group_mask.corner_radii_x[1] = 2.0F;
    group_mask.corner_radii_x[2] = 2.0F;
    group_mask.corner_radii_x[3] = 2.0F;
    group_mask.corner_radii_y[0] = 2.0F;
    group_mask.corner_radii_y[1] = 2.0F;
    group_mask.corner_radii_y[2] = 2.0F;
    group_mask.corner_radii_y[3] = 2.0F;
    group_mask.opacity = 1.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained analytic group-mask replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.uniform_upload_bytes == 96U &&
        layer_metrics.mask_kind ==
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE &&
        layer_metrics.mask_bind_group_cache_hit == 1U &&
        layer_metrics.mask_uniform_upload_bytes == 96U,
        "analytic group-mask metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "unchanged analytic group-mask replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.mask_bind_group_cache_hit == 1U &&
        layer_metrics.mask_uniform_upload_bytes == 0U &&
        layer_metrics.uniform_upload_bytes == 0U,
        "unchanged analytic group-mask replay uploaded state");

    alignas(progpu_native_group_mask)
        std::array<std::byte, 144U> legacy_group_mask_bytes{};
    std::memcpy(
        legacy_group_mask_bytes.data(),
        &group_mask,
        legacy_group_mask_bytes.size());
    auto* legacy_group_mask =
        reinterpret_cast<progpu_native_group_mask*>(
            legacy_group_mask_bytes.data());
    legacy_group_mask->struct_size = legacy_group_mask_bytes.size();
    draw_state.group_mask = legacy_group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "legacy common-mask descriptor prefix failed");

    const progpu_native_path_segment clip_segments[]{
        {{0.0F, 0.0F}, {20.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{20.0F, 0.0F}, {20.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{20.0F, 20.0F}, {0.0F, 20.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{0.0F, 20.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}
    };
    const progpu_native_clip_path clip_paths[]{
        {0U, 4U, 0.0F, 0.0F, 20.0F, 20.0F,
            {1.0F, 0.15F, -0.1F, 1.0F, 10.0F, 8.0F},
            PROGPU_NATIVE_FILL_RULE_NON_ZERO, 8U,
            PROGPU_NATIVE_CLIP_INTERSECT, 0U},
        {0U, 4U, 0.0F, 0.0F, 20.0F, 20.0F,
            {0.4F, -0.1F, 0.15F, 0.35F, 16.0F, 12.0F},
            PROGPU_NATIVE_FILL_RULE_EVEN_ODD, 8U,
            PROGPU_NATIVE_CLIP_DIFFERENCE, 0U}
    };
    const progpu_native_clip_chain clip_chain{
        sizeof(progpu_native_clip_chain),
        0U,
        clip_paths,
        2U,
        clip_segments,
        4U
    };
    group_mask = {};
    group_mask.struct_size = sizeof(group_mask);
    group_mask.kind = PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN;
    group_mask.revision = 1U;
    group_mask.clip_chain = &clip_chain;
    draw_state.group_mask = &group_mask;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "retained vector clip-chain render failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.mask_kind ==
            PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN &&
        layer_metrics.clip_path_count == 2U &&
        layer_metrics.clip_rasterized_path_count > 0U &&
        layer_metrics.clip_pass_count == 5U &&
        layer_metrics.clip_cache_hit == 0U &&
        layer_metrics.clip_path_upload_bytes > 0U &&
        layer_metrics.clip_coverage_staging_bytes > 0U,
        "changed vector clip-chain metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "unchanged vector clip-chain replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.clip_path_count == 2U &&
        layer_metrics.clip_rasterized_path_count == 0U &&
        layer_metrics.clip_pass_count == 0U &&
        layer_metrics.clip_cache_hit == 1U &&
        layer_metrics.clip_path_upload_bytes == 0U &&
        layer_metrics.clip_coverage_staging_bytes == 0U,
        "unchanged vector clip-chain replay rebuilt coverage");

    progpu_native_group_effect group_effect{};
    group_effect.struct_size = sizeof(group_effect);
    group_effect.kind = 0xFFFFFFFFU;
    draw_state.group_effect = &group_effect;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "unknown group-effect kind did not fail closed");
    group_effect = {};
    group_effect.struct_size = sizeof(group_effect);
    group_effect.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    group_effect.revision = 1U;
    group_effect.sigma_x = 32.0F;
    group_effect.sigma_y = 2.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "out-of-range physical Gaussian sigma did not fail closed");
    group_effect.sigma_x = 2.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained Gaussian group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
        layer_metrics.effect_revision == 1U &&
        layer_metrics.effect_pass_count == 2U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 32U &&
        layer_metrics.effect_texture_bytes == 64U * 48U * 8U,
        "changed Gaussian group-effect metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "unchanged Gaussian group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.effect_pass_count == 0U &&
        layer_metrics.effect_cache_hit == 1U &&
        layer_metrics.effect_uniform_upload_bytes == 0U &&
        layer_metrics.effect_texture_bytes == 64U * 48U * 8U,
        "unchanged Gaussian group-effect replay dispatched work");
    group_effect.revision = 2U;
    group_effect.sigma_x = 3.0F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "changed Gaussian group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.effect_pass_count == 2U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 16U,
        "changed Gaussian group-effect replay did not reuse content");
    group_effect.kind = PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
    group_effect.revision = 3U;
    group_effect.sigma_x = 2.0F;
    group_effect.sigma_y = 2.0F;
    group_effect.offset_x = 3.5F;
    group_effect.offset_y = -1.25F;
    group_effect.color_r = 0.1F;
    group_effect.color_g = 0.2F;
    group_effect.color_b = 0.3F;
    group_effect.color_a = 1.5F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        "out-of-range drop-shadow color did not fail closed");
    group_effect.color_a = 0.75F;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "retained drop-shadow group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.cache_hit == 1U &&
        layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
        layer_metrics.effect_revision == 3U &&
        layer_metrics.effect_pass_count == 3U &&
        layer_metrics.effect_cache_hit == 0U &&
        layer_metrics.effect_uniform_upload_bytes == 48U &&
        layer_metrics.effect_texture_bytes == 64U * 48U * 8U,
        "changed drop-shadow group-effect metrics are invalid");
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count == 0U,
        "unchanged drop-shadow group-effect replay failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.composite_pass_count == 1U &&
        layer_metrics.effect_pass_count == 0U &&
        layer_metrics.effect_cache_hit == 1U &&
        layer_metrics.effect_uniform_upload_bytes == 0U,
        "unchanged drop-shadow group-effect replay dispatched work");
    group_effect.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "same-revision group-effect kind transition failed");
    require(progpu_native_engine_get_layer_metrics(
        engine, &layer_metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        layer_metrics.content_pass_count == 0U &&
        layer_metrics.effect_kind ==
            PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
        layer_metrics.effect_pass_count == 2U &&
        layer_metrics.effect_cache_hit == 0U,
        "same-revision group-effect kind transition reused stale output");
    draw_state.group_effect = nullptr;
    require(progpu_native_engine_render(engine, &frame, &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS,
        "post-effect retained group replay failed");
    std::uint64_t submission{};
    require(progpu_native_engine_get_last_submission(engine, &submission) ==
        PROGPU_NATIVE_STATUS_SUCCESS && submission != 0U,
        "submission token unavailable");
    std::uint8_t complete{};
    require(progpu_native_engine_poll_submission(
        engine, submission, 1U, &complete) == PROGPU_NATIVE_STATUS_SUCCESS &&
        complete != 0U, "ProGPU submission did not complete");

    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(texture);
    webscene_gpu_external_texture external{};
    external.struct_size = sizeof(external);
    require(api.present(provider, canvas, &external) ==
        WEBSCENE_GPU_STATUS_SUCCESS &&
        external.handle_kind == WEBSCENE_GPU_HANDLE_IOSURFACE &&
        (external.flags & WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE) != 0U,
        "provider presentation failed");

    verify_and_capture(
        reinterpret_cast<IOSurfaceRef>(external.shared_handle),
        argc == 3 ? argv[2] : nullptr);
    require(api.retain_external(provider, &external) ==
        WEBSCENE_GPU_STATUS_SUCCESS,
        "external texture retain failed");
    api.release_external(provider, &external);
    api.release_external(provider, &external);

    progpu_native_engine_destroy(engine);
    resolve<WGPUProcTextureViewRelease>(
        api, provider, "wgpuTextureViewRelease")(mask_view);
    resolve<WGPUProcTextureRelease>(
        api, provider, "wgpuTextureRelease")(mask_texture);
    api.destroy_canvas(provider, canvas);
    resolve<WGPUProcQueueRelease>(api, provider, "wgpuQueueRelease")(queue);
    resolve<WGPUProcDeviceRelease>(api, provider, "wgpuDeviceRelease")(device);
    api.destroy(provider);
    require(dlclose(module) == 0, "provider unload failed");
    std::printf(
        "ProGPU rendered through WebScene provider '%s' on '%s': "
        "draws=%u submission=%llu capture=%s\n",
        provider_info.name,
        provider_info.adapter,
        metrics.draw_call_count,
        static_cast<unsigned long long>(submission),
        argc == 3 ? argv[2] : "disabled");
    return 0;
}
