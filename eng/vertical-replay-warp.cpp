#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "progpu_native.h"
#include <wgpu.h>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <thread>
#include <vector>
constexpr std::uint32_t width = 64U;
constexpr std::uint32_t height = 64U;
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

[[nodiscard]] gpu_context create_gpu(bool software = false)
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
    std::size_t selected = 0U;
    if (software) {
        selected = written;
        for (std::size_t index = 0U; index < written; ++index) {
            WGPUAdapterProperties candidate{};
            wgpuAdapterGetProperties(adapters[index], &candidate);
            if (candidate.adapterType == WGPUAdapterType_CPU) selected = index;
        }
        require(selected != written, "requested software adapter is unavailable");
    }
    WGPUAdapter adapter = adapters[selected];
    for (std::size_t index = 0U; index < written; ++index) {
        if (index != selected) wgpuAdapterRelease(adapters[index]);
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


int main(int argc, char** argv) {
    require(argc == 3, "usage: replay original-scene output-prefix");
    std::ifstream source(argv[1], std::ios::binary);
    std::vector<char> bytes((std::istreambuf_iterator<char>(source)), {});
    require(bytes.size() >= sizeof(progpu_native_scene_header) && bytes.size() <= 32U*1024U*1024U, "scene size");
    progpu_native_scene_header h{}; std::memcpy(&h, bytes.data(), sizeof(h));
    require(h.magic == PROGPU_NATIVE_SCENE_STREAM_MAGIC && h.total_size == bytes.size(), "original header");
    auto start=std::chrono::steady_clock::now();
    auto elapsed=[&](){return std::chrono::duration<double>(std::chrono::steady_clock::now()-start).count();};
    auto gpu=create_gpu(true);
    require(gpu.properties.adapterType == WGPUAdapterType_CPU && std::strcmp(gpu.properties.name,"Microsoft Basic Render Driver")==0,"exact WARP adapter required");
    for (auto name : {"progpu_native.dll", "wgpu_native.dll"}) {
        char path[32768]{};
        require(GetModuleFileNameA(GetModuleHandleA(name),path,sizeof(path)) != 0,"loaded module path");
        std::printf("loaded=%s\n",path);
    }
    std::printf("adapter=%s backend=%s\n",gpu.properties.name,backend_name(gpu.properties.backendType));std::fflush(stdout);
    progpu_native_engine_options options{};
    options.struct_size=sizeof(options);options.abi_version=PROGPU_NATIVE_ABI_VERSION;
    options.backend_abi=PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
    options.target_format=PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    options.device=reinterpret_cast<std::uintptr_t>(gpu.device);options.queue=reinterpret_cast<std::uintptr_t>(gpu.queue);
    progpu_native_engine* engine=nullptr;
    auto checked=[&](progpu_native_status s,const char* operation){ if(s!=PROGPU_NATIVE_STATUS_SUCCESS){char error[4096]{};if(engine)progpu_native_engine_get_last_error(engine,error,sizeof(error));std::fprintf(stderr,"%s status=%u %s\n",operation,(unsigned)s,error);std::exit(2);} };
    checked(progpu_native_engine_create(&options,&engine),"create");
    progpu_native_scene_metrics sm{};sm.struct_size=sizeof(sm);
    checked(progpu_native_engine_update_scene(engine,bytes.data(),bytes.size(),&sm),"update");
    constexpr std::uint32_t w=884,hh=861,pitch=(w*4+255)&~255;
    WGPUTextureDescriptor td{};td.usage=WGPUTextureUsage_RenderAttachment|WGPUTextureUsage_CopySrc;
    td.dimension=WGPUTextureDimension_2D;td.size={w,hh,1};td.format=WGPUTextureFormat_BGRA8Unorm;td.mipLevelCount=1;td.sampleCount=1;
    auto texture=wgpuDeviceCreateTexture(gpu.device,&td);require(texture!=nullptr,"target");
    auto view=wgpuTextureCreateView(texture,nullptr);require(view!=nullptr,"view");
    progpu_native_scene_frame frame{};frame.struct_size=sizeof(frame);frame.width=w;frame.height=hh;frame.dpi_scale=1;
    frame.target_view=reinterpret_cast<std::uintptr_t>(view);frame.clear_color={1,1,1,1};frame.scene_id=h.scene_id;frame.generation=h.generation;
    frame.flags=PROGPU_NATIVE_SCENE_FRAME_PRESENTATION|PROGPU_NATIVE_SCENE_FRAME_CAPTURE_CPU_STAGES;
    frame.presentation={sizeof(progpu_native_scene_presentation),0,0,w,hh,1,1,0};
    progpu_native_scene_frame_metrics fm{};fm.struct_size=sizeof(fm);
    checked(progpu_native_engine_render_scene(engine,&frame,&fm),"render");
    std::printf("submitted seconds=%.6f commands=%u draws=%u submissions=%llu cpuMs=%.3f\n",elapsed(),fm.command_count,fm.draw_call_count,(unsigned long long)fm.submission_count,fm.cpu_total_nanoseconds/1000000.0);std::fflush(stdout);
    std::uint64_t token=0;checked(progpu_native_engine_get_last_submission(engine,&token),"token");
    std::uint8_t complete=0;auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(30);
    while(!complete){checked(progpu_native_engine_poll_submission(engine,token,0,&complete),"poll");require(std::chrono::steady_clock::now()<deadline,"GPU completion deadline");if(!complete)std::this_thread::sleep_for(std::chrono::milliseconds(1));}
    std::printf("completed seconds=%.6f token=%llu\n",elapsed(),(unsigned long long)token);std::fflush(stdout);
    WGPUBufferDescriptor bd{};bd.size=(std::uint64_t)pitch*hh;bd.usage=WGPUBufferUsage_CopyDst|WGPUBufferUsage_MapRead;
    auto buffer=wgpuDeviceCreateBuffer(gpu.device,&bd);require(buffer!=nullptr,"readback");
    auto enc=wgpuDeviceCreateCommandEncoder(gpu.device,nullptr);WGPUImageCopyTexture src{};src.texture=texture;src.aspect=WGPUTextureAspect_All;
    WGPUImageCopyBuffer dst{};dst.buffer=buffer;dst.layout.bytesPerRow=pitch;dst.layout.rowsPerImage=hh;
    WGPUExtent3D extent{w,hh,1};wgpuCommandEncoderCopyTextureToBuffer(enc,&src,&dst,&extent);auto cmd=wgpuCommandEncoderFinish(enc,nullptr);wgpuQueueSubmit(gpu.queue,1,&cmd);
    map_request mapped{};wgpuBufferMapAsync(buffer,WGPUMapMode_Read,0,bd.size,on_buffer_mapped,&mapped);
    wait_for_gpu_callback(gpu.device,mapped,"map deadline");require(mapped.status==WGPUBufferMapAsyncStatus_Success,"mapped");
    auto ptr=static_cast<const std::uint8_t*>(wgpuBufferGetConstMappedRange(buffer,0,bd.size));require(ptr!=nullptr,"bytes");
    std::ofstream image(std::string(argv[2])+".ppm",std::ios::binary);image<<"P6\n"<<w<<" "<<hh<<"\n255\n";
    std::uint64_t nonwhite=0;for(std::uint32_t y=0;y<hh;++y)for(std::uint32_t x=0;x<w;++x){auto p=ptr+y*pitch+x*4;char rgb[]{(char)p[2],(char)p[1],(char)p[0]};image.write(rgb,3);nonwhite+=(p[0]!=255||p[1]!=255||p[2]!=255);}image.close();
    std::printf("diagnosticOnly=true nonwhitePixels=%llu totalPixels=%u seconds=%.6f\n",(unsigned long long)nonwhite,w*hh,elapsed());
    wgpuBufferUnmap(buffer);wgpuCommandBufferRelease(cmd);wgpuCommandEncoderRelease(enc);wgpuBufferDestroy(buffer);wgpuBufferRelease(buffer);
    progpu_native_engine_destroy(engine);wgpuTextureViewRelease(view);wgpuTextureDestroy(texture);wgpuTextureRelease(texture);release_gpu(gpu);
    return 0;
}
