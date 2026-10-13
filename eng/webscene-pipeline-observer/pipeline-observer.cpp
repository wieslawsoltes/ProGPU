// Diagnostic forwarding only. Original descriptors, return values, provider,
// queue operations and ownership are unchanged. Public WebGPU/Provider ABI.
#include "webgpu.h"
#include "webscene_gpu_provider.h"
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <map>
#include <mutex>
#include <string>

namespace {
using clock_type = std::chrono::steady_clock;
const auto launch = clock_type::now();
std::mutex record_mutex;
std::map<WGPUShaderModule, unsigned> module_ids;
unsigned next_module = 0;
WGPUProcDeviceCreateShaderModule original_module{};
WGPUProcDeviceCreateRenderPipeline original_render{};
WGPUProcDeviceCreateComputePipeline original_compute{};

double seconds(clock_type::time_point from, clock_type::time_point to) {
    return std::chrono::duration<double>(to - from).count();
}
std::string text(WGPUStringView s) {
    if (!s.data) return {};
    std::string value(s.data, s.length == WGPU_STRLEN ? std::strlen(s.data) : s.length);
    for (auto& c : value) if (c == '\t' || c == '\n' || c == '\r') c = ' ';
    return value;
}
FILE* records() {
    static FILE* file = std::fopen("pipeline-times.tsv", "wb");
    if (!file) std::abort();
    return file;
}
WGPUShaderModule observe_module(WGPUDevice device, const WGPUShaderModuleDescriptor* descriptor) {
    const auto start = clock_type::now();
    auto result = original_module(device, descriptor);
    const auto finish = clock_type::now();
    std::lock_guard lock(record_mutex);
    const auto id = ++next_module;
    if (result) module_ids[result] = id;
    for (auto* chain = descriptor->nextInChain; chain; chain = chain->next) {
        if (chain->sType != WGPUSType_ShaderSourceWGSL) continue;
        const auto* wgsl = reinterpret_cast<const WGPUShaderSourceWGSL*>(chain);
        const auto length = wgsl->code.length == WGPU_STRLEN ? std::strlen(wgsl->code.data) : wgsl->code.length;
        char name[64]; std::snprintf(name, sizeof(name), "module-%05u.wgsl", id);
        auto* file = std::fopen(name, "wb");
        if (!file || std::fwrite(wgsl->code.data, 1, length, file) != length || std::fclose(file)) std::abort();
    }
    std::fprintf(records(), "%.9f\t%.9f\tmodule\t%u\t%p\t%p\t%s\n",
        seconds(launch, start), seconds(start, finish), id,
        static_cast<void*>(device), static_cast<void*>(result), text(descriptor->label).c_str());
    std::fflush(records());
    return result;
}
WGPURenderPipeline observe_render(WGPUDevice device, const WGPURenderPipelineDescriptor* descriptor) {
    const auto start = clock_type::now();
    auto result = original_render(device, descriptor);
    const auto finish = clock_type::now();
    std::lock_guard lock(record_mutex);
    const auto* fragment = descriptor->fragment;
    std::fprintf(records(), "%.9f\t%.9f\trender\t%u\t%p\t%p\t%s\t%s\t%s\t%u\n",
        seconds(launch, start), seconds(start, finish), module_ids[descriptor->vertex.module],
        static_cast<void*>(device), static_cast<void*>(result), text(descriptor->label).c_str(),
        text(descriptor->vertex.entryPoint).c_str(), fragment ? text(fragment->entryPoint).c_str() : "",
        fragment && fragment->targetCount ? static_cast<unsigned>(fragment->targets[0].format) : 0U);
    std::fflush(records());
    return result;
}
WGPUComputePipeline observe_compute(WGPUDevice device, const WGPUComputePipelineDescriptor* descriptor) {
    const auto start = clock_type::now();
    auto result = original_compute(device, descriptor);
    const auto finish = clock_type::now();
    std::lock_guard lock(record_mutex);
    std::fprintf(records(), "%.9f\t%.9f\tcompute\t%u\t%p\t%p\t%s\t%s\n",
        seconds(launch, start), seconds(start, finish), module_ids[descriptor->compute.module],
        static_cast<void*>(device), static_cast<void*>(result), text(descriptor->label).c_str(),
        text(descriptor->compute.entryPoint).c_str());
    std::fflush(records());
    return result;
}
template<class T> void* replace(void* original, T& slot, T observer) {
    T fn{}; static_assert(sizeof(fn) == sizeof(original));
    std::memcpy(&fn, &original, sizeof(fn));
    if (slot && slot != fn) std::abort();
    slot = fn;
    std::memcpy(&original, &observer, sizeof(original));
    return original;
}
}

extern "C" WEBSCENE_GPU_API void* webscene_gpu_provider_get_wgpu_proc_address(
    webscene_gpu_provider* provider, const char* name) {
    static void* module = dlopen(PROGPU_PROVIDER_PATH, RTLD_NOW | RTLD_LOCAL);
    if (!module) std::abort();
    using resolver_type = decltype(&webscene_gpu_provider_get_wgpu_proc_address);
    static auto resolver = reinterpret_cast<resolver_type>(dlsym(module, "webscene_gpu_provider_get_wgpu_proc_address"));
    if (!resolver || resolver == &webscene_gpu_provider_get_wgpu_proc_address) std::abort();
    auto result = resolver(provider, name);
    if (!result) return result;
    if (std::strcmp(name, "wgpuDeviceCreateShaderModule") == 0) return replace(result, original_module, observe_module);
    if (std::strcmp(name, "wgpuDeviceCreateRenderPipeline") == 0) return replace(result, original_render, observe_render);
    if (std::strcmp(name, "wgpuDeviceCreateComputePipeline") == 0) return replace(result, original_compute, observe_compute);
    return result;
}
