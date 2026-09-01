#include "progpu_native_dawn.h"
#include "progpu_native_direct2d_scene_submission.hpp"

#include <webgpu.h>

#include <d2d1.h>
#include <objbase.h>
#include <windows.h>
#include <wincodec.h>

#include <array>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <mutex>
#include <span>
#include <type_traits>
#include <vector>

namespace d2d = progpu::native::direct2d::compat;
namespace native_com = progpu::native::com;

namespace {

constexpr std::uint32_t width = 64U;
constexpr std::uint32_t height = 48U;
constexpr std::uint32_t row_bytes = width * 4U;

[[noreturn]] void fail(const char* message)
{
    std::fprintf(stderr, "ProGPU Direct2D differential failed: %s\n", message);
    std::abort();
}

void require(bool condition, const char* message)
{
    if (!condition) {
        fail(message);
    }
}

class dawn_api final {
public:
    explicit dawn_api(const wchar_t* path) : module_(LoadLibraryW(path))
    {
        require(module_ != nullptr, "could not load webgpu_dawn.dll");
    }

    ~dawn_api()
    {
        if (module_ != nullptr) {
            FreeLibrary(module_);
        }
    }

    dawn_api(const dawn_api&) = delete;
    dawn_api& operator=(const dawn_api&) = delete;

    template<typename Procedure>
    [[nodiscard]] Procedure get(const char* name) const
    {
        static_assert(std::is_pointer_v<Procedure>);
        FARPROC symbol = GetProcAddress(module_, name);
        require(symbol != nullptr, name);
        Procedure result{};
        static_assert(sizeof(result) == sizeof(symbol));
        std::memcpy(&result, &symbol, sizeof(result));
        return result;
    }

    [[nodiscard]] void* resolve(const char* name) const noexcept
    {
        FARPROC symbol = GetProcAddress(module_, name);
        void* result = nullptr;
        static_assert(sizeof(result) == sizeof(symbol));
        std::memcpy(&result, &symbol, sizeof(result));
        return result;
    }

private:
    HMODULE module_ = nullptr;
};

struct adapter_request final {
    std::mutex mutex;
    std::condition_variable changed;
    WGPUAdapter adapter = nullptr;
    bool complete = false;
};

struct device_request final {
    std::mutex mutex;
    std::condition_variable changed;
    WGPUDevice device = nullptr;
    bool complete = false;
};

struct map_request final {
    std::mutex mutex;
    std::condition_variable changed;
    WGPUMapAsyncStatus status = WGPUMapAsyncStatus_Error;
    bool complete = false;
};

template<typename Request>
void wait_for(Request& request, const char* message)
{
    std::unique_lock lock(request.mutex);
    require(request.changed.wait_for(
        lock,
        std::chrono::seconds(30),
        [&request] { return request.complete; }), message);
}

struct gpu_context final {
    WGPUInstance instance = nullptr;
    WGPUAdapter adapter = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
};

gpu_context create_gpu(const dawn_api& api)
{
    constexpr WGPUInstanceFeatureName instance_features[]{
        WGPUInstanceFeatureName_TimedWaitAny};
    WGPUInstanceDescriptor instance_descriptor =
        WGPU_INSTANCE_DESCRIPTOR_INIT;
    instance_descriptor.requiredFeatureCount = std::size(instance_features);
    instance_descriptor.requiredFeatures = instance_features;
    WGPUInstance instance = api.get<WGPUProcCreateInstance>(
        "wgpuCreateInstance")(&instance_descriptor);
    require(instance != nullptr, "Dawn instance creation failed");

    adapter_request adapter_state;
    WGPURequestAdapterOptions adapter_options =
        WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
    adapter_options.backendType = WGPUBackendType_D3D12;
    adapter_options.featureLevel = WGPUFeatureLevel_Core;
    WGPURequestAdapterCallbackInfo adapter_callback =
        WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
    adapter_callback.mode = WGPUCallbackMode_AllowSpontaneous;
    adapter_callback.userdata1 = &adapter_state;
    adapter_callback.callback = [](
        WGPURequestAdapterStatus status,
        WGPUAdapter adapter,
        WGPUStringView message,
        void* userdata1,
        void*) {
        auto* state = static_cast<adapter_request*>(userdata1);
        if (status != WGPURequestAdapterStatus_Success) {
            std::fprintf(stderr, "Dawn adapter error: %.*s\n",
                static_cast<int>(message.length),
                message.data == nullptr ? "" : message.data);
        }
        {
            const std::lock_guard lock(state->mutex);
            state->adapter = status == WGPURequestAdapterStatus_Success
                ? adapter
                : nullptr;
            state->complete = true;
        }
        state->changed.notify_one();
    };
    api.get<WGPUProcInstanceRequestAdapter>("wgpuInstanceRequestAdapter")(
        instance, &adapter_options, adapter_callback);
    wait_for(adapter_state, "D3D12 adapter request timed out");
    require(adapter_state.adapter != nullptr, "D3D12 adapter unavailable");

    device_request device_state;
    WGPUDeviceDescriptor device_descriptor = WGPU_DEVICE_DESCRIPTOR_INIT;
    device_descriptor.uncapturedErrorCallbackInfo.callback = [](
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
    device_descriptor.deviceLostCallbackInfo.mode =
        WGPUCallbackMode_AllowSpontaneous;
    device_descriptor.deviceLostCallbackInfo.callback = [](
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
        WGPUStringView message,
        void* userdata1,
        void*) {
        auto* state = static_cast<device_request*>(userdata1);
        if (status != WGPURequestDeviceStatus_Success) {
            std::fprintf(stderr, "Dawn device error: %.*s\n",
                static_cast<int>(message.length),
                message.data == nullptr ? "" : message.data);
        }
        {
            const std::lock_guard lock(state->mutex);
            state->device = status == WGPURequestDeviceStatus_Success
                ? device
                : nullptr;
            state->complete = true;
        }
        state->changed.notify_one();
    };
    api.get<WGPUProcAdapterRequestDevice>("wgpuAdapterRequestDevice")(
        adapter_state.adapter, &device_descriptor, device_callback);
    wait_for(device_state, "D3D12 device request timed out");
    require(device_state.device != nullptr, "D3D12 device unavailable");
    WGPUQueue queue = api.get<WGPUProcDeviceGetQueue>(
        "wgpuDeviceGetQueue")(device_state.device);
    require(queue != nullptr, "D3D12 queue unavailable");
    return {instance, adapter_state.adapter, device_state.device, queue};
}

void release_gpu(const dawn_api& api, gpu_context& gpu)
{
    api.get<WGPUProcQueueRelease>("wgpuQueueRelease")(gpu.queue);
    api.get<WGPUProcDeviceRelease>("wgpuDeviceRelease")(gpu.device);
    api.get<WGPUProcAdapterRelease>("wgpuAdapterRelease")(gpu.adapter);
    api.get<WGPUProcInstanceRelease>("wgpuInstanceRelease")(gpu.instance);
    gpu = {};
}

void* resolve_for_engine(void* context, const char* name)
{
    return static_cast<const dawn_api*>(context)->resolve(name);
}

struct portable_scene final {
    native_com::pointer<d2d::factory> factory;
    native_com::pointer<d2d::render_target> target;
    native_com::pointer<d2d::scene_render_target_native> scene_target;
};

portable_scene record_portable_scene()
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
        width, height, 96.0F, 96.0F, 9201U, 1U};
    d2d::render_target* raw_target = nullptr;
    require(scene_factory->CreateSceneRenderTarget(
            &properties, &raw_target) == native_com::ok &&
        raw_target != nullptr, "portable target creation failed");
    native_com::pointer<d2d::render_target> target;
    target.attach(raw_target);

    const std::array<d2d::color_f, 2U> colors{
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
    const d2d::gradient_stop linear_stops[]{
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

    const d2d::gradient_stop radial_stops[]{
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
    const d2d::color_f clear{0.05F, 0.1F, 0.15F, 1.0F};
    const d2d::rectangle_f rectangle{4.0F, 4.0F, 20.0F, 20.0F};
    const d2d::ellipse ellipse_value{{40.0F, 14.0F}, 8.0F, 8.0F};
    const d2d::rectangle_f stroked{8.0F, 28.0F, 28.0F, 42.0F};
    const d2d::rounded_rectangle rounded{
        {36.0F, 28.0F, 56.0F, 44.0F}, 4.0F, 4.0F};
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
    require(target->EndDraw(nullptr, nullptr) == native_com::ok,
        "portable scene recording failed");
    native_com::pointer<d2d::scene_render_target_native> scene_target;
    require(target.as(
            d2d::scene_render_target_native_interface_id,
            scene_target) == native_com::ok &&
        scene_target, "portable scene target query failed");
    return {std::move(factory), std::move(target), std::move(scene_target)};
}

std::vector<std::uint8_t> render_progpu(
    const dawn_api& api,
    const gpu_context& gpu,
    d2d::scene_render_target_native* scene_target)
{
    progpu_native_dawn_engine_options options{};
    options.struct_size = sizeof(options);
    options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.adapter_abi_version = PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
    options.provider_abi_version =
        PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION;
    options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    options.resolver_context = const_cast<dawn_api*>(&api);
    options.resolve_proc = resolve_for_engine;
    options.instance = reinterpret_cast<std::uintptr_t>(gpu.instance);
    options.device = reinterpret_cast<std::uintptr_t>(gpu.device);
    options.queue = reinterpret_cast<std::uintptr_t>(gpu.queue);
    progpu_native_engine* engine = nullptr;
    require(progpu_native_dawn_engine_create(&options, &engine) ==
            PROGPU_NATIVE_STATUS_SUCCESS &&
        engine != nullptr, "ProGPU Dawn engine creation failed");

    WGPUTextureDescriptor texture_descriptor = WGPU_TEXTURE_DESCRIPTOR_INIT;
    texture_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_CopySrc;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {width, height, 1U};
    texture_descriptor.format = WGPUTextureFormat_BGRA8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    WGPUTexture texture = api.get<WGPUProcDeviceCreateTexture>(
        "wgpuDeviceCreateTexture")(gpu.device, &texture_descriptor);
    require(texture != nullptr, "D3D12 target texture creation failed");
    WGPUTextureViewDescriptor view_descriptor =
        WGPU_TEXTURE_VIEW_DESCRIPTOR_INIT;
    WGPUTextureView view = api.get<WGPUProcTextureCreateView>(
        "wgpuTextureCreateView")(texture, &view_descriptor);
    require(view != nullptr, "D3D12 target view creation failed");

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
    require(d2d::render_scene_target(
            scene_target,
            engine,
            render_options,
            scratch,
            &scene_metrics,
            &frame_metrics,
            &diagnostics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        diagnostics.stage == d2d::scene_submission_stage::none &&
        scene_metrics.draw_count == 4U &&
        frame_metrics.command_count == 4U &&
        frame_metrics.submission_count == 1U,
        "ProGPU D3D12 Direct2D render failed");

    WGPUBufferDescriptor buffer_descriptor = WGPU_BUFFER_DESCRIPTOR_INIT;
    buffer_descriptor.size = static_cast<std::uint64_t>(row_bytes) * height;
    buffer_descriptor.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
    WGPUBuffer buffer = api.get<WGPUProcDeviceCreateBuffer>(
        "wgpuDeviceCreateBuffer")(gpu.device, &buffer_descriptor);
    require(buffer != nullptr, "D3D12 readback buffer creation failed");
    WGPUCommandEncoderDescriptor encoder_descriptor =
        WGPU_COMMAND_ENCODER_DESCRIPTOR_INIT;
    WGPUCommandEncoder encoder = api.get<WGPUProcDeviceCreateCommandEncoder>(
        "wgpuDeviceCreateCommandEncoder")(gpu.device, &encoder_descriptor);
    require(encoder != nullptr, "D3D12 readback encoder creation failed");
    WGPUTexelCopyTextureInfo source = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    source.texture = texture;
    WGPUTexelCopyBufferInfo destination = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
    destination.buffer = buffer;
    destination.layout.bytesPerRow = row_bytes;
    destination.layout.rowsPerImage = height;
    const WGPUExtent3D extent{width, height, 1U};
    api.get<WGPUProcCommandEncoderCopyTextureToBuffer>(
        "wgpuCommandEncoderCopyTextureToBuffer")(
        encoder, &source, &destination, &extent);
    WGPUCommandBufferDescriptor command_descriptor =
        WGPU_COMMAND_BUFFER_DESCRIPTOR_INIT;
    WGPUCommandBuffer command = api.get<WGPUProcCommandEncoderFinish>(
        "wgpuCommandEncoderFinish")(encoder, &command_descriptor);
    require(command != nullptr, "D3D12 readback command creation failed");
    api.get<WGPUProcQueueSubmit>("wgpuQueueSubmit")(
        gpu.queue, 1U, &command);

    map_request map_state;
    WGPUBufferMapCallbackInfo map_callback =
        WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    map_callback.mode = WGPUCallbackMode_WaitAnyOnly;
    map_callback.userdata1 = &map_state;
    map_callback.callback = [](
        WGPUMapAsyncStatus status,
        WGPUStringView message,
        void* userdata1,
        void*) {
        auto* state = static_cast<map_request*>(userdata1);
        if (status != WGPUMapAsyncStatus_Success) {
            std::fprintf(stderr, "Dawn map error: %.*s\n",
                static_cast<int>(message.length),
                message.data == nullptr ? "" : message.data);
        }
        {
            const std::lock_guard lock(state->mutex);
            state->status = status;
            state->complete = true;
        }
        state->changed.notify_one();
    };
    const WGPUFuture map_future =
        api.get<WGPUProcBufferMapAsync>("wgpuBufferMapAsync")(
        buffer,
        WGPUMapMode_Read,
        0U,
        static_cast<std::size_t>(buffer_descriptor.size),
        map_callback);
    WGPUFutureWaitInfo map_wait = WGPU_FUTURE_WAIT_INFO_INIT;
    map_wait.future = map_future;
    const WGPUWaitStatus wait_status =
        api.get<WGPUProcInstanceWaitAny>("wgpuInstanceWaitAny")(
            gpu.instance,
            1U,
            &map_wait,
            30'000'000'000ULL);
    require(wait_status == WGPUWaitStatus_Success && map_wait.completed,
        "D3D12 readback mapping timed out");
    require(map_state.complete,
        "D3D12 readback mapping callback was not delivered");
    require(map_state.status == WGPUMapAsyncStatus_Success,
        "D3D12 readback mapping failed");
    const void* mapped = api.get<WGPUProcBufferGetConstMappedRange>(
        "wgpuBufferGetConstMappedRange")(
        buffer, 0U, static_cast<std::size_t>(buffer_descriptor.size));
    require(mapped != nullptr, "D3D12 readback range unavailable");
    std::vector<std::uint8_t> result(
        static_cast<std::size_t>(buffer_descriptor.size));
    std::memcpy(result.data(), mapped, result.size());
    api.get<WGPUProcBufferUnmap>("wgpuBufferUnmap")(buffer);

    api.get<WGPUProcCommandBufferRelease>(
        "wgpuCommandBufferRelease")(command);
    api.get<WGPUProcCommandEncoderRelease>(
        "wgpuCommandEncoderRelease")(encoder);
    api.get<WGPUProcBufferRelease>("wgpuBufferRelease")(buffer);
    api.get<WGPUProcTextureViewRelease>("wgpuTextureViewRelease")(view);
    api.get<WGPUProcTextureRelease>("wgpuTextureRelease")(texture);
    progpu_native_engine_destroy(engine);
    return result;
}

std::vector<std::uint8_t> render_system_direct2d()
{
    IWICImagingFactory* raw_wic_factory = nullptr;
    require(SUCCEEDED(CoCreateInstance(
            CLSID_WICImagingFactory,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&raw_wic_factory))) &&
        raw_wic_factory != nullptr, "WIC factory creation failed");
    native_com::pointer<IWICImagingFactory> wic_factory;
    wic_factory.attach(raw_wic_factory);
    IWICBitmap* raw_bitmap = nullptr;
    require(SUCCEEDED(wic_factory->CreateBitmap(
            width,
            height,
            GUID_WICPixelFormat32bppPBGRA,
            WICBitmapCacheOnLoad,
            &raw_bitmap)) &&
        raw_bitmap != nullptr, "WIC bitmap creation failed");
    native_com::pointer<IWICBitmap> bitmap;
    bitmap.attach(raw_bitmap);

    ID2D1Factory* raw_factory = nullptr;
    require(SUCCEEDED(D2D1CreateFactory(
            D2D1_FACTORY_TYPE_SINGLE_THREADED,
            &raw_factory)) &&
        raw_factory != nullptr, "system Direct2D factory creation failed");
    native_com::pointer<ID2D1Factory> factory;
    factory.attach(raw_factory);
    const D2D1_RENDER_TARGET_PROPERTIES target_properties =
        D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            D2D1::PixelFormat(
                DXGI_FORMAT_B8G8R8A8_UNORM,
                D2D1_ALPHA_MODE_PREMULTIPLIED),
            96.0F,
            96.0F);
    ID2D1RenderTarget* raw_target = nullptr;
    require(SUCCEEDED(factory->CreateWicBitmapRenderTarget(
            bitmap.get(), &target_properties, &raw_target)) &&
        raw_target != nullptr, "system Direct2D WIC target creation failed");
    native_com::pointer<ID2D1RenderTarget> target;
    target.attach(raw_target);

    const std::array<D2D1_COLOR_F, 2U> colors{
        D2D1_COLOR_F{0.0F, 0.0F, 1.0F, 1.0F},
        D2D1_COLOR_F{1.0F, 0.0F, 1.0F, 1.0F}};
    std::array<native_com::pointer<ID2D1SolidColorBrush>, 2U> brushes;
    for (std::size_t index = 0U; index < brushes.size(); ++index) {
        ID2D1SolidColorBrush* raw_brush = nullptr;
        require(SUCCEEDED(target->CreateSolidColorBrush(
                &colors[index], nullptr, &raw_brush)) &&
            raw_brush != nullptr, "system Direct2D brush creation failed");
        brushes[index].attach(raw_brush);
    }
    const D2D1_GRADIENT_STOP linear_stops[]{
        {0.0F, {1.0F, 0.0F, 0.0F, 1.0F}},
        {1.0F, {1.0F, 1.0F, 0.0F, 1.0F}}};
    ID2D1GradientStopCollection* raw_linear_stops = nullptr;
    require(SUCCEEDED(target->CreateGradientStopCollection(
            linear_stops,
            2U,
            D2D1_GAMMA_2_2,
            D2D1_EXTEND_MODE_CLAMP,
            &raw_linear_stops)) &&
        raw_linear_stops != nullptr,
        "system linear gradient stops creation failed");
    native_com::pointer<ID2D1GradientStopCollection> linear_collection;
    linear_collection.attach(raw_linear_stops);
    const D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES linear_properties{
        {4.0F, 12.0F}, {20.0F, 12.0F}};
    ID2D1LinearGradientBrush* raw_linear = nullptr;
    require(SUCCEEDED(target->CreateLinearGradientBrush(
            &linear_properties,
            nullptr,
            linear_collection.get(),
            &raw_linear)) &&
        raw_linear != nullptr,
        "system linear gradient brush creation failed");
    native_com::pointer<ID2D1LinearGradientBrush> linear;
    linear.attach(raw_linear);

    const D2D1_GRADIENT_STOP radial_stops[]{
        {0.0F, {0.0F, 1.0F, 0.0F, 1.0F}},
        {1.0F, {0.0F, 1.0F, 1.0F, 1.0F}}};
    ID2D1GradientStopCollection* raw_radial_stops = nullptr;
    require(SUCCEEDED(target->CreateGradientStopCollection(
            radial_stops,
            2U,
            D2D1_GAMMA_2_2,
            D2D1_EXTEND_MODE_CLAMP,
            &raw_radial_stops)) &&
        raw_radial_stops != nullptr,
        "system radial gradient stops creation failed");
    native_com::pointer<ID2D1GradientStopCollection> radial_collection;
    radial_collection.attach(raw_radial_stops);
    const D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES radial_properties{
        {40.0F, 14.0F}, {0.0F, 0.0F}, 8.0F, 8.0F};
    ID2D1RadialGradientBrush* raw_radial = nullptr;
    require(SUCCEEDED(target->CreateRadialGradientBrush(
            &radial_properties,
            nullptr,
            radial_collection.get(),
            &raw_radial)) &&
        raw_radial != nullptr,
        "system radial gradient brush creation failed");
    native_com::pointer<ID2D1RadialGradientBrush> radial;
    radial.attach(raw_radial);
    const D2D1_COLOR_F clear{0.05F, 0.1F, 0.15F, 1.0F};
    const D2D1_RECT_F rectangle{4.0F, 4.0F, 20.0F, 20.0F};
    const D2D1_ELLIPSE ellipse_value{{40.0F, 14.0F}, 8.0F, 8.0F};
    const D2D1_RECT_F stroked{8.0F, 28.0F, 28.0F, 42.0F};
    const D2D1_ROUNDED_RECT rounded{
        {36.0F, 28.0F, 56.0F, 44.0F}, 4.0F, 4.0F};
    target->BeginDraw();
    target->Clear(&clear);
    target->FillRectangle(&rectangle, linear.get());
    target->FillEllipse(&ellipse_value, radial.get());
    target->DrawRectangle(&stroked, brushes[0U].get(), 3.0F);
    target->FillRoundedRectangle(&rounded, brushes[1U].get());
    require(SUCCEEDED(target->EndDraw()), "system Direct2D draw failed");

    WICRect lock_rectangle{0, 0, static_cast<INT>(width),
        static_cast<INT>(height)};
    IWICBitmapLock* raw_lock = nullptr;
    require(SUCCEEDED(bitmap->Lock(
            &lock_rectangle, WICBitmapLockRead, &raw_lock)) &&
        raw_lock != nullptr, "WIC bitmap lock failed");
    native_com::pointer<IWICBitmapLock> bitmap_lock;
    bitmap_lock.attach(raw_lock);
    UINT stride = 0U;
    UINT data_size = 0U;
    BYTE* data = nullptr;
    require(SUCCEEDED(bitmap_lock->GetStride(&stride)) &&
        SUCCEEDED(bitmap_lock->GetDataPointer(&data_size, &data)) &&
        stride >= row_bytes && data != nullptr,
        "WIC bitmap data unavailable");
    std::vector<std::uint8_t> result(
        static_cast<std::size_t>(row_bytes) * height);
    for (std::uint32_t row = 0U; row < height; ++row) {
        std::memcpy(
            result.data() + static_cast<std::size_t>(row) * row_bytes,
            data + static_cast<std::size_t>(row) * stride,
            row_bytes);
    }
    return result;
}

void compare_images(
    std::span<const std::uint8_t> progpu,
    std::span<const std::uint8_t> system)
{
    require(progpu.size() == static_cast<std::size_t>(row_bytes) * height &&
        system.size() == progpu.size(), "differential image size mismatch");
    constexpr std::array<std::array<std::uint32_t, 2U>, 6U> probes{{
        {2U, 2U},
        {10U, 10U},
        {40U, 14U},
        {46U, 14U},
        {18U, 28U},
        {46U, 36U}}};
    for (const auto& probe : probes) {
        const std::size_t offset =
            static_cast<std::size_t>(probe[1U]) * row_bytes +
            static_cast<std::size_t>(probe[0U]) * 4U;
        for (std::size_t channel = 0U; channel < 4U; ++channel) {
            const int difference = std::abs(
                static_cast<int>(progpu[offset + channel]) -
                static_cast<int>(system[offset + channel]));
            require(difference <= 48, "Direct2D probe exceeded tolerance");
        }
    }
    std::uint64_t absolute_error = 0U;
    for (std::size_t index = 0U; index < progpu.size(); ++index) {
        absolute_error += static_cast<std::uint64_t>(std::abs(
            static_cast<int>(progpu[index]) -
            static_cast<int>(system[index])));
    }
    const double mean_error = static_cast<double>(absolute_error) /
        static_cast<double>(progpu.size());
    require(mean_error <= 12.0, "Direct2D mean pixel error exceeded tolerance");
    std::printf(
        "Direct2D native-vs-ProGPU D3D12 passed: mean_error=%.4f bytes=%zu\n",
        mean_error,
        progpu.size());
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    require(argc == 2, "usage: test WEBGPU_DAWN_DLL");
    const HRESULT initialize_result = CoInitializeEx(
        nullptr, COINIT_MULTITHREADED);
    require(SUCCEEDED(initialize_result), "COM initialization failed");
    dawn_api api(argv[1]);
    gpu_context gpu = create_gpu(api);
    portable_scene scene = record_portable_scene();
    const std::vector<std::uint8_t> progpu = render_progpu(
        api, gpu, scene.scene_target.get());
    const std::vector<std::uint8_t> system = render_system_direct2d();
    compare_images(progpu, system);
    scene = {};
    release_gpu(api, gpu);
    CoUninitialize();
    return 0;
}
