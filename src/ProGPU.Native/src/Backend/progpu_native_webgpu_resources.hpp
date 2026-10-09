#pragma once

#include <vector>
#include <array>

// Internal WebGPU handle ownership. Include only after the selected WebGPU C
// header has declared WGPUBuffer and WGPUBindGroup.
namespace progpu::native {

struct target_clear_pipeline_resources final {
    WGPUShaderModule shader = nullptr;
    WGPUBindGroupLayout binding_layout = nullptr;
    WGPUPipelineLayout pipeline_layout = nullptr;
    WGPURenderPipeline pipeline = nullptr;
    target_clear_pipeline_resources() = default;
    target_clear_pipeline_resources(const target_clear_pipeline_resources&) = delete;
    target_clear_pipeline_resources& operator=(const target_clear_pipeline_resources&) = delete;
    ~target_clear_pipeline_resources() { reset(); }
    void reset() noexcept {
        if (pipeline != nullptr) wgpuRenderPipelineRelease(pipeline);
        if (pipeline_layout != nullptr) wgpuPipelineLayoutRelease(pipeline_layout);
        if (binding_layout != nullptr) wgpuBindGroupLayoutRelease(binding_layout);
        if (shader != nullptr) wgpuShaderModuleRelease(shader);
        pipeline = nullptr;
        pipeline_layout = nullptr;
        binding_layout = nullptr;
        shader = nullptr;
    }
};

struct rgb_glyph_pipeline_resources final {
    WGPUShaderModule raster_shader = nullptr;
    WGPUShaderModule composite_shader = nullptr;
    WGPUBindGroupLayout raster_layout = nullptr;
    WGPUBindGroupLayout composite_layout = nullptr;
    WGPUPipelineLayout raster_pipeline_layout = nullptr;
    WGPUPipelineLayout composite_pipeline_layout = nullptr;
    WGPUComputePipeline compute = nullptr;
    WGPURenderPipeline fragment = nullptr;
    std::array<WGPURenderPipeline, 3U> composite{};
    // Ordinary RGB draws never allocate mask pipeline layouts or empty groups.
    WGPUBindGroupLayout empty_layout = nullptr;
    WGPUBindGroup empty_bind_group = nullptr;
    std::array<WGPUPipelineLayout, 2U> masked_layouts{};
    std::array<std::array<WGPURenderPipeline, 3U>, 2U> masked_composite{};

    rgb_glyph_pipeline_resources() = default;
    rgb_glyph_pipeline_resources(const rgb_glyph_pipeline_resources&) = delete;
    rgb_glyph_pipeline_resources& operator=(const rgb_glyph_pipeline_resources&) = delete;
    ~rgb_glyph_pipeline_resources() { reset(); }
    void reset() noexcept {
        for (auto& family : masked_composite) for (auto& pipeline : family) {
            if (pipeline != nullptr) wgpuRenderPipelineRelease(pipeline);
            pipeline = nullptr;
        }
        for (auto& layout : masked_layouts) {
            if (layout != nullptr) wgpuPipelineLayoutRelease(layout);
            layout = nullptr;
        }
        if (empty_bind_group != nullptr) wgpuBindGroupRelease(empty_bind_group);
        if (empty_layout != nullptr) wgpuBindGroupLayoutRelease(empty_layout);
        empty_bind_group = nullptr;
        empty_layout = nullptr;
        for (auto& pipeline : composite) {
            if (pipeline != nullptr) wgpuRenderPipelineRelease(pipeline);
            pipeline = nullptr;
        }
        if (compute != nullptr) wgpuComputePipelineRelease(compute);
        if (fragment != nullptr) wgpuRenderPipelineRelease(fragment);
        if (raster_pipeline_layout != nullptr) wgpuPipelineLayoutRelease(raster_pipeline_layout);
        if (composite_pipeline_layout != nullptr) wgpuPipelineLayoutRelease(composite_pipeline_layout);
        if (raster_layout != nullptr) wgpuBindGroupLayoutRelease(raster_layout);
        if (composite_layout != nullptr) wgpuBindGroupLayoutRelease(composite_layout);
        if (raster_shader != nullptr) wgpuShaderModuleRelease(raster_shader);
        if (composite_shader != nullptr) wgpuShaderModuleRelease(composite_shader);
        compute = nullptr;
        fragment = nullptr;
        raster_pipeline_layout = nullptr;
        composite_pipeline_layout = nullptr;
        raster_layout = nullptr;
        composite_layout = nullptr;
        raster_shader = nullptr;
        composite_shader = nullptr;
    }
};

struct path_raster_resources {
    WGPUBuffer uniforms = nullptr;
    std::vector<WGPUBuffer> split_leaf_uniforms;
    std::vector<WGPUBuffer> split_signed_leaf_uniforms;
    WGPUBuffer records = nullptr;
    WGPUBuffer segments = nullptr;
    WGPUBuffer coverage = nullptr;
    WGPUBuffer coverage_combine_uniforms = nullptr;
    WGPUBuffer signed_coverage_combine_uniforms = nullptr;
    WGPUBindGroup bind_group = nullptr;
    std::vector<WGPUBindGroup> split_leaf_bind_groups;
    std::vector<WGPUBindGroup> split_signed_leaf_bind_groups;
    WGPUBindGroup signed_combine_bind_group = nullptr;
    // RGB coverage uses the same real submission-retirement lease as ordinary
    // glyph raster storage, never a frame counter or temporary host lifetime.
    WGPUBuffer rgb_policy = nullptr;
    WGPUBuffer rgb_instances = nullptr;
    WGPUBuffer rgb_cells = nullptr;
    WGPUBuffer rgb_references = nullptr;
    WGPUBuffer rgb_frame = nullptr;
    WGPUTexture rgb_coverage = nullptr;
    WGPUTextureView rgb_coverage_view = nullptr;
    WGPUTexture rgb_backdrop = nullptr;
    WGPUTextureView rgb_backdrop_view = nullptr;
    WGPUBindGroup rgb_composite = nullptr;

    path_raster_resources() = default;
    path_raster_resources(const path_raster_resources&) = delete;
    path_raster_resources& operator=(const path_raster_resources&) = delete;

    ~path_raster_resources() {
        if (rgb_composite != nullptr) wgpuBindGroupRelease(rgb_composite);
        if (rgb_backdrop_view != nullptr) wgpuTextureViewRelease(rgb_backdrop_view);
        if (rgb_backdrop != nullptr) wgpuTextureRelease(rgb_backdrop);
        if (rgb_coverage_view != nullptr) wgpuTextureViewRelease(rgb_coverage_view);
        if (rgb_coverage != nullptr) wgpuTextureRelease(rgb_coverage);
        release_buffer(rgb_policy);
        release_buffer(rgb_instances);
        release_buffer(rgb_cells);
        release_buffer(rgb_references);
        release_buffer(rgb_frame);
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
        }
        for (WGPUBindGroup split_bind_group : split_leaf_bind_groups) {
            if (split_bind_group != nullptr) {
                wgpuBindGroupRelease(split_bind_group);
            }
        }
        for (WGPUBindGroup split_bind_group :
             split_signed_leaf_bind_groups) {
            if (split_bind_group != nullptr) {
                wgpuBindGroupRelease(split_bind_group);
            }
        }
        if (signed_combine_bind_group != nullptr) {
            wgpuBindGroupRelease(signed_combine_bind_group);
        }
        release_buffer(uniforms);
        for (WGPUBuffer buffer : split_leaf_uniforms) {
            release_buffer(buffer);
        }
        for (WGPUBuffer buffer : split_signed_leaf_uniforms) {
            release_buffer(buffer);
        }
        release_buffer(records);
        release_buffer(segments);
        release_buffer(coverage);
        release_buffer(coverage_combine_uniforms);
        release_buffer(signed_coverage_combine_uniforms);
    }

private:
    static void release_buffer(WGPUBuffer buffer) {
        if (buffer != nullptr) {
            // Native engine leases retain these caller references through
            // submission completion. Never explicitly destroy a buffer still
            // referenced by another encoded operation or a browser command.
            wgpuBufferRelease(buffer);
        }
    }
};

} // namespace progpu::native
