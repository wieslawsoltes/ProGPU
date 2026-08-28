#pragma once

#include <vector>

// Internal WebGPU handle ownership. Include only after the selected WebGPU C
// header has declared WGPUBuffer and WGPUBindGroup.
namespace progpu::native {

struct path_raster_resources {
    WGPUBuffer uniforms = nullptr;
    std::vector<WGPUBuffer> split_leaf_uniforms;
    WGPUBuffer records = nullptr;
    WGPUBuffer segments = nullptr;
    WGPUBuffer coverage = nullptr;
    WGPUBuffer coverage_combine_uniforms = nullptr;
    WGPUBindGroup bind_group = nullptr;
    std::vector<WGPUBindGroup> split_leaf_bind_groups;

    path_raster_resources() = default;
    path_raster_resources(const path_raster_resources&) = delete;
    path_raster_resources& operator=(const path_raster_resources&) = delete;

    ~path_raster_resources() {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
        }
        for (WGPUBindGroup split_bind_group : split_leaf_bind_groups) {
            if (split_bind_group != nullptr) {
                wgpuBindGroupRelease(split_bind_group);
            }
        }
        release_buffer(uniforms);
        for (WGPUBuffer buffer : split_leaf_uniforms) {
            release_buffer(buffer);
        }
        release_buffer(records);
        release_buffer(segments);
        release_buffer(coverage);
        release_buffer(coverage_combine_uniforms);
    }

private:
    static void release_buffer(WGPUBuffer buffer) {
        if (buffer != nullptr) {
            // Encoders and submitted command buffers retain temporary staging
            // resources. Dropping caller ownership is safe; explicitly
            // destroying here would invalidate a shared semantic encoder
            // before it is finished.
            wgpuBufferRelease(buffer);
        }
    }
};

} // namespace progpu::native
